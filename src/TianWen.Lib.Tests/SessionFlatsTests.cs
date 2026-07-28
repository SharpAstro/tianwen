using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Orchestration tests for <c>Session.TakeFlatsAsync</c> (panel/calibrator flats). The exposure
/// convergence math itself is pinned separately by <see cref="FlatExposureSolverTests"/>; here the
/// acceptance band is opened wide (tolerance 1.0) so the first metering frame always converges, and
/// we assert the orchestration: cover closed, calibrator on then off, every installed filter cycled,
/// and N <c>FrameType.Flat</c> frames written per filter.
/// </summary>
/// <remarks>
/// Shares <c>[Collection("Flats")]</c> with <see cref="SessionSkyFlatsTests"/> so the flat-writing tests
/// run sequentially: they all write into one shared fake output subtree (keyed by the test helper name)
/// and clear it on entry, so running them concurrently would clobber each other's file counts.
/// </remarks>
[Collection("Flats")]
public class SessionFlatsTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_PanelFlats_WritesFlatFramesPerInstalledFilter()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatAduTolerance = 1.0,   // any metering frame is "in tolerance" -> Capture on attempt 0
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
            FlatCalibratorBrightnessPercent = 50,
        };

        // NOTE: no PromptRequested subscriber, and a DRIVER-CONTROLLED calibrator. That combination is
        // load-bearing beyond this test's own subject: it is the ordinary unattended rig, and it proves
        // SessionConfiguration.UnattendedPromptResponse (default Decline) never reaches the non-prompting
        // path. Widen the prompt gate in Session.Flats.cs and this test goes red -- verified by doing it.
        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withCoverCalibrator: true, withFilterWheel: true, cancellationToken: ct);

        // Persist every flat (FakeExternal only writes the first frame by default).
        ctx.External.MaxFitsWrites = 100;

        // The fake output folder is keyed by the (shared) helper's caller name, so the Flats subtree
        // can carry over from a prior run / sibling test. Clear it so the count is this run's only.
        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        var fw = ctx.FilterWheel.ShouldNotBeNull();
        var filterCount = fw.Filters.Count;
        filterCount.ShouldBeGreaterThan(1); // fake LRGB wheel = 4

        Directory.Exists(flatsRoot).ShouldBeTrue();
        var files = Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories);

        // N frames per filter, one folder per filter.
        files.Length.ShouldBe(filterCount * config.FlatsPerFilter);

        // Frame-type leaf folder must be "Flat" -> proves ImageMeta.FrameType == Flat propagated to the path/headers.
        files.ShouldAllBe(f => Directory.GetParent(f)!.Name == "Flat");

        // One distinct filter folder (parent of the Flat folder) per installed filter.
        files.Select(f => Directory.GetParent(f)!.Parent!.Name).Distinct().Count().ShouldBe(filterCount);

        // Calibrator was turned off again, and the cover left closed.
        var cover = ctx.Cover.ShouldNotBeNull();
        (await cover.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Off);
        (await cover.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Closed);
    }

    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_ManualCover_WritesFlatsViaCalibratorPath()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with
        {
            // A manual panel is now a device (ManualCoverDevice), captured through the SAME Calibrator path
            // as a flip-flat -- no ManualPanel source, no session branching.
            FlatSource = FlatIlluminationSource.Calibrator,
            FlatAduTolerance = 1.0,   // any metering frame is "in tolerance" -> Capture on attempt 0
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
            // This test is about the CAPTURE path, and its premise (below) is that the user already turned
            // the panel on. With no prompt subscriber that premise has to be stated: the unattended default
            // is now Decline, which would skip the OTA and make this test about prompt policy instead. The
            // policy itself is pinned separately by the two NobodySubscribed tests.
            UnattendedPromptResponse = UnattendedPromptResponse.Proceed,
        };

        // A ManualCoverDevice assigned to the OTA cover slot: it reports no flap (CoverStatus.NotPresent) and
        // the calibrator Ready on demand, so the ordinary Calibrator path drives it and writes flats -- exactly
        // like a real hand-switched analog panel the user turned on.
        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withManualCover: true, withFilterWheel: true, cancellationToken: ct);

        ctx.External.MaxFitsWrites = 100;

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        var fw = ctx.FilterWheel.ShouldNotBeNull();
        var filterCount = fw.Filters.Count;
        filterCount.ShouldBeGreaterThan(1); // fake LRGB wheel = 4

        Directory.Exists(flatsRoot).ShouldBeTrue();
        var files = Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories);

        // N frames per filter, one folder per filter -- identical output contract to the calibrator path.
        files.Length.ShouldBe(filterCount * config.FlatsPerFilter);
        files.ShouldAllBe(f => Directory.GetParent(f)!.Name == "Flat");
        files.Select(f => Directory.GetParent(f)!.Parent!.Name).Distinct().Count().ShouldBe(filterCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task RunFlatsOnlyAsync_Calibrator_ConnectsCapturesAndFinalises()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.Calibrator,
            // Setpoint == the fake camera's 20 C ambient so the on-demand cool-to-setpoint is an immediate
            // no-op (the fake cools 1 C per read), and skip the warm ramp -- keeps the connect/cool/finalise
            // cycle deterministic + fast under FakeTimeProvider without exercising the (separately-tested) ramp.
            SetpointCCDTemperature = new SetpointTemp(20, SetpointTempKind.Normal),
            WarmCamerasOnSessionEnd = false,
            FlatAduTolerance = 1.0,
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
            FlatCalibratorBrightnessPercent = 50,
        };

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withCoverCalibrator: true, withFilterWheel: true, cancellationToken: ct);

        ctx.External.MaxFitsWrites = 100;

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        // Full on-demand cycle: connect the flat devices -> cool -> capture -> finalise (warm/close/disconnect).
        await ctx.Session.RunFlatsOnlyAsync(TwilightPeriod.Dusk, ct);

        ctx.Session.Phase.ShouldBe(SessionPhase.Complete);

        var fw = ctx.FilterWheel.ShouldNotBeNull();
        var filterCount = fw.Filters.Count;

        Directory.Exists(flatsRoot).ShouldBeTrue();
        var files = Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories);
        files.Length.ShouldBe(filterCount * config.FlatsPerFilter);
        files.ShouldAllBe(f => Directory.GetParent(f)!.Name == "Flat");
    }

    [Fact(Timeout = 30_000)]
    public async Task TakeFlatsAsync_NoCalibrator_SkipsWithoutWritingFlats()
    {
        var ct = TestContext.Current.CancellationToken;

        // Default helper wires no cover/calibrator on the OTA.
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        // Clear any carried-over Flats subtree (shared fake output folder, see the other test).
        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        if (Directory.Exists(flatsRoot))
        {
            Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length.ShouldBe(0);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TakeFlatsAsync_CoverConnectFailure_SkipsOtaInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;

        // A cover whose connect throws (realistic for a serial panel: port unplugged/busy, identity
        // mismatch) must be skipped like any other missing-precondition OTA -- an escaping exception
        // here would fail the WHOLE session from the end-of-session flats hook.
        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, withFilterWheel: true, coverFactory: sp => new Cover(new BrokenCoverDevice(), sp), cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        if (Directory.Exists(flatsRoot))
        {
            Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length.ShouldBe(0);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_ManualCover_PromptContinue_CapturesFlats()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.Calibrator,
            FlatAduTolerance = 1.0,
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
        };

        // A manual panel can't be dimmed by the driver (CanControlBrightness == false), so the flat routine
        // pauses for a user prompt before capturing. An interactive handler that answers Continue lets it proceed.
        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withManualCover: true, withFilterWheel: true, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        var prompts = 0;
        DateTimeOffset? raisedUtc = null;
        ctx.Session.PromptRequested += (_, e) =>
        {
            prompts++;
            raisedUtc = e.RaisedUtc;
            e.Respond(true);
        };

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        prompts.ShouldBe(1); // one OTA with a manual panel -> exactly one prompt
        // Stamped by the session, from the session's clock. An observer cannot work this out for itself,
        // and without it a board of rigs can show that a prompt is outstanding but not that it has been
        // outstanding for forty minutes -- which is the part that makes it worth showing.
        raisedUtc.ShouldNotBeNull();
        var filterCount = ctx.FilterWheel.ShouldNotBeNull().Filters.Count;
        Directory.Exists(flatsRoot).ShouldBeTrue();
        Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length
            .ShouldBe(filterCount * config.FlatsPerFilter);
    }

    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_ManualCover_NobodySubscribed_SkipsRatherThanAssumingThePanelIsOn()
    {
        var ct = TestContext.Current.CancellationToken;

        // The unattended default. Answering "proceed" here would assert that a human switched on a
        // hand-switched panel when demonstrably nobody was asked -- so the gated step is skipped instead.
        // Missing calibration is recoverable; silently wrong calibration is not (and the planned
        // dark-frame prompt has no exposure solver to catch it the way flats do).
        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.Calibrator,
            FlatAduTolerance = 1.0,
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
        };
        config.UnattendedPromptResponse.ShouldBe(UnattendedPromptResponse.Decline, "the safe default must not drift");

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withManualCover: true, withFilterWheel: true, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        // Deliberately NO PromptRequested subscriber -- this is the headless CLI / server shape.
        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        if (Directory.Exists(flatsRoot))
        {
            Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length.ShouldBe(0);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_ManualCover_NobodySubscribedButOperatorInvoked_Proceeds()
    {
        var ct = TestContext.Current.CancellationToken;

        // The workflow the Decline default must not break: an operator runs `tianwen flats` (or POSTs
        // /session/flats), walks out, switches the panel on, and comes back. Those entry points opt into
        // Proceed precisely because a human DID act, even though no handler is subscribed to say so.
        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.Calibrator,
            FlatAduTolerance = 1.0,
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
            UnattendedPromptResponse = UnattendedPromptResponse.Proceed,
        };

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withManualCover: true, withFilterWheel: true, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        var filterCount = ctx.FilterWheel.ShouldNotBeNull().Filters.Count;
        Directory.Exists(flatsRoot).ShouldBeTrue();
        Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length
            .ShouldBe(filterCount * config.FlatsPerFilter);
    }

    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_ManualCover_OperatorNeverSwitchedThePanelOn_WritesNothingAndFinishesCleanly()
    {
        var ct = TestContext.Current.CancellationToken;

        // MIMICS THE OPERATOR WHO DID NOT FLIP THE SWITCH -- the case the whole prompt policy is argued
        // around. A ManualCoverDriver reports the calibrator Ready regardless (it cannot see an analog
        // panel), so "the panel is off" is only ever observable downstream, as a frame that cannot reach
        // the target level however long the exposure. That is modelled here by a max exposure the fake
        // sensor cannot possibly fill to 50% -- FlatExposureSolver then returns Fail ("panel too dim at
        // max"), exactly as it would against a dark panel.
        //
        // Why this test earns its place: the claim that proceeding on an unlit panel is *survivable*
        // rests on this degradation, and only half of it was pinned. The solver's Fail is covered by
        // FlatExposureSolverTests.TooDimAtMaxExposure_Fails_PanelTooDim; what nothing covered is the
        // ORCHESTRATION half -- that a Fail skips the OTA, writes no frames, and lets the run finish so
        // Finalise still parks the mount and closes the covers. If TakeFlatsForOtaAsync ever threw on
        // Fail, or wrote its metering frames anyway, the argument would collapse silently.
        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.Calibrator,
            FlatTargetAduFraction = 0.5,
            FlatAduTolerance = 0.01,                          // a real acceptance band, not the wide-open 1.0
            FlatsPerFilter = 2,
            FlatMaxBrackets = 3,
            FlatInitialExposure = TimeSpan.FromMilliseconds(1),
            FlatMinExposure = TimeSpan.FromMilliseconds(1),
            FlatMaxExposure = TimeSpan.FromMilliseconds(1),   // nowhere left to go -> "too dim at max"
            // The operator-invoked policy, so the run proceeds past the prompt and actually reaches the
            // metering. With Decline it would skip earlier and prove nothing about the solver path.
            UnattendedPromptResponse = UnattendedPromptResponse.Proceed,
        };

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withManualCover: true, withFilterWheel: true, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        // Must not throw: a flat block that fails is best-effort, never a reason to fail the night.
        await ctx.Session.TakeFlatsAsync(ct);

        // No frames -- crucially the DISCARDED metering exposures must not be mistaken for flats.
        if (Directory.Exists(flatsRoot))
        {
            Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length.ShouldBe(0);
        }

        // And the panel is left off, so the run is tidy for whatever comes next.
        (await ctx.Cover.ShouldNotBeNull().GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Off);
    }

    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_ManualCover_PromptCancel_SkipsOtaWithoutWritingFlats()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.Calibrator,
            FlatAduTolerance = 1.0,
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
        };

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withManualCover: true, withFilterWheel: true, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        var prompts = 0;
        // Declining the prompt (the user isn't ready / can't light the panel) skips the OTA -- it must not
        // write flats and must not throw or abort.
        ctx.Session.PromptRequested += (_, e) =>
        {
            prompts++;
            e.Respond(false);
        };

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        prompts.ShouldBe(1);
        if (Directory.Exists(flatsRoot))
        {
            Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length.ShouldBe(0);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task TakeFlatsAsync_ControllablePanel_DoesNotPrompt()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.Calibrator,
            FlatAduTolerance = 1.0,
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
        };

        // A driver-controlled calibrator (CanControlBrightness == true) sets its own level -> no prompt.
        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withCoverCalibrator: true, withFilterWheel: true, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        var prompts = 0;
        ctx.Session.PromptRequested += (_, e) => { prompts++; e.Respond(true); };

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeFlatsAsync(ct);

        prompts.ShouldBe(0);
        var filterCount = ctx.FilterWheel.ShouldNotBeNull().Filters.Count;
        Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length
            .ShouldBe(filterCount * config.FlatsPerFilter);
    }
}