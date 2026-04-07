using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Hosting.Dto;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting.Api;

internal static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessionApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/session");

        group.MapGet("/state", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var dto = SessionStateDto.FromSession(session);
            return Results.Json(
                ResponseEnvelope<SessionStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeSessionStateDto);
        });

        /// <summary>
        /// Starts a new session for the given profile. Creates the session via ISessionFactory
        /// and runs it in a background task. Returns immediately.
        /// </summary>
        group.MapPost("/start", (string profileId, IHostedSession hosted, ISessionFactory factory, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (!Guid.TryParse(profileId, out var guid))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"Invalid profile ID: {profileId}"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            Sequencing.ISession session;
            try
            {
                session = factory.Create(guid, new SessionConfiguration(), []);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail(ex.Message, 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (hosted is HostedSession hs)
            {
                hs.SetSession(session);
            }

            // Run in background — caller polls /state for progress
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Expected on abort
                }
            }, ct);

            return Results.Json(
                ResponseEnvelope<string>.Ok("Session started"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        group.MapPost("/abort", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (hosted is HostedSession hs)
            {
                // StopAsync will cancel the CTS and dispose the session
                _ = Task.Run(async () => await hs.StopAsync(CancellationToken.None));
            }

            return Results.Json(
                ResponseEnvelope<string>.Ok("Abort requested"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }
}
