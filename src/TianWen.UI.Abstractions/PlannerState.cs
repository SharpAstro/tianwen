using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Shared state for the observation planner, used by both TUI and GUI.
/// </summary>
public class PlannerState
{
    /// <summary>The active equipment profile.</summary>
    public Profile? ActiveProfile { get; set; }

    /// <summary>Tonight's best targets ranked by score.
    /// <para>
    /// Immutable so the render thread and the background planner recompute can read /
    /// replace without racing. Writers build a new <see cref="ImmutableArray{T}"/> and
    /// assign in one atomic reference update.
    /// </para></summary>
    public ImmutableArray<ScoredTarget> TonightsBest { get; set; } = [];

    /// <summary>User's proposed observations (selected from TonightsBest or manually added).
    /// <para>
    /// Immutable (same rationale as <see cref="TonightsBest"/>). Mutations go through
    /// <c>PlannerActions.AddProposal / RemoveProposal / ToggleProposal</c> which assign
    /// a new <see cref="ImmutableArray{T}"/> so readers never see a half-updated list.
    /// </para></summary>
    public ImmutableArray<ProposedObservation> Proposals { get; set; } = [];

    /// <summary>
    /// Primary-OTA camera sensor field of view (width, height) in degrees, or null when the active
    /// profile has no captured sensor geometry (see <c>ProfileData.PrimarySensorFovDeg</c>). Set by the
    /// host from the active profile. Gates smart framing: with no FOV there is no frame to group into,
    /// and <see cref="FramingGroups"/> stays empty so scheduling is byte-identical to the ungrouped path.
    /// </summary>
    public (double WidthDeg, double HeightDeg)? SensorFovDeg { get; set; }

    /// <summary>
    /// Derived "smart framing" groups: pinned proposals plus catalogued neighbours that share one
    /// sensor frame (e.g. M8 + M20). Recomputed from <see cref="Proposals"/> + <see cref="SensorFovDeg"/>
    /// whenever either changes (<c>PlannerFramingActions.ComputeFramingGroups</c>). Consumed by the
    /// schedule build (one pointing per multi-target group) and the sky-map / planner overlays.
    /// Immutable + atomically replaced, same rationale as <see cref="Proposals"/>.
    /// </summary>
    public ImmutableArray<FramingGroup> FramingGroups { get; set; } = [];

    /// <summary>Index of the currently selected target in the merged target list.</summary>
    public int SelectedTargetIndex { get; set; }

    /// <summary>Start of astronomical night.</summary>
    public DateTimeOffset AstroDark { get; set; }

    /// <summary>End of astronomical night.</summary>
    public DateTimeOffset AstroTwilight { get; set; }

    /// <summary>Civil twilight set time (evening). Null if not computed or polar.</summary>
    public DateTimeOffset? CivilSet { get; set; }

    /// <summary>Civil twilight rise time (morning). Null if not computed or polar.</summary>
    public DateTimeOffset? CivilRise { get; set; }

    /// <summary>Nautical twilight set time (evening).</summary>
    public DateTimeOffset? NauticalSet { get; set; }

    /// <summary>Nautical twilight rise time (morning).</summary>
    public DateTimeOffset? NauticalRise { get; set; }

    /// <summary>Site latitude in degrees. NaN when not yet resolved from profile.</summary>
    public double SiteLatitude { get; set; } = double.NaN;

    /// <summary>Site longitude in degrees. NaN when not yet resolved from profile.</summary>
    public double SiteLongitude { get; set; } = double.NaN;

    /// <summary>Minimum altitude above horizon in degrees.</summary>
    public byte MinHeightAboveHorizon { get; set; } = 20;

    /// <summary>Fine-grained altitude profiles per target (for chart rendering).
    /// <para>
    /// Immutable so the render thread and the background planner recompute can read /
    /// replace without racing. Writers build a new dict (via builder for bulk rebuilds,
    /// or <see cref="ImmutableDictionary{TKey, TValue}.SetItem"/> for single-entry
    /// updates) and assign the property in one atomic reference update.
    /// </para></summary>
    public ImmutableDictionary<Target, List<(DateTimeOffset Time, double Alt)>> AltitudeProfiles { get; set; }
        = ImmutableDictionary<Target, List<(DateTimeOffset Time, double Alt)>>.Empty;

    /// <summary>Scored targets keyed by Target (for elevation profile access).
    /// Immutable (same rationale as <see cref="AltitudeProfiles"/>).</summary>
    public ImmutableDictionary<Target, ScoredTarget> ScoredTargets { get; set; }
        = ImmutableDictionary<Target, ScoredTarget>.Empty;

    /// <summary>Number of pinned (proposed) targets at the top of the filtered list.</summary>
    public int PinnedCount { get; set; }

    /// <summary>
    /// Handoff times between adjacent pinned targets (N-1 entries for N pinned targets).
    /// Slider[i] is the boundary between pinned target i and pinned target i+1.
    /// <para>
    /// <see cref="ImmutableArray{T}"/> with atomic replacement: <see cref="AppSignalHandler.InitializePlannerAsync"/>
    /// recomputes these on a background task while the render thread reads them, so the
    /// collection must never be observed mid-mutation (see CLAUDE.md shared-UI-state rule).
    /// </para>
    /// </summary>
    public ImmutableArray<DateTimeOffset> HandoffSliders { get; set; } = [];

    /// <summary>
    /// Conflict flags parallel to the sorted pinned targets list.
    /// True = this pinned target's peak window overlaps significantly with an adjacent target.
    /// </summary>
    public bool[] PinnedTargetConflicts { get; set; } = [];

    /// <summary>Index of the slider currently being dragged (-1 = none).</summary>
    public int DraggingSliderIndex { get; set; } = -1;

    /// <summary>Index of the slider currently selected for keyboard stepping (-1 = none).</summary>
    public int SelectedSliderIndex { get; set; } = -1;

    /// <summary>Original slider time when selection started, for Escape-to-revert.</summary>
    public DateTimeOffset? SelectedSliderOriginalTime { get; set; }

    /// <summary>Cross-index aliases for targets (e.g. "Also: NGC 224, UGC 454").
    /// Immutable (same rationale as <see cref="AltitudeProfiles"/>).</summary>
    public ImmutableDictionary<Target, string> TargetAliases { get; set; }
        = ImmutableDictionary<Target, string>.Empty;

    /// <summary>Minimum star rating filter (0 = show all, 3 = 3★+, 4 = 4★+, 5 = 5★ only).</summary>
    public float MinRatingFilter { get; set; } = 0f;

    /// <summary>Search input state for target name search.</summary>
    public TextInputState SearchInput { get; } = new() { Placeholder = "Search target..." };

    /// <summary>Manually searched/added targets (exempt from rating filter).
    /// Immutable — search adds build up a new <see cref="ImmutableArray{T}"/> via
    /// <see cref="ImmutableArray{T}.Add"/> and replace the property atomically.</summary>
    public ImmutableArray<ScoredTarget> SearchResults { get; set; } = [];

    /// <summary>
    /// The planner search-box interaction (autocomplete + target search / commit): a
    /// <see cref="PlannerSearchInteraction"/> over <see cref="SearchInput"/>. Owns the suggestion list
    /// (<c>Search.Results</c>), the highlighted index (<c>Search.SelectedIndex</c>), and the last-query
    /// dedup -- state that used to live directly on this type (Suggestions / SuggestionIndex /
    /// LastSuggestionQuery / CommitSuggestionAt). Set by the host (desktop <see cref="AppSignalHandler"/> /
    /// web Planner.razor); null in the standalone <c>plan</c> CLI, where the search box is inert -- the
    /// same nullable contract the old CommitSuggestionAt carried. The dropdown's mouse-click commit goes
    /// through <c>Search.CommitAt(index)</c>, identical to the keyboard Enter-on-highlighted path.
    /// </summary>
    public PlannerSearchInteraction? Search { get; set; }

    /// <summary>Maximum number of autocomplete suggestions to show.</summary>
    public const int MaxSuggestions = 8;

    // Single app-wide site timezone source. In the interactive app (GUI/TUI) this reads
    // and writes through to GuiAppState.SiteTimeZone (wired via AttachAppState) so the
    // planner, live session, sky map, and notifications share one value and cannot drift
    // apart. The standalone `plan` CLI command has no GuiAppState, so it falls back to a
    // local field. Used for display instead of machine-local time (never render UTC).
    private GuiAppState? _appState;
    private TimeSpan _siteTimeZoneFallback;

    /// <summary>Site timezone offset (from GeoTimeZone). Used for display instead of machine local time.</summary>
    public TimeSpan SiteTimeZone
    {
        get => _appState?.SiteTimeZone ?? _siteTimeZoneFallback;
        set
        {
            if (_appState is not null) _appState.SiteTimeZone = value;
            else _siteTimeZoneFallback = value;
        }
    }

    /// <summary>Attach the app-wide state so <see cref="SiteTimeZone"/> reads/writes the
    /// single shared <see cref="GuiAppState.SiteTimeZone"/>. Called once during app
    /// composition (the AppSignalHandler constructor).</summary>
    internal void AttachAppState(GuiAppState appState) => _appState = appState;

    /// <summary>
    /// The date to plan for. Null = tonight (uses current time).
    /// Changing this triggers recomputation of the night window and targets.
    /// </summary>
    public DateTimeOffset? PlanningDate { get; set; }

    /// <summary>Signal bus for posting planner events. Set by the host during initialization.</summary>
    public SignalBus? Bus { get; set; }

    /// <summary>Whether the planner session has unsaved changes (proposals, sliders, settings).</summary>
    public bool IsDirty
    {
        get => _isDirty;
        internal set
        {
            _isDirty = value;
            if (value)
            {
                Bus?.Post(new SavePlannerSessionSignal());
            }
        }
    }
    private bool _isDirty;

    /// <summary>
    /// Clears the dirty flag once the host has persisted the session. The flag's setter is
    /// internal (mutations go through <see cref="PlannerActions"/>), so out-of-assembly hosts —
    /// e.g. the browser host's localStorage store — call this from their
    /// <see cref="SavePlannerSessionSignal"/> subscriber, mirroring the desktop handler.
    /// </summary>
    public void MarkSessionSaved() => _isDirty = false;

    /// <summary>Whether the display needs a redraw.</summary>
    public bool NeedsRedraw { get; set; }

    /// <summary>Whether the target list needs recomputation (date changed, etc.).</summary>
    public bool NeedsRecompute { get; set; }

    /// <summary>Whether a recomputation is currently in progress.</summary>
    public bool IsRecomputing { get; set; }

    /// <summary>Cached precomputed astrometry grid for fast altitude profile computation.</summary>
    internal (Astrom[] Astroms, DateTimeOffset[] Times)? CachedAstromGrid { get; set; }

    /// <summary>Hourly weather forecast for the planning night, or null if no weather device is assigned.</summary>
    public IReadOnlyList<HourlyWeatherForecast>? WeatherForecast { get; set; }

    /// <summary>Moon altitude profile for the planning night.</summary>
    public List<(DateTimeOffset Time, double Alt)>? MoonAltitudeProfile { get; set; }

    /// <summary>Moon illumination fraction (0 = new, 1 = full) for the planning night.</summary>
    public double MoonIllumination { get; set; }

    /// <summary>Whether the moon is waxing (true) or waning (false).</summary>
    public bool MoonWaxing { get; set; }

    /// <summary>Moon phase emoji for the planning night (accounts for hemisphere).</summary>
    public string? MoonPhaseEmoji { get; set; }

    /// <summary>Status message to display.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Celestial object database reference, set after <see cref="AppSignalHandler.InitializePlannerAsync"/>.
    /// Used by the sky map tab for star positions and constellation data.
    /// </summary>
    public ICelestialObjectDB? ObjectDb { get; set; }

    /// <summary>
    /// The locally-cached JPL comet set, set alongside <see cref="ObjectDb"/> after
    /// <see cref="AppSignalHandler.InitializePlannerAsync"/> kicks off its (background) load. Used by the
    /// sky map tab to draw ephemeris-computed comet markers and their live magnitude, the same way the
    /// planet markers come from <see cref="VSOP87a"/>. Null until wired; empty until the load completes.
    /// </summary>
    public ICometRepository? Comets { get; set; }
}
