using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record ProposedObservation(
    Target Target,
    ObjectType ObjectType = ObjectType.Unknown,
    int? Gain = null,
    int? Offset = null,
    ObservationPriority Priority = ObservationPriority.Normal,
    TimeSpan? ObservationTime = null,
    TimeSpan? SubExposure = null,
    ImmutableArray<FilterExposure>? FilterPlan = null,
    Guid? MosaicGroupId = null
);
