using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the half of <see cref="RemoteSessionMirror"/> that <b>drives</b> a rig rather than watching
    /// it: preview frames, the prompt round-trip, and the start / flats / abort path
    /// (docs/plans/remote-profile.md P3 remainder). <see cref="RemoteSessionMirrorTests"/> covers the
    /// telemetry-fidelity half.
    /// <para>
    /// Preview frames are produced by the <b>real server-side encoder</b> and decoded by the real client
    /// path, so a drift in either end fails here rather than showing a black rectangle on a rig.
    /// </para>
    /// </summary>
    public class RemoteSessionMirrorDriveTests
    {
        // -------------------------------------------------------------------------------------------
        // Scripted transport, routed by path so one handler can serve state + preview + prompt
        // -------------------------------------------------------------------------------------------

        private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public List<string> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add($"{request.Method} {request.RequestUri?.PathAndQuery}");
                return Task.FromResult(respond(request));
            }
        }

        private static HttpResponseMessage Json<T>(ResponseEnvelope<T> envelope, HttpStatusCode status = HttpStatusCode.OK)
        {
            var json = JsonSerializer.Serialize(envelope, typeof(ResponseEnvelope<T>), HostingJsonContext.Default);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static HttpResponseMessage Ok(string message) =>
            Json(ResponseEnvelope<string>.Ok(message));

        private static HttpResponseMessage Conflict(string error) =>
            Json(ResponseEnvelope<string>.Fail(error, statusCode: 409));

        private static (RemoteSessionMirror Mirror, RoutingHandler Handler) BuildMirror(
            Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var handler = new RoutingHandler(respond);
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://rig.local:1888/") };
            var client = new TianWenNodeClient(http);
            var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero));
            var events = new TianWenEventStream(http.BaseAddress, timeProvider, NullLogger.Instance);
            return (new RemoteSessionMirror(client, events, timeProvider, NullLogger.Instance), handler);
        }

        /// <summary>
        /// A running-session snapshot with <paramref name="otaCount"/> OTAs, reusing
        /// <see cref="RemoteSessionMirrorTests.RunningState"/> so both suites exercise one sample shape --
        /// a second hand-built DTO would drift from the contract the moment a required member is added.
        /// </summary>
        private static SessionStateDto StateWith(int otaCount = 1, PendingPromptDto? prompt = null) =>
            RemoteSessionMirrorTests.RunningState(otaCount: otaCount, pendingPrompt: prompt);

        /// <summary>A real JPEG, produced by the same encoder the node uses.</summary>
        private static async Task<byte[]> RealPreviewJpegAsync(int width = 64, int height = 48)
        {
            var planes = Image.CreateChannelData(1, height, width);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    planes[0][y, x] = 900f + ((x * 7 + y * 13) % 40);
                }
            }
            planes[0][10, 10] = 48000f;

            var image = new Image(planes, BitDepth.Int16, maxValue: 48000f, minValue: 900f, pedestal: 0f,
                new ImageMeta { SensorType = SensorType.Monochrome });

            return await PreviewEncoder.EncodeJpegAsync(image, quality: 80, scale: 1.0, TestContext.Current.CancellationToken);
        }

        private static HttpResponseMessage Jpeg(byte[] bytes, long frameNumber)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            response.Headers.Add(TianWenNodeClient.PreviewFrameNumberHeader, frameNumber.ToString());
            return response;
        }

        // -------------------------------------------------------------------------------------------
        // Preview frames
        // -------------------------------------------------------------------------------------------

        [Fact]
        public async Task PreviewsAreOffUntilAskedFor()
        {
            // A dashboard watching six rigs wants phase and counters, not six JPEG streams. Previews are
            // by far the most expensive thing on the link, so they must be opt-in.
            var (mirror, handler) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                mirror.LastCapturedImages.ShouldBeEmpty();
                handler.Requests.ShouldNotContain(r => r.Contains("/preview/"));
            }
        }

        [Fact]
        public async Task APreviewFrameCrossesTheWireAndDecodes()
        {
            var jpeg = await RealPreviewJpegAsync();
            var (mirror, _) = BuildMirror(request =>
                request.RequestUri!.AbsolutePath.Contains("/preview/")
                    ? Jpeg(jpeg, frameNumber: 7)
                    : Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                var images = mirror.LastCapturedImages;
                images.Length.ShouldBe(1);
                var decoded = images[0];
                decoded.ShouldNotBeNull();
                decoded.Width.ShouldBe(64);
                decoded.Height.ShouldBe(48);
            }
        }

        /// <summary>Counts how many times its body is actually read, which is the only honest way to
        /// observe the change token working -- the request is issued either way; what the token saves is
        /// the transfer.</summary>
        private sealed class CountingContent(byte[] payload) : HttpContent
        {
            public int Reads { get; private set; }

            protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
            {
                Reads++;
                return stream.WriteAsync(payload, 0, payload.Length);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = payload.Length;
                return true;
            }
        }

        [Fact]
        public async Task AnUnchangedFrameIsNotRetransferred()
        {
            // The change token is the whole reason a 2 Hz preview poll is affordable: without it every
            // tick would re-download a full-resolution frame that has not moved.
            //
            // Asserting on the request COUNT would prove nothing -- the GET is issued either way, and the
            // client decides from the response header whether to read the body. So count body reads.
            var jpeg = await RealPreviewJpegAsync();
            var content = new CountingContent(jpeg);
            var (mirror, _) = BuildMirror(request =>
            {
                if (!request.RequestUri!.AbsolutePath.Contains("/preview/"))
                {
                    return Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith()));
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                response.Headers.Add(TianWenNodeClient.PreviewFrameNumberHeader, "7");
                return response;
            });

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                var first = mirror.LastCapturedImages[0];

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                content.Reads.ShouldBe(1, "polls 2 and 3 must stop at the X-Frame-Number header");
                mirror.LastCapturedImages[0].ShouldBeSameAs(first, "and must keep the frame already decoded");
            }
        }

        [Fact]
        public async Task PreviewOptionsReachTheNodeSoADownscaleActuallySavesBandwidth()
        {
            var jpeg = await RealPreviewJpegAsync();
            var (mirror, handler) = BuildMirror(request =>
                request.RequestUri!.AbsolutePath.Contains("/preview/")
                    ? Jpeg(jpeg, frameNumber: 1)
                    : Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions(Quality: 55, Scale: 0.25);

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                handler.Requests.ShouldContain(r => r.Contains("quality=55") && r.Contains("scale=0.25"));
            }
        }

        [Fact]
        public async Task AFailingPreviewNeverBlanksTheTelemetry()
        {
            // A link too slow or a node too busy for previews must degrade to "no thumbnail", not to a
            // blank Live Session tab.
            var (mirror, _) = BuildMirror(request =>
                request.RequestUri!.AbsolutePath.Contains("/preview/")
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                mirror.HasSession.ShouldBeTrue();
                mirror.Phase.ShouldBe(SessionPhase.Observing);
                mirror.IsNodeReachable.ShouldBeTrue();
            }
        }

        [Fact]
        public async Task PreviewsAreDroppedWhenTheSessionEnds()
        {
            var jpeg = await RealPreviewJpegAsync();
            var sessionRunning = true;
            var (mirror, _) = BuildMirror(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/preview/"))
                {
                    return Jpeg(jpeg, frameNumber: 3);
                }

                return sessionRunning
                    ? Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith()))
                    : Json(ResponseEnvelope<SessionStateDto>.NotFound("No session"), HttpStatusCode.NotFound);
            });

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                mirror.LastCapturedImages.Length.ShouldBe(1);

                sessionRunning = false;
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                mirror.LastCapturedImages.ShouldBeEmpty("a finished session must stop showing its last frame");
            }
        }

        // -------------------------------------------------------------------------------------------
        // Prompt round-trip
        // -------------------------------------------------------------------------------------------

        private static PendingPromptDto ManualPanelPrompt() => new PendingPromptDto
        {
            Title = "Manual flat panel",
            Message = "Switch on the flat panel for OTA 1, then Continue.",
            ContinueLabel = "Continue",
            CancelLabel = "Cancel",
            RequiresPhysicalPresence = true,
        };

        [Fact]
        public async Task APromptOnTheSnapshotIsRaisedLocally()
        {
            // Sourced from the poll, not the broadcast: a client that attached after PROMPT-REQUESTED
            // fired would otherwise never learn there was a question, and the run would hang.
            var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: ManualPanelPrompt()))));

            await using (mirror)
            {
                SessionPromptEventArgs? raised = null;
                mirror.PromptRequested += (_, e) => raised = e;

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                raised.ShouldNotBeNull();
                raised.Title.ShouldBe("Manual flat panel");
                raised.RequiresPhysicalPresence.ShouldBeTrue(
                    "a remote operator cannot see the panel, so the UI has to be able to say so");
            }
        }

        [Fact]
        public async Task AStandingPromptIsRaisedOnceNotOncePerPoll()
        {
            // The prompt sits on every snapshot until answered. Re-raising would stack a dialog per poll.
            var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: ManualPanelPrompt()))));

            await using (mirror)
            {
                var raised = 0;
                mirror.PromptRequested += (_, _) => raised++;

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                raised.ShouldBe(1);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AnsweringLocallyPostsTheAnswerBackToTheNode(bool proceed)
        {
            var answered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (mirror, _) = BuildMirror(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/prompt/respond", StringComparison.Ordinal))
                {
                    answered.TrySetResult(request.RequestUri.Query);
                    return Ok("Answered");
                }

                return Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: ManualPanelPrompt())));
            });

            await using (mirror)
            {
                mirror.PromptRequested += (_, e) => e.Respond(proceed);

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                var query = await answered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                query.ShouldContain($"proceed={(proceed ? "true" : "false")}");
            }
        }

        [Fact]
        public async Task AnUnansweredPromptIsNotAnsweredOnTheOperatorsBehalf()
        {
            // With no local handler the mirror must stay silent. The node already applied its own
            // unattended policy before broadcasting; a client inventing a second answer would be
            // fabricating a decision about hardware it cannot see.
            var (mirror, handler) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: ManualPanelPrompt()))));

            await using (mirror)
            {
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                handler.Requests.ShouldNotContain(r => r.Contains("/prompt/respond"));
            }
        }

        [Fact]
        public async Task ANewPromptWithIdenticalWordingIsRaisedAgain()
        {
            // Same panel, next filter: the wording repeats verbatim. De-duplicating on text alone would
            // swallow the second prompt and hang the run.
            var prompt = ManualPanelPrompt();
            PendingPromptDto? current = prompt;
            var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: current))));

            await using (mirror)
            {
                var raised = 0;
                mirror.PromptRequested += (_, _) => raised++;

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                current = null;                       // answered
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                current = prompt;                     // asked again for the next filter
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                raised.ShouldBe(2);
            }
        }

        // -------------------------------------------------------------------------------------------
        // Driving the run
        // -------------------------------------------------------------------------------------------

        private static ScheduledObservationDto[] OneObservation() =>
        [
            new ScheduledObservationDto
            {
                TargetName = "M42",
                TargetRA = 5.588,
                TargetDec = -5.39,
                Start = new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero),
                DurationMinutes = 90,
                AcrossMeridian = true,
            },
        ];

        [Fact]
        public async Task StartingPushesTheScheduleBeforeTheRun()
        {
            var (mirror, handler) = BuildMirror(_ => Ok("ok"));

            await using (mirror)
            {
                var result = await mirror.StartAsync(OneObservation(), profileId: null, TestContext.Current.CancellationToken);

                result.IsSuccess.ShouldBeTrue();
                handler.Requests.ShouldBe([
                    "POST /api/v1/session/schedule",
                    "POST /api/v1/session/start",
                ], "the schedule has to land before start drains it");
            }
        }

        [Fact]
        public async Task AFailedSchedulePushDoesNotStartTheRun()
        {
            // Starting anyway would run whatever stale or empty schedule the node still had -- which
            // looks like success and images the wrong thing all night.
            var (mirror, handler) = BuildMirror(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/schedule", StringComparison.Ordinal)
                    ? Conflict("Cannot change the schedule while a session is running")
                    : Ok("started"));

            await using (mirror)
            {
                var result = await mirror.StartAsync(OneObservation(), profileId: null, TestContext.Current.CancellationToken);

                result.IsSuccess.ShouldBeFalse();
                result.Error.ShouldBe("Cannot change the schedule while a session is running");
                handler.Requests.ShouldNotContain("POST /api/v1/session/start");
            }
        }

        [Fact]
        public async Task StartingWithNoScheduleSkipsThePushEntirely()
        {
            // "Just run the node's own plan" is a legitimate ask; posting an empty array would clear it.
            var (mirror, handler) = BuildMirror(_ => Ok("started"));

            await using (mirror)
            {
                await mirror.StartAsync([], profileId: null, TestContext.Current.CancellationToken);

                handler.Requests.ShouldBe(["POST /api/v1/session/start"]);
            }
        }

        [Fact]
        public async Task TheNodesOwnRefusalIsSurfacedVerbatim()
        {
            // The node owns the rules (409 while running, its ProfileSwitchGate, device ownership). A
            // client that reworded them would eventually disagree with the rig about the rig.
            var (mirror, _) = BuildMirror(_ => Conflict("A session is already running"));

            await using (mirror)
            {
                var result = await mirror.AbortAsync(TestContext.Current.CancellationToken);

                result.Error.ShouldBe("A session is already running");
                result.StatusCode.ShouldBe(409);
            }
        }

        [Fact]
        public async Task AbortAndFlatsReachTheirEndpoints()
        {
            var (mirror, handler) = BuildMirror(_ => Ok("ok"));

            await using (mirror)
            {
                await mirror.AbortAsync(TestContext.Current.CancellationToken);
                await mirror.StartFlatsAsync(new FlatsRequestDto(), profileId: null, TestContext.Current.CancellationToken);
                await mirror.ClearScheduleAsync(TestContext.Current.CancellationToken);

                handler.Requests.ShouldBe([
                    "POST /api/v1/session/abort",
                    "POST /api/v1/session/flats",
                    "DELETE /api/v1/session/schedule",
                ]);
            }
        }
    }
}
