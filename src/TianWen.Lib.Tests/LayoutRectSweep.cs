using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Every rect the engine arranged for the chrome a text leaf's line box moves (#1490, P5 of
/// docs/plans/viewer-layout-engine.md): the viewer with its info panel, histogram, planetary panels and both popovers, the night
/// calendar, and the Guider and Live Session tabs, each at DPI 1 and 1.5, one line per arranged node with what it holds. Run
/// before and after a change to how a text node measures, the two dumps' diff is what moved
/// (docs/plans/viewer-layout-engine-p5/, before, after the DIR.Lib bump, and after the hand line boxes went).
/// </summary>
/// <remarks>
/// Set <c>TIANWEN_LAYOUT_SWEEP</c> to a file path to write the dump there; unset, the sweep still runs and only checks that every
/// scenario arranged something, so it cannot rot unnoticed.
/// </remarks>
[Collection("UI")]
public class LayoutRectSweep(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_LAYOUT_SWEEP";
    private const uint WindowW = 1400;
    private const uint WindowH = 900;
    private static readonly float[] Scales = [1f, 1.5f];

    private sealed class SweepViewer : ImageRendererBase<RgbaImage>
    {
        private HistogramDisplay? _histogram;

        public SweepViewer(RgbaImageRenderer renderer, float dpiScale) : base(renderer)
        {
            Bus = new SignalBus();
            Width = renderer.Width;
            Height = renderer.Height;
            DpiScale = dpiScale;
            FontPath = FontResolver.ResolveSystemFont();
        }

        protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
            in DisplayRendition rendition, WCS? wcs,
            float left, float top, float right, float bottom, uint projW, uint projH,
            RenditionSlot slot, bool sampleBeforeChannels) { }

        protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
            ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

        protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
            float rotationRad, RGBAColor32 color, float thickness) { }

        protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

        protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
            RGBAColor32 color, float thickness) { }

        protected override void OnResize(uint width, uint height) { }

        public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int width, int height) { }

        public override void UploadHistogramData(IPreviewSource source) => _histogram = new HistogramDisplay(source.ChannelStatistics);

        protected override HistogramDisplay? GetHistogramDisplay() => _histogram;
    }

    [Fact(Timeout = 120_000)]
    public async Task EveryArrangedRectOfTheTextChrome()
    {
        var ct = TestContext.Current.CancellationToken;
        var dump = new StringBuilder();

        foreach (var scale in Scales)
        {
            foreach (var (name, configure) in ViewerScenarios())
            {
                using var renderer = new RgbaImageRenderer(WindowW, WindowH);
                var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
                var viewer = new SweepViewer(renderer, scale);
                viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 8, 6);
                var state = new ViewerState
                {
                    ShowFileList = false,
                    ShowHistogram = true,
                    ShowInfoPanel = true,
                    StretchMode = StretchMode.None,
                    ZoomToFit = false,
                    Zoom = 1f,
                };
                configure(state);
                // Twice, as a window does: the first frame uploads the histogram the second paints.
                viewer.Render(document, state);
                viewer.Render(document, state);
                Append(dump, $"viewer: {name}", scale, viewer);
            }

            {
                var (popover, renderer, state, calendarClock) = await NightCalendarTests.OpenCalendarAsync(output);
                using var _ = renderer;
                popover.DpiScale = scale;
                popover.Render(state, new RectF32(600f, 0f, 200f, 28f), new RectF32(0f, 0f, renderer.Width, renderer.Height), calendarClock);
                Append(dump, "night calendar", scale, popover);
            }

            // A fixed instant, so nothing the tabs draw from the clock moves between two dumps.
            var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
            var live = new LiveSessionState
            {
                IsRunning = true,
                Phase = SessionPhase.Observing,
                LastGuideStats = GuideStats.FromRms(0.62, 0.41, 0.46, 1.2, 1.4),
            };
            using (var renderer = new RgbaImageRenderer(WindowW, WindowH))
            {
                var guider = new GuiderTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
                guider.Render(live, new RectF32(0, 0, WindowW, WindowH), clock);
                Append(dump, "guider tab", scale, guider);
            }
            using (var renderer = new RgbaImageRenderer(WindowW, WindowH))
            {
                var tab = new LiveSessionTab<RgbaImage>(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
                tab.Render(live, new RectF32(0, 0, WindowW, WindowH), clock);
                Append(dump, "live session tab", scale, tab);
            }
        }

        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, dump.ToString().ReplaceLineEndings("\n"), ct);
        }
    }

    private static IEnumerable<(string Name, Action<ViewerState> Configure)> ViewerScenarios()
    {
        yield return ("info panel and histogram", _ => { });
        yield return ("planetary master (telescope and wavelet panels)", s => s.MasterPlanet = CatalogIndex.Jupiter);
        yield return ("tone popover", s => s.TonePopover.Open());
        yield return ("white balance popover", s => s.WhiteBalancePopover.Open());
    }

    private static void Append(StringBuilder dump, string scenario, float scale, IPixelWidget widget)
    {
        var nodes = new List<Layout.ArrangedNode<float>>();
        widget.CollectPaintedNodes(nodes);
        nodes.ShouldNotBeEmpty($"{scenario} at {scale}x arranged nothing");

        var inv = CultureInfo.InvariantCulture;
        dump.Append(inv, $"## {scenario} @ {scale:0.0}x\n");
        foreach (var n in nodes)
        {
            var b = n.Bounds;
            dump.Append(' ', n.Depth * 2)
                .Append(inv, $"{Describe(n.Node)} [{b.X:0.##},{b.Y:0.##} {b.Width:0.##}x{b.Height:0.##}]\n");
        }
        dump.Append('\n');
    }

    private static string Describe(Layout.Node node)
    {
        var what = node switch
        {
            Layout.Node.Leaf { Content: Layout.Content.Text t } => $"Text \"{t.Value}\" {t.FontSize:0.##}",
            Layout.Node.Leaf { Content: Layout.Content.Fill { Key: { } key } } => $"Fill {key}",
            Layout.Node.Leaf { Content: var c } => c.GetType().Name,
            _ => node.GetType().Name,
        };
        return node.Hit is HitResult.ButtonHit { Action: var action } ? $"{what} <{action}>" : what;
    }
}
