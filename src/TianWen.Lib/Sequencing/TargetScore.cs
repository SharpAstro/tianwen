using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public readonly record struct TargetScore(
    Target Target,
    double TotalScore,
    IReadOnlyDictionary<RaDecEventTime, RaDecEventInfo> ElevationProfile,
    DateTimeOffset OptimalStart,
    TimeSpan OptimalDuration
);
