using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="SessionNotes"/>, the ONE mapping from a run's events to the notes a person reads, for the GUI's and the TUI's
/// bootstrappers and a node's feed alike (P5b part 4b).
/// </summary>
public class SessionNotesTests
{
    [Theory]
    [InlineData(SessionPhase.Initialising)]
    [InlineData(SessionPhase.WaitingForDark)]
    [InlineData(SessionPhase.Cooling)]
    [InlineData(SessionPhase.RoughFocus)]
    [InlineData(SessionPhase.AutoFocus)]
    [InlineData(SessionPhase.CalibratingGuider)]
    [InlineData(SessionPhase.Observing)]
    [InlineData(SessionPhase.Finalising)]
    public void EveryWorkingPhaseSaysWhereTheNightIs(SessionPhase phase)
        => SessionNotes.ForPhase(phase).ShouldNotBeNull().Severity.ShouldBe(NotificationSeverity.Info);

    [Theory]
    [InlineData(SessionPhase.NotStarted)]
    [InlineData(SessionPhase.Complete)]
    [InlineData(SessionPhase.Aborted)]
    [InlineData(SessionPhase.Failed)]
    public void AnEndingIsNotedWhenTheRunHasEndedNotAtItsPhase(SessionPhase phase)
        => SessionNotes.ForPhase(phase).ShouldBeNull("the terminal phase comes before Finalise; the end is ForRunEnd's");

    [Fact]
    public void ARunsEndCarriesItsFailureInTheWordsItGave()
    {
        SessionNotes.ForRunEnd(SessionPhase.Complete, null).ShouldBe(new SessionNote(NotificationSeverity.Info, "Session complete"));
        SessionNotes.ForRunEnd(SessionPhase.Aborted, null).ShouldBe(new SessionNote(NotificationSeverity.Warning, "Session aborted"));
        SessionNotes.ForRunEnd(SessionPhase.Failed, "Check the mount's cable").ShouldBe(
            new SessionNote(NotificationSeverity.Error, "Session failed: Check the mount's cable"));
        SessionNotes.ForRunEnd(SessionPhase.Failed, null).ShouldBe(new SessionNote(NotificationSeverity.Error, "Session failed"));
        SessionNotes.ForFlatRunEnd(SessionPhase.Aborted, null).ShouldBe(new SessionNote(NotificationSeverity.Warning, "Flat run cancelled"));
    }

    [Fact]
    public void AHealthyScoutAndOrdinaryGuiderChurnStayQuiet()
    {
        SessionNotes.ForScout(Scout(ScoutClassification.Healthy, ScoutOutcome.Proceed)).ShouldBeNull();
        SessionNotes.ForGuiderTransition("Guiding", "Settling").ShouldBeNull();
        SessionNotes.ForGuiderTransition("Guiding", "LostLock").ShouldNotBeNull().Severity.ShouldBe(NotificationSeverity.Warning);
        SessionNotes.ForGuiderTransition("LostLock", "Guiding").ShouldNotBeNull().Severity.ShouldBe(NotificationSeverity.Info);
    }

    [Fact]
    public void AScoutThatMovesOnSaysWhyAndForHowLong()
    {
        var note = SessionNotes.ForScout(Scout(ScoutClassification.Obstruction, ScoutOutcome.Advance, TimeSpan.FromMinutes(25))).ShouldNotBeNull();

        note.Severity.ShouldBe(NotificationSeverity.Warning);
        note.Message.ShouldContain("M42");
        note.Message.ShouldContain("12/15 stars");
        note.Message.ShouldContain("25 min");
    }

    [Fact]
    public void NoNoteCarriesAnEmDash()
    {
        // The writing rule, over everything the mapping can say.
        var notes = Enum.GetValues<SessionPhase>().SelectMany(p => new[]
            {
                SessionNotes.ForPhase(p), SessionNotes.ForRunEnd(p, "why"), SessionNotes.ForFlatRunEnd(p, "why"),
            })
            .Concat(Enum.GetValues<ScoutClassification>().SelectMany(c => Enum.GetValues<ScoutOutcome>().SelectMany(o => new[]
            {
                SessionNotes.ForScout(Scout(c, o, TimeSpan.FromMinutes(5))), SessionNotes.ForScout(Scout(c, o, null)),
            })))
            .Concat([SessionNotes.ForRunStart(true), SessionNotes.ForRunStart(false), SessionNotes.ForGuiderTransition("Guiding", "LostLock"),
                SessionNotes.ForGuiderTransition("LostLock", "Guiding")])
            .OfType<SessionNote>();

        notes.ShouldAllBe(n => !n.Message.Contains('—'));
    }

    private static ScoutCompletedEventArgs Scout(ScoutClassification classification, ScoutOutcome outcome, TimeSpan? clearIn = null)
        => new ScoutCompletedEventArgs(new Target(5.588, -5.391, "M42", null), classification, clearIn, outcome, [12, 15]);
}
