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
/// A solver that reports the orientation the fake camera actually rendered its field at, plus the
/// frame's own centre. It stands in for the one step of the chain this suite does not exercise --
/// recovering a CD matrix from pixels -- so the rest of it can be driven end to end: mount
/// mechanical state -&gt; the instrument's roll -&gt; a solve that describes it -&gt; the session's
/// flip verdict -&gt; what the session then does about it.
/// <para>
/// That step is stubbed deliberately, not for convenience. The real <c>CatalogPlateSolver</c> cannot
/// currently lock onto a <see cref="FakeCameraDriver"/> synthetic field: the render places far fewer
/// stars than the solver draws catalog anchors for (43 detected against 160 anchors on a one-degree
/// field), so every solve is refused by its acceptance gate. That is a gap in the fake's
/// star-density model, not in the flip logic, and it is recorded in
/// <c>docs/plans/meridian-flip-verification.md</c>. The pixels-to-angle half is pinned separately by
/// <c>WcsRotationTests</c> (the CD matrix maths) and <c>VelaMosaicFieldTests</c> (the solver, on real
/// fields).
/// </para>
/// </summary>
internal sealed class RenderedFieldPlateSolver(Func<FakeCameraDriver> camera) : IPlateSolver
{
    private const double PixelScaleDeg = 1.0 / 3600.0;

    public string Name => "Rendered-field plate solver";

    public float Priority => 1.0f;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("this stand-in solves frames, not files");

    public Task<PlateSolveResult> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Where the frame is centred, which is what a real solve recovers and what the session syncs
        // the mount to: the pointing the frame was taken at, never the target.
        var (centreRa, centreDec) = !double.IsNaN(image.ImageMeta.TargetRA) && !double.IsNaN(image.ImageMeta.TargetDec)
            ? (image.ImageMeta.TargetRA, image.ImageMeta.TargetDec)
            : searchOrigin is { } origin ? (origin.CenterRA, origin.CenterDec) : (0.0, 0.0);

        // And how it lies: the roll the camera rendered this frame at, which on a coupled fake mount
        // already carries the instrument's half-turn whenever the tube is through the pole.
        var (sin, cos) = Math.SinCos(double.DegreesToRadians(camera().LastRenderRotationDeg));
        var wcs = new WCS(centreRa, centreDec)
        {
            CRPix1 = (image.Width + 1) / 2.0,
            CRPix2 = (image.Height + 1) / 2.0,
            CD1_1 = -PixelScaleDeg * cos,
            CD2_1 = PixelScaleDeg * sin,
            CD1_2 = PixelScaleDeg * sin,
            CD2_2 = PixelScaleDeg * cos
        };
        return Task.FromResult(new PlateSolveResult(wcs, sw.Elapsed));
    }
}

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
        // The camera does not exist until CreateSessionAsync has built it, and the solver is one of
        // its arguments, so the solver reaches it through a closure over the context.
        SessionTestContext? built = null;
        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, SessionTestHelper.DefaultConfiguration, AcrossMeridianObservation(),
            now: WinterNightStart, focalLength: 480, mountPort: null,
            plateSolverOverride: new RenderedFieldPlateSolver(() => built!.Camera),
            coupleCameraToMount: true, cancellationToken: ct);
        built = ctx;

        ctx.Camera.TrueBestFocus = 1000;
        ctx.Camera.FocusPosition = 1000;
        // What Session.InitialisationAsync would denormalise onto the camera from the OTA. These
        // tests drive one flip rather than a whole run, so they state the optics themselves; without
        // a focal length the camera renders no star field and has no roll to report.
        ctx.Camera.FocalLength = 480;

        ctx.Session.AdvanceObservationForTest();

        // Put the tube on the target by SLEWING, not syncing: a goto is the only thing in ordinary
        // operation that moves a tube across the pier, so it is the only way to arrange one that is
        // genuinely through the pole. (The clock auto-advances here, so the slew completes.)
        var mount = ctx.Mount;
        await mount.SetTrackingAsync(true, ct);
        await mount.BeginSlewRaDecAsync(FlipTarget.RA, FlipTarget.Dec, ct);
        for (var i = 0; i < 600 && await mount.IsSlewingAsync(ct); i++)
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
        }
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse("the slew onto the target must complete");
        (await mount.GetHourAngleAsync(ct)).ShouldBeLessThan(0.0, "the target must start east of the meridian");
        (await ((IFakeMechanicalPointingStateSource)mount).GetMechanicalPointingStateAsync(ct))
            .ShouldBe(PointingState.ThroughThePole, "east of the meridian a GEM looks through the pole");

        (await ctx.Session.CenterOnTargetAsync(FlipTarget, 0, thresholdArcmin: 60.0, maxAttempts: 2, ct))
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
}
