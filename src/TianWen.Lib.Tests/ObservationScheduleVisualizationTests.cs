using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using ImageMagick.Drawing;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Stat;
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
    private static readonly MagickColor[] TargetColors =
    [
        new MagickColor("#E63946"),  // Red
        new MagickColor("#457B9D"),  // Steel blue
        new MagickColor("#2A9D8F"),  // Teal
        new MagickColor("#E9C46A"),  // Gold
        new MagickColor("#F4A261"),  // Orange
        new MagickColor("#264653"),  // Dark teal
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

        using var image = new MagickImage(new MagickColor("#1a1a2e"), width, height)
        {
            Format = MagickFormat.Png
        };

        var drawables = new Drawables();

        // --- Twilight zones ---
        DrawTwilightZones(drawables, twilight, tStart, tEnd, TimeToX, yMargin, plotH);

        // --- Night window (dark background already covers it) ---

        // --- Grid lines and labels ---
        DrawGrid(drawables, tStart, tEnd, TimeToX, AltToY, xMargin, yMargin, plotW, plotH);

        // --- Min altitude threshold ---
        var minAltY = AltToY(MinHeight);
        drawables
            .StrokeColor(new MagickColor("#FF6B6B80"))
            .StrokeWidth(1.5)
            .StrokeDashArray(6, 4)
            .FillColor(MagickColors.Transparent)
            .Line(xMargin, minAltY, xMargin + plotW, minAltY)
            .StrokeDashArray()
            .FontPointSize(10)
            .StrokeColor(MagickColors.Transparent)
            .FillColor(new MagickColor("#FF6B6B"))
            .TextAlignment(TextAlignment.Right)
            .Text(xMargin - 5, minAltY + 4, $"{MinHeight}°");

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
            var bandColor = new MagickColor(color.R, color.G, color.B, (ushort)(ushort.MaxValue * 0.25));

            drawables
                .FillColor(bandColor)
                .StrokeColor(color)
                .StrokeWidth(1)
                .StrokeDashArray()
                .Rectangle(x1, yMargin, x2, yMargin + plotH);

            // Draw spare windows as dashed outlines
            if (sparesPerSlot.TryGetValue(i, out var spares))
            {
                foreach (var spare in spares)
                {
                    var spareColorIdx = targetColorMap.GetValueOrDefault(spare.Target, 0) % TargetColors.Length;
                    var spareColor = TargetColors[spareColorIdx];

                    drawables
                        .FillColor(MagickColors.Transparent)
                        .StrokeColor(spareColor)
                        .StrokeWidth(1.5)
                        .StrokeDashArray(4, 4)
                        .Rectangle(x1 + 2, yMargin + 2, x2 - 2, yMargin + plotH - 2)
                        .StrokeDashArray();
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
            var visiblePoints = Array.ConvertAll(smoothed, p => new PointD(p.X, p.Y));

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
                drawables
                    .StrokeDashArray(6, 3);
            }

            drawables
                .StrokeColor(color)
                .StrokeWidth(2.5)
                .FillColor(MagickColors.Transparent)
                .Polyline(visiblePoints)
                .StrokeDashArray();

            // Label at the peak
            var peak = profile.MaxBy(p => p.Alt);
            drawables
                .FontPointSize(12)
                .StrokeColor(MagickColors.Transparent)
                .FillColor(color)
                .TextAlignment(TextAlignment.Center)
                .Text(TimeToX(peak.Time), AltToY(peak.Alt) - 8, target.Name);
        }

        // --- Title ---
        drawables
            .FontPointSize(16)
            .StrokeColor(MagickColors.Transparent)
            .FillColor(MagickColors.White)
            .TextAlignment(TextAlignment.Center)
            .Text(width / 2.0, 20, $"Observation Schedule — {label} ({SiteLatitude:F1}°N, {SiteLongitude:F1}°E)");

        // --- Legend ---
        var legendY = height - 20;
        var legendX = xMargin;
        for (var i = 0; i < targets.Length; i++)
        {
            var color = TargetColors[i % TargetColors.Length];
            drawables
                .StrokeColor(color)
                .StrokeWidth(2)
                .FillColor(MagickColors.Transparent)
                .Line(legendX, legendY - 4, legendX + 20, legendY - 4)
                .StrokeColor(MagickColors.Transparent)
                .FillColor(color)
                .FontPointSize(11)
                .TextAlignment(TextAlignment.Left)
                .Text(legendX + 25, legendY, targets[i].Name);
            legendX += 120;
        }

        // Priority legend
        legendX += 40;
        drawables
            .StrokeColor(MagickColors.Gray)
            .StrokeWidth(2)
            .FillColor(MagickColors.Transparent)
            .Line(legendX, legendY - 4, legendX + 20, legendY - 4)
            .StrokeColor(MagickColors.Transparent)
            .FillColor(MagickColors.Gray)
            .FontPointSize(11)
            .Text(legendX + 25, legendY, "Primary (solid)");

        legendX += 130;
        drawables
            .StrokeColor(MagickColors.Gray)
            .StrokeWidth(2)
            .StrokeDashArray(4, 4)
            .FillColor(MagickColors.Transparent)
            .Line(legendX, legendY - 4, legendX + 20, legendY - 4)
            .StrokeDashArray()
            .StrokeColor(MagickColors.Transparent)
            .FillColor(MagickColors.Gray)
            .FontPointSize(11)
            .Text(legendX + 25, legendY, "Spare (dashed)");

        drawables.Draw(image);

        var outputDir = SharedTestData.CreateTempTestOutputDir(nameof(ObservationScheduleVisualizationTests));
        var fullPath = Path.Combine(outputDir, fileName);
        await image.WriteAsync(fullPath);

        testOutputHelper.WriteLine($"Wrote altitude chart to: {fullPath}");
    }

    private static void DrawTwilightZones(
        Drawables drawables,
        TwilightBoundaries twilight,
        DateTimeOffset tStart,
        DateTimeOffset tEnd,
        Func<DateTimeOffset, double> timeToX,
        int yMargin,
        int plotH)
    {
        // Zone colors from lightest (civil) to darkest (night)
        var civilColor = new MagickColor("#3a3a5e");
        var nauticalColor = new MagickColor("#2a2a4e");
        var astroColor = new MagickColor("#22223e");
        var labelColor = new MagickColor("#ffffff50");

        // Collect all zones as (x1, x2, color, label) for drawing
        var zones = new List<(double X1, double X2, MagickColor Color, string Label)>();

        var x0 = timeToX(tStart);
        var xEnd = timeToX(tEnd);
        var xAstroDark = timeToX(twilight.AstroDark);
        var xAstroTwilight = timeToX(twilight.AstroTwilight);

        // Evening zones
        if (twilight.CivilSet.HasValue)
        {
            var xCivilSet = timeToX(twilight.CivilSet.Value);
            zones.Add((x0, xCivilSet, civilColor, "Civil"));

            if (twilight.NauticalSet.HasValue)
            {
                var xNautSet = timeToX(twilight.NauticalSet.Value);
                zones.Add((xCivilSet, xNautSet, nauticalColor, "Nautical"));
                zones.Add((xNautSet, xAstroDark, astroColor, "Astro"));
            }
            else
            {
                zones.Add((xCivilSet, xAstroDark, nauticalColor, "Nautical"));
            }
        }

        // Night zone (between astro dark and astro twilight — already background color)
        zones.Add((xAstroDark, xAstroTwilight, MagickColors.Transparent, "Night"));

        // Morning zones
        if (twilight.CivilRise.HasValue)
        {
            var xCivilRise = timeToX(twilight.CivilRise.Value);

            if (twilight.NauticalRise.HasValue)
            {
                var xNautRise = timeToX(twilight.NauticalRise.Value);
                zones.Add((xAstroTwilight, xNautRise, astroColor, "Astro"));
                zones.Add((xNautRise, xCivilRise, nauticalColor, "Nautical"));
            }
            else
            {
                zones.Add((xAstroTwilight, xCivilRise, nauticalColor, "Nautical"));
            }

            zones.Add((xCivilRise, xEnd, civilColor, "Civil"));
        }

        // Draw zone rectangles (plot area only)
        foreach (var (x1, x2, color, _) in zones)
        {
            if (color != MagickColors.Transparent)
            {
                drawables.FillColor(color).StrokeColor(MagickColors.Transparent)
                    .Rectangle(x1, yMargin, x2, yMargin + plotH);
            }
        }

        // Draw zone labels above the 90° line
        var labelY = yMargin - 10;

        foreach (var (x1, x2, _, label) in zones)
        {
            var bandWidth = x2 - x1;
            if (bandWidth <= 20)
            {
                continue;
            }

            var cx = (x1 + x2) / 2.0;

            // Draw a thin line at each zone boundary dropping from the label area to the plot
            drawables
                .StrokeColor(new MagickColor("#ffffff60"))
                .StrokeWidth(1)
                .StrokeDashArray(2, 3)
                .FillColor(MagickColors.Transparent)
                .Line(x1, labelY + 4, x1, yMargin)
                .Line(x2, labelY + 4, x2, yMargin)
                .StrokeDashArray();

            var fontSize = bandWidth < 50 ? 9.0 : 11.0;
            drawables
                .FontPointSize(fontSize)
                .StrokeColor(MagickColors.Transparent)
                .FillColor(MagickColors.White)
                .TextAlignment(TextAlignment.Center)
                .Text(cx, labelY, label);
        }
    }

    private static void DrawGrid(
        Drawables drawables,
        DateTimeOffset tStart,
        DateTimeOffset tEnd,
        Func<DateTimeOffset, double> timeToX,
        Func<double, double> altToY,
        int xMargin,
        int yMargin,
        int plotW,
        int plotH)
    {
        var gridColor = new MagickColor("#ffffff30");
        var textColor = new MagickColor("#cccccc");

        // Altitude grid lines (every 10°)
        for (var alt = 0; alt <= 90; alt += 10)
        {
            var y = altToY(alt);
            drawables
                .StrokeColor(gridColor)
                .StrokeWidth(0.5)
                .FillColor(MagickColors.Transparent)
                .Line(xMargin, y, xMargin + plotW, y)
                .FontPointSize(10)
                .StrokeColor(MagickColors.Transparent)
                .FillColor(textColor)
                .TextAlignment(TextAlignment.Right)
                .Text(xMargin - 5, y + 4, $"{alt}°");
        }

        // Time grid lines (every hour)
        var firstHour = new DateTimeOffset(
            tStart.Year, tStart.Month, tStart.Day,
            tStart.Hour + 1, 0, 0, tStart.Offset);

        for (var t = firstHour; t < tEnd; t = t.AddHours(1))
        {
            var x = timeToX(t);
            drawables
                .StrokeColor(gridColor)
                .StrokeWidth(0.5)
                .FillColor(MagickColors.Transparent)
                .Line(x, yMargin, x, yMargin + plotH)
                .FontPointSize(10)
                .StrokeColor(MagickColors.Transparent)
                .FillColor(textColor)
                .TextAlignment(TextAlignment.Center)
                .Text(x, yMargin + plotH + 15, t.ToString("HH:mm"));
        }

        // Axes
        drawables
            .StrokeColor(new MagickColor("#ffffff60"))
            .StrokeWidth(1)
            .FillColor(MagickColors.Transparent)
            .Line(xMargin, yMargin, xMargin, yMargin + plotH)
            .Line(xMargin, yMargin + plotH, xMargin + plotW, yMargin + plotH);

        // Axis labels
        drawables
            .FontPointSize(12)
            .StrokeColor(MagickColors.Transparent)
            .FillColor(textColor)
            .TextAlignment(TextAlignment.Center)
            .Text(xMargin + plotW / 2.0, yMargin + plotH + 40, "Local Time");
    }
}
