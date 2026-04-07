using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Lib.Hosting.Dto;

namespace TianWen.Lib.Hosting.Api;

internal static class ProfileEndpoints
{
    public static RouteGroupBuilder MapProfileApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/profiles");

        // GET /api/v1/profiles — list all profiles
        group.MapGet("/", (ICombinedDeviceManager deviceManager) =>
        {
            var profiles = deviceManager.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .Select(p => new ProfileSummaryDto { ProfileId = p.ProfileId, Name = p.DisplayName })
                .ToArray();

            return Results.Json(
                ResponseEnvelope<ProfileSummaryDto[]>.Ok(profiles),
                HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDtoArray);
        });

        // GET /api/v1/profiles/{id} — get profile detail
        group.MapGet("/{id:guid}", (Guid id, ICombinedDeviceManager deviceManager) =>
        {
            var profile = deviceManager.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .FirstOrDefault(p => p.ProfileId == id);

            if (profile is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.NotFound($"Profile {id} not found"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            return Results.Json(
                ResponseEnvelope<ProfileDetailDto>.Ok(ProfileDetailDto.FromProfile(profile)),
                HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto);
        });

        // POST /api/v1/profiles — create a new profile
        group.MapPost("/", async (CreateProfileRequest request, IExternal external, ICombinedDeviceManager deviceManager, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Profile name is required"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var profile = new Profile(Guid.NewGuid(), request.Name, ProfileData.Empty);
            await profile.SaveAsync(external, ct);

            // Refresh device registry so the new profile is discoverable
            await deviceManager.DiscoverOnlyDeviceType(DeviceType.Profile, ct);

            return Results.Json(
                ResponseEnvelope<ProfileDetailDto>.Ok(ProfileDetailDto.FromProfile(profile)),
                HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto);
        });

        // DELETE /api/v1/profiles/{id} — delete a profile
        group.MapDelete("/{id:guid}", async (Guid id, IExternal external, ICombinedDeviceManager deviceManager, CancellationToken ct) =>
        {
            var profile = deviceManager.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .FirstOrDefault(p => p.ProfileId == id);

            if (profile is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.NotFound($"Profile {id} not found"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            profile.Delete(external);

            // Refresh device registry
            await deviceManager.DiscoverOnlyDeviceType(DeviceType.Profile, ct);

            return Results.Json(
                ResponseEnvelope<string>.Ok($"Profile {id} deleted"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }
}

public sealed class CreateProfileRequest
{
    public required string Name { get; init; }
}

public sealed class ProfileSummaryDto
{
    public required Guid ProfileId { get; init; }
    public required string Name { get; init; }
}
