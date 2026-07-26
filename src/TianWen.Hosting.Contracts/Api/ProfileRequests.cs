using System;

namespace TianWen.Hosting.Api;

// Wire types for the profile endpoints. They live in TianWen.Hosting.Contracts (not next to the
// endpoints that use them) because a client has to construct/read them too, and because
// HostingJsonContext -- also in this assembly -- registers them.

/// <summary>Body of <c>POST /api/v1/profiles</c>.</summary>
public sealed class CreateProfileRequest
{
    public required string Name { get; init; }
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
