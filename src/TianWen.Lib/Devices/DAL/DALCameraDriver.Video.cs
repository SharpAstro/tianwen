using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.DAL;

/// <summary>
/// Video (#813, docs/plans/planetary-native-video.md, Phase D): a body that streams
/// (<see cref="ICMOSNativeInterface.CanVideoCapture"/>) serves a planetary or focusing live view from one continuous
/// readout, rather than from an exposure started, awaited and downloaded per frame, which is what held an ASI462MC at
/// 5.8 frames a second at full frame.
/// </summary>
/// <remarks>
/// <para><b>One flow owns the SDK while a stream runs, and its frame calls run on a thread of their own.</b> The vendors'
/// frame call blocks until a frame arrives, so each run of the body's stream between a start and a stop (a SEGMENT) reads
/// on a thread of its own, never a pool thread, at the highest priority, since a frame left unread in the body's buffers
/// is a frame the camera drops. Every other SDK call the stream makes is made there too: the exposure, the gain, and the
/// window's pan and size are STAGED by their callers and applied between frames. Between two segments, and only then, the
/// stream's recovery (below) makes its calls, so no two calls of a stream ever overlap.</para>
/// <para><b>The reader is handed every frame unless it falls a budget behind.</b> Frames reach the reader through a
/// bounded queue (<see cref="VideoHandOffMaxFrames"/>, and no more than <see cref="VideoHandOffBudgetBytes"/> of planes)
/// which, once full, drops the OLDEST frame and gives its plane back. It was a single frame until TIA2, which lost a
/// recording most of its frames: on a G3M678M at 197 frames a second, 640 x 480, with the machine's cores all busy, the
/// capture loop was handed 1,985 of 11,839 in a minute and the hand-off replaced 9,853 (the driver's thread kept up, the
/// reader, woken through the thread pool, did not). A queue lets a reader that pauses catch up; one that is slower than the
/// camera for good still sees frames a budget old at worst, never an unbounded backlog. Docs: live-planetary-capture.md,
/// "Video at the Saturn window on the ToupTek (measured 2026-10-15)".</para>
/// <para><b>A stalled stream is brought back, cheapest first, and given up only when nothing does.</b> A body that stops
/// sending (any answer from the frame call but a frame, <see cref="CMOSErrorCode.Timeout"/> or
/// <see cref="CMOSErrorCode.CameraRemoved"/>, or nothing but <see cref="CMOSErrorCode.Timeout"/> for longer than the
/// stream's <see cref="VideoSilenceWatch.Limit"/>) is restarted on the same handle; if that brings no frame, closed and
/// opened again; if that brings none, reset as a replug would (<see cref="INativeDeviceInfo.CanResetDevice"/>), the same
/// reset and restore a lost single exposure gets (<c>DALCameraDriver.Recovery.cs</c>). ToupTek.SDK measured the order on a
/// G3M678M (2026-10-15): a restart undid 10 of 11 stalls in about 6.5 s, the eleventh needed the reset, 30.4 s in all,
/// and nudging the speed or the window undid none. Every setting the stream had (exposure, gain, window, depth, readout
/// mode, bandwidth) is set again on every start, so a restarted, reopened or reset body streams as it did. A stream that
/// has run <see cref="VideoHealthyAfter"/> since it came back starts its next stall's ladder from the bottom again; one
/// that stalls sooner goes on up it, and past the reset the stream ends with a plain reason. A body gone from the bus
/// (<see cref="CMOSErrorCode.CameraRemoved"/>) ends it at once.</para>
/// <para><b>A recovery is off the reader's path and a stop stays responsive.</b> The reader waits on the hand-off, so it is
/// never inside a recovery's calls (a stop on a stalled ToupTek blocks about 5 s); a stop is seen between rungs, during a
/// reset camera's wait to reappear, and within one frame wait of a segment, so it costs at most the one SDK call under
/// way. A close and reopen is finished before a stop is honoured, since a stop half way would leave the camera closed.</para>
/// <para><b>A body streams OR exposes.</b> A stream will not start while an exposure runs, and an exposure will not start
/// while a stream runs. The window a stream set is put back by the single-frame path's own check, which compares the
/// body's window with its settings before every exposure.</para>
/// <para><b>A disconnect stops the stream first</b>, and waits for its flow to leave the SDK: closing the body under a
/// frame call is the one thing a stream must never see.</para>
/// <para><b>A stream's depth, readout mode and USB bandwidth are its own.</b> It reads out in the depth it was asked for
/// (<see cref="VideoCaptureOptions.BitDepth"/>, else the camera's), with the high-speed readout on unless asked otherwise,
/// and takes the body's whole USB bandwidth while it runs, giving back what it found when it ends. The raw SDK measured
/// the difference on an ASI462MC at full frame (2026-09-28): 136 frames a second in 8 bits at bandwidth 100 with the
/// high-speed readout, 31.9 in 16 bits at the 50 a connect sets.</para>
/// </remarks>
internal abstract partial class DALCameraDriver<TDevice, TDeviceInfo> : IVideoCameraDriver
{
    /// <summary>
    /// The longest one frame call waits, so a stop is seen within about this long whatever the exposure. A frame
    /// that takes longer simply answers Timeout, and the stream asks again.
    /// </summary>
    internal static readonly TimeSpan VideoFrameWait = TimeSpan.FromMilliseconds(250);

    /// <summary>The most frames the hand-off holds for a reader that has fallen behind: 325 ms at 197 frames a second.</summary>
    internal const int VideoHandOffMaxFrames = 64;

    /// <summary>
    /// What the frames waiting in the hand-off may hold between them, so a large window queues fewer: all 64 of a 640 x 480
    /// window (1.2 MB a plane), 8 of a 3840 x 2160 one (33 MB). Never fewer than two.
    /// </summary>
    internal const long VideoHandOffBudgetBytes = 256L << 20;

    /// <summary>
    /// How long a stream must run after it came back from a stall for its next stall to count as a new one, whose recovery
    /// starts from a restart again; a stall sooner than this means the last rung did not hold, and the next one is tried.
    /// </summary>
    internal static readonly TimeSpan VideoHealthyAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a settle after a pan lasts (<see cref="IsUnderVideoRoi"/>): past it the next frame is taken as under the
    /// new window, as if the body's buffers had run dry, so a reader that never catches up cannot stop the recenter for
    /// good. A G3M678M's pan blocks 32 to 285 ms at 5 ms, and its ring holds 320 ms.
    /// </summary>
    internal static readonly TimeSpan VideoSettleLimit = TimeSpan.FromSeconds(1);

    /// <summary>The smallest window side a stream offers: a planetary disc or a focusing star needs more than a few pixels.</summary>
    private const int MinimumVideoWindowSide = 16;

    private int _videoActive;
    private CancellationTokenSource? _videoStop;
    private Task? _videoStreamEnded;

    // The window as the capture thread last set it, published whole; null while no stream runs.
    private StrongBox<RoiRect>? _videoWindow;

    // From which frame on the frames were read under that window with every jog asked so far applied; null while no
    // stream runs. Jogs are counted as asked (JogRoiAsync) and as applied (the segment), so a frame read between the two
    // is never taken for one under the new window.
    private VideoWindowSettle? _videoWindowSettle;
    private long _videoJogsAsked;

    private int _videoDroppedFrames;

    // What the stream did, for its closing log line and the probes: frames read from the body, frames the reader never
    // took because the hand-off was full and dropped them, the most frames that ever waited there, and its stalls and
    // what brought each back.
    private long _videoFramesRead;
    private long _videoFramesReplaced;
    private long _videoReaderWaits;
    private long _videoReaderWakesOnPool;
    private int _videoHandOffHighWater;
    private int _videoStalls;
    private int _videoRecoveries;
    private ImmutableArray<VideoStallRecovery> _videoRecoveryLog = [];

    // Staged for the capture thread, which takes each one as it applies it: 0 ticks and -1 gain are "nothing staged".
    private long _stagedVideoExposureTicks;
    private int _stagedVideoGain = -1;
    private int _stagedJogX;
    private int _stagedJogY;
    // 0 and -1 are "nothing staged": a depth is staged as its BitDepth value, the readout mode as 0 or 1.
    private int _stagedVideoBitDepth;
    private int _stagedHighSpeed = -1;

    private readonly PlaneRecycler _videoPlanes = new PlaneRecycler("DALCameraDriver.Video");

    /// <summary>How many planes the stream has had to allocate: a steady stream allocates none.</summary>
    internal int VideoPlanesAllocated => _videoPlanes.PlanesAllocated;

    /// <summary>Frames the current or last stream read from the body, for the rate probes.</summary>
    internal long VideoFramesRead => Interlocked.Read(ref _videoFramesRead);

    /// <summary>Frames of the current or last stream that the full hand-off dropped before the reader took them.</summary>
    internal long VideoFramesReplaced => Interlocked.Read(ref _videoFramesReplaced);

    /// <summary>Frames the body says it lost in the current or last stream (<see cref="DroppedFrames"/> answers 0 once it ends).</summary>
    internal int VideoFramesDroppedByBody => Volatile.Read(ref _videoDroppedFrames);

    /// <summary>The most frames that ever waited for the reader in the hand-off, in the current or last stream.</summary>
    internal int VideoHandOffHighWater => Volatile.Read(ref _videoHandOffHighWater);

    /// <summary>How often the reader of the current or last stream found the hand-off empty and waited for a frame.</summary>
    internal long VideoReaderWaits => Interlocked.Read(ref _videoReaderWaits);

    /// <summary>Of <see cref="VideoReaderWaits"/>, the waits that woke on a thread pool thread rather than the reader's own.</summary>
    internal long VideoReaderWakesOnPool => Interlocked.Read(ref _videoReaderWakesOnPool);

    /// <summary>The stalls the current or last stream had, recovered or not.</summary>
    internal int VideoStalls => Volatile.Read(ref _videoStalls);

    /// <summary>The stalls the current or last stream came back from.</summary>
    internal int VideoRecoveries => Volatile.Read(ref _videoRecoveries);

    /// <summary>Each stall the current or last stream came back from, in order: what brought it back, and the gap it left.</summary>
    internal ImmutableArray<VideoStallRecovery> VideoRecoveryLog => _videoRecoveryLog;

    /// <inheritdoc/>
    public bool CanVideoCapture => Connected && _deviceInfo.CanVideoCapture;

    /// <inheritdoc/>
    public bool CanJogRoi => Volatile.Read(ref _videoActive) == 1 && _deviceInfo.CanPanRoiWhileStreaming;

    /// <inheritdoc/>
    public ImmutableArray<BitDepth> VideoBitDepths => Connected && _deviceInfo.CanVideoCapture
        ? [.. _supportedBitDepth.Where(depth => depth.IsIntegral).OrderBy(depth => depth.BitSize)]
        : [];

    /// <inheritdoc/>
    public int DroppedFrames => Volatile.Read(ref _videoActive) == 1 ? Volatile.Read(ref _videoDroppedFrames) : 0;

    /// <inheritdoc/>
    public RoiRect VideoRoi => Volatile.Read(ref _videoWindow) is { } window
        ? window.Value
        : new RoiRect(0, 0, CameraXSize, CameraYSize);

    /// <inheritdoc/>
    /// <remarks>
    /// A pan blocks its caller while the body streams on into its buffers (32 to 285 ms on a G3M678M at 5 ms), and the
    /// frames still there when it returns are read after it, under the new <see cref="VideoRoi"/>, though they were taken
    /// under the old. So after a pan the segment first asks for a frame without waiting: while one is there it is part of
    /// that backlog, and the first frame it has to wait for is the first under the new window. Any answer but a frame ends
    /// the settle as an empty buffer does, and so does <see cref="VideoSettleLimit"/>.
    /// </remarks>
    public bool IsUnderVideoRoi(Image frame)
        => Volatile.Read(ref _videoWindowSettle) is { } settle
           && settle.JogsApplied == Interlocked.Read(ref _videoJogsAsked)
           && frame.ImageMeta.FrameSequence >= settle.FromSequence;

    /// <summary>
    /// The SDK's own steps for a window (<see cref="ICMOSNativeInterface.GetRoiSteps"/>), with an even origin on a colour
    /// sensor, whose CFA phase follows the origin: an odd one would swap its colours.
    /// </summary>
    public RoiConstraints RoiConstraints
    {
        get
        {
            if (!Connected)
            {
                return Devices.RoiConstraints.ForSensor(CameraXSize, CameraYSize);
            }

            _deviceInfo.GetRoiSteps(out var widthStep, out var heightStep, out var originStepX, out var originStepY);
            if (SensorType is SensorType.RGGB)
            {
                originStepX = originStepX % 2 == 0 ? originStepX : originStepX * 2;
                originStepY = originStepY % 2 == 0 ? originStepY : originStepY * 2;
            }

            var bin = Math.Max(1, (int)_cameraSettings.BinX);
            return new RoiConstraints(
                MaxWidth: Math.Max(1, CameraXSize / bin),
                MaxHeight: Math.Max(1, CameraYSize / bin),
                MinWidth: RoundUp(MinimumVideoWindowSide, widthStep),
                MinHeight: RoundUp(MinimumVideoWindowSide, heightStep),
                WidthStep: widthStep,
                HeightStep: heightStep,
                OriginStepX: originStepX,
                OriginStepY: originStepY);

            static int RoundUp(int value, int step) => step <= 1 ? value : (value + step - 1) / step * step;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Image> CaptureVideoAsync(VideoCaptureOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw NotConnectedException();
        }
        if (!_deviceInfo.CanVideoCapture)
        {
            throw new InvalidOperationException($"{Name} cannot stream video");
        }
        if (_camState is CameraState.Exposing)
        {
            throw new InvalidOperationException($"{Name} is exposing, and a camera streams or exposes, never both");
        }
        if (Interlocked.CompareExchange(ref _videoActive, 1, 0) != 0)
        {
            throw new InvalidOperationException($"A video capture is already running on {Name}");
        }

        // Two readers: the reader proper, and the segment, which drops the oldest frame itself once the frames waiting
        // pass the byte budget (the capacity bounds only their count).
        var frames = System.Threading.Channels.Channel.CreateBounded<Image>(
            new BoundedChannelOptions(VideoHandOffMaxFrames) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true },
            dropped =>
            {
                Interlocked.Increment(ref _videoFramesReplaced);
                dropped.Release();
            });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Never disposed: it has no timer and is linked to no other token, so it holds nothing to give back, and a
        // disconnect may still cancel it after the stream has ended. The reader's token cancels it through a registration.
        var stop = new CancellationTokenSource();
        using var stopOnCancel = cancellationToken.Register(stop.Cancel);

        Interlocked.Exchange(ref _stagedVideoExposureTicks, 0);
        Interlocked.Exchange(ref _stagedVideoGain, -1);
        Interlocked.Exchange(ref _stagedJogX, 0);
        Interlocked.Exchange(ref _stagedJogY, 0);
        Interlocked.Exchange(ref _stagedVideoBitDepth, 0);
        Interlocked.Exchange(ref _stagedHighSpeed, -1);
        Interlocked.Exchange(ref _videoFramesRead, 0);
        Interlocked.Exchange(ref _videoFramesReplaced, 0);
        Interlocked.Exchange(ref _videoReaderWaits, 0);
        Interlocked.Exchange(ref _videoReaderWakesOnPool, 0);
        Volatile.Write(ref _videoHandOffHighWater, 0);
        Volatile.Write(ref _videoStalls, 0);
        Volatile.Write(ref _videoRecoveries, 0);
        _videoRecoveryLog = [];
        _videoStop = stop;
        Task? streamEnded = null;
        try
        {
            streamEnded = Task.Run(() => RunVideoAsync(options, frames, started, stop.Token), CancellationToken.None);
            Volatile.Write(ref _videoStreamEnded, streamEnded);

            // A stream the body refused to start throws here, naming what was refused. Keeps its context, as the frame wait
            // below does (NextVideoFrameAsync), so a reader of its own goes on there from the first frame.
            await started.Task;

            while (await NextVideoFrameAsync(frames.Reader, cancellationToken).ConfigureAwait(false) is { } frame)
            {
                yield return frame;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (streamEnded is not null)
            {
                // Never faults: the stream reports its failure through the hand-off and the start.
                await streamEnded.ConfigureAwait(false);
            }
            while (frames.Reader.TryRead(out var unread))
            {
                unread.Release();
            }
            Volatile.Write(ref _videoStreamEnded, null);
            Volatile.Write(ref _videoStop, null);
            Interlocked.Exchange(ref _videoActive, 0);
        }
    }

    /// <summary>The stream's next frame; null once it has ended or been cancelled, and its failure thrown if it failed.</summary>
    private async ValueTask<Image?> NextVideoFrameAsync(ChannelReader<Image> frames, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var wait = frames.WaitToReadAsync(cancellationToken);
                var waited = !wait.IsCompleted;
                // Keeps its context, on purpose: a reader with one of its own (a planetary capture's thread,
                // CaptureReaderThread) is woken there straight from the stream's thread, never through the thread pool,
                // whose normal-priority workers wait behind a busy machine's load. A reader without one resumes on the pool
                // as any await would.
                if (!await wait)
                {
                    return null;
                }
                if (waited)
                {
                    // Where the reader woke when it had to wait for a frame: the thread pool, or the reader's own thread.
                    Interlocked.Increment(ref _videoReaderWaits);
                    if (Thread.CurrentThread.IsThreadPoolThread)
                    {
                        Interlocked.Increment(ref _videoReaderWakesOnPool);
                    }
                }
                if (frames.TryRead(out var frame))
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancel is the stop signal (IVideoCameraDriver): the stream ends, it does not fail.
            return null;
        }
    }

    /// <inheritdoc/>
    public ValueTask JogRoiAsync(int dxPixels, int dyPixels, CancellationToken cancellationToken = default)
    {
        if (!CanJogRoi)
        {
            throw new InvalidOperationException($"{Name} pans its window only while it streams, and only on a body that pans mid-stream");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _stagedJogX, dxPixels);
        Interlocked.Add(ref _stagedJogY, dyPixels);
        // Counted only once its deltas are staged, so a segment that has read this count has the deltas too.
        Interlocked.Increment(ref _videoJogsAsked);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ApplyVideoControlsAsync(VideoCaptureOptions controls, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (controls.Exposure > TimeSpan.Zero)
        {
            Interlocked.Exchange(ref _stagedVideoExposureTicks, controls.Exposure.Ticks);
        }
        if (controls.Gain is { } gain)
        {
            Interlocked.Exchange(ref _stagedVideoGain, gain);
        }
        if (controls.BitDepth is { } bitDepth)
        {
            Interlocked.Exchange(ref _stagedVideoBitDepth, (int)bitDepth);
        }
        if (controls.HighSpeedMode is { } highSpeedMode)
        {
            Interlocked.Exchange(ref _stagedHighSpeed, highSpeedMode ? 1 : 0);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops a stream going on and waits for its flow to leave the SDK, before the body is closed.</summary>
    protected override async Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _videoStreamEnded) is { } streamEnded)
        {
            if (Volatile.Read(ref _videoStop) is { } stop)
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
            await streamEnded.ConfigureAwait(false);
        }
        return await base.DoDisconnectDeviceAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What brings a stalled stream back, cheapest first.</summary>
    internal enum VideoRecoveryRung
    {
        /// <summary>The stream stopped and started again on the same handle: 10 of 11 stalls on a G3M678M, about 6.5 s.</summary>
        Restart,

        /// <summary>The camera closed and opened again, its settings restored.</summary>
        Reopen,

        /// <summary>The camera reset as a replug would, found again and its settings restored: 30.4 s in all on a G3M678M.</summary>
        Reset,
    }

    /// <summary>
    /// One stall a stream came back from: when its last frame before the stall was read, what the body answered, what
    /// brought it back, and how long it went without a frame (from that last frame to the first after).
    /// </summary>
    internal readonly record struct VideoStallRecovery(DateTimeOffset LastFrameAt, CMOSErrorCode Code, VideoRecoveryRung Rung, TimeSpan Gap);

    /// <summary>From which frame on a stream's frames are under its window, and with how many jogs applied (<see cref="IsUnderVideoRoi"/>).</summary>
    private sealed record VideoWindowSettle(long JogsApplied, long FromSequence);

    /// <summary>
    /// What one stream is running with, carried from one segment to the next so a restarted, reopened or reset body
    /// streams as it did. Touched by one flow at a time: a segment's thread, or the recovery between segments, handed over
    /// through the segment's task.
    /// </summary>
    private sealed class VideoStream(TimeSpan exposure, short? gain, BitDepth bitDepth, bool highSpeed)
    {
        public TimeSpan Exposure { get; set; } = exposure;

        /// <summary>How long the stream may go without a frame before it counts as stalled: the stream's, never a segment's.</summary>
        public VideoSilenceWatch Silence { get; } = new VideoSilenceWatch(exposure);

        public short? Gain { get; set; } = gain;

        public BitDepth BitDepth { get; set; } = bitDepth;

        public bool HighSpeed { get; set; } = highSpeed;

        /// <summary>The window; null until the first segment places it, centred on the sensor.</summary>
        public RoiRect? Window { get; set; }

        public nint Buffer { get; set; }

        public int BufferSize { get; set; }

        /// <summary>The bandwidth the body had before the stream took it all, given back as the stream ends.</summary>
        public int? BandwidthFound { get; set; }

        public bool BandwidthTaken { get; set; }

        /// <summary>Frames the body lost in the stream's earlier starts: its own count starts again with every start.</summary>
        public int DroppedEarlier { get; set; }

        /// <summary>The body's count since the start going on.</summary>
        public int DroppedSinceStart { get; set; }

        /// <summary>The jogs asked for that the window carries.</summary>
        public long JogsApplied { get; set; }
    }

    /// <summary>How one segment of a stream went: written by its thread, read by the stream once its task has completed.</summary>
    private sealed class VideoSegment(VideoStall? recovering, VideoRecoveryRung rung)
    {
        /// <summary>The stall this segment is bringing the stream back from, and by which rung; null for the first.</summary>
        public VideoStall? Recovering { get; } = recovering;

        public VideoRecoveryRung Rung { get; } = rung;

        /// <summary>The stall that ended it; null when it ended on a stop.</summary>
        public VideoStall? Stall { get; set; }

        /// <summary>Whether the body said it was gone from the bus, which ends the stream whatever else.</summary>
        public bool Removed { get; set; }

        public long Frames { get; set; }
    }

    /// <summary>A stall: what the body answered, and the last frame before it (its timestamp, and its time for a log).</summary>
    private sealed record VideoStall(CMOSErrorCode Code, long LastFrameTimestamp, DateTimeOffset LastFrameAt, bool Healthy);

    /// <summary>
    /// The stream: segment after segment, each on a thread of its own, and between them the recovery of a stall, until it
    /// is stopped, the body fails, or the ladder gives up. Never throws: a failure goes to the reader through the hand-off
    /// and the start.
    /// </summary>
    private async Task RunVideoAsync(VideoCaptureOptions options, Channel<Image> frames, TaskCompletionSource started, CancellationToken stop)
    {
        var stream = new VideoStream(options.Exposure, options.Gain, StreamDepth(options.BitDepth), options.HighSpeedMode ?? true)
        {
            JogsApplied = Interlocked.Read(ref _videoJogsAsked),
        };
        var streamStart = TimeProvider.GetTimestamp();
        Exception? failure = null;
        try
        {
            VideoStall? ladder = null;
            var rung = VideoRecoveryRung.Restart;
            while (!stop.IsCancellationRequested && Connected)
            {
                var segment = new VideoSegment(ladder, rung);
                try
                {
                    await RunVideoSegmentAsync(stream, segment, frames, started, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ladder is not null && segment.Frames == 0 && !segment.Removed)
                {
                    // A rung that could not even start the stream again (a window or control the reopened body refused)
                    // failed as surely as one that started it and saw no frame.
                    Logger.LogWarning(ex, "{Camera}'s stream did not start again after a {Rung}", Name, rung);
                    segment.Stall = new VideoStall(CMOSErrorCode.GeneralError, ladder.LastFrameTimestamp, ladder.LastFrameAt, Healthy: false);
                }

                if (segment.Stall is not { } stall)
                {
                    break;
                }

                // The gap a recovery leaves runs from the last frame before the latest stall (counted and logged by its
                // segment as it saw it).
                var healthy = ladder is null || stall.Healthy;
                ladder = stall;
                if (healthy)
                {
                    rung = VideoRecoveryRung.Restart;
                    Logger.LogWarning("{Camera}: restarting its stream", Name);
                }
                else if (rung is VideoRecoveryRung.Restart)
                {
                    rung = VideoRecoveryRung.Reopen;
                    Logger.LogWarning("{Camera}'s stream did not come back on a restart ({Code}): closing the camera and opening it again", Name, stall.Code);
                }
                else if (rung is VideoRecoveryRung.Reopen && _deviceInfo.CanResetDevice)
                {
                    rung = VideoRecoveryRung.Reset;
                    Logger.LogWarning("{Camera}'s stream did not come back on a close and reopen ({Code}): resetting the camera", Name, stall.Code);
                }
                else
                {
                    throw OperationalException(stall.Code, GiveUpReason(ladder.Code, rung));
                }

                if (stop.IsCancellationRequested || !Connected)
                {
                    break;
                }

                switch (rung)
                {
                    case VideoRecoveryRung.Reopen:
                        // Not the stop's token: a stop half way through would leave the camera closed.
                        await ReopenAsync("to bring its stalled stream back", CancellationToken.None).ConfigureAwait(false);
                        break;
                    case VideoRecoveryRung.Reset:
                        if (!await ResetAndReopenAsync("to bring its stalled stream back", stop).ConfigureAwait(false))
                        {
                            throw OperationalException(ladder.Code, ResetRefusedReason(ladder.Code));
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped while a reset camera was waited for: it is closed, and only a new connect opens it again.
            _camState = CameraState.Error;
            Logger.LogError("{Camera} was stopped while it came back from a reset, and is closed: disconnect it and connect it again", Name);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (_deviceInfo.TryGetControlRange(CMOSControlType.HighSpeedMode, out _, out _))
            {
                // Back to what the camera's settings ask of a single exposure.
                _ = _deviceInfo.SetControlValue(CMOSControlType.HighSpeedMode, Convert.ToInt32(_cameraSettings.FastReadout));
            }
            if (stream.BandwidthFound is { } bandwidth)
            {
                _ = _deviceInfo.SetControlValue(CMOSControlType.BandwidthOverload, bandwidth);
            }
            Volatile.Write(ref _videoWindow, null);
            Volatile.Write(ref _videoWindowSettle, null);
            if (stream.Buffer != 0)
            {
                Marshal.FreeCoTaskMem(stream.Buffer);
            }
            var read = Interlocked.Read(ref _videoFramesRead);
            var replaced = Interlocked.Read(ref _videoFramesReplaced);
            var seconds = TimeProvider.GetElapsedTime(streamStart).TotalSeconds;
            Logger.LogInformation(
                "{Camera} streamed {Frames} frames in {Seconds:F1} s ({Rate:F1} a second): the reader took {Taken}, {Replaced} were dropped by the full hand-off "
                + "(at most {HighWater} waited), the body dropped {Dropped}, {Stalls} stalls, {Recoveries} recovered; the reader waited for a frame {Waits} times, "
                + "{WakesOnPool} of them woken on the thread pool",
                Name, read, seconds, seconds > 0 ? read / seconds : 0, read - replaced, replaced, VideoHandOffHighWater, Volatile.Read(ref _videoDroppedFrames),
                VideoStalls, VideoRecoveries, VideoReaderWaits, VideoReaderWakesOnPool);
            if (failure is not null)
            {
                Logger.LogError(failure, "{Camera}'s video stream failed", Name);
                started.TrySetException(failure);
            }
            else
            {
                started.TrySetResult();
            }
            frames.Writer.TryComplete(failure);
        }
    }

    private string GiveUpReason(CMOSErrorCode code, VideoRecoveryRung lastTried)
        => lastTried is VideoRecoveryRung.Reset
            ? $"{Name} stopped sending frames ({code}), and neither a restart of the stream, nor closing and opening the camera, nor a reset brought it back"
            : $"{Name} stopped sending frames ({code}), and neither a restart of the stream nor closing and opening the camera brought it back; "
              + "it cannot be reset from software, so unplug it and plug it in again";

    // A reset the camera refused never ran, so it brought nothing back: say what happened rather than that it failed.
    private string ResetRefusedReason(CMOSErrorCode code)
        => $"{Name} stopped sending frames ({code}), and neither a restart of the stream nor closing and opening the camera brought it back; "
           + "the camera refused the reset, so unplug it and plug it in again";

    /// <summary>Runs one segment on a thread of its own; completes when it has stopped the body's stream again.</summary>
    private Task RunVideoSegmentAsync(VideoStream stream, VideoSegment segment, Channel<Image> frames, TaskCompletionSource started, CancellationToken stop)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RunVideoSegment(stream, segment, frames, started, stop);
                ended.TrySetResult();
            }
            catch (Exception ex)
            {
                // Everything that goes wrong on this thread goes to the stream: an exception left to escape a thread of
                // its own would end the process.
                ended.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"{Name} video",
            // A frame the segment is late for waits in the body's buffers (64 on a ToupTek at 640 x 480, 320 ms at 200 a
            // second), and one more is dropped by the camera. The thread spends nearly all its time waiting on the body.
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
        return ended.Task;
    }

    /// <summary>
    /// One segment, on its own thread: sets everything the stream has, starts the body's stream, and reads frames until
    /// asked to stop, the camera is disconnected, the body fails, or it stalls; then stops the body's stream. Every SDK
    /// call of the segment is made here.
    /// </summary>
    private void RunVideoSegment(VideoStream stream, VideoSegment segment, Channel<Image> frames, TaskCompletionSource started, CancellationToken stop)
    {
        var streaming = false;
        var highSpeed = _deviceInfo.TryGetControlRange(CMOSControlType.HighSpeedMode, out _, out _);
        var lastFrame = segment.Recovering?.LastFrameTimestamp ?? TimeProvider.GetTimestamp();
        var lastFrameAt = segment.Recovering?.LastFrameAt ?? TimeProvider.GetUtcNow();
        var firstFrame = 0L;
        var silentFor = TimeSpan.Zero;
        var settling = false;
        var settleNext = false;
        var settleStarted = 0L;
        try
        {
            // Everything the stream had, set again: a first start, a restart on the same handle, and a camera opened
            // again or reset alike.
            SetVideoControl(CMOSControlType.Exposure, ExposureControlValue(stream.Exposure), "set the exposure");
            if (stream.Gain is { } streamGain)
            {
                SetVideoControl(CMOSControlType.Gain, ClampedGain(streamGain), "set the gain");
            }
            if (highSpeed)
            {
                SetVideoControl(CMOSControlType.HighSpeedMode, stream.HighSpeed ? 1 : 0, "set the high-speed readout");
            }
            var bandwidthFound = TakeWholeBandwidth();
            if (!stream.BandwidthTaken)
            {
                stream.BandwidthFound = bandwidthFound;
                stream.BandwidthTaken = true;
            }
            var gain = CurrentGain();

            var constraints = RoiConstraints;
            // The size is read from the settings, here and in the loop, never through NumX / NumY: those throw once
            // the driver is disconnecting, and a disconnect marks it so BEFORE it stops this stream and waits for it
            // (DoDisconnectDeviceAsync), so a read between the two failed the stream instead of letting it stop. The
            // body stays open until this stream has ended, so the settings are this stream's to read.
            var window = stream.Window ?? constraints.Snap(RoiRect.Centered(constraints.MaxWidth, constraints.MaxHeight, _cameraSettings.Width, _cameraSettings.Height));
            var bitDepth = stream.BitDepth;
            SetVideoWindow(window, bitDepth, resize: true);
            EnsureVideoBuffer(stream, FrameBytes(window, bitDepth));

            StartBodyStream(stream, ref streaming, "start the video stream");
            stream.Window = window;
            Volatile.Write(ref _videoWindow, new StrongBox<RoiRect>(window));
            Volatile.Write(ref _videoWindowSettle, new VideoWindowSettle(stream.JogsApplied, Interlocked.Read(ref _frameSequence) + 1));
            started.TrySetResult();
            if (segment.Recovering is null)
            {
                Logger.LogInformation("{Camera} streaming video: {Width}x{Height} at ({X}, {Y}), {Depth}, {Exposure} ms",
                    Name, window.Width, window.Height, window.X, window.Y, bitDepth, stream.Exposure.TotalMilliseconds);
            }

            var handOffLimit = HandOffFrames(window);
            while (!stop.IsCancellationRequested && Connected)
            {
                if (Interlocked.Exchange(ref _stagedVideoExposureTicks, 0) is > 0 and var ticks)
                {
                    // The frame in flight may still come at the old exposure: the watch holds the longer until it has.
                    stream.Silence.ExposureChanged(new TimeSpan(ticks));
                    stream.Exposure = new TimeSpan(ticks);
                    SetVideoControl(CMOSControlType.Exposure, ExposureControlValue(stream.Exposure), "set the exposure");
                }
                if (Interlocked.Exchange(ref _stagedVideoGain, -1) is >= 0 and var stagedGain)
                {
                    stream.Gain = (short)stagedGain;
                    SetVideoControl(CMOSControlType.Gain, ClampedGain(stagedGain), "set the gain");
                    gain = CurrentGain();
                    // Once the body has it: when a staged gain reached the camera, for the rate probes (TIA2). Written off the
                    // stream's thread, so the file log's lock never holds the camera's reads (review round 1 of #1621).
                    Logger.LogDebugOffThread("{Camera}'s stream is at gain {Gain}", Name, gain);
                }

                // A new size, depth or readout mode needs the stream stopped on every body; the origin is kept where it
                // fits.
                var depth = Interlocked.Exchange(ref _stagedVideoBitDepth, 0) is var stagedDepth and not 0 ? StreamDepth((BitDepth)stagedDepth) : bitDepth;
                var mode = highSpeed ? Interlocked.Exchange(ref _stagedHighSpeed, -1) : -1;
                if (constraints.SnapWidth(_cameraSettings.Width) != window.Width || constraints.SnapHeight(_cameraSettings.Height) != window.Height || depth != bitDepth || mode >= 0)
                {
                    StopBodyStream(stream, ref streaming, "stop the video stream to change it");
                    if (mode >= 0)
                    {
                        stream.HighSpeed = mode == 1;
                        SetVideoControl(CMOSControlType.HighSpeedMode, mode, "set the high-speed readout");
                    }
                    bitDepth = stream.BitDepth = depth;
                    window = constraints.Snap(window with { Width = _cameraSettings.Width, Height = _cameraSettings.Height });
                    SetVideoWindow(window, bitDepth, resize: true);
                    EnsureVideoBuffer(stream, FrameBytes(window, bitDepth));
                    StartBodyStream(stream, ref streaming, "restart the video stream");
                    stream.Window = window;
                    handOffLimit = HandOffFrames(window);
                    Volatile.Write(ref _videoWindow, new StrongBox<RoiRect>(window));
                    Volatile.Write(ref _videoWindowSettle, new VideoWindowSettle(stream.JogsApplied, Interlocked.Read(ref _frameSequence) + 1));
                    (settling, settleNext) = (false, false);
                    silentFor = TimeSpan.Zero;
                    stream.Silence.ModeChanged();
                    Logger.LogInformation("{Camera} streaming video again: {Width}x{Height} at ({X}, {Y}), {Depth}", Name, window.Width, window.Height,
                        window.X, window.Y, bitDepth);
                }

                // The count is read before the deltas are taken, so every jog it counts is in them.
                var asked = Interlocked.Read(ref _videoJogsAsked);
                var dx = Interlocked.Exchange(ref _stagedJogX, 0);
                var dy = Interlocked.Exchange(ref _stagedJogY, 0);
                if (dx != 0 || dy != 0)
                {
                    window = constraints.Snap(window with { X = window.X + dx, Y = window.Y + dy });
                    SetVideoWindow(window, bitDepth, resize: false);
                    // Once the body has it: the window a pan left, as the camera took it, never the jog that asked for it.
                    Logger.LogDebugOffThread("{Camera}'s stream window is {Width}x{Height} at ({X}, {Y}) after a pan", Name, window.Width, window.Height, window.X, window.Y);
                    stream.Window = window;
                    stream.JogsApplied = asked;
                    // Started before the window is published, so whoever sees the new window knows its settle is under way.
                    settleStarted = TimeProvider.GetTimestamp();
                    Volatile.Write(ref _videoWindow, new StrongBox<RoiRect>(window));
                    Volatile.Write(ref _videoWindowSettle, new VideoWindowSettle(asked, long.MaxValue));
                    (settling, settleNext) = (true, false);
                }
                else if (asked != stream.JogsApplied && !settling && !settleNext)
                {
                    // Jogs that came to nothing (one way and back before they were applied): the window, and with it every
                    // frame read since it settled, stands.
                    stream.JogsApplied = asked;
                    if (Volatile.Read(ref _videoWindowSettle) is { } settled)
                    {
                        Volatile.Write(ref _videoWindowSettle, settled with { JogsApplied = asked });
                    }
                }

                // A settle is bounded in time: buffers that never run dry (a reader that never catches up) end it as empty
                // ones would, or the recenter loop, which measures only frames under the window, would never act again.
                if (settling && TimeProvider.GetElapsedTime(settleStarted) >= VideoSettleLimit)
                {
                    (settling, settleNext) = (false, true);
                }

                // After a pan, a frame that is already there is one the body took before it: asked for without a wait,
                // until one has to be waited for.
                var code = _deviceInfo.GetVideoData(stream.Buffer, FrameBytes(window, bitDepth), settling ? 0 : (int)VideoFrameWait.TotalMilliseconds);
                if (settling && code is not CMOSErrorCode.Success and not CMOSErrorCode.CameraRemoved)
                {
                    // Buffers with nothing more in them (Timeout), and an answer an SDK gives a call it was never asked
                    // to make without a wait (a ZWO pans mid-stream too), alike end the settle: never a stall, which would
                    // restart the stream for every recentring pan. A body that has really stalled says so again to the
                    // next call, which waits.
                    (settling, settleNext) = (false, true);
                    continue;
                }
                // No frame in the wait, which is no failure, until the silence passes the stream's limit (VideoSilenceWatch):
                // then the body has stalled though it says no more than Timeout, and it is the same stall, and the same
                // ladder, as one it reports.
                if (code is CMOSErrorCode.Timeout && (silentFor += VideoFrameWait) <= stream.Silence.Limit)
                {
                    continue;
                }
                if (code is CMOSErrorCode.CameraRemoved)
                {
                    segment.Removed = true;
                    Check(code, "read a video frame");
                }
                if (code is not CMOSErrorCode.Success)
                {
                    var healthy = segment.Frames > 0 && TimeProvider.GetElapsedTime(firstFrame) >= VideoHealthyAfter;
                    segment.Stall = new VideoStall(code, lastFrame, lastFrameAt, healthy);
                    // A stall of the camera's own is one that came after frames (or before the first); a segment a rung
                    // started that never had a frame is that rung failing, which the stream says instead.
                    if (segment.Recovering is null || segment.Frames > 0)
                    {
                        Interlocked.Increment(ref _videoStalls);
                        Logger.LogWarning("{Camera}'s stream stalled ({Code}), {Since:F2} s after its last frame at {LastFrame:o}: stopping it",
                            Name, code, TimeProvider.GetElapsedTime(lastFrame).TotalSeconds, lastFrameAt);
                    }
                    return;
                }

                var frame = DecodeVideoFrame(stream.Buffer, window, bitDepth, stream.Exposure, gain);
                stream.Silence.Frame(silentFor);
                silentFor = TimeSpan.Zero;
                lastFrame = TimeProvider.GetTimestamp();
                lastFrameAt = frame.ImageMeta.ExposureStartTime;
                if (segment.Frames++ == 0)
                {
                    firstFrame = lastFrame;
                    if (segment.Recovering is { } recovered)
                    {
                        NoteVideoRecovery(recovered, segment.Rung);
                    }
                }
                if (settleNext)
                {
                    Volatile.Write(ref _videoWindowSettle, new VideoWindowSettle(stream.JogsApplied, frame.ImageMeta.FrameSequence));
                    settleNext = false;
                }
                Interlocked.Increment(ref _videoFramesRead);
                if (_deviceInfo.TryGetDroppedFrames(out var dropped))
                {
                    stream.DroppedSinceStart = dropped;
                    Volatile.Write(ref _videoDroppedFrames, stream.DroppedEarlier + dropped);
                }
                HandOff(frames, frame, handOffLimit);
            }
        }
        finally
        {
            if (streaming)
            {
                var stopping = TimeProvider.GetTimestamp();
                StopBodyStream(stream, ref streaming, null);
                if (segment.Stall is not null)
                {
                    Logger.LogInformation("{Camera}'s stalled stream took {Seconds:F1} s to stop", Name, TimeProvider.GetElapsedTime(stopping).TotalSeconds);
                }
            }
        }
    }

    /// <summary>Starts the body's stream; its count of lost frames starts again with it.</summary>
    private void StartBodyStream(VideoStream stream, ref bool streaming, string what)
    {
        Check(_deviceInfo.StartVideoCapture(), what);
        streaming = true;
        stream.DroppedEarlier += stream.DroppedSinceStart;
        stream.DroppedSinceStart = 0;
    }

    /// <summary>Stops the body's stream, counting what it lost first; <paramref name="what"/> null for a stop whose refusal is only logged.</summary>
    private void StopBodyStream(VideoStream stream, ref bool streaming, string? what)
    {
        if (_deviceInfo.TryGetDroppedFrames(out var dropped))
        {
            stream.DroppedSinceStart = dropped;
            Volatile.Write(ref _videoDroppedFrames, stream.DroppedEarlier + dropped);
        }
        var stopped = _deviceInfo.StopVideoCapture();
        streaming = false;
        if (what is not null)
        {
            Check(stopped, what);
        }
        else if (stopped is not CMOSErrorCode.Success)
        {
            Logger.LogWarning("{Camera} did not stop its video stream: {Code}", Name, stopped);
        }
    }

    /// <summary>Records a stall the stream came back from, at its first frame after: the gap is from the last frame before the stall to this one.</summary>
    private void NoteVideoRecovery(VideoStall stall, VideoRecoveryRung rung)
    {
        var gap = TimeProvider.GetElapsedTime(stall.LastFrameTimestamp);
        _videoRecoveryLog = _videoRecoveryLog.Add(new VideoStallRecovery(stall.LastFrameAt, stall.Code, rung, gap));
        Interlocked.Increment(ref _videoRecoveries);
        Logger.LogWarning("{Camera}'s stream is back after its stall ({Code}): a {Rung} brought it back, {Gap:F1} s from its last frame to this one",
            Name, stall.Code, rung, gap.TotalSeconds);
    }

    /// <summary>
    /// Gives <paramref name="frame"/> to the reader. Past the budget's count of frames waiting the OLDEST goes first, as
    /// the channel's own capacity drops it, counted the same way.
    /// </summary>
    private void HandOff(Channel<Image> frames, Image frame, int limit)
    {
        while (frames.Reader.Count >= limit && frames.Reader.TryRead(out var oldest))
        {
            Interlocked.Increment(ref _videoFramesReplaced);
            oldest.Release();
        }
        if (!frames.Writer.TryWrite(frame))
        {
            frame.Release();
        }
        if (frames.Reader.Count is var waiting && waiting > Volatile.Read(ref _videoHandOffHighWater))
        {
            Volatile.Write(ref _videoHandOffHighWater, waiting);
        }
    }

    /// <summary>The frames of <paramref name="window"/> the hand-off holds: its budget's worth of planes, at least two, at most <see cref="VideoHandOffMaxFrames"/>.</summary>
    internal static int HandOffFrames(RoiRect window)
        => (int)Math.Clamp(VideoHandOffBudgetBytes / Math.Max(1L, (long)window.Width * window.Height * sizeof(float)), 2L, VideoHandOffMaxFrames);

    private static void EnsureVideoBuffer(VideoStream stream, int needed)
    {
        if (needed > stream.BufferSize)
        {
            if (stream.Buffer != 0)
            {
                Marshal.FreeCoTaskMem(stream.Buffer);
                stream.Buffer = 0;
            }
            stream.BufferSize = needed;
            stream.Buffer = Marshal.AllocCoTaskMem(needed);
        }
    }

    /// <summary>The depth a stream asked for <paramref name="asked"/> reads out in: that one where the camera has it, else the camera's own.</summary>
    private BitDepth StreamDepth(BitDepth? asked)
        => asked is { } depth && depth.IsIntegral && _supportedBitDepth.Contains(depth) ? depth : _cameraSettings.BitDepth;

    /// <summary>
    /// Gives the stream the body's whole USB bandwidth, where it has the control, and answers what it was so the stream
    /// gives it back as it ends; null when there is nothing to give back. A body that refuses streams on at what it had.
    /// </summary>
    private int? TakeWholeBandwidth()
    {
        if (!_deviceInfo.TryGetControlRange(CMOSControlType.BandwidthOverload, out _, out var most)
            || _deviceInfo.GetControlValue(CMOSControlType.BandwidthOverload, out var found, out _) is not CMOSErrorCode.Success)
        {
            return null;
        }
        if (_deviceInfo.SetControlValue(CMOSControlType.BandwidthOverload, most) is var set and not CMOSErrorCode.Success)
        {
            Logger.LogWarning("{Camera} would not take USB bandwidth {Bandwidth} for its stream: {Code}", Name, most, set);
            return null;
        }
        return found;
    }

    /// <summary>Sets the body's window: its size and format too when <paramref name="resize"/>, which needs the stream stopped.</summary>
    private void SetVideoWindow(RoiRect window, BitDepth bitDepth, bool resize)
    {
        if (resize)
        {
            Check(_deviceInfo.SetROIFormat(window.Width, window.Height, Math.Max(1, (int)_cameraSettings.BinX), bitDepth.ToRawPixelFormat()),
                $"set the video window to {window.Width}x{window.Height}");
        }
        Check(_deviceInfo.SetStartPosition(window.X, window.Y), $"move the video window to ({window.X}, {window.Y})");
    }

    private void SetVideoControl(CMOSControlType control, int value, string what) => Check(_deviceInfo.SetControlValue(control, value), what);

    private short ClampedGain(int gain) => GainMin <= GainMax ? (short)Math.Clamp(gain, GainMin, GainMax) : (short)gain;

    private short CurrentGain() => _deviceInfo.GetControlValue(CMOSControlType.Gain, out var gain, out _) is CMOSErrorCode.Success ? (short)gain : (short)-1;

    private void Check(CMOSErrorCode code, string what)
    {
        if (code is not CMOSErrorCode.Success)
        {
            throw OperationalException(code, $"{Name} could not {what}");
        }
    }

    private static int FrameBytes(RoiRect window, BitDepth bitDepth) => bitDepth.BitSize / 8 * window.Width * window.Height;

    /// <summary>The frame in the SDK's buffer as an <see cref="Image"/> on a recycled plane, the reader's to release.</summary>
    private Image DecodeVideoFrame(nint buffer, RoiRect window, BitDepth bitDepth, TimeSpan exposure, short gain)
    {
        var plane = _videoPlanes.Take(window.Height, window.Width);
        var (min, max) = WidenNativeFrame(buffer, plane, bitDepth);
        var meta = new ImageMeta(
            Name,
            TimeProvider.GetUtcNow(),
            exposure,
            FrameType.Light,
            Telescope ?? "",
            (float)PixelSizeX,
            (float)PixelSizeY,
            FocalLength,
            FocusPosition,
            Filter,
            _cameraSettings.BinX,
            _cameraSettings.BinY,
            float.NaN,
            SensorType,
            SensorType is SensorType.RGGB ? BayerOffsetX : 0,
            SensorType is SensorType.RGGB ? BayerOffsetY : 0,
            RowOrder.TopDown,
            (float)(Latitude ?? double.NaN),
            (float)(Longitude ?? double.NaN),
            ObjectName: Target?.Name ?? "",
            Gain: gain,
            ElectronsPerADU: (float)ElectronsPerADU,
            SWCreator: External.SWCreator,
            Aperture: Aperture ?? -1,
            SensorModel: SensorModelName ?? "",
            SensorFullScaleAdu: ICameraDriver.DeclarableFullScale(MaxAduFor(bitDepth), bitDepth),
            SiteElevation: (float)(SiteElevation ?? double.NaN))
        {
            FrameSequence = Interlocked.Increment(ref _frameSequence),
            FrameCounterSource = FrameCounterSource,
        };
        return new Image([_videoPlanes.Wrap(plane, min, max, 0)], bitDepth, pedestal: 0f, meta);
    }
}
