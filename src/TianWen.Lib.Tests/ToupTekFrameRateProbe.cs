using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.ToupTek;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using ToupTek.SDK;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Diagnostic probe, not an assertion: the frame rate TianWen's own path takes from a real ToupTek camera at the
/// 2026-10-07 Saturn region (640 x 480 at 1716, 546, bin 1), one layer at a time, in this process and with no node.
/// A ToupTek body streams no video here: ToupTek.SDK leaves <see cref="ICMOSNativeInterface.CanVideoCapture"/> false and
/// takes each frame by the SDK's software trigger, so a live capture is <see cref="PlanetaryCapture"/>'s rapid-exposure
/// loop (expose, poll, read, repeat). The layers: the bare driver polled as fast as it answers, the bare driver polled at
/// the capture loop's own 5 ms sleep, and the capture loop whole. The camera's own ceiling, its video mode, was measured
/// beside this by a scratch console on the vendor's binding: docs/plans/live-planetary-capture.md, "Frame rate at the
/// Saturn ROI on the ToupTek (measured 2026-10-14)".
/// </summary>
/// <remarks>
/// Set <c>TIANWEN_TOUPTEK_RATE_PROBE=1</c> with a ToupTek camera attached and no other program holding it. It sets the
/// camera's gain, exposure, depth and region and nothing else: unlike <see cref="ToupTekResetRecoveryProbe"/> it never
/// resets the camera. Real clock, since a frame rate is wall-clock time by nature.
/// </remarks>
[Collection("Devices")]
public class ToupTekFrameRateProbe(ITestOutputHelper output)
{
    private const string GateVar = "TIANWEN_TOUPTEK_RATE_PROBE";
    private const int RoiX = 1716;
    private const int RoiY = 546;
    private const int RoiWidth = 640;
    private const int RoiHeight = 480;

    /// <summary>The 2026-10-07 night's gain from 11:47 UTC on (SharpCap's Analogue Gain, the SDK's percent).</summary>
    private const short SaturnGain = 2252;

    private static readonly TimeSpan Warm = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Measured = TimeSpan.FromSeconds(3);

    /// <summary>The sleep between ready polls in <see cref="PlanetaryCapture"/>'s rapid-exposure loop.</summary>
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(5);

    [Fact(Timeout = 600_000)]
    public async Task WhatTheTriggerLoopDeliversAtTheSaturnRoi()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached ToupTek camera)");
        var ct = TestContext.Current.CancellationToken;
        var device = new ToupTekDeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault();
        Assert.SkipWhen(device is null, "no ToupTek camera attached");

        var external = new FakeExternal(output);
        using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, false)).SetMinimumLevel(LogLevel.Warning))
            .BuildServiceProvider();
        var camera = new ToupTekCameraDriver(device, sp);
        await camera.ConnectAsync(ct);
        try
        {
            // Which loop this measures: a ToupTek body never streams, so the capture takes the rapid-exposure fallback.
            camera.ShouldBeAssignableTo<IVideoCameraDriver>().ShouldNotBeNull().CanVideoCapture.ShouldBeFalse(
                "ToupTek.SDK runs the software trigger, one frame per Toupcam_Trigger, so PlanetaryCapture takes its rapid-exposure loop");

            var minimum = SdkMinimumExposure();
            if (SaturnGain >= camera.GainMin && SaturnGain <= camera.GainMax)
            {
                await camera.SetGainAsync(SaturnGain, ct);
            }
            camera.BinX = 1;
            camera.NumX = RoiWidth;
            camera.NumY = RoiHeight;
            camera.StartX = RoiX;
            camera.StartY = RoiY;
            output.WriteLine($"{camera.Name}: {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY}), gain {await camera.GetGainAsync(ct)} "
                + $"(range {camera.GainMin} to {camera.GainMax}, percent), SDK minimum exposure {minimum.TotalMicroseconds} us, "
                + "speed never set by the binding (the camera's default)");

            TimeSpan[] exposures =
            [
                TimeSpan.FromMicroseconds(15_625), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1), minimum,
            ];

            var rows = new List<Row>();
            foreach (var depth in new[] { BitDepth.Int8, BitDepth.Int16 })
            {
                await camera.SetBitDepthAsync(depth, ct);
                (await camera.GetBitDepthAsync(ct)).ShouldBe(depth);
                foreach (var exposure in exposures)
                {
                    rows.Add(await MeasureDriverAsync(camera, depth, exposure, pollSleep: null, ct));
                    rows.Add(await MeasureDriverAsync(camera, depth, exposure, CapturePollInterval, ct));
                    rows.Add(await MeasureCaptureAsync(camera, depth, exposure, external.AppLogger, ct));
                    await SettleAsync(camera, ct);
                }
            }

            output.WriteLine("");
            output.WriteLine($"{"exposure",10} {"depth",5} {"layer",-34} {"fps",7} {"frames",6} {"lost",4}  detail");
            foreach (var row in rows)
            {
                output.WriteLine($"{FormatExposure(row.Exposure),10} {row.Depth.BitSize,5} {row.Layer,-34} {row.Fps,7:F1} {row.Frames,6} {row.Lost,4}  {row.Detail}");
            }

            // Only what is always true: every layer had frames, of the region asked for, at the depth asked for.
            foreach (var row in rows)
            {
                row.Frames.ShouldBeGreaterThan(0, $"{row.Layer} at {FormatExposure(row.Exposure)}, {row.Depth}");
                row.Size.ShouldBe((RoiWidth, RoiHeight), $"{row.Layer} at {FormatExposure(row.Exposure)}, {row.Depth}");
                row.FrameDepth.ShouldBe(row.Depth, $"{row.Layer} at {FormatExposure(row.Exposure)}");
            }
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    private sealed record Row(string Layer, BitDepth Depth, TimeSpan Exposure, double Fps, int Frames, int Lost,
        (int Width, int Height)? Size, BitDepth? FrameDepth, string Detail);

    /// <summary>
    /// The bare driver's single-exposure loop: start, poll until ready (as fast as it answers, or sleeping
    /// <paramref name="pollSleep"/> between polls as the capture loop does), read, release, repeat. An exposure that ends
    /// with no frame is counted lost and the next one started.
    /// </summary>
    private static async Task<Row> MeasureDriverAsync(ICameraDriver camera, BitDepth depth, TimeSpan exposure, TimeSpan? pollSleep, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan? measuredFrom = null;
        var frames = 0;
        var lost = 0;
        var startToReady = new List<double>();
        var reads = new List<double>();
        (int, int)? size = null;
        BitDepth? frameDepth = null;
        while (measuredFrom is null || clock.Elapsed - measuredFrom.Value < Measured)
        {
            var started = Stopwatch.GetTimestamp();
            await camera.StartExposureAsync(exposure, FrameType.Light, ct);
            if (!await WaitForFrameAsync(camera, pollSleep, ct))
            {
                if (measuredFrom is not null)
                {
                    lost++;
                }
                continue;
            }

            var ready = Stopwatch.GetTimestamp();
            var image = await camera.GetImageAsync(ct);
            var read = Stopwatch.GetTimestamp();
            if (image is not null)
            {
                size = (image.Width, image.Height);
                frameDepth = image.BitDepth;
                image.Release();
            }

            if (measuredFrom is null)
            {
                if (clock.Elapsed >= Warm)
                {
                    measuredFrom = clock.Elapsed;
                }
            }
            else if (image is not null)
            {
                frames++;
                startToReady.Add(Stopwatch.GetElapsedTime(started, ready).TotalMilliseconds);
                reads.Add(Stopwatch.GetElapsedTime(ready, read).TotalMilliseconds);
            }
        }

        var seconds = (clock.Elapsed - (measuredFrom ?? TimeSpan.Zero)).TotalSeconds;
        var layer = pollSleep is { } sleep ? $"driver, {sleep.TotalMilliseconds:F0} ms sleep between polls" : "driver, polled without a sleep";
        return new Row(layer, depth, exposure, frames / seconds, frames, lost, size, frameDepth,
            $"start to ready p50 {Median(startToReady):F2} ms, read p50 {Median(reads):F2} ms");
    }

    /// <summary>
    /// <see cref="PlanetaryCapture"/> whole, on its rapid-exposure loop: ring, recording hook, recenter check and host
    /// callback, as a live capture runs it. Its exposure floor is 1 ms, so the SDK's minimum is asked for and 1 ms taken.
    /// </summary>
    private static async Task<Row> MeasureCaptureAsync(ICameraDriver camera, BitDepth depth, TimeSpan exposure, ILogger logger, CancellationToken ct)
    {
        var frames = 0;
        var width = 0;
        var height = 0;
        var frameDepth = 0;
        await using var capture = new PlanetaryCapture(SystemTimeProvider.Instance, logger, onFrame: frame =>
        {
            Volatile.Write(ref width, frame.Width);
            Volatile.Write(ref height, frame.Height);
            Volatile.Write(ref frameDepth, (int)frame.BitDepth);
            Interlocked.Increment(ref frames);
        });
        capture.Start(camera, new VideoCaptureOptions(exposure), ct).ShouldBeTrue();
        await SystemTimeProvider.Instance.SleepAsync(Warm, ct);
        var firstAfterWarm = Volatile.Read(ref frames);
        var measuring = Stopwatch.StartNew();
        await SystemTimeProvider.Instance.SleepAsync(Measured, ct);
        var taken = Volatile.Read(ref frames) - firstAfterWarm;
        var seconds = measuring.Elapsed.TotalSeconds;
        var heartbeatFps = capture.MeasuredFps;
        await capture.StopAsync(ct);

        var detail = $"the capture's own figure {heartbeatFps:F1} fps"
            + (exposure < TimeSpan.FromMilliseconds(1) ? ", exposure floored to 1 ms by the capture" : "")
            + (capture.FailureReason is { } failure ? $", FAILED: {failure}" : "");
        return new Row("PlanetaryCapture rapid-exposure loop", depth, exposure, taken / seconds, taken, 0,
            width > 0 ? (width, height) : null, frameDepth != 0 ? (BitDepth)frameDepth : null, detail);
    }

    /// <summary>
    /// Waits for the exposure in flight. False when it ended without a frame: the driver gave it up, or the SDK failed it.
    /// </summary>
    private static async Task<bool> WaitForFrameAsync(ICameraDriver camera, TimeSpan? pollSleep, CancellationToken ct)
    {
        while (!await camera.GetImageReadyAsync(ct))
        {
            if (await camera.GetCameraStateAsync(ct) is not CameraState.Exposing)
            {
                // The state read can itself be what found the frame, so the answer is asked once more.
                return await camera.GetImageReadyAsync(ct);
            }

            if (pollSleep is { } sleep)
            {
                await SystemTimeProvider.Instance.SleepAsync(sleep, ct);
            }
            else
            {
                await Task.Yield();
            }
        }

        return true;
    }

    /// <summary>
    /// A stopped capture can leave an exposure in flight (its loop ends inside the poll), and the trigger's frame would
    /// otherwise answer the next layer's first exposure: it is waited for and dropped.
    /// </summary>
    private static async Task SettleAsync(ICameraDriver camera, CancellationToken ct)
    {
        if (await camera.GetCameraStateAsync(ct) is CameraState.Exposing && await WaitForFrameAsync(camera, CapturePollInterval, ct))
        {
            (await camera.GetImageAsync(ct))?.Release();
        }
    }

    /// <summary>The SDK's own exposure floor (<c>get_ExpTimeRange</c>), read through the session the driver holds open.</summary>
    private static TimeSpan SdkMinimumExposure()
    {
        foreach (var info in new DeviceIterator())
        {
            if (info.TryGetControlRange(CMOSControlType.Exposure, out var minUs, out _))
            {
                return TimeSpan.FromMicroseconds(minUs);
            }
        }

        throw new InvalidOperationException("no open ToupTek camera answered its exposure range");
    }

    private static string FormatExposure(TimeSpan exposure)
        => exposure < TimeSpan.FromMilliseconds(1) ? $"{exposure.TotalMicroseconds:F0} us" : $"{exposure.TotalMilliseconds:0.###} ms";

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
