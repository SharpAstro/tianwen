using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;
// Disambiguate from Microsoft.AspNetCore.Http.ISession (ambient via the Web SDK).
using ISession = TianWen.Lib.Sequencing.ISession;

namespace TianWen.Hosting.Api;

internal static class OtaEndpoints
{
    public static RouteGroupBuilder MapOtaApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/ota");

        // List all OTAs
        group.MapGet("/", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var otas = new OtaInfoDto[session.Setup.Telescopes.Length];
            for (var i = 0; i < session.Setup.Telescopes.Length; i++)
            {
                otas[i] = OtaInfoDto.FromOta(i, session.Setup.Telescopes[i]);
            }

            return EnvelopeResults.Json(
                ResponseEnvelope<OtaInfoDto[]>.Ok(otas),
                HostingJsonContext.Default.ResponseEnvelopeOtaInfoDtoArray);
        });

        // Per-OTA camera state
        group.MapGet("/{index:int}/camera/info", (int index, IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (!TryGetOta(session, index, out _))
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail($"OTA index {index} out of range (0..{session.Setup.Telescopes.Length - 1})"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var metrics = index < session.LastFrameMetrics.Length ? session.LastFrameMetrics[index] : default;
            var displays = session.TelescopeDisplays;
            var display = !displays.IsDefaultOrEmpty && index < displays.Length ? displays[index] : default;
            var dto = OtaCameraStateDto.FromState(index, session.CameraStates[index], metrics, display);
            return EnvelopeResults.Json(
                ResponseEnvelope<OtaCameraStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeOtaCameraStateDto);
        });

        // The session-scoped routes from before the device plane, re-pointed at it (P2 part 4, #929): each resolves the
        // OTA's device (the running session's, else the active profile's) and asks DeviceOperations, so it works with no
        // session and follows the device plane's rules, the lease first. A move and a filter change answer their job.
        group.MapPost("/{index:int}/focuser/move", async (int index, int position, DeviceOperations devices, CancellationToken ct) =>
            await devices.RigOtaAsync(index, ct) is { Focuser: { } focuser }
                ? EnvelopeResults.Json(await devices.MoveFocuserAsync(new FocuserMoveRequestDto { DeviceUri = focuser, Position = position }, ct),
                    HostingJsonContext.Default.ResponseEnvelopeJobDto)
                : NoDevice(index, "focuser"));

        group.MapPost("/{index:int}/focuser/stop", async (int index, DeviceOperations devices, CancellationToken ct) =>
            await devices.RigOtaAsync(index, ct) is { Focuser: { } focuser }
                ? EnvelopeResults.Json(await devices.StopFocuserAsync(focuser, ct), HostingJsonContext.Default.ResponseEnvelopeString)
                : NoDevice(index, "focuser"));

        group.MapPost("/{index:int}/filterwheel/change", async (int index, int position, DeviceOperations devices, CancellationToken ct) =>
            await devices.RigOtaAsync(index, ct) is { FilterWheel: { } wheel }
                ? EnvelopeResults.Json(devices.ChangeFilter(new FilterChangeRequestDto { DeviceUri = wheel, Position = position }),
                    HostingJsonContext.Default.ResponseEnvelopeJobDto)
                : NoDevice(index, "filter wheel"));

        return group;
    }

    private static IResult NoDevice(int index, string kind) => EnvelopeResults.Json(
        ResponseEnvelope<string>.Fail($"No {kind} on OTA {index}: no session is running and the active profile names none there", 404),
        HostingJsonContext.Default.ResponseEnvelopeString);

    private static bool TryGetOta(ISession session, int index, [MaybeNullWhen(false)] out OTA ota)
    {
        if (index >= 0 && index < session.Setup.Telescopes.Length)
        {
            ota = session.Setup.Telescopes[index];
            return true;
        }
        ota = default;
        return false;
    }
}
