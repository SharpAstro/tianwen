using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.QHYCCD;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An attached QHY camera streaming through our own driver: discovery, <see cref="QHYCameraDriver"/>, the DAL's one video loop
/// (#813, D.3: QHYCCD.SDK's live mode), then a single exposure straight after, which must find the body back in single-frame
/// mode, since on QHY a switch of mode is a re-init.
/// </summary>
/// <remarks>
/// <para>Set <c>TIANWEN_QHY_VIDEO_PROBE=1</c> with a QHY camera attached and no other program holding it; with several,
/// <c>TIANWEN_QHY_VIDEO_NAME</c> picks the one whose name contains it. <c>TIANWEN_QHY_VIDEO_GAIN</c> and
/// <c>TIANWEN_QHY_VIDEO_EXPOSURE_MS</c> (default 1 ms) suit the light: in daylight the bench's PoleMaster read a third of full
/// scale at 1 ms and gain 1, and black at gain 0 (2026-11-05).</para>
/// <para>A diagnostic first and an assertion second: it prints the rate, the frames' level and whether successive frames
/// differ, so a body that "streams" one frame over and over shows as such.</para>
/// </remarks>
[Collection("Devices")]
public class QhyVideoProbe(ITestOutputHelper output)
{
    private const string GateVar = "TIANWEN_QHY_VIDEO_PROBE";
    private static readonly TimeSpan StreamFor = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 300_000)]
    public async Task AQhyCameraStreamsThroughTheDriverAndExposesAfter()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached QHY camera)");
        var ct = TestContext.Current.CancellationToken;

        var probes = Substitute.For<ISerialProbeService>();
        probes.ResultsFor(Arg.Any<string>()).Returns([]);
        var source = new QHYDeviceSource(probes);
        await source.DiscoverAsync(ct);
        var name = Environment.GetEnvironmentVariable("TIANWEN_QHY_VIDEO_NAME");
        var device = source.RegisteredDevices(DeviceType.Camera)
            .FirstOrDefault(d => name is not { Length: > 0 } || d.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(device is null, "no QHY camera attached");
        output.WriteLine($"discovered {device.DeviceUri}");

        await using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(new FakeExternal(output))
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" }))
                .SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider();
        var camera = new QHYCameraDriver(device, sp);
        await camera.ConnectAsync(ct);
        try
        {
            output.WriteLine($"{camera.Name}: {camera.CameraXSize} x {camera.CameraYSize}, {camera.SensorType}, can stream {camera.CanVideoCapture}, "
                + $"gain [{camera.GainMin}, {camera.GainMax}], depths {string.Join("/", camera.VideoBitDepths)}");
            camera.CanVideoCapture.ShouldBeTrue("the SDK offers live mode (CAM_LIVEVIDEOMODE)");

            var exposure = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_VIDEO_EXPOSURE_MS"), out var ms) ? ms : 1.0);
            short? gain = short.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_VIDEO_GAIN"), out var g) ? g : null;
            camera.BinX = 1;
            camera.StartX = 0;
            camera.StartY = 0;
            camera.NumX = camera.CameraXSize;
            camera.NumY = camera.CameraYSize;

            var (frames, fps, distinct) = await StreamAsync(camera, new VideoCaptureOptions(exposure, gain), $"full frame, {exposure.TotalMilliseconds} ms, gain {gain?.ToString() ?? "as set"}");
            frames.ShouldBeGreaterThan(10);
            distinct.ShouldBeGreaterThan(1, "successive frames differ: the stream is not one frame handed back again");

            await ExposeAsync(camera, exposure + TimeSpan.FromMilliseconds(9), "an exposure after the stream");
        }
        finally
        {
            await camera.DisconnectAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The SDK's own live rate with no driver in between: which speed controls the body offers, and the frames a second at
    /// the lowest and highest USB traffic (a delay on QHY), each poll counted. What the driver's rate is compared against.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void TheSdksOwnLiveRate()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached QHY camera)");
        QHYCCD.SDK.QHYCamera.EnsureResourceInitialized().ShouldBeTrue();
        var count = QHYCCD.SDK.QHYCamera.ScanQHYCCD();
        Assert.SkipWhen(count is 0 or uint.MaxValue, "no QHY camera attached");
        var id = new System.Text.StringBuilder(64);
        (QHYCCD.SDK.QHYCamera.GetQHYCCDId(0, id) is 0).ShouldBeTrue();
        var idPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(id.ToString());
        try
        {
            var handle = QHYCCD.SDK.QHYCamera.OpenQHYCCD(idPtr);
            (handle != IntPtr.Zero).ShouldBeTrue();
            try
            {
                QHYCCD.SDK.QHYCamera.SetQHYCCDStreamMode(handle, 1);
                (QHYCCD.SDK.QHYCamera.InitQHYCCD(handle) is 0).ShouldBeTrue();
                foreach (var control in new[] { QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_USBTRAFFIC, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_SPEED, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_DDR })
                {
                    var available = QHYCCD.SDK.QHYCamera.IsQHYCCDControlAvailable(handle, control) is 0;
                    QHYCCD.SDK.QHYCamera.GetQHYCCDParamMinMaxStep(handle, control, out var min, out var max, out var step);
                    output.WriteLine($"{control}: available {available}, range {min} to {max} step {step}, now {QHYCCD.SDK.QHYCamera.GetQHYCCDParam(handle, control)}");
                }

                QHYCCD.SDK.QHYCamera.GetQHYCCDChipInfo(handle, out _, out _, out var w, out var h, out _, out _, out _);
                QHYCCD.SDK.QHYCamera.SetQHYCCDBinMode(handle, 1, 1);
                QHYCCD.SDK.QHYCamera.SetQHYCCDBitsMode(handle, 8);
                QHYCCD.SDK.QHYCamera.SetQHYCCDResolution(handle, 0, 0, w, h);
                QHYCCD.SDK.QHYCamera.SetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_EXPOSURE, 1000);
                QHYCCD.SDK.QHYCamera.SetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_GAIN, 1);
                var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)QHYCCD.SDK.QHYCamera.GetQHYCCDMemLength(handle));
                try
                {
                    QHYCCD.SDK.QHYCamera.GetQHYCCDParamMinMaxStep(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_USBTRAFFIC, out var tMin, out var tMax, out _);
                    // Exposure and window: if the frame time stays put as the exposure grows and a small window is much
                    // faster, the pace is the readout or the link, not the exposure.
                    foreach (var (ew, eh, exposureUs) in new (uint, uint, double)[] { (w, h, 1_000), (w, h, 50_000), (w, h, 120_000), (640, 480, 1_000) })
                    {
                        QHYCCD.SDK.QHYCamera.SetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_USBTRAFFIC, tMin);
                        QHYCCD.SDK.QHYCamera.SetQHYCCDResolution(handle, (w - ew) / 2, (h - eh) / 2, ew, eh);
                        QHYCCD.SDK.QHYCamera.SetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_EXPOSURE, exposureUs);
                        (QHYCCD.SDK.QHYCamera.BeginQHYCCDLive(handle) is 0).ShouldBeTrue();
                        var sweep = Stopwatch.StartNew();
                        var times = new System.Collections.Generic.List<double>();
                        while (sweep.Elapsed < TimeSpan.FromSeconds(5))
                        {
                            if (QHYCCD.SDK.QHYCamera.GetQHYCCDLiveFrame(handle, out _, out _, out _, out _, buffer) is 0)
                            {
                                times.Add(sweep.Elapsed.TotalMilliseconds);
                            }
                            else
                            {
                                Thread.Sleep(1);
                            }
                        }

                        QHYCCD.SDK.QHYCamera.StopQHYCCDLive(handle);
                        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).Order().ToArray();
                        output.WriteLine($"{ew} x {eh}, exposure {exposureUs / 1000:F0} ms: {times.Count} frames in 5 s, "
                            + (gaps.Length > 0 ? $"frame gap median {gaps[gaps.Length / 2]:F0} ms ({1000 / gaps[gaps.Length / 2]:F1} fps), min {gaps[0]:F0}, max {gaps[^1]:F0}" : "no gaps"));
                    }

                    QHYCCD.SDK.QHYCamera.SetQHYCCDResolution(handle, 0, 0, w, h);
                    QHYCCD.SDK.QHYCamera.SetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_EXPOSURE, 1000);
                    QHYCCD.SDK.QHYCamera.GetQHYCCDParamMinMaxStep(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_SPEED, out var sMin, out var sMax, out _);
                    foreach (var (traffic, speed) in new[] { (tMin, sMin), (tMax, sMin), (tMin, sMin + 1), (tMin, sMax) })
                    {
                        QHYCCD.SDK.QHYCamera.SetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_USBTRAFFIC, traffic);
                        var speedSet = QHYCCD.SDK.QHYCamera.SetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_SPEED, speed);
                        output.WriteLine($"speed {speed}: set returned {speedSet}, reads back {QHYCCD.SDK.QHYCamera.GetQHYCCDParam(handle, QHYCCD.SDK.QHYCamera.CONTROL_ID.CONTROL_SPEED)}");
                        (QHYCCD.SDK.QHYCamera.BeginQHYCCDLive(handle) is 0).ShouldBeTrue();
                        var clock = Stopwatch.StartNew();
                        int frames = 0, polls = 0;
                        double firstMs = double.NaN;
                        while (clock.Elapsed < TimeSpan.FromSeconds(4))
                        {
                            polls++;
                            if (QHYCCD.SDK.QHYCamera.GetQHYCCDLiveFrame(handle, out _, out _, out _, out _, buffer) is 0)
                            {
                                frames++;
                                if (double.IsNaN(firstMs))
                                {
                                    firstMs = clock.Elapsed.TotalMilliseconds;
                                }
                            }
                            else
                            {
                                Thread.Sleep(1);
                            }
                        }

                        QHYCCD.SDK.QHYCamera.StopQHYCCDLive(handle);
                        output.WriteLine($"USB traffic {traffic}, speed {speed}: first frame {firstMs:F0} ms, {frames} frames in 4 s ({(frames - 1) / ((4000 - firstMs) / 1000):F1} fps), {polls} polls");
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                QHYCCD.SDK.QHYCamera.CloseQHYCCD(handle);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(idPtr);
        }
    }

    private async Task<(int Frames, double Fps, int Distinct)> StreamAsync(QHYCameraDriver camera, VideoCaptureOptions options, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();
        TimeSpan? first = null;
        var frames = 0;
        var distinct = 0;
        double previousSum = double.NaN;
        float median = 0, max = 0;
        await foreach (var frame in camera.CaptureVideoAsync(options, stop.Token))
        {
            try
            {
                first ??= clock.Elapsed;
                frames++;
                var plane = frame.GetChannelSpan(0);
                double sum = 0;
                for (var i = 0; i < plane.Length; i += 97)
                {
                    sum += plane[i];
                }

                if (sum != previousSum)
                {
                    distinct++;
                    previousSum = sum;
                }

                if (frames == 10)
                {
                    var sorted = plane.ToArray();
                    Array.Sort(sorted);
                    median = sorted[sorted.Length / 2];
                    max = sorted[^1];
                    output.WriteLine($"{label}: frame 10 is {frame.Width} x {frame.Height}, median {median}, max {max}, full scale {frame.ImageMeta.SensorFullScaleAdu}");
                }
            }
            finally
            {
                frame.Release();
            }

            if (clock.Elapsed - first >= StreamFor)
            {
                break;
            }
        }

        var fps = (frames - 1) / (clock.Elapsed - (first ?? TimeSpan.Zero)).TotalSeconds;
        output.WriteLine($"{label}: first frame after {first?.TotalMilliseconds:F0} ms, {fps:F1} fps ({frames} frames, {distinct} distinct), dropped {camera.DroppedFrames}");
        return (frames, fps, distinct);
    }

    private async Task ExposeAsync(QHYCameraDriver camera, TimeSpan exposure, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await camera.StartExposureAsync(exposure, cancellationToken: ct);
        var ready = await camera.WaitForImageReadyAsync(exposure, SystemTimeProvider.Instance, TimeSpan.FromMilliseconds(20),
            timeout: exposure + TimeSpan.FromSeconds(15), cancellationToken: ct);
        ready.ShouldBeTrue($"{label} arrived");
        var image = (await ((ICameraDriver)camera).GetImageAsync(ct)).ShouldNotBeNull();
        try
        {
            var plane = image.GetChannelSpan(0).ToArray();
            Array.Sort(plane);
            output.WriteLine($"{label}: {image.Width} x {image.Height}, ready after {watch.ElapsedMilliseconds} ms, median {plane[plane.Length / 2]}, max {image.MaxValue}");
            (image.Width, image.Height).ShouldBe((camera.CameraXSize, camera.CameraYSize));
        }
        finally
        {
            image.Release();
        }
    }
}
