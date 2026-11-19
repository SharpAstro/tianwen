using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Measures, on real frames, how far from the true plate scale the scale-free quad recovery still answers: the reach of
/// one rung of a scale ladder (#2249), which is what decides how far apart the rungs may stand.
/// </summary>
/// <remarks>
/// <para>Env-gated on <c>TIANWEN_SCALE_LADDER_FRAMES</c>, so it skips by default and holds no path. The value is a
/// list of frames separated by <c>;</c>, each <c>path</c> or <c>path|raHours|decDeg|arcsecPerPixel</c>: the three
/// numbers are needed only for a frame whose header carries no solved WCS (they are the hint and the truth the reach is
/// read against).</para>
/// <para>Per frame and per believed scale it reports three ways of choosing the two star sets a rung compares:
/// <b>as shipped</b> (the catalogue stars inside the believed frame plus its margin, the brightest 500 of each side
/// taken independently, which is what the solver's own recovery does), <b>equal counts</b> (the same windows, both
/// sides cut to the smaller count) and <b>disc</b> (both sides cut to the disc that is in frame at any camera angle,
/// then to equal counts).</para>
/// </remarks>
[Collection("Imaging")]
public class ScaleLadderProbe(ITestOutputHelper output)
{
    private const string FramesVar = "TIANWEN_SCALE_LADDER_FRAMES";

    /// <summary>Believed scale over true scale, for the reach table.</summary>
    private static readonly double[] BelievedOverTrue =
        [0.25, 0.33, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0];

    /// <summary>Keeps the last line <see cref="QuadScaleRecovery"/> logged, which is where a refusal says why.</summary>
    private sealed class LastLineLogger : ILogger
    {
        public string Last { get; private set; } = "";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Last = formatter(state, exception);
    }

    [Fact]
    public async Task ReportHowFarOneRungReaches()
    {
        var frames = Environment.GetEnvironmentVariable(FramesVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(frames), $"{FramesVar} not set");

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var solver = new CatalogPlateSolver(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        foreach (var entry in frames!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('|');
            var path = parts[0];
            if (!Image.TryReadFitsFile(path, out var image, out var fileWcs))
            {
                output.WriteLine($"could not read {path}");
                continue;
            }

            WCS origin;
            double trueScale;
            if (parts.Length >= 4)
            {
                origin = new WCS(double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture));
                trueScale = double.Parse(parts[3], CultureInfo.InvariantCulture);
            }
            else if (fileWcs is { HasCDMatrix: true, PixelScaleArcsec: > 0 } solved)
            {
                origin = new WCS(solved.CenterRA, solved.CenterDec);
                trueScale = solved.PixelScaleArcsec;
            }
            else
            {
                output.WriteLine($"{System.IO.Path.GetFileName(path)}: no solved WCS in the header and no hint given; skipped");
                continue;
            }

            var sw = Stopwatch.StartNew();
            var detected = new List<ImagedStar>(await solver.DetectStarsAsync(image, ct));
            var detectMs = sw.ElapsedMilliseconds;
            detected.Sort(static (a, b) => b.Flux.CompareTo(a.Flux));

            double cx = image.Width / 2.0, cy = image.Height / 2.0;
            var discRadius = Math.Min(image.Width, image.Height) / 2.0;
            var all = new List<Vector2>(detected.Count);
            var inDisc = new List<Vector2>(detected.Count);
            foreach (var s in detected)
            {
                var p = new Vector2(s.XCentroid, s.YCentroid);
                all.Add(p);
                if (((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)) <= discRadius * discRadius)
                {
                    inDisc.Add(p);
                }
            }

            // One query wide enough for the widest believed frame of the table, its margin included.
            var widest = BelievedOverTrue[^1] * trueScale;
            var halfDiagonalDeg = 0.5 * widest * Math.Sqrt(((double)image.Width * image.Width) + ((double)image.Height * image.Height)) / 3600.0;
            var radiusDeg = Math.Min(85.0, halfDiagonalDeg * 1.2);
            var exposureStart = image.ImageMeta.ExposureStartTime;
            var dtYr = exposureStart.Year > 1900 ? exposureStart.JulianYearsSinceJ2000() : 0.0;
            sw.Restart();
            var catalog = solver.QueryCatalogStarsInRegion(origin, radiusDeg, dtYr);
            var queryMs = sw.ElapsedMilliseconds;

            output.WriteLine("");
            output.WriteLine($"{System.IO.Path.GetFileName(path)}");
            output.WriteLine($"  {image.Width}x{image.Height}, true scale {trueScale:F3} arcsec/px, field {trueScale * image.Width / 3600:F2} x {trueScale * image.Height / 3600:F2} deg, "
                + $"hint RA {origin.CenterRA:F4} h Dec {origin.CenterDec:F3}");
            output.WriteLine($"  detected {detected.Count} (in the disc {inDisc.Count}) in {detectMs} ms; catalogue {catalog.Count} stars within {radiusDeg:F1} deg in {queryMs} ms");
            output.WriteLine("  believed/true   in frame  as shipped: cand  implied/true  spread | equal counts: n  cand  implied/true  spread |   in disc  disc: n  cand  implied/true  spread   ms");

            foreach (var factor in BelievedOverTrue)
            {
                var believed = factor * trueScale;
                var dim = new ImageDim(believed, image.Width, image.Height);
                var scaleRad = double.DegreesToRadians(believed / 3600.0);
                sw.Restart();

                var rect = Points(CatalogPlateSolver.ProjectCatalogStars(catalog, origin, scaleRad, cx, cy, dim, xSign: 1.0));
                var disc = Points(CatalogPlateSolver.ProjectCatalogStars(catalog, origin, scaleRad, cx, cy, dim, xSign: 1.0, rotationInvariant: true));

                var shipped = Recover(all.ToArray(), rect, factor);
                var equal = Math.Min(Math.Min(all.Count, rect.Length), QuadScaleRecovery.MaxStars);
                var equalCounts = Recover(all.ToArray()[..equal], rect[..equal], factor);
                var discEqual = Math.Min(Math.Min(inDisc.Count, disc.Length), QuadScaleRecovery.MaxStars);
                var discCounts = Recover(inDisc.ToArray()[..discEqual], disc[..discEqual], factor);

                output.WriteLine($"  {factor,13:F2} {rect.Length,10}  {shipped,34} | {equal,15} {equalCounts,28} | {disc.Length,9} {discEqual,8} {discCounts,28} {sw.ElapsedMilliseconds,4}");
            }
        }
    }

    /// <summary>
    /// At the TRUE scale, which slice of the catalogue's brightness ranking the detections' brightest 500 share quads
    /// with. A frame whose brightest stars the detector does not return (saturated, bloated) has its top 500 further
    /// down the catalogue's ranking than rank 0, and then no rung of a ladder answers however right its scale is.
    /// </summary>
    [Fact]
    public async Task ReportWhichCatalogueDepthAnswersAtTheTrueScale()
    {
        var frames = Environment.GetEnvironmentVariable(FramesVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(frames), $"{FramesVar} not set");

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var solver = new CatalogPlateSolver(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        int[] catalogueFrom = [0, 100, 250, 500, 1000, 2000, 4000, 8000];
        int[] detectedFrom = [0, 100, 250, 500, 1000];

        foreach (var entry in frames!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('|');
            if (!Image.TryReadFitsFile(parts[0], out var image, out var fileWcs))
            {
                continue;
            }

            WCS origin;
            double trueScale;
            if (parts.Length >= 4)
            {
                origin = new WCS(double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture));
                trueScale = double.Parse(parts[3], CultureInfo.InvariantCulture);
            }
            else if (fileWcs is { HasCDMatrix: true, PixelScaleArcsec: > 0 } solved)
            {
                origin = new WCS(solved.CenterRA, solved.CenterDec);
                trueScale = solved.PixelScaleArcsec;
            }
            else
            {
                continue;
            }

            var detected = new List<ImagedStar>(await solver.DetectStarsAsync(image, ct));
            detected.Sort(static (a, b) => b.Flux.CompareTo(a.Flux));
            var all = new Vector2[detected.Count];
            for (var i = 0; i < all.Length; i++)
            {
                all[i] = new Vector2(detected[i].XCentroid, detected[i].YCentroid);
            }

            double cx = image.Width / 2.0, cy = image.Height / 2.0;
            var dim = new ImageDim(trueScale, image.Width, image.Height);
            var scaleRad = double.DegreesToRadians(trueScale / 3600.0);
            var radiusDeg = Math.Min(85.0, 0.6 * trueScale * Math.Sqrt(((double)image.Width * image.Width) + ((double)image.Height * image.Height)) / 3600.0);
            var exposureStart = image.ImageMeta.ExposureStartTime;
            var dtYr = exposureStart.Year > 1900 ? exposureStart.JulianYearsSinceJ2000() : 0.0;
            var catalog = solver.QueryCatalogStarsInRegion(origin, radiusDeg, dtYr);
            var rect = Points(CatalogPlateSolver.ProjectCatalogStars(catalog, origin, scaleRad, cx, cy, dim, xSign: 1.0));

            output.WriteLine("");
            output.WriteLine($"{System.IO.Path.GetFileName(parts[0])}");
            output.WriteLine($"  true scale {trueScale:F3} arcsec/px, detected {all.Length}, catalogue in frame {rect.Length}; candidates (implied/true) by the first rank of each side's 500");
            var header = "  detections from \\ catalogue from";
            foreach (var c in catalogueFrom)
            {
                header += $"{c,16}";
            }

            output.WriteLine(header);
            foreach (var d in detectedFrom)
            {
                if (d + QuadScaleRecovery.MinStars > all.Length)
                {
                    break;
                }

                var line = $"  {d,32}";
                var det = all.AsSpan(d, Math.Min(QuadScaleRecovery.MaxStars, all.Length - d));
                foreach (var c in catalogueFrom)
                {
                    if (c + QuadScaleRecovery.MinStars > rect.Length)
                    {
                        line += $"{"-",16}";
                        continue;
                    }

                    var cat = rect.AsSpan(c, Math.Min(QuadScaleRecovery.MaxStars, rect.Length - c));
                    var log = new LastLineLogger();
                    line += QuadScaleRecovery.TryRecover(det, cat, log) is { } r
                        ? $"{r.Candidates,8} ({1.0 / r.Ratio:F3})"
                        : $"{"none",16}";
                }

                output.WriteLine(line);
            }
        }
    }

    /// <summary>
    /// What the ladder itself answers on each frame, told nothing but the hint, and how that holds as the hint moves off
    /// the frame's centre by a fraction of its long side (north, so the offset is a real angle on the sky).
    /// </summary>
    [Fact]
    public async Task ReportWhatTheLadderAnswers()
    {
        var frames = Environment.GetEnvironmentVariable(FramesVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(frames), $"{FramesVar} not set");

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var solver = new CatalogPlateSolver(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        double[] hintOffFractions = [0.0, 0.25, 0.5, 0.75];

        foreach (var entry in frames!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('|');
            if (!Image.TryReadFitsFile(parts[0], out var image, out var fileWcs))
            {
                continue;
            }

            WCS origin;
            double trueScale;
            if (parts.Length >= 4)
            {
                origin = new WCS(double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture));
                trueScale = double.Parse(parts[3], CultureInfo.InvariantCulture);
            }
            else if (fileWcs is { HasCDMatrix: true, PixelScaleArcsec: > 0 } solved)
            {
                origin = new WCS(solved.CenterRA, solved.CenterDec);
                trueScale = solved.PixelScaleArcsec;
            }
            else
            {
                continue;
            }

            var sw = Stopwatch.StartNew();
            var detected = new List<ImagedStar>(await solver.DetectStarsAsync(image, ct));
            var detectMs = sw.ElapsedMilliseconds;
            detected.Sort(static (a, b) => b.Flux.CompareTo(a.Flux));
            var points = new Vector2[detected.Count];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new Vector2(detected[i].XCentroid, detected[i].YCentroid);
            }

            var exposureStart = image.ImageMeta.ExposureStartTime;
            var dtYr = exposureStart.Year > 1900 ? exposureStart.JulianYearsSinceJ2000() : 0.0;
            var longSideDeg = trueScale * Math.Max(image.Width, image.Height) / 3600.0;

            output.WriteLine("");
            output.WriteLine($"{System.IO.Path.GetFileName(parts[0])}");
            output.WriteLine($"  {image.Width}x{image.Height}, true scale {trueScale:F3} arcsec/px, long side {longSideDeg:F2} deg, {detected.Count} detected in {detectMs} ms");
            foreach (var off in hintOffFractions)
            {
                var hint = new WCS(origin.CenterRA, Math.Clamp(origin.CenterDec + (off * longSideDeg), -89.9, 89.9));
                sw.Restart();
                var catalogue = solver.QueryCatalogStarsInRegion(hint, ScaleLadder.QueryRadiusDeg(image.Width, image.Height), dtYr);
                var queryMs = sw.ElapsedMilliseconds;
                sw.Restart();
                var found = ScaleLadder.TryFind(points, catalogue, hint, image.Width, image.Height, cancellationToken: ct);
                var ladderMs = sw.ElapsedMilliseconds;
                output.WriteLine(found is { } f
                    ? $"  hint off by {off:F2} of the long side: {f.ScaleArcsec:F4} arcsec/px ({(f.ScaleArcsec / trueScale) - 1.0:+0.00%;-0.00%;0.00%} off the truth), rung {f.RungsAsked} at {f.RungScaleArcsec:F3}, "
                        + $"{f.Candidates} candidates, confirmed by {f.ConfirmCandidates}; query {catalogue.Count} stars {queryMs} ms, ladder {ladderMs} ms"
                    : $"  hint off by {off:F2} of the long side: no answer; query {catalogue.Count} stars {queryMs} ms, ladder {ladderMs} ms");
            }
        }
    }

    private static Vector2[] Points(List<CatalogPlateSolver.ProjectedCatalogStar> projected)
    {
        var points = new Vector2[projected.Count];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new Vector2(projected[i].Pixel.XCentroid, projected[i].Pixel.YCentroid);
        }

        return points;
    }

    /// <summary>
    /// One recovery, as a table cell: the candidates, the scale the stars imply over the true one, and the spread; or why
    /// it was refused. <paramref name="believedOverTrue"/> turns the recovered ratio (detected pixels per projected pixel)
    /// into that quotient: the implied scale is the believed one over the ratio.
    /// </summary>
    private static string Recover(ReadOnlySpan<Vector2> detected, ReadOnlySpan<Vector2> projected, double believedOverTrue)
    {
        var log = new LastLineLogger();
        if (QuadScaleRecovery.TryRecover(detected, projected, log) is { } r)
        {
            return $"{r.Candidates,4}  {believedOverTrue / r.Ratio,10:F4}  {r.RelativeSpread,6:F4}";
        }

        // A refusal's own words, shortened to what the table can carry.
        var why = log.Last;
        var cut = why.IndexOf("QuadScaleRecovery: ", StringComparison.Ordinal);
        why = cut >= 0 ? why[(cut + "QuadScaleRecovery: ".Length)..] : why;
        return why.Length == 0 ? "too few stars" : why.Length > 26 ? why[..26] : why;
    }
}
