namespace TianWen.Lib.Imaging.Grading;

using System;
using System.Collections.Immutable;
using System.Threading;

/// <summary>
/// Answers "is this light cloud" for each frame of ONE run as it lands, by the very measures the viewer's grade uses
/// (<see cref="LightGrading.Verdict"/>), so the session and the file list cannot disagree (#1977). Keep one per OTA: a
/// frame is read against the run's earlier frames of its own group and rig, and, given the rig's clear nights
/// (<paramref name="references"/>, <see cref="LightGradeReferenceStore"/>, #1976), against another night where the run's own
/// baseline is implausibly bright, so a run cloudy from its first light is not its own clear baseline. With no reference for
/// the rig the run's own baselines decide, as before.
/// </summary>
/// <remarks>
/// The graded lights are an <see cref="ImmutableDictionary{TKey, TValue}"/> swapped atomically, so the imaging loop adds and
/// telemetry may read <see cref="Grades"/> without a lock. Cost per frame: one <see cref="LightGrading.Grade"/> by the caller
/// (the sky patchiness and masked median on a frame it already found stars on) and one baseline build per new snapshot,
/// which is a single pass over the run's grades (a few hundred at most) plus a sort per group.
/// </remarks>
public sealed class LiveCloudJudge(ImmutableArray<LightGradeReference> references = default)
{
    private ImmutableDictionary<string, LightGrade> _grades = ImmutableDictionary<string, LightGrade>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The run's graded lights so far, one immutable snapshot.</summary>
    public ImmutableDictionary<string, LightGrade> Grades => Volatile.Read(ref _grades);

    /// <summary>
    /// Records <paramref name="grade"/> under <paramref name="name"/> and returns the verdict of that newest frame against the
    /// run so far, or null when the frame has no grade (no stars and no twilight, or no usable score): that is "not judged", never "clear".
    /// </summary>
    public LightGradeVerdict? Add(string name, LightGrade? grade)
    {
        if (grade is not { } g)
        {
            return null;
        }
        // Single writer in practice (the imaging loop); the CAS keeps a stray second one from losing an add.
        ImmutableDictionary<string, LightGrade> seen, next;
        do
        {
            seen = Volatile.Read(ref _grades);
            next = seen.SetItem(name, g);
        }
        while (Interlocked.CompareExchange(ref _grades, next, seen) != seen);
        return LightGrading.Verdict(next, g, references);
    }

    /// <summary>Whether a verdict carries <see cref="GradeCause.Cloud"/>; null (not judged) is not cloud, and callers must not read it as clear.</summary>
    public static bool IsCloud(LightGradeVerdict? verdict) => verdict is { } v && (v.Causes & GradeCause.Cloud) != 0;
}
