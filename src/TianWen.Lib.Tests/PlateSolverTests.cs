using Shouldly;
using System;
using System.Diagnostics;
using System.Linq;
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

    [Theory]
    [InlineData(5.59, -5.39, "M42", 200, 0.05, 1)]   // Orion Nebula — IMX294C
    [InlineData(5.59, -5.39, "M42", 200, 0.05, 2)]   // Orion Nebula — IMX533M
    [InlineData(6.75, 16.7, "M35", 200, 0.05, 1)]     // M35 — IMX294C
    [InlineData(6.75, 16.7, "M35", 200, 0.05, 2)]     // M35 — IMX533M
    [InlineData(18.87, 33.03, "M57", 400, 0.05, 1)]   // Ring Nebula — IMX294C
    [InlineData(18.87, 33.03, "M57", 400, 0.05, 2)]   // Ring Nebula — IMX533M
    [InlineData(5.60, -1.12, "OrionBelt", 50, 0.05, 1)]  // Orion's Belt wide field — IMX294C
    [InlineData(5.60, -1.12, "OrionBelt", 50, 0.05, 2)]  // Orion's Belt wide field — IMX533M
    [InlineData(3.79, 24.12, "M45", 100, 0.05, 1)]    // Pleiades — IMX294C
    [InlineData(3.79, 24.12, "M45", 100, 0.05, 2)]    // Pleiades — IMX533M
    public async Task GivenSyntheticCatalogImageWhenCatalogPlateSolvingThenSolutionMatchesTarget(
        double targetRA, double targetDec, string targetName, int focalLengthMm, double accuracy, int cameraDeviceId)
    {
        // given — set up fake camera with catalog DB
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(cancellationToken);

        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var cameraDevice = new FakeDevice(DeviceType.Camera, cameraDeviceId);
        var camera = new FakeCameraDriver(cameraDevice, external);
        await camera.ConnectAsync(cancellationToken);

        camera.BinX = 1;
        camera.NumX = camera.CameraXSize - 1;
        camera.NumY = camera.CameraYSize - 1;
        camera.TrueBestFocus = 1000;
        camera.FocusPosition = 1000; // at perfect focus
        camera.FocalLength = focalLengthMm;
        camera.Target = new Target(targetRA, targetDec, targetName, null);
        camera.CelestialObjectDB = db;

        // Pixel scale: 206265 * pixelSizeUm * 1e-3 / focalLengthMm
        var pixelScaleArcsec = 206264.806 * camera.PixelSizeX * 1e-3 / focalLengthMm;
        var imgWidth = camera.CameraXSize - 1;
        var imgHeight = camera.CameraYSize - 1;
        var imageDim = new ImageDim(pixelScaleArcsec, imgWidth, imgHeight);

        output.WriteLine($"Target: {targetName} RA={targetRA:F4}h Dec={targetDec:F4}°");
        output.WriteLine($"Focal length: {focalLengthMm}mm, pixel scale: {pixelScaleArcsec:F2}\"/px");
        output.WriteLine($"FOV: {imageDim.FieldOfView.width:F3}° × {imageDim.FieldOfView.height:F3}°");

        // when — take an exposure and get image
        var exposureDuration = TimeSpan.FromSeconds(60);

        // Check how many catalog stars project onto the sensor (same cutoff as FakeCameraDriver)
        var magCutoff = Math.Min(12.0, 7.0 + 2.5 * Math.Log10(exposureDuration.TotalSeconds));
        var projectedStars = SyntheticStarFieldRenderer.ProjectCatalogStars(
            targetRA, targetDec, focalLengthMm, camera.PixelSizeX,
            imgWidth, imgHeight, db, magCutoff);
        output.WriteLine($"Projected catalog stars: {projectedStars.Count} (mag ≤ {magCutoff:F1})");
        projectedStars.Count.ShouldBeGreaterThanOrEqualTo(6, $"Need at least 6 catalog stars in FOV for {targetName}");
        await camera.StartExposureAsync(exposureDuration, cancellationToken: cancellationToken);
        await external.SleepAsync(exposureDuration, cancellationToken);

        (await camera.GetImageReadyAsync(cancellationToken)).ShouldBeTrue("Image should be ready after exposure");
        ICameraDriver cameraDriver = camera;
        var image = await cameraDriver.GetImageAsync(cancellationToken);
        image.ShouldNotBeNull("Camera should produce an image");

        output.WriteLine($"Image: {image.Width}×{image.Height}, BitDepth={image.BitDepth}, MaxValue={image.MaxValue:F0}, MinValue={image.MinValue:F0}");

        // Detect stars in the rendered image
        var detectedStars = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, cancellationToken: cancellationToken);
        output.WriteLine($"Detected stars: {detectedStars.Count}");
        detectedStars.Count.ShouldBeGreaterThanOrEqualTo(6, "Need at least 6 detected stars for plate solving");

        // Plate solve with search origin near the target
        var searchOrigin = new WCS(targetRA, targetDec);
        var solver = new CatalogPlateSolver(db);
        var result = await solver.SolveImageAsync(image, imageDim, searchOrigin: searchOrigin, searchRadius: 3d, cancellationToken: cancellationToken);

        output.WriteLine($"Solve: {result.Elapsed.TotalMilliseconds:F0}ms, {result.Iterations} iterations, " +
            $"{result.CatalogStars} catalog, {result.DetectedStars} detected, " +
            $"{result.ProjectedStars} projected, {result.MatchedStars} matched");

        // then — solution should match target coordinates
        result.Solution.ShouldNotBeNull($"CatalogPlateSolver should solve synthetic image of {targetName}");

        // Save rendered image to FITS with solved WCS (includes CD matrix)
        var outputDir = SharedTestData.CreateTempTestOutputDir("SyntheticCatalogPlateSolve");
        var fitsPath = System.IO.Path.Combine(outputDir, $"{targetName}_{focalLengthMm}mm_{exposureDuration.TotalSeconds:F0}s.fits");
        image.WriteToFitsFile(fitsPath, result.Solution.Value);
        output.WriteLine($"FITS written: {fitsPath}");

        var solvedWcs = result.Solution.Value;
        var (solvedRA, solvedDec) = solvedWcs;
        var cosDec = Math.Cos(double.DegreesToRadians(targetDec));
        var errorRAArcsec = Math.Abs(solvedRA - targetRA) * 15.0 * cosDec * 3600.0;
        var errorDecArcsec = Math.Abs(solvedDec - targetDec) * 3600.0;
        var totalErrorArcsec = Math.Sqrt(errorRAArcsec * errorRAArcsec + errorDecArcsec * errorDecArcsec);
        output.WriteLine($"Solved: RA={solvedRA:F6}h Dec={solvedDec:F6}° (expected RA={targetRA:F6}h Dec={targetDec:F6}°)");
        output.WriteLine($"Error: ΔRA={errorRAArcsec:F1}\" ΔDec={errorDecArcsec:F1}\" total={totalErrorArcsec:F1}\"");

        // Report WCS CD matrix
        output.WriteLine($"\nWCS: CRPIX=({solvedWcs.CRPix1:F1}, {solvedWcs.CRPix2:F1}), CRVAL=({solvedWcs.CenterRA:F6}h, {solvedWcs.CenterDec:F6}°)");
        output.WriteLine($"  CD1_1={solvedWcs.CD1_1:E4} CD1_2={solvedWcs.CD1_2:E4}");
        output.WriteLine($"  CD2_1={solvedWcs.CD2_1:E4} CD2_2={solvedWcs.CD2_2:E4}");
        output.WriteLine($"  HasCDMatrix={solvedWcs.HasCDMatrix}");

        // Report rendered vs WCS pixel position mismatch for brightest stars
        var brightestStars = projectedStars.OrderBy(s => s.Magnitude).Take(Math.Min(10, projectedStars.Count)).ToList();
        output.WriteLine($"\nStar position mismatch (rendered 0-based vs WCS SkyToPixel 1-based - 1):");
        output.WriteLine($"  {"Mag",5} {"RA",10} {"Dec",10} {"RendX",8} {"RendY",8} {"WcsX",8} {"WcsY",8} {"dX",7} {"dY",7} {"dist",7}");
        double sumSqDist = 0;
        int mismatchCount = 0;
        foreach (var star in brightestStars)
        {
            var wcsPixel = solvedWcs.SkyToPixel(star.RA, star.Dec);
            if (wcsPixel is not { } px)
            {
                continue;
            }
            // WCS SkyToPixel returns 1-based; rendered positions are 0-based
            var wcsX0 = px.X - 1.0;
            var wcsY0 = px.Y - 1.0;
            var dx = star.PixelX - wcsX0;
            var dy = star.PixelY - wcsY0;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            sumSqDist += dist * dist;
            mismatchCount++;
            output.WriteLine($"  {star.Magnitude,5:F1} {star.RA,10:F5} {star.Dec,10:F5} {star.PixelX,8:F2} {star.PixelY,8:F2} {wcsX0,8:F2} {wcsY0,8:F2} {dx,7:F2} {dy,7:F2} {dist,7:F2}");
        }
        if (mismatchCount > 0)
        {
            var rmsPixels = Math.Sqrt(sumSqDist / mismatchCount);
            var rmsArcsec = rmsPixels * pixelScaleArcsec;
            output.WriteLine($"  RMS mismatch: {rmsPixels:F2} px ({rmsArcsec:F1}\")");
        }

        solvedRA.ShouldBeInRange(targetRA - accuracy, targetRA + accuracy, $"RA should be within {accuracy}h for {targetName}");
        solvedDec.ShouldBeInRange(targetDec - accuracy, targetDec + accuracy, $"Dec should be within {accuracy}° for {targetName}");
    }
}
