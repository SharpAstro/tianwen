using System;
using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A marker may be occluded by the host's content; a LABEL may not be sliced by its edge.
    /// </summary>
    /// <remarks>
    /// <para>Point markers are drawn with the imagery, behind the photograph, deliberately: the
    /// photograph is a better picture of those same objects. That reasoning covers a marker, which is
    /// a dot either way. It does not cover a name -- half a word cut down its middle by the frame edge
    /// reads as a rendering fault, which is what "the RCW marker is below the image" was looking at.</para>
    /// <para>The placer already had the mechanism (<c>reservedRegions</c>, built for the mount
    /// reticle's label); the host just had to say where its content is. The second effect is the one
    /// worth keeping in mind: labels are CAPPED, so one placed under the photograph was spending a
    /// scarce slot to draw something nobody could see.</para>
    /// </remarks>
    public class SkyLabelOcclusionTests
    {
        private static OverlayItem Item(string name, float x, float y) => new OverlayItem
        {
            LabelLines = [name],
            ScreenX = x,
            ScreenY = y,
            Marker = new OverlayMarker { Kind = OverlayMarkerKind.Circle, SemiMajorPx = 3f },
        };

        private static float Measure(string text, float size) => text.Length * size * 0.6f;

        private static List<string> Place(
            IReadOnlyList<OverlayItem> items,
            IReadOnlyList<(float X, float Y, float W, float H)>? reserved)
        {
            var drawn = new List<string>();
            OverlayEngine.PlaceLabelsBestEffort(items, labelSize: 10f, labelPad: 4f, Measure,
                label => drawn.Add(label.Item.LabelLines[0]),
                reservedRegions: reserved);
            return drawn;
        }

        /// <summary>
        /// With nothing covering the map every name is placed -- the control, without which "keeps
        /// clear" could be satisfied by drawing nothing at all.
        /// </summary>
        [Fact]
        public void WithNothingOverTheMapEveryLabelIsDrawn()
        {
            var items = new[]
            {
                Item("RCW 144", 400f, 300f),
                Item("NGC 6465", 400f, 80f),
            };

            var drawn = Place(items, reserved: null);

            drawn.ShouldContain("RCW 144");
            drawn.ShouldContain("NGC 6465");
        }

        /// <summary>
        /// <b>A name that would land on the photograph is dropped rather than drawn under it.</b>
        /// The object at 300 sits inside the frame; the one well above it does not.
        /// </summary>
        [Fact]
        public void ALabelOverTheHostsContentIsDroppedAndOneOutsideIsKept()
        {
            var items = new[]
            {
                Item("RCW 144", 400f, 300f),
                Item("NGC 6465", 400f, 80f),
            };

            // The photograph: a band across the middle of the pane.
            var drawn = Place(items, reserved: [(200f, 200f, 400f, 400f)]);

            drawn.ShouldNotContain("RCW 144", "this name would have been sliced by the frame edge");
            drawn.ShouldContain("NGC 6465", "and one in open sky is unaffected");
        }

        /// <summary>
        /// The state carries the box only while something is actually over the map, so a map nothing
        /// covers never carves a hole in its own labels.
        /// </summary>
        [Fact]
        public void TheOccluderIsAbsentUntilAHostSetsIt()
        {
            var state = new SkyMapState();

            state.OccludedByHost.ShouldBeNull("nothing is over a map by default");

            state.OccludedByHost = new RectF32(10f, 20f, 30f, 40f);
            state.OccludedByHost.Value.Width.ShouldBe(30f);

            state.OccludedByHost = null;
            state.OccludedByHost.ShouldBeNull();
        }
    }
}
