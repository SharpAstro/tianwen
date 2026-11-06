using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Real QHY PoleMaster frames near the south celestial pole solve from the one hint a polar camera always has: the pole.
/// </summary>
/// <remarks>
/// <para>The frames are the owner's (2021, saved by the PoleMaster software as 8-bit PNG): a 25 mm lens on a 1280x960
/// sensor of 3.75 um pixels, 30.7"/px over 11 x 8 degrees, far wider and shallower than anything else the solver is
/// tested on (118 stars detected against thousands of catalog stars in frame).</para>
/// <para>Until 2026-11-05 neither solved. The pair-lock seed found the field at once (94 of 160 anchors, chance 0.8) and
/// the matching loop then matched NOTHING: the seed measured the field's rotation against the projection tangent at the
/// hint, the loop re-projected about the seeded centre 15 degrees of RA away, and at Dec -80 that turns north by about
/// 15 degrees, some 200 px at the frame's edge against a 4.8 px tolerance. <c>CarryFitToNewTangentPoint</c> carries the
/// rotation over. The answers below are what the fixed solver gives; their scale is the lens's own, 25.2 mm.</para>
/// <para>The tree frame keeps a quarter of the field behind a tree: it is also the obstruction-detection fixture.</para>
/// </remarks>
[Collection("Astrometry")]
public class PoleMasterFrameSolveTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The PoleMaster's scale from its specification, 25 mm and 3.75 um: what a caller would type.</summary>
    private const float NominalScaleArcsec = 30.9f;

    private readonly TempFolders _temp = new TempFolders();

    public void Dispose() => _temp.Dispose();

    [Theory(Timeout = 300_000)]
    [InlineData("polemaster-scp-dec-79.9", 22.9653, -79.8825)]
    [InlineData("polemaster-scp-tree", 15.6618, -83.3819)]
    public async Task APoleMasterFrameSolvesFromAPoleHint(string fixture, double expectedRaHours, double expectedDecDeg)
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await LoadAsync(fixture);
        image.Width.ShouldBe(1280);
        image.Height.ShouldBe(960);

        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
        var solver = new CatalogPlateSolver(db, NullLogger<CatalogPlateSolver>.Instance);

        var result = await solver.SolveImageAsync(image, new ImageDim(NominalScaleArcsec, image.Width, image.Height), range: 0.1f,
            searchOrigin: new WCS(0, -89), searchRadius: 20, cancellationToken: ct);

        var solution = result.Solution.ShouldNotBeNull($"{fixture} solves from a pole hint");
        output.WriteLine($"{fixture}: RA={solution.CenterRA:F4}h Dec={solution.CenterDec:F4} scale={solution.PixelScaleArcsec:F3}\"/px matched {result.MatchedStars}/{result.DetectedStars}");

        // Separation on the sky, so an RA difference near the pole is not overstated.
        var separationDeg = CoordinateUtils.AngularSeparationDeg(solution.CenterRA, solution.CenterDec, expectedRaHours, expectedDecDeg);
        separationDeg.ShouldBeLessThan(0.05, "the centre is where the fixed solver put it");
        solution.PixelScaleArcsec.ShouldBe(30.7, 0.1, "the lens's own scale, 25.2 mm at 3.75 um");
        result.MatchedStars.ShouldBeGreaterThanOrEqualTo(60);
    }

    private async Task<Image> LoadAsync(string fixture)
    {
        var path = Path.Combine(_temp.Create("polemaster").FullName, fixture + ".png");
        await using (var embedded = SharedTestData.OpenEmbeddedFileStream(fixture + ".png").ShouldNotBeNull($"{fixture}.png is embedded"))
        await using (var file = File.Create(path))
        {
            await embedded.CopyToAsync(file, TestContext.Current.CancellationToken);
        }

        Image.TryReadImageFile(path, out var image).ShouldBeTrue($"{fixture}.png reads");
        return image;
    }
}
