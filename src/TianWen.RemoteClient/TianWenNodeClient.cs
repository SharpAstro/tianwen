using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;

namespace TianWen.RemoteClient
{
    /// <summary>
    /// The outcome of one native-v1 call: either a payload or the server's own error text.
    /// <para>
    /// Every endpoint answers with a <see cref="ResponseEnvelope{T}"/> carrying <c>Success</c>, an
    /// <c>Error</c> string and a status code, so a failure is ordinary data rather than an exception --
    /// "no session running" (404) and "a session is already running" (409) are normal states of a rig,
    /// not faults. Transport failures (host down, DNS, timeout) collapse into the same shape, because a
    /// caller that has to render "the rig is unreachable" cannot act differently on the two.
    /// </para>
    /// </summary>
    public readonly record struct NodeResult<T>(T? Value, string? Error, int StatusCode)
    {
        /// <summary>True when the server returned a payload.</summary>
        public bool IsSuccess => Error is null;

        /// <summary>True when the request itself reached the node and it answered 404 (e.g. no active
        /// session). Distinguishing this from a transport failure is what lets a caller show "idle"
        /// rather than "offline".</summary>
        public bool IsNotFound => StatusCode == 404;

        internal static NodeResult<T> Ok(T value) => new NodeResult<T>(value, null, 200);
        internal static NodeResult<T> Fail(string error, int statusCode) => new NodeResult<T>(default, error, statusCode);
    }

    /// <summary>
    /// Outcome of a preview fetch. Four states, because "nothing yet", "same frame you already have" and
    /// "the node is unreachable" are all normal and mean different things to a UI -- collapsing them into
    /// a null byte array would make an idle rig indistinguishable from a broken link.
    /// </summary>
    public readonly record struct PreviewResult(byte[]? Jpeg, long? FrameNumber, bool IsUnchanged, string? Error)
    {
        /// <summary>A new frame arrived.</summary>
        public static PreviewResult Ok(byte[] jpeg, long? frameNumber) => new PreviewResult(jpeg, frameNumber, false, null);

        /// <summary>The node still has the frame the caller already holds; nothing was transferred.</summary>
        public static PreviewResult Unchanged => new PreviewResult(null, null, true, null);

        /// <summary>No frame has been captured yet (the endpoint 404s).</summary>
        public static PreviewResult None => new PreviewResult(null, null, false, null);

        /// <summary>The fetch failed.</summary>
        public static PreviewResult Fail(string error) => new PreviewResult(null, null, false, error);

        /// <summary>True when <see cref="Jpeg"/> holds a frame to decode.</summary>
        [MemberNotNullWhen(true, nameof(Jpeg))]
        public bool HasImage => Jpeg is { Length: > 0 };
    }

    /// <summary>
    /// Per-request time budgets for one node.
    /// <para>
    /// <b>Why not a single <see cref="HttpClient.Timeout"/>:</b> one client serves both a ~2 KB state
    /// poll and a multi-megabyte preview JPEG, and no single value fits. Tight enough to notice a dead
    /// rig would abort previews on a marginal link; loose enough for previews leaves the UI asserting a
    /// rig is alive long after it stopped answering. The client's own <c>Timeout</c> stays a loose
    /// backstop so a call that forgets a budget degrades to slow rather than to unbounded.
    /// </para>
    /// <para>
    /// These matter because a rig that is switched off usually does not <i>refuse</i> the connection --
    /// that would fail instantly. It black-holes the packets, so the caller waits out the full budget.
    /// </para>
    /// </summary>
    /// <param name="StatePoll">
    /// <c>GET /session/state</c> -- the liveness signal, so this sets how fast a dead rig is noticed
    /// (worst case = this plus the poll interval, so ~7 s at the idle cadence).
    /// <para>
    /// Not tighter: a mini PC mid-frame-download, a GC pause or a Wi-Fi retry can legitimately blow past
    /// a second, and a card flapping between online and offline is worse than a couple of seconds of
    /// staleness. Better slightly slow to declare death than crying wolf on a healthy rig.
    /// </para>
    /// </param>
    /// <param name="Preview">
    /// Preview frames. Generous because they are opt-in and non-critical -- a preview that misses its
    /// budget simply does not update this tick -- while the payload is genuinely large: a couple of
    /// megabytes over weak 2.4 GHz Wi-Fi is legitimately several seconds.
    /// </param>
    /// <param name="Control">
    /// Everything else: start / abort / flats, schedule and target pushes, prompt replies, and the
    /// one-shot profile / device / notification reads. One-shot and consequential -- an abort in
    /// particular should either work or say that it did not, rather than hang.
    /// </param>
    public readonly record struct NodeTimeouts(TimeSpan StatePoll, TimeSpan Preview, TimeSpan Control)
    {
        /// <summary>The shipping values. A client that is not handed others uses these.</summary>
        public static readonly NodeTimeouts Default = new NodeTimeouts(
            StatePoll: TimeSpan.FromSeconds(5),
            Preview: TimeSpan.FromSeconds(30),
            Control: TimeSpan.FromSeconds(10));

        /// <summary>
        /// Backstop for <see cref="HttpClient.Timeout"/>: above every budget above, so the per-request
        /// values are what actually bite, but finite so a future call site that forgets one is merely
        /// slow instead of hanging forever.
        /// </summary>
        public static readonly TimeSpan ClientBackstop = TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Typed client for one node's native v1 API (<c>/api/v1/...</c>).
    /// <para>
    /// Takes an <see cref="HttpClient"/> whose <see cref="HttpClient.BaseAddress"/> is the node root, so
    /// it composes with <c>IHttpClientFactory</c> and is trivially testable against a scripted
    /// <see cref="HttpMessageHandler"/>. Serialization always passes an explicit
    /// <see cref="JsonTypeInfo"/> from <see cref="HostingJsonContext"/> -- the same source-generated
    /// context the server writes with, which is the whole point of the contracts split.
    /// </para>
    /// <para>
    /// Deliberately transport-only: no polling, no caching, no state. <see cref="RemoteSessionMirror"/>
    /// layers those on top.
    /// </para>
    /// </summary>
    public sealed class TianWenNodeClient(HttpClient httpClient, NodeTimeouts? timeouts = null)
    {
        // Overridable so a test can set budgets it can actually wait out: the expiry path is real
        // wall-clock (CancelAfter), and it is the one branch that must never be mistaken for caller
        // cancellation, so it has to be exercised for real rather than simulated.
        private readonly NodeTimeouts _timeouts = timeouts ?? NodeTimeouts.Default;

        /// <summary>The node root this client talks to, for logging and display.</summary>
        public Uri? BaseAddress => httpClient.BaseAddress;

        /// <summary>
        /// A cancellation source that fires on the caller's token OR when <paramref name="budget"/>
        /// elapses.
        /// <para>
        /// The two must stay distinguishable: the caller's token cancelling means "we are shutting down,
        /// unwind", while the budget elapsing means "the node did not answer, report it as unreachable".
        /// Callers therefore keep hold of the ORIGINAL token for their <c>when</c> guards and pass only
        /// this one to HTTP -- guarding on the linked token would turn every timeout into a rethrow and
        /// tear down the poll loop the first time a rig went quiet.
        /// </para>
        /// </summary>
        private static CancellationTokenSource WithBudget(TimeSpan budget, CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(budget);
            return cts;
        }

        /// <summary>Error text for a call that ran out of budget -- <c>OperationCanceledException.Message</c>
        /// is just "The operation was canceled", which tells a user nothing when surfaced verbatim.</summary>
        private static string TimedOut(TimeSpan budget) =>
            $"No answer within {budget.TotalSeconds:0.#}s";

        // ---------------------------------------------------------------------------------
        // Session
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET /session/state</c>. A node with no session running answers 404, which surfaces as
        /// <see cref="NodeResult{T}.IsNotFound"/> rather than an error to render.
        /// </summary>
        public Task<NodeResult<SessionStateDto>> GetSessionStateAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/session/state", HostingJsonContext.Default.ResponseEnvelopeSessionStateDto, _timeouts.StatePoll, cancellationToken);

        /// <summary>
        /// <c>POST /session/start</c>. <paramref name="profileId"/> null uses the node's active profile.
        /// Returns as soon as the node has launched the run; poll the state for progress.
        /// </summary>
        public Task<NodeResult<string>> StartSessionAsync(Guid? profileId, CancellationToken cancellationToken) =>
            PostAsync(
                profileId is { } id ? $"api/v1/session/start?profileId={id}" : "api/v1/session/start",
                content: null,
                HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /session/flats</c>. All request fields are optional; unset knobs use the node's
        /// <c>SessionConfiguration</c> defaults.
        /// </summary>
        public Task<NodeResult<string>> StartFlatsAsync(FlatsRequestDto request, Guid? profileId, CancellationToken cancellationToken) =>
            PostAsync(
                profileId is { } id ? $"api/v1/session/flats?profileId={id}" : "api/v1/session/flats",
                JsonContent.Create(request, HostingJsonContext.Default.FlatsRequestDto),
                HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        /// <summary><c>POST /session/abort</c>.</summary>
        public Task<NodeResult<string>> AbortSessionAsync(CancellationToken cancellationToken) =>
            PostAsync("api/v1/session/abort", content: null, HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Pre-session target queue
        // ---------------------------------------------------------------------------------

        /// <summary><c>GET /session/targets</c>.</summary>
        public Task<NodeResult<PendingTarget[]>> GetTargetsAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/session/targets", HostingJsonContext.Default.ResponseEnvelopePendingTargetArray, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /session/targets</c>.</summary>
        public Task<NodeResult<string>> AddTargetAsync(PendingTarget target, CancellationToken cancellationToken) =>
            PostAsync(
                "api/v1/session/targets",
                JsonContent.Create(target, HostingJsonContext.Default.PendingTarget),
                HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        /// <summary><c>DELETE /session/targets</c>.</summary>
        public Task<NodeResult<string>> ClearTargetsAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/session/targets", content: null,
                HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Pushed schedule -- the planner's own plan, not the flat target queue
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// <c>POST /session/schedule</c>. Use this, never <see cref="AddTargetAsync"/>, for anything the
        /// planner produced: <see cref="PendingTarget"/> carries no per-filter plan, no altitude-optimised
        /// <c>Start</c> and no <c>AcrossMeridian</c>, and <c>/session/start</c> stamps <c>Start = now</c>
        /// over whatever it drains from the queue.
        /// </summary>
        public Task<NodeResult<string>> SetScheduleAsync(ScheduledObservationDto[] schedule, CancellationToken cancellationToken) =>
            PostAsync(
                "api/v1/session/schedule",
                JsonContent.Create(schedule, HostingJsonContext.Default.ScheduledObservationDtoArray),
                HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        /// <summary><c>DELETE /session/schedule</c>.</summary>
        public Task<NodeResult<string>> ClearScheduleAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/session/schedule", content: null,
                HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Prompts + notifications
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// <c>POST /session/prompt/respond</c> -- answers the outstanding prompt.
        /// <para>
        /// Answering a prompt whose <c>RequiresPhysicalPresence</c> is set asserts that something was
        /// physically done at the rig ("the flat panel is switched on"). A remote operator cannot see
        /// that, so a UI must not present Continue as a neutral default; the node records it at Error
        /// severity for the same reason.
        /// </para>
        /// </summary>
        public Task<NodeResult<string>> RespondToPromptAsync(bool proceed, CancellationToken cancellationToken) =>
            PostAsync(
                $"api/v1/session/prompt/respond?proceed={(proceed ? "true" : "false")}",
                content: null,
                HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        /// <summary><c>GET /session/notifications</c> -- the node's ring, newest last.</summary>
        public Task<NodeResult<NotificationDto[]>> GetNotificationsAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/session/notifications", HostingJsonContext.Default.ResponseEnvelopeNotificationDtoArray, _timeouts.Control, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Devices + preview
        // ---------------------------------------------------------------------------------

        /// <summary><c>GET /devices/structured</c> -- URIs, type and live connected state. The plain
        /// <c>/devices</c> returns pre-formatted display strings a client cannot act on.</summary>
        public Task<NodeResult<DeviceDto[]>> GetDevicesAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/devices/structured", HostingJsonContext.Default.ResponseEnvelopeDeviceDtoArray, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>GET /preview/{otaIndex}</c> -- the latest frame as JPEG.
        /// <para>
        /// <b>Not an envelope endpoint</b>: it answers raw image bytes, so it bypasses
        /// <see cref="SendAsync"/> entirely. The response carries <c>X-Frame-Number</c>; pass the last one
        /// you saw as <paramref name="ifNotFrameNumber"/> and the fetch is skipped when nothing new has
        /// landed (<see cref="PreviewResult.Unchanged"/>). A preview poll that re-downloaded an unchanged
        /// full-resolution frame twice a second would dominate the link for no benefit.
        /// </para>
        /// </summary>
        public Task<PreviewResult> GetPreviewAsync(
            int otaIndex, int? quality, double? scale, long? ifNotFrameNumber, CancellationToken cancellationToken)
            => GetPreviewAsync(
                otaIndex.ToString(CultureInfo.InvariantCulture), quality, scale, ifNotFrameNumber, cancellationToken);

        /// <summary>
        /// <c>GET /preview/guider</c> -- the latest guide-camera frame as JPEG, on the same conditional
        /// -fetch contract as the per-OTA previews. Its own route because there is one guider per rig and
        /// its frames arrive at guiding cadence, not per sub.
        /// </summary>
        public Task<PreviewResult> GetGuidePreviewAsync(
            int? quality, double? scale, long? ifNotFrameNumber, CancellationToken cancellationToken)
            => GetPreviewAsync("guider", quality, scale, ifNotFrameNumber, cancellationToken);

        /// <summary>
        /// The one preview fetch. Both callers want identical handling of the change-token short circuit,
        /// the 404-is-not-a-fault rule and the two separate timeout budgets (headers, then body), and the
        /// only thing that differs between them is the last path segment.
        /// </summary>
        private async Task<PreviewResult> GetPreviewAsync(
            string segment, int? quality, double? scale, long? ifNotFrameNumber, CancellationToken cancellationToken)
        {
            var query = new List<string>(2);
            if (quality is { } q) query.Add($"quality={q}");
            if (scale is { } s) query.Add($"scale={s.ToString(CultureInfo.InvariantCulture)}");
            var path = $"api/v1/preview/{segment}{(query.Count > 0 ? "?" + string.Join("&", query) : "")}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var budgeted = WithBudget(_timeouts.Preview, cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budgeted.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return PreviewResult.Fail(
                    ex is OperationCanceledException ? TimedOut(_timeouts.Preview) : ex.Message);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // 404 is the ordinary "no frame captured yet" answer, not a fault to report.
                    return response.StatusCode is HttpStatusCode.NotFound
                        ? PreviewResult.None
                        : PreviewResult.Fail($"{(int)response.StatusCode} {response.ReasonPhrase}");
                }

                var frameNumber = TryReadFrameNumber(response);
                if (frameNumber is { } n && n == ifNotFrameNumber)
                {
                    return PreviewResult.Unchanged;
                }

                // Budgeted, not the raw caller token: the JPEG body is the slow part, so a node that
                // answers and then stalls mid-transfer has to be given up on like any other silence.
                byte[] bytes;
                try
                {
                    bytes = await response.Content.ReadAsByteArrayAsync(budgeted.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    return PreviewResult.Fail(
                        ex is OperationCanceledException ? TimedOut(_timeouts.Preview) : ex.Message);
                }

                return PreviewResult.Ok(bytes, frameNumber);
            }
        }

        private static long? TryReadFrameNumber(HttpResponseMessage response)
            => response.Headers.TryGetValues(PreviewFrameNumberHeader, out var values)
                && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;

        /// <summary>Change token the preview endpoint stamps on every response.</summary>
        public const string PreviewFrameNumberHeader = "X-Frame-Number";

        // ---------------------------------------------------------------------------------
        // Profiles
        // ---------------------------------------------------------------------------------

        /// <summary><c>GET /profiles</c>.</summary>
        public Task<NodeResult<ProfileSummaryDto[]>> GetProfilesAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/profiles", HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDtoArray, _timeouts.Control, cancellationToken);

        /// <summary><c>GET /profiles/{id}</c> -- the full equipment profile, which is all the planner
        /// and sky map need to work against a remote rig.</summary>
        public Task<NodeResult<ProfileDetailDto>> GetProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
            GetAsync($"api/v1/profiles/{profileId}", HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>GET /session/profile</c> -- which profile the node is set up to run, as opposed to
        /// <see cref="GetProfilesAsync"/>, which lists what it HAS. A <b>404</b> is a normal answer here
        /// (no active profile, or one that has since been deleted) and means "unknown", not "unreachable".
        /// </summary>
        public Task<NodeResult<ProfileSummaryDto>> GetActiveProfileAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/session/profile", HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>PUT /session/profile</c>. The node applies its own <c>ProfileSwitchGate</c> and answers
        /// <b>409</b> while its equipment is connected or a run owns it, with the gate's own wording as
        /// the error -- surface that verbatim rather than inventing a client-side message.
        /// </summary>
        public Task<NodeResult<string>> SetActiveProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
            SendAsync(
                HttpMethod.Put,
                "api/v1/session/profile",
                JsonContent.Create(new SetProfileRequest { ProfileId = profileId }, HostingJsonContext.Default.SetProfileRequest),
                HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Transport
        // ---------------------------------------------------------------------------------

        private Task<NodeResult<T>> GetAsync<T>(string path, JsonTypeInfo<ResponseEnvelope<T>> typeInfo,
            TimeSpan budget, CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Get, path, content: null, typeInfo, budget, cancellationToken);

        private Task<NodeResult<T>> PostAsync<T>(string path, HttpContent? content,
            JsonTypeInfo<ResponseEnvelope<T>> typeInfo, TimeSpan budget, CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Post, path, content, typeInfo, budget, cancellationToken);

        private async Task<NodeResult<T>> SendAsync<T>(HttpMethod method, string path, HttpContent? content,
            JsonTypeInfo<ResponseEnvelope<T>> typeInfo, TimeSpan budget, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var budgeted = WithBudget(budget, cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budgeted.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-initiated: propagate so a shutdown unwinds instead of being reported as an
                // unreachable node. Guarded on the ORIGINAL token, so a budget expiry falls through to
                // the handler below instead of being mistaken for a shutdown.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Unreachable host, DNS failure, or the request outrunning its budget. All mean "no
                // answer", and the UI surfaces this text verbatim, so name the timeout rather than
                // passing on OperationCanceledException's contentless message.
                return NodeResult<T>.Fail(
                    ex is OperationCanceledException ? TimedOut(budget) : ex.Message,
                    (int)HttpStatusCode.ServiceUnavailable);
            }

            using (response)
            {
                ResponseEnvelope<T>? envelope;
                try
                {
                    envelope = await response.Content.ReadFromJsonAsync(typeInfo, budgeted.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // The budget covers the whole exchange, not just the headers: a node that answers and
                    // then stalls mid-body is as unreachable as one that never answered.
                    return NodeResult<T>.Fail(TimedOut(budget), (int)HttpStatusCode.ServiceUnavailable);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException)
                {
                    // A bodiless 500 (Kestrel's shape when an endpoint throws), a non-JSON error page, or
                    // a genuine contract mismatch. Carry the parser's reason as well as the status: with
                    // the status alone, a schema drift between node and client reads as a plain "200 OK"
                    // failure and is near-undebuggable.
                    return NodeResult<T>.Fail(
                        $"{(int)response.StatusCode} {response.ReasonPhrase}: {ex.Message}",
                        (int)response.StatusCode);
                }

                if (envelope is null)
                {
                    return NodeResult<T>.Fail($"{(int)response.StatusCode} {response.ReasonPhrase} (empty body)", (int)response.StatusCode);
                }

                // The envelope is authoritative over the HTTP status: the endpoints return 200 with
                // Success=false and their own StatusCode inside for application-level failures.
                return envelope is { Success: true, Response: { } value }
                    ? NodeResult<T>.Ok(value)
                    : NodeResult<T>.Fail(
                        string.IsNullOrEmpty(envelope.Error) ? $"{envelope.StatusCode}" : envelope.Error,
                        envelope.StatusCode);
            }
        }
    }
}
