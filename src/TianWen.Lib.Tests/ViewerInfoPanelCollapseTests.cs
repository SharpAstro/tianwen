using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The docked info strip rolls its Statistics section up to a heading, starts that way, and
    /// reports the statistics as a table when it is open.
    /// </summary>
    /// <remarks>
    /// <para>The user's words: "the statistics take a shitton of space". Thirteen rows of per-channel
    /// numbers were the tallest block in the strip. Two things fixed it: the section rolls up to its
    /// heading by default, and open, it is a five-row table rather than thirteen space-padded lines,
    /// because a run of spaces lines nothing up once the strip's font stopped being monospaced.</para>
    /// <para><b>Observed through the hit tracker, not through pixels.</b> The heading is a registered
    /// button, so the scan finds it; and everything below a rolled-up section moves up by the rows it
    /// no longer draws, which the next heading's y reports. The table's contents are pinned on
    /// <see cref="InfoPanelData.GetStatisticsTable"/> directly.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerInfoPanelCollapseTests
    {
        private const uint WindowW = 900;
        private const uint WindowH = 700;
        private const int ImageW = 8;
        private const int ImageH = 6;

        private sealed class StripViewer : ImageRendererBase<RgbaImage>
        {
            public StripViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
                DpiScale = 1f;
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

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;
        }

        /// <summary>A small three-channel frame, so the statistics have three channel rows and a Luma row.</summary>
        internal static async Task<AstroImageDocument> NewColourDocumentAsync(CancellationToken ct)
        {
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                planes[c] = new float[ImageH, ImageW];
                for (var y = 0; y < ImageH; y++)
                {
                    for (var x = 0; x < ImageW; x++)
                    {
                        planes[c][y, x] = (100f * (c + 1)) + (y * ImageW) + x;
                    }
                }
            }

            var image = new Image([planes[0], planes[1], planes[2]], BitDepth.Int16, 65535f, 0f, 0f,
                new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                    0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
                    RowOrder.TopDown, float.NaN, float.NaN));
            return await AstroImageDocument.AdoptImageAsync(
                image, DebayerAlgorithm.None, null, filePath: "synthetic.fits", cancellationToken: ct);
        }

        private static async Task<(StripViewer Viewer, ViewerState State, AstroImageDocument Document)>
            NewViewerAsync(RgbaImageRenderer renderer, CancellationToken ct)
        {
            var document = await NewColourDocumentAsync(ct);
            var viewer = new StripViewer(renderer);
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);
            var state = new ViewerState
            {
                ShowFileList = false,
                ShowHistogram = false,
                ShowInfoPanel = true,
                StretchMode = StretchMode.None,
                ZoomToFit = false,
                Zoom = 1f,
            };
            return (viewer, state, document);
        }

        /// <summary>
        /// Every button registered in the strip after a render, by name, with the y of its first hit.
        /// <see cref="PixelWidgetBase{T}.HitTest"/> looks without dispatching, so the scan changes
        /// nothing.
        /// </summary>
        private static Dictionary<string, float> PanelButtons(StripViewer viewer)
        {
            var rect = viewer.CurrentInfoPanelRect;
            rect.Width.ShouldBeGreaterThan(0f, "the strip has to be laid out for this to observe anything");

            var buttons = new Dictionary<string, float>();
            var x = rect.X + 8f;
            for (var y = rect.Y; y < rect.Y + rect.Height; y += 1f)
            {
                if (viewer.HitTest(x, y) is HitResult.ButtonHit button)
                {
                    buttons.TryAdd(button.Action, y);
                }
            }
            return buttons;
        }

        private static void Click(StripViewer viewer, float y)
            => viewer.HitTestAndDispatch(viewer.CurrentInfoPanelRect.X + 8f, y);

        [Fact]
        public async Task StatisticsStartRolledUpBehindTheirHeading()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document) = await NewViewerAsync(renderer, ct);

            state.InfoPanelStatisticsCollapsed.ShouldBeTrue("rolled up is the default");

            viewer.Render(document, state);
            PanelButtons(viewer).ShouldContainKey("ToggleStatistics");
        }

        /// <summary>
        /// The heading is the toggle, and rolling Statistics up is what gives the space back:
        /// everything below it moves up by the rows it no longer draws.
        /// </summary>
        [Fact]
        public async Task TheHeadingTogglesTheSectionAndTheRowsBelowMoveWithIt()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document) = await NewViewerAsync(renderer, ct);

            // The cursor section is the one thing drawn below the statistics on this fixture, so it
            // is the yardstick: a pixel under the pointer, then its heading is a button? It is not --
            // so the yardstick is the strip's next REGISTERED thing, which needs a stacked view for
            // the wavelet buttons. Simpler: measure the heading's own y twice and the height the
            // section adds through the wavelet toggle, which registers when ShowStacked is on.
            state.ShowStacked = true;

            viewer.Render(document, state);
            var rolledUp = PanelButtons(viewer);
            var waveletWhenRolledUp = rolledUp["WaveletToggle"];

            Click(viewer, rolledUp["ToggleStatistics"]);
            state.InfoPanelStatisticsCollapsed.ShouldBeFalse("the heading is the toggle");

            viewer.Render(document, state);
            var open = PanelButtons(viewer);

            // Five table rows for a colour frame (a header, R, G, B and Luma), each at least eight
            // pixels tall at any font size the strip draws.
            (open["WaveletToggle"] - waveletWhenRolledUp).ShouldBeGreaterThan(5 * 8f,
                "the statistics table sits between the two only while the section is open");

            Click(viewer, open["ToggleStatistics"]);
            state.InfoPanelStatisticsCollapsed.ShouldBeTrue("and the heading rolls it back up");
        }

        /// <summary>
        /// Open, the statistics are a table: a header and one row per channel plus Luma, numbers at
        /// the precision their size deserves, nothing padded with spaces.
        /// </summary>
        [Fact]
        public async Task TheStatisticsAreATableOfFiveRowsForAColourFrame()
        {
            var ct = TestContext.Current.CancellationToken;
            var document = await NewColourDocumentAsync(ct);

            var (header, rows) = InfoPanelData.GetStatisticsTable(document);

            header.ShouldBe(["", "mean", "med", "MAD", "bg"]);
            rows.Length.ShouldBe(4, "R, G, B and Luma");
            rows[0][0].ShouldBe("R");
            rows[1][0].ShouldBe("G");
            rows[2][0].ShouldBe("B");
            rows[3][0].ShouldBe("Luma");
            rows[3][1].ShouldBe("", "Luma carries only its background");
            rows[3][4].ShouldNotBe("");
            foreach (var row in rows)
            {
                row.Length.ShouldBe(header.Length);
                foreach (var cell in row)
                {
                    cell.ShouldNotContain("  ", customMessage: "no cell is padded: the renderer aligns columns, not the text");
                }
            }
        }

        /// <summary>The compact number format, at each of its four precisions.</summary>
        [Theory]
        [InlineData(12345.678, "12346")]
        [InlineData(123.456, "123.5")]
        [InlineData(1.23456, "1.23")]
        [InlineData(0.0123456, "0.0123")]
        [InlineData(double.NaN, "-")]
        public void ANumberIsAsShortAsItsSizeAllows(double value, string expected)
            => InfoPanelData.Compact(value).ShouldBe(expected);
    }
}
