using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace TianWen.Hosting.Dto;

/// <summary>
/// The frame sources a node serves linear frames from (<c>GET /api/v1/frames/{source}/latest</c>, P4 of
/// docs/plans/hardware-in-the-server.md, #931): each OTA's last captured frame, the guide camera's, and a planetary
/// capture's live frame and rolling master (P5 part 5, #934).
/// </summary>
public static class FrameSources
{
    /// <summary>The guide camera's frames, at guiding cadence.</summary>
    public const string Guider = "guider";

    /// <summary>A planetary capture's live frame, as the camera gave it, at up to display rate.</summary>
    public const string PlanetaryLive = "planetary/live";

    /// <summary>A planetary capture's rolling master: the stack of its latest window, linear.</summary>
    public const string PlanetaryMaster = "planetary/master";

    /// <summary>A live view's frame (#1111), as the camera gave it, at up to display rate.</summary>
    public const string LiveView = "live";

    /// <summary>The frame OTA <paramref name="index"/> shows: a session's sub, focus rung or flat, or a preview the node took.</summary>
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

    /// <summary>The frame's number, the node's change token for its source: compare for difference, not order.</summary>
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
