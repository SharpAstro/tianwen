using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A planetary capture's interlude (<see cref="PlanetaryCapture.InterludeAsync"/>): the stream stopped between two frames, a
/// caller's action run, and the stream started again at the settings it had or a transform's, its first frames dropped. The
/// one routine a filter change (#1618), a sequence's step (#1340) and a full-sensor still (#1033) go through. Over the fake
/// camera's video, the test stepping the loop one frame at a time through its frame gate.
/// </summary>
[Collection("Session")]
public class PlanetaryCaptureInterludeTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static readonly Uri CameraUri = new Uri("fake://camera/8");

    private async Task<FakeCameraDriver> CameraAsync(CancellationToken ct)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero));
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), new FakeExternal(output, timeProvider).BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.NumX = 128;
        camera.NumY = 128;
        camera.PlanetRadiusPixels = 40;
        return camera;
    }

    /// <summary>A claim on the camera whose one lease counts how often it is given back.</summary>
    private sealed class CountingLease : IDisposable
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private static DeviceLeaseSet Claim(CountingLease lease)
    {
        var hub = Substitute.For<IDeviceHub>();
        hub.TryAcquireLease(CameraUri, PlanetaryCapture.LeaseOwner, out Arg.Any<IDisposable?>()).Returns(call =>
        {
            call[2] = lease;
            return true;
        });
        DeviceLeaseSet.TryAcquire(hub, [CameraUri], PlanetaryCapture.LeaseOwner, out var claim, out _).ShouldBeTrue();
        return claim;
    }

    private static PlanetaryCapture NewCapture(Action<Image>? onFrame = null)
    {
        var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 16 }, onFrame);
        capture.ArmFrameGate();
        return capture;
    }

    // Lets the loop take one frame and waits until it is whole: pushed, or dropped, and signalled.
    private static async Task StepAsync(PlanetaryCapture capture, int frames, CancellationToken ct)
    {
        for (var i = 0; i < frames; i++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }
    }

    // Steps the loop until `done` has completed, a frame at a time, within a bound the caller's assertion reports.
    private static async Task<int> StepUntilAsync(PlanetaryCapture capture, Task done, CancellationToken ct, int maxFrames = 50)
    {
        var stepped = 0;
        while (!done.IsCompleted && stepped < maxFrames)
        {
            await StepAsync(capture, 1, ct);
            stepped++;
        }
        return stepped;
    }

    private static PlanetaryInterlude Nothing(string what = "a test") => new PlanetaryInterlude(what, _ => ValueTask.CompletedTask);

    [Fact(Timeout = 30_000)]
    public async Task TheCaptureKeepsItsClaimThroughAnInterludeAndGivesItBackOnceAsItEnds()
    {
        // The loop's end gives the camera back. An interlude ends the stream's enumeration, never the loop, so the claim must
        // be held across it: let go there, a flat run could take the camera from under the restart; let go twice, a second
        // owner's claim could be dropped by the first's.
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        var lease = new CountingLease();
        await using var capture = NewCapture();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct, Claim(lease)).ShouldBeTrue();
        await StepAsync(capture, 3, ct);

        var givenBackDuring = -1;
        var capturingDuring = false;
        var interlude = capture.InterludeAsync(new PlanetaryInterlude("a test", _ =>
        {
            givenBackDuring = lease.Disposals;
            capturingDuring = capture.IsCapturing;
            return ValueTask.CompletedTask;
        }), ct);
        await StepUntilAsync(capture, interlude, ct);

        (await interlude).ShouldBeNull("the stream is back");
        givenBackDuring.ShouldBe(0, "the camera stays the capture's while its stream is stopped");
        capturingDuring.ShouldBeTrue("an interlude is part of the capture, not its end");
        lease.Disposals.ShouldBe(0, "nor once the stream is back");

        var before = capture.FramesReceived;
        await StepAsync(capture, 3, ct);
        capture.FramesReceived.ShouldBe(before + 3, "the capture goes on after it");
        camera.VideoStarts.Length.ShouldBe(2, "the stream was stopped and started once");

        await capture.StopAsync(ct);
        capture.IsCapturing.ShouldBeFalse();
        lease.Disposals.ShouldBe(1, "the capture ends normally and gives the camera back");
        await capture.DisposeAsync();
        lease.Disposals.ShouldBe(1, "once");
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterludeStartsTheStreamAgainAtTheSettingsItHadItsLiveControlsIncluded()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = NewCapture();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2), Gain: 50), ct).ShouldBeTrue();
        await StepAsync(capture, 2, ct);
        // Live controls a client turned after the start: the stream it starts again with is the one it was running.
        capture.SetExposure(TimeSpan.FromMilliseconds(3));
        capture.SetGain(80);
        await StepAsync(capture, 2, ct);

        var interlude = capture.InterludeAsync(Nothing(), ct);
        await StepUntilAsync(capture, interlude, ct);
        (await interlude).ShouldBeNull();

        camera.VideoStarts.Length.ShouldBe(2);
        var (options, width, height, bin) = camera.VideoStarts[1];
        output.WriteLine($"started again at {options}, {width}x{height} bin {bin}");
        (options.Exposure, options.Gain).ShouldBe((TimeSpan.FromMilliseconds(3), (short)80));
        (width, height, bin).ShouldBe((camera.VideoStarts[0].Width, camera.VideoStarts[0].Height, camera.VideoStarts[0].Bin));
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterludeStartsTheStreamAgainAtWhatItsTransformReturns()
    {
        // A full-sensor still (#1033) comes back at a window it proposes; a step of a sequence (#1340) at its own exposure.
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = NewCapture();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        await StepAsync(capture, 2, ct);
        capture.Stream.ShouldNotBeNull().Width.ShouldBe(128);

        PlanetaryStreamSettings? asked = null;
        var interlude = capture.InterludeAsync(Nothing() with
        {
            RestartAt = had =>
            {
                asked = had;
                return had with { RoiWidth = 64, RoiHeight = 48, Options = had.Options with { Exposure = TimeSpan.FromMilliseconds(4) } };
            },
        }, ct);
        await StepUntilAsync(capture, interlude, ct);
        (await interlude).ShouldBeNull();

        asked.ShouldNotBeNull().RoiWidth.ShouldBe(128, "the transform is given the settings the stream had");
        var (options, width, height, bin) = camera.VideoStarts[1];
        (options.Exposure, width, height, bin).ShouldBe((TimeSpan.FromMilliseconds(4), 64, 48, (short)1));
        await StepAsync(capture, 1, ct);
        var stream = capture.Stream.ShouldNotBeNull();
        (stream.Width, stream.Height).ShouldBe((64, 48), "the ring follows the new window");
    }

    [Fact(Timeout = 30_000)]
    public async Task TheFirstFramesAfterTheRestartReachNoRingNoHostAndNoRecording()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        var shown = 0;
        await using var capture = NewCapture(onFrame: _ => Interlocked.Increment(ref shown));
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        await StepAsync(capture, 3, ct);
        var (received, latest, seen) = (capture.FramesReceived, capture.Stream.ShouldNotBeNull().LatestIndex, Volatile.Read(ref shown));

        // A recording asked for while the stream is stopped is refused until the stream is back past the dropped frames.
        var path = Path.Combine(_folders.Create("twint").FullName, "during.ser");
        string? refusedDuring = null;
        var interlude = capture.InterludeAsync(new PlanetaryInterlude("a test", stopped =>
        {
            capture.TryStartRecording(path, TimeSpan.FromHours(1), out _, out var refused).ShouldBeFalse();
            refusedDuring = refused;
            return ValueTask.CompletedTask;
        }) { DropFrames = 3 }, ct);
        // One step: the interlude is taken as the loop asks for its next frame, the stream starts again, and its first three
        // frames are dropped on the way to the one it keeps.
        await StepAsync(capture, 1, ct);
        (await interlude).ShouldBeNull("the stream is back at the first frame it keeps");

        refusedDuring.ShouldBe("The planetary capture has stopped its stream for a test; record once it streams again");
        capture.FramesDroppedAfterInterludes.ShouldBe(3);
        capture.FramesReceived.ShouldBe(received + 1, "of the four frames read since, only the one kept reached the ring");
        capture.Stream.ShouldNotBeNull().LatestIndex.ShouldBe(latest + 1);
        Volatile.Read(ref shown).ShouldBe(seen + 1, "and the host's live view");
        File.Exists(path).ShouldBeFalse("and no recording was made of any");

        capture.TryStartRecording(path, TimeSpan.FromHours(1), out var recording, out var refusal).ShouldBeTrue(refusal);
        await StepAsync(capture, 2, ct);
        await capture.StopAsync(ct);
        await recording.Completion.WaitAsync(ct);
        recording.FramesWritten.ShouldBe(2, "a recording made after it has the frames after it alone");
    }

    [Fact(Timeout = 30_000)]
    public async Task ARecordingRefusesAnInterludeInWordsAndTheStreamGoesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = NewCapture();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        var path = Path.Combine(_folders.Create("twint").FullName, "recording.ser");
        capture.TryStartRecording(path, TimeSpan.FromHours(1), out var recording, out var refusal).ShouldBeTrue(refusal);
        await StepAsync(capture, 2, ct);

        var ran = false;
        var answer = await capture.InterludeAsync(new PlanetaryInterlude("a filter change", _ =>
        {
            ran = true;
            return ValueTask.CompletedTask;
        }), ct);

        output.WriteLine(answer ?? "no refusal");
        answer.ShouldBe("A recording is being made: a filter change would stop the stream it is made from, and put frames from before and "
            + "after it in one file. Stop the recording, or let it end, first");
        capture.InterludeRefusal("a filter change").ShouldBe(answer, "the refusal a host asks for first is the same");
        ran.ShouldBeFalse();
        await StepAsync(capture, 2, ct);
        camera.VideoStarts.Length.ShouldBe(1, "the stream was never stopped");
        await capture.StopAsync(ct);
        await recording.Completion.WaitAsync(ct);
        recording.FramesWritten.ShouldBe(4, "every frame the stream gave is in the one file");
    }

    [Fact(Timeout = 30_000)]
    public async Task AnActionsFailureIsItsCallersOnceTheStreamIsBackAsItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = NewCapture();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        await StepAsync(capture, 2, ct);

        var interlude = capture.InterludeAsync(new PlanetaryInterlude("a test", _ => throw new InvalidOperationException("the wheel jammed"))
        {
            RestartAt = had => had with { RoiWidth = 64, RoiHeight = 48 },
        }, ct);
        await StepUntilAsync(capture, interlude, ct);

        (await Should.ThrowAsync<InvalidOperationException>(interlude)).Message.ShouldBe("the wheel jammed");
        camera.VideoStarts[1].Width.ShouldBe(128, "a failed action's transform is not asked: the stream comes back as it was");
        capture.IsCapturing.ShouldBeTrue();
        capture.FailureReason.ShouldBeNull("the capture did not fail");
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterludeAskedOfACaptureThatEndsIsToldSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await CameraAsync(ct);
        await using var capture = NewCapture();
        capture.InterludeRefusal("a test").ShouldBe("No planetary capture is streaming, so a test has no stream to stop");
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2)), ct).ShouldBeTrue();
        await StepAsync(capture, 1, ct);

        var interlude = capture.InterludeAsync(Nothing(), ct);
        interlude.IsCompleted.ShouldBeFalse("taken only as the loop asks for its next frame, which no step has let it");
        await capture.StopAsync(ct);

        (await interlude).ShouldBe("The planetary capture ended before a test could stop its stream");
    }
}
