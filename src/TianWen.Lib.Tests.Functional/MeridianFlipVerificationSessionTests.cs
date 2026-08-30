using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Tests;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// P2 of the meridian-flip verification plan (docs/plans/meridian-flip-verification.md), through the
/// session: the flip is read off the frame, not off the mount.
/// <para>
/// The rig is the one the plan is about -- a GEM whose driver COMPUTES its pointing state from the
/// hour angle, the family the LX200 base driver and SGP belong to. The moment the POINTING crosses
/// the meridian such a mount begins reporting the flipped state, so the imaging loop sees a
/// pier-side change, concludes the firmware auto-flipped, skips the slew and carries on. The tube
/// never moved. Every frame after that is upside down and the guider's Dec sense is inverted, and
/// nothing in the run says so.
/// </para>
/// <para>
/// Driven through the REAL <see cref="CatalogPlateSolver"/>, so the whole chain is exercised: mount
/// mechanical state -&gt; the instrument's roll -&gt; a genuine CD matrix recovered from pixels -&gt;
/// the session's flip verdict -&gt; what the session does about it. This used to stub the
/// pixels-to-WCS step, on the belief that the solver could not handle a synthetic field; measurement
/// showed the fake's projection is exact and the frame was simply too SMALL, which
/// <c>withCatalogStarField</c> fixes.
/// </para>
/// </summary>
[Collection("Session")]
public class MeridianFlipVerificationSessionTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset WinterNightStart = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);
    // An hour EAST of the meridian at the session start (LST ~ 4.74h from Vienna at this epoch), so
    // the tube that points at it is through the pole and two hours of tracking carry it across.
    private static readonly Target FlipTarget = new(5.74, 20.0, "FlipTarget", null);

    private static ScheduledObservation[] AcrossMeridianObservation() =>
    [
        new ScheduledObservation(
            FlipTarget,
            WinterNightStart,
            TimeSpan.FromMinutes(30),
            AcrossMeridian: true,
            FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(30)),
            Gain: 0,
            Offset: 0)
    ];

    /// <summary>
    /// A session on the plain (computed-pointing-state) fake mount, whose solves describe the field
    /// the camera rendered, sitting an hour east of the meridian with one centring solve behind it --
    /// the "before" any flip check compares against.
    /// </summary>
    private async Task<SessionTestContext> ArrangeEastOfMeridianAsync(CancellationToken ct)
    {
        // The REAL solver, against the field the fake actually renders. withCatalogStarField is what
        // makes that possible: it hands FakeExternal a loaded catalog (so the camera projects Tycho-2
        // stars instead of falling back to a random field) and widens the ROI to 2048, because at the
        // helper's default 512 the frame spans 0.28 deg and holds 2-7 catalog stars against the
        // solver's MinStarsForMatch of 6. Both measured in FakeFieldSolveProbe.
        var solverLogger = LoggerFactory
            .Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new XUnitLoggerProvider(output, false)))
            .CreateLogger<CatalogPlateSolver>();
        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, SessionTestHelper.DefaultConfiguration, AcrossMeridianObservation(),
            now: WinterNightStart, focalLength: 480, mountPort: null,
            plateSolverOverride: new CatalogPlateSolver(await SharedCatalogDB.InitAsync(ct), solverLogger),
            coupleCameraToMount: true, withCatalogStarField: true, cancellationToken: ct);

        ctx.Camera.TrueBestFocus = 1000;
        ctx.Camera.FocusPosition = 1000;
        // What Session.InitialisationAsync would denormalise onto the camera from the OTA. These
        // tests drive one flip rather than a whole run, so they state the optics themselves; without
        // a focal length the camera renders no star field and has no roll to report.
        ctx.Camera.FocalLength = 480;
        // And what ImagingLoopAsync sets before it exposes on a target (Session.Imaging.cs:60). The
        // fake renders CATALOG stars only when it knows where the OTA points, and for the main camera
        // that is Target; without it the render silently falls back to a random field, which no solver
        // can match against a real catalog.
        ctx.Camera.Target = FlipTarget;

        ctx.Session.AdvanceObservationForTest();

        // Put the tube on the target by SLEWING, not syncing: a goto is the only thing in ordinary
        // operation that moves a tube across the pier, so it is the only way to arrange one that is
        // genuinely through the pole. (The clock auto-advances here, so the slew completes.)
        var mount = ctx.Mount;
        await mount.SetTrackingAsync(true, ct);
        await mount.BeginSlewRaDecAsync(FlipTarget.RA, FlipTarget.Dec, ct);
        for (var i = 0; i < 600 && await mount.IsSlewingAsync(ct); i++)
        {
            // Poll through the SESSION, which is what every real slew wait does: the poll is where the
            // canonical pier side latches the value the goto landed on.
            await ctx.Session.PollDeviceStatesAsync(ct);
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
        }
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse("the slew onto the target must complete");
        await ctx.Session.PollDeviceStatesAsync(ct);
        (await mount.GetHourAngleAsync(ct)).ShouldBeLessThan(0.0, "the target must start east of the meridian");
        (await ((IFakeMechanicalPointingStateSource)mount).GetMechanicalPointingStateAsync(ct))
            .ShouldBe(PointingState.ThroughThePole, "east of the meridian a GEM looks through the pole");

        var centred = await ctx.Session.CenterOnTargetAsync(FlipTarget, 0, thresholdArcmin: 60.0, maxAttempts: 2, ct);

        // Assert the PREMISE before the outcome. A random star field cannot plate-solve against a real
        // catalog by construction, so without this a broken fake binding reads as a solver failure --
        // which is exactly how it was misdiagnosed once already.
        ctx.Camera.LastCatalogRenderCentre.ShouldNotBeNull(
            "the camera must render a CATALOG field; the random fallback is unsolvable by construction");

        centred
            .ShouldBeTrue("the run needs a solved field behind it, or there is nothing to compare against");
        ctx.Session.PlateSolveHistory.Any(r => r is { Succeeded: true, Solution.HasCDMatrix: true })
            .ShouldBeTrue("and that solve must carry an orientation, or the check under test is inert");

        return ctx;
    }

    /// <summary>
    /// A mount reporting an auto-flip it did not perform must not be believed. The session takes the
    /// pier-side change at face value (<c>alreadyFlipped: true</c>), recentres, finds the field
    /// exactly where it was, and commands the flip rather than imaging on from the wrong side.
    /// <para>
    /// The discriminator is the mount's own MECHANICAL state -- the thing the report was lying about.
    /// Under the behaviour this replaces, the call returned success with the tube untouched, which is
    /// the entire defect and is the assertion that fails without the fix.
    /// </para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task GivenAMountReportingAFlipItDidNotPerformWhenTheFieldDidNotRotateThenTheFlipIsCommanded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await ArrangeEastOfMeridianAsync(ct);
        var mount = ctx.Mount;
        var mechanical = (IFakeMechanicalPointingStateSource)mount;

        // Track past the meridian. Nothing is commanded; the tube does not move.
        ctx.TimeProvider.Advance(TimeSpan.FromHours(2));
        (await mount.GetHourAngleAsync(ct)).ShouldBeGreaterThan(0.0);
        mount.PointingStateSource.ShouldBe(PointingStateSource.Computed);
        (await mount.GetSideOfPierAsync(ct)).ShouldBe(PointingState.Normal,
            "the mount now REPORTS the flipped state");
        (await mechanical.GetMechanicalPointingStateAsync(ct)).ShouldBe(PointingState.ThroughThePole,
            "while the tube is still where its last goto left it -- the premise of this whole test");

        // What the imaging loop does when it sees that pier-side change: believe it.
        var result = await ctx.Session.PerformMeridianFlipAsync(
            ctx.Session.ActiveObservation!, alreadyFlipped: true, ct);

        output.WriteLine(
            $"verdict {result.Verdict.Evidence} (field turned {result.Verdict.RotationDeltaDeg:F2} deg); success {result.Success}");

        result.Success.ShouldBeTrue("the flip must end up performed, not abandoned");
        (await mechanical.GetMechanicalPointingStateAsync(ct)).ShouldBe(PointingState.Normal,
            "the session must have COMMANDED the flip the mount only claimed; believing the report "
            + "leaves the tube through the pole and the run imaging upside down");
        result.Verdict.Evidence.ShouldBe(FlipEvidence.Flipped,
            "and the frame it finished on must show the rotated field");
    }

    /// <summary>
    /// The complement: a real auto-flip must still be recognised and accepted as it stands, or the
    /// check would have traded one wrong answer for another and re-flipped every rig whose firmware
    /// does its own.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task GivenARealAutoFlipWhenTheFieldRotatedThenItIsAcceptedAsIs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await ArrangeEastOfMeridianAsync(ct);
        var mount = ctx.Mount;
        var mechanical = (IFakeMechanicalPointingStateSource)mount;

        ctx.TimeProvider.Advance(TimeSpan.FromHours(2));

        // This time the firmware really does take the tube over.
        await mount.SetSideOfPierAsync(PointingState.Normal, ct);
        (await mechanical.GetMechanicalPointingStateAsync(ct)).ShouldBe(PointingState.Normal);

        var result = await ctx.Session.PerformMeridianFlipAsync(
            ctx.Session.ActiveObservation!, alreadyFlipped: true, ct);

        output.WriteLine(
            $"verdict {result.Verdict.Evidence} (field turned {result.Verdict.RotationDeltaDeg:F2} deg); success {result.Success}");

        result.Success.ShouldBeTrue();
        result.Verdict.Evidence.ShouldBe(FlipEvidence.Flipped,
            "a genuine auto-flip must be read off the image and accepted, not re-commanded");
    }

    /// <summary>
    /// The canonical pier side (<c>Session.GetSideOfPierAsync</c>) must hold what the last goto landed
    /// on, while the MOUNT's own report drifts. That difference is the entire failure this plan is
    /// about, reduced to two reads: a computed-state driver turns its answer over as the POINTING
    /// crosses the meridian, and everything that believed it -- the imaging loop reading an auto-flip
    /// that never happened, the guider reversing a calibration and inverting the sense that keeps it
    /// converging -- was wrong downstream of that one drift.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheCanonicalPierSideHoldsWhereTheMountsOwnReportDrifts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await ArrangeEastOfMeridianAsync(ct);
        var mount = ctx.Mount;

        mount.PointingStateSource.ShouldBe(PointingStateSource.Computed);
        (await ctx.Session.GetSideOfPierAsync(ct)).ShouldBe(PointingState.ThroughThePole,
            "the goto landed through the pole, and the poll latched it");

        // Track past the meridian. Nothing slews; the tube does not move.
        ctx.TimeProvider.Advance(TimeSpan.FromHours(2));
        await ctx.Session.PollDeviceStatesAsync(ct);

        (await mount.GetSideOfPierAsync(ct)).ShouldBe(PointingState.Normal,
            "the driver's own answer turns over as the pointing crosses -- this is the drift");
        (await ctx.Session.GetSideOfPierAsync(ct)).ShouldBe(PointingState.ThroughThePole,
            "the canonical answer must NOT: no slew, so the tube is where the goto left it");
        (await ((IFakeMechanicalPointingStateSource)mount).GetMechanicalPointingStateAsync(ct))
            .ShouldBe(PointingState.ThroughThePole, "and the canonical answer is the one that matches the tube");
    }

}
