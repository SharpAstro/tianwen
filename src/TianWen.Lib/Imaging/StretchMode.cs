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
        /// <para><b>Only vetoes when a calibration is what would otherwise make it Linked</b>, because
        /// a bogus white balance is the entire reason the veto exists and with none active there is no
        /// fit to preserve. It used to fire unconditionally, which cost the case it was written about:
        /// an HOO composite carrying no calibration, already colour-balanced by the tool that made it,
        /// opened Unlinked and had three curves fitted to the noise between channels that were already
        /// in agreement -- discarding the very colour the file came with.</para>
        /// </param>
        /// <param name="channelsAlreadyAgree">
        /// True when this frame's per-channel backgrounds are ALREADY level, which makes it Linked even
        /// with no calibration to show. <b>Measured, never declared</b>: it used to be a provenance flag
        /// set only by our own enhance path, so a frame flattened in ANY other tool carried nothing and
        /// got the opposite answer -- which is every Astro Pixel Processor, Siril or GraXpert master a
        /// user opens. The measurement costs nothing: the per-channel medians are computed at document
        /// open because the stretch needs them to render the first frame at all.
        /// <para><b>Unlinked exists to neutralise a background that has not been neutralised.</b> Once
        /// a corrector has done it, three per-channel curves are being fitted to three nearly
        /// identical inputs, so what separates them is no longer the sky but the noise: measured on an
        /// enhanced 10P drizzle master, the channel medians landed within 0.15 percent of each other
        /// while the MADs still spanned 1.7x, and the frame rendered as a flat crimson wash. Linked
        /// applies ONE curve, which is the honest answer for a frame whose channels have already been
        /// brought into agreement -- and it is what the user reached for by hand before this rule
        /// existed.</para>
        /// <para>The threshold is 0.15 percent of the median, which is not a free parameter: it is the
        /// separation measured on an enhanced 10P drizzle master whose channels had been brought into
        /// agreement, and an Astro Pixel Processor HOO composite sits at 0.1 percent. A frame that has
        /// NOT been levelled is nowhere near it -- an uncalibrated OSC sky is percents apart, which is
        /// what a white balance of 1.44 / 1.00 / 1.23 is describing.</para>
        /// </param>
        public StretchMode ResolveAuto(bool isColour, bool calibrationActive,
            bool colourIsNotPhotometric = false, bool channelsAlreadyAgree = false)
            => mode is not StretchMode.Auto ? mode
                : !isColour ? StretchMode.Linked
                : colourIsNotPhotometric && calibrationActive ? StretchMode.Unlinked
                : calibrationActive || channelsAlreadyAgree ? StretchMode.Linked
                : StretchMode.Unlinked;
    }
}
