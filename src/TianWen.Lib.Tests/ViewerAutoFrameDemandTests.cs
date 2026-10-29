using System;
using System.IO;
using System.Threading;
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
/// <remarks>
/// Measured live on the Debug tianwen-fits against the sibling sources (SEB-SURFACE, 2026-10-29), the inspector's full-frame count
/// over about 20 s on a synthetic 1,500-frame 640x480 mono capture played at 30 fps, before (main) and after this fix:
/// while the Auto run is pending, about 30 frames a second before (921 in 30.6 s, the playhead moving 398 to 787) and 1.6 after
/// (36 in 22 s, the playhead held); on the finished master, 30 a second before (649 in 21 s, every one a blit of an unchanged
/// cached layer, the playhead still moving) and none after (1 in 22 s). How long the run itself took is not told apart by
/// these two samples.
/// </remarks>
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
        var gate = await PendingAutoRunAsync(e2e, fromStack, ct);

        // Honest placeholder (#1875): the raw frame at the held playhead shows, never the live stack or an older Auto layer, and one line says so.
        e2e.Frame();
        e2e.Controller.Source.ShouldNotBeNull().ShouldNotBeOfType<LiveStackPreviewSource>("the raw frame, not a previous stack");
        e2e.State.AutoShowsFrames.ShouldBeTrue();
        ViewerState.AutoShowsFramesWording.ShouldBe("Showing the frames while Auto stacks");

        var playhead = e2e.State.FrameIndex;
        var raw = e2e.Controller.Source.ShouldBeOfType<SerPreviewSource>();
        var live = e2e.Controller.ViewLayers.Live;
        var (decodes, folds) = (raw.DecodesStarted, live?.StacksPublished);
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
        raw.DecodesStarted.ShouldBe(decodes, "no raw decode while the Auto view is pending");
        (live?.StacksPublished).ShouldBe(folds, "no live restack while the Auto view is pending");

        // A change of the Auto progress is the one thing that asks for a frame, and one frame answers it.
        var shown = e2e.State.AutoStackProgress;
        e2e.State.AutoStackProgress = (shown ?? 0) + 0.01;
        e2e.Host.WantsFrame().ShouldBeTrue("the progress changed, so a frame is asked for");
        e2e.Frame();
        e2e.State.AutoStackProgress.ShouldBe(shown, "the frame shows the run's own progress again");
        e2e.Host.WantsFrame().ShouldBeFalse("the progress is shown, so nothing more is asked for");
        e2e.State.FrameIndex.ShouldBe(playhead, "the playhead holds under the Auto view");
        e2e.Controller.ViewLayers.Auto?.HasMaster.ShouldNotBe(true);

        // Released: the run finishes and its master shows; another view frees the playhead.
        gate.SetResult();
        await ViewerBestStackTests.AutoOnShowAsync(e2e, ct);
        e2e.Controller.Source.ShouldBeSameAs(e2e.Controller.ViewLayers.Auto, "the run's master replaces the raw frame");
        e2e.State.AutoShowsFrames.ShouldBeFalse();
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
        for (var i = 0; i < 1000 && e2e.State.FrameIndex == playhead; i++)
        {
            e2e.Frame();
            await Task.Delay(10, ct);
        }
        e2e.State.FrameIndex.ShouldNotBe(playhead, "playback resumes when another view is chosen");
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f, true)]
    [InlineData(1.5f, true)]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    public async Task AnAutoRunCancelledOrFailedLetsTheViewUnderItPlay(float dpi, bool cancelled)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var gate = await PendingAutoRunAsync(e2e, fromStack: true, ct);
        e2e.Frame();
        e2e.State.AutoShowsFrames.ShouldBeTrue("held while the run is pending");
        var playhead = e2e.State.FrameIndex;

        // Ended without a master: Shift+K cancels the pending run, or the run throws.
        if (cancelled)
        {
            e2e.Key(InputKey.K, InputModifier.Shift);
        }
        else
        {
            gate.SetException(new InvalidOperationException("no planet"));
        }
        await e2e.PumpUntilAsync(() => !e2e.Controller.IsAutoStackPending && e2e.State.AutoStackProgress is null, "the Auto run to end", ct);
        e2e.Frame();

        // The hold lets go: no placeholder line, the bar says how the run ended, and the live stack under it shows and follows again.
        e2e.State.AutoShowsFrames.ShouldBeFalse("nothing is stacking, so the frames are not shown 'while Auto stacks'");
        e2e.State.StatusMessage.ShouldBe(cancelled ? "Auto stack cancelled" : "Auto stack failed: no planet");
        e2e.Controller.Source.ShouldBeSameAs(e2e.Controller.ViewLayers.Live, "the view under Auto, the live stack, shows again");
        var folds = e2e.Controller.ViewLayers.Live?.StacksPublished;
        for (var i = 0; i < 1000 && (e2e.State.FrameIndex == playhead || e2e.Controller.ViewLayers.Live?.StacksPublished == folds); i++)
        {
            e2e.Frame();
            await Task.Delay(10, ct);
        }
        e2e.State.FrameIndex.ShouldNotBe(playhead, "playback resumes once the run has ended");
        (e2e.Controller.ViewLayers.Live?.StacksPublished).ShouldNotBe(folds, "the live stack follows the playhead again");

        // Paused, the loop goes quiet again.
        e2e.State.IsPlaying = false;
        var quiet = false;
        for (var i = 0; i < 1000 && !quiet; i++)
        {
            if (e2e.Host.WantsFrame())
            {
                e2e.Frame();
                await Task.Delay(10, ct);
            }
            else
            {
                quiet = true;
            }
        }
        quiet.ShouldBeTrue("an idle view asks for no frame");
        e2e.State.AutoShowsFrames.ShouldBeFalse();
    }

    // A capture opened and played (from the Stack view, with its live master, or from Raw), then the Auto view chosen with its run held
    // pending until the returned gate is settled.
    private static async Task<TaskCompletionSource> PendingAutoRunAsync(ViewerE2E e2e, bool fromStack, CancellationToken ct)
    {
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture && e2e.State.IsSequence && e2e.State.FrameCount > 1,
            "the capture's frames to be known", ct);

        if (fromStack)
        {
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewLive" }, "the Live view"));
            // The live stack has a master before Auto is chosen, so the placeholder is what keeps it off the screen, not its absence.
            await e2e.PumpUntilAsync(() => e2e.Controller.ViewLayers.Live is { HasMaster: true, IsBusy: false }, "the live stack's first master", ct);
        }
        e2e.State.PlaybackFps = 1000f;
        e2e.State.IsPlaying = true;

        // Playback runs under the Raw and the Stack view, which is what the Auto view stops.
        var first = e2e.State.FrameIndex;
        for (var i = 0; i < 1000 && e2e.State.FrameIndex == first; i++)
        {
            e2e.Frame();
            await Task.Delay(10, ct);
        }
        e2e.State.FrameIndex.ShouldNotBe(first,
            $"playback runs before Auto is chosen (playing {e2e.State.IsPlaying}, auto {e2e.State.ShowAuto}, "
            + $"stacked {e2e.State.ShowStacked}, frames {e2e.State.FrameCount}, status '{e2e.State.StatusMessage}')");

        // Held: the run starts, and stays pending until the gate opens.
        var gate = new TaskCompletionSource();
        e2e.Controller.AutoRunGate = gate.Task;
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewAuto" }, "the Auto view"));
        await e2e.PumpUntilAsync(() => e2e.Controller.IsAutoStackPending && e2e.State.AutoStackProgress is not null, "the Auto run to start", ct);
        e2e.Controller.AutoStacksStarted.ShouldBe(1);
        return gate;
    }
}
