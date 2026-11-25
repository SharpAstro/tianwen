using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

// The atlas's redraw flag is raised by whoever changed something and cleared by the host after a full frame. A clear
// cannot tell what the frame drew from what was asked of it while it was being drawn, and both ways of asking behind a
// frame were lost to it (#2130): a load finishing on another thread, and a texture uploaded in this frame that is
// drawable in the next. Under a moving pointer the frames that repaint the highlight's rectangle alone then showed the
// loaded picture on some swapchain images and not on others (the Sun flickered on its first click).
public class SkyMapRedrawFlagTests
{
    [Fact]
    public void WhatWasRaisedBeforeAFrameIsDrawnByItAndCleared()
    {
        var state = new SkyMapState();
        state.NeedsRedraw = true;

        state.BeginDraw();
        state.NeedsRedraw = false;

        state.NeedsRedraw.ShouldBeFalse();
    }

    // The draw raises the flag for changes its own frame shows (it levels the roll every frame in Horizon mode). Kept,
    // each would buy another frame for ever.
    [Fact]
    public void WhatTheDrawRaisesForItsOwnFrameIsClearedWithIt()
    {
        var state = new SkyMapState();
        state.BeginDraw();
        state.NeedsRedraw = true;

        state.NeedsRedraw = false;

        state.NeedsRedraw.ShouldBeFalse();
    }

    [Fact]
    public void AFrameAskedForFromInsideTheDrawOutlivesThatFramesClear()
    {
        var state = new SkyMapState();
        state.BeginDraw();
        state.RequestFollowingFrame();

        state.NeedsRedraw = false;
        state.NeedsRedraw.ShouldBeTrue("the texture uploaded in that frame is drawable in the next");

        // And it buys ONE frame: the next clear takes it.
        state.BeginDraw();
        state.NeedsRedraw = false;
        state.NeedsRedraw.ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task ALoadThatFinishesOnAnotherThreadWhileAFrameIsDrawnOutlivesItsClear()
    {
        var state = new SkyMapState();
        state.NeedsRedraw = false;
        state.BeginDraw();

        var raised = new Thread(() => state.NeedsRedraw = true);
        raised.Start();
        await Task.Run(raised.Join, TestContext.Current.CancellationToken);

        state.NeedsRedraw = false;
        state.NeedsRedraw.ShouldBeTrue("the frame may have drawn the object before its picture arrived");
    }

    // The roll easing back to level (the L key, a mode switch) asked for each next frame with a plain raise from inside
    // the draw, which is exactly the raise the clear after that frame takes. It had no frame of its own: it moved a step
    // whenever something else drew, a step a second on a still map (2026-11-25). It is a STANDING reason now, which no
    // clear consumes, until it arrives.
    [Fact]
    public void ALevelOnItsWayIsAStandingReasonForFramesUntilItArrives()
    {
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 6.0, CenterDec = 20.0, CenterRoll = 0.4 };
        state.NeedsContinuousFrames.ShouldBeFalse("nothing moves by itself");

        state.RequestLevelToReference();
        state.NeedsContinuousFrames.ShouldBeTrue();

        // One frame of it, as the host draws and clears: the flag goes, the reason stands.
        state.BeginDraw();
        state.UpdateRollForReference(deltaSeconds: 1.0 / 60.0);
        state.NeedsRedraw = false;
        state.NeedsRedraw.ShouldBeFalse("the draw's own raise is cleared with its frame");
        state.IsLevelling.ShouldBeTrue("one frame does not level a roll of 0.4 rad");
        state.NeedsContinuousFrames.ShouldBeTrue("so the next frame is asked for by the reason, not by the flag");

        for (var frame = 0; frame < 600 && state.IsLevelling; frame++)
        {
            state.UpdateRollForReference(deltaSeconds: 1.0 / 60.0);
        }

        state.CenterRoll.ShouldBe(0.0, 1e-9);
        state.NeedsContinuousFrames.ShouldBeFalse("arrived: nothing asks for frames any more");
    }

    // The roll is advanced by the map's own frames, and none is drawn while the orrery is open: a level asked for just
    // before would stand as a reason for as long as the orrery did, every frame of it drawn for nothing.
    [Fact]
    public void ALevelIsNoReasonForFramesWhileTheOrreryIsOpen()
    {
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRoll = 0.4 };
        state.RequestLevelToReference();

        state.Orrery.IsOpen = true;
        state.NeedsContinuousFrames.ShouldBeFalse();

        state.Orrery.IsOpen = false;
        state.NeedsContinuousFrames.ShouldBeTrue("and it goes on when the map is back");
    }
}
