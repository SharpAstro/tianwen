using System;
using System.Collections.Generic;
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
    /// The F3 search interaction (input wiring + Up/Down/Enter/Escape protocol + the typed result list): a
    /// <see cref="SkyMapSearchInteraction"/> over <see cref="SearchInput"/>. Owns the results
    /// (<c>Interaction.Results</c>) and the highlighted index (<c>Interaction.SelectedIndex</c>) -- state
    /// that used to live directly on this type. Set by the host (desktop <see cref="AppSignalHandler"/> /
    /// web Planner.razor); the sky map only appears in those hosts, so it is non-null whenever the modal is
    /// used (readers still null-guard defensively).
    /// </summary>
    public SkyMapSearchInteraction? Interaction { get; set; }

    /// <summary>
    /// Flat prefix/substring search index built lazily on first open from
    /// <see cref="ICelestialObjectDB.CreateAutoCompleteList"/> merged with comet
    /// designations + common names. Rebuilt on catalog reload (atomic replacement).
    /// </summary>
    public ImmutableArray<string> SearchIndex { get; set; } = [];

    /// <summary>
    /// Maps a searchable comet string (canonical designation AND common name, both keys point at the same
    /// comet) to its <see cref="Catalog.Comet"/> index AND its full display label (e.g. both "10P" and
    /// "Tempel" map to index 10P + display "10P (Tempel)", so a match on either key shows the full name in
    /// the results list). Comets are NOT in the object DB, so the filter + commit resolve a matched comet
    /// entry through this map rather than <see cref="ICelestialObjectDB"/>. Built alongside
    /// <see cref="SearchIndex"/> in <c>OpenSearch</c>; touched only on the UI thread (search open / keystroke
    /// / commit), so a plain dictionary is safe.
    /// </summary>
    public Dictionary<string, (CatalogIndex Index, string Display)> CometEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Selected object — shown in the info panel after Enter / click / click-on-map.
    /// Null when no object is selected.
    /// </summary>
    public SkyMapInfoPanelData? InfoPanel { get; set; }

    /// <summary>Scroll offset (rows) for the results list.</summary>
    public int ResultsScrollOffset { get; set; }
}
