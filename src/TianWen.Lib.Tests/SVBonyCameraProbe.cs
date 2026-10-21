using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.SVBony;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A real SVBONY camera through TianWen's own driver, as a session takes it: discovery, connect, a full frame, a
/// binned sub-frame and a short run, each checked for what the binding promises (the friendly name as the
/// instrument, the Bayer phase, 16-bit frames on the 14-bit left-aligned scale, the window asked for).
/// </summary>
/// <remarks>
/// Set <c>TIANWEN_SVBONY_PROBE=1</c> with an SVBONY camera attached and no other program holding it. Uncooled: the
/// cooler is never switched on. Real clock, since an exposure is wall-clock time.
/// </remarks>
[Collection("Devices")]
public class SVBonyCameraProbe(ITestOutputHelper output)
{
    private const string GateVar = "TIANWEN_SVBONY_PROBE";

    [Fact(Timeout = 300_000)]
    public async Task ACameraTakesFramesThroughTheDriver()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached SVBONY camera)");
        var ct = TestContext.Current.CancellationToken;
        var device = new SVBonyDeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault();
        Assert.SkipWhen(device is null, "no SVBONY camera attached");
        output.WriteLine($"discovered {device.DeviceUri}");

        await using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(new FakeExternal(output))
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" }))
                .SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider();
        var camera = new SVBonyCameraDriver(device, sp);
        var watch = Stopwatch.StartNew();
        await camera.ConnectAsync(ct);
        output.WriteLine($"connect {watch.ElapsedMilliseconds} ms: {camera.Name}, {camera.CameraXSize} x {camera.CameraYSize}, {camera.SensorType} offset ({camera.BayerOffsetX}, {camera.BayerOffsetY}), "
            + $"{camera.PixelSizeX} um, gain [{camera.GainMin}, {camera.GainMax}], offset [{camera.OffsetMin}, {camera.OffsetMax}], max ADU {camera.MaxADU}, cooler {camera.CanSetCCDTemperature}, "
            + $"sensor {await camera.GetCCDTemperatureAsync(ct):F1} C");
        try
        {
            camera.Name.ShouldBe(device.DisplayName, "the instrument is the SDK's friendly name");
            camera.SensorType.ShouldBe(SensorType.RGGB);
            (await camera.GetBitDepthAsync(ct)).ShouldBe(BitDepth.Int16);

            var full = await ExposeAsync(camera, TimeSpan.FromSeconds(1), "full frame, 1 s");
            full.Width.ShouldBe(camera.CameraXSize);
            full.Height.ShouldBe(camera.CameraYSize);

            camera.BinX = 2;
            camera.NumX = 512;
            camera.NumY = 512;
            camera.StartX = 100;
            camera.StartY = 100;
            var binned = await ExposeAsync(camera, TimeSpan.FromMilliseconds(100), "512 x 512 bin 2 at (100, 100), 100 ms");
            (binned.Width, binned.Height).ShouldBe((512, 512));
            binned.Median.ShouldBe(full.Median, full.Median * 0.2, "average binning keeps the unbinned scale");

            camera.BinX = 1;
            camera.StartX = 0;
            camera.StartY = 0;
            camera.NumX = camera.CameraXSize;
            camera.NumY = camera.CameraYSize;
            watch.Restart();
            for (var i = 0; i < 5; i++)
            {
                await ExposeAsync(camera, TimeSpan.FromMilliseconds(50), null);
            }

            output.WriteLine($"5 x 50 ms full frames: {watch.ElapsedMilliseconds} ms");
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    /// <summary>
    /// The same camera streaming through the driver's one DAL video loop (#1748): full frame and a planetary window, in 8
    /// and 16 bits, then an exposure straight after, which must find the soft trigger stream back.
    /// </summary>
    /// <remarks>
    /// Measured through SVBony.SDK's probe on the bench SV605CC (2026-10-21, USB 3 direct, 2 ms, uncooled): 19.8 frames a
    /// second at full frame in either depth and 116 through 640 x 480, against 6.6 and 32 one exposure at a time.
    /// </remarks>
    [Fact(Timeout = 300_000)]
    public async Task ACameraStreamsThroughTheDriver()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached SVBONY camera)");
        var ct = TestContext.Current.CancellationToken;
        var device = new SVBonyDeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault();
        Assert.SkipWhen(device is null, "no SVBONY camera attached");

        await using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(new FakeExternal(output))
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" }))
                .SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider();
        var camera = new SVBonyCameraDriver(device, sp);
        await camera.ConnectAsync(ct);
        try
        {
            camera.CanVideoCapture.ShouldBeTrue();
            foreach (var (width, height, depth, floorFps) in new (int, int, BitDepth, double)[]
            {
                (camera.CameraXSize, camera.CameraYSize, BitDepth.Int16, 10),
                (camera.CameraXSize, camera.CameraYSize, BitDepth.Int8, 10),
                (640, 480, BitDepth.Int16, 60),
                (640, 480, BitDepth.Int8, 60),
            })
            {
                camera.BinX = 1;
                camera.NumX = width;
                camera.NumY = height;
                camera.StartX = (camera.CameraXSize - width) / 2 & ~1;
                camera.StartY = (camera.CameraYSize - height) / 2 & ~1;
                var label = $"{width} x {height} {depth}";
                var (frames, fps) = await StreamAsync(camera, new VideoCaptureOptions(TimeSpan.FromMilliseconds(2), BitDepth: depth), width, height, label);
                frames.ShouldBeGreaterThan(10, label);
                fps.ShouldBeGreaterThan(floorFps, label);
            }

            var after = await ExposeAsync(camera, TimeSpan.FromMilliseconds(200), "an exposure after the stream, 200 ms");
            (after.Width, after.Height).ShouldBe((640, 480));
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    private async Task<(int Frames, double Fps)> StreamAsync(SVBonyCameraDriver camera, VideoCaptureOptions options, int width, int height, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();
        TimeSpan? first = null;
        var frames = 0;
        var median = 0f;
        await foreach (var frame in camera.CaptureVideoAsync(options, stop.Token))
        {
            try
            {
                (frame.Width, frame.Height).ShouldBe((width, height), label);
                first ??= clock.Elapsed;
                if (++frames == 10)
                {
                    var plane = frame.GetChannelSpan(0).ToArray();
                    Array.Sort(plane);
                    median = plane[plane.Length / 2];
                }
            }
            finally
            {
                frame.Release();
            }

            if (clock.Elapsed - first >= TimeSpan.FromSeconds(5))
            {
                break;
            }
        }

        var fps = (frames - 1) / (clock.Elapsed - (first ?? TimeSpan.Zero)).TotalSeconds;
        output.WriteLine($"{label}: first frame after {first?.TotalMilliseconds:F0} ms, {fps:F1} fps ({frames} frames), median {median}, "
            + $"dropped {camera.DroppedFrames}");
        return (frames, fps);
    }

    private readonly record struct Frame(int Width, int Height, float Median);

    private async Task<Frame> ExposeAsync(SVBonyCameraDriver camera, TimeSpan exposure, string? label)
    {
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await camera.StartExposureAsync(exposure, cancellationToken: ct);
        var ready = await camera.WaitForImageReadyAsync(exposure, SystemTimeProvider.Instance, TimeSpan.FromMilliseconds(20),
            timeout: exposure + TimeSpan.FromSeconds(15), cancellationToken: ct);
        ready.ShouldBeTrue($"{label ?? "a run's frame"} arrived");
        var image = (await ((ICameraDriver)camera).GetImageAsync(ct)).ShouldNotBeNull();
        try
        {
            var plane = image.GetChannelSpan(0).ToArray();
            Array.Sort(plane);
            var median = plane[plane.Length / 2];
            if (label is not null)
            {
                output.WriteLine($"{label}: {image.Width} x {image.Height}, ready after {watch.ElapsedMilliseconds} ms, median {median}, max {image.MaxValue}, "
                    + $"full scale {image.ImageMeta.SensorFullScaleAdu}, instrument '{image.ImageMeta.Instrument}'");
                image.ImageMeta.Instrument.ShouldBe(camera.Name);
            }

            return new Frame(image.Width, image.Height, median);
        }
        finally
        {
            image.Release();
        }
    }
}
