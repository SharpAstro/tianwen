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
                output.WriteLine($"{label,-8} SOLVED {sw.ElapsedMilliseconds,6} ms  centre=({s.CenterRA:F5}h, {s.CenterDec:F4}) rot={s.RotationDeg:F2} scale={s.PixelScaleArcsec:F4}\"/px  CD=[{s.CD1_1:E3} {s.CD1_2:E3}; {s.CD2_1:E3} {s.CD2_2:E3}] det={(s.CD1_1 * s.CD2_2 - s.CD1_2 * s.CD2_1):E3}  race: abandoned={solver.LastParityRace.AbandonedAParity} winnerIsStd={solver.LastParityRace.WinnerIsStd} reRan={solver.LastParityRace.ReRanAbandonedParity}");
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

    private static async Task<Astrometry.WCS> _AssertSolves(
        CatalogPlateSolver solver, Image image, ImageDim dim,
        Astrometry.WCS? hint, System.Threading.CancellationToken ct)
    {
        var result = await solver.SolveImageAsync(image, dim, searchOrigin: hint, cancellationToken: ct);
        Assert.NotNull(result.Solution);
        return result.Solution.Value;
    }
}
