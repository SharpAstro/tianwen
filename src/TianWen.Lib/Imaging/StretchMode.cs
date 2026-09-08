namespace TianWen.Lib.Imaging;

public enum StretchMode
{
    None,
    Linked,
    Unlinked,
    Luma,

    /// <summary>
    /// Let the viewer pick between <see cref="Linked"/> and <see cref="Unlinked"/> from the frame and
    /// whether a colour calibration is active. A UI-level intent, NOT a shader mode: it is resolved to a
    /// concrete mode before <see cref="StretchUniforms"/> is built, so it never reaches the GLSL/CPU
    /// stretch (which only ever sees None/Linked/Unlinked/Luma). Deliberately LAST so the numeric values
    /// the shader reads for the real modes are unchanged. Resolution: <see cref="StretchModeExtensions.ResolveAuto"/>.
    /// </summary>
    Auto
}

/// <summary>
/// The ONE resolver of <see cref="StretchMode.Auto"/>. It lives beside the enum rather than in the viewer
/// because a headless producer needs the same answer: the Explorer thumbnail (<see cref="ThumbnailRenderer"/>)
/// must show what the viewer will show when the file is opened, and a second copy of a three-line rule is
/// exactly how two renderings of one frame start to disagree.
/// </summary>
public static class StretchModeExtensions
{
    extension(StretchMode mode)
    {
        /// <summary>
        /// Resolves <see cref="StretchMode.Auto"/> to a concrete mode; returns any other mode unchanged.
        /// Colour + a calibration to show -&gt; Linked (the WB survives as colour); colour without one -&gt;
        /// Unlinked (each channel's background neutralised, no cast asserted); mono -&gt; Linked (the two
        /// coincide). Called by the producers before a <see cref="StretchUniforms"/> is built, so Auto
        /// never reaches the shader.
        /// </summary>
        /// <param name="colourIsNotPhotometric">
        /// True when the frame's colour is not a measurement of the sky, which makes it Unlinked whatever
        /// the calibration says. Two things say so, and the second is the one that reaches real files.
        /// <list type="bullet">
        /// <item>The calibration was fitted through a LINE-SELECTIVE filter. A photometric white balance
        /// assumes a stellar continuum through a broad passband, and through an Ha + OIII filter that
        /// premise never held, so Linked preserves a fit of nothing as a strong cast.</item>
        /// <item>The frame is RANK-DEFICIENT: an HOO composite puts OIII in both green and blue, so three
        /// gains are being fitted to two independent measurements. This is the case an HOO master from
        /// another tool actually hits, because such a file often carries no filter, instrument or sensor
        /// header for the first test to read.</item>
        /// </list>
        /// Ignored when no calibration is active, where the answer is already Unlinked.
        /// </param>
        public StretchMode ResolveAuto(bool isColour, bool calibrationActive,
            bool colourIsNotPhotometric = false)
            => mode is not StretchMode.Auto ? mode
                : !isColour ? StretchMode.Linked
                : calibrationActive && !colourIsNotPhotometric ? StretchMode.Linked : StretchMode.Unlinked;
    }
}
