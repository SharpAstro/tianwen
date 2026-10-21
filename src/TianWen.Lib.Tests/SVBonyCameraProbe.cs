using System;
using System.Diagnostics;
using System.Linq;
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
