using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    public async Task TakeFlatsAsync_ManualPanel_WritesFlatsWithoutCalibrator()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with
        {
            FlatSource = FlatIlluminationSource.ManualPanel,
            FlatAduTolerance = 1.0,   // any metering frame is "in tolerance" -> Capture on attempt 0
            FlatsPerFilter = 2,
            FlatMaxBrackets = 2,
            FlatInitialExposure = TimeSpan.FromSeconds(1),
        };

        // No cover/calibrator on the OTA: the Calibrator path would skip every OTA here ("no cover/calibrator
        // device"), but the ManualPanel path never gates on one -- it just meters + captures against whatever
        // light is arranged, so it must still write flats.
        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withCoverCalibrator: false, withFilterWheel: true, cancellationToken: ct);

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
}
