using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The night calendar off the status-bar date: which night is planned, choosing another, the one multi-day forecast
/// behind the calendar AND the planner's band, and the popover a person actually clicks (docs/plans/night-calendar.md).
/// </summary>
/// <remarks>
/// Every forecast here is a FRESH cache file under the drivers' own names, so the real drivers answer and nothing
/// reaches the network; a test that let one through would be asking Open-Meteo about Glen Waverley.
/// </remarks>
[Collection("UI")]
public class NightCalendarTests(ITestOutputHelper output)
{
    private const double Latitude = -37.877;
    private const double Longitude = 145.178;
    private static readonly TimeSpan Aest = TimeSpan.FromHours(10);

    // 20:00 at the site on Saturday 19 September 2026: tonight is the 19th's evening.
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Tonight = new DateOnly(2026, 9, 19);

    private static Profile SiteProfile(Uri? weather) => new Profile(Guid.NewGuid(), "Test", ProfileData.Empty with
    {
        Mount = new Uri("ascom://GSServer"),
        SiteLatitude = Latitude,
        SiteLongitude = Longitude,
        SiteElevation = 120.0,
        Weather = weather ?? NoneDevice.Instance.DeviceUri,
    });

    private static Transform SiteTransform() => new Transform(SystemTimeProvider.Instance)
    {
        SiteLatitude = Latitude,
        SiteLongitude = Longitude,
        SiteElevation = 120,
        SiteTemperature = 15,
        DateTimeOffset = Now,
    };

    /// <summary>A planner on <paramref name="evening"/>'s night (null: tonight, unpinned), its window computed.</summary>
    private static PlannerState Planner(DateOnly? evening = null)
    {
        var state = new PlannerState { SiteLatitude = Latitude, SiteLongitude = Longitude, SiteTimeZone = Aest };
        if (evening is { } pinned)
        {
            state.PlanningDate = new DateTimeOffset(pinned.ToDateTime(new TimeOnly(20, 0)), Aest);
        }

        var night = NightSummary.Compute(SiteTransform(), evening ?? Tonight);
        state.AstroDark = night.DarkStart;
        state.AstroTwilight = night.DarkEnd;
        return state;
    }

    private static HourlyWeatherForecast Hour(DateTimeOffset time, double cloud) =>
        new HourlyWeatherForecast(time, CloudCover: cloud, Precipitation: 0, Temperature: 10, Humidity: 60,
            DewPoint: 3, WindSpeed: 2, WindGust: 4, WindDirection: 270, Visibility: 20000, WeatherCode: 0,
            WindSpeed250hPa: 10);

    /// <summary>Writes Open-Meteo's answer for [start, end] where the driver will look for it, fresh at Now.</summary>
    private static string WriteFreshCache(IExternal external, DateTimeOffset start, DateTimeOffset end, double cloud)
    {
        var dir = external.CreateSubDirectoryInAppDataFolder("Weather");
        var path = Path.Combine(dir.FullName,
            $"{Latitude:F2}_{Longitude:F2}_{start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json");
        var first = new DateTimeOffset(start.UtcDateTime.Date.AddHours(start.UtcDateTime.Hour), TimeSpan.Zero);
        var hours = new List<HourlyWeatherForecast>();
        for (var t = first; t <= end; t = t.AddHours(1))
        {
            hours.Add(Hour(t, cloud));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(hours, OpenMeteoJsonContext.Default.ListHourlyWeatherForecast));
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        return path;
    }

    private static string WriteFreshRangeCache(IExternal external, double cloud)
    {
        var (start, end) = ExtendedForecast.RangeFor(Now);
        return WriteFreshCache(external, start, end, cloud);
    }

    [Fact]
    public void AMonthIsSixWeeksOfNightsFromWhereTheWeekStarts()
    {
        var monday = NightCalendarActions.MonthGrid(new DateOnly(2026, 9, 17), DayOfWeek.Monday);
        monday.Length.ShouldBe(42);
        monday[0].ShouldBe(new DateOnly(2026, 8, 31), "1 September 2026 is a Tuesday");
        monday.ShouldContain(new DateOnly(2026, 9, 30));
        monday.Zip(monday.Skip(1)).ShouldAllBe(p => p.Second == p.First.AddDays(1));

        NightCalendarActions.MonthGrid(new DateOnly(2026, 9, 1), DayOfWeek.Sunday)[0].ShouldBe(new DateOnly(2026, 8, 30));
    }

    [Fact]
    public void TheNightBeingPlannedIsTheEveningBeforeNoon()
    {
        var state = new PlannerState { SiteTimeZone = Aest };

        // 03:00 on the 20th at the site is still the 19th's night.
        NightCalendarActions.PlanningEveningDate(state, new FakeTimeProviderWrapper(new DateTimeOffset(2026, 9, 19, 17, 0, 0, TimeSpan.Zero)))
            .ShouldBe(Tonight);

        state.PlanningDate = new DateTimeOffset(2026, 9, 25, 21, 0, 0, Aest);
        NightCalendarActions.PlanningEveningDate(state, new FakeTimeProviderWrapper(Now)).ShouldBe(new DateOnly(2026, 9, 25));
    }

    [Fact]
    public void PickingANightPlansItAtTheSameTimeOfDayAndPickingTonightUnpins()
    {
        var clock = new FakeTimeProviderWrapper(Now);
        var state = new PlannerState { SiteTimeZone = Aest };
        var sky = new SkyMapState { TimeOffset = TimeSpan.FromHours(3) };
        state.Calendar.Popover.Open();

        NightCalendarActions.PickNight(state, new DateOnly(2026, 9, 25), clock, sky);

        state.PlanningDate.ShouldBe(new DateTimeOffset(2026, 9, 25, 20, 0, 0, Aest), "the evening chosen, at the time being looked at");
        state.NeedsRecompute.ShouldBeTrue();
        state.Calendar.Popover.IsOpen.ShouldBeFalse();
        sky.TimeOffset.ShouldBe(TimeSpan.Zero, "a scrubbed sky map would sit on another night");

        NightCalendarActions.PickNight(state, Tonight, clock);
        state.PlanningDate.ShouldBeNull("tonight follows the clock, as the label's old reset did");
    }

    [Fact]
    public async Task OneForecastServesTheCalendarAndThePlannersBandAndIsReusedWithoutAsking()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        var clock = sp.GetRequiredService<ITimeProvider>();
        var cache = WriteFreshRangeCache(external, cloud: 5);
        var state = Planner();
        var profile = SiteProfile(new OpenMeteoDevice().DeviceUri);

        await NightCalendarActions.RefreshAsync(state, profile, sp, clock, external.AppLogger, ct);

        var data = state.Calendar.Data;
        data.Forecast.ShouldNotBeNull().Hours.Count.ShouldBe(17 * 24);
        foreach (var night in NightCalendarActions.MonthGrid(Tonight, NightCalendarActions.FirstDayOfWeek))
        {
            data.Nights.ShouldContainKey(night, "the planned night's month is summarised with the forecast");
        }
        NightVerdict.For(data.Nights[Tonight]).IsForecast.ShouldBeTrue();

        var band = state.WeatherForecast.ShouldNotBeNull();
        band.ShouldNotBeEmpty();
        band.ShouldAllBe(h => h.Time >= state.AstroDark.AddHours(-1) && h.Time <= state.AstroTwilight.AddHours(1),
            "the planner's band is tonight's slice of the same forecast");
        state.WeatherForecastOrigin.ShouldBeSameAs(data.Forecast);

        // Ten minutes later, with the cache file gone: the forecast in memory is still inside the drivers' own
        // lifetime, so nothing is asked of them at all.
        File.Delete(cache);
        external.TimeProvider.Advance(TimeSpan.FromMinutes(10));
        await NightCalendarActions.RefreshAsync(state, profile, sp, clock, external.AppLogger, ct);

        state.Calendar.Data.Generation.ShouldBe(data.Generation, "reused, not refetched");
        state.Calendar.Data.Forecast.ShouldBeSameAs(data.Forecast);
        state.WeatherForecast.ShouldNotBeNull().Count.ShouldBe(band.Count);
    }

    [Fact]
    public async Task ANightPastTheHorizonAsksForNothingAndHasNoBand()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        WriteFreshRangeCache(external, cloud: 5);
        var state = Planner(new DateOnly(2026, 10, 20));

        await NightCalendarActions.RefreshAsync(state, SiteProfile(new OpenMeteoDevice().DeviceUri), sp,
            sp.GetRequiredService<ITimeProvider>(), external.AppLogger, ct);

        state.WeatherForecast.ShouldBeNull("Open-Meteo would refuse the request, so it is not made");
        var night = state.Calendar.Data.Nights[new DateOnly(2026, 10, 20)];
        night.Forecast.ShouldBeNull();
        NightVerdict.For(night).IsForecast.ShouldBeFalse("beyond the horizon, the Moon alone");
    }

    [Fact]
    public async Task ANightSteppedBackToBeforeTheRangeStillGetsItsOwnForecast()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        WriteFreshRangeCache(external, cloud: 5);
        var state = Planner(new DateOnly(2026, 9, 10));
        WriteFreshCache(external, state.AstroDark.AddHours(-1), state.AstroTwilight.AddHours(1), cloud: 70);

        await NightCalendarActions.RefreshAsync(state, SiteProfile(new OpenMeteoDevice().DeviceUri), sp,
            sp.GetRequiredService<ITimeProvider>(), external.AppLogger, ct);

        state.WeatherForecast.ShouldNotBeNull().ShouldNotBeEmpty();
        state.WeatherForecast.ShouldAllBe(h => h.CloudCover == 70, "that night's own request, not the range's");
        state.WeatherForecastOrigin.ShouldNotBeNull().Provider.ShouldBe("Open-Meteo");
    }

    [Fact]
    public async Task WithNoWeatherDeviceTheCalendarIsTheMoonAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        var state = Planner();

        await NightCalendarActions.RefreshAsync(state, SiteProfile(weather: null), sp,
            sp.GetRequiredService<ITimeProvider>(), external.AppLogger, ct);

        state.WeatherForecast.ShouldBeNull();
        state.Calendar.Data.Forecast.ShouldBeNull();
        state.Calendar.Data.Nights.Count.ShouldBe(NightCalendarActions.GridDays);
        state.Calendar.Data.Nights.Values.ShouldAllBe(n => n.Forecast == null);
    }

    /// <summary>A popover on a CPU surface over a planner whose September is summarised without a forecast.</summary>
    private static async Task<(NightCalendarPopover<RgbaImage> Popover, RgbaImageRenderer Renderer, PlannerState State,
        FakeTimeProviderWrapper Clock)> OpenCalendarAsync(ITestOutputHelper output, PlannerState? state = null)
    {
        var external = new FakeExternal(output, now: Now);
        var sp = external.BuildServiceProvider();
        state ??= Planner();
        await NightCalendarActions.RefreshAsync(state, SiteProfile(weather: null), sp,
            sp.GetRequiredService<ITimeProvider>(), external.AppLogger, TestContext.Current.CancellationToken);

        var renderer = new RgbaImageRenderer(1400, 900);
        var popover = new NightCalendarPopover<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        state.Calendar.Popover.Open();
        return (popover, renderer, state, external.TimeProvider);
    }

    private static readonly RectF32 DateLabel = new RectF32(600f, 0f, 200f, 28f);
    private static readonly RectF32 Window = new RectF32(0f, 0f, 1400f, 900f);

    private static RectF32 Region(PixelWidgetBase<RgbaImage> widget, string action)
    {
        var region = widget.GetRegisteredRegions().Single(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);
        return new RectF32(region.X, region.Y, region.Width, region.Height);
    }

    [Fact]
    public async Task AClickOnANightPlansItAndClosesTheCalendar()
    {
        var (popover, renderer, state, clock) = await OpenCalendarAsync(output);
        using var _ = renderer;

        popover.Render(state, DateLabel, Window, clock);

        popover.GetRegisteredRegions().Count(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("Night:"))
            .ShouldBe(NightCalendarActions.GridDays, "six weeks of nights");
        var cell = Region(popover, "Night:2026-09-25");
        cell.Y.ShouldBeGreaterThanOrEqualTo(DateLabel.Y + DateLabel.Height, "the calendar hangs under the date");

        UiRouting.RoutePress(popover, cell.X + (cell.Width / 2f), cell.Y + (cell.Height / 2f));

        state.PlanningDate.ShouldNotBeNull().Date.ShouldBe(new DateTime(2026, 9, 25));
        state.Calendar.Popover.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task PagingMovesTheMonthAndAsksForItsNights()
    {
        var (popover, renderer, state, clock) = await OpenCalendarAsync(output);
        using var _ = renderer;
        var bus = new SignalBus();
        var asked = new List<DateOnly>();
        bus.Subscribe<NightCalendarMonthSignal>(s => asked.Add(s.Month));

        popover.Render(state, DateLabel, Window, clock, bus);
        state.Calendar.Month.ShouldBe(new DateOnly(2026, 9, 1), "an opening calendar shows the planned night's month");
        var next = Region(popover, "CalendarNext");
        UiRouting.RoutePress(popover, next.X + (next.Width / 2f), next.Y + (next.Height / 2f));
        bus.ProcessPending();

        state.Calendar.Month.ShouldBe(new DateOnly(2026, 10, 1));
        asked.ShouldBe([new DateOnly(2026, 10, 1)]);
        state.Calendar.Popover.IsOpen.ShouldBeTrue("paging is not choosing");
    }

    [Fact]
    public async Task TheTonightButtonUnpinsTheDate()
    {
        var (popover, renderer, state, clock) = await OpenCalendarAsync(output, Planner(new DateOnly(2026, 9, 25)));
        using var _ = renderer;

        popover.Render(state, DateLabel, Window, clock);
        var tonight = Region(popover, "CalendarTonight");
        UiRouting.RoutePress(popover, tonight.X + (tonight.Width / 2f), tonight.Y + (tonight.Height / 2f));

        state.PlanningDate.ShouldBeNull();
        state.Calendar.Popover.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task EscapeClosesTheCalendar()
    {
        var (popover, renderer, state, clock) = await OpenCalendarAsync(output);
        using var _ = renderer;

        popover.Render(state, DateLabel, Window, clock);
        UiRouting.RouteKey(popover, InputKey.Escape);

        state.Calendar.Popover.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void TheDetailSaysWhereTheVerdictCameFrom()
    {
        var state = Planner();
        var night = NightSummary.Compute(SiteTransform(), Tonight);
        var none = NightCalendarData.Empty with { Nights = NightCalendarData.Empty.Nights.Add(Tonight, night) };

        var lines = NightCalendarPopover<RgbaImage>.DetailLines(state, Tonight, none);

        lines[0].ShouldStartWith(Tonight.ToString("ddd d MMM", CultureInfo.CurrentCulture));
        lines[1].ShouldStartWith("Dark ");
        lines[2].ShouldBe("No forecast: the Moon alone");

        var beyond = none with { Forecast = new ExtendedForecast([], "Open-Meteo", null, null) };
        NightCalendarPopover<RgbaImage>.DetailLines(state, Tonight, beyond)[2].ShouldBe("Beyond the forecast: the Moon alone");
    }

    [Theory]
    [InlineData(0.1, true)]
    [InlineData(0.25, false)]
    [InlineData(0.5, true)]
    [InlineData(0.75, false)]
    [InlineData(0.9, true)]
    public void TheDrawnMoonIsLitByItsIlluminationOnTheRightLimb(double illumination, bool litOnRight)
    {
        using var renderer = new RgbaImageRenderer(220, 220);
        var popover = new NightCalendarPopover<RgbaImage>(renderer);
        var lit = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        var dark = new RGBAColor32(0x00, 0x00, 0xff, 0xff);

        popover.DrawMoonPhase(new RectF32(10f, 10f, 200f, 200f), illumination, litOnRight, lit, dark);

        var pixels = renderer.Surface.Pixels;
        long litCount = 0, discCount = 0, litX = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var isLit = pixels[i] == 0xff && pixels[i + 2] == 0xff;
            var isDark = pixels[i] == 0 && pixels[i + 2] == 0xff;
            if (isLit || isDark)
            {
                discCount++;
            }
            if (isLit)
            {
                litCount++;
                litX += (i / 4) % 220;
            }
        }

        ((double)litCount / discCount).ShouldBe(illumination, 0.03, "the lit area is the illuminated fraction");
        var centroid = (double)litX / litCount;
        (litOnRight ? centroid > 110 : centroid < 110).ShouldBeTrue($"lit centroid {centroid:F1} on the {(litOnRight ? "right" : "left")}");
    }

    private static readonly Target South = new Target(1.0, -80.0, "South", null);
    private static readonly Target North = new Target(12.0, 60.0, "North", null);

    [Fact]
    public async Task TheOpenCalendarAsksForItsPinsOnceAndGetsEachNightsPointings()
    {
        var state = Planner();
        state.Proposals = [new ProposedObservation(South)];
        var (popover, renderer, _, clock) = await OpenCalendarAsync(output, state);
        using var surface = renderer;
        var bus = new SignalBus();
        var asked = new List<DateOnly>();
        bus.Subscribe<NightCalendarMonthSignal>(s => asked.Add(s.Month));

        popover.Render(state, DateLabel, Window, clock, bus);
        popover.Render(state, DateLabel, Window, clock, bus);
        bus.ProcessPending();
        asked.ShouldBe([new DateOnly(2026, 9, 1)], "asked for once, not every frame");

        NightCalendarActions.EnsureMonth(state, SiteProfile(weather: null), clock, asked[0]);

        var data = state.Calendar.Data;
        data.PinsKey.ShouldBe(NightCalendarActions.PinsKey(state));
        NightCalendarActions.HasPins(data, NightCalendarActions.PinsKey(state), asked[0]).ShouldBeTrue();
        data.Pins[Tonight].Pointings.ShouldHaveSingleItem().Name.ShouldBe("South");
        NightCalendarPopover<RgbaImage>.DetailLines(state, Tonight, data, pinsCurrent: true)[^1]
            .ShouldStartWith("South  ", Case.Sensitive, "a line per pinned pointing under the night's own");
    }

    [Fact]
    public async Task ChangingThePinsReplacesThePinsHalfAndKeepsTheNights()
    {
        var state = Planner();
        state.Proposals = [new ProposedObservation(South)];
        var (_, renderer, _, clock) = await OpenCalendarAsync(output, state);
        using var surface = renderer;
        var month = new DateOnly(2026, 9, 1);
        NightCalendarActions.EnsureMonth(state, SiteProfile(weather: null), clock, month);
        var nights = state.Calendar.Data.Nights;

        state.Proposals = [new ProposedObservation(North)];
        NightCalendarActions.HasPins(state.Calendar.Data, NightCalendarActions.PinsKey(state), month)
            .ShouldBeFalse("a new pin set is a new key");
        NightCalendarActions.EnsureMonth(state, SiteProfile(weather: null), clock, month);

        state.Calendar.Data.Pins[Tonight].Pointings.ShouldHaveSingleItem().Name.ShouldBe("North");
        state.Calendar.Data.Nights.ShouldBeSameAs(nights, "the sky half did not move");
        NightCalendarPopover<RgbaImage>.DetailLines(state, Tonight, state.Calendar.Data, pinsCurrent: true)[^1]
            .ShouldStartWith("North: never above 20");
    }

    [Fact]
    public async Task WithNothingPinnedTheCalendarStopsAskingAndShowsNoPins()
    {
        var (popover, renderer, state, clock) = await OpenCalendarAsync(output);
        using var surface = renderer;
        var month = new DateOnly(2026, 9, 1);

        NightCalendarActions.EnsureMonth(state, SiteProfile(weather: null), clock, month);

        var data = state.Calendar.Data;
        NightCalendarActions.HasPins(data, NightCalendarActions.PinsKey(state), month).ShouldBeTrue("the empty set is recorded");
        data.Pins.ShouldBeEmpty();
        NightCalendarPopover<RgbaImage>.DetailLines(state, Tonight, data, pinsCurrent: true).Count.ShouldBe(3);
    }

    [Fact]
    public void TheArrowsMoveACursorFromThePlannedNightAndEnterPlansIt()
    {
        var clock = new FakeTimeProviderWrapper(Now);
        var state = new PlannerState { SiteTimeZone = Aest };
        state.Calendar.Popover.Open();
        state.Calendar.Month = new DateOnly(2026, 9, 1);
        var bus = new SignalBus();
        var asked = new List<DateOnly>();
        bus.Subscribe<NightCalendarMonthSignal>(s => asked.Add(s.Month));

        NightCalendarActions.HandleKey(state, InputKey.Right, clock, bus).ShouldBeTrue();
        state.Calendar.Cursor.ShouldBe(Tonight.AddDays(1), "from the planned night, tonight");
        NightCalendarActions.HandleKey(state, InputKey.Down, clock, bus).ShouldBeTrue();
        state.Calendar.Cursor.ShouldBe(Tonight.AddDays(8));
        state.Calendar.Month.ShouldBe(new DateOnly(2026, 9, 1), "the 27th is still on September's grid");

        // Three more weeks: 18 October is past September's six-week grid whichever day the week starts on.
        for (var i = 0; i < 3; i++)
        {
            NightCalendarActions.HandleKey(state, InputKey.Down, clock, bus).ShouldBeTrue();
        }
        state.Calendar.Cursor.ShouldBe(new DateOnly(2026, 10, 18));
        state.Calendar.Month.ShouldBe(new DateOnly(2026, 10, 1), "a cursor off the grid takes the month with it");
        bus.ProcessPending();
        asked.ShouldBe([new DateOnly(2026, 10, 1)], "and asks for that month's nights, once");

        NightCalendarActions.HandleKey(state, InputKey.Enter, clock, bus).ShouldBeTrue();
        state.PlanningDate.ShouldNotBeNull().Date.ShouldBe(new DateTime(2026, 10, 18));
        state.Calendar.Popover.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void PagingMovesTheCursorAMonthAndTPlansTonight()
    {
        var clock = new FakeTimeProviderWrapper(Now);
        var state = new PlannerState { SiteTimeZone = Aest, PlanningDate = new DateTimeOffset(2026, 9, 25, 20, 0, 0, Aest) };
        state.Calendar.Popover.Open();
        state.Calendar.Month = new DateOnly(2026, 9, 1);

        NightCalendarActions.HandleKey(state, InputKey.PageDown, clock).ShouldBeTrue();
        state.Calendar.Cursor.ShouldBe(new DateOnly(2026, 10, 25));
        state.Calendar.Month.ShouldBe(new DateOnly(2026, 10, 1));
        NightCalendarActions.HandleKey(state, InputKey.PageUp, clock).ShouldBeTrue();
        state.Calendar.Month.ShouldBe(new DateOnly(2026, 9, 1));

        NightCalendarActions.HandleKey(state, InputKey.Q, clock).ShouldBeFalse("not the calendar's key");
        NightCalendarActions.HandleKey(state, InputKey.T, clock).ShouldBeTrue();
        state.PlanningDate.ShouldBeNull();
    }

    [Fact]
    public async Task AnOpenCalendarTakesItsKeysThroughThePopover()
    {
        var (popover, renderer, state, clock) = await OpenCalendarAsync(output);
        using var surface = renderer;

        popover.Render(state, DateLabel, Window, clock);
        UiRouting.RouteKey(popover, InputKey.Left);

        state.Calendar.Cursor.ShouldBe(Tonight.AddDays(-1), "the router hands the popover's content its keys");
        state.Calendar.Popover.IsOpen.ShouldBeTrue();

        NightCalendarActions.PrepareToOpen(state.Calendar);
        state.Calendar.Cursor.ShouldBeNull("a reopened calendar starts on the planned night again");
        state.Calendar.Month.ShouldBe(default);
    }
}
