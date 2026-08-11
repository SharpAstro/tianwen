using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Canonical spellings for optics whose FITS <c>TELESCOP</c> header was written differently at
    /// different times. One physical lens recorded under two names becomes two optical trains, and
    /// since the field-radius PSF profile is measured PER TRAIN, that splits one profile into two
    /// weaker ones: the archive's Samyang 135 was captured as both "SAMYANG 135mm" (3 sessions) and
    /// "Samyang 135 f/2 ED" (35), so the deconvolver sweep was being calibrated from a 3-session
    /// profile and a 35-session profile of the same glass.
    ///
    /// <para><b>This is a display-time merge, and deliberately so.</b> The alias is applied where the
    /// report buckets sessions into trains, never on the way into
    /// <see cref="DatasetPsfStore"/>: the store records what the headers actually said, so a
    /// mis-aliased entry here can be corrected by re-rendering the report rather than by
    /// re-measuring 50 sessions. It follows that adding an alias is free -- no re-registration, no
    /// re-export, just a report re-render.</para>
    ///
    /// <para><b>Only the NAME is aliased, never the focal length.</b> The same archive holds
    /// "WO ZS61 @ 288mm" and "WO ZS61 @ 360mm", and that is one scope behind two different
    /// correctors: a 0.8x reducer and a flattener that claims 1x (user-confirmed, and 360 x 0.8 =
    /// 288 exactly). A reducer changes the off-axis aberration the field-radius profile exists to
    /// characterise, so those must stay separate trains however identical the glass is. The focal
    /// length is what carries that distinction, so an alias must never touch it -- collapsing on
    /// name alone would merge a reduced field with an unreduced one.</para>
    /// </summary>
    public static class TelescopeAliases
    {
        /// <summary>
        /// Raw <c>TELESCOP</c> value to canonical name, matched case-insensitively (a header that
        /// differs only in case is the same instrument by any reading). Extend it as older archive
        /// years are baked and surface further spellings.
        ///
        /// <para>Kept in the repo rather than in a file beside the dataset on purpose: an alias
        /// asserts that two names are the same physical hardware, which is a factual claim worth
        /// reviewing in a diff, and the set is small and slow-moving. If it ever grows past what a
        /// reviewer can sanity-check, that is the point to move it to a data file, not before.</para>
        /// </summary>
        private static readonly FrozenDictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SAMYANG 135mm"] = "Samyang 135 f/2 ED",
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>The canonical name for one telescope, or <paramref name="telescope"/> unchanged
        /// when no alias applies (the common case, and the safe direction: an unknown name stays its
        /// own train rather than being folded into someone else's profile).</summary>
        public static string Canonical(string telescope)
        {
            return Aliases.TryGetValue(telescope, out var canonical) ? canonical : telescope;
        }

        /// <summary>
        /// Canonicalises a whole train label as produced by
        /// <see cref="CalibrationResolver.CalTrain.Describe"/>, e.g.
        /// "ZWO ASI533MC Pro / SAMYANG 135mm @ 130mm" becomes
        /// "ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm". A label operating on the label rather
        /// than on a <see cref="CalibrationResolver.CalTrain"/> is what the report needs: a session
        /// read back from the PSF store carries only the label, because its frames were never
        /// re-read.
        ///
        /// <para>A label that does not parse is returned unchanged. That is the fail-safe direction:
        /// the worst outcome is the split profile we already had, never two unrelated trains merged
        /// into one.</para>
        /// </summary>
        public static string CanonicalizeLabel(string label)
        {
            if (!CalibrationResolver.CalTrain.TryParseDescription(label, out var train))
            {
                return label;
            }
            var canonical = TelescopeAliases.Canonical(train.Telescope);
            return string.Equals(canonical, train.Telescope, StringComparison.Ordinal)
                ? label
                : (train with { Telescope = canonical }).Describe();
        }
    }
}
