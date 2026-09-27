using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

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
    /// What <see cref="TianWenNodeClient.GetLatestFrameAsync"/> answered: a new linear frame (the caller's, read into a plane
    /// the <see cref="FrameReader"/> recycles once the caller releases it), the frame the caller already holds, no frame
    /// yet, or a failure.
    /// </summary>
    public readonly record struct FrameResult(Image? Image, int? FrameNumber, bool IsUnchanged, string? Error)
    {
        /// <summary>A new frame arrived; the caller releases <see cref="Image"/> when done with it.</summary>
        public static FrameResult Ok(Image image, int? frameNumber) => new FrameResult(image, frameNumber, false, null);

        /// <summary>The source still shows the frame the caller holds; nothing was transferred.</summary>
        public static FrameResult Unchanged(int? frameNumber) => new FrameResult(null, frameNumber, true, null);

        /// <summary>The source has no frame to show (no run, or none captured yet): the endpoint's 404.</summary>
        public static FrameResult None => new FrameResult(null, null, false, null);

        /// <summary>The fetch failed.</summary>
        public static FrameResult Fail(string error) => new FrameResult(null, null, false, error);

        /// <summary>True when <see cref="Image"/> holds a frame.</summary>
        [MemberNotNullWhen(true, nameof(Image))]
        public bool HasImage => Image is not null;
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
        // Node
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET /node</c>: which node this is and the wire it speaks. On the state poll's budget, since it is what
        /// a client asks first to learn whether the node answers at all.
        /// </summary>
        public Task<NodeResult<NodeInfoDto>> GetNodeAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/node", HostingJsonContext.Default.ResponseEnvelopeNodeInfoDto, _timeouts.StatePoll, cancellationToken);

        /// <summary>
        /// <c>DELETE /node/recovery</c>: the report of the node that died before this one (<see cref="NodeInfoDto.Recovery"/>)
        /// has been shown, and its user has chosen what to do about it.
        /// </summary>
        public Task<NodeResult<string>> DismissRecoveryAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/node/recovery", content: null, HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Session
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET /session/state</c>. A node with no session running answers 404, which surfaces as
        /// <see cref="NodeResult{T}.IsNotFound"/> rather than an error to render.
        /// </summary>
        public Task<NodeResult<SessionStateDto>> GetSessionStateAsync(CancellationToken cancellationToken) =>
            GetSessionStateAsync(cursor: null, cancellationToken);

        /// <summary>
        /// <c>GET /session/state</c> with every history from where <paramref name="cursor"/> says the client's copy ends (P5b
        /// part 7), so a night's log crosses once; null asks for everything. The answer's <see cref="SessionStateDto.HistoryFrom"/>
        /// says where each part starts.
        /// </summary>
        public Task<NodeResult<SessionStateDto>> GetSessionStateAsync(SessionStateCursor? cursor, CancellationToken cancellationToken) =>
            GetAsync(cursor is { } c ? $"api/v1/session/state?{c.ToQuery()}" : "api/v1/session/state",
                HostingJsonContext.Default.ResponseEnvelopeSessionStateDto, _timeouts.StatePoll, cancellationToken);

        /// <summary>
        /// <c>POST /session/start</c>. <paramref name="profileId"/> null uses the node's active profile, and
        /// <paramref name="configuration"/> null runs on the node's declared defaults; a client that holds a
        /// configuration sends it whole (<see cref="SessionConfigApiDto.FromConfiguration"/>). Returns as
        /// soon as the node has launched the run; poll the state for progress.
        /// </summary>
        public Task<NodeResult<string>> StartSessionAsync(Guid? profileId, SessionConfigApiDto? configuration, CancellationToken cancellationToken)
        {
            var path = profileId is { } id ? $"api/v1/session/start?profileId={id}" : "api/v1/session/start";
            return configuration is null
                ? PostAsync(path, content: null, HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken)
                : SendJsonAsync(HttpMethod.Post, path, configuration, HostingJsonContext.Default.SessionConfigApiDto,
                    HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);
        }

        /// <summary>
        /// <c>POST /session/flats</c>. All request fields are optional; unset knobs use the node's
        /// <c>SessionConfiguration</c> defaults.
        /// </summary>
        public Task<NodeResult<string>> StartFlatsAsync(FlatsRequestDto request, Guid? profileId, CancellationToken cancellationToken) =>
            SendJsonAsync(
                HttpMethod.Post,
                profileId is { } id ? $"api/v1/session/flats?profileId={id}" : "api/v1/session/flats",
                request, HostingJsonContext.Default.FlatsRequestDto,
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
            SendJsonAsync(
                HttpMethod.Post,
                "api/v1/session/targets",
                target, HostingJsonContext.Default.PendingTarget,
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
            SendJsonAsync(
                HttpMethod.Post,
                "api/v1/session/schedule",
                schedule, HostingJsonContext.Default.ScheduledObservationDtoArray,
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
        /// <c>GET /devices/state</c> -- every device the node has connected or a run holds, with what the node last read
        /// of it, whether it is connected and which run holds it (P2 part 1, #929). Authoritative; the <c>DEVICE-STATE</c>
        /// push is the latency hint (<see cref="DeviceStateDto.TryFromEvent"/>). Asking keeps the node reading at the
        /// GUI's cadences, for a client that polls rather than listens.
        /// </summary>
        public Task<NodeResult<DeviceStateDto[]>> GetDeviceStatesAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/devices/state", HostingJsonContext.Default.ResponseEnvelopeDeviceStateDtoArray, _timeouts.StatePoll, cancellationToken);

        /// <summary>
        /// <c>POST /devices/connect</c> -- connects the device the URI names (the whole URI: its settings ride on it), as a
        /// job the node finishes on its own (P2 part 2, #929). Answers the job at once; a second connect of the same
        /// device joins it, and one while another job holds the device is refused (409).
        /// </summary>
        public Task<NodeResult<JobDto>> ConnectDeviceAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/connect", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>GET /devices/disconnect-safety</c> -- whether the connected device can be disconnected now (a camera's cooler
        /// and whether it is at work) and which run holds it: the read before a disconnect is offered. 404 when it is not
        /// connected.
        /// </summary>
        public Task<NodeResult<DisconnectCheckDto>> GetDisconnectSafetyAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            GetAsync($"api/v1/devices/disconnect-safety?deviceUri={Uri.EscapeDataString(deviceUri.ToString())}",
                HostingJsonContext.Default.ResponseEnvelopeDisconnectCheckDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /devices/disconnect</c> -- disconnects the device as a job. Refused (409) while a run holds it, whatever
        /// <paramref name="skipWarmUp"/> says, and, unless it says to skip the warm-up, while a camera is cold or at work.
        /// </summary>
        public Task<NodeResult<JobDto>> DisconnectDeviceAsync(Uri deviceUri, bool skipWarmUp, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/disconnect", new DisconnectRequestDto { DeviceUri = deviceUri.ToString(), SkipWarmUp = skipWarmUp },
                HostingJsonContext.Default.DisconnectRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /devices/warm-and-disconnect</c> -- warms a cooled camera, turns its cooler off and disconnects it, as a
        /// job whose ramp runs in the node, so it finishes whatever becomes of this client. Refused (409) while a run holds it.
        /// </summary>
        public Task<NodeResult<JobDto>> WarmAndDisconnectDeviceAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/warm-and-disconnect", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /devices/camera/cool</c> -- cools the camera to <paramref name="setpointC"/> through the session's own
        /// ramp, as a job in the node (P2 part 3, #929). <paramref name="rampMinutes"/> null takes a session's default.
        /// </summary>
        public Task<NodeResult<JobDto>> CoolCameraAsync(Uri deviceUri, double setpointC, double? rampMinutes, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/camera/cool", new CoolRequestDto { DeviceUri = deviceUri.ToString(), SetpointC = setpointC, RampMinutes = rampMinutes },
                HostingJsonContext.Default.CoolRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /darks</c> -- takes a dark library with a camera connected to the node, as the node's run (P5 part 1 of
        /// docs/plans/hardware-in-the-server.md). Answered at once; <see cref="GetDarkLibraryAsync"/> follows it.
        /// </summary>
        public Task<NodeResult<DarkLibraryStateDto>> StartDarkLibraryAsync(DarkLibraryRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/darks", request, HostingJsonContext.Default.DarkLibraryRequestDto,
                HostingJsonContext.Default.ResponseEnvelopeDarkLibraryStateDto, _timeouts.Control, cancellationToken);

        /// <summary><c>GET /darks</c> -- the dark library going on, or the last one to end.</summary>
        public Task<NodeResult<DarkLibraryStateDto>> GetDarkLibraryAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/darks", HostingJsonContext.Default.ResponseEnvelopeDarkLibraryStateDto, _timeouts.StatePoll, cancellationToken);

        /// <summary><c>DELETE /darks</c> -- stops the dark library going on after the frame in hand, answered once it has ended.</summary>
        public Task<NodeResult<DarkLibraryStateDto>> StopDarkLibraryAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/darks", null, HostingJsonContext.Default.ResponseEnvelopeDarkLibraryStateDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /polar</c> -- polar alignment as the node's run (P5 part 4 of docs/plans/hardware-in-the-server.md). Answered
        /// at once; <see cref="GetPolarAlignmentAsync"/> follows it, and it refines until <see cref="StopPolarAlignmentAsync"/>.
        /// It ends by itself once this client has stopped beating for the node's detach grace.
        /// </summary>
        public Task<NodeResult<PolarStateDto>> StartPolarAlignmentAsync(PolarAlignmentRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/polar", request, HostingJsonContext.Default.PolarAlignmentRequestDto,
                HostingJsonContext.Default.ResponseEnvelopePolarStateDto, _timeouts.Control, cancellationToken);

        /// <summary><c>GET /polar</c> -- the polar alignment going on, or the last one to end.</summary>
        public Task<NodeResult<PolarStateDto>> GetPolarAlignmentAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/polar", HostingJsonContext.Default.ResponseEnvelopePolarStateDto, _timeouts.StatePoll, cancellationToken);

        /// <summary>
        /// <c>DELETE /polar</c> -- Done and Cancel alike. Answered at once, with the mount still to restore:
        /// <see cref="GetPolarAlignmentAsync"/> says when it has ended.
        /// </summary>
        public Task<NodeResult<PolarStateDto>> StopPolarAlignmentAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/polar", null, HostingJsonContext.Default.ResponseEnvelopePolarStateDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /planetary</c> -- a live planetary capture as the node's run (P5 part 5 of docs/plans/hardware-in-the-server.md),
        /// stacked on the node. Its frames are <see cref="FrameSources.PlanetaryLive"/> and <see cref="FrameSources.PlanetaryMaster"/>
        /// (<see cref="GetLatestFrameAsync"/>); it runs until <see cref="StopPlanetaryAsync"/>, or by itself until this client has
        /// stopped beating for the node's detach grace.
        /// </summary>
        public Task<NodeResult<PlanetaryStateDto>> StartPlanetaryAsync(PlanetaryRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/planetary", request, HostingJsonContext.Default.PlanetaryRequestDto,
                HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto, _timeouts.Control, cancellationToken);

        /// <summary><c>GET /planetary</c> -- the planetary capture going on, or the last one to end.</summary>
        public Task<NodeResult<PlanetaryStateDto>> GetPlanetaryAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/planetary", HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto, _timeouts.StatePoll, cancellationToken);

        /// <summary><c>PUT /planetary/controls</c> -- a change to the capture going on, which it takes after its next frame.</summary>
        public Task<NodeResult<PlanetaryStateDto>> SetPlanetaryControlsAsync(PlanetaryControlsDto controls, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Put, "api/v1/planetary/controls", controls, HostingJsonContext.Default.PlanetaryControlsDto,
                HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /planetary/record</c> -- records the capture going on to a SER file under the node's image folder (P5 part
        /// 5d). It finishes its duration whether or not anyone watches; the state's <c>Recording</c> follows it.
        /// </summary>
        public Task<NodeResult<PlanetaryStateDto>> StartPlanetaryRecordingAsync(PlanetaryRecordRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/planetary/record", request, HostingJsonContext.Default.PlanetaryRecordRequestDto,
                HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto, _timeouts.Control, cancellationToken);

        /// <summary><c>DELETE /planetary/record</c> -- ends the recording going on sooner than its duration.</summary>
        public Task<NodeResult<PlanetaryStateDto>> StopPlanetaryRecordingAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/planetary/record", null, HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto, _timeouts.Control, cancellationToken);

        /// <summary><c>DELETE /planetary</c> -- ends the planetary capture going on, answered once it has.</summary>
        public Task<NodeResult<PlanetaryStateDto>> StopPlanetaryAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/planetary", null, HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /preview/ota/{index}/exposure</c> -- a preview exposure with OTA <paramref name="otaIndex"/>'s camera outside a
        /// session, as a job (P5 part 2 of docs/plans/hardware-in-the-server.md). Its frame is then the OTA's, served by
        /// <see cref="GetLatestFrameAsync"/>.
        /// </summary>
        public Task<NodeResult<JobDto>> StartPreviewExposureAsync(int otaIndex, PreviewExposureRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, $"api/v1/preview/ota/{otaIndex.ToString(CultureInfo.InvariantCulture)}/exposure", request,
                HostingJsonContext.Default.PreviewExposureRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /preview/ota/{index}/solve</c> -- plate-solves the frame OTA <paramref name="otaIndex"/> shows, as a job; <see cref="GetSolutionAsync"/> reads the result.</summary>
        public Task<NodeResult<JobDto>> StartSolveAsync(int otaIndex, CancellationToken cancellationToken) =>
            PostAsync($"api/v1/preview/ota/{otaIndex.ToString(CultureInfo.InvariantCulture)}/solve", content: null,
                HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>GET /preview/ota/{index}/solution</c> -- the last solution of OTA <paramref name="otaIndex"/>'s frame, with the frame's token.</summary>
        public Task<NodeResult<PlateSolutionDto>> GetSolutionAsync(int otaIndex, CancellationToken cancellationToken) =>
            GetAsync($"api/v1/preview/ota/{otaIndex.ToString(CultureInfo.InvariantCulture)}/solution",
                HostingJsonContext.Default.ResponseEnvelopePlateSolutionDto, _timeouts.StatePoll, cancellationToken);

        /// <summary>
        /// <c>POST /preview/ota/{index}/solve-sync</c> -- takes a frame with OTA <paramref name="otaIndex"/>'s camera, solves it and
        /// syncs the mount, as a job that succeeds only when the mount synced.
        /// </summary>
        public Task<NodeResult<JobDto>> StartSolveSyncAsync(int otaIndex, PreviewExposureRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, $"api/v1/preview/ota/{otaIndex.ToString(CultureInfo.InvariantCulture)}/solve-sync", request,
                HostingJsonContext.Default.PreviewExposureRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /preview/ota/{index}/snapshot</c> -- saves the frame OTA <paramref name="otaIndex"/> shows on the node; answers its path there.</summary>
        public Task<NodeResult<string>> SaveSnapshotAsync(int otaIndex, CancellationToken cancellationToken) =>
            PostAsync($"api/v1/preview/ota/{otaIndex.ToString(CultureInfo.InvariantCulture)}/snapshot", content: null,
                HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/camera/warm</c> -- warms the camera and turns its cooler off, as a job, leaving it connected.</summary>
        public Task<NodeResult<JobDto>> WarmCameraAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/camera/warm", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /devices/camera/cooler-off</c> -- switches the cooler off at once, with no warm-up. Refused (409) while a
        /// run holds the camera or a job is working on it.
        /// </summary>
        public Task<NodeResult<string>> CameraCoolerOffAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/camera/cooler-off", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /devices/camera/settings</c> -- changes the gain, offset, binning and frame the request names, and answers
        /// what the camera reads back. A value the camera does not take is refused (400) before anything changes.
        /// </summary>
        public Task<NodeResult<CameraSettingsDto>> SetCameraSettingsAsync(CameraSettingsRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/camera/settings", request,
                HostingJsonContext.Default.CameraSettingsRequestDto, HostingJsonContext.Default.ResponseEnvelopeCameraSettingsDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/focuser/move</c> -- to a position or by a number of steps, as a job that ends when the focuser has stopped (P2 part 4, #929). Refused (409) while another job moves it.</summary>
        public Task<NodeResult<JobDto>> MoveFocuserAsync(FocuserMoveRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/focuser/move", request,
                HostingJsonContext.Default.FocuserMoveRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/focuser/stop</c> -- halts the focuser, ending the move job that drives it, if one does.</summary>
        public Task<NodeResult<string>> StopFocuserAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/focuser/stop", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/filterwheel/change</c> -- turns the wheel to a position counted from 0, as a job that ends when it is there.</summary>
        public Task<NodeResult<JobDto>> ChangeFilterAsync(Uri deviceUri, int position, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/filterwheel/change", new FilterChangeRequestDto { DeviceUri = deviceUri.ToString(), Position = position },
                HostingJsonContext.Default.FilterChangeRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/mount/goto</c> -- slews to a J2000 position through the one goto every host uses, as a job that ends when the mount has landed. What only the mount can answer (below the horizon) fails the job with its reason.</summary>
        public Task<NodeResult<JobDto>> GotoAsync(MountGotoRequestDto request, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/mount/goto", request,
                HostingJsonContext.Default.MountGotoRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/mount/park</c> -- parks the mount, as a job that ends when it reports itself parked.</summary>
        public Task<NodeResult<JobDto>> ParkMountAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/mount/park", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/mount/unpark</c> -- unparks the mount, as a job.</summary>
        public Task<NodeResult<JobDto>> UnparkMountAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/mount/unpark", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/mount/tracking</c> -- switches tracking at once; refused (409) while a job moves the mount.</summary>
        public Task<NodeResult<string>> SetMountTrackingAsync(Uri deviceUri, bool on, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/mount/tracking", new MountTrackingRequestDto { DeviceUri = deviceUri.ToString(), On = on },
                HostingJsonContext.Default.MountTrackingRequestDto, HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        /// <summary><c>POST /devices/mount/stop</c> -- stops the mount where it is, ending the goto or park job that drives it, if one does.</summary>
        /// <summary>
        /// <c>POST /devices/mount/move-axis</c> -- moves the axis at <paramref name="rate"/> degrees a second, or stops it
        /// at 0 (P2 part 5, #929). LEASED: the axis stops by itself <see cref="NodeWire.MoveAxisLease"/> after the last
        /// call, so a caller holding a move repeats this while it holds (every half second), and one that dies stops it.
        /// </summary>
        public Task<NodeResult<JobDto>> MoveAxisAsync(Uri deviceUri, TelescopeAxis axis, double rate, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/mount/move-axis", new MoveAxisRequestDto { DeviceUri = deviceUri.ToString(), Axis = axis, Rate = rate },
                HostingJsonContext.Default.MoveAxisRequestDto, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        public Task<NodeResult<string>> StopMountAsync(Uri deviceUri, CancellationToken cancellationToken) =>
            SendJsonAsync(HttpMethod.Post, "api/v1/devices/mount/stop", new DeviceRequestDto { DeviceUri = deviceUri.ToString() },
                HostingJsonContext.Default.DeviceRequestDto, HostingJsonContext.Default.ResponseEnvelopeString, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>POST /devices/discover</c> -- starts a discovery on the node, or joins the one running, and answers
        /// at once with its job (202). Follow it with <see cref="GetJobAsync"/> or the <c>JOB-PROGRESS</c> push,
        /// then read what it found with <see cref="GetDevicesAsync"/>. It runs on the node's token, so this
        /// call's budget ending, or this client going away, leaves it running.
        /// </summary>
        public Task<NodeResult<JobDto>> StartDiscoveryAsync(CancellationToken cancellationToken) =>
            PostAsync("api/v1/devices/discover", content: null, HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Jobs: the node's slow operations
        // ---------------------------------------------------------------------------------

        /// <summary><c>GET /jobs/{id}</c> -- how a job stands. Authoritative; the push is only a hint.</summary>
        public Task<NodeResult<JobDto>> GetJobAsync(string id, CancellationToken cancellationToken) =>
            GetAsync($"api/v1/jobs/{Uri.EscapeDataString(id)}", HostingJsonContext.Default.ResponseEnvelopeJobDto, _timeouts.Control, cancellationToken);

        /// <summary><c>GET /jobs</c> -- every job the node knows about, running and recently ended, newest first.</summary>
        public Task<NodeResult<JobDto[]>> GetJobsAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/jobs", HostingJsonContext.Default.ResponseEnvelopeJobDtoArray, _timeouts.Control, cancellationToken);

        /// <summary><c>DELETE /jobs/{id}</c> -- asks a job to stop; it ends Cancelled once its work notices.</summary>
        public Task<NodeResult<JobDto>> CancelJobAsync(string id, CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, $"api/v1/jobs/{Uri.EscapeDataString(id)}", content: null, HostingJsonContext.Default.ResponseEnvelopeJobDto,
                _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>GET /preview/{otaIndex}</c> -- the latest frame as JPEG.
        /// <para>
        /// <b>Not an envelope endpoint</b>: it answers raw image bytes, so it bypasses
        /// <see cref="SendAsync"/> entirely. The response carries <c>X-Frame-Number</c>; pass the last one
        /// you saw as <paramref name="ifNotFrameNumber"/> and the node answers 304 without encoding anything
        /// when nothing new has landed (<see cref="PreviewResult.Unchanged"/>). A preview poll that
        /// re-downloaded an unchanged full-resolution frame twice a second would dominate the link for no
        /// benefit, and one the node re-encoded twice a second would load the rig for none.
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
            if (ifNotFrameNumber is { } held)
            {
                // A conditional GET: the node answers 304 before leasing or encoding when this is still its
                // current frame (PreviewHeaders). A node older than that ignores it and sends the picture,
                // which the header comparison below still catches.
                request.Headers.IfNoneMatch.Add(
                    new EntityTagHeaderValue($"\"{held.ToString(CultureInfo.InvariantCulture)}\""));
            }

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
                // Before the success test: 304 is outside 2xx, and it is the answer this request hopes for.
                if (response.StatusCode is HttpStatusCode.NotModified)
                {
                    return PreviewResult.Unchanged;
                }

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

        /// <summary>
        /// <c>GET /frames/{source}/latest</c> -- the frame <paramref name="source"/> shows now, LINEAR and full-resolution
        /// (<see cref="FrameWire"/>), read through <paramref name="reader"/>, which recycles a released frame's planes (one
        /// reader per source). Pass the number of the frame held as <paramref name="after"/> and the node answers 204,
        /// touching nothing, while its source still shows it (<see cref="FrameResult.IsUnchanged"/>). A <c>FRAME-AVAILABLE</c>
        /// push says when to ask. On the preview budget: the body is the slow part.
        /// </summary>
        /// <param name="source">A <see cref="FrameSources"/> name: <c>ota/{index}</c>, <c>guider</c>, <c>planetary/live</c> or
        /// <c>planetary/master</c>.</param>
        public async Task<FrameResult> GetLatestFrameAsync(string source, int? after, FrameReader reader, CancellationToken cancellationToken)
        {
            var path = $"api/v1/frames/{source}/latest{(after is { } held ? $"?after={held.ToString(CultureInfo.InvariantCulture)}" : "")}";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var budgeted = WithBudget(_timeouts.Preview, cancellationToken);
            try
            {
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budgeted.Token).ConfigureAwait(false);
                var number = TryReadFrameNumber(response) is { } n ? (int?)n : null;
                if (response.StatusCode is HttpStatusCode.NoContent)
                {
                    return FrameResult.Unchanged(number);
                }
                if (!response.IsSuccessStatusCode)
                {
                    // 404 is the ordinary "no frame to show" answer, not a fault to report.
                    return response.StatusCode is HttpStatusCode.NotFound
                        ? FrameResult.None
                        : FrameResult.Fail($"{(int)response.StatusCode} {response.ReasonPhrase}");
                }

                await using var body = await response.Content.ReadAsStreamAsync(budgeted.Token).ConfigureAwait(false);
                return FrameResult.Ok(await reader.ReadAsync(body, budgeted.Token).ConfigureAwait(false), number);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
            {
                return FrameResult.Fail(ex is OperationCanceledException ? TimedOut(_timeouts.Preview) : ex.Message);
            }
        }

        private static long? TryReadFrameNumber(HttpResponseMessage response)
            => response.Headers.TryGetValues(PreviewFrameNumberHeader, out var values)
                && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;

        /// <summary>Change token the preview endpoint stamps on every response.</summary>
        public const string PreviewFrameNumberHeader = PreviewHeaders.FrameNumber;

        // ---------------------------------------------------------------------------------
        // Profiles
        // ---------------------------------------------------------------------------------

        /// <summary><c>GET /profiles</c>.</summary>
        public Task<NodeResult<ProfileSummaryDto[]>> GetProfilesAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/profiles", HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDtoArray, _timeouts.Control, cancellationToken);

        /// <summary><c>GET /profiles/{id}</c> -- the full equipment profile, which is all the planner
        /// and sky map need to work against a remote rig. Carries the WHOLE profile (<see cref="ProfileDetailDto.Data"/>)
        /// and the revision it was read at, which an edit names.</summary>
        public Task<NodeResult<ProfileDetailDto>> GetProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
            GetAsync($"api/v1/profiles/{profileId}", HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto, _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>PUT /profiles/{id}</c>: replaces the whole profile with <paramref name="data"/>, made against
        /// <paramref name="revision"/> (<see cref="ProfileDetailDto.Revision"/> of the copy it was edited from), and answers
        /// the profile as stored. Only over the node's socket (a <b>403</b> over TCP). A <b>412</b> means the stored profile
        /// has moved on since that revision: read it again and reapply the edit, never resend it as it was.
        /// </summary>
        /// <param name="name">A new name; null keeps the profile's.</param>
        public Task<NodeResult<ProfileDetailDto>> UpdateProfileAsync(Guid profileId, ProfileData data, string revision, string? name,
            CancellationToken cancellationToken) =>
            SendJsonAsync(
                HttpMethod.Put,
                $"api/v1/profiles/{profileId}",
                new UpdateProfileRequest { Name = name, Data = data, Revision = revision }, HostingJsonContext.Default.UpdateProfileRequest,
                HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto,
                _timeouts.Control, cancellationToken);

        /// <summary><c>POST /profiles</c>: creates an empty profile named <paramref name="name"/>. Only over the node's socket.</summary>
        public Task<NodeResult<ProfileDetailDto>> CreateProfileAsync(string name, CancellationToken cancellationToken) =>
            SendJsonAsync(
                HttpMethod.Post,
                "api/v1/profiles",
                new CreateProfileRequest { Name = name }, HostingJsonContext.Default.CreateProfileRequest,
                HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto,
                _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>DELETE /profiles/{id}</c>. Only over the node's socket; a <b>409</b> for the node's active profile, or while a
        /// run is going.
        /// </summary>
        public Task<NodeResult<string>> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, $"api/v1/profiles/{profileId}", content: null, HostingJsonContext.Default.ResponseEnvelopeString,
                _timeouts.Control, cancellationToken);

        /// <summary>
        /// <c>PUT /devices/setting</c>: commits one of a device's settings as the Equipment tab does. A masked one (an API
        /// key) goes into the node's credential store and never comes back; any other goes onto the device's URI, written
        /// into <paramref name="profileId"/>'s slot for it when one is named. Only over the node's socket.
        /// </summary>
        public Task<NodeResult<DeviceSettingDto>> SetDeviceSettingAsync(Uri deviceUri, string key, string value, Guid? profileId,
            CancellationToken cancellationToken) =>
            SendJsonAsync(
                HttpMethod.Put,
                "api/v1/devices/setting",
                new DeviceSettingRequestDto { DeviceUri = deviceUri.ToString(), Key = key, Value = value, ProfileId = profileId },
                HostingJsonContext.Default.DeviceSettingRequestDto,
                HostingJsonContext.Default.ResponseEnvelopeDeviceSettingDto,
                _timeouts.Control, cancellationToken);

        /// <summary><c>GET /devices/setting/secret</c>: whether a device's masked setting has a value on the node. Never the value.</summary>
        public Task<NodeResult<DeviceSecretDto>> GetDeviceSecretAsync(Uri deviceUri, string key, CancellationToken cancellationToken) =>
            GetAsync($"api/v1/devices/setting/secret?deviceUri={Uri.EscapeDataString(deviceUri.ToString())}&key={Uri.EscapeDataString(key)}",
                HostingJsonContext.Default.ResponseEnvelopeDeviceSecretDto, _timeouts.Control, cancellationToken);

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
            SendJsonAsync(
                HttpMethod.Put,
                "api/v1/session/profile",
                new SetProfileRequest { ProfileId = profileId }, HostingJsonContext.Default.SetProfileRequest,
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

        /// <summary>
        /// JSON-body variant that creates the content HERE, in the same scope that disposes it -- the
        /// request's own dispose in <see cref="SendAsync"/> covers the transfer, but creating at the call
        /// sites left four copies of an ownership hand-off no analyzer can verify. One owner, provably
        /// disposed on every path.
        /// </summary>
        private async Task<NodeResult<T>> SendJsonAsync<TBody, T>(HttpMethod method, string path, TBody body,
            JsonTypeInfo<TBody> bodyInfo, JsonTypeInfo<ResponseEnvelope<T>> typeInfo, TimeSpan budget,
            CancellationToken cancellationToken)
        {
            using var content = JsonContent.Create(body, bodyInfo);
            return await SendAsync(method, path, content, typeInfo, budget, cancellationToken).ConfigureAwait(false);
        }

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

                // The envelope is authoritative over the HTTP status. A node answers the envelope's own
                // status as the HTTP status too (since P0b item 6, #752), so the two agree, but the message
                // is only in the envelope, and a node older than that answers 200 with Success=false.
                return envelope is { Success: true, Response: { } value }
                    ? NodeResult<T>.Ok(value)
                    : NodeResult<T>.Fail(
                        string.IsNullOrEmpty(envelope.Error) ? $"{envelope.StatusCode}" : envelope.Error,
                        envelope.StatusCode);
            }
        }
    }
}
