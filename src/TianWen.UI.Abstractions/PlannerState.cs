using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
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
    /// </summary>
    public List<DateTimeOffset> HandoffSliders { get; set; } = [];

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

    /// <summary>Current autocomplete suggestions for the search bar (max <see cref="MaxSuggestions"/>).</summary>
    public List<string> Suggestions { get; } = [];

    /// <summary>Index of the highlighted suggestion (-1 = none).</summary>
    public int SuggestionIndex { get; set; } = -1;

    /// <summary>The query that produced the current <see cref="Suggestions"/> list (avoids re-scanning on arrow keys).</summary>
    public string LastSuggestionQuery { get; set; } = "";

    /// <summary>Maximum number of autocomplete suggestions to show.</summary>
    public const int MaxSuggestions = 8;

    /// <summary>Site timezone offset (from GeoTimeZone). Used for display instead of machine local time.</summary>
    public TimeSpan SiteTimeZone { get; set; }

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
}
