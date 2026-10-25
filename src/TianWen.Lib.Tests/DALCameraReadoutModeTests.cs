using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A DAL camera's readout mode: switched through the driver, set from the URI on connect, and the sensor's geometry read
/// again after either, since a mode decides it (a QHY294PROC's 47M mode is twice as wide and high at half the pitch).
/// </summary>
public class DALCameraReadoutModeTests(ITestOutputHelper output)
{
    private static readonly string[] Modes = ["11M MODE", "47M MODE"];

    [Fact]
    public async Task SwitchingTheModeReadsTheGeometryAgainAndPutsTheFrameBackToTheWholeSensor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, readoutModes: Modes);
        camera.ReadoutModes.ShouldBe(Modes);
        (await camera.GetReadoutModeAsync(ct)).ShouldBe("11M MODE");
        camera.BinX = 2;

        await camera.SetReadoutModeAsync("47m mode", ct);

        state.ReadoutMode.ShouldBe(1, "a mode is matched by name whatever its case");
        (await camera.GetReadoutModeAsync(ct)).ShouldBe("47M MODE");
        camera.CameraXSize.ShouldBe(200);
        camera.CameraYSize.ShouldBe(200);
        camera.PixelSizeX.ShouldBe(1.88);
        camera.BinX.ShouldBe((short)1);
        camera.NumX.ShouldBe(200);

        // The mode it is in is not asked of the body again.
        await camera.SetReadoutModeAsync("47M MODE", ct);
        state.ReadoutModeSets.ShouldBe(1);
    }

    [Fact]
    public async Task AModeTheCameraDoesNotHaveIsRefusedAndNothingChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, readoutModes: Modes);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => camera.SetReadoutModeAsync("High Gain", ct).AsTask());
        state.ReadoutModeSets.ShouldBe(0);
        camera.CameraXSize.ShouldBe(100);
    }

    [Fact]
    public async Task TheModeCannotChangeDuringAnExposure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, readoutModes: Modes);
        await camera.StartExposureAsync(TimeSpan.FromSeconds(10), cancellationToken: ct);

        await Should.ThrowAsync<InvalidOperationException>(() => camera.SetReadoutModeAsync("47M MODE", ct).AsTask());
        state.ReadoutModeSets.ShouldBe(0);
    }

    [Fact]
    public async Task TheUrisModeIsSetOnConnectBeforeTheGeometryIsRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, readoutModes: Modes, uriReadoutMode: "47M MODE");

        state.ReadoutMode.ShouldBe(1);
        (await camera.GetReadoutModeAsync(ct)).ShouldBe("47M MODE");
        camera.CameraXSize.ShouldBe(200);
    }

    [Fact]
    public async Task AUriModeTheCameraDoesNotHaveKeepsItsOwnAndStillConnects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output, readoutModes: Modes, uriReadoutMode: "Other camera's mode");

        state.ReadoutModeSets.ShouldBe(0);
        (await camera.GetReadoutModeAsync(ct)).ShouldBe("11M MODE");
        camera.CameraXSize.ShouldBe(100);
    }

    [Fact]
    public async Task ASingleModeCameraOffersNoChoiceAndNamesNoMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _, _) = await ScriptedDalCamera.ConnectAsync(output);

        camera.ReadoutModes.ShouldBeEmpty();
        (await camera.GetReadoutModeAsync(ct)).ShouldBeNull();
    }
}
