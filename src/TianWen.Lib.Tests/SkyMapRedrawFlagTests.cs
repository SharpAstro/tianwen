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
}
