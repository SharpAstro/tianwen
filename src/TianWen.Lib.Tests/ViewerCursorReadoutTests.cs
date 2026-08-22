using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The cursor readout reports the channel that is on SCREEN, not every channel the image has.
    /// </summary>
    /// <remarks>
    /// <para>Two things at once, which is why it is worth a suite. It is a correctness fix -- reporting
    /// R, G and B while the display shows a single channel names two channels the user cannot see -- and
    /// it is the readout's cost: <c>GetPixelInfo</c> runs on every mouse move, so reading one plane
    /// instead of three is what would let the unused planes be released at all.</para>
    /// <para>The mapping is asserted on <c>ChannelView</c> itself as well as through the document,
    /// because the texture upload resolves the displayed channel the same way and the two disagreeing
    /// is the original bug.</para>
    /// </remarks>
    [Collection("UI")]
    public class ViewerCursorReadoutTests
    {
        private const int Width = 8;
        private const int Height = 6;

        [Fact]
        public async Task CompositeReadsEveryChannel()
        {
            var document = await NewThreeChannelDocumentAsync();

            var info = document.GetPixelInfo(3, 2);

            info.Values.Length.ShouldBe(3, "a composite view shows all three, so it must report all three");
            info.Values[0].ShouldBeLessThan(info.Values[1]);
            info.Values[1].ShouldBeLessThan(info.Values[2]);
        }

        [Fact]
        public async Task ASingleChannelViewReadsOnlyThatChannel()
        {
            var document = await NewThreeChannelDocumentAsync();

            var composite = document.GetPixelInfo(3, 2);
            var blue = document.GetPixelInfo(3, 2, channel: 2);

            blue.Values.Length.ShouldBe(1, "one plane read, not three");
            blue.Values[0].ShouldBe(composite.Values[2], 1e-7f, "and it must be the channel asked for");
        }

        /// <summary>The whole point: the readout follows the VIEW, with no second copy of the mapping.</summary>
        [Fact]
        public async Task TheReadoutFollowsTheChannelView()
        {
            var document = await NewThreeChannelDocumentAsync();
            var state = new ViewerState();
            var composite = document.GetPixelInfo(3, 2);

            state.ChannelView = ChannelView.Green;
            ViewerActions.UpdateCursorInfo(document, state, 3, 2);

            var info = state.CursorPixelInfo.ShouldNotBeNull();
            info.Values.Length.ShouldBe(1);
            info.Values[0].ShouldBe(composite.Values[1], 1e-7f, "Green view must report channel 1");

            state.ChannelView = ChannelView.Composite;
            ViewerActions.UpdateCursorInfo(document, state, 3, 2);

            state.CursorPixelInfo.ShouldNotBeNull().Values.Length.ShouldBe(3,
                "and cycling back to composite must restore all three");
        }

        /// <summary>
        /// A single value with no label is ambiguous once a colour image can produce one, so the panel
        /// names the channel. Asserted on the rendered line, because that is what the user reads.
        /// </summary>
        [Theory]
        [InlineData(ChannelView.Red, "R: ")]
        [InlineData(ChannelView.Green, "G: ")]
        [InlineData(ChannelView.Blue, "B: ")]
        public async Task TheSingleValueIsNamedAfterTheChannelOnScreen(ChannelView view, string expectedPrefix)
        {
            var document = await NewThreeChannelDocumentAsync();
            var state = new ViewerState { ChannelView = view };
            ViewerActions.UpdateCursorInfo(document, state, 3, 2);

            var lines = InfoPanelData.GetCursorLines(state);

            lines.ShouldContain(line => line.StartsWith(expectedPrefix, StringComparison.Ordinal));
            lines.ShouldNotContain(line => line.StartsWith("Val: ", StringComparison.Ordinal));
        }

        /// <summary>A mono image has nothing to disambiguate, so it keeps the neutral label.</summary>
        [Fact]
        public async Task AMonoImageKeepsTheNeutralLabel()
        {
            var document = await NewMonoDocumentAsync();
            // Mono never leaves Composite: CycleChannelView only moves for more than one channel.
            var state = new ViewerState();
            ViewerActions.UpdateCursorInfo(document, state, 3, 2);

            var lines = InfoPanelData.GetCursorLines(state);

            lines.ShouldContain(line => line.StartsWith("Val: ", StringComparison.Ordinal));
        }

        /// <summary>
        /// The mapping itself, asserted where it lives. The clamp matters: a 2-channel image can reach
        /// Channel1 but not Channel2, and an unclamped index would read past the channel list.
        /// </summary>
        [Theory]
        [InlineData(ChannelView.Composite, 3, null)]
        [InlineData(ChannelView.Red, 3, 0)]
        [InlineData(ChannelView.Green, 3, 1)]
        [InlineData(ChannelView.Blue, 3, 2)]
        [InlineData(ChannelView.Channel0, 3, 0)]
        [InlineData(ChannelView.Channel2, 2, 1)]   // clamped
        [InlineData(ChannelView.Green, 1, 0)]      // clamped
        [InlineData(ChannelView.Composite, 1, null)]
        public void TheViewNamesItsSourceChannel(ChannelView view, int channelCount, int? expected)
        {
            view.DisplayedSourceChannel(channelCount).ShouldBe(expected);
        }

        // Distinct per-channel values, ascending, so a wrong channel is a wrong NUMBER rather than a
        // coincidence. AdoptImageAsync normalises in place, which preserves the ordering.
        private static Task<AstroImageDocument> NewThreeChannelDocumentAsync()
        {
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                planes[c] = new float[Height, Width];
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        planes[c][y, x] = 1000f + c * 5000f + y * Width + x;
                    }
                }
            }

            return AstroImageDocument.AdoptImageAsync(
                new Image(planes, BitDepth.Int16, 65535f, 0f, 0f, Meta(SensorType.Color)),
                DebayerAlgorithm.None);
        }

        /// <summary>
        /// A pointer over the chrome must report nothing, even when the zoomed image's extent covers
        /// the chrome's position.
        /// </summary>
        /// <remarks>
        /// The caller repaints the window whenever this readout CHANGES, so a readout that tracks the
        /// pointer across the file list repaints on every motion event -- measured at 8% GPU on an
        /// Adreno X1-85, and 0% once zoomed out far enough that the image no longer reaches there.
        /// That is the shape that hides the bug: the waste appears as you zoom IN, so it reads as a
        /// rendering cost rather than a hit test against the wrong rectangle.
        /// </remarks>
        [Fact]
        public async Task APointerOverTheChromeReportsNothingHoweverFarTheImageIsZoomed()
        {
            var document = await NewMonoDocumentAsync();
            var state = new ViewerState { Zoom = 40f };

            // A pane inset from the window origin, as the toolbar and file list make it.
            const float areaX = 100f, areaY = 50f, areaW = 200f, areaH = 150f;

            // Inside the pane: a real readout, which is the control for everything below.
            ViewerActions.UpdateCursorFromScreenPosition(document, state,
                areaX + areaW / 2f, areaY + areaH / 2f, areaX, areaY, areaW, areaH)
                .ShouldBeTrue();
            state.CursorImagePosition.ShouldNotBeNull();

            // Over the file list (left of the pane). At zoom 40 the image extent easily spans it, so
            // the old image-bounds-only test reported a pixel here.
            ViewerActions.UpdateCursorFromScreenPosition(document, state,
                areaX - 40f, areaY + areaH / 2f, areaX, areaY, areaW, areaH)
                .ShouldBeFalse();
            state.CursorImagePosition.ShouldBeNull();
            state.CursorPixelInfo.ShouldBeNull();

            // And it must STAY null as the pointer keeps moving, because it is the CHANGE that costs a
            // repaint. One null is a fix; a null that keeps re-reporting the same null is the saving.
            for (var dy = 0; dy < 20; dy++)
            {
                ViewerActions.UpdateCursorFromScreenPosition(document, state,
                    areaX - 40f, areaY + dy, areaX, areaY, areaW, areaH)
                    .ShouldBeFalse();
                state.CursorImagePosition.ShouldBeNull();
            }

            // Over the toolbar (above the pane).
            ViewerActions.UpdateCursorFromScreenPosition(document, state,
                areaX + areaW / 2f, areaY - 10f, areaX, areaY, areaW, areaH)
                .ShouldBeFalse();
            state.CursorImagePosition.ShouldBeNull();

            // Over the info panel (right of the pane).
            ViewerActions.UpdateCursorFromScreenPosition(document, state,
                areaX + areaW + 5f, areaY + areaH / 2f, areaX, areaY, areaW, areaH)
                .ShouldBeFalse();
            state.CursorImagePosition.ShouldBeNull();
        }

        private static Task<AstroImageDocument> NewMonoDocumentAsync()
        {
            var plane = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    plane[y, x] = 1000f + y * Width + x;
                }
            }

            return AstroImageDocument.AdoptImageAsync(
                new Image([plane], BitDepth.Int16, 65535f, 0f, 0f, Meta(SensorType.Monochrome)),
                DebayerAlgorithm.None);
        }

        private static ImageMeta Meta(SensorType sensorType)
            => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, sensorType, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN);
    }
}
