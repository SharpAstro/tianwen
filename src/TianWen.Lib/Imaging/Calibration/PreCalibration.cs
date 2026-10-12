using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// The corrections a frame's own header says were ALREADY applied to it, by the software that wrote it:
/// the SBFITSEXT <c>CALSTAT</c> card, whose letters accumulate one per correction (B for bias, D for dark,
/// F for flat). MaxIm DL writes it, and iTelescope's pipeline hands out lights that way.
/// </summary>
[Flags]
public enum CalibrationApplied
{
    None = 0,
    Bias = 1,
    Dark = 2,
    Flat = 4,
}

/// <summary>
/// The read guard on <c>CALSTAT</c> (#466): a light that already says it was calibrated must never be
/// calibrated again. Subtracting a dark from a dark-subtracted frame takes the dark current out twice,
/// and dividing a flat-fielded frame by a flat imprints the inverse of the vignette back in.
/// </summary>
/// <remarks>
/// <para><b>Per correction, not all or nothing.</b> A frame that carries <c>BD</c> had no flat, so a flat
/// still belongs on it; only the letters present are skipped.</para>
/// <para><b>A session takes the UNION of its lights' letters.</b> A correction any light already carries
/// is skipped for the whole session, since applying it there would correct that light twice, which is
/// the worse of the two errors; a session whose lights disagree is reported so it can be looked at.</para>
/// <para>Read only. Nothing here writes <c>CALSTAT</c> (#467 is that half).</para>
/// </remarks>
public static class PreCalibration
{
    /// <summary>The SBFITSEXT card naming the applied corrections.</summary>
    public const string CalStatCard = "CALSTAT";

    /// <summary>
    /// The corrections a <c>CALSTAT</c> value names. Case-insensitive; letters other than B, D and F are
    /// passed over, and an absent or empty card is <see cref="CalibrationApplied.None"/>.
    /// </summary>
    public static CalibrationApplied Parse(string? calStat)
    {
        var applied = CalibrationApplied.None;
        foreach (var c in calStat.AsSpan())
        {
            applied |= char.ToUpperInvariant(c) switch
            {
                'B' => CalibrationApplied.Bias,
                'D' => CalibrationApplied.Dark,
                'F' => CalibrationApplied.Flat,
                _ => CalibrationApplied.None,
            };
        }
        return applied;
    }

    /// <summary>
    /// The corrections any of <paramref name="lights"/> already carries (the union), with
    /// <paramref name="mixed"/> true when the lights do not all carry the same ones.
    /// </summary>
    public static CalibrationApplied Across(IReadOnlyList<FrameInfo> lights, out bool mixed)
    {
        var union = CalibrationApplied.None;
        mixed = false;
        for (var i = 0; i < lights.Count; i++)
        {
            var applied = lights[i].Meta.PreCalibrated;
            if (i > 0 && applied != lights[0].Meta.PreCalibrated)
            {
                mixed = true;
            }
            union |= applied;
        }
        return union;
    }

    /// <summary>The letters <c>CALSTAT</c> would carry for <paramref name="applied"/>, in B, D, F order.</summary>
    public static string Letters(CalibrationApplied applied)
        => string.Concat(
            applied.HasFlag(CalibrationApplied.Bias) ? "B" : "",
            applied.HasFlag(CalibrationApplied.Dark) ? "D" : "",
            applied.HasFlag(CalibrationApplied.Flat) ? "F" : "");
}
