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
        new RGBAColor32( 69, 123, 157, 255),  // Steel blue
        new RGBAColor32(200, 100, 200, 255),  // Magenta/purple
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

    // Slider / handoff colours
    private static readonly RGBAColor32 SliderColor        = new RGBAColor32(255, 255, 255, 180);
    private static readonly RGBAColor32 SliderLabelColor   = new RGBAColor32(255, 255, 255, 220);
    private static readonly RGBAColor32 ConflictColor      = new RGBAColor32(255, 215,   0, 255);

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
        int? highlightTargetIndex = null,
        DateTimeOffset? currentTime = null,
        (float X, float Y)? mouseScreenPosition = null)
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

        // --- Pinned target viable windows + handoff sliders ---
        var allTargets = BuildTargetList(state, highlightTargetIndex);
        var targetColorMap = BuildColorMap(allTargets);

        if (state.PinnedCount >= 1)
        {
            DrawPinnedTargetWindows(renderer, state, allTargets, targetColorMap,
                TimeToX, AltToY, plotX, plotY, plotW, plotH, fontFamily, h);
        }

        // --- Altitude curves ---
        DrawAltitudeCurves(renderer, state, allTargets, targetColorMap,
            TimeToX, AltToY, fontFamily, h, highlightTargetIndex);

        // --- Title ---
        var titleRect = MakeRect(0, 2, w, yMarginTop - 4);
        var latSign   = state.SiteLatitude >= 0 ? "N" : "S";
        var lonSign   = state.SiteLongitude >= 0 ? "E" : "W";
        renderer.DrawText(
            $"Observation Schedule — {Math.Abs(state.SiteLatitude):F1}°{latSign}, {Math.Abs(state.SiteLongitude):F1}°{lonSign}",
            fontFamily, FontSize(h, 16), WhiteColor,
            titleRect, TextAlign.Center, TextAlign.Near);

        // --- Current time shade (grey out elapsed time) ---
        if (currentTime is { } now && now >= tStart && now <= tEnd)
        {
            var nowX = TimeToX(now);
            if (nowX > plotX)
            {
                FillRect(renderer, plotX, plotY, nowX - plotX, plotH, new RGBAColor32(0, 0, 0, 100));
                DrawVLine(renderer, nowX, plotY, plotY + plotH, new RGBAColor32(255, 255, 255, 120));
            }
        }

        // --- Mouse follower (vertical line with time label near 90° mark) ---
        if (mouseScreenPosition is var (mx, my) && mx >= plotX && mx <= plotX + plotW
            && my >= plotY && my <= plotY + plotH)
        {
            DrawVLine(renderer, (int)mx, plotY, plotY + plotH, new RGBAColor32(255, 255, 255, 50));
            var mouseTime = XToTime(mx, tStart, tEnd, plotX, plotW);
            var mouseLabel = mouseTime.ToOffset(state.SiteTimeZone).ToString("HH:mm");
            var mouseLabelRect = MakeRect((int)mx - 20, plotY + 2, 40, 14);
            renderer.DrawText(mouseLabel, fontFamily, FontSize(h, 10), new RGBAColor32(255, 255, 255, 160),
                mouseLabelRect, TextAlign.Center, TextAlign.Near);
        }

        // --- Legend (sorted by score, highest first) ---
        var legendTargets = allTargets
            .OrderByDescending(t => state.ScoredTargets.TryGetValue(t, out var s) ? s.CombinedScore : 0)
            .ToArray();
        DrawLegend(renderer, legendTargets, targetColorMap,
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
    // Pinned target windows + handoff sliders
    // -----------------------------------------------------------------------

    private static void DrawPinnedTargetWindows<TSurface>(
        Renderer<TSurface> renderer,
        PlannerState state,
        Target[] allTargets,
        Dictionary<Target, int> colorMap,
        Func<DateTimeOffset, int> timeToX,
        Func<double, int> altToY,
        int plotX, int plotY, int plotW, int plotH,
        string fontFamily, int h)
    {
        var pinnedCount = state.PinnedCount;
        var minAlt = (double)state.MinHeightAboveHorizon;
        var minAltY = altToY(minAlt);

        for (var i = 0; i < pinnedCount && i < allTargets.Length; i++)
        {
            var target = allTargets[i];
            if (!state.AltitudeProfiles.TryGetValue(target, out var profile) || profile.Count < 2)
            {
                continue;
            }

            // Determine this target's time window from sliders
            var windowStart = i == 0 ? state.AstroDark : state.HandoffSliders[i - 1];
            var windowEnd = i >= pinnedCount - 1 || i >= state.HandoffSliders.Count
                ? state.AstroTwilight
                : state.HandoffSliders[i];

            var xStart = timeToX(windowStart);
            var xEnd = timeToX(windowEnd);
            var colorIdx = colorMap.GetValueOrDefault(target, 0) % TargetColors.Length;
            var baseColor = state.PinnedTargetConflicts.Length > i && state.PinnedTargetConflicts[i]
                ? ConflictColor
                : TargetColors[colorIdx];
            var fillColor = baseColor with { Alpha = 45 };

            // Column-by-column fill between curve and min-altitude line
            for (var px = Math.Max(xStart, plotX); px < Math.Min(xEnd, plotX + plotW); px++)
            {
                // Interpolate altitude at this pixel's time
                var fraction = (px - plotX) / (double)plotW;
                var t = profile[0].Time + TimeSpan.FromSeconds(
                    (profile[^1].Time - profile[0].Time).TotalSeconds * fraction);

                // Find bracketing profile samples
                var alt = InterpolateAltitude(profile, t);

                if (alt > minAlt)
                {
                    var curveY = altToY(alt);
                    var fillH = minAltY - curveY;
                    if (fillH > 0)
                    {
                        FillRect(renderer, px, curveY, 1, fillH, fillColor);
                    }
                }
            }
        }

        // Draw handoff slider lines
        for (var i = 0; i < state.HandoffSliders.Count; i++)
        {
            var sliderX = timeToX(state.HandoffSliders[i]);
            if (sliderX >= plotX && sliderX <= plotX + plotW)
            {
                // Vertical line
                var isHighlighted = state.DraggingSliderIndex == i || state.SelectedSliderIndex == i;
                var lineColor = isHighlighted
                    ? WhiteColor
                    : SliderColor;

                DrawVLine(renderer, sliderX, plotY, plotY + plotH, lineColor);

                // Handle diamonds at top and bottom
                FillRect(renderer, sliderX - 2, plotY - 2, 5, 5, lineColor);
                FillRect(renderer, sliderX - 2, plotY + plotH - 3, 5, 5, lineColor);

                // Time label above the min-altitude line
                var minAltLabelY = altToY(state.MinHeightAboveHorizon);
                var sliderTime = state.HandoffSliders[i].ToOffset(state.SiteTimeZone);
                var labelText = sliderTime.ToString("HH:mm");
                var labelRect = MakeRect(sliderX - 22, minAltLabelY - 18, 44, 14);
                // Dark background for readability
                FillRect(renderer, sliderX - 22, minAltLabelY - 18, 44, 14, new RGBAColor32(20, 20, 30, 200));
                renderer.DrawText(labelText, fontFamily, FontSize(h, 10), SliderLabelColor,
                    labelRect, TextAlign.Center, TextAlign.Center);
            }
        }
    }

    private static double InterpolateAltitude(List<(DateTimeOffset Time, double Alt)> profile, DateTimeOffset t)
    {
        if (t <= profile[0].Time) return profile[0].Alt;
        if (t >= profile[^1].Time) return profile[^1].Alt;

        for (var k = 0; k < profile.Count - 1; k++)
        {
            if (t >= profile[k].Time && t <= profile[k + 1].Time)
            {
                var span = (profile[k + 1].Time - profile[k].Time).TotalSeconds;
                if (span <= 0) return profile[k].Alt;
                var frac = (t - profile[k].Time).TotalSeconds / span;
                return profile[k].Alt + (profile[k + 1].Alt - profile[k].Alt) * frac;
            }
        }

        return 0;
    }

    /// <summary>
    /// Returns the chart's time-to-pixel layout parameters for external hit testing (slider drag).
    /// </summary>
    public static (DateTimeOffset TStart, DateTimeOffset TEnd, float PlotX, float PlotW) GetChartTimeLayout(
        PlannerState state, int areaX, int areaW)
    {
        var xMargin = Math.Max(48, areaW / 14);
        var plotX = areaX + xMargin;
        var plotW = areaW - xMargin * 2;
        var tStart = (state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1)) - TimeSpan.FromMinutes(15);
        var tEnd = (state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1)) + TimeSpan.FromMinutes(15);
        return (tStart, tEnd, plotX, plotW);
    }

    /// <summary>
    /// Converts a screen X pixel to a time using the chart's time layout.
    /// </summary>
    public static DateTimeOffset XToTime(float px, DateTimeOffset tStart, DateTimeOffset tEnd, float plotX, float plotW)
    {
        var fraction = (px - plotX) / plotW;
        fraction = Math.Clamp(fraction, 0, 1);
        return tStart + TimeSpan.FromSeconds(fraction * (tEnd - tStart).TotalSeconds);
    }

    // -----------------------------------------------------------------------
    // Scheduled observation windows
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Altitude curves
    // -----------------------------------------------------------------------

    private static void DrawAltitudeCurves<TSurface>(
        Renderer<TSurface> renderer,
        PlannerState state,
        Target[] allTargets,
        Dictionary<Target, int> targetColorMap,
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

            var isHighlight = highlightTargetIndex.HasValue && highlightTargetIndex.Value == i;
            var dotSize     = isHighlight ? 3 : 2;
            var curveColor  = isHighlight
                ? new RGBAColor32(
                    (byte)Math.Min(255, color.Red   + 40),
                    (byte)Math.Min(255, color.Green + 40),
                    (byte)Math.Min(255, color.Blue  + 40),
                    255)
                : color;

            DrawSolidCurve(renderer, smoothed, curveColor, dotSize);

            // Label at the peak altitude point: name above, peak time below
            var peak     = profile.MaxBy(p => p.Alt);
            var peakX    = timeToX(peak.Time);
            var peakY    = altToY(peak.Alt);
            var nameRect = MakeRect(peakX - 60, peakY - 18, 120, 14);
            renderer.DrawText(target.Name, fontFamily, FontSize(rendererH, 12), curveColor,
                nameRect, TextAlign.Center, TextAlign.Far);
            var peakTimeStr = peak.Time.ToOffset(state.SiteTimeZone).ToString("HH:mm");
            var timeRect = MakeRect(peakX - 30, peakY + 4, 60, 12);
            renderer.DrawText(peakTimeStr, fontFamily, FontSize(rendererH, 9), GrayColor,
                timeRect, TextAlign.Center, TextAlign.Near);
        }
    }

    // -----------------------------------------------------------------------
    // Legend
    // -----------------------------------------------------------------------

    private static void DrawLegend<TSurface>(
        Renderer<TSurface> renderer,
        Target[] allTargets,
        Dictionary<Target, int> targetColorMap,
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

    private static Target[] BuildTargetList(PlannerState state, int? highlightTargetIndex)
    {
        // Show only: proposed targets + the currently selected target
        // This keeps the chart clean and focused on the user's plan
        var seen = new HashSet<Target>();
        var result = new List<Target>();

        // Pinned targets in peak-time sorted order (matches GetFilteredTargets and slider windows)
        var filtered = PlannerActions.GetFilteredTargets(state);
        for (var i = 0; i < state.PinnedCount && i < filtered.Count; i++)
        {
            if (seen.Add(filtered[i].Target))
            {
                result.Add(filtered[i].Target);
            }
        }

        // Currently selected target (so user can preview before proposing)
        if (highlightTargetIndex.HasValue && highlightTargetIndex.Value >= 0)
        {
            if (highlightTargetIndex.Value < filtered.Count)
            {
                var selectedTarget = filtered[highlightTargetIndex.Value].Target;
                if (seen.Add(selectedTarget))
                {
                    result.Add(selectedTarget);
                }
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
