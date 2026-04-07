using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Lib.Hosting.Dto;
using TianWen.Lib.Hosting.Dto.NinaV2;

namespace TianWen.Lib.Hosting.Api.NinaV2;

/// <summary>
/// ninaAPI v2 system endpoints: version, time, event-history, profile.
/// </summary>
internal static class NinaSystemEndpoints
{
    private static readonly string _version = typeof(NinaSystemEndpoints).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    public static RouteGroupBuilder MapNinaSystemApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v2/api");

        // GET /v2/api/version — connectivity check, returns version string
        group.MapGet("/version", () => Results.Json(
            ResponseEnvelope<string>.Ok(_version),
            NinaApiJsonContext.Default.ResponseEnvelopeString));

        // GET /v2/api/time — current server time in ISO 8601
        group.MapGet("/time", () => Results.Json(
            ResponseEnvelope<string>.Ok(DateTimeOffset.UtcNow.ToString("o")),
            NinaApiJsonContext.Default.ResponseEnvelopeString));

        // GET /v2/api/event-history — synthesized from session phase changes
        group.MapGet("/event-history", (IHostedSession hosted) =>
        {
            var events = new List<NinaEventDto>();

            if (hosted.CurrentSession is { } session)
            {
                // Report device connections as events
                if (session.Setup.Mount.Driver.Connected)
                {
                    events.Add(new NinaEventDto { Time = DateTimeOffset.UtcNow.ToString("o"), Event = "MOUNT-CONNECTED" });
                }
                if (session.Setup.Telescopes.Length > 0 && session.Setup.Telescopes[0].Camera.Driver.Connected)
                {
                    events.Add(new NinaEventDto { Time = DateTimeOffset.UtcNow.ToString("o"), Event = "CAMERA-CONNECTED" });
                }
                if (session.Setup.Guider.Driver.Connected)
                {
                    events.Add(new NinaEventDto { Time = DateTimeOffset.UtcNow.ToString("o"), Event = "GUIDER-CONNECTED" });
                }
                if (session.Setup.Telescopes.Length > 0 && session.Setup.Telescopes[0].Focuser?.Driver.Connected == true)
                {
                    events.Add(new NinaEventDto { Time = DateTimeOffset.UtcNow.ToString("o"), Event = "FOCUSER-CONNECTED" });
                }
                if (session.Setup.Telescopes.Length > 0 && session.Setup.Telescopes[0].FilterWheel?.Driver.Connected == true)
                {
                    events.Add(new NinaEventDto { Time = DateTimeOffset.UtcNow.ToString("o"), Event = "FILTERWHEEL-CONNECTED" });
                }
            }

            return Results.Json(
                ResponseEnvelope<NinaEventDto[]>.Ok(events.ToArray()),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaEventDtoArray);
        });

        // GET /v2/api/profile/show — active profile (TNS passes ?active=true)
        group.MapGet("/profile/show", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            var dto = await NinaProfileDto.FromSessionAsync(session, ct);
            return Results.Json(
                ResponseEnvelope<NinaProfileDto>.Ok(dto),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaProfileDto);
        });

        // GET /v2/api/profile/switch?profileid= — set active profile
        group.MapGet("/profile/switch", (string profileid, IHostedSession hosted) =>
        {
            if (!Guid.TryParse(profileid, out var guid))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"Invalid profile ID: {profileid}"),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            hosted.SetActiveProfile(guid);
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Switched to profile {guid}"),
                NinaApiJsonContext.Default.ResponseEnvelopeString);
        });

        // GET /v2/api/profile/list-available — list profiles (alias for /v2/api/profile/show without ?active)
        group.MapGet("/profile/list-available", (ICombinedDeviceManager deviceManager) =>
        {
            var profiles = deviceManager.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .Select(p => new ProfileSummaryDto { ProfileId = p.ProfileId, Name = p.DisplayName })
                .ToArray();

            return Results.Json(
                ResponseEnvelope<ProfileSummaryDto[]>.Ok(profiles),
                NinaApiJsonContext.Default.ResponseEnvelopeProfileSummaryDtoArray);
        });

        return group;
    }
}
