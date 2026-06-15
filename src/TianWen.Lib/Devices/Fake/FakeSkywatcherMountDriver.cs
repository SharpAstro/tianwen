using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices.Fake.Disturbance;
using TianWen.Lib.Devices.Fake.Disturbance.Terms;
using TianWen.Lib.Devices.Skywatcher;

namespace TianWen.Lib.Devices.Fake;

internal class FakeSkywatcherMountDriver(FakeDevice device, IServiceProvider serviceProvider) : SkywatcherMountDriverBase<FakeDevice>(device, serviceProvider), IFakeTruePointingSource
{
    /// <summary>
    /// Current azimuth misalignment in arcminutes, in the topocentric (horizon)
    /// frame at the site's latitude / longitude. Initialised from the
    /// <c>polarMisalignmentAzArcmin</c> URI query key, then mutable via
    /// <see cref="NudgeMisalignment"/> so the live polar-align panel can
    /// simulate twisting az/alt knobs during refining. The polar-alignment
    /// routine's <see cref="PolarAxisSolver.DecomposeAxisError"/> reports
    /// az/alt errors in this same frame, so the configured value round-trips
    /// back through the solver -- "I dialled in 30', the routine reads 30'".
    /// </summary>
    private double _azErrArcmin = ParseDoubleQuery(device, DeviceQueryKey.PolarMisalignmentAzArcmin.Key, defaultValue: 30.0);

    /// <summary>
    /// Current altitude misalignment in arcminutes, topocentric. Sign convention
    /// matches <see cref="PolarAxisSolver.DecomposeAxisError"/>: positive = axis
    /// above the apparent pole. See <see cref="_azErrArcmin"/> for mutability
    /// rationale.
    /// </summary>
    private double _altErrArcmin = ParseDoubleQuery(device, DeviceQueryKey.PolarMisalignmentAltArcmin.Key, defaultValue: -10.0);

    // Deterministic RNG for nudge jitter -- seeded so a given URI replay
    // produces the same trajectory across runs. Real polar-align knobs have
    // backlash + finger feel that means a "1' adjustment" is rarely exactly 1';
    // we model that, AND it conveniently keeps the user from landing exactly on
    // (0, 0). At the singularity MisalignmentEnabled flips false and the base
    // SkyWatcher driver returns the perfect-pole pointing, where RA atan2 is
    // ill-conditioned and the catalog plate solver loses lock.
    private readonly Random _nudgeJitter = new(12345);

    /// <summary>
    /// Apply a delta to the configured topocentric (az, alt) misalignment in
    /// arcminutes. Used by the polar-align refining UI to simulate the user
    /// twisting alt/az knobs while the routine watches the field drift across
    /// the pole. The next camera frame picks up the new values via
    /// <see cref="GetTruePointingNativeAsync"/>'s per-call SOFA recompute -- no
    /// driver reconnect needed. (Public position reads report the believed
    /// pointing and deliberately never observe the nudge.)
    /// </summary>
    /// <remarks>
    /// Each delta is multiplied by a small random factor in [0.85, 1.05]:
    /// requesting 1' typically applies ~0.95'. Models real knob backlash /
    /// finger feel and avoids the (0, 0) pole singularity that would
    /// otherwise stall plate-solving when the user drags the slider exactly
    /// to zero. With per-axis independent jitter the chance of both coords
    /// landing within MisalignmentEnabled's 1e-3 threshold is effectively
    /// zero so the live tracker never flips off the misaligned-pointing
    /// path mid-refine.
    /// </remarks>
    internal void NudgeMisalignment(double dAzArcmin, double dAltArcmin)
    {
        _azErrArcmin += dAzArcmin * NudgeFactor();
        _altErrArcmin += dAltArcmin * NudgeFactor();
    }

    private double NudgeFactor() => 0.85 + _nudgeJitter.NextDouble() * 0.20;

    /// <summary>Current (az, alt) misalignment in arcminutes -- read-only snapshot.</summary>
    internal (double AzArcmin, double AltArcmin) CurrentMisalignment => (_azErrArcmin, _altErrArcmin);

    /// <summary>
    /// True when the configured (az, alt) misalignment is large enough to model;
    /// otherwise the override no-ops and the driver behaves as a perfectly
    /// polar-aligned mount. Threshold is the smallest value the polar-alignment
    /// routine could meaningfully resolve.
    /// </summary>
    private bool MisalignmentEnabled => Math.Abs(_azErrArcmin) > 1e-3 || Math.Abs(_altErrArcmin) > 1e-3;

    /// <summary>
    /// Set once a plate-solve-driven <see cref="SyncRaDecAsync"/> to a target
    /// away from the pole lands. Models the mount LEARNING its true orientation
    /// from the sync: after that the residual polar misalignment is corrected,
    /// so <see cref="GetRightAscensionAsync"/> / <see cref="GetDeclinationAsync"/>
    /// report the believed (encoder) pointing verbatim and imaging frames render
    /// on-target. This is how the imaging centering loop converges -- the first
    /// frame shows the misalignment offset, plate-solve syncs, and the re-slew
    /// lands true. Startup / park syncs (to the pole) deliberately do NOT set
    /// this, so the polar-align simulation survives until a real imaging sync.
    /// </summary>
    private bool _alignmentCorrected;

    /// <summary>Test seam: whether a plate-solve sync away from the pole has
    /// modelled the alignment as learned (residual misalignment corrected).</summary>
    internal bool IsAlignmentCorrected => _alignmentCorrected;

    /// <summary>
    /// UTC of the last plate-solve sync away from the pole -- the reference from
    /// which the post-centering tracking drift accumulates. A sync removes the
    /// static pointing offset but NOT the polar-axis tilt, so the field keeps
    /// drifting (mostly in Dec) as the mount tracks about the wrong axis. Reset
    /// on every away-from-pole sync so the drift always grows from the most
    /// recently centred position -- exactly the residual a real misaligned mount
    /// leaves for the guider to chase. <see cref="ApplyTrackingDrift"/> consumes it.
    /// </summary>
    private DateTimeOffset _trackingRefUtc;

    /// <summary>
    /// Believed (commanded) RA/Dec captured at the last away-from-pole sync -- the
    /// fixed anchor the post-centering drift accumulates from. Anchoring to the
    /// synced position (rather than the live <c>base</c> RA) keeps the drift purely
    /// a function of elapsed tracking time: the base driver derives RA as
    /// <c>LST - HA(steps)</c>, which advances with sidereal time even on a
    /// stationary encoder, so using it live would swamp the misalignment signal.
    /// </summary>
    private double _trackingRefRa;
    private double _trackingRefDec;

    /// <summary>
    /// Believed (encoder-derived) RA/Dec at the same reference moment. Under perfect
    /// tracking the believed pointing is CONSTANT, so any deviation from this value at
    /// read time is exactly the commanded axis motion since the reference — guide
    /// pulses, MoveAxis nudges, the in-flight portion of a GOTO. The post-centering
    /// branch adds that deviation to the drifted pointing 1:1: without it, pulse
    /// guiding moved the encoders but was INVISIBLE in pointing reads (the guider
    /// measured ~0 displacement per pulse and calibration was rejected for
    /// insufficient displacement). After a sync / completed GOTO the encoders match
    /// the commanded position, so the commanded RA/Dec doubles as the believed anchor.
    /// </summary>
    private double _trackingRefBaseRa;
    private double _trackingRefBaseDec;

    /// <summary>
    /// True while a post-centering GOTO is in flight and the believed-pointing anchor
    /// has not been captured yet. The fake goto computes its target steps from the HA
    /// at COMMAND time, so the believed RA on arrival is the target plus the slew
    /// duration of sidereal motion — anchoring at the commanded target would leave a
    /// permanent slew-duration error in every read (~9' for a long slew). Instead the
    /// first true-pointing read (camera frame) after the slew completes captures the
    /// believed pointing as the anchor (and restarts the drift clock at arrival).
    /// </summary>
    private bool _trackingRefBasePending;

    /// <summary>
    /// Angular distance from the site's celestial pole, in degrees, within which
    /// the OTA is treated as "parked on the pole" for polar-align simulation
    /// (encoder-swept pole) rather than slewed to an imaging target (axis-tilt of
    /// the believed pointing). Polar align operates with the believed Dec within
    /// a few degrees of the pole; imaging targets are well below this.
    /// </summary>
    private const double NearPoleDeg = 5.0;

    // ---- Additive disturbance perturbations -------------------------------------------------
    // Layered on top of the believed->true polar transform via the shared DisturbanceModel. All
    // default OFF, so a mount without these knobs behaves exactly as before -- the coupling and
    // polar-align suites are unaffected; the test harness opts in by setting them. Periodic worm
    // error is a MOUNT term here (moved off the fake camera) so it lives in one place and the
    // guide camera observes it through the true-pointing read.

    /// <summary>Worm periodic-error peak-to-peak amplitude in arcsec (0 = off).</summary>
    internal double PePeakTopeakArcsec { get; set; }

    /// <summary>RA worm period in seconds.</summary>
    internal double PePeriodSeconds { get; set; } = 600.0;

    /// <summary>Differential flexure: Dec drift in arcsec per hour of hour-angle tracked (0 = off).</summary>
    internal double FlexureDriftRateDecArcsecPerHaHour { get; set; }

    /// <summary>Wind-gust stationary amplitude in arcsec (0 = off).</summary>
    internal double WindGustAmplitudeArcsec { get; set; }

    /// <summary>Wind-gust correlation (decay) time in seconds.</summary>
    internal double WindGustDecayTimeSeconds { get; set; } = 8.0;

    /// <summary>Gear-noise stationary amplitude in arcsec (0 = off).</summary>
    internal double GearNoiseArcsec { get; set; }

    /// <summary>Gear-noise correlation (decay) time in seconds.</summary>
    internal double GearNoiseDecayTimeSeconds { get; set; } = 0.5;

    /// <summary>Cable-snag trigger time in seconds since the disturbance epoch (0 = off).</summary>
    internal double CableSnagTimeSeconds { get; set; }

    /// <summary>Cable-snag RA step in arcsec, applied once at <see cref="CableSnagTimeSeconds"/>.</summary>
    internal double CableSnagAmplitudeRaArcsec { get; set; }

    /// <summary>Cable-snag Dec step in arcsec.</summary>
    internal double CableSnagAmplitudeDecArcsec { get; set; }

    private const double ArcsecPerRaHour = 3600.0 * 15.0; // RA-coordinate arcsec per hour
    private const double ArcsecPerDegree = 3600.0;

    // Built lazily on the first true-pointing read (after the knobs above are set), then cached so
    // the stochastic terms (wind / gear) keep their state across frames.
    private DisturbanceModel? _disturbances;
    private DateTimeOffset? _disturbanceEpoch;

    /// <inheritdoc/>
    /// <remarks>
    /// The TRUE pointing applies a topocentric polar-misalignment transform on top
    /// of the base driver's believed (encoder) pointing so the synthesised
    /// fake-camera frames trace a small circle around an offset axis as the RA
    /// encoder rotates. The misaligned axis is constructed in the same topocentric
    /// frame <see cref="PolarAxisSolver.DecomposeAxisError"/> projects into, so the
    /// configured arcmin values round-trip cleanly through the polar-align
    /// routine -- "I dialled in 30', the routine reads 30'".
    ///
    /// The public <see cref="SkywatcherMountDriverBase{TDevice}.GetRightAscensionAsync"/> /
    /// GetDeclinationAsync reads deliberately stay on the base (believed) pointing:
    /// a real mount's encoders cannot observe their own polar misalignment, so the
    /// hidden error must only be visible through the camera (plate solving).
    /// Note: a learned alignment (_alignmentCorrected) does not short-circuit
    /// here -- it removes the STATIC offset but the residual polar-axis tracking
    /// drift is still applied inside ComputeMisalignedPointingAsync.
    /// </remarks>
    public async ValueTask<(double Ra, double Dec)> GetTruePointingNativeAsync(CancellationToken cancellationToken)
    {
        var baseRa = await base.GetRightAscensionAsync(cancellationToken);
        var baseDec = await base.GetDeclinationAsync(cancellationToken);
        var (trueRa, trueDec) = !MisalignmentEnabled || CprRa == 0
            ? (baseRa, baseDec)
            : await ComputeMisalignedPointingAsync(baseRa, baseDec, cancellationToken);
        // Layer the additive perturbations (PE, flexure, wind, gear, cable snag) on top of the
        // believed->true polar transform. Inert until a term is configured, so the default mount
        // (and a perfectly polar-aligned one) is unaffected.
        return ApplyDisturbances(trueRa, trueDec);
    }

    /// <summary>
    /// Adds the configured additive disturbance perturbations to a pointing that already carries
    /// the believed-&gt;true polar transform. Returns the input unchanged when no term is active
    /// (the common case), so the cost and the behaviour are both zero for an unconfigured mount.
    /// </summary>
    private (double Ra, double Dec) ApplyDisturbances(double raHours, double decDeg)
    {
        _disturbances ??= BuildDisturbanceModel();
        var epoch = _disturbanceEpoch ??= TimeProvider.GetUtcNow();
        var elapsedSeconds = (TimeProvider.GetUtcNow() - epoch).TotalSeconds;

        var (dRaArcsec, dDecArcsec) = _disturbances.PointingDelta(
            new DisturbanceContext(elapsedSeconds, RaWormPhaseRadians()));
        if (dRaArcsec == 0.0 && dDecArcsec == 0.0)
        {
            return (raHours, decDeg);
        }

        var raOut = (raHours + dRaArcsec / ArcsecPerRaHour) % 24.0;
        if (raOut < 0.0) raOut += 24.0;
        var decOut = Math.Clamp(decDeg + dDecArcsec / ArcsecPerDegree, -90.0, 90.0);
        return (raOut, decOut);
    }

    private DisturbanceModel BuildDisturbanceModel() => new(new IDisturbanceTerm[]
    {
        new PeriodicErrorTerm(PePeakTopeakArcsec, PePeriodSeconds),
        new FlexureTerm(FlexureDriftRateDecArcsecPerHaHour),
        new CableSnagTerm(CableSnagTimeSeconds, CableSnagAmplitudeRaArcsec, CableSnagAmplitudeDecArcsec),
        new WindGustTerm(WindGustAmplitudeArcsec, WindGustDecayTimeSeconds),
        new GearNoiseTerm(GearNoiseArcsec, GearNoiseDecayTimeSeconds),
    });

    /// <summary>
    /// RA worm phase in radians for the positional periodic-error term, or <see cref="double.NaN"/>
    /// when the worm period is unavailable (the PE term then falls back to a wall-clock sine).
    /// Wired to the RA encoder when periodic error moves onto the mount (Phase 2b); NaN today, which
    /// is harmless while <see cref="PePeakTopeakArcsec"/> defaults to 0.
    /// </summary>
    private double RaWormPhaseRadians() => double.NaN;

    /// <inheritdoc/>
    /// <remarks>
    /// A sync to a target away from the pole is plate-solve-driven (the imaging
    /// centering loop tells the mount its TRUE sky position). We model that as
    /// the mount LEARNING its orientation: the residual polar misalignment is
    /// corrected, so subsequent <see cref="GetRightAscensionAsync"/> /
    /// <see cref="GetDeclinationAsync"/> report the believed (encoder) pointing
    /// verbatim and the re-slew converges on target. Startup / park syncs go to
    /// the pole and must NOT trigger this -- otherwise the polar-align
    /// simulation would be zeroed before it can run. The base call still moves
    /// the encoders either way.
    /// </remarks>
    public override async ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        await base.SyncRaDecAsync(ra, dec, cancellationToken);
        if (!MisalignmentEnabled)
        {
            return;
        }
        var siteLatDeg = await GetSiteLatitudeAsync(cancellationToken);
        var hemisphere = siteLatDeg >= 0 ? Hemisphere.North : Hemisphere.South;
        if (IsNearSitePole(dec, hemisphere))
        {
            // Startup / park syncs go to the pole and must NOT establish the
            // imaging tracking-drift reference -- the polar-align simulation
            // (the encoder-swept regime) owns the near-pole behaviour, and
            // zeroing it here would kill the polar-align routine before it runs.
            return;
        }
        // Plate-solve sync away from the pole. The static pointing offset is now
        // learned out (so the centering loop converges), but the polar axis is
        // STILL tilted: reset the drift reference so the field starts drifting
        // afresh from this freshly-centred position. That residual drift is what
        // the guider then chases -- exactly like a real polar-misaligned mount,
        // where plate-solve centering fixes pointing but not polar alignment.
        if (!_alignmentCorrected)
        {
            _alignmentCorrected = true;
            Logger.LogInformation(
                "FakeSkywatcher: plate-solve sync to ({Ra:F4}h, {Dec:F4}deg) away from the pole -- static pointing offset learned; residual polar-axis tracking drift retained.",
                ra, dec);
        }
        _trackingRefRa = ra;
        _trackingRefDec = dec;
        // The sync just wrote the encoders to match (ra, dec), so the believed
        // pointing anchor is the commanded position itself.
        _trackingRefBaseRa = ra;
        _trackingRefBaseDec = dec;
        _trackingRefBasePending = false;
        _trackingRefUtc = TimeProvider.GetUtcNow();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Once the alignment has been LEARNED (<see cref="_alignmentCorrected"/>), a GOTO to a
    /// new imaging target must re-baseline the tracking-drift reference. Post-centering,
    /// <see cref="GetRightAscensionAsync"/>/<see cref="GetDeclinationAsync"/> report
    /// <see cref="ApplyTrackingDrift"/> anchored to <see cref="_trackingRefRa"/>/
    /// <see cref="_trackingRefDec"/> -- deliberately frozen to the last sync (decoupled from
    /// the live encoder so sidereal LST jitter doesn't swamp the misalignment signal). Without
    /// this override, that frozen anchor makes a subsequent slew a no-op in the REPORTED
    /// position: the encoder moves to the new target but GetRA/GetDec keep returning the old
    /// synced position, so the imaging centering loop never converges. A real mount slews
    /// accurately once pointing is learned; the residual polar-axis drift then re-accumulates
    /// from the freshly-commanded target -- which is exactly what re-baselining models.
    /// Pre-alignment slews need no special handling: <see cref="ApplyAxisTiltToPointing"/>
    /// already tracks the live encoder.
    /// </remarks>
    public override async ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        await base.BeginSlewRaDecAsync(ra, dec, cancellationToken);
        if (!MisalignmentEnabled || !_alignmentCorrected)
        {
            return;
        }
        var siteLatDeg = await GetSiteLatitudeAsync(cancellationToken);
        var hemisphere = siteLatDeg >= 0 ? Hemisphere.North : Hemisphere.South;
        if (IsNearSitePole(dec, hemisphere))
        {
            // Parking / polar-align slews to the pole do not establish an imaging reference.
            return;
        }
        _trackingRefRa = ra;
        _trackingRefDec = dec;
        // The believed-pointing anchor cannot be captured here: the goto's target steps
        // encode the HA at command time, so the believed RA at ARRIVAL differs from the
        // commanded target by the slew duration of sidereal motion. Defer the capture to
        // the first pointing read after the slew completes (see _trackingRefBasePending).
        _trackingRefBasePending = true;
        _trackingRefUtc = TimeProvider.GetUtcNow();
        Logger.LogInformation(
            "FakeSkywatcher: GOTO ({Ra:F4}h, {Dec:F4}deg) re-baselined the tracking-drift reference (alignment already learned -- the slew lands on target and the residual polar drift restarts from here).",
            ra, dec);
    }

    /// <summary>
    /// Read site parameters, build the misaligned axis in J2000, and project
    /// the perfectly-aligned home pointing through the encoder angle around
    /// that axis. The single entry point is <see cref="GetTruePointingNativeAsync"/>;
    /// public position reads never call this (they report the believed pointing).
    /// </summary>
    private async ValueTask<(double Ra, double Dec)> ComputeMisalignedPointingAsync(
        double baseRa, double baseDec, CancellationToken ct)
    {
        var siteLatDeg = await GetSiteLatitudeAsync(ct);
        var siteLonDeg = await GetSiteLongitudeAsync(ct);
        var siteElevM = await GetSiteElevationAsync(ct);
        // Which celestial pole the mount homes to is fixed by the observing
        // hemisphere (site latitude), NOT by where the OTA currently points.
        var hemisphere = siteLatDeg >= 0 ? Hemisphere.North : Hemisphere.South;
        var utc = TimeProvider.GetUtcNow();
        var axis = TopocentricMisalignmentToJ2000Axis(
            siteLatDeg, siteLonDeg, siteElevM, utc,
            _azErrArcmin, _altErrArcmin, hemisphere, TimeProvider);

        if (IsNearSitePole(baseDec, hemisphere))
        {
            // Polar-align regime: the OTA is parked on the pole and the RA axis
            // is rotated via MoveAxis to trace a small circle about the
            // misaligned axis. The believed RA is degenerate at the pole, so we
            // sweep the pole vector by the raw encoder angle (the polar-align
            // routine recovers the circle centre = the misaligned axis).
            var encoderRad = EncoderAngleRadians(PosRa, CprRa);
            return ApplyPolarMisalignment(axis, hemisphere, encoderRad);
        }

        if (_alignmentCorrected)
        {
            // Post-centering imaging regime. The static cone-error offset has been
            // synced out, but the mount is still tracking about its TILTED axis, so
            // the field drifts from the last sync onward -- predominantly in Dec.
            // This is the residual the guider corrects; it grows with elapsed
            // sidereal time, not a single fixed offset (see ApplyTrackingDrift).
            // Anchored to the synced reference position, NOT the live base RA
            // (which advances with LST and would swamp the drift -- see the
            // _trackingRefRa rationale).
            // A post-centering GOTO defers the believed-pointing anchor capture to the
            // first read after the slew completes (the goto's target steps go stale by
            // the slew duration -- see _trackingRefBasePending). Mid-slew reads report
            // the drift-anchored target, matching the pre-deviation behaviour.
            if (_trackingRefBasePending)
            {
                if (await IsSlewingAsync(ct))
                {
                    var trackedSecondsInFlight = (TimeProvider.GetUtcNow() - _trackingRefUtc).TotalSeconds;
                    return ApplyTrackingDrift(hemisphere, axis, _trackingRefRa, _trackingRefDec, trackedSecondsInFlight);
                }
                _trackingRefBaseRa = baseRa;
                _trackingRefBaseDec = baseDec;
                _trackingRefBasePending = false;
                // Restart the drift clock at arrival: the residual polar drift
                // re-accumulates from the freshly-reached target.
                _trackingRefUtc = TimeProvider.GetUtcNow();
            }

            var trackedSeconds = (TimeProvider.GetUtcNow() - _trackingRefUtc).TotalSeconds;
            var (raDrifted, decDrifted) = ApplyTrackingDrift(hemisphere, axis, _trackingRefRa, _trackingRefDec, trackedSeconds);

            // Commanded axis motion since the reference must still show up 1:1. Under
            // perfect tracking the believed (encoder) pointing is constant, so its
            // deviation from the reference anchor IS that motion: guide pulses,
            // MoveAxis nudges. Without this term the anchor freeze made pulse guiding
            // invisible in pointing reads -- the guider's calibration measured ~0
            // displacement per pulse and rejected itself for insufficient displacement.
            var dRaHours = baseRa - _trackingRefBaseRa;
            if (dRaHours > 12.0) dRaHours -= 24.0;
            else if (dRaHours < -12.0) dRaHours += 24.0;
            var dDecDeg = baseDec - _trackingRefBaseDec;

            var raOut = (raDrifted + dRaHours) % 24.0;
            if (raOut < 0) raOut += 24.0;
            return (raOut, Math.Clamp(decDrifted + dDecDeg, -90.0, 90.0));
        }

        // Imaging regime, pre-centering: the OTA has been slewed away from the
        // pole. The true sky it reaches is the believed (encoder) pointing rotated
        // rigidly by the axis tilt -- a small (~misalignment-sized) offset that
        // respects WHERE the scope was slewed, so a GOTO to Dec=45 reports ~45
        // (not the pole). The imaging centering loop plate-solves this offset and
        // syncs it away (see SyncRaDecAsync), after which the drift branch above
        // takes over.
        return ApplyAxisTiltToPointing(axis, hemisphere, baseRa, baseDec);
    }

    /// <summary>
    /// True when <paramref name="decDeg"/> sits within <see cref="NearPoleDeg"/>
    /// of the site's celestial pole -- i.e. the OTA is parked on the pole for
    /// polar-align, as opposed to slewed to an imaging target.
    /// </summary>
    private static bool IsNearSitePole(double decDeg, Hemisphere hemisphere)
    {
        var poleDec = hemisphere == Hemisphere.North ? 90.0 : -90.0;
        return Math.Abs(decDeg - poleDec) <= NearPoleDeg;
    }

    /// <summary>
    /// Convert RA encoder steps to the equivalent rotation angle about the
    /// polar axis, in radians. Fully encoder-derived -- no LST term -- so a
    /// stationary axis reports the same angle from one second to the next
    /// even as sidereal time advances. This is the key correctness pivot
    /// over the previous <c>baseRa</c>-based proxy: <c>StepsToRa</c>
    /// computes <c>RA = LST - HA(steps)</c>, which drifted the angle even
    /// when the encoder didn't move and inflated the observed misalignment.
    /// </summary>
    /// <param name="posRa">Encoder steps from home. May be negative.</param>
    /// <param name="cprRa">Steps per full revolution. Must be &gt; 0.</param>
    /// <returns>Rotation angle in radians, wrapped to [-pi, pi).</returns>
    internal static double EncoderAngleRadians(int posRa, uint cprRa)
    {
        if (cprRa == 0) return 0.0;
        var rev = (double)posRa / cprRa;
        var rad = rev * 2.0 * Math.PI;
        // Wrap to (-pi, pi] so the Rodrigues rotation behaves identically at
        // wrap-around boundaries -- otherwise a 359deg vs -1deg encoder gives
        // numerically different small-circle positions over many revolutions.
        rad %= 2.0 * Math.PI;
        if (rad > Math.PI) rad -= 2.0 * Math.PI;
        else if (rad <= -Math.PI) rad += 2.0 * Math.PI;
        return rad;
    }

    /// <summary>
    /// Convert a topocentric (azErr, altErr) misalignment offset into the
    /// J2000 unit vector of the actual mount RA-axis. Inverts the geometry
    /// of <see cref="PolarAxisSolver.DecomposeAxisError"/>: that function
    /// asks "given a J2000 axis, where is it relative to the apparent pole
    /// in topocentric az/alt?" -- here we ask the reverse, "given a desired
    /// (azErr, altErr) offset from the apparent pole, what J2000 axis vector
    /// produces that?". Round-tripping a value through this function and
    /// then through <c>DecomposeAxisError</c> recovers the input within the
    /// small-angle linearisation noise of SOFA's refraction model.
    /// </summary>
    /// <remarks>
    /// We hold pressure / temperature constant (standard atmosphere) for the
    /// pole-position lookup so the test is deterministic across machines.
    /// The axis itself bypasses refraction (mechanical orientation), which
    /// matches <c>DecomposeAxisError</c>'s convention; this means the round
    /// trip has a small residual (sub-arcsec at sensible site latitudes)
    /// from the asymmetric refraction handling -- which is intentional and
    /// not a bug.
    /// </remarks>
    internal static Vec3 TopocentricMisalignmentToJ2000Axis(
        double siteLatDeg, double siteLonDeg, double siteElevM,
        DateTimeOffset utc,
        double azErrArcmin, double altErrArcmin,
        Hemisphere hemisphere,
        ITimeProvider timeProvider)
    {
        // Apparent pole in topocentric (refracted), matching the pole branch
        // of DecomposeAxisError. Use a standard atmosphere so the test is
        // reproducible -- real users aren't running this during tests.
        const double sitePressureHPa = 1010.0;
        const double siteTempC = 10.0;
        utc.ToSOFAUtcJd(out var utc1, out var utc2);
        var poleDecDeg = hemisphere == Hemisphere.North ? 90.0 : -90.0;
        var (_, _, poleAzDeg, poleAltDeg) = SOFAHelpers.J2000ToTopo(
            ra: 0.0, dec: poleDecDeg,
            utc1: utc1, utc2: utc2,
            siteLat: siteLatDeg, siteLong: siteLonDeg, siteElevation: siteElevM,
            sitePressure: sitePressureHPa, siteTemp: siteTempC);

        // Misaligned axis topocentric = pole + offset. arcmin -> degrees.
        var axisAzDeg = (poleAzDeg + azErrArcmin / 60.0) % 360.0;
        if (axisAzDeg < 0.0) axisAzDeg += 360.0;
        var axisAltDeg = poleAltDeg + altErrArcmin / 60.0;

        // Convert axis topocentric -> J2000 with refraction OFF (axis is a
        // mechanical orientation, not a sky observation). Transform.Refraction
        // defaults to false, which is what we want. SiteTemperature must still
        // be set because the AzEl→J2000 path requires all four site params to
        // be non-NaN before it will attempt the conversion at all.
        var transform = new Transform(timeProvider);
        transform.JulianDateUTC = utc1 + utc2;
        transform.SiteLatitude = siteLatDeg;
        transform.SiteLongitude = siteLonDeg;
        transform.SiteElevation = siteElevM;
        transform.SiteTemperature = siteTempC;
        transform.SetAzimuthElevation(axisAzDeg, axisAltDeg);
        return PolarAxisSolver.RaDecToUnitVec(transform.RAJ2000, transform.DecJ2000);
    }

    /// <summary>
    /// Project the home (celestial-pole) pointing through an encoder rotation
    /// of <paramref name="encoderRad"/> about the misaligned axis vector.
    /// Pure rotation math (Rodrigues' formula) -- the topocentric-to-J2000
    /// conversion happens in <see cref="TopocentricMisalignmentToJ2000Axis"/>.
    /// Decoupling these two steps lets the unit tests exercise each on its
    /// own.
    /// </summary>
    /// <returns>Misaligned (RA, Dec). RA in [0, 24) hours; Dec in [-90, 90] degrees.</returns>
    internal static (double Ra, double Dec) ApplyPolarMisalignment(
        Vec3 misalignedAxisJ2000,
        Hemisphere hemisphere,
        double encoderRad)
    {
        var poleZ = hemisphere == Hemisphere.North ? 1.0 : -1.0;

        // Tilted axis components.
        var kx = misalignedAxisJ2000.X;
        var ky = misalignedAxisJ2000.Y;
        var kz = misalignedAxisJ2000.Z;

        // Home pointing = celestial pole of the user's hemisphere. At home
        // the scope physically lies along its OWN polar axis, but its OTA
        // points wherever the user parked it; for the polar-align routine to
        // work the user is expected to start near the pole, so this is the
        // right home reference for the synthesiser.
        var vx = 0.0;
        var vy = 0.0;
        var vz = poleZ;

        var cosT = Math.Cos(encoderRad);
        var sinT = Math.Sin(encoderRad);

        // Rodrigues: R(k, theta) . v = v.cos(theta) + (k cross v).sin(theta) + k.(k . v).(1 - cos(theta)).
        var kDotV = kx * vx + ky * vy + kz * vz;
        var crossX = ky * vz - kz * vy;
        var crossY = kz * vx - kx * vz;
        var crossZ = kx * vy - ky * vx;
        var oneMinusCos = 1.0 - cosT;
        var rx = vx * cosT + crossX * sinT + kx * kDotV * oneMinusCos;
        var ry = vy * cosT + crossY * sinT + ky * kDotV * oneMinusCos;
        var rz = vz * cosT + crossZ * sinT + kz * kDotV * oneMinusCos;

        // Decompose back to (RA, Dec). Clamp z to avoid Asin domain errors
        // from accumulated float noise at the pole.
        var dec = Math.Asin(Math.Clamp(rz, -1.0, 1.0)) * 180.0 / Math.PI;
        var raDeg = Math.Atan2(ry, rx) * 180.0 / Math.PI;
        var ra = raDeg / 15.0;
        if (ra < 0.0) ra += 24.0;
        return (ra, dec);
    }

    /// <summary>
    /// Project the believed (encoder) pointing through the mount's rigid axis
    /// tilt to recover the true sky direction the OTA reaches when slewed away
    /// from the pole. Unlike <see cref="ApplyPolarMisalignment"/> -- which pins
    /// the OTA to the pole and sweeps by the RA encoder, valid only while the
    /// scope is parked on the pole for polar-align -- this rotates the believed
    /// pointing by the rotation that carries the true pole onto the misaligned
    /// axis. The result is a small (~misalignment-magnitude) position-dependent
    /// offset from the commanded coordinates: exactly the pointing error a real
    /// polar-misaligned GEM exhibits, which plate-solve centering then detects
    /// and syncs away.
    /// </summary>
    /// <param name="misalignedAxisJ2000">The actual mount RA-axis in J2000 (the
    /// tilted pole), as produced by <see cref="TopocentricMisalignmentToJ2000Axis"/>.</param>
    /// <param name="hemisphere">Site hemisphere -- selects which true pole the
    /// tilt is measured from.</param>
    /// <param name="baseRa">Believed (encoder-derived) RA in hours.</param>
    /// <param name="baseDec">Believed (encoder-derived) Dec in degrees.</param>
    /// <returns>True (RA, Dec). RA in [0, 24) hours; Dec in [-90, 90] degrees.</returns>
    internal static (double Ra, double Dec) ApplyAxisTiltToPointing(
        Vec3 misalignedAxisJ2000,
        Hemisphere hemisphere,
        double baseRa,
        double baseDec)
    {
        var poleZ = hemisphere == Hemisphere.North ? 1.0 : -1.0;
        var kx = misalignedAxisJ2000.X;
        var ky = misalignedAxisJ2000.Y;
        var kz = misalignedAxisJ2000.Z;

        var believed = PolarAxisSolver.RaDecToUnitVec(baseRa, baseDec);

        // Rotation R that carries the true pole (0, 0, poleZ) onto the misaligned
        // axis k: rotation axis r = norm(pole x k), angle = acos(pole . k).
        // pole x k = (-poleZ*ky, poleZ*kx, 0).
        var dot = Math.Clamp(poleZ * kz, -1.0, 1.0);
        var angle = Math.Acos(dot);
        var rxRaw = -poleZ * ky;
        var ryRaw = poleZ * kx;
        var axisLen = Math.Sqrt(rxRaw * rxRaw + ryRaw * ryRaw);
        if (axisLen < 1e-12 || angle < 1e-9)
        {
            // Axis already coincides with the pole (no tilt) -- believed == true.
            return (baseRa, baseDec);
        }
        var rx = rxRaw / axisLen;
        var ry = ryRaw / axisLen;
        const double rz = 0.0; // pole x k always lies in the equatorial plane

        var cosT = Math.Cos(angle);
        var sinT = Math.Sin(angle);
        var oneMinusCos = 1.0 - cosT;

        // Rodrigues: R(r, angle) . v = v.cos + (r x v).sin + r.(r . v).(1 - cos).
        var vx = believed.X;
        var vy = believed.Y;
        var vz = believed.Z;
        var rDotV = rx * vx + ry * vy + rz * vz;
        var crossX = ry * vz - rz * vy;
        var crossY = rz * vx - rx * vz;
        var crossZ = rx * vy - ry * vx;
        var tx = vx * cosT + crossX * sinT + rx * rDotV * oneMinusCos;
        var ty = vy * cosT + crossY * sinT + ry * rDotV * oneMinusCos;
        var tz = vz * cosT + crossZ * sinT + rz * rDotV * oneMinusCos;

        var (ra, dec) = PolarAxisSolver.UnitVecToRaDec(new Vec3(tx, ty, tz));
        return (ra, dec);
    }

    /// <summary>
    /// True sky direction of a polar-misaligned mount that has been TRACKING for
    /// <paramref name="trackedSeconds"/> since its pointing was last established
    /// (the most recent plate-solve sync). The mount drives its OTA about its own
    /// (tilted) RA axis at the sidereal rate; the sky rotates the same angle about
    /// the TRUE celestial pole. The residual between those two rotations is the
    /// classic polar-misalignment field drift -- predominantly in Declination,
    /// with a rate set by the misalignment magnitude and the target's position.
    /// </summary>
    /// <remarks>
    /// At <paramref name="trackedSeconds"/> == 0 both rotations are the identity,
    /// so the freshly-centred frame sits exactly on the believed pointing and the
    /// drift accumulates only thereafter. This is what makes the imaging centering
    /// loop converge (zero residual at the moment of sync) while still leaving the
    /// slow drift a guider must correct. Reuses <see cref="PolarAxisSolver.Rotate"/>
    /// (Rodrigues) rather than re-deriving the rotation inline.
    /// </remarks>
    /// <param name="hemisphere">Site hemisphere -- selects the true pole sign.</param>
    /// <param name="misalignedAxisJ2000">The mount's tilted RA axis in J2000, from
    /// <see cref="TopocentricMisalignmentToJ2000Axis"/>.</param>
    /// <param name="believedRa">Believed (commanded/encoder) RA in hours.</param>
    /// <param name="believedDec">Believed (commanded/encoder) Dec in degrees.</param>
    /// <param name="trackedSeconds">Seconds tracked since the last sync. Negative
    /// values are clamped to 0 (a sync timestamped in the future cannot drift).</param>
    /// <returns>True (RA, Dec). RA in [0, 24) hours; Dec in [-90, 90] degrees.</returns>
    internal static (double Ra, double Dec) ApplyTrackingDrift(
        Hemisphere hemisphere,
        Vec3 misalignedAxisJ2000,
        double believedRa,
        double believedDec,
        double trackedSeconds)
    {
        if (trackedSeconds <= 0.0)
        {
            return (believedRa, believedDec);
        }

        // Sidereal angular rate (one full turn per sidereal day).
        const double siderealRadPerSec = 2.0 * Math.PI / 86164.0905;
        var theta = siderealRadPerSec * trackedSeconds;

        var poleZ = hemisphere == Hemisphere.North ? 1.0 : -1.0;
        // The true celestial pole is the J2000 equatorial z-axis by definition.
        var truePole = new Vec3(0.0, 0.0, poleZ);

        var believed = PolarAxisSolver.RaDecToUnitVec(believedRa, believedDec);
        // Track about the misaligned axis, then de-rotate by the ideal sidereal
        // motion about the true pole. Net = the field drift the misalignment leaks.
        var tracked = PolarAxisSolver.Rotate(believed, misalignedAxisJ2000, theta);
        var drifted = PolarAxisSolver.Rotate(tracked, truePole, -theta);

        return PolarAxisSolver.UnitVecToRaDec(drifted);
    }

    /// <summary>
    /// Pull a double-valued URI query key off the device, falling back to
    /// <paramref name="defaultValue"/> when absent or unparseable. Uses
    /// invariant culture so test-author URIs stay portable across machines.
    /// </summary>
    private static double ParseDoubleQuery(FakeDevice device, string key, double defaultValue)
    {
        var raw = device.Query[key];
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : defaultValue;
    }
}
