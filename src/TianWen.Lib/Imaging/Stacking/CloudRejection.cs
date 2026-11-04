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
    /// The verdict of every frame in <paramref name="grades"/> that carries a rejecting cause, keyed as the grades are. Read against
    /// <paramref name="references"/> where a rig's clear nights are known (the viewer's store), else on the group's own baselines.
    /// </summary>
    public static ImmutableDictionary<string, LightGradeVerdict> Rejected(ImmutableDictionary<string, LightGrade> grades,
        ImmutableArray<LightGradeReference> references = default)
    {
        var rejected = ImmutableDictionary.CreateBuilder<string, LightGradeVerdict>(grades.KeyComparer);
        foreach (var (path, grade) in grades)
        {
            var verdict = LightGrading.Verdict(grades, grade, references);
            if ((verdict.Causes & Rejects) != GradeCause.None)
            {
                rejected[path] = verdict;
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
