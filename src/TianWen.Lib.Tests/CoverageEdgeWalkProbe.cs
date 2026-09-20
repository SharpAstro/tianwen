using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the edge walk actually sees on a REAL master, swept across the trim fraction. A probe, not
/// an assertion: the answer comes from the master rather than from reasoning about it.
///
/// <para>The question it exists for: <see cref="CoverageEdgeWalkOptions.MaxTrimFraction"/> is read
/// twice in <c>Trim</c>, once as the cap on what an edge may lose and once as the depth the profile
/// must have SETTLED within. So a dither strip deeper than the cap is not trimmed to the cap, it is
/// refused outright and the master keeps the whole strip. Sweeping the one knob moves both meanings
/// together, which is exactly what this prints, and the depth at which each edge flips to settled is
/// the number a split knob needs.</para>
///
/// <para>Env-gated on <c>TIANWEN_CROP_PROBE</c> (a FITS master, or a directory of them) so a bare
/// <c>dotnet test</c> skips it: the files live outside the repo and are gigabytes of someone's
/// archive. Optional <c>TIANWEN_CROP_PROBE_LIMIT</c> caps how many a directory contributes.</para>
/// </summary>
public class CoverageEdgeWalkProbe(ITestOutputHelper output)
{
    /// <summary>The sweep. Spans the shipped 0.05 and the 10.2% strip that motivated the split.</summary>
    private static readonly double[] Fractions = [0.02, 0.05, 0.08, 0.10, 0.12, 0.15, 0.20, 0.25];

    [Fact]
    public void ReportWhatTheEdgeWalkSeesAcrossTheTrimFraction()
    {
        var target = Environment.GetEnvironmentVariable("TIANWEN_CROP_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target)),
            "set TIANWEN_CROP_PROBE to a FITS master or a directory of them");

        var limit = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_CROP_PROBE_LIMIT"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 12;

        var files = File.Exists(target)
            ? [target!]
            : Directory.EnumerateFiles(target!, "*.fits", SearchOption.TopDirectoryOnly)
                .Where(static f => !f.Contains(".rejection.", StringComparison.OrdinalIgnoreCase)
                                && !f.Contains(".coverage.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();

        output.WriteLine($"{files.Length} master(s)");

        foreach (var file in files)
        {
            if (!Image.TryReadFitsFile(file, out var image) || image is null)
            {
                output.WriteLine($"\n{Path.GetFileName(file)}: could not read");
                continue;
            }

            var full = new PixelRect(0, 0, image.Width, image.Height);
            output.WriteLine($"\n=== {Path.GetFileName(file)}  {image.Width}x{image.Height} ===");

            // The shipped verdict first, so the sweep below is read against what ships today.
            var shipped = CoverageEdgeWalk.Measure(image, full);
            output.WriteLine($"  shipped (MaxTrimFraction {CoverageEdgeWalkOptions.Default.MaxTrimFraction:F2}): "
                + Describe(shipped) + (shipped.AnyDeclined ? "   <- AT LEAST ONE EDGE DECLINED" : ""));

            output.WriteLine($"  {"frac",6} {"cap px",7} | {"left",-16} {"top",-16} {"right",-16} {"bottom",-16}");
            foreach (var f in Fractions)
            {
                var o = CoverageEdgeWalkOptions.Default with { MaxTrimFraction = f };
                var t = CoverageEdgeWalk.Measure(image, full, o);
                var capX = (int)(f * image.Width);
                output.WriteLine($"  {f,6:F2} {capX,7} | {Cell(t.Left)} {Cell(t.Top)} {Cell(t.Right)} {Cell(t.Bottom)}");
            }

            // The settle depth per edge: the shallowest cap at which that edge stops refusing. THIS is
            // the number the search fraction has to reach, and it is independent of what a caller is
            // willing to lose.
            output.WriteLine("  settles at: " + string.Join("  ", new[]
            {
                ("left", SettleFraction(image, full, static t => t.Left)),
                ("top", SettleFraction(image, full, static t => t.Top)),
                ("right", SettleFraction(image, full, static t => t.Right)),
                ("bottom", SettleFraction(image, full, static t => t.Bottom)),
            }.Select(static p => $"{p.Item1} {(p.Item2 is { } v ? v.ToString("F2", CultureInfo.InvariantCulture) : "never")}")));

            image.Release();
        }
    }

    private static string Cell(CoverageEdgeTrim t)
        => (t.Settled ? $"{t.Depth}px r={t.EdgeRatio:F2}" : $"DECLINED r={t.EdgeRatio:F2}").PadRight(16);

    private static string Describe(CoverageEdgeTrims t)
        => $"L {Cell(t.Left).Trim()}  T {Cell(t.Top).Trim()}  R {Cell(t.Right).Trim()}  B {Cell(t.Bottom).Trim()}";

    /// <summary>The shallowest swept fraction at which one edge reports settled, or null if none does.</summary>
    private static double? SettleFraction(Image image, PixelRect full, Func<CoverageEdgeTrims, CoverageEdgeTrim> pick)
    {
        foreach (var f in Fractions)
        {
            var t = CoverageEdgeWalk.Measure(image, full, CoverageEdgeWalkOptions.Default with { MaxTrimFraction = f });
            if (pick(t).Settled)
            {
                return f;
            }
        }

        return null;
    }
}
