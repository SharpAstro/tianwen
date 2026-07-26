using System.Collections.Generic;

namespace TianWen.Hosting.Dto;

/// <summary>
/// WebSocket push event payload. The <see cref="Event"/> field identifies the event type;
/// additional fields are event-specific and carried in <see cref="Data"/>.
/// </summary>
public sealed class WebSocketEventDto
{
    public required string Event { get; init; }
    public Dictionary<string, object?>? Data { get; init; }
}
