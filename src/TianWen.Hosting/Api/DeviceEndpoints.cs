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

        // A camera's cooling and settings (P2 part 3, #929): cooling and warming are ramps, so jobs; the cooler off and the
        // settings are immediate, and refused while a job is working on the camera.
        group.MapPost("/devices/camera/cool", (CoolRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.Cool(request), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/camera/warm", (DeviceRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.Warm(request.DeviceUri), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/camera/cooler-off", async (DeviceRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.CoolerOffAsync(request.DeviceUri, ct), HostingJsonContext.Default.ResponseEnvelopeString));

        group.MapPost("/devices/camera/settings", async (CameraSettingsRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.ApplySettingsAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeCameraSettingsDto));

        // Moving a focuser, a filter wheel or a mount (P2 part 4, #929): a move is a job that ends when the device has
        // settled; a stop ends the job it stops; tracking is immediate.
        group.MapPost("/devices/focuser/move", async (FocuserMoveRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.MoveFocuserAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/focuser/stop", async (DeviceRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.StopFocuserAsync(request.DeviceUri, ct), HostingJsonContext.Default.ResponseEnvelopeString));

        group.MapPost("/devices/filterwheel/change", (FilterChangeRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.ChangeFilter(request), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/mount/goto", async (MountGotoRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.GotoAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/mount/park", (DeviceRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.Park(request.DeviceUri), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/mount/unpark", (DeviceRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.Unpark(request.DeviceUri), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/mount/tracking", async (MountTrackingRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.SetTrackingAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeString));

        // One guide-rate pulse, as a job (P6 part 1): the planetary panel's coarse recentre.
        group.MapPost("/devices/mount/nudge", (MountNudgeRequestDto request, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.NudgeMount(request), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        // Leased (P2 part 5): the axis stops by itself NodeWire.MoveAxisLease after the last request asking for it, so a
        // client holding a move repeats this request while it holds.
        group.MapPost("/devices/mount/move-axis", async (MoveAxisRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.MoveAxisAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        group.MapPost("/devices/mount/stop", async (DeviceRequestDto request, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(await devices.StopMountAsync(request.DeviceUri, ct), HostingJsonContext.Default.ResponseEnvelopeString));

        // Starts a discovery, or joins the one running, and answers 202 with its job at once. It used to run
        // inline on the REQUEST's token: a client's 10 s control budget cut a serial sweep off mid-probe, and a
        // dropped request cancelled a probe half-way (P0b item 17 of docs/plans/hardware-in-the-server.md).
        // What it found is read from /devices/structured once the job has ended.
        // A device setting, the Equipment tab's text field (P3 part 4, #930): a masked one (an API key) into the credential
        // store, any other onto the device's URI in the named profile. Only over the socket, since a secret must not cross
        // the LAN, and a LAN client changes no profile (decision 4). The secret never comes back; a client asks whether one
        // is set.
        group.MapPut("/devices/setting", async (DeviceSettingRequestDto request, HttpContext context, DeviceOperations devices, CancellationToken ct) =>
            EnvelopeResults.Json(
                NodeEndpoints.CameOverTheSocket(context)
                    ? await devices.CommitSettingAsync(request, ct)
                    : ResponseEnvelope<DeviceSettingDto>.Fail("Only a client on this machine's node socket may change a device's setting", 403),
                HostingJsonContext.Default.ResponseEnvelopeDeviceSettingDto));

        group.MapGet("/devices/setting/secret", (string deviceUri, string key, DeviceOperations devices) =>
            EnvelopeResults.Json(devices.SecretIsSet(deviceUri, key), HostingJsonContext.Default.ResponseEnvelopeDeviceSecretDto));

        // Every stored profile is reconciled with what it found at the end (P3 part 2, #930), as the GUI's discovery does.
        group.MapPost("/devices/discover", (IDeviceDiscovery deviceDiscovery, NodeProfiles profiles, NodeJobs jobs) =>
            EnvelopeResults.Json(
                ResponseEnvelope<JobDto>.Accepted(jobs.StartOrJoin(DiscoverJob, async (step, ct) =>
                {
                    step.Report("Discovering devices");
                    await deviceDiscovery.DiscoverAsync(ct);
                    var found = deviceDiscovery.RegisteredDeviceTypes
                        .Where(dt => dt is not DeviceType.Profile)
                        .Sum(dt => deviceDiscovery.RegisteredDevices(dt).Count());
                    step.Report("Reconciling the profiles with what was found");
                    var reconciled = await profiles.ReconcileAllAsync(ct);
                    var devices = found == 1 ? "Found 1 device" : $"Found {found} devices";
                    return reconciled switch
                    {
                        0 => devices,
                        1 => $"{devices}; reconciled 1 profile",
                        _ => $"{devices}; reconciled {reconciled} profiles",
                    };
                })),
                HostingJsonContext.Default.ResponseEnvelopeJobDto));

        return group;
    }
}
