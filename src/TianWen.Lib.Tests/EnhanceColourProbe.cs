using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Prints the colour-separation and extended-object table for a set of enhance outputs, so the
/// question "did this run keep the frame's colour, and did it keep the nebula" has one answer
/// everybody computes the same way.
///
/// <para>Env-gated and skips by default, matching <see cref="DebayerColourProbe"/>: point
/// <c>TIANWEN_ENHANCE_PROBE_REF</c> at the untouched sub and <c>TIANWEN_ENHANCE_PROBE_OUT</c> at a
/// directory of enhance outputs to compare against it. The reference must come FIRST and be
/// untouched -- the measurement region is chosen on it once and reused, so a reference that has
/// already been processed silently rebases every number in the table.</para>
///
/// <para>What it found on 2026-08-28, on a 3008x3008x1 RGGB sub of M8/M20 (SV605CC): handing the
/// AI steps a raw mosaic collapsed the R/G/B separation from 3.72% to 0.90% while
/// <c>sxt</c> + gradient correction alone left it at 3.72%, i.e. deconvolution and denoise are what
/// destroy an OSC frame's colour, and debayering first restores it to 3.19% at no measurable cost
/// to the nebula (99.9% of its contrast).</para>
/// </summary>
public class EnhanceColourProbe(ITestOutputHelper output)
{
    /// <summary>Fraction of frame width used for the extended-object box. ~4% is a nebula core.</summary>
    private const double BoxFraction = 0.0425;

    [Fact]
    public void ReportColourAndExtendedObjectRetention()
    {
        var refPath = Environment.GetEnvironmentVariable("TIANWEN_ENHANCE_PROBE_REF");
        var outDir = Environment.GetEnvironmentVariable("TIANWEN_ENHANCE_PROBE_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(refPath) || string.IsNullOrEmpty(outDir),
            "set TIANWEN_ENHANCE_PROBE_REF (the untouched sub) and TIANWEN_ENHANCE_PROBE_OUT (a dir of enhance outputs) to run this probe");
        Assert.SkipWhen(!File.Exists(refPath), $"reference frame not present: {refPath}");

        Image.TryReadFitsFile(refPath!, out var reference).ShouldBeTrue();
        reference.ShouldNotBeNull();

        // Region chosen ONCE, on the untouched reference, and expressed fractionally so the same
        // sky is measured on a half-res mosaic and a full-res RGB plate alike.
        var (fx, fy) = CfaColourMetrics.FindBrightestBox(reference, BoxFraction);
        var baseContrast = CfaColourMetrics.RegionContrast(reference, fx, fy, BoxFraction);
        output.WriteLine($"region from reference: fractional ({fx:F3},{fy:F3}) size {BoxFraction:P1} of width");
        output.WriteLine("");
        output.WriteLine($"{"run",-30} {"R",9} {"G",9} {"B",9} {"colour sep",11} {"|G1-G2|",10} {"object kept",12}");
        output.WriteLine(new string('-', 96));

        Report("REFERENCE (untouched)", reference);

        foreach (var path in Directory.EnumerateFiles(outDir!, "*.fit*").OrderBy(p => p))
        {
            if (!Image.TryReadFitsFile(path, out var img) || img is null)
            {
                output.WriteLine($"{Path.GetFileName(path),-30} <unreadable>");
                continue;
            }
            Report(Path.GetFileNameWithoutExtension(path), img);
        }

        void Report(string label, Image image)
        {
            var colour = CfaColourMetrics.MeasureColour(image);
            var contrast = CfaColourMetrics.RegionContrast(image, fx, fy, BoxFraction);
            var kept = baseContrast == 0 ? double.NaN : contrast / baseContrast * 100.0;
            var split = double.IsNaN(colour.GreenSplit) ? "     n/a" : colour.GreenSplit.ToString("F6");
            output.WriteLine(
                $"{label,-30} {colour.Levels.R,9:F5} {colour.Levels.G,9:F5} {colour.Levels.B,9:F5} " +
                $"{colour.SeparationPercent,10:F2}% {split,10} {kept,11:F1}%");
        }
    }

    /// <summary>
    /// The separation metric has to survive the thing it exists to detect, so pin it on synthetic
    /// data that needs no fixture: a mosaic with genuinely different photosite levels reads as
    /// separated, and the same mosaic with its colours averaged together -- what a spatial kernel
    /// does -- reads as flat. Ungated, unlike the report above.
    /// </summary>
    [Fact]
    public void SeparationFallsWhenPhotositesAreBlendedTogether()
    {
        const int n = 64;
        var separated = new float[n, n];
        var blended = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                separated[y, x] = (y % 2, x % 2) switch { (0, 0) => 0.30f, (1, 1) => 0.10f, _ => 0.20f };
                blended[y, x] = 0.20f;   // every photosite averaged into its neighbours
            }
        }
        var meta = new ImageMeta { SensorType = SensorType.RGGB };

        var withColour = CfaColourMetrics.MeasureColour(
            new Image([separated], BitDepth.Float32, 1f, 0f, 0f, meta));
        var withoutColour = CfaColourMetrics.MeasureColour(
            new Image([blended], BitDepth.Float32, 1f, 0f, 0f, meta));

        withColour.SeparationPercent.ShouldBeGreaterThan(50.0);
        withoutColour.SeparationPercent.ShouldBe(0.0, tolerance: 1e-9);
    }
}
