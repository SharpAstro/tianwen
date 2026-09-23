using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.DAL;

/// <summary>
/// What a DAL camera does about an exposure that never finishes: give it up at a deadline, and,
/// when that keeps happening on a body that can be reset from software, reset it and carry on.
/// </summary>
/// <remarks>
/// <para><b>Why the deadline exists at all: a lost frame used to freeze a night silently.</b> The
/// imaging loop only starts an exposure on a camera that reports <see cref="CameraState.Idle"/>, and a
/// frame the camera never delivers leaves it reporting <see cref="CameraState.Exposing"/> for ever, so
/// the loop stops starting frames and nothing reports an error. Measured on a ToupTek G3M678M
/// 2026-09-23: over 600 software-triggered frames with the bit depth changed by a close and reopen
/// every 20, one frame never arrived and the SDK raised no event at all (with both its no-frame and
/// no-packet timeouts enabled), and the next trigger worked. Only a clock can tell that frame from a
/// slow one.</para>
/// <para><b>Why the reset is the second step and not the first.</b> The same camera in video mode
/// stalled 2 times in 80 fresh opens that changed resolution, bit depth or speed, reporting
/// <c>TOUPCAM_EVENT_NOPACKETTIMEOUT</c> every ~0.5 s. Once it stopped after 895 frames of a stream, once
/// at the start; that one survived both a restart of the stream on the same handle and a close and
/// reopen, and came back only after <c>TOUPCAM_OPTION_DEVICE_RESET</c>, streaming again after 2.8 s.
/// So a single lost frame is abandoned and the next one tried, which is enough for the triggered
/// case; a second loss in a row means the camera is wedged, and a body with
/// <see cref="INativeDeviceInfo.CanResetDevice"/> is reset before the next exposure. Configuration
/// changes are the trigger on this family, and the owner reports the same of QHY bodies under SharpCap.</para>
/// <para><b>A reset is a replug.</b> The device drops off the bus, re-enumerates, and comes back
/// with every setting at its power-on default, cooler included, so the driver re-finds it by the
/// same identity its connect used and restores what it was running with. On a cooled camera that is
/// a thermal event, which is why nothing here resets on the first loss.</para>
/// </remarks>
internal abstract partial class DALCameraDriver<TDevice, TDeviceInfo>
{
    /// <summary>Consecutive lost exposures after which a resettable camera is reset.</summary>
    internal const int ResetAfterConsecutiveLostExposures = 2;

    /// <summary>How long a reset camera is given to reappear on the bus (measured: 0.6 to 2.8 s).</summary>
    internal static readonly TimeSpan ResetReappearTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ResetReappearPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>Losses since the last frame that arrived, counted across exposures.</summary>
    private int _consecutiveLostExposures;

    /// <summary>The frame sequence number a loss was last recorded for, so a state poll racing the
    /// imaging loop cannot count one lost frame twice.</summary>
    private long _lostExposureSequence = long.MinValue;

    /// <summary>
    /// How long past its intended end an exposure may run before it is declared lost: fifteen seconds
    /// for readout and transfer, plus a tenth of the exposure for clock drift over a long one.
    /// </summary>
    /// <remarks>
    /// Generous on purpose, since abandoning a frame that WAS coming costs that frame, while waiting
    /// a little longer for one that is not costs seconds. A full 16-bit frame of the largest sensors
    /// the DAL drives reads out in a few seconds over USB 3, well inside the floor.
    /// </remarks>
    internal static TimeSpan LostExposureGrace(TimeSpan duration) => TimeSpan.FromSeconds(15) + duration / 10;

    /// <summary>Losses in a row; exposed for tests.</summary>
    internal int ConsecutiveLostExposures => Volatile.Read(ref _consecutiveLostExposures);

    private bool ShouldResetBeforeNextExposure
        => Volatile.Read(ref _consecutiveLostExposures) >= ResetAfterConsecutiveLostExposures
           && _deviceInfo.CanResetDevice;

    private bool IsPastLostExposureDeadline()
        => _exposureData is { } data
           && TimeProvider.GetUtcNow() > data.StartTime + data.IntendedDuration + LostExposureGrace(data.IntendedDuration);

    /// <summary>
    /// Gives up an exposure the camera is not going to deliver, and returns the camera to idle so the
    /// next one can be started.
    /// </summary>
    /// <remarks>
    /// Stops the exposure on the device directly rather than through the stop path, which marks the
    /// frame ready to download: that is right for a stopped exposure the camera still holds, and
    /// wrong for one that never arrived.
    /// </remarks>
    private void AbandonLostExposure(string state)
    {
        if (!NoteLostExposure($"{state} past its deadline"))
        {
            return;
        }

        _ = _deviceInfo.StopExposure();
        Interlocked.Exchange(ref _camImageReady, IMAGE_STATE_NO_IMG);
        _camState = CameraState.Idle;
    }

    /// <summary>
    /// Records one lost exposure, once per frame. Returns false when this frame's loss was already
    /// recorded by a racing poll.
    /// </summary>
    private bool NoteLostExposure(string reason)
    {
        var sequence = Interlocked.Read(ref _frameSequence);
        if (Interlocked.Exchange(ref _lostExposureSequence, sequence) == sequence)
        {
            return false;
        }

        var lost = Interlocked.Increment(ref _consecutiveLostExposures);
        var data = _exposureData;
        Logger.LogWarning(
            "Camera {Name} lost frame {Sequence} ({Duration} exposure started {Start:o}): {Reason}. {Lost} lost in a row{Next}.",
            Name, sequence, data?.IntendedDuration, data?.StartTime, reason, lost,
            lost >= ResetAfterConsecutiveLostExposures
                ? _deviceInfo.CanResetDevice ? "; the camera will be reset before the next exposure" : "; this camera cannot be reset from software, so the next exposure is simply tried"
                : "; the next exposure is tried as normal");
        return true;
    }

    private async ValueTask<DateTimeOffset> ResetThenStartExposureAsync(TimeSpan duration, FrameType frameType, CancellationToken cancellationToken)
    {
        await ResetAndReopenAsync(cancellationToken);
        return StartExposureCore(duration, frameType);
    }

    /// <summary>
    /// Resets the camera, finds it again by the identity it was connected by, and restores the
    /// settings it was running with.
    /// </summary>
    /// <remarks>
    /// <para>The loss count is cleared whatever the outcome, so a reset that did not help is not
    /// repeated before every frame; it takes two more losses to try again.</para>
    /// <para>Settings are read BEFORE the reset, from the wedged device, which still answers reads
    /// (every call kept succeeding through the measured stalls). A control that cannot be read is not
    /// restored and is logged, rather than guessed at.</para>
    /// <para>Internal rather than private for <c>ToupTekResetRecoveryProbe</c>, the hardware check
    /// that the whole path works on a real camera; nothing else calls it.</para>
    /// </remarks>
    internal async ValueTask ResetAndReopenAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _consecutiveLostExposures, 0);

        var settings = _cameraSettings;
        var restore = new List<(CMOSControlType Control, int Value)>();
        foreach (var control in RestoredAfterReset)
        {
            if (_deviceInfo.GetControlValue(control, out var value, out _) is CMOSErrorCode.Success)
            {
                restore.Add((control, value));
            }
        }

        Logger.LogWarning("Resetting camera {Name} after {Count} lost exposures in a row, as a replug would.",
            Name, ResetAfterConsecutiveLostExposures);
        var resetResult = _deviceInfo.ResetDevice();
        if (resetResult is not CMOSErrorCode.Success)
        {
            Logger.LogError("Camera {Name} refused the reset ({Error}); the next exposure is tried as is.", Name, resetResult);
            return;
        }

        var waitedSince = TimeProvider.GetTimestamp();
        while (true)
        {
            var (found, _, deviceInfo) = await DoConnectDeviceAsync(cancellationToken);
            if (found)
            {
                _deviceInfo = deviceInfo;
                break;
            }

            if (TimeProvider.GetElapsedTime(waitedSince) >= ResetReappearTimeout)
            {
                _camState = CameraState.Error;
                Logger.LogError("Camera {Name} did not reappear within {Timeout} of its reset; unplug and replug it.",
                    Name, ResetReappearTimeout);
                throw OperationalException(CMOSErrorCode.CameraRemoved, $"Camera did not reappear within {ResetReappearTimeout} of a reset");
            }

            await TimeProvider.SleepAsync(ResetReappearPoll, cancellationToken);
        }

        InitCamera();
        _cameraSettings = settings;
        _ = _deviceInfo.SetControlValue(CMOSControlType.HighSpeedMode, settings.FastReadout ? 1 : 0);
        foreach (var (control, value) in restore)
        {
            if (_deviceInfo.SetControlValue(control, value) is not CMOSErrorCode.Success and var error)
            {
                Logger.LogWarning("Camera {Name}: could not restore {Control} = {Value} after the reset ({Error}).",
                    Name, control, value, error);
            }
        }

        Logger.LogInformation("Camera {Name} is back after its reset, {Elapsed:F1} s, with {Count} settings restored.",
            Name, TimeProvider.GetElapsedTime(waitedSince).TotalSeconds, restore.Count);
    }

    /// <summary>
    /// What a reset puts back to the power-on default and the driver restores: the exposure's own
    /// levels, the colour balance, and the cooler, whose target is written before it is switched on.
    /// Region, binning and bit depth are not here: they live in the driver's settings and every
    /// exposure applies them.
    /// </summary>
    private static readonly CMOSControlType[] RestoredAfterReset =
    [
        CMOSControlType.Gain,
        CMOSControlType.Brightness,
        CMOSControlType.WB_R,
        CMOSControlType.WB_G,
        CMOSControlType.WB_B,
        CMOSControlType.TargetTemperature,
        CMOSControlType.CoolerOn,
    ];
}
