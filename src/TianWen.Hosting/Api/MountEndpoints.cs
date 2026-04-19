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
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var dto = MountStateDto.FromState(session.MountState);
            return Results.Json(
                ResponseEnvelope<MountStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeMountStateDto);
        });

        group.MapPost("/slew", async (double ra, double dec, IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Mount is not connected"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            await mount.BeginSlewRaDecAsync(ra, dec, ct);
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Slewing to RA={ra:F4}h Dec={dec:F4}°"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        group.MapPost("/park", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected || !mount.CanPark)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Mount is not connected or cannot park"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            await mount.ParkAsync(ct);
            return Results.Json(
                ResponseEnvelope<string>.Ok("Parking"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        group.MapPost("/unpark", async (IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected || !mount.CanUnpark)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Mount is not connected or cannot unpark"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            await mount.UnparkAsync(ct);
            return Results.Json(
                ResponseEnvelope<string>.Ok("Unparked"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        group.MapPost("/tracking", async (bool on, IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var mount = session.Setup.Mount.Driver;
            if (!mount.Connected || !mount.CanSetTracking)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Mount is not connected or cannot set tracking"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            await mount.SetTrackingAsync(on, ct);
            return Results.Json(
                ResponseEnvelope<string>.Ok(on ? "Tracking enabled" : "Tracking disabled"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }
}
