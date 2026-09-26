using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace TianWen.Hosting.Dto;

/// <summary>
/// A profile the node wrote or deleted, which a <c>PROFILE-CHANGED</c> event carries (P3 part 1 of
/// docs/plans/hardware-in-the-server.md, #930): every write the node makes, whoever asked for it, the node's own
/// included. A latency hint, as every push is: a client whose copy is at another revision reads
/// <c>GET /api/v1/profiles/{id}</c>, which is authoritative.
/// </summary>
public sealed record ProfileChangedDto
{
    public required Guid ProfileId { get; init; }

    /// <summary>The profile's name now; null when it was deleted.</summary>
    public string? Name { get; init; }

    /// <summary>The revision the profile has now; null when it was deleted.</summary>
    public string? Revision { get; init; }

    public bool Deleted { get; init; }

    /// <summary>The key a <c>PROFILE-CHANGED</c> event carries the change under.</summary>
    public const string EventKey = "Profile";

    /// <summary>
    /// The change a <c>PROFILE-CHANGED</c> event carries (<see cref="Api.NodeWire.ProfileChangedEvent"/>): false for any
    /// other event, or one whose payload does not read as a profile change.
    /// </summary>
    public static bool TryFromEvent(WebSocketEventDto dto, [NotNullWhen(true)] out ProfileChangedDto? change)
    {
        change = dto.Event == Api.NodeWire.ProfileChangedEvent && dto.Data is { } data && data.TryGetValue(EventKey, out var value)
            ? value switch
            {
                ProfileChangedDto inProcess => inProcess,
                JsonElement { ValueKind: JsonValueKind.Object } element => element.Deserialize(HostingJsonContext.Default.ProfileChangedDto),
                _ => null,
            }
            : null;
        return change is not null;
    }
}
