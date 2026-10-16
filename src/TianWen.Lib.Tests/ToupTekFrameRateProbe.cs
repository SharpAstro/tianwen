using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpAstro.Ser;
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
/// Two paths. The software trigger, one exposure per frame (expose, poll, read, repeat), is what a single exposure
/// takes and what TIA1 measured: docs/plans/live-planetary-capture.md, "Frame rate at the Saturn ROI on the ToupTek
/// (measured 2026-10-14)". Video, the camera free-running since ToupTek.SDK 1.2, is what a live capture takes: the
/// driver's stream alone, then each thing the capture loop does to a frame added on top (as <see cref="ZwoVideoRateProbe"/>
/// layers a ZWO), the capture loop whole, and a recording read back off its SER.
/// </summary>
/// <remarks>
/// Set <c>TIANWEN_TOUPTEK_RATE_PROBE=1</c> with a ToupTek camera attached and no other program holding it. It sets the
/// camera's gain, exposure, depth and region; the black level stays where the driver's connect puts it, a tenth of its
/// range. It never asks for a reset, as <see cref="ToupTekResetRecoveryProbe"/> does, but the DAL driver may make one
/// itself: a stalled stream's last rung, and a body that loses two single exposures in a row. Real clock, since a frame
/// rate is wall-clock time by nature. The recording arm writes about 3.6 GB a minute to the test's temp folder and
/// deletes it once read; <c>TIANWEN_TOUPTEK_RECORD_RUNS</c> (3) and <c>TIANWEN_TOUPTEK_RECORD_SECONDS</c> (60) size it. The soak
/// runs only when <c>TIANWEN_TOUPTEK_SOAK_MINUTES</c> is set.
/// </remarks>
[Collection("Devices")]
public class ToupTekFrameRateProbe(ITestOutputHelper output)
{
    internal const string GateVar = "TIANWEN_TOUPTEK_RATE_PROBE";
    internal const int RoiX = 1716;
    internal const int RoiY = 546;
    internal const int RoiWidth = 640;
    internal const int RoiHeight = 480;

    /// <summary>The 2026-10-07 night's gain from 11:47 UTC on (SharpCap's Analogue Gain, the SDK's percent).</summary>
    internal const short SaturnGain = 2252;

    /// <summary>
    /// The longest step between two of a recording's frame timestamps the bar allows (TIA2): 20 frame times at 5 ms, a third
    /// of the SDK's 64-frame ring, so a step this long is the driver's own reading stopped for that long. A recovered stall
    /// (about 6.5 s) is reported apart.
    /// </summary>
    internal static readonly TimeSpan LongStepBound = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Warm = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Measured = TimeSpan.FromSeconds(3);

    /// <summary>How long each video layer is read: long enough that a reader's rare pause shows as a replaced frame.</summary>
    private static readonly TimeSpan VideoMeasured = TimeSpan.FromSeconds(20);

    /// <summary>The Saturn night's exposure, at which the camera's readout (4.98 ms at speed 2) fits inside the frame time.</summary>
    internal static readonly TimeSpan SaturnExposure = TimeSpan.FromMilliseconds(5);

    /// <summary>The sleep between ready polls in <see cref="PlanetaryCapture"/>'s rapid-exposure loop.</summary>
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(5);

    [Fact(Timeout = 600_000)]
    public async Task WhatTheTriggerPathDeliversAtTheSaturnRoi()
    {
        var (camera, external, sp) = await ConnectAsync(output, LogLevel.Warning);
        await using var _ = sp;
        try
        {
            var minimum = SdkMinimumExposure();
            output.WriteLine($"{camera.Name}: {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY}), gain {await camera.GetGainAsync(TestContext.Current.CancellationToken)} "
                + $"(range {camera.GainMin} to {camera.GainMax}, percent), SDK minimum exposure {minimum.TotalMicroseconds} us, "
                + "speed never set by the binding (the camera's default)");

            TimeSpan[] exposures =
            [
                TimeSpan.FromMicroseconds(15_625), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1), minimum,
            ];

            var ct = TestContext.Current.CancellationToken;
            var rows = new List<Row>();
            foreach (var depth in new[] { BitDepth.Int8, BitDepth.Int16 })
            {
                await camera.SetBitDepthAsync(depth, ct);
                (await camera.GetBitDepthAsync(ct)).ShouldBe(depth);
                foreach (var exposure in exposures)
                {
                    rows.Add(await MeasureDriverAsync(camera, depth, exposure, pollSleep: null, ct));
                    rows.Add(await MeasureDriverAsync(camera, depth, exposure, CapturePollInterval, ct));
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
            await camera.DisconnectAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Where a 5 ms frame's time goes on the video path, one layer at a time: the driver's stream alone, plus the frame
    /// ring's copy, plus a recording's conversion (both as the capture loop does them, on the reader), then
    /// <see cref="PlanetaryCapture"/> whole. Each row says what the reader's work cost a frame and how often the hand-off
    /// between the driver's thread and the reader gave a frame up because the reader was still busy.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task WhereTheVideoRateGoesAtTheSaturnRoi()
    {
        var (camera, external, sp) = await ConnectAsync(output, LogLevel.Warning);
        await using var _ = sp;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var options = new VideoCaptureOptions(SaturnExposure, Gain: SaturnGain, BitDepth: BitDepth.Int8);
            output.WriteLine($"{camera.Name}: video, {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY}), 8 bits, {SaturnExposure.TotalMilliseconds} ms, gain {SaturnGain}, "
                + $"{VideoMeasured.TotalSeconds:F0} s a layer, server GC {System.Runtime.GCSettings.IsServerGC}");

            var rows = new List<string>();
            rows.Add(await MeasureStreamAsync("driver only", camera, options, ct, frame => { }));

            {
                LiveCameraFrameStream? ring = null;
                var pushes = new List<double>();
                try
                {
                    rows.Add(await MeasureStreamAsync("+ ring push (1024)", camera, options, ct, frame =>
                    {
                        ring ??= new LiveCameraFrameStream(frame.Width, frame.Height, PlanetaryFrameLayout.Mono, 1024);
                        var start = Stopwatch.GetTimestamp();
                        ring.Push(frame, frame.ImageMeta.ExposureStartTime);
                        pushes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }) + $", push {Percentiles(pushes)}");
                }
                finally
                {
                    ring?.Dispose();
                }
                GC.Collect();
            }

            {
                LiveCameraFrameStream? ring = null;
                var path = Path.Combine(external.ImageOutputFolder.FullName, "probe-append.ser");
                var recording = new SerRecording(path, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), external.AppLogger);
                var appends = new List<double>();
                try
                {
                    rows.Add(await MeasureStreamAsync("+ ring push + recording", camera, options, ct, frame =>
                    {
                        ring ??= new LiveCameraFrameStream(frame.Width, frame.Height, PlanetaryFrameLayout.Mono, 1024);
                        ring.Push(frame, frame.ImageMeta.ExposureStartTime);
                        var start = Stopwatch.GetTimestamp();
                        recording.TryAppend(frame, frame.ImageMeta.ExposureStartTime);
                        appends.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }) + $", append {Percentiles(appends)}");
                }
                finally
                {
                    ring?.Dispose();
                    recording.End("the probe is done");
                    await recording.Completion.WaitAsync(ct);
                    rows[^1] += $", {recording.FramesWritten} written, {recording.FramesDropped} dropped by the disk";
                    File.Delete(path);
                }
                GC.Collect();
            }

            {
                var result = await CaptureAsync(camera, options, record: null, external.AppLogger, ct);
                rows.Add($"{"capture loop whole",-30} {result.Fps,6:F1} fps, {result.Detail}");
            }

            foreach (var row in rows)
            {
                output.WriteLine(row);
            }
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    /// <summary>
    /// The bar the video path is held to (TIA2): a recording through <see cref="PlanetaryCapture"/> at the Saturn
    /// window, 5 ms and 8 bits, read back off the SER it wrote, frame count over the SER's own timestamp span, each run a
    /// minute. Printed per run: that rate, the frames the hand-off replaced, the body's drops and the disk's, and every
    /// stall the stream recovered from with its gap.
    /// </summary>
    [Fact(Timeout = 1_800_000)]
    public async Task ARecordingAtTheSaturnWindowKeepsEveryFrame()
    {
        var (camera, external, sp) = await ConnectAsync(output, LogLevel.Information);
        await using var _ = sp;
        var ct = TestContext.Current.CancellationToken;
        var runs = IntFromEnvironment("TIANWEN_TOUPTEK_RECORD_RUNS", 3);
        var seconds = IntFromEnvironment("TIANWEN_TOUPTEK_RECORD_SECONDS", 60);
        // A run the camera stalled in is reported apart and made again, as the node rate probe does: the camera stalls in
        // periods, and a stall costs a recording about 1,300 frames whatever the capture does.
        var extra = IntFromEnvironment("TIANWEN_TOUPTEK_EXTRA_RUNS", 2);
        try
        {
            var options = SaturnOptions;
            var lines = new List<string>();
            var clean = 0;
            for (var run = 1; clean < runs && run <= runs + extra; run++)
            {
                var path = Path.Combine(external.ImageOutputFolder.FullName, $"probe-run{run}.ser");
                var quiet = await OtherWorktreeBuilds.WaitUntilQuietAsync(ct);
                var load = HostCpuLoad.Start();
                var result = await CaptureAsync(camera, options, (path, TimeSpan.FromSeconds(seconds)), external.AppLogger, ct);
                var stalled = result.Stalls > 0 ? $" (STALLED, {result.Stalls} logged: reported apart)" : "";
                clean += result.Stalls > 0 ? 0 : 1;
                lines.Add($"run {run}{stalled}: {result.Detail}; {load.Describe()}; before it, {quiet}");
                File.Delete(path);
                GC.Collect();
            }

            output.WriteLine("");
            output.WriteLine($"{camera.Name}: recording through the capture loop, {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY}), 8 bits, "
                + $"{SaturnExposure.TotalMilliseconds} ms, gain {SaturnGain}, {seconds} s a run");
            foreach (var line in lines)
            {
                output.WriteLine(line);
            }
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    /// <summary>
    /// The capture loop left streaming for <c>TIANWEN_TOUPTEK_SOAK_MINUTES</c> (skipped unless set) at the Saturn window,
    /// with no recording, to meet the stalls this camera has about once in 1,250 s at speed 2 and see the driver bring
    /// each back: every stall printed with the rung that undid it and the gap it left.
    /// </summary>
    [Fact(Timeout = 7_200_000)]
    public async Task ASoakAtTheSaturnWindowBringsItsStallsBack()
    {
        var minutes = IntFromEnvironment("TIANWEN_TOUPTEK_SOAK_MINUTES", 0);
        Assert.SkipWhen(minutes == 0, "TIANWEN_TOUPTEK_SOAK_MINUTES is not set");
        var (camera, external, sp) = await ConnectAsync(output, LogLevel.Information);
        await using var _ = sp;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var options = new VideoCaptureOptions(SaturnExposure, Gain: SaturnGain, BitDepth: BitDepth.Int8);
            await using var capture = new PlanetaryCapture(SystemTimeProvider.Instance, external.AppLogger, kind: LiveCaptureKind.LiveView);
            capture.Start(camera, options, ct).ShouldBeTrue();
            var clock = Stopwatch.StartNew();
            var span = TimeSpan.FromMinutes(minutes);
            while (clock.Elapsed < span && capture.IsCapturing)
            {
                await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromSeconds(1), ct);
            }
            var seconds = clock.Elapsed.TotalSeconds;
            var (read, replaced, dropped) = (camera.VideoFramesRead, camera.VideoFramesReplaced, camera.VideoFramesDroppedByBody);
            await capture.StopAsync(ct);

            output.WriteLine("");
            output.WriteLine($"{camera.Name}: soak through the capture loop, {RoiWidth}x{RoiHeight} centred, 8 bits, {SaturnExposure.TotalMilliseconds} ms, "
                + $"gain {SaturnGain}, {seconds / 60:F1} min{(capture.FailureReason is { } failed ? $", ENDED: {failed}" : "")}");
            output.WriteLine($"read {read} = {read / seconds:F1} fps over the whole soak, gaps included; replaced {replaced}, body dropped {dropped}{StreamHealth(camera)}");
        }
        finally
        {
            await camera.DisconnectAsync(ct);
        }
    }

    /// <summary>The Saturn window's stream: 5 ms, gain 2252, 8 bits.</summary>
    internal static VideoCaptureOptions SaturnOptions => new VideoCaptureOptions(SaturnExposure, Gain: SaturnGain, BitDepth: BitDepth.Int8);

    /// <summary>
    /// The attached ToupTek camera, connected in this process and set to the Saturn window's gain, size and origin; skips
    /// the test unless <see cref="GateVar"/> is 1 and a camera is attached.
    /// </summary>
    /// <param name="device">The camera when the caller has found it already: an enumeration OPENS every camera to read its
    /// serial, so a caller that enumerated passes what it found rather than opening the camera twice in a row.</param>
    internal static async Task<(ToupTekCameraDriver Camera, FakeExternal External, ServiceProvider Services)> ConnectAsync(ITestOutputHelper output, LogLevel level,
        ToupTekDevice? device = null)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1", $"{GateVar} is not 1 (this drives the attached ToupTek camera)");
        var ct = TestContext.Current.CancellationToken;
        device ??= new ToupTekDeviceSource().RegisteredDevices(DeviceType.Camera).FirstOrDefault();
        Assert.SkipWhen(device is null, "no ToupTek camera attached");

        var external = new FakeExternal(output);
        var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            // Stamped, so a stall's time into its stream can be read off the output.
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" }))
                .SetMinimumLevel(level))
            .BuildServiceProvider();
        var camera = new ToupTekCameraDriver(device, sp);
        await camera.ConnectAsync(ct);

        camera.CanVideoCapture.ShouldBeTrue("ToupTek.SDK 1.2 streams: the camera free-running, TOUPCAM_OPTION_TRIGGER = 0");
        if (SaturnGain >= camera.GainMin && SaturnGain <= camera.GainMax)
        {
            await camera.SetGainAsync(SaturnGain, ct);
        }
        camera.BinX = 1;
        camera.NumX = RoiWidth;
        camera.NumY = RoiHeight;
        camera.StartX = RoiX;
        camera.StartY = RoiY;
        return (camera, external, sp);
    }

    /// <summary>
    /// Reads the driver's stream, moved onto the Saturn window (a stream starts centred), for the warm-up and then
    /// <see cref="VideoMeasured"/>, doing <paramref name="work"/> to each frame on the reader as the capture loop would.
    /// </summary>
    private static async Task<string> MeasureStreamAsync(string what, ToupTekCameraDriver camera, VideoCaptureOptions options, CancellationToken ct, Action<Image> work)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();
        var frames = 0L;
        TimeSpan? measuredFrom = null;
        var (readFrom, replacedFrom, droppedFrom) = (0L, 0L, 0);
        var (read, replaced, dropped) = (0L, 0L, 0);
        var gaps = new List<double>();
        var lastFrame = 0L;
        var panned = false;
        await foreach (var frame in camera.CaptureVideoAsync(options, stop.Token))
        {
            try
            {
                work(frame);
            }
            finally
            {
                frame.Release();
            }

            if (!panned)
            {
                var roi = camera.VideoRoi;
                await camera.JogRoiAsync(RoiX - roi.X, RoiY - roi.Y, ct);
                panned = true;
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            if (measuredFrom is null)
            {
                if (camera.VideoRoi is { X: RoiX, Y: RoiY } && clock.Elapsed >= Warm)
                {
                    measuredFrom = clock.Elapsed;
                    (readFrom, replacedFrom, droppedFrom) = (camera.VideoFramesRead, camera.VideoFramesReplaced, camera.VideoFramesDroppedByBody);
                    lastFrame = now;
                }
                continue;
            }

            frames++;
            gaps.Add(Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds);
            lastFrame = now;
            if (clock.Elapsed - measuredFrom.Value >= VideoMeasured)
            {
                (read, replaced, dropped) = (camera.VideoFramesRead, camera.VideoFramesReplaced, camera.VideoFramesDroppedByBody);
                break;
            }
        }

        var seconds = (clock.Elapsed - (measuredFrom ?? TimeSpan.Zero)).TotalSeconds;
        return $"{what,-30} {frames / seconds,6:F1} fps, read {read - readFrom}, taken {frames}, replaced {replaced - replacedFrom}, "
            + $"body dropped {dropped - droppedFrom}{StreamHealth(camera)}, between frames {Percentiles(gaps)}";
    }

    /// <summary>
    /// What a capture took: its rate (off the SER when it recorded), its words, and, for a recording, the SER read back and
    /// the driver's counts over it (read, replaced by the hand-off, dropped by the body, and the recording's own disk drops).
    /// </summary>
    internal sealed record CaptureResult(double Fps, string Detail, SerRateReadBack? Ser = null, long Replaced = 0, int BodyDropped = 0,
        int DiskDropped = 0, int Stalls = 0);

    /// <summary>
    /// <see cref="PlanetaryCapture"/> whole on the driver's stream, moved onto the Saturn window: for
    /// <see cref="VideoMeasured"/> with no recording, or with a recording for its duration, read back off the SER.
    /// </summary>
    internal static async Task<CaptureResult> CaptureAsync(ToupTekCameraDriver camera, VideoCaptureOptions options, (string Path, TimeSpan Duration)? record,
        ILogger logger, CancellationToken ct)
    {
        var gaps = new List<double>();
        var lastFrame = 0L;
        var measuring = 0;
        await using var capture = new PlanetaryCapture(SystemTimeProvider.Instance, logger, onFrame: _ =>
        {
            var now = Stopwatch.GetTimestamp();
            if (Volatile.Read(ref measuring) == 1)
            {
                gaps.Add(Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds);
            }
            lastFrame = now;
        });
        capture.Start(camera, options, ct).ShouldBeTrue();

        // A stream starts centred; the Saturn window is a pan away, which costs the camera one frame.
        while (capture.FramesReceived == 0)
        {
            await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(20), ct);
        }
        var roi = camera.VideoRoi;
        capture.JogRoi(RoiX - roi.X, RoiY - roi.Y);
        while (camera.VideoRoi is not { X: RoiX, Y: RoiY })
        {
            await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(20), ct);
        }
        await SystemTimeProvider.Instance.SleepAsync(Warm, ct);

        var (readFrom, replacedFrom, droppedFrom, receivedFrom) = (camera.VideoFramesRead, camera.VideoFramesReplaced, camera.VideoFramesDroppedByBody, capture.FramesReceived);
        // The collections over the measurement, and how long they held every managed thread, the stream's included: a pause
        // waits for every thread to reach a safe point, a starved one too.
        var (gen0From, gen1From, gen2From, pausedFrom) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
        var clock = Stopwatch.StartNew();
        Volatile.Write(ref measuring, 1);
        SerRecording? recording = null;
        if (record is { } asked)
        {
            capture.TryStartRecording(asked.Path, asked.Duration, out recording, out var refusal).ShouldBeTrue(refusal);
            while (recording.IsRecording && clock.Elapsed < asked.Duration + TimeSpan.FromMinutes(2))
            {
                await SystemTimeProvider.Instance.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
            }
        }
        else
        {
            await SystemTimeProvider.Instance.SleepAsync(VideoMeasured, ct);
        }
        Volatile.Write(ref measuring, 0);
        var seconds = clock.Elapsed.TotalSeconds;
        var (read, replaced, dropped, received) = (camera.VideoFramesRead, camera.VideoFramesReplaced, camera.VideoFramesDroppedByBody, capture.FramesReceived);
        var (gen0, gen1, gen2, paused) = (GC.CollectionCount(0) - gen0From, GC.CollectionCount(1) - gen1From, GC.CollectionCount(2) - gen2From,
            GC.GetTotalPauseDuration() - pausedFrom);
        // The stalls the stream logged, the start and the pan included: what sets a recording aside (RateGate.StandingOf).
        var stalls = camera.VideoStalls;
        var (onPool, readerPriority) = (capture.FramesOnThreadPool, capture.ReaderPriority);
        await capture.StopAsync(ct);
        var failure = capture.FailureReason is { } failed ? $", FAILED: {failed}" : "";

        var counts = $"read {read - readFrom}, taken {received - receivedFrom}, replaced {replaced - replacedFrom}, body dropped {dropped - droppedFrom}"
            + $"{StreamHealth(camera)}, the loop at {readerPriority?.ToString() ?? "?"} with {onPool} frames on the thread pool"
            + $", {gen0}/{gen1}/{gen2} collections (gen 0/1/2) pausing {paused.TotalMilliseconds:F0} ms in all"
            + $", between frames {Percentiles(gaps)}{failure}";
        if (recording is null)
        {
            return new CaptureResult((received - receivedFrom) / seconds, counts);
        }

        await recording.Completion.WaitAsync(ct);
        var ser = SerRateReadBack.Read(recording.Path, LongStepBound);
        var bodyRate = (dropped - droppedFrom) > 0 ? $" (1 in {(read - readFrom) / (double)(dropped - droppedFrom):F0})" : "";
        return new CaptureResult(ser.Fps,
            $"{ser}, {recording.FramesDropped} dropped by the disk (its longest write {recording.LongestWrite.TotalMilliseconds:F0} ms), the recording's own gaps: {recording.Gaps.Length}; "
            + counts + bodyRate + $"; ended: {recording.EndReason}",
            ser, replaced - replacedFrom, dropped - droppedFrom, recording.FramesDropped, stalls);
    }

    /// <summary>The most frames that waited for the reader, and the stream's stalls with what brought each back and the gap it left.</summary>
    private static string StreamHealth(ToupTekCameraDriver camera)
    {
        var each = camera.VideoRecoveryLog is { Length: > 0 } recoveries
            ? " (" + string.Join("; ", recoveries.Select(r => $"{r.Code} after the frame of {r.LastFrameAt:HH:mm:ss.fff}, back by a {r.Rung}, {r.Gap.TotalSeconds:F1} s without a frame")) + ")"
            : "";
        return $", hand-off at most {camera.VideoHandOffHighWater} waiting, {camera.VideoStalls} stalls, {camera.VideoRecoveries} recovered{each}"
            + $", the reader waited for a frame {camera.VideoReaderWaits} times, {camera.VideoReaderWakesOnPool} of them woken on the thread pool";
    }

    private sealed record Row(string Layer, BitDepth Depth, TimeSpan Exposure, double Fps, int Frames, int Lost,
        (int Width, int Height)? Size, BitDepth? FrameDepth, string Detail);

    /// <summary>
    /// The bare driver's single-exposure loop: start, poll until ready (as fast as it answers, or sleeping
    /// <paramref name="pollSleep"/> between polls as the capture loop's fallback does), read, release, repeat. An exposure
    /// that ends with no frame is counted lost and the next one started.
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
    /// A stopped loop can leave an exposure in flight, and the trigger's frame would otherwise answer the next layer's
    /// first exposure: it is waited for and dropped.
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

    private static int IntFromEnvironment(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private static string FormatExposure(TimeSpan exposure)
        => exposure < TimeSpan.FromMilliseconds(1) ? $"{exposure.TotalMicroseconds:F0} us" : $"{exposure.TotalMilliseconds:0.###} ms";

    private static string Percentiles(List<double> ms)
    {
        if (ms.Count == 0)
        {
            return "none";
        }
        var sorted = ms.Order().ToArray();
        double At(double p) => sorted[(int)Math.Round(p * (sorted.Length - 1))];
        return $"p50 {At(0.5):F2} / p99 {At(0.99):F2} / max {sorted[^1]:F1} ms";
    }

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
