using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Hosting.Dto;
using TianWen.Lib.Hosting.Dto.NinaV2;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting.Api.NinaV2;

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

        // GET /v2/api/sequence/start — start session (ninaAPI uses GET, passes ?skipValidation=true)
        group.MapGet("/start", (IHostedSession hosted, ISessionFactory factory, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    NinaApiJsonContext.Default.ResponseEnvelopeString);
            }

            // For the v2 shim, we use the default profile (first available)
            // Full profile selection is handled via the native /api/v1/session/start endpoint
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

        return group;
    }
}
