using DIR.Lib;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Stat;
using TianWen.Lib.Tests.Helpers;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public sealed class ObservationScheduleVisualizationTests(ITestOutputHelper testOutputHelper)
{
    // Vienna, Austria — ~48.2°N, ~16.4°E
    private const double SiteLatitude = 48.2;
    private const double SiteLongitude = 16.4;
    private const byte MinHeight = 20;

    // Well-known DSO targets
    private static readonly Target M13 = new Target(16.695, 36.46, "M13", null);
    private static readonly Target M31 = new Target(0.712, 41.27, "M31", null);
    private static readonly Target M42 = new Target(5.588, -5.39, "M42", null);
    private static readonly Target M57 = new Target(18.893, 33.03, "M57", null);
    private static readonly Target NGC7000 = new Target(20.976, 44.53, "NGC 7000", null);

    // Target colors for the chart
    private static readonly RGBAColor32[] TargetColors =
    [
        Rgb("#E63946"),  // Red
        Rgb("#457B9D"),  // Steel blue
        Rgb("#2A9D8F"),  // Teal
        Rgb("#E9C46A"),  // Gold
        Rgb("#F4A261"),  // Orange
        Rgb("#264653"),  // Dark teal
    ];

    private record TwilightBoundaries(
        DateTimeOffset AstroDark,
        DateTimeOffset AstroTwilight,
        DateTimeOffset? CivilSet,
        DateTimeOffset? CivilRise,
        DateTimeOffset? NauticalSet,
        DateTimeOffset? NauticalRise);

    private static TwilightBoundaries ComputeTwilightBoundaries(Transform transform, DateTimeOffset astroDark, DateTimeOffset astroTwilight)
    {
        // Use astroDark to derive the evening day — NOT transform.DateTimeOffset,
        // which may have been mutated by CalculateNightWindow.
        // Subtract 12h so that post-midnight astroDark (e.g., 00:23) maps to the previous day's evening.
        var eveningDate = astroDark.AddHours(-12);
        var eveningDayStart = new DateTimeOffset(eveningDate.Date, eveningDate.Offset);
        // Morning events are on the day containing astroTwilight
        var morningDayStart = new DateTimeOffset(astroTwilight.Date, astroTwilight.Offset);

        DateTimeOffset? civilSet = null, civilRise = null;
        DateTimeOffset? nauticalSet = null, nauticalRise = null;

        // Evening SET events (dusk)
        transform.DateTimeOffset = eveningDayStart;
        var (_, _, civilS) = transform.EventTimes(EventType.CivilTwilight);
        if (civilS is { Count: >= 1 })
        {
            civilSet = eveningDayStart + civilS[0];
        }

        transform.DateTimeOffset = eveningDayStart;
        var (_, _, nautS) = transform.EventTimes(EventType.NauticalTwilight);
        if (nautS is { Count: >= 1 })
        {
            nauticalSet = eveningDayStart + nautS[0];
        }

        // Morning RISE events (dawn)
        transform.DateTimeOffset = morningDayStart;
        var (_, civilR, _) = transform.EventTimes(EventType.CivilTwilight);
        if (civilR is { Count: >= 1 })
        {
            civilRise = morningDayStart + civilR[0];
        }

        transform.DateTimeOffset = morningDayStart;
        var (_, nautR, _) = transform.EventTimes(EventType.NauticalTwilight);
        if (nautR is { Count: >= 1 })
        {
            nauticalRise = morningDayStart + nautR[0];
        }

        return new TwilightBoundaries(astroDark, astroTwilight, civilSet, civilRise, nauticalSet, nauticalRise);
    }

    [Theory]
    [InlineData("2025-06-15T00:00:00+02:00", "Vienna Summer", false)]
    [InlineData("2025-06-15T00:00:00+02:00", "Vienna Summer DawnDusk", true)]
    [InlineData("2025-12-15T00:00:00+01:00", "Vienna Winter", false)]
    [InlineData("2025-12-15T00:00:00+01:00", "Vienna Winter DawnDusk", true)]
    public async Task Schedule_MultiTarget_GeneratesAltitudeChart(string dateStr, string label, bool showDawnDusk)
    {
        var dto = DateTimeOffset.Parse(dateStr, System.Globalization.CultureInfo.InvariantCulture);
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = SiteLatitude,
            SiteLongitude = SiteLongitude,
            SiteElevation = 200,
            SiteTemperature = 15,
            DateTimeOffset = dto
        };

        var proposals = new[]
        {
            new ProposedObservation(M13, Priority: ObservationPriority.High),
            new ProposedObservation(M31, Priority: ObservationPriority.Normal),
            new ProposedObservation(M57, Priority: ObservationPriority.Normal),
            new ProposedObservation(NGC7000, Priority: ObservationPriority.Spare),
        };

        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);

        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            astroDark,
            astroTwilight,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(60)
        );

        tree.Count.ShouldBeGreaterThanOrEqualTo(1);

        // Score all targets (including spares) for elevation profiles
        var allTargets = proposals.Select(p => p.Target).Distinct().ToArray();
        var scores = new Dictionary<Target, ScoredTarget>();
        foreach (var target in allTargets)
        {
            scores[target] = ObservationScheduler.ScoreTarget(target, transform, astroDark, astroTwilight, MinHeight);
        }

        // Compute twilight boundaries for chart extent and zone drawing
        var twilight = ComputeTwilightBoundaries(transform, astroDark, astroTwilight);

        // Compute fine-grained altitude profiles
        var fineProfiles = new Dictionary<Target, List<(DateTimeOffset Time, double Alt)>>();
        foreach (var target in allTargets)
        {
            fineProfiles[target] = ComputeFineAltitudeProfile(transform, target, twilight, showDawnDusk);
        }

        // Collect spares per slot
        var sparesPerSlot = new Dictionary<int, List<ScheduledObservation>>();
        for (var i = 0; i < tree.Count; i++)
        {
            var spares = tree.GetSparesForSlot(i);
            if (!spares.IsEmpty)
            {
                sparesPerSlot[i] = [.. spares];
            }
        }

        // Draw the chart
        var fileName = $"schedule_{label.Replace(' ', '_')}.png";
        await DrawAltitudeChart(
            twilight, transform,
            allTargets, fineProfiles, tree, sparesPerSlot,
            fileName, label, showDawnDusk);

        testOutputHelper.WriteLine($"Night window: {astroDark:HH:mm} → {astroTwilight:HH:mm} ({(astroTwilight - astroDark).TotalHours:F1}h)");
        testOutputHelper.WriteLine($"Scheduled {tree.Count} primary observations, {sparesPerSlot.Values.Sum(s => s.Count)} spares");
        for (var i = 0; i < tree.Count; i++)
        {
            var obs = tree[i];
            testOutputHelper.WriteLine($"  [{i}] {obs.Target.Name} ({obs.Priority}) {obs.Start:HH:mm}→{obs.Start + obs.Duration:HH:mm}");
            if (sparesPerSlot.TryGetValue(i, out var spares))
            {
                foreach (var spare in spares)
                {
                    testOutputHelper.WriteLine($"       spare: {spare.Target.Name}");
                }
            }
        }
    }

    private static List<(DateTimeOffset Time, double Alt)> ComputeFineAltitudeProfile(
        Transform transform, Target target, TwilightBoundaries twilight, bool showDawnDusk)
    {
        var (start, end) = ChartTimeRange(twilight, showDawnDusk);
        var step = TimeSpan.FromMinutes(10);

        var profile = new List<(DateTimeOffset Time, double Alt)>();

        for (var t = start; t <= end; t += step)
        {
            transform.SetJ2000(target.RA, target.Dec);
            transform.JulianDateUTC = t.ToJulian();

            if (transform.ElevationTopocentric is double alt)
            {
                profile.Add((t, alt));
            }
        }

        return profile;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ChartTimeRange(TwilightBoundaries twilight, bool showDawnDusk)
    {
        if (showDawnDusk)
        {
            // Extend to civil twilight (dusk/dawn) with 15 min padding
            var start = twilight.CivilSet ?? twilight.AstroDark - TimeSpan.FromHours(1);
            var end = twilight.CivilRise ?? twilight.AstroTwilight + TimeSpan.FromHours(1);
            return (start - TimeSpan.FromMinutes(15), end + TimeSpan.FromMinutes(15));
        }

        // Default: 1 hour before/after astronomical night
        return (twilight.AstroDark - TimeSpan.FromHours(1), twilight.AstroTwilight + TimeSpan.FromHours(1));
    }

    private async Task DrawAltitudeChart(
        TwilightBoundaries twilight,
        Transform transform,
        Target[] targets,
        Dictionary<Target, List<(DateTimeOffset Time, double Alt)>> fineProfiles,
        ScheduledObservationTree tree,
        Dictionary<int, List<ScheduledObservation>> sparesPerSlot,
        string fileName,
        string label,
        bool showDawnDusk)
    {
        const int width = 1400;
        const int height = 800;
        const int xMargin = 100;
        const int yMargin = 60;
        const int legendHeight = 30;

        var plotW = width - (xMargin * 2);
        var plotH = height - yMargin - (yMargin + legendHeight);

        var (tStart, tEnd) = ChartTimeRange(twilight, showDawnDusk);
        var tRange = (tEnd - tStart).TotalHours;

        double TimeToX(DateTimeOffset t) => xMargin + ((t - tStart).TotalHours / tRange * plotW);
        double AltToY(double alt) => (yMargin + plotH) - (alt / 90.0 * plotH);

        // RgbaImageRenderer replaces Magick's MagickImage + Drawables pair:
        // it owns an RgbaImage buffer and provides Fill/Draw primitives that
        // mirror the Magick API surface we used here. PNG output via
        // SharpAstro.Png.PngWriter at the end (no Magick on the path).
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var background = Rgb("#1a1a2e");
        renderer.Surface.Clear(background);
        var fontPath = FontResolver.ResolveSystemFont();

        // --- Twilight zones ---
        DrawTwilightZones(renderer, fontPath, twilight, tStart, tEnd, TimeToX, yMargin, plotH);

        // --- Night window (dark background already covers it) ---

        // --- Grid lines and labels ---
        DrawGrid(renderer, fontPath, tStart, tEnd, TimeToX, AltToY, xMargin, yMargin, plotW, plotH);

        // --- Min altitude threshold ---
        // Dashed horizontal red line at the minimum-altitude cutoff with a
        // right-aligned "{MinHeight}°" label in the left gutter.
        var minAltY = AltToY(MinHeight);
        var minAltColor = Rgb("#FF6B6B");
        var minAltLineColor = minAltColor.WithAlpha(0x80); // half-strength so it doesn't dominate
        renderer.DrawLineDashed(xMargin, (float)minAltY, xMargin + plotW, (float)minAltY,
            minAltLineColor, dashLength: 6f, gapLength: 4f, thickness: 2);
        DrawAnchoredText(renderer, fontPath, $"{MinHeight}°", xMargin - 5, (float)minAltY + 4,
            fontSize: 10, minAltColor, hAnchor: TextAlign.Far);

        // --- Scheduled observation windows (rectangles) ---
        var targetColorMap = new Dictionary<Target, int>();
        for (var i = 0; i < targets.Length; i++)
        {
            targetColorMap[targets[i]] = i;
        }

        // Draw primary observation windows
        for (var i = 0; i < tree.Count; i++)
        {
            var obs = tree[i];
            var colorIdx = targetColorMap.GetValueOrDefault(obs.Target, 0) % TargetColors.Length;
            var color = TargetColors[colorIdx];

            var x1 = TimeToX(obs.Start);
            var x2 = TimeToX(obs.Start + obs.Duration);
            // 25% alpha translucent fill (Magick used ushort.MaxValue * 0.25 →
            // ~0x40 / 255). With RGBAColor32.WithAlpha we get the same band.
            var bandColor = color.WithAlpha((byte)(255 * 0.25));

            renderer.Surface.FillRect((int)x1, yMargin, (int)x2, yMargin + plotH, bandColor);
            renderer.DrawRectangle(
                new RectInt(new PointInt((int)x1, yMargin), new PointInt((int)x2, yMargin + plotH)),
                color, strokeWidth: 1);

            // Draw spare windows as dashed outlines
            if (sparesPerSlot.TryGetValue(i, out var spares))
            {
                foreach (var spare in spares)
                {
                    var spareColorIdx = targetColorMap.GetValueOrDefault(spare.Target, 0) % TargetColors.Length;
                    var spareColor = TargetColors[spareColorIdx];

                    // Dashed outline 2 px inside the primary band so both rectangles are visible.
                    DrawDashedRectangle(renderer,
                        x1 + 2, yMargin + 2, x2 - 2, yMargin + plotH - 2,
                        spareColor, dashLength: 4f, gapLength: 4f, thickness: 2);
                }
            }
        }

        // --- Altitude curves ---
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            var colorIdx = i % TargetColors.Length;
            var color = TargetColors[colorIdx];

            if (!fineProfiles.TryGetValue(target, out var profile) || profile.Count < 2)
            {
                continue;
            }

            // Filter to points with non-negative altitude, map to plot coordinates
            var visibleRaw = profile
                .Where(p => p.Alt >= 0)
                .Select(p => (X: TimeToX(p.Time), Y: AltToY(p.Alt)))
                .ToArray();

            if (visibleRaw.Length < 2)
            {
                continue;
            }

            // Smooth with Catmull-Rom spline
            var smoothed = CatmullRomSpline.Interpolate(visibleRaw, segmentsPerSpan: 8);
            // Project to the (float, float) tuples DrawPolyline expects.
            var visiblePoints = new (float X, float Y)[smoothed.Length];
            for (var j = 0; j < smoothed.Length; j++)
                visiblePoints[j] = ((float)smoothed[j].X, (float)smoothed[j].Y);

            // Check if this target is only used as spare
            var isSpare = true;
            for (var j = 0; j < tree.Count; j++)
            {
                if (tree[j].Target == target)
                {
                    isSpare = false;
                    break;
                }
            }

            if (isSpare)
            {
                renderer.DrawPolylineDashed(visiblePoints, color, dashLength: 6f, gapLength: 3f, thickness: 3);
            }
            else
            {
                renderer.DrawPolyline(visiblePoints, color, thickness: 3);
            }

            // Label at the peak
            var peak = profile.MaxBy(p => p.Alt);
            DrawAnchoredText(renderer, fontPath, target.Name,
                (float)TimeToX(peak.Time), (float)AltToY(peak.Alt) - 8,
                fontSize: 12, color, hAnchor: TextAlign.Center);
        }

        // --- Title ---
        DrawAnchoredText(renderer, fontPath,
            $"Observation Schedule — {label} ({SiteLatitude:F1}°N, {SiteLongitude:F1}°E)",
            width / 2f, 20f, fontSize: 16, Rgb("#FFFFFF"), hAnchor: TextAlign.Center);

        // --- Legend ---
        // Per-target swatch line + name, evenly spaced across the bottom row.
        var legendY = height - 20;
        var legendX = xMargin;
        for (var i = 0; i < targets.Length; i++)
        {
            var color = TargetColors[i % TargetColors.Length];
            renderer.DrawLine(legendX, legendY - 4, legendX + 20, legendY - 4, color, thickness: 2);
            DrawAnchoredText(renderer, fontPath, targets[i].Name,
                legendX + 25, legendY, fontSize: 11, color, hAnchor: TextAlign.Near);
            legendX += 120;
        }

        // Priority legend — "Primary (solid)" + "Spare (dashed)" entries
        // explain the line style convention used above.
        legendX += 40;
        var gray = Rgb("#808080");
        renderer.DrawLine(legendX, legendY - 4, legendX + 20, legendY - 4, gray, thickness: 2);
        DrawAnchoredText(renderer, fontPath, "Primary (solid)",
            legendX + 25, legendY, fontSize: 11, gray, hAnchor: TextAlign.Near);

        legendX += 130;
        renderer.DrawLineDashed(legendX, legendY - 4, legendX + 20, legendY - 4,
            gray, dashLength: 4f, gapLength: 4f, thickness: 2);
        DrawAnchoredText(renderer, fontPath, "Spare (dashed)",
            legendX + 25, legendY, fontSize: 11, gray, hAnchor: TextAlign.Near);

        // PNG out. RgbaImage.Pixels is already row-major interleaved RGBA bytes
        // which PngWriter.Encode wants verbatim — no swizzle / re-pack needed.
        var outputDir = SharedTestData.CreateTempTestOutputDir(nameof(ObservationScheduleVisualizationTests));
        var fullPath = Path.Combine(outputDir, fileName);
        var png = DisplayImageWriter.EncodePng(renderer.Surface.Pixels, width, height);
        await File.WriteAllBytesAsync(fullPath, png);

        testOutputHelper.WriteLine($"Wrote altitude chart to: {fullPath}");
    }

    private static void DrawTwilightZones(
        RgbaImageRenderer renderer,
        string fontPath,
        TwilightBoundaries twilight,
        DateTimeOffset tStart,
        DateTimeOffset tEnd,
        Func<DateTimeOffset, double> timeToX,
        int yMargin,
        int plotH)
    {
        // Zone colors from lightest (civil) to darkest (night)
        var civilColor = Rgb("#3a3a5e");
        var nauticalColor = Rgb("#2a2a4e");
        var astroColor = Rgb("#22223e");

        // Collect all zones as (x1, x2, color, label) for drawing
        var zones = new List<(double X1, double X2, RGBAColor32 Color, string Label, bool Fill)>();

        var x0 = timeToX(tStart);
        var xEnd = timeToX(tEnd);
        var xAstroDark = timeToX(twilight.AstroDark);
        var xAstroTwilight = timeToX(twilight.AstroTwilight);

        // Evening zones
        if (twilight.CivilSet.HasValue)
        {
            var xCivilSet = timeToX(twilight.CivilSet.Value);
            zones.Add((x0, xCivilSet, civilColor, "Civil", true));

            if (twilight.NauticalSet.HasValue)
            {
                var xNautSet = timeToX(twilight.NauticalSet.Value);
                zones.Add((xCivilSet, xNautSet, nauticalColor, "Nautical", true));
                zones.Add((xNautSet, xAstroDark, astroColor, "Astro", true));
            }
            else
            {
                zones.Add((xCivilSet, xAstroDark, nauticalColor, "Nautical", true));
            }
        }

        // Night zone (between astro dark and astro twilight — already background color)
        // Fill=false so we still emit the label but don't paint over the background.
        zones.Add((xAstroDark, xAstroTwilight, default, "Night", false));

        // Morning zones
        if (twilight.CivilRise.HasValue)
        {
            var xCivilRise = timeToX(twilight.CivilRise.Value);

            if (twilight.NauticalRise.HasValue)
            {
                var xNautRise = timeToX(twilight.NauticalRise.Value);
                zones.Add((xAstroTwilight, xNautRise, astroColor, "Astro", true));
                zones.Add((xNautRise, xCivilRise, nauticalColor, "Nautical", true));
            }
            else
            {
                zones.Add((xAstroTwilight, xCivilRise, nauticalColor, "Nautical", true));
            }

            zones.Add((xCivilRise, xEnd, civilColor, "Civil", true));
        }

        // Draw zone rectangles (plot area only)
        foreach (var (x1, x2, color, _, fill) in zones)
        {
            if (fill)
            {
                renderer.Surface.FillRect((int)x1, yMargin, (int)x2, yMargin + plotH, color);
            }
        }

        // Draw zone labels above the 90° line
        var labelY = yMargin - 10;
        var divider = Rgb("#FFFFFF").WithAlpha(0x60);
        var labelColor = Rgb("#FFFFFF");

        foreach (var (x1, x2, _, lab, _) in zones)
        {
            var bandWidth = x2 - x1;
            if (bandWidth <= 20)
            {
                continue;
            }

            var cx = (x1 + x2) / 2.0;

            // Draw a thin line at each zone boundary dropping from the label area to the plot
            renderer.DrawLineDashed((float)x1, labelY + 4, (float)x1, yMargin,
                divider, dashLength: 2f, gapLength: 3f);
            renderer.DrawLineDashed((float)x2, labelY + 4, (float)x2, yMargin,
                divider, dashLength: 2f, gapLength: 3f);

            var fontSize = bandWidth < 50 ? 9f : 11f;
            DrawAnchoredText(renderer, fontPath, lab,
                (float)cx, labelY, fontSize, labelColor, hAnchor: TextAlign.Center);
        }
    }

    private static void DrawGrid(
        RgbaImageRenderer renderer,
        string fontPath,
        DateTimeOffset tStart,
        DateTimeOffset tEnd,
        Func<DateTimeOffset, double> timeToX,
        Func<double, double> altToY,
        int xMargin,
        int yMargin,
        int plotW,
        int plotH)
    {
        var gridColor = Rgb("#FFFFFF").WithAlpha(0x30);
        var textColor = Rgb("#CCCCCC");

        // Altitude grid lines (every 10°)
        for (var alt = 0; alt <= 90; alt += 10)
        {
            var y = altToY(alt);
            renderer.DrawLine(xMargin, (float)y, xMargin + plotW, (float)y, gridColor);
            DrawAnchoredText(renderer, fontPath, $"{alt}°",
                xMargin - 5, (float)y + 4, fontSize: 10, textColor, hAnchor: TextAlign.Far);
        }

        // Time grid lines (every hour)
        var firstHour = new DateTimeOffset(
            tStart.Year, tStart.Month, tStart.Day,
            tStart.Hour + 1, 0, 0, tStart.Offset);

        for (var t = firstHour; t < tEnd; t = t.AddHours(1))
        {
            var x = timeToX(t);
            renderer.DrawLine((float)x, yMargin, (float)x, yMargin + plotH, gridColor);
            DrawAnchoredText(renderer, fontPath, t.ToString("HH:mm"),
                (float)x, yMargin + plotH + 15, fontSize: 10, textColor, hAnchor: TextAlign.Center);
        }

        // Axes — solid 1-pixel lines on the left + bottom edges of the plot area.
        var axisColor = Rgb("#FFFFFF").WithAlpha(0x60);
        renderer.DrawLine(xMargin, yMargin, xMargin, yMargin + plotH, axisColor);
        renderer.DrawLine(xMargin, yMargin + plotH, xMargin + plotW, yMargin + plotH, axisColor);

        // Axis labels
        DrawAnchoredText(renderer, fontPath, "Local Time",
            xMargin + plotW / 2f, yMargin + plotH + 40,
            fontSize: 12, textColor, hAnchor: TextAlign.Center);
    }

    /// <summary>Parses an #RRGGBB hex string into <see cref="RGBAColor32"/>
    /// with alpha = 0xFF. Magick.NET took these directly; DIR.Lib's
    /// <see cref="RGBAColor32"/> ctor wants three bytes, so we tokenise here.</summary>
    private static RGBAColor32 Rgb(string hex)
    {
        var s = hex.AsSpan().TrimStart('#');
        var r = byte.Parse(s[..2], System.Globalization.NumberStyles.HexNumber);
        var g = byte.Parse(s.Slice(2, 2), System.Globalization.NumberStyles.HexNumber);
        var b = byte.Parse(s.Slice(4, 2), System.Globalization.NumberStyles.HexNumber);
        return new RGBAColor32(r, g, b, 0xFF);
    }

    /// <summary>Draws text with a Magick.NET-style anchor point. Magick's
    /// <c>Text(x, y, str)</c> with <c>TextAlignment.Left|Center|Right</c>
    /// treats (x, y) as the baseline-anchored point with horizontal
    /// alignment relative to x. DIR.Lib's <see cref="RgbaImageRenderer.DrawText"/>
    /// uses a layout rect + alignment within. We translate by measuring
    /// the text first and computing a layout rect of exactly the measured
    /// size, offset so the anchor lands where Magick would have placed it.</summary>
    private static void DrawAnchoredText(
        RgbaImageRenderer renderer,
        string fontPath,
        string text,
        float anchorX,
        float anchorY,
        float fontSize,
        RGBAColor32 color,
        TextAlign hAnchor = TextAlign.Near)
    {
        if (string.IsNullOrEmpty(text)) return;
        var (w, h) = renderer.MeasureText(text, fontPath, fontSize);
        var layoutX = hAnchor switch
        {
            TextAlign.Near => (int)anchorX,
            TextAlign.Center => (int)(anchorX - w / 2f),
            TextAlign.Far => (int)(anchorX - w),
            _ => (int)anchorX,
        };
        // Magick.NET's y in Text(x, y, str) is the baseline. DIR.Lib's
        // layout rect is top-anchored, so we shift up by the measured
        // height to put the baseline approximately at anchorY.
        var layoutY = (int)(anchorY - h);
        var rect = new RectInt(
            new PointInt(layoutX, layoutY),
            new PointInt(layoutX + (int)MathF.Ceiling(w), layoutY + (int)MathF.Ceiling(h)));
        renderer.DrawText(text.AsSpan(), fontPath, fontSize, color, rect,
            horizAlignment: hAnchor, vertAlignment: TextAlign.Near);
    }

    /// <summary>Renders a dashed rectangle outline as four dashed lines.
    /// DIR.Lib has <see cref="RgbaImageRenderer.DrawLineDashed"/> but no
    /// direct DrawRectangleDashed — emit the four edges manually.</summary>
    private static void DrawDashedRectangle(
        RgbaImageRenderer renderer,
        double x1, double y1, double x2, double y2,
        RGBAColor32 color,
        float dashLength, float gapLength, int thickness)
    {
        renderer.DrawLineDashed((float)x1, (float)y1, (float)x2, (float)y1, color, dashLength, gapLength, thickness);
        renderer.DrawLineDashed((float)x2, (float)y1, (float)x2, (float)y2, color, dashLength, gapLength, thickness);
        renderer.DrawLineDashed((float)x2, (float)y2, (float)x1, (float)y2, color, dashLength, gapLength, thickness);
        renderer.DrawLineDashed((float)x1, (float)y2, (float)x1, (float)y1, color, dashLength, gapLength, thickness);
    }
}
