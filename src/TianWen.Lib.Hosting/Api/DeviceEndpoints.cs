using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Lib.Hosting.Dto;

namespace TianWen.Lib.Hosting.Api;

internal static class DeviceEndpoints
{
    public static RouteGroupBuilder MapDeviceApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1");

        // List discovered devices (excludes profiles)
        group.MapGet("/devices", (ICombinedDeviceManager deviceManager) =>
        {
            var devices = deviceManager.RegisteredDeviceTypes
                .Where(dt => dt is not DeviceType.Profile)
                .SelectMany(dt => deviceManager.RegisteredDevices(dt))
                .Select(d => $"{d.DeviceType}: {d.DisplayName} ({d.DeviceId})")
                .ToArray();

            return Results.Json(
                ResponseEnvelope<string[]>.Ok(devices),
                HostingJsonContext.Default.ResponseEnvelopeStringArray);
        });

        // Trigger device discovery
        group.MapGet("/devices/discover", async (ICombinedDeviceManager deviceManager, CancellationToken ct) =>
        {
            await deviceManager.DiscoverAsync(ct);

            var devices = deviceManager.RegisteredDeviceTypes
                .Where(dt => dt is not DeviceType.Profile)
                .SelectMany(dt => deviceManager.RegisteredDevices(dt))
                .Select(d => $"{d.DeviceType}: {d.DisplayName} ({d.DeviceId})")
                .ToArray();

            return Results.Json(
                ResponseEnvelope<string[]>.Ok(devices),
                HostingJsonContext.Default.ResponseEnvelopeStringArray);
        });

        // List profiles
        group.MapGet("/profiles", (ICombinedDeviceManager deviceManager) =>
        {
            var profiles = deviceManager.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .Select(p => $"{p.DisplayName} ({p.ProfileId:D})")
                .ToArray();

            return Results.Json(
                ResponseEnvelope<string[]>.Ok(profiles),
                HostingJsonContext.Default.ResponseEnvelopeStringArray);
        });

        return group;
    }
}
