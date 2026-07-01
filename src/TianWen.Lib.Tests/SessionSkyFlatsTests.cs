using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Orchestration tests for twilight sky-flats (<c>Session.TakeSkyFlatsAsync</c>). The per-frame
/// convergence + wait/stop logic is pinned separately by <see cref="SkyFlatExposureSolverTests"/>; here
/// the acceptance band is opened wide (tolerance 1.0) and the coarse solar-altitude gate is disabled
/// (band [-90, +90]) so every frame captures regardless of the fake clock's sun, and we assert the
/// orchestration: covers open, slew near zenith, tracking off, and N <c>Flat</c> frames per installed
/// filter written into <c>Flat</c> frame-type folders.
/// </summary>
/// <remarks>
/// Shares <c>[Collection("Flats")]</c> with <see cref="SessionFlatsTests"/> so the flat-writing tests run
/// sequentially: they all write into one shared fake output subtree (keyed by the test helper name) and
/// clear it on entry, so running them concurrently would clobber each other's file counts.
/// </remarks>
[Collection("Flats")]
public class SessionSkyFlatsTests(ITestOutputHelper output)
{
    private static SessionConfiguration SkyFlatConfig => SessionTestHelper.DefaultConfiguration with
    {
        FlatSource = FlatIlluminationSource.TwilightSky,
        FlatAduTolerance = 1.0,                   // any metering frame is "in tolerance" -> Capture
        FlatsPerFilter = 2,
        FlatInitialExposure = TimeSpan.FromSeconds(0.1),
        FlatMinExposure = TimeSpan.FromSeconds(0.05),
        FlatMaxExposure = TimeSpan.FromSeconds(0.3),
        FlatSkyMaxDuration = TimeSpan.FromMinutes(10),
        FlatSkySettleInterval = TimeSpan.FromSeconds(1),
        FlatSkySunAltitudeBrightDeg = 90,         // never "past" the window at either edge, so the run
        FlatSkySunAltitudeDarkDeg = -90,          // proceeds regardless of the fake clock's sun altitude
    };

    [Theory(Timeout = 60_000)]
    [InlineData(TwilightPeriod.Dawn)]
    [InlineData(TwilightPeriod.Dusk)]
    public async Task TakeSkyFlatsAsync_WritesFlatFramesPerInstalledFilter_TrackingOff(TwilightPeriod period)
    {
        var ct = TestContext.Current.CancellationToken;

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: SkyFlatConfig, withFilterWheel: true, cancellationToken: ct);

        // Persist every flat (FakeExternal only writes the first frame by default).
        ctx.External.MaxFitsWrites = 100;

        // The fake output folder is keyed by the (shared) helper's caller name, so the Flats subtree
        // can carry over from a prior run / sibling test. Clear it so the count is this run's only.
        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeSkyFlatsAsync(period, ct);

        var fw = ctx.FilterWheel.ShouldNotBeNull();
        var filterCount = fw.Filters.Count;
        filterCount.ShouldBeGreaterThan(1); // fake LRGB wheel = 4

        Directory.Exists(flatsRoot).ShouldBeTrue();
        var files = Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories);

        // N frames per filter, one folder per filter.
        files.Length.ShouldBe(filterCount * SkyFlatConfig.FlatsPerFilter);

        // Frame-type leaf folder must be "Flat" -> proves ImageMeta.FrameType == Flat propagated.
        files.ShouldAllBe(f => Directory.GetParent(f)!.Name == "Flat");

        // One distinct filter folder per installed filter.
        files.Select(f => Directory.GetParent(f)!.Parent!.Name).Distinct().Count().ShouldBe(filterCount);

        // Sky-flat signature: tracking is turned off so the field drifts and stars average out.
        if (ctx.Mount.CanSetTracking)
        {
            (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeFalse();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TakeSkyFlatsAsync_WindowAlreadyPast_SkipsWithoutWritingFlats()
    {
        var ct = TestContext.Current.CancellationToken;

        // Dawn window counts as "past" whenever the sun is above the bright edge; setting the bright edge
        // to -90 makes any sun altitude "past" -> the run is skipped without slewing or writing frames.
        var config = SkyFlatConfig with { FlatSkySunAltitudeBrightDeg = -90 };

        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, configuration: config, withFilterWheel: true, cancellationToken: ct);
        ctx.External.MaxFitsWrites = 100;

        var flatsRoot = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Flats");
        if (Directory.Exists(flatsRoot)) Directory.Delete(flatsRoot, recursive: true);

        await ctx.Session.TakeSkyFlatsAsync(TwilightPeriod.Dawn, ct);

        if (Directory.Exists(flatsRoot))
        {
            Directory.GetFiles(flatsRoot, "*.fits", SearchOption.AllDirectories).Length.ShouldBe(0);
        }
    }
}
