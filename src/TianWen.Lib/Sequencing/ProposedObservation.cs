using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record ProposedObservation(
    Target Target,
    int? Gain = null,
    int? Offset = null,
    ObservationPriority Priority = ObservationPriority.Normal,
    TimeSpan? ObservationTime = null,
    TimeSpan? SubExposure = null
);
