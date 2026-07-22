using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Paint-owning session timeline strip. Two Gantt-style variants share the bar + axis-tick + now-needle
/// idiom: <see cref="RenderPhaseTimeline{TSurface}"/> draws the running session's phase bars, and
/// <see cref="RenderTwilightTimeline{TSurface}"/> draws the pre-session civil/nautical/astronomical
/// twilight bands. Draws onto any <see cref="Renderer{TSurface}"/> (the same seam as
/// <see cref="AltitudeChartRenderer"/>); both used to be inlined in <see cref="LiveSessionTab{TSurface}"/>.
/// Each variant fills its own background.
/// </summary>
public static class SessionTimelineRenderer
{
    private static readonly RGBAColor32 TimelineBg       = new RGBAColor32(0x18, 0x18, 0x22, 0xff);
    private static readonly RGBAColor32 TimelineTickColor = new RGBAColor32(0x55, 0x55, 0x66, 0xff);
    private static readonly RGBAColor32 NowNeedleColor   = new RGBAColor32(0xff, 0xff, 0xff, 0xcc);
    private static readonly RGBAColor32 BrightText       = new RGBAColor32(0xff, 0xff, 0xff, 0xff);

    /// <summary>Running-session phase timeline: coloured phase bars + now needle + time-axis ticks.</summary>
    public static void RenderPhaseTimeline<TSurface>(
        Renderer<TSurface> renderer,
        RectF32 rect,
        LiveSessionState state,
        DateTimeOffset now,
        float dpiScale,
        string fontPath,
        float fontSize)
    {
        FillRect(renderer, rect.X, rect.Y, rect.Width, rect.Height, TimelineBg);

        var timeline = state.PhaseTimeline;
        if (timeline.Length == 0)
        {
            DrawText(renderer, "No timeline data", fontPath,
                rect.X, rect.Y, rect.Width, rect.Height,
                fontSize * 0.85f, GuiTheme.Palette.DimText, TextAlign.Center, TextAlign.Center);
            return;
        }

        var pad = GuiTheme.Metrics.Padding * dpiScale;
        var barH = 24f * dpiScale;
        var barY = rect.Y + pad;
        var axisY = barY + barH + 2 * dpiScale;
        var axisH = rect.Height - barH - pad * 2 - 2 * dpiScale;

        // Time range: session start to now + 30min lookahead
        var timeStart = timeline[0].StartTime;
        var sessionEnd = now + TimeSpan.FromMinutes(30);

        // Don't let range be too narrow (10 minutes minimum)
        var totalSeconds = Math.Max((sessionEnd - timeStart).TotalSeconds, 600);

        float TimeToX(DateTimeOffset t)
        {
            var frac = (float)((t - timeStart).TotalSeconds / totalSeconds);
            return rect.X + pad + frac * (rect.Width - pad * 2);
        }

        // Draw phase bars
        for (var i = 0; i < timeline.Length; i++)
        {
            var phaseStart = timeline[i].StartTime;
            var phaseEnd = i + 1 < timeline.Length ? timeline[i + 1].StartTime : now;
            var color = LiveSessionActions.PhaseColor(timeline[i].Phase);

            var x1 = Math.Max(TimeToX(phaseStart), rect.X + pad);
            var x2 = Math.Min(TimeToX(phaseEnd), rect.X + rect.Width - pad);
            var w = x2 - x1;
            if (w > 0)
            {
                FillRect(renderer, x1, barY, w, barH, color);

                // Label if wide enough
                if (w > 40 * dpiScale)
                {
                    var phaseLabel = LiveSessionActions.PhaseLabel(timeline[i].Phase);
                    // Shorten long labels
                    if (phaseLabel.Length > 8 && w < 80 * dpiScale)
                    {
                        phaseLabel = phaseLabel[..7] + "…";
                    }
                    DrawText(renderer, phaseLabel, fontPath,
                        x1 + 2, barY, w - 4, barH,
                        fontSize * 0.8f, BrightText, TextAlign.Center, TextAlign.Center);
                }
            }
        }

        // Now needle
        if (now >= timeStart && now <= sessionEnd)
        {
            var nowX = TimeToX(now);
            FillRect(renderer, nowX, barY - 2 * dpiScale, 2 * dpiScale, barH + axisH + 4 * dpiScale, NowNeedleColor);
        }

        // Time axis ticks (adaptive interval)
        if (axisH > 4)
        {
            // Adaptive tick interval: 5min if range < 30min, 10min if < 2h, 30min otherwise
            var rangeMins = totalSeconds / 60.0;
            var tickMins = rangeMins < 30 ? 5 : rangeMins < 120 ? 10 : 30;
            var tickStart = new DateTimeOffset(timeStart.Year, timeStart.Month, timeStart.Day,
                timeStart.Hour, (int)(timeStart.Minute / tickMins) * (int)tickMins, 0, timeStart.Offset);
            for (var t = tickStart; t <= sessionEnd; t = t.AddMinutes(tickMins))
            {
                if (t < timeStart) continue;
                var tx = TimeToX(t);
                if (tx < rect.X + pad || tx > rect.X + rect.Width - pad) continue;

                FillRect(renderer, tx, axisY, 1, axisH * 0.5f, TimelineTickColor);
                DrawText(renderer, t.ToOffset(state.SiteTimeZone).ToString("HH:mm"), fontPath,
                    tx - 25 * dpiScale, axisY + axisH * 0.4f, 50 * dpiScale, axisH * 0.6f,
                    fontSize * 0.8f, GuiTheme.Palette.DimText, TextAlign.Center, TextAlign.Center);
            }
        }
    }

    /// <summary>
    /// Preview-mode timeline: civil/nautical/astronomical twilight bands + now needle, so the user knows
    /// when astronomical dark arrives.
    /// </summary>
    public static void RenderTwilightTimeline<TSurface>(
        Renderer<TSurface> renderer,
        RectF32 rect,
        LiveSessionState state,
        DateTimeOffset now,
        float dpiScale,
        string fontPath,
        float fontSize)
    {
        FillRect(renderer, rect.X, rect.Y, rect.Width, rect.Height, TimelineBg);

        if (state.AstroDark == default)
        {
            DrawText(renderer, "Twilight data loading…", fontPath,
                rect.X, rect.Y, rect.Width, rect.Height,
                fontSize * 0.85f, GuiTheme.Palette.DimText, TextAlign.Center, TextAlign.Center);
            return;
        }

        var pad = GuiTheme.Metrics.Padding * dpiScale;
        var barH = 24f * dpiScale;

        // Time range: 15 min before civil set -> 15 min after civil rise
        var tStart = (state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1)) - TimeSpan.FromMinutes(15);
        var tEnd = (state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1)) + TimeSpan.FromMinutes(15);
        var totalSeconds = Math.Max((tEnd - tStart).TotalSeconds, 600);
        var barY = rect.Y + pad;

        float TimeToX(DateTimeOffset t) =>
            rect.X + pad + (float)((t - tStart).TotalSeconds / totalSeconds) * (rect.Width - pad * 2);

        // Twilight zone colors
        var civilColor = new RGBAColor32(0x44, 0x44, 0x22, 0x88);
        var nautColor = new RGBAColor32(0x22, 0x33, 0x55, 0x88);
        var astroColor = new RGBAColor32(0x11, 0x22, 0x44, 0x88);
        var nightColor = new RGBAColor32(0x00, 0x00, 0x22, 0xcc);

        // Fill the twilight bands
        if (state.CivilSet is { } cs)
        {
            FillRect(renderer, TimeToX(tStart), barY, TimeToX(cs) - TimeToX(tStart), barH, civilColor);
        }
        if (state.NauticalSet is { } ns)
        {
            var nsX = TimeToX(ns);
            var fromX = state.CivilSet is { } cs2 ? TimeToX(cs2) : TimeToX(tStart);
            FillRect(renderer, fromX, barY, nsX - fromX, barH, nautColor);
        }
        {
            var astroStartX = state.NauticalSet is { } ns2 ? TimeToX(ns2) : (state.CivilSet is { } cs3 ? TimeToX(cs3) : TimeToX(tStart));
            var darkX = TimeToX(state.AstroDark);
            FillRect(renderer, astroStartX, barY, darkX - astroStartX, barH, astroColor);
        }
        // Night (dark)
        {
            var darkX = TimeToX(state.AstroDark);
            var dawnX = TimeToX(state.AstroTwilight);
            FillRect(renderer, darkX, barY, dawnX - darkX, barH, nightColor);
        }
        // Dawn side: astro -> nautical -> civil (mirror)
        {
            var dawnX = TimeToX(state.AstroTwilight);
            var astroEndX = state.NauticalRise is { } nr ? TimeToX(nr) : (state.CivilRise is { } cr ? TimeToX(cr) : TimeToX(tEnd));
            FillRect(renderer, dawnX, barY, astroEndX - dawnX, barH, astroColor);
        }
        if (state.NauticalRise is { } nRise)
        {
            var nrX = TimeToX(nRise);
            var toX = state.CivilRise is { } cr2 ? TimeToX(cr2) : TimeToX(tEnd);
            FillRect(renderer, nrX, barY, toX - nrX, barH, nautColor);
        }
        if (state.CivilRise is { } cRise)
        {
            FillRect(renderer, TimeToX(cRise), barY, TimeToX(tEnd) - TimeToX(cRise), barH, civilColor);
        }

        // Now needle
        if (now >= tStart && now <= tEnd)
        {
            var nowX = TimeToX(now);
            FillRect(renderer, nowX, barY - 2, 2 * dpiScale, barH + 4, NowNeedleColor);
        }

        // Time axis ticks
        var axisY = barY + barH + 2;
        var axisH = rect.Height - barH - pad * 2 - 2;
        if (axisH > 4)
        {
            var rangeMins = totalSeconds / 60.0;
            var tickMins = rangeMins < 120 ? 10 : 30;
            var tickStart = new DateTimeOffset(tStart.Year, tStart.Month, tStart.Day,
                tStart.Hour, (int)(tStart.Minute / tickMins) * (int)tickMins, 0, tStart.Offset);
            for (var t = tickStart; t <= tEnd; t = t.AddMinutes(tickMins))
            {
                if (t < tStart) continue;
                var tx = TimeToX(t);
                if (tx < rect.X + pad || tx > rect.X + rect.Width - pad) continue;

                FillRect(renderer, tx, axisY, 1, axisH * 0.5f, TimelineTickColor);
                DrawText(renderer, t.ToOffset(state.SiteTimeZone).ToString("HH:mm"), fontPath,
                    tx - 25 * dpiScale, axisY + axisH * 0.4f, 50 * dpiScale, axisH * 0.6f,
                    fontSize * 0.8f, GuiTheme.Palette.DimText, TextAlign.Center, TextAlign.Center);
            }
        }
    }

    // --- Drawing helpers (float -> RectInt wrappers, byte-identical to PixelWidgetBase's) ---

    private static void FillRect<TSurface>(Renderer<TSurface> renderer, float x, float y, float w, float h, RGBAColor32 color)
    {
        if (w <= 0 || h <= 0) return;
        renderer.FillRectangle(
            new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
            color);
    }

    private static void DrawText<TSurface>(Renderer<TSurface> renderer, ReadOnlySpan<char> text, string fontPath,
        float x, float y, float w, float h, float fontSize, RGBAColor32 color, TextAlign horizAlign, TextAlign vertAlign)
    {
        if (string.IsNullOrEmpty(fontPath)) return;
        renderer.DrawText(text, fontPath, fontSize, color,
            new RectInt(new PointInt((int)(x + w), (int)(y + h)), new PointInt((int)x, (int)y)),
            horizAlign, vertAlign);
    }
}
