using System.Collections.Generic;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// The working collections of one <see cref="OverlayEngine.GatherSkyMapOverlayCandidates"/>, lent by a caller that
/// gathers again and again so that each gather fills the ones the last emptied.
/// </summary>
/// <remarks>
/// One gather at a time: the collections are the gather's for as long as it runs, so a caller that gathers on another
/// thread lends them to one walk and to the next only once that one has ended (the GPU atlas has one in flight at a
/// time by construction). A gather without one makes its own, as before.
/// </remarks>
public sealed class OverlayGatherScratch
{
    /// <summary>Every index the walk has met, so an object listed in several cells is taken once.</summary>
    internal HashSet<CatalogIndex> Seen { get; } = [];

    /// <summary>What the walk kept, before it is sorted and made into candidates.</summary>
    internal List<(CatalogIndex CatIdx, CelestialObject Obj, bool IsPinned)> Found { get; } = [];

    /// <summary>
    /// Each object's candidate as an earlier gather made it, unpinned, by its index and its label's zoom tier: what a
    /// candidate is made of (its marker, colour, label, priority, whether it has a picture) is the catalogue's and the
    /// tier's, and only its pin is the gather's. Making them again was most of what a gather allocated (its label
    /// strings above all: 13 MB in 18 s of a pan, 2026-11-25), and a pan runs a gather a second.
    /// </summary>
    internal Dictionary<(CatalogIndex Index, int Tier), OverlayCandidate> Made { get; } = [];

    /// <summary>The catalogue <see cref="Made"/> was made from: another empties it.</summary>
    internal ICelestialObjectDB? MadeFrom { get; set; }

    /// <summary>The most candidates <see cref="Made"/> keeps before it starts again, which a catalogue never reaches.</summary>
    internal const int MadeLimit = 100_000;
}
