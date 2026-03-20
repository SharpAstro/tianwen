using System;
using System.Collections.Generic;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Shared state for the observation planner, used by both TUI and GUI.
/// </summary>
public class PlannerState
{
    /// <summary>The active equipment profile.</summary>
    public Profile? ActiveProfile { get; set; }

    /// <summary>Tonight's best targets ranked by score.</summary>
    public IReadOnlyList<ScoredTarget> TonightsBest { get; set; } = [];

    /// <summary>User's proposed observations (selected from TonightsBest or manually added).</summary>
    public List<ProposedObservation> Proposals { get; set; } = [];

    /// <summary>The computed schedule from proposals. Null until scheduling is run.</summary>
    public ScheduledObservationTree? Schedule { get; set; }

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

    /// <summary>Site latitude in degrees.</summary>
    public double SiteLatitude { get; set; }

    /// <summary>Site longitude in degrees.</summary>
    public double SiteLongitude { get; set; }

    /// <summary>Minimum altitude above horizon in degrees.</summary>
    public byte MinHeightAboveHorizon { get; set; } = 20;

    /// <summary>Fine-grained altitude profiles per target (for chart rendering).</summary>
    public Dictionary<Target, List<(DateTimeOffset Time, double Alt)>> AltitudeProfiles { get; set; } = [];

    /// <summary>Scored targets keyed by Target (for elevation profile access).</summary>
    public Dictionary<Target, ScoredTarget> ScoredTargets { get; set; } = [];

    /// <summary>Minimum star rating filter (0 = show all, 3 = 3★+, 4 = 4★+, 5 = 5★ only).</summary>
    public float MinRatingFilter { get; set; } = 0f;

    /// <summary>Search input state for target name search.</summary>
    public TextInputState SearchInput { get; } = new() { Placeholder = "Search target..." };

    /// <summary>Manually searched/added targets (exempt from rating filter).</summary>
    public List<ScoredTarget> SearchResults { get; set; } = [];

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

    /// <summary>Whether the display needs a redraw.</summary>
    public bool NeedsRedraw { get; set; }

    /// <summary>Status message to display.</summary>
    public string? StatusMessage { get; set; }
}
