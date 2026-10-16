using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a planetary capture and the node's live stack allocate, attributed by type and by heap (TIA2's capture-priority leg:
/// through the node under a saturated box, 3 to 9 gen 2 collections a capture held the reader up, against 0 to 4 in process).
/// The node's own shape, in process over the fake camera at the Saturn window's size: the capture loop with its ring, a frame
/// sampled every 33 ms as the node's live view does, and the live stack's master again and again over the ring, as
/// <see cref="LiveStackLoop"/> stacks it. The runtime's own allocation ticks (one about every 100 KB, with its type and whether
/// it went to the large object heap) attribute the bytes. Opt-in: set <c>TIANWEN_ALLOCATION_PROBE=1</c>.
/// </summary>
[Collection("Session")]
public class PlanetaryNodeAllocationProbe(ITestOutputHelper output)
{
    [Fact(Timeout = 600_000)]
    public async Task WhatTheCaptureAndTheLiveStackAllocate()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_ALLOCATION_PROBE") == "1", "TIANWEN_ALLOCATION_PROBE is not 1");
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 16, 22, 0, 0, TimeSpan.Zero));
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), new FakeExternal(output, time).BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.NumX = 640;
        camera.NumY = 480;
        var options = new RollingWindowOptions();
        var sampler = new FrameSampler(time, TimeSpan.FromMilliseconds(33), "allocation probe");
        await using var capture = new PlanetaryCapture(time, NullLogger.Instance, options, onFrame: frame =>
        {
            if (sampler.TrySample(frame, time.GetUtcNow(), out var sample))
            {
                sample.Release();
            }
        });
        capture.ArmFrameGate();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
        await StepAsync(capture, 1, ct);
        var capacity = capture.Stream.ShouldNotBeNull().Capacity;

        // While the ring fills (a new capture's first frames), and once it is full and recycles its evicted frames' planes.
        using var ticks = new AllocationTicks();
        var from = GC.GetTotalAllocatedBytes(precise: true);
        await StepAsync(capture, capacity, ct);
        var fillBytes = GC.GetTotalAllocatedBytes(precise: true) - from;
        var fillTicks = ticks.TakeAndClear();
        output.WriteLine($"a new capture's first {capacity} frames at 640x480, its ring filling: {fillBytes / 1048576.0:F0} MB, {fillBytes / 1024.0 / capacity:F1} KB a frame");
        Report(fillTicks);

        from = GC.GetTotalAllocatedBytes(precise: true);
        const int Frames = 2_000;
        await StepAsync(capture, Frames, ct);
        var captureBytes = GC.GetTotalAllocatedBytes(precise: true) - from;
        var captureTicks = ticks.TakeAndClear();

        var stream = capture.Stream.ShouldNotBeNull();
        var stacker = new RollingWindowStacker(stream, options);
        (await stacker.StackToAsync(stream.LatestIndex, ct)).Release(); // its accumulators warm
        from = GC.GetTotalAllocatedBytes(precise: true);
        const int Masters = 10;
        for (var master = 0; master < Masters; master++)
        {
            await StepAsync(capture, 200, ct);
            (await stacker.StackToAsync(stream.LatestIndex, ct)).Release();
        }
        var stackBytes = GC.GetTotalAllocatedBytes(precise: true) - from;
        var stackTicks = ticks.TakeAndClear();
        await capture.StopAsync(ct);

        output.WriteLine($"then, the ring full, {Frames} frames with a 33 ms view sample: {captureBytes / 1024.0 / Frames:F1} KB a frame");
        Report(captureTicks);
        output.WriteLine($"the live stack, {Masters} masters over a {stacker.WindowFrameCount}-frame window (and 200 more frames each): "
            + $"{stackBytes / 1048576.0 / Masters:F1} MB a master");
        Report(stackTicks);
    }

    private void Report(ConcurrentDictionary<(string Type, bool Large), long> ticks)
    {
        foreach (var ((type, large), bytes) in ticks.OrderByDescending(static t => t.Value).Take(12))
        {
            output.WriteLine($"  {(large ? "LOH" : "SOH")} {bytes / 1048576.0,8:F1} MB  {type}");
        }
    }

    private static async Task StepAsync(PlanetaryCapture capture, int frames, CancellationToken ct)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }
    }

    /// <summary>The runtime's GCAllocationTick events (about one per 100 KB allocated), summed by type and heap.</summary>
    private sealed class AllocationTicks : EventListener
    {
        private ConcurrentDictionary<(string, bool), long> _ticks = new();

        public ConcurrentDictionary<(string Type, bool Large), long> TakeAndClear() => Interlocked.Exchange(ref _ticks, new());

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // GC
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || e.Payload is not { } payload || e.PayloadNames is not { } names)
            {
                return;
            }
            var kind = Convert.ToInt32(payload[names.IndexOf("AllocationKind")]);
            var amount = names.IndexOf("AllocationAmount64") is >= 0 and var at ? Convert.ToInt64(payload[at]) : Convert.ToInt64(payload[names.IndexOf("AllocationAmount")]);
            var type = names.IndexOf("TypeName") is >= 0 and var t ? payload[t] as string ?? "?" : "?";
            _ticks.AddOrUpdate((type, kind == 1), amount, (_, sum) => sum + amount);
        }
    }
}
