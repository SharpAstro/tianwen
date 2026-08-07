namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Which way the app paints. Four states rather than a light/dark toggle, so "follow the
    /// desktop" is a real choice and not merely the initial value of a boolean the user then has to
    /// keep in step by hand.
    /// </summary>
    public enum UiThemeState
    {
        /// <summary>
        /// Follow the desktop's own light/dark setting, including a change made mid-session.
        /// Resolves to <see cref="Light"/> or <see cref="Dark"/>, and <b>never</b> to
        /// <see cref="Night"/>.
        /// </summary>
        System,

        /// <summary>Light chrome, whatever the desktop is set to.</summary>
        Light,

        /// <summary>Dark chrome, whatever the desktop is set to.</summary>
        Dark,

        /// <summary>
        /// Deep red on black, to preserve the observer's dark adaptation at the mount.
        ///
        /// <para><b>Not a darker <see cref="Dark"/>, and deliberately unreachable from
        /// <see cref="System"/>.</b> Most hours in this app are desk hours (planning, stacking,
        /// reviewing subs) in a normally lit room, where red on black is fatiguing for no benefit;
        /// dark adaptation only matters within a few metres of the eyepiece. So it must never be
        /// entered by accident, and must be unmistakable once entered. The hue discontinuity at the
        /// boundary is doing that work, not a styling accident.</para>
        ///
        /// <para>Two rules follow from the physics and are enforced by the palette, not by
        /// convention. Red is the only cheap channel: scotopic sensitivity at the sRGB primaries'
        /// dominant wavelengths is roughly R 0.0155, G 0.49, B 0.61, so blue is marginally
        /// <i>worse</i> than green and both are 30 to 40 times worse than red. Blue is therefore
        /// zero throughout and green is spent only to buy hue separation. And because red on black
        /// caps at 5.25:1 contrast, the whole text ladder has to fit under that ceiling, which is
        /// why anything that must be READ uses <c>BodyText</c> here and <c>DimText</c> is reserved
        /// for chrome nobody needs to read.</para>
        /// </summary>
        Night,
    }
}
