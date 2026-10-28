using System;
using System.Collections.Generic;
using System.Threading;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The stage a best stack is in (#1879). <see cref="PlanetaryBestStack.RunAsync"/> reports each as it starts; <see cref="Writing"/> is the
/// caller's, since the stack hands its images back and writes nothing itself.
/// </summary>
public enum PlanetaryStackStage
{
    /// <summary>Grading, registering and folding frames: the only stage whose length follows the capture's frame reads.</summary>
    Stacking,
    /// <summary>
    /// The de-rotation's limb fits and the stacks of the capture's best frames they are read from, including the de-rotated reference
    /// stack, whose loads the fits' estimate does not count: the fraction holds at 99 % through it until Stacking resumes.
    /// </summary>
    Derotating,
    /// <summary>The folded sum turned into a master: normalising, the channels' limb fits, the demosaic and the crop.</summary>
    Finishing,
    Sharpening,
    BalancingColour,
    /// <summary>Reported by the caller that writes the files, never by the stack.</summary>
    Writing,
}

/// <summary>
/// Where a best stack is: its <see cref="Stage"/> and how far through that stage, 0 to 1. A stage whose length is not known ahead
/// reports 0 when it starts and 1 when it is done.
/// </summary>
public readonly record struct PlanetaryStackProgress(PlanetaryStackStage Stage, double Fraction)
{
    /// <summary>The stage in words, the ONE place a host takes its label from.</summary>
    public static string Words(PlanetaryStackStage stage) => stage switch
    {
        PlanetaryStackStage.Stacking => "Stacking",
        PlanetaryStackStage.Derotating => "Fitting the planet's turn",
        PlanetaryStackStage.Finishing => "Finishing the master",
        PlanetaryStackStage.Sharpening => "Sharpening",
        PlanetaryStackStage.BalancingColour => "Balancing colour",
        PlanetaryStackStage.Writing => "Writing",
        _ => stage.ToString(),
    };

    /// <summary>The stage in a word short enough for a segment of a switch; the percent is shown only where <see cref="HasPercent"/>.</summary>
    public static string ShortWords(PlanetaryStackStage stage) => stage switch
    {
        PlanetaryStackStage.Stacking => "Stacking",
        PlanetaryStackStage.Derotating => "De-rotating",
        PlanetaryStackStage.Finishing => "Finishing",
        PlanetaryStackStage.Sharpening => "Sharpening",
        PlanetaryStackStage.BalancingColour => "Colour",
        PlanetaryStackStage.Writing => "Writing",
        _ => stage.ToString(),
    };

    /// <summary>Whether the stage's fraction is a real measure, which only stacking's frame reads are; the others report 0 then 1.</summary>
    public static bool HasPercent(PlanetaryStackStage stage) => stage == PlanetaryStackStage.Stacking;

    public override string ToString() => $"{Words(Stage)} {Fraction:P0}";
}

/// <summary>
/// Wall time per stage of a run reporting <see cref="PlanetaryStackProgress"/>: a stage's time runs from the report that enters it to the
/// report that enters another, so Derotating's time is not Stacking's, and a caller that writes brackets it with <see cref="Enter"/>.
/// </summary>
public sealed class PlanetaryStageTimer(TimeProvider? clock = null) : IProgress<PlanetaryStackProgress>
{
    // One immutable snapshot swapped by Interlocked.CompareExchange (the rule's lock-free row): the stages spent, the open one and its start.
    private sealed record State((PlanetaryStackStage Stage, TimeSpan Elapsed)[] Spent, PlanetaryStackStage? Current, long Since);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private State _state = new([], null, 0);

    /// <summary>The time of the stage reported so far, in the order they first appeared; the open stage runs to now.</summary>
    public IReadOnlyList<(PlanetaryStackStage Stage, TimeSpan Elapsed)> Spent
    {
        get
        {
            // Reports come from the stack's parallel passes and the caller's thread; one snapshot is read, so the open stage and its start agree.
            var state = _state;
            return state.Current is { } open ? Add(state.Spent, open, _clock.GetElapsedTime(state.Since)) : state.Spent;
        }
    }

    public void Report(PlanetaryStackProgress value) => Enter(value.Stage);

    /// <summary>Starts <paramref name="stage"/>'s clock, and stops the open one's; entering the open stage changes nothing.</summary>
    public void Enter(PlanetaryStackStage stage)
    {
        while (true)
        {
            var seen = _state;
            if (seen.Current == stage)
            {
                return;
            }
            var spent = seen.Current is { } open ? Add(seen.Spent, open, _clock.GetElapsedTime(seen.Since)) : seen.Spent;
            // A racing Enter that won leaves `seen` stale: read again and decide again.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, new State(spent, stage, _clock.GetTimestamp()), seen), seen))
            {
                return;
            }
        }
    }

    private static (PlanetaryStackStage Stage, TimeSpan Elapsed)[] Add((PlanetaryStackStage Stage, TimeSpan Elapsed)[] into, PlanetaryStackStage stage, TimeSpan elapsed)
    {
        var at = Array.FindIndex(into, s => s.Stage == stage);
        if (at < 0)
        {
            return [.. into, (stage, elapsed)];
        }
        (PlanetaryStackStage Stage, TimeSpan Elapsed)[] copy = [.. into];
        copy[at] = (stage, into[at].Elapsed + elapsed);
        return copy;
    }
}
