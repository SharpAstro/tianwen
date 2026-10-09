using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// The blur a deconvolution is asked to remove, stated by the caller: a circular Moffat per channel, its FWHM in the
/// frame's own (native) pixels, and the scale the frame is deconvolved at. TianWen's own deconvolver (E3.4d,
/// <c>docs/plans/deconvolver-training.md</c>, section 6) takes its kernel as an input and has no way yet to find one in
/// a single frame or to decline a frame that has nothing to remove (#741), so it serves only a run that states one.
/// </summary>
/// <remarks>
/// <para><b>The FWHM is the blur's EXCESS, not the stars' width</b>: the kernel that, convolved with the sharp frame,
/// gives the one in hand. The readouts that measured E3.4d used 0.77 / 0.91 / 0.98 px on a frame whose stars were
/// 1.81 px wide; a kernel as wide as the stars over-reads and fabricates (E3.2's kernel sweep).</para>
///
/// <para><b>The frame is deconvolved at <see cref="Resample"/> times its size</b> and brought back (a cubic spline
/// either way, as the readouts did), with each kernel's width scaled by the same factor, because the learned prior
/// works on stars inside the width band it was trained on (E3.2: its floor sits near 1.1 times the training truths'
/// median width). 1.28125 is the operating point every published readout used.</para>
/// </remarks>
/// <param name="FwhmRed">The red channel's kernel FWHM in native pixels; 0 is the delta (nothing removed).</param>
/// <param name="FwhmGreen">The green channel's.</param>
/// <param name="FwhmBlue">The blue channel's.</param>
/// <param name="Beta">The Moffat beta every channel's kernel takes; 4 in every readout.</param>
/// <param name="Resample">The factor the frame is deconvolved at; 1.28125 in every readout.</param>
public sealed record DeconvolutionKernel(
    double FwhmRed,
    double FwhmGreen,
    double FwhmBlue,
    double Beta = DeconvolutionKernel.DefaultBeta,
    double Resample = DeconvolutionKernel.DefaultResample)
{
    /// <summary>The Moffat beta every E3.4d readout used.</summary>
    public const double DefaultBeta = 4.0;

    /// <summary>The scale every E3.4d readout deconvolved at (<c>n2n_operator_master.py --zoom</c>).</summary>
    public const double DefaultResample = 1.28125;

    /// <summary>The FWHM of channel <paramref name="channel"/> (0 red, 1 green, 2 blue).</summary>
    public double FwhmOf(int channel) => channel switch
    {
        0 => FwhmRed,
        1 => FwhmGreen,
        2 => FwhmBlue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "a deconvolution kernel has three channels"),
    };

    /// <summary>
    /// Parses a kernel from the caller's spelling: one FWHM for every channel (<c>0.9</c>) or one per channel
    /// (<c>0.77,0.91,0.98</c>), in native pixels, with an optional beta and resample factor (<c>null</c> takes the
    /// default). The one parser for the CLI and the hosted enhance endpoint.
    /// </summary>
    /// <param name="fwhm">The FWHM text; <c>null</c> or blank means no kernel was stated, and returns <c>true</c> with
    /// <paramref name="kernel"/> <c>null</c>.</param>
    /// <param name="beta">The Moffat beta, positive; <c>null</c> for <see cref="DefaultBeta"/>.</param>
    /// <param name="resample">The resample factor, at least 1; <c>null</c> for <see cref="DefaultResample"/>.</param>
    /// <param name="kernel">The parsed kernel, or <c>null</c> where none was stated or the text is refused.</param>
    /// <param name="error">Why the text was refused; <c>null</c> on success.</param>
    public static bool TryParse(string? fwhm, double? beta, double? resample, out DeconvolutionKernel? kernel, [NotNullWhen(false)] out string? error)
    {
        kernel = null;
        error = null;
        if (string.IsNullOrWhiteSpace(fwhm))
        {
            if (beta.HasValue || resample.HasValue)
            {
                error = "a deconvolution beta or resample factor was given without the kernel's FWHM";
                return false;
            }
            return true;
        }

        var parts = fwhm.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (1 or 3))
        {
            error = $"the deconvolution kernel '{fwhm}' must be one FWHM for every channel or three (red,green,blue), in native pixels";
            return false;
        }
        Span<double> widths = stackalloc double[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) || !double.IsFinite(w) || w < 0)
            {
                error = $"the deconvolution kernel's FWHM '{parts[i]}' is not a width in pixels (a finite number, 0 or more)";
                return false;
            }
            widths[i] = w;
        }
        if (parts.Length == 1)
        {
            widths[1] = widths[0];
            widths[2] = widths[0];
        }

        var b = beta ?? DefaultBeta;
        if (!double.IsFinite(b) || b <= 0)
        {
            error = $"the deconvolution kernel's beta {b} must be a positive number";
            return false;
        }
        var r = resample ?? DefaultResample;
        if (!double.IsFinite(r) || r < 1)
        {
            error = $"the deconvolution resample factor {r} must be 1 or more (the prior works on stars at least as wide as the frame's own)";
            return false;
        }

        kernel = new DeconvolutionKernel(widths[0], widths[1], widths[2], b, r);
        return true;
    }
}
