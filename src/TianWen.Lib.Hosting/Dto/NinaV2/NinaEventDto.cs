using System;

namespace TianWen.Lib.Hosting.Dto.NinaV2;

/// <summary>
/// Event history entry matching ninaAPI v2 <c>/v2/api/event-history</c> response shape.
/// </summary>
public sealed class NinaEventDto
{
    public required string Time { get; init; }
    public required string Event { get; init; }
}
