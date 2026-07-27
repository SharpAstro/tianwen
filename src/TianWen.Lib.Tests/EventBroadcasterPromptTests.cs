using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The node's prompt-handling policy. This is the most safety-relevant logic in the hosted surface and
    /// it was previously verified only by reading it.
    /// <para>
    /// <b>What is actually at stake.</b> A session answers a user prompt itself only while <i>nothing</i>
    /// is subscribed to <c>PromptRequested</c>. <see cref="EventBroadcaster"/> subscribes on behalf of
    /// remote clients, which silently converts a server from "never blocks" to "blocks until cancelled" --
    /// and the prompt await sits inside <c>RunAsync</c>'s try, whose finally is what parks the mount,
    /// warms the cameras and closes the covers. A prompt nothing answers does not throw; it simply never
    /// returns, so the rig would sit unparked with the covers open at dawn. Every test here pins one of
    /// the guarantees that keeps that from happening.
    /// </para>
    /// </summary>
    public class EventBroadcasterPromptTests
    {
        private static (EventBroadcaster Broadcaster, HostedSession Host, EventHub Hub) Build()
        {
            var host = new HostedSession(Substitute.For<ISessionFactory>());
            var hub = new EventHub();
            var enhancer = new HostedImageEnhancer(pipeline: null, NullLogger<HostedImageEnhancer>.Instance);
            var broadcaster = new EventBroadcaster(
                host, enhancer, hub,
                Substitute.For<ITimeProvider>(),
                NullLogger<EventBroadcaster>.Instance);
            return (broadcaster, host, hub);
        }

        private static (SessionPromptEventArgs Prompt, Task<bool> Answer) MakePrompt(
            bool requiresPhysicalPresence = true, bool defaultIfUnanswerable = false)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = new SessionPromptEventArgs(
                "Manual flat panel",
                "Switch on the flat panel for telescope #1 (OTA 1) and set a comfortable brightness, then Continue.",
                "Continue", "Cancel", completion,
                requiresPhysicalPresence, defaultIfUnanswerable);
            return (prompt, completion.Task);
        }

        /// <summary>
        /// A stand-in observer socket.
        /// <para>
        /// <b>It must report <see cref="WebSocketState.Open"/>.</b> <c>EventHub</c> evicts any client whose
        /// state is not Open the first time it broadcasts, and raising a prompt broadcasts (the
        /// notification). A bare <c>new ClientWebSocket()</c> sits in state <c>None</c>, so it gets pruned
        /// mid-test and the client count silently drops to zero -- which then makes the liveness check fire
        /// and answer the prompt. That pruning is correct product behaviour (a dead socket is not an
        /// observer), and because the broadcast is fire-and-forget it lands non-deterministically, so an
        /// unopened socket makes these tests flaky rather than merely wrong.
        /// </para>
        /// </summary>
        private static WebSocket FakeClient()
        {
            var socket = Substitute.For<WebSocket>();
            socket.State.Returns(WebSocketState.Open);
            return socket;
        }

        // --- Nobody attached: behave exactly as an unsubscribed session would --------------------------

        [Fact]
        public async Task WithNoObserverAttachedThePromptIsAnsweredImmediatelyWithTheSessionsOwnDefault()
        {
            // The guarantee that keeps an unattended run from blocking: attaching an event broadcaster
            // must not change the outcome of a run nobody is watching.
            var (broadcaster, host, _) = Build();
            var (prompt, answer) = MakePrompt(defaultIfUnanswerable: false);

            broadcaster.OnPromptRequested(this, prompt);

            answer.IsCompletedSuccessfully.ShouldBeTrue();
            (await answer).ShouldBeFalse("declining skips the gated step, which is the safe default");
            host.PendingPrompt.ShouldBeNull("nothing is outstanding, so no client should be offered it");
        }

        [Fact]
        public async Task AnOperatorInvokedRunStillProceedsWhenNobodyIsThereToAnswer()
        {
            // tianwen flats / POST /session/flats opt into Proceed, because the operator who asked for
            // the run may have switched a hand-switched panel on and walked back inside. The policy rides
            // on the prompt, so this class never has to know which caller it was.
            var (broadcaster, host, _) = Build();
            var (prompt, answer) = MakePrompt(defaultIfUnanswerable: true);

            broadcaster.OnPromptRequested(this, prompt);

            answer.IsCompletedSuccessfully.ShouldBeTrue();
            (await answer).ShouldBeTrue();
            host.PendingPrompt.ShouldBeNull();
        }

        // --- Observer attached: hold indefinitely, and surface it ------------------------------------

        [Fact]
        public void WithAnObserverAttachedThePromptIsHeldForThemRatherThanGuessed()
        {
            var (broadcaster, host, hub) = Build();
            hub.AddClient(FakeClient());
            var (prompt, answer) = MakePrompt();

            broadcaster.OnPromptRequested(this, prompt);

            // Deliberately NOT answered: somebody can answer, so we wait for them however long it takes.
            // There is no timer to expire -- an attached client that ignores the prompt is a client bug,
            // and fabricating an answer after an arbitrary interval would not fix it.
            answer.IsCompleted.ShouldBeFalse();
            host.PendingPrompt.ShouldBeSameAs(prompt);
        }

        [Fact]
        public void APromptNeedingSomebodyAtTheRigIsRecordedAsAnErrorSayingSo()
        {
            // The warning a remote observer needs: they cannot clear this one themselves, however many
            // buttons their UI offers. Severity is Error, not Warning, because the run is stopped.
            var (broadcaster, host, hub) = Build();
            hub.AddClient(FakeClient());
            var (prompt, _) = MakePrompt(requiresPhysicalPresence: true);

            broadcaster.OnPromptRequested(this, prompt);

            var notifications = host.Notifications;
            notifications.Length.ShouldBe(1);
            notifications[0].Severity.ShouldBe("Error");
            notifications[0].Message.ShouldContain("needs someone at the rig");
            notifications[0].Message.ShouldContain("Manual flat panel");
        }

        [Fact]
        public void APromptThatDoesNotNeedPhysicalPresenceOmitsThatWarning()
        {
            var (broadcaster, host, hub) = Build();
            hub.AddClient(FakeClient());
            var (prompt, _) = MakePrompt(requiresPhysicalPresence: false);

            broadcaster.OnPromptRequested(this, prompt);

            var notifications = host.Notifications;
            notifications.Length.ShouldBe(1);
            notifications[0].Message.ShouldNotContain("needs someone at the rig");
        }

        // --- Liveness: the observer going away must not leave the run wedged ------------------------

        [Fact]
        public async Task APromptOutstandingWhenTheLastObserverLeavesIsResolvedNotLeftHanging()
        {
            // The wedge reached by a different route: a client attaches, triggers the hold, then drops its
            // socket. Nobody is left who can answer, so the liveness check has to close it -- otherwise
            // Finalise never runs and the rig stays exposed.
            var (broadcaster, host, hub) = Build();
            var clientId = hub.AddClient(FakeClient());
            var (prompt, answer) = MakePrompt(defaultIfUnanswerable: false);

            broadcaster.OnPromptRequested(this, prompt);
            answer.IsCompleted.ShouldBeFalse();

            hub.RemoveClient(clientId);
            broadcaster.ResolveOrphanedPrompt();

            answer.IsCompletedSuccessfully.ShouldBeTrue();
            (await answer).ShouldBeFalse();
            host.PendingPrompt.ShouldBeNull();
        }

        [Fact]
        public void TheLivenessCheckLeavesAPromptAloneWhileAnObserverIsStillThere()
        {
            // It runs on every poll tick, so it must be inert in the normal case.
            var (broadcaster, host, hub) = Build();
            hub.AddClient(FakeClient());
            var (prompt, answer) = MakePrompt();

            broadcaster.OnPromptRequested(this, prompt);

            broadcaster.ResolveOrphanedPrompt();
            broadcaster.ResolveOrphanedPrompt();

            answer.IsCompleted.ShouldBeFalse();
            host.PendingPrompt.ShouldBeSameAs(prompt);
        }

        [Fact]
        public async Task AClientsAnswerWinsAndTheLivenessCheckThenDoesNothing()
        {
            var (broadcaster, host, hub) = Build();
            var clientId = hub.AddClient(FakeClient());
            var (prompt, answer) = MakePrompt(defaultIfUnanswerable: false);

            broadcaster.OnPromptRequested(this, prompt);

            // The client says "yes, the panel is on" -- the opposite of the unattended default, so a
            // later resolve overwriting it would be visible.
            host.TryRespondToPrompt(true).ShouldBeTrue();

            hub.RemoveClient(clientId);
            broadcaster.ResolveOrphanedPrompt();

            answer.IsCompletedSuccessfully.ShouldBeTrue();
            (await answer).ShouldBeTrue("the human's answer must not be overwritten by the fallback");
            host.PendingPrompt.ShouldBeNull();
        }

        [Fact]
        public void TheLivenessCheckIsSafeWithNoPromptOutstanding()
        {
            var (broadcaster, _, _) = Build();

            Should.NotThrow(() => broadcaster.ResolveOrphanedPrompt());
        }
    }
}
