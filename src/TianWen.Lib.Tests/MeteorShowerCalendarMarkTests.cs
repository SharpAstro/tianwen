using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DIR.Lib;
using Shouldly;
using SharpAstro.Png;
using TianWen.Lib.Astrometry.MeteorShowers;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The night calendar's shower mark: the data travels beside each night's summary (never in its verdict), the cell says it
/// by SHAPE and its tooltip and the detail strip by words, and the one widget draws it in every theme at every DPI
/// (docs/plans/yearly-planner.md, E).
/// </summary>
[Collection("UI")]
public class MeteorShowerCalendarMarkTests
{
    private const double Latitude = 50.1;
    private const double Longitude = 8.7;
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);

    private static Transform Site() => new Transform(SystemTimeProvider.Instance)
    {
        SiteLatitude = Latitude,
        SiteLongitude = Longitude,
        SiteElevation = 100,
        SiteTemperature = 15,
        DateTimeOffset = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero),
    };

    private static PlannerState MonthOf(DateOnly month)
    {
        var state = new PlannerState { SiteLatitude = Latitude, SiteLongitude = Longitude, SiteTimeZone = Cest };
        state.Calendar.Publish(NightCalendarData.Empty with { Generation = 1 });
        state.Calendar.Month = month;
        NightCalendarActions.EnsureMonth(state, Site(), month);
        return state;
    }

    private static DateOnly NightOf(NightCalendarData data, Func<MeteorShowerNight, bool> peak)
        => data.Showers.First(kv => kv.Value.Any(peak)).Key;

    [Fact]
    public void AMonthHoldingThePerseidsPeakMarksThePeakNightAndNamesTheShower()
    {
        var state = MonthOf(new DateOnly(2026, 8, 1));
        var data = state.Calendar.Data;
        var peak = data.Showers.Where(kv => kv.Value.Any(s => s.IsPeakNight && s.Shower.Code == "PER")).Select(kv => kv.Key).ToArray();

        peak.Length.ShouldBe(1, "one night of the grid holds the Perseids' peak");
        peak[0].Month.ShouldBe(8);
        peak[0].Day.ShouldBeInRange(11, 13);
        var line = NightCalendarPopover<RgbaImage>.DetailLines(state, peak[0], data).Single(l => l.StartsWith("Perseids"));
        line.ShouldContain("peak tonight");
        line.ShouldContain("an hour here");
        NightCalendarPopover<RgbaImage>.CellTooltip(peak[0], data).ShouldNotBeNull()
            .ShouldStartWith("Perseids peak tonight", Case.Sensitive, "the peak leads the showers that are only active");
        NightCalendarPopover<RgbaImage>.DetailLines(state, peak[0], data).First(l => l.Contains(" active") || l.Contains("peak tonight"))
            .ShouldStartWith("Perseids");
    }

    [Fact]
    public void TheShowersTravelBesideTheSummaryAndNeverMoveTheVerdict()
    {
        var state = MonthOf(new DateOnly(2026, 8, 1));
        var data = state.Calendar.Data;
        var night = NightOf(data, s => s.IsPeakNight);

        data.Showers.Count.ShouldBe(data.Nights.Count, "every summarised night has its shower answer");
        var without = data with { Showers = data.Showers.Remove(night) };
        NightVerdict.For(without.Nights[night]).ShouldBe(NightVerdict.For(data.Nights[night]));
    }

    [Fact]
    public void ANightOutsideEveryWindowHasNoMarkNoTooltipAndNoShowerLine()
    {
        var bare = Enumerable.Range(0, 12).Select(m => MonthOf(new DateOnly(2026, m + 1, 1)).Calendar.Data)
            .SelectMany(d => d.Showers.Where(kv => kv.Value.IsEmpty).Select(kv => (Data: d, Night: kv.Key)))
            .FirstOrDefault();
        if (bare.Data is null)
        {
            Assert.Skip("the IMO list covers every night of 2026 at this site, so there is no bare night to look at");
        }

        NightCalendarPopover<RgbaImage>.CellTooltip(bare.Night, bare.Data).ShouldBeNull();
        var state = new PlannerState { SiteLatitude = Latitude, SiteLongitude = Longitude, SiteTimeZone = Cest };
        NightCalendarPopover<RgbaImage>.DetailLines(state, bare.Night, bare.Data)
            .ShouldNotContain(l => l.Contains(" active,") || l.Contains("peak tonight"));
    }

    [Fact]
    public void ANightWithTwoActiveShowersSaysEachOnItsOwnLine()
    {
        var state = MonthOf(new DateOnly(2026, 8, 1));
        var data = state.Calendar.Data;
        var night = data.Showers.Where(kv => kv.Value.Length >= 2).Select(kv => kv.Key).First();

        var lines = NightCalendarPopover<RgbaImage>.DetailLines(state, night, data);
        lines.Count(l => l.Contains(" active,") || l.Contains("peak tonight")).ShouldBeGreaterThanOrEqualTo(2);
        NightCalendarPopover<RgbaImage>.CellTooltip(night, data)!.Split('\n').Length.ShouldBe(data.Showers[night].Length);
    }

    [Fact]
    public void ARateThatVariesSaysSoAndARadiantBelowTheHorizonSaysThat()
    {
        var shower = MeteorShowerTable.All[0];
        var peak = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.Zero);
        var night = new MeteorShowerNight(shower, 0, 0, 0.5, 6.5, double.NaN, peak, TimeSpan.Zero);
        NightCalendarPopover<RgbaImage>.ShowerLine(night).ShouldContain("rate varies");
        NightCalendarPopover<RgbaImage>.ShowerLine(night with { ExpectedRate = 10, MeanSinAltitude = 0 }).ShouldContain("radiant not up");
        NightCalendarPopover<RgbaImage>.ShowerLine(night with { ExpectedRate = 10 }).ShouldContain("about 10 an hour here");
    }

    [Fact]
    public void ANightOffThePeakSaysHowFarThePeakIsAndNoRate()
    {
        // The table holds a shower's maximum, not how fast it falls away, so a rate off the peak would be the peak's.
        var shower = MeteorShowerTable.All[0];
        var peak = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.Zero);
        var before = new MeteorShowerNight(shower, 0, 0, 0.5, 6.5, 60, peak, TimeSpan.FromDays(-5));

        before.IsPeakNight.ShouldBeFalse();
        before.IsMajorPeak.ShouldBeFalse();
        var line = NightCalendarPopover<RgbaImage>.ShowerLine(before);
        line.ShouldBe($"{shower.Name} active, peak in 5 d");
        NightCalendarPopover<RgbaImage>.ShowerLine(before with { PeakOffset = TimeSpan.FromDays(3) }).ShouldEndWith("peak 3 d ago");

        var onIt = before with { PeakOffset = TimeSpan.FromHours(2) };
        onIt.IsMajorPeak.ShouldBeTrue();
        (onIt with { ExpectedRate = 4 }).IsMajorPeak.ShouldBeFalse("a peak under the floor is a minor one");
        (onIt with { ExpectedRate = double.NaN }).IsMajorPeak.ShouldBeFalse();
    }

    [Theory]
    [InlineData(UiThemeState.Dark, 1f)]
    [InlineData(UiThemeState.Dark, 1.5f)]
    [InlineData(UiThemeState.Night, 1f)]
    [InlineData(UiThemeState.Night, 1.5f)]
    public void TheMarkIsDrawnAndHoldsThreeToOneAgainstItsCell(UiThemeState theme, float dpi)
    {
        GuiTheme.Apply(theme, desktopIsDark: true);
        try
        {
            var state = MonthOf(new DateOnly(2026, 8, 1));
            var data = state.Calendar.Data;
            // A major peak (the Perseids) and a minor one (a peak under the floor): a ring around a dot, and a ring.
            var peakNight = NightOf(data, s => s.IsMajorPeak);
            var minorNight = data.Showers.Where(kv => kv.Value.Any(s => s.IsPeakNight) && !kv.Value.Any(s => s.IsMajorPeak)).Select(kv => kv.Key).First();

            var (pixels, popover, width, height) = Render(state, dpi);
            var palette = GuiTheme.Palette;
            foreach (var night in new[] { peakNight, minorNight })
            {
                var cell = CellRect(popover, night);
                // The cell's own tint, from the left margin at mid height, which only the cell's fill occupies.
                var fill = PixelAt(pixels, width, (int)(cell.X + (2f * dpi)), (int)(cell.Y + (cell.Height / 2f)));
                Contrast(palette.BodyText, fill).ShouldBeGreaterThanOrEqualTo(3.0, $"{theme} {night}: the mark's ink against its cell");

                // The mark's own rectangle, between the day number and the Moon: the Moon is lit in the same ink, so the
                // cell's whole top row would count it.
                InkCount(pixels, width, MarkBand(cell), palette.BodyText)
                    .ShouldBeGreaterThan(0, $"{theme} {night}: a mark is painted between the day number and the Moon");
            }

            InkCount(pixels, width, MarkBand(CellRect(popover, peakNight)), palette.BodyText)
                .ShouldBeGreaterThan(InkCount(pixels, width, MarkBand(CellRect(popover, minorNight)), palette.BodyText),
                    "a major peak's solid dot carries more ink than a minor peak's ring: shape, not colour");

            Save($"shower_{theme}_{dpi:0.0}_wide", pixels, width, height);
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }

    [Fact]
    public void ANarrowWindowStillPaintsTheMonthWithItsMarks()
    {
        GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        var state = MonthOf(new DateOnly(2026, 8, 1));
        var (pixels, _, width, height) = Render(state, 1.5f, windowWidth: 480, windowHeight: 520);
        Save("shower_Dark_1.5_narrow", pixels, width, height);
        pixels.Length.ShouldBe(width * height * 4);
    }

    [Fact]
    public void AMonthWithNoShowerDrawsNoMark()
    {
        GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        var state = MonthOf(new DateOnly(2026, 8, 1));
        var all = state.Calendar.Data;
        state.Calendar.Publish(all with { Showers = all.Showers.Clear() });
        var (pixels, popover, width, _) = Render(state, 1f);

        foreach (var night in all.Nights.Keys.Where(n => n.Month == 8))
        {
            var cell = CellRect(popover, night);
            InkCount(pixels, width, MarkBand(cell), GuiTheme.Palette.BodyText)
                .ShouldBe(0, $"{night}: nothing between the day number and the Moon");
        }
    }

    [Fact]
    public void ANightAShowerIsOnlyActiveOnIsNamedInWordsAndCarriesNoMark()
    {
        GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        var state = MonthOf(new DateOnly(2026, 8, 1));
        var data = state.Calendar.Data;
        var offPeak = data.Showers.Where(kv => kv.Key.Month == 8 && kv.Value.Length > 0 && !kv.Value.Any(s => s.IsPeakNight))
            .Select(kv => kv.Key).ToArray();
        var marked = data.Showers.Count(kv => kv.Key.Month == 8 && kv.Value.Any(s => s.IsPeakNight));

        offPeak.ShouldNotBeEmpty("most August nights have a shower active and none at its peak");
        marked.ShouldBeGreaterThan(0);
        marked.ShouldBeLessThan(8, "a mark on most nights of a month says nothing");

        var (pixels, popover, width, _) = Render(state, 1f);
        foreach (var night in offPeak)
        {
            InkCount(pixels, width, MarkBand(CellRect(popover, night)), GuiTheme.Palette.BodyText)
                .ShouldBe(0, $"{night}: no mark for a shower that is only active");
            var tooltip = NightCalendarPopover<RgbaImage>.CellTooltip(night, data).ShouldNotBeNull();
            tooltip.ShouldNotContain("an hour");
        }
    }

    /// <summary>The part of a cell's top row the shower mark is drawn in: right of the day number, left of the Moon.</summary>
    private static RectF32 MarkBand(RectF32 cell)
        => new(cell.X + (cell.Width * 0.4f), cell.Y, cell.Width * 0.3f, cell.Height * 0.4f);

    private static (byte[] Pixels, NightCalendarPopover<RgbaImage> Popover, int Width, int Height) Render(
        PlannerState state, float dpi, int windowWidth = 1000, int windowHeight = 700)
    {
        var renderer = new RgbaImageRenderer((uint)windowWidth, (uint)windowHeight);
        var popover = new NightCalendarPopover<RgbaImage>(renderer) { DpiScale = dpi, FontPath = FontResolver.ResolveSystemFont() };
        state.Calendar.Popover.Open();
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        popover.Render(state, new RectF32(windowWidth / 2f - 100f, 0f, 200f, 28f * dpi), new RectF32(0f, 0f, windowWidth, windowHeight), clock);
        return (renderer.Surface.Pixels.ToArray(), popover, windowWidth, windowHeight);
    }

    private static RectF32 CellRect(PixelWidgetBase<RgbaImage> popover, DateOnly night)
    {
        var action = "Night:" + night.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var r = popover.GetRegisteredRegions().Single(x => x.Result is HitResult.ButtonHit { Action: var a } && a == action);
        return new RectF32(r.X, r.Y, r.Width, r.Height);
    }

    private static RGBAColor32 PixelAt(byte[] pixels, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return new RGBAColor32(pixels[i], pixels[i + 1], pixels[i + 2], 0xff);
    }

    private static int InkCount(byte[] pixels, int width, RectF32 rect, RGBAColor32 ink)
    {
        var n = 0;
        for (var y = (int)rect.Y; y < (int)(rect.Y + rect.Height); y++)
        {
            for (var x = (int)rect.X; x < (int)(rect.X + rect.Width); x++)
            {
                n += PixelAt(pixels, width, x, y) == ink ? 1 : 0;
            }
        }

        return n;
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(RGBAColor32 c)
    {
        static double L(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * L(c.Red)) + (0.7152 * L(c.Green)) + (0.0722 * L(c.Blue));
    }

    /// <summary>Written to the test's own output folder, deleted when the test ends unless <c>TIANWEN_KEEP_TEST_OUTPUT=1</c> keeps it for a person to look at.</summary>
    private static void Save(string name, byte[] pixels, int width, int height)
    {
        var dir = SharedTestData.CreateTempTestOutputDir("MeteorShowerCalendar");
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(pixels, width, height));
    }
}
