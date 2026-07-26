using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

internal static class DeviceEndpoints
{
    public static RouteGroupBuilder MapDeviceApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1");

        // List discovered devices (excludes profiles)
        group.MapGet("/devices", (IDeviceDiscovery deviceDiscovery) =>
        {
            var devices = deviceDiscovery.RegisteredDeviceTypes
                .Where(dt => dt is not DeviceType.Profile)
                .SelectMany(dt => deviceDiscovery.RegisteredDevices(dt))
                .Select(d => $"{d.DeviceType}: {d.DisplayName} ({d.DeviceId})")
                .ToArray();

            return Results.Json(
                ResponseEnvelope<string[]>.Ok(devices),
                HostingJsonContext.Default.ResponseEnvelopeStringArray);
        });

        // Structured device list: URI + type + live connection state (see DeviceDto for why the
        // string endpoint above is not sufficient for a client).
        group.MapGet("/devices/structured", (IDeviceDiscovery deviceDiscovery, IDeviceHub hub) =>
        {
            var devices = deviceDiscovery.RegisteredDeviceTypes
                .Where(dt => dt is not DeviceType.Profile)
                .SelectMany(dt => deviceDiscovery.RegisteredDevices(dt))
                .Select(d => DeviceDto.FromDevice(d, hub.IsConnected(d.DeviceUri)))
                .ToArray();

            return Results.Json(
                ResponseEnvelope<DeviceDto[]>.Ok(devices),
                HostingJsonContext.Default.ResponseEnvelopeDeviceDtoArray);
        });

        // Trigger device discovery
        group.MapGet("/devices/discover", async (IDeviceDiscovery deviceDiscovery, CancellationToken ct) =>
        {
            await deviceDiscovery.DiscoverAsync(ct);

            var devices = deviceDiscovery.RegisteredDeviceTypes
                .Where(dt => dt is not DeviceType.Profile)
                .SelectMany(dt => deviceDiscovery.RegisteredDevices(dt))
                .Select(d => $"{d.DeviceType}: {d.DisplayName} ({d.DeviceId})")
                .ToArray();

            return Results.Json(
                ResponseEnvelope<string[]>.Ok(devices),
                HostingJsonContext.Default.ResponseEnvelopeStringArray);
        });

        return group;
    }
}
