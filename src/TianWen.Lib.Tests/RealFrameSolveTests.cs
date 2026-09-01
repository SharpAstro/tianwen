using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The committed real frame solves from nothing but its own header, and this says so -- the premise
/// <see cref="T:TianWen.UI.Benchmarks.PlateSolveBenchmarks"/> rests on. A harness whose frame stopped
/// solving would measure the FAILURE path at a completely different cost and look like a win, so the
/// premise is asserted here rather than assumed there.
///
/// <para>It also reports the stage split, including the catalog COLD START that BenchmarkDotNet
/// structurally cannot measure: it happens once per process and is cached after, so a second BDN
/// iteration of it is a warm start. That stage is 51% of the plan's budget and all of phase B, which
/// is why it is worth having a number for it somewhere.</para>
///
/// <para>The frame: NGC 3576 (Statue of Liberty Nebula), SVBONY SV605CC / IMX533 3008x3008, SH61
/// EDPH 270 mm f/4.5, 60 s, N.I.N.A. It is a raw GRBG mosaic -- <c>SensorType.RGGB</c> means only
/// "this is a CFA mosaic", the pattern itself riding in the Bayer offsets -- and is deliberately
/// solved as one, because <see cref="Image.FindStarsAsync"/> debayers to mono internally and that is
/// the path a session actually takes.</para>
/// </summary>
[Collection("Astrometry")]
public class RealFrameSolveTests(ITestOutputHelper output)
{
    private const string FixtureName = "2026-02-15_00-56-23__-5.00_60.00s_0058";

    [Fact(Timeout = 600_000)]
    public async Task TheRealFrameSolvesFromItsOwnHeaderAndRecoversItsScale()
    {
        var ct = TestContext.Current.CancellationToken;

        var sw = Stopwatch.StartNew();
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(FixtureName, isReadOnly: false, cancellationToken: ct);
        output.WriteLine($"load          {sw.ElapsedMilliseconds} ms  {image.Width}x{image.Height} {image.ImageMeta.SensorType} bayer=({image.ImageMeta.BayerOffsetX},{image.ImageMeta.BayerOffsetY}) channels={image.ChannelCount}");

        var dim = image.GetImageDim();
        output.WriteLine($"implied dim   {(dim is { } d ? $"{d.PixelScale:F4}\"/px {d.Width}x{d.Height} -> {d.Width * d.PixelScale / 3600.0:F2} deg" : "NONE")}");
        output.WriteLine($"header hint   RA={image.ImageMeta.TargetRA:F5}h Dec={image.ImageMeta.TargetDec:F4} (focallen={image.ImageMeta.FocalLength}mm pix={image.ImageMeta.PixelSizeX}um)");

        Assert.NotNull(dim);

        sw.Restart();
        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
        output.WriteLine($"catalog init  {sw.ElapsedMilliseconds} ms  <- phase B targets this");
        foreach (var (phase, elapsed) in db.LastInitPhaseTimings)
        {
            output.WriteLine($"   phase       {elapsed.TotalMilliseconds,8:F1} ms  {phase}");
        }
        output.WriteLine($"   tycho2      {db.Tycho2BulkLoadState}");

        sw.Restart();
        var stars = await image.FindStarsAsync(channel: 0, snrMin: 10f, cancellationToken: ct);
        output.WriteLine($"detect        {sw.ElapsedMilliseconds} ms  {stars.Count} stars at snrMin=10");

        var solver = new CatalogPlateSolver(db, NullLogger<CatalogPlateSolver>.Instance);
        var hint = double.IsNaN(image.ImageMeta.TargetRA)
            ? null
            : (Astrometry.WCS?)new Astrometry.WCS(image.ImageMeta.TargetRA, image.ImageMeta.TargetDec);

        foreach (var (label, origin) in new[] { ("hinted", hint), ("blind", null) })
        {
            sw.Restart();
            var result = await solver.SolveImageAsync(image, dim.Value, searchOrigin: origin, cancellationToken: ct);
            sw.Stop();
            if (result.Solution is { } s)
            {
                var prior = solver.LastScalePrior is { } sp
                    ? $"ratio {sp.Ratio:F5} ({sp.Candidates} cands, spread {sp.RelativeSpread:F4}) -> {dim.Value.PixelScale / sp.Ratio:F4}\"/px"
                    : "declined";
                output.WriteLine($"{label,-8} SOLVED {sw.ElapsedMilliseconds,6} ms  centre=({s.CenterRA:F5}h, {s.CenterDec:F4}) rot={s.RotationDeg:F2} scale={s.PixelScaleArcsec:F4}\"/px  CD=[{s.CD1_1:E3} {s.CD1_2:E3}; {s.CD2_1:E3} {s.CD2_2:E3}] det={(s.CD1_1 * s.CD2_2 - s.CD1_2 * s.CD2_1):E3}  race: abandoned={solver.LastParityRace.AbandonedAParity} winnerIsStd={solver.LastParityRace.WinnerIsStd} reRan={solver.LastParityRace.ReRanAbandonedParity}  quad scale: {prior}");
            }
            else
            {
                output.WriteLine($"{label,-8} NO SOLUTION after {sw.ElapsedMilliseconds} ms");
            }
        }

        // Assertions, not just output: this is the benchmark's premise and a solver regression guard.
        var solved = await _AssertSolves(solver, image, dim.Value, hint, ct);
        Shouldly.ShouldBeTestExtensions.ShouldBeLessThan(
            Math.Abs(solved.PixelScaleArcsec - dim.Value.PixelScale) / dim.Value.PixelScale, 0.01,
            $"the scale recovered from the stars ({solved.PixelScaleArcsec:F4}) must agree with the one FOCALLEN implies "
            + $"({dim.Value.PixelScale:F4}) to within 1% -- FOCALLEN is a hint, but not a wrong one");
        Shouldly.ShouldBeTestExtensions.ShouldBeLessThan(
            Math.Abs(solved.CenterDec - image.ImageMeta.TargetDec), 0.2,
            "the solved centre must be the field the header says it is");

        image.Release();
    }

    /// <summary>
    /// A frame whose header points somewhere else entirely still solves. The Vela panel 10 crop
    /// carries its PARENT panel's pointing -- <c>OBJECT = HD 72800</c> is accurate to 0.4 arcmin, but
    /// the pixels are 93 arcmin from it, on a 2.17 deg field -- so the anchor pool the seed projects
    /// from the hint lands 74% outside the frame and starves. Before the positional search this
    /// returned no solution at ANY radius out to 12 deg and any scale from 0.5x to 2x; ASTAP solved
    /// it in 0.7 s, which is what said the gap was ours.
    /// </summary>
    /// <remarks>
    /// <para>The two assertions are deliberately opposed. "It solves" alone would pass on a solver that
    /// simply trusted the hint if the hint were ever fixed; "the answer is far from the hint" alone
    /// would pass on garbage. Together they say the solver went looking and came back with the right
    /// field -- and the second one is what fails if every relocation path is removed.</para>
    /// <para>The third says WHICH path: the quad seed, ahead of the parity race, not the positional
    /// search behind it. The search answered this frame in 48 ms; the seconds were the two parities
    /// failing at the header's pointing before it ran, so a relocation that happens after them saves
    /// nothing by construction (phase C3, attempt 1). A solve that still reaches the search here is
    /// correct and slow, and this is what says so.</para>
    /// </remarks>
    [Fact(Timeout = 600_000)]
    public async Task TheCropWhoseHeaderPointsElsewhereStillSolves()
    {
        var ct = TestContext.Current.CancellationToken;

        var image = await SharedTestData.ExtractGZippedFitsImageAsync(
            "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop", isReadOnly: false, cancellationToken: ct);
        var dim = image.GetImageDim();
        Assert.NotNull(dim);

        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
        var solver = new CatalogPlateSolver(db, NullLogger<CatalogPlateSolver>.Instance);

        var hint = new Astrometry.WCS(image.ImageMeta.TargetRA, image.ImageMeta.TargetDec);
        var sw = Stopwatch.StartNew();
        var result = await solver.SolveImageAsync(image, dim.Value, searchOrigin: hint, cancellationToken: ct);
        sw.Stop();

        Assert.NotNull(result.Solution);
        var solved = result.Solution.Value;
        var offArcmin = 60.0 * Astrometry.CoordinateUtils.AngularSeparationDeg(
            hint.CenterRA, hint.CenterDec, solved.CenterRA, solved.CenterDec);
        output.WriteLine($"solved {sw.ElapsedMilliseconds} ms at ({solved.CenterRA:F5}h, {solved.CenterDec:F4}) " +
            $"{offArcmin:F0} arcmin off the header hint, {solved.PixelScaleArcsec:F4}\"/px, {result.MatchedStars} matched");

        // ASTAP puts this field at 08:41:47.5 -48:02:24 with 71 of 72 quads matched; agreeing with an
        // independent solver is the only oracle this fixture has, since its own header does not know.
        Shouldly.ShouldBeTestExtensions.ShouldBeLessThan(
            Astrometry.CoordinateUtils.AngularSeparationDeg(8.69653, -48.0400, solved.CenterRA, solved.CenterDec), 0.1,
            "the relocated solve must land where an independent solver puts this field");
        Shouldly.ShouldBeTestExtensions.ShouldBeGreaterThan(offArcmin, 60.0,
            "the whole point is that the answer is NOT near the header hint -- if this drops below an "
            + "arcminute the test has stopped exercising relocation at all");

        Assert.NotNull(solver.LastQuadSeed);
        var quadSeed = solver.LastQuadSeed.Value;
        output.WriteLine($"quad seed {quadSeed.Inliers}/{quadSeed.RawPairs} moved the origin {60.0 * quadSeed.RelocationDeg:F1} arcmin, "
            + $"scale ratio {quadSeed.ScaleRatio:F5}, {(quadSeed.IsStd ? "standard" : "mirror")} parity; origin source {solver.LastOriginSource}");
        Shouldly.ShouldBeTestExtensions.ShouldBe(solver.LastOriginSource, CatalogPlateSolver.OriginSource.QuadSeed,
            "the relocation must come from the quad seed AHEAD of the parity race; reaching the positional search means the race ran and failed at the hint first");
        Shouldly.ShouldBeTestExtensions.ShouldBeGreaterThan(60.0 * quadSeed.RelocationDeg, 60.0,
            "the seed must have moved the origin by the header error, not merely locked where the hint already was");
        Shouldly.ShouldBeTestExtensions.ShouldBe(quadSeed.IsStd, solver.LastParityRace.WinnerIsStd,
            "the parity read off the seed's determinant must be the parity that actually won the race, or the belief caps the wrong half");

        // And now the SAME rig again, on the same solver, which is what a session does all night.
        //
        // Before the quad seed this was the cache's end-to-end pin: the second solve spent 1.6x fewer
        // hypotheses (9,165,717 -> 5,813,171) because the remembered PARITY capped the doubted half.
        // The quad seed measures parity and scale on the frame itself, ahead of the race, so the first
        // solve already knows everything the cache could tell the second and the two spend the same
        // 8,381 hypotheses -- 1,094x fewer than the first solve used to. What is pinned now is that no
        // material saving is LEFT: a second solve markedly cheaper than the first would mean the seed
        // had stopped supplying parity or scale and the cache was covering for it. The cache's own
        // behaviour is pinned in SolveHintCacheTests; its end-to-end win survives only on a frame where
        // the quad seed declines, which no committed fixture reproduces.
        var firstHypotheses = solver.LastSeedHypotheses;
        var swSecond = Stopwatch.StartNew();
        var again = await solver.SolveImageAsync(image, dim.Value, searchOrigin: hint, cancellationToken: ct);
        swSecond.Stop();
        var secondHypotheses = solver.LastSeedHypotheses;
        output.WriteLine($"second solve {swSecond.ElapsedMilliseconds} ms, seed hypotheses {firstHypotheses:N0} -> {secondHypotheses:N0} "
            + $"({(firstHypotheses > 0 ? (double)firstHypotheses / Math.Max(1, secondHypotheses) : 0):F1}x fewer)");

        Assert.NotNull(again.Solution);
        var solvedAgain = again.Solution.Value;
        Shouldly.ShouldBeTestExtensions.ShouldBeLessThan(
            Astrometry.CoordinateUtils.AngularSeparationDeg(
                solved.CenterRA, solved.CenterDec, solvedAgain.CenterRA, solvedAgain.CenterDec),
            1.0 / 3600.0,
            "a remembered scale and parity may make the solve cheaper, never different");
        Shouldly.ShouldBeTestExtensions.ShouldBe(solver.LastOriginSource, CatalogPlateSolver.OriginSource.QuadSeed,
            "the second solve must relocate through the quad seed as the first did");
        Shouldly.ShouldBeTestExtensions.ShouldBeInRange(secondHypotheses, (int)(firstHypotheses * 0.75), (int)(firstHypotheses * 1.25),
            $"with the quad seed supplying parity and scale on the FIRST solve the cache has nothing material left to add "
            + $"({firstHypotheses:N0} -> {secondHypotheses:N0} hypotheses; measured equal, and the band is +/-25% because the "
            + "abandoned parity's count depends on when its sibling cancelled it)");

        image.Release();
    }

    private static async Task<Astrometry.WCS> _AssertSolves(
        CatalogPlateSolver solver, Image image, ImageDim dim,
        Astrometry.WCS? hint, System.Threading.CancellationToken ct)
    {
        var result = await solver.SolveImageAsync(image, dim, searchOrigin: hint, cancellationToken: ct);
        Assert.NotNull(result.Solution);
        return result.Solution.Value;
    }
}
