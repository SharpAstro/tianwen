using NSubstitute;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A guide loop stopped with its exposure in flight hands the camera back idle (#1945, #2000): the exposure is aborted before
/// the cancellation propagates, so the next consumer's first StartExposureAsync does not meet a busy camera.
/// </summary>
public class GuideFrameCancelTests
{
    [Fact(Timeout = 30_000)]
    public async Task A_guide_frame_cancelled_mid_exposure_aborts_the_exposure_before_the_cancellation_propagates()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.CanAbortExposure.Returns(true);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        camera.StartExposureAsync(Arg.Any<TimeSpan>(), Arg.Any<FrameType>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The stop arrives while the exposure runs.
                cancelled.Cancel();
                return ValueTask.FromResult(DateTimeOffset.UnixEpoch);
            });

        await Should.ThrowAsync<OperationCanceledException>(async () => await BuiltInGuiderDriver.CaptureGuideFrameAsync(
            camera, TimeSpan.FromSeconds(2), new FakeTimeProviderWrapper(), TimeSpan.FromMilliseconds(10), cancelled.Token));

        await camera.Received(1).AbortExposureAsync(Arg.Any<CancellationToken>());
    }
}
