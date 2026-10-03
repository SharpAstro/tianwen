using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
    /// it: the node's frames, the prompt round-trip, and the start / flats / abort path
    /// (docs/plans/remote-profile.md P3 remainder). <see cref="RemoteSessionMirrorTests"/> covers the
    /// telemetry-fidelity half.
    /// <para>
    /// A frame is written by the <b>real wire writer</b> (<see cref="FrameWire"/>) and read by the real client
    /// path, so a drift in either end fails here rather than showing a black rectangle on a rig.
    /// </para>
    /// </summary>
    public class RemoteSessionMirrorDriveTests : IDisposable
    {
        /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
        private readonly TempFolders _folders = new();

        public void Dispose() => _folders.Dispose();

        // -------------------------------------------------------------------------------------------
        // Scripted transport, routed by path so one handler can serve state + preview + prompt
        // -------------------------------------------------------------------------------------------

        internal sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public List<string> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add($"{request.Method} {request.RequestUri?.PathAndQuery}");
                return Task.FromResult(respond(request));
            }
        }

        internal static HttpResponseMessage Json<T>(ResponseEnvelope<T> envelope, HttpStatusCode status = HttpStatusCode.OK)
        {
            var json = JsonSerializer.Serialize(envelope, typeof(ResponseEnvelope<T>), HostingJsonContext.Default);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static HttpResponseMessage Ok(string message) =>
            Json(ResponseEnvelope<string>.Ok(message));

        private static HttpResponseMessage Conflict(string error) =>
            Json(ResponseEnvelope<string>.Fail(error, statusCode: 409));

        internal static (RemoteSessionMirror Mirror, RoutingHandler Handler) BuildMirror(
            Func<HttpRequestMessage, HttpResponseMessage> respond, bool onThisMachine = false, FakeTimeProviderWrapper? timeProvider = null)
        {
            var handler = new RoutingHandler(respond);
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://rig.local:1888/") };
            var client = new TianWenNodeClient(http);
            timeProvider ??= new FakeTimeProviderWrapper(new DateTimeOffset(2026, 7, 27, 21, 0, 0, TimeSpan.Zero));
            var events = new TianWenEventStream(http.BaseAddress, timeProvider, NullLogger.Instance);
            return (new RemoteSessionMirror(client, events, timeProvider, NullLogger.Instance) { IsOnThisMachine = onThisMachine }, handler);
        }

        /// <summary>
        /// A running-session snapshot with <paramref name="otaCount"/> OTAs, reusing
        /// <see cref="RemoteSessionMirrorTests.RunningState"/> so both suites exercise one sample shape --
        /// a second hand-built DTO would drift from the contract the moment a required member is added.
        /// </summary>
        private static SessionStateDto StateWith(int otaCount = 1, PendingPromptDto? prompt = null) =>
            RemoteSessionMirrorTests.RunningState(otaCount: otaCount, pendingPrompt: prompt);

        /// <summary>A whole-ADU mono frame, as a camera delivers one.</summary>
        internal static Image CameraFrame(float offset = 0f, int width = 64, int height = 48)
        {
            var planes = Image.CreateChannelData(1, height, width);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    planes[0][y, x] = 900f + offset + ((x * 7 + y * 13) % 40);
                }
            }
            planes[0][10, 10] = 48000f;

            return new Image(planes, BitDepth.Int16, maxValue: 48000f, minValue: 900f, pedestal: 0f,
                new ImageMeta { SensorType = SensorType.Monochrome });
        }

        /// <summary>The frame as the node's route writes it.</summary>
        internal static async Task<byte[]> WireAsync(Image frame)
        {
            using var body = new MemoryStream();
            await FrameWire.WriteAsync(frame, body, TestContext.Current.CancellationToken);
            return body.ToArray();
        }

        /// <summary>The route's 200: the frame's bytes, stamped with its number.</summary>
        internal static HttpResponseMessage FrameResponse(byte[] wire, int frameNumber)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wire) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(FrameWire.ContentType);
            response.Headers.Add(TianWenNodeClient.PreviewFrameNumberHeader, frameNumber.ToString());
            return response;
        }

        /// <summary>The route's 204: the slot still shows the frame the client named.</summary>
        internal static HttpResponseMessage Unchanged(int frameNumber)
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.Add(TianWenNodeClient.PreviewFrameNumberHeader, frameNumber.ToString());
            return response;
        }

        internal static bool IsFrameRoute(HttpRequestMessage request) =>
            request.RequestUri is { } uri && uri.AbsolutePath.Contains("/frames/", StringComparison.Ordinal);

        internal static bool Released(Image image)
        {
            if (image.TryLease(out var lease))
            {
                lease.Dispose();
                return false;
            }
            return true;
        }

        // -------------------------------------------------------------------------------------------
        // The node's frames
        // -------------------------------------------------------------------------------------------

        [Fact]
        public async Task FramesAreOffUntilAskedFor()
        {
            // A dashboard watching six rigs wants phase and counters, not six streams of full frames. Frames
            // are by far the most expensive thing on the link, so they must be opt-in.
            var (mirror, handler) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                mirror.LastCapturedImages.ShouldBeEmpty();
                handler.Requests.ShouldNotContain(r => r.Contains("/frames/"));
            }
        }

        [Fact]
        public async Task TheNodesFrameArrivesLinearAndBitExact()
        {
            // The pane stretches, measures and saves what it is handed, so the mirror hands it the node's own
            // frame, never a stretched picture of it.
            var sent = CameraFrame();
            var wire = await WireAsync(sent);
            var (mirror, handler) = BuildMirror(request => IsFrameRoute(request)
                ? FrameResponse(wire, frameNumber: 7)
                : Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                handler.Requests.ShouldContain(r => r.EndsWith("/api/v1/frames/ota/0/latest", StringComparison.Ordinal),
                    "a rig's frame is asked for as bytes: shared memory is for this machine's node");
                var got = mirror.LastCapturedImages.ShouldHaveSingleItem().ShouldNotBeNull();
                got.ChannelCount.ShouldBe(1);
                got.GetChannelSpan(0).SequenceEqual(sent.GetChannelSpan(0)).ShouldBeTrue("the frame must arrive bit for bit");
                got.ImageMeta.SensorType.ShouldBe(SensorType.Monochrome);
                mirror.LastCapturedImageNumber(0).ShouldBe(7);
            }
        }

        [Fact]
        public async Task TheMirrorNamesTheFrameItHoldsAndKeepsItWhileTheNodeStillShowsIt()
        {
            // The held number is the whole reason a 2 Hz poll is affordable: the node answers a 204 with no body
            // while its slot still shows that frame, rather than send a full frame that has not moved.
            var wire = await WireAsync(CameraFrame());
            var bodiesSent = 0;
            var (mirror, handler) = BuildMirror(request =>
            {
                if (!IsFrameRoute(request))
                {
                    return Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith()));
                }
                if (request.RequestUri!.Query.Contains("after=7", StringComparison.Ordinal))
                {
                    return Unchanged(7);
                }
                bodiesSent++;
                return FrameResponse(wire, frameNumber: 7);
            });

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                var first = mirror.LastCapturedImages[0];

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                bodiesSent.ShouldBe(1, "polls 2 and 3 must name frame 7, which the node answers without a body");
                mirror.LastCapturedImages[0].ShouldBeSameAs(first, "and must keep the frame already read");
                first.ShouldNotBeNull();
                Released(first).ShouldBeFalse("a frame still shown is not given back");
            }
        }

        /// <summary>
        /// A mirror of this computer's own node asks for every frame through shared memory, and a frame the node names in a
        /// slot is copied out of it (P4b, #932). A rig's mirror never asks (the test above).
        /// </summary>
        [Fact]
        public async Task AFrameTheNodeNamesInASlotIsCopiedOutOfIt()
        {
            using var writer = new FrameSlotWriter($"tianwen-test-{Guid.NewGuid():N}", Path.GetTempPath());
            var sent = CameraFrame(offset: 3);
            var slot = FrameSlotDto.Of(7, writer.Write(7, sent));
            string? asked = null;
            var (mirror, _) = BuildMirror(onThisMachine: true, respond: request =>
            {
                if (!IsFrameRoute(request))
                {
                    return Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith()));
                }
                asked = request.RequestUri?.Query;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(slot, HostingJsonContext.Default.FrameSlotDto)),
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(FrameStreamWire.SlotContentType);
                response.Headers.Add(TianWenNodeClient.PreviewFrameNumberHeader, "7");
                return response;
            });

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                asked.ShouldNotBeNull().ShouldContain($"{FrameStreamWire.CarrierQuery}={FrameStreamWire.SharedMemory}");
                var shown = mirror.LastCapturedImages[0].ShouldNotBeNull("the frame came out of the slot");
                shown.GetChannelSpan(0).SequenceEqual(sent.GetChannelSpan(0)).ShouldBeTrue("bit for bit the frame the node wrote");
                mirror.FramesFromSharedMemory.ShouldBe(1);
                mirror.LastCapturedImageNumber(0).ShouldBe(7);
            }
        }

        /// <summary>
        /// A frame's planes are the mirror's reader's: a replaced frame gives them back so the next frame is read into
        /// them, but only once its successor is published AND no reader still leases it, which is what keeps a pane
        /// that is copying the old frame from reading planes the next frame is being written into.
        /// </summary>
        [Fact]
        public async Task AReplacedFrameGivesItsPlanesBackOnceItsLastReaderIsDone()
        {
            var wires = new[] { await WireAsync(CameraFrame(offset: 0)), await WireAsync(CameraFrame(offset: 1)) };
            var number = 1;
            var (mirror, _) = BuildMirror(request => IsFrameRoute(request)
                ? FrameResponse(wires[number - 1], number)
                : Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                var first = mirror.LastCapturedImages[0].ShouldNotBeNull();

                // The pane is copying frame 1 as frame 2 lands.
                first.TryLease(out var paneLease).ShouldBeTrue();
                number = 2;
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                var second = mirror.LastCapturedImages[0].ShouldNotBeNull();
                second.ShouldNotBeSameAs(first);
                mirror.LastCapturedImageNumber(0).ShouldBe(2);
                Released(first).ShouldBeTrue("the replaced frame is released once its successor is out");
                mirror.FreeFramePlanes.ShouldBe(0, "but its planes wait for the pane's lease");

                paneLease.Dispose();

                mirror.FreeFramePlanes.ShouldBe(1, "and come back to the reader for the next frame");
            }
        }

        /// <summary>
        /// The guide camera is a separate opt-in from the OTA frames, because the screens that want them are
        /// different: a dashboard draws science frames and never a guide frame, so it must not pay a request per
        /// poll for a picture nothing shows.
        /// </summary>
        [Fact]
        public async Task TheGuideFrameIsOnlyFetchedWhenAskedFor()
        {
            var wire = await WireAsync(CameraFrame());
            var guideNumber = 3;
            var guiderRequests = 0;
            var (mirror, _) = BuildMirror(request =>
            {
                if (!IsFrameRoute(request))
                {
                    return Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith()));
                }

                if (request.RequestUri!.AbsolutePath.EndsWith("/frames/guider/latest", StringComparison.Ordinal))
                {
                    guiderRequests++;
                    return FrameResponse(wire, guideNumber);
                }

                return FrameResponse(wire, frameNumber: 3);
            });

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                guiderRequests.ShouldBe(0, "OTA frames alone must not pull the guide camera");
                mirror.LastGuideFrame.ShouldBeNull();

                mirror.Previews = new PreviewOptions(IncludeGuider: true);
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                guiderRequests.ShouldBe(1);
                var first = mirror.LastGuideFrame.ShouldNotBeNull();

                guideNumber = 4;
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                var second = mirror.LastGuideFrame.ShouldNotBeNull();
                second.ShouldNotBeSameAs(first);
                Released(first).ShouldBeTrue("a replaced guide frame is given back");

                mirror.Previews = new PreviewOptions();
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                mirror.LastGuideFrame.ShouldBeNull("a guide frame no longer asked for is dropped");
                Released(second).ShouldBeTrue("and given back");
            }
        }

        [Fact]
        public async Task AMirrorAskedForNoFramesHoldsNone()
        {
            // A rig taken off screen stops pulling frames, and must not go on holding a full frame per OTA either.
            var wire = await WireAsync(CameraFrame());
            var (mirror, _) = BuildMirror(request => IsFrameRoute(request)
                ? FrameResponse(wire, frameNumber: 3)
                : Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions(IncludeGuider: true);
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                var shown = mirror.LastCapturedImages.ShouldHaveSingleItem().ShouldNotBeNull();
                var guide = mirror.LastGuideFrame.ShouldNotBeNull();

                mirror.Previews = null;
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                mirror.LastCapturedImages.ShouldBeEmpty();
                mirror.LastGuideFrame.ShouldBeNull();
                Released(shown).ShouldBeTrue();
                Released(guide).ShouldBeTrue();
            }
        }

        [Fact]
        public async Task ADisposedMirrorGivesItsFramesBack()
        {
            var wire = await WireAsync(CameraFrame());
            var (mirror, _) = BuildMirror(request => IsFrameRoute(request)
                ? FrameResponse(wire, frameNumber: 3)
                : Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith())));
            mirror.Previews = new PreviewOptions(IncludeGuider: true);
            await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
            var shown = mirror.LastCapturedImages.ShouldHaveSingleItem().ShouldNotBeNull();
            var guide = mirror.LastGuideFrame.ShouldNotBeNull();

            await mirror.DisposeAsync();

            Released(shown).ShouldBeTrue("a mirror disposed gives its frames back");
            Released(guide).ShouldBeTrue("its guide frame too");
        }

        [Fact]
        public async Task AFailingFrameFetchNeverBlanksTheTelemetry()
        {
            // A link too slow or a node too busy for frames must degrade to "no frame", not to a blank Live
            // Session tab.
            var (mirror, _) = BuildMirror(request => IsFrameRoute(request)
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
        public async Task FramesAreDroppedAndGivenBackWhenTheSessionEnds()
        {
            var wire = await WireAsync(CameraFrame());
            var sessionRunning = true;
            var (mirror, _) = BuildMirror(request =>
            {
                if (IsFrameRoute(request))
                {
                    return FrameResponse(wire, frameNumber: 3);
                }

                return sessionRunning
                    ? Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith()))
                    : Json(ResponseEnvelope<SessionStateDto>.NotFound("No session"), HttpStatusCode.NotFound);
            });

            await using (mirror)
            {
                mirror.Previews = new PreviewOptions();
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                var shown = mirror.LastCapturedImages.ShouldHaveSingleItem().ShouldNotBeNull();

                sessionRunning = false;
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                mirror.LastCapturedImages.ShouldBeEmpty("a finished session must stop showing its last frame");
                Released(shown).ShouldBeTrue("and give it back");
            }
        }

        [Fact]
        public async Task ASavedFrameIsAFileHereOnlyWhenTheNodeIsOnThisMachine()
        {
            // The node names the sub it saved by its path. On this machine that IS the file, headers and all; from
            // a node on another machine it names a file there, which a file of the same name here must never be
            // taken for.
            var saved = CameraFrame();
            var path = Path.Combine(_folders.Create("tw-saved").FullName, "frame7.fits");
            saved.WriteToFitsFile(path);
            var state = RemoteSessionMirrorTests.RunningState(lastFramePath: path);

            var (remote, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(state)));
            await using (remote)
            {
                await remote.PollOnceAsync(TestContext.Current.CancellationToken);

                remote.LastFramePath.ShouldBe(path);
                remote.SavedFramePathOnThisMachine.ShouldBeNull("a path from a node over TCP is not a path here");
            }

            var (local, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(state)), onThisMachine: true);
            await using (local)
            {
                await local.PollOnceAsync(TestContext.Current.CancellationToken);

                var here = local.SavedFramePathOnThisMachine.ShouldNotBeNull();
                Image.TryReadFitsFile(here, out var read).ShouldBeTrue();
                read.ShouldNotBeNull().GetChannelSpan(0).SequenceEqual(saved.GetChannelSpan(0)).ShouldBeTrue("the saved sub, as the node wrote it");
            }
        }

        // -------------------------------------------------------------------------------------------
        // Prompt round-trip
        // -------------------------------------------------------------------------------------------

        private static PendingPromptDto ManualPanelPrompt(DateTimeOffset? raisedUtc = null) => new PendingPromptDto
        {
            Title = "Manual flat panel",
            Message = "Switch on the flat panel for OTA 1, then Continue.",
            ContinueLabel = "Continue",
            CancelLabel = "Cancel",
            RequiresPhysicalPresence = true,
            RaisedUtc = raisedUtc,
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
        public async Task APromptCarriesTheInstantTheNodeRaisedIt()
        {
            // How long a prompt has been waiting is the fact that makes it visible as a problem, and only
            // the node knows it -- so it has to survive the wire rather than being re-derived here. This
            // goes through the real HostingJsonContext, which also pins that a NULLABLE timestamp
            // round-trips at all (a nullable wire member that were also required could not).
            var raisedAt = new DateTimeOffset(2026, 7, 27, 20, 20, 0, TimeSpan.Zero);
            var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: ManualPanelPrompt(raisedAt)))));

            await using (mirror)
            {
                SessionPromptEventArgs? raised = null;
                mirror.PromptRequested += (_, e) => raised = e;

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                raised.ShouldNotBeNull();
                raised.RaisedUtc.ShouldBe(raisedAt);
            }
        }

        [Fact]
        public async Task APromptFromANodeThatSendsNoTimestampHasAnUnknownAge()
        {
            // An older node sends no RaisedUtc. The mirror must leave the age unknown rather than stamping
            // "now": dating the prompt from when this client attached would show a rig that has been stuck
            // since dusk as freshly waiting, and would reset on every GUI restart.
            var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: ManualPanelPrompt()))));

            await using (mirror)
            {
                SessionPromptEventArgs? raised = null;
                mirror.PromptRequested += (_, e) => raised = e;

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                raised.ShouldNotBeNull();
                raised.RaisedUtc.ShouldBeNull();
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
        public async Task APromptTheNodeStopsOfferingIsWithdrawnLocally()
        {
            // The node's run moved on: aborted while the prompt waited, or answered from another client. Its
            // snapshot stops carrying the prompt, and a prompt bar still showing it would invite an answer to a
            // question nobody asks (P0b item 13 of docs/plans/hardware-in-the-server.md, #752). Withdrawing is
            // not answering, so nothing goes back to the node.
            PendingPromptDto? current = ManualPanelPrompt();
            var (mirror, handler) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(StateWith(prompt: current))));

            await using (mirror)
            {
                SessionPromptEventArgs? raised = null;
                mirror.PromptRequested += (_, e) => raised = e;

                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
                var prompt = raised.ShouldNotBeNull();
                prompt.Settled.IsCompleted.ShouldBeFalse("the node still offers it");

                current = null;
                await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

                await prompt.Settled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                handler.Requests.ShouldNotContain(r => r.Contains("/prompt/respond"));
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
                var result = await mirror.StartAsync(OneObservation(), profileId: null, configuration: null, TestContext.Current.CancellationToken);

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
                var result = await mirror.StartAsync(OneObservation(), profileId: null, configuration: null, TestContext.Current.CancellationToken);

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
                await mirror.StartAsync([], profileId: null, configuration: null, TestContext.Current.CancellationToken);

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
