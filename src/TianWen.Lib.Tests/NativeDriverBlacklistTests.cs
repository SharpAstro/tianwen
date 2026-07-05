using System;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using Xunit;

namespace TianWen.Lib.Tests;

public class NativeDriverBlacklistTests
{
    // A stand-in for a native device family. Its DeviceClass is the URI host, exactly as real devices
    // (ZWODevice/QHYDevice/GeminiDevice) derive it -- so we don't need SDK-backed sources to test.
    private sealed record TestNativeDevice(Uri DeviceUri) : DeviceBase(DeviceUri);

    private static DeviceBase Native(string deviceClass, DeviceType type, string id)
        => new TestNativeDevice(new Uri($"{type}://{deviceClass}/{id}#{id}"));

    private static AscomDevice Ascom(DeviceType type, string progId)
        => new(type, progId, progId);

    private static string[] FilterIds(params DeviceBase[] devices)
        => NativeDriverBlacklist.FilterSuperseded(devices, NullLogger.Instance).Select(d => d.DeviceId).ToArray();

    [Fact]
    public void GivenBlacklistedAscomAndItsNativeTwinPresentThenAscomIsHidden()
    {
        var ids = FilterIds(
            Ascom(DeviceType.Camera, "ASCOM.ASICamera2.Camera"),
            Native("ZWODevice", DeviceType.Camera, "0"));

        ids.ShouldBe(["0"]); // the native ZWO camera survives, the ASCOM twin is dropped
    }

    [Fact]
    public void GivenBlacklistedAscomButNoNativeTwinThenAscomIsKeptAsFallback()
    {
        // No native ZWODevice discovered (SDK absent / no camera) -> never strand the user.
        var ids = FilterIds(Ascom(DeviceType.Camera, "ASCOM.ASICamera2.Camera"));

        ids.ShouldBe(["ASCOM.ASICamera2.Camera"]);
    }

    [Fact]
    public void GivenGeminiFlatPanelWithNativeSerialPresentThenAscomGeminiIsHidden()
    {
        var ids = FilterIds(
            Ascom(DeviceType.CoverCalibrator, "ASCOM.GeminiFPLite.CoverCalibrator"),
            Native("GeminiDevice", DeviceType.CoverCalibrator, "COM3"));

        ids.ShouldBe(["COM3"]);
    }

    [Fact]
    public void GivenNonBlacklistedAscomThenItIsKeptEvenAlongsideANativeDevice()
    {
        // ASCOM.PlayerOne isn't reimplemented natively; a ZWO native device must not hide it.
        var ids = FilterIds(
            Ascom(DeviceType.Camera, "ASCOM.PlayerOne.Camera"),
            Native("ZWODevice", DeviceType.Camera, "0"));

        ids.ShouldBe(["ASCOM.PlayerOne.Camera", "0"]);
    }

    [Fact]
    public void GivenGeminiFocuserProThenItIsNotHiddenByTheNativeGeminiCoverFamily()
    {
        // GeminiFocuserPro is a different product with no native impl -- must survive even if a native
        // GeminiDevice (the flat panel) is present.
        var ids = FilterIds(
            Ascom(DeviceType.Focuser, "ASCOM.GeminiFocuserPro.Focuser"),
            Native("GeminiDevice", DeviceType.Focuser, "COM3"));

        ids.ShouldBe(["ASCOM.GeminiFocuserPro.Focuser", "COM3"]);
    }

    [Theory]
    [InlineData("ASCOM.GeminiFPLite.CoverCalibrator", "GeminiDevice")]
    [InlineData("ascom.asicamera2.camera", "ZWODevice")] // case-insensitive ProgID match
    [InlineData("ASCOM.EFW2_2.FilterWheel", "ZWODevice")]
    [InlineData("ASCOM.qfoc.Focuser", "QHYDevice")]
    [InlineData("ASCOM.QHYCFW.FilterWheel", "QHYDevice")]
    public void GivenKnownProgIdThenMapsToExpectedNativeClass(string progId, string expected)
    {
        NativeDriverBlacklist.TryGetNativeClass(progId, out var nativeClass).ShouldBeTrue();
        nativeClass.ShouldBe(expected);
    }

    [Theory]
    [InlineData("ASCOM.PlayerOne.Camera")]
    [InlineData("ASCOM.GeminiFocuserPro.Focuser")]
    [InlineData("ASCOM.iOptron2017.Telescope")]
    public void GivenUnlistedProgIdThenNoNativeClass(string progId)
        => NativeDriverBlacklist.TryGetNativeClass(progId, out _).ShouldBeFalse();
}
