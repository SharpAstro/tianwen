using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

        // GET /v2/api/sequence/state — maps to session phase
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

        // GET /v2/api/sequence/start — start session using active profile + pending targets
        group.MapGet("/start", (IHostedSession hosted, ISessionFactory factory, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not null)
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

            // Drain pending targets
            var pendingTargets = hosted is HostedSession hs2 ? hs2.DrainTargets() : [];
            var observations = pendingTargets.Select(t => new ScheduledObservation(
                new Target(t.RA, t.Dec, t.Name, null),
                DateTimeOffset.UtcNow,
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

            if (hosted is HostedSession hs)
            {
                hs.SetSession(session);
            }

            _ = Task.Run(async () =>
            {
                try { await session.RunAsync(ct); }
                catch (OperationCanceledException) { }
            }, ct);

            return Results.Json(
                ResponseEnvelope<string>.Ok("Sequence started"),
                NinaApiJsonContext.Default.ResponseEnvelopeString);
        });

        // GET /v2/api/sequence/stop — stop session
        group.MapGet("/stop", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No sequence running", 404),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            if (hosted is HostedSession hs)
            {
                _ = Task.Run(async () => await hs.StopAsync(CancellationToken.None));
            }

            return Results.Json(
                ResponseEnvelope<string>.Ok("Sequence stopped"),
                NinaApiJsonContext.Default.ResponseEnvelopeString);
        });

        // GET /v2/api/sequence/set-target?name=&ra=&dec=&rotation=&index= — queue a target
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
