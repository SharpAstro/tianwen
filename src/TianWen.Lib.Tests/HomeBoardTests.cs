using Shouldly;
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
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

        /// <summary>
        /// Puts a run on the local context: <paramref name="written"/> frames of the active target already in
        /// the log, out of a plan asking for <paramref name="planned"/> per OTA.
        /// </summary>
        private static LiveSessionState RunningSession(
            ViewContexts contexts, int written, int planned, int scheduled = 1, int activeIndex = 0)
        {
            var session = contexts.Local.LiveSession;
            session.IsRunning = true;
            session.Phase = SessionPhase.Observing;
            session.CurrentObservationIndex = activeIndex;
            session.ObservationCount = scheduled;

            var observations = ImmutableArray.CreateBuilder<ScheduledObservation>(scheduled);
            for (var i = 0; i < scheduled; i++)
            {
                observations.Add(new ScheduledObservation(
                    new Target(1.0, 2.0, $"T{i}", null), Now, TimeSpan.FromHours(1), false,
                    FilterPlan: [new FilterExposure(-1, TimeSpan.FromSeconds(300), planned)],
                    Gain: null, Offset: null));
            }

            var tree = new ScheduledObservationTree(observations.ToImmutable());
            session.ActiveObservation = tree[activeIndex];
            // The one OTA's worth of state the progress denominator scales by.
            session.CameraStates = [default];

            var log = ImmutableArray.CreateBuilder<ExposureLogEntry>(written);
            for (var i = 0; i < written; i++)
            {
                log.Add(new ExposureLogEntry(
                    Now, tree[activeIndex].Target.Name, "L", TimeSpan.FromSeconds(300), i + 1, 3.1f, 400));
            }
            session.ExposureLog = log.ToImmutable();

            return session;
        }

        [Fact]
        public void ProgressCountsTheCurrentTargetsFramesNotTheWholeNights()
        {
            var (contexts, rigs, appState) = Board();
            var session = RunningSession(contexts, written: 23, planned: 100, scheduled: 3, activeIndex: 1);

            // Frames from an EARLIER target sit ahead of the current one's in the log. A session total would
            // fold them in and report 31/100 for a target that has taken 23.
            session.ExposureLog = session.ExposureLog.InsertRange(0, ImmutableArray.Create(
                new ExposureLogEntry(Now - TimeSpan.FromHours(2), "T0", "L", TimeSpan.FromSeconds(300), 1, 3.0f, 380),
                new ExposureLogEntry(Now - TimeSpan.FromHours(2), "T0", "L", TimeSpan.FromSeconds(300), 2, 3.0f, 380)));

            var progress = HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Progress.ShouldNotBeNull();

            progress.TargetIndex.ShouldBe(2);
            progress.TargetCount.ShouldBe(3);
            progress.FramesDone.ShouldBe(23);
            progress.FramesPlanned.ShouldBe(100);
            progress.Describe().ShouldBe("target 2/3 · frame 23/100");
        }

        [Fact]
        public void ProgressDropsThePartsItCannotHonestlyState()
        {
            // No filter plan means no denominator, and inventing one would misreport how far along a rig is.
            new RigCardProgress(TargetIndex: 2, TargetCount: 3, FramesDone: 23, FramesPlanned: 0)
                .Describe().ShouldBe("target 2/3 · 23 frames");

            // No schedule (a single ad-hoc target) drops the target part rather than reading "1/0".
            new RigCardProgress(TargetIndex: 0, TargetCount: 0, FramesDone: 23, FramesPlanned: 100)
                .Describe().ShouldBe("frame 23/100");
        }

        [Fact]
        public void TheDenominatorScalesWithTheOtasThatAreShooting()
        {
            var (contexts, rigs, appState) = Board();
            var session = RunningSession(contexts, written: 10, planned: 50);
            // A dual-OTA rig works the same plan on both cameras, and the log counts both OTAs' frames, so
            // the planned side has to scale too or a two-scope rig reads as twice as far along as it is.
            session.CameraStates = [default, default];

            var progress = HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Progress.ShouldNotBeNull();

            progress.FramesPlanned.ShouldBe(100);
        }

        [Fact]
        public void CoolingReportsTheCameraFurthestFromItsSetpoint()
        {
            var (contexts, rigs, appState) = Board();
            var session = contexts.Local.LiveSession;
            session.Phase = SessionPhase.Cooling;
            session.CoolingSamples =
            [
                // Older samples for both cameras, then a newer one each -- the newest per camera wins.
                new CoolingSample(Now - TimeSpan.FromMinutes(5), 0, 5.0, -10.0, 100.0),
                new CoolingSample(Now - TimeSpan.FromMinutes(5), 1, 5.0, -10.0, 100.0),
                new CoolingSample(Now, 0, -9.6, -10.0, 41.0),
                new CoolingSample(Now, 1, -2.4, -10.0, 100.0),
            ];

            var cooling = HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldNotBeNull();

            // A rig is ready when its LAST camera is, so the lagging one is what the card reports.
            cooling.TemperatureC.ShouldBe(-2.4, 0.001);
            cooling.CameraCount.ShouldBe(2);
            cooling.AtSetpoint.ShouldBe(1);
            cooling.IsSettled.ShouldBeFalse();
            cooling.Describe().ShouldBe("-2.4 → -10.0°C · 100% · 1/2 cameras");
        }

        [Fact]
        public void ASettledRigSaysSoOnceTheSessionHasStoppedRamping()
        {
            var (contexts, rigs, appState) = Board();
            var session = contexts.Local.LiveSession;
            session.CoolingSamples = [new CoolingSample(Now, 0, -9.9, -10.0, 38.0)];

            // Phase is NOT Cooling, so the ramp is over and the numbers agree.
            var settled = HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldNotBeNull();
            settled.IsSettled.ShouldBeTrue();
            settled.Describe().ShouldBe("at -10.0°C · 38%");

            // Same numbers while the session says it is still ramping: its own answer outranks the arithmetic,
            // so "finished" is never reported early.
            session.Phase = SessionPhase.Cooling;
            HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldNotBeNull()
                .IsSettled.ShouldBeFalse();
        }

        [Fact]
        public void ACameraWithNoSetpointDoesNotHideOneThatIsLagging()
        {
            var (contexts, rigs, appState) = Board();
            var session = contexts.Local.LiveSession;
            session.Phase = SessionPhase.Cooling;
            session.CoolingSamples =
            [
                // Camera 0 reports no setpoint (uncooled guide camera), and is seen FIRST. Its delta is NaN,
                // and every NaN comparison is false -- so a naive "is this delta the biggest" check leaves it
                // holding "worst" forever and the card reports the wrong camera's temperature.
                new CoolingSample(Now, 0, 18.0, double.NaN, double.NaN),
                new CoolingSample(Now, 1, -2.4, -10.0, 100.0),
            ];

            var cooling = HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldNotBeNull();

            cooling.TemperatureC.ShouldBe(-2.4, 0.001);
            cooling.SetpointC.ShouldBe(-10.0, 0.001);
            // Neither is at setpoint: one is ramping and the other has no setpoint to be at, so the rig
            // cannot be called ready.
            cooling.AtSetpoint.ShouldBe(0);
            cooling.IsSettled.ShouldBeFalse();
        }

        [Fact]
        public void ALoneCameraWithNoSetpointStillReportsItsTemperatureWithoutImplyingATarget()
        {
            var (contexts, rigs, appState) = Board();
            contexts.Local.LiveSession.CoolingSamples =
                [new CoolingSample(Now, 0, 18.3, double.NaN, double.NaN)];

            var cooling = HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldNotBeNull();

            // "18.3 -> NaN" would be nonsense and "18.3 -> 0.0" would be a lie about a setpoint nobody set.
            cooling.Describe().ShouldBe("18.3°C");
        }

        [Fact]
        public void NoCoolingSamplesMeansNoCoolingRowRatherThanZeroes()
        {
            var (contexts, rigs, appState) = Board();

            // "0.0 -> 0.0C" would read as a camera sitting at ambient with the cooler off.
            HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldBeNull();
        }

        [Fact]
        public void AnHoursOldRampSampleIsNotPresentedAsTheCurrentTemperature()
        {
            var (contexts, rigs, appState) = Board();
            var session = contexts.Local.LiveSession;
            // The ramp is the ONLY writer of cooling samples, and it stops when it finishes -- so mid-night
            // the newest sample is from the start of the night. Showing it would report where the camera was
            // hours ago as where it is now, the same class of lie as live-looking counters from a finished run.
            session.CoolingSamples = [new CoolingSample(Now - TimeSpan.FromHours(3), 0, -9.9, -10.0, 38.0)];
            session.Phase = SessionPhase.Observing;

            HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldBeNull();

            // While the ramp is actually running, age is irrelevant -- the phase is the rig saying so.
            session.Phase = SessionPhase.Cooling;
            HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].Cooling.ShouldNotBeNull();
        }

        [Fact]
        public void TheFlipCountdownIsResolvedAgainstTheReadersClockNotStored()
        {
            var card = HomeBoard.BuildCards(new ViewContexts(), new RemoteRigRegistry(), new GuiAppState(), Now)[0]
                with { MeridianFlipUtc = Now + TimeSpan.FromMinutes(35) };

            // The same card read later reports less time, which is the whole reason the instant is what is
            // stored and carried: a duration would freeze at whatever the last poll computed.
            card.TimeToMeridianFlip(Now).ShouldNotBeNull().TotalMinutes.ShouldBe(35, 0.001);
            card.TimeToMeridianFlip(Now + TimeSpan.FromMinutes(20)).ShouldNotBeNull().TotalMinutes.ShouldBe(15, 0.001);

            // Past due is not a negative countdown.
            card.TimeToMeridianFlip(Now + TimeSpan.FromMinutes(40)).ShouldBeNull();
            (card with { MeridianFlipUtc = null }).TimeToMeridianFlip(Now).ShouldBeNull();
        }

        [Fact]
        public void TheLastNoteSurvivesAnActivityStringThatHasMovedOn()
        {
            var (contexts, rigs, appState) = Board();
            appState.AppendNotification(
                Now - TimeSpan.FromMinutes(9), NotificationSeverity.Warning, "Guide star lost, recovering");
            appState.AppendNotification(
                Now - TimeSpan.FromMinutes(3), NotificationSeverity.Info, "Plate solve succeeded");

            var note = HomeBoard.BuildCards(contexts, rigs, appState, Now)[0].LastNote.ShouldNotBeNull();

            // The app's feed is newest-FIRST (the node's wire ring is oldest-first) -- indexing one as the
            // other silently shows the oldest thing the rig ever said.
            note.Message.ShouldBe("Plate solve succeeded");
            note.Severity.ShouldBe(NotificationSeverity.Info);
        }

        [Fact]
        public void ANoteWithNoTimestampReadsWithoutAnAge()
        {
            // Unknown must render as unknown, the same rule the prompt badge follows.
            new RigCardNote(NotificationSeverity.Error, "Mount lost", null).Describe().ShouldBe("Mount lost");
            new RigCardNote(NotificationSeverity.Error, "Mount lost", TimeSpan.FromMinutes(3)).Describe()
                .ShouldEndWith("· Mount lost");
        }
    }
}
