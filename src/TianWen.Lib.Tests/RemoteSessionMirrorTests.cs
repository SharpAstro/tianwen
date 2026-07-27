using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="RemoteSessionMirror"/> and <see cref="TianWenNodeClient"/> against a scripted
/// <see cref="HttpMessageHandler"/> -- no server, no sockets, no wall-clock waits.
/// <para>
/// Two properties matter most and are each pinned directly: a mirror must be a <b>faithful</b>
/// <see cref="ISessionTelemetry"/> (so the Live Session tab renders a rig unchanged), and it must
/// distinguish <b>"node idle"</b> from <b>"node unreachable"</b> -- conflating those would report a
/// powered-off rig as idle all night, or blank the tab on a one-poll network blip.
/// </para>
/// </summary>
public class RemoteSessionMirrorTests
{
    // -------------------------------------------------------------------------------------------
    // Scripted transport
    // -------------------------------------------------------------------------------------------

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json<T>(ResponseEnvelope<T> envelope, HttpStatusCode status = HttpStatusCode.OK)
    {
        // Serialize through the SHARED source-generated context, which is the point of the contracts
        // split: if a DTO shape drifts, this round-trip breaks rather than the client silently reading
        // nulls off a hand-copied type.
        var json = JsonSerializer.Serialize(envelope, typeof(ResponseEnvelope<T>), HostingJsonContext.Default);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static (RemoteSessionMirror Mirror, ScriptedHandler Handler) BuildMirror(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new ScriptedHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://rig.local:1888/") };
        var client = new TianWenNodeClient(http);
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero));
        var events = new TianWenEventStream(http.BaseAddress, timeProvider, NullLogger.Instance);
        return (new RemoteSessionMirror(client, events, timeProvider, NullLogger.Instance), handler);
    }

    // -------------------------------------------------------------------------------------------
    // Sample state
    // -------------------------------------------------------------------------------------------

    /// <summary>One OTA's camera state per index. Extracted so <paramref name="count"/> can vary without
    /// a second hand-written DTO going stale the next time a required member is added.</summary>
    private static ImmutableArray<OtaCameraStateDto> SampleCameras(int count)
    {
        var builder = ImmutableArray.CreateBuilder<OtaCameraStateDto>(count);
        for (var i = 0; i < count; i++)
        {
            builder.Add(new OtaCameraStateDto
            {
                OtaIndex = i,
                CameraName = $"Fake Camera {i + 1} (IMX294C)",
                HasFocuser = true,
                HasFilterWheel = false,
                State = nameof(CameraState.Exposing),
                ExposureStart = new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero),
                SubExposureSeconds = 120,
                FrameNumber = 7,
                FilterName = "L",
                FocusPosition = 980,
                FocuserTemperature = 15.0,
                FocuserIsMoving = false,
                StarCount = 412,
                MedianHfd = 3.1f,
                MedianFwhm = 2.4f,
                SensorTemperatureC = -9.8,
                SetpointTemperatureC = -10.0,
                CoolerPowerPercent = 41.5,
            });
        }
        return builder.MoveToImmutable();
    }

    /// <summary>Shared with <see cref="RemoteSessionMirrorDriveTests"/> so one sample shape serves both
    /// suites and a newly-required DTO member breaks in exactly one place.</summary>
    internal static SessionStateDto RunningState(
        SessionPhase phase = SessionPhase.Observing,
        string? guiderState = "Guiding",
        int otaCount = 1,
        PendingPromptDto? pendingPrompt = null) => new SessionStateDto
        {
            Phase = phase,
            CurrentActivity = "Imaging M42 (3/12)",
            FailureReason = null,
            TotalFramesWritten = 7,
            TotalExposureTimeSeconds = 840,
            CurrentObservationIndex = 0,
            ActiveTargetName = "M42",
            LastFramePath = @"C:\Data\2026-07-26\M42\Light\frame7.fits",
            Mount = new MountStateDto
            {
                RightAscension = 5.588,
                Declination = -5.39,
                HourAngle = -0.75,
                PierSide = nameof(PointingState.ThroughThePole),
                IsSlewing = false,
                IsTracking = true,
            },
            MountDisplayName = "Fake Mount (SkyWatcher)",
            Guider = new GuiderStateDto
            {
                State = guiderState,
                TotalRMS = 0.42,
                RaRMS = 0.31,
                DecRMS = 0.28,
                PeakRa = 0.9,
                PeakDec = 0.7,
                GuideExposureSeconds = 2.5,
                RecentSteps =
                [
                    new GuideStepDto
                    {
                        Timestamp = new DateTimeOffset(2026, 7, 26, 20, 0, 1, TimeSpan.Zero),
                        RaError = 0.2, DecError = -0.1, RaCorrectionMs = 120, DecCorrectionMs = -80,
                        IsDither = false, IsSettling = false,
                    },
                ],
            },
            Cameras = SampleCameras(otaCount),
            PendingPrompt = pendingPrompt,
            Observations =
            [
                new ObservationDto
                {
                    TargetName = "M42", TargetRA = 5.588, TargetDec = -5.39,
                    Start = new DateTimeOffset(2026, 7, 26, 19, 45, 0, TimeSpan.Zero),
                    DurationMinutes = 60, AcrossMeridian = false,
                },
            ],
            PhaseTimeline =
            [
                new PhaseTimestampDto { Phase = SessionPhase.Cooling, StartTime = new DateTimeOffset(2026, 7, 26, 19, 30, 0, TimeSpan.Zero) },
                new PhaseTimestampDto { Phase = SessionPhase.Observing, StartTime = new DateTimeOffset(2026, 7, 26, 19, 45, 0, TimeSpan.Zero) },
            ],
            CoolingSamples =
            [
                new CoolingSampleDto
                {
                    Timestamp = new DateTimeOffset(2026, 7, 26, 19, 35, 0, TimeSpan.Zero),
                    CameraIndex = 0, TemperatureC = -9.8, SetpointTemperatureC = -10.0, CoolerPowerPercent = 41.5,
                },
            ],
            FocusHistory =
            [
                new FocusRunDto
                {
                    Timestamp = new DateTimeOffset(2026, 7, 26, 19, 40, 0, TimeSpan.Zero),
                    OtaName = "OTA 1", FilterName = "L", BestPosition = 980, BestHfd = 2.9f,
                    Curve =
                    [
                        new FocusSampleDto { Position = 940, Hfd = 4.1f },
                        new FocusSampleDto { Position = 980, Hfd = 2.9f },
                        new FocusSampleDto { Position = 1020, Hfd = 4.3f },
                    ],
                    FitA = 1.5, FitB = 40.0,
                },
            ],
            ActiveFocusSamples = [],
            ExposureLog =
            [
                new ExposureLogDto
                {
                    Timestamp = new DateTimeOffset(2026, 7, 26, 19, 47, 0, TimeSpan.Zero),
                    TargetName = "M42", FilterName = "L", ExposureSeconds = 120,
                    FrameNumber = 1, MedianHfd = 3.2f, StarCount = 400,
                },
            ],
        };

    // -------------------------------------------------------------------------------------------
    // Faithful projection
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task PollProjectsTheWholeStateOntoISessionTelemetry()
    {
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState())));
        await using var _mirror = mirror;

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        mirror.LastError.ShouldBeNull();
        mirror.IsNodeReachable.ShouldBeTrue();
        ISessionTelemetry telemetry = mirror;

        telemetry.Phase.ShouldBe(SessionPhase.Observing);
        telemetry.CurrentActivity.ShouldBe("Imaging M42 (3/12)");
        telemetry.FailureReason.ShouldBeNull();
        telemetry.TotalFramesWritten.ShouldBe(7);
        telemetry.TotalExposureTime.ShouldBe(TimeSpan.FromMinutes(14));
        telemetry.CurrentObservationIndex.ShouldBe(0);
        telemetry.LastFramePath.ShouldNotBeNull();

        // Mount, including the pier side that crosses the wire as a string.
        telemetry.MountDisplayName.ShouldBe("Fake Mount (SkyWatcher)");
        var mount = telemetry.MountState;
        mount.RightAscension.ShouldBe(5.588, 1e-9);
        mount.Declination.ShouldBe(-5.39, 1e-9);
        mount.HourAngle.ShouldBe(-0.75, 1e-9);
        mount.PierSide.ShouldBe(PointingState.ThroughThePole);
        mount.IsTracking.ShouldBeTrue();
        mount.IsSlewing.ShouldBeFalse();

        // The display facts that let the OTA column render (P3.1's TelescopeDisplayInfo, now on the wire).
        telemetry.TelescopeDisplays.Length.ShouldBe(1);
        telemetry.TelescopeDisplays[0].CameraName.ShouldBe("Fake Camera 1 (IMX294C)");
        telemetry.TelescopeDisplays[0].HasFocuser.ShouldBeTrue();
        telemetry.TelescopeDisplays[0].HasFilterWheel.ShouldBeFalse();

        telemetry.CameraStates.Length.ShouldBe(1);
        var camera = telemetry.CameraStates[0];
        camera.State.ShouldBe(CameraState.Exposing);
        camera.SubExposure.ShouldBe(TimeSpan.FromSeconds(120));
        camera.FrameNumber.ShouldBe(7);
        camera.FilterName.ShouldBe("L");
        camera.FocusPosition.ShouldBe(980);
        camera.FocuserTemperature.ShouldBe(15.0, 1e-9);

        telemetry.LastFrameMetrics.Length.ShouldBe(1);
        telemetry.LastFrameMetrics[0].StarCount.ShouldBe(412);
        telemetry.LastFrameMetrics[0].MedianHfd.ShouldBe(3.1f, 1e-5f);

        telemetry.LastGuideStats.ShouldNotBeNull().TotalRMS.ShouldBe(0.42, 1e-9);
        telemetry.GuideExposure.ShouldBe(TimeSpan.FromSeconds(2.5));
        telemetry.GuiderState.ShouldBe("Guiding");
        telemetry.GuideSamples.Length.ShouldBe(1);
        telemetry.GuideSamples[0].RaError.ShouldBe(0.2, 1e-9);
        telemetry.GuideSamples[0].RaCorrectionMs.ShouldBe(120, 1e-9);

        telemetry.Observations.Count.ShouldBe(1);
        telemetry.Observations[0].Target.Name.ShouldBe("M42");
        telemetry.Observations[0].Duration.ShouldBe(TimeSpan.FromHours(1));
        telemetry.ActiveObservation.ShouldNotBeNull().Target.Name.ShouldBe("M42");

        telemetry.PhaseTimeline.Length.ShouldBe(2);
        telemetry.PhaseTimeline[1].Phase.ShouldBe(SessionPhase.Observing);
    }

    [Fact]
    public async Task GuideStatsAreNullBeforeTheGuiderHasProducedAny()
    {
        // An all-zero guider block is "no stats yet", not a flawless 0.00" RMS.
        var state = RunningState();
        var zeroed = new SessionStateDto
        {
            Phase = state.Phase, CurrentActivity = state.CurrentActivity, FailureReason = null,
            TotalFramesWritten = 0, TotalExposureTimeSeconds = 0, CurrentObservationIndex = -1,
            ActiveTargetName = null, LastFramePath = null,
            Mount = state.Mount, MountDisplayName = state.MountDisplayName,
            Guider = new GuiderStateDto
            {
                State = "Looping", TotalRMS = 0, RaRMS = 0, DecRMS = 0, PeakRa = 0, PeakDec = 0,
                GuideExposureSeconds = 2, RecentSteps = [],
            },
            Cameras = [], Observations = [], PhaseTimeline = [],
            CoolingSamples = [], FocusHistory = [], ActiveFocusSamples = [], ExposureLog = [],
        };

        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(zeroed)));
        await using var _mirror = mirror;

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        mirror.LastGuideStats.ShouldBeNull();
        mirror.GuiderState.ShouldBe("Looping");
    }

    [Fact]
    public async Task WithNoSnapshotEveryFieldReadsAsAnUnstartedSession()
    {
        // Nothing polled yet: the tabs must see exactly what a fresh local session looks like, so no
        // consumer needs a null check the local path does not need.
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState())));
        await using var _mirror = mirror;

        ISessionTelemetry telemetry = mirror;
        telemetry.Phase.ShouldBe(SessionPhase.NotStarted);
        telemetry.CurrentObservationIndex.ShouldBe(-1);
        telemetry.ActiveObservation.ShouldBeNull();
        telemetry.TelescopeDisplays.ShouldBeEmpty();
        telemetry.CameraStates.ShouldBeEmpty();
        telemetry.Observations.Count.ShouldBe(0);
        telemetry.MountDisplayName.ShouldBeEmpty();
        double.IsNaN(telemetry.MountState.RightAscension).ShouldBeTrue();
        telemetry.MountState.PierSide.ShouldBe(PointingState.Unknown);
    }

    // -------------------------------------------------------------------------------------------
    // Idle vs unreachable
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A404DropsTheSnapshotButKeepsTheNodeReachable()
    {
        var running = true;
        var (mirror, _) = BuildMirror(_ => running
            ? Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState()))
            : Json(ResponseEnvelope<string>.Fail("No active session", 404)));
        await using var _mirror = mirror;

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
        mirror.HasSession.ShouldBeTrue();

        running = false;
        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        // The run ended: stop rendering it, but the rig is still there and healthy.
        mirror.HasSession.ShouldBeFalse();
        mirror.IsNodeReachable.ShouldBeTrue();
        mirror.LastError.ShouldBeNull();
        mirror.Phase.ShouldBe(SessionPhase.NotStarted);
    }

    [Fact]
    public async Task AnUnreachableNodeKeepsTheLastSnapshotAndFlagsItself()
    {
        var reachable = true;
        var (mirror, _) = BuildMirror(_ => reachable
            ? Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState()))
            : throw new HttpRequestException("No such host is known"));
        await using var _mirror = mirror;

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        reachable = false;
        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        // A network blip must leave the last known state on screen, flagged stale -- not blank the tab
        // and not claim the session ended.
        mirror.HasSession.ShouldBeTrue();
        mirror.Phase.ShouldBe(SessionPhase.Observing);
        mirror.IsNodeReachable.ShouldBeFalse();
        mirror.LastError.ShouldNotBeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task PhaseChangeIsRaisedOncePerTransition()
    {
        var phase = SessionPhase.Cooling;
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState(phase))));
        await using var _mirror = mirror;

        var transitions = new List<(SessionPhase From, SessionPhase To)>();
        mirror.PhaseChanged += (_, e) => transitions.Add((e.OldPhase, e.NewPhase));

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
        await mirror.PollOnceAsync(TestContext.Current.CancellationToken); // same phase: no event
        phase = SessionPhase.Observing;
        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        transitions.ShouldBe([(SessionPhase.NotStarted, SessionPhase.Cooling), (SessionPhase.Cooling, SessionPhase.Observing)]);
    }

    [Fact]
    public async Task GuiderStateChangeIsDerivedFromConsecutivePolls()
    {
        // The node has no GUIDER-STATE-CHANGED broadcast yet, so the mirror diffs it rather than
        // leaving star-loss invisible to a remote watcher.
        var guiderState = "Guiding";
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(
            RunningState(guiderState: guiderState))));
        await using var _mirror = mirror;

        var changes = new List<(string? From, string To)>();
        mirror.GuiderStateChanged += (_, e) => changes.Add((e.OldState, e.NewState ?? ""));

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
        guiderState = "LostLock";
        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
        await mirror.PollOnceAsync(TestContext.Current.CancellationToken); // unchanged: no event

        changes.ShouldBe([(null, "Guiding"), ("Guiding", "LostLock")]);
    }

    [Fact]
    public async Task FrameWrittenEventFeedsBothTheEventAndTheExposureLog()
    {
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState())));
        await using var _mirror = mirror;

        var written = new List<ExposureLogEntry>();
        mirror.FrameWritten += (_, e) => written.Add(e.Entry);

        mirror.OnNodeEvent(this, new WebSocketEventDto
        {
            Event = "FRAME-WRITTEN",
            Data = new Dictionary<string, object?>
            {
                ["TargetName"] = "M42",
                ["FilterName"] = "L",
                ["ExposureSeconds"] = 120.0,
                ["FrameNumber"] = 8,
                ["MedianHfd"] = 3.05,
                ["StarCount"] = 420,
            },
        });

        written.Count.ShouldBe(1);
        written[0].TargetName.ShouldBe("M42");
        written[0].FilterName.ShouldBe("L");
        written[0].Exposure.ShouldBe(TimeSpan.FromSeconds(120));
        written[0].FrameNumber.ShouldBe(8);
        written[0].StarCount.ShouldBe(420);

        // The broadcast fires the notification ONLY. The collection is not event-sourced: it comes from
        // the polled snapshot, so a frame announced but not yet polled is absent here by design.
        mirror.ExposureLog.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheExposureLogComesFromTheSnapshotSoItCoversFramesWrittenBeforeAttaching()
    {
        // The point of sourcing it from the poll rather than from FRAME-WRITTEN: a client attaching
        // mid-night must see the frames it was never broadcast, instead of an empty list beside a
        // non-zero TotalFramesWritten.
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState())));
        await using var _mirror = mirror;

        mirror.ExposureLog.ShouldBeEmpty();

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        mirror.ExposureLog.Length.ShouldBe(1);
        mirror.ExposureLog[0].TargetName.ShouldBe("M42");
        mirror.ExposureLog[0].FrameNumber.ShouldBe(1);
        mirror.ExposureLog[0].Exposure.ShouldBe(TimeSpan.FromSeconds(120));
        mirror.ExposureLog[0].StarCount.ShouldBe(400);
    }

    [Fact]
    public async Task PollProjectsTheDeepTelemetryAddedForARemoteVCurveAndCoolingChart()
    {
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState())));
        await using var _mirror = mirror;

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);
        ISessionTelemetry telemetry = mirror;

        telemetry.CoolingSamples.Length.ShouldBe(1);
        telemetry.CoolingSamples[0].CameraIndex.ShouldBe(0);
        telemetry.CoolingSamples[0].TemperatureC.ShouldBe(-9.8);
        telemetry.CoolingSamples[0].SetpointTempC.ShouldBe(-10.0);
        telemetry.CoolingSamples[0].CoolerPowerPercent.ShouldBe(41.5);

        telemetry.FocusHistory.Length.ShouldBe(1);
        var run = telemetry.FocusHistory[0];
        run.OtaName.ShouldBe("OTA 1");
        run.BestPosition.ShouldBe(980);
        run.BestHfd.ShouldBe(2.9f);
        // The whole V-curve travels, not just the best point -- otherwise a remote focus chart has
        // nothing to draw.
        run.Curve.Length.ShouldBe(3);
        run.Curve[1].Position.ShouldBe(980);
        run.Curve[1].Hfd.ShouldBe(2.9f);
        run.FitA.ShouldBe(1.5);
        run.FitB.ShouldBe(40.0);

        telemetry.ActiveFocusSamples.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlateSolveEventFeedsTheHistory()
    {
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState())));
        await using var _mirror = mirror;

        var solves = new List<PlateSolveRecord>();
        mirror.PlateSolveCompleted += (_, e) => solves.Add(e.Record);

        mirror.OnNodeEvent(this, new WebSocketEventDto
        {
            Event = "PLATE-SOLVE-COMPLETED",
            Data = new Dictionary<string, object?>
            {
                ["Context"] = nameof(PlateSolveContext.Centering),
                ["OtaName"] = "OTA 1",
                ["Succeeded"] = true,
                ["ElapsedMs"] = 1234.5,
                ["DetectedStars"] = 500,
                ["MatchedStars"] = 44,
            },
        });

        solves.Count.ShouldBe(1);
        solves[0].Context.ShouldBe(PlateSolveContext.Centering);
        solves[0].OtaName.ShouldBe("OTA 1");
        solves[0].Succeeded.ShouldBeTrue();
        solves[0].Elapsed.ShouldBe(TimeSpan.FromMilliseconds(1234.5));
        solves[0].MatchedStars.ShouldBe(44);
        // No WCS crosses the wire, so the solution stays null rather than being half-invented.
        solves[0].Solution.ShouldBeNull();

        mirror.PlateSolveHistory.Length.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnparseableEventBagIsIgnoredRatherThanCrashingTheStream()
    {
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RunningState())));
        await using var _mirror = mirror;

        // Missing Data, and an unknown event name: both are ordinary forward-compatibility cases (an
        // older client against a newer node), so neither may throw.
        mirror.OnNodeEvent(this, new WebSocketEventDto { Event = "FRAME-WRITTEN", Data = null });
        mirror.OnNodeEvent(this, new WebSocketEventDto { Event = "SOMETHING-NEW", Data = null });

        mirror.ExposureLog.ShouldBeEmpty();
        mirror.PlateSolveHistory.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------------------------
    // Client transport
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClientSurfacesTheGatesOwn409WordingVerbatim()
    {
        // The node applies ProfileSwitchGate and answers 409 with its own explanation; a client that
        // invented its own message would drift from the GUI and TUI wording.
        const string gateMessage = "Cannot switch profile: 2 devices are connected (Mount, Camera)";
        var handler = new ScriptedHandler(_ => Json(ResponseEnvelope<string>.Fail(gateMessage, 409)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://rig.local:1888/") };
        var client = new TianWenNodeClient(http);

        var result = await client.SetActiveProfileAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(409);
        result.Error.ShouldBe(gateMessage);
        handler.LastRequest.ShouldNotBeNull().Method.ShouldBe(HttpMethod.Put);
    }

    [Fact]
    public async Task ClientSeparatesNotFoundFromUnreachable()
    {
        var handler404 = new ScriptedHandler(_ => Json(ResponseEnvelope<string>.Fail("No active session", 404)));
        var client404 = new TianWenNodeClient(new HttpClient(handler404) { BaseAddress = new Uri("http://rig.local:1888/") });
        var notFound = await client404.GetSessionStateAsync(TestContext.Current.CancellationToken);
        notFound.IsSuccess.ShouldBeFalse();
        notFound.IsNotFound.ShouldBeTrue();

        var handlerDown = new ScriptedHandler(_ => throw new HttpRequestException("Connection refused"));
        var clientDown = new TianWenNodeClient(new HttpClient(handlerDown) { BaseAddress = new Uri("http://rig.local:1888/") });
        var down = await clientDown.GetSessionStateAsync(TestContext.Current.CancellationToken);
        down.IsSuccess.ShouldBeFalse();
        down.IsNotFound.ShouldBeFalse();
        down.StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task ClientPostsTargetsThroughTheSharedContract()
    {
        var handler = new ScriptedHandler(_ => Json(ResponseEnvelope<string>.Ok("Target 'M42' added (1 pending)")));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://rig.local:1888/") };
        var client = new TianWenNodeClient(http);

        var result = await client.AddTargetAsync(new PendingTarget("M42", 5.588, -5.39, 30),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull().ShouldContain("M42");
        var request = handler.LastRequest.ShouldNotBeNull();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldNotBeNull().AbsolutePath.ShouldBe("/api/v1/session/targets");
    }

    [Fact]
    public async Task ABodilessErrorResponseReportsTheHttpStatusInsteadOfThrowing()
    {
        // Kestrel's shape when an endpoint throws (exactly what the nina camera-info NaN bug produces).
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        });
        var client = new TianWenNodeClient(new HttpClient(handler) { BaseAddress = new Uri("http://rig.local:1888/") });

        var result = await client.GetSessionStateAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
        result.Error.ShouldNotBeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Server projection round-trip
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheServersOwnProjectionRoundTripsBackIntoAMirror()
    {
        // The tests above hand-build the DTO, which cannot catch a projection that serializes into
        // something no client can read -- exactly the failure mode of a `required` nullable under
        // WhenWritingNull (a healthy session with FailureReason = null produced JSON that threw on
        // read). So drive the REAL server-side projection over a session, push it through the wire
        // format, and require it to arrive intact.
        var session = Substitute.For<ISessionTelemetry>();
        session.Phase.Returns(SessionPhase.Observing);
        session.CurrentActivity.Returns("Imaging M42");
        session.FailureReason.Returns((string?)null);        // the field that broke it
        session.TotalFramesWritten.Returns(7);
        session.TotalExposureTime.Returns(TimeSpan.FromMinutes(14));
        session.CurrentObservationIndex.Returns(0);
        session.LastFramePath.Returns((string?)null);        // another optional left unset
        session.MountDisplayName.Returns("Fake Mount (SkyWatcher)");
        session.MountState.Returns(new MountState(5.588, -5.39, -0.75, PointingState.Normal, false, true));
        session.TelescopeDisplays.Returns(
            [new TelescopeDisplayInfo("Fake Camera 1 (IMX294C)", HasFocuser: true, HasFilterWheel: false)]);
        session.CameraStates.Returns(
            [new CameraExposureState(0, new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(120), 7, "L", 980, CameraState.Exposing, 15.0, false)]);
        session.LastFrameMetrics.Returns([new FrameMetrics(412, 3.1f, 2.4f, TimeSpan.FromSeconds(120), 100)]);
        session.Observations.Returns(new ScheduledObservationTree(
            [new ScheduledObservation(new Target(5.588, -5.39, "M42", null),
                new DateTimeOffset(2026, 7, 26, 19, 45, 0, TimeSpan.Zero), TimeSpan.FromHours(1),
                AcrossMeridian: false, FilterPlan: [], Gain: null, Offset: null)]));
        session.PhaseTimeline.Returns(
            [new PhaseTimestamp(SessionPhase.Observing, new DateTimeOffset(2026, 7, 26, 19, 45, 0, TimeSpan.Zero))]);
        session.GuideSamples.Returns([]);
        session.GuiderState.Returns("Guiding");
        session.GuideExposure.Returns(TimeSpan.FromSeconds(2.5));

        var projected = SessionStateDto.FromSession(session);

        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(projected)));
        await using var _mirror = mirror;

        await mirror.PollOnceAsync(TestContext.Current.CancellationToken);

        mirror.LastError.ShouldBeNull();
        mirror.Phase.ShouldBe(SessionPhase.Observing);
        mirror.CurrentActivity.ShouldBe("Imaging M42");
        mirror.FailureReason.ShouldBeNull();
        mirror.TotalFramesWritten.ShouldBe(7);
        mirror.MountDisplayName.ShouldBe("Fake Mount (SkyWatcher)");
        mirror.MountState.RightAscension.ShouldBe(5.588, 1e-9);
        mirror.TelescopeDisplays.ShouldBe(session.TelescopeDisplays);
        mirror.CameraStates.Length.ShouldBe(1);
        mirror.CameraStates[0].FilterName.ShouldBe("L");
        mirror.Observations.Count.ShouldBe(1);
        mirror.ActiveObservation.ShouldNotBeNull().Target.Name.ShouldBe("M42");
    }

    // -------------------------------------------------------------------------------------------
    // Event-stream URI derivation
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://rig.local:1888/", "ws://rig.local:1888/api/v1/events")]
    [InlineData("http://192.168.1.50:1888", "ws://192.168.1.50:1888/api/v1/events")]
    [InlineData("https://rig.local:1888/", "wss://rig.local:1888/api/v1/events")]
    public void EventUriIsDerivedFromTheNodeRoot(string baseAddress, string expected) =>
        TianWenEventStream.BuildEventUri(new Uri(baseAddress)).ToString().ShouldBe(expected);
}
