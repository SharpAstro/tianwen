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
        public StretchMode ResolveAuto(bool isColour, bool calibrationActive)
            => mode is not StretchMode.Auto ? mode
                : !isColour ? StretchMode.Linked
                : calibrationActive ? StretchMode.Linked : StretchMode.Unlinked;
    }
}
