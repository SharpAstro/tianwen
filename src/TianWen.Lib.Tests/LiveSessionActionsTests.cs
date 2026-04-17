using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the I/O helpers extracted from <see cref="AppSignalHandler"/>'s preview-mode
/// subscribe lambdas. Use NSubstitute for driver interfaces + <see cref="FakeExternal"/>
/// for file I/O, so each helper is exercised without the signal bus or DI container.
/// </summary>
public class LiveSessionActionsTests(ITestOutputHelper output)
{
    // ── JogFocuserAsync ──

    [Fact]
    public async Task JogFocuserAsync_AddsStepsToCurrentPosition()
    {
        var focuser = Substitute.For<IFocuserDriver>();
        focuser.GetPositionAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(1000));

        var target = await LiveSessionActions.JogFocuserAsync(focuser, steps: 50, TestContext.Current.CancellationToken);

        target.ShouldBe(1050);
        await focuser.Received(1).BeginMoveAsync(1050, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JogFocuserAsync_WithNegativeSteps_MovesInward()
    {
        var focuser = Substitute.For<IFocuserDriver>();
        focuser.GetPositionAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(500));

        var target = await LiveSessionActions.JogFocuserAsync(focuser, steps: -80, TestContext.Current.CancellationToken);

        target.ShouldBe(420);
        await focuser.Received(1).BeginMoveAsync(420, Arg.Any<CancellationToken>());
    }

    // ── CaptureCameraPreviewAsync ──

    [Fact]
    public async Task CaptureCameraPreviewAsync_WithNullGain_DoesNotCallSetGain()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(true);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await LiveSessionActions.CaptureCameraPreviewAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.DidNotReceiveWithAnyArgs().SetGainAsync(default, default);
    }

    [Fact]
    public async Task CaptureCameraPreviewAsync_WithGainOnNumericCamera_AppliesGain()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(true);
        camera.UsesGainMode.Returns(false);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await LiveSessionActions.CaptureCameraPreviewAsync(
            camera, TimeSpan.FromSeconds(1), gain: 200, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.Received(1).SetGainAsync((short)200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureCameraPreviewAsync_WithGainOnModeCamera_AppliesGainAsIsoIndex()
    {
        // DSLR-style: UsesGainMode=true, UsesGainValue=false. Gain param is treated as
        // an ISO-list index by the driver.
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(false);
        camera.UsesGainMode.Returns(true);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await LiveSessionActions.CaptureCameraPreviewAsync(
            camera, TimeSpan.FromSeconds(1), gain: 3, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.Received(1).SetGainAsync((short)3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureCameraPreviewAsync_WithGainButCameraUsesNeither_SkipsSetGain()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(false);
        camera.UsesGainMode.Returns(false);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await LiveSessionActions.CaptureCameraPreviewAsync(
            camera, TimeSpan.FromSeconds(1), gain: 100, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.DidNotReceiveWithAnyArgs().SetGainAsync(default, default);
    }

    [Fact]
    public async Task CaptureCameraPreviewAsync_WithBinningAboveOne_SetsBinX()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await LiveSessionActions.CaptureCameraPreviewAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 2, timeProvider, TestContext.Current.CancellationToken);

        camera.Received(1).BinX = (byte)2;
    }

    [Fact]
    public async Task CaptureCameraPreviewAsync_WithBinningOne_DoesNotSetBinX()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await LiveSessionActions.CaptureCameraPreviewAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        camera.DidNotReceiveWithAnyArgs().BinX = default;
    }

    [Fact]
    public async Task CaptureCameraPreviewAsync_PollsUntilImageReady()
    {
        var camera = Substitute.For<ICameraDriver>();
        // Simulate two "not ready" polls before the exposure completes.
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(
            ValueTask.FromResult(false),
            ValueTask.FromResult(false),
            ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await LiveSessionActions.CaptureCameraPreviewAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.Received(3).GetImageReadyAsync(Arg.Any<CancellationToken>());
        await camera.Received(1).StartExposureAsync(TimeSpan.FromSeconds(1), FrameType.Light, Arg.Any<CancellationToken>());
        await camera.Received(1).GetImageAsync(Arg.Any<CancellationToken>());
    }

    // ── SaveSnapshotAsync ──

    [Fact]
    public async Task SaveSnapshotAsync_WritesFileAndReturnsSnapshotFileName()
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 4, 17, 22, 5, 30, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var image = CreateSyntheticImage();

        var fileName = await LiveSessionActions.SaveSnapshotAsync(image, otaIndex: 0, external, timeProvider);

        fileName.ShouldStartWith("snapshot_2026-04-17T22_05_30_OTA1");
        fileName.ShouldEndWith(".fits");

        // Folder structure: <ImageOutputFolder>/Snapshot/<yyyy-MM-dd>/<fileName>
        var expectedPath = Path.Combine(
            external.ImageOutputFolder.FullName,
            "Snapshot",
            "2026-04-17",
            fileName);
        File.Exists(expectedPath).ShouldBeTrue($"Expected FITS file at {expectedPath}");
    }

    [Fact]
    public async Task SaveSnapshotAsync_SecondOtaUsesOta2InFileName()
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 4, 17, 22, 5, 30, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var image = CreateSyntheticImage();

        var fileName = await LiveSessionActions.SaveSnapshotAsync(image, otaIndex: 1, external, timeProvider);

        fileName.ShouldContain("_OTA2");
    }

    // ── SampleOTATelemetryAsync ──

    [Fact]
    public async Task SampleOTATelemetryAsync_WhenNothingConnected_ReturnsAllDisconnected()
    {
        var hub = Substitute.For<IDeviceHub>();
        // TryGetConnectedDriver returns false for every URI (default substitute behaviour).
        var ota = new OTAData(
            Name: "Disconnected Scope",
            FocalLength: 1000,
            Camera: new Uri("Camera://FakeDevice/FakeCamera1#Fake Camera 1"),
            Cover: null,
            Focuser: new Uri("Focuser://FakeDevice/FakeFocuser1#Fake Focuser 1"),
            FilterWheel: new Uri("FilterWheel://FakeDevice/FakeFilterWheel1#Fake FW 1"),
            PreferOutwardFocus: null,
            OutwardIsPositive: null,
            Aperture: null,
            OpticalDesign: OpticalDesign.Unknown);

        var telemetry = await LiveSessionActions.SampleOTATelemetryAsync(hub, ota, FakeExternal.CreateLogger(output), TestContext.Current.CancellationToken);

        telemetry.CameraConnected.ShouldBeFalse();
        telemetry.FocuserConnected.ShouldBeFalse();
        telemetry.FilterWheelConnected.ShouldBeFalse();
        telemetry.FilterName.ShouldBe("--");
        double.IsNaN(telemetry.CcdTempC).ShouldBeTrue();
    }

    [Fact]
    public async Task SampleOTATelemetryAsync_WhenCameraConnected_PopulatesCameraFields()
    {
        var cameraUri = new Uri("Camera://FakeDevice/FakeCamera1#Fake Camera 1");
        var camera = Substitute.For<ICameraDriver>();
        camera.CanGetCCDTemperature.Returns(true);
        camera.CanGetCoolerPower.Returns(true);
        camera.CanGetCoolerOn.Returns(true);
        camera.GetCCDTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(-9.5));
        camera.GetCoolerPowerAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(42.0));
        camera.GetCoolerOnAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetSetCCDTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(-10.0));
        camera.UsesGainValue.Returns(true);
        camera.GainMin.Returns((short)0);
        camera.GainMax.Returns((short)300);
        camera.GetGainAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult((short)120));

        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out Arg.Any<ICameraDriver?>())
            .Returns(call => { call[1] = camera; return true; });

        var ota = new OTAData(
            Name: "Scope",
            FocalLength: 1000,
            Camera: cameraUri,
            Cover: null,
            Focuser: null,
            FilterWheel: null,
            PreferOutwardFocus: null,
            OutwardIsPositive: null,
            Aperture: null,
            OpticalDesign: OpticalDesign.Unknown);

        var telemetry = await LiveSessionActions.SampleOTATelemetryAsync(hub, ota, FakeExternal.CreateLogger(output), TestContext.Current.CancellationToken);

        telemetry.CameraConnected.ShouldBeTrue();
        telemetry.CcdTempC.ShouldBe(-9.5);
        telemetry.SetpointC.ShouldBe(-10.0);
        telemetry.CoolerPowerPct.ShouldBe(42.0);
        telemetry.CoolerOn.ShouldBeTrue();
        telemetry.UsesGainValue.ShouldBeTrue();
        telemetry.GainMin.ShouldBe((short)0);
        telemetry.GainMax.ShouldBe((short)300);
        telemetry.CurrentGain.ShouldBe((short)120);
    }

    [Fact]
    public async Task SampleOTATelemetryAsync_WhenFocuserConnected_PopulatesFocuserFields()
    {
        var focuserUri = new Uri("Focuser://FakeDevice/FakeFocuser1#Fake Focuser 1");
        var focuser = Substitute.For<IFocuserDriver>();
        focuser.GetPositionAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(1234));
        focuser.GetTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(8.2));
        focuser.GetIsMovingAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));

        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver<IFocuserDriver>(focuserUri, out Arg.Any<IFocuserDriver?>())
            .Returns(call => { call[1] = focuser; return true; });

        var ota = new OTAData(
            Name: "Scope",
            FocalLength: 1000,
            Camera: new Uri("none://NoneDevice/None"),
            Cover: null,
            Focuser: focuserUri,
            FilterWheel: null,
            PreferOutwardFocus: null,
            OutwardIsPositive: null,
            Aperture: null,
            OpticalDesign: OpticalDesign.Unknown);

        var telemetry = await LiveSessionActions.SampleOTATelemetryAsync(hub, ota, FakeExternal.CreateLogger(output), TestContext.Current.CancellationToken);

        telemetry.FocuserConnected.ShouldBeTrue();
        telemetry.FocusPosition.ShouldBe(1234);
        telemetry.FocuserTempC.ShouldBe(8.2);
        telemetry.FocuserIsMoving.ShouldBeFalse();
    }

    [Fact]
    public async Task SampleOTATelemetryAsync_WhenFilterWheelConnected_PopulatesFilterName()
    {
        var fwUri = new Uri("FilterWheel://FakeDevice/FakeFilterWheel1#Fake FW 1");
        var fw = Substitute.For<IFilterWheelDriver>();
        // Stub the default-interface-method directly; NSubstitute intercepts it regardless
        // of the Connected/Filters/GetPositionAsync inputs the default body would read.
        fw.GetCurrentFilterAsync(Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new InstalledFilter(Filter.Red, Position: 1)));

        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver<IFilterWheelDriver>(fwUri, out Arg.Any<IFilterWheelDriver?>())
            .Returns(call => { call[1] = fw; return true; });

        var ota = new OTAData(
            Name: "Scope",
            FocalLength: 1000,
            Camera: new Uri("none://NoneDevice/None"),
            Cover: null,
            Focuser: null,
            FilterWheel: fwUri,
            PreferOutwardFocus: null,
            OutwardIsPositive: null,
            Aperture: null,
            OpticalDesign: OpticalDesign.Unknown);

        var telemetry = await LiveSessionActions.SampleOTATelemetryAsync(hub, ota, FakeExternal.CreateLogger(output), TestContext.Current.CancellationToken);

        telemetry.FilterWheelConnected.ShouldBeTrue();
        telemetry.FilterName.ShouldBe(Filter.Red.Name);
    }

    private static Image CreateSyntheticImage()
    {
        var data = new float[16, 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                data[y, x] = (x + y) * 0.1f;
            }
        }
        var meta = new ImageMeta(
            "snapshot", DateTime.UtcNow, TimeSpan.FromSeconds(1), FrameType.Light, "",
            3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([data], BitDepth.Float32, maxValue: 3.0f, minValue: 0f, pedestal: 0, meta);
    }
}
