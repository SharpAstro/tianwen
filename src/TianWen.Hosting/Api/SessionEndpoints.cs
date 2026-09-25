using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

            // Newest ring entry only. The ring is oldest-first, so the tail is the newest; a client that
            // wants the history still calls /session/notifications.
            var notifications = hosted.Notifications;
            var dto = SessionStateDto.FromSession(
                session,
                ToPromptDto(hosted.PendingPrompt),
                notifications.IsDefaultOrEmpty ? null : notifications[^1]);
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
            if (hosted.IsRunning)
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

            // The configuration: the body's fields over the declared defaults. The body is read whatever its
            // framing, since the client's own JsonContent goes chunked with no Content-Length and used to be
            // ignored; and a body that does not parse is the caller's mistake, answered as one, never a run on
            // the defaults the caller did not ask for.
            var config = new SessionConfiguration();
            if (HasBody(httpContext))
            {
                SessionConfigApiDto? configDto;
                try
                {
                    configDto = await httpContext.Request.ReadFromJsonAsync(HostingJsonContext.Default.SessionConfigApiDto, ct);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    return Results.Json(
                        ResponseEnvelope<string>.Fail($"Malformed session configuration: {ex.Message}", 400),
                        HostingJsonContext.Default.ResponseEnvelopeString);
                }
                if (configDto is not null)
                {
                    config = configDto.ToConfiguration();
                }
            }

            // Profiles and devices are discovered as the node starts, in the background, so the first start
            // after a launch waits for them here, on the REQUEST's token: the waiting is the caller's. Before
            // the drain, so a client that gives up waiting takes nothing it queued with it.
            await hosted.WhenInitialisedAsync(ct);

            // A pushed schedule wins over the pending-target queue, because it is strictly richer: it
            // carries the planner's altitude-optimised Start per target, a per-filter exposure plan, and
            // AcrossMeridian, none of which PendingTarget can express. Falling back to the queue keeps
            // every existing caller (and the ninaAPI shim) working unchanged.
            var hs = hosted as HostedSession;
            var pushedSchedule = hs?.DrainSchedule() ?? [];
            PendingTarget[] pendingTargets = [];
            ScheduledObservation[] observations;
            if (!pushedSchedule.IsDefaultOrEmpty)
            {
                observations = [.. pushedSchedule];
            }
            else
            {
                pendingTargets = hs?.DrainTargets() ?? [];
                // Start = now for every target: a bare PendingTarget carries no slot time, so the loop
                // runs them back-to-back in list order (see WaitForScheduledStartAsync's same-Start
                // short-circuit). This is exactly the fidelity loss POST /schedule exists to avoid.
                var startUtc = timeProvider.GetUtcNow();
                observations = pendingTargets.Select(t => new ScheduledObservation(
                    new Target(t.RA, t.Dec, t.Name, null),
                    startUtc,
                    TimeSpan.FromMinutes(t.DurationMinutes ?? 30),
                    AcrossMeridian: false,
                    FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(
                        TimeSpan.FromSeconds(t.SubExposureSeconds ?? 120)),
                    Gain: t.Gain.HasValue ? (int?)t.Gain.Value : null,
                    Offset: t.Offset.HasValue ? (int?)t.Offset.Value : null
                )).ToArray();
            }

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

            // The run is the NODE's, on the node's token, never this request's: a client dropping its
            // connection, or a GUI restarting, must not cancel a night. Caller polls /state for progress.
            if (!await hosted.TryStartAsync(session, static (run, runToken) => run.RunAsync(runToken)))
            {
                await session.DisposeAsync();
                // Lost a race with another start: what this one drained goes back for the next.
                if (!pushedSchedule.IsDefaultOrEmpty)
                {
                    hosted.SetSchedule(pushedSchedule);
                }
                foreach (var target in pendingTargets)
                {
                    hosted.AddTarget(target);
                }
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            hosted.SetActiveProfile(profileId.Value);
            return Results.Json(
                ResponseEnvelope<string>.Ok("Session started"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        /// <summary>
        /// Starts an on-demand flat run (no observations) for the given profile via
        /// <see cref="ISession.RunFlatsOnlyAsync"/> and runs it in a background task. Returns immediately;
        /// poll /state for phase progress. Accepts an optional JSON body (<see cref="FlatsRequestDto"/>)
        /// selecting the source (calibrator / sky), period, and flat knobs. A manual hand-switched panel
        /// is a <c>ManualCoverDevice</c> assigned to the OTA's cover slot, captured via <c>calibrator</c>.
        /// </summary>
        group.MapPost("/flats", async (HttpContext httpContext, IHostedSession hosted, ISessionFactory factory, CancellationToken ct) =>
        {
            if (hosted.IsRunning)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // Optional body: source / period / flat knobs. Absent body = calibrator defaults. Validate the
            // request shape before the profile lookup so a bad source/period surfaces regardless of profile.
            FlatsRequestDto? request = null;
            if (HasBody(httpContext))
            {
                try
                {
                    request = await httpContext.Request.ReadFromJsonAsync(HostingJsonContext.Default.FlatsRequestDto, ct);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    return Results.Json(
                        ResponseEnvelope<string>.Fail($"Malformed flats request body: {ex.Message}"),
                        HostingJsonContext.Default.ResponseEnvelopeString);
                }
            }

            if (!FlatRunParsing.TryParseSource(request?.Source, out var source) && request?.Source is not null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"Invalid source '{request.Source}'. Use 'calibrator' or 'sky' (a manual panel is a Manual Light Panel device on the OTA's cover slot, captured via 'calibrator')."),
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

            // The first run after a launch waits for the node's discovery, on the request's token.
            await hosted.WhenInitialisedAsync(ct);

            // The declared defaults with the flat knobs overlaid. The site is left unset: the factory takes the
            // profile's when the profile is the site's authority, and otherwise RunFlatsOnlyAsync falls back to
            // the mount's own (the headless rig's mount carries its site). It was 0, 0 until the defaults
            // stopped being zeros.
            var defaults = new SessionConfiguration();
            var config = defaults with
            {
                // An operator asked for this run explicitly and may well have switched a hand-switched
                // panel on before walking back inside, so a prompt nobody answers proceeds rather than
                // skipping. The scheduled end-of-session flat block keeps the safe default (Decline) --
                // see UnattendedPromptResponse for why proceeding is not a safe blanket policy.
                UnattendedPromptResponse = UnattendedPromptResponse.Proceed,
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

            // The run is the node's, on the node's token (see /start). Caller polls /state for progress
            // (phase Flats -> Complete/Failed); the finished run stays readable until the next start.
            if (!await hosted.TryStartAsync(session, (run, runToken) => run.RunFlatsOnlyAsync(period, runToken)))
            {
                await session.DisposeAsync();
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            hosted.SetActiveProfile(profileId.Value);
            return Results.Json(
                ResponseEnvelope<string>.Ok("Flats started"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        group.MapPost("/abort", (IHostedSession hosted) =>
        {
            // Cancels the run, which ends through its own Finalise (park, warm-up, covers) while /state
            // shows it doing so. It used to dispose the session at once, disconnecting its drivers under a
            // run that carried on, so Finalise never ran properly.
            if (hosted.TryAbort() is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            return Results.Json(
                ResponseEnvelope<string>.Ok("Abort requested"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // --- Target management (pre-session) ---

        // GET /api/v1/session/targets: list pending targets.
        // Concrete PendingTarget[] (not ResponseEnvelope<object>) so the source-gen JSON context
        // can resolve the payload statically -- a polymorphic object payload throws under AOT.
        group.MapGet("/targets", (IHostedSession hosted) =>
        {
            return Results.Json(
                ResponseEnvelope<PendingTarget[]>.Ok([.. hosted.PendingTargets]),
                HostingJsonContext.Default.ResponseEnvelopePendingTargetArray);
        });

        // POST /api/v1/session/targets: add a target
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

        // DELETE /api/v1/session/targets: clear all pending targets
        group.MapDelete("/targets", (IHostedSession hosted) =>
        {
            hosted.ClearTargets();
            return Results.Json(
                ResponseEnvelope<string>.Ok("Pending targets cleared"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // --- Schedule push (full-fidelity, pre-session) ---

        // POST /api/v1/session/schedule: replace the pending schedule with a planner-computed one.
        // Distinct from /targets: this preserves slot times and per-filter plans (see
        // ScheduledObservationDto for why PendingTarget cannot).
        group.MapPost("/schedule", async (HttpContext httpContext, IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.IsRunning)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("A session is already running", 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            ScheduledObservationDto[]? dtos;
            try
            {
                dtos = await httpContext.Request.ReadFromJsonAsync(
                    HostingJsonContext.Default.ScheduledObservationDtoArray, ct);
            }
            catch
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Malformed schedule body"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (dtos is null)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("Schedule body is required"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var schedule = ImmutableArray.CreateBuilder<ScheduledObservation>(dtos.Length);
            foreach (var dto in dtos)
            {
                schedule.Add(dto.ToScheduled());
            }

            hosted.SetSchedule(schedule.MoveToImmutable());
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Schedule set ({dtos.Length} observation(s))"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // DELETE /api/v1/session/schedule: clear the pending schedule
        group.MapDelete("/schedule", (IHostedSession hosted) =>
        {
            hosted.SetSchedule([]);
            return Results.Json(
                ResponseEnvelope<string>.Ok("Pending schedule cleared"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // --- User prompts ---

        // POST /api/v1/session/prompt/respond: answer the session's outstanding prompt.
        // Without this a remote client can never clear a manual-flat-panel prompt, and the node's
        // auto-proceed timer would be the only thing that ever unblocks the run.
        group.MapPost("/prompt/respond", (bool proceed, IHostedSession hosted) =>
        {
            if (!hosted.TryRespondToPrompt(proceed))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No prompt is awaiting a response", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            return Results.Json(
                ResponseEnvelope<string>.Ok(proceed ? "Prompt accepted" : "Prompt declined"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // --- Notifications ---

        // GET /api/v1/session/notifications: the node's notification ring, oldest first.
        group.MapGet("/notifications", (IHostedSession hosted) =>
        {
            return Results.Json(
                ResponseEnvelope<NotificationDto[]>.Ok([.. hosted.Notifications]),
                HostingJsonContext.Default.ResponseEnvelopeNotificationDtoArray);
        });

        // GET /api/v1/session/profile, which profile this node is set up to run.
        //
        // The symmetric read of the PUT below, and the ONLY way a client can learn this: the node held
        // ActiveProfileId privately (every reader was server-side) and /profiles lists what exists without
        // saying which one is live. A remote observer needs it to label a rig by the optical train it is
        // running rather than by address alone -- and unlike the session snapshot, it answers for an IDLE
        // rig too, which is most of them most of the time.
        group.MapGet("/profile", (IHostedSession hosted, IDeviceDiscovery deviceDiscovery) =>
        {
            if (hosted.ActiveProfileId is not { } activeId)
            {
                return Results.Json(
                    ResponseEnvelope<ProfileSummaryDto>.NotFound("No active profile is set"),
                    HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDto);
            }

            var profile = deviceDiscovery.RegisteredDevices(DeviceType.Profile)
                .OfType<Profile>()
                .FirstOrDefault(p => p.ProfileId == activeId);

            // Set but no longer present: report it as missing rather than inventing a name, so a client
            // shows "profile unknown" instead of a label for a profile that has been deleted.
            return profile is null
                ? Results.Json(
                    ResponseEnvelope<ProfileSummaryDto>.NotFound($"Active profile {activeId} no longer exists"),
                    HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDto)
                : Results.Json(
                    ResponseEnvelope<ProfileSummaryDto>.Ok(
                        new ProfileSummaryDto { ProfileId = profile.ProfileId, Name = profile.DisplayName }),
                    HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDto);
        });

        // PUT /api/v1/session/profile: set active profile (pre-session)
        group.MapPut("/profile", (SetProfileRequest request, IHostedSession hosted, IDeviceHub hub) =>
        {
            // Same single-profile-context invariant the GUI/TUI enforce (see ProfileSwitchGate): a
            // running session or connected hardware belongs to the CURRENT profile, so re-pointing the
            // active profile underneath it would strand those drivers.
            var verdict = ProfileSwitchGate.Evaluate(hub, hosted.IsRunning);
            if (!verdict.Allowed)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail(verdict.Describe(), 409),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            hosted.SetActiveProfile(request.ProfileId);
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Active profile set to {request.ProfileId}"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }

    /// <summary>
    /// Whether the request carries a body: a Content-Length above zero or a chunked one. Keying on
    /// Content-Length alone skipped every chunked body, which is what <c>JsonContent</c> sends.
    /// </summary>
    private static bool HasBody(HttpContext httpContext)
        => httpContext.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: true };

    /// <summary>
    /// Projects the host's live prompt onto its wire shape. Separate from
    /// <see cref="SessionStateDto.FromSession"/> because the prompt lives on the host, not the session.
    /// </summary>
    private static PendingPromptDto? ToPromptDto(SessionPromptEventArgs? prompt)
        => prompt is null
            ? null
            : new PendingPromptDto
            {
                Title = prompt.Title,
                Message = prompt.Message,
                ContinueLabel = prompt.ContinueLabel,
                CancelLabel = prompt.CancelLabel,
                RequiresPhysicalPresence = prompt.RequiresPhysicalPresence,
                RaisedUtc = prompt.RaisedUtc,
            };
}
