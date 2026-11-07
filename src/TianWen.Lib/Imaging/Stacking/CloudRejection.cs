using System;
using System.Collections.Generic;
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
    /// The fewest graded lights a baseline needs before it judges cloud: the group's clearest tenth and its neighbours within an hour
    /// (patchiness, counted per rig within the group, since the baseline is keyed on the rig) and a night's darkest tenth of sky (counted per rig and night, since that baseline is
    /// one night's) are what a cloud verdict reads, and a handful of frames gives none of them (a 4-frame night's "darkest tenth" is its
    /// one darkest frame). The viewer's own bar for a clear night, <see cref="LightGrading.MinClearFramesPerReference"/>.
    /// </summary>
    public const int MinGradedForCloud = LightGrading.MinClearFramesPerReference;

    /// <summary>
    /// The verdict of every frame in <paramref name="grades"/> that carries a rejecting cause, keyed as the grades are. Read against
    /// <paramref name="references"/> where a rig's clear nights are known (the viewer's store), else on the group's own baselines.
    /// Patchiness is judged only in a group of <see cref="MinGradedForCloud"/> graded lights, the night-sky term only for a rig's night
    /// with that many. Twilight reads the Sun's altitude and needs no baseline, but only a night that reaches astronomical dark is judged
    /// for it, meaning at least <see cref="MinGradedForCloud"/> of its graded lights are outside twilight: where the Sun never gets
    /// below -18 degrees every light reads as twilight, and where it dips under for a few minutes only, those few lights would take the
    /// rest of the night with them, and dropping them all would empty the group (no astro dark is a fallback, never a throw). Each
    /// count is of the lights its baseline is built from (not twilight, a finite patchiness or sky level).
    /// </summary>
    public static ImmutableDictionary<string, LightGradeVerdict> Rejected(ImmutableDictionary<string, LightGrade> grades,
        ImmutableArray<LightGradeReference> references = default)
    {
        // Each count is of the lights its baseline is BUILT from (LightGrading.Baselines.Build leaves out twilight lights, and takes a
        // finite patchiness for one baseline, a finite sky level above 0 and a night for the other), never of every graded light.
        // The patchiness baseline is keyed on the grade's key AND its rig (one group can hold two rigs: one camera on two OTAs).
        var patchinessPerRig = new Dictionary<(LightGradeKey Key, string Rig), int>();
        var perRigNight = new Dictionary<(string Rig, DateOnly Night), int>();
        var darkPerNight = new Dictionary<DateOnly, int>();
        foreach (var g in grades.Values)
        {
            if (g.Twilight is not null)
            {
                continue;
            }
            darkPerNight[g.Night] = darkPerNight.GetValueOrDefault(g.Night) + 1;
            if (float.IsFinite(g.Patchiness))
            {
                patchinessPerRig[(g.Key, g.Rig)] = patchinessPerRig.GetValueOrDefault((g.Key, g.Rig)) + 1;
            }
            if (float.IsFinite(g.SkyLevel) && g.SkyLevel > 0f && g.Night != default)
            {
                perRigNight[(g.Rig, g.Night)] = perRigNight.GetValueOrDefault((g.Rig, g.Night)) + 1;
            }
        }
        var rejected = ImmutableDictionary.CreateBuilder<string, LightGradeVerdict>(grades.KeyComparer);
        foreach (var (path, grade) in grades)
        {
            var verdict = LightGrading.Verdict(grades, grade, references);
            var causes = verdict.Causes & Rejects;
            if ((causes & GradeCause.Cloud) != GradeCause.None)
            {
                var byPatchiness = patchinessPerRig.GetValueOrDefault((grade.Key, grade.Rig)) >= MinGradedForCloud &&(LightGrading.PatchyCloud(verdict.Patchiness, verdict.LocalPatchiness, verdict.LocalNoise, verdict.StarRatio) || verdict.InCloudRun || verdict.SkyRising);
                var bySky = verdict.NightSky >= LightGrading.NightSkyCloudFrom && perRigNight.GetValueOrDefault((grade.Rig, grade.Night)) >= MinGradedForCloud;
                if (!byPatchiness && !bySky)
                {
                    causes &= ~GradeCause.Cloud;
                }
                else
                {
                    // A term that was not judged must not be worded as a finding.
                    verdict = verdict with
                    {
                        Patchiness = byPatchiness ? verdict.Patchiness : float.NaN,
                        LocalPatchiness = byPatchiness ? verdict.LocalPatchiness : float.NaN,
                        NightSky = bySky ? verdict.NightSky : float.NaN,
                    };
                }
            }
            if (darkPerNight.GetValueOrDefault(grade.Night) < MinGradedForCloud)
            {
                causes &= GradeCause.Cloud;
            }
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

    /// <summary>
    /// The skip reason for a group left with fewer than two frames, when it was THIS filter that emptied it (#1998): at least two
    /// frames registered, and leaving out <paramref name="leftOut"/> of them for cloud or twilight took the group below two.
    /// <see langword="null"/> when registration was already short, so the registration census stays the diagnosis there.
    /// </summary>
    public static string? EmptiedGroupReason(int matchedBeforeCloud, int leftOut)
        => leftOut > 0 && matchedBeforeCloud >= 2 && matchedBeforeCloud - leftOut < 2
            ? $"{leftOut} of {matchedBeforeCloud} matched frames left out for cloud or twilight, fewer than 2 remain"
            : null;
}
