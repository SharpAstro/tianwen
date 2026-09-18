using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Corrections to OpenNGC rows that are wrong upstream, applied where a row is merged into the catalogue
/// (<c>CelestialObjectDB.MergeNgcRow</c>, the one path both the CSV and the <c>.gs.gz</c> readers take),
/// before the SIMBAD merge or any consumer sees the row.
/// </summary>
/// <remarks>
/// <para><b>Every line here is a fix that has been sent upstream and has not shipped yet.</b> OpenNGC is
/// the geometry base of the catalogue (positions, sizes, types), and it is copied verbatim; this table is
/// not a second source of truth for names, it is the gap between a fix being known and the next OpenNGC
/// refresh bringing it in. <c>OpenNgcCorrectionsTests</c> pins each line against the RAW embedded row:
/// a value to remove must still be there and a value to add must still be absent. The day a refresh
/// carries the upstream fix, that test goes red, and the fix is to delete the line. The table can only
/// shrink toward empty.</para>
/// <para><b>Every line came out of <c>tools/openngc-audit</c>, never from memory.</b> The audit resolves
/// each name and identifier through SIMBAD by POSITION, and the evidence on each line is the SIMBAD
/// object whose position the value actually lands on, so a reviewer checks a coordinate, not an
/// opinion. The first case: "Flame Nebula", "Orion B" and LBN 953 on IC 434, which SIMBAD, Stellarium
/// and Wikipedia all place 34 arcminutes north on NGC 2024. It showed as the red label over the
/// Horsehead in the viewer (2026-09-18).</para>
/// <para>Only common names and identifiers are corrected. A wrong position or size is a different kind of
/// fix, and OpenNGC has not been found wrong on one yet.</para>
/// </remarks>
internal static class OpenNgcCorrections
{
    /// <summary>
    /// One row's correction: the values to drop from and add to its common names and identifiers, the
    /// SIMBAD evidence, and the upstream pull request carrying the fix.
    /// </summary>
    /// <param name="Row">The OpenNGC <c>Name</c> column, e.g. <c>IC0434</c>, <c>NGC2024</c>.</param>
    /// <param name="Evidence">The SIMBAD object and position the removed or added values agree with.</param>
    /// <param name="Upstream">The OpenNGC pull request carrying the fix, e.g. <c>mattiaverga/OpenNGC#53</c>.</param>
    internal readonly record struct Correction(
        string Row,
        ImmutableArray<string> RemoveCommonNames,
        ImmutableArray<string> AddCommonNames,
        ImmutableArray<string> RemoveIdentifiers,
        ImmutableArray<string> AddIdentifiers,
        string Evidence,
        string Upstream);

    private const string FlameEvidence =
        "SIMBAD resolves NAME Flame Nebula, LBN 953 and NAME Orion B to 05 41 43 -01 54 44, the NGC 2024 "
        + "position, 34 arcminutes north of IC 434 (05 41 00.9 -02 27 14), whose only SIMBAD identifier is "
        + "IC 434; Stellarium lists the same aliases on NGC 2024. "
        + "https://simbad.cds.unistra.fr/simbad/sim-id?Ident=NAME+Flame+Nebula";

    private const string CocoonEvidence =
        "SIMBAD resolves NAME Cocoon Galaxy to NGC 4490 (12 30 36 +41 38 37), 2,864 arcminutes from the "
        + "NGC 4990 row it sat on: a digit transposition. "
        + "https://simbad.cds.unistra.fr/simbad/sim-id?Ident=NAME+Cocoon+Galaxy";

    private const string UpstreamPullRequest = "mattiaverga/OpenNGC#53";

    internal static readonly ImmutableArray<Correction> All =
    [
        new Correction("IC0434",
            RemoveCommonNames: ["Flame Nebula", "Orion B"], AddCommonNames: [],
            RemoveIdentifiers: ["LBN 953"], AddIdentifiers: [],
            FlameEvidence, UpstreamPullRequest),
        new Correction("NGC2024",
            RemoveCommonNames: [], AddCommonNames: ["Flame Nebula"],
            RemoveIdentifiers: [], AddIdentifiers: ["LBN 953"],
            FlameEvidence, UpstreamPullRequest),
        new Correction("NGC4990",
            RemoveCommonNames: ["Cocoon Galaxy"], AddCommonNames: [],
            RemoveIdentifiers: [], AddIdentifiers: [],
            CocoonEvidence, UpstreamPullRequest),
        new Correction("NGC4490",
            RemoveCommonNames: [], AddCommonNames: ["Cocoon Galaxy"],
            RemoveIdentifiers: [], AddIdentifiers: [],
            CocoonEvidence, UpstreamPullRequest),
    ];

    private static readonly FrozenDictionary<string, Correction> ByRow =
        All.ToFrozenDictionary(c => c.Row, StringComparer.Ordinal);

    /// <summary>
    /// Applies the correction for <paramref name="row"/>, if there is one. The arrays are replaced, never
    /// mutated, and an array that ends up empty becomes null, which is what the merge takes "none" as.
    /// A value to remove that is not there, or one to add that already is, is left as found: the pin
    /// test is where that is an error, not here.
    /// </summary>
    internal static void Apply(string row, ref string[]? commonNames, ref string[]? identifiers)
    {
        if (!ByRow.TryGetValue(row, out var correction))
        {
            return;
        }

        commonNames = Corrected(commonNames, correction.RemoveCommonNames, correction.AddCommonNames);
        identifiers = Corrected(identifiers, correction.RemoveIdentifiers, correction.AddIdentifiers);
    }

    private static string[]? Corrected(string[]? values, ImmutableArray<string> remove, ImmutableArray<string> add)
    {
        if (remove.IsEmpty && add.IsEmpty)
        {
            return values;
        }

        var kept = (values ?? []).Where(v => !remove.Contains(v, StringComparer.Ordinal)).ToList();
        foreach (var value in add)
        {
            if (!kept.Contains(value, StringComparer.Ordinal))
            {
                kept.Add(value);
            }
        }

        return kept.Count == 0 ? null : [.. kept];
    }
}
