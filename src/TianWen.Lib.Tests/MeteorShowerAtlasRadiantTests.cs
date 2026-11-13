using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.MeteorShowers;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The sky atlas draws the radiant of every shower active on the viewing night, placed by its drift, and the pointer treats it like
/// any object: hover and click come from the one resolver, the object layers switch it off, and the active set is computed once per
/// night (docs/plans/yearly-planner.md, E).
/// </summary>
[Collection("UI")]
public sealed class MeteorShowerAtlasRadiantTests
{
    private const int Size = 600;
    private const double Latitude = 50.0;
    private const double Longitude = 8.0;

    private static MeteorShower Perseids => MeteorShowerTable.ByCode("PER").ShouldNotBeNull();

    /// <summary>The Perseids' peak night of 2026, at 21h UT (the dark of the site).</summary>
    private static DateTimeOffset PeakNight
    {
        get
        {
            var peak = MeteorShowerCalendar.PeakInstant(Perseids, 2026).ShouldNotBeNull();
            return new DateTimeOffset(peak.UtcDateTime.Date.AddHours(21), TimeSpan.Zero);
        }
    }

    private sealed class RadiantTestSkyMapTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
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

    private static PlannerState PlannerAt(ICelestialObjectDB db, DateTimeOffset night) => new()
    {
        ObjectDb = db,
        SiteLatitude = Latitude,
        SiteLongitude = Longitude,
        SiteTimeZone = TimeSpan.FromHours(2),
        PlanningDate = night,
    };

    private static void LookAtPerseids(SkyMapState state, DateTimeOffset night, double fov = 30.0)
    {
        var radiant = MeteorShowerNights.RadiantAt(Perseids, night);
        state.Mode = SkyMapMode.Equatorial;
        state.CenterRA = radiant.RaHours;
        state.CenterDec = radiant.DecDeg;
        state.FieldOfViewDeg = fov;
        state.ShowObjectOverlay = true;
        state.LastContentRect = new RectF32(0, 0, Size, Size);
    }

    private static (float X, float Y) Project(SkyMapState state, double ra, double dec)
    {
        var ppr = SkyMapProjection.PixelsPerRadian(Size, state.FieldOfViewDeg);
        SkyMapProjection.ProjectWithMatrix(ra, dec, state.ComputeViewMatrix(), ppr, Size * 0.5f, Size * 0.5f, out var x, out var y)
            .ShouldBeTrue();
        return (x, y);
    }

    [Fact(Timeout = 120_000)]
    public async Task TheActiveSetIsComputedOncePerNightAndNotPerFrame()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        using var renderer = new RgbaImageRenderer(Size, Size);
        var tab = new RadiantTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var night = PeakNight;
        var planner = PlannerAt(db, night);
        LookAtPerseids(tab.State, night);
        var time = new FakeTimeProviderWrapper(night);
        var content = new RectF32(0, 0, Size, Size);

        for (var frame = 0; frame < 30; frame++)
        {
            tab.Render(planner, content, time);
            time.Advance(TimeSpan.FromMilliseconds(16));
        }

        tab.State.ShowerCacheRebuilds.ShouldBe(1, "30 frames of one night are one computation");
        tab.State.GetShowerRadiantsCached(night).Length.ShouldBeGreaterThan(0);

        planner.PlanningDate = night.AddDays(1);
        tab.Render(planner, content, time);
        tab.State.ShowerCacheRebuilds.ShouldBe(2, "the next night is computed again, the drift having moved");
    }

    [Fact]
    public void TheActiveSetIsTheTablesWindowsAndTheRadiantsAreMovedByTheDrift()
    {
        var state = new SkyMapState();
        var night = PeakNight;
        var set = state.GetShowerRadiantsCached(night).ToArray();
        set.ShouldContain(m => m.Index == MeteorShowerIdentity.IndexOf(Perseids));
        var radiant = MeteorShowerNights.RadiantAt(Perseids, night);
        var perseid = set[Array.FindIndex(set, m => m.Code == "PER")];
        perseid.RA.ShouldBe(radiant.RaHours, 1e-9);
        perseid.Dec.ShouldBe(radiant.DecDeg, 1e-9);

        new SkyMapState().GetShowerRadiantsCached(new DateTimeOffset(2026, 3, 20, 21, 0, 0, TimeSpan.Zero)).ToArray()
            .ShouldNotContain(m => m.Code == "PER", "outside its window a shower has no radiant");
    }

    [Fact(Timeout = 120_000)]
    public async Task AClickOnARadiantSelectsTheShowerAndTheHoverAgrees()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var night = PeakNight;
        var state = new SkyMapState();
        LookAtPerseids(state, night);
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var radiant = MeteorShowerNights.RadiantAt(Perseids, night);
        var (x, y) = Project(state, radiant.RaHours, radiant.DecDeg);
        var index = MeteorShowerIdentity.IndexOf(Perseids);

        var hover = SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, night, x, y, pinnedCatalogIndices: null)
            .ShouldNotBeNull();
        hover.Index.ShouldBe(index);

        var search = new SkyMapSearchState();
        var ppr = SkyMapProjection.PixelsPerRadian(Size, state.FieldOfViewDeg);
        SkyMapSearchActions.SelectObjectByClick(search, state, db, Latitude, Longitude, night,
            SiteContext.Create(Latitude, Longitude, night), x, y, state.ComputeViewMatrix(), ppr, Size * 0.5f, Size * 0.5f)
            .ShouldBeTrue();

        var panel = search.InfoPanel.ShouldNotBeNull();
        panel.Index.ShouldBe(index);
        panel.Name.ShouldBe("Perseids");
        panel.Detail.ShouldNotBeNull().ShouldContain("ZHR 100");
        panel.Detail.ShouldContain("an hour here");
        panel.DetailNote.ShouldNotBeNull().ShouldStartWith("peak ");
        panel.DetailNote.ShouldEndWith("site time");
        ObjectInfoPanel.DiskLine(in panel).ShouldBe(panel.Detail);
    }

    [Fact(Timeout = 120_000)]
    public async Task TheObjectLayerSwitchesARadiantOffForTheDrawAndTheClickAlikeAndAPinBypassesIt()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var night = PeakNight;
        var state = new SkyMapState();
        LookAtPerseids(state, night);
        state.ShowObjectOverlay = false;
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var radiant = MeteorShowerNights.RadiantAt(Perseids, night);
        var (x, y) = Project(state, radiant.RaHours, radiant.DecDeg);
        var index = MeteorShowerIdentity.IndexOf(Perseids);

        state.ShouldDrawShowerRadiant(isPinned: false).ShouldBeFalse();
        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, night, x, y, pinnedCatalogIndices: null)
            ?.Index.ShouldNotBe(index);

        state.ShouldDrawShowerRadiant(isPinned: true).ShouldBeTrue();
        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, night, x, y, new HashSet<CatalogIndex> { index })
            .ShouldNotBeNull().Index.ShouldBe(index);

        state.ShowObjectOverlay = true;
        state.ShowOnlyObjectsWithPicture = true;
        state.ShouldDrawShowerRadiant(isPinned: false).ShouldBeFalse("a shower has no picture");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(UiThemeState.Dark, 1.0f)]
    [InlineData(UiThemeState.Dark, 1.5f)]
    [InlineData(UiThemeState.Night, 1.0f)]
    [InlineData(UiThemeState.Night, 1.5f)]
    public async Task ThePerseidsRadiantIsDrawnOnTheirPeakNightInEveryThemeAtEveryDpi(UiThemeState theme, float dpi)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var night = PeakNight;
        try
        {
            GuiTheme.Apply(theme, desktopIsDark: true);
            var (withMark, label) = Capture(db, night, dpi, overlay: true);
            var (without, noLabel) = Capture(db, night, dpi, overlay: false);

            // The box of the ring and its rays around the radiant (at the view's centre): the object overlay beside it draws
            // other things, so only what the radiant itself paints is judged.
            var reach = (int)MathF.Ceiling(14f * dpi);
            var changed = 0;
            var blueDominant = 0;
            for (var y = (Size / 2) - reach; y <= (Size / 2) + reach; y++)
            {
                for (var x = (Size / 2) - reach; x <= (Size / 2) + reach; x++)
                {
                    var i = ((y * Size) + x) * 4;
                    if (withMark[i] != without[i] || withMark[i + 1] != without[i + 1] || withMark[i + 2] != without[i + 2])
                    {
                        changed++;
                        blueDominant += AddsBlue(withMark, without, i) ? 1 : 0;
                    }
                }
            }

            changed.ShouldBeGreaterThan(40, "the ring and its rays are painted");
            if (theme == UiThemeState.Night)
            {
                blueDominant.ShouldBe(0, "nothing blue in Night");
            }

            // The name: the tab registers the shower's label to the right of the radiant and paints text ink inside it, which
            // the capture without the overlay does not have.
            noLabel.ShouldBeNull("the object layers switch the name off with the mark");
            var box = label.ShouldNotBeNull("the shower's name is drawn and clickable");
            box.X.ShouldBeGreaterThan(Size / 2f, "the name sits to the right of the radiant");
            box.Y.ShouldBeLessThan(Size / 2f);
            (box.Y + box.Height).ShouldBeGreaterThan(Size / 2f, "the name is level with the radiant");
            // The box is wider than the name, and its far end can hold another object's ink (every deep-sky marker comes and
            // goes with the overlay too), so the name is the run of inked columns from the box's left edge up to the first gap
            // wider than any between two letters.
            var top = (int)box.Y;
            var bottom = Math.Min(Size, (int)(box.Y + box.Height));
            var maxGap = (int)MathF.Ceiling(12f * dpi);
            var nameEnd = (int)box.X;
            for (int x = (int)box.X, gap = 0; x < Math.Min(Size, (int)(box.X + box.Width)) && gap <= maxGap; x++)
            {
                var inked = false;
                for (var y = top; y < bottom && !inked; y++)
                {
                    var i = ((y * Size) + x) * 4;
                    inked = withMark[i] != without[i] || withMark[i + 1] != without[i + 1] || withMark[i + 2] != without[i + 2];
                }

                gap = inked ? 0 : gap + 1;
                nameEnd = inked ? x + 1 : nameEnd;
            }

            var nameInk = 0;
            var nameBlue = 0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = (int)box.X; x < nameEnd; x++)
                {
                    var i = ((y * Size) + x) * 4;
                    if (withMark[i] != without[i] || withMark[i + 1] != without[i + 1] || withMark[i + 2] != without[i + 2])
                    {
                        nameInk++;
                        nameBlue += AddsBlue(withMark, without, i) ? 1 : 0;
                    }
                }
            }

            nameInk.ShouldBeGreaterThan((int)(30 * dpi), "the name's glyphs are painted in its box");
            if (theme == UiThemeState.Night)
            {
                nameBlue.ShouldBe(0, "the name is not blue in Night");
            }

            Save($"atlas-perseids-{theme}-dpi{dpi:0.0}", withMark);
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }

    /// <summary>The capture's pixels and the box of the shower's registered name label, null when none was registered.</summary>
    /// <summary>
    /// Whether the ink painted at pixel <paramref name="i"/> added more blue than red: the CHANGE, not the pixel, since an
    /// anti-aliased edge of red ink blended over a bluish sky pixel can still read bluer than red with no blue added.
    /// </summary>
    private static bool AddsBlue(byte[] with, byte[] without, int i)
        => with[i + 2] - without[i + 2] > Math.Max(0, with[i] - without[i]);

    private static (byte[] Pixels, RectF32? Label) Capture(ICelestialObjectDB db, DateTimeOffset night, float dpi, bool overlay)
    {
        using var renderer = new RgbaImageRenderer(Size, Size);
        var tab = new RadiantTestSkyMapTab(renderer) { DpiScale = dpi, FontPath = FontResolver.ResolveSystemFont() };
        var planner = PlannerAt(db, night);
        LookAtPerseids(tab.State, night, fov: 20.0);
        tab.State.ShowObjectOverlay = overlay;
        // The first pass would re-home the view to the pole; the capture looks at the radiant.
        tab.State.ViewDrivenExternally = true;
        tab.Render(planner, new RectF32(0, 0, Size, Size), new FakeTimeProviderWrapper(night));
        RectF32? label = null;
        foreach (var region in tab.GetRegisteredRegions())
        {
            if (region.Result is HitResult.ButtonHit { Action: "SkyMapShowerLabel:Perseids" })
            {
                label.ShouldBeNull("one label per shower");
                label = new RectF32(region.X, region.Y, region.Width, region.Height);
            }
        }

        return ([.. renderer.Surface.Pixels], label);
    }

    /// <summary>Written to the test's own output folder, deleted when the test ends unless <c>TIANWEN_KEEP_TEST_OUTPUT=1</c> keeps it for a person to look at.</summary>
    private static void Save(string name, byte[] pixels)
    {
        var dir = SharedTestData.CreateTempTestOutputDir("MeteorShowerAtlas");
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(pixels, Size, Size));
    }
}
