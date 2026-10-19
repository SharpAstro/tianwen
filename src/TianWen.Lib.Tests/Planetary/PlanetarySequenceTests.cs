using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A mono filter-wheel sequence on a planetary capture (#1340, <see cref="PlanetarySequenceRunner"/>): each step's filter turned
/// in through the one filter change, the stream started again at its settings, and one file recorded per step. Over the fake
/// camera's video, stepped a frame at a time, and the fake wheel (L, R, G, B; focus offsets 0, +20, 0, -15) and focuser.
/// </summary>
[Collection("Session")]
public class PlanetarySequenceTests(ITestOutputHelper output) : IDisposable
{
    private const int L = 0, R = 1, G = 2, B = 3;

    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private sealed record Rig(FakeCameraDriver Camera, IFilterWheelDriver Wheel, FakeFocuserDriver Focuser, FakeTimeProviderWrapper Time, PlanetaryCapture Capture)
        : IAsyncDisposable
    {
        public PlanetarySequenceRunner Runner(ITestOutputHelper output, bool withFocuser = true)
            => new(Capture, Wheel, withFocuser ? FilterFocuser.Direct(Focuser, new FocusDirection(PreferOutward: true, OutwardIsPositive: true)) : null,
                Time, FakeExternal.CreateLogger(output));

        public ValueTask DisposeAsync() => Capture.DisposeAsync();
    }

    private async Task<Rig> RigAsync(CancellationToken ct)
    {
        var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero));
        var services = new FakeExternal(output, time).BuildServiceProvider();
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 8), services);
        var wheel = new FakeFilterWheelDriver(new FakeDevice(DeviceType.FilterWheel, 1), services);
        var focuser = new FakeFocuserDriver(new FakeDevice(DeviceType.Focuser, 1), services);
        await camera.ConnectAsync(ct);
        await wheel.ConnectAsync(ct);
        await focuser.ConnectAsync(ct);
        camera.NumX = 128;
        camera.NumY = 128;
        camera.PlanetRadiusPixels = 40;

        var capture = new PlanetaryCapture(new FakeTimeProviderWrapper(), NullLogger.Instance,
            new RollingWindowOptions { FallbackWindowFrames = 10, MaxWindowFrames = 16 });
        capture.ArmFrameGate();
        capture.Start(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2), Gain: 50), ct).ShouldBeTrue();
        await StepAsync(capture, 2, ct);
        return new Rig(camera, wheel, focuser, time, capture);
    }

    // Lets the loop take one frame at a time and waits until each is whole.
    private static async Task StepAsync(PlanetaryCapture capture, int frames, CancellationToken ct)
    {
        for (var i = 0; i < frames; i++)
        {
            var next = capture.WaitForNextFrameAsync(ct);
            capture.StepFrame();
            await next;
        }
    }

    // Steps the loop until `done` has completed, within a bound the caller's assertion reports.
    private static async Task<int> StepUntilAsync(PlanetaryCapture capture, Task done, CancellationToken ct, int maxFrames = 400)
    {
        var stepped = 0;
        while (!done.IsCompleted && stepped < maxFrames)
        {
            await StepAsync(capture, 1, ct);
            stepped++;
        }
        return stepped;
    }

    // A path per file in a folder of the test's, numbered in the order asked for and named for the filter.
    private Func<string?, DateTimeOffset, string> Files(out DirectoryInfo folder)
    {
        var dir = _folders.Create("twseq");
        folder = dir;
        var made = 0;
        return (filter, _) => Path.Combine(dir.FullName, $"{++made:00}-{filter ?? "none"}.ser");
    }

    private static PlanetarySequenceStep Step(int filter, double exposureMs, short gain, int frames)
        => new(filter, TimeSpan.FromMilliseconds(exposureMs), gain, RecordingLength.OfFrames(frames));

    [Fact(Timeout = 60_000)]
    public async Task TwoSetsOfRedGreenAndBlueRecordSixFilesEachThroughItsFilterAtItsSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await RigAsync(ct);
        var luminanceFocus = await rig.Focuser.GetPositionAsync(ct);
        var sequence = new PlanetarySequence([Step(R, 3, 100, 4), Step(G, 4, 110, 4), Step(B, 5, 120, 4)], Sets: 2);
        var runner = rig.Runner(output);

        var run = runner.RunAsync(sequence, Files(out var folder), ct);
        var stepped = await StepUntilAsync(rig.Capture, run, ct);
        output.WriteLine($"stepped {stepped} frames");
        await run;

        var progress = runner.Progress;
        (progress.Phase, progress.Set, progress.Step, progress.FailureReason).ShouldBe((PlanetarySequencePhase.Done, 2, 3, (string?)null));
        var names = rig.Wheel.Filters;
        string[] expected = [.. new[] { R, G, B, R, G, B }.Select((filter, i) => $"{i + 1:00}-{FilterChange.NameOf(names, filter)}.ser")];
        progress.Files.Select(Path.GetFileName).ShouldBe(expected, "one file a step, in order, each named for its filter");
        Directory.GetFiles(folder.FullName).Length.ShouldBe(6);
        foreach (var file in progress.Files)
        {
            using var reader = SerReader.Open(file);
            reader.FrameCount.ShouldBe(4, $"{Path.GetFileName(file)} holds the step's frame target");
        }

        // The stream it started with, then one start a step, each at the step's own exposure and gain.
        var starts = rig.Camera.VideoStarts;
        starts.Length.ShouldBe(7);
        starts.Skip(1).Select(start => (start.Options.Exposure.TotalMilliseconds, start.Options.Gain))
            .ShouldBe([(3, (short)100), (4, (short)110), (5, (short)120), (3, (short)100), (4, (short)110), (5, (short)120)]);

        (await rig.Wheel.GetPositionAsync(ct)).ShouldBe(B, "the last step's filter");
        (await rig.Focuser.GetPositionAsync(ct) - luminanceFocus).ShouldBe(-15, "the focuser at blue's focus, the offsets of every change in between summed");
        rig.Capture.IsCapturing.ShouldBeTrue("the capture streams on after its sequence");
    }

    [Fact(Timeout = 30_000)]
    public async Task ARecordingOfAFrameTargetEndsAtItsFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await RigAsync(ct);
        var path = Path.Combine(_folders.Create("twseq").FullName, "three.ser");

        rig.Capture.TryStartRecording(path, RecordingLength.OfFrames(3), out var recording, out var refusal).ShouldBeTrue(refusal);
        recording.EndsAt.ShouldBeNull("it ends at its frames, not at a time");
        await StepAsync(rig.Capture, 6, ct);
        await recording.Completion.WaitAsync(ct);

        (recording.FramesWritten, recording.FrameTarget, recording.EndReason).ShouldBe((3, (int?)3, "its frames are taken"));
        using var reader = SerReader.Open(path);
        reader.FrameCount.ShouldBe(3);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStopEndsTheStepsRecordingWithTheFramesItHas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await RigAsync(ct);
        var runner = rig.Runner(output);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = runner.RunAsync(new PlanetarySequence([Step(R, 3, 100, 1000)], Sets: 1), Files(out _), stop.Token);
        // Through the change, into the recording, and a few frames on.
        for (var i = 0; i < 50 && runner.Progress.Phase is not PlanetarySequencePhase.Recording; i++)
        {
            await StepAsync(rig.Capture, 1, ct);
        }
        runner.Progress.Phase.ShouldBe(PlanetarySequencePhase.Recording);
        await StepAsync(rig.Capture, 5, ct);
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(run);
        runner.Progress.Phase.ShouldBe(PlanetarySequencePhase.Stopped);
        var recording = rig.Capture.Recording.ShouldNotBeNull();
        (recording.IsRecording, recording.Completion.IsCompleted, recording.EndReason).ShouldBe((false, true, "the sequence was stopped"));
        recording.FramesWritten.ShouldBeGreaterThanOrEqualTo(5, "the frames the step took are kept");
        runner.Progress.Files.ShouldBeEmpty("a step it did not finish is not one of its files");
        rig.Capture.IsCapturing.ShouldBeTrue("a stopped sequence leaves the capture streaming");
    }

    [Fact(Timeout = 30_000)]
    public async Task NoSetBeginsPastItsEndTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await RigAsync(ct);
        var runner = rig.Runner(output);
        var sequence = new PlanetarySequence([Step(R, 3, 100, 2)], Sets: 3, Until: rig.Time.GetUtcNow() - TimeSpan.FromSeconds(1));

        await runner.RunAsync(sequence, Files(out var folder), ct);

        (runner.Progress.Phase, runner.Progress.Set).ShouldBe((PlanetarySequencePhase.Done, 0));
        Directory.GetFiles(folder.FullName).ShouldBeEmpty();
        rig.Camera.VideoStarts.Length.ShouldBe(1, "the stream was never stopped");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASequenceTheWheelCannotRunFailsBeforeTheStreamStops()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await RigAsync(ct);
        var runner = rig.Runner(output);

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            runner.RunAsync(new PlanetarySequence([Step(R, 3, 100, 2), Step(9, 3, 100, 2)], Sets: 1), Files(out _), ct));

        failure.Message.ShouldBe($"Step 2 asks for position 9; the wheel has positions 0 to {rig.Wheel.Filters.Count - 1}");
        (runner.Progress.Phase, runner.Progress.FailureReason).ShouldBe((PlanetarySequencePhase.Failed, failure.Message));
        rig.Camera.VideoStarts.Length.ShouldBe(1, "nothing was begun");
    }

    [Theory]
    [InlineData(0, 1, 2, "A sequence runs its steps at least once")]
    [InlineData(1, 0, 2, "Step 1 needs a positive exposure")]
    [InlineData(1, 3, 0, "Step 1: A recording needs a positive length")]
    public void ASequenceSaysWhyItCannotRun(int sets, double exposureMs, int frames, string expected)
    {
        var sequence = new PlanetarySequence([Step(R, exposureMs, 100, frames)], sets);
        sequence.Invalid(FakeFilterWheelDriver.GetDefaultFiltersForId(1)).ShouldBe(expected);
    }

    [Fact]
    public void AStepThroughAFilterNeedsAWheelAndOneThatLeavesItDoesNot()
    {
        new PlanetarySequence([Step(R, 3, 100, 2)], 1).Invalid(filters: null)
            .ShouldBe("Step 1 is taken through a filter, and the camera's telescope has no filter wheel");
        new PlanetarySequence([Step(PlanetarySequenceStep.NoFilterChange, 3, 100, 2)], 1).Invalid(filters: null).ShouldBeNull();
        new PlanetarySequence(ImmutableArray<PlanetarySequenceStep>.Empty, 1).Invalid(filters: null).ShouldBe("A sequence needs at least one step");
    }
}
