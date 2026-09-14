using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nom.tam.fits;
using nom.tam.util;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Opt-in: the viewer's auto-crop (<see cref="ViewerActions.ScanForCrop"/>, the shipped decision path)
/// over every retained master of a dataset bake named by <c>TIANWEN_CROP_BAKE_ROOT</c>, and what the
/// crop does to the exporter's stretch gate. Per master: the strategy, the crop rectangle and its share
/// of the canvas, whether the coverage sidecar or the edge walk answered and whether an edge declined,
/// the exact-zero and non-finite pixels left INSIDE the crop, and the SAS auto-detect statistic
/// (median of channel 0 over its minimum, unit scale; 0.125 refuses) on the whole frame and inside
/// the crop. Skips without the variable; <c>TIANWEN_CROP_PROBE_MAX</c> caps the count, 0 for all.
/// </summary>
public class MasterAutoCropProbe(ITestOutputHelper output)
{
    private const string RootVar = "TIANWEN_CROP_BAKE_ROOT";
    private const string MaxVar = "TIANWEN_CROP_PROBE_MAX";
    private const float GateThreshold = 0.125f;

    [Fact]
    public void ReportTheAutoCropOverABakesMasters()
    {
        var root = Environment.GetEnvironmentVariable(RootVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{RootVar} not set");
        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");

        var maxMasters = int.TryParse(Environment.GetEnvironmentVariable(MaxVar), out var m) ? m : 0;
        var all = Directory.GetFiles(mastersDir, "*.fits").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var masters = maxMasters > 0 && all.Length > maxMasters ? all.Take(maxMasters).ToArray() : all;

        output.WriteLine($"masters  {masters.Length} of {all.Length} in {mastersDir}");
        output.WriteLine("crop     ViewerActions.ScanForCrop(image, path): the coverage sidecar's exact tier when present, else the union rectangle trimmed by the edge-noise walk");
        output.WriteLine($"gate     median(ch0 - min ch0) on the unit-scaled frame; {GateThreshold} or over reads as already stretched and the exporter refuses the session");
        output.WriteLine("");
        output.WriteLine($"{"master",-44} {"strategy",-13} {"canvas",11} {"crop",22} {"share",6} {"src",4} {"decl",4} {"zeros in",8} {"nan in",7} {"gate",7} {"gate in",7}");

        var shares = new List<double>();
        var zerosInside = new List<int>();
        var admitted = 0;
        var refusedWhole = 0;
        foreach (var path in masters)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var shortName = name[..Math.Min(44, name.Length)];
            var strategy = ReadCard(path, "STRATEGY") ?? "?";
            if (!Image.TryReadFitsFile(path, out var image) || image is null)
            {
                output.WriteLine($"{shortName,-44} (unreadable)");
                continue;
            }

            try
            {
                var unit = image.ScaleFloatValuesToUnitInPlace();
                var scan = ViewerActions.ScanForCrop(unit, path);
                var r = scan.Rect;
                var share = (double)r.Width * r.Height / ((double)unit.Width * unit.Height);
                var (zeros, nans) = CountAbsentInside(unit, r.X, r.Y, r.Width, r.Height);
                var gateWhole = GateStatistic(unit, 0, 0, unit.Width, unit.Height);
                var gateInside = GateStatistic(unit, r.X, r.Y, r.Width, r.Height);
                shares.Add(share);
                zerosInside.Add(zeros);
                if (gateWhole >= GateThreshold)
                {
                    refusedWhole++;
                    if (gateInside < GateThreshold)
                    {
                        admitted++;
                    }
                }

                output.WriteLine($"{shortName,-44} {strategy,-13} {unit.Width,5}x{unit.Height,-5} [{r.X,4},{r.Y,4} {r.Width,5}x{r.Height,-5}] {share,6:F3} {(scan.FromCoverage ? "cov" : "walk"),4} {(scan.Declined ? "yes" : "no"),4} {zeros,8} {nans,7} {gateWhole,7:F4} {gateInside,7:F4}"
                    + (gateWhole >= GateThreshold ? (gateInside < GateThreshold ? "  <- refused whole, admitted cropped" : "  <- refused whole AND cropped") : string.Empty));
            }
            finally
            {
                image.Release();
            }
        }

        shares.Sort();
        zerosInside.Sort();
        output.WriteLine("");
        output.WriteLine($"share of canvas kept: p10 {Pct(shares, 0.10):F3} p50 {Pct(shares, 0.50):F3} p90 {Pct(shares, 0.90):F3} min {(shares.Count > 0 ? shares[0] : double.NaN):F3}");
        output.WriteLine($"exact-zero pixels left inside the crop: masters with none {zerosInside.Count(z => z == 0)} of {zerosInside.Count}; p90 {(zerosInside.Count > 0 ? zerosInside[(int)(0.9 * (zerosInside.Count - 1))] : 0)}; max {(zerosInside.Count > 0 ? zerosInside[^1] : 0)}");
        output.WriteLine($"stretch gate: {refusedWhole} refused on the whole frame, {admitted} of them admitted inside the crop");
    }

    private static double Pct(List<double> sorted, double p)
        => sorted.Count == 0 ? double.NaN : sorted[(int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1)];

    private static string? ReadCard(string path, string key)
    {
        using var reader = new BufferedFile(path, FileAccess.Read, FileShare.Read, 4 * 2880);
        using var fits = new Fits(reader, path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
        var header = fits.ReadFirstImageHduHeaderOnly()?.Header;
        return header?.ContainsKey(key) == true ? header.GetStringValue(key) : null;
    }

    /// <summary>Pixels inside the rectangle that are exact zero in EVERY channel, and non-finite in any.</summary>
    private static (int Zeros, int NonFinite) CountAbsentInside(Image image, int x0, int y0, int w, int h)
    {
        var channels = image.ChannelCount;
        var zeros = 0;
        var nans = 0;
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var allZero = true;
                var anyNan = false;
                for (var c = 0; c < channels; c++)
                {
                    var v = image.GetChannelSpan(c)[y * image.Width + x];
                    if (float.IsNaN(v) || float.IsInfinity(v))
                    {
                        anyNan = true;
                    }
                    else if (v != 0f)
                    {
                        allZero = false;
                    }
                }

                if (anyNan)
                {
                    nans++;
                }
                else if (allZero)
                {
                    zeros++;
                }
            }
        }

        return (zeros, nans);
    }

    /// <summary>The SAS auto-detect statistic (`ChunkedNafnetRunner.NeedsStretch`) over a rectangle of channel 0.</summary>
    private static float GateStatistic(Image image, int x0, int y0, int w, int h)
    {
        var ch0 = image.GetChannelSpan(0);
        var min = float.PositiveInfinity;
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var v = ch0[y * image.Width + x];
                if (!float.IsNaN(v) && v < min)
                {
                    min = v;
                }
            }
        }

        if (float.IsPositiveInfinity(min))
        {
            return float.NaN;
        }

        var values = new float[w * h];
        var count = 0;
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var v = ch0[y * image.Width + x];
                if (!float.IsNaN(v))
                {
                    values[count++] = v - min;
                }
            }
        }

        return count == 0 ? float.NaN : StatisticsHelper.MedianFast(values.AsSpan(0, count));
    }
}
