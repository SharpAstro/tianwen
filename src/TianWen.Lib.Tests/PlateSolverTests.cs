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

[Collection("Astrometry")]
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
        var cts = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(20));

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
    [InlineData(20.04, 48.2, "Zenith", 130, 0.1, -1)]  // Zenith from lat=48.2 — Guide camera IMX178M at 130mm
    public async Task GivenSyntheticCatalogImageWhenCatalogPlateSolvingThenSolutionMatchesTarget(
        double targetRA, double targetDec, string targetName, int focalLengthMm, double accuracy, int cameraDeviceId)
    {
        // given — set up fake camera with catalog DB
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(cancellationToken);

        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        // cameraDeviceId == -1 means use the guide camera (FakeGuideCam IMX178M)
        var cameraDevice = cameraDeviceId >= 0
            ? new FakeDevice(DeviceType.Camera, cameraDeviceId)
            : new FakeDevice(new Uri("Camera://FakeDevice/FakeGuideCam#Fake Guide Cam (IMX178M)"));
        var camera = new FakeCameraDriver(cameraDevice, external.BuildServiceProvider());
        await camera.ConnectAsync(cancellationToken);
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
        await timeProvider.SleepAsync(exposureDuration, cancellationToken);

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

    [Fact(Timeout = 30_000)]
    public async Task GivenRGGBCameraWhenRenderingThenBayerImageDebayersToColor()
    {
        // given — IMX294C (RGGB) pointing at M42
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(cancellationToken);

        var targetRA = 5.59;
        var targetDec = -5.39;
        var focalLengthMm = 200;
        var pixelSizeUm = 4.63; // IMX294C
        var width = 1024; // smaller for speed
        var height = 768;
        var magCutoff = 10.0;

        var stars = SyntheticStarFieldRenderer.ProjectCatalogStars(
            targetRA, targetDec, focalLengthMm, pixelSizeUm, width, height, db, magCutoff);
        output.WriteLine($"Projected stars: {stars.Count}");

        // Check B-V distribution
        var bvValues = stars.Select(s => s.BMinusV).OrderBy(v => v).ToList();
        output.WriteLine($"B-V range: [{bvValues.First():F2}, {bvValues.Last():F2}]");
        bvValues.Count.ShouldBeGreaterThan(0);

        // when — render as Bayer
        var bayerData = SyntheticStarFieldRenderer.RenderBayer(width, height, defocusSteps: 0,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stars),
            exposureSeconds: 30, noiseSeed: 42);

        var dataMax = 0f;
        var dataMin = float.MaxValue;
        foreach (var v in bayerData)
        {
            if (v > dataMax) dataMax = v;
            if (v < dataMin) dataMin = v;
        }
        var bayerImage = new Image([bayerData], BitDepth.Float32, dataMax, dataMin, 0f,
            default(ImageMeta) with { SensorType = SensorType.RGGB, BayerOffsetX = 0, BayerOffsetY = 0 });

        bayerImage.ChannelCount.ShouldBe(1, "Bayer image should be single-channel");
        bayerImage.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);

        // Save Bayer FITS
        var outputDir = SharedTestData.CreateTempTestOutputDir("BayerRGGB");
        var bayerPath = System.IO.Path.Combine(outputDir, "bayer_m42.fits");
        bayerImage.WriteToFitsFile(bayerPath, null);
        output.WriteLine($"Bayer FITS: {bayerPath} ({new System.IO.FileInfo(bayerPath).Length} bytes)");

        // then — debayer should produce 3-channel color image
        var colorImage = await bayerImage.ScaleFloatValuesToUnit().DebayerAsync(DebayerAlgorithm.AHD, normalizeToUnit: false, cancellationToken);
        colorImage.ChannelCount.ShouldBe(3, "Debayered image should have 3 channels");
        colorImage.Width.ShouldBe(width);
        colorImage.Height.ShouldBe(height);

        // Save debayered FITS (as 3-channel)
        var colorPath = System.IO.Path.Combine(outputDir, "debayered_m42.fits");
        colorImage.WriteToFitsFile(colorPath, null);
        output.WriteLine($"Color FITS: {colorPath} ({new System.IO.FileInfo(colorPath).Length} bytes)");

        // Verify channels differ — a mono image would have identical channels
        var rMedian = Median(colorImage.GetChannelSpan(0));
        var gMedian = Median(colorImage.GetChannelSpan(1));
        var bMedian = Median(colorImage.GetChannelSpan(2));
        output.WriteLine($"Channel medians: R={rMedian:F4} G={gMedian:F4} B={bMedian:F4}");

        // At least one channel should differ meaningfully from the others
        var maxDiff = Math.Max(Math.Abs(rMedian - gMedian), Math.Max(Math.Abs(rMedian - bMedian), Math.Abs(gMedian - bMedian)));
        output.WriteLine($"Max channel median difference: {maxDiff:F4}");
        ((double)maxDiff).ShouldBeGreaterThan(0.0001, "Debayered channels should differ for a color star field");
    }

    private static float Median(ReadOnlySpan<float> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    /// Manual diagnostic: time <see cref="Image.FindStarsAsync"/> on a saved
    /// snapshot to chase the production-vs-benchmark discrepancy. The synthetic
    /// FindStarsBenchmarks finishes in ~157 ms on a 9576x6388 frame; production
    /// polar-align previews against the same sensor size were burning >88 s of
    /// FindStarsAsync wall time. Running this against the FITS the user
    /// captured pinpoints the difference. Skipped automatically when the
    /// snapshot path doesn't exist (i.e. on CI / other dev machines).
    /// </summary>
    [Fact(Skip = "Manual diagnostic — run by hand against a locally-saved FITS to time FindStarsAsync stages.")]
    public async Task DiagnoseFindStarsAsyncOnSavedSnapshot()
    {
        var snapshotPath = @"C:\Users\SebastianGodelet\Pictures\TianWen\Snapshot\2026-04-27\snapshot_2026-04-27T03_48_06_OTA1.fits";
        if (!System.IO.File.Exists(snapshotPath))
        {
            output.WriteLine($"Skipping: snapshot {snapshotPath} not present on this machine.");
            return;
        }

        var swLoad = Stopwatch.StartNew();
        Image.TryReadFitsFile(snapshotPath, out var image, out _).ShouldBeTrue();
        image.ShouldNotBeNull();
        output.WriteLine($"Loaded {image.Width}x{image.Height} ({image.ChannelCount} ch, {image.BitDepth}, min={image.MinValue:F0} max={image.MaxValue:F0}) in {swLoad.Elapsed.TotalMilliseconds:F0} ms");
        output.WriteLine($"Meta: PixelSizeX={image.ImageMeta.PixelSizeX} um, FocalLength={image.ImageMeta.FocalLength} mm, BinX={image.ImageMeta.BinX}, SensorType={image.ImageMeta.SensorType}");

        var sw = Stopwatch.StartNew();
        var bg = image.Background(0);
        output.WriteLine($"Background(0): bg={bg.Item1:F1} starLevel={bg.Item2:F1} noise={bg.Item3:F2} histThr={bg.Item4:F1}  in {sw.Elapsed.TotalMilliseconds:F0} ms");
        output.WriteLine($"  Detection level start = max(3.5*noise, starLevel) = {Math.Max(3.5f * bg.Item3, bg.Item2):F1}");

        var ct = TestContext.Current.CancellationToken;

        // Plate-solver path: snrMin=5, maxStars=500, default 2 retries.
        sw.Restart();
        var stars = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, cancellationToken: ct);
        output.WriteLine($"FindStarsAsync(snrMin=5, maxStars=500, retries=2) -> {stars.Count} stars in {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Same but no retry -- isolates the cost of the retry loop.
        sw.Restart();
        var stars0r = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, maxRetries: 0, cancellationToken: ct);
        output.WriteLine($"FindStarsAsync(snrMin=5, maxStars=500, retries=0) -> {stars0r.Count} stars in {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Higher SNR threshold -- isolates the cost of low-threshold AnalyseStar calls.
        sw.Restart();
        var stars10 = await image.FindStarsAsync(0, snrMin: 10f, maxStars: 500, cancellationToken: ct);
        output.WriteLine($"FindStarsAsync(snrMin=10, maxStars=500, retries=2) -> {stars10.Count} stars in {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Tiny maxStars -- if the retry loop is the bottleneck, this should terminate fast.
        sw.Restart();
        var stars50 = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 50, cancellationToken: ct);
        output.WriteLine($"FindStarsAsync(snrMin=5, maxStars=50, retries=2) -> {stars50.Count} stars in {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Plate-solver path with new minStars early-termination.
        // Cache invalidated each call so we measure the cold path, not the cache.
        image.InvalidateStarListCache();
        sw.Restart();
        var stars50min = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, cancellationToken: ct);
        output.WriteLine($"FindStarsAsync(snrMin=5, maxStars=500, minStars=50, retries=2) -> {stars50min.Count} stars in {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Cache hit: same params, second call should be near-zero.
        sw.Restart();
        var stars50cached = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, cancellationToken: ct);
        output.WriteLine($"FindStarsAsync(... cached) -> {stars50cached.Count} stars in {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// Regression test for the polar-cap fast path in
    /// <see cref="Tycho2RaDecIndex.EnumerateStarsInDecBand"/>. The legacy per-cell
    /// path triggered when CatalogPlateSolver queried a polar cap was scanning
    /// the same handful of polar GSC regions hundreds of times -- a single
    /// plate solve at the SCP took ~5 minutes in production. The fast path
    /// dedupes the regions and walks each one's binary entries once, returning
    /// the same set of stars in well under a second.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [Trait("Category", "Perf")]
    [InlineData(-90.0, -85.0, "SCP cap")]
    [InlineData(85.0, 90.0, "NCP cap")]
    [InlineData(-89.5, -89.0, "Near-SCP narrow band")]
    public async Task GivenPolarDecBand_WhenEnumeratingTycho2_ThenCompletesInUnderHalfASecond(
        double minDec, double maxDec, string label)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(cancellationToken);

        var composite = db.CoordinateGrid as CompositeRaDecIndex;
        composite.ShouldNotBeNull("expected CelestialObjectDB to expose a CompositeRaDecIndex");
        var tycho2 = composite.Tycho2;
        tycho2.ShouldNotBeNull("expected Tycho-2 catalog to be loaded for the polar-cap fast path test");

        // Warm up: first call pays JIT + initial cache fill.
        _ = tycho2.EnumerateStarsInDecBand(minDec, maxDec).Take(1).ToList();

        var sw = Stopwatch.StartNew();
        var stars = tycho2.EnumerateStarsInDecBand(minDec, maxDec).ToList();
        sw.Stop();

        output.WriteLine($"{label}: {stars.Count} Tycho-2 entries in [{minDec:F1}, {maxDec:F1}] in {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Polar caps are the slowest case; even there, dedup-and-scan-once
        // should land well under half a second on a developer laptop. The
        // pre-fix per-cell path took ~30+ seconds for the same inputs.
        sw.Elapsed.TotalMilliseconds.ShouldBeLessThan(500,
            $"polar dec-band enumeration regressed: {label} took {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Sanity: the SCP/NCP caps should contain plenty of Tycho-2 stars
        // (Octans/Polaris regions). 100 is conservative.
        stars.Count.ShouldBeGreaterThan(100, $"{label} should contain >100 Tycho-2 entries");

        // Correctness: every returned entry should land within the dec band.
        // We can't easily re-read the binary here, but we can confirm via the
        // db lookup by-index path.
        foreach (var idx in stars.Take(50))
        {
            db.TryLookupByIndex(idx, out var obj).ShouldBeTrue();
            ((double)obj.Dec).ShouldBeGreaterThanOrEqualTo(minDec - 0.01);
            ((double)obj.Dec).ShouldBeLessThanOrEqualTo(maxDec + 0.01);
        }
    }
}
