using System.Collections.Generic;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The display controls as the USER set them, snapshotted so the split can say which ones differ
/// between its two halves.
/// </summary>
/// <remarks>
/// <para>Deliberately not <see cref="DisplayRendition"/>. A rendition holds DERIVED uniforms, and they
/// are coupled: <c>ComputeStretchUniforms</c> scales the per-channel stats by white balance before
/// deriving shadows/midtones/rescale, so moving WB also moves the stretch numbers. Diffing a rendition
/// would therefore report "white balance AND stretch" when only WB was touched, and the same goes for
/// the normalise scale and pedestal. Only the inputs the user actually set can name what the user
/// actually did.</para>
/// <para>Presets are compared by INDEX rather than by their expanded values, for the same reason: two
/// presets can resolve to nearby numbers, and what the user changed is the preset.</para>
/// </remarks>
public readonly record struct DisplayControls(
    StretchMode StretchMode,
    int StretchPresetIndex,
    int CurvesBoostIndex,
    int CurvesMode,
    int HdrPresetIndex,
    bool ColorCalibrationEnabled,
    bool BackgroundNeutralizationEnabled,
    BackgroundNeutralizationMethod BackgroundNeutralizationMethod,
    float BackgroundNeutralizationStrength,
    (float R, float G, float B) ManualWhiteBalance)
{
    /// <summary>How many names a label spells out before collapsing the rest into a count.</summary>
    public const int MaxNamedDifferences = 2;

    public static DisplayControls FromState(ViewerState state) => new DisplayControls(
        state.StretchMode,
        state.StretchPresetIndex,
        state.CurvesBoostIndex,
        state.CurvesMode,
        state.HdrPresetIndex,
        state.ColorCalibrationEnabled,
        state.BackgroundNeutralizationEnabled,
        state.BackgroundNeutralizationMethod,
        state.BackgroundNeutralizationStrength,
        state.ManualWhiteBalance);

    /// <summary>
    /// Appends the name of every control that differs from <paramref name="pinned"/>, in a FIXED
    /// order.
    /// </summary>
    /// <remarks>
    /// <para>Fixed order, not order-of-change: the label describes the two STATES, so it must not
    /// depend on the sequence that produced them. Change HDR then WB, or WB then HDR, and the label
    /// reads the same -- and changing something back removes it, because the halves then genuinely
    /// agree about it.</para>
    /// <para>Names match the toolbar buttons, since the button is what the user pressed.</para>
    /// </remarks>
    public void CollectDifferencesFrom(in DisplayControls pinned, List<string> into)
    {
        // Stretch mode and strength are separate buttons (STF / Linked / the preset), so they are
        // named separately even though both land in the same uniforms.
        if (StretchMode != pinned.StretchMode)
        {
            into.Add("Stretch");
        }
        if (StretchPresetIndex != pinned.StretchPresetIndex)
        {
            into.Add("Strength");
        }
        if (CurvesBoostIndex != pinned.CurvesBoostIndex || CurvesMode != pinned.CurvesMode)
        {
            into.Add("Boost");
        }
        if (HdrPresetIndex != pinned.HdrPresetIndex)
        {
            into.Add("HDR");
        }
        if (ColorCalibrationEnabled != pinned.ColorCalibrationEnabled)
        {
            into.Add("Calibrate");
        }
        if (BackgroundNeutralizationEnabled != pinned.BackgroundNeutralizationEnabled
            || BackgroundNeutralizationMethod != pinned.BackgroundNeutralizationMethod
            || BackgroundNeutralizationStrength != pinned.BackgroundNeutralizationStrength)
        {
            into.Add("NeutBg");
        }
        if (ManualWhiteBalance != pinned.ManualWhiteBalance)
        {
            into.Add("WB");
        }
    }

    /// <summary>
    /// The right half's label: "Live" plus what differs from <paramref name="pinned"/>.
    /// </summary>
    /// <remarks>
    /// Says "no change" rather than rendering a bare "Live" when nothing differs. Two identical halves
    /// with a line between them is the one state that reads as the feature being broken, so when it
    /// does happen -- change something and change it back -- the label has to be the thing that
    /// explains it.
    /// </remarks>
    public static string DescribeLive(in DisplayControls pinned, in DisplayControls live, List<string> scratch)
    {
        scratch.Clear();
        live.CollectDifferencesFrom(pinned, scratch);

        if (scratch.Count == 0)
        {
            return "Live (no change)";
        }

        if (scratch.Count <= MaxNamedDifferences)
        {
            return "Live: " + string.Join(", ", scratch);
        }

        var named = string.Join(", ", scratch.GetRange(0, MaxNamedDifferences));
        return $"Live: {named} +{scratch.Count - MaxNamedDifferences}";
    }
}
