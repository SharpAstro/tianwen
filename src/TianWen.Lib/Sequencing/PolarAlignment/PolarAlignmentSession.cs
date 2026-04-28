using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <param name="ct">Cancellation token.</param>
        /// <param name="rampProgress">Optional reporter for ramp progress —
        /// the GUI / TUI subscribes so the side panel can show "Probing 200ms
        /// (rung 3/8)" instead of a static message during the multi-second
        /// ASTAP retries.</param>
        /// <param name="phaseProgress">Optional reporter for Phase A
        /// sub-phase transitions (rotation, settle, frame 2). Without this,
        /// the UI status panel sits on the last probe-ramp message for the
        /// 15-30 seconds of rotation + settle + frame-2 capture and looks
        /// hung even though the orchestrator is making forward progress.</param>
        public async ValueTask<TwoFrameSolveResult> SolveAsync(
            CancellationToken ct,
            IProgress<ProbeProgress>? rampProgress = null,
            IProgress<PolarPhaseUpdate>? phaseProgress = null)
        {
            // --- Frame 1 with adaptive exposure ramp ---
            var probe = await AdaptiveExposureRamp.ProbeAsync(_source, _solver, _config.ExposureRamp, _config.MinStarsForSolve, ct, rampProgress, _logger);
            if (!probe.Success)
            {
                // Source-supplied reason (e.g. "PHD2 Save Images disabled") wins over the
                // generic "no solve at any rung" message — the user gets actionable text.
                return Failed(probe.FailureReason
                    ?? $"Plate solve failed at every exposure rung up to {_config.ExposureRamp[^1].TotalSeconds:F1}s — check focus, dew, light pollution.");
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

            // Initial Rotating report carries total angle + rate so the UI can
            // show "Rotating 60deg at 4.0deg/s (~15s)" instead of an opaque
            // status line.
            phaseProgress?.Report(new PolarPhaseUpdate(
                PolarAlignmentPhase.Rotating,
                $"Rotating {_config.RotationDeg:F0}\u00B0 at {rateDegPerSec:F1}\u00B0/s (~{rotationDurationSec:F0}s)"));

            var startTimestamp = _timeProvider.GetTimestamp();
            await _mount.MoveAxisAsync(TelescopeAxis.Primary, signedRate, ct);
            try
            {
                // Wait the full rotation through SleepAsync so the call advances
                // fake time in tests. An earlier "capture-during-rotation" loop
                // here gave a SharpCap-style live preview while the mount slewed,
                // but it spun infinitely under FakeTimeProvider (synthetic
                // captures return instantly, and time never advances on its own).
                // With the SelectRotationRate fix the whole rotation is ~15s on
                // a Skywatcher, so the lost UX is small. Re-add a decoupled
                // background capture loop later if it matters.
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
                phaseProgress?.Report(new PolarPhaseUpdate(
                    PolarAlignmentPhase.Rotating,
                    $"Settling {_config.SettleSeconds:F0}s..."));
                await _timeProvider.SleepAsync(TimeSpan.FromSeconds(_config.SettleSeconds), ct);
            }
            phaseProgress?.Report(new PolarPhaseUpdate(PolarAlignmentPhase.Frame2));
            CaptureAndSolveResult frame2 = default;
            int frame2Attempts = 0;
            int maxFrame2Attempts = Math.Max(1, _config.MaxFrame2Retries + 1);
            while (frame2Attempts < maxFrame2Attempts)
            {
                ct.ThrowIfCancellationRequested();
                frame2 = await _source.CaptureAndSolveAsync(probe.ExposureUsed, _solver, ct: ct);
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
                return Failed(frame2.FailureReason
                    ?? $"Frame 2 plate solve failed after {maxFrame2Attempts} attempts at locked exposure {probe.ExposureUsed.TotalMilliseconds:F0}ms — check that the mount has settled and stars are still in frame.");
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

            // Incremental solver: ROI-centroid + affine refit at <20ms per
            // frame vs ~700ms for a full hinted solve on a 60MP frame. First
            // iteration runs the full solve and seeds; subsequent iterations
            // try Refine() first and fall back to the full solve on null
            // (residual spike, too few anchors). The full-solve fallback
            // re-seeds so the fast path resumes on the next tick.
            var incremental = new IncrementalSolver(_logger);

            // Periodic re-seed: every Nth successful refine forces a full
            // hinted solve (instead of the fast path) to refresh the anchor
            // list against drift / changing exposure conditions and reset any
            // accumulated affine-fit floating-point error. Counter ticks only
            // on successful fast-path refines; fallback solves already act as
            // implicit re-seeds and reset the counter to 0 on their own.
            int fastRefinesSinceFullSolve = 0;
            int fullSolveInterval = Math.Max(0, _config.RefineFullSolveInterval);

            while (!ct.IsCancellationRequested)
            {
                // Per-iteration stage timings -- logged at end of iteration so we can see
                // whether sluggishness is in capture (exposure wall-time + render + transfer),
                // fast incremental refine (~ms), full plate solve (catalog match / external
                // process, can be hundreds of ms to seconds), or post-fallback re-seed.
                var iterStart = Stopwatch.GetTimestamp();
                TimeSpan captureElapsed = TimeSpan.Zero;
                TimeSpan fastElapsed = TimeSpan.Zero;
                TimeSpan fullElapsed = TimeSpan.Zero;
                TimeSpan seedElapsed = TimeSpan.Zero;

                CaptureResult capture;
                try
                {
                    var captureStart = Stopwatch.GetTimestamp();
                    capture = await _source.CaptureAsync(lockedExposure, ct);
                    captureElapsed = Stopwatch.GetElapsedTime(captureStart);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (!capture.Success || capture.Image is not { } image)
                {
                    consecutiveFailures++;
                    _logger.LogInformation(
                        "PolarAlignment refine iter: capture={CaptureMs:F0}ms total={TotalMs:F0}ms outcome=capture-failed",
                        captureElapsed.TotalMilliseconds, Stopwatch.GetElapsedTime(iterStart).TotalMilliseconds);
                    continue;
                }

                LiveSolveResult? yieldResult = null;
                string outcome = "no-solve";
                try
                {
                    WCS? refinedWcs = null;
                    int starsMatched = 0;
                    bool fastPath = false;

                    // Fast path: ROI centroid + affine refit. Returns null on
                    // residual spike (knob nudge too large, anchor list lost)
                    // or insufficient surviving anchors -- caller falls
                    // through to the full solve. Skipped when the periodic
                    // full-solve interval is hit so the next branch can
                    // refresh the anchor list against drift.
                    bool periodicReseed = fullSolveInterval > 0 && fastRefinesSinceFullSolve >= fullSolveInterval;
                    if (_config.UseIncrementalSolver && incremental.IsSeeded && !periodicReseed)
                    {
                        var fastStart = Stopwatch.GetTimestamp();
                        var fast = incremental.Refine(image, ct);
                        fastElapsed = Stopwatch.GetElapsedTime(fastStart);
                        if (fast is { Solution: { } w } fr && fr.MatchedStars >= _config.MinStarsForSolve)
                        {
                            refinedWcs = w;
                            starsMatched = fr.MatchedStars;
                            fastPath = true;
                        }
                    }

                    // Fallback: full hinted solve. Runs on the cold start, on
                    // any frame where Refine returned null, and on the
                    // periodic-reseed tick. Seeds afterwards so the next
                    // tick goes through the fast path; resets the counter.
                    if (refinedWcs is null)
                    {
                        try
                        {
                            var fullStart = Stopwatch.GetTimestamp();
                            var full = await _solver.SolveImageAsync(image,
                                searchOrigin: capture.SearchOrigin,
                                cancellationToken: ct);
                            fullElapsed = Stopwatch.GetElapsedTime(fullStart);
                            if (full.Solution is { } fullWcs && full.MatchedStars >= _config.MinStarsForSolve)
                            {
                                refinedWcs = fullWcs;
                                starsMatched = full.MatchedStars;
                                // Re-seed so the next iteration goes through
                                // the fast path. Seed failure (too few stars)
                                // leaves IsSeeded false; we'll just keep
                                // running full solves until conditions
                                // improve. Skip seeding when the user has
                                // disabled the incremental path -- avoids the
                                // SeedAsync star-detect cost when we'd never
                                // use the result.
                                if (_config.UseIncrementalSolver)
                                {
                                    var seedStart = Stopwatch.GetTimestamp();
                                    _ = await incremental.SeedAsync(image, fullWcs, ct);
                                    seedElapsed = Stopwatch.GetElapsedTime(seedStart);
                                }
                                fastRefinesSinceFullSolve = 0;
                            }
                        }
                        catch (PlateSolverException ex)
                        {
                            _logger.LogDebug(ex, "PolarAlignment refining: full solve threw");
                        }
                        catch (OperationCanceledException)
                        {
                            yield break;
                        }
                    }
                    else if (fastPath)
                    {
                        // Successful fast-path refine: tick the counter so the
                        // next periodic re-seed eventually fires.
                        fastRefinesSinceFullSolve++;
                    }

                    if (refinedWcs is not { } wcs)
                    {
                        consecutiveFailures++;
                        outcome = "no-solve";
                        continue;
                    }
                    outcome = fastPath ? "fast" : "full";

                    var wcsCenter = PolarAxisSolver.RaDecToUnitVec(wcs.CenterRA, wcs.CenterDec);
                    if (!PolarAxisSolver.TryRecoverAxis(_v1, wcsCenter, absDelta, out var axis, out _))
                    {
                        // Live geometry briefly ill-conditioned (knob mid-turn through
                        // an axis-aligned position). Same handling as a solve failure.
                        consecutiveFailures++;
                        outcome = "axis-recover-failed";
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

                    // Populate the overlay so the GUI can render pole crosses, rings, and
                    // axis marker via the generic WcsAnnotationLayer without re-doing any
                    // SOFA math. Refraction-corrected pole position is *not* yet recovered
                    // here (would require inverse SOFA topo->J2000); v1 reuses the true
                    // pole as the ring centre, which superimposes the two crosses but
                    // keeps the gauges (which use the refraction-aware az/alt errors)
                    // numerically correct. Phase 4 polish.
                    double trueRa = 0.0;
                    double trueDec = _hemisphere == Hemisphere.North ? 90.0 : -90.0;
                    var (axisRa, axisDec) = PolarAxisSolver.UnitVecToRaDec(axis);
                    var azArcmin = azErr * 180.0 / Math.PI * 60.0;
                    var altArcmin = altErr * 180.0 / Math.PI * 60.0;
                    var overlay = new PolarOverlay(
                        TruePoleRaHours: trueRa,
                        TruePoleDecDeg: trueDec,
                        RefractedPoleRaHours: trueRa,
                        RefractedPoleDecDeg: trueDec,
                        AxisRaHours: axisRa,
                        AxisDecDeg: axisDec,
                        RingRadiiArcmin: System.Collections.Immutable.ImmutableArray.Create(5f, 15f, 30f),
                        AzErrorArcmin: azArcmin,
                        AltErrorArcmin: altArcmin,
                        Hemisphere: _hemisphere);

                    yieldResult = new LiveSolveResult(
                        StarsMatched: starsMatched,
                        ExposureUsed: capture.ExposureUsed,
                        FitsPath: capture.FitsPath,
                        AzErrorRad: azErr,
                        AltErrorRad: altErr,
                        SmoothedAzErrorRad: smAz,
                        SmoothedAltErrorRad: smAlt,
                        IsSettled: isSettled,
                        IsAligned: isAligned,
                        ConsecutiveFailedSolves: failedRun,
                        AxisJ2000: axis,
                        Overlay: overlay);
                    _ = fastPath; // surface in telemetry/diagnostics later if needed
                }
                finally
                {
                    if (!capture.OwnershipTransferredToUi)
                    {
                        image.Release();
                    }

                    // Per-iteration timing log -- runs whether we yielded a result, hit
                    // a continue path (no-solve / axis-recover-failed), or threw. Use
                    // Information level so it shows up in SEQ without enabling Debug.
                    _logger.LogInformation(
                        "PolarAlignment refine iter: capture={CaptureMs:F0}ms fast={FastMs:F1}ms full={FullMs:F0}ms seed={SeedMs:F0}ms total={TotalMs:F0}ms outcome={Outcome}",
                        captureElapsed.TotalMilliseconds,
                        fastElapsed.TotalMilliseconds,
                        fullElapsed.TotalMilliseconds,
                        seedElapsed.TotalMilliseconds,
                        Stopwatch.GetElapsedTime(iterStart).TotalMilliseconds,
                        outcome);
                }

                if (yieldResult is { } r)
                {
                    yield return r;
                }
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
        /// Strategy: take the highest rate <= 5 deg/s (a generous cap that any
        /// real GEM tolerates without wobble), otherwise 0.7 * max. Polar
        /// alignment wants the rotation finished quickly -- the previous
        /// "second-from-top discrete rate" heuristic locked Skywatcher to its
        /// 32x-sidereal entry (0.13 deg/s), turning a 45 deg rotation into a
        /// 5 minute wait when the mount actually supports 3 deg/s slews.
        /// </summary>
        internal static double SelectRotationRate(IReadOnlyList<AxisRate> rates)
        {
            const double SafeCapDegPerSec = 5.0;
            if (rates is null || rates.Count == 0) return 3.0;

            double maxBelowCap = 0;
            double maxRate = 0;
            foreach (var r in rates)
            {
                if (r.Maximum > maxRate) maxRate = r.Maximum;
                if (r.Maximum <= SafeCapDegPerSec && r.Maximum > maxBelowCap)
                {
                    maxBelowCap = r.Maximum;
                }
            }
            if (maxRate <= 0) return 0;
            if (maxBelowCap > 0) return maxBelowCap;
            return Math.Min(SafeCapDegPerSec, 0.7 * maxRate);
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
