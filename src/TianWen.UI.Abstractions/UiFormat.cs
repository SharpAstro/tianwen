using System.Globalization;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Number formatting for on-screen labels, where the same value must read the same way on every host.
    /// </summary>
    internal static class UiFormat
    {
        /// <summary>
        /// A fraction as a whole-number percentage: <c>0.5f</c> becomes <c>"50%"</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Not <c>"P0"</c>, and the difference is not cosmetic pedantry.</b> The invariant percent
        /// pattern puts a SPACE before the sign ("50 %") while en-US does not ("50%"), so the identical
        /// label rendered differently depending on which culture the host resolved -- "Boost 50%" on a
        /// Windows dev box and "Boost 50 %" on a Linux CI runner, from one <c>{x:P0}</c>. Measured on
        /// Ubuntu 24.04 rather than recalled: a bare Linux host with no LANG resolves the INVARIANT
        /// culture, and its separator is U+0020, a plain space (other cultures use U+00A0 or U+202F,
        /// which is why the guard below rejects all three rather than just the one seen).</para>
        /// <para><b>How it was found is the part worth keeping.</b> It surfaced only because a split label
        /// happens to be pinned by a test, and only once CI could run again after an unrelated outage --
        /// every other percentage in the viewer had exactly the same split and none of them was asserted,
        /// so the app would have shipped rendering "50 %" in the toolbar beside "50%" in the split label on
        /// the same screen. A formatting difference that no test pins is invisible on the box you develop
        /// on, because it is the culture you never vary.</para>
        /// <para><b>This is not only a CI artifact.</b> <c>TianWen.UI.Web</c> sets
        /// <c>InvariantGlobalization</c>, so the shipped browser build runs in exactly the mode that
        /// produces "50 %" -- the Linux runner just happened to be where it was noticed first.</para>
        /// <para>Invariant rather than current-culture on purpose: this is a technical readout, and the
        /// whole point is that it does not vary by host. Decimal readouts (<c>F1</c>, <c>F2</c>) are left
        /// on the current culture, where a locale-appropriate separator is genuinely wanted.</para>
        /// </remarks>
        internal static string Percent0(float fraction)
            => string.Create(CultureInfo.InvariantCulture, $"{fraction * 100f:F0}%");
    }
}
