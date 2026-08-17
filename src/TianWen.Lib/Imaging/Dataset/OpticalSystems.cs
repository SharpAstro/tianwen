using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Coarse optical-system kinds for the archive's telescopes. The field-radius PSF profile
    /// exists because a Newtonian's coma grows with field radius while a refractor's field stays
    /// comparatively flat, so the report should SAY which kind each train is instead of leaving
    /// the reader to know the hardware by name. Deliberately coarse, because kind is the axis the
    /// field-radius profile cares about; doublet-vs-triplet would be asserting element counts
    /// nobody has verified against the physical hardware.
    /// </summary>
    public enum OpticalSystem
    {
        /// <summary>
        /// The name did not classify (unknown telescope, or an unparseable train label). The safe
        /// default, and deliberately the zero value.
        /// </summary>
        Unclassified = 0,

        /// <summary>A camera behind a bare photographic lens, telephoto primes included.</summary>
        CameraLens,

        /// <summary>Any refracting telescope; petzvals count as refractors at this grain.</summary>
        Refractor,

        /// <summary>A reflector whose coma grows with field radius. None in the archive yet.</summary>
        Newtonian,
    }

    /// <summary>
    /// Classifies the archive's telescopes into <see cref="OpticalSystem"/> kinds.
    ///
    /// <para><b>The archive holds no Newtonian yet.</b> Checked 2026-08-17 across every
    /// <c>psf-sessions.jsonl</c> store (six bake roots, 50-134 records each): the trains are the
    /// Samyang 135 telephoto, the SH61 EDPH / WO ZS61 / WO RC51 refractors, and bare camera-lens
    /// rigs; the SW8 never appears. When older archive years bake one in (task #11), add it here.
    /// The per-(train, filter) field-radius sections exist largely for that case.</para>
    ///
    /// <para>Kept in the repo for the same reason as <see cref="TelescopeAliases"/>: a
    /// classification is a factual claim about hardware, worth reviewing in a diff, and the set is
    /// small and slow-moving.</para>
    /// </summary>
    public static class OpticalSystems
    {
        extension(OpticalSystem kind)
        {
            /// <summary>The words the report prints for this kind.</summary>
            public string Label => kind switch
            {
                OpticalSystem.Refractor => "refractor",
                OpticalSystem.CameraLens => "camera lens",
                OpticalSystem.Newtonian => "Newtonian",
                _ => "(unclassified)",
            };
        }

        /// <summary>Canonical telescope name (post-<see cref="TelescopeAliases"/>) to kind.</summary>
        private static readonly FrozenDictionary<string, OpticalSystem> ByTelescope =
            new Dictionary<string, OpticalSystem>(StringComparer.OrdinalIgnoreCase)
            {
                ["Samyang 135 f/2 ED"] = OpticalSystem.CameraLens,
                ["SH61 EDPH"] = OpticalSystem.Refractor,
                ["WO ZS61"] = OpticalSystem.Refractor,
                // The RedCat 51 is a petzval, which is still a refractor at this table's grain.
                ["WO RC51"] = OpticalSystem.Refractor,
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Classifies one telescope name. Empty means the train carries no TELESCOP at all (a
        /// camera behind a bare photographic lens, identified only by focal length), which is a
        /// camera-lens rig by construction, not an unknown. An unknown NAME stays
        /// <see cref="OpticalSystem.Unclassified"/> rather than being guessed at: the safe
        /// direction, mirroring <see cref="TelescopeAliases.Canonical"/>.
        /// </summary>
        public static OpticalSystem Classify(string telescope)
        {
            if (string.IsNullOrWhiteSpace(telescope))
            {
                return OpticalSystem.CameraLens;
            }
            var canonical = TelescopeAliases.Canonical(telescope.Trim());
            return ByTelescope.TryGetValue(canonical, out var kind) ? kind : OpticalSystem.Unclassified;
        }

        /// <summary>
        /// Classifies a whole train label ("ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm"), the
        /// only form a report holds for a stored session. A label that does not parse is
        /// <see cref="OpticalSystem.Unclassified"/> rather than <see cref="OpticalSystem.CameraLens"/>:
        /// an unreadable telescope slot is not evidence there is no telescope.
        /// </summary>
        public static OpticalSystem ClassifyLabel(string label)
        {
            return CalibrationResolver.CalTrain.TryParseDescription(label, out var train)
                ? Classify(train.Telescope)
                : OpticalSystem.Unclassified;
        }
    }
}
