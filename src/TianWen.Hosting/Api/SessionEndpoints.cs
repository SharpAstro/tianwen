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
        group.MapPost("/start", async (HttpContext httpContext, IHostedSession hosted, ISessionFactory factory, ITimeProvider timeProvider, CancellationToken ct) =>
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
            var startUtc = timeProvider.GetUtcNow();
            var observations = pendingTargets.Select(t => new ScheduledObservation(
                new Target(t.RA, t.Dec, t.Name, null),
                startUtc,
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

        /// <summary>
        /// Starts an on-demand flat run (no observations) for the given profile via
        /// <see cref="ISession.RunFlatsOnlyAsync"/> and runs it in a background task. Returns immediately;
        /// poll /state for phase progress. Accepts an optional JSON body (<see cref="FlatsRequestDto"/>)
        /// selecting the source (calibrator / manual / sky), period, and flat knobs.
        /// </summary>
        group.MapPost("/flats", async (HttpContext httpContext, IHostedSession hosted, ISessionFactory factory, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // Optional body: source / period / flat knobs. Absent body = calibrator defaults. Validate the
            // request shape before the profile lookup so a bad source/period surfaces regardless of profile.
            FlatsRequestDto? request = null;
            if (httpContext.Request.ContentLength > 0)
            {
                try
                {
                    request = await httpContext.Request.ReadFromJsonAsync(HostingJsonContext.Default.FlatsRequestDto, ct);
                }
                catch
                {
                    return Results.Json(
                        ResponseEnvelope<string>.Fail("Malformed flats request body"),
                        HostingJsonContext.Default.ResponseEnvelopeString);
                }
            }

            if (!FlatRunParsing.TryParseSource(request?.Source, out var source) && request?.Source is not null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"Invalid source '{request.Source}'. Use 'calibrator', 'manual', or 'sky'."),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }
            if (!FlatRunParsing.TryParsePeriod(request?.Period, out var period) && request?.Period is not null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"Invalid period '{request.Period}'. Use 'dawn' or 'dusk'."),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // Profile ID from query string or active profile.
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

            // Site is left at NaN so RunFlatsOnlyAsync falls back to the mount's own configured site
            // (the headless rig's mount carries its site); only the flat knobs are overlaid onto defaults.
            var defaults = new SessionConfiguration();
            var config = defaults with
            {
                FlatSource = source,
                FlatsPerFilter = request?.Count ?? defaults.FlatsPerFilter,
                FlatTargetAduFraction = request?.Target ?? defaults.FlatTargetAduFraction,
                FlatAduTolerance = request?.Tolerance ?? defaults.FlatAduTolerance,
                FlatMaxBrackets = request?.MaxBrackets ?? defaults.FlatMaxBrackets,
                FlatCalibratorBrightnessPercent = request?.BrightnessPercent ?? defaults.FlatCalibratorBrightnessPercent,
                FlatInitialExposure = request?.InitialExposureSeconds is { } ie ? TimeSpan.FromSeconds(ie) : defaults.FlatInitialExposure,
                FlatMinExposure = request?.MinExposureSeconds is { } mn ? TimeSpan.FromSeconds(mn) : defaults.FlatMinExposure,
                FlatMaxExposure = request?.MaxExposureSeconds is { } mx ? TimeSpan.FromSeconds(mx) : defaults.FlatMaxExposure,
            };

            ISession session;
            try
            {
                session = factory.Create(profileId.Value, config, []);
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

            // Run in background — caller polls /state for progress (phase Flats -> Complete/Failed). The
            // session stays set on completion (mirrors /start) so the terminal phase is observable; POST
            // /abort disposes + clears it before the next run.
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.RunFlatsOnlyAsync(period, ct);
                }
                catch (OperationCanceledException)
                {
                    // Expected on abort.
                }
            }, ct);

            return Results.Json(
                ResponseEnvelope<string>.Ok("Flats started"),
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

        // GET /api/v1/session/targets — list pending targets.
        // Concrete PendingTarget[] (not ResponseEnvelope<object>) so the source-gen JSON context
        // can resolve the payload statically -- a polymorphic object payload throws under AOT.
        group.MapGet("/targets", (IHostedSession hosted) =>
        {
            return Results.Json(
                ResponseEnvelope<PendingTarget[]>.Ok([.. hosted.PendingTargets]),
                HostingJsonContext.Default.ResponseEnvelopePendingTargetArray);
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
