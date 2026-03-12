using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record ScheduledObservation(
    Target Target,
    DateTimeOffset Start,
    TimeSpan Duration,
    bool AcrossMeridian,
    TimeSpan SubExposure,
    int Gain,
    int Offset,
    ObservationPriority Priority = ObservationPriority.Normal
);
