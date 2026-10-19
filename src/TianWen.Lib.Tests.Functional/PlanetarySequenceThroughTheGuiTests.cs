using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The Planetary mode's SEQUENCE section through the GUI's real signal handler over a real node (#1340): Start sends the draft to
/// the capture's node, which runs it, and the view's capture state carries its progress to the end.
/// </summary>
[Collection("NodeProcesses")]
public class PlanetarySequenceThroughTheGuiTests(ITestOutputHelper output)
{
    [Fact(Timeout = 120_000)]
    public async Task AStartedSequenceRunsOnTheNodeAndThePanelsStateFollowsItToItsEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct, withFilterWheel: true);
        h.Post(new StartVideoCaptureSignal(RoiWidth: 320, RoiHeight: 200));
        await h.UntilSettledAsync(ct);
        var planetary = h.Planetary;
        await h.UntilAsync(() => planetary.Capture.FramesReceived > 0, ct);

        var draft = new PlanetarySequenceDraft();
        draft.Add(slots: 4, exposureMs: 5, gain: 100);
        draft.Add(slots: 4, exposureMs: 6, gain: 110);
        for (var i = 0; i < 4; i++)
        {
            draft.StepLength(0, -1);
            draft.StepLength(1, -1);
        }
        h.Post(new StartPlanetarySequenceSignal(draft.ToRequest()));
        await h.UntilSettledAsync(ct);

        await h.UntilAsync(() => planetary.Capture.Sequence is { Phase: PlanetarySequencePhase.Done or PlanetarySequencePhase.Failed }, ct);
        var done = planetary.Capture.Sequence.ShouldNotBeNull();
        output.WriteLine(PlanetaryControlPanel.SequenceInWords(done));
        (done.Phase, done.FailureReason).ShouldBe((PlanetarySequencePhase.Done, (string?)null));
        done.Files.Length.ShouldBe(2);
        done.Files.ShouldAllBe(file => File.Exists(file));
        h.AppState.Notifications.ShouldContain(static n => n.Message == "Sequence started: 1 set of 2 steps");
        planetary.IsCapturing.ShouldBeTrue("the capture streams on after its sequence");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }
}
