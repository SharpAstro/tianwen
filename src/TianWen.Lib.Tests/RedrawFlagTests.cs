using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

// A redraw flag is raised by whoever changed something and cleared by the host after a frame that repainted everything.
// A plain flag's clear cannot tell what that frame drew from what was asked of it while it was being drawn, and three of
// the GUI's four were plain: a run's progress landing on a thread-pool thread mid-frame was cleared unseen. The GUI drew
// a "settling" frame a second after EVERY full frame to catch that, lost or not, which on an idle atlas was every second
// full frame (2026-11-25). The flag now knows, and the settling frame is owed only to what the clear says it dropped.
public class RedrawFlagTests
{
    [Fact]
    public void WithoutAFrameItIsAPlainFlag()
    {
        var flag = new RedrawFlag();
        flag.IsRaised.ShouldBeFalse();

        flag.Raise();
        flag.IsRaised.ShouldBeTrue();

        flag.ClearAfterFrame().ShouldBeFalse("nothing was raised by a draw");
        flag.IsRaised.ShouldBeFalse();
    }

    [Fact]
    public void WhatWasRaisedBeforeAFrameIsDrawnByItAndCleared()
    {
        var flag = new RedrawFlag(raised: true);

        flag.BeginDraw();

        flag.ClearAfterFrame().ShouldBeFalse();
        flag.IsRaised.ShouldBeFalse();
    }

    // The draw raises for changes its own frame shows (the atlas levels its roll every frame). Kept, each such raise
    // would buy another frame for ever, so it is cleared; and the clear SAYS it dropped one, since a draw may also have
    // meant "and once more", which only a settling frame can then honour.
    [Fact]
    public void ARaiseTheDrawItselfMadeIsClearedAndReported()
    {
        var flag = new RedrawFlag();
        flag.BeginDraw();
        flag.Raise();

        flag.ClearAfterFrame().ShouldBeTrue("the draw's own raise was dropped");
        flag.IsRaised.ShouldBeFalse();

        // And only that frame's: the next clear has nothing to report.
        flag.BeginDraw();
        flag.ClearAfterFrame().ShouldBeFalse();
    }

    [Fact]
    public void AFrameAskedForFromInsideTheDrawOutlivesThatFramesClear()
    {
        var flag = new RedrawFlag();
        flag.BeginDraw();
        flag.RequestFollowingFrame();

        flag.ClearAfterFrame().ShouldBeFalse("nothing was dropped: the flag stands");
        flag.IsRaised.ShouldBeTrue();

        // It buys ONE frame: the next clear takes it.
        flag.BeginDraw();
        flag.ClearAfterFrame().ShouldBeFalse();
        flag.IsRaised.ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task ARaiseFromAnotherThreadWhileAFrameIsDrawnOutlivesItsClear()
    {
        var flag = new RedrawFlag();
        flag.BeginDraw();

        var raised = new Thread(flag.Raise);
        raised.Start();
        await Task.Run(raised.Join, TestContext.Current.CancellationToken);

        flag.ClearAfterFrame().ShouldBeFalse("it was not dropped, so no settling frame is owed for it");
        flag.IsRaised.ShouldBeTrue("the frame may have drawn its subject before the reading arrived");
    }

    // The draw's own raise beside one from another thread: the flag stands for the other thread's, and the frame that
    // follows draws everything, the draw's own subject included. Nothing was dropped.
    [Fact(Timeout = 10_000)]
    public async Task ADrawsRaiseBesideAnotherThreadsIsNotReportedDropped()
    {
        var flag = new RedrawFlag();
        flag.BeginDraw();
        flag.Raise();

        var raised = new Thread(flag.Raise);
        raised.Start();
        await Task.Run(raised.Join, TestContext.Current.CancellationToken);

        flag.ClearAfterFrame().ShouldBeFalse();
        flag.IsRaised.ShouldBeTrue();
    }

    // The three flags that were plain. Each is raised from thread-pool callbacks (a run's progress, a mirror's reading),
    // and each one's clear after a frame now leaves such a raise standing.
    [Theory(Timeout = 10_000)]
    [InlineData("app")]
    [InlineData("planner")]
    [InlineData("live session")]
    public async Task AStatesFlagKeepsWhatAnotherThreadRaisedWhileAFrameWasDrawn(string which)
    {
        var app = new GuiAppState();
        var planner = new PlannerState();
        var live = new LiveSessionState();
        (Action BeginDraw, Action Raise, Func<bool> Clear, Func<bool> IsRaised) flag = which switch
        {
            "app" => (app.BeginDraw, () => app.NeedsRedraw = true, app.ClearRedrawAfterFrame, () => app.NeedsRedraw),
            "planner" => (planner.BeginDraw, () => planner.NeedsRedraw = true, planner.ClearRedrawAfterFrame, () => planner.NeedsRedraw),
            _ => (live.BeginDraw, () => live.NeedsRedraw = true, live.ClearRedrawAfterFrame, () => live.NeedsRedraw),
        };

        // A frame that draws what stood before it, and clears it.
        flag.BeginDraw();
        flag.Clear();
        flag.IsRaised().ShouldBeFalse();

        // The next frame, with a raise from another thread while it is drawn.
        flag.BeginDraw();
        var raised = new Thread(() => flag.Raise());
        raised.Start();
        await Task.Run(raised.Join, TestContext.Current.CancellationToken);

        flag.Clear().ShouldBeFalse();
        flag.IsRaised().ShouldBeTrue($"the {which} flag's clear took a raise that landed behind the frame");
    }

    // A host whose loop sleeps until something changes is woken by a raise from another thread, and by no raise of its
    // own: its loop is awake then, and asks its check before it sleeps again. The hook is the process's, so what is
    // asserted is WHICH threads woke it: other tests raise flags of their own while this one runs.
    [Fact(Timeout = 10_000)]
    public async Task ARaiseFromAnotherThreadWakesTheHostAndItsOwnDoesNot()
    {
        var wokenBy = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var host = Environment.CurrentManagedThreadId;
        var otherId = 0;
        var afterId = 0;
        RedrawFlag.WakeHostOnRaise(() => wokenBy[Environment.CurrentManagedThreadId] = true);
        try
        {
            var flag = new RedrawFlag();

            flag.Raise();
            flag.RequestFollowingFrame();
            wokenBy.ContainsKey(host).ShouldBeFalse("raised on the host's own thread");

            var other = new Thread(() =>
            {
                otherId = Environment.CurrentManagedThreadId;
                flag.Raise();
            });
            other.Start();
            await Task.Run(other.Join, TestContext.Current.CancellationToken);
            wokenBy.ContainsKey(otherId).ShouldBeTrue("raised on another thread");
        }
        finally
        {
            RedrawFlag.WakeHostOnRaise(null);
        }

        // Taken back: nothing is woken any more.
        wokenBy.Clear();
        var after = new Thread(() =>
        {
            afterId = Environment.CurrentManagedThreadId;
            new RedrawFlag().Raise();
        });
        after.Start();
        await Task.Run(after.Join, TestContext.Current.CancellationToken);
        wokenBy.ContainsKey(afterId).ShouldBeFalse();
    }

    // The setter's false is the same clear, for the hosts and tests that write it that way.
    [Fact]
    public void SettingAStatesFlagFalseIsTheClear()
    {
        var app = new GuiAppState();
        app.NeedsRedraw.ShouldBeTrue("a new window is owed its first frame");

        app.NeedsRedraw = false;
        app.NeedsRedraw.ShouldBeFalse();

        app.NeedsRedraw = true;
        app.NeedsRedraw.ShouldBeTrue();
    }
}
