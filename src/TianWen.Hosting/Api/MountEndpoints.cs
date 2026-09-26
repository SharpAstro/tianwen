using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

internal static class MountEndpoints
{
    public static RouteGroupBuilder MapMountApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/mount");

        group.MapGet("/info", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var dto = MountStateDto.FromState(session.MountState);
            return EnvelopeResults.Json(
                ResponseEnvelope<MountStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeMountStateDto);
        });

        // The session-scoped routes from before the device plane, re-pointed at it (P2 part 4, #929): each resolves the
        // mount it means (the running session's, else the active profile's) and asks DeviceOperations, so it works with no
        // session and follows the device plane's rules, the lease first. The device plane's own routes take a device URI.
        // /slew takes J2000 coordinates, through the one goto every host uses (MountGoto), and answers the job.
        group.MapPost("/slew", async (double ra, double dec, DeviceOperations devices, CancellationToken ct) =>
            await devices.RigMountAsync(ct) is { } mount
                ? EnvelopeResults.Json(await devices.GotoAsync(new MountGotoRequestDto { DeviceUri = mount, RaJ2000 = ra, DecJ2000 = dec }, ct),
                    HostingJsonContext.Default.ResponseEnvelopeJobDto)
                : NoMount());

        group.MapPost("/park", async (DeviceOperations devices, CancellationToken ct) =>
            await devices.RigMountAsync(ct) is { } mount
                ? EnvelopeResults.Json(devices.Park(mount), HostingJsonContext.Default.ResponseEnvelopeJobDto)
                : NoMount());

        group.MapPost("/unpark", async (DeviceOperations devices, CancellationToken ct) =>
            await devices.RigMountAsync(ct) is { } mount
                ? EnvelopeResults.Json(devices.Unpark(mount), HostingJsonContext.Default.ResponseEnvelopeJobDto)
                : NoMount());

        group.MapPost("/tracking", async (bool on, DeviceOperations devices, CancellationToken ct) =>
            await devices.RigMountAsync(ct) is { } mount
                ? EnvelopeResults.Json(await devices.SetTrackingAsync(new MountTrackingRequestDto { DeviceUri = mount, On = on }, ct),
                    HostingJsonContext.Default.ResponseEnvelopeString)
                : NoMount());

        return group;
    }

    private static IResult NoMount() => EnvelopeResults.Json(
        ResponseEnvelope<string>.Fail("No mount: no session is running and the active profile names none", 404),
        HostingJsonContext.Default.ResponseEnvelopeString);
}
