using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// D1's first measurement (deconvolver-training.md): how much the per-tile PSF radius actually varies
/// across a real master, tile by tile, on the grid <see cref="OnnxNonStellarDeconvolver"/> would
/// condition on, and how many tiles starve and fall back to the whole-image value. No model runs:
/// the estimate is star detection per region, so this is the CPU half of "measure on Rim"; whether
/// per-tile conditioning changes the OUTPUT is the GPU half and a separate probe.
/// </summary>
/// <remarks>
/// Skipped unless <c>TIANWEN_PSF_STORE_DIR</c> points at a dataset out-dir holding
/// <c>session-masters/</c>; <c>TIANWEN_PSF_PROBE_FILTER</c> (default <c>Rim</c>) selects masters by
/// file-name substring. Radii are decoded from psf01 over TianWen's <c>[0.5, 4.0]</c> px contract,
/// which does not clamp this archive, so the columns read in pixels.
/// </remarks>
public class PerChunkPsfProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PSF_STORE_DIR";
    private const string FilterVar = "TIANWEN_PSF_PROBE_FILTER";
    private const int ChunkSize = 256;
    private const int Overlap = 64;

    private static float Radius(float psf01)
        => HfdPsfEstimator.TianWenMinRadiusPx * MathF.Pow(HfdPsfEstimator.TianWenMaxRadiusPx / HfdPsfEstimator.TianWenMinRadiusPx, psf01);

    [Fact]
    public async Task ReportHowMuchThePsfVariesAcrossAMastersTiles()
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");
        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");

        var filter = Environment.GetEnvironmentVariable(FilterVar) is { Length: > 0 } f ? f : "Rim";
        var masters = Directory.GetFiles(mastersDir, "*.fits")
            .Where(p => Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.SkipWhen(masters.Length == 0, $"no master matches '{filter}'");

        var ct = TestContext.Current.CancellationToken;
        var estimator = new HfdPsfEstimator(minRadiusPx: HfdPsfEstimator.TianWenMinRadiusPx, maxRadiusPx: HfdPsfEstimator.TianWenMaxRadiusPx);
        // Constructing the deconvolver loads nothing; the session is acquired on the first EnhanceAsync,
        // which this probe never calls.
        using var deconvolver = new OnnxNonStellarDeconvolver(new ModelResolver(), estimator, chunkSize: ChunkSize, overlap: Overlap, perChunkPsf: true);

        output.WriteLine($"masters   {masters.Length} matching '{filter}'; tiles {ChunkSize} px, overlap {Overlap}, the runner's own grid over the bordered plane");
        output.WriteLine($"radius    decoded from psf01 over [{HfdPsfEstimator.TianWenMinRadiusPx}, {HfdPsfEstimator.TianWenMaxRadiusPx}] px; 'starved' tiles answered the whole-image value (< {HfdPsfEstimator.MinChunkStars} stars)");
        output.WriteLine("");
        output.WriteLine($"{"master",-44} {"tiles",5} {"starved",7} {"whole",6} {"min",6} {"p10",6} {"p50",6} {"p90",6} {"max",6} {"centre",6} {"corners",7} {"c/c",5} {"ms",6}");

        foreach (var path in masters)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!Image.TryReadFitsFile(path, out var master) || master is null)
            {
                output.WriteLine($"{name[..Math.Min(44, name.Length)],-44} (unreadable)");
                continue;
            }

            try
            {
                var (_, width, height) = master.Shape;
                var started = DateTime.UtcNow;
                var whole = await estimator.EstimateAsync(master, ct);
                var perTile = await deconvolver.EstimatePerChunkAsync(master, whole, ct);
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

                var border = AiNafnetInputs.StitchBorderPx;
                var layout = ChunkedInference.Layout(width + (2 * border), height + (2 * border), ChunkSize, Overlap);
                var radii = perTile.Select(Radius).ToArray();
                var starved = perTile.Count(v => v == whole);
                var measured = radii.Where((_, i) => perTile[i] != whole).OrderBy(r => r).ToArray();

                // The tile whose centre is nearest the frame centre, and the four whose centres are
                // nearest the corners, so a centre-to-corner ratio reads off the same grid.
                float Nearest(double tx, double ty)
                {
                    var best = 0;
                    var bestD = double.MaxValue;
                    for (var i = 0; i < layout.Length; i++)
                    {
                        var cx = layout[i].X + (layout[i].Width / 2.0);
                        var cy = layout[i].Y + (layout[i].Height / 2.0);
                        var d = ((cx - tx) * (cx - tx)) + ((cy - ty) * (cy - ty));
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    return radii[best];
                }

                var pw = width + (2 * border);
                var ph = height + (2 * border);
                var centre = Nearest(pw / 2.0, ph / 2.0);
                var corners = new[] { Nearest(0, 0), Nearest(pw, 0), Nearest(0, ph), Nearest(pw, ph) };
                var cornerMean = corners.Average();

                static float P(float[] sorted, double q) => sorted.Length == 0 ? float.NaN : sorted[Math.Clamp((int)(sorted.Length * q), 0, sorted.Length - 1)];

                output.WriteLine($"{name[..Math.Min(44, name.Length)],-44} {perTile.Length,5} {starved,7} {Radius(whole),6:F2} "
                    + $"{P(measured, 0.0),6:F2} {P(measured, 0.1),6:F2} {P(measured, 0.5),6:F2} {P(measured, 0.9),6:F2} {P(measured, 1.0),6:F2} "
                    + $"{centre,6:F2} {cornerMean,7:F2} {(cornerMean > 0 ? centre / cornerMean : float.NaN),5:F2} {elapsed,6:F0}");
            }
            finally
            {
                master.Release();
            }
        }

        output.WriteLine("");
        output.WriteLine("A frame whose p10 to p90 spans more than about 15 percent is one where a single psf01 tells most");
        output.WriteLine("tiles the wrong width; 'c/c' over 1 is the centre softer than the corners (the usual sign for a");
        output.WriteLine("refractor with field curvature is the opposite, under 1, so read the sign per optical train).");
    }
}
