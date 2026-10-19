using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Api;

/// <summary>
/// The user's key for the blind solve of last resort (<see cref="BlindSolveService"/>, nova.astrometry.net): set or removed over
/// the node socket only, as a device's secret is, since a secret must not cross the LAN; asked whether one is set by any client
/// that may read, never answered with the key. The node's own solves read it from the same credential store.
/// </summary>
internal static class BlindSolveEndpoints
{
    public static void MapBlindSolveApi(this IEndpointRouteBuilder group)
    {
        group.MapPut("/api/v1/platesolve/blind/key", (BlindSolveKeyRequestDto request, HttpContext context, ICredentialStore credentials) =>
        {
            if (!NodeEndpoints.CameOverTheSocket(context))
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<DeviceSecretDto>.Fail($"Only a client on this machine's node socket may set the {BlindSolveService.Name} key", 403),
                    HostingJsonContext.Default.ResponseEnvelopeDeviceSecretDto);
            }
            credentials.Set(BlindSolveService.CredentialKey, request.Key is { Length: > 0 } key ? key.Trim() : null);
            return EnvelopeResults.Json(KeyState(credentials), HostingJsonContext.Default.ResponseEnvelopeDeviceSecretDto);
        });

        group.MapGet("/api/v1/platesolve/blind/key", (ICredentialStore credentials) =>
            EnvelopeResults.Json(KeyState(credentials), HostingJsonContext.Default.ResponseEnvelopeDeviceSecretDto));
    }

    private static ResponseEnvelope<DeviceSecretDto> KeyState(ICredentialStore credentials)
        => ResponseEnvelope<DeviceSecretDto>.Ok(new DeviceSecretDto { IsSet = credentials.Get(BlindSolveService.CredentialKey) is { Length: > 0 } });
}
