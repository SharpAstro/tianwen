using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Label placement consumes items in priority order but usually stops after
    /// <see cref="OverlayEngine.MaxOverlayLabels"/> of them (80). It used to copy the whole item list
    /// and sort it completely to find those 80 -- at a full-sky zoom, ~7,800 items copied and sorted
    /// on every repaint, measured at 1.84 ms per frame. It now pops from a heap built in O(n).
    ///
    /// <para><b>What has to be pinned is the ORDER, not the speed.</b> Placement is deliberately
    /// deterministic frame to frame: the slot is a pure function of the catalog index and the
    /// priority is a pure function of the object, so labels do not reshuffle while panning. A pop
    /// order that differed from the sorted one even for equal-priority items would reintroduce
    /// exactly the flicker the StableSortKey tiebreak was added to kill -- and it would look like a
    /// rendering glitch, not like a comparison bug.</para>
    /// </summary>
    public class OverlayLabelOrderTests(ITestOutputHelper output)
    {
        private static OverlayItem Item(float priority, ulong key) => new()
        {
            ScreenX = 100f,
            ScreenY = 100f,
            Marker = OverlayMarker.Circle(4f),
            LabelLines = ["L" + key],
            LabelPriority = priority,
            StableSortKey = key,
        };

        /// <summary>The order the old copy-and-sort produced, kept here as the oracle.</summary>
        private static List<OverlayItem> SortedTheOldWay(IReadOnlyList<OverlayItem> items)
        {
            var sorted = new List<OverlayItem>(items);
            sorted.Sort((a, b) =>
            {
                var c = b.LabelPriority.CompareTo(a.LabelPriority);
                if (c != 0) return c;
                return a.StableSortKey.CompareTo(b.StableSortKey);
            });
            return sorted;
        }

        private static List<ulong> DrawOrder(IReadOnlyList<OverlayItem> items, int maxLabels)
        {
            var seen = new List<ulong>();
            OverlayEngine.PlaceLabelsBestEffort(
                items, labelSize: 10f, labelPad: 4f,
                measureText: (t, s) => t.Length * s * 0.5f,
                drawLabelLines: (item, _, _) => seen.Add(item.StableSortKey),
                maxLabels: maxLabels);
            return seen;
        }

        /// <summary>
        /// Element for element, including ties. Deliberately seeds MANY equal priorities: that is the
        /// case the tiebreak exists for, and a heap that only agreed on distinct priorities would pass
        /// a test built from unique values.
        /// </summary>
        [Fact]
        public void ThePopOrderMatchesTheSortedOrderExactly()
        {
            var rng = new Random(42);
            var items = new List<OverlayItem>();
            for (var i = 0; i < 2000; i++)
            {
                // Only 20 distinct priorities over 2000 items, so ties are the common case.
                items.Add(Item(rng.Next(0, 20), (ulong)rng.Next(0, int.MaxValue)));
            }

            var expected = SortedTheOldWay(items).Select(i => i.StableSortKey).ToList();
            var actual = DrawOrder(items, maxLabels: items.Count);

            actual.Count.ShouldBe(expected.Count);
            actual.SequenceEqual(expected).ShouldBeTrue("the heap order must equal the sorted order");
            output.WriteLine($"{items.Count} items, {expected.Distinct().Count()} distinct keys: order identical");
        }

        /// <summary>
        /// The cap still applies, and the items drawn are the TOP ones -- the whole point of ordering
        /// before capping. A heap that popped in arbitrary order would still draw exactly 80 labels,
        /// so a count assertion alone cannot see this.
        /// </summary>
        [Fact]
        public void TheCapKeepsTheHighestPriorityItems()
        {
            var items = new List<OverlayItem>();
            for (var i = 0; i < 500; i++)
            {
                items.Add(Item(priority: i, key: (ulong)i));
            }

            var drawn = DrawOrder(items, maxLabels: OverlayEngine.MaxOverlayLabels);

            drawn.Count.ShouldBe(OverlayEngine.MaxOverlayLabels);
            var expected = SortedTheOldWay(items)
                .Take(OverlayEngine.MaxOverlayLabels).Select(i => i.StableSortKey).ToList();
            drawn.SequenceEqual(expected).ShouldBeTrue();
            drawn[0].ShouldBe(499ul, "the highest priority must come first");
        }

        /// <summary>
        /// Why the order is popped LAZILY rather than selected as a bounded top-80. The collision
        /// variant drops a label that cannot find a free slot and lets the next one through, so it can
        /// walk well past the cap; truncating its input to 80 would silently change which labels
        /// appear. Every item here is stacked on one point, so all four slots collide immediately and
        /// placement has to keep reaching further down the order to fill the cap.
        /// </summary>
        [Fact]
        public void TheCollisionVariantWalksPastTheCap()
        {
            var items = new List<OverlayItem>();
            for (var i = 0; i < 400; i++)
            {
                items.Add(Item(priority: i, key: (ulong)i));
            }

            var examined = 0;
            var placed = 0;
            OverlayEngine.PlaceLabels(
                items, labelSize: 10f, labelPad: 4f,
                measureText: (t, s) => { examined++; return 400f; },
                drawLabelLines: (_, _, _) => placed++,
                maxLabels: 10);

            output.WriteLine($"placed {placed} labels after measuring {examined} items");
            examined.ShouldBeGreaterThan(placed,
                "collisions must let placement reach past the items it actually drew");
        }

        /// <summary>An empty input must not pop, allocate, or throw.</summary>
        [Fact]
        public void AnEmptyItemListDrawsNothing()
            => DrawOrder([], maxLabels: OverlayEngine.MaxOverlayLabels).ShouldBeEmpty();
    }
}
