using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A single guider error sample for RA/Dec, used to populate the live session guide graph.
/// </summary>
public readonly record struct GuideErrorSample(
    DateTimeOffset Timestamp,
    double RaError,
    double DecError);
