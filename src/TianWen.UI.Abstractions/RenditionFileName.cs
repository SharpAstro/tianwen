using System;
using System.Globalization;
using System.Linq;
using System.Text;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The suffix "Save image as displayed" suggests after the file's stem, saying how the picture was rendered: the
/// stretch on show always, then a short code for each display setting moved off its default, so two saves of one master
/// under different settings get different names, and the name alone says how the picture was made
/// (<c>master_SculptorGalaxy_enh_luma_sky0.09_ss302_ssat1.24.png</c>).
/// </summary>
/// <remarks>
/// <para>It is a note for the person who saved it, never read back: nothing parses a name, and the full settings go into
/// the file itself. So a code is short rather than complete: a custom curve says <c>curve</c>, not its knots.</para>
/// <para>A setting is named only where it reaches the picture: the star codes only when a star split is shown, the sky
/// level only while the background reduce runs, the neutralisation's method and strength only while it is on.</para>
/// </remarks>
public static class RenditionFileName
{
    /// <summary>
    /// The suffix for <paramref name="state"/>, beginning with an underscore.
    /// </summary>
    /// <param name="state">The viewer's display settings.</param>
    /// <param name="shownMode">The stretch actually on show: <see cref="StretchMode.Auto"/> already resolved, as the
    /// rendition's uniforms carry it.</param>
    /// <param name="enhanced">Whether the document is an enhance's result (<see cref="AstroImageDocument.IsEnhanceResult"/>).</param>
    /// <param name="starSplitShown">Whether a star split is composed into the picture: the document holds one and the
    /// composite view is on show.</param>
    public static string Suffix(ViewerState state, StretchMode shownMode, bool enhanced, bool starSplitShown)
    {
        var sb = new StringBuilder();
        if (enhanced)
        {
            Add(sb, "enh");
        }

        Add(sb, shownMode switch
        {
            StretchMode.None => "linear",
            StretchMode.Linked => "linked",
            StretchMode.Unlinked => "unlinked",
            StretchMode.Luma => "luma",
            _ => shownMode.ToString().ToLowerInvariant(),
        });

        if (shownMode is not StretchMode.None)
        {
            var defaults = StretchParameters.Default;
            if (!Same(state.StretchParameters.Factor, defaults.Factor))
            {
                Add(sb, "f", state.StretchParameters.Factor);
            }
            if (!Same(state.StretchParameters.ShadowsClipping, defaults.ShadowsClipping))
            {
                Add(sb, "clip", state.StretchParameters.ShadowsClipping);
            }
        }

        if (state.ChannelView is not ChannelView.Composite)
        {
            Add(sb, state.ChannelView.ToString().ToLowerInvariant());
        }
        if (state.DebayerAlgorithm != ViewerActions.DefaultDebayerAlgorithm)
        {
            Add(sb, state.DebayerAlgorithm.ToString().ToLowerInvariant());
        }
        if (!state.ColorCalibrationEnabled)
        {
            Add(sb, "nocal");
        }
        if (state.ManualWhiteBalance is var (r, g, b) && !(Same(r, 1) && Same(g, 1) && Same(b, 1)))
        {
            Add(sb, "wb", r);
            sb.Append('-').Append(Format(g)).Append('-').Append(Format(b));
        }

        if (!state.BackgroundNeutralizationEnabled)
        {
            Add(sb, "nobn");
        }
        else
        {
            if (state.BackgroundNeutralizationMethod is not BackgroundNeutralizationMethod.MinPivot)
            {
                Add(sb, "bn" + state.BackgroundNeutralizationMethod.ToString().ToLowerInvariant());
            }
            if (!Same(state.BackgroundNeutralizationStrength, 1))
            {
                Add(sb, "bns", state.BackgroundNeutralizationStrength);
            }
        }

        if (!state.BackgroundReduceEnabled)
        {
            Add(sb, "nored");
        }
        else if (!Same(state.BackgroundSkyLevel, ViewerState.DefaultBackgroundSkyLevel))
        {
            Add(sb, "sky", state.BackgroundSkyLevel);
        }

        if (!Same(state.CurvesBoost, 0))
        {
            Add(sb, "boost", state.CurvesBoost);
        }
        if (state.CurvesMode != 1 || !state.CurveData.SequenceEqual(ViewerState.SplineSCurve))
        {
            Add(sb, "curve");
        }
        if (!Same(state.HdrAmount, 0))
        {
            Add(sb, "hdr", state.HdrAmount);
            if (!Same(state.HdrKnee, 0.8f))
            {
                sb.Append('-').Append(Format(state.HdrKnee));
            }
        }
        if (!Same(state.FinishContrast, 0))
        {
            Add(sb, "con", state.FinishContrast);
        }
        if (!Same(state.FinishSaturation, 1))
        {
            Add(sb, "sat", state.FinishSaturation);
        }

        if (starSplitShown)
        {
            if (state.StarMethod is StarStretchMethod.StarStretch)
            {
                // SETI's Star Stretch is named by its amount; the arcsinh's stretch factor does not reach it.
                Add(sb, "sst", state.StarAmount);
            }
            else if (!Same(state.StarStretch, StarSplit.StarCurve))
            {
                Add(sb, "ss", state.StarStretch);
            }
            if (!Same(state.StarSaturation, StarSplit.StarSaturation))
            {
                Add(sb, "ssat", state.StarSaturation);
            }
        }

        if (state.WaveletSharpenEnabled)
        {
            Add(sb, "wav");
        }
        if (state.DisplayCrop is not null)
        {
            Add(sb, "crop");
        }

        return sb.ToString();
    }

    private static void Add(StringBuilder sb, string code) => sb.Append('_').Append(code);

    private static void Add(StringBuilder sb, string code, double value) => sb.Append('_').Append(code).Append(Format(value));

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // A dial's value is its default exactly until moved, but a float that went through a slider can land a hair off.
    private static bool Same(double a, double b) => Math.Abs(a - b) < 5e-4;
}
