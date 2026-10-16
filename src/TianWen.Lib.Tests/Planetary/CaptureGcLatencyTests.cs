using System;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A planetary capture holds the process's GC latency mode at SustainedLowLatency while it runs (<see cref="CaptureGcLatency"/>,
/// TIA2), and the mode found before is put back when the last capture holding it ends, on every path out of its loop.
/// </summary>
public class CaptureGcLatencyTests
{
    [Fact]
    public void TheModeFoundIsPutBackOnlyWhenTheLastHolderLetsGo()
    {
        var mode = GCLatencyMode.Batch;
        var latency = new CaptureGcLatency(() => mode, set => mode = set);

        var first = latency.Hold();
        mode.ShouldBe(GCLatencyMode.SustainedLowLatency);
        var second = latency.Hold();
        first.Dispose();
        mode.ShouldBe(GCLatencyMode.SustainedLowLatency, "a second capture still runs");
        first.Dispose();
        latency.Holders.ShouldBe(1, "a hold let go twice counts once");

        second.Dispose();
        mode.ShouldBe(GCLatencyMode.Batch, "the mode found before the first capture");
        latency.Holders.ShouldBe(0);
    }

    [Fact(Timeout = 30_000)]
    public async Task ACaptureHoldsTheModeWhileItRunsAndLetsGoAsItStops()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = new PlanetaryCapture(SystemTimeProvider.Instance, NullLogger.Instance);
        capture.Start(Camera(token => NoFrameEverAsync(started, token)), new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
        await started.Task.WaitAsync(ct);

        capture.HoldsGcLatency.ShouldBeTrue();
        GCSettings.LatencyMode.ShouldBe(GCLatencyMode.SustainedLowLatency);

        await capture.StopAsync(ct);
        capture.HoldsGcLatency.ShouldBeFalse("a stop is a path out");
    }

    [Fact(Timeout = 30_000)]
    public async Task ACaptureWhoseCameraFailsLetsGoAndALiveViewNeverHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var capture = new PlanetaryCapture(SystemTimeProvider.Instance, NullLogger.Instance))
        {
            capture.Start(Camera(_ => FailingAsync()), new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
            await capture.Completion.WaitAsync(ct);

            capture.FailureReason.ShouldNotBeNull("premise: the loop faulted");
            capture.HoldsGcLatency.ShouldBeFalse("a fault is a path out");
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var view = new PlanetaryCapture(SystemTimeProvider.Instance, NullLogger.Instance, kind: LiveCaptureKind.LiveView);
        view.Start(Camera(token => NoFrameEverAsync(started, token)), new VideoCaptureOptions(TimeSpan.FromMilliseconds(5)), ct).ShouldBeTrue();
        await started.Task.WaitAsync(ct);
        view.HoldsGcLatency.ShouldBeFalse("a live view keeps no frames and holds nothing");
        await view.StopAsync(ct);
    }

    private static IVideoCameraDriver Camera(Func<CancellationToken, IAsyncEnumerable<Image>> frames)
    {
        var camera = Substitute.For<IVideoCameraDriver>();
        camera.CanVideoCapture.Returns(true);
        camera.CaptureVideoAsync(Arg.Any<VideoCaptureOptions>(), Arg.Any<CancellationToken>()).Returns(call => frames(call.Arg<CancellationToken>()));
        return camera;
    }

    private static async IAsyncEnumerable<Image> NoFrameEverAsync(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult();
        await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<Image> FailingAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("the camera stopped");
#pragma warning disable CS0162 // an iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }
}
