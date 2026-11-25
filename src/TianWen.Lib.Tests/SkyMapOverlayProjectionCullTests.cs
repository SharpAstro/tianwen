using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <b>The projection's early cull drops nothing the exact cull would have kept, and a pan makes no item after its first
/// frame.</b> Three of four candidates are off screen (the gather covers the cells around the view), and each was taken
/// through the whole projection to be dropped at its end: most of what a frame of a pan cost with the Objects layer on
/// (2026-11-25). They are dropped on one dot product now, and that test has to be a strict superset of the exact one.
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapOverlayProjectionCullTests
{
    private static readonly RectF32 Rect = new RectF32(60f, 32f, 1850f, 1068f);

    /// <summary>What the projection keeps, by the exact rule alone: on screen within the margin, each shape's own.</summary>
    private static HashSet<int> ExpectedOnScreen(IReadOnlyList<OverlayCandidate> candidates, SkyMapState state)
    {
        var ppr = SkyMapProjection.PixelsPerRadian(Rect.Height, state.FieldOfViewDeg);
        var arcminToPixels = (float)(ppr * Math.PI / (180.0 * 60.0));
        var cx = Rect.X + Rect.Width * 0.5f;
        var cy = Rect.Y + Rect.Height * 0.5f;
        var expected = new HashSet<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var cand = candidates[i];
            if (!float.IsNaN(cand.ScreenSizeFilterArcmin) && !cand.IsPinned
                && cand.ScreenSizeFilterArcmin * arcminToPixels < OverlayEngine.DarkNebulaMinOnScreenPx)
            {
                continue;
            }

            if (!SkyMapProjection.ProjectWithMatrix(cand.RA, cand.Dec, state.CurrentViewMatrix, ppr, cx, cy, out var x, out var y))
            {
                continue;
            }

            var margin = 100f;
            if (cand.Marker is OverlayCandidateMarker.Ellipse ellipse)
            {
                margin = MathF.Max(margin, ellipse.SemiMajArcmin * arcminToPixels + 50f);
            }

            if (x >= Rect.X - margin && x <= Rect.X + Rect.Width + margin && y >= Rect.Y - margin && y <= Rect.Y + Rect.Height + margin)
            {
                expected.Add(i);
            }
        }

        return expected;
    }

    [Theory]
    [InlineData(18.0, -25.0, 60.0)]
    [InlineData(5.5, -5.0, 20.0)]
    [InlineData(0.7, 41.0, 4.0)]
    [InlineData(12.0, 88.0, 30.0)]
    [InlineData(6.0, -89.0, 120.0)]
    public async Task TheEarlyCullKeepsExactlyWhatTheExactCullKeeps(double ra, double dec, double fov)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        // Gathered wide, so most candidates are well outside the view projected below.
        var gatherAt = new SkyMapState { CenterRA = ra, CenterDec = dec, FieldOfViewDeg = Math.Min(170.0, fov * 3.0) };
        var candidates = new List<OverlayCandidate>(16384);
        OverlayEngine.GatherSkyMapOverlayCandidates(gatherAt.ComputeViewMatrix(), gatherAt.FieldOfViewDeg, Rect, 1f, db, null, candidates);
        candidates.Count.ShouldBeGreaterThan(50, "or the view tests nothing");

        var state = new SkyMapState { CenterRA = ra, CenterDec = dec, FieldOfViewDeg = fov };
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var items = new List<OverlayItem>(16384);
        OverlayEngine.ProjectSkyMapCandidatesInto(candidates, state, Rect, 1f, items);

        var expected = ExpectedOnScreen(candidates, state);
        items.Select(item => item.CandidateIndex).ToHashSet().SetEquals(expected).ShouldBeTrue(
            $"kept {items.Count} of {candidates.Count}, the exact rule keeps {expected.Count}");
        expected.Count.ShouldBeLessThan(candidates.Count, "some candidate is off screen, or the cull was not exercised");
    }

    [Fact]
    public async Task APanMovesTheItemsItMadeAndMakesNoMore()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var state = new SkyMapState { CenterRA = 18.0, CenterDec = -25.0, FieldOfViewDeg = 60.0 };
        var candidates = new List<OverlayCandidate>(16384);
        OverlayEngine.GatherSkyMapOverlayCandidates(state.ComputeViewMatrix(), 170.0, Rect, 1f, db, null, candidates);
        var reuse = new OverlayItem?[candidates.Count];
        var items = new List<OverlayItem>(16384);

        state.CurrentViewMatrix = state.ComputeViewMatrix();
        OverlayEngine.ProjectSkyMapCandidatesInto(candidates, state, Rect, 1f, items, reuse);
        var first = items.ToDictionary(item => item.CandidateIndex, item => (Item: item, item.ScreenX, item.ScreenY));
        first.Count.ShouldBeGreaterThan(20);

        state.CenterRA += 0.02;
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        OverlayEngine.ProjectSkyMapCandidatesInto(candidates, state, Rect, 1f, items, reuse);

        var moved = 0;
        foreach (var item in items)
        {
            if (!first.TryGetValue(item.CandidateIndex, out var before))
            {
                continue;
            }

            item.ShouldBeSameAs(before.Item, "the item made for a candidate is the one a later frame moves");
            if (item.ScreenX != before.ScreenX || item.ScreenY != before.ScreenY)
            {
                moved++;
            }
        }
        moved.ShouldBeGreaterThan(20, "and it is where the new view puts it");

        // The same projection without anything kept gives the same places: reuse changes who the item is, never where.
        var fresh = new List<OverlayItem>(16384);
        OverlayEngine.ProjectSkyMapCandidatesInto(candidates, state, Rect, 1f, fresh);
        fresh.Count.ShouldBe(items.Count);
        for (var i = 0; i < fresh.Count; i++)
        {
            (items[i].CandidateIndex, items[i].ScreenX, items[i].ScreenY, items[i].Marker)
                .ShouldBe((fresh[i].CandidateIndex, fresh[i].ScreenX, fresh[i].ScreenY, fresh[i].Marker));
        }
    }

    [Fact]
    public void TheCollectorIsAskedOnceEnoughHasBeenAllocatedAndNotBefore()
    {
        var collector = new BetweenFramesCollector(thresholdBytes: 256 * 1024);
        collector.AfterFrame();
        var asked = collector.Collections;

        // Little allocated: nothing is asked for, however many frames pass.
        for (var frame = 0; frame < 10; frame++)
        {
            collector.AfterFrame();
        }
        // The runtime may have collected by itself meanwhile (other tests allocate too), which only resets the count.
        collector.Collections.ShouldBe(asked);

        // Past the threshold, a frame's end takes the collection.
        var kept = new List<byte[]>();
        var took = false;
        for (var attempt = 0; attempt < 20 && !took; attempt++)
        {
            for (var i = 0; i < 64; i++)
            {
                kept.Add(new byte[8 * 1024]);
            }
            took = collector.AfterFrame();
        }
        took.ShouldBeTrue("512 KB a frame passes a 256 KB threshold unless the runtime collects first, which twenty frames in a row do not");
        collector.Collections.ShouldBe(asked + 1);
        GC.KeepAlive(kept);
    }
}

/// <summary>
/// <b>A gather that takes its candidates from an earlier gather's (OverlayGatherScratch) gives the list a fresh one does,
/// pins and all.</b> Only a candidate's pin is a gather's own; the rest is the catalogue's, made once per object and label
/// tier, where it was made again for every candidate of every gather (13 MB in 18 s of a pan, 2026-11-25).
/// </summary>
[Collection("Astrometry")]
public sealed class OverlayGatherReuseTests
{
    private static readonly DIR.Lib.RectF32 Rect = new DIR.Lib.RectF32(0f, 0f, 1600f, 1000f);

    [Fact]
    public async Task AGatherFromKeptCandidatesIsTheGatherAFreshOneMakes()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var state = new SkyMapState { CenterRA = 5.5, CenterDec = -5.0, FieldOfViewDeg = 30.0 };
        var view = state.ComputeViewMatrix();
        db.TryLookupByIndex("M42", out var orion).ShouldBeTrue();
        var pinned = new HashSet<CatalogIndex> { orion.Index };

        var scratch = new OverlayGatherScratch();
        var first = new List<OverlayCandidate>();
        OverlayEngine.GatherSkyMapOverlayCandidates(view, 30.0, Rect, 1f, db, null, first, reuse: scratch);
        // The second gather of the same view finds every candidate kept, and this time M42 is pinned.
        var kept = new List<OverlayCandidate>();
        OverlayEngine.GatherSkyMapOverlayCandidates(view, 30.0, Rect, 1f, db, pinned, kept, reuse: scratch);
        var fresh = new List<OverlayCandidate>();
        OverlayEngine.GatherSkyMapOverlayCandidates(view, 30.0, Rect, 1f, db, pinned, fresh);

        kept.Count.ShouldBe(fresh.Count);
        kept.Count.ShouldBeGreaterThan(50);
        for (var i = 0; i < fresh.Count; i++)
        {
            var (a, b) = (kept[i], fresh[i]);
            (a.CatalogIndex, a.RA, a.Dec, a.UnitVec, a.Color, a.Marker, a.IsPinned, a.LabelPriority, a.LabelSlotHint, a.HasPicture)
                .ShouldBe((b.CatalogIndex, b.RA, b.Dec, b.UnitVec, b.Color, b.Marker, b.IsPinned, b.LabelPriority, b.LabelSlotHint, b.HasPicture));
            a.LabelLines.ShouldBe(b.LabelLines);
            a.ScreenSizeFilterArcmin.ShouldBe(b.ScreenSizeFilterArcmin);
        }
        kept.ShouldContain(c => c.CatalogIndex == orion.Index && c.IsPinned, "the pin is this gather's");
        first.ShouldNotContain(c => c.IsPinned, "and was not the first's");
    }
}
