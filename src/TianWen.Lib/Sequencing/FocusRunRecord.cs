using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Snapshot of a completed auto-focus run, stored for the live session tab's focus history panel.
/// </summary>
/// <param name="FitA">Hyperbola parameter A (minimum HFD at best focus).</param>
/// <param name="FitB">Hyperbola parameter B (curve width/shape).</param>
public readonly record struct FocusRunRecord(
    DateTimeOffset Timestamp,
    string OtaName,
    string FilterName,
    int BestPosition,
    float BestHfd,
    ImmutableArray<(int Position, float Hfd)> Curve,
    double FitA = double.NaN,
    double FitB = double.NaN);
