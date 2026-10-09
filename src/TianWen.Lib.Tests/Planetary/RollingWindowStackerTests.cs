using System;
using System.Linq;
using TianWen.Lib.Geometry;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class RollingWindowStackerTests
{
    private const int N = 40;

    // A textured disk on a dark sky; `blurPasses` softens it so frames grade differently (the sharpest
    // frame wins the alignment reference). No per-frame translation -> the aligner residual is ~0, so an
    // add followed by the matching evict cancels to within FP rounding.
    private static float[,] Disk(int n, int blurPasses)
    {
        var a = new float[n, n];
        double cx = n / 2.0, cy = n / 2.0, r = n * 0.38;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                a[y, x] = (dx * dx) + (dy * dy) < r * r
                    ? (float)(0.5 + (0.25 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) + (0.12 * Math.Sin((x - y) * 0.3)))
                    : 0.03f;
            }
        }

        return BoxBlur(a, blurPasses);
    }

    private static float[,] BoxBlur(float[,] src, int passes)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var cur = src.Copy();
        for (var p = 0; p < passes; p++)
        {
            var next = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    float sum = 0;
                    var c = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            sum += cur[yy, xx];
                            c++;
                        }
                    }

                    next[y, x] = sum / c;
                }
            }

            cur = next;
        }

        return cur;
    }

    // 10 frames where frame 5 is the sharpest (blurPasses = |i - 5|), so the alignment reference is frame 5
    // for any window that contains it -- which keeps the reference identical between the incremental and the
    // freshly-rebuilt stacker in the eviction test below.
    private static float[][,] SharpestAtFive()
    {
        var frames = new float[10][,];
        for (var i = 0; i < 10; i++)
        {
            frames[i] = Disk(N, Math.Abs(i - 5));
        }

        return frames;
    }

    private static double MeanAbsDiff(Image a, Image b, PixelRect region)
    {
        double sum = 0;
        var cnt = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var va = a[0, y, x];
                var vb = b[0, y, x];
                if (float.IsNaN(va) || float.IsNaN(vb)) continue;
                sum += Math.Abs(va - vb);
                cnt++;
            }
        }

        return cnt > 0 ? sum / cnt : double.MaxValue;
    }

    [Fact]
    public async Task TheScoreCacheIsBoundedByTheWindowNotByTheCapture()
    {
        // A live stream's frame count grows for as long as the capture runs, and the score cache used to keep
        // an entry for every frame ever graded: a long live stack grew it without bound.
        const int window = 8;
        var frames = new float[60][,];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = Disk(N, i % 3);
        }

        var stacker = new RollingWindowStacker(
            new InMemoryFrameStream(frames), new RollingWindowOptions { FallbackWindowFrames = window, MaxWindowFrames = window });
        for (var f = 0; f < frames.Length; f++)
        {
            (await stacker.StackToAsync(f, TestContext.Current.CancellationToken)).Release();
        }

        stacker.WindowEnd.ShouldBe(frames.Length - 1);
        stacker.ScoreCacheCount.ShouldBeLessThanOrEqualTo(2 * window, "the window and one window before it, of 60 frames seen");
    }

    // A live ring of `capacity` frames holding frames 0..count-1, of which it keeps the last `capacity`. Frame i is a disk
    // blurred `blur(i)` times (i % 3 unless given), so the sharpest frame, which the stack aligns to, is chosen by the test.
    private static LiveCameraFrameStream LiveRing(int capacity, int count, Func<int, int>? blur = null)
    {
        var ring = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono, capacity, hasTimestamps: false);
        PushInto(ring, 0, count, blur);
        return ring;
    }

    private static void PushInto(LiveCameraFrameStream ring, int from, int to, Func<int, int>? blur = null)
    {
        for (var i = from; i < to; i++)
        {
            ring.Push(Image.FromChannel(Disk(N, blur?.Invoke(i) ?? i % 3), 1f, 0f));
        }
    }

    /// <summary>
    /// A stacker behind a fast camera: the ring has dropped frames its window still holds. It used to throw for the first
    /// one, drop the stack and start again, over and over, so an ASI462MC at 92 frames a second never had a live stack
    /// (the ZWO live check, 2026-09-28). What a dropped frame added can no longer be taken back, so the stacker folds the
    /// window again from what the ring holds, which is exactly the stack a fresh stacker gives.
    /// </summary>
    [Fact]
    public async Task AStackerThatFellBehindALiveRingStacksWhatTheRingStillHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        // Every frame folded, so the frames the ring drops are frames the sum holds.
        var options = new RollingWindowOptions { FallbackWindowFrames = 6, MaxWindowFrames = 6, KeepFraction = 1 };
        // Frame 5 is the sharpest, so the stack aligns to it before and after the slide and nothing but the drop can
        // send the stacker back to a rebuild.
        static int FrameFiveSharpest(int i) => i == 5 ? 0 : 2;
        using var ring = LiveRing(capacity: 8, count: 6, FrameFiveSharpest);
        var behind = new RollingWindowStacker(ring, options);
        (await behind.StackToAsync(5, ct)).Release();

        // A slide, not a jump: the window moves to [4, 9], so frames 0 to 3 must be taken out of the sum, and the ring,
        // now holding [2, 9], has dropped 0 and 1.
        PushInto(ring, 6, 10, FrameFiveSharpest);
        var caughtUp = await behind.StackToAsync(9, ct);
        var fresh = await new RollingWindowStacker(ring, options).StackToAsync(9, ct);

        (behind.WindowStart, behind.WindowEnd, behind.ReferenceIndex).ShouldBe((4, 9, 5));
        MeanAbsDiff(caughtUp, fresh, new PixelRect(6, 6, 28, 28)).ShouldBeLessThan(1e-6, "the same frames, folded again");
        caughtUp.Release();
        fresh.Release();
    }

    [Fact]
    public async Task AWindowReachingPastTheRingIsStackedFromTheFramesTheRingHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ring = LiveRing(capacity: 8, count: 16); // holds [8, 15]

        // A window of 12 reaches back to frame 4; frames 4 to 7 are gone and add nothing.
        var wide = await new RollingWindowStacker(ring, new RollingWindowOptions { FallbackWindowFrames = 12, MaxWindowFrames = 12 })
            .StackToAsync(15, ct);
        var held = await new RollingWindowStacker(ring, new RollingWindowOptions { FallbackWindowFrames = 8, MaxWindowFrames = 8 })
            .StackToAsync(15, ct);

        MeanAbsDiff(wide, held, new PixelRect(6, 6, 28, 28)).ShouldBeLessThan(1e-6, "only the frames the ring holds are in either stack");
        wide.Release();
        held.Release();
    }

    /// <summary>
    /// A stack keeping half its frames (#1174) grades every frame and folds one only while it grades among the best half of those its
    /// window has graded: streamed one at a time, sharp and blurred in turn, it folds every sharp frame and no blurred one, and rebuilt
    /// in one step it folds the window's best half, the same frames, so its master is the sharp frames' alone.
    /// </summary>
    [Fact]
    public async Task AStackKeepingHalfItsFramesGradesThemAllAndFoldsTheSharpOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = new float[10][,];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = Disk(N, i % 2 == 0 ? 0 : 3);
        }
        var options = new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 10, KeepFraction = 0.5 };

        var streamed = new RollingWindowStacker(new InMemoryFrameStream(frames), options);
        for (var f = 0; f < frames.Length; f++)
        {
            (await streamed.StackToAsync(f, ct)).Release();
        }
        (streamed.GradedFrames, streamed.FoldedFrameCount, streamed.Folds).ShouldBe((10L, 5, 5L), "every frame graded, the sharp ones folded");

        var rebuilt = new RollingWindowStacker(new InMemoryFrameStream(frames), options);
        var master = await rebuilt.StackToAsync(9, ct);
        rebuilt.FoldedFrameCount.ShouldBe(5, "the window's best half");

        var sharp = new RollingWindowStacker(new InMemoryFrameStream([.. frames.Where((_, i) => i % 2 == 0)]), options with { KeepFraction = 1 });
        var sharpMaster = await sharp.StackToAsync(4, ct);
        MeanAbsDiff(master, sharpMaster, new PixelRect(6, 6, 28, 28)).ShouldBeLessThan(1e-5, "the sharp frames alone");
        master.Release();
        sharpMaster.Release();
    }

    /// <summary>
    /// A reference that ages out of a sliding window is replaced in place (#1174, <see cref="RollingWindowOptions.ReReferenceInPlace"/>):
    /// the window's best folded frame becomes the reference and the sum is kept, so a still disk streamed through a window of six never
    /// rebuilds after its first stack, and its master is the one a fresh rebuild of the last window gives.
    /// </summary>
    [Fact]
    public async Task AnAgedReferenceIsReplacedInPlaceWithoutFoldingTheWindowAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = new float[20][,];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = Disk(N, i % 3);
        }
        var options = new RollingWindowOptions { FallbackWindowFrames = 6, MaxWindowFrames = 6, KeepFraction = 1, ReReferenceInPlace = true };

        var streamed = new RollingWindowStacker(new InMemoryFrameStream(frames), options);
        Image? last = null;
        for (var f = 0; f < frames.Length; f++)
        {
            last?.Release();
            last = await streamed.StackToAsync(f, ct);
        }
        var fresh = await new RollingWindowStacker(new InMemoryFrameStream(frames), options).StackToAsync(frames.Length - 1, ct);

        (streamed.Rebuilds, streamed.RebuildCauses.ReferenceAged).ShouldBe((1, 0), "only the first stack folds a window");
        streamed.ReReferences.ShouldBeGreaterThanOrEqualTo(2, "a window of six ages its reference out within six frames");
        MeanAbsDiff(last.ShouldNotBeNull(), fresh, new PixelRect(6, 6, 28, 28)).ShouldBeLessThan(1e-3, "the same frames on the same grid");
        last.Release();
        fresh.Release();
    }

    /// <summary>
    /// The grid follows a drifting planet: a new reference registered more than a quarter of the aligner's tile from the sum's grid is
    /// not taken in place, and the window is folded again around it.
    /// </summary>
    [Fact]
    public async Task ADriftingPlanetsGridIsFoldedAgainOnceItsReferenceLiesAQuarterTileAway()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 96;
        var frames = new float[20][,];
        for (var i = 0; i < frames.Length; i++)
        {
            // A small textured disk moving 2 px a frame across a wide field: 38 px over the run, the aligner's tile 64 px.
            var frame = new float[size, size];
            double cx = 28 + (2 * i), cy = size / 2.0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    frame[y, x] = (dx * dx) + (dy * dy) < 12 * 12
                        ? (float)(0.5 + (0.25 * Math.Sin(dx * 0.6) * Math.Cos(dy * 0.55)))
                        : 0.03f;
                }
            }
            frames[i] = frame;
        }
        var stacker = new RollingWindowStacker(new InMemoryFrameStream(frames),
            new RollingWindowOptions { FallbackWindowFrames = 6, MaxWindowFrames = 6, KeepFraction = 1, ReReferenceInPlace = true });
        for (var f = 0; f < frames.Length; f++)
        {
            (await stacker.StackToAsync(f, ct)).Release();
        }

        stacker.ReReferences.ShouldBeGreaterThanOrEqualTo(1, "a reference within a quarter tile of the grid is taken in place");
        stacker.RebuildCauses.ReferenceAged.ShouldBeGreaterThanOrEqualTo(1, "one beyond it folds the window again");
    }

    /// <summary>
    /// A planet drifting slowly across the field, its reference re-taken in place again and again (#1319): every frame is moved onto the
    /// FIRST reference's grid, so the master's disk stays where that reference had it, however far the planet has drifted since.
    /// </summary>
    [Fact]
    public async Task ADriftingPlanetStacksOnTheFirstReferencesGridAcrossReReferences()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 96;
        var frames = new float[40][,];
        var centres = new double[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            // 0.4 px a frame, 15.6 px over the run, within a quarter of the 64 px tile; frame 0 the sharpest, so it is the first reference.
            var frame = new float[size, size];
            double cx = 32 + (0.4 * i), cy = size / 2.0;
            centres[i] = cx;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    frame[y, x] = (dx * dx) + (dy * dy) < 12 * 12
                        ? (float)(0.5 + (0.25 * Math.Sin(dx * 0.6) * Math.Cos(dy * 0.55)))
                        : 0.03f;
                }
            }
            frames[i] = BoxBlur(frame, i == 0 ? 0 : 1);
        }
        var stacker = new RollingWindowStacker(new InMemoryFrameStream(frames),
            new RollingWindowOptions { FallbackWindowFrames = 6, MaxWindowFrames = 6, KeepFraction = 1, ReReferenceInPlace = true });
        Image? master = null;
        for (var f = 0; f < frames.Length; f++)
        {
            master?.Release();
            master = await stacker.StackToAsync(f, ct);
        }

        stacker.ReReferences.ShouldBeGreaterThanOrEqualTo(3, "the reference is re-taken in place as the window slides");
        stacker.RebuildCauses.ReferenceAged.ShouldBe(0, "the drift stays within a quarter tile");
        var (masterX, _) = Centroid(master.ShouldNotBeNull());
        TestContext.Current.TestOutputHelper?.WriteLine($"master's disk at x {masterX:0.00}; the first reference's at {centres[0]:0.00}, the last frame's at {centres[^1]:0.00}");
        masterX.ShouldBe(centres[0], 0.5, "every frame moved onto the first reference's grid");
        // The columns only the earliest frames reached are uncovered once those frames are evicted: what rounding leaves of their weight
        // and sum is no measurement, so the master holds nothing there outside the frames' own range (#1319: -3.17 and 1.16 on the twin).
        var (low, high) = (float.MaxValue, float.MinValue);
        for (var y = 0; y < master.Height; y++)
        {
            for (var x = 0; x < master.Width; x++)
            {
                (low, high) = (MathF.Min(low, master[0, y, x]), MathF.Max(high, master[0, y, x]));
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"master's range {low:G4} to {high:G4}");
        low.ShouldBeGreaterThanOrEqualTo(0f, "no pixel below the frames' darkest");
        high.ShouldBeLessThanOrEqualTo(0.76f, "no pixel above the frames' brightest");
        master.Release();
    }

    /// <summary>
    /// The edge columns only the earliest frames reached (#1319): a planet drifting slowly, its best quarter folded and its reference
    /// re-taken in place, so the window is never folded again from nothing. Once those frames are evicted, what rounding leaves of the
    /// columns' weight and sum is no measurement, and the master holds nothing there outside the frames' own range. On the twin it held
    /// -3.17 and 1.16 against frames between 0.07 and 0.3.
    /// </summary>
    [Fact]
    public async Task AnEdgeOnlyEvictedFramesReachedHoldsNothingOutsideTheFramesRange()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 96;
        var random = new Random(1319);
        var frames = new float[300][,];
        var (low, high) = (float.MaxValue, float.MinValue);
        for (var i = 0; i < frames.Length; i++)
        {
            // 0.06 px a frame, 18 px over the run; seeded noise, so the frames' weights and values differ as a real capture's do.
            var frame = new float[size, size];
            double cx = 30 + (0.06 * i), cy = size / 2.0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    var signal = (dx * dx) + (dy * dy) < 12 * 12 ? 0.5 + (0.25 * Math.Sin(dx * 0.6) * Math.Cos(dy * 0.55)) : 0.03;
                    frame[y, x] = (float)(signal + (0.01 * (random.NextDouble() - 0.5)));
                }
            }
            frames[i] = BoxBlur(frame, random.Next(0, 3));
            foreach (var v in frames[i])
            {
                (low, high) = (MathF.Min(low, v), MathF.Max(high, v));
            }
        }
        var stacker = new RollingWindowStacker(new InMemoryFrameStream(frames),
            new RollingWindowOptions { FallbackWindowFrames = 80, MaxWindowFrames = 80, KeepFraction = 0.25, ReReferenceInPlace = true });
        var (masterLow, masterHigh) = (float.MaxValue, float.MinValue);
        for (var f = 9; f < frames.Length; f += 10)
        {
            var master = await stacker.StackToAsync(f, ct);
            for (var y = 0; y < master.Height; y++)
            {
                for (var x = 0; x < master.Width; x++)
                {
                    // An uncovered pixel reads 0, the master's own mark for no coverage.
                    if (master[0, y, x] is var v && v != 0f)
                    {
                        (masterLow, masterHigh) = (MathF.Min(masterLow, v), MathF.Max(masterHigh, v));
                    }
                }
            }
            master.Release();
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"frames {low:G4} to {high:G4}; masters {masterLow:G4} to {masterHigh:G4}; {stacker.ReReferences} re-references, {stacker.Rebuilds} rebuilds");
        stacker.ReReferences.ShouldBeGreaterThanOrEqualTo(2, "the reference is re-taken in place");
        masterLow.ShouldBeGreaterThanOrEqualTo(low, "no master pixel below the frames' darkest");
        masterHigh.ShouldBeLessThanOrEqualTo(high, "no master pixel above the frames' brightest");
    }

    // The centroid of a mono image's pixels above half its peak.
    private static (double X, double Y) Centroid(Image image)
    {
        var (sx, sy, sw) = (0.0, 0.0, 0.0);
        var peak = image.MaxValue;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var v = image[0, y, x];
                if (v > 0.5f * peak)
                {
                    (sx, sy, sw) = (sx + (x * v), sy + (y * v), sw + v);
                }
            }
        }
        return (sx / sw, sy / sw);
    }

    [Fact]
    public async Task Incremental_slide_matches_a_fresh_rebuild_of_the_same_window()
    {
        // Window of 6 frames (frame-count fallback). Path A slides 5 -> 8 (evict 0,1,2 + add 6,7,8); path B
        // rebuilds [3..8] from scratch. Both end aligned to frame 5 (the sharpest, present in both windows),
        // so the running sum A reconstructs by add+evict must match B's fresh integral to FP rounding.
        // Every frame folded: a stack keeping a share chooses as frames arrive, which a fresh rebuild of the window does not.
        var opts = new RollingWindowOptions { FallbackWindowFrames = 6, KeepFraction = 1 };

        var streamA = new InMemoryFrameStream(SharpestAtFive());
        var a = new RollingWindowStacker(streamA, opts);
        await a.StackToAsync(5, TestContext.Current.CancellationToken);
        var masterA = await a.StackToAsync(8, TestContext.Current.CancellationToken);

        var streamB = new InMemoryFrameStream(SharpestAtFive());
        var b = new RollingWindowStacker(streamB, opts);
        var masterB = await b.StackToAsync(8, TestContext.Current.CancellationToken);

        a.WindowStart.ShouldBe(3);
        a.WindowEnd.ShouldBe(8);
        a.ReferenceIndex.ShouldBe(5);
        b.WindowStart.ShouldBe(3);
        b.ReferenceIndex.ShouldBe(5);

        MeanAbsDiff(masterA, masterB, new PixelRect(6, 6, 28, 28)).ShouldBeLessThan(1e-3);
    }

    [Fact]
    public async Task Time_based_window_start_spans_the_configured_duration()
    {
        // Frames 1 s apart; a 5 s window ending at frame 8 covers frames 3..8 (t8 - t3 = 5 s).
        var t0 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ts = new DateTimeOffset[10];
        for (var i = 0; i < 10; i++) ts[i] = t0.AddSeconds(i);

        var stacker = new RollingWindowStacker(new InMemoryFrameStream(SharpestAtFive(), ts),
            new RollingWindowOptions { WindowDuration = TimeSpan.FromSeconds(5) });

        stacker.ComputeWindowStart(8).ShouldBe(3);
        stacker.ComputeWindowStart(2).ShouldBe(0); // clamps at the start of the stream
    }

    [Fact]
    public void Frame_count_fallback_applies_when_untimed()
    {
        var stacker = new RollingWindowStacker(new InMemoryFrameStream(SharpestAtFive()),
            new RollingWindowOptions { FallbackWindowFrames = 4 });

        stacker.ComputeWindowStart(8).ShouldBe(5); // 8 - 4 + 1
        stacker.ComputeWindowStart(1).ShouldBe(0);
    }

    [Fact]
    public async Task Master_reconstructs_the_bright_disk()
    {
        var stacker = new RollingWindowStacker(new InMemoryFrameStream(SharpestAtFive()),
            new RollingWindowOptions { FallbackWindowFrames = 6 });

        var master = await stacker.StackToAsync(8, TestContext.Current.CancellationToken);

        master.ChannelCount.ShouldBe(1);
        master.Width.ShouldBe(N);
        master[0, N / 2, N / 2].ShouldBeGreaterThan(0.3f); // disk centre is bright
        master[0, 1, 1].ShouldBeLessThan(0.1f);             // corner sky stays dark
    }

    [Fact]
    public async Task Backward_jump_rebuilds_the_window()
    {
        var stacker = new RollingWindowStacker(new InMemoryFrameStream(SharpestAtFive()),
            new RollingWindowOptions { FallbackWindowFrames = 6 });

        await stacker.StackToAsync(8, TestContext.Current.CancellationToken);
        var master = await stacker.StackToAsync(2, TestContext.Current.CancellationToken);

        stacker.WindowEnd.ShouldBe(2);
        stacker.WindowStart.ShouldBe(0); // max(0, 2 - 6 + 1)
        master[0, N / 2, N / 2].ShouldBeGreaterThan(0.3f); // still a covered disk after the rebuild
    }

    [Fact]
    public async Task Published_mono_master_stays_valid_after_the_next_publish()
    {
        // Guards the BuildMasterAsync destination rule: for mono/RGB the normalise destination IS the
        // returned master (MergeAndDemosaicAsync passes it through), so consecutive publishes must own
        // INDEPENDENT arrays -- the viewer / wavelet re-sharpen may still hold the previous master while
        // the next one is built. Routing mono through the split-CFA _sumScratch would make masterA alias
        // masterB's build and this test would see masterA's pixels change under it.
        var stacker = new RollingWindowStacker(new InMemoryFrameStream(SharpestAtFive()),
            new RollingWindowOptions { FallbackWindowFrames = 6 });

        var masterA = await stacker.StackToAsync(5, TestContext.Current.CancellationToken);
        var centreBefore = masterA[0, N / 2, N / 2];
        var skyBefore = masterA[0, 1, 1];

        var masterB = await stacker.StackToAsync(8, TestContext.Current.CancellationToken);

        masterB.GetChannelArray(0).ShouldNotBeSameAs(masterA.GetChannelArray(0));
        masterA[0, N / 2, N / 2].ShouldBe(centreBefore); // untouched by the second publish
        masterA[0, 1, 1].ShouldBe(skyBefore);
    }
}
