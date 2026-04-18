using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions;

/// <summary>
/// One row in the live-filtered result list. Carries only what the renderer
/// needs — the full <see cref="CelestialObject"/> is re-resolved on commit.
/// </summary>
public readonly record struct SkyMapSearchResult(
    string Display,
    CatalogIndex? Index,
    ObjectType ObjType,
    float VMag);

/// <summary>
/// Mutable state for the F3 search modal. Lives on <see cref="SkyMapState"/>.
/// Collections are <see cref="ImmutableArray{T}"/> for atomic swap — the catalog
/// index and filter results can be rebuilt on a background thread while the
/// render thread walks a snapshot.
/// </summary>
public class SkyMapSearchState
{
    /// <summary>True while the modal is open.</summary>
    public bool IsOpen { get; set; }

    /// <summary>The search textbox.</summary>
    public TextInputState SearchInput { get; } = new() { Placeholder = "Type object name..." };

    /// <summary>
    /// Live-filtered results for the current <see cref="SearchInput"/> text.
    /// Atomically replaced on each query — readers snapshot into a local.
    /// </summary>
    public ImmutableArray<SkyMapSearchResult> Results { get; set; } = [];

    /// <summary>Index of the keyboard-highlighted row, -1 = none.</summary>
    public int SelectedResultIndex { get; set; } = -1;

    /// <summary>
    /// Flat prefix/substring search index built lazily on first open from
    /// <see cref="ICelestialObjectDB.CreateAutoCompleteList"/>. Rebuilt on
    /// catalog reload (atomic replacement).
    /// </summary>
    public ImmutableArray<string> SearchIndex { get; set; } = [];

    /// <summary>
    /// Selected object — shown in the info panel after Enter / click / click-on-map.
    /// Null when no object is selected.
    /// </summary>
    public SkyMapInfoPanelData? InfoPanel { get; set; }

    /// <summary>Scroll offset (rows) for the results list.</summary>
    public int ResultsScrollOffset { get; set; }
}
