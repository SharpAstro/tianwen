using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The atlas info box's night curve (#2059, docs/plans/yearly-planner.md section A) through the REAL tab render on the CPU surface:
/// the shade's pixels in the panel for a selected object, nothing without a site or a night, one score per (object, night, minimum
/// altitude) however many frames, a pan and a scrub, the scrub moving only the now mark, a body with no shade, and the Night palette
/// from <see cref="GuiTheme"/>. The score runs off the render thread, so every test awaits the cache's own task, never a clock.
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapAtlasNightCurveTests
{
    private const double Lat = 48.0;
    private const double Lon = 11.0;

    // Mid-October at 48N: astronomical dusk about 18:40 UTC, dawn about 04:10 UTC. The planner's night is whatever the planner
    // state says, so the exact figures are the tests' own.
    internal static readonly DateTimeOffset AstroDark = new(2026, 10, 13, 18, 40, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset AstroTwilight = new(2026, 10, 14, 4, 10, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Evening = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);

    // Messier 31: well up all night from 48N in October.
    private const double M31Ra = 0.7123;
    private const double M31Dec = 41.27;

    // The tab publishes the view matrix each frame as the GPU pipelines do, as the disc tests' tab does.
    internal sealed class CurveTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site,
            SkyMapDrawPhase phase = SkyMapDrawPhase.All)
        {
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site, phase);
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }
    }

    internal sealed record Rig(CurveTab Tab, RgbaImageRenderer Renderer, PlannerState Planner, FakeTimeProviderWrapper Time, int Width, int Height)
        : IDisposable
    {
        public RectF32 Content => new(0, 0, Width, Height);

        public void Render() => Tab.Render(Planner, Content, Time);

        /// <summary>Renders, awaits the score the frame dispatched (if any) and renders again, so the curve is in the frame.</summary>
        public async Task RenderSettledAsync()
        {
            Render();
            if (Tab.State.NightCurveTask is { } score)
            {
                await score.WaitAsync(TestContext.Current.CancellationToken);
            }
            Render();
        }

        public void Select(string name, double ra, double dec)
            => Tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromPosition(name, ra, dec, Lat, Lon, Planner.PlanningDate ?? Evening, default);

        public void Dispose() => Renderer.Dispose();
    }

    internal static async Task<Rig> RigAsync(
        CancellationToken cancellationToken, float dpiScale = 1f, int width = 1000, int height = 800,
        DateTimeOffset? dark = null, DateTimeOffset? twilight = null, DateTimeOffset? viewing = null)
    {
        var db = await SharedCatalogDB.InitAsync(cancellationToken);
        var renderer = new RgbaImageRenderer(width, height);
        var tab = new CurveTab(renderer) { FontPath = FontResolver.ResolveSystemFont(), DpiScale = dpiScale };
        var planner = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = Lat,
            SiteLongitude = Lon,
            SiteTimeZone = TimeSpan.FromHours(2),
            PlanningDate = viewing ?? Evening,
            AstroDark = dark ?? AstroDark,
            AstroTwilight = twilight ?? AstroTwilight,
            MinHeightAboveHorizon = 20,
        };
        var rig = new Rig(tab, renderer, planner, new FakeTimeProviderWrapper(viewing ?? Evening), width, height);
        rig.Render();
        tab.State.ShowHorizon = false;
        return rig;
    }

    // The panel's curve slot as the last frame drew it, in device pixels.
    private static (int X0, int Y0, int X1, int Y1) Slot(Rig rig)
    {
        var rect = rig.Tab.LastNightCurveRect.ShouldNotBeNull("the panel drew no night curve slot");
        return ((int)rect.X, (int)rect.Y, (int)(rect.X + rect.Width), (int)(rect.Y + rect.Height));
    }

    // The shade is the line colour at partial alpha over the panel: greenish and neither the line's full colour nor the grey text.
    // Returns how many pixels of the slot are shade and their mean green, which falls as the shade's alpha does.
    private static (int Count, double MeanGreen) ShadeIn(Rig rig, (int X0, int Y0, int X1, int Y1) slot)
    {
        var pixels = rig.Renderer.Surface.Pixels;
        var (count, sum) = (0, 0L);
        for (var y = slot.Y0; y < slot.Y1; y++)
        {
            for (var x = slot.X0; x < slot.X1; x++)
            {
                var i = ((y * rig.Width) + x) * 4;
                int r = pixels[i], g = pixels[i + 1];
                if (g - r >= 12 && g is > 0x2c and < 0xaa)
                {
                    count++;
                    sum += g;
                }
            }
        }
        return (count, count == 0 ? 0 : (double)sum / count);
    }

    private static int PixelsDiffering(Rig rig, (int X0, int Y0, int X1, int Y1) slot, byte[] before)
    {
        var pixels = rig.Renderer.Surface.Pixels;
        var differing = 0;
        for (var y = slot.Y0; y < slot.Y1; y++)
        {
            for (var x = slot.X0; x < slot.X1; x++)
            {
                var i = ((y * rig.Width) + x) * 4;
                if (pixels[i] != before[i] || pixels[i + 1] != before[i + 1] || pixels[i + 2] != before[i + 2])
                {
                    differing++;
                }
            }
        }
        return differing;
    }

    // The now mark's centre is exactly SparklineNow's yellow; neither the shade, the line nor grey text has that red with that blue.
    private static int NowMarkPixels(Rig rig, (int X0, int Y0, int X1, int Y1) slot)
    {
        var pixels = rig.Renderer.Surface.Pixels;
        var count = 0;
        for (var y = slot.Y0; y < slot.Y1; y++)
        {
            for (var x = slot.X0; x < slot.X1; x++)
            {
                var i = ((y * rig.Width) + x) * 4;
                if (pixels[i] >= 0xE8 && pixels[i + 1] >= 0xD8 && pixels[i + 2] <= 0x90)
                {
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// A selected object that is up in the dark has its curve under the rows, the counting part shaded: the pixels are in the slot,
    /// at 1x and 1.5x, and the model behind them has usable time and a window the planner would state.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ASelectedObjectShowsItsCurveWithTheShadedPart(float dpiScale)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale);
        rig.Select("Messier 31", M31Ra, M31Dec);
        await rig.RenderSettledAsync();

        var curve = rig.Tab.LastNightCurve.ShouldNotBeNull("no curve was drawn for a selected object up in the dark");
        curve.Shaded.ShouldBeTrue();
        curve.UsableTime.ShouldBeGreaterThan(TimeSpan.FromHours(4));
        curve.WindowStart.ShouldNotBeNull();
        curve.AstroDark.ShouldBe(AstroDark);
        curve.AstroTwilight.ShouldBe(AstroTwilight);
        var slot = Slot(rig);
        slot.X1.ShouldBeLessThanOrEqualTo(rig.Width);
        slot.Y1.ShouldBeLessThanOrEqualTo(rig.Height);
        ShadeIn(rig, slot).Count.ShouldBeGreaterThan((int)(500 * dpiScale * dpiScale), "the counting part of the night is not shaded");
    }

    /// <summary>An object that never gets above the minimum in the dark has its curve with no shade, and its line says so.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnObjectThatNeverCountsHasNoShade()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Select("Far south", 12.0, -80.0);
        await rig.RenderSettledAsync();

        var curve = rig.Tab.LastNightCurve.ShouldNotBeNull();
        curve.UsableTime.ShouldBe(TimeSpan.Zero);
        curve.WindowStart.ShouldBeNull();
        ShadeIn(rig, Slot(rig)).Count.ShouldBeLessThan(40, "an object that never counts was shaded");
    }

    /// <summary>
    /// Nothing is drawn, or scored, for a host with no site, or no night: the panel is as it was, and no score runs.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task NoSiteOrNoNightDrawsNothingAndScoresNothing()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Select("Messier 31", M31Ra, M31Dec);

        rig.Planner.SiteLatitude = double.NaN;
        rig.Render();
        rig.Tab.LastNightCurveRect.ShouldBeNull();
        rig.Tab.State.NightCurveComputes.ShouldBe(0);

        rig.Planner.SiteLatitude = Lat;
        rig.Planner.AstroTwilight = rig.Planner.AstroDark;
        rig.Render();
        rig.Tab.LastNightCurveRect.ShouldBeNull();
        rig.Tab.State.NightCurveComputes.ShouldBe(0);

        // The same panel with both, to be sure the nothing above is the rule and not a rig that cannot draw it.
        rig.Planner.AstroTwilight = AstroTwilight;
        await rig.RenderSettledAsync();
        rig.Tab.LastNightCurve.ShouldNotBeNull();
        rig.Tab.State.NightCurveComputes.ShouldBe(1);
    }

    /// <summary>
    /// ONE score per (object, night, minimum altitude): a pan and a time scrub of many frames recompute nothing, a date change (a new
    /// night) recomputes once, a new minimum altitude once. Each bound is paired with a count above zero.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheScoreIsComputedOnceOverAPanAndAScrubAndAgainOnADateChange()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Select("Messier 31", M31Ra, M31Dec);
        await rig.RenderSettledAsync();
        rig.Tab.State.NightCurveComputes.ShouldBe(1);
        var curve = rig.Tab.LastNightCurve.ShouldNotBeNull();

        for (var frame = 0; frame < 6; frame++)
        {
            rig.Tab.State.CenterRA += 0.2;
            rig.Tab.State.TimeOffset += TimeSpan.FromMinutes(20);
            rig.Render();
            rig.Tab.State.NightCurveTask.ShouldBeNull("a pan or a scrub dispatched a score");
        }
        rig.Tab.State.NightCurveComputes.ShouldBe(1);
        ReferenceEquals(rig.Tab.LastNightCurve, curve).ShouldBeTrue("the curve was rebuilt for a pan or a scrub");

        // The planner moves to the next night, as its date shift does.
        rig.Tab.State.TimeOffset = TimeSpan.Zero;
        rig.Planner.PlanningDate = Evening + TimeSpan.FromDays(1);
        rig.Planner.AstroDark = AstroDark + TimeSpan.FromDays(1);
        rig.Planner.AstroTwilight = AstroTwilight + TimeSpan.FromDays(1);
        await rig.RenderSettledAsync();
        rig.Tab.State.NightCurveComputes.ShouldBe(2);
        var next = rig.Tab.LastNightCurve.ShouldNotBeNull();
        next.AstroDark.ShouldBe(AstroDark + TimeSpan.FromDays(1));

        rig.Planner.MinHeightAboveHorizon = 40;
        await rig.RenderSettledAsync();
        rig.Tab.State.NightCurveComputes.ShouldBe(3);
        rig.Tab.LastNightCurve.ShouldNotBeNull().UsableTime.ShouldBeLessThan(next.UsableTime);

        // Frames that change nothing change nothing.
        for (var frame = 0; frame < 3; frame++)
        {
            rig.Render();
        }
        rig.Tab.State.NightCurveComputes.ShouldBe(3);
    }

    /// <summary>
    /// The scrub moves only the now mark: the curve is the same object, the slot's pixels differ by the mark alone (a small share of
    /// them), and a scrub out of the night takes the mark away.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheScrubMovesOnlyTheNowMark()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Select("Messier 31", M31Ra, M31Dec);
        await rig.RenderSettledAsync();
        var curve = rig.Tab.LastNightCurve.ShouldNotBeNull();
        var slot = Slot(rig);
        NowMarkPixels(rig, slot).ShouldBeGreaterThan(0, "no now mark inside the night");
        byte[] before = [.. rig.Renderer.Surface.Pixels];

        rig.Tab.State.TimeOffset = TimeSpan.FromHours(3);
        rig.Render();

        ReferenceEquals(rig.Tab.LastNightCurve, curve).ShouldBeTrue();
        rig.Tab.State.NightCurveComputes.ShouldBe(1);
        var differing = PixelsDiffering(rig, slot, before);
        differing.ShouldBeGreaterThan(0, "the mark did not move");
        differing.ShouldBeLessThan((slot.X1 - slot.X0) * (slot.Y1 - slot.Y0) / 8, "a scrub changed more than the now mark");
        NowMarkPixels(rig, slot).ShouldBeGreaterThan(0);

        // 8 hours on is past dawn: the curve stays, the mark goes.
        rig.Tab.State.TimeOffset = TimeSpan.FromHours(8);
        rig.Render();
        NowMarkPixels(rig, slot).ShouldBe(0, "a now mark outside the night");
        ShadeIn(rig, slot).Count.ShouldBeGreaterThan(500);
    }

    /// <summary>
    /// A moving body is keyed by its index, never its live position: Jupiter scrubbed through twelve hours is scored once. The Moon
    /// gets its curve with no shade.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMovingBodyIsScoredOnceAndTheMoonGetsNoShade()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        var utc = Evening;

        async Task SelectBodyAsync(CatalogIndex body)
        {
            rig.Tab.State.TryGetPlanetCached(body, utc + rig.Tab.State.TimeOffset, out var marker).ShouldBeTrue();
            rig.Tab.State.Search.InfoPanel = SkyMapSearchActions.PlanetInfoPanel(
                rig.Planner.ObjectDb.ShouldNotBeNull(), marker, Lat, Lon, utc, SiteContext.Create(Lat, Lon, utc));
            await rig.RenderSettledAsync();
        }

        await SelectBodyAsync(CatalogIndex.Jupiter);
        rig.Tab.LastNightCurve.ShouldNotBeNull().Shaded.ShouldBeTrue();
        rig.Tab.State.NightCurveComputes.ShouldBe(1);
        for (var step = 1; step <= 6; step++)
        {
            rig.Tab.State.TimeOffset = TimeSpan.FromHours(2 * step);
            rig.Render();
        }
        rig.Tab.State.NightCurveComputes.ShouldBe(1);

        rig.Tab.State.TimeOffset = TimeSpan.Zero;
        await SelectBodyAsync(CatalogIndex.Moon);
        rig.Tab.State.NightCurveComputes.ShouldBe(2);
        var moon = rig.Tab.LastNightCurve.ShouldNotBeNull();
        moon.Shaded.ShouldBeFalse();
        moon.WindowStart.ShouldBeNull();
        moon.Samples.ShouldAllBe(s => s.Weight == 0f);
        ShadeIn(rig, Slot(rig)).Count.ShouldBeLessThan(40);
    }

    /// <summary>
    /// The shade is paler where the Moon penalises the night: an object placed at the full Moon's own position has a lower mean shade
    /// than Messier 31 on the same night, and its weights (the planner's own moon factor) say why.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheShadeIsPalerWhereTheMoonPenalisesIt()
    {
        var dark = new DateTimeOffset(2026, 10, 26, 17, 35, 0, TimeSpan.Zero);
        var twilight = new DateTimeOffset(2026, 10, 27, 4, 40, 0, TimeSpan.Zero);
        var viewing = new DateTimeOffset(2026, 10, 26, 22, 0, 0, TimeSpan.Zero);
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dark: dark, twilight: twilight, viewing: viewing);
        VSOP87a.ReduceJ2000(CatalogIndex.Moon, dark + (twilight - dark) / 2, out var moonRa, out var moonDec, out _).ShouldBeTrue();

        rig.Select("Messier 31", M31Ra, M31Dec);
        await rig.RenderSettledAsync();
        var clear = rig.Tab.LastNightCurve.ShouldNotBeNull();
        var clearShade = ShadeIn(rig, Slot(rig));

        rig.Select("Under the Moon", moonRa, moonDec);
        await rig.RenderSettledAsync();
        var washed = rig.Tab.LastNightCurve.ShouldNotBeNull();
        var washedShade = ShadeIn(rig, Slot(rig));

        washed.UsableTime.ShouldBeGreaterThan(TimeSpan.Zero);
        var lowest = float.MaxValue;
        foreach (var sample in washed.Samples)
        {
            if (sample.Weight > 0f)
            {
                lowest = Math.Min(lowest, sample.Weight);
            }
        }
        lowest.ShouldBeLessThan(0.5f, "the full Moon did not weigh on a sample beside it");
        clear.Samples.ShouldContain(s => s.Weight > lowest);
        clearShade.Count.ShouldBeGreaterThan(0);
        washedShade.Count.ShouldBeGreaterThan(0);
        washedShade.MeanGreen.ShouldBeLessThan(clearShade.MeanGreen, "the moon-washed shade is not paler");
    }

    /// <summary>The rating is the planner's against tonight's best: a stronger best leaves the object below five stars.</summary>
    [Fact(Timeout = 120_000)]
    public async Task TheRatingIsAgainstTonightsBest()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Select("Messier 31", M31Ra, M31Dec);
        await rig.RenderSettledAsync();
        rig.Tab.LastNightCurve.ShouldNotBeNull().Rating.ShouldBe(5.0f, 1e-4f, "no list yet: the object is the best there is");

        var transform = new Transform(rig.Time) { SiteLatitude = Lat, SiteLongitude = Lon, SiteElevation = 0 };
        var stronger = ObservationScheduler.ScoreTarget(new Target(M31Ra, M31Dec, "Best", null), transform, AstroDark, AstroTwilight, 20);
        stronger = stronger with { ObjectBonus = (Half)4 };
        rig.Planner.TonightsBest = [stronger];
        await rig.RenderSettledAsync();

        var rating = rig.Tab.LastNightCurve.ShouldNotBeNull().Rating;
        rating.ShouldBeLessThan(5.0f);
        rating.ShouldBeGreaterThan(0.5f);
        rig.Tab.State.NightCurveComputes.ShouldBe(2);
    }

    /// <summary>
    /// The panel is as tall while the score is computed as after: the section's height is reserved, so a curve landing does not move
    /// the panel's top edge under the pointer (its slot is the same size either side).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSectionHeightIsReservedWhileTheScoreIsComputed()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Select("Messier 31", M31Ra, M31Dec);

        rig.Render();
        var pending = rig.Tab.State.NightCurveTask.ShouldNotBeNull("no score was dispatched");
        rig.Tab.LastNightCurve.ShouldBeNull();
        var duringTop = TopOfPanel(rig);
        await pending.WaitAsync(TestContext.Current.CancellationToken);

        rig.Render();
        rig.Tab.LastNightCurve.ShouldNotBeNull();
        TopOfPanel(rig).ShouldBe(duringTop);
    }

    // The panel's top edge: the first row (from the top of the surface) at which a pixel of the panel's border colour sits in the
    // panel's column. Read off the surface, so it is what a person sees move.
    private static int TopOfPanel(Rig rig)
    {
        var pixels = rig.Renderer.Surface.Pixels;
        var x = (int)(12 * rig.Tab.DpiScale) + 4;
        for (var y = 0; y < rig.Height; y++)
        {
            var i = ((y * rig.Width) + x) * 4;
            // 0x50 0x50 0x60: SearchPanelBorder, the panel's one-pixel frame.
            if (pixels[i] == 0x50 && pixels[i + 1] == 0x50 && pixels[i + 2] == 0x60)
            {
                return y;
            }
        }
        throw new InvalidOperationException("the panel's frame was not found on the surface");
    }

    /// <summary>
    /// The panel with its curve as a person sees it, written as PNGs: a narrow and a wide content rect, a long object name, at 1x and
    /// 1.5x, the panel's bottom-left region cropped out. Asserts only that the curve was drawn; the pictures are for eyes.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f, 480)]
    [InlineData(1.5f, 480)]
    [InlineData(1f, 1100)]
    [InlineData(1.5f, 1100)]
    public async Task ThePanelWithItsCurveIsWrittenAsAPicture(float dpiScale, int width)
    {
        var ct = TestContext.Current.CancellationToken;
        const int Height = 700;
        using var rig = await RigAsync(ct, dpiScale, width, Height);
        rig.Select("NGC 7000 North America Nebula in Cygnus, a deliberately long object name", 20.98, 44.33);
        await rig.RenderSettledAsync();

        var slot = Slot(rig);
        var (x0, y0) = (0, Math.Max(0, slot.Y0 - (int)(190 * dpiScale)));
        var (cw, ch) = (Math.Min(width, (int)(400 * dpiScale)), Height - y0);
        var crop = new byte[cw * ch * 4];
        for (var y = 0; y < ch; y++)
        {
            Array.Copy(rig.Renderer.Surface.Pixels, (((y0 + y) * width) + x0) * 4, crop, y * cw * 4, cw * 4);
        }
        var dir = SharedTestData.CreateTempTestOutputDir(nameof(ThePanelWithItsCurveIsWrittenAsAPicture));
        await File.WriteAllBytesAsync(Path.Combine(dir, $"night-curve-{dpiScale:0.0}x-{width}.png"), PngWriter.Encode(crop, cw, ch), ct);
        rig.Tab.LastNightCurve.ShouldNotBeNull();
    }
}

/// <summary>
/// Night spends no blue (CLAUDE.md, "Colour Theme"): the night curve, drawn from <see cref="GuiTheme"/> under Night, has a blue
/// channel of zero in its slot and the line under it, and its line is the palette's accent. In the "UI" collection, since it flips
/// the app-wide theme.
/// </summary>
[Collection("UI")]
public sealed class SkyMapAtlasNightCurveNightTests
{
    [Fact(Timeout = 120_000)]
    public async Task NightDrawsTheCurveFromThePalette()
    {
        try
        {
            GuiTheme.Apply(UiThemeState.Night, desktopIsDark: true);
            using var rig = await SkyMapAtlasNightCurveTests.RigAsync(TestContext.Current.CancellationToken, 1.5f);
            rig.Select("Messier 31", 0.7123, 41.27);
            await rig.RenderSettledAsync();

            rig.Tab.LastNightCurve.ShouldNotBeNull();
            var rect = rig.Tab.LastNightCurveRect.ShouldNotBeNull();
            var pixels = rig.Renderer.Surface.Pixels;
            var accent = GuiTheme.Palette.Accent;
            var (blue, onAccent) = (0, 0);
            // The slot and the line of text under it.
            var bottom = (int)(rect.Y + rect.Height + 1.35f * 12f * 1.5f);
            for (var y = (int)rect.Y; y < bottom; y++)
            {
                for (var x = (int)rect.X; x < (int)(rect.X + rect.Width); x++)
                {
                    var i = ((y * rig.Width) + x) * 4;
                    blue += pixels[i + 2] != 0 ? 1 : 0;
                    onAccent += pixels[i] == accent.Red && pixels[i + 1] == accent.Green && pixels[i + 2] == accent.Blue ? 1 : 0;
                }
            }

            blue.ShouldBe(0, "pixels with any blue under Night");
            onAccent.ShouldBeGreaterThan(0, "the curve's line is not the palette's accent");
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }
}
