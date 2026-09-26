using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A prompt is held only while a client can SEE it (P1 of docs/plans/hardware-in-the-server.md, #917). A socket used
/// to be enough, and a window frozen by a GPU wedge keeps its socket open, so it held a prompt, and the night, for ever.
/// The real client beats from the loop that draws it (<see cref="TianWenEventStream.Beat"/>); these drive that from a
/// stand-in loop against a real node and stop it without closing the socket, which is what a frozen window looks like.
/// </summary>
[Collection("Hosting")]
#pragma warning disable CS8774 // MemberNotNull on InitializeAsync; xUnit guarantees init before tests
#pragma warning disable CS8602 // Dereference of possibly null; same reason
public class NodePresenceTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness? _harness;

    [MemberNotNull(nameof(_harness))]
    public async ValueTask InitializeAsync() => _harness = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task APromptWaitsForAWindowThatKeepsDrawingAndIsLetGoOnceItFreezes()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var run = await _harness.StartSessionAsync(ct);
        var hub = _harness.App.Services.GetRequiredService<EventHub>();

        await using var stream = new TianWenEventStream(
            _harness.Client.BaseAddress ?? throw new InvalidOperationException("the harness client has no base address"),
            new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.Start(ct);

        // The window's loop: a beat every iteration, as the GUI and the TUI do, until it freezes.
        using var frozen = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drawing = Task.Run(async () =>
        {
            while (!frozen.IsCancellationRequested)
            {
                stream.Beat();
                await Task.Delay(16, CancellationToken.None);
            }
        }, CancellationToken.None);

        for (var i = 0; i < 250 && hub.PromptObserverCount == 0; i++)
        {
            await Task.Delay(20, ct);
        }
        hub.PromptObserverCount.ShouldBe(1, "a connected, drawing window can see a prompt");

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new SessionPromptEventArgs(
            "Manual flat panel", "Switch on the flat panel for OTA 1, then Continue.",
            "Continue", "Cancel", answer, requiresPhysicalPresence: true, defaultIfUnanswerable: false);
        run.Session.PromptRequested += Raise.EventWith(run.Session, prompt);

        // Longer than the lapse: a window that keeps drawing is waited for, with no timer, however long its human takes.
        await Task.Delay(NodeWire.PresenceLapse + TimeSpan.FromSeconds(2), ct);
        answer.Task.IsCompleted.ShouldBeFalse("the window is drawing, so its human may still answer");
        _harness.Node.PendingPrompt.ShouldBeSameAs(prompt);

        // It freezes: the loop stops, the socket stays open.
        await frozen.CancelAsync();
        await drawing;
        var frozeAt = DateTimeOffset.UtcNow;

        var proceeded = await answer.Task.WaitAsync(NodeWire.PresenceLapse + TimeSpan.FromSeconds(5), ct);

        proceeded.ShouldBeFalse("the session's own unattended answer, which declines a physical act");
        (DateTimeOffset.UtcNow - frozeAt).ShouldBeGreaterThan(NodeWire.PresenceLapse - NodeWire.PresenceBeatInterval,
            "a few missed beats are not a frozen window");
        _harness.Node.PendingPrompt.ShouldBeNull();
        stream.IsConnected.ShouldBeTrue("its socket never closed; only its beats stopped");
    }

    [Fact(Timeout = 30_000)]
    public async Task AClientThatListensButNeverDrawsHoldsNothing()
    {
        // An event stream alone (a script, a log tail, a window that never got a frame up) is nobody to wait for.
        var ct = TestContext.Current.CancellationToken;
        _harness.Factory.Initialised.SetResult();
        var run = await _harness.StartSessionAsync(ct);
        var hub = _harness.App.Services.GetRequiredService<EventHub>();

        await using var stream = new TianWenEventStream(
            _harness.Client.BaseAddress ?? throw new InvalidOperationException("the harness client has no base address"),
            new SystemTimeProvider(), FakeExternal.CreateLogger(outputHelper));
        stream.Start(ct);
        for (var i = 0; i < 250 && hub.NativeClientCount == 0; i++)
        {
            await Task.Delay(20, ct);
        }
        hub.NativeClientCount.ShouldBe(1, "it is attached");

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Session.PromptRequested += Raise.EventWith(run.Session, new SessionPromptEventArgs(
            "Manual flat panel", "Switch on the flat panel for OTA 1, then Continue.",
            "Continue", "Cancel", answer, requiresPhysicalPresence: true, defaultIfUnanswerable: false));

        answer.Task.IsCompletedSuccessfully.ShouldBeTrue("answered at once, as with nobody attached");
        (await answer.Task).ShouldBeFalse();
    }
}
