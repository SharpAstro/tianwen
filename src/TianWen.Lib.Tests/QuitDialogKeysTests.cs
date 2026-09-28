using System.Collections.Generic;
using Console.Lib;
using DIR.Lib;
using NSubstitute;
using Shouldly;
using TianWen.Cli.Tui;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The quit's question answered from either tab (decision 1 of docs/plans/hardware-in-the-server.md, #936): Enter takes the
/// default, the other choice's letter the other, Escape stays, and a question on screen comes before the tab's own keys (in
/// preview, Enter captures and S saves).
/// </summary>
[Collection("UI")]
public class QuitDialogKeysTests
{
    private static InputEvent Key(InputKey key) => new InputEvent.KeyDown(key);

    private static List<QuitAction?> Answers(SignalBus bus)
    {
        var answers = new List<QuitAction?>();
        bus.Subscribe<AnswerQuitSignal>(sig => answers.Add(sig.Action));
        return answers;
    }

    [Fact]
    public void TheGuiTabDrawsTheQuestionAndItsKeysAnswerIt()
    {
        var bus = new SignalBus();
        var answers = Answers(bus);
        using var renderer = new RgbaImageRenderer(1280, 800);
        var tab = new LiveSessionTab<RgbaImage>(renderer) { DpiScale = 1f, FontPath = FontResolver.ResolveSystemFont(), Bus = bus };
        var state = new LiveSessionState { QuitDialog = QuitDialog.DevicesConnected(2, aCameraNeedsWarming: true) };
        tab.Render(state, new RectF32(0f, 0f, 1280f, 800f), new SystemTimeProvider());

        tab.HandleInput(Key(InputKey.L)).ShouldBeTrue();
        tab.HandleInput(Key(InputKey.Enter)).ShouldBeTrue();
        tab.HandleInput(Key(InputKey.Escape)).ShouldBeTrue();
        bus.ProcessPending(new BackgroundTaskTracker());

        answers.ShouldBe([QuitAction.LeaveConnected, QuitAction.WarmUpAndDisconnect, null]);
    }

    [Fact]
    public void TheTuiTabsQuestionComesBeforeItsPreviewKeys()
    {
        var bus = new SignalBus();
        var answers = Answers(bus);
        var contexts = new ViewContexts();
        var terminal = Substitute.For<IVirtualTerminal>();
        terminal.Size.Returns((120, 40));
        terminal.CellSize.Returns(new TermCell(1, 1));
        var tab = new TuiLiveSessionTab(new GuiAppState(), contexts, terminal, new FakeTimeProviderWrapper(), bus);
        tab.Attach(terminal);
        contexts.Local.LiveSession.QuitDialog = QuitDialog.RunGoingOn(new Hosting.Dto.NodeRunDto { Kind = Hosting.Dto.NodeRunKind.Polar });

        tab.HandleInput(Key(InputKey.S));
        tab.HandleInput(Key(InputKey.Enter));
        tab.HandleInput(Key(InputKey.Escape));
        bus.ProcessPending(new BackgroundTaskTracker());

        answers.ShouldBe([QuitAction.StopTheRig, QuitAction.LeaveTheRigRunning, null], "S stopped the rig rather than saving a frame");
    }
}
