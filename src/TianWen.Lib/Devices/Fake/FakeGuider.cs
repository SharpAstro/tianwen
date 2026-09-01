using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Fake;

internal class FakeGuider(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakeDeviceDriverBase(fakeDevice, serviceProvider), IDeviceDependentGuider
{

    private const double DefaultPixelScale = 1.5;

    /// <summary>
    /// Mount driver for reading current RA/Dec. Set via <see cref="LinkDevices"/>.
    /// </summary>
    private IMountDriver? _mount;

    /// <summary>
    /// Current pointing RA in hours (J2000). Set by the test or read from the mount driver.
    /// </summary>
    internal double PointingRA { get; set; } = double.NaN;

    /// <summary>
    /// Current pointing Dec in degrees (J2000). Set by the test or read from the mount driver.
    /// </summary>
    internal double PointingDec { get; set; } = double.NaN;

    /// <summary>
    /// Guider camera for reading sensor dimensions. Set via <see cref="LinkDevices"/>.
    /// </summary>
    private ICameraDriver? _camera;

    /// <inheritdoc/>
    public void LinkDevices(IMountDriver mount, ICameraDriver? camera)
    {
        _mount = mount;
        _camera = camera;
    }

    private int _state = (int)GuiderState.Idle;
    private bool _equipmentConnected;
    private bool _paused;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private GuideLoop? _guideLoop;
    private volatile Image? _lastLoopFrame;

    private double _settlePixels;
    private double _settleTime;
    private double _ditherPixels;
    private long _settleStartedTicks;

    private int _guideStartAttempts;

    // --- Test hook: simulate the built-in guider recovering a lock IN PLACE (re-acquire after a
    // star loss, recalibrate after a divergence). During the window the guider reports
    // not-guiding + "Calibrating" WITHOUT a GuideAsync call -- exactly as the real driver does
    // while it self-recovers -- then reverts to whatever the underlying state is (still Guiding).
    // Lets a session-loop test verify the session DEFERS to that recovery instead of fighting it
    // (the #4-vs-#3 race: a session restart mid-recovery throws "cannot start in state
    // Calibrating" and reschedules the target). ---
    private long _simulatedRecoveryStartTicks;
    private long _simulatedRecoveryDurationTicks; // 0 = not simulating

    /// <summary>
    /// Number of times <see cref="GuideAsync"/> was invoked. A session that fights an in-place
    /// recovery (calls GuideAsync while the guider reports "Calibrating") is observable here.
    /// </summary>
    internal int GuideStartAttempts => Volatile.Read(ref _guideStartAttempts);

    /// <summary>
    /// Begin a simulated in-place recovery lasting <paramref name="duration"/> of fake time: the
    /// guider reports not-guiding + "Calibrating" for the window, with no GuideAsync call, then
    /// reverts to the real state. See the field comment above.
    /// </summary>
    internal void BeginSimulatedInPlaceRecovery(TimeSpan duration)
    {
        Interlocked.Exchange(ref _simulatedRecoveryStartTicks, TimeProvider.GetTimestamp());
        Interlocked.Exchange(ref _simulatedRecoveryDurationTicks, duration.Ticks);
    }

    private bool InSimulatedRecovery
    {
        get
        {
            var durationTicks = Interlocked.Read(ref _simulatedRecoveryDurationTicks);
            return durationTicks > 0
                && TimeProvider.GetElapsedTime(Interlocked.Read(ref _simulatedRecoveryStartTicks)) < TimeSpan.FromTicks(durationTicks);
        }
    }

    private enum GuiderState
    {
        Idle = 0,
        Looping = 1,
        Calibrating = 2,
        Guiding = 3,
        Settling = 4,
    }

#pragma warning disable CS0067 // Events required by IGuider interface
    public event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;
    public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChangedEvent;
#pragma warning restore CS0067

    private GuiderState CurrentState => (GuiderState)Interlocked.CompareExchange(ref _state, 0, 0);

    private bool TryTransition(GuiderState from, GuiderState to)
    {
        var previous = (GuiderState)Interlocked.CompareExchange(ref _state, (int)to, (int)from);
        return previous == from;
    }

    private void ForceState(GuiderState to)
    {
        Interlocked.Exchange(ref _state, (int)to);
    }

    public ValueTask<(int Width, int Height)?> CameraFrameSizeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_camera is { Connected: true, NumX: > 0, NumY: > 0 } cam
                ? ((int Width, int Height)?)(cam.NumX, cam.NumY)
                : null);

    public ValueTask ConnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        _equipmentConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        _equipmentConnected = false;

        ForceState(GuiderState.Idle);
        return ValueTask.CompletedTask;
    }

    private int _ditherCount;

    /// <summary>
    /// Number of times <see cref="DitherAsync"/> has been called. Used by tests to verify dithering was triggered.
    /// </summary>
    public int DitherCount => _ditherCount;

    public ValueTask DitherAsync(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is not GuiderState.Guiding)
        {
            throw new GuiderException($"Cannot dither in state {current}");
        }

        Interlocked.Increment(ref _ditherCount);

        _ditherPixels = ditherPixels;
        _settlePixels = settlePixels;
        _settleTime = settleTime;

        ForceState(GuiderState.Settling);
        RecordSettleStart();

        return ValueTask.CompletedTask;
    }

    public ValueTask<TimeSpan> ExposureTimeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TimeSpan.FromSeconds(2));

    public ValueTask<string?> GetActiveProfileNameAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>("FakeProfile");

    public ValueTask<IReadOnlyList<string>> GetEquipmentProfilesAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<string>>(["FakeProfile"]);

    public ValueTask<SettleProgress?> GetSettleProgressAsync(CancellationToken cancellationToken = default)
    {
        var state = CurrentState;

        if (state is GuiderState.Settling or GuiderState.Calibrating)
        {
            var tracker = _guideLoop?.ErrorTracker;
            var distance = tracker is { LastRaError: { } ra, LastDecError: { } dec }
                ? Math.Sqrt(ra * ra + dec * dec)
                : _ditherPixels * 0.5;
            var elapsed = TimeProvider.GetElapsedTime(_settleStartedTicks);

            return ValueTask.FromResult<SettleProgress?>(new SettleProgress
            {
                Done = false,
                Distance = distance,
                SettlePx = _settlePixels,
                Time = elapsed.TotalSeconds,
                SettleTime = _settleTime,
                Status = 0,
                StarLocked = tracker?.TotalSamples > 0,
            });
        }

        if (state is GuiderState.Guiding)
        {
            return ValueTask.FromResult<SettleProgress?>(new SettleProgress
            {
                Done = true,
                Distance = 0.1,
                SettlePx = _settlePixels,
                Time = _settleTime,
                SettleTime = _settleTime,
                Status = 0,
                StarLocked = true,
            });
        }

        return ValueTask.FromResult<SettleProgress?>(null);
    }

    public ValueTask<GuideStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentState is not GuiderState.Guiding and not GuiderState.Settling)
        {
            return ValueTask.FromResult<GuideStats?>(null);
        }

        var tracker = _guideLoop?.ErrorTracker;
        var scale = _camera is { PixelSizeX: > 0, FocalLength: > 0 }
            ? Astrometry.CoordinateUtils.PixelScaleArcsec(_camera.PixelSizeX, _camera.FocalLength)
            : DefaultPixelScale;
        return ValueTask.FromResult<GuideStats?>(new GuideStats
        {
            // Recent rolling-window stats (not all-time) so the panel reflects current guide
            // quality and isn't poisoned by an early transient -- mirrors BuiltInGuiderDriver.
            TotalRMS = (tracker?.TotalRmsShort ?? 0.3) * scale,
            RaRMS = (tracker?.RaRmsShort ?? 0.2) * scale,
            DecRMS = (tracker?.DecRmsShort ?? 0.2) * scale,
            PeakRa = (tracker?.PeakRaShort ?? 0.5) * scale,
            PeakDec = (tracker?.PeakDecShort ?? 0.4) * scale,
            LastRaErr = tracker?.LastRaError * scale,
            LastDecErr = tracker?.LastDecError * scale,
        });
    }

    public ValueTask<(string? AppState, double AvgDist)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (InSimulatedRecovery)
        {
            // Recovering a lock in place -- report "Calibrating" (not guiding) as the real driver does.
            return ValueTask.FromResult<(string?, double)>(("Calibrating", 0.0));
        }

        var state = CurrentState;
        var appState = state switch
        {
            GuiderState.Idle => "Stopped",
            GuiderState.Looping => "Looping",
            GuiderState.Calibrating => "Calibrating",
            GuiderState.Guiding => "Guiding",
            GuiderState.Settling => "Settling",
            _ => "Unknown",
        };

        var avgDist = state is GuiderState.Guiding or GuiderState.Settling ? 0.2 : 0.0;
        return ValueTask.FromResult<(string?, double)>((appState, avgDist));
    }

    public ValueTask ClearCalibrationAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask FlipCalibrationAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask GuideAsync(double settlePixels, double settleTime, double settleTimeout, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _guideStartAttempts);

        if (InSimulatedRecovery)
        {
            // Mirror the real driver: starting guiding while it is recovering a lock in place is
            // rejected (BuiltInGuiderDriver.GuideAsync throws for non-Idle/Looping states). A session
            // that calls this mid-recovery is fighting the driver -- the bug this models.
            throw new GuiderException("Cannot start guiding in state Calibrating (simulated in-place recovery)");
        }

        if (!_equipmentConnected)
        {
            throw new GuiderException("Equipment is not connected. Call ConnectEquipmentAsync first.");
        }

        _settlePixels = settlePixels;
        _settleTime = settleTime;

        var current = CurrentState;
        if (current is GuiderState.Guiding)
        {
            // Already guiding: nothing to do
            return ValueTask.CompletedTask;
        }

        if (current is GuiderState.Settling)
        {
            // Already settling: update settle params and restart settle timer
            _settlePixels = settlePixels;
            RecordSettleStart();
            return ValueTask.CompletedTask;
        }

        if (current is not GuiderState.Idle and not GuiderState.Looping)
        {
            throw new GuiderException($"Cannot start guiding in state {current}");
        }

        // Transition to Settling: the shared capture loop (started by LoopAsync) will
        // detect the state change and start applying guide corrections once settled.
        ForceState(GuiderState.Settling);
        RecordSettleStart();

        // If not already looping, start the capture loop now
        if (_camera is { Connected: true } camera && _mount is { Connected: true } mount && _loopCts is null)
        {
            // The lambda reads the LOCAL, never the field. StopCaptureAsync cancels and then nulls
            // _loopCts, and the thread pool may not have started this delegate yet -- so a field read
            // here is a NullReferenceException thrown inside the loop task and surfaced, confusingly,
            // at the awaiting StopCaptureAsync. It needs a busy pool to happen at all, which is why
            // it shows up as a CI-only failure that passes every time it is run on its own.
            var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopCts = loopCts;
            _loopTask = Task.Run(() => RunCaptureLoopAsync(camera, mount, loopCts.Token), loopCts.Token);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Last guide frame as a mono Image.
    /// Returns the guide loop frame (when guiding) or the loop capture frame (when looping).</summary>
    public Image? LastGuideFrame => _guideLoop?.LastFrame ?? _lastLoopFrame;

    /// <summary>
    /// The loop-capture publish path, funnelled through one setter so every site bumps the frame
    /// counter. The fake needs a moving change token as much as real hardware does: an unattended
    /// end-to-end run drives a remote preview against exactly this driver.
    /// </summary>
    private Image? LastLoopFrame
    {
        get => _lastLoopFrame;
        set
        {
            _lastLoopFrame = value;
            Interlocked.Increment(ref _publishedFrameCount);
        }
    }

    private int _publishedFrameCount;

    /// <inheritdoc cref="BuiltInGuiderDriver.LastGuideFrameNumber"/>
    public int LastGuideFrameNumber => Volatile.Read(ref _publishedFrameCount) + (_guideLoop?.PublishedFrameCount ?? 0);

    /// <summary>Guide star position in frame pixels.</summary>
    public (double X, double Y)? GuideStarPosition =>
        _guideLoop?.LastCentroidResult is { } r ? (r.X, r.Y) : null;

    /// <summary>Guide star SNR.</summary>
    public double? GuideStarSNR =>
        _guideLoop?.LastCentroidResult?.SNR;

    /// <summary>Star profile: horizontal and vertical intensity cross-sections.</summary>
    public (float[] H, float[] V)? GuideStarProfile =>
        _guideLoop?.LastCentroidResult is { HProfile: { } h, VProfile: { } v } ? (h, v) : null;

    private void RecordSettleStart()
    {
        Interlocked.Exchange(ref _settleStartedTicks, TimeProvider.GetTimestamp());
    }

    /// <summary>
    /// Checks whether enough (fake) time has elapsed since settling started.
    /// If so, transitions from Settling to Guiding. This is polled by
    /// <see cref="IsGuidingAsync"/> and <see cref="IsSettlingAsync"/>,
    /// making it reliable with <see cref="FakeTimeProvider"/> (no timer callback needed).
    /// </summary>
    private bool TryCompleteSettle()
    {
        if (CurrentState is not GuiderState.Settling)
        {
            return false;
        }

        var elapsed = TimeProvider.GetElapsedTime(_settleStartedTicks);
        if (elapsed.TotalSeconds >= _settleTime)
        {
            TryTransition(GuiderState.Settling, GuiderState.Guiding);
            return true;
        }

        return false;
    }

    public ValueTask<bool> IsGuidingAsync(CancellationToken cancellationToken = default)
    {
        TryCompleteSettle();
        return ValueTask.FromResult(!InSimulatedRecovery && CurrentState is GuiderState.Guiding && !_paused);
    }

    public ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Looping or GuiderState.Guiding or GuiderState.Calibrating or GuiderState.Settling);

    public ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
    {
        TryCompleteSettle();
        return ValueTask.FromResult(InSimulatedRecovery || CurrentState is GuiderState.Settling or GuiderState.Calibrating);
    }

    public async ValueTask<bool> LoopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is GuiderState.Idle)
        {
            ForceState(GuiderState.Looping);

            // Capture one frame immediately so SaveImageAsync has data right away (like PHD2 looping)
            if (_camera is { Connected: true } camera)
            {
                var exposureTime = TimeSpan.FromSeconds(2);
                LastLoopFrame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, TimeProvider, External.ImageReadyPollInterval, cancellationToken);

                // Start the unified capture loop in background, on a LOCAL token source -- see the
                // note at the other start site: the field is nulled by StopCaptureAsync and the
                // delegate may not have run yet.
                var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _loopCts = loopCts;
                _loopTask = Task.Run(() => RunCaptureLoopAsync(camera, _mount!, loopCts.Token), loopCts.Token);
            }
        }

        return true;
    }

    /// <summary>
    /// Unified capture loop: continuously captures frames on the guide camera.
    /// In Looping/Settling state, just captures and stores frames.
    /// When state transitions to Guiding, sets up the GuideLoop and hands off
    /// to <see cref="GuideLoop.RunAsync"/> which takes over the capture loop
    /// with correction computations (like real PHD2).
    /// </summary>
    private async Task RunCaptureLoopAsync(ICameraDriver camera, IMountDriver mount, CancellationToken ct)
    {
        try
        {
            var exposureTime = TimeSpan.FromSeconds(2);
            var ext = TimeProvider;
            var pollInterval = External.ImageReadyPollInterval;

            // Phase 1: Loop capture; expose and store frames until guiding starts
            while (!ct.IsCancellationRequested)
            {
                var state = CurrentState;
                if (state is GuiderState.Idle)
                {
                    return;
                }

                if (state is GuiderState.Guiding or GuiderState.Settling)
                {
                    // Abort any in-flight exposure before transitioning
                    if (await camera.GetCameraStateAsync(ct) is CameraState.Exposing)
                    {
                        await camera.AbortExposureAsync(ct);
                    }
                    break;
                }

                // Capture -> publish -> release the superseded frame, in that order. Release-first
                // left the published pointer dangling at a spent frame for an entire capture, and the
                // very first iteration released the frame LoopAsync had just published -- so a
                // SaveImageAsync racing this loop lost its lease exactly when a caller had been told
                // looping was ready. Same invariant as GuideLoop.RunAsync.
                var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, pollInterval, ct);
                var superseded = _lastLoopFrame;
                LastLoopFrame = frame;
                superseded?.Release();
            }

            // Continue capturing during settle: keeps the guider view updating
            while (!TryCompleteSettle() && CurrentState is GuiderState.Settling && !ct.IsCancellationRequested)
            {
                var settleFrame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, pollInterval, ct);
                var supersededBySettle = _lastLoopFrame;
                LastLoopFrame = settleFrame;
                supersededBySettle?.Release();
            }

            if (ct.IsCancellationRequested || CurrentState is GuiderState.Idle) return;

            // Phase 2: Guided capture; acquire guide star, then run GuideLoop
            var tracker = new GuiderCentroidTracker(maxStars: 1);
            var initFrame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, pollInterval, ct);
            var supersededByInit = _lastLoopFrame;
            LastLoopFrame = initFrame;
            supersededByInit?.Release(); // was silently leaked before: assigned over, never released
            tracker.ProcessFrame(initFrame.GetChannelArray(0));
            tracker.SetLockPosition();

            var pulseTarget = new PulseGuideRouter(PulseGuideSource.Auto, camera, mount);
            var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
            var guideLoop = new GuideLoop(pulseTarget, tracker, pController, TimeProvider);
            // CameraAngle=0 (RA along +x), Dec orthogonal at +90deg, unit rates.
            guideLoop.SetCalibration(new GuiderCalibrationResult(0, Math.PI / 2.0, 1.0, 1.0, 0, 0, 0));
            _guideLoop = guideLoop;

            var declination = await mount.GetDeclinationAsync(ct);
            var ra = await mount.GetRightAscensionAsync(ct);
            var siderealTime = await mount.GetSiderealTimeAsync(ct);
            var hourAngle = siderealTime - ra;
            var siteLatitude = await mount.GetSiteLatitudeAsync(ct);

            // GuideLoop.RunAsync captures frames via the delegate and applies corrections
            await guideLoop.RunAsync(
                async token =>
                {
                    var f = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, pollInterval, token);
                    LastLoopFrame = f; // same ref as GuideLoop.LastFrame; no extra Release needed
                    return f;
                },
                exposureTime, hourAngle, declination, siteLatitude, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "FakeGuider capture loop error");
        }
        finally
        {
            _guideLoop = null;
        }
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        _paused = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> PixelScaleAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(
            _camera is { Connected: true, PixelSizeX: > 0, FocalLength: > 0 }
                ? Astrometry.CoordinateUtils.PixelScaleArcsec(_camera.PixelSizeX, _camera.FocalLength)
                : DefaultPixelScale);

    public async ValueTask<string?> SaveImageAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        // Save the last guide or loop frame if available -- BORROWED, not merely referenced: this
        // method awaits the mount below, and holding a bare reference across that await once wrote a
        // plausible FITS full of the NEXT frame's pixels. Publishers swap the pointer BEFORE
        // releasing the superseded frame, so a failed lease has exactly one meaning: the frame was
        // superseded between our read and the lease. Re-reading then observes the successor, which
        // is live until the capture after it completes -- one retry converges; the extras are
        // insurance against stacked swaps. No frame at all is a real "no", not a race.
        var lease = default(ImageLease);
        var haveLease = false;
        for (var attempt = 0; attempt < 3 && !haveLease; attempt++)
        {
            if ((_guideLoop?.LastFrame ?? _lastLoopFrame) is not { } source)
            {
                return null;
            }

            haveLease = source.TryLease(out lease);
        }

        if (!haveLease)
        {
            return null;
        }

        using (lease)
        {
            Directory.CreateDirectory(outputFolder);
            var path = Path.Combine(outputFolder, $"guider_{TimeProvider.GetUtcNow().UtcDateTime:yyyyMMdd_HHmmss}.fits");

            // Write WCS headers from current mount pointing so FakePlateSolver can read them.
            // FITS WCS is a J2000 quantity: convert from the mount's native (typically JNOW) frame.
            // A plate solve reports the TRUE sky, so prefer the fake mount's hidden-error seam
            // (polar misalignment / drift) over the public believed read; this is what feeds the
            // polar-align routine its misalignment signal. Real mounts only have believed reads.
            WCS? wcs = null;
            if (_mount is { Connected: true } mount)
            {
                var mountJ2000 = mount is IFakeTruePointingSource trueSource
                    ? (await mount.TryGetTransformAsync(cancellationToken) is { } transform
                        ? await trueSource.GetTruePointingJ2000Async(transform, updateTime: false, cancellationToken)
                        : null)
                    : await mount.GetRaDecJ2000Async(cancellationToken);
                var ra = double.IsNaN(PointingRA) ? mountJ2000?.RaJ2000 : PointingRA;
                var dec = double.IsNaN(PointingDec) ? mountJ2000?.DecJ2000 : PointingDec;
                if (ra is { } raJ2000 && dec is { } decJ2000)
                {
                    wcs = new WCS(raJ2000, decJ2000);
                }
            }
            else if (!double.IsNaN(PointingRA) && !double.IsNaN(PointingDec))
            {
                wcs = new WCS(PointingRA, PointingDec);
            }

            lease.Image.WriteToFitsFile(path, wcs);

            return path;
        }
    }

    /// <summary>
    /// Cancels the capture loop and WAITS for it to exit, so the guide camera has exactly one consumer
    /// by the time this returns. <paramref name="timeout"/> and <paramref name="cancellationToken"/> are
    /// deliberately unused -- see the remarks on <see cref="IGuider.StopCaptureAsync"/>: a cancelled
    /// in-process loop unwinds at its next await, so the wait is one guide frame at most.
    /// <para>Returning before that exit was the race behind
    /// <c>DeviceOwnershipTests.AFinishedRunGivesTheRigBack</c>: the session's "stop guiding, slew, start
    /// guiding" at every target began the next loop while this one was still mid-frame, the two released
    /// each other's frames (<c>ChannelBuffer</c>: more releases than refs) and the second one's
    /// <see cref="_guideLoop"/> was nulled by the first one's exit.</para>
    /// </summary>
    public async ValueTask StopCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _loopCts?.Cancel();
        _loopCts = null;
        if (_loopTask is { } loop)
        {
            _loopTask = null;
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Task.Run observed the cancelled token before the loop body started; equally gone.
            }
        }
        ForceState(GuiderState.Idle);
    }

    public ValueTask UnpauseAsync(CancellationToken cancellationToken = default)
    {
        _paused = false;
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

    }
}
