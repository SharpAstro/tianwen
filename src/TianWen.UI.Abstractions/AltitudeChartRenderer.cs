using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Stat;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Renders an altitude-over-time chart for a scheduled observation night
/// onto any <see cref="Renderer{TSurface}"/> (TUI Sixel, Vulkan GPU, etc.).
/// </summary>
public static class AltitudeChartRenderer
{
    // Target colors — same palette as ObservationScheduleVisualizationTests
    private static readonly RGBAColor32[] TargetColors =
    [
        new RGBAColor32(230,  57,  70, 255),  // Red
        new RGBAColor32( 69, 123, 157, 255),  // Steel blue
        new RGBAColor32( 42, 157, 143, 255),  // Teal
        new RGBAColor32(233, 196, 106, 255),  // Gold
        new RGBAColor32(244, 162,  97, 255),  // Orange
        new RGBAColor32( 38,  70,  83, 255),  // Dark teal
    ];

    // Background fill for the whole chart surface
    private static readonly RGBAColor32 BackgroundColor    = new RGBAColor32( 26,  26,  46, 255);  // #1a1a2e

    // Twilight zone colours
    private static readonly RGBAColor32 CivilZoneColor     = new RGBAColor32( 58,  58,  94, 255);  // #3a3a5e
    private static readonly RGBAColor32 NauticalZoneColor  = new RGBAColor32( 42,  42,  78, 255);  // #2a2a4e
    private static readonly RGBAColor32 AstroZoneColor     = new RGBAColor32( 34,  34,  62, 255);  // #22223e

    // Grid / axis / label colours
    private static readonly RGBAColor32 GridColor          = new RGBAColor32(255, 255, 255,  48);   // #ffffff30
    private static readonly RGBAColor32 AxisColor          = new RGBAColor32(255, 255, 255,  96);   // #ffffff60
    private static readonly RGBAColor32 TextColor          = new RGBAColor32(204, 204, 204, 255);   // #cccccc
    private static readonly RGBAColor32 WhiteColor         = new RGBAColor32(255, 255, 255, 255);
    private static readonly RGBAColor32 GrayColor          = new RGBAColor32(180, 180, 180, 255);
    private static readonly RGBAColor32 ZoneLabelColor     = new RGBAColor32(255, 255, 255,  80);   // #ffffff50

    // Min-altitude threshold line colour
    private static readonly RGBAColor32 MinAltColor        = new RGBAColor32(255, 107, 107, 200);   // #FF6B6BC8
    private static readonly RGBAColor32 MinAltLabelColor   = new RGBAColor32(255, 107, 107, 255);
    private static readonly RGBAColor32 MinAltShade        = new RGBAColor32(255, 60, 60, 30);     // subtle red shade

    // Default font family — callers may prefer to pass one via fontPath parameter
    private const string DefaultFontFamily = "monospace";

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Renders the complete altitude chart to <paramref name="renderer"/>.
    /// Works with any <see cref="Renderer{TSurface}"/> implementation.
    /// </summary>
    /// <param name="renderer">Target renderer (RgbaImageRenderer for TUI, VkRenderer for GUI).</param>
    /// <param name="state">Planner state containing twilight times, schedule, and altitude profiles.</param>
    /// <param name="fontFamily">Font family name to pass to <see cref="Renderer{TSurface}.DrawText"/>.</param>
    /// <param name="highlightTargetIndex">
    ///     When set, that target's curve is drawn with a thicker/brighter line.
    /// </param>
    /// <summary>
    /// Renders the chart filling the entire renderer surface.
    /// </summary>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        PlannerState state,
        string fontFamily = DefaultFontFamily,
        int? highlightTargetIndex = null)
        => Render(renderer, state, fontFamily, 0, 0, (int)renderer.Width, (int)renderer.Height, highlightTargetIndex);

    /// <summary>
    /// Renders the chart within a specific area of the renderer surface.
    /// Use this when the chart is embedded in a tab layout with surrounding panels.
    /// </summary>
    public static void Render<TSurface>(
        Renderer<TSurface> renderer,
        PlannerState state,
        string fontFamily,
        int areaX, int areaY, int areaW, int areaH,
        int? highlightTargetIndex = null)
    {
        // Guard: no data loaded yet (AstroDark is default = 0001-01-01)
        if (state.AstroDark == default)
        {
            FillRect(renderer, areaX, areaY, areaW, areaH, BackgroundColor);
            return;
        }

        var w = areaW;
        var h = areaH;

        // --- Layout (proportional to area size, offset by areaX/areaY) ---
        var xMargin     = Math.Max(48, w / 14);
        var yMarginTop  = Math.Max(30, h / 22);
        var yMarginBot  = Math.Max(44, h / 14);
        var legendH     = Math.Max(20, h / 33);

        var plotX = areaX + xMargin;
        var plotY = areaY + yMarginTop;
        var plotW = w - xMargin * 2;
        var plotH = h - yMarginTop - yMarginBot - legendH;

        // --- Time range ---
        var tStart = (state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1)) - TimeSpan.FromMinutes(15);
        var tEnd   = (state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1)) + TimeSpan.FromMinutes(15);
        var tRange = (tEnd - tStart).TotalHours;

        // --- Coordinate helpers ---
        int TimeToX(DateTimeOffset t) =>
            plotX + (int)Math.Round((t - tStart).TotalHours / tRange * plotW);

        int AltToY(double alt) =>
            (plotY + plotH) - (int)Math.Round(alt / 90.0 * plotH);

        // --- Background ---
        FillRect(renderer, areaX, areaY, w, h, BackgroundColor);

        // --- Twilight zones ---
        DrawTwilightZones(renderer, state, tStart, tEnd, TimeToX, plotX, plotY, plotW, plotH, fontFamily);

        // --- Grid ---
        DrawGrid(renderer, state, tStart, tEnd, TimeToX, AltToY, plotX, plotY, plotW, plotH, fontFamily);

        // --- Min altitude threshold (dashed red line + shaded area below) ---
        var minAltY = AltToY(state.MinHeightAboveHorizon);

        // Shade the area below minimum altitude
        var shadeH = (plotY + plotH) - minAltY;
        if (shadeH > 0)
        {
            FillRect(renderer, plotX, minAltY, plotW, shadeH, MinAltShade);
        }

        DrawDashedHLine(renderer, plotX, plotX + plotW, minAltY, MinAltColor, dashLen: 6, gapLen: 4);

        // Label on the RIGHT side (avoids overlap with grid's degree scale on the left)
        var minAltLabelRect = MakeRect(plotX + plotW + 4, minAltY - 8, 40, 16);
        renderer.DrawText(
            $"{state.MinHeightAboveHorizon}°",
            fontFamily, FontSize(h, 10), MinAltLabelColor,
            minAltLabelRect, TextAlign.Near, TextAlign.Center);

        // --- Scheduled observation windows ---
        var allTargets = BuildTargetList(state);
        var targetColorMap = BuildColorMap(allTargets);

        if (state.Schedule is { } tree)
        {
            DrawScheduledWindows(renderer, tree, targetColorMap, TimeToX, plotX, plotY, plotH);
        }

        // --- Altitude curves ---
        DrawAltitudeCurves(renderer, state, allTargets, targetColorMap, tree: state.Schedule,
            TimeToX, AltToY, fontFamily, h, highlightTargetIndex);

        // --- Title ---
        var titleRect = MakeRect(0, 2, w, yMarginTop - 4);
        var latSign   = state.SiteLatitude >= 0 ? "N" : "S";
        var lonSign   = state.SiteLongitude >= 0 ? "E" : "W";
        renderer.DrawText(
            $"Observation Schedule — {Math.Abs(state.SiteLatitude):F1}°{latSign}, {Math.Abs(state.SiteLongitude):F1}°{lonSign}",
            fontFamily, FontSize(h, 16), WhiteColor,
            titleRect, TextAlign.Center, TextAlign.Near);

        // --- Legend (sorted by score, highest first) ---
        var legendTargets = allTargets
            .OrderByDescending(t => state.ScoredTargets.TryGetValue(t, out var s) ? s.CombinedScore : 0)
            .ToArray();
        DrawLegend(renderer, legendTargets, targetColorMap, state.Schedule,
            plotX, areaY + h - legendH - 4, legendH, fontFamily, h, areaX + w);
    }

    // -----------------------------------------------------------------------
    // Twilight zones
    // -----------------------------------------------------------------------

    private static void DrawTwilightZones<TSurface>(
        Renderer<TSurface> renderer,
        PlannerState state,
        DateTimeOffset tStart,
        DateTimeOffset tEnd,
        Func<DateTimeOffset, int> timeToX,
        int plotX,
        int plotY,
        int plotW,
        int plotH,
        string fontFamily)
    {
        var xAstroDark     = timeToX(state.AstroDark);
        var xAstroTwilight = timeToX(state.AstroTwilight);
        var x0             = timeToX(tStart);
        var xEnd           = timeToX(tEnd);

        var zones = new List<(int X1, int X2, RGBAColor32? Color, string Label)>();

        // Evening zones
        if (state.CivilSet.HasValue)
        {
            var xCivSet = timeToX(state.CivilSet.Value);
            zones.Add((x0, xCivSet, CivilZoneColor, "Civil"));

            if (state.NauticalSet.HasValue)
            {
                var xNautSet = timeToX(state.NauticalSet.Value);
                zones.Add((xCivSet, xNautSet, NauticalZoneColor, "Nautical"));
                zones.Add((xNautSet, xAstroDark, AstroZoneColor, "Astro"));
            }
            else
            {
                zones.Add((xCivSet, xAstroDark, NauticalZoneColor, "Nautical"));
            }
        }

        // Night zone — transparent (background is already the night colour)
        zones.Add((xAstroDark, xAstroTwilight, null, "Night"));

        // Morning zones
        if (state.CivilRise.HasValue)
        {
            var xCivRise = timeToX(state.CivilRise.Value);

            if (state.NauticalRise.HasValue)
            {
                var xNautRise = timeToX(state.NauticalRise.Value);
                zones.Add((xAstroTwilight, xNautRise, AstroZoneColor, "Astro"));
                zones.Add((xNautRise, xCivRise, NauticalZoneColor, "Nautical"));
            }
            else
            {
                zones.Add((xAstroTwilight, xCivRise, NauticalZoneColor, "Nautical"));
            }

            zones.Add((xCivRise, xEnd, CivilZoneColor, "Civil"));
        }

        // Draw zone fills
        foreach (var (x1, x2, color, _) in zones)
        {
            if (color.HasValue)
            {
                FillRect(renderer, x1, plotY, x2 - x1, plotH, color.Value);
            }
        }

        // Draw zone boundary markers and labels above the plot (staggered two-row layout)
        var fs = FontSize((int)renderer.Height, 10);
        var labelRow0Y = plotY - 24; // top row (even zones)
        var labelRow1Y = plotY - 10; // bottom row (odd zones)

        for (var zi = 0; zi < zones.Count; zi++)
        {
            var (x1, x2, _, label) = zones[zi];
            var bandW = x2 - x1;

            // Thin boundary lines (even for narrow zones)
            if (bandW > 3)
            {
                DrawVLine(renderer, x1, labelRow0Y + 4, plotY, ZoneLabelColor);
                DrawVLine(renderer, x2, labelRow0Y + 4, plotY, ZoneLabelColor);
            }

            if (bandW < 10) continue;

            // Stagger: even zones on top row, odd on bottom row
            var labelY = (zi % 2 == 0) ? labelRow0Y : labelRow1Y;
            var cx = (x1 + x2) / 2;
            var lRect = MakeRect(cx - 60, labelY - 14, 120, 14);
            renderer.DrawText(label, fontFamily, fs, ZoneLabelColor, lRect, TextAlign.Center, TextAlign.Far);
        }
    }

    // -----------------------------------------------------------------------
    // Grid
    // -----------------------------------------------------------------------

    private static void DrawGrid<TSurface>(
        Renderer<TSurface> renderer,
        PlannerState state,
        DateTimeOffset tStart,
        DateTimeOffset tEnd,
        Func<DateTimeOffset, int> timeToX,
        Func<double, int> altToY,
        int plotX,
        int plotY,
        int plotW,
        int plotH,
        string fontFamily)
    {
        var h = (int)renderer.Height;

        // Altitude grid lines every 10°
        for (var alt = 0; alt <= 90; alt += 10)
        {
            var y = altToY(alt);
            DrawHLine(renderer, plotX, plotX + plotW, y, GridColor);

            var labelRect = MakeRect(plotX - 44, y - 8, 40, 16);
            renderer.DrawText($"{alt}°", fontFamily, FontSize(h, 10), TextColor,
                labelRect, TextAlign.Far, TextAlign.Center);
        }

        // Time grid lines every hour
        var firstHour = new DateTimeOffset(
            tStart.Year, tStart.Month, tStart.Day,
            tStart.Hour + 1, 0, 0, tStart.Offset);

        for (var t = firstHour; t < tEnd; t = t.AddHours(1))
        {
            var x = timeToX(t);
            DrawVLine(renderer, x, plotY, plotY + plotH, GridColor);

            var labelRect = MakeRect(x - 24, plotY + plotH + 2, 48, 16);
            renderer.DrawText(t.ToString("HH:mm"), fontFamily, FontSize(h, 10), TextColor,
                labelRect, TextAlign.Center, TextAlign.Near);
        }

        // Axes
        DrawVLine(renderer, plotX, plotY, plotY + plotH, AxisColor);
        DrawHLine(renderer, plotX, plotX + plotW, plotY + plotH, AxisColor);

        // Axis label
        var axisLabelRect = MakeRect(plotX, plotY + plotH + 20, plotW, 18);
        renderer.DrawText("Local Time", fontFamily, FontSize(h, 12), TextColor,
            axisLabelRect, TextAlign.Center, TextAlign.Near);
    }

    // -----------------------------------------------------------------------
    // Scheduled observation windows
    // -----------------------------------------------------------------------

    private static void DrawScheduledWindows<TSurface>(
        Renderer<TSurface> renderer,
        ScheduledObservationTree tree,
        Dictionary<Target, int> targetColorMap,
        Func<DateTimeOffset, int> timeToX,
        int plotX,
        int plotY,
        int plotH)
    {
        for (var i = 0; i < tree.Count; i++)
        {
            var obs      = tree[i];
            var colorIdx = targetColorMap.GetValueOrDefault(obs.Target, 0) % TargetColors.Length;
            var color    = TargetColors[colorIdx];
            var fill     = color.WithAlpha(64);  // ~25% alpha

            var x1 = timeToX(obs.Start);
            var x2 = timeToX(obs.Start + obs.Duration);
            if (x2 <= x1)
            {
                x2 = x1 + 1;
            }

            FillRect(renderer, x1, plotY, x2 - x1, plotH, fill);
            // Stroke: draw a 1px border rectangle
            renderer.DrawRectangle(MakeRect(x1, plotY, x2 - x1, plotH), color, 1);

            // Spare windows as dashed outline (simulate by drawing corner ticks)
            var spares = tree.GetSparesForSlot(i);
            foreach (var spare in spares)
            {
                var spareColorIdx = targetColorMap.GetValueOrDefault(spare.Target, 0) % TargetColors.Length;
                var spareColor    = TargetColors[spareColorIdx];

                // Inner dashed rectangle approximated with 4 corner L-shapes
                DrawDashedRect(renderer, x1 + 2, plotY + 2, x2 - x1 - 4, plotH - 4, spareColor);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Altitude curves
    // -----------------------------------------------------------------------

    private static void DrawAltitudeCurves<TSurface>(
        Renderer<TSurface> renderer,
        PlannerState state,
        Target[] allTargets,
        Dictionary<Target, int> targetColorMap,
        ScheduledObservationTree? tree,
        Func<DateTimeOffset, int> timeToX,
        Func<double, int> altToY,
        string fontFamily,
        int rendererH,
        int? highlightTargetIndex)
    {
        for (var i = 0; i < allTargets.Length; i++)
        {
            var target   = allTargets[i];
            var colorIdx = targetColorMap.GetValueOrDefault(target, 0) % TargetColors.Length;
            var color    = TargetColors[colorIdx];

            if (!state.AltitudeProfiles.TryGetValue(target, out var profile) || profile.Count < 2)
            {
                continue;
            }

            // Filter to non-negative altitude and map to pixel coords
            var visibleRaw = profile
                .Where(p => p.Alt >= 0)
                .Select(p => (X: (double)timeToX(p.Time), Y: (double)altToY(p.Alt)))
                .ToArray();

            if (visibleRaw.Length < 2)
            {
                continue;
            }

            // Smooth with Catmull-Rom
            var smoothed = CatmullRomSpline.Interpolate(visibleRaw, segmentsPerSpan: 16);

            // Determine if this target is spare-only
            var isSpare = true;
            if (tree is not null)
            {
                for (var j = 0; j < tree.Count; j++)
                {
                    if (tree[j].Target == target)
                    {
                        isSpare = false;
                        break;
                    }
                }
            }

            var isHighlight = highlightTargetIndex.HasValue && highlightTargetIndex.Value == i;
            var dotSize     = isHighlight ? 3 : 2;
            var curveColor  = isHighlight
                ? new RGBAColor32(
                    (byte)Math.Min(255, color.Red   + 40),
                    (byte)Math.Min(255, color.Green + 40),
                    (byte)Math.Min(255, color.Blue  + 40),
                    255)
                : color;

            if (isSpare)
            {
                // Dashed: draw every other dot cluster
                DrawDashedCurve(renderer, smoothed, curveColor, dotSize);
            }
            else
            {
                DrawSolidCurve(renderer, smoothed, curveColor, dotSize);
            }

            // Label at the peak altitude point
            var peak     = profile.MaxBy(p => p.Alt);
            var peakX    = timeToX(peak.Time);
            var peakY    = altToY(peak.Alt);
            var nameRect = MakeRect(peakX - 40, peakY - 18, 80, 14);
            renderer.DrawText(target.Name, fontFamily, FontSize(rendererH, 12), curveColor,
                nameRect, TextAlign.Center, TextAlign.Far);
        }
    }

    // -----------------------------------------------------------------------
    // Legend
    // -----------------------------------------------------------------------

    private static void DrawLegend<TSurface>(
        Renderer<TSurface> renderer,
        Target[] allTargets,
        Dictionary<Target, int> targetColorMap,
        ScheduledObservationTree? tree,
        int plotX,
        int legendY,
        int legendH,
        string fontFamily,
        int rendererH,
        int rendererW)
    {
        var fs       = FontSize(rendererH, 10);
        var cursorX  = plotX;
        var lineY    = legendY + legendH / 2;
        var availW   = rendererW - plotX * 2;

        // Reserve space for Primary/Spare labels at the end
        var suffixW  = 180;
        var targetW  = availW - suffixW;

        // Show top-scored targets that fit (max 6 for readability)
        var maxTargets = Math.Min(allTargets.Length, 6);
        var itemW = maxTargets > 0 ? Math.Max(100, targetW / maxTargets) : 100;

        for (var i = 0; i < maxTargets; i++)
        {
            if (cursorX + itemW > plotX + targetW)
            {
                break;
            }

            var colorIdx = targetColorMap.GetValueOrDefault(allTargets[i], 0) % TargetColors.Length;
            var color    = TargetColors[colorIdx];

            // Short coloured sample line (3px tall rect)
            FillRect(renderer, cursorX, lineY - 1, 20, 3, color);

            // Truncate long names
            var name = allTargets[i].Name;
            if (name.Length > 12)
            {
                name = name[..11] + ".";
            }

            var labelRect = MakeRect(cursorX + 22, legendY, itemW - 24, legendH);
            renderer.DrawText(name, fontFamily, fs, color,
                labelRect, TextAlign.Near, TextAlign.Center);

            cursorX += itemW;
        }

        if (maxTargets < allTargets.Length)
        {
            var moreRect = MakeRect(cursorX, legendY, 60, legendH);
            renderer.DrawText($"+{allTargets.Length - maxTargets}", fontFamily, fs, GrayColor,
                moreRect, TextAlign.Near, TextAlign.Center);
            cursorX += 60;
        }

        // "Primary (solid)" sample
        cursorX += 10;
        FillRect(renderer, cursorX, lineY - 1, 20, 3, GrayColor);
        var primRect = MakeRect(cursorX + 22, legendY, 70, legendH);
        renderer.DrawText("Primary", fontFamily, fs, GrayColor, primRect, TextAlign.Near, TextAlign.Center);
        cursorX += 90;

        // "Spare (dashed)" sample — dots to suggest dashes
        for (var d = 0; d < 20; d += 5)
        {
            FillRect(renderer, cursorX + d, lineY - 1, 3, 3, GrayColor);
        }
        var spareRect = MakeRect(cursorX + 22, legendY, 60, legendH);
        renderer.DrawText("Spare", fontFamily, fs, GrayColor, spareRect, TextAlign.Near, TextAlign.Center);
    }

    // -----------------------------------------------------------------------
    // Drawing helpers
    // -----------------------------------------------------------------------

    /// <summary>Draws a horizontal 1px line as a thin filled rectangle.</summary>
    private static void DrawHLine<TSurface>(
        Renderer<TSurface> renderer, int x1, int x2, int y, RGBAColor32 color)
    {
        if (x2 < x1)
        {
            (x1, x2) = (x2, x1);
        }
        FillRect(renderer, x1, y, x2 - x1, 1, color);
    }

    /// <summary>Draws a vertical 1px line as a thin filled rectangle.</summary>
    private static void DrawVLine<TSurface>(
        Renderer<TSurface> renderer, int x, int y1, int y2, RGBAColor32 color)
    {
        if (y2 < y1)
        {
            (y1, y2) = (y2, y1);
        }
        FillRect(renderer, x, y1, 1, y2 - y1, color);
    }

    /// <summary>Draws a dashed horizontal line by alternating filled and gap rectangles.</summary>
    private static void DrawDashedHLine<TSurface>(
        Renderer<TSurface> renderer, int x1, int x2, int y,
        RGBAColor32 color, int dashLen = 6, int gapLen = 4)
    {
        var x = x1;
        var draw = true;
        while (x < x2)
        {
            var segEnd = Math.Min(x + (draw ? dashLen : gapLen), x2);
            if (draw)
            {
                FillRect(renderer, x, y, segEnd - x, 1, color);
            }
            x    = segEnd;
            draw = !draw;
        }
    }

    /// <summary>Draws a dashed rectangle outline using short filled rectangles.</summary>
    private static void DrawDashedRect<TSurface>(
        Renderer<TSurface> renderer, int x, int y, int w, int h, RGBAColor32 color,
        int dashLen = 4, int gapLen = 4)
    {
        // Top and bottom
        DrawDashedHLine(renderer, x, x + w, y, color, dashLen, gapLen);
        DrawDashedHLine(renderer, x, x + w, y + h, color, dashLen, gapLen);

        // Left and right (vertical dashes)
        var curY = y;
        var draw = true;
        while (curY < y + h)
        {
            var segEnd = Math.Min(curY + (draw ? dashLen : gapLen), y + h);
            if (draw)
            {
                FillRect(renderer, x, curY, 1, segEnd - curY, color);
                FillRect(renderer, x + w - 1, curY, 1, segEnd - curY, color);
            }
            curY = segEnd;
            draw = !draw;
        }
    }

    /// <summary>Renders smoothed spline points as a continuous dot trail.</summary>
    private static void DrawSolidCurve<TSurface>(
        Renderer<TSurface> renderer,
        (double X, double Y)[] points,
        RGBAColor32 color,
        int lineWidth)
    {
        for (var i = 0; i < points.Length - 1; i++)
        {
            var x1 = (int)Math.Round(points[i].X);
            var y1 = (int)Math.Round(points[i].Y);
            var x2 = (int)Math.Round(points[i + 1].X);
            var y2 = (int)Math.Round(points[i + 1].Y);

            // Bresenham-style: draw connected line segments as thin rects
            var dx = Math.Abs(x2 - x1);
            var dy = Math.Abs(y2 - y1);

            if (dx >= dy)
            {
                // More horizontal — step in X, draw a short vertical rect per column
                var xStart = Math.Min(x1, x2);
                var xEnd = Math.Max(x1, x2);
                if (xEnd == xStart) xEnd = xStart + 1;
                for (var x = xStart; x <= xEnd; x++)
                {
                    var t = dx > 0 ? (float)(x - x1) / (x2 - x1) : 0f;
                    var y = (int)Math.Round(y1 + t * (y2 - y1));
                    FillRect(renderer, x, y - lineWidth / 2, 1, lineWidth, color);
                }
            }
            else
            {
                // More vertical — step in Y
                var yStart = Math.Min(y1, y2);
                var yEnd = Math.Max(y1, y2);
                for (var y = yStart; y <= yEnd; y++)
                {
                    var t = dy > 0 ? (float)(y - y1) / (y2 - y1) : 0f;
                    var x = (int)Math.Round(x1 + t * (x2 - x1));
                    FillRect(renderer, x - lineWidth / 2, y, lineWidth, 1, color);
                }
            }
        }
    }

    /// <summary>Renders spline points as a dashed dot trail (every other cluster).</summary>
    private static void DrawDashedCurve<TSurface>(
        Renderer<TSurface> renderer,
        (double X, double Y)[] points,
        RGBAColor32 color,
        int dotSize,
        int dashDots = 6,
        int gapDots  = 3)
    {
        var counter = 0;
        var draw    = true;
        foreach (var (px, py) in points)
        {
            if (draw)
            {
                var ix = (int)Math.Round(px);
                var iy = (int)Math.Round(py);
                FillRect(renderer, ix - dotSize / 2, iy - dotSize / 2, dotSize, dotSize, color);
            }

            counter++;
            if (counter >= (draw ? dashDots : gapDots))
            {
                draw    = !draw;
                counter = 0;
            }
        }
    }

    /// <summary>Convenience wrapper that fills a rect given top-left + size.</summary>
    private static void FillRect<TSurface>(
        Renderer<TSurface> renderer, int x, int y, int w, int h, RGBAColor32 color)
    {
        if (w <= 0 || h <= 0)
        {
            return;
        }
        renderer.FillRectangle(MakeRect(x, y, w, h), color);
    }

    /// <summary>
    /// Builds a <see cref="RectInt"/> from top-left origin + width/height.
    /// <see cref="RectInt"/> is (LowerRight, UpperLeft) so: LowerRight = (x+w, y+h), UpperLeft = (x, y).
    /// </summary>
    private static RectInt MakeRect(int x, int y, int w, int h)
        => new RectInt((x + w, y + h), (x, y));

    // -----------------------------------------------------------------------
    // Data helpers
    // -----------------------------------------------------------------------

    private static Target[] BuildTargetList(PlannerState state)
    {
        // Collect all distinct targets: from proposals + those appearing only in the schedule tree
        var seen = new HashSet<Target>();
        var result = new List<Target>();

        foreach (var p in state.Proposals)
        {
            if (seen.Add(p.Target))
            {
                result.Add(p.Target);
            }
        }

        if (state.Schedule is { } tree)
        {
            for (var i = 0; i < tree.Count; i++)
            {
                if (seen.Add(tree[i].Target))
                {
                    result.Add(tree[i].Target);
                }

                foreach (var spare in tree.GetSparesForSlot(i))
                {
                    if (seen.Add(spare.Target))
                    {
                        result.Add(spare.Target);
                    }
                }
            }
        }

        // Also include any target that has an altitude profile
        foreach (var kv in state.AltitudeProfiles)
        {
            if (seen.Add(kv.Key))
            {
                result.Add(kv.Key);
            }
        }

        return [.. result];
    }

    private static Dictionary<Target, int> BuildColorMap(Target[] targets)
    {
        var map = new Dictionary<Target, int>(targets.Length);
        for (var i = 0; i < targets.Length; i++)
        {
            map[targets[i]] = i;
        }
        return map;
    }

    /// <summary>Scales a nominal font size proportionally to the renderer height.</summary>
    private static float FontSize(int rendererH, float nominalAt800)
        => Math.Max(6f, nominalAt800 * rendererH / 800f);
}
