using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The Planetary mode's Record control (#1344): its signals ask this computer's node to record the capture going on to a
/// SER, the node being the recording's one writer, and the panel reads the recording back from the capture's state. Before
/// it, a capture taken in the GUI left no SER behind: the node could record (P5 part 5d), but nothing in the GUI asked it to.
/// Through the GUI's real signal handler over a real node on its socket, its camera a fake.
/// </summary>
[Collection("NodeProcesses")]
public class PlanetaryRecordingThroughTheNodeTests(ITestOutputHelper output)
{
    private static async Task<PlanetaryCaptureController> CapturingAsync(GuiNodeHarness h)
    {
        var ct = TestContext.Current.CancellationToken;
        h.Post(new StartVideoCaptureSignal(RoiWidth: 320, RoiHeight: 200));
        await h.UntilSettledAsync(ct);
        h.AppState.Notifications.ShouldNotContain(static n => n.Severity >= NotificationSeverity.Warning,
            string.Join("; ", h.AppState.Notifications.Select(static n => n.Message)));
        var planetary = h.Planetary;
        planetary.IsCapturing.ShouldBeTrue("premise: the capture started");
        await h.UntilAsync(() => planetary.Capture.FramesReceived > 0, ct);
        return planetary;
    }

    private static void ShouldNotHaveWarned(GuiNodeHarness h)
        => h.AppState.Notifications.ShouldNotContain(static n => n.Severity >= NotificationSeverity.Warning,
            string.Join("; ", h.AppState.Notifications.Select(static n => n.Message)));

    [Fact(Timeout = 90_000)]
    public async Task TheStartRecordsTheCaptureOnTheNodeForItsDurationAndThePanelShowsTheFileAndItsFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var planetary = await CapturingAsync(h);
        var duration = TimeSpan.FromSeconds(3);

        h.Post(new StartPlanetaryRecordingSignal(duration));
        await h.UntilSettledAsync(ct);

        ShouldNotHaveWarned(h);
        var onTheNode = (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Recording.ShouldNotBeNull();
        (onTheNode.EndsUtc - onTheNode.StartedUtc).ShouldBe(duration, "the node records for the duration the control asked");
        var shown = planetary.Capture.Recording.ShouldNotBeNull("the node's answer is on show at once, not at the next poll");
        shown.Path.ShouldBe(onTheNode.Path);
        var file = Path.GetFileName(onTheNode.Path);
        h.AppState.Notifications.ShouldContain(n => n.Message == $"Recording 3 s to {file}");

        // It ends by itself at its duration, and the panel says why.
        await h.UntilAsync(() => planetary.Capture.Recording is { Written: true }, ct);
        var written = planetary.Capture.Recording.ShouldNotBeNull();
        (written.Recording, written.EndReason, written.FailureReason).ShouldBe((false, "its duration is over", (string?)null));
        written.FramesWritten.ShouldBeGreaterThan(0);
        string.Join(" | ", PlanetaryRecordControl.Describe(written, planetary.RecordingTimeLeft))
            .ShouldBe($"{file} | Ended because its duration is over | {written.FramesWritten} written   {written.FramesDropped} dropped");
        planetary.RecordingTimeLeft.ShouldBe(TimeSpan.Zero, "none is being made");
        using (var ser = SharpAstro.Ser.SerReader.Open(written.Path))
        {
            ser.FrameCount.ShouldBe(written.FramesWritten, "the SER holds the frames the panel said were written");
            (ser.Width, ser.Height).ShouldBe((320, 200));
        }
        planetary.IsCapturing.ShouldBeTrue("a recording's end is not the capture's");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }

    [Fact(Timeout = 90_000)]
    public async Task WhileItRecordsThePanelShowsTheFileItsFramesAndTheTimeLeftAndTheStopEndsItSooner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var planetary = await CapturingAsync(h);
        var duration = TimeSpan.FromMinutes(10);

        h.Post(new StartPlanetaryRecordingSignal(duration));
        // The handler's tracked work is the request alone: a drain of the tracker ends with the node's answer, never with the
        // recording, which the capture's own poll follows outside the tracker. A watch tracked until the recording ends would
        // hold this drain, and a probe's start wait, for the ten minutes.
        await h.UntilSettledAsync(ct);
        ShouldNotHaveWarned(h);
        planetary.Capture.Recording.ShouldNotBeNull().Recording.ShouldBeTrue("the drain ended while the recording goes on");

        // While it records, what the panel shows is the node's: the file, the frames written and dropped, and the time left.
        await h.UntilAsync(() => planetary.Capture.Recording is { Recording: true, FramesWritten: > 0 }, ct);
        var recording = planetary.Capture.Recording.ShouldNotBeNull();
        var onTheNode = (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Recording.ShouldNotBeNull();
        recording.Path.ShouldBe(onTheNode.Path);
        onTheNode.FramesWritten.ShouldBeGreaterThanOrEqualTo(recording.FramesWritten, "the panel shows the node's count as last read");
        var timeLeft = planetary.RecordingTimeLeft;
        timeLeft.ShouldBeInRange(duration - TimeSpan.FromSeconds(60), duration, "by this computer's clock, which is the node's here");
        var lines = PlanetaryRecordControl.Describe(recording, timeLeft);
        lines[0].ShouldBe(Path.GetFileName(onTheNode.Path));
        lines[1].ShouldMatch($"^{recording.FramesWritten} written   {recording.FramesDropped} dropped   (10:00|9:[0-5][0-9]) left$");

        h.Post(new StopPlanetaryRecordingSignal());
        await h.UntilSettledAsync(ct);

        ShouldNotHaveWarned(h);
        h.AppState.Notifications.ShouldContain(static n => n.Message == "Recording stopped");
        await h.UntilAsync(() => planetary.Capture.Recording is { Written: true }, ct);
        var stopped = planetary.Capture.Recording.ShouldNotBeNull();
        // Ten minutes asked, so a recording that ended on the stop, inside this test's bound, ended early.
        (stopped.Recording, stopped.EndReason).ShouldBe((false, "it was stopped"));
        PlanetaryRecordControl.Describe(stopped, planetary.RecordingTimeLeft)[1].ShouldBe("Ended because it was stopped");
        using (var ser = SharpAstro.Ser.SerReader.Open(stopped.Path))
        {
            ser.FrameCount.ShouldBe(stopped.FramesWritten);
        }
        var capture = (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull();
        capture.Running.ShouldBeTrue("the capture goes on after its recording is stopped");
        planetary.IsCapturing.ShouldBeTrue();

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }

    /// <summary>
    /// Record and its Stop are controls of the capture on screen, so they go to the capture's OWN node, never the view's
    /// command target (CLAUDE.md, Remote Rigs: "a control of the RUN on screen goes to its own node"). A capture started on a
    /// granted rig records there while this computer's view is on show, where Record used to reach this computer's node and be
    /// refused (review round 1 of #1610, Q1).
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task RecordAndItsStopGoToTheCapturesOwnNodeWhicheverViewIsOnShow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var (rigNode, rig, _) = await h.ConnectRigAsync(output, ct);
        await GuiNodeHarness.GrantAsync(rigNode, rig, ct);
        var planetary = await CapturingAsync(h);
        var rigClient = new TianWenNodeClient(rigNode.Client);
        (await rigClient.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Running.ShouldBeTrue("premise: the capture is the rig's");

        h.Contexts.Activate(h.Contexts.Local).ShouldBeTrue();
        h.Contexts.IsRemoteActive.ShouldBeFalse("premise: this computer's view is on show");
        h.Post(new StartPlanetaryRecordingSignal(TimeSpan.FromMinutes(10)));
        await h.UntilSettledAsync(ct);

        ShouldNotHaveWarned(h);
        (await rigClient.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull().Recording.ShouldNotBeNull()
            .Recording.ShouldBeTrue("the capture's own node records it");
        (await h.Local.Client.GetPlanetaryAsync(ct)).IsNotFound.ShouldBeTrue("this computer's node ran no capture, and was asked nothing");

        h.Post(new StopPlanetaryRecordingSignal());
        await h.UntilSettledAsync(ct);

        ShouldNotHaveWarned(h);
        var stopped = (await rigClient.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull();
        (stopped.Running, stopped.Recording?.Recording, stopped.Recording?.EndReason).ShouldBe((true, false, "it was stopped"),
            "the stop reached the capture's own node, and the capture goes on");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARecordWithNoCaptureGoingOnIsRefusedInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        h.Post(new StartPlanetaryRecordingSignal(TimeSpan.FromSeconds(60)));
        await h.UntilSettledAsync(ct);

        h.ShouldHaveRefused("No planetary capture is running to record");
        h.Planetary.Capture.Recording.ShouldBeNull("nothing was recorded");

        h.Post(new StopPlanetaryRecordingSignal());
        await h.UntilSettledAsync(ct);

        h.ShouldHaveRefused("No recording is being made");
        h.AppState.Notifications.ShouldNotContain(static n => n.Severity == NotificationSeverity.Error,
            "a refusal is said as one, never as a failure");
    }
}
