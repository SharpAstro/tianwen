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
using TianWen.Lib.Devices;
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
/// What the sweep cannot see: anything a GPU pipeline draws, the window's own top bar and sidebar (they live in
/// the Vulkan host), and any state the scenarios below do not put on screen.
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
        ["sky map"] = async (renderer, content, scale) =>
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
            tab.Render(planner, content, new FakeTimeProviderWrapper(Now));
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
    // first control that is not a planet's label is the map, and the rules are asked of the chrome after it.
    private static WireframeOptions SkyChromeOnly(WireframeFrame frame)
    {
        static bool OfTheMap(WireframeRegion r)
            => r.Result is HitResult.ButtonHit { Action: var action } && action.StartsWith("SkyMapPlanetLabel", StringComparison.Ordinal);

        var chromeFrom = frame.Regions.Where(r => !OfTheMap(r)).Select(r => r.Order).DefaultIfEmpty(int.MaxValue).Min();
        return new WireframeOptions { IgnoreText = t => t.Order < chromeFrom, IgnoreRegion = OfTheMap };
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Tabs))]
    public async Task NothingOverlapsAtAnyWindowSize(string tab)
    {
        var ct = TestContext.Current.CancellationToken;
        var draw = Scenarios[tab];
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
                    using var renderer = new WireframeRenderer(width, height, paint: pictures is not null);
                    renderer.Begin();
                    await draw(renderer, new RectF32(0, 0, width, height), scale);
                    if (pictures is not null)
                    {
                        var name = $"{tab.Replace(' ', '-')}_{width}x{height}_{scale:0.0}.png";
                        await File.WriteAllBytesAsync(Path.Combine(pictures, name),
                            PngWriter.Encode(renderer.Surface.Pixels, (int)width, (int)height), ct);
                    }

                    var frame = renderer.Frame;
                    (frame.Texts.Length + frame.Regions.Length).ShouldBeGreaterThan(0, $"{tab} drew nothing at {width}x{height}");
                    var findings = Wireframe.Check(frame, tab == "sky map" ? SkyChromeOnly(frame) : null)
                        .Select(f => f.Message)
                        .Concat(MustShow.TryGetValue(tab, out var shown) ? NotShownWhole(frame, shown) : [])
                        .ToArray();
                    if (findings.Length > 0)
                    {
                        report.Append($"{tab} at {width}x{height}, scale {scale:0.0}:\n  ")
                            .Append(string.Join("\n  ", findings)).Append('\n');
                    }
                }
            }
        }

        report.ToString().ShouldBeEmpty();
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
