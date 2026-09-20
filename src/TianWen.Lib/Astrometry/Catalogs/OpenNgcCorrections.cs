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
/// <para><b>A line here is either a fix upstream has ACCEPTED and not yet shipped, or one upstream has
/// DECLINED and we have kept anyway.</b> OpenNGC is the geometry base of the catalogue (positions, sizes,
/// types), and it is copied verbatim; the accepted kind is the gap between a fix being known and the next
/// refresh bringing it in, and the table shrinks toward empty as those land.
/// <c>OpenNgcCorrectionsTests</c> pins each line against the RAW embedded row: a value to remove must
/// still be there and a value to add must still be absent. For an ACCEPTED line, the day a refresh carries
/// the upstream fix that test goes red and the fix is to delete the line.</para>
/// <para><b>A DECLINED line is permanent, and saying so is the point.</b> It reads exactly like an
/// accepted one, so without the distinction a future reader waits for a refresh that is never coming, and
/// a maintainer's considered "no" is silently re-litigated as a stale shim. Each states the upstream
/// verdict and why we still differ. Adding one is a deliberate divergence from the upstream catalogue,
/// not a shortcut; it needs a reason better than "SIMBAD says so", because the whole lesson of
/// mattiaverga/OpenNGC#53 is that SIMBAD alone is not enough (see the audit tool's own README).</para>
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
    /// <param name="Upstream">The OpenNGC pull request the fix was sent as, e.g.
    /// <c>mattiaverga/OpenNGC#53</c>, AND its verdict. A line whose verdict is a decline never expires,
    /// so the string has to carry it -- naming only the PR reads as "shipping soon" for a change that
    /// will never arrive.</param>
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
        + "IC 434. https://simbad.cds.unistra.fr/simbad/sim-id?Ident=NAME+Flame+Nebula "
        + "-- and the corroboration is NOT SIMBAD's, which matters because upstream DECLINED this on a "
        + "taxonomy argument (NGC 2024 the star cluster inside Orion B, IC 434 the HII region). "
        + "Stellarium's curated names.dat puts Flame Nebula on NGC 2024 under NINE independent source "
        + "keys -- WK, TSS, DSC-HT, WSO, CC, DN, B500, RNGCIC, U2K -- where WK is its own 'well-known "
        + "name which does not need special source ... the preferred name'. SIMBAD is a separate key in "
        + "that file and is not among them. The same file carries '# NGC 4990 _(\"Cocoon Galaxy\") "
        + "# SIMBAD' as a WITHDRAWN line, i.e. Stellarium met the neighbouring error in this same PR and "
        + "recorded SIMBAD as its only backer. See FlameUpstream.";

    private const string CocoonEvidence =
        "SIMBAD resolves NAME Cocoon Galaxy to NGC 4490 (12 30 36 +41 38 37), 2,864 arcminutes from the "
        + "NGC 4990 row it sat on: a digit transposition. "
        + "https://simbad.cds.unistra.fr/simbad/sim-id?Ident=NAME+Cocoon+Galaxy";

    /// <summary>
    /// The Cocoon Galaxy fix, ACCEPTED upstream ("good catch", 2026-09-19). Both lines carrying it are
    /// therefore temporary and go red on the refresh that brings it in.
    /// </summary>
    private const string CocoonUpstream = "mattiaverga/OpenNGC#53, accepted 2026-09-19";

    /// <summary>
    /// The Flame Nebula fix, DECLINED upstream (2026-09-19), so the two lines carrying it are permanent.
    /// </summary>
    /// <remarks>
    /// The maintainer's reasoning, verbatim: <i>"NGC2024 is listed as a cluster of stars within Orion B,
    /// IC0434 is the HII region. So I'd prefer to leave the nebula names / LBN associated to IC0434."</i>
    /// He conceded "Orion B" was a stretch and will add a note to OpenNGC's own notes column instead.
    /// <para><b>We keep the correction, and the reason is what a user reads on a photograph rather than a
    /// taxonomy.</b> The Flame Nebula IS NGC 2024 in every source a user of this app will meet -- SIMBAD,
    /// Stellarium, Wikipedia, and every astrophotograph captioned with it -- and IC 434 is the emission
    /// nebula behind the Horsehead, 34 arcminutes south. This is the defect that started the audit: the
    /// viewer drew "Flame Nebula" over the Horsehead (2026-09-18). A note in a column the merge does not
    /// read cannot fix a label, so the divergence stays.</para>
    /// <para>This is the one place the table deliberately disagrees with upstream rather than waiting for
    /// it, and it is a NAME, which is the only kind of value this table touches. Nothing about the
    /// geometry is second-guessed.</para>
    /// </remarks>
    private const string FlameUpstream = "mattiaverga/OpenNGC#53, DECLINED 2026-09-19 (permanent divergence)";

    internal static readonly ImmutableArray<Correction> All =
    [
        new Correction("IC0434",
            RemoveCommonNames: ["Flame Nebula", "Orion B"], AddCommonNames: [],
            RemoveIdentifiers: ["LBN 953"], AddIdentifiers: [],
            FlameEvidence, FlameUpstream),
        new Correction("NGC2024",
            RemoveCommonNames: [], AddCommonNames: ["Flame Nebula"],
            RemoveIdentifiers: [], AddIdentifiers: ["LBN 953"],
            FlameEvidence, FlameUpstream),
        new Correction("NGC4990",
            RemoveCommonNames: ["Cocoon Galaxy"], AddCommonNames: [],
            RemoveIdentifiers: [], AddIdentifiers: [],
            CocoonEvidence, CocoonUpstream),
        new Correction("NGC4490",
            RemoveCommonNames: [], AddCommonNames: ["Cocoon Galaxy"],
            RemoveIdentifiers: [], AddIdentifiers: [],
            CocoonEvidence, CocoonUpstream),
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
