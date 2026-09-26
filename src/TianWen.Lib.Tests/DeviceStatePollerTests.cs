using Shouldly;
using System;
using System.Text.Json;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The node's device plane read side (P2 part 1 of docs/plans/hardware-in-the-server.md, #929): the cadences it reads at,
/// the GUI's, and what a client makes of a <c>DEVICE-STATE</c> push. <c>DeviceStateTests</c> drives a real node.
/// </summary>
public class DeviceStatePollerTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static DeviceStateDto State(DeviceType type, FocuserDeviceStateDto? focuser = null, MountDeviceStateDto? mount = null,
        CoverDeviceStateDto? cover = null) => new DeviceStateDto
    {
        DeviceUri = $"{type}://FakeDevice/1",
        DeviceType = type,
        Connected = true,
        Focuser = focuser,
        Mount = mount,
        Cover = cover,
    };

    private static CoverDeviceStateDto Cover(CoverStatus flap) => new CoverDeviceStateDto
    {
        CoverState = flap,
        CalibratorState = CalibratorStatus.Off,
        CanControlBrightness = true,
    };

    private static MountDeviceStateDto Mount(bool slewing, bool tracking) => new MountDeviceStateDto
    {
        PierSide = PointingState.Normal,
        IsSlewing = slewing,
        IsTracking = tracking,
    };

    [Theory]
    [InlineData(DeviceType.Camera)]
    [InlineData(DeviceType.Focuser)]
    [InlineData(DeviceType.FilterWheel)]
    [InlineData(DeviceType.Mount)]
    [InlineData(DeviceType.CoverCalibrator)]
    public void WithNobodyWatchingEveryDeviceIsReadAtTheSlowCadence(DeviceType type)
    {
        var clock = new FakeTimeProviderWrapper(Now);
        DeviceStatePoller.Interval(type, State(type, mount: Mount(slewing: true, tracking: false), cover: Cover(CoverStatus.Moving)), 0, watched: false, clock.GetTimestamp(), clock)
            .ShouldBe(DeviceStatePoller.Unwatched);
    }

    [Fact]
    public void WatchedDevicesAreReadAtTheGuisCadences()
    {
        var clock = new FakeTimeProviderWrapper(Now);
        var now = clock.GetTimestamp();
        TimeSpan Interval(DeviceStateDto state, long steadySince = 0) => DeviceStatePoller.Interval(state.DeviceType, state, steadySince, watched: true, now, clock);

        Interval(State(DeviceType.Camera)).ShouldBe(TimeSpan.FromSeconds(2));
        Interval(State(DeviceType.FilterWheel)).ShouldBe(TimeSpan.FromSeconds(2));
        Interval(State(DeviceType.Focuser, new FocuserDeviceStateDto { Position = 1, IsMoving = true })).ShouldBe(TimeSpan.FromSeconds(1), "a moving focuser's readout follows it");
        Interval(State(DeviceType.Focuser, new FocuserDeviceStateDto { Position = 1, IsMoving = false })).ShouldBe(TimeSpan.FromSeconds(2));
        Interval(State(DeviceType.Mount, mount: Mount(slewing: true, tracking: true))).ShouldBe(TimeSpan.FromMilliseconds(500), "a slew keeps up with visible motion");
        Interval(State(DeviceType.Mount, mount: Mount(slewing: false, tracking: false))).ShouldBe(TimeSpan.FromSeconds(2), "parked or not tracking");
        Interval(State(DeviceType.CoverCalibrator, cover: Cover(CoverStatus.Moving))).ShouldBe(TimeSpan.FromSeconds(1), "a moving flap is followed as a moving focuser is");
        Interval(State(DeviceType.CoverCalibrator, cover: Cover(CoverStatus.Open))).ShouldBe(TimeSpan.FromSeconds(2));
    }

    // A mount that has just landed and started tracking is read fast for a while, so the reticle follows a finished goto,
    // and at the steady sidereal rate once it has tracked undisturbed that long.
    [Fact]
    public void ATrackingMountIsReadFastUntilItHasSettledThenSlowly()
    {
        var clock = new FakeTimeProviderWrapper(Now);
        var steadySince = clock.GetTimestamp();
        var tracking = State(DeviceType.Mount, mount: Mount(slewing: false, tracking: true));

        DeviceStatePoller.Interval(DeviceType.Mount, tracking, steadySince, watched: true, clock.GetTimestamp(), clock).ShouldBe(TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(10));
        DeviceStatePoller.Interval(DeviceType.Mount, tracking, steadySince, watched: true, clock.GetTimestamp(), clock).ShouldBe(TimeSpan.FromSeconds(10));
    }

    // What a client makes of the push, through the wire exactly as the node sends it.
    [Fact]
    public void ADeviceStatePushReadsBackAsTheStateItCarries()
    {
        var state = new DeviceStateDto
        {
            DeviceUri = "Camera://FakeDevice/1#Fake Camera 1",
            DeviceType = DeviceType.Camera,
            DisplayName = "Fake Camera 1",
            Connected = true,
            LeaseOwner = "Session run",
            ReadUtc = Now,
            Camera = new CameraDeviceStateDto
            {
                CcdTemperatureC = -9.5,
                SetpointC = -10,
                CoolerOn = true,
                State = CameraState.Exposing,
                UsesGainValue = true,
                UsesGainMode = false,
                GainMin = 0,
                GainMax = 300,
                Gain = 100,
                SensorWidth = 6248,
                SensorHeight = 4176,
                CoolerIntent = CoolerIntentKind.Cool,
                CoolerIntentSetpointC = -10,
            },
        };

        var envelope = new ResponseEnvelope<WebSocketEventDto>(BroadcastEvents.DeviceState(state), "", 200, true, "Socket");
        var json = JsonSerializer.Serialize(envelope, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto);
        var received = JsonSerializer.Deserialize(json, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto).ShouldNotBeNull().Response.ShouldNotBeNull();

        DeviceStateDto.TryFromEvent(received, out var read).ShouldBeTrue();
        read.DeviceUri.ShouldBe(state.DeviceUri);
        read.LeaseOwner.ShouldBe("Session run");
        read.ReadUtc.ShouldBe(Now);
        var camera = read.Camera.ShouldNotBeNull();
        camera.CcdTemperatureC.ShouldBe(-9.5);
        camera.HeatsinkTemperatureC.ShouldBeNull("a reading the camera did not give crosses as null, never 0");
        camera.State.ShouldBe(CameraState.Exposing);
        camera.CoolerIntent.ShouldBe(CoolerIntentKind.Cool);
        camera.CoolerIntentSetpointC.ShouldBe(-10);
    }

    [Fact]
    public void AnotherEventIsNoDeviceState()
    {
        DeviceStateDto.TryFromEvent(new WebSocketEventDto { Event = "JOB-PROGRESS" }, out _).ShouldBeFalse();
    }
}
