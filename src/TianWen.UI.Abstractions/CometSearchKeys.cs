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
        ///
        /// <para><b>These are MATCH keys, never suggestion text.</b> A bare common name is shared by
        /// design: SBDB's <c>name</c> field is the discoverer, so 216 names cover 3,563 of the 4,069
        /// comets ("SOHO" alone is 1,465, and "Tempel" is eight). A surface that lists the key shows a
        /// column of identical rows, and a surface that maps key to comet 1:1 silently keeps whichever
        /// came first. Use <see cref="EnumerateSuggestions"/> for anything the user reads or picks;
        /// <see cref="IsUniqueForm"/> says whether a given key can identify a comet on its own.</para>
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
        /// One entry per loaded comet, always the full <see cref="CometElements.DisplayName"/>. This is
        /// what a suggestion list, a dropdown or a result row shows: it is unique (the designation is in
        /// it), it round-trips through <see cref="TryResolve"/> because the display form IS one of the
        /// accepted key spellings, and it is a quarter the size of <see cref="Enumerate"/>.
        /// </summary>
        public static IEnumerable<(string Display, CatalogIndex Index)> EnumerateSuggestions(ICometRepository? comets)
        {
            if (comets is null)
            {
                yield break;
            }

            foreach (var el in comets.All)
            {
                if (el.CatalogIndex is { } idx)
                {
                    yield return (el.DisplayName, idx);
                }
            }
        }

        /// <summary>
        /// True when <paramref name="key"/> is a spelling that identifies ONE comet: the canonical
        /// designation and the two combined forms all embed it, so only the bare common name is
        /// ambiguous. Lets a caller register the unambiguous spellings in a 1:1 map without having to
        /// know which of the four forms it is holding.
        /// </summary>
        public static bool IsUniqueForm(string key, string? commonName)
            => commonName is not { Length: > 0 }
                || !string.Equals(key, commonName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves a typed string to a comet (case-insensitive exact match against any of the key
        /// forms), returning its <see cref="CatalogIndex"/> + display label.
        ///
        /// <para>Two passes, and the order is the point: an unambiguous spelling wins over a bare common
        /// name ANYWHERE in the set. One pass in catalog order would resolve "10P/Tempel" correctly only
        /// because 10P happens to be enumerated before some other comet whose common name is the whole
        /// string; with a single pass, a query that exactly names one comet could still be answered with
        /// a different one. A bare shared name (pass two) is genuinely ambiguous and still resolves to
        /// the first, which is why a picker must offer <see cref="EnumerateSuggestions"/> instead.</para>
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
            foreach (var el in comets.All)
            {
                if (el.CatalogIndex is not { } idx)
                {
                    continue;
                }

                var canonical = el.Designation.ToCanonical();
                if (string.Equals(canonical, q, StringComparison.OrdinalIgnoreCase)
                    || (el.CommonName is { Length: > 0 } cn
                        && (string.Equals($"{canonical} ({cn})", q, StringComparison.OrdinalIgnoreCase)
                            || string.Equals($"{canonical}/{cn}", q, StringComparison.OrdinalIgnoreCase))))
                {
                    index = idx;
                    display = el.DisplayName;
                    return true;
                }
            }

            foreach (var el in comets.All)
            {
                if (el.CatalogIndex is { } idx
                    && el.CommonName is { Length: > 0 } commonName
                    && string.Equals(commonName, q, StringComparison.OrdinalIgnoreCase))
                {
                    index = idx;
                    display = el.DisplayName;
                    return true;
                }
            }

            return false;
        }
    }
}
