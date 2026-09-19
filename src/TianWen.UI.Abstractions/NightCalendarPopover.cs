using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The night calendar: a month of NIGHTS in a popover under the status-bar date. A cell carries the evening's day
/// number, the Moon's phase, and the night's <see cref="NightVerdict"/> as a word and a tint; a click plans that
/// night. A strip along the bottom details the night under the pointer, or the planned one
/// (docs/plans/night-calendar.md, P2).
/// </summary>
/// <remarks>
/// <para>
/// A widget of its own rather than chrome painted by the status bar, for the one reason it has to be: the Moon is
/// DRAWN (a disc, its lit half clipped to one side, and a terminator ellipse), and the clip and ellipse helpers
/// that does it with are the widget base's. A phase glyph from the emoji face was the alternative, and a colour
/// glyph ignores the palette, so in Night mode it would be the brightest thing on screen.
/// </para>
/// <para>
/// The host paints it LAST, over everything, every frame; closed, it begins its frame and paints nothing, which
/// is what retires last frame's regions. The detail strip resolves the hovered night from the cell rects the
/// previous frame arranged, since hover is decided before this frame's tree exists; the grid does not move
/// between frames, so the answer is the current one.
/// </para>
/// </remarks>
public sealed class NightCalendarPopover<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    // Design units, scaled by DpiScale in the arrange.
    private const float CellW = 62f;
    private const float CellH = 44f;
    private const float Gap = 2f;
    private const float FontSize = 13f;
    private const float SmallFontSize = 11f;
    private const float MoonSize = 12f;
    private const float PinStripH = 4f;
    private const int MaxPinLines = 3;
    private const string MoonKeyPrefix = "moon:";
    private const string PinsKeyPrefix = "pins:";
    private const string NightActionPrefix = "Night:";

    private readonly List<(DateOnly Night, RectF32 Rect)> _cells = new List<(DateOnly, RectF32)>(NightCalendarActions.GridDays);
    private NightCalendarData _painted = NightCalendarData.Empty;
    private bool _southern;
    private bool _pinsCurrent;
    private (NightPinsKey Key, long Generation, DateOnly Month)? _pinsRequested;

    /// <summary>
    /// Paints the calendar under <paramref name="anchor"/> (the date label's painted rect) when it is open, over
    /// <paramref name="window"/>. Closed, it only begins the frame.
    /// </summary>
    public void Render(PlannerState state, RectF32 anchor, RectF32 window, ITimeProvider timeProvider,
        SignalBus? bus = null, SkyMapState? skyMap = null)
    {
        var hovered = HoveredNight();
        BeginFrame();

        var calendar = state.Calendar;
        if (!calendar.Popover.IsOpen || string.IsNullOrEmpty(FontPath))
        {
            _cells.Clear();
            return;
        }

        if (calendar.Month == default)
        {
            calendar.Month = NightCalendarActions.FirstOfMonth(NightCalendarActions.PlanningEveningDate(state, timeProvider));
        }

        _painted = calendar.Data;
        _southern = state.SiteLatitude < 0;

        // The pins half is computed only while the calendar is open, so this is where it is asked for: once per pin
        // set, forecast and month, off the render thread through the same signal paging posts.
        var pinsKey = NightCalendarActions.PinsKey(state);
        _pinsCurrent = _painted.PinsKey == pinsKey;
        if (!NightCalendarActions.HasPins(_painted, pinsKey, calendar.Month)
            && _pinsRequested != (pinsKey, _painted.Generation, calendar.Month))
        {
            _pinsRequested = (pinsKey, _painted.Generation, calendar.Month);
            bus?.Post(new NightCalendarMonthSignal(calendar.Month));
        }
        var planned = NightCalendarActions.PlanningEveningDate(state, timeProvider);
        var tonight = NightCalendarActions.TonightEveningDate(state, timeProvider);

        var content = BuildContent(state, calendar.Month, planned, tonight, hovered ?? planned, timeProvider, bus, skyMap);
        var tree = Layout.Builder.Popover(anchor, content, calendar.Popover);
        var arranged = ArrangeLayout(tree, window);
        CollectCells(arranged);
        PaintLayout(arranged, drawFill: DrawFill);
    }

    /// <summary>The night whose cell the pointer is over, from the cells the previous frame arranged.</summary>
    private DateOnly? HoveredNight()
    {
        if (Pointer is not { } p)
        {
            return null;
        }

        foreach (var (night, rect) in _cells)
        {
            if (rect.Contains(p.X, p.Y))
            {
                return night;
            }
        }
        return null;
    }

    private void CollectCells(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        _cells.Clear();
        foreach (var node in arranged)
        {
            if (node.Node.Hit is HitResult.ButtonHit { Action: var action }
                && action.StartsWith(NightActionPrefix, StringComparison.Ordinal)
                && DateOnly.TryParseExact(action.AsSpan(NightActionPrefix.Length), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var night))
            {
                var r = node.Bounds;
                _cells.Add((night, new RectF32(r.X, r.Y, r.Width, r.Height)));
            }
        }
    }

    private Layout.Node BuildContent(PlannerState state, DateOnly month, DateOnly planned, DateOnly tonight,
        DateOnly detailNight, ITimeProvider timeProvider, SignalBus? bus, SkyMapState? skyMap)
    {
        var palette = GuiTheme.Palette;
        var calendar = state.Calendar;
        var gridW = (7 * CellW) + (6 * Gap);

        // Header: [<] Month Year [>] ........ [Tonight]
        var headerBg = palette.HeaderBg;
        var prev = FormRowLayout.StepMark("◀", FontSize, palette.BodyText)
            .WFixed(26f).HStar().Bg(headerBg).BgHover(GuiTheme.Hover(headerBg))
            .Clickable(new HitResult.ButtonHit("CalendarPrev"), _ => Page(calendar, -1, bus));
        var next = FormRowLayout.StepMark("▶", FontSize, palette.BodyText)
            .WFixed(26f).HStar().Bg(headerBg).BgHover(GuiTheme.Hover(headerBg))
            .Clickable(new HitResult.ButtonHit("CalendarNext"), _ => Page(calendar, +1, bus));
        var title = Layout.Builder.Text(month.ToString("MMMM yyyy", CultureInfo.CurrentCulture), FontSize,
                palette.BodyText, TextAlign.Center, TextAlign.Center, widthSample: "September 0000")
            .WAuto().HStar();
        var tonightButton = Layout.Builder.Text("Tonight", FontSize * 0.9f, palette.BodyText, TextAlign.Center,
                TextAlign.Center)
            .WAuto().HStar().PadX(8f).Bg(headerBg).BgHover(GuiTheme.Hover(headerBg))
            .Clickable(new HitResult.ButtonHit("CalendarTonight"),
                _ => NightCalendarActions.PickNight(state, tonight, timeProvider, skyMap));
        var header = Layout.Builder.HStack(prev, title, next, Layout.Builder.Spacer().WStar(), tonightButton)
            .RowH(26f).WithGap(Gap * 2f);

        // Weekday names, starting where the user's week starts.
        var firstDay = NightCalendarActions.FirstDayOfWeek;
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var weekdays = new Layout.Node[7];
        for (var i = 0; i < 7; i++)
        {
            weekdays[i] = Layout.Builder.Text(names[((int)firstDay + i) % 7], SmallFontSize, palette.DimText,
                TextAlign.Center, TextAlign.Center).WFixed(CellW).HStar();
        }
        var weekdayRow = Layout.Builder.HStack(weekdays).RowH(18f).WithGap(Gap);

        // Six weeks of nights.
        var grid = NightCalendarActions.MonthGrid(month, firstDay);
        var rows = new Layout.Node[6];
        for (var week = 0; week < 6; week++)
        {
            var cells = new Layout.Node[7];
            for (var day = 0; day < 7; day++)
            {
                var night = grid[(week * 7) + day];
                cells[day] = BuildCell(state, night, month, planned, tonight, timeProvider, skyMap);
            }
            rows[week] = Layout.Builder.HStack(cells).RowH(CellH).WithGap(Gap);
        }

        var detail = BuildDetail(state, detailNight);

        return Layout.Builder.VStack(
                header,
                weekdayRow,
                Layout.Builder.VStack(rows).WithGap(Gap).WAuto(),
                detail)
            .WithGap(Gap * 2f)
            .WFixed(gridW + 16f)
            .Pad(8f)
            .Bg(palette.PanelBg);
    }

    private static void Page(NightCalendarState calendar, int months, SignalBus? bus)
    {
        NightCalendarActions.ShiftMonth(calendar, months);
        bus?.Post(new NightCalendarMonthSignal(calendar.Month));
    }

    private Layout.Node BuildCell(PlannerState state, DateOnly night, DateOnly month, DateOnly planned,
        DateOnly tonight, ITimeProvider timeProvider, SkyMapState? skyMap)
    {
        var palette = GuiTheme.Palette;
        var inMonth = night.Month == month.Month && night.Year == month.Year;
        var known = _painted.Nights.TryGetValue(night, out var summary);
        var verdict = known ? NightVerdict.For(summary) : (NightVerdict?)null;

        var fill = verdict is { } v ? CellTint(v) : GuiTheme.Mix(palette.PanelBg, palette.Separator, 0.35f);
        var dayColour = night == tonight ? palette.Accent : inMonth ? palette.BodyText : palette.DimText;

        var top = Layout.Builder.HStack(
                Layout.Builder.Text(night.Day.ToString(CultureInfo.CurrentCulture), FontSize, dayColour,
                    TextAlign.Near, TextAlign.Center, widthSample: "00").WAuto().HStar(),
                Layout.Builder.Spacer().WStar(),
                known
                    ? Layout.Builder.Fill(MoonSize, MoonSize, MoonKeyPrefix + night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                        .WFixed(MoonSize).HFixed(MoonSize).CrossCenter()
                    : Layout.Builder.Spacer().WFixed(MoonSize))
            .RowH(FontSize + 4f);

        var word = verdict is { } w
            ? Layout.Builder.Text(w.Label, SmallFontSize, w.IsForecast ? palette.BodyText : palette.DimText,
                TextAlign.Near, TextAlign.Center)
            : Layout.Builder.Text(string.Empty, SmallFontSize, palette.DimText);

        // The pinned pointings' strip, dusk to dawn: drawn by DrawFill, and a plain spacer with nothing pinned so
        // every cell keeps one height.
        var strip = _pinsCurrent && _painted.Pins.TryGetValue(night, out var pins) && !pins.Pointings.IsEmpty
            ? Layout.Builder.Fill(0f, PinStripH, PinsKeyPrefix + night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : Layout.Builder.Spacer();

        var inner = Layout.Builder.VStack(top, word.RowH(SmallFontSize + 4f), strip.RowH(PinStripH))
            .Pad(4f, 2f)
            .WStar().HStar()
            .Bg(fill)
            .BgHover(GuiTheme.Hover(fill));

        // The planned night is ringed in the accent: a two-unit frame of it around the cell, so the tint inside
        // still says the verdict.
        return Layout.Builder.VStack(inner)
            .Pad(night == planned ? 2f : 0f)
            .Bg(night == planned ? palette.Accent : fill)
            .WFixed(CellW).HStar()
            .Clickable(new HitResult.ButtonHit(NightActionPrefix + night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                _ => NightCalendarActions.PickNight(state, night, timeProvider, skyMap));
    }

    /// <summary>The verdict's tint, from the palette: green, amber and red for a forecast, none for the Moon alone.</summary>
    private static RGBAColor32 CellTint(NightVerdict verdict)
    {
        var palette = GuiTheme.Palette;
        return verdict.Outlook switch
        {
            NightOutlook.Go => GuiTheme.Mix(palette.PanelBg, palette.Success, 0.35f),
            NightOutlook.Marginal => GuiTheme.Mix(palette.PanelBg, palette.Warn, 0.3f),
            NightOutlook.NoGo => GuiTheme.Mix(palette.PanelBg, palette.Error, 0.25f),
            _ => GuiTheme.Mix(palette.PanelBg, palette.Separator, 0.35f),
        };
    }

    /// <summary>The strip under the grid: everything the cell cannot fit, for one night.</summary>
    private Layout.Node BuildDetail(PlannerState state, DateOnly night)
    {
        var palette = GuiTheme.Palette;
        var lines = DetailLines(state, night, _painted, _pinsCurrent);
        var nodes = new Layout.Node[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            nodes[i] = Layout.Builder.Text(lines[i], i == 0 ? FontSize : SmallFontSize,
                i == 0 ? palette.BodyText : palette.DimText).RowH((i == 0 ? FontSize : SmallFontSize) + 5f);
        }
        return Layout.Builder.VStack(nodes).WStar();
    }

    /// <summary>
    /// The detail for one night: the verdict, the dark window and the Moon, then the forecast or why there is
    /// none. Pure, so the wording is tested without a surface.
    /// </summary>
    internal static List<string> DetailLines(PlannerState state, DateOnly night, NightCalendarData data,
        bool pinsCurrent = false)
    {
        var lines = new List<string>(3);
        var date = night.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        if (!data.Nights.TryGetValue(night, out var summary))
        {
            lines.Add(date);
            lines.Add("Working this night out...");
            return lines;
        }

        var verdict = NightVerdict.For(summary);
        lines.Add($"{date}: {verdict.Label}");

        var tz = state.SiteTimeZone;
        var twilight = summary.DarkBoundary switch
        {
            EventType.NauticalTwilight => " (nautical twilight only)",
            null => " (no twilight)",
            _ => "",
        };
        var phase = summary.MoonIllumination < 0.02 ? "new Moon"
            : $"Moon {summary.MoonIllumination:P0} {(summary.MoonWaxing ? "waxing" : "waning")}";
        lines.Add($"Dark {summary.DarkStart.ToOffset(tz):HH:mm} to {summary.DarkEnd.ToOffset(tz):HH:mm}{twilight}, "
            + $"{summary.Dark.TotalHours:0.0} h; {phase}, moon-free {summary.MoonFreeDark.TotalHours:0.0} h");

        if (summary.Forecast is { } f && verdict.IsForecast)
        {
            var seeing = f.Seeing is SeeingClass.Unknown ? "" : $", seeing {f.Seeing}";
            var source = data.Forecast is { } origin ? $" ({origin.SourceFor(summary.DarkStart, summary.DarkEnd)})" : "";
            lines.Add($"Clear {f.ClearDark.TotalHours:0.0} h, cloud {f.MeanCloudCover:0}%, rain {f.Precipitation:0.0} mm{seeing}{source}");
        }
        else if (data.Forecast is null)
        {
            lines.Add("No forecast: the Moon alone");
        }
        else if (summary.Forecast is not null)
        {
            lines.Add("The forecast covers too little of this night: the Moon alone");
        }
        else
        {
            lines.Add("Beyond the forecast: the Moon alone");
        }

        if (pinsCurrent && data.PinsKey is { } key && data.Pins.TryGetValue(night, out var pins))
        {
            AddPinLines(lines, pins, key.MinAltitude, tz);
        }

        return lines;
    }

    /// <summary>One line per pinned pointing, <see cref="MaxPinLines"/> at most and then how many more.</summary>
    private static void AddPinLines(List<string> lines, NightPins pins, byte minAltitude, TimeSpan tz)
    {
        var shown = Math.Min(pins.Pointings.Length, MaxPinLines);
        for (var i = 0; i < shown; i++)
        {
            var p = pins.Pointings[i];
            if (!p.Located)
            {
                lines.Add($"{p.Name}: no position for this night");
            }
            else if (p.From is not { } from || p.To is not { } to)
            {
                lines.Add($"{p.Name}: never above {minAltitude}° in the dark");
            }
            else
            {
                var clear = p.Forecast > TimeSpan.Zero ? $" ({p.Clear.TotalHours:0.0} h clear)" : "";
                var moon = double.IsNaN(p.MinMoonSeparationDeg) ? "" : $", Moon {p.MinMoonSeparationDeg:0}°";
                lines.Add($"{p.Name}  {from.ToOffset(tz):HH:mm} to {to.ToOffset(tz):HH:mm}, "
                    + $"{p.Up.TotalHours:0.0} h{clear}{moon}");
            }
        }

        if (pins.Pointings.Length > shown)
        {
            lines.Add($"+{pins.Pointings.Length - shown} more pinned");
        }
    }

    private void DrawFill(Layout.Content.Fill fill, RectF32 rect)
    {
        if (fill.Key is { } pinsKey && pinsKey.StartsWith(PinsKeyPrefix, StringComparison.Ordinal))
        {
            DrawPinStrip(pinsKey, rect);
            return;
        }

        if (fill.Key is not { } key || !key.StartsWith(MoonKeyPrefix, StringComparison.Ordinal)
            || !DateOnly.TryParseExact(key.AsSpan(MoonKeyPrefix.Length), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var night)
            || !_painted.Nights.TryGetValue(night, out var summary))
        {
            return;
        }

        var palette = GuiTheme.Palette;
        DrawMoonPhase(rect, summary.MoonIllumination, litOnRight: _southern ? !summary.MoonWaxing : summary.MoonWaxing,
            lit: palette.BodyText, dark: GuiTheme.Mix(palette.PanelBg, palette.BodyText, 0.18f));
    }

    /// <summary>
    /// The pinned pointings' night, dusk to dawn: a faint track the width of the dark window, and over it every slice
    /// at least one pointing can use, in the accent where it is clear or past the forecast and dimmed where the
    /// forecast says cloud.
    /// </summary>
    private void DrawPinStrip(string key, RectF32 rect)
    {
        if (!DateOnly.TryParseExact(key.AsSpan(PinsKeyPrefix.Length), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var night)
            || !_painted.Pins.TryGetValue(night, out var pins)
            || pins.End <= pins.Start)
        {
            return;
        }

        var palette = GuiTheme.Palette;
        FillRect(rect.X, rect.Y, rect.Width, rect.Height, GuiTheme.Mix(palette.PanelBg, palette.BodyText, 0.15f));

        var span = (pins.End - pins.Start).TotalSeconds;
        var cloudy = GuiTheme.Mix(palette.PanelBg, palette.DimText, 0.6f);
        for (var i = 0; i < pins.Timeline.Length; i++)
        {
            if (pins.Timeline[i] is PinCoverage.None)
            {
                continue;
            }

            var x0 = rect.X + (float)(rect.Width * (NightPins.SampleStep * i).TotalSeconds / span);
            var w = (float)(rect.Width * pins.SliceAt(i).TotalSeconds / span);
            FillRect(x0, rect.Y, w, rect.Height, pins.Timeline[i] is PinCoverage.Cloudy ? cloudy : palette.Accent);
        }
    }

    /// <summary>
    /// The Moon as the sky shows it: a dark disc, its lit half clipped to one side, and a terminator ellipse
    /// |1 - 2k| of the diameter wide, which carves the crescent out of the lit half (k under a half) or carries the
    /// gibbous into the dark one (k over it). The lit area is k of the disc, exactly, for every k.
    /// </summary>
    /// <param name="litOnRight">Which limb is lit AS SEEN: the waxing limb is on the right in the north and on the
    /// left in the south, the rule <c>MeeusMoon.GetPhaseEmoji</c> states for the glyphs.</param>
    internal void DrawMoonPhase(RectF32 rect, double illumination, bool litOnRight, RGBAColor32 lit, RGBAColor32 dark)
    {
        var d = MathF.Min(rect.Width, rect.Height);
        if (d <= 0f)
        {
            return;
        }

        var x = rect.X + ((rect.Width - d) / 2f);
        var y = rect.Y + ((rect.Height - d) / 2f);
        var half = d / 2f;
        var cx = x + half;
        var k = (float)Math.Clamp(illumination, 0.0, 1.0);
        var litX = litOnRight ? cx : x;
        var darkX = litOnRight ? x : cx;
        var terminatorW = MathF.Abs(1f - (2f * k)) * d;

        FillEllipse(x, y, d, d, dark);

        PushClip(litX, y, half, d);
        FillEllipse(x, y, d, d, lit);
        if (k < 0.5f)
        {
            FillEllipse(cx - (terminatorW / 2f), y, terminatorW, d, dark);
        }
        PopClip();

        if (k > 0.5f)
        {
            PushClip(darkX, y, half, d);
            FillEllipse(cx - (terminatorW / 2f), y, terminatorW, d, lit);
            PopClip();
        }
    }
}
