using NSubstitute;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="PreviewCapture"/>: a preview frame outside a session, taken and saved as a snapshot, the one copy the GUI's
/// Live Session preview and the node's preview job share. NSubstitute for the camera and <see cref="FakeExternal"/> for
/// file I/O, so each helper is exercised without the signal bus or DI container.
/// </summary>
public class PreviewCaptureTests(ITestOutputHelper output)
{
    // ── CaptureAsync ──

    [Fact]
    public async Task CaptureAsync_WithNullGain_DoesNotCallSetGain()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(true);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.DidNotReceiveWithAnyArgs().SetGainAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureAsync_WithGainOnNumericCamera_AppliesGain()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(true);
        camera.UsesGainMode.Returns(false);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(1), gain: 200, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.Received(1).SetGainAsync((short)200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureAsync_WithGainOnModeCamera_AppliesGainAsIsoIndex()
    {
        // DSLR-style: UsesGainMode=true, UsesGainValue=false. Gain param is treated as
        // an ISO-list index by the driver.
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(false);
        camera.UsesGainMode.Returns(true);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(1), gain: 3, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.Received(1).SetGainAsync((short)3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureAsync_WithGainButCameraUsesNeither_SkipsSetGain()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainValue.Returns(false);
        camera.UsesGainMode.Returns(false);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(1), gain: 100, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        await camera.DidNotReceiveWithAnyArgs().SetGainAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureAsync_WithBinningAboveOne_SetsBinX()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 2, timeProvider, TestContext.Current.CancellationToken);

        camera.Received(1).BinX = (byte)2;
    }

    [Fact]
    public async Task CaptureAsync_WithBinningOne_DoesNotSetBinX()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 1, timeProvider, TestContext.Current.CancellationToken);

        camera.DidNotReceiveWithAnyArgs().BinX = default;
    }

    [Fact]
    public async Task CaptureAsync_PollsUntilImageReady()
    {
        var camera = Substitute.For<ICameraDriver>();
        // Simulate two "not ready" polls before the exposure completes.
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(
            ValueTask.FromResult(false),
            ValueTask.FromResult(false),
            ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<Image?>(null));
        var timeProvider = new FakeTimeProviderWrapper();

        await PreviewCapture.CaptureAsync(
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

        var path = await PreviewCapture.SaveSnapshotAsync(image, otaIndex: 0, external, timeProvider);

        var fileName = Path.GetFileName(path);
        fileName.ShouldStartWith("snapshot_2026-04-17T22_05_30_OTA1");
        fileName.ShouldEndWith(".fits");

        // Folder structure: <ImageOutputFolder>/Snapshot/<yyyy-MM-dd>/<fileName>, and the path answered is that file.
        var expectedPath = Path.Combine(
            external.ImageOutputFolder.FullName,
            "Snapshot",
            "2026-04-17",
            fileName);
        path.ShouldBe(expectedPath);
        File.Exists(expectedPath).ShouldBeTrue($"Expected FITS file at {expectedPath}");
    }

    [Fact]
    public async Task SaveSnapshotAsync_SecondOtaUsesOta2InFileName()
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 4, 17, 22, 5, 30, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var image = CreateSyntheticImage();

        var path = await PreviewCapture.SaveSnapshotAsync(image, otaIndex: 1, external, timeProvider);

        Path.GetFileName(path).ShouldContain("_OTA2");
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
