namespace TianWen.Lib.Hosting.Dto;

/// <summary>
/// A target queued before session start. Converted to <see cref="TianWen.Lib.Sequencing.ScheduledObservation"/>
/// when the session begins.
/// </summary>
public sealed record PendingTarget(
    string Name,
    double RA,
    double Dec,
    double? DurationMinutes = null,
    double? SubExposureSeconds = null,
    int? Gain = null,
    int? Offset = null);
