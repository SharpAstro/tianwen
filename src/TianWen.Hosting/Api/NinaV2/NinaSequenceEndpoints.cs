using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;
using TianWen.Hosting.Dto.NinaV2;
using TianWen.Lib.Sequencing;
// Disambiguate from Microsoft.AspNetCore.Http.ISession (ambient via the Web SDK).
using ISession = TianWen.Lib.Sequencing.ISession;

namespace TianWen.Hosting.Api.NinaV2;

/// <summary>
/// ninaAPI v2 sequence endpoints. Maps to session start/stop/state.
/// </summary>
internal static class NinaSequenceEndpoints
{
    public static RouteGroupBuilder MapNinaSequenceApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v2/api/sequence");

        // GET /v2/api/sequence/state: maps to session phase
        group.MapGet("/state", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Ok("IDLE"),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            // Map session phase to ninaAPI sequence state
            var state = session.Phase switch
            {
                SessionPhase.NotStarted => "IDLE",
                SessionPhase.Initialising => "STARTING",
                SessionPhase.WaitingForDark or SessionPhase.Cooling => "STARTING",
                SessionPhase.RoughFocus or SessionPhase.AutoFocus => "RUNNING",
                SessionPhase.CalibratingGuider => "RUNNING",
                SessionPhase.Observing => "RUNNING",
                SessionPhase.Finalising => "FINISHING",
                SessionPhase.Complete => "IDLE",
                SessionPhase.Failed or SessionPhase.Aborted => "IDLE",
                _ => "RUNNING",
            };

            return Results.Json(
                ResponseEnvelope<string>.Ok(state),
                NinaApiJsonContext.Default.ResponseEnvelopeString);
        });

        // GET /v2/api/sequence/start: start session using active profile + pending targets
        group.MapGet("/start", async (IHostedSession hosted, ISessionFactory factory, ILogger<HostedSession> logger, ITimeProvider timeProvider, CancellationToken ct) =>
        {
            if (hosted.IsRunning)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            if (hosted.ActiveProfileId is not { } profileId)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active profile. Use /v2/api/profile/switch first."),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            // The first run after a launch waits for the node's discovery, on the request's token, and
            // before the drain, so a caller that gives up takes nothing it queued with it.
            await hosted.WhenInitialisedAsync(ct);

            // Drain pending targets
            var pendingTargets = hosted is HostedSession hs2 ? hs2.DrainTargets() : [];
            var startUtc = timeProvider.GetUtcNow();
            var observations = pendingTargets.Select(t => new ScheduledObservation(
                new Target(t.RA, t.Dec, t.Name, null),
                startUtc,
                TimeSpan.FromMinutes(t.DurationMinutes ?? 30),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(
                    TimeSpan.FromSeconds(t.SubExposureSeconds ?? 120)),
                Gain: t.Gain,
                Offset: t.Offset
            )).ToArray();

            ISession session;
            try
            {
                session = factory.Create(profileId, new SessionConfiguration(), observations);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail(ex.Message, 404),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            // The run is the node's, on the node's token, never this request's (see the native /start).
            if (!await hosted.TryStartAsync(session, NodeRunKind.Session, profileId, static (run, runToken) => run.RunAsync(runToken)))
            {
                await session.DisposeAsync();
                foreach (var target in pendingTargets)
                {
                    hosted.AddTarget(target);
                }
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }
            logger.LogInformation("ninaAPI: sequence started");

            return Results.Json(
                ResponseEnvelope<string>.Ok("Sequence started"),
                NinaApiJsonContext.Default.ResponseEnvelopeString);
        });

        // GET /v2/api/sequence/stop: stop session
        group.MapGet("/stop", (IHostedSession hosted) =>
        {
            // Cancels the run into its own Finalise, never disposing it underneath (see the native /abort).
            if (hosted.TryAbort() is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No sequence running", 404),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            return Results.Json(
                ResponseEnvelope<string>.Ok("Sequence stopped"),
                NinaApiJsonContext.Default.ResponseEnvelopeString);
        });

        // GET /v2/api/sequence/set-target?name=&ra=&dec=&rotation=&index=; queue a target
        group.MapGet("/set-target", (string name, double ra, double dec, IHostedSession hosted) =>
        {
            hosted.AddTarget(new PendingTarget(name, ra, dec));
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Target '{name}' queued"),
                NinaApiJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }
}
