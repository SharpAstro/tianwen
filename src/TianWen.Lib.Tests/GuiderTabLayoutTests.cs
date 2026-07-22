using System;
using System.Collections.Immutable;
using System.IO;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Offline render tests for <see cref="GuiderTab{TSurface}"/> over the CPU
    /// <see cref="RgbaImageRenderer"/> -- no GPU/device needed. These pin the L1 layout-driven
    /// conversion (docs/plans/layout-driven-ui.md): the whole frame is ONE Layout tree, so the
    /// arranged raster-pane rects (graph band, target scatter, star-profile plot) must reproduce
    /// the shipped hand-computed geometry, at 1x and at high DPI, and every region must paint
    /// (the sentinel sweep catches a zero-width pane that silently drops out of the tree).
    /// </summary>
    [Collection("UI")]
    public class GuiderTabLayoutTests
    {
        private static readonly DateTimeOffset SampleStart = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

        /// <summary>A live state mid-guiding: samples, stats, and a star profile, so the graph,
        /// target view, stats rows, and profile plot all have real content to paint.</summary>
        private static LiveSessionState BuildGuidingState()
        {
            var samples = ImmutableArray.CreateBuilder<GuideErrorSample>(60);
            for (var i = 0; i < 60; i++)
            {
                samples.Add(new GuideErrorSample(
                    SampleStart + TimeSpan.FromSeconds(i * 2),
                    RaError: Math.Sin(i * 0.3) * 0.8,
                    DecError: Math.Cos(i * 0.2) * 0.5,
                    RaCorrectionMs: i % 5 == 0 ? 120 : 0,
                    DecCorrectionMs: i % 7 == 0 ? -90 : 0,
                    IsDither: i == 30,
                    IsSettling: i is > 30 and < 35));
            }

            // Gaussian-ish cross-sections so the profile pane plots + fits real curves.
            var h = new float[21];
            var v = new float[21];
            for (var i = 0; i < h.Length; i++)
            {
                var d = i - 10;
                h[i] = 1000f * MathF.Exp(-d * d / 8f);
                v[i] = 900f * MathF.Exp(-d * d / 10f);
            }

            return new LiveSessionState
            {
                IsRunning = true,
                Phase = SessionPhase.Observing,
                GuiderState = "Guiding",
                GuideSamples = samples.MoveToImmutable(),
                LastGuideStats = new GuideStats
                {
                    TotalRMS = 0.82,
                    RaRMS = 0.55,
                    DecRMS = 0.61,
                    PeakRa = 1.6,
                    PeakDec = 1.3,
                    LastRaErr = 0.31,
                    LastDecErr = -0.22,
                },
                GuideExposure = TimeSpan.FromSeconds(2),
                GuideStarProfile = (h, v),
            };
        }

        private static GuiderTab<RgbaImage> RenderTab(RgbaImageRenderer renderer, LiveSessionState live, float dpiScale = 1f)
        {
            // DPI + font are widget-owned properties now (host-set), not Render arguments.
            // A real font: the header/stats/axis labels rasterize glyphs on the CPU renderer.
            var tab = new GuiderTab<RgbaImage>(renderer) { DpiScale = dpiScale, FontPath = FontResolver.ResolveSystemFont() };
            var time = new FakeTimeProviderWrapper(SampleStart);
            tab.Render(live, new RectF32(0, 0, renderer.Width, renderer.Height), time);
            return tab;
        }

        [Fact]
        public void Guiding_ArrangesTheShippedFrame()
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var tab = RenderTab(renderer, BuildGuidingState());

            // Graph band pinned to the bottom, full width, max(20% of the body, 80) tall.
            var graphH = MathF.Max((800f - 32f) * 0.2f, 80f);
            tab.GraphRect.X.ShouldBe(0f, 0.5f);
            tab.GraphRect.Width.ShouldBe(1280f, 0.5f);
            tab.GraphRect.Height.ShouldBe(graphH, 0.5f);
            (tab.GraphRect.Y + tab.GraphRect.Height).ShouldBe(800f, 0.5f);

            // Right column = profile (120) + stats (220); target view is its bottom half.
            var mainH = 800f - 32f - graphH;
            tab.TargetViewRect.X.ShouldBe(1280f - 340f, 0.5f);
            tab.TargetViewRect.Width.ShouldBe(340f, 0.5f);
            tab.TargetViewRect.Y.ShouldBe(32f + mainH / 2f, 0.5f);
            tab.TargetViewRect.Height.ShouldBe(mainH / 2f, 0.5f);

            // Star-profile PLOT rect: the pane minus the panel padding + title row, which are layout.
            var pad = GuiTheme.Metrics.Padding;
            var titleH = GuiTheme.Metrics.BaseFontSize * 1.4f;
            tab.ProfilePlotRect.X.ShouldBe(1280f - 340f + pad, 0.5f);
            tab.ProfilePlotRect.Width.ShouldBe(120f - pad * 2f, 0.5f);
            tab.ProfilePlotRect.Y.ShouldBe(32f + pad + titleH, 0.5f);
            (tab.ProfilePlotRect.Y + tab.ProfilePlotRect.Height).ShouldBe(32f + mainH / 2f - pad, 0.5f);

            // No viewer wired in this test, so the camera pane is its empty-state text leaf.
            tab.CameraRect.Width.ShouldBe(0f);
        }

        [Fact]
        public void Guiding_HighDpi_ScalesTheSameDesignLayout()
        {
            // Same 1280x800 design space at dpiScale 2 (a 2560x1600 physical surface): every rect is
            // exactly the 1x layout scaled -- design units are the single source of truth.
            using var renderer = new RgbaImageRenderer(2560, 1600);
            var tab = RenderTab(renderer, BuildGuidingState(), dpiScale: 2f);

            var graphH = MathF.Max((800f - 32f) * 0.2f, 80f) * 2f;
            tab.GraphRect.Height.ShouldBe(graphH, 1f);
            tab.TargetViewRect.X.ShouldBe(2560f - 680f, 1f);
            tab.TargetViewRect.Width.ShouldBe(680f, 1f);
            tab.GraphRect.Y.ShouldBe(1600f - graphH, 1f);
        }

        [Fact]
        public void Placeholder_HasNoRasterPanes()
        {
            // A default live state (no session) renders the placeholder tree: header + centred
            // reason, no camera/profile/target/graph fills.
            using var renderer = new RgbaImageRenderer(1280, 800);
            var tab = RenderTab(renderer, new LiveSessionState());

            tab.State.PlaceholderReason.ShouldBe(GuiderPlaceholder.NoSession);
            tab.GraphRect.ShouldBe(default(RectF32));
            tab.TargetViewRect.ShouldBe(default(RectF32));
            tab.ProfilePlotRect.ShouldBe(default(RectF32));
            tab.CameraRect.ShouldBe(default(RectF32));
        }

        // Sentinel the guider tab never paints: the root tree carries the content background and every
        // pane paints an opaque panel background, so after a render almost no sentinel survives. A
        // zero/negative-width pane (the classic conversion failure mode) leaves its whole rect
        // sentinel-coloured, which this catches.
        private static bool IsSentinel(byte r, byte g, byte b) => r == 0xff && g == 0x00 && b == 0xff;

        [Theory]
        [InlineData(1280, 800, true, "guiding-desktop")]
        [InlineData(500, 360, true, "guiding-narrow")]
        [InlineData(1280, 800, false, "placeholder")]
        public void Guider_PaintsTheFullContent(int width, int height, bool guiding, string label)
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var pixels = renderer.Surface.Pixels;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                pixels[i] = 0xff; pixels[i + 1] = 0x00; pixels[i + 2] = 0xff; pixels[i + 3] = 0xff;
            }

            RenderTab(renderer, guiding ? BuildGuidingState() : new LiveSessionState());

            long sentinel = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (IsSentinel(pixels[i], pixels[i + 1], pixels[i + 2]))
                {
                    sentinel++;
                }
            }

            // Emit a PNG beside the test binary so the render can be eyeballed.
            var pngPath = Path.Combine(AppContext.BaseDirectory, $"guidertab-{label}.png");
            File.WriteAllBytes(pngPath, PngWriter.Encode(pixels, renderer.Surface.Width, renderer.Surface.Height));

            var sentinelFraction = (double)sentinel / ((long)width * height);
            sentinelFraction.ShouldBeLessThan(0.02,
                $"{label} ({width}x{height}) left unpainted regions; PNG at {pngPath}");
        }
    }
}
