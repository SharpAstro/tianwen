using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Records when a session phase started. Used to render the session timeline/Gantt graph.
/// </summary>
public readonly record struct PhaseTimestamp(SessionPhase Phase, DateTimeOffset StartTime);
