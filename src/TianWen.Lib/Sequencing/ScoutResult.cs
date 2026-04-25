using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Result of <see cref="Session.ScoutAndProbeAsync"/> — a predictive probe that runs
/// after centering and before guider/exposure commitment, to detect a fixed FOV
/// obstruction (tree, building, neighbour's roof) early.
/// </summary>
internal readonly record struct ScoutResult(
    FrameMetrics[] Metrics,
    ScoutClassification Classification,
    TimeSpan? EstimatedClearIn);

internal enum ScoutClassification
{
    /// <summary>Star count meets the healthy band; proceed to imaging.</summary>
    Healthy,

    /// <summary>Star count low and altitude nudge did not recover it — likely cloud/dew.</summary>
    Transparency,

    /// <summary>Star count low at target alt, recovered after altitude nudge — fixed obstruction.</summary>
    Obstruction,
}

/// <summary>
/// Routing decision returned by <see cref="Session.RunObstructionScoutAsync"/> to the
/// observation loop. Maps the scout classification + trajectory check onto a binary
/// "proceed with this target" vs. "advance to next observation" choice.
/// </summary>
internal enum ScoutOutcome
{
    /// <summary>Scout passed (or wait completed and re-scout passed) — start guider and image.</summary>
    Proceed,

    /// <summary>Obstruction won't clear in time, or transparency hand-off path; skip this target.</summary>
    Advance,
}
