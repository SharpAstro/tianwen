using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Auto view is a still picture and costs a still frame (#1875): with its run pending, playback does not advance under it, the live
/// rolling stack does not follow, and the loop is asked for a frame only when the Auto progress or the picture changed.
/// </summary>
[Collection("Viewer")]
public class ViewerAutoFrameDemandTests
{
    private const int Iterations = 150;

    [Theory(Timeout = 300_000)]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1f, true)]
    [InlineData(1.5f, true)]
    public async Task APendingAutoRunAsksForNoFrameAndMovesNoPlayhead(float dpi, bool fromStack)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);
        e2e.State.PlaybackFps = 1000f;
        e2e.State.IsPlaying = true;

        if (fromStack)
        {
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewLive" }, "the Live view"));
        }
        // Playback runs under the Raw and the Stack view, which is what the Auto view stops.
        var moved = false;
        var first = e2e.State.FrameIndex;
        for (var i = 0; i < Iterations && !moved; i++)
        {
            e2e.Frame();
            moved = e2e.State.FrameIndex != first;
            await Task.Delay(2, ct);
        }
        moved.ShouldBeTrue("playback runs before Auto is chosen");

        // Held: the run starts, and stays pending until the gate opens.
        var gate = new TaskCompletionSource();
        e2e.Controller.AutoRunGate = gate.Task;
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
        await e2e.PumpUntilAsync(() => e2e.Controller.IsAutoStackPending && e2e.State.AutoStackProgress is not null, "the Auto run to start", ct);
        e2e.Controller.AutoStacksStarted.ShouldBe(1);

        var playhead = e2e.State.FrameIndex;
        var asked = 0;
        for (var i = 0; i < Iterations; i++)
        {
            if (e2e.Host.WantsFrame())
            {
                asked++;
                e2e.Frame();
            }
            await Task.Delay(2, ct);
        }

        asked.ShouldBe(0, "no progress and no new picture, so no frame is asked for");
        e2e.State.FrameIndex.ShouldBe(playhead, "the playhead holds under the Auto view");
        e2e.Controller.ViewLayers.Auto?.HasMaster.ShouldNotBe(true);

        // Released: the run finishes and its master shows; another view frees the playhead.
        gate.SetResult();
        await ViewerBestStackTests.AutoOnShowAsync(e2e, ct);
        e2e.State.IsPlaying = true;
        for (var i = 0; i < Iterations; i++)
        {
            if (e2e.Host.WantsFrame())
            {
                e2e.Frame();
            }
        }
        e2e.State.FrameIndex.ShouldBe(playhead, "the master on show is a still picture too");
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewFrames" }, "the Frames view"));
        for (var i = 0; i < Iterations && e2e.State.FrameIndex == playhead; i++)
        {
            e2e.Frame();
            await Task.Delay(2, ct);
        }
        e2e.State.FrameIndex.ShouldNotBe(playhead, "playback resumes when another view is chosen");
    }
}
