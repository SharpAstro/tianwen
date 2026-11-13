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
            var (withMark, text) = Capture(db, night, dpi, overlay: true);
            var (without, _) = Capture(db, night, dpi, overlay: false);

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
                        blueDominant += withMark[i + 2] > withMark[i] ? 1 : 0;
                    }
                }
            }

            changed.ShouldBeGreaterThan(40, "the ring and its rays are painted");
            if (theme == UiThemeState.Night)
            {
                blueDominant.ShouldBe(0, "nothing blue in Night");
            }

            text.ShouldBe("Perseids");
            Save($"atlas-perseids-{theme}-dpi{dpi:0.0}", withMark);
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }

    private static (byte[] Pixels, string Label) Capture(ICelestialObjectDB db, DateTimeOffset night, float dpi, bool overlay)
    {
        using var renderer = new RgbaImageRenderer(Size, Size);
        var tab = new RadiantTestSkyMapTab(renderer) { DpiScale = dpi, FontPath = FontResolver.ResolveSystemFont() };
        var planner = PlannerAt(db, night);
        LookAtPerseids(tab.State, night, fov: 20.0);
        tab.State.ShowObjectOverlay = overlay;
        tab.Render(planner, new RectF32(0, 0, Size, Size), new FakeTimeProviderWrapper(night));
        return ([.. renderer.Surface.Pixels], Perseids.Name);
    }

    /// <summary>Written under the ORIGINAL temp (<c>TestRunTemp.Root</c>), which outlives the run folder, for a person to look at.</summary>
    private static void Save(string name, byte[] pixels)
    {
        var dir = Directory.CreateDirectory(Path.Combine(TestRunTemp.Root, "MeteorShowerAtlas")).FullName;
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(pixels, Size, Size));
    }
}
