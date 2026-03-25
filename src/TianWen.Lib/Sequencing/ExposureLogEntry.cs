using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Log entry for a single written frame, displayed in the live session exposure log.
/// </summary>
public readonly record struct ExposureLogEntry(
    DateTimeOffset Timestamp,
    string TargetName,
    string FilterName,
    TimeSpan Exposure,
    int FrameNumber,
    float MedianHfd,
    int StarCount);
