using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// D1's GPU half (deconvolver-training.md, "The GPU half, pre-registered"): the shipped SAS AI4
/// graph run twice on each matching retained master, whole-image and per-tile psf01, with the stars
/// of the input and both outputs measured in three field-radius bins. Whether telling the graph the
/// LOCAL width changes what it does where the width differs, and whether it does anything it should
/// not (a bin narrower than the input's sharpest, a star count that moves).
/// </summary>
/// <remarks>
/// Skipped unless <c>TIANWEN_PSF_STORE_DIR</c> points at a dataset out-dir with <c>session-masters/</c>
/// and the AI4 model resolves; <c>TIANWEN_PSF_PROBE_FILTER</c> (default <c>Rim</c>) selects masters.
/// Off-label: the graph is a non-stellar deconvolver meant for a starless plate, but stars are the
/// only PSF probe a frame offers and the comparison is per-tile AGAINST whole-image on the same
/// pixels, so what is off-label cancels.
/// </remarks>
public class PerChunkPsfOutputProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PSF_STORE_DIR";
    private const string FilterVar = "TIANWEN_PSF_PROBE_FILTER";
    private const int Bins = 3;

    private sealed record BinStats(int Stars, float MedianFwhm);

    private static async Task<BinStats[]> MeasureAsync(Image image, CancellationToken ct)
    {
        var (_, width, height) = image.Shape;
        var cx = width * 0.5;
        var cy = height * 0.5;
        var halfDiag = Math.Sqrt((cx * cx) + (cy * cy));
        var stars = await image.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct);
        var perBin = new List<float>[Bins];
        for (var b = 0; b < Bins; b++) perBin[b] = [];
        foreach (var s in stars)
        {
            if (!(s.StarFWHM > 0f)) continue;
            var r = Math.Sqrt(((s.XCentroid - cx) * (s.XCentroid - cx)) + ((s.YCentroid - cy) * (s.YCentroid - cy))) / halfDiag;
            perBin[Math.Clamp((int)(r * Bins), 0, Bins - 1)].Add(s.StarFWHM);
        }

        var result = new BinStats[Bins];
        for (var b = 0; b < Bins; b++)
        {
            perBin[b].Sort();
            result[b] = new BinStats(perBin[b].Count, perBin[b].Count == 0 ? float.NaN : perBin[b][perBin[b].Count / 2]);
        }
        return result;
    }

    [Fact]
    public async Task ReportWhetherPerTileConditioningChangesTheOutputWhereTheWidthDiffers()
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");
        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");
        var resolver = new ModelResolver();
        string modelPath;
        try
        {
            modelPath = resolver.Resolve(OnnxNonStellarDeconvolver.Model);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            Assert.Skip($"{OnnxNonStellarDeconvolver.Model} does not resolve: {ex.Message}");
            return;
        }

        var filter = Environment.GetEnvironmentVariable(FilterVar) is { Length: > 0 } f ? f : "Rim";
        var masters = Directory.GetFiles(mastersDir, "*.fits")
            .Where(p => Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.SkipWhen(masters.Length == 0, $"no master matches '{filter}'");

        var ct = TestContext.Current.CancellationToken;
        // The SHIPPED range, because the shipped graph is what runs.
        var estimator = new HfdPsfEstimator();
        using var whole = new OnnxNonStellarDeconvolver(resolver, estimator, chunkSize: 256, overlap: 64, perChunkPsf: false);
        using var perTile = new OnnxNonStellarDeconvolver(resolver, estimator, chunkSize: 256, overlap: 64, perChunkPsf: true);

        output.WriteLine($"model     {modelPath}");
        output.WriteLine($"masters   {masters.Length} matching '{filter}'; bins are thirds of the half-diagonal from the frame centre (inner, middle, outer)");
        output.WriteLine($"columns   median star FWHM px and star count per bin, for the INPUT, the whole-image run and the per-tile run; c/o = inner over outer");
        output.WriteLine($"psf01     the SHIPPED encoding over [{estimator.RadiusRange.Min}, {estimator.RadiusRange.Max}] px, since the shipped graph is what runs; a master whose");
        output.WriteLine("          radii sit under the floor clamps every tile to the whole-image value and cannot discriminate (tiles differing 0)");
        output.WriteLine("");
        output.WriteLine($"{"master",-40} {"arm",-9} {"inner",6} {"n",5} {"middle",6} {"n",5} {"outer",6} {"n",5} {"c/o",5} {"s",6}");

        foreach (var path in masters)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var shortName = name[..Math.Min(40, name.Length)];
            if (!Image.TryReadFitsFile(path, out var master) || master is null)
            {
                output.WriteLine($"{shortName,-40} (unreadable)");
                continue;
            }

            try
            {
                // The rescale REWRAPS: the returned image carries the unit-range values and MaxValue, the
                // original's MaxValue is stale (Image's remarks). The first run of this probe used the
                // original and the deconvolver refused the second master at MaxValue 65535.
                var unit = master.ScaleFloatValuesToUnitInPlace();
                var wholePsf01 = await estimator.EstimateAsync(unit, ct);
                var tilePsf01 = await perTile.EstimatePerChunkAsync(unit, wholePsf01, ct);
                var differing = tilePsf01.Count(v => v != wholePsf01);
                output.WriteLine($"{shortName,-40} whole psf01 {wholePsf01:F3}; tiles {tilePsf01.Length}, differing from it {differing}, "
                    + $"tile psf01 {tilePsf01.Min():F3} to {tilePsf01.Max():F3}");
                var inputBins = await MeasureAsync(unit, ct);
                Print(shortName, "input", inputBins, 0);

                foreach (var (label, deconvolver) in new[] { ("whole", whole), ("per-tile", perTile) })
                {
                    var started = DateTime.UtcNow;
                    var result = await deconvolver.EnhanceAsync(unit, ct);
                    try
                    {
                        var bins = await MeasureAsync(result, ct);
                        Print(shortName, label, bins, (DateTime.UtcNow - started).TotalSeconds);
                    }
                    finally
                    {
                        result.Release();
                    }
                }
            }
            finally
            {
                master.Release();
            }
        }

        output.WriteLine("");
        output.WriteLine("Read per-tile against whole on the SAME master: the prediction is c/o moving toward 1 where the input's");
        output.WriteLine("c/o is far from it, with n within 10 percent; a bin narrower than the input's sharpest bin, or n moving");
        output.WriteLine("over 20 percent, is the kill line.");
    }

    private sealed class FixedPsfEstimator(float psf01) : IPsfEstimator
    {
        public Task<float> EstimateAsync(Image image, CancellationToken cancellationToken = default)
            => Task.FromResult(psf01);
    }

    /// <summary>
    /// Does the shipped graph respond to psf01 AT ALL on a real master? The per-tile comparison above
    /// read as a null on every Rim master (whole and per-tile within 0.01 px and one percent in count
    /// while 250 of 289 tiles carried a different label), which is either a graph that ignores its
    /// conditioning input at this scale or a difference too small to see; running the same graph at
    /// psf01 0, 0.5 and 1 on one master separates the two. Opt-in (<c>TIANWEN_PSF_PROBE_SENSITIVITY=1</c>),
    /// same filter and bins as the comparison.
    /// </summary>
    [Fact]
    public async Task ReportWhetherTheShippedGraphRespondsToPsf01AtAll()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_PSF_PROBE_SENSITIVITY") == "1", "TIANWEN_PSF_PROBE_SENSITIVITY is not 1");
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");
        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");
        var resolver = new ModelResolver();
        Assert.SkipUnless(resolver.TryResolve(OnnxNonStellarDeconvolver.Model, out var modelPath), $"{OnnxNonStellarDeconvolver.Model} does not resolve");

        var filter = Environment.GetEnvironmentVariable(FilterVar) is { Length: > 0 } f ? f : "Rim";
        var masters = Directory.GetFiles(mastersDir, "*.fits")
            .Where(p => Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.SkipWhen(masters.Length == 0, $"no master matches '{filter}'");

        var ct = TestContext.Current.CancellationToken;
        output.WriteLine($"model     {modelPath}");
        output.WriteLine($"masters   {masters.Length} matching '{filter}'; the graph run whole-image at three FIXED psf01 values; bins as above");
        output.WriteLine("");
        output.WriteLine($"{"master",-40} {"arm",-9} {"inner",6} {"n",5} {"middle",6} {"n",5} {"outer",6} {"n",5} {"c/o",5} {"s",6}");

        foreach (var path in masters)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var shortName = name[..Math.Min(40, name.Length)];
            if (!Image.TryReadFitsFile(path, out var master) || master is null)
            {
                output.WriteLine($"{shortName,-40} (unreadable)");
                continue;
            }

            try
            {
                var unit = master.ScaleFloatValuesToUnitInPlace();
                Print(shortName, "input", await MeasureAsync(unit, ct), 0);
                foreach (var psf01 in new[] { 0.0f, 0.5f, 1.0f })
                {
                    using var deconvolver = new OnnxNonStellarDeconvolver(resolver, new FixedPsfEstimator(psf01), chunkSize: 256, overlap: 64);
                    var started = DateTime.UtcNow;
                    var result = await deconvolver.EnhanceAsync(unit, ct);
                    try
                    {
                        Print(shortName, $"psf {psf01:F1}", await MeasureAsync(result, ct), (DateTime.UtcNow - started).TotalSeconds);
                    }
                    finally
                    {
                        result.Release();
                    }
                }
            }
            finally
            {
                master.Release();
            }
        }

        output.WriteLine("");
        output.WriteLine("Three identical rows mean the conditioning input is inert on this graph for a real master's stars and the");
        output.WriteLine("per-tile null above is a property of the graph; rows that differ mean the per-tile labels' spread was too small.");
    }

    private void Print(string master, string arm, BinStats[] bins, double seconds)
    {
        var ratio = bins[0].MedianFwhm / bins[Bins - 1].MedianFwhm;
        output.WriteLine($"{master,-40} {arm,-9} {bins[0].MedianFwhm,6:F2} {bins[0].Stars,5} {bins[1].MedianFwhm,6:F2} {bins[1].Stars,5} "
            + $"{bins[2].MedianFwhm,6:F2} {bins[2].Stars,5} {ratio,5:F2} {seconds,6:F0}");
    }
}
