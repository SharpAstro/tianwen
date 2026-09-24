using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.DAL;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A DAL camera that loses exposures: the frame is given up at its deadline so the night goes on,
/// and a camera that keeps losing them is reset, found again and put back as it was.
/// </summary>
/// <remarks>
/// Pinned against a scripted fake rather than hardware because the failure is intermittent on the
/// hardware (one lost trigger in 600 on a G3M678M), and what matters is the driver's answer to it:
/// before the deadline existed a lost frame left the camera reporting Exposing for ever, and the
/// imaging loop, which only starts frames on an idle camera, stopped starting them without a word.
/// </remarks>
[Collection("Devices")]
public class DALCameraRecoveryTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AnExposureThatNeverFinishesIsGivenUpAtItsDeadline()
    {
        var (camera, state, time) = await NewConnectedCameraAsync();
        state.LoseNext = 1;

        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        var deadline = Exposure + TestDalCameraDriver.Grace(Exposure);

        time.Advance(deadline - TimeSpan.FromSeconds(1));
        (await camera.GetCameraStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CameraState.Exposing, "still inside its grace");

        time.Advance(TimeSpan.FromSeconds(2));
        (await camera.GetCameraStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CameraState.Idle, "given up, so the loop can start the next frame");
        (await camera.GetImageReadyAsync(TestContext.Current.CancellationToken)).ShouldBeFalse("a lost frame is not a frame to download");
        state.StopCount.ShouldBe(1);
        camera.ConsecutiveLostExposures.ShouldBe(1);

        // The next exposure is simply tried, and one that arrives clears the count.
        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(Exposure);
        (await camera.GetCameraStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CameraState.Idle);
        (await camera.GetImageReadyAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        camera.ConsecutiveLostExposures.ShouldBe(0);
        state.ResetCount.ShouldBe(0, "one lost frame is not a wedged camera");
    }

    [Fact]
    public async Task ALostFrameIsCountedOnceHoweverOftenItIsPolled()
    {
        var (camera, state, time) = await NewConnectedCameraAsync();
        state.LoseNext = 1;

        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(Exposure + TestDalCameraDriver.Grace(Exposure) + TimeSpan.FromSeconds(1));
        for (var poll = 0; poll < 5; poll++)
        {
            _ = await camera.GetCameraStateAsync(TestContext.Current.CancellationToken);
        }

        camera.ConsecutiveLostExposures.ShouldBe(1);
        state.StopCount.ShouldBe(1);
    }

    [Fact]
    public async Task TwoLostExposuresInARowResetTheCameraAndPutItBackAsItWas()
    {
        var (camera, state, time) = await NewConnectedCameraAsync(canReset: true);
        await camera.SetGainAsync(42, TestContext.Current.CancellationToken);
        await camera.SetOffsetAsync(7, TestContext.Current.CancellationToken);
        await camera.SetSetCCDTemperatureAsync(-10, TestContext.Current.CancellationToken);
        await camera.SetCoolerOnAsync(true, TestContext.Current.CancellationToken);
        camera.BinX = 2;
        var binnedWidth = camera.NumX;

        state.LoseNext = 2;
        await LoseOneExposureAsync(camera, time);
        await LoseOneExposureAsync(camera, time);
        state.ResetCount.ShouldBe(0, "the reset waits for the next exposure, never runs inside a state poll");

        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);

        state.ResetCount.ShouldBe(1);
        state.Controls[CMOSControlType.Gain].ShouldBe(42);
        state.Controls[CMOSControlType.Brightness].ShouldBe(7);
        state.Controls[CMOSControlType.CoolerOn].ShouldBe(1, "a reset switched the cooler off; it is switched back on");
        state.Controls.ShouldContainKey(CMOSControlType.TargetTemperature);
        camera.BinX.ShouldBe(2, "binning lives in the driver's settings and survives the reset");
        camera.NumX.ShouldBe(binnedWidth);
        state.Width.ShouldBe(binnedWidth, "the exposure after the reset was started with the settings from before it");
        camera.ConsecutiveLostExposures.ShouldBe(0);

        time.Advance(Exposure);
        (await camera.GetImageReadyAsync(TestContext.Current.CancellationToken)).ShouldBeTrue("the exposure after the reset is a normal one");
    }

    [Fact]
    public async Task ACameraThatCannotBeResetKeepsTakingFramesWithoutOne()
    {
        var (camera, state, time) = await NewConnectedCameraAsync(canReset: false);
        state.LoseNext = 3;

        await LoseOneExposureAsync(camera, time);
        await LoseOneExposureAsync(camera, time);
        await LoseOneExposureAsync(camera, time);

        state.ResetCount.ShouldBe(0);
        state.StartCount.ShouldBe(3, "every exposure was still tried");
        camera.ConsecutiveLostExposures.ShouldBe(3);
    }

    [Fact]
    public async Task AFrameThatArrivesBetweenTwoLossesMeansNoReset()
    {
        var (camera, state, time) = await NewConnectedCameraAsync(canReset: true);

        state.LoseNext = 1;
        await LoseOneExposureAsync(camera, time);

        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(Exposure);
        (await camera.GetImageReadyAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        state.LoseNext = 1;
        await LoseOneExposureAsync(camera, time);
        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);

        state.ResetCount.ShouldBe(0, "the losses were not in a row");
    }

    [Fact]
    public async Task AnExposureTheCameraReportsFailedCountsAsLost()
    {
        var (camera, state, time) = await NewConnectedCameraAsync(canReset: true);
        state.FailNext = 2;

        for (var i = 0; i < 2; i++)
        {
            await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(1));
            (await camera.GetCameraStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CameraState.Idle, "a failure is final at once, with no deadline to wait for");
        }

        camera.ConsecutiveLostExposures.ShouldBe(2);
        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        state.ResetCount.ShouldBe(1);
    }

    [Fact]
    public async Task ACameraThatDoesNotComeBackAfterItsResetIsLeftInErrorAndSaysSo()
    {
        var (camera, state, time) = await NewConnectedCameraAsync(canReset: true);
        state.EnumerationsUntilBackAfterReset = int.MaxValue;
        state.LoseNext = 2;
        await LoseOneExposureAsync(camera, time);
        await LoseOneExposureAsync(camera, time);

        await Should.ThrowAsync<InvalidOperationException>(
            camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        state.ResetCount.ShouldBe(1);
        (await camera.GetCameraStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CameraState.Error);
    }

    [Fact]
    public async Task ACameraThatTakesAFewPollsToReappearIsWaitedFor()
    {
        var (camera, state, time) = await NewConnectedCameraAsync(canReset: true);
        state.EnumerationsUntilBackAfterReset = 4;
        state.LoseNext = 2;
        await LoseOneExposureAsync(camera, time);
        await LoseOneExposureAsync(camera, time);

        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);

        state.ResetCount.ShouldBe(1);
        state.StartCount.ShouldBe(3);
    }

    private static async Task LoseOneExposureAsync(TestDalCameraDriver camera, FakeTimeProviderWrapper time)
    {
        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(Exposure + TestDalCameraDriver.Grace(Exposure) + TimeSpan.FromSeconds(1));
        (await camera.GetCameraStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CameraState.Idle);
    }

    private Task<(TestDalCameraDriver Camera, FakeCmosState State, FakeTimeProviderWrapper Time)> NewConnectedCameraAsync(bool canReset = false)
        => ScriptedDalCamera.ConnectAsync(output, canReset);
}

/// <summary>Connects a <see cref="TestDalCameraDriver"/> over a scripted SDK, for every DAL test class.</summary>
internal static class ScriptedDalCamera
{
    internal static async Task<(TestDalCameraDriver Camera, FakeCmosState State, FakeTimeProviderWrapper Time)> ConnectAsync(
        ITestOutputHelper output, bool canReset = false, IReadOnlyList<PixelDataFormat>? formats = null)
    {
        var time = new FakeTimeProviderWrapper();
        var external = new FakeExternal(output, time);
        var device = new FakeDevice(DeviceType.Camera, 1);
        var state = new FakeCmosState(device.DeviceId) { CanReset = canReset, Formats = formats ?? [PixelDataFormat.RAW16] };
        var camera = new TestDalCameraDriver(device, external.BuildServiceProvider(), state);
        await camera.ConnectAsync(TestContext.Current.CancellationToken);
        camera.Connected.ShouldBeTrue();
        return (camera, state, time);
    }
}

internal sealed class TestDalCameraDriver(FakeDevice device, IServiceProvider sp, FakeCmosState state)
    : DALCameraDriver<FakeDevice, FakeCmosCamera>(device, sp)
{
    public static TimeSpan Grace(TimeSpan duration) => LostExposureGrace(duration);

    public override string? DriverInfo => "Scripted DAL camera";

    public override string? Description => "Scripted DAL camera for recovery tests";

    public override double ExposureResolution => 1E-06;

    protected override INativeDeviceIterator<FakeCmosCamera> NewIterator() => new FakeCmosIterator(state);

    protected override Exception NotConnectedException() => new InvalidOperationException("not connected");

    protected override Exception OperationalException(CMOSErrorCode errorCode, string message) => new InvalidOperationException($"{message} ({errorCode})");
}

/// <summary>The camera's mutable state, shared by every copy of the device-info struct.</summary>
internal sealed class FakeCmosState(string serial)
{
    public enum Frame { None, Arriving, Lost, Failed }

    public string Serial { get; } = serial;

    public bool CanReset { get; init; }

    public bool Present { get; set; } = true;

    public int EnumerationsUntilBackAfterReset { get; set; } = 1;

    public int EnumerationsLeft { get; set; }

    public int LoseNext { get; set; }

    public int FailNext { get; set; }

    public Frame Current { get; set; }

    public int StartCount { get; set; }

    public int StopCount { get; set; }

    public int ResetCount { get; set; }

    public int Width { get; set; } = 100;

    public int Height { get; set; } = 100;

    public int Bin { get; set; } = 1;

    public PixelDataFormat Format { get; set; } = PixelDataFormat.RAW16;

    /// <summary>The pixel formats the SDK offers; a second one makes the bit depth settable.</summary>
    public IReadOnlyList<PixelDataFormat> Formats { get; init; } = [PixelDataFormat.RAW16];

    /// <summary>
    /// The bytes the SDK hands over on a download, exactly as it lays them out (16-bit pixels
    /// little-endian). Null leaves the driver's buffer untouched, which is all the recovery tests need.
    /// </summary>
    public byte[]? Pixels { get; set; }

    public int StartX { get; set; }

    public int StartY { get; set; }

    public Dictionary<CMOSControlType, int> Controls { get; } = PowerOnDefaults();

    public static Dictionary<CMOSControlType, int> PowerOnDefaults() => new()
    {
        [CMOSControlType.Exposure] = 1000,
        [CMOSControlType.Gain] = 0,
        [CMOSControlType.Brightness] = 0,
        [CMOSControlType.CoolerOn] = 0,
        [CMOSControlType.TargetTemperature] = 20,
        [CMOSControlType.TemperatureDeci] = 200,
    };

    public void PowerCycle()
    {
        ResetCount++;
        Present = false;
        EnumerationsLeft = EnumerationsUntilBackAfterReset;
        Current = Frame.None;
        Width = Height = 100;
        Bin = 1;
        StartX = StartY = 0;
        Controls.Clear();
        foreach (var (control, value) in PowerOnDefaults())
        {
            Controls[control] = value;
        }
    }

    public bool Enumerate()
    {
        if (!Present && EnumerationsLeft != int.MaxValue && --EnumerationsLeft <= 0)
        {
            Present = true;
        }

        return Present;
    }
}

internal sealed class FakeCmosIterator(FakeCmosState state) : INativeDeviceIterator<FakeCmosCamera>
{
    public IEnumerator<FakeCmosCamera> GetEnumerator()
    {
        if (state.Enumerate())
        {
            yield return new FakeCmosCamera(state);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal readonly struct FakeCmosCamera(FakeCmosState state) : ICMOSNativeInterface
{
    private static readonly IReadOnlyList<int> Bins = [1, 2, 0];

    public int ID => 0;
    public string Name => "Scripted CMOS";
    public string CustomId => state.Serial;
    public bool Open() => state.Present;
    public bool Close() => true;
    public string? SerialNumber => state.Serial;
    public bool IsUSB3Device => false;
    public bool CanResetDevice => state.CanReset;

    public CMOSErrorCode ResetDevice()
    {
        if (!state.CanReset)
        {
            return CMOSErrorCode.GeneralError;
        }

        state.PowerCycle();
        return CMOSErrorCode.Success;
    }

    public int MaxHeight => 100;
    public int MaxWidth => 100;
    public int BitDepth => 16;
    public double PixelSize => 3.76;
    public BayerPattern BayerPattern => BayerPattern.Monochrome;
    public IReadOnlyList<int> SupportedBins => Bins;
    public IReadOnlyList<PixelDataFormat> SupportedPixelDataFormats => state.Formats;
    public bool IsTriggerCamera => false;
    public bool HasMechanicalShutter => false;
    public bool HasCooler => true;
    public bool HasST4Port => false;
    public double ElectronPerADU => 1.0;

    public bool TryGetControlRange(CMOSControlType ctrlType, out int min, out int max)
    {
        (min, max) = ctrlType switch
        {
            CMOSControlType.Exposure => (1, int.MaxValue),
            CMOSControlType.Gain => (0, 100),
            CMOSControlType.Brightness => (0, 100),
            CMOSControlType.TargetTemperature => (-40, 30),
            _ => (0, 0),
        };
        return max > min;
    }

    public CMOSErrorCode GetControlValue(CMOSControlType controlType, out int value, out bool isAuto)
    {
        isAuto = false;
        return state.Controls.TryGetValue(controlType, out value) ? CMOSErrorCode.Success : CMOSErrorCode.InvalidControlType;
    }

    public CMOSErrorCode SetControlValue(CMOSControlType controlType, int value, bool isAuto = false)
    {
        state.Controls[controlType] = value;
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode PulseGuideOn(GuideDirection direction) => CMOSErrorCode.GeneralError;
    public CMOSErrorCode PulseGuideOff(GuideDirection direction) => CMOSErrorCode.GeneralError;

    public CMOSErrorCode StartLightExposure()
    {
        state.StartCount++;
        if (state.LoseNext > 0)
        {
            state.LoseNext--;
            state.Current = FakeCmosState.Frame.Lost;
        }
        else if (state.FailNext > 0)
        {
            state.FailNext--;
            state.Current = FakeCmosState.Frame.Failed;
        }
        else
        {
            state.Current = FakeCmosState.Frame.Arriving;
        }

        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode StartDarkExposure() => StartLightExposure();

    public CMOSErrorCode StopExposure()
    {
        state.StopCount++;
        state.Current = FakeCmosState.Frame.None;
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode GetExposureStatus(out ExposureStatus exposureStatus)
    {
        exposureStatus = state.Current switch
        {
            FakeCmosState.Frame.Lost => ExposureStatus.Working,
            FakeCmosState.Frame.Arriving => ExposureStatus.Success,
            FakeCmosState.Frame.Failed => ExposureStatus.Failed,
            _ => ExposureStatus.Idle,
        };
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode GetStartPosition(out int startX, out int startY)
    {
        (startX, startY) = (state.StartX, state.StartY);
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode SetStartPosition(int startX, int startY)
    {
        (state.StartX, state.StartY) = (startX, startY);
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode GetROIFormat(out int width, out int height, out int bin, out PixelDataFormat pixelDataFormat)
    {
        (width, height, bin, pixelDataFormat) = (state.Width, state.Height, state.Bin, state.Format);
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode SetROIFormat(int width, int height, int bin, PixelDataFormat pixelDataFormat)
    {
        (state.Width, state.Height, state.Bin, state.Format) = (width, height, bin, pixelDataFormat);
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode GetDataAfterExposure(IntPtr buffer, int bufferSize)
    {
        if (state.Pixels is { } pixels)
        {
            Marshal.Copy(pixels, 0, buffer, Math.Min(pixels.Length, bufferSize));
        }

        return CMOSErrorCode.Success;
    }
}
