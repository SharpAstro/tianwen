using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Why a <see cref="FakeCameraDriver"/> synthetic field does not solve. Renders the exact geometry
/// the flip session test uses (FakeCamera1 = IMX294C at 480 mm) and separates the three candidate
/// causes, which need different fixes and are otherwise indistinguishable from "no solution":
/// the frame's METADATA (a wrong or missing pixel scale), the fake's own star PROJECTION (positions
/// that do not agree with the catalog they were drawn from), or the SOLVER.
/// </summary>
[Collection("Imaging")]
public class FakeFieldSolveProbe(ITestOutputHelper output)
{
    private sealed class OutputLogger(ITestOutputHelper output) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => output.WriteLine($"  [{logLevel}] {formatter(state, exception)}");
    }

    [Theory]
    // Row 1 is the session E2E's own configuration: FocalLength set, Aperture NOT (apertureScale 1.0).
    // The rest walk the two knobs that decide how many stars the fake places.
    // The E2E's own FlipTarget at the ROI SessionTestHelper actually sets (512x512), then the same
    // field at successively larger ROIs, and the full sensor. This is the axis that decides whether a
    // synthetic frame holds enough Tycho-2 stars to solve at all.
    [InlineData(0, 10.0, 5.74, 20.0, 512)]
    [InlineData(0, 10.0, 5.74, 20.0, 1024)]
    [InlineData(0, 10.0, 5.74, 20.0, 2048)]
    [InlineData(0, 10.0, 5.74, 20.0, 0)]
    // Sparse fields: high galactic latitude, where Tycho-2 thins out. This is where a synthetic
    // field is most likely to stop being solvable, and the dense winter fields say nothing about it.
    // A sparse high-galactic-latitude field at the same ROIs.
    [InlineData(0, 10.0, 12.5, 27.0, 512)]
    [InlineData(0, 10.0, 12.5, 27.0, 2048)]
    public async Task ReportWhyAFakeFieldDoesNotSolve(
        int apertureMm, double exposureSec, double targetRA, double targetDec, int roi)
    {
        var ct = TestContext.Current.CancellationToken;

        // FakeCamera1's preset, and the focal length MeridianFlipVerificationSessionTests sets.
        var preset = FakeCameraDriver.GetPresetForId(1);
        const double focalLengthMm = 480.0;

        // roi = 0 means the whole sensor; anything else is the square ROI a test sets via NumX/NumY.
        var width = roi > 0 ? roi : preset.Width;
        var height = roi > 0 ? roi : preset.Height;
        var pixelScaleArcsec = CoordinateUtils.PixelScaleArcsec(preset.PixelSize, focalLengthMm);
        var pixelScaleDeg = pixelScaleArcsec / 3600.0;

        output.WriteLine($"CONFIG     roi={(roi > 0 ? roi + "px" : "full sensor")} RA={targetRA}h Dec={targetDec} aperture={(apertureMm > 0 ? apertureMm + "mm" : "UNSET")} exposure={exposureSec}s");
        output.WriteLine($"sensor     {preset.SensorName} {width}x{height} @ {preset.PixelSize}um, {preset.SensorType}");
        output.WriteLine($"scale      {pixelScaleArcsec:F3}\"/px  FOV {width * pixelScaleDeg:F2} x {height * pixelScaleDeg:F2} deg");

        var db = await SharedCatalogDB.InitAsync(ct);

        var apertureScale = apertureMm > 0 ? Math.Pow(apertureMm / 50.0, 2.0) : 1.0;
        var magCutoff = Math.Min(15.0, SyntheticStarFieldRenderer.DetectabilityMagCutoff(apertureScale, exposureSec));
        output.WriteLine($"magCutoff  {magCutoff:F2} (apertureScale {apertureScale:F2}, {exposureSec}s)");

        // ---- 1. what the fake PLACES -------------------------------------------------------
        var projected = SyntheticStarFieldRenderer.ProjectCatalogStars(
            targetRA, targetDec, focalLengthMm, preset.PixelSize, width, height, db, magCutoff);
        output.WriteLine($"projected  {projected.Count} stars placed by the fake");

        // ---- 2. what the CATALOG holds over the same field, unfiltered ----------------------
        var fovDiagDeg = Math.Sqrt(Math.Pow(width * pixelScaleDeg, 2) + Math.Pow(height * pixelScaleDeg, 2)) / 2.0;
        var catalogAll = CatalogStarCounter.EnumerateFieldStars(db, targetRA, targetDec, fovDiagDeg).ToList();
        output.WriteLine($"catalog    {catalogAll.Count} stars within {fovDiagDeg:F2} deg (any magnitude)");

        // ---- 3. what the DETECTOR finds in the rendered frame -------------------------------
        var data = SyntheticStarFieldRenderer.Render(
            width, height, defocusSteps: 0, offsetX: 0, offsetY: 0,
            stars: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(projected),
            exposureSeconds: exposureSec, noiseSeed: 4242, apertureScaleFactor: apertureScale);
        var meta = new ImageMeta(
            Instrument: "FakeCamera1",
            ExposureStartTime: DateTimeOffset.UtcNow,
            ExposureDuration: TimeSpan.FromSeconds(exposureSec),
            FrameType: FrameType.Light,
            Telescope: "Fake",
            PixelSizeX: (float)preset.PixelSize,
            PixelSizeY: (float)preset.PixelSize,
            FocalLength: (int)focalLengthMm,
            FocusPos: 0,
            Filter: Filter.None,
            BinX: 1, BinY: 1,
            CCDTemperature: -10,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: 0f, Longitude: 0f,
            Gain: 100,
            Aperture: apertureMm,
            SensorModel: preset.SensorName);
        var image = new Image([data], BitDepth.Int16, maxValue: 65535f, minValue: 0f, pedestal: 0f, meta);
        var detected = await image.FindStarsAsync(channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: ct);
        output.WriteLine($"detected   {detected.Count} stars at snrMin=10");

        // ---- 4. does the RENDER agree with what was PLACED? --------------------------------
        // Nearest-neighbour from each detection to the placed list. This is the fake against
        // itself: a low rate here is a renderer/detector problem and nothing to do with the solver.
        var placedHits = detected.Count(d => projected.Any(p =>
            Math.Abs(p.PixelX - d.XCentroid) < 3.0 && Math.Abs(p.PixelY - d.YCentroid) < 3.0));
        output.WriteLine($"placed-hit {placedHits}/{detected.Count} detections land on a star the fake placed");

        // ---- 5. does the fake's geometry agree with a real WCS? ----------------------------
        // The WCS the fake's own projection formula implies, built the way GpuStretchPipelineTests
        // builds it. If detections do not land on catalog stars under THIS, the fake's projection
        // disagrees with the catalog it drew from, and no solver could ever confirm it.
        var trueWcs = new WCS(targetRA, targetDec)
        {
            CRPix1 = width / 2.0 + 1,
            CRPix2 = height / 2.0 + 1,
            CD1_1 = -pixelScaleDeg,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = -pixelScaleDeg,
        };
        output.WriteLine($"trueWcs    RotationDeg={trueWcs.RotationDeg:F1} det={(trueWcs.CD1_1 * trueWcs.CD2_2 - trueWcs.CD1_2 * trueWcs.CD2_1):E2}");

        var catPix = new List<(double X, double Y)>();
        foreach (var obj in catalogAll)
        {
            if (trueWcs.SkyToPixel(obj.RA, (double)obj.Dec) is { } p)
            {
                catPix.Add(p);
            }
        }
        var wcsHits = detected.Count(d => catPix.Any(p =>
            Math.Abs(p.X - d.XCentroid) < 4.0 && Math.Abs(p.Y - d.YCentroid) < 4.0));
        output.WriteLine($"wcs-hit    {wcsHits}/{detected.Count} detections land on a catalog star under the true WCS");

        // ---- 6. what the SOLVER does, with and without a hint -------------------------------
        var dim = new ImageDim(pixelScaleArcsec, width, height);
        var solver = new CatalogPlateSolver(db, new OutputLogger(output));

        foreach (var (label, origin) in new (string, WCS?)[] { ("with-hint", trueWcs), ("blind", null) })
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await solver.SolveImageAsync(image, dim, searchOrigin: origin, cancellationToken: ct);
                sw.Stop();
                output.WriteLine(result.Solution is { } s
                    ? $"{label,-10} SOLVED in {sw.ElapsedMilliseconds}ms: RA={s.CenterRA:F4}h Dec={s.CenterDec:F3} rot={s.RotationDeg:F1} scale={s.PixelScaleArcsec:F3}"
                    : $"{label,-10} NO SOLUTION after {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                output.WriteLine($"{label,-10} THREW after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            }
        }

    }
}
