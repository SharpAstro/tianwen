using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class LiveCameraFrameStreamTests
{
    private const int N = 32;

    private static Image ConstantMono(int n, float value)
    {
        var a = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                a[y, x] = value;
            }
        }

        return Image.FromChannel(a, 1f, 0f);
    }

    private static Image ConstantMonoAdu(int n, float aduValue, float maxAdu)
    {
        var a = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                a[y, x] = aduValue;
            }
        }

        return Image.FromChannel(a, maxAdu, 0f);
    }

    private static Image Disk(int n)
    {
        var a = new float[n, n];
        double cx = n / 2.0, cy = n / 2.0, r = n * 0.38;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                a[y, x] = (dx * dx) + (dy * dy) < r * r ? 0.6f : 0.03f;
            }
        }

        return Image.FromChannel(a, 1f, 0f);
    }

    [Fact]
    public async Task Push_grows_FrameCount_and_loads_pushed_data()
    {
        using var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono);
        stream.FrameCount.ShouldBe(0);
        stream.LatestIndex.ShouldBe(-1);

        for (var i = 0; i < 5; i++)
        {
            stream.Push(ConstantMono(N, i * 0.1f));
        }

        stream.FrameCount.ShouldBe(5);
        stream.LatestIndex.ShouldBe(4);

        for (var i = 0; i < 5; i++)
        {
            var frame = await stream.LoadAsync(i, TestContext.Current.CancellationToken);
            frame.ChannelCount.ShouldBe(1);
            frame.Width.ShouldBe(N);
            frame[0, N / 2, N / 2].ShouldBe(i * 0.1f, 1e-6f);
        }
    }

    [Fact]
    public async Task Push_deep_copies_so_the_source_can_be_mutated_after()
    {
        using var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono);

        var backing = new float[N, N];
        backing[5, 5] = 0.42f;
        var source = Image.FromChannel(backing, 1f, 0f);
        stream.Push(source);

        // The camera would recycle / overwrite its buffer for the next frame. Mutating the source's array
        // here must NOT change what the ring holds (Push deep-copies).
        backing[5, 5] = 999f;

        var loaded = await stream.LoadAsync(0, TestContext.Current.CancellationToken);
        loaded[0, 5, 5].ShouldBe(0.42f, 1e-6f);
    }

    [Fact]
    public async Task Push_normalises_an_ADU_frame_to_unit_range()
    {
        // A live camera delivers ADU (MaxValue = sensor full-scale), but the planetary stack pipeline works
        // in [0,1] (PlanetaryMaster.NormalizeInPlace declares the master MaxValue = 1, and the SER bridge
        // decodes raw frames to [0,1]). The stream normalises on copy so the coverage-normalised master
        // isn't left holding ADU values that the viewer then clamps to a flat white frame.
        using var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono);
        const float maxAdu = 65535f;
        stream.Push(ConstantMonoAdu(N, 0.6f * maxAdu, maxAdu));

        var loaded = await stream.LoadAsync(0, TestContext.Current.CancellationToken);
        loaded.MaxValue.ShouldBe(1f);
        loaded[0, N / 2, N / 2].ShouldBe(0.6f, 1e-4f); // 0.6 * MaxADU, scaled back into [0,1]
    }

    [Fact]
    public async Task Old_frames_evict_when_capacity_is_exceeded()
    {
        using var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono, capacity: 4);

        for (var i = 0; i < 6; i++)
        {
            stream.Push(ConstantMono(N, i * 0.1f));
        }

        stream.FrameCount.ShouldBe(6);          // grows past capacity
        stream.Capacity.ShouldBe(4);

        // Retained = the last 4: [2, 5]. The rolled-out frames throw.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await stream.LoadAsync(0, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await stream.LoadAsync(1, TestContext.Current.CancellationToken));

        for (var i = 2; i < 6; i++)
        {
            var frame = await stream.LoadAsync(i, TestContext.Current.CancellationToken);
            frame[0, 0, 0].ShouldBe(i * 0.1f, 1e-6f); // still the right frame, no aliasing across the wrap
        }
    }

    [Fact]
    public void Timestamps_round_trip_and_untimed_returns_null()
    {
        var t0 = new DateTimeOffset(2026, 6, 24, 22, 0, 0, TimeSpan.Zero);
        using var timed = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono, hasTimestamps: true);
        timed.Push(ConstantMono(N, 0.1f), t0);
        timed.Push(ConstantMono(N, 0.2f), t0.AddSeconds(1));
        timed.HasTimestamps.ShouldBeTrue();
        timed.TimestampOf(0).ShouldBe(t0);
        timed.TimestampOf(1).ShouldBe(t0.AddSeconds(1));
        timed.TimestampOf(99).ShouldBeNull(); // out of range

        using var untimed = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono, hasTimestamps: false);
        untimed.Push(ConstantMono(N, 0.1f), t0);
        untimed.HasTimestamps.ShouldBeFalse();
        untimed.TimestampOf(0).ShouldBeNull();
    }

    [Fact]
    public void Wrong_size_push_throws()
    {
        using var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono);
        Should.Throw<ArgumentException>(() => stream.Push(ConstantMono(N + 2, 0.1f)));
    }

    [Fact]
    public void Push_after_dispose_throws()
    {
        var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono);
        stream.Dispose();
        Should.Throw<ObjectDisposedException>(() => stream.Push(ConstantMono(N, 0.1f)));
    }

    [Fact]
    public async Task Empty_stream_makes_StackToAsync_throw()
    {
        // The RollingWindowStacker requires a non-empty stream; LiveStackPreviewSource guards against
        // kicking a stack before the first live frame. This pins the invariant the guard relies on.
        using var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono);
        var stacker = new RollingWindowStacker(stream);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await stacker.StackToAsync(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Drives_the_rolling_window_stacker_end_to_end()
    {
        // The whole point of Phase A: a live camera push-stream feeds the existing stacker unchanged.
        using var stream = new LiveCameraFrameStream(N, N, PlanetaryFrameLayout.Mono);
        for (var i = 0; i < 8; i++)
        {
            stream.Push(Disk(N));
        }

        var stacker = new RollingWindowStacker(stream, new RollingWindowOptions { FallbackWindowFrames = 6 });
        var master = await stacker.StackToAsync(stream.LatestIndex, TestContext.Current.CancellationToken);

        master.ChannelCount.ShouldBe(1);
        master.Width.ShouldBe(N);
        master[0, N / 2, N / 2].ShouldBeGreaterThan(0.3f); // stacked disk centre is bright
        master[0, 1, 1].ShouldBeLessThan(0.1f);             // corner sky stays dark
    }
}
