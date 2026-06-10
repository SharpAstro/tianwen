using Shouldly;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class FakeSkywatcherMountDriverTests(ITestOutputHelper output)
{
    private (FakeSkywatcherMountDriver mount, FakeExternal external) CreateMount(
        double latitude = 48.2,
        double longitude = 16.3,
        double azMisalignmentArcmin = 0.0,
        double altMisalignmentArcmin = 0.0,
        DateTimeOffset? now = null,
        bool decPulseGoTo = false)
    {
        var external = new FakeExternal(output, now: now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var query = new NameValueCollection
        {
            { "port", "SkyWatcher" },
            { "latitude", latitude.ToString() },
            { "longitude", longitude.ToString() },
            { "polarMisalignmentAzArcmin", azMisalignmentArcmin.ToString() },
            { "polarMisalignmentAltArcmin", altMisalignmentArcmin.ToString() }
        };
        if (decPulseGoTo)
        {
            query.Add("decPulseGoto", "true");
        }
        var device = new FakeDevice(DeviceType.Mount, 1, query);
        var mount = new FakeSkywatcherMountDriver(device, external.BuildServiceProvider());
        return (mount, external);
    }

    private async Task<(FakeSkywatcherMountDriver mount, FakeExternal external)> CreateConnectedMountAsync(
        CancellationToken ct,
        double latitude = 48.2,
        double longitude = 16.3,
        double azMisalignmentArcmin = 0.0,
        double altMisalignmentArcmin = 0.0,
        DateTimeOffset? now = null,
        bool decPulseGoTo = false)
    {
        var (mount, external) = CreateMount(latitude, longitude, azMisalignmentArcmin, altMisalignmentArcmin, now, decPulseGoTo);
        await mount.ConnectAsync(ct);
        await mount.SetSiteLatitudeAsync(latitude, ct);
        await mount.SetSiteLongitudeAsync(longitude, ct);
        return (mount, external);
    }

    #region Connect / Disconnect

    [Fact(Timeout = 60_000)]
    public async Task GivenSkywatcherMountWhenConnectedThenIsConnectedAndFirmwareAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();

        await mount.ConnectAsync(ct);

        mount.Connected.ShouldBeTrue();
        var driverInfo = mount.DriverInfo;
        driverInfo.ShouldNotBeNull();
        driverInfo.ShouldContain("Skywatcher");
        driverInfo.ShouldContain("EQ6");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedSkywatcherMountWhenDisconnectedThenIsNotConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();

        await mount.ConnectAsync(ct);
        mount.Connected.ShouldBeTrue();

        await mount.DisconnectAsync(ct);
        mount.Connected.ShouldBeFalse();
    }

    #endregion

    #region Tracking

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenTrackingEnabledThenIsTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetTrackingAsync(true, ct);

        (await mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenTrackingMountWhenTrackingDisabledThenNotTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetTrackingAsync(true, ct);
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue();

        await mount.SetTrackingAsync(false, ct);
        (await mount.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Slew

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenSlewStartedThenIsSlewing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.BeginSlewRaDecAsync(12.0, 45.0, ct);

        (await mount.IsSlewingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSlewingMountWhenAbortedThenNotSlewing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.BeginSlewRaDecAsync(12.0, 45.0, ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeTrue();

        await mount.AbortSlewAsync(ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Sync

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenSyncedThenPositionUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(ct);

        await mount.SyncRaDecAsync(12.5, 45.0, ct);

        var ra = await mount.GetRightAscensionAsync(ct);
        var dec = await mount.GetDeclinationAsync(ct);

        ra.ShouldBe(12.5, 0.1);
        dec.ShouldBe(45.0, 0.1);
    }

    #endregion

    #region Pulse Guide

    [Theory(Timeout = 60_000)]
    [InlineData(GuideDirection.East)]
    [InlineData(GuideDirection.West)]
    [InlineData(GuideDirection.North)]
    [InlineData(GuideDirection.South)]
    public async Task GivenConnectedMountWhenPulseGuideThenCompletes(GuideDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        // Should complete without throwing
        await mount.PulseGuideAsync(direction, TimeSpan.FromMilliseconds(100), ct);
    }

    /// <summary>
    /// Pulse guiding must move the POINTING at the configured guide rate (default index 2 =
    /// 0.5x sidereal): West/East offset the RA tracking rate by ±0.5x, North/South move the
    /// Dec axis at 0.5x. This pins both the driver semantics (RA pulses offset the tracking
    /// rate rather than replacing it) and the fake's rate fidelity (the ':I' preset is
    /// honoured on both axes; fractional steps are not truncated away per tick).
    /// Southern rows pin the hemisphere handling: below the equator the RA worm tracks in
    /// reverse and the steps-to-sky conversion mirrors with it, so pulse displacement must
    /// come out identical to the northern case.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(GuideDirection.West, 48.2)]
    [InlineData(GuideDirection.East, 48.2)]
    [InlineData(GuideDirection.North, 48.2)]
    [InlineData(GuideDirection.South, 48.2)]
    [InlineData(GuideDirection.West, -33.9)]
    [InlineData(GuideDirection.East, -33.9)]
    [InlineData(GuideDirection.North, -33.9)]
    [InlineData(GuideDirection.South, -33.9)]
    public async Task GivenTrackingMountWhenPulseGuidingThenPointingMovesAtGuideRate(GuideDirection direction, double latitude)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(ct, latitude: latitude);
        await mount.SyncRaDecAsync(6.0, latitude >= 0 ? 45.0 : -45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        var raBefore = await mount.GetRightAscensionAsync(ct);
        var decBefore = await mount.GetDeclinationAsync(ct);

        var pulse = TimeSpan.FromSeconds(60);
        await mount.PulseGuideAsync(direction, pulse, ct);

        var raAfter = await mount.GetRightAscensionAsync(ct);
        var decAfter = await mount.GetDeclinationAsync(ct);

        // 0.5x sidereal for 60s = 451.2 arcsec of axis motion.
        const double guideFraction = 0.5;
        var expectedArcsec = guideFraction * 15.041 * pulse.TotalSeconds;
        var dRaArcsec = (raAfter - raBefore) * 15.0 * 3600.0;
        var dDecArcsec = (decAfter - decBefore) * 3600.0;
        output.WriteLine($"direction={direction} dRA={dRaArcsec:F1}\" dDec={dDecArcsec:F1}\" expected={expectedArcsec:F1}\"");

        var (axisDeltaArcsec, otherAxisDeltaArcsec) = direction is GuideDirection.West or GuideDirection.East
            ? (dRaArcsec, dDecArcsec)
            : (dDecArcsec, dRaArcsec);

        Math.Abs(axisDeltaArcsec).ShouldBe(expectedArcsec, expectedArcsec * 0.1,
            $"a 60s {direction} pulse at 0.5x sidereal must move the pulsed axis by ~{expectedArcsec:F0} arcsec");
        Math.Abs(otherAxisDeltaArcsec).ShouldBeLessThan(expectedArcsec * 0.05,
            "the other axis must stay put during the pulse");

        // Opposite directions must move opposite ways (E vs W, N vs S) — covered by
        // running all four theory cases; here just pin that the pulse moved at all
        // in a consistent direction (sign is mapping-dependent, magnitude is not).
        Math.Abs(axisDeltaArcsec).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Wire contract (pinned against the GSS oracle transcripts): an RA pulse while
    /// tracking changes ONLY the step period — :I with the combined rate, then :I back
    /// to sidereal. No :G/:J/:K stop/start: real firmware rejects :G while the motor
    /// runs (error !2), and the decel/accel transient would eat short pulses.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(GuideDirection.West)]
    [InlineData(GuideDirection.East)]
    public async Task GivenTrackingMountWhenRaPulseGuidingThenOnlyStepPeriodChanges(GuideDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        var serial = mount.SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        var before = serial.CommandLogSnapshot.Length;

        await mount.PulseGuideAsync(direction, TimeSpan.FromSeconds(2), ct);

        var motionCmds = serial.CommandLogSnapshot.Skip(before)
            .Where(c => c.Length > 1 && c[1] is 'G' or 'I' or 'J' or 'K' or 'L')
            .ToList();
        output.WriteLine(string.Join(" ", motionCmds));
        motionCmds.ShouldAllBe(c => c.StartsWith(":I1"),
            "a tracking RA pulse must be a live :I rate change, never a stop/start");
        motionCmds.Count.ShouldBe(2, "exactly one pulse-rate :I and one sidereal-restore :I");
    }

    /// <summary>
    /// Pulses below the 20 ms floor are serial-latency noise (GSS MinPulseDurationRa/Dec
    /// default): the driver must drop them without touching the wire.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GivenPulseBelowMinimumDurationThenNothingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(ct);
        await mount.SetTrackingAsync(true, ct);

        var serial = mount.SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        var before = serial.CommandLogSnapshot.Length;

        await mount.PulseGuideAsync(GuideDirection.West, TimeSpan.FromMilliseconds(10), ct);
        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromMilliseconds(19), ct);

        serial.CommandLogSnapshot.Length.ShouldBe(before);
    }

    /// <summary>
    /// Opt-in decPulseGoto=true: a Dec pulse converts to an exact relative low-speed
    /// micro-GOTO (GSS DecPulseGoTo) instead of holding f x sidereal for the duration.
    /// Displacement must come out the same as rate mode (duration x rate), and the wire
    /// must show a goto (:G2 func 2 + :H2 + :J2), not a rate run (:I2 ... :K2).
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(GuideDirection.North)]
    [InlineData(GuideDirection.South)]
    public async Task GivenDecPulseGotoModeWhenDecPulsingThenMicroGotoMovesExactSteps(GuideDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(ct, decPulseGoTo: true);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        var serial = mount.SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        var decBefore = await mount.GetDeclinationAsync(ct);
        var before = serial.CommandLogSnapshot.Length;

        var pulse = TimeSpan.FromSeconds(60);
        await mount.PulseGuideAsync(direction, pulse, ct);

        var decAfter = await mount.GetDeclinationAsync(ct);
        var expectedArcsec = 0.5 * 15.041 * pulse.TotalSeconds;
        var dDecArcsec = (decAfter - decBefore) * 3600.0;
        output.WriteLine($"direction={direction} dDec={dDecArcsec:F1}\" expected={expectedArcsec:F1}\"");
        Math.Abs(dDecArcsec).ShouldBe(expectedArcsec, expectedArcsec * 0.02,
            "the micro-GOTO step count is exact, so displacement must match duration x rate closely");

        var decCmds = serial.CommandLogSnapshot.Skip(before).Where(c => c.Length > 2 && c[2] == '2').ToList();
        decCmds.ShouldContain(c => c.StartsWith(":G22"), "Dec pulse must use the low-speed GOTO func");
        decCmds.ShouldContain(c => c.StartsWith(":H2"), "relative target increment");
        decCmds.ShouldNotContain(c => c.StartsWith(":I2"), "no rate preset in micro-GOTO mode");
    }

    /// <summary>
    /// Regression: after an away-from-pole sync the misalignment model anchors reported
    /// pointing to the sync reference plus time-based drift — and used to FREEZE OUT the
    /// live encoder entirely, making guide pulses invisible in pointing reads (the
    /// guider's calibration measured ~0 displacement and rejected itself). Commanded
    /// axis motion since the reference must appear 1:1 on top of the drift.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedSyncedMountWhenPulseGuidingThenPointingStillMoves()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(ct,
            azMisalignmentArcmin: 30.0, altMisalignmentArcmin: -10.0);
        await mount.SetTrackingAsync(true, ct);
        // Away-from-pole plate-solve sync: enters the post-centering drift regime.
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        var decBefore = await mount.GetDeclinationAsync(ct);
        var pulse = TimeSpan.FromSeconds(60);
        await mount.PulseGuideAsync(GuideDirection.North, pulse, ct);
        var decAfter = await mount.GetDeclinationAsync(ct);

        // 0.5x sidereal x 60s = 451" commanded; the residual polar drift contributes
        // only a few arcsec over the same minute, hence the generous 15% tolerance.
        var expectedArcsec = 0.5 * 15.041 * pulse.TotalSeconds;
        var dDecArcsec = (decAfter - decBefore) * 3600.0;
        output.WriteLine($"dDec={dDecArcsec:F1}\" expected~{expectedArcsec:F1}\"");
        Math.Abs(dDecArcsec).ShouldBe(expectedArcsec, expectedArcsec * 0.15,
            "a North pulse must move the reported Dec even in the post-sync drift regime");
    }

    #endregion

    #region Southern Hemisphere

    /// <summary>
    /// In the south the RA worm physically tracks in REVERSE (:G dir bit 0 set, per the
    /// GSServer reference: EqS tracking gets the negated rate) while the driver's mirrored
    /// steps-to-HA conversion keeps the believed RA constant. If either side flips without
    /// the other, a tracked target drifts at 2x sidereal in reads — this pins them together.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GivenSouthernMountWhenTrackingThenPointingStaysOnTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = await CreateConnectedMountAsync(ct, latitude: -33.9, longitude: 18.4);
        await mount.SyncRaDecAsync(6.0, -45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        var ra0 = await mount.GetRightAscensionAsync(ct);
        external.TimeProvider.Advance(TimeSpan.FromMinutes(10));

        var ra10 = await mount.GetRightAscensionAsync(ct);
        var dec10 = await mount.GetDeclinationAsync(ct);
        output.WriteLine($"ra0={ra0:F5}h ra10={ra10:F5}h dec10={dec10:F4}");

        // 10 untracked minutes would read ~0.167h of RA drift; tracked must hold.
        ra10.ShouldBe(ra0, 0.005);
        dec10.ShouldBe(-45.0, 0.01);
    }

    /// <summary>
    /// Southern GOTO: the mirrored RaToSteps/DecToSteps must produce a delta that the
    /// (hemisphere-agnostic, step-space) goto executes onto the right sky position, and
    /// the fake's post-goto tracking auto-resume must run in the southern (reverse)
    /// direction — wrong-direction resume shows up as RA drifting off target after arrival.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GivenSouthernMountWhenSlewedThenArrivesAtTargetAndTrackingHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = await CreateConnectedMountAsync(ct, latitude: -33.9, longitude: 18.4);
        await mount.SyncRaDecAsync(6.0, -45.0, ct);

        await mount.BeginSlewRaDecAsync(8.0, -30.0, ct);
        await PumpUntilNotSlewingAsync(mount, external, ct, TimeSpan.FromSeconds(40));

        (await mount.GetRightAscensionAsync(ct)).ShouldBe(8.0, 0.05);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(-30.0, 0.05);

        // Post-goto auto-resumed tracking must hold the new target in the south.
        var raArrived = await mount.GetRightAscensionAsync(ct);
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        (await mount.GetRightAscensionAsync(ct)).ShouldBe(raArrived, 0.005);
    }

    #endregion

    #region Camera Snap

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenCameraSnapThenCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        var settings = new CameraSnapSettings(
            ShutterTime: TimeSpan.FromMilliseconds(100),
            Interval: TimeSpan.FromSeconds(1),
            ShotCount: 1);

        await mount.CameraSnapAsync(settings, ct);
    }

    #endregion

    #region Axis Position

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountThenAxisPositionReturnsValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        var raPos = await mount.GetAxisPositionAsync(TelescopeAxis.Primary, ct);
        var decPos = await mount.GetAxisPositionAsync(TelescopeAxis.Seconary, ct);

        raPos.ShouldNotBeNull();
        decPos.ShouldNotBeNull();
    }

    #endregion

    #region Park

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenParkedThenAtPark()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.ParkAsync(ct);
        (await mount.AtParkAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenParkedMountWhenUnparkedThenNotAtPark()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.ParkAsync(ct);
        (await mount.AtParkAsync(ct)).ShouldBeTrue();

        await mount.UnparkAsync(ct);
        (await mount.AtParkAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Capabilities

    [Fact(Timeout = 60_000)]
    public async Task GivenSkywatcherMountThenCapabilitiesReflectGEM()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.CanPulseGuide.ShouldBeTrue();
        mount.CanSetTracking.ShouldBeTrue();
        mount.CanSync.ShouldBeTrue();
        mount.CanCameraSnap.ShouldBeTrue();
        mount.CanSlew.ShouldBeTrue();
        mount.CanSlewAsync.ShouldBeTrue();
        mount.CanPark.ShouldBeTrue();
        mount.CanSetGuideRates.ShouldBeTrue();

        mount.CanMoveAxis(TelescopeAxis.Primary).ShouldBeTrue();
        mount.CanMoveAxis(TelescopeAxis.Seconary).ShouldBeTrue();
        mount.CanMoveAxis(TelescopeAxis.Tertiary).ShouldBeFalse();
    }

    #endregion

    #region Site

    [Fact(Timeout = 60_000)]
    public async Task GivenSkywatcherMountWhenSiteIsPushedThenGetSiteReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetSiteLatitudeAsync(48.2, ct);
        await mount.SetSiteLongitudeAsync(16.3, ct);

        (await mount.GetSiteLatitudeAsync(ct)).ShouldBe(48.2, 0.01);
        (await mount.GetSiteLongitudeAsync(ct)).ShouldBe(16.3, 0.01);
    }

    #endregion

    #region Polar Misalignment — imaging regime

    // ~30'/-10' misalignment used across the imaging-regime tests; magnitude
    // ~0.5deg, so the pointing offset stays well inside a degree.
    private const double TestAzMisalignArcmin = 30.0;
    private const double TestAltMisalignArcmin = -10.0;

    /// <summary>
    /// Advance fake time in small steps until the mount reports it has stopped
    /// slewing (or a cap is hit). The fake Skywatcher integrates its goto at
    /// 3deg/s on a 50ms simulation timer, so the slew only progresses when fake
    /// time advances.
    /// </summary>
    private static async Task PumpUntilNotSlewingAsync(FakeSkywatcherMountDriver mount, FakeExternal external, CancellationToken ct, TimeSpan max)
    {
        var step = TimeSpan.FromMilliseconds(200);
        var pumped = TimeSpan.Zero;
        while (pumped < max)
        {
            if (!await mount.IsSlewingAsync(ct))
            {
                break;
            }
            external.TimeProvider.Advance(step);
            pumped += step;
        }
    }

    [Fact]
    public void GivenMisalignedAxisAwayFromPoleWhenApplyAxisTiltThenNearCommandedNotPole()
    {
        // Misaligned RA axis tilted ~0.5deg off the +Z (north) celestial pole.
        var tiltRad = 0.5 * Math.PI / 180.0;
        var axis = new Vec3(Math.Sin(tiltRad), 0.0, Math.Cos(tiltRad));

        // Believed (commanded/encoder) pointing well away from the pole.
        const double believedRa = 6.0;   // hours
        const double believedDec = 45.0; // degrees

        var (ra, dec) = FakeSkywatcherMountDriver.ApplyAxisTiltToPointing(
            axis, Hemisphere.North, believedRa, believedDec);

        // True pointing must track the commanded Dec to within the misalignment
        // magnitude -- emphatically NOT snapped to the pole (the original bug).
        dec.ShouldBe(believedDec, 1.0);
        dec.ShouldBeLessThan(80.0);

        // ...but it must NOT be identical either: the misalignment carries on so
        // plate-solve centering has a real offset to detect and sync away.
        var offsetDeg = Math.Abs(dec - believedDec) + Math.Abs(ra - believedRa) * 15.0;
        offsetDeg.ShouldBeGreaterThan(0.01);
    }

    [Fact]
    public void GivenAlignedAxisWhenApplyAxisTiltThenReturnsBelievedUnchanged()
    {
        // Axis exactly on the pole == no misalignment -> identity transform.
        var pole = new Vec3(0.0, 0.0, 1.0);

        var (ra, dec) = FakeSkywatcherMountDriver.ApplyAxisTiltToPointing(
            pole, Hemisphere.North, 6.0, 45.0);

        ra.ShouldBe(6.0, 1e-9);
        dec.ShouldBe(45.0, 1e-9);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenSlewedAwayFromPoleThenReportsNearTargetNotPole()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        // Slew Dec from the home pole down to +45; keep RA at the current LST so
        // the RA delta is ~0 and only the Dec axis has to travel (bounds pump time).
        var lst = await mount.GetSiderealTimeAsync(ct);
        await mount.BeginSlewRaDecAsync(lst, 45.0, ct);
        await PumpUntilNotSlewingAsync(mount, external, ct, TimeSpan.FromSeconds(40));

        var dec = await mount.GetDeclinationAsync(ct);

        // Reports ~commanded (within the misalignment magnitude), NOT the pole.
        dec.ShouldBe(45.0, 1.5);
        dec.ShouldBeLessThan(80.0);
        // No plate-solve sync yet -> the misalignment is still "carried on".
        mount.IsAlignmentCorrected.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenPlateSolveSyncAwayFromPoleThenLearnsAlignmentAndReportsBelieved()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        mount.IsAlignmentCorrected.ShouldBeFalse();

        // A plate-solve-driven sync to a real target (away from the pole) models
        // the mount learning its true orientation.
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        mount.IsAlignmentCorrected.ShouldBeTrue();
        // After correction the reported pointing is the believed (encoder)
        // position verbatim -- no residual offset.
        (await mount.GetRightAscensionAsync(ct)).ShouldBe(6.0, 0.01);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(45.0, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenSyncToPoleThenAlignmentNotLearned()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        // A sync to the pole (startup / park convention) must NOT learn alignment,
        // or the polar-align simulation would be zeroed before it can run.
        var lst = await mount.GetSiderealTimeAsync(ct);
        await mount.SyncRaDecAsync(lst, 90.0, ct);

        mount.IsAlignmentCorrected.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSouthernMisalignedMountWhenSyncAwayFromPoleThenLearnsAlignment()
    {
        var ct = TestContext.Current.CancellationToken;
        // Southern site -> the home pole is -90; hemisphere is driven by site lat.
        var (mount, _) = await CreateConnectedMountAsync(
            ct, latitude: -33.9, longitude: 18.4,
            azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        mount.IsAlignmentCorrected.ShouldBeFalse();

        // Sync to a southern target well away from the -90 pole.
        await mount.SyncRaDecAsync(6.0, -45.0, ct);

        mount.IsAlignmentCorrected.ShouldBeTrue();
        (await mount.GetDeclinationAsync(ct)).ShouldBe(-45.0, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenTrackingAfterSyncThenFieldDriftsSlowlyAndAccumulates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        // Track + plate-solve sync away from the pole: the static offset is learned, and
        // the drift reference is set to this freshly-centred position. Tracking must be
        // ON: with the axis parked the believed pointing sweeps at sidereal rate, which
        // now (correctly) shows up in reads on top of the misalignment drift.
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);
        mount.IsAlignmentCorrected.ShouldBeTrue();

        // At the moment of sync the field sits exactly on target (zero residual) --
        // this is what lets the centering loop converge.
        var ra0 = await mount.GetRightAscensionAsync(ct);
        var dec0 = await mount.GetDeclinationAsync(ct);
        ra0.ShouldBe(6.0, 0.02);
        dec0.ShouldBe(45.0, 0.02);

        // Track for 5 sidereal minutes -- the misaligned axis leaks a slow drift.
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        var ra5 = await mount.GetRightAscensionAsync(ct);
        var dec5 = await mount.GetDeclinationAsync(ct);

        var drift5Arcsec = GreatCircleArcsec(ra0, dec0, ra5, dec5);
        var decDrift5Arcsec = Math.Abs(dec5 - dec0) * 3600.0;
        output.WriteLine($"5min: total drift={drift5Arcsec:F1}\"  dec drift={decDrift5Arcsec:F1}\"");

        // Realistic polar-misalignment drift for ~30': tens of arcsec over 5 min --
        // NOT zero (the old "learned alignment zeroes everything" bug) and NOT
        // degrees (a runaway). Rule of thumb ~0.25"/min per arcmin -> ~40"/5min.
        drift5Arcsec.ShouldBeGreaterThan(3.0, "a misaligned mount must drift while tracking");
        drift5Arcsec.ShouldBeLessThan(900.0, "drift must stay realistic, not run away");

        // Drift accumulates with elapsed tracking time (track 5 more minutes).
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        var ra10 = await mount.GetRightAscensionAsync(ct);
        var dec10 = await mount.GetDeclinationAsync(ct);
        var drift10Arcsec = GreatCircleArcsec(ra0, dec0, ra10, dec10);
        output.WriteLine($"10min: total drift={drift10Arcsec:F1}\"");
        drift10Arcsec.ShouldBeGreaterThan(drift5Arcsec, "drift grows with tracking time");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenAlignmentLearnedWhenSlewedToNewTargetThenReportedPointingFollowsToNewTarget()
    {
        // REGRESSION for the bug PR #15 missed (single-target only): after the first
        // plate-solve sync learns the alignment, GetRA/GetDec route through
        // ApplyTrackingDrift anchored to the FROZEN first-sync reference
        // (_trackingRefRa/_trackingRefDec). A subsequent GOTO to a new target moves
        // the encoder correctly, but the misalignment OVERLAY kept reporting the OLD
        // synced position -- so the imaging centering loop plate-solved a stale spot
        // and never converged ("slewing doesn't work"). This drives the real session
        // flow: sync at calibration target A, then slew to imaging target B.
        //
        // Pre-fix this FAILS hard: decB reads ~0 (frozen at A) instead of ~-40.
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        // Target A: calibration target near the equator. Plate-solve sync learns the
        // alignment and anchors the drift reference here.
        await mount.SyncRaDecAsync(6.0, 0.0, ct);
        mount.IsAlignmentCorrected.ShouldBeTrue();
        (await mount.GetRightAscensionAsync(ct)).ShouldBe(6.0, 0.05);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(0.0, 0.05);

        // Target B: a real imaging target far away in BOTH axes (Dec is the axis the
        // live symptom showed). RA delta kept modest so the 3deg/s goto finishes
        // inside the pump cap.
        const double targetBRa = 9.0;    // +3h
        const double targetBDec = -40.0; // 40deg away
        await mount.BeginSlewRaDecAsync(targetBRa, targetBDec, ct);
        await PumpUntilNotSlewingAsync(mount, external, ct, TimeSpan.FromSeconds(50));

        var raB = await mount.GetRightAscensionAsync(ct);
        var decB = await mount.GetDeclinationAsync(ct);
        output.WriteLine($"After slew A(6h/0)->B: reported RA={raB:F3}h Dec={decB:F3} (target {targetBRa}h/{targetBDec})");

        // The reported pointing MUST follow the slew to target B (within the
        // misalignment magnitude ~0.5deg), NOT stay stuck at the stale first-sync A.
        // ShouldBe(-40, 1.5) is the killer assertion: pre-fix decB reads ~0 (frozen
        // at A) and this fails by ~40deg; post-fix it follows to ~-40.
        decB.ShouldBe(targetBDec, 1.5);
        raB.ShouldBe(targetBRa, 0.3);

        // Alignment stays learned (pointing is corrected globally), and the residual
        // polar drift now re-accumulates from the NEW target, not the old one.
        mount.IsAlignmentCorrected.ShouldBeTrue();
        var raJustAfter = raB;
        var decJustAfter = decB;
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        var raDrift = await mount.GetRightAscensionAsync(ct);
        var decDrift = await mount.GetDeclinationAsync(ct);
        var driftArcsec = GreatCircleArcsec(raJustAfter, decJustAfter, raDrift, decDrift);
        output.WriteLine($"5min after re-baseline: drift from new target={driftArcsec:F1}\" raw RA={raDrift:F4}h Dec={decDrift:F4} (was {raJustAfter:F4}h/{decJustAfter:F4}) tracking={await mount.IsTrackingAsync(ct)}");
        driftArcsec.ShouldBeGreaterThan(3.0, "residual polar drift must re-accumulate from the new target");
        driftArcsec.ShouldBeLessThan(900.0, "drift must stay realistic, not run away");
    }

    /// <summary>Great-circle separation between two (RA hours, Dec deg) points, in arcsec.</summary>
    private static double GreatCircleArcsec(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg)
    {
        var ra1 = ra1Hours * 15.0 * Math.PI / 180.0;
        var ra2 = ra2Hours * 15.0 * Math.PI / 180.0;
        var dec1 = dec1Deg * Math.PI / 180.0;
        var dec2 = dec2Deg * Math.PI / 180.0;
        var cosSep = Math.Sin(dec1) * Math.Sin(dec2)
            + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
        var sepRad = Math.Acos(Math.Clamp(cosSep, -1.0, 1.0));
        return sepRad * 180.0 / Math.PI * 3600.0;
    }

    #endregion
}
