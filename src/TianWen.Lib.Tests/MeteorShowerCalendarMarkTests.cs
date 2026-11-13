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
        NightCalendarPopover<RgbaImage>.CellTooltip(peak[0], data).ShouldContain("Perseids peak tonight");
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
        var night = new MeteorShowerNight(shower, 0, 0, 0.5, 6.5, double.NaN, null, TimeSpan.Zero);
        NightCalendarPopover<RgbaImage>.ShowerLine(night).ShouldContain("rate varies");
        NightCalendarPopover<RgbaImage>.ShowerLine(night with { ExpectedRate = 10, MeanSinAltitude = 0 }).ShouldContain("radiant not up");
        NightCalendarPopover<RgbaImage>.ShowerLine(night with { ExpectedRate = 10 }).ShouldContain("about 10 an hour here");
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
            var peakNight = NightOf(data, s => s.IsPeakNight);
            var activeNight = data.Showers.Where(kv => kv.Value.Length > 0 && !kv.Value.Any(s => s.IsPeakNight)).Select(kv => kv.Key).First();

            var (pixels, popover, width, height) = Render(state, dpi);
            var palette = GuiTheme.Palette;
            foreach (var night in new[] { peakNight, activeNight })
            {
                var cell = CellRect(popover, night);
                // The cell's own tint, from the left margin at mid height, which only the cell's fill occupies.
                var fill = PixelAt(pixels, width, (int)(cell.X + (2f * dpi)), (int)(cell.Y + (cell.Height / 2f)));
                Contrast(palette.BodyText, fill).ShouldBeGreaterThanOrEqualTo(3.0, $"{theme} {night}: the mark's ink against its cell");

                var inkInTopRight = 0;
                for (var y = (int)cell.Y; y < (int)(cell.Y + (cell.Height / 2f)); y++)
                {
                    for (var x = (int)(cell.X + (cell.Width / 2f)); x < (int)(cell.X + cell.Width); x++)
                    {
                        inkInTopRight += PixelAt(pixels, width, x, y) == palette.BodyText ? 1 : 0;
                    }
                }

                inkInTopRight.ShouldBeGreaterThan(0, $"{theme} {night}: a mark is painted in the cell's top row");
            }

            var ink = (int night) => InkCount(pixels, width, CellRect(popover, night == 0 ? peakNight : activeNight), palette.BodyText);
            ink(0).ShouldBeGreaterThan(ink(1), "the peak's solid dot carries more ink than the active ring: shape, not colour");

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
            InkCount(pixels, width, new RectF32(cell.X + (cell.Width * 0.4f), cell.Y, cell.Width * 0.3f, cell.Height * 0.4f),
                GuiTheme.Palette.BodyText).ShouldBe(0, $"{night}: nothing between the day number and the Moon");
        }
    }

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

    /// <summary>Written under the ORIGINAL temp (<c>TestRunTemp.Root</c>), which outlives the run folder, for a person to look at.</summary>
    private static void Save(string name, byte[] pixels, int width, int height)
    {
        var dir = Directory.CreateDirectory(Path.Combine(TestRunTemp.Root, "MeteorShowerCalendar")).FullName;
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(pixels, width, height));
    }
}
