using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Sequencing;
using Xunit;
using GuiderStateChangedEventArgs = TianWen.Lib.Sequencing.GuiderStateChangedEventArgs;

namespace TianWen.Lib.Tests;

/// <summary>
/// Every event the node broadcasts serialises, through both JSON contexts, as the hub sends it (P0b item 16,
/// #752). A payload holds <c>object</c> values, so the source-generated serialiser needs metadata for each
/// value's RUNTIME type, and nothing checks that until the event is sent: <c>SCOUT-COMPLETED</c> carried an
/// <c>int[]</c> neither context knew and never went out at all. An event added to the broadcaster is added
/// to <see cref="BroadcastEvents"/> and here.
/// </summary>
public class BroadcastEventSerializationTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 25, 11, 30, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> EveryEvent() =>
    [
        [BroadcastEvents.GuideStep(new GuideErrorSample(Now, 0.4, -0.3, 120, -80, IsDither: false, IsSettling: true))],
        [BroadcastEvents.EnhanceProgress(new EnhanceProgress("Denoise", 1, 3, 0.5f, 42.0))],
        [BroadcastEvents.EnhanceCompleted(new EnhanceJobCompletedEventArgs("in.fits", "out.fits", succeeded: true, error: null))],
        [BroadcastEvents.PhaseChanged(new SessionPhaseChangedEventArgs(SessionPhase.Cooling, SessionPhase.RoughFocus))],
        [BroadcastEvents.FrameWritten(new ExposureLogEntry(Now, "NGC 7293", "L", TimeSpan.FromSeconds(320), 1, 2.4f, 33))],
        [BroadcastEvents.PlateSolveCompleted(new PlateSolveRecord(Now, PlateSolveContext.Centering, "OTA 1", Succeeded: true, Solution: null, TimeSpan.FromMilliseconds(434), 33, 29))],
        [BroadcastEvents.ScoutCompleted(new ScoutCompletedEventArgs(new Target(22.49, -20.8, "NGC 7293", null),
            ScoutClassification.Healthy, estimatedClearIn: TimeSpan.FromMinutes(5), ScoutOutcome.Proceed, [19, 37]))],
        [BroadcastEvents.GuiderStateChanged(new GuiderStateChangedEventArgs("Calibrating", "Guiding"))],
        [BroadcastEvents.PromptRequested(new SessionPromptEventArgs("Panel", "Switch the panel on", "Continue", "Cancel",
            new TaskCompletionSource<bool>(), defaultIfUnanswerable: false, raisedUtc: Now))],
        [BroadcastEvents.Notification(new NotificationDto { Severity = "Info", Message = "Cooling -> RoughFocus", TimestampUtc = Now })],
        [BroadcastEvents.JobProgress(new JobDto { Id = "7f3c", Kind = "discover", State = JobState.Failed, Step = "Discovering devices", Error = "the serial sweep failed", StartedUtc = Now, EndedUtc = Now })],
    ];

    [Theory]
    [MemberData(nameof(EveryEvent))]
    public void TheNativeSocketCanSendIt(WebSocketEventDto dto)
    {
        var envelope = new ResponseEnvelope<WebSocketEventDto>(dto, "", 200, true, "Socket");

        var json = JsonSerializer.Serialize(envelope, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto);

        JsonSerializer.Deserialize(json, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto)
            .ShouldNotBeNull().Response.ShouldNotBeNull().Event.ShouldBe(dto.Event);
    }

    [Theory]
    [MemberData(nameof(EveryEvent))]
    public void TheNinaSocketCanSendIt(WebSocketEventDto dto)
    {
        var envelope = new ResponseEnvelope<WebSocketEventDto>(dto, "", 200, true, "Socket");

        Should.NotThrow(() => JsonSerializer.Serialize(envelope, NinaApiJsonContext.Default.ResponseEnvelopeWebSocketEventDto));
    }
}
