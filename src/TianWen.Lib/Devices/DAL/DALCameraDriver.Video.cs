using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.DAL;

/// <summary>
/// Video (#813, docs/plans/planetary-native-video.md, Phase D): a body that streams
/// (<see cref="ICMOSNativeInterface.CanVideoCapture"/>) serves a planetary or focusing live view from one continuous
/// readout, rather than from an exposure started, awaited and downloaded per frame, which is what held an ASI462MC at
/// 5.8 frames a second at full frame.
/// </summary>
/// <remarks>
/// <para><b>One thread owns the SDK while a stream runs.</b> The vendors' frame call blocks until a frame arrives, so it
/// runs on a thread of the stream's own, never a pool thread, and so does every other SDK call the stream makes: the
/// exposure, the gain, and the window's pan and size are STAGED by their callers and applied there between frames.
/// Frames reach the reader through a one-frame hand-off which, when the reader falls behind, drops the older frame and
/// gives its plane back, so a slow reader sees the newest frame, never a queue of old ones.</para>
/// <para><b>A body streams OR exposes.</b> A stream will not start while an exposure runs, and an exposure will not start
/// while a stream runs. The window a stream set is put back by the single-frame path's own check, which compares the
/// body's window with its settings before every exposure.</para>
/// <para><b>A disconnect stops the stream first</b>, and waits for its thread to leave the SDK: closing the body under a
/// frame call is the one thing a stream must never see.</para>
/// </remarks>
internal abstract partial class DALCameraDriver<TDevice, TDeviceInfo> : IVideoCameraDriver
{
    /// <summary>
    /// The longest one frame call waits, so a stop is seen within about this long whatever the exposure. A frame
    /// that takes longer simply answers Timeout, and the stream asks again.
    /// </summary>
    internal static readonly TimeSpan VideoFrameWait = TimeSpan.FromMilliseconds(250);

    /// <summary>The smallest window side a stream offers: a planetary disc or a focusing star needs more than a few pixels.</summary>
    private const int MinimumVideoWindowSide = 16;

    private int _videoActive;
    private int _videoStopRequested;
    private Task? _videoStreamEnded;

    // The window as the capture thread last set it, published whole; null while no stream runs.
    private StrongBox<RoiRect>? _videoWindow;

    private int _videoDroppedFrames;

    // What the stream did, for its closing log line: frames read from the body, and frames the reader never took
    // because a newer one replaced them in the hand-off first.
    private long _videoFramesRead;
    private long _videoFramesReplaced;

    // Staged for the capture thread, which takes each one as it applies it: 0 ticks and -1 gain are "nothing staged".
    private long _stagedVideoExposureTicks;
    private int _stagedVideoGain = -1;
    private int _stagedJogX;
    private int _stagedJogY;

    private readonly PlaneRecycler _videoPlanes = new PlaneRecycler("DALCameraDriver.Video");

    /// <summary>How many planes the stream has had to allocate: a steady stream allocates none.</summary>
    internal int VideoPlanesAllocated => _videoPlanes.PlanesAllocated;

    /// <inheritdoc/>
    public bool CanVideoCapture => Connected && _deviceInfo.CanVideoCapture;

    /// <inheritdoc/>
    public bool CanJogRoi => Volatile.Read(ref _videoActive) == 1 && _deviceInfo.CanPanRoiWhileStreaming;

    /// <inheritdoc/>
    public int DroppedFrames => Volatile.Read(ref _videoActive) == 1 ? Volatile.Read(ref _videoDroppedFrames) : 0;

    /// <inheritdoc/>
    public RoiRect VideoRoi => Volatile.Read(ref _videoWindow) is { } window
        ? window.Value
        : new RoiRect(0, 0, CameraXSize, CameraYSize);

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

        var frames = System.Threading.Channels.Channel.CreateBounded<Image>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
            dropped =>
            {
                Interlocked.Increment(ref _videoFramesReplaced);
                dropped.Release();
            });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _videoStopRequested, 0);
        Interlocked.Exchange(ref _stagedVideoExposureTicks, 0);
        Interlocked.Exchange(ref _stagedVideoGain, -1);
        Interlocked.Exchange(ref _stagedJogX, 0);
        Interlocked.Exchange(ref _stagedJogY, 0);
        Interlocked.Exchange(ref _videoFramesRead, 0);
        Interlocked.Exchange(ref _videoFramesReplaced, 0);
        var threadStarted = false;
        try
        {
            var thread = new Thread(() => RunVideoStream(options, frames.Writer, started, ended, cancellationToken))
            {
                IsBackground = true,
                Name = $"{Name} video",
            };
            Volatile.Write(ref _videoStreamEnded, ended.Task);
            thread.Start();
            threadStarted = true;

            // A stream the body refused to start throws here, naming what was refused.
            await started.Task.ConfigureAwait(false);

            while (await NextVideoFrameAsync(frames.Reader, cancellationToken).ConfigureAwait(false) is { } frame)
            {
                yield return frame;
            }
        }
        finally
        {
            Volatile.Write(ref _videoStopRequested, 1);
            if (threadStarted)
            {
                await ended.Task.ConfigureAwait(false);
            }
            while (frames.Reader.TryRead(out var unread))
            {
                unread.Release();
            }
            Volatile.Write(ref _videoStreamEnded, null);
            Interlocked.Exchange(ref _videoActive, 0);
        }
    }

    /// <summary>The stream's next frame; null once it has ended or been cancelled, and its failure thrown if it failed.</summary>
    private static async ValueTask<Image?> NextVideoFrameAsync(ChannelReader<Image> frames, CancellationToken cancellationToken)
    {
        try
        {
            while (await frames.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (frames.TryRead(out var frame))
                {
                    return frame;
                }
            }
            return null;
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
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops a stream going on and waits for its thread to leave the SDK, before the body is closed.</summary>
    protected override async Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _videoStreamEnded) is { } streamEnded)
        {
            Volatile.Write(ref _videoStopRequested, 1);
            await streamEnded.ConfigureAwait(false);
        }
        return await base.DoDisconnectDeviceAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The stream's own thread: sets the body up, starts it, and reads frames until asked to stop, the camera is
    /// disconnected, or the body fails. Every SDK call of the stream is made here.
    /// </summary>
    private void RunVideoStream(VideoCaptureOptions options, ChannelWriter<Image> frames, TaskCompletionSource started, TaskCompletionSource ended,
        CancellationToken cancellationToken)
    {
        nint buffer = 0;
        var bufferSize = 0;
        var streaming = false;
        var highSpeed = _deviceInfo.TryGetControlRange(CMOSControlType.HighSpeedMode, out _, out _);
        Exception? failure = null;
        var streamStart = TimeProvider.GetTimestamp();
        try
        {
            var bitDepth = _cameraSettings.BitDepth;
            var exposure = options.Exposure;
            SetVideoControl(CMOSControlType.Exposure, ExposureControlValue(exposure), "set the exposure");
            if (options.Gain is { } startGain)
            {
                SetVideoControl(CMOSControlType.Gain, ClampedGain(startGain), "set the gain");
            }
            if (highSpeed)
            {
                SetVideoControl(CMOSControlType.HighSpeedMode, options.HighSpeedMode ? 1 : 0, "set the high-speed readout");
            }
            var gain = CurrentGain();

            var constraints = RoiConstraints;
            var window = constraints.Snap(RoiRect.Centered(constraints.MaxWidth, constraints.MaxHeight, NumX, NumY));
            SetVideoWindow(window, bitDepth, resize: true);
            bufferSize = FrameBytes(window, bitDepth);
            buffer = Marshal.AllocCoTaskMem(bufferSize);

            Check(_deviceInfo.StartVideoCapture(), "start the video stream");
            streaming = true;
            Volatile.Write(ref _videoDroppedFrames, 0);
            Volatile.Write(ref _videoWindow, new StrongBox<RoiRect>(window));
            started.TrySetResult();
            Logger.LogInformation("{Camera} streaming video: {Width}x{Height} at ({X}, {Y}), {Depth}, {Exposure} ms",
                Name, window.Width, window.Height, window.X, window.Y, bitDepth, exposure.TotalMilliseconds);

            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _videoStopRequested) == 0 && Connected)
            {
                if (Interlocked.Exchange(ref _stagedVideoExposureTicks, 0) is > 0 and var ticks)
                {
                    exposure = new TimeSpan(ticks);
                    SetVideoControl(CMOSControlType.Exposure, ExposureControlValue(exposure), "set the exposure");
                }
                if (Interlocked.Exchange(ref _stagedVideoGain, -1) is >= 0 and var stagedGain)
                {
                    SetVideoControl(CMOSControlType.Gain, ClampedGain(stagedGain), "set the gain");
                    gain = CurrentGain();
                }

                // A new size needs the stream stopped on every body; the origin is kept where it fits.
                if (constraints.SnapWidth(NumX) != window.Width || constraints.SnapHeight(NumY) != window.Height)
                {
                    Check(_deviceInfo.StopVideoCapture(), "stop the video stream to resize it");
                    streaming = false;
                    window = constraints.Snap(window with { Width = NumX, Height = NumY });
                    SetVideoWindow(window, bitDepth, resize: true);
                    if (FrameBytes(window, bitDepth) is var needed && needed > bufferSize)
                    {
                        Marshal.FreeCoTaskMem(buffer);
                        buffer = 0;
                        bufferSize = needed;
                        buffer = Marshal.AllocCoTaskMem(bufferSize);
                    }
                    Check(_deviceInfo.StartVideoCapture(), "restart the video stream at its new size");
                    streaming = true;
                    Volatile.Write(ref _videoWindow, new StrongBox<RoiRect>(window));
                }

                var dx = Interlocked.Exchange(ref _stagedJogX, 0);
                var dy = Interlocked.Exchange(ref _stagedJogY, 0);
                if (dx != 0 || dy != 0)
                {
                    window = constraints.Snap(window with { X = window.X + dx, Y = window.Y + dy });
                    SetVideoWindow(window, bitDepth, resize: false);
                    Volatile.Write(ref _videoWindow, new StrongBox<RoiRect>(window));
                }

                var code = _deviceInfo.GetVideoData(buffer, FrameBytes(window, bitDepth), (int)VideoFrameWait.TotalMilliseconds);
                if (code is CMOSErrorCode.Timeout)
                {
                    continue;
                }
                Check(code, "read a video frame");

                var frame = DecodeVideoFrame(buffer, window, bitDepth, exposure, gain);
                Interlocked.Increment(ref _videoFramesRead);
                if (_deviceInfo.TryGetDroppedFrames(out var dropped))
                {
                    Volatile.Write(ref _videoDroppedFrames, dropped);
                }
                if (!frames.TryWrite(frame))
                {
                    frame.Release();
                }
            }
        }
        catch (Exception ex)
        {
            // Everything that goes wrong on this thread is reported to the reader: an exception left to escape a thread
            // of its own would end the process.
            failure = ex;
        }
        finally
        {
            if (streaming && _deviceInfo.StopVideoCapture() is var stopped and not CMOSErrorCode.Success)
            {
                Logger.LogWarning("{Camera} did not stop its video stream: {Code}", Name, stopped);
            }
            if (highSpeed)
            {
                // Back to what the camera's settings ask of a single exposure.
                _ = _deviceInfo.SetControlValue(CMOSControlType.HighSpeedMode, Convert.ToInt32(_cameraSettings.FastReadout));
            }
            Volatile.Write(ref _videoWindow, null);
            if (buffer != 0)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
            var read = Interlocked.Read(ref _videoFramesRead);
            var replaced = Interlocked.Read(ref _videoFramesReplaced);
            var seconds = TimeProvider.GetElapsedTime(streamStart).TotalSeconds;
            Logger.LogInformation(
                "{Camera} streamed {Frames} frames in {Seconds:F1} s ({Rate:F1} a second): the reader took {Taken}, {Replaced} were replaced by newer ones first, the body dropped {Dropped}",
                Name, read, seconds, seconds > 0 ? read / seconds : 0, read - replaced, replaced, Volatile.Read(ref _videoDroppedFrames));
            if (failure is not null)
            {
                Logger.LogError(failure, "{Camera}'s video stream failed", Name);
                started.TrySetException(failure);
            }
            else
            {
                started.TrySetResult();
            }
            frames.TryComplete(failure);
            ended.TrySetResult();
        }
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
            SensorFullScaleAdu: ICameraDriver.DeclarableFullScale(MaxADU, bitDepth),
            SiteElevation: (float)(SiteElevation ?? double.NaN))
        {
            FrameSequence = Interlocked.Increment(ref _frameSequence),
            FrameCounterSource = FrameCounterSource,
        };
        return new Image([_videoPlanes.Wrap(plane, min, max, 0)], bitDepth, pedestal: 0f, meta);
    }
}
