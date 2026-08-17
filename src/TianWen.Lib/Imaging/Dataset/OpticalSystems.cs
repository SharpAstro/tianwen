using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Coarse optical-system classification for the archive's telescopes. The field-radius PSF
    /// profile exists because a Newtonian's coma grows with field radius while a refractor's field
    /// stays comparatively flat, so the report should SAY which kind each train is instead of
    /// leaving the reader to know the hardware by name.
    ///
    /// <para><b>The archive holds no Newtonian yet.</b> Checked 2026-08-17 across every
    /// <c>psf-sessions.jsonl</c> store (six bake roots, 50-134 records each): the trains are the
    /// Samyang 135 telephoto, the SH61 EDPH / WO ZS61 / WO RC51 refractors, and bare camera-lens
    /// rigs -- the SW8 never appears. When older archive years bake one in (task #11), add it here;
    /// the per-(train, filter) field-radius sections exist largely for that case.</para>
    ///
    /// <para>Kept in the repo for the same reason as <see cref="TelescopeAliases"/>: a
    /// classification is a factual claim about hardware, worth reviewing in a diff, and the set is
    /// small and slow-moving. Deliberately coarse (refractor vs Newtonian vs camera lens), because
    /// that is the axis the field-radius profile cares about; doublet-vs-triplet would be asserting
    /// element counts nobody has verified against the physical hardware.</para>
    /// </summary>
    public static class OpticalSystems
    {
        public const string Refractor = "refractor";
        public const string CameraLens = "camera lens";
        public const string Newtonian = "Newtonian";
        public const string Unclassified = "(unclassified)";

        /// <summary>Canonical telescope name (post-<see cref="TelescopeAliases"/>) to kind.</summary>
        private static readonly FrozenDictionary<string, string> ByTelescope =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Samyang 135 f/2 ED"] = CameraLens,
                ["SH61 EDPH"] = Refractor,
                ["WO ZS61"] = Refractor,
                // The RedCat 51 is a petzval, which is still a refractor at this table's grain.
                ["WO RC51"] = Refractor,
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Classifies one telescope name. Empty means the train carries no TELESCOP at all -- a
        /// camera behind a bare photographic lens, identified only by focal length -- which is a
        /// camera-lens rig by construction, not an unknown. An unknown NAME stays
        /// <see cref="Unclassified"/> rather than being guessed at: the safe direction, mirroring
        /// <see cref="TelescopeAliases.Canonical"/>.
        /// </summary>
        public static string Classify(string telescope)
        {
            if (string.IsNullOrWhiteSpace(telescope))
            {
                return CameraLens;
            }
            var canonical = TelescopeAliases.Canonical(telescope.Trim());
            return ByTelescope.TryGetValue(canonical, out var kind) ? kind : Unclassified;
        }

        /// <summary>
        /// Classifies a whole train label ("ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm"), the
        /// only form a report holds for a stored session. A label that does not parse is
        /// <see cref="Unclassified"/> rather than <see cref="CameraLens"/>: an unreadable telescope
        /// slot is not evidence there is no telescope.
        /// </summary>
        public static string ClassifyLabel(string label)
        {
            return CalibrationResolver.CalTrain.TryParseDescription(label, out var train)
                ? Classify(train.Telescope)
                : Unclassified;
        }
    }
}
