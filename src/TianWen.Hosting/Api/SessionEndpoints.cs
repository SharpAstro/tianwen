using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;
// Disambiguate from Microsoft.AspNetCore.Http.ISession (ambient via the Web SDK).
using ISession = TianWen.Lib.Sequencing.ISession;

namespace TianWen.Hosting.Api;

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
        /// Consumes pending targets from IHostedSession.
        /// Accepts optional JSON body with SessionConfigApiDto.
        /// </summary>
        group.MapPost("/start", async (HttpContext httpContext, IHostedSession hosted, ISessionFactory factory, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // Profile ID from query string or active profile
            var profileIdStr = httpContext.Request.Query["profileId"].FirstOrDefault();
            Guid? profileId = null;
            if (profileIdStr is not null)
            {
                if (!Guid.TryParse(profileIdStr, out var parsed))
                {
                    return Results.Json(
                        ResponseEnvelope<string>.Fail($"Invalid profile ID '{profileIdStr}'"),
                        HostingJsonContext.Default.ResponseEnvelopeString);
                }
                profileId = parsed;
            }
            profileId ??= hosted.ActiveProfileId;

            if (profileId is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No profile ID specified. Set via ?profileId= or /api/v1/session/profile"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // Try to read optional config from body
            SessionConfiguration config = new SessionConfiguration();
            if (httpContext.Request.ContentLength > 0)
            {
                try
                {
                    var configDto = await httpContext.Request.ReadFromJsonAsync(HostingJsonContext.Default.SessionConfigApiDto, ct);
                    if (configDto is not null)
                    {
                        config = configDto.ToConfiguration();
                    }
                }
                catch
                {
                    // Body parsing failed — use defaults
                }
            }

            // Drain pending targets
            var pendingTargets = hosted is HostedSession hs ? hs.DrainTargets() : [];
            var observations = pendingTargets.Select(t => new ScheduledObservation(
                new Target(t.RA, t.Dec, t.Name, null),
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(t.DurationMinutes ?? 30),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(
                    TimeSpan.FromSeconds(t.SubExposureSeconds ?? 120)),
                Gain: t.Gain.HasValue ? (int?)t.Gain.Value : null,
                Offset: t.Offset.HasValue ? (int?)t.Offset.Value : null
            )).ToArray();

            ISession session;
            try
            {
                session = factory.Create(profileId.Value, config, observations);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail(ex.Message, 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (hosted is HostedSession hostedSession)
            {
                hostedSession.SetSession(session);
                hostedSession.SetActiveProfile(profileId.Value);
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

        // --- Target management (pre-session) ---

        // GET /api/v1/session/targets — list pending targets
        group.MapGet("/targets", (IHostedSession hosted) =>
        {
            return Results.Json(
                ResponseEnvelope<object>.Ok(hosted.PendingTargets),
                HostingJsonContext.Default.ResponseEnvelopeObject);
        });

        // POST /api/v1/session/targets — add a target
        group.MapPost("/targets", (PendingTarget target, IHostedSession hosted) =>
        {
            if (string.IsNullOrWhiteSpace(target.Name))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Target name is required"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            hosted.AddTarget(target);
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Target '{target.Name}' added ({hosted.PendingTargets.Count} pending)"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // DELETE /api/v1/session/targets — clear all pending targets
        group.MapDelete("/targets", (IHostedSession hosted) =>
        {
            hosted.ClearTargets();
            return Results.Json(
                ResponseEnvelope<string>.Ok("Pending targets cleared"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // PUT /api/v1/session/profile — set active profile (pre-session)
        group.MapPut("/profile", (SetProfileRequest request, IHostedSession hosted) =>
        {
            hosted.SetActiveProfile(request.ProfileId);
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Active profile set to {request.ProfileId}"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }
}

public sealed class SetProfileRequest
{
    public required Guid ProfileId { get; init; }
}
