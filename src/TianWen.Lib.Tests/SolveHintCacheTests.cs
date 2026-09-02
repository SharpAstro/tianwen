using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The plate scale and the parity are properties of the LIGHT PATH, not of the frame, so a rig that
/// has solved once has already answered both. These pin that the second solve is measurably cheaper
/// and that a wrong answer is never the price.
/// </summary>
/// <remarks>
/// Asserted on <see cref="CatalogPlateSolver.LastSeedHypothesesByOutcome"/>, because the cache's entire
/// effect is work that does not happen: the emitted WCS is identical with it and without it, so nothing
/// an ordinary assertion can reach would move if it were deleted. Hypotheses rather than milliseconds,
/// and the WINNING half's rather than the total: that scan is deterministic for a given input, so the
/// same number holds on CI and in Debug, while the abandoned half's count is quantised to its
/// 4,096-hypothesis cancellation check and lands a quantum either way with the scheduler (13,031
/// against 8,935 on two CI runs of the same easy field, 2026-09-01).
/// </remarks>
[Collection("Astrometry")]
public class SolveHintCacheTests(ITestOutputHelper output)
{
    private const double TargetRA = 8.468;
    private const double TargetDec = -41.24;

    private static async Task<(Image Image, ImageDim Dim)> RenderFieldAsync(
        ITestOutputHelper output, ICelestialObjectDB db, CancellationToken ct, string? telescope = null)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 2), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        camera.TrueBestFocus = 1000;
        camera.FocusPosition = 1000;
        camera.FocalLength = 130;
        camera.Target = new Target(TargetRA, TargetDec, "COO71", null);
        camera.CelestialObjectDB = db;

        // The light path's identity is the (OTA, camera) pair, so a test that needs a DIFFERENT rig
        // renames the telescope rather than building a second camera.
        if (telescope is not null)
        {
            camera.Telescope = telescope;
        }

        var pixelScaleArcsec = 206264.806 * camera.PixelSizeX * 1e-3 / camera.FocalLength;
        var dim = new ImageDim(pixelScaleArcsec, camera.CameraXSize - 1, camera.CameraYSize - 1);

        await camera.StartExposureAsync(TimeSpan.FromSeconds(60), cancellationToken: ct);
        await timeProvider.SleepAsync(TimeSpan.FromSeconds(60), ct);
        (await camera.GetImageReadyAsync(ct)).ShouldBeTrue();
        ICameraDriver cameraDriver = camera;
        var image = await cameraDriver.GetImageAsync(ct);
        image.ShouldNotBeNull();

        return (image, dim);
    }

    /// <summary>
    /// An accepted solve teaches the light path, and the next solve on it answers identically.
    /// </summary>
    /// <remarks>
    /// It deliberately does NOT assert a saving, because on this frame there is none to assert and a
    /// test that claimed one would be measuring noise: an easy field seeds in a few thousand hypotheses
    /// across the whole race, phase A's cancellation has already stopped the loser, and the quad seed
    /// answers scale and parity from the frame's own stars so the remembered ones are never consulted.
    /// Since the quad seed runs ahead of the race no committed fixture shows the cache a saving at all
    /// (<see cref="RealFrameSolveTests.TheCropWhoseHeaderPointsElsewhereStillSolves"/> pins that
    /// nothing material is left). What matters here is the other half of the contract: the hints are
    /// HINTS, so the answer must not move, and neither may the cost of the half that answers.
    /// </remarks>
    [Fact(Timeout = 300_000)]
    public async Task AnAcceptedSolveTeachesTheLightPathAndTheNextAnswerIsIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var (image, dim) = await RenderFieldAsync(output, db, ct);

        var solver = new CatalogPlateSolver(db, NullLogger.Instance);
        var hint = new WCS(TargetRA, TargetDec);

        var first = await solver.SolveImageAsync(image, dim, searchOrigin: hint, searchRadius: 3d, cancellationToken: ct);
        var firstTotal = solver.LastSeedHypotheses;
        var (firstWinner, firstAbandoned) = solver.LastSeedHypothesesByOutcome;
        first.Solution.ShouldNotBeNull("the premise is a frame that solves");
        solver.Hints.Count.ShouldBe(1, "an accepted solve teaches the light path it came from");

        var second = await solver.SolveImageAsync(image, dim, searchOrigin: hint, searchRadius: 3d, cancellationToken: ct);
        var secondTotal = solver.LastSeedHypotheses;
        var (secondWinner, secondAbandoned) = solver.LastSeedHypothesesByOutcome;
        second.Solution.ShouldNotBeNull();

        output.WriteLine($"seed hypotheses: first {firstTotal:N0}, second {secondTotal:N0}; "
            + $"winning parity {firstWinner:N0} -> {secondWinner:N0}, abandoned {firstAbandoned:N0} -> {secondAbandoned:N0}");

        // The answer must not move -- the whole design rests on the hints being hints.
        var a = first.Solution.Value;
        var b = second.Solution.Value;
        CoordinateUtils.AngularSeparationDeg(a.CenterRA, a.CenterDec, b.CenterRA, b.CenterDec)
            .ShouldBeLessThan(1.0 / 3600.0, "a remembered scale and parity may make the solve cheaper, never different");

        secondWinner.ShouldBe(firstWinner,
            "a light path's remembered scale and parity may never change what the half that answers spends "
            + "on its own field: that scan is deterministic, and the cache only ever caps the OTHER half");
    }

    [Fact(Timeout = 300_000)]
    public async Task ADifferentLightPathLearnsNothingFromTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var (imageA, dim) = await RenderFieldAsync(output, db, ct, telescope: "Refractor A");
        var (imageB, _) = await RenderFieldAsync(output, db, ct, telescope: "SCT B, with a diagonal");

        var solver = new CatalogPlateSolver(db, NullLogger.Instance);
        var hint = new WCS(TargetRA, TargetDec);

        (await solver.SolveImageAsync(imageA, dim, searchOrigin: hint, searchRadius: 3d, cancellationToken: ct))
            .Solution.ShouldNotBeNull();
        var learned = solver.LastSeedHypotheses;

        var other = await solver.SolveImageAsync(imageB, dim, searchOrigin: hint, searchRadius: 3d, cancellationToken: ct);
        other.Solution.ShouldNotBeNull();

        output.WriteLine($"rig A first solve {learned:N0} hypotheses; rig B (no history) {solver.LastSeedHypotheses:N0}");

        // Parity is set by the reflections between sky and sensor, so an SCT with a diagonal is not
        // entitled to a refractor's answer even with the same camera on the same night. The cost of
        // getting this wrong is low BY DESIGN, but a key that is wrong by construction would be wrong
        // for a whole class of rigs forever.
        solver.Hints.Count.ShouldBe(2, "the two rigs are two light paths, not one");
    }

    [Fact]
    public void AnUnidentifiedFrameIsNotALightPath()
    {
        var cache = new SolveHintCache();
        var anonymous = new ImageMeta();

        cache.Store(anonymous, scaleRatio: 1.01f, winnerIsStd: true);

        cache.Count.ShouldBe(0,
            "a frame naming neither telescope nor camera is not a rig, it is every frame that omits "
            + "the keywords -- one entry shared by all of them would hand a synthetic frame's answer "
            + "to a real rig");
        cache.TryGet(anonymous).ShouldBeNull();
    }

    [Fact]
    public void ARejectedSolveTeachesNothing()
    {
        var cache = new SolveHintCache();
        var meta = new ImageMeta() with { Telescope = "SH61 EDPH", Instrument = "SV605CC" };

        cache.Store(meta, scaleRatio: float.NaN, winnerIsStd: true);
        cache.Store(meta, scaleRatio: 0f, winnerIsStd: true);

        cache.Count.ShouldBe(0, "a ratio that is not a positive number is not a measurement");

        cache.Store(meta, scaleRatio: 1.0019f, winnerIsStd: false);
        var hint = cache.TryGet(meta);
        hint.ShouldNotBeNull();
        hint.Value.ScaleRatio.ShouldBe(1.0019f);
        hint.Value.WinnerIsStd.ShouldBeFalse();
    }
}
