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
        SessionNotes.ForRunEnd(SessionPhase.Complete, null).ShouldBe(new SessionNote(NotificationSeverity.Info, "Session complete", NoteKind.SessionComplete));
        SessionNotes.ForRunEnd(SessionPhase.Aborted, null).ShouldBe(new SessionNote(NotificationSeverity.Warning, "Session aborted", NoteKind.SessionAborted));
        SessionNotes.ForRunEnd(SessionPhase.Failed, "Check the mount's cable").ShouldBe(
            new SessionNote(NotificationSeverity.Error, "Session failed: Check the mount's cable", NoteKind.SessionFailed));
        SessionNotes.ForRunEnd(SessionPhase.Failed, null).ShouldBe(new SessionNote(NotificationSeverity.Error, "Session failed", NoteKind.SessionFailed));
        SessionNotes.ForFlatRunEnd(SessionPhase.Aborted, null).ShouldBe(new SessionNote(NotificationSeverity.Warning, "Flat run cancelled", NoteKind.FlatRunCancelled));
    }

    [Fact]
    public void EveryNoteSaysWhatKindOfEventItIs()
    {
        SessionNotes.ForRunStart(flatRun: false).Kind.ShouldBe(NoteKind.RunStarted);
        SessionNotes.ForRunStart(flatRun: true).Kind.ShouldBe(NoteKind.RunStarted);
        foreach (var phase in Enum.GetValues<SessionPhase>())
        {
            if (SessionNotes.ForPhase(phase) is { } phaseNote)
            {
                phaseNote.Kind.ShouldBe(NoteKind.Phase, phase.ToString());
            }
        }

        SessionNotes.ForRunEnd(SessionPhase.Complete, null).ShouldNotBeNull().Kind.ShouldBe(NoteKind.SessionComplete);
        SessionNotes.ForRunEnd(SessionPhase.Aborted, null).ShouldNotBeNull().Kind.ShouldBe(NoteKind.SessionAborted);
        SessionNotes.ForRunEnd(SessionPhase.Failed, "why").ShouldNotBeNull().Kind.ShouldBe(NoteKind.SessionFailed);
        SessionNotes.ForRunEnd(SessionPhase.Failed, null).ShouldNotBeNull().Kind.ShouldBe(NoteKind.SessionFailed);
        SessionNotes.ForFlatRunEnd(SessionPhase.Complete, null).Kind.ShouldBe(NoteKind.FlatsComplete);
        SessionNotes.ForFlatRunEnd(SessionPhase.Aborted, null).Kind.ShouldBe(NoteKind.FlatRunCancelled);
        SessionNotes.ForFlatRunEnd(SessionPhase.Failed, "why").Kind.ShouldBe(NoteKind.FlatsFailed);
        SessionNotes.ForFlatRunEnd(SessionPhase.Failed, null).Kind.ShouldBe(NoteKind.FlatsFailed);
        SessionNotes.ForFlatRunEnd(SessionPhase.Finalising, null).Kind.ShouldBe(NoteKind.FlatRunFinished);
        SessionNotes.ForScout(Scout(ScoutClassification.Transparency, ScoutOutcome.Proceed)).ShouldNotBeNull().Kind.ShouldBe(NoteKind.ScoutTransparency);
        SessionNotes.ForScout(Scout(ScoutClassification.Obstruction, ScoutOutcome.Proceed)).ShouldNotBeNull().Kind.ShouldBe(NoteKind.ScoutCleared);
        SessionNotes.ForScout(Scout(ScoutClassification.Obstruction, ScoutOutcome.Advance, TimeSpan.FromMinutes(5))).ShouldNotBeNull().Kind.ShouldBe(NoteKind.ScoutAdvanced);
        SessionNotes.ForScout(Scout(ScoutClassification.Obstruction, ScoutOutcome.Advance)).ShouldNotBeNull().Kind.ShouldBe(NoteKind.ScoutAdvanced);
        SessionNotes.ForGuiderTransition("Guiding", "LostLock").ShouldNotBeNull().Kind.ShouldBe(NoteKind.GuideStarLost);
        SessionNotes.ForGuiderTransition("LostLock", "Guiding").ShouldNotBeNull().Kind.ShouldBe(NoteKind.GuideStarReacquired);
    }

    [Fact]
    public void ANoteNobodyClassifiedIsNone() => new SessionNote(NotificationSeverity.Info, "x").Kind.ShouldBe(NoteKind.None);

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
