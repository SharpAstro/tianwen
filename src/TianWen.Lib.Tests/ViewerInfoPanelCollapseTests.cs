using System;
using System.Collections.Generic;
using System.Linq;
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
            public StripViewer(RgbaImageRenderer renderer, float dpiScale = 1f) : base(renderer)
            {
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
                    0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
                    RowOrder.TopDown, float.NaN, float.NaN));
            return await AstroImageDocument.AdoptImageAsync(
                image, DebayerAlgorithm.None, null, filePath: "synthetic.fits", cancellationToken: ct);
        }

        private static async Task<(StripViewer Viewer, ViewerState State, AstroImageDocument Document)>
            NewViewerAsync(RgbaImageRenderer renderer, CancellationToken ct, float dpiScale = 1f)
        {
            var document = await NewColourDocumentAsync(ct);
            var viewer = new StripViewer(renderer, dpiScale);
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
        /// Every button registered in the strip after a render, by name, with its own painted y. Read
        /// straight back from <see cref="PixelWidgetBase{T}.GetRegisteredRegions"/> -- the arranged
        /// regions the render itself produced -- rather than found by sweeping the panel's left column
        /// pixel by pixel and asking <see cref="PixelWidgetBase{T}.HitTest"/> at each one.
        /// </summary>
        private static Dictionary<string, float> PanelButtons(StripViewer viewer)
        {
            var rect = viewer.CurrentInfoPanelRect;
            rect.Width.ShouldBeGreaterThan(0f, "the strip has to be laid out for this to observe anything");

            // A column just inside the strip's padding at any scale the tests use: a heading's hit region spans
            // most of the panel width, but this keeps an unrelated same-named button elsewhere in the
            // strip from being picked up.
            var x = rect.X + ProbeInset;
            var buttons = new Dictionary<string, float>();
            foreach (var region in viewer.GetRegisteredRegions())
            {
                if (region.Result is HitResult.ButtonHit button
                    && region.Y >= rect.Y && region.Y < rect.Y + rect.Height
                    && x >= region.X && x < region.X + region.Width)
                {
                    buttons.TryAdd(button.Action, region.Y);
                }
            }
            return buttons;
        }

        /// <summary>Whether the statistics table was PAINTED: its header's "mean" cell is among the text the strip laid out.</summary>
        private static bool TablePainted(StripViewer viewer)
            => viewer.GetCapturedLayout().Any(n => n.Node is DIR.Lib.Layout.Node.Leaf { Content: DIR.Lib.Layout.Content.Text { Value: "mean" } });

        private static void Click(StripViewer viewer, float y)
            => UiRouting.RoutePress(viewer, viewer.CurrentInfoPanelRect.X + ProbeInset, y);

        /// <summary>Past the strip's padding (6 design units) at 1.5x, where 8 px was inside it.</summary>
        private const float ProbeInset = 16f;

        /// <summary>
        /// Unpinned, the table opens where the strip has the room for it and everything below, the Cursor readout's room
        /// included, and rolls up to its heading where it has not; in one frame, at either scale.
        /// </summary>
        [Theory]
        [InlineData(1f, WindowH, true)]
        [InlineData(1f, 300u, false)]
        [InlineData(1.5f, WindowH, true)]
        [InlineData(1.5f, 300u, false)]
        public async Task StatisticsOpenWhereTheStripHasTheRoom(float dpiScale, uint designHeight, bool open)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW * 2, (uint)(designHeight * dpiScale));
            var (viewer, state, document) = await NewViewerAsync(renderer, ct, dpiScale);

            state.InfoPanelStatisticsPinned.ShouldBeNull("the room decides by default");
            viewer.Render(document, state);

            TablePainted(viewer).ShouldBe(open);
            PanelButtons(viewer).ShouldContainKey("ToggleStatistics");
        }

        /// <summary>
        /// The pointer entering the picture changes nothing above the Cursor readout: its room was kept while it was off.
        /// </summary>
        [Fact]
        public async Task ThePointerOverThePictureMovesNothingAboveTheReadout()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document) = await NewViewerAsync(renderer, ct);
            state.ShowStacked = true;

            viewer.Render(document, state);
            var off = PanelButtons(viewer);
            var tableOff = TablePainted(viewer);

            state.CursorPixelInfo = new PixelInfo(1, 2, [0.1f, 0.2f, 0.3f], null, null);
            viewer.Render(document, state);
            var over = PanelButtons(viewer);

            TablePainted(viewer).ShouldBe(tableOff);
            over["ToggleStatistics"].ShouldBe(off["ToggleStatistics"]);
        }

        /// <summary>
        /// The strip only reports (#1345, P36): for a SER, the live stack and a planetary master it registers none of the planetary
        /// controls, which live in the Enhance popover (<see cref="ViewerEnhancePopoverTests"/>).
        /// </summary>
        [Theory]
        [InlineData(1f, false, true, false)]
        [InlineData(1f, true, false, false)]
        [InlineData(1f, false, false, true)]
        [InlineData(1.5f, false, true, false)]
        [InlineData(1.5f, true, false, false)]
        [InlineData(1.5f, false, false, true)]
        public async Task TheStripRegistersNoPlanetaryControl(float dpiScale, bool ser, bool liveStack, bool master)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW * 2, (uint)(1200 * dpiScale));
            var (viewer, state, document) = await NewViewerAsync(renderer, ct, dpiScale);
            if (ser)
            {
                state.SequencePath = "capture.ser";
            }
            state.ShowStacked = liveStack;
            state.MasterPlanet = master ? TianWen.Lib.Astrometry.Catalogs.CatalogIndex.Jupiter : null;

            viewer.Render(document, state);

            state.EnhancesAsPlanet.ShouldBeTrue("each of these is on a planet's way");
            PanelButtons(viewer).Keys.ShouldNotContain(a => a.StartsWith("Wavelet", StringComparison.Ordinal)
                    || a.StartsWith("Planet", StringComparison.Ordinal) || a.StartsWith("Filter", StringComparison.Ordinal)
                    || a.StartsWith("Design", StringComparison.Ordinal) || a.StartsWith("Aperture", StringComparison.Ordinal)
                    || a.StartsWith("Strength", StringComparison.Ordinal),
                "no telescope or wavelet control is in the strip");
            PanelButtons(viewer).ShouldContainKey("ToggleStatistics");
        }

        /// <summary>
        /// The heading pins the other state; pressed again, the pin goes, since it would only restate what the room decides.
        /// </summary>
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        public async Task TheHeadingPinsTheOtherState(float dpiScale)
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW * 2, (uint)(1200 * dpiScale));
            var (viewer, state, document) = await NewViewerAsync(renderer, ct, dpiScale);

            viewer.Render(document, state);
            var before = PanelButtons(viewer);
            var openBefore = TablePainted(viewer);

            Click(viewer, before["ToggleStatistics"]);
            state.InfoPanelStatisticsPinned.ShouldBe(!openBefore, "the heading pins the state not on show");

            viewer.Render(document, state);
            var after = PanelButtons(viewer);
            TablePainted(viewer).ShouldBe(!openBefore);

            Click(viewer, after["ToggleStatistics"]);
            state.InfoPanelStatisticsPinned.ShouldBeNull("back to what the room decides, the pin has nothing left to say");
            viewer.Render(document, state);
            TablePainted(viewer).ShouldBe(openBefore);
        }

        /// <summary>The room kept for the Cursor readout is the readout itself over the most a pixel of the frame can say.</summary>
        [Fact]
        public void TheCursorRoomIsTheWidestReadout()
        {
            var state = new ViewerState { CursorPixelInfo = new PixelInfo(1, 2, [0.1f, 0.2f, 0.3f], 1.5, -20.0) };
            InfoPanelData.GetCursorLines(state).Count
                .ShouldBe(InfoPanelData.GetWidestCursorLines(3, solved: true, state.ChannelView).Count);

            state.CursorPixelInfo = new PixelInfo(1, 2, [0.1f], null, null);
            InfoPanelData.GetCursorLines(state).Count
                .ShouldBe(InfoPanelData.GetWidestCursorLines(1, solved: false, state.ChannelView).Count);
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
