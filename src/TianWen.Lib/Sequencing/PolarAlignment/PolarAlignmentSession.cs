using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Orchestrates the SharpCap-style polar-alignment routine outside of any
    /// imaging session. Phase A: two-frame plate-solve sequence to recover
    /// the mount RA-axis. Phase B: live refinement loop while the user
    /// adjusts polar-aligner knobs. Reverses the original Phase-A rotation
    /// on disposal so the mount is left near its pre-routine pose.
    ///
    /// Not part of <see cref="Sequencing.Session"/> — runs against a
    /// manually-connected mount only. The caller (UI tab) creates one
    /// instance per run, calls <see cref="SolveAsync"/> then
    /// <see cref="RefineAsync"/>, and disposes when done.
    /// </summary>
    /// <remarks>
    /// Driver resilience: hot-path mount and camera operations should still
    /// be wrapped at the call site, but this class does not re-implement the
    /// session's <c>ResilientInvokeAsync</c> wrapper — failures here surface
    /// as <see cref="TwoFrameSolveResult.Success"/> = false with a free-form
    /// reason string. The user can simply restart the routine.
    /// </remarks>
    internal sealed class PolarAlignmentSession : IAsyncDisposable
    {
        private readonly IExternal _external;
        private readonly IMountDriver _mount;
        private readonly ICaptureSource _source;
        private readonly IPlateSolver _solver;
        private readonly ITimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly PolarAlignmentSite _site;
        private readonly PolarAlignmentConfiguration _config;
        private readonly Hemisphere _hemisphere;

        // Phase A artifacts kept for Phase B + reverse-restore.
        private Vec3 _v1;
        private double _lockedExposureSeconds;
        private double _phaseARotationRate;     // deg/s, signed (positive = forward)
        private TimeSpan _phaseARotationElapsed; // wall-clock, used for symmetric reverse
        private bool _phaseACompleted;

        public PolarAlignmentSession(
            IExternal external,
            IMountDriver mount,
            ICaptureSource source,
            IPlateSolver solver,
            ITimeProvider timeProvider,
            ILogger logger,
            PolarAlignmentSite site,
            PolarAlignmentConfiguration config)
        {
            _external = external;
            _mount = mount;
            _source = source;
            _solver = solver;
            _timeProvider = timeProvider;
            _logger = logger;
            _site = site;
            _config = config;
            _hemisphere = site.LatitudeDeg >= 0 ? Hemisphere.North : Hemisphere.South;
        }

        /// <summary>
        /// Phase A: capture frame 1 (with adaptive exposure ramp), rotate the
        /// RA axis by the configured delta, capture frame 2 at the same
        /// exposure, recover the mount axis, and decompose against the
        /// apparent pole.
        /// </summary>
        public async ValueTask<TwoFrameSolveResult> SolveAsync(CancellationToken ct)
        {
            // --- Frame 1 with adaptive exposure ramp ---
            var probe = await AdaptiveExposureRamp.ProbeAsync(_source, _solver, _config.ExposureRamp, _config.MinStarsForSolve, ct);
            if (!probe.Success)
            {
                return Failed($"Plate solve failed at every exposure rung up to {_config.ExposureRamp[^1].TotalSeconds:F1}s — check focus, dew, light pollution.");
            }
            _v1 = probe.WcsCenter;
            _lockedExposureSeconds = probe.ExposureUsed.TotalSeconds;

            // --- RA-axis rotation via raw MoveAxis (bypasses pointing model) ---
            if (!_mount.CanMoveAxis(TelescopeAxis.Primary))
            {
                return Failed("Mount does not support raw RA-axis MoveAxis — required for cone-error-immune pole recovery.");
            }
            double rateDegPerSec = SelectRotationRate(_mount.AxisRates(TelescopeAxis.Primary));
            if (rateDegPerSec <= 0)
            {
                return Failed("No usable axis rate available from mount.");
            }
            double signedRate = _config.RotationDeg >= 0 ? rateDegPerSec : -rateDegPerSec;
            double rotationDurationSec = Math.Abs(_config.RotationDeg) / rateDegPerSec;

            var startTimestamp = _timeProvider.GetTimestamp();
            await _mount.MoveAxisAsync(TelescopeAxis.Primary, signedRate, ct);
            try
            {
                await _timeProvider.SleepAsync(TimeSpan.FromSeconds(rotationDurationSec), ct);
            }
            finally
            {
                await _mount.MoveAxisAsync(TelescopeAxis.Primary, 0.0, CancellationToken.None);
            }
            _phaseARotationRate = signedRate;
            _phaseARotationElapsed = _timeProvider.GetElapsedTime(startTimestamp);

            // --- Settle, then frame 2 at locked exposure with retries ---
            // The mount may not be fully damped after a fast slew, or the user may have
            // bumped the rig adjusting Δ. Retry up to MaxFrame2Retries with a settle wait
            // between each attempt before giving up.
            if (_config.SettleSeconds > 0)
            {
                await _timeProvider.SleepAsync(TimeSpan.FromSeconds(_config.SettleSeconds), ct);
            }
            CaptureAndSolveResult frame2 = default;
            int frame2Attempts = 0;
            int maxFrame2Attempts = Math.Max(1, _config.MaxFrame2Retries + 1);
            while (frame2Attempts < maxFrame2Attempts)
            {
                ct.ThrowIfCancellationRequested();
                frame2 = await _source.CaptureAndSolveAsync(probe.ExposureUsed, _solver, ct);
                if (frame2.Success && frame2.StarsMatched >= _config.MinStarsForSolve) break;
                frame2Attempts++;
                _logger.LogInformation("PolarAlignment: frame 2 attempt {Attempt}/{Max} did not solve, settling and retrying",
                    frame2Attempts, maxFrame2Attempts);
                if (frame2Attempts < maxFrame2Attempts && _config.SettleSeconds > 0)
                {
                    await _timeProvider.SleepAsync(TimeSpan.FromSeconds(_config.SettleSeconds), ct);
                }
            }
            if (!frame2.Success)
            {
                _phaseACompleted = true; // record so reverse-restore still runs
                return Failed($"Frame 2 plate solve failed after {maxFrame2Attempts} attempts at locked exposure {probe.ExposureUsed.TotalMilliseconds:F0}ms — check that the mount has settled and stars are still in frame.");
            }

            // --- Recover axis + decompose against apparent pole ---
            var deltaRad = _phaseARotationRate * _phaseARotationElapsed.TotalSeconds * (Math.PI / 180.0);
            // Use absolute delta for the geometric solve; sign goes into the right-hand-rule
            // axis direction the math returns (which we ignore for pole projection — we only
            // need the line, not the sign).
            if (!PolarAxisSolver.TryRecoverAxis(_v1, frame2.WcsCenter, Math.Abs(deltaRad), out var axis, out var coneRad))
            {
                _phaseACompleted = true;
                return Failed("Axis recovery ill-conditioned — try a larger rotation or different starting position.");
            }

            var (azErr, altErr) = PolarAxisSolver.DecomposeAxisError(
                axis, _hemisphere,
                _site.LatitudeDeg, _site.LongitudeDeg, _site.ElevationM,
                _site.PressureHPa, _site.TemperatureC, _timeProvider.GetUtcNow());

            var observedChord = PolarAxisSolver.ChordAngle(_v1, frame2.WcsCenter);
            var predictedChord = PolarAxisSolver.PredictedChordAngle(Math.Abs(deltaRad), coneRad);

            _phaseACompleted = true;
            return new TwoFrameSolveResult(
                Success: true,
                FailureReason: null,
                AxisJ2000: axis,
                AzErrorRad: azErr,
                AltErrorRad: altErr,
                ChordAngleObservedRad: observedChord,
                ChordAnglePredictedRad: predictedChord,
                LockedExposure: probe.ExposureUsed,
                StarsMatchedFrame1: probe.StarsMatched,
                StarsMatchedFrame2: frame2.StarsMatched);
        }

        /// <summary>
        /// Phase B: stream a fresh axis-error update for each live solve.
        /// Anchors the original Phase A frame (<c>v1</c>); each new live frame
        /// becomes <c>v_live</c> and the recovered axis is recomputed. The
        /// rotation angle stays the same as Phase A (delta), since the user
        /// is told not to rotate the mount in RA during refinement — only the
        /// live frame's apparent direction shifts as polar knobs adjust.
        /// </summary>
        public async IAsyncEnumerable<LiveSolveResult> RefineAsync([EnumeratorCancellation] CancellationToken ct)
        {
            if (!_phaseACompleted)
            {
                throw new InvalidOperationException("Call SolveAsync before RefineAsync.");
            }
            var lockedExposure = TimeSpan.FromSeconds(_lockedExposureSeconds);
            var deltaRad = _phaseARotationRate * _phaseARotationElapsed.TotalSeconds * (Math.PI / 180.0);
            var absDelta = Math.Abs(deltaRad);
            var targetAccuracyRad = _config.TargetAccuracyArcmin * Math.PI / (180.0 * 60.0);

            var smoother = new RefinementSmoother(_config.SmoothingWindow, _config.SettleSigmaArcmin);
            int consecutiveFailures = 0;

            while (!ct.IsCancellationRequested)
            {
                CaptureAndSolveResult solve;
                try
                {
                    solve = await _source.CaptureAndSolveAsync(lockedExposure, _solver, ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                // Tolerance for transient failures: the user may have bumped the rig,
                // a cloud may have rolled in, the star count may have briefly dipped
                // below the threshold during a knob turn. Just count and skip — the
                // smoother carries the previous good value forward, the gauges hold
                // steady, and the GUI surfaces the streak via ConsecutiveFailedSolves.
                if (!solve.Success || solve.StarsMatched < _config.MinStarsForSolve)
                {
                    consecutiveFailures++;
                    continue;
                }

                if (!PolarAxisSolver.TryRecoverAxis(_v1, solve.WcsCenter, absDelta, out var axis, out _))
                {
                    // Live geometry briefly ill-conditioned (knob mid-turn through
                    // an axis-aligned position). Same handling as a solve failure.
                    consecutiveFailures++;
                    continue;
                }
                var (azErr, altErr) = PolarAxisSolver.DecomposeAxisError(
                    axis, _hemisphere,
                    _site.LatitudeDeg, _site.LongitudeDeg, _site.ElevationM,
                    _site.PressureHPa, _site.TemperatureC, _timeProvider.GetUtcNow());

                var (smAz, smAlt, isSettled) = smoother.Update(azErr, altErr);
                bool isAligned = Math.Abs(smAz) < targetAccuracyRad && Math.Abs(smAlt) < targetAccuracyRad;
                int failedRun = consecutiveFailures;
                consecutiveFailures = 0; // reset on success

                yield return new LiveSolveResult(
                    StarsMatched: solve.StarsMatched,
                    ExposureUsed: solve.ExposureUsed,
                    FitsPath: solve.FitsPath,
                    AzErrorRad: azErr,
                    AltErrorRad: altErr,
                    SmoothedAzErrorRad: smAz,
                    SmoothedAltErrorRad: smAlt,
                    IsSettled: isSettled,
                    IsAligned: isAligned,
                    ConsecutiveFailedSolves: failedRun,
                    AxisJ2000: axis,
                    Overlay: null); // overlay pixel projection is the GUI's responsibility
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Reverse-axis the original Phase A rotation if requested. We use the
            // recorded (rate, duration) from SolveAsync rather than a goto, because
            // any pointing model on the mount has been invalidated by the mid-routine
            // raw-axis rotation; a goto would slew through the model and miss.
            if (!_phaseACompleted) return;
            switch (_config.OnDone)
            {
                case PolarAlignmentOnDone.ReverseAxisBack:
                    await ReverseAxisAsync();
                    break;
                case PolarAlignmentOnDone.Park:
                    if (_mount.CanPark)
                    {
                        try { await _mount.ParkAsync(CancellationToken.None); }
                        catch (Exception ex) { _logger.LogWarning(ex, "PolarAlignment: ParkAsync failed during dispose"); }
                    }
                    break;
                case PolarAlignmentOnDone.LeaveInPlace:
                    break;
            }
        }

        private async ValueTask ReverseAxisAsync()
        {
            if (Math.Abs(_phaseARotationRate) < 1e-6 || _phaseARotationElapsed <= TimeSpan.Zero) return;
            try
            {
                await _mount.MoveAxisAsync(TelescopeAxis.Primary, -_phaseARotationRate, CancellationToken.None);
                await _timeProvider.SleepAsync(_phaseARotationElapsed, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PolarAlignment: reverse-axis restore failed");
            }
            finally
            {
                try { await _mount.MoveAxisAsync(TelescopeAxis.Primary, 0.0, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "PolarAlignment: stop-axis after reverse-restore failed"); }
            }
        }

        private TwoFrameSolveResult Failed(string reason)
        {
            _logger.LogWarning("PolarAlignment Phase A failed: {Reason}", reason);
            return new TwoFrameSolveResult(
                Success: false,
                FailureReason: reason,
                AxisJ2000: default,
                AzErrorRad: 0,
                AltErrorRad: 0,
                ChordAngleObservedRad: 0,
                ChordAnglePredictedRad: 0,
                LockedExposure: TimeSpan.Zero,
                StarsMatchedFrame1: 0,
                StarsMatchedFrame2: 0);
        }

        /// <summary>
        /// Pick a sensible RA-axis rate from the mount's <c>AxisRates</c> list.
        /// Strategy:
        /// 1. If the list has &gt;= 2 discrete rates (Min == Max per entry), pick rates[Length - 2]
        ///    (one below max — avoids mechanical wobble at top speed).
        /// 2. If a contiguous range is reported (Max &gt; Min on any entry), pick 0.7 * Max.
        /// 3. If neither, fall back to 8 deg/s (well below any mount that supports MoveAxis).
        /// Returns 0 if the list is empty or the maximum reported rate is non-positive.
        /// </summary>
        internal static double SelectRotationRate(IReadOnlyList<AxisRate> rates)
        {
            if (rates is null || rates.Count == 0) return 8.0;

            // Detect whether the list is purely discrete (every entry has Min == Max).
            bool allDiscrete = true;
            double maxRate = 0;
            foreach (var r in rates)
            {
                if (Math.Abs(r.Maximum - r.Mininum) > 1e-9) allDiscrete = false;
                if (r.Maximum > maxRate) maxRate = r.Maximum;
            }
            if (maxRate <= 0) return 0;

            if (allDiscrete && rates.Count >= 2)
            {
                // Sort ascending by Maximum and pick second-from-top.
                var sorted = new List<double>(rates.Count);
                foreach (var r in rates) sorted.Add(r.Maximum);
                sorted.Sort();
                return sorted[sorted.Count - 2];
            }

            // Continuous range or single discrete entry -> 70% of max.
            return Math.Min(maxRate, 0.7 * maxRate);
        }
    }

    /// <summary>
    /// Site context for the polar-alignment routine: lat/lon/elev plus
    /// pressure and temperature for refraction-aware pole projection.
    /// Sourced (in priority) from a connected weather device, the active
    /// profile, or standard-atmosphere fallback.
    /// </summary>
    /// <param name="LatitudeDeg">Site latitude in degrees, +north.</param>
    /// <param name="LongitudeDeg">Site longitude in degrees, +east.</param>
    /// <param name="ElevationM">Site elevation in metres above MSL.</param>
    /// <param name="PressureHPa">Atmospheric pressure at the site, hPa.</param>
    /// <param name="TemperatureC">Air temperature at the site, Celsius.</param>
    public readonly record struct PolarAlignmentSite(
        double LatitudeDeg,
        double LongitudeDeg,
        double ElevationM,
        double PressureHPa,
        double TemperatureC);
}
