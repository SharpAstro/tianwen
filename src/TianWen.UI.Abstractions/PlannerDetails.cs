using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Static content helper for the planner details panel.
/// Produces display-ready text lines from <see cref="PlannerState"/>.
/// </summary>
public static class PlannerDetails
{
    // Drop ranks for the line-budget: when the panel has fewer rows than lines (portrait's compact
    // strip), lines shed least-important-first -- highest rank goes first, the name (rank 0) never
    // drops, and the survivors keep their display order. Content *selection*, not truncation.
    private const int RankName = 0;
    private const int RankCoords = 1;
    private const int RankImaging = 2;
    private const int RankRating = 3;
    private const int RankSubtitle = 4;
    private const int RankAlias = 5;
    private const int RankPhotometry = 6;
    private const int RankSize = 7;

    /// <summary>
    /// Returns display lines for the selected target's details panel.
    /// Returns an empty list if no target is selected. <paramref name="maxLines"/> is a line budget
    /// for space-starved panels (portrait phones): over budget, lines shed least-important-first
    /// (size, photometry, alias, type/constellation, rating, imaging window, coordinates -- the name
    /// never drops) while the survivors keep their display order.
    /// </summary>
    public static List<string> GetLines(
        PlannerState state,
        IReadOnlyList<ScoredTarget> filteredTargets,
        DateTimeOffset? currentTime = null,
        int maxLines = int.MaxValue)
    {
        var idx = state.SelectedTargetIndex;
        if (idx < 0 || idx >= filteredTargets.Count)
        {
            return [];
        }

        var scored = filteredTargets[idx];
        var isProposed = state.Proposals.Any(p => p.Target == scored.Target);
        var pinnedCount = state.PinnedCount;
        var entries = new List<(int Rank, string Line)>();

        // Line 1: target name
        var statusSuffix = isProposed ? " [Proposed]" : "";
        entries.Add((RankName, scored.Target.Name + statusSuffix));

        // Physical catalog properties (type, constellation, magnitude, surface brightness, colour
        // index, angular size) for a catalogued target. Resolved live from the immutable
        // ICelestialObjectDB via the target's catalog index -- comets/planets (not in the DB) and
        // bare positions simply skip these lines. One O(1) lookup for the single selected target.
        AddCatalogLines(state, scored, entries);

        // Line 2: coordinates + altitude + peak time + window
        entries.Add((RankCoords, FormatCoordinateLine(state, scored)));

        // Line 3: allocated imaging time (for pinned targets)
        if (isProposed && idx < pinnedCount)
        {
            var imgStart = idx == 0 ? state.AstroDark
                : idx - 1 < state.HandoffSliders.Length ? state.HandoffSliders[idx - 1] : state.AstroDark;
            var imgEnd = idx >= pinnedCount - 1 || idx >= state.HandoffSliders.Length
                ? state.AstroTwilight : state.HandoffSliders[idx];

            var effectiveStart = imgStart;
            if (currentTime is { } ct && ct > imgStart && ct < imgEnd)
            {
                effectiveStart = ct;
            }
            var imgDuration = imgEnd - effectiveStart;
            var imgStartStr = imgStart.ToOffset(state.SiteTimeZone).ToString("HH:mm");
            var imgEndStr = imgEnd.ToOffset(state.SiteTimeZone).ToString("HH:mm");

            var durationStr = imgDuration.TotalHours >= 1.0
                ? $"{(int)imgDuration.TotalHours}h {imgDuration.Minutes:D2}m"
                : $"{(int)imgDuration.TotalMinutes}m";
            entries.Add((RankImaging, $"Imaging: {imgStartStr}\u2013{imgEndStr} ({durationStr})"));
        }

        // Aliases
        if (state.TargetAliases.TryGetValue(scored.Target, out var alias))
        {
            entries.Add((RankAlias, $"Also: {alias}"));
        }

        // Rating
        var maxScore = state.TonightsBest.Length > 0 ? state.TonightsBest[0].CombinedScore : 1.0;
        var rating = PlannerActions.ScoreToRating(scored.CombinedScore, maxScore);
        entries.Add((RankRating, $"Rating: {rating:F1}\u2605"));

        // Over budget: repeatedly shed the highest-ranked (least important) survivor. The name is
        // rank 0 and can never be the strict maximum, so at least one line always remains.
        while (entries.Count > maxLines)
        {
            var worstIdx = 0;
            for (var i = 1; i < entries.Count; i++)
            {
                if (entries[i].Rank > entries[worstIdx].Rank)
                {
                    worstIdx = i;
                }
            }

            if (entries[worstIdx].Rank == RankName)
            {
                break; // only the never-drop name left
            }

            entries.RemoveAt(worstIdx);
        }

        var lines = new List<string>(entries.Count);
        foreach (var (_, line) in entries)
        {
            lines.Add(line);
        }

        return lines;
    }

    private const string WikipediaArticleBase = "https://en.wikipedia.org/wiki/";

    /// <summary>
    /// The Wikipedia article URL for the currently selected target, built from its MAIN catalog
    /// designation (e.g. "NGC 6523" -> https://en.wikipedia.org/wiki/NGC_6523). Returns null when nothing
    /// is selected or the target carries no catalog index (bare positions). Spaces map to '_' (the
    /// MediaWiki title convention) and the rest is percent-encoded -- MediaWiki decodes encoded titles, so
    /// the link stays correct even for designations containing '/', '(' or an en-dash (comets, named DSOs).
    /// The name line rendered in the details panel is the display name; the LINK deliberately uses the
    /// canonical catalog designation so it resolves regardless of which common name we happen to show.
    /// </summary>
    public static string? GetWikipediaUrl(PlannerState state, IReadOnlyList<ScoredTarget> filteredTargets)
    {
        var idx = state.SelectedTargetIndex;
        if (idx < 0 || idx >= filteredTargets.Count)
        {
            return null;
        }
        if (filteredTargets[idx].Target.CatalogIndex is not { } index)
        {
            return null;
        }
        var name = index.ToCanonical();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        return WikipediaArticleBase + Uri.EscapeDataString(name.Replace(' ', '_'));
    }

    /// <summary>
    /// Formats the coordinate + altitude + peak-time + window line for a scored target. Extracted
    /// so the CLI inline prompt can render exactly this line without depending on its positional
    /// index in <see cref="GetLines"/> (which now inserts catalog-property lines above it).
    /// </summary>
    public static string FormatCoordinateLine(PlannerState state, ScoredTarget scored)
    {
        var window = FormatWindow(scored, state);
        var peakTime = state.AltitudeProfiles.TryGetValue(scored.Target, out var prof) && prof.Count > 0
            ? prof.MaxBy(p => p.Alt).Time.ToOffset(state.SiteTimeZone).ToString("HH:mm")
            : "?";
        return $"RA {scored.Target.RA:F3}h  Dec {scored.Target.Dec:+0.0;-0.0}°" +
               $"  Alt {scored.OptimalAltitude:F0}°  Peak {peakTime}  Window {window}";
    }

    // Appends physical-property lines (type + constellation, magnitude + surface brightness + colour
    // index, angular size) for a catalogued target, resolved live from the immutable object DB.
    // Anything not in the DB (comets, planets, bare positions) or without a catalog index adds no
    // lines, leaving the panel byte-identical to the pre-enrichment output.
    private static void AddCatalogLines(PlannerState state, ScoredTarget scored, List<(int Rank, string Line)> entries)
    {
        if (state.ObjectDb is not { } db || scored.Target.CatalogIndex is not { } index)
        {
            return;
        }
        if (!db.TryLookupByIndex(index, out var obj))
        {
            return;
        }

        // Type + constellation, e.g. "Planetary Nebula  CrA".
        var type = obj.ObjectType != ObjectType.Unknown ? obj.ObjectType.ToName() : "";
        var constellation = obj.Constellation != default ? obj.Constellation.ToIAUAbbreviation() : "";
        var subtitle = type;
        if (constellation.Length > 0)
        {
            subtitle = subtitle.Length > 0 ? $"{subtitle}  {constellation}" : constellation;
        }
        if (subtitle.Length > 0)
        {
            entries.Add((RankSubtitle, subtitle));
        }

        // Magnitude + surface brightness + colour index -- each part only when the catalog holds it,
        // so a target with just a V-mag shows "mag 10.70" alone rather than empty placeholders.
        var photometry = "";
        if (!Half.IsNaN(obj.V_Mag))
        {
            photometry = $"mag {(double)obj.V_Mag:F2}";
        }
        if (!Half.IsNaN(obj.SurfaceBrightness))
        {
            var sb = $"SB {(double)obj.SurfaceBrightness:F2} mag/arcsec²";
            photometry = photometry.Length > 0 ? $"{photometry}   {sb}" : sb;
        }
        if (!Half.IsNaN(obj.BMinusV))
        {
            var bv = $"B-V {(double)obj.BMinusV:+0.00;-0.00}";
            photometry = photometry.Length > 0 ? $"{photometry}   {bv}" : bv;
        }
        if (photometry.Length > 0)
        {
            entries.Add((RankPhotometry, photometry));
        }

        // Angular size from the shape catalog: arcseconds for sub-arcminute objects (small planetary
        // nebulae, compact galaxies), arcminutes otherwise. Minor axis appended when defined.
        if (db.TryGetShape(index, out var shape))
        {
            var major = (double)shape.MajorAxis;
            if (!double.IsNaN(major) && major > 0)
            {
                entries.Add((RankSize, FormatSize(major, (double)shape.MinorAxis)));
            }
        }
    }

    private static string FormatSize(double majorArcmin, double minorArcmin)
    {
        var hasMinor = !double.IsNaN(minorArcmin) && minorArcmin > 0;
        if (majorArcmin < 1.0)
        {
            // Sub-arcminute: arcseconds read better than "0.1'".
            return hasMinor
                ? $"size {majorArcmin * 60:F0}\" x {minorArcmin * 60:F0}\""
                : $"size {majorArcmin * 60:F0}\"";
        }
        return hasMinor
            ? $"size {majorArcmin:F1}' x {minorArcmin:F1}'"
            : $"size {majorArcmin:F1}'";
    }

    private static string FormatWindow(ScoredTarget scored, PlannerState state)
    {
        var start = scored.OptimalStart.ToOffset(state.SiteTimeZone);
        var end = (scored.OptimalStart + scored.OptimalDuration).ToOffset(state.SiteTimeZone);
        return $"{start:HH:mm}-{end:HH:mm}";
    }
}
