using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Snapshot of a completed auto-focus run, stored for the live session tab's focus history panel.
/// </summary>
public readonly record struct FocusRunRecord(
    DateTimeOffset Timestamp,
    string OtaName,
    string FilterName,
    int BestPosition,
    float BestHfd,
    ImmutableArray<(int Position, float Hfd)> Curve);
