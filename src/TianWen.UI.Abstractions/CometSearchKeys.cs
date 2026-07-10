using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Single source of the searchable key forms for the loaded comets, shared by the sky-map F3
    /// search (<see cref="SkyMapSearchActions"/>) and the planner-tab search/autocomplete
    /// (<see cref="PlannerActions"/>). Comets are NOT in <c>ICelestialObjectDB</c> (immutable after
    /// init), so every text-search surface augments its own index from <see cref="ICometRepository"/>
    /// through here -- keeping the four accepted key spellings identical everywhere.
    /// </summary>
    public static class CometSearchKeys
    {
        /// <summary>
        /// Every way a user might type each loaded comet, all mapping to the same
        /// <see cref="CatalogIndex"/> + <see cref="CometElements.DisplayName"/> label:
        /// the canonical designation (<c>10P</c> / <c>C/2026 A1</c>), the common name (<c>Tempel</c>),
        /// the parenthetical form (<c>C/2026 A1 (PANSTARRS)</c>), and the slash form (<c>10P/Tempel</c>).
        /// Yields nothing when <paramref name="comets"/> is null or not yet loaded.
        /// </summary>
        public static IEnumerable<(string Key, CatalogIndex Index, string Display)> Enumerate(ICometRepository? comets)
        {
            if (comets is null)
            {
                yield break;
            }

            foreach (var el in comets.All)
            {
                if (el.CatalogIndex is not { } idx)
                {
                    continue;
                }

                var canonical = el.Designation.ToCanonical();
                var display = el.DisplayName;
                yield return (canonical, idx, display);

                if (el.CommonName is { Length: > 0 } commonName)
                {
                    yield return (commonName, idx, display);
                    yield return ($"{canonical} ({commonName})", idx, display);
                    yield return ($"{canonical}/{commonName}", idx, display);
                }
            }
        }

        /// <summary>
        /// Resolves a typed string to a comet (case-insensitive exact match against any of the key
        /// forms), returning its <see cref="CatalogIndex"/> + display label. First match wins.
        /// </summary>
        public static bool TryResolve(ICometRepository? comets, string query, out CatalogIndex index, out string display)
        {
            index = default;
            display = string.Empty;
            if (comets is null || string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            var q = query.Trim();
            foreach (var (key, idx, disp) in Enumerate(comets))
            {
                if (string.Equals(key, q, StringComparison.OrdinalIgnoreCase))
                {
                    index = idx;
                    display = disp;
                    return true;
                }
            }

            return false;
        }
    }
}
