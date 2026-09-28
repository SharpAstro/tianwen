using System.Collections.Generic;
using Console.Lib;
using DIR.Lib;
using NSubstitute;
using Shouldly;
using TianWen.Cli.Tui;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A request for control of this computer's rig, answered from either tab (P6b of docs/plans/hardware-in-the-server.md,
/// #1021): Enter declines, so an Enter meant for something else hands the rig to nobody; A allows; Escape answers it later.
/// It comes before the tab's own keys and after the quit's question.
/// </summary>
[Collection("UI")]
public class ControlRequestKeysTests
{
    private static readonly PendingControlRequestDto Request = new PendingControlRequestDto
    {
        Id = "request-1",
        Label = "Laptop, TianWen",
        Address = "192.168.1.20",
    };

    private static InputEvent Key(InputKey key) => new InputEvent.KeyDown(key);

    private static List<object> Signals(SignalBus bus)
    {
        var signals = new List<object>();
        bus.Subscribe<AnswerControlRequestSignal>(sig => signals.Add(sig));
        bus.Subscribe<DismissControlRequestSignal>(sig => signals.Add(sig));
        bus.Subscribe<AnswerQuitSignal>(sig => signals.Add(sig));
        return signals;
    }

    [Fact]
    public void TheGuiTabDrawsTheRequestAndItsKeysAnswerIt()
    {
        var bus = new SignalBus();
        var signals = Signals(bus);
        using var renderer = new RgbaImageRenderer(1280, 800);
        var tab = new LiveSessionTab<RgbaImage>(renderer) { DpiScale = 1f, FontPath = FontResolver.ResolveSystemFont(), Bus = bus };
        var state = new LiveSessionState { ControlRequest = Request };
        tab.Render(state, new RectF32(0f, 0f, 1280f, 800f), new SystemTimeProvider());

        tab.HandleInput(Key(InputKey.A)).ShouldBeTrue();
        tab.HandleInput(Key(InputKey.Enter)).ShouldBeTrue();
        tab.HandleInput(Key(InputKey.Escape)).ShouldBeTrue();
        bus.ProcessPending(new BackgroundTaskTracker());

        signals.ShouldBe([
            new AnswerControlRequestSignal("request-1", Allow: true),
            new AnswerControlRequestSignal("request-1", Allow: false),
            new DismissControlRequestSignal(),
        ]);
    }

    [Fact]
    public void TheQuitsQuestionComesFirst()
    {
        var bus = new SignalBus();
        var signals = Signals(bus);
        using var renderer = new RgbaImageRenderer(1280, 800);
        var tab = new LiveSessionTab<RgbaImage>(renderer) { DpiScale = 1f, FontPath = FontResolver.ResolveSystemFont(), Bus = bus };
        var state = new LiveSessionState { ControlRequest = Request, QuitDialog = QuitDialog.DevicesConnected(1, aCameraNeedsWarming: true) };
        tab.Render(state, new RectF32(0f, 0f, 1280f, 800f), new SystemTimeProvider());

        tab.HandleInput(Key(InputKey.Enter)).ShouldBeTrue();
        bus.ProcessPending(new BackgroundTaskTracker());

        signals.ShouldBe([new AnswerQuitSignal(QuitAction.WarmUpAndDisconnect)], "Enter answered the quit, and declined nobody");
    }

    [Fact]
    public void TheTuiTabsRequestComesBeforeItsPreviewKeys()
    {
        var bus = new SignalBus();
        var signals = Signals(bus);
        var contexts = new ViewContexts();
        var terminal = Substitute.For<IVirtualTerminal>();
        terminal.Size.Returns((120, 40));
        terminal.CellSize.Returns(new TermCell(1, 1));
        var tab = new TuiLiveSessionTab(new GuiAppState(), contexts, terminal, new FakeTimeProviderWrapper(), bus);
        tab.Attach(terminal);
        contexts.Local.LiveSession.ControlRequest = Request;

        tab.HandleInput(Key(InputKey.A));
        tab.HandleInput(Key(InputKey.Enter));
        tab.HandleInput(Key(InputKey.Escape));
        bus.ProcessPending(new BackgroundTaskTracker());

        signals.ShouldBe([
            new AnswerControlRequestSignal("request-1", Allow: true),
            new AnswerControlRequestSignal("request-1", Allow: false),
            new DismissControlRequestSignal(),
        ], "Enter declined rather than capturing a frame");
    }
}
