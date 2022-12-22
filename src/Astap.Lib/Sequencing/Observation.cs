using System;

namespace Astap.Lib.Sequencing;

public record Observation(Target Target, DateTimeOffset Start, TimeSpan Duration, bool AcrossMeridian);
