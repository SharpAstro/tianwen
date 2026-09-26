using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace TianWen.Hosting.Dto;

/// <summary>
/// The frame sources a node serves linear frames from (<c>GET /api/v1/frames/{source}/latest</c>, P4 of
/// docs/plans/hardware-in-the-server.md, #931): each OTA's last captured frame, and the guide camera's.
/// </summary>
public static class FrameSources
{
    /// <summary>The guide camera's frames, at guiding cadence.</summary>
    public const string Guider = "guider";

    /// <summary>OTA <paramref name="index"/>'s last captured frame: a sub, a focus rung, a flat.</summary>
    public static string Ota(int index) => $"ota/{index}";
}

/// <summary>
/// A frame a node now has to serve, which a <c>FRAME-AVAILABLE</c> event carries (P4 of
/// docs/plans/hardware-in-the-server.md, #931). A hint, as every push is: <c>GET /api/v1/frames/{source}/latest</c> is
/// authoritative, and a client that holds a frame of another number asks it for this one.
/// </summary>
public sealed record FrameAvailableDto
{
    /// <summary>Where the frame is served from (<see cref="FrameSources"/>).</summary>
    public required string Source { get; init; }

    /// <summary>The frame's number, its change token: compare for difference, not order (a new run numbers from the start).</summary>
    public required int Number { get; init; }

    /// <summary>The key a <c>FRAME-AVAILABLE</c> event carries the frame under.</summary>
    public const string EventKey = "Frame";

    /// <summary>
    /// The frame a <c>FRAME-AVAILABLE</c> event carries (<see cref="Api.NodeWire.FrameAvailableEvent"/>): false for any other
    /// event, or one whose payload does not read as one.
    /// </summary>
    public static bool TryFromEvent(WebSocketEventDto dto, [NotNullWhen(true)] out FrameAvailableDto? frame)
    {
        frame = dto.Event == Api.NodeWire.FrameAvailableEvent && dto.Data is { } data && data.TryGetValue(EventKey, out var value)
            ? value switch
            {
                FrameAvailableDto inProcess => inProcess,
                JsonElement { ValueKind: JsonValueKind.Object } element => element.Deserialize(HostingJsonContext.Default.FrameAvailableDto),
                _ => null,
            }
            : null;
        return frame is not null;
    }
}
