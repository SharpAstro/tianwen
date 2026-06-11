using System;
using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Unit tests for <see cref="Session.DecideGuiderIntervention"/> -- the rule the imaging loop
    /// uses to react to guider health. Pins the #4-vs-#3 fix: while the guider recovers a lock in
    /// place ("Calibrating"/"Settling") the loop must DEFER, not restart guiding (the old behavior
    /// fought the driver's own bounded recovery and rescheduled targets that were recovering fine).
    /// </summary>
    public class SessionGuiderInterventionTests
    {
        private static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

        [Fact]
        public void WhenGuiding_Proceeds()
        {
            // Guiding -> image, regardless of any stale state string or elapsed time.
            Session.DecideGuiderIntervention(isGuiding: true, guiderState: "Guiding", recoveringFor: TimeSpan.Zero, recoveryGrace: Grace)
                .ShouldBe(Session.GuiderInterventionAction.Proceed);
            Session.DecideGuiderIntervention(isGuiding: true, guiderState: "Calibrating", recoveringFor: TimeSpan.FromHours(1), recoveryGrace: Grace)
                .ShouldBe(Session.GuiderInterventionAction.Proceed);
        }

        [Fact]
        public void WhenRecalibratingWithinGrace_DefersInsteadOfRestarting()
        {
            // THE regression: a divergence recalibration reports "Calibrating". The old loop saw
            // !IsGuiding and restarted -> GuideAsync threw "cannot start in state Calibrating" ->
            // reschedule. It must defer instead.
            Session.DecideGuiderIntervention(isGuiding: false, guiderState: "Calibrating", recoveringFor: TimeSpan.FromSeconds(20), recoveryGrace: Grace)
                .ShouldBe(Session.GuiderInterventionAction.DeferForRecovery);
        }

        [Fact]
        public void WhenSettlingWithinGrace_Defers()
        {
            // A re-acquire / dither settle reports "Settling" -- also in-place recovery, defer.
            Session.DecideGuiderIntervention(isGuiding: false, guiderState: "Settling", recoveringFor: TimeSpan.FromSeconds(20), recoveryGrace: Grace)
                .ShouldBe(Session.GuiderInterventionAction.DeferForRecovery);
        }

        [Fact]
        public void WhenRecoveringPastGrace_Restarts()
        {
            // Backstop: a never-completing settle (the driver's TryCompleteSettle has no
            // settle-timeout-to-Idle path) must eventually force a clean restart, not defer forever.
            Session.DecideGuiderIntervention(isGuiding: false, guiderState: "Calibrating", recoveringFor: Grace, recoveryGrace: Grace)
                .ShouldBe(Session.GuiderInterventionAction.Restart);
            Session.DecideGuiderIntervention(isGuiding: false, guiderState: "Settling", recoveringFor: Grace + TimeSpan.FromSeconds(1), recoveryGrace: Grace)
                .ShouldBe(Session.GuiderInterventionAction.Restart);
        }

        [Theory]
        [InlineData("Stopped")]
        [InlineData("Looping")]
        [InlineData("Unknown")]
        [InlineData(null)]
        public void WhenStoppedOrNotRecoveringInPlace_Restarts(string? guiderState)
        {
            // "Stopped" = the driver gave up (and raised a guiding error); "Looping"/"Unknown"/null
            // are not an in-place lock recovery -- the session must (re)start guiding immediately,
            // not wait out the grace window.
            Session.DecideGuiderIntervention(isGuiding: false, guiderState: guiderState, recoveringFor: TimeSpan.Zero, recoveryGrace: Grace)
                .ShouldBe(Session.GuiderInterventionAction.Restart);
        }
    }
}
