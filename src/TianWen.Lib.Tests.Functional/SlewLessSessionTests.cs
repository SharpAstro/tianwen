using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Tests;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A session on a mount that cannot goto, the fake iOptron SkyGuider Pro (docs/plans/slew-less-mount.md, P1): the session
/// moves RA with the mount's own motor and leaves Dec to the operator, with a real plate solve against the catalog field the
/// fake camera renders at the rig's TRUE pointing between every step.
/// </summary>
[Collection("Session")]
public class SlewLessSessionTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset WinterNightStart = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);
    private static readonly Target M42 = new(5.588, -5.391, "M42", null);

    private async Task<(SessionTestContext Ctx, FakeSgpMountDriver Mount)> ArrangeRigAsync(
        double placedRaJ2000, double placedDecJ2000, double moveRateError, CancellationToken ct)
    {
        var solverLogger = LoggerFactory
            .Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new XUnitLoggerProvider(output, false)))
            .CreateLogger<CatalogPlateSolver>();
        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, SessionTestHelper.DefaultConfiguration, observations: null,
            now: WinterNightStart, focalLength: 480, mountPort: "SGP",
            plateSolverOverride: new CatalogPlateSolver(await SharedCatalogDB.InitAsync(ct), solverLogger),
            coupleCameraToMount: true, withCatalogStarField: true, cancellationToken: ct);

        ctx.Camera.TrueBestFocus = 1000;
        ctx.Camera.FocusPosition = 1000;
        ctx.Camera.FocalLength = 480;
        ctx.Camera.Target = M42;

        var mount = ctx.Mount.ShouldBeOfType<FakeSgpMountDriver>();
        await mount.SetSiteLatitudeAsync(48.2, ct);
        await mount.SetSiteLongitudeAsync(16.3, ct);

        // The operator sets the rig up, then solves and syncs it once: the SkyGuider Pro believes it points at the pole
        // until something tells it otherwise, and a solve searches around what the mount believes.
        await mount.PlaceAtJ2000Async(placedRaJ2000, placedDecJ2000, ct);
        await ((IMountDriver)mount).SyncRaDecJ2000Async(placedRaJ2000, placedDecJ2000, ct);
        mount.SerialDevice.ShouldNotBeNull().MoveRateError = moveRateError;
        return (ctx, mount);
    }

    /// <summary>
    /// The operator: when the session asks, turns Dec to the target, read in the mount's native frame as the setting circle
    /// would be, and answers "Solve now".
    /// </summary>
    private static async Task TurnDecWhenAskedAsync(SessionTestContext ctx, FakeSgpMountDriver mount, Target target, Action onPrompt, CancellationToken ct)
    {
        IMountDriver driver = mount;
        var transform = (await driver.TryGetTransformAsync(ct)).ShouldNotBeNull();
        var nativeTargetDec = (await driver.TryTransformJ2000ToMountNativeAsync(transform, target.RA, target.Dec, updateTime: true, ct))
            .ShouldNotBeNull().DecMount;

        ctx.Session.PromptRequested += (_, e) =>
        {
            onPrompt();
            var serial = mount.SerialDevice.ShouldNotBeNull();
            serial.TurnDecByHand(nativeTargetDec - serial.TruePointing.ShouldNotBeNull().Dec);
            e.Respond(true);
        };
    }

    [Fact(Timeout = 300_000)]
    public async Task ARigThatCannotGotoIsCentredWithItsOwnRaMotorAndTheOperatorsDec()
    {
        var ct = TestContext.Current.CancellationToken;

        // An hour of RA west of M42 and five degrees north of it, with every move running 3 % long, so one timed move
        // cannot land it and the session has to solve and move again.
        var (ctx, mount) = await ArrangeRigAsync(placedRaJ2000: 4.588, placedDecJ2000: 0.0, moveRateError: 0.03, ct);
        await using var session = ctx;
        var prompts = 0;
        await TurnDecWhenAskedAsync(ctx, mount, M42, () => prompts++, ct);

        var centred = await ctx.Session.CenterOnTargetAsync(M42, 0, thresholdArcmin: 1.0, maxAttempts: 3, ct);

        ctx.Camera.LastCatalogRenderCentre.ShouldNotBeNull("the camera must render a CATALOG field, or nothing can solve");
        centred.ShouldBeTrue("the session must reach M42 with the RA motor and the operator's Dec");
        prompts.ShouldBe(1, "the operator is asked once, for Dec, and never for RA");
        mount.SerialDevice.ShouldNotBeNull().MovesStarted.ShouldBeGreaterThanOrEqualTo(2,
            "a move 3 % long cannot land the rig, so the session must solve and move RA again");

        // Where the rig TRULY points now, read back in J2000 through the session's own last solve.
        var solve = ctx.Session.PlateSolveHistory.Last(r => r.Succeeded);
        var solution = solve.Solution.ShouldNotBeNull();
        var threshold = ctx.Session.SlewLessThresholdArcmin(0, 1.0);
        (Math.Abs(CoordinateUtils.ConditionHA(solution.CenterRA - M42.RA)) * 15 * 60 * Math.Cos(double.DegreesToRadians(M42.Dec)))
            .ShouldBeLessThanOrEqualTo(threshold, "RA within the arrival threshold");
        (Math.Abs(solution.CenterDec - M42.Dec) * 60).ShouldBeLessThanOrEqualTo(threshold, "Dec within the arrival threshold");
    }

    [Fact(Timeout = 300_000)]
    public async Task ATargetAtTheSameDecIsReachedWithNoQuestionForTheOperator()
    {
        var ct = TestContext.Current.CancellationToken;

        // Already at M42's Dec, half an hour of RA west of it: the panorama case, where only RA moves.
        var (ctx, mount) = await ArrangeRigAsync(placedRaJ2000: 5.088, placedDecJ2000: M42.Dec, moveRateError: 0.03, ct);
        await using var session = ctx;
        var prompts = 0;
        await TurnDecWhenAskedAsync(ctx, mount, M42, () => prompts++, ct);

        var centred = await ctx.Session.CenterOnTargetAsync(M42, 0, thresholdArcmin: 1.0, maxAttempts: 3, ct);

        centred.ShouldBeTrue();
        prompts.ShouldBe(0, "a target at the rig's Dec needs only the RA motor");
        mount.SerialDevice.ShouldNotBeNull().MovesStarted.ShouldBeGreaterThanOrEqualTo(1, "RA moved by the motor");
    }
}
