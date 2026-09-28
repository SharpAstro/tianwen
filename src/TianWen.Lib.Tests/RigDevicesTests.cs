using NSubstitute;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Text.Json;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An idle rig's view reads its devices from its node (P5b part 9 of docs/plans/hardware-in-the-server.md, #935): a
/// device's state reads back as the reading it was made from, across the wire, and the rig's profile's OTAs and mount are
/// laid out from them as this computer's are from its own (<see cref="RigDevices"/>).
/// </summary>
public class RigDevicesTests
{
    private static T AcrossTheWire<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(value, typeInfo), typeInfo).ShouldNotBeNull();

    [Fact]
    public void ADeviceStateReadsBackAcrossTheWireAsTheReadingItWasMadeFrom()
    {
        var camera = new CameraReading(-10.5, double.NaN, -10, 42, CoolerOn: true, CameraState.Idle, UsesGainValue: true,
            UsesGainMode: false, GainMin: 0, GainMax: 300, Gain: 120, GainModes: ["Low", "High"], SensorWidth: 4144,
            SensorHeight: 2822, new RoiConstraints(4144, 2822, 64, 64, 8, 2, 4, 2),
            VideoBitDepths: [BitDepth.Int8, BitDepth.Int16], CanFastReadout: true);
        var focuser = new FocuserReading(12345, double.NaN, IsMoving: true);
        var filterWheel = new FilterWheelReading(2, "Ha");
        var mount = new MountState(5.5, 20.25, -0.75, PointingState.ThroughThePole, IsSlewing: false, IsTracking: true, 5.49, 20.2);

        var device = AcrossTheWire(new DeviceStateDto
        {
            DeviceUri = "Camera://FakeDevice/cam1",
            DeviceType = DeviceType.Camera,
            Connected = true,
            Camera = CameraDeviceStateDto.FromReading(camera, intent: null),
            Focuser = FocuserDeviceStateDto.FromReading(focuser),
            FilterWheel = FilterWheelDeviceStateDto.FromReading(filterWheel),
            Mount = MountDeviceStateDto.FromState(mount, verdict: null),
        }, HostingJsonContext.Default.DeviceStateDto);

        var cameraBack = device.Camera.ShouldNotBeNull().ToReading();
        cameraBack.GainModes.ShouldBe(["Low", "High"]);
        cameraBack.VideoBitDepths.ShouldBe([BitDepth.Int8, BitDepth.Int16], "what the planetary panel may offer");
        (cameraBack with { GainModes = camera.GainModes, VideoBitDepths = camera.VideoBitDepths }).ShouldBe(camera,
            "every field, a NaN heatsink, the ROI rules and the high-speed readout included");
        device.Focuser.ShouldNotBeNull().ToReading().ShouldBe(focuser);
        device.FilterWheel.ShouldNotBeNull().ToReading().ShouldBe(filterWheel);
        device.Mount.ShouldNotBeNull().ToState().ShouldBe(mount);
    }

    [Fact]
    public void ACameraWithNoRulesReadsBackWithNone()
    {
        var reading = new CameraReading(double.NaN, double.NaN, double.NaN, double.NaN, false, CameraState.Idle, false, false,
            0, 0, 0, [], 0, 0, default);

        var dto = CameraDeviceStateDto.FromReading(reading, intent: null);

        dto.Roi.ShouldBeNull();
        dto.ToReading().RoiConstraints.ShouldBe(default(RoiConstraints));
    }

    private static DeviceStateDto Held(DeviceBase device, bool connected = true) => new DeviceStateDto
    {
        DeviceUri = device.DeviceUri.ToString(),
        DeviceType = device.DeviceType,
        DisplayName = device.DisplayName,
        Connected = connected,
    };

    private static CameraDeviceStateDto CameraAt(double celsius) => CameraDeviceStateDto.FromReading(
        new CameraReading(celsius, 25, double.NaN, 0, false, CameraState.Idle, true, false, 0, 100, 50, [], 1920, 1080, default),
        intent: null);

    [Fact]
    public void ARigsOtasAreItsProfilesMatchedToWhatItsNodeHoldsConnected()
    {
        var mainCamera = new FakeDevice(DeviceType.Camera, 1);
        var focuser = new FakeDevice(DeviceType.Focuser, 1);
        var wideCamera = new FakeDevice(DeviceType.Camera, 2);
        var mount = new FakeDevice(DeviceType.Mount, 1);
        var profile = new ProfileData(mount.DeviceUri, NoneDevice.Instance.DeviceUri,
        [
            new OTAData("Main", 800, mainCamera.DeviceUri, null, focuser.DeviceUri, null, null, null),
            new OTAData("Wide", 250, wideCamera.DeviceUri, null, null, null, null, null),
        ]);
        // The node's URI for the main camera carries a query the profile's does not: the hub's identity rule ignores it.
        var nodesMainCamera = new UriBuilder(mainCamera.DeviceUri) { Query = "gain=120" }.Uri;
        var devices = new[]
        {
            Held(mainCamera) with { DeviceUri = nodesMainCamera.ToString(), Camera = CameraAt(-5) },
            Held(focuser) with { Focuser = FocuserDeviceStateDto.FromReading(new FocuserReading(800, 12.5, false)) },
            // Held but not connected (a run let it go): not connected on the view either.
            Held(wideCamera, connected: false) with { Camera = CameraAt(20) },
        };
        // The pointing a finished run left on the view, which a mount its node no longer holds must not keep.
        var view = new LiveSessionState { MountState = new MountState(1.5, 30, 0.1, PointingState.Normal, false, true, 1.49, 29.9) };

        RigDevices.Apply(view, profile, devices);

        view.PreviewOTATelemetry.Length.ShouldBe(2);
        var main = view.PreviewOTATelemetry[0];
        main.CameraConnected.ShouldBeTrue();
        main.CcdTempC.ShouldBe(-5);
        main.CameraDisplayName.ShouldBe(mainCamera.DisplayName);
        main.FocuserConnected.ShouldBeTrue();
        main.FocusPosition.ShouldBe(800);
        main.FilterWheelConnected.ShouldBeFalse("the OTA has none");
        view.PreviewOTATelemetry[1].CameraConnected.ShouldBeFalse();
        view.PreviewCapturing.Length.ShouldBe(2, "the preview controls are sized to the rig's OTAs");
        double.IsNaN(view.MountState.RightAscension).ShouldBeTrue("a mount its node does not hold points nowhere known");
    }

    [Fact]
    public void ARigsMountCarriesItsNodesLimitVerdict()
    {
        var mount = new FakeDevice(DeviceType.Mount, 1);
        var profile = new ProfileData(mount.DeviceUri, NoneDevice.Instance.DeviceUri, []);
        var verdict = new MountLimitVerdict(MountLimitKind.Meridian, MountLimitResponse.Warn, 12.5, MountLimitBasis.HourAngle);
        var state = new MountState(5.5, 20, 0.2, PointingState.Normal, false, true, 5.49, 19.9);
        var view = new LiveSessionState();

        RigDevices.Apply(view, profile, [Held(mount) with { Mount = MountDeviceStateDto.FromState(state, verdict) }]);

        view.MountState.ShouldBe(state);
        view.MountDisplayName.ShouldBe(mount.DisplayName);
        view.MountLimitVerdict.ShouldBe(verdict);
    }

    /// <summary>
    /// The per-frame poll of a rig's view leaves what its node's device plane read while the node serves no session: a
    /// session's verdict is adopted with the pointing it was judged on, and a mirror with no session has neither.
    /// </summary>
    [Fact]
    public void AnIdleRigKeepsItsNodesVerdictThroughThePoll()
    {
        var mount = new FakeDevice(DeviceType.Mount, 1);
        var verdict = new MountLimitVerdict(MountLimitKind.Horizon, MountLimitResponse.Warn, 2.5, MountLimitBasis.HourAngle);
        var state = new MountState(5.5, 20, 0.2, PointingState.Normal, false, true, 5.49, 19.9);
        var idle = Substitute.For<ISessionTelemetry>();
        idle.Run.Returns(ReportedRun.NoSession);
        idle.MountState.Returns(MountState.Unknown);
        idle.MountLimitVerdict.Returns(MountLimitVerdict.Clear);
        var view = new LiveSessionState { ActiveSession = idle };
        RigDevices.Apply(view, new ProfileData(mount.DeviceUri, NoneDevice.Instance.DeviceUri, []),
            [Held(mount) with { Mount = MountDeviceStateDto.FromState(state, verdict) }]);

        view.PollSession();

        view.MountLimitVerdict.ShouldBe(verdict);
        view.MountState.ShouldBe(state);
    }

    /// <summary>
    /// A rig's view always holds its mirror, so holding it is not having a session: a node that serves none is idle, and its
    /// card says so as this computer's does. A session that has ended is still held on both sides, as is an in-process one,
    /// which reports no run of its own.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(ReportedRun.None, true)]
    [InlineData(ReportedRun.Session, true)]
    [InlineData(ReportedRun.Flats, true)]
    [InlineData(ReportedRun.NoSession, false)]
    public void AViewHasARunWhenItsSessionReportsOne(ReportedRun? reported, bool hasRun)
    {
        var session = Substitute.For<ISessionTelemetry>();
        session.Run.Returns(reported);

        new LiveSessionState { ActiveSession = session }.HasActiveRun.ShouldBe(hasRun);
    }

    [Fact]
    public void AnIdleViewsCardSaysIdle()
    {
        var session = Substitute.For<ISessionTelemetry>();
        session.Run.Returns(ReportedRun.NoSession);

        HomeBoard.RunCard(new LiveSessionState { ActiveSession = session }, DateTimeOffset.UnixEpoch).Status.ShouldBe("Idle");
    }
}
