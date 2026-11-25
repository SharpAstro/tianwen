using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <b>A frame that changes nothing a widget shows builds none of that widget's tree, and a change it does show builds
/// it once.</b> The window's chrome and the atlas's panels built, measured and arranged their trees for every frame:
/// about 300 KB of each one, a collection every twentieth frame, and each collection a pause inside a paint (traced
/// 2026-11-25). They keep the arranged tree now (<see cref="KeptLayout{TKey}"/>) until an input changes.
/// </summary>
/// <remarks>
/// Counts, not pixels: a frame painted from a kept tree is the identical picture. Each test also changes what the
/// widget shows and expects ONE build, which is what a value left out of a widget's key fails.
/// </remarks>
[Collection("Astrometry")]
public sealed class KeptLayoutTests
{
    private const uint Width = 1600, Height = 1000;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 17, 23, 0, 0, TimeSpan.FromHours(2));

    private sealed class MapLessSkyMapTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site,
            SkyMapDrawPhase phase = SkyMapDrawPhase.All)
            => State.CurrentViewMatrix = State.ComputeViewMatrix();
    }

    private sealed record Rig(
        RgbaImageRenderer Renderer, GuiWindowChrome<RgbaImage> Chrome, MapLessSkyMapTab Tab, GuiAppState App,
        PlannerState Planner, LiveSessionState Live, FakeTimeProviderWrapper Time) : IDisposable
    {
        public readonly EquipmentTabState Equipment = new EquipmentTabState();
        public readonly SessionTabState Session = new SessionTabState();
        public readonly RectF32 Content = new RectF32(96f, 48f, Width - 96f, Height - 90f);

        public void Frame()
        {
            Tab.Render(Planner, Content, Time);
            Chrome.Render(App, Planner, Time, Live, Live, Equipment, Session, Tab.State, Width, Height);
        }

        public void Dispose() => Renderer.Dispose();
    }

    private static async Task<Rig> NewRigAsync()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var font = FontResolver.ResolveSystemFont();
        var renderer = new RgbaImageRenderer(Width, Height);
        var chrome = new GuiWindowChrome<RgbaImage>(renderer) { FontPath = font, DpiScale = 1.5f };
        chrome.Sidebar.FontPath = font;
        chrome.Sidebar.DpiScale = 1.5f;
        chrome.NavigationStrip.FontPath = font;
        chrome.NavigationStrip.DpiScale = 1.5f;
        var app = new GuiAppState
        {
            ActiveProfile = new Profile(Guid.NewGuid(), "Test", ProfileData.Empty),
            StatusMessage = "Found 90 devices",
            ActiveTab = GuiTab.SkyMap,
        };
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = -37.8, SiteLongitude = 145.0, SiteTimeZone = TimeSpan.FromHours(11), PlanningDate = Now,
        };
        var tab = new MapLessSkyMapTab(renderer) { FontPath = font, DpiScale = 1.5f };
        var rig = new Rig(renderer, chrome, tab, app, planner, new LiveSessionState(), new FakeTimeProviderWrapper(Now));
        rig.Frame();
        tab.State.Mode = SkyMapMode.Equatorial;
        tab.State.FieldOfViewDeg = 60.0;
        // Twice more: the palette reconciles its offset with where the engine put it, once.
        rig.Frame();
        rig.Frame();
        return rig;
    }

    [Fact]
    public async Task FramesThatChangeNothingBuildNoTree()
    {
        using var rig = await NewRigAsync();
        var builds = (rig.Chrome.StatusBarBuilds, rig.Chrome.Sidebar.StripBuilds, rig.Chrome.NavigationStrip.Builds,
            rig.Tab.LayerPaletteBuilds, rig.Tab.InfoStripBuilds);

        // Twenty frames of a pan, inside one second and one minute: the view moves under panels that show none of it.
        for (var frame = 1; frame <= 20; frame++)
        {
            rig.Tab.State.CenterRA = 5.5 + (frame * 0.01);
            rig.Frame();
        }

        (rig.Chrome.StatusBarBuilds, rig.Chrome.Sidebar.StripBuilds, rig.Chrome.NavigationStrip.Builds,
            rig.Tab.LayerPaletteBuilds, rig.Tab.InfoStripBuilds).ShouldBe(builds);
    }

    [Fact]
    public async Task TheStatusBarIsBuiltWhenWhatItShowsChanges()
    {
        using var rig = await NewRigAsync();
        var builds = rig.Chrome.StatusBarBuilds;

        // The clock is drawn into its box, not built into the tree: its second moves the time and builds nothing.
        var clock = rig.Chrome.ClockText;
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Frame();
        rig.Chrome.ClockText.ShouldNotBe(clock, "the clock shows the new second");
        rig.Chrome.StatusBarBuilds.ShouldBe(builds, "and the bar around it is the same tree");

        rig.App.StatusMessage = "Mount connected";
        rig.Frame();
        rig.Chrome.StatusBarBuilds.ShouldBe(builds + 1, "the status message");

        rig.Planner.PlanningDate = Now.AddDays(1);
        rig.Frame();
        rig.Chrome.StatusBarBuilds.ShouldBe(builds + 2, "the planned night");

        rig.Chrome.ClockRect.ShouldNotBeNull("a frame painted from the kept bar still says where its clock is");
    }

    [Fact]
    public async Task TheRailIsBuiltWhenTheActiveTabChanges()
    {
        using var rig = await NewRigAsync();
        var builds = rig.Chrome.Sidebar.StripBuilds;

        rig.App.ActiveTab = GuiTab.Planner;
        rig.Frame();
        rig.Chrome.Sidebar.StripBuilds.ShouldBe(builds + 1);
        rig.Frame();
        rig.Chrome.Sidebar.StripBuilds.ShouldBe(builds + 1);

        // The pointer over a cell is that cell's hover plate, which is part of the tree.
        rig.App.MouseScreenPosition = (20f, 200f);
        rig.Frame();
        rig.Chrome.Sidebar.StripBuilds.ShouldBe(builds + 2);
    }

    [Fact]
    public async Task ThePaletteIsBuiltWhenALayerIsSwitchedAndItsRowStillSwitchesIt()
    {
        using var rig = await NewRigAsync();
        var builds = rig.Tab.LayerPaletteBuilds;

        rig.Tab.State.ShowGrid = !rig.Tab.State.ShowGrid;
        rig.Frame();
        rig.Tab.LayerPaletteBuilds.ShouldBe(builds + 1, "a layer switched by its key");

        // A row's press runs frames after its tree was made: it switches the layer as it stands then.
        for (var i = 0; i < 5; i++)
        {
            rig.Frame();
        }
        var gridRow = SkyMapLayerPalette.RowAction(SkyMapLayers.All[0]);
        var before = rig.Tab.State.ShowGrid;
        ClickRegion(rig.Tab, gridRow);
        rig.Tab.State.ShowGrid.ShouldBe(!before);
        ClickRegion(rig.Tab, gridRow);
        rig.Tab.State.ShowGrid.ShouldBe(before, "the kept row's press reads the state when it runs, so a second press switches it back");
    }

    [Fact]
    public async Task TheInfoStripIsBuiltWhenTheModeOrTheTimeChanges()
    {
        using var rig = await NewRigAsync();
        var builds = rig.Tab.InfoStripBuilds;

        rig.Tab.State.Mode = SkyMapMode.Horizon;
        rig.Frame();
        rig.Tab.InfoStripBuilds.ShouldBe(builds + 1, "the mode its switch shows");

        rig.Tab.State.TimeOffset = TimeSpan.FromHours(1);
        rig.Frame();
        rig.Tab.InfoStripBuilds.ShouldBe(builds + 2, "a scrubbed time: another text, and Now beside it");

        rig.Frame();
        rig.Tab.InfoStripBuilds.ShouldBe(builds + 2);
    }

    [Fact]
    public async Task AnObjectsPanelIsBuiltWhenWhatItPrintsChangesAndNotForEveryFrame()
    {
        using var rig = await NewRigAsync();
        var db = rig.Planner.ObjectDb.ShouldNotBeNull();
        db.TryLookupByIndex("M42", out var orion).ShouldBeTrue();
        var site = SiteContext.Create(rig.Planner.SiteLatitude, rig.Planner.SiteLongitude, Now);
        rig.Tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromCatalogObject(
            orion, rig.Planner.SiteLatitude, rig.Planner.SiteLongitude, Now, site, null, db);
        rig.Frame();
        rig.Frame();
        var builds = rig.Tab.ObjectPanelBuilds;
        builds.ShouldBeGreaterThan(0, "the panel is on show");

        // A pan under an open panel: its rows say the same, so it is the same tree.
        for (var frame = 1; frame <= 10; frame++)
        {
            rig.Tab.State.CenterRA = 5.5 + (frame * 0.01);
            rig.Frame();
        }
        rig.Tab.ObjectPanelBuilds.ShouldBe(builds, "a pan changes nothing the panel prints");

        // A minute later the object has moved a quarter of a degree across the sky: the altitude it prints is another,
        // so the panel is built again, once. (The rig's planning date pins the instant the atlas shows; the scrub moves it.)
        rig.Tab.State.TimeOffset = TimeSpan.FromMinutes(1);
        rig.Frame();
        rig.Tab.ObjectPanelBuilds.ShouldBe(builds + 1, "the altitude and azimuth it prints moved");
        rig.Frame();
        rig.Tab.ObjectPanelBuilds.ShouldBe(builds + 1);
    }

    [Theory]
    [InlineData(41.25, 41.24)]
    [InlineData(41.25, 41.26)]
    [InlineData(-0.05, -0.04)]
    [InlineData(-0.05, -0.06)]
    [InlineData(12.349, 12.351)]
    public void APanelsKeyChangesWheneverItsPrintedAltitudeDoes(double before, double after)
    {
        // Whatever the printed form rounds to, two altitudes that print differently have different keys.
        var a = Info(before);
        var b = Info(after);
        if ($"{before:+0.0;-0.0}" != $"{after:+0.0;-0.0}")
        {
            SkyMapTab<RgbaImage>.ShownOnPanel(in a).ShouldNotBe(SkyMapTab<RgbaImage>.ShownOnPanel(in b));
        }

        static SkyMapInfoPanelData Info(double alt) => new SkyMapInfoPanelData(
            "M42", "M42", default, default, 5.5, -5.4, 4f, float.NaN, alt, 120.0, null, null, null, false, false, null, null, null);
    }

    private static void ClickRegion(MapLessSkyMapTab tab, string action)
    {
        foreach (var region in tab.GetRegisteredRegions())
        {
            if (region.Result is HitResult.ButtonHit hit && hit.Action == action)
            {
                region.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
                return;
            }
        }

        Assert.Fail($"no region for {action}");
    }
}
