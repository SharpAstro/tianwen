using System;
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
    public sealed class TianWenNodeClient(HttpClient httpClient)
    {
        /// <summary>The node root this client talks to, for logging and display.</summary>
        public Uri? BaseAddress => httpClient.BaseAddress;

        // ---------------------------------------------------------------------------------
        // Session
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET /session/state</c>. A node with no session running answers 404, which surfaces as
        /// <see cref="NodeResult{T}.IsNotFound"/> rather than an error to render.
        /// </summary>
        public Task<NodeResult<SessionStateDto>> GetSessionStateAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/session/state", HostingJsonContext.Default.ResponseEnvelopeSessionStateDto, cancellationToken);

        /// <summary>
        /// <c>POST /session/start</c>. <paramref name="profileId"/> null uses the node's active profile.
        /// Returns as soon as the node has launched the run; poll the state for progress.
        /// </summary>
        public Task<NodeResult<string>> StartSessionAsync(Guid? profileId, CancellationToken cancellationToken) =>
            PostAsync(
                profileId is { } id ? $"api/v1/session/start?profileId={id}" : "api/v1/session/start",
                content: null,
                HostingJsonContext.Default.ResponseEnvelopeString,
                cancellationToken);

        /// <summary>
        /// <c>POST /session/flats</c>. All request fields are optional; unset knobs use the node's
        /// <c>SessionConfiguration</c> defaults.
        /// </summary>
        public Task<NodeResult<string>> StartFlatsAsync(FlatsRequestDto request, Guid? profileId, CancellationToken cancellationToken) =>
            PostAsync(
                profileId is { } id ? $"api/v1/session/flats?profileId={id}" : "api/v1/session/flats",
                JsonContent.Create(request, HostingJsonContext.Default.FlatsRequestDto),
                HostingJsonContext.Default.ResponseEnvelopeString,
                cancellationToken);

        /// <summary><c>POST /session/abort</c>.</summary>
        public Task<NodeResult<string>> AbortSessionAsync(CancellationToken cancellationToken) =>
            PostAsync("api/v1/session/abort", content: null, HostingJsonContext.Default.ResponseEnvelopeString, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Pre-session target queue
        // ---------------------------------------------------------------------------------

        /// <summary><c>GET /session/targets</c>.</summary>
        public Task<NodeResult<PendingTarget[]>> GetTargetsAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/session/targets", HostingJsonContext.Default.ResponseEnvelopePendingTargetArray, cancellationToken);

        /// <summary><c>POST /session/targets</c>.</summary>
        public Task<NodeResult<string>> AddTargetAsync(PendingTarget target, CancellationToken cancellationToken) =>
            PostAsync(
                "api/v1/session/targets",
                JsonContent.Create(target, HostingJsonContext.Default.PendingTarget),
                HostingJsonContext.Default.ResponseEnvelopeString,
                cancellationToken);

        /// <summary><c>DELETE /session/targets</c>.</summary>
        public Task<NodeResult<string>> ClearTargetsAsync(CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Delete, "api/v1/session/targets", content: null,
                HostingJsonContext.Default.ResponseEnvelopeString, cancellationToken);

        // ---------------------------------------------------------------------------------
        // Profiles
        // ---------------------------------------------------------------------------------

        /// <summary><c>GET /profiles</c>.</summary>
        public Task<NodeResult<ProfileSummaryDto[]>> GetProfilesAsync(CancellationToken cancellationToken) =>
            GetAsync("api/v1/profiles", HostingJsonContext.Default.ResponseEnvelopeProfileSummaryDtoArray, cancellationToken);

        /// <summary><c>GET /profiles/{id}</c> -- the full equipment profile, which is all the planner
        /// and sky map need to work against a remote rig.</summary>
        public Task<NodeResult<ProfileDetailDto>> GetProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
            GetAsync($"api/v1/profiles/{profileId}", HostingJsonContext.Default.ResponseEnvelopeProfileDetailDto, cancellationToken);

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
                cancellationToken);

        // ---------------------------------------------------------------------------------
        // Transport
        // ---------------------------------------------------------------------------------

        private Task<NodeResult<T>> GetAsync<T>(string path, JsonTypeInfo<ResponseEnvelope<T>> typeInfo,
            CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Get, path, content: null, typeInfo, cancellationToken);

        private Task<NodeResult<T>> PostAsync<T>(string path, HttpContent? content,
            JsonTypeInfo<ResponseEnvelope<T>> typeInfo, CancellationToken cancellationToken) =>
            SendAsync(HttpMethod.Post, path, content, typeInfo, cancellationToken);

        private async Task<NodeResult<T>> SendAsync<T>(HttpMethod method, string path, HttpContent? content,
            JsonTypeInfo<ResponseEnvelope<T>> typeInfo, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-initiated: propagate so a shutdown unwinds instead of being reported as an
                // unreachable node.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Unreachable host, DNS failure, or the HttpClient timeout (which surfaces as an
                // OperationCanceledException with our token NOT cancelled). All mean "no answer".
                return NodeResult<T>.Fail(ex.Message, (int)HttpStatusCode.ServiceUnavailable);
            }

            using (response)
            {
                ResponseEnvelope<T>? envelope;
                try
                {
                    envelope = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
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
