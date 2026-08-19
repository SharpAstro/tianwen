using System;
using System.Collections.Generic;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The display controls as the USER set them, snapshotted so each half of the split can say what it
/// is showing.
/// </summary>
/// <remarks>
/// <para>Deliberately not <see cref="DisplayRendition"/>. A rendition holds DERIVED uniforms, and they
/// are coupled: <c>ComputeStretchUniforms</c> scales the per-channel stats by white balance before
/// deriving shadows/midtones/rescale, so moving WB also moves the stretch numbers. Reading a rendition
/// would therefore report "white balance AND stretch" when only WB was touched, and the same goes for
/// the normalise scale and pedestal. Only the inputs the user actually set can name what the user
/// actually did.</para>
/// <para>Values, not preset indices, and the same values the toolbar buttons display -- because the
/// label has to be readable next to the button it refers to, and two presets that resolve to the same
/// numbers render identically, so calling them different would name a difference no eye can find.</para>
/// </remarks>
public readonly record struct DisplayControls(
    StretchMode StretchMode,
    StretchParameters StretchParameters,
    float CurvesBoost,
    int CurvesMode,
    float HdrAmount,
    bool ColorCalibrationEnabled,
    bool BackgroundNeutralizationEnabled,
    BackgroundNeutralizationMethod BackgroundNeutralizationMethod,
    float BackgroundNeutralizationStrength,
    (float R, float G, float B) ManualWhiteBalance)
{
    /// <summary>How many controls a label names before collapsing the rest into a count.</summary>
    public const int MaxNamedControls = 2;

    /// <summary>
    /// What a freshly opened viewer shows, which is the baseline each half is described against.
    /// </summary>
    /// <remarks>
    /// Must track <see cref="ViewerState"/>'s own property initialisers. NOT <c>default</c>: an
    /// all-zero struct claims a white balance of (0,0,0) and a background-neutralisation strength of
    /// zero, neither of which any viewer has ever displayed, so every snapshot would report controls
    /// the user never touched.
    /// </remarks>
    public static readonly DisplayControls Defaults = new DisplayControls(
        StretchMode.Unlinked,
        StretchParameters.Default,
        0f,
        0,
        0f,
        false,
        false,
        BackgroundNeutralizationMethod.Mean,
        1f,
        (1f, 1f, 1f));

    public static DisplayControls FromState(ViewerState state) => new DisplayControls(
        state.StretchMode,
        state.StretchParameters,
        state.CurvesBoost,
        state.CurvesMode,
        state.HdrAmount,
        state.ColorCalibrationEnabled,
        state.BackgroundNeutralizationEnabled,
        state.BackgroundNeutralizationMethod,
        state.BackgroundNeutralizationStrength,
        state.ManualWhiteBalance);

    /// <summary>One user-facing control, in the order a label names them.</summary>
    private enum Slot
    {
        StretchMode,
        StretchParameters,
        Curves,
        Hdr,
        Calibration,
        BackgroundNeutralization,
        WhiteBalance,
    }

    private static readonly Slot[] SlotOrder =
    [
        Slot.StretchMode,
        Slot.StretchParameters,
        Slot.Curves,
        Slot.Hdr,
        Slot.Calibration,
        Slot.BackgroundNeutralization,
        Slot.WhiteBalance,
    ];

    /// <summary>
    /// How this snapshot's <paramref name="slot"/> reads, or null when it sits at its default.
    /// </summary>
    /// <remarks>
    /// Worded like the toolbar button, because the button is what the user pressed. Null for a default
    /// is what keeps a label to the few controls that were actually touched instead of reciting all
    /// ten every frame.
    /// </remarks>
    private string? Describe(Slot slot) => slot switch
    {
        Slot.StretchMode when StretchMode != Defaults.StretchMode => StretchMode.ToString(),
        Slot.StretchParameters when !StretchParameters.Equals(Defaults.StretchParameters) =>
            $"Stretch {StretchParameters}",
        // The curve mode only reaches the pixels through the boost, so at zero boost it is invisible
        // and naming it would point at a difference the picture cannot show.
        Slot.Curves when CurvesBoost > 0f =>
            CurvesMode == 0 ? $"Boost {CurvesBoost:P0}" : $"Boost {CurvesBoost:P0} spline",
        Slot.Hdr when HdrAmount > 0f => $"HDR {HdrAmount:F1}",
        Slot.Calibration when ColorCalibrationEnabled => "Calibrate",
        Slot.BackgroundNeutralization when BackgroundNeutralizationEnabled =>
            BackgroundNeutralizationStrength >= 0.9999f
                ? $"NeutBg {ShortMethod(BackgroundNeutralizationMethod)}"
                : $"NeutBg {ShortMethod(BackgroundNeutralizationMethod)} {BackgroundNeutralizationStrength:P0}",
        Slot.WhiteBalance when ManualWhiteBalance != Defaults.ManualWhiteBalance =>
            $"WB {ManualWhiteBalance.R:F2}/{ManualWhiteBalance.G:F2}/{ManualWhiteBalance.B:F2}",
        _ => null,
    };

    private static string ShortMethod(BackgroundNeutralizationMethod m) => m switch
    {
        BackgroundNeutralizationMethod.GreenPivot => "Green",
        BackgroundNeutralizationMethod.MinPivot => "Min",
        _ => "Mean",
    };

    /// <summary>
    /// What to call a slot that is switched OFF, for slots that have an off state at all.
    /// </summary>
    /// <remarks>
    /// Null for the two slots that are always a value rather than a toggle: every stretch mode IS a
    /// mode and every stretch parameter pair IS a pair, so there is nothing to be "off" and
    /// <see cref="DescribeDefault"/> names the value they fell back to instead.
    /// </remarks>
    private static string? OffName(Slot slot) => slot switch
    {
        Slot.Curves => "Boost",
        Slot.Hdr => "HDR",
        Slot.Calibration => "Calibrate",
        Slot.BackgroundNeutralization => "NeutBg",
        Slot.WhiteBalance => "WB",
        _ => null,
    };

    /// <summary>How an always-a-value slot reads when it sits at its default.</summary>
    private static string DescribeDefault(Slot slot) => slot switch
    {
        Slot.StretchMode => Defaults.StretchMode.ToString(),
        _ => $"Stretch {Defaults.StretchParameters}",
    };

    /// <summary>
    /// Names what this snapshot has that is not a default, CONTESTED controls first.
    /// </summary>
    /// <remarks>
    /// This is the PINNED half's label: the reference the other half is read against, so it states
    /// its own settings in full rather than a delta. Contested first because the truncation below is
    /// what the reader actually loses, and a control both halves share is context while a control
    /// they disagree about is the whole reason the split is open. In plain declaration order the
    /// shared ones can fill the quota and collapse the contested one into "+1", which is exactly the
    /// case that made this worth fixing.
    /// </remarks>
    private string SelfLabel(string prefix, in DisplayControls other, List<string> scratch)
    {
        scratch.Clear();

        foreach (var slot in SlotOrder)
        {
            if (Describe(slot) is { } text && !Same(other, slot))
            {
                scratch.Add(text);
            }
        }

        foreach (var slot in SlotOrder)
        {
            if (Describe(slot) is { } text && Same(other, slot))
            {
                scratch.Add(text);
            }
        }

        return Join(prefix, scratch);
    }

    /// <summary>
    /// Names only what the live controls ADD, REMOVE or change relative to <paramref name="pinned"/>.
    /// </summary>
    /// <remarks>
    /// <para>A delta, not a self-description, because the pinned half beside it already states the
    /// baseline in full -- repeating it would make the reader compare two long lists to find the one
    /// token that moved.</para>
    /// <para>Directional, which is the part a plain difference list cannot express. Naming the control
    /// alone reads as an attribute of whichever half carries the label: pin with a boost, turn it off,
    /// and a bare "Boost" sits over the half that no longer has any.</para>
    /// <para>A switched-off control reads "No Boost", NOT "-Boost 25%". A minus in front of a
    /// percentage reads as a quantity -- boost reduced BY 25%, or a negative boost -- which is a
    /// different claim from the one being made, and the pinned half states the lost value one word
    /// across the divider anyway. A changed value needs no marker at all; the new number IS the
    /// statement.</para>
    /// </remarks>
    private string DeltaLabel(string prefix, in DisplayControls pinned, List<string> scratch)
    {
        scratch.Clear();

        foreach (var slot in SlotOrder)
        {
            var mine = Describe(slot);
            var theirs = pinned.Describe(slot);
            if (string.Equals(mine, theirs, StringComparison.Ordinal))
            {
                continue;
            }

            scratch.Add((mine, theirs) switch
            {
                ({ } added, null) => "+" + added,
                (null, _) => OffName(slot) is { } off ? "No " + off : DescribeDefault(slot),
                var (changed, _) => changed!,
            });
        }

        return Join(prefix, scratch);
    }

    private bool Same(in DisplayControls other, Slot slot)
        => string.Equals(Describe(slot), other.Describe(slot), StringComparison.Ordinal);

    private static string Join(string prefix, List<string> parts)
    {
        if (parts.Count == 0)
        {
            return prefix;
        }

        if (parts.Count <= MaxNamedControls)
        {
            return $"{prefix}: {string.Join(", ", parts)}";
        }

        var named = string.Join(", ", parts.GetRange(0, MaxNamedControls));
        return $"{prefix}: {named} +{parts.Count - MaxNamedControls}";
    }

    /// <summary>
    /// The two half labels: what the pin holds, and what the live controls changed since.
    /// </summary>
    /// <remarks>
    /// "Live (same)" rather than an empty delta when the two agree. Two identical halves with a line
    /// between them is the one state that reads as the feature being broken, so it has to be said
    /// outright.
    /// </remarks>
    public static (string Left, string Right) Labels(
        in DisplayControls pinned, in DisplayControls live, List<string> scratch)
    {
        var left = pinned.SelfLabel("Pinned", live, scratch);
        var right = pinned.Equals(live) ? "Live (same)" : live.DeltaLabel("Live", pinned, scratch);
        return (left, right);
    }
}
