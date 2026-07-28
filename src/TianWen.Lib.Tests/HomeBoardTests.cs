using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the home screen's rig board (docs/plans/remote-profile.md, "Multi-rig dashboard").
    /// <para>
    /// Card CONTENT is the thing worth pinning rather than layout: the board exists to answer "is anything
    /// waiting on me" across every rig at once, and each of the ways it could quietly answer wrongly --
    /// dropping an offline rig, ageing a prompt from the wrong instant, presenting a finished night's
    /// counters as live -- is a silent failure that looks fine on screen.
    /// </para>
    /// </summary>
    public class HomeBoardTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 7, 28, 22, 0, 0, TimeSpan.Zero);

        private static RemoteRigBinding Binding(string alias, DateTimeOffset? lastSeen = null) =>
            new RemoteRigBinding
            {
                BindingId = Guid.NewGuid(),
                NodeId = $"node-{alias}",
                Alias = alias,
                LastSeenUtc = lastSeen,
            };

        private static SessionPromptEventArgs Prompt(DateTimeOffset? raisedUtc) =>
            new SessionPromptEventArgs(
                "Manual flat panel", "Switch it on, then Continue.", "Continue", "Cancel",
                new TaskCompletionSource<bool>(), requiresPhysicalPresence: true, raisedUtc: raisedUtc);

        private static (ViewContexts Contexts, RemoteRigRegistry Rigs, GuiAppState AppState) Board() =>
            (new ViewContexts(), new RemoteRigRegistry(), new GuiAppState());

        [Fact]
        public void TheLocalNodeIsTheFirstCardAndNeedsNothingConnected()
        {
            var (contexts, rigs, appState) = Board();

            var cards = HomeBoard.BuildCards(contexts, rigs, appState, Now);

            // The local node is a card like any other -- that is what stops a single-scope user's board
            // looking like a remote-monitoring feature they do not use.
            cards.Length.ShouldBe(1);
            cards[0].IsLocal.ShouldBeTrue();
            cards[0].IsOnline.ShouldBeTrue("it is this machine; 'not answering' is not a state it can be in");
            cards[0].Status.ShouldBe("Idle");
            // No driver is connected and none needs to be: the card reads LiveSessionState, so an idle
            // local scope is an accurate free card and the board performs no device I/O to draw it.
            cards[0].IsRunning.ShouldBeFalse();
        }

        [Fact]
        public void ABoundRigWithNoConnectionStaysOnTheBoardAndSaysHowStaleItIs()
        {
            var (contexts, rigs, appState) = Board();
            rigs.SetBindings([Binding("Backyard", lastSeen: Now - TimeSpan.FromHours(6))]);

            var cards = HomeBoard.BuildCards(contexts, rigs, appState, Now);

            var rig = cards[1];
            rig.IsOnline.ShouldBeFalse();
            // Listed, not hidden: "I own this rig and it is not there" is information, and a missing card
            // reads as a lost binding.
            rig.Title.ShouldBe("Backyard");
            rig.Status.ShouldBe("Offline (last seen 6 h ago)");
        }

        [Fact]
        public void RigsAfterTheLocalCardAreOrderedByNameNotByBindingOrder()
        {
            var (contexts, rigs, appState) = Board();
            // Deliberately reverse-alphabetical, i.e. load order that must NOT survive: a board whose cards
            // moved between runs would make the wrong rig the one you click from muscle memory.
            rigs.SetBindings([Binding("Roof"), Binding("Backyard"), Binding("Mountain")]);

            var cards = HomeBoard.BuildCards(contexts, rigs, appState, Now);

            cards[0].IsLocal.ShouldBeTrue();
            cards[1].Title.ShouldBe("Backyard");
            cards[2].Title.ShouldBe("Mountain");
            cards[3].Title.ShouldBe("Roof");
        }

        [Fact]
        public void AnOutstandingPromptBecomesABadgeCarryingHowLongItHasWaited()
        {
            var (contexts, rigs, appState) = Board();
            contexts.Local.LiveSession.PendingPrompt = Prompt(Now - TimeSpan.FromMinutes(40));

            var cards = HomeBoard.BuildCards(contexts, rigs, appState, Now);

            var prompt = cards[0].Prompt.ShouldNotBeNull();
            prompt.Waiting.ShouldBe(TimeSpan.FromMinutes(40));
            prompt.RequiresPhysicalPresence.ShouldBeTrue();
            // The duration is the whole point of the badge: a prompt holds a run open with no timer, so
            // "outstanding" is unremarkable and "outstanding for forty minutes" is the problem.
            prompt.Describe().ShouldContain("40");
        }

        [Fact]
        public void APromptWithNoRaisedInstantBadgesWithoutInventingADuration()
        {
            var (contexts, rigs, appState) = Board();
            contexts.Local.LiveSession.PendingPrompt = Prompt(raisedUtc: null);

            var cards = HomeBoard.BuildCards(contexts, rigs, appState, Now);

            var prompt = cards[0].Prompt.ShouldNotBeNull();
            prompt.Waiting.ShouldBeNull("an unknown age must stay unknown rather than being filled in");
            // Still badged: that a rig is blocked is the part that must never be dropped.
            prompt.Describe().ShouldBe("WAITING");
        }

        [Fact]
        public void AFinishedRunIsNotReportedAsRunning()
        {
            var (contexts, rigs, appState) = Board();
            var session = contexts.Local.LiveSession;
            session.Phase = SessionPhase.Complete;
            session.TotalFramesWritten = 412;

            var cards = HomeBoard.BuildCards(contexts, rigs, appState, Now);

            // The counters are real but they belong to a night that is over; presenting them as live would
            // be the card's one outright lie.
            cards[0].IsRunning.ShouldBeFalse();
            cards[0].FramesWritten.ShouldBe(412);
        }
    }
}
