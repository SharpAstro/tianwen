using Microsoft.Extensions.DependencyInjection;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.ZWO;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Diagnostic probe, not an assertion: where a real ZWO camera's full-frame video rate goes, one layer at a time, in this
/// process and with no node, stream or shared memory. Each layer is what the capture loop does to a frame, added on top
/// of the one before: the driver's stream alone, the frame ring's copy, a recording's conversion, and the capture loop
/// whole. Raw ZWOptical.SDK video is the baseline, measured 2026-09-28 on an ASI462MC at full frame: 136 frames a second
/// in RAW8 at USB bandwidth 100 with the high-speed readout, 63.8 in RAW16 at 100, and half of each at 50. A stream takes
/// the whole bandwidth and asks for its own depth (<see cref="VideoCaptureOptions.BitDepth"/>), never the camera's setting.
/// </summary>
/// <remarks>
/// Set <c>TIANWEN_ZWO_VIDEO_PROBE=1</c> with a ZWO camera attached and no other program holding it.
/// </remarks>
[Collection("Devices")]
public class ZwoVideoRateProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_ZWO_VIDEO_PROBE";
    private static readonly TimeSpan Warm = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Measured = TimeSpan.FromSeconds(8);

    [Theory(Timeout = 600_000)]
    [InlineData(BitDepth.Int8)]
    [InlineData(BitDepth.Int16)]
    public async Task WhereTheFullFrameRateGoes(BitDepth depth)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is "1", $"{EnvVar} not set");
        var ct = TestContext.Current.CancellationToken;

        var external = new FakeExternal(output);
        await using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddSingleton<IDeviceHub, DeviceHub>()
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, appendScope: false)).SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider();

        var device = new ZWODeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault().ShouldNotBeNull("a ZWO camera is attached");
        device.TryInstantiateDriver<ICameraDriver>(sp, out var camera).ShouldBeTrue();
        await using var connected = camera.ShouldNotBeNull();
        await camera.ConnectAsync(ct);
        var video = camera.ShouldBeAssignableTo<IVideoCameraDriver>().ShouldNotBeNull();
        video.CanVideoCapture.ShouldBeTrue();
        var options = new VideoCaptureOptions(TimeSpan.FromMilliseconds(1), BitDepth: depth);
        camera.BinX = 1;
        camera.BinY = 1;
        camera.NumX = camera.CameraXSize;
        camera.NumY = camera.CameraYSize;
        output.WriteLine($"{camera.Name}, {camera.NumX}x{camera.NumY}, {depth}, server GC {System.Runtime.GCSettings.IsServerGC}");

        var rows = new List<string>();

        // 1. The driver's stream alone: each frame released as it comes.
        rows.Add(await MeasureAsync("driver only", video, options, ct, frame => { }));

        // 2. The frame ring as the capture sizes it (1024 frames, still filling after the whole measurement), and a
        //    small one (64) that wraps at once, so its planes are recycled rather than allocated.
        foreach (var capacity in new[] { 1024, 64 })
        {
            LiveCameraFrameStream? ring = null;
            var pushes = new List<double>();
            try
            {
                rows.Add(await MeasureAsync($"+ ring push ({capacity})", video, options, ct, frame =>
                {
                    ring ??= NewRing(frame, capacity);
                    var start = Stopwatch.GetTimestamp();
                    ring.Push(frame, DateTimeOffset.UtcNow);
                    pushes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }) + $", push {Percentiles(pushes)}, {ring?.PlanesAllocated} planes allocated");
            }
            finally
            {
                ring?.Dispose();
            }
            GC.Collect();
        }

        // 3. A recording's conversion (the capture loop's half of SerRecording: 16 bits a sample, queued).
        {
            var path = Path.Combine(external.AppDataFolder.FullName, $"probe-{depth}.ser");
            var recording = new SerRecording(path, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), external.AppLogger);
            var appends = new List<double>();
            try
            {
                rows.Add(await MeasureAsync("+ recording append", video, options, ct, frame =>
                {
                    var start = Stopwatch.GetTimestamp();
                    recording.TryAppend(frame, DateTimeOffset.UtcNow);
                    appends.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }) + $", append {Percentiles(appends)}");
            }
            finally
            {
                recording.End("the probe is done");
                await recording.Completion.WaitAsync(ct);
                rows[^1] += $", {recording.FramesWritten} written, {recording.FramesDropped} dropped by the disk";
                File.Delete(path);
            }
        }

        // 4. The capture loop whole (PlanetaryCapture: ring, recenter, host callback, controls), recenter off and on.
        foreach (var recenter in new[] { false, true })
        {
            var frames = 0;
            await using var capture = new PlanetaryCapture(SystemTimeProvider.Instance, external.AppLogger,
                onFrame: _ => Interlocked.Increment(ref frames));
            capture.ConfigureRecenter(auto: recenter, mountJog: false, deadbandPixels: 4, gain: 0.5);
            capture.Start(camera, options, ct).ShouldBeTrue();
            await SystemTimeProvider.Instance.SleepAsync(Warm, ct);
            var firstAfterWarm = Volatile.Read(ref frames);
            var measuring = Stopwatch.StartNew();
            await SystemTimeProvider.Instance.SleepAsync(Measured, ct);
            var taken = Volatile.Read(ref frames) - firstAfterWarm;
            var seconds = measuring.Elapsed.TotalSeconds;
            await capture.StopAsync(ct);
            rows.Add($"{"capture loop, recenter " + (recenter ? "on" : "off"),-28} {taken / seconds,6:F1} fps");
            GC.Collect();
        }

        // 5. The capture loop with the node's live stack beside it (NodePlanetaryRun.StackAsync's cadence, a master of the
        //    window ending at the newest frame, 250 ms after the last one), recenter off, then on: the node's default.
        foreach (var recenter in new[] { false, true })
        {
            var frames = 0;
            await using var capture = new PlanetaryCapture(SystemTimeProvider.Instance, external.AppLogger,
                onFrame: _ => Interlocked.Increment(ref frames));
            capture.ConfigureRecenter(auto: recenter, mountJog: false, deadbandPixels: 4, gain: 0.5);
            capture.Start(camera, options, ct).ShouldBeTrue();
            using var stopStack = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stacks = new List<double>();
            var window = 0;
            var stacking = Task.Run(async () =>
            {
                RollingWindowStacker? stacker = null;
                LiveCameraFrameStream? stacked = null;
                var built = -1;
                while (!stopStack.IsCancellationRequested)
                {
                    await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(250), stopStack.Token);
                    if (capture.Stream is not { } stream)
                    {
                        continue;
                    }
                    if (stacker is null || !ReferenceEquals(stream, stacked))
                    {
                        stacker = new RollingWindowStacker(stream);
                        stacked = stream;
                    }
                    var latest = stream.LatestIndex;
                    if (latest <= built)
                    {
                        continue;
                    }
                    var start = Stopwatch.GetTimestamp();
                    var master = await stacker.StackToAsync(latest, stopStack.Token);
                    stacks.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    window = stacker.WindowFrameCount;
                    built = latest;
                    master.Release();
                }
            }, stopStack.Token);
            await SystemTimeProvider.Instance.SleepAsync(Warm, ct);
            var firstAfterWarm = Volatile.Read(ref frames);
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var measuring = Stopwatch.StartNew();
            await SystemTimeProvider.Instance.SleepAsync(Measured, ct);
            var taken = Volatile.Read(ref frames) - firstAfterWarm;
            var seconds = measuring.Elapsed.TotalSeconds;
            var cores = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds / seconds;
            await stopStack.CancelAsync();
            try
            {
                await stacking;
            }
            catch (OperationCanceledException)
            {
            }
            await capture.StopAsync(ct);
            rows.Add($"{"+ node's stack, recenter " + (recenter ? "on" : "off"),-28} {taken / seconds,6:F1} fps, {stacks.Count} masters, a master {Percentiles(stacks)}, "
                + $"window {window} frames, {cores:F1} cores busy");
            GC.Collect();
        }

        foreach (var row in rows)
        {
            output.WriteLine(row);
        }
    }

    private static LiveCameraFrameStream NewRing(Image frame, int capacity)
    {
        var isBayer = frame.ChannelCount == 1 && frame.ImageMeta.SensorType == SensorType.RGGB;
        var layout = frame.ChannelCount >= 3 ? PlanetaryFrameLayout.Rgb : isBayer ? PlanetaryFrameLayout.SplitCfa : PlanetaryFrameLayout.Mono;
        var (w, h) = layout == PlanetaryFrameLayout.SplitCfa ? (frame.Width / 2, frame.Height / 2) : (frame.Width, frame.Height);
        return new LiveCameraFrameStream(w, h, layout, capacity);
    }

    /// <summary>Reads the driver's stream for the warm-up and then the measured time, doing <paramref name="work"/> to each frame.</summary>
    private static async Task<string> MeasureAsync(string what, IVideoCameraDriver video, VideoCaptureOptions options, CancellationToken ct, Action<Image> work)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();
        var frames = 0;
        TimeSpan? measuredFrom = null;
        await foreach (var frame in video.CaptureVideoAsync(options, stop.Token))
        {
            try
            {
                work(frame);
            }
            finally
            {
                frame.Release();
            }

            if (measuredFrom is null)
            {
                if (clock.Elapsed >= Warm)
                {
                    measuredFrom = clock.Elapsed;
                }
            }
            else
            {
                frames++;
                if (clock.Elapsed - measuredFrom >= Measured)
                {
                    break;
                }
            }
        }
        var seconds = (clock.Elapsed - (measuredFrom ?? TimeSpan.Zero)).TotalSeconds;
        return $"{what,-28} {frames / seconds,6:F1} fps";
    }

    private static string Percentiles(List<double> ms)
    {
        if (ms.Count == 0)
        {
            return "none";
        }
        var sorted = ms.Order().ToArray();
        double At(double p) => sorted[(int)Math.Round(p * (sorted.Length - 1))];
        return $"p50 {At(0.5):F1} / p90 {At(0.9):F1} / max {sorted[^1]:F1} ms";
    }
}
