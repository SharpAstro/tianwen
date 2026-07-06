using System;
using System.Drawing;
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
        var cur = (float[,])src.Clone();
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

    private static double MeanAbsDiff(Image a, Image b, Rectangle region)
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

    private sealed class FakeFrameStream(float[][,] frames, DateTimeOffset[]? timestamps = null) : IPlanetaryFrameStream
    {
        public int LoadCount { get; private set; }
        public int FrameCount => frames.Length;
        public int Width => frames[0].GetLength(1);
        public int Height => frames[0].GetLength(0);
        public PlanetaryFrameLayout Layout => PlanetaryFrameLayout.Mono;
        public bool HasTimestamps => timestamps is not null;
        public DateTimeOffset? TimestampOf(int index)
            => timestamps is { } ts && (uint)index < (uint)ts.Length ? ts[index] : null;

        public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            // Clone so the stacker's Release()/recycle never touches our backing frame data.
            return ValueTask.FromResult(Image.FromChannel((float[,])frames[index].Clone(), 1f, 0f));
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Incremental_slide_matches_a_fresh_rebuild_of_the_same_window()
    {
        // Window of 6 frames (frame-count fallback). Path A slides 5 -> 8 (evict 0,1,2 + add 6,7,8); path B
        // rebuilds [3..8] from scratch. Both end aligned to frame 5 (the sharpest, present in both windows),
        // so the running sum A reconstructs by add+evict must match B's fresh integral to FP rounding.
        var opts = new RollingWindowOptions { FallbackWindowFrames = 6 };

        var streamA = new FakeFrameStream(SharpestAtFive());
        var a = new RollingWindowStacker(streamA, opts);
        await a.StackToAsync(5, TestContext.Current.CancellationToken);
        var masterA = await a.StackToAsync(8, TestContext.Current.CancellationToken);

        var streamB = new FakeFrameStream(SharpestAtFive());
        var b = new RollingWindowStacker(streamB, opts);
        var masterB = await b.StackToAsync(8, TestContext.Current.CancellationToken);

        a.WindowStart.ShouldBe(3);
        a.WindowEnd.ShouldBe(8);
        a.ReferenceIndex.ShouldBe(5);
        b.WindowStart.ShouldBe(3);
        b.ReferenceIndex.ShouldBe(5);

        MeanAbsDiff(masterA, masterB, new Rectangle(6, 6, 28, 28)).ShouldBeLessThan(1e-3);
    }

    [Fact]
    public async Task Time_based_window_start_spans_the_configured_duration()
    {
        // Frames 1 s apart; a 5 s window ending at frame 8 covers frames 3..8 (t8 - t3 = 5 s).
        var t0 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ts = new DateTimeOffset[10];
        for (var i = 0; i < 10; i++) ts[i] = t0.AddSeconds(i);

        var stacker = new RollingWindowStacker(new FakeFrameStream(SharpestAtFive(), ts),
            new RollingWindowOptions { WindowDuration = TimeSpan.FromSeconds(5) });

        stacker.ComputeWindowStart(8).ShouldBe(3);
        stacker.ComputeWindowStart(2).ShouldBe(0); // clamps at the start of the stream
    }

    [Fact]
    public void Frame_count_fallback_applies_when_untimed()
    {
        var stacker = new RollingWindowStacker(new FakeFrameStream(SharpestAtFive()),
            new RollingWindowOptions { FallbackWindowFrames = 4 });

        stacker.ComputeWindowStart(8).ShouldBe(5); // 8 - 4 + 1
        stacker.ComputeWindowStart(1).ShouldBe(0);
    }

    [Fact]
    public async Task Master_reconstructs_the_bright_disk()
    {
        var stacker = new RollingWindowStacker(new FakeFrameStream(SharpestAtFive()),
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
        var stacker = new RollingWindowStacker(new FakeFrameStream(SharpestAtFive()),
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
        var stacker = new RollingWindowStacker(new FakeFrameStream(SharpestAtFive()),
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
