using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

internal static class ProfileEndpoints
{
    public static RouteGroupBuilder MapProfileApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/profiles");

        // GET /api/v1/profiles: list all profiles
        group.MapGet("/", (IDeviceDiscovery deviceDiscovery) =>
        {
            var profiles = deviceDiscovery.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .Select(p => new ProfileSummaryDto { ProfileId = p.ProfileId, Name = p.DisplayName })
                .ToArray();

            return EnvelopeResults.Json(
                ResponseEnvelope<ProfileSummaryDto[]>.Ok(profiles),
                HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDtoArray);
        });

        // GET /api/v1/profiles/{id}: the WHOLE profile as its file holds it now, with its revision (P3 part 1 of
        // docs/plans/hardware-in-the-server.md, #930). Read from the file rather than the registry, which another
        // process's write can leave behind, and open to a LAN client too: decision 4 lets one read a profile.
        group.MapGet("/{id:guid}", async (Guid id, NodeProfiles profiles, CancellationToken ct) =>
            await profiles.ReadAsync(id, ct) is { } stored
                ? EnvelopeResults.Json(
                    ResponseEnvelope<ProfileDetailDto>.Ok(ProfileDetailDto.FromStored(stored)),
                    HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto)
                : EnvelopeResults.Json(
                    ResponseEnvelope<string>.NotFound($"Profile {id} not found"),
                    HostingJsonContext.Default.ResponseEnvelopeString));

        // PUT /api/v1/profiles/{id}: replaces the WHOLE profile, made against the revision the client read. Only over
        // the socket (decision 4), and through the node's one writer, which refuses a stale revision (412) rather than
        // overwrite a change the client never saw.
        group.MapPut("/{id:guid}", async (Guid id, UpdateProfileRequest request, HttpContext context, NodeProfiles profiles, DeviceOperations devices,
            CancellationToken ct) =>
        {
            if (!NodeEndpoints.CameOverTheSocket(context))
            {
                return OnlyOverTheSocket("change a profile");
            }
            if (Problem(request) is { } problem)
            {
                return EnvelopeResults.Json(ResponseEnvelope<string>.Fail(problem), HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var write = await profiles.UpdateAsync(id, request.Revision, current => new Profile(id, request.Name ?? current.DisplayName, request.Data), ct);
            await devices.AfterProfileEditAsync(id, write, ct);
            return write switch
            {
                { Outcome: ProfileWriteOutcome.NotFound } => EnvelopeResults.Json(
                    ResponseEnvelope<string>.NotFound($"Profile {id} not found"),
                    HostingJsonContext.Default.ResponseEnvelopeString),
                { Outcome: ProfileWriteOutcome.Stale, Stored: { } stored } => EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail(
                        $"Profile {id} has changed since revision {request.Revision} (it is at {stored.Revision}); read it again and reapply the edit", 412),
                    HostingJsonContext.Default.ResponseEnvelopeString),
                { Stored: { } stored } => EnvelopeResults.Json(
                    ResponseEnvelope<ProfileDetailDto>.Ok(ProfileDetailDto.FromStored(stored)),
                    HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto),
                _ => throw new InvalidOperationException($"A {write.Outcome} write of profile {id} stored nothing"),
            };
        });

        // POST /api/v1/profiles: creates an empty profile. Only over the socket (decision 4).
        group.MapPost("/", async (CreateProfileRequest request, HttpContext context, NodeProfiles profiles, CancellationToken ct) =>
        {
            if (!NodeEndpoints.CameOverTheSocket(context))
            {
                return OnlyOverTheSocket("create a profile");
            }
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail("Profile name is required"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            return EnvelopeResults.Json(
                ResponseEnvelope<ProfileDetailDto>.Ok(ProfileDetailDto.FromStored(await profiles.CreateAsync(request.Name, ct))),
                HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto);
        });

        // DELETE /api/v1/profiles/{id}: delete a profile
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context, IHostedSession hosted, NodeProfiles profiles, CancellationToken ct) =>
        {
            if (!NodeEndpoints.CameOverTheSocket(context))
            {
                return OnlyOverTheSocket("delete a profile");
            }

            // A profile in use is not deleted from under its user (P0b item 18 of
            // docs/plans/hardware-in-the-server.md, #752): the node's active profile. That is also the profile any run
            // going on was started from, which the run writes back into as it ends (the backlash mirror): a start makes
            // its profile the active one, and ProfileSwitchGate keeps it active until the run ends. Any other profile
            // may go while a run is going; until P3 part 3 every one was refused, for want of knowing which was in use.
            if (hosted.ActiveProfileId == id)
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail($"Profile {id} is the node's active profile; make another one active first", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            return await profiles.DeleteAsync(id, ct)
                ? EnvelopeResults.Json(
                    ResponseEnvelope<string>.Ok($"Profile {id} deleted"),
                    HostingJsonContext.Default.ResponseEnvelopeString)
                : EnvelopeResults.Json(
                    ResponseEnvelope<string>.NotFound($"Profile {id} not found"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }

    /// <summary>
    /// A client on the LAN reads profiles and never changes them (decision 4 of docs/plans/hardware-in-the-server.md):
    /// only one on this machine's node socket may.
    /// </summary>
    private static IResult OnlyOverTheSocket(string what) =>
        EnvelopeResults.Json(
            ResponseEnvelope<string>.Fail($"Only a client on this machine's node socket may {what}", 403),
            HostingJsonContext.Default.ResponseEnvelopeString);

    /// <summary>
    /// What makes a whole profile from the wire unusable, or null when nothing does: a field the JSON left out arrives
    /// as null (or, for the telescopes, as no array at all) rather than being refused by the reader.
    /// </summary>
    private static string? Problem(UpdateProfileRequest request)
    {
        if (request.Name is { } name && string.IsNullOrWhiteSpace(name))
        {
            return "A profile's name cannot be blank";
        }
        if (string.IsNullOrEmpty(request.Revision))
        {
            return "Name the revision the edit was made against (read the profile first)";
        }

        var data = request.Data;
        if (data.Mount is null || data.Guider is null)
        {
            return "A profile names a mount and a guider (the none device for neither)";
        }
        if (data.OTAs.IsDefault)
        {
            return "A profile lists its telescopes (an empty list for none)";
        }
        for (var i = 0; i < data.OTAs.Length; i++)
        {
            if (data.OTAs[i] is not { Name: not null, Camera: not null })
            {
                return $"Telescope {i + 1} has no name or no camera (the none device for none)";
            }
        }
        return null;
    }
}
