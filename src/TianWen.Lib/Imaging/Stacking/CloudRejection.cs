using System;
using System.Collections.Immutable;
using TianWen.Lib.Imaging.Grading;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Which lights of a group the stacker leaves out for cloud or twilight (<see cref="StackingOptions.RejectCloud"/>, #1981): the
/// viewer's own verdict (<see cref="LightGrading.Verdict"/>), read over the group's grades, so a frame the viewer marks with a cloud
/// or a sun is the frame the stack drops. The grades are taken on the CALIBRATED frame before normalization, which would erase the
/// night-sky term; a frame with no grade (no stars outside twilight) is not judged here, since registration already drops it.
/// </summary>
internal static class CloudRejection
{
    /// <summary>The causes that drop a frame: cloud by either measure, and a sky past astronomical dark.</summary>
    public const GradeCause Rejects = GradeCause.Cloud | GradeCause.Sunrise | GradeCause.Sunset;

    /// <summary>
    /// The fewest graded lights a group needs before cloud is judged at all: the group's clearest tenth and its neighbours within an hour
    /// are the baselines a cloud verdict reads, and a handful of frames gives neither (a 4-frame group's "clearest tenth" is its one
    /// clearest frame). The viewer's own bar for a clear night, <see cref="LightGrading.MinClearFramesPerReference"/>. Twilight reads the
    /// Sun's altitude, no baseline, so it drops a frame in a group of any size.
    /// </summary>
    public const int MinGradedForCloud = LightGrading.MinClearFramesPerReference;

    /// <summary>
    /// The verdict of every frame in <paramref name="grades"/> that carries a rejecting cause, keyed as the grades are. Read against
    /// <paramref name="references"/> where a rig's clear nights are known (the viewer's store), else on the group's own baselines.
    /// Under <see cref="MinGradedForCloud"/> graded frames only twilight drops a frame.
    /// </summary>
    public static ImmutableDictionary<string, LightGradeVerdict> Rejected(ImmutableDictionary<string, LightGrade> grades,
        ImmutableArray<LightGradeReference> references = default)
    {
        var judgeCloud = grades.Count >= MinGradedForCloud;
        var rejected = ImmutableDictionary.CreateBuilder<string, LightGradeVerdict>(grades.KeyComparer);
        foreach (var (path, grade) in grades)
        {
            var verdict = LightGrading.Verdict(grades, grade, references);
            var causes = judgeCloud ? verdict.Causes & Rejects : verdict.Causes & (GradeCause.Sunrise | GradeCause.Sunset);
            if (causes != GradeCause.None)
            {
                rejected[path] = verdict with { Causes = causes };
            }
        }
        return rejected.ToImmutable();
    }

    /// <summary>Why a frame was left out, in the words the viewer's Grade line uses.</summary>
    public static string InWords(in LightGradeVerdict verdict)
    {
        if ((verdict.Causes & GradeCause.Cloud) != GradeCause.None)
        {
            return LightGrading.CloudInWords(verdict);
        }
        return (verdict.Causes & GradeCause.Sunrise) != GradeCause.None ? "taken past astronomical dark as the Sun rose"
            : (verdict.Causes & GradeCause.Sunset) != GradeCause.None ? "taken past astronomical dark as the Sun set"
            : throw new ArgumentException("the verdict carries no rejecting cause", nameof(verdict));
    }
}
