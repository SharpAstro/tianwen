using System;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Api;

// Wire types for the profile endpoints. They live in TianWen.Hosting.Contracts (not next to the
// endpoints that use them) because a client has to construct/read them too, and because
// HostingJsonContext -- also in this assembly -- registers them.

/// <summary>Body of <c>POST /api/v1/profiles</c>.</summary>
public sealed class CreateProfileRequest
{
    public required string Name { get; init; }
}

/// <summary>
/// Body of <c>PUT /api/v1/profiles/{id}</c>: the WHOLE profile, and the revision it was read at (P3 part 1 of
/// docs/plans/hardware-in-the-server.md, #930). Only over the node's socket: a LAN client reads profiles and never
/// changes them (decision 4).
/// </summary>
public sealed class UpdateProfileRequest
{
    /// <summary>The profile's new name; null keeps the name it has.</summary>
    public string? Name { get; init; }

    /// <summary>The whole profile, as <see cref="Dto.ProfileDetailDto.Data"/> reads it, changed.</summary>
    public required ProfileData Data { get; init; }

    /// <summary>
    /// The revision the change was made against (<see cref="Dto.ProfileDetailDto.Revision"/>). The node refuses the edit
    /// with a 412 when the stored profile has moved on since, so a change made elsewhere is never silently lost.
    /// </summary>
    public required string Revision { get; init; }
}

/// <summary>An entry of <c>GET /api/v1/profiles</c>.</summary>
public sealed class ProfileSummaryDto
{
    public required Guid ProfileId { get; init; }
    public required string Name { get; init; }
}

/// <summary>Body of <c>PUT /api/v1/session/profile</c>.</summary>
public sealed class SetProfileRequest
{
    public required Guid ProfileId { get; init; }
}
