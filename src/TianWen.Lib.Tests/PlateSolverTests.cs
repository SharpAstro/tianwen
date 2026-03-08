using Shouldly;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class PlateSolverTests(ITestOutputHelper output)
{
    private static ICelestialObjectDB? _cachedDB;
    private static readonly SemaphoreSlim _dbSem = new SemaphoreSlim(1, 1);

    private static async Task<ICelestialObjectDB> InitDBAsync(CancellationToken cancellationToken)
    {
        if (_cachedDB is ICelestialObjectDB db)
        {
            return db;
        }
        await _dbSem.WaitAsync(cancellationToken);
        try
        {
            if (_cachedDB is ICelestialObjectDB db2)
            {
                return db2;
            }
            var newDb = new CelestialObjectDB();
            await newDb.InitDBAsync(cancellationToken: cancellationToken);
            _cachedDB = newDb;
            return newDb;
        }
        finally
        {
            _dbSem.Release();
        }
    }

    private static IPlateSolver CreateSolver(Type solverType, ICelestialObjectDB? db = null)
    {
        if (solverType == typeof(CatalogPlateSolver))
        {
            db.ShouldNotBeNull();
            return new CatalogPlateSolver(db);
        }

        return (Activator.CreateInstance(solverType) as IPlateSolver).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverUnix))]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstapPlateSolver))]
    public async Task GivenStarFieldTestFileWhenBlindPlateSolvingThenItIsSolved(string name, Type solverType, double accuracy = 0.01)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name, cancellationToken);

        var solver = CreateSolver(solverType);
        var platform = Environment.OSVersion.Platform;

        Assert.SkipWhen(solverType.IsAssignableTo(typeof(AstrometryNetPlateSolver))
            && platform == PlatformID.Win32NT
            && !Debugger.IsAttached,
            $"Is multi-platform and running on Windows without debugger (Windows is skipped by default as WSL has a long cold start time)");

        Assert.SkipUnless(await solver.CheckSupportAsync(TestContext.Current.CancellationToken), $"Platform {platform} is not supported!");

        if (SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords))
        {
            // when
            var result = await solver.SolveFileAsync(extractedFitsFile, dimAndCoords.ImageDim, cancellationToken: cancellationToken);

            output.WriteLine($"[{solver.Name}] {name}: solved in {result.Elapsed.TotalMilliseconds:F0}ms");

            // then
            result.Solution.ShouldNotBeNull();
            var (ra, dec) = result.Solution.Value;
            output.WriteLine($"  RA={ra:F6}h Dec={dec:F6}° (expected RA={dimAndCoords.WCS.CenterRA:F6}h Dec={dimAndCoords.WCS.CenterDec:F6}°)");
            ra.ShouldBeInRange(dimAndCoords.WCS.CenterRA - accuracy, dimAndCoords.WCS.CenterRA + accuracy);
            dec.ShouldBeInRange(dimAndCoords.WCS.CenterDec - accuracy, dimAndCoords.WCS.CenterDec + accuracy);
        }
        else
        {
            Assert.Fail($"Could not extract test image dimensions for {name}");
        }
    }

    [Theory]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverUnix))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstapPlateSolver))]
    public async Task GivenStarFieldTestFileAndSearchOriginWhenPlateSolvingThenItIsSolved(string name, Type solverType, double accuracy = 0.01)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name, cancellationToken);
        var cts = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromHours(10) : TimeSpan.FromSeconds(10));

        var solver = CreateSolver(solverType);
        var platform = Environment.OSVersion.Platform;

        Assert.SkipWhen(solverType.IsAssignableTo(typeof(AstrometryNetPlateSolver))
            && platform == PlatformID.Win32NT
            && !Debugger.IsAttached,
            $"Is multi-platform and running on Windows without debugger (Windows is skipped by default as WSL has a long cold start time)");
        Assert.SkipUnless(await solver.CheckSupportAsync(cancellationToken), $"Platform {platform} is not supported!");

        if (SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords))
        {
            // when
            var result = await solver.SolveFileAsync(extractedFitsFile, dimAndCoords.ImageDim, searchOrigin: dimAndCoords.WCS, searchRadius: 1d, cancellationToken: cts.Token);

            output.WriteLine($"[{solver.Name}] {name}: solved in {result.Elapsed.TotalMilliseconds:F0}ms");

            // then
            result.Solution.ShouldNotBeNull();
            var (ra, dec) = result.Solution.Value;
            output.WriteLine($"  RA={ra:F6}h Dec={dec:F6}° (expected RA={dimAndCoords.WCS.CenterRA:F6}h Dec={dimAndCoords.WCS.CenterDec:F6}°)");
            ra.ShouldBeInRange(dimAndCoords.WCS.CenterRA - accuracy, dimAndCoords.WCS.CenterRA + accuracy);
            dec.ShouldBeInRange(dimAndCoords.WCS.CenterDec - accuracy, dimAndCoords.WCS.CenterDec + accuracy);
        }
        else
        {
            Assert.Fail($"Could not extract test image dimensions for {name}");
        }
    }

    [Theory]
    [InlineData("PlateSolveTestFile", 0.02)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 0.02)]
    public async Task GivenStarFieldImageAndSearchOriginWhenCatalogPlateSolvingThenItIsSolved(string name, double accuracy)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(cancellationToken);
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        var solver = new CatalogPlateSolver(db);

        SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords).ShouldBeTrue();

        // when
        var result = await solver.SolveImageAsync(image, dimAndCoords.ImageDim, searchOrigin: dimAndCoords.WCS, searchRadius: 3d, cancellationToken: cancellationToken);

        output.WriteLine($"[{solver.Name}] {name}: solved in {result.Elapsed.TotalMilliseconds:F0}ms, {result.Iterations} iterations, {result.CatalogStars} catalog, {result.DetectedStars} detected, {result.ProjectedStars} projected, {result.MatchedStars} matched");

        // then
        result.Solution.ShouldNotBeNull($"CatalogPlateSolver should solve {name}");
        var solution = result.Solution.Value;
        var (ra, dec) = solution;
        output.WriteLine($"  RA={ra:F6}h Dec={dec:F6}° (expected RA={dimAndCoords.WCS.CenterRA:F6}h Dec={dimAndCoords.WCS.CenterDec:F6}°, error RA={Math.Abs(ra - dimAndCoords.WCS.CenterRA):F6}h Dec={Math.Abs(dec - dimAndCoords.WCS.CenterDec):F6}°)");
        ra.ShouldBeInRange(dimAndCoords.WCS.CenterRA - accuracy, dimAndCoords.WCS.CenterRA + accuracy, $"RA should be within {accuracy}h");
        dec.ShouldBeInRange(dimAndCoords.WCS.CenterDec - accuracy, dimAndCoords.WCS.CenterDec + accuracy, $"Dec should be within {accuracy}°");

        // CD matrix should be populated
        solution.HasCDMatrix.ShouldBeTrue("CatalogPlateSolver should produce a CD matrix");
        output.WriteLine($"  CD matrix: [{solution.CD1_1:E4}, {solution.CD1_2:E4}; {solution.CD2_1:E4}, {solution.CD2_2:E4}]");
        output.WriteLine($"  CRPIX=({solution.CRPix1:F1}, {solution.CRPix2:F1}), pixel scale={solution.PixelScaleArcsec:F2}\"/px");

        // Pixel scale from CD matrix should be close to the ImageDim pixel scale
        var expectedPixelScale = dimAndCoords.ImageDim.PixelScale;
        solution.PixelScaleArcsec.ShouldBeInRange(expectedPixelScale * 0.8, expectedPixelScale * 1.2, "CD-derived pixel scale should be within 20% of ImageDim pixel scale");

        // WCS validation: project catalog stars to pixels via SkyToPixel, find the
        // catalog star with the best detected star match, and verify the separation is small.
        var detectedStars = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, cancellationToken: cancellationToken);

        // Collect catalog stars in the solved field
        var cosDec = Math.Cos(double.DegreesToRadians(solution.CenterDec));
        var fov = dimAndCoords.ImageDim.FieldOfView;
        var radiusDeg = Math.Max(fov.width, fov.height) * 0.5;
        var radiusRA = cosDec > 0.01 ? radiusDeg / (15.0 * cosDec) : 24.0;
        var raCellSize = 1.0 / 15.0;

        var bestDetDist = double.MaxValue;
        CelestialObject? bestCatStar = null;
        ImagedStar? bestDetStar = null;
        (double X, double Y)? bestPixel = null;

        for (var cellRA = solution.CenterRA - radiusRA; cellRA <= solution.CenterRA + radiusRA; cellRA += raCellSize)
        {
            var qRA = cellRA;
            if (qRA < 0) qRA += 24.0;
            if (qRA >= 24.0) qRA -= 24.0;

            for (var cellDec = solution.CenterDec - radiusDeg; cellDec <= solution.CenterDec + radiusDeg; cellDec += 1.0)
            {
                foreach (var idx in db.CoordinateGrid[qRA, cellDec])
                {
                    if (!db.TryLookupByIndex(idx, out var obj) || obj.ObjectType is not ObjectType.Star)
                    {
                        continue;
                    }

                    if (solution.SkyToPixel(obj.RA, obj.Dec) is not { } pix)
                    {
                        continue;
                    }

                    // Only consider stars projected within the image
                    if (pix.X < 1 || pix.X > image.Width || pix.Y < 1 || pix.Y > image.Height)
                    {
                        continue;
                    }

                    // Find nearest detected star (centroid is 0-based, SkyToPixel is 1-based)
                    foreach (var det in detectedStars)
                    {
                        var dx = (det.XCentroid + 1.0) - pix.X;
                        var dy = (det.YCentroid + 1.0) - pix.Y;
                        var dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist < bestDetDist)
                        {
                            bestDetDist = dist;
                            bestCatStar = obj;
                            bestDetStar = det;
                            bestPixel = pix;
                        }
                    }
                }
            }
        }

        bestCatStar.ShouldNotBeNull("Should find a catalog star matchable to a detected star");
        output.WriteLine($"  Best match: catalog {bestCatStar.Value.Index} V={bestCatStar.Value.V_Mag} → SkyToPixel=({bestPixel!.Value.X:F1}, {bestPixel.Value.Y:F1}), detected=({bestDetStar!.Value.XCentroid:F1}, {bestDetStar.Value.YCentroid:F1}), dist={bestDetDist:F1}px");
        bestDetDist.ShouldBeLessThan(5.0, "Best catalog→detected match via SkyToPixel should be within 5px");

        // Round-trip: SkyToPixel→PixelToSky should recover original coordinates exactly
        var roundTrip = solution.PixelToSky(bestPixel.Value.X, bestPixel.Value.Y);
        roundTrip.ShouldNotBeNull("PixelToSky round-trip should succeed");
        var rtSepRA = Math.Abs(roundTrip.Value.RA - bestCatStar.Value.RA) * 15.0 * cosDec * 3600.0;
        var rtSepDec = Math.Abs(roundTrip.Value.Dec - bestCatStar.Value.Dec) * 3600.0;
        var rtSep = Math.Sqrt(rtSepRA * rtSepRA + rtSepDec * rtSepDec);
        output.WriteLine($"  SkyToPixel→PixelToSky round-trip separation={rtSep:E2}\"");
        rtSep.ShouldBeLessThan(0.001, "SkyToPixel→PixelToSky round-trip should be < 0.001 arcsec");
    }

    [Theory]
    [InlineData("PlateSolveTestFile", 0.02)]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 0.02)]
    public async Task GivenStarFieldFileAndSearchOriginWhenCatalogPlateSolvingViaSolveFileThenItIsSolved(string name, double accuracy)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(cancellationToken);
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name, cancellationToken);
        var solver = new CatalogPlateSolver(db);

        SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords).ShouldBeTrue();

        // when
        var result = await solver.SolveFileAsync(extractedFitsFile, dimAndCoords.ImageDim, searchOrigin: dimAndCoords.WCS, searchRadius: 3d, cancellationToken: cancellationToken);

        output.WriteLine($"[{solver.Name}] {name}: solved in {result.Elapsed.TotalMilliseconds:F0}ms, {result.Iterations} iterations, {result.CatalogStars} catalog, {result.DetectedStars} detected, {result.ProjectedStars} projected, {result.MatchedStars} matched");

        // then
        result.Solution.ShouldNotBeNull($"CatalogPlateSolver should solve {name} via SolveFileAsync");
        var (ra, dec) = result.Solution.Value;
        output.WriteLine($"  RA={ra:F6}h Dec={dec:F6}° (expected RA={dimAndCoords.WCS.CenterRA:F6}h Dec={dimAndCoords.WCS.CenterDec:F6}°, error RA={Math.Abs(ra - dimAndCoords.WCS.CenterRA):F6}h Dec={Math.Abs(dec - dimAndCoords.WCS.CenterDec):F6}°)");
        ra.ShouldBeInRange(dimAndCoords.WCS.CenterRA - accuracy, dimAndCoords.WCS.CenterRA + accuracy);
        dec.ShouldBeInRange(dimAndCoords.WCS.CenterDec - accuracy, dimAndCoords.WCS.CenterDec + accuracy);
    }

    [Fact]
    public async Task GivenNoSearchOriginWhenCatalogPlateSolvingThenItReturnsNull()
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(cancellationToken);
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(SharedTestData.PlateSolveTestFile, cancellationToken: cancellationToken);
        var solver = new CatalogPlateSolver(db);

        // when — no searchOrigin provided
        var result = await solver.SolveImageAsync(image, cancellationToken: cancellationToken);

        // then
        result.Solution.ShouldBeNull();
    }
}
