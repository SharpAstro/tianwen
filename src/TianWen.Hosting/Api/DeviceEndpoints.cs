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
    /// <summary>The <see cref="JobDto.Kind"/> of a discovery.</summary>
    internal const string DiscoverJob = "discover";

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

            return EnvelopeResults.Json(
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

            return EnvelopeResults.Json(
                ResponseEnvelope<DeviceDto[]>.Ok(devices),
                HostingJsonContext.Default.ResponseEnvelopeDeviceDtoArray);
        });

        // Every connected or held device with what the node last read of it, whether it is connected and which run holds
        // it (P2 part 1, #929). Authoritative; DEVICE-STATE is the latency hint. Asking counts as watching, which keeps
        // the node reading at the GUI's cadences for a client that polls rather than listens.
        group.MapGet("/devices/state", (DeviceStatePoller poller) =>
            EnvelopeResults.Json(
                ResponseEnvelope<DeviceStateDto[]>.Ok(poller.Snapshot()),
                HostingJsonContext.Default.ResponseEnvelopeDeviceStateDtoArray));

        // The device plane's connect and disconnect (P2 part 2, #929), each a job the node finishes on its own token, one
        // job per device at a time. The rules and their order are the Equipment tab's: DeviceOperations.
        group.MapPost("/devices/connect", (DeviceRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.Connect(request.DeviceUri), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        // The read before a disconnect is offered: a camera's cooler and whether it is at work, and the run holding it.
        group.MapGet("/devices/disconnect-safety", async (string deviceUri, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.CheckDisconnectAsync(deviceUri, ct), HostingJsonContext.Default.ResponseEnvelopeDisconnectCheckDto));

        group.MapPost("/devices/disconnect", async (DisconnectRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.DisconnectAsync(request.DeviceUri, request.SkipWarmUp, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/warm-and-disconnect", (DeviceRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.WarmAndDisconnect(request.DeviceUri), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        // Starts a discovery, or joins the one running, and answers 202 with its job at once. It used to run
        // inline on the REQUEST's token: a client's 10 s control budget cut a serial sweep off mid-probe, and a
        // dropped request cancelled a probe half-way (P0b item 17 of docs/plans/hardware-in-the-server.md).
        // What it found is read from /devices/structured once the job has ended.
        group.MapPost("/devices/discover", (IDeviceDiscovery deviceDiscovery, NodeJobs jobs) =>
            EnvelopeResults.Json(
                ResponseEnvelope<JobDto>.Accepted(jobs.StartOrJoin(DiscoverJob, async (step, ct) =>
                {
                    step.Report("Discovering devices");
                    await deviceDiscovery.DiscoverAsync(ct);
                    var found = deviceDiscovery.RegisteredDeviceTypes
                        .Where(dt => dt is not DeviceType.Profile)
                        .Sum(dt => deviceDiscovery.RegisteredDevices(dt).Count());
                    return found == 1 ? "Found 1 device" : $"Found {found} devices";
                })),
                HostingJsonContext.Default.ResponseEnvelopeJobDto));

        return group;
    }
}
