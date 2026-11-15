using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Every tab drawn at one size after another on DIR.Lib's <see cref="WireframeRenderer"/>, and each frame judged
/// by <see cref="Wireframe.Check"/>: no text over text, no text half on a control, no control over a control,
/// nothing off the surface, no text cut without an ellipsis. A row packed from both ends draws over itself once
/// the window is narrower than the two together (the atlas's strip did from about 1100 px, #2133), and that is
/// only seen at the width where it happens, so the widths are swept here (#2139).
/// </summary>
/// <remarks>
/// Set <c>TIANWEN_WIREFRAME_PICTURES</c> to a folder to have every frame painted and saved there as a PNG.
/// The window's top bar and sidebar are swept too, as <see cref="GuiWindowChrome{TSurface}"/> over the whole window
/// (the content widths plus the sidebar) in the states that stress them (#2139). What the sweep cannot see: anything a
/// GPU pipeline draws, and any state the scenarios below do not put on screen.
/// The Notifications tab and the FITS viewer's chrome (toolbar, info strip and panel, file list, status bar, and the Tone,
/// Enhance and White Balance popovers open in turn) are swept too (#2139), the viewer as the no-op <c>SweepViewer</c> on
/// the wireframe renderer. The image quad and the histogram quad are GPU-drawn and unseen here, as are the planetary
/// panels, the help panel and the context menu.
/// </remarks>
[Collection("UI")]
public class WireframeSweepTests
{
    // The content area a tab is given: the window less the sidebar and the bars, from a narrow half-screen window
    // to a wide one.
    private static readonly uint[] Widths = [560, 640, 720, 800, 900, 1000, 1100, 1280, 1440, 1700, 2000, 2400];
    private static readonly uint[] Heights = [520, 900];
    private static readonly float[] Scales = [1f, 1.5f];

    private const string PicturesEnvVar = "TIANWEN_WIREFRAME_PICTURES";

    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Tabs => [.. Scenarios.Keys];

    private delegate Task Draw(WireframeRenderer renderer, RectF32 content, float scale);

    private static readonly Dictionary<string, Draw> Scenarios = new Dictionary<string, Draw>(StringComparer.Ordinal)
    {
        ["guider"] = (renderer, content, scale) =>
        {
            var tab = new GuiderTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            tab.Render(RunningSession(), content, new FakeTimeProviderWrapper(Now));
            return Task.CompletedTask;
        },
        ["live session"] = (renderer, content, scale) =>
        {
            var tab = new LiveSessionTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            tab.Render(RunningSession(), content, new FakeTimeProviderWrapper(Now));
            return Task.CompletedTask;
        },
        ["planner"] = (renderer, content, scale) =>
        {
            var tab = new PlannerTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            tab.Render(PlannerTabLayoutTests.BuildState(), content,
                new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero)));
            return Task.CompletedTask;
        },
        ["sky map"] = SkyMap((_, _, _) => { }),
        // The F3 search window open over the map with a query that has many rows (the palette gives way to it).
        ["sky map, search open"] = SkyMap((tab, planner, db) =>
        {
            var search = tab.State.Search;
            search.Interaction = new SkyMapSearchInteraction(search, db, commit: () => { }, close: () => { }, requestRedraw: () => { });
            SkyMapSearchActions.OpenSearch(search, db, new TextInputFocus());
            search.SearchInput.OnTextChanged?.Invoke("NGC 7");
        }),
        // The layer palette with its key hints, over a map with the search closed.
        ["sky map, layer palette"] = SkyMap((tab, _, _) => tab.State.ShowLayerPalette = true),
        // A selected atlas object: the info panel for the catalogue object with the longest common name of a few known ones.
        ["sky map, selected object"] = SkyMap((tab, planner, db) =>
        {
            var obj = LongestNamed(db, "NGC 7000", "NGC 7293", "NGC 1499", "NGC 2237", "IC 1396", "IC 5070", "M42", "M8", "M20", "NGC 6992");
            var site = SiteContext.Create(planner.SiteLatitude, planner.SiteLongitude, Now);
            tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromCatalogObject(
                obj, planner.SiteLatitude, planner.SiteLongitude, Now, site, null, db);
        }),
        // The planner's target search with its suggestion dropdown open.
        ["planner, suggestions open"] = (renderer, content, scale) =>
        {
            var tab = new PlannerTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            var state = PlannerTabLayoutTests.BuildState();
            new TextInputFocus().Focus(state.SearchInput, "NGC");
            state.Search = new PlannerSearchInteraction(
                state, db: null!, createTransform: () => null,
                autoComplete: () =>
                [
                    "NGC 7000 North America Nebula", "NGC 7293 Helix Nebula", "NGC 6992 Eastern Veil Nebula, Cygnus Loop",
                    "NGC 2237 Rosette Nebula and the open cluster NGC 2244", "NGC 1499 California Nebula", "NGC 281 Pacman Nebula",
                    "NGC 6888 Crescent Nebula", "NGC 7380 Wizard Nebula",
                ],
                ensureVisible: null, deactivate: () => { }, requestRedraw: () => { });
            state.SearchInput.OnTextChanged!("NGC");
            tab.Render(state, content, new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero)));
            return Task.CompletedTask;
        },
        // A rig of devices whose names are what a vendor and a driver give them: a model, a serial number and a port.
        ["equipment, long device names"] = (renderer, content, scale) =>
        {
            var tab = new EquipmentTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            tab.Render(LongNamedRig(), content);
            return Task.CompletedTask;
        },
        ["live session, long device names"] = (renderer, content, scale) =>
        {
            var tab = new LiveSessionTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            var state = new LiveSessionState
            {
                Mode = LiveSessionMode.Preview,
                MountDisplayName = LongMount,
                PreviewOTATelemetry =
                [
                    PreviewOTATelemetry.Unknown with
                    {
                        OtaName = "Main imaging train, Takahashi FSQ-106EDX4 on the east pier",
                        CameraDisplayName = LongCamera,
                        CameraConnected = true,
                        FocuserConnected = true,
                        FilterWheelConnected = true,
                        CcdTempC = -10.1,
                        SetpointC = -10,
                        CoolerPowerPct = 62,
                        CoolerOn = true,
                        FocusPosition = 12345,
                        FilterName = "Hydrogen alpha 3 nm narrowband (Astrodon)",
                    },
                ],
            };
            tab.Render(state, content, new FakeTimeProviderWrapper(Now));
            return Task.CompletedTask;
        },
        ["home"] = (renderer, content, scale) =>
        {
            var tab = new HomeTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            ImmutableArray<RigCard> cards =
            [
                HomeTabLayoutTests.Card("This computer", running: true, hfd: 2.41),
                HomeTabLayoutTests.Card("Observatory on the hill, east pier", running: true),
                HomeTabLayoutTests.Card("Backyard"),
            ];
            tab.Render(new GuiAppState { HomeCards = cards }, content, Now);
            return Task.CompletedTask;
        },
        ["equipment"] = (renderer, content, scale) =>
        {
            var tab = new EquipmentTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            tab.Render(new GuiAppState(), content);
            return Task.CompletedTask;
        },
        ["session"] = (renderer, content, scale) =>
        {
            var tab = new SessionTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            var planner = PlannerTabLayoutTests.BuildState();
            planner.Proposals =
            [
                new ProposedObservation(new Target(18.06, -24.38, "M8, the Lagoon Nebula in Sagittarius", null)),
                new ProposedObservation(new Target(5.58, -5.39, "M42", null), SubExposure: TimeSpan.FromSeconds(120)),
            ];
            tab.Render(new GuiAppState(), planner, content, new FakeTimeProviderWrapper(Now));
            return Task.CompletedTask;
        },
    };

    private const string LongMount = "Sky-Watcher EQ6-R Pro (serial EQ6R-20230417-00981) via EQMOD on COM12";
    private const string LongCamera = "ZWO ASI2600MM Pro (serial 0123456789ABCDEF0123456789ABCDEF) on USB 3.0 hub port 4";

    private static GuiAppState LongNamedRig()
    {
        var mount = new Uri($"Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3#{LongMount}");
        var camera = new Uri($"Camera://FakeDevice/FakeCamera1#{LongCamera}");
        var focuser = new Uri("Focuser://FakeDevice/FakeFocuser1#Pegasus Astro Focus Cube 3 (serial FC3-2024-000417) on COM14");
        var wheel = new Uri("FilterWheel://FakeDevice/cfw?filter1=Luminance&filter2=Red#ZWO EFW 8x1.25 inch (serial EFW8-0042) on COM15");
        return new GuiAppState
        {
            LocalProfileKnown = true,
            ActiveProfile = new Profile(Guid.NewGuid(), "Observatory east pier, Takahashi FSQ-106EDX4 and the guide scope",
                new ProfileData(Mount: mount, Guider: NoneDevice.Instance.DeviceUri,
                    OTAs: [new OTAData("Main imaging train, Takahashi FSQ-106EDX4 on the east pier", 530, Camera: camera, Cover: null,
                        Focuser: focuser, FilterWheel: wheel, PreferOutwardFocus: null, OutwardIsPositive: null)])),
        };
    }

    // The catalogue object, of those named, whose common names are longest: a panel title that has to wrap or cut.
    private static CelestialObject LongestNamed(ICelestialObjectDB db, params string[] indices)
    {
        CelestialObject? best = null;
        var bestLength = -1;
        foreach (var name in indices)
        {
            if (db.TryLookupByIndex(name, out var obj) && obj.DisplayName.Length > bestLength)
            {
                best = obj;
                bestLength = obj.DisplayName.Length;
            }
        }

        return best ?? throw new InvalidOperationException("none of the named objects is in the catalogue");
    }

    // The sky map over a catalogue, with a state put on it by the scenario. The tab is kept for the options: the map's own labels
    // are judged through what it says it placed.
    private static SkyMapTab<RgbaImage>? _sky;

    private static Draw SkyMap(Action<SkyMapTab<RgbaImage>, PlannerState, ICelestialObjectDB> setup) => async (renderer, content, scale) =>
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var tab = new SkyMapTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
        var planner = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = 45.0,
            SiteLongitude = -75.0,
            SiteTimeZone = TimeSpan.Zero,
            PlanningDate = Now,
        };
        setup(tab, planner, db);
        tab.Render(planner, content, new FakeTimeProviderWrapper(Now));
        _sky = tab;
    };

    // What a tab is opened for, which must be on screen whole at every size. A row left undrawn overlaps nothing, so
    // the overlap rules cannot see it go: the Session tab once stacked its form over the observations at a fixed
    // share of a short tab and showed their header alone.
    private static readonly Dictionary<string, string[]> MustShow = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["session"] = ["M8", "M42", "Start Session"],
    };

    private static IEnumerable<string> NotShownWhole(WireframeFrame frame, string[] shown)
        => shown
            .Where(part => !frame.Texts.Any(t => t.Text.Contains(part, StringComparison.Ordinal)
                && t.Visible.Width > 0f && t.Visible.Height >= t.Ink.Height - 0.5f))
            .Select(part => $"no text holding \"{part}\" is shown whole");

    private static LiveSessionState RunningSession() => new LiveSessionState
    {
        IsRunning = true,
        Phase = SessionPhase.Observing,
        LastGuideStats = GuideStats.FromRms(0.62, 0.41, 0.46, 1.2, 1.4),
    };

    // What the map draws lies where the sky puts it: a constellation's name runs off the edge, a grid label sits
    // under the layer palette, and none of that is a layout. The map is drawn first, so everything before the
    // first control that is not of the map is the map, and the rules are asked of the chrome after it.
    // Of the map's own text, one kind is judged: a catalogue label the overlay engine placed WITH collision avoidance
    // (OverlayEngine.PlaceLabels), whose promise is that no two overlap and none lies under what is drawn after it. The
    // map's own placer is the best-effort one (PlaceLabelsBestEffort: Stellarium-style, overlaps accepted by design, and a
    // label under a panel is covered, not cut), so those stay out, as do the planet, comet and constellation names, the grid
    // numbers and everything a GPU pipeline draws, which this renderer does not see. SkyMapTab.PlacedMapLabels says which
    // placer put each label where.
    private static WireframeOptions SkyChromeOnly(WireframeFrame frame)
    {
        static bool OfTheMap(WireframeRegion r)
            => r.Result is HitResult.ButtonHit { Action: var action }
                && (action.StartsWith("SkyMapPlanetLabel", StringComparison.Ordinal)
                    || action.StartsWith("SkyMapObjectLabel", StringComparison.Ordinal));

        var collisionPlaced = _sky?.PlacedMapLabels
            .Where(l => l.Placement == SkyMapTab<RgbaImage>.LabelPlacement.Collision)
            .Select(l => l.Box).ToArray() ?? [];
        bool InCollisionLabel(WireframeText t)
        {
            var cx = t.Ink.X + t.Ink.Width / 2f;
            var cy = t.Ink.Y + t.Ink.Height / 2f;
            return collisionPlaced.Any(b => cx >= b.X && cx <= b.X + b.Width && cy >= b.Y && cy <= b.Y + b.Height);
        }

        var chromeFrom = frame.Regions.Where(r => !OfTheMap(r)).Select(r => r.Order).DefaultIfEmpty(int.MaxValue).Min();
        return new WireframeOptions { IgnoreText = t => t.Order < chromeFrom && !InCollisionLabel(t), IgnoreRegion = OfTheMap };
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Tabs))]
    public async Task NothingOverlapsAtAnyWindowSize(string tab)
    {
        var report = await Sweep(tab, (width, height, _) => (width, height), Scenarios[tab],
            frame => tab.StartsWith("sky map", StringComparison.Ordinal) ? SkyChromeOnly(frame) : null,
            MustShow.TryGetValue(tab, out var shown) ? shown : null);
        report.ShouldBeEmpty();
    }

    // One sweep of a scenario over every scale, height and width. The surface is the size the scenario asks for from the
    // swept content size (a tab's is the content; the window chrome's is the content plus the sidebar and the top bar).
    private static async Task<string> Sweep(string name,
        Func<uint, uint, float, (uint Width, uint Height)> surface, Draw draw,
        Func<WireframeFrame, WireframeOptions?> options, string[]? shown)
    {
        var ct = TestContext.Current.CancellationToken;
        // Set to a folder, every frame is also painted and saved there, to look at what a finding names.
        var pictures = Environment.GetEnvironmentVariable(PicturesEnvVar) is { Length: > 0 } folder
            ? Directory.CreateDirectory(folder).FullName
            : null;
        var report = new StringBuilder();

        foreach (var scale in Scales)
        {
            foreach (var height in Heights)
            {
                foreach (var width in Widths)
                {
                    var (surfaceW, surfaceH) = surface(width, height, scale);
                    using var renderer = new WireframeRenderer(surfaceW, surfaceH, paint: pictures is not null);
                    renderer.Begin();
                    await draw(renderer, new RectF32(0, 0, width, height), scale);
                    if (pictures is not null)
                    {
                        var file = $"{name.Replace(' ', '-').Replace(',', '_')}_{width}x{height}_{scale:0.0}.png";
                        await File.WriteAllBytesAsync(Path.Combine(pictures, file),
                            PngWriter.Encode(renderer.Surface.Pixels, (int)surfaceW, (int)surfaceH), ct);
                    }

                    var frame = renderer.Frame;
                    (frame.Texts.Length + frame.Regions.Length).ShouldBeGreaterThan(0, $"{name} drew nothing at {width}x{height}");
                    var findings = Wireframe.Check(frame, options(frame))
                        .Select(f => f.Message)
                        .Concat(shown is not null ? NotShownWhole(frame, shown) : [])
                        .ToArray();
                    if (findings.Length > 0)
                    {
                        report.Append($"{name} at {width}x{height}, scale {scale:0.0}:\n  ")
                            .Append(string.Join("\n  ", findings)).Append('\n');
                    }
                }
            }
        }

        return report.ToString();
    }

    // The window chrome's states that stress it.
    public static TheoryData<string> ChromeStates =>
        ["no status message", "long status message", "connect all", "session running", "pinned date with verdict",
         "everything at once", "night calendar open"];

    private const string LongStatusMessage =
        "Could not open the mount on COM12: Access to the path 'C:\\Users\\observer\\AppData\\Local\\TianWen\\Profiles\\Observatory east pier\\mount.json' is denied";

    private static Draw ChromeScenario(string state) => (renderer, content, scale) =>
    {
        var font = FontResolver.ResolveSystemFont();
        var chrome = new GuiWindowChrome<RgbaImage>(renderer) { FontPath = font, DpiScale = scale };
        chrome.Sidebar.FontPath = font;
        chrome.Sidebar.DpiScale = scale;
        chrome.NavigationStrip.FontPath = font;
        chrome.NavigationStrip.DpiScale = scale;

        var everything = state is "everything at once" or "night calendar open";
        var connectAll = state is "connect all" || everything;
        var appState = new GuiAppState
        {
            ActiveProfile = new Profile(Guid.NewGuid(), "Observatory",
                connectAll
                    ? ProfileData.Empty with { Mount = new Uri("Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3#Fake Mount") }
                    : ProfileData.Empty),
        };
        if (state is "long status message" || everything)
        {
            appState.StatusMessage = LongStatusMessage;
        }
        else if (state is not "no status message")
        {
            appState.StatusMessage = "Found 3 devices";
        }

        var planner = new PlannerState();
        if (state is "pinned date with verdict" || everything)
        {
            var pinned = new DateTimeOffset(2026, 6, 23, 22, 0, 0, TimeSpan.Zero);
            planner.PlanningDate = pinned;
            planner.AstroDark = pinned.AddHours(1);
            planner.AstroTwilight = pinned.AddHours(5);
            var night = new NightSummary(new DateOnly(2026, 6, 23), planner.AstroDark, planner.AstroTwilight,
                EventType.AmateurAstronomicalTwilight, 0.1, true, TimeSpan.FromHours(4),
                new NightForecast(TimeSpan.FromHours(4), TimeSpan.FromHours(3.5), TimeSpan.FromHours(3.5), 10, 0, SeeingClass.Good));
            planner.Calendar.Publish(NightCalendarData.Empty with
            {
                Nights = NightCalendarData.Empty.Nights.SetItem(new DateOnly(2026, 6, 23), night),
            });
        }

        var local = new LiveSessionState { IsRunning = state is "session running" };
        chrome.Render(appState, planner, new FakeTimeProviderWrapper(Now), local, local,
            new EquipmentTabState(), new SessionTabState(), new SkyMapState(),
            content.Width + chrome.SidebarWidth, content.Height + chrome.StatusBarHeight);

        if (state == "night calendar open")
        {
            // The calendar hangs from the date label the bar just painted, over the window, as a host paints it.
            planner.Calendar.Popover.Open();
            var calendar = new NightCalendarPopover<RgbaImage>(renderer) { FontPath = font, DpiScale = scale };
            var anchor = chrome.DateCalendarAnchor ?? throw new InvalidOperationException("the bar drew no date label");
            calendar.Render(planner, anchor,
                new RectF32(0, 0, content.Width + chrome.SidebarWidth, content.Height + chrome.StatusBarHeight),
                new FakeTimeProviderWrapper(Now));
        }

        return Task.CompletedTask;
    };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ChromeStates))]
    public async Task TheWindowChromeFitsAtAnyWindowSize(string state)
    {
        var report = await Sweep($"window chrome, {state}",
            (width, height, scale) =>
            {
                // The window is the content area plus the sidebar and the top bar, so the swept widths keep their meaning.
                var side = (uint)MathF.Ceiling(52f * scale);
                var bar = (uint)MathF.Ceiling(28f * scale);
                return (width + side, height + bar);
            },
            ChromeScenario(state), _ => null, null);
        report.ShouldBeEmpty();
    }

    // The Notifications tab: empty, forty entries, and one of each severity with a message far longer than any window.
    public static TheoryData<string> NotificationStates => ["empty", "forty entries", "very long, every severity"];

    private static Draw NotificationScenario(string state) => (renderer, content, scale) =>
    {
        var app = new GuiAppState();
        var longText = string.Join(' ', Enumerable.Repeat(
            "Mount://ASCOMDevice/ASCOM.OnStep.Telescope refused the slew to Sharpless 2-132 (the Lion Nebula) in Cepheus", 3));
        if (state == "forty entries")
        {
            for (var i = 0; i < 40; i++)
            {
                app.RecordNotification(Now.AddMinutes(i), (NotificationSeverity)(i % 3), $"Entry {i}: guiding RMS 0.6 arcsec");
            }
        }
        else if (state != "empty")
        {
            foreach (var severity in Enum.GetValues<NotificationSeverity>())
            {
                app.RecordNotification(Now, severity, $"{severity}: {longText}");
                app.RecordNotification(Now, severity, $"{severity}: {new string('W', 160)}");
            }
        }

        var tab = new NotificationsTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
        tab.Render(app, new ViewContexts().Local, content);
        return Task.CompletedTask;
    };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(NotificationStates))]
    public async Task TheNotificationsTabFitsAtAnyWindowSize(string state)
    {
        var report = await Sweep($"notifications, {state}", (width, height, _) => (width, height),
            NotificationScenario(state), _ => null, null);
        report.ShouldBeEmpty();
    }

    // The viewer's chrome with an image loaded, the file list (long names) and the info panel shown, then each popover
    // open in turn. The viewer chrome has its own scale (InterfaceScale 1), so only the DPI is swept.
    public static TheoryData<string> ViewerStates => ["chrome", "tone popover", "enhance popover", "white balance popover"];

    private static Draw ViewerScenario(string state) => async (renderer, content, scale) =>
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
        var viewer = new SweepViewer(renderer, scale)
        {
            Offer = ToolbarOffer.FileViewer.With(ToolbarAction.Enhance),
        };
        viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 8, 6);
        var viewerState = new ViewerState
        {
            ShowFileList = true,
            ShowHistogram = true,
            ShowInfoPanel = true,
            StretchMode = StretchMode.None,
            ZoomToFit = false,
            Zoom = 1f,
        };
        viewerState.ImageFileNames.AddRange(
        [
            "M31_Andromeda_Galaxy_LRGB_Light_300s_-10C_gain100_0001_with_a_very_long_session_suffix_for_the_east_pier.fits",
            "IC 1805.fit",
            "NGC 7000 North America Nebula Ha OIII SII mosaic panel 2 of 4 integration 12h40m.xisf",
        ]);
        viewerState.EnhanceChoices =
        [
            new(EnhanceBackend.Auto, "Auto", ["gradient-correction", "denoise-starless"]),
            new(EnhanceBackend.TianWen, "TianWen", ["gradient-correction", "denoise-frame"]),
        ];
        switch (state)
        {
            case "tone popover": viewerState.TonePopover.Open(); break;
            case "enhance popover": viewerState.EnhancePopover.Open(); break;
            case "white balance popover": viewerState.WhiteBalancePopover.Open(); break;
        }

        // Twice, as a window does: the first frame uploads the histogram the second paints.
        viewer.Render(document, viewerState);
        viewer.Render(document, viewerState);
    };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ViewerStates))]
    public async Task TheViewerChromeFitsAtAnyWindowSize(string state)
    {
        var report = await Sweep($"viewer, {state}", (width, height, _) => (width, height),
            ViewerScenario(state), _ => new WireframeOptions { IgnoreRegion = Overlaps }, null);
        report.ShouldBeEmpty();

        // Two regions that lie over one another by design, not by a layout's mistake: the file list's splitter handle is a
        // grab zone wider than the divider, so it reaches over the list and the image beside it; and a popover's
        // background is a click sink under the panel, drawn over the toolbar and the picture it floats on.
        static bool Overlaps(WireframeRegion r)
            => r.Result is ResizeHandleHit
                || r.Result is HitResult.ButtonHit { Action: var action }
                    && action.EndsWith("PanelBackground", StringComparison.Ordinal);
    }

    /// <summary>
    /// More observations than a short tab has room for scroll, every one reachable; the list used to build the rows
    /// that fit and leave the rest out without a word. Two in a tall tab fit, and nothing scrolls (DIR.Lib 11.62: a
    /// list given the room its fractional rows take used to read as one surface unit too long, and drew a bar).
    /// </summary>
    [Fact]
    public void TheObservationListScrollsOnlyWhenItHoldsMoreThanShows()
    {
        var planner = PlannerTabLayoutTests.BuildState();
        planner.Proposals = [.. Enumerable.Range(1, 12).Select(i => new ProposedObservation(new Target(i, 10, $"Target {i}", null)))];
        using var renderer = new WireframeRenderer(800, 520);
        var tab = new SessionTab<RgbaImage>(renderer) { DpiScale = 1.5f, FontPath = FontResolver.ResolveSystemFont() };
        var content = new RectF32(0, 0, 800, 520);
        renderer.Begin();
        tab.Render(new GuiAppState(), planner, content, new FakeTimeProviderWrapper(Now));

        tab.ObservationScroll.MaxOffset.ShouldBeGreaterThan(0f);

        // Scrolled to its end, the last target is shown whole, and the button stays below the list.
        tab.ObservationScroll.EnsureVisible(tab.ObservationScroll.TotalAtoms - 1);
        renderer.Begin();
        tab.Render(new GuiAppState(), planner, content, new FakeTimeProviderWrapper(Now));
        NotShownWhole(renderer.Frame, ["Target 12", "Start Session"]).ShouldBeEmpty();

        planner.Proposals = [.. planner.Proposals.Take(2)];
        using var tall = new WireframeRenderer(1440, 900);
        var fits = new SessionTab<RgbaImage>(tall) { DpiScale = 1.5f, FontPath = FontResolver.ResolveSystemFont() };
        fits.Render(new GuiAppState(), planner, new RectF32(0, 0, 1440, 900), new FakeTimeProviderWrapper(Now));
        fits.ObservationScroll.MaxOffset.ShouldBe(0f);
    }
}
