using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Observation(Target Target, DateTimeOffset Start, TimeSpan Duration, bool AcrossMeridian, TimeSpan SubExposure);
