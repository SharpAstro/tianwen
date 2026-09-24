using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the polar-align refining fast path: seed an <see cref="IncrementalSolver"/> from a known WCS,
/// render the same star field moved by a known affine, and confirm that
/// <see cref="IncrementalSolver.RefineAsync"/> returns a WCS that puts every moved star on the sky
/// position the seed WCS gave it before the move. Every refine quad-matches against the FROZEN seed
/// star list, never the previous refine, so a run of refines cannot drift, and a field that shares
/// no quads with the seed returns null so the orchestrator falls back to a full solve.
/// </summary>
/// <remarks>
/// Sky agreement is measured as an angular separation, converted to pixels at
/// <see cref="PixelScaleArcsec"/>, never by comparing <see cref="WCS.CenterRA"/>: the test WCS sits on
/// RA 0h, where a sky position one pixel west of the centre reads 23.9999h, and a refined WCS moves
/// its reference pixel to wherever the solver canonicalises it, so only the sky at a named pixel is
/// comparable between two solutions.
/// </remarks>
[Collection("Imaging")]
public class IncrementalSolverTests(ITestOutputHelper output)
{
    private const int Width = 512;
    private const int Height = 512;
    private const int Seed = 42;
    private const double Exposure = 1.0;

    /// <summary>Pixel scale of 1.5 arcsec/pixel ≈ a typical polar-align main camera at 200mm focal length.</summary>
    private const double PixelScaleArcsec = 1.5;

    /// <summary>The frame centre in the 0-based detected-centroid coordinates a <see cref="WCS"/> uses in memory.</summary>
    private const double CentreX = (Width - 1) / 2.0;
    private const double CentreY = (Height - 1) / 2.0;

    /// <summary>
    /// Largest sky disagreement, in pixels, a refine of a moved star field may show at any checked
    /// star or at the frame centre. Measured at 0.031 px at worst (the 2.5, -1.5 shift) and 0.006 to
    /// 0.027 px elsewhere across every shift, rotation and sequential case here (the test output logs
    /// each one); the bound leaves a margin of six over that for centroid noise, and 0.2 px is 0.3",
    /// far under the polar-align gates.
    /// </summary>
    private const double MaxSkyErrorPx = 0.2;

    /// <summary>
    /// Builds a deterministic list of star positions inside the frame margin.
    /// Used by <see cref="RenderStarsAt"/> to set up rotation-test pairs where
    /// frame 2 is frame 1 transformed by a known affine -- the standard
    /// translation-only renderer can't express rotation.
    /// </summary>
    private static (double X, double Y, double Flux)[] MakeStars(int count, int seed)
    {
        var rng = new Random(seed);
        var stars = new (double, double, double)[count];
        const int margin = 25;
        for (int i = 0; i < count; i++)
        {
            var x = margin + rng.NextDouble() * (Width - 2 * margin);
            var y = margin + rng.NextDouble() * (Height - 2 * margin);
            var mag = 5.0 + rng.NextDouble() * 6.0;
            var flux = 10000.0 * Math.Pow(10, -0.4 * (mag - 5.0));
            stars[i] = (x, y, flux);
        }
        return stars;
    }

    /// <summary>
    /// Renders Gaussian-PSF stars at the given (X, Y, Flux) tuples. Pure white
    /// noise sky background; no shot noise per pixel, just enough sky-tilt to
    /// give the centroid a sensible noise floor for the SNR gate.
    /// </summary>
    private static Image RenderStarsAt((double X, double Y, double Flux)[] positions, double sigma = 1.6, double sky = 50.0, double readNoise = 3.0, int noiseSeed = 7)
    {
        var data = new float[Height, Width];
        var bgRng = new Random(noiseSeed);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                data[y, x] = (float)(sky + bgRng.NextDouble() * readNoise);
            }
        }

        int psfRadius = (int)Math.Ceiling(sigma * 4);
        var sigma2x2 = 2.0 * sigma * sigma;
        foreach (var (cx, cy, flux) in positions)
        {
            var norm = flux / (Math.PI * sigma2x2);
            int xMin = Math.Max(0, (int)(cx - psfRadius));
            int xMax = Math.Min(Width - 1, (int)(cx + psfRadius));
            int yMin = Math.Max(0, (int)(cy - psfRadius));
            int yMax = Math.Min(Height - 1, (int)(cy + psfRadius));
            for (int y = yMin; y <= yMax; y++)
            {
                var dy = y - cy;
                for (int x = xMin; x <= xMax; x++)
                {
                    var dx = x - cx;
                    var v = norm * Math.Exp(-(dx * dx + dy * dy) / sigma2x2);
                    data[y, x] += (float)v;
                }
            }
        }

        var min = float.MaxValue;
        var max = float.MinValue;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                var v = data[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(Exposure),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([data], BitDepth.Float32, max, min, 0, meta);
    }

    private static Image RenderFrame(float offsetX = 0, float offsetY = 0, int starCount = 60, int focalLength = 500)
    {
        var data = SyntheticStarFieldRenderer.Render(
            width: Width,
            height: Height,
            defocusSteps: 0,                    // perfect focus -- tight stars, easy to centroid
            offsetX: offsetX,
            offsetY: offsetY,
            hyperbolaA: 2.0,
            hyperbolaB: 50.0,
            exposureSeconds: Exposure,
            skyBackground: 50.0,
            readNoise: 3.0,
            starCount: starCount,
            seed: Seed);

        // Compute min/max for the Image metadata (required for FindStarsAsync to work).
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var v = data[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(Exposure),
            FrameType.Light, "", 3.76f, 3.76f, focalLength, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([data], BitDepth.Float32, max, min, 0, meta);
    }

    /// <summary>
    /// Builds a CD-matrix WCS centred on (0h, 0°) at <see cref="PixelScaleArcsec"/>
    /// arcsec/pixel with no rotation and no flip. Mirrors the form
    /// <see cref="CatalogPlateSolver"/> emits, so <see cref="WCS.PixelToSky"/>
    /// and <see cref="WCS.SkyToPixel"/> round-trip without surprises.
    /// </summary>
    private static WCS MakeKnownWcs()
    {
        var pixelScaleDeg = PixelScaleArcsec / 3600.0;
        return new WCS(0.0, 0.0)
        {
            // Reference pixel at the image centre, in the 0-based detected-centroid frame a WCS
            // uses in memory (only a FITS header carries the 1-based form).
            CRPix1 = CentreX,
            CRPix2 = CentreY,
            CD1_1 = pixelScaleDeg,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = pixelScaleDeg,
        };
    }

    /// <summary>
    /// Moves every star by a rotation of <paramref name="rotationDeg"/> about the frame centre
    /// followed by the translation (<paramref name="dx"/>, <paramref name="dy"/>): the field as the
    /// mount sees it after a knob nudge. Flux is carried unchanged.
    /// </summary>
    private static (double X, double Y, double Flux)[] Move((double X, double Y, double Flux)[] stars, double rotationDeg, double dx, double dy)
    {
        var moved = new (double X, double Y, double Flux)[stars.Length];
        for (int i = 0; i < stars.Length; i++)
        {
            var (x, y) = MovePoint(stars[i].X, stars[i].Y, rotationDeg, dx, dy);
            moved[i] = (x, y, stars[i].Flux);
        }
        return moved;
    }

    private static (double X, double Y) MovePoint(double x, double y, double rotationDeg, double dx, double dy)
    {
        var (sin, cos) = Math.SinCos(double.DegreesToRadians(rotationDeg));
        var rx = x - CentreX;
        var ry = y - CentreY;
        return (CentreX + (rx * cos - ry * sin) + dx, CentreY + (rx * sin + ry * cos) + dy);
    }

    /// <summary>Great-circle separation in arcsec between two (RA hours, Dec degrees) positions (haversine, safe across RA 0h).</summary>
    private static double SeparationArcsec((double RA, double Dec) a, (double RA, double Dec) b)
    {
        var ra1 = a.RA * Math.PI / 12.0;
        var ra2 = b.RA * Math.PI / 12.0;
        var dec1 = double.DegreesToRadians(a.Dec);
        var dec2 = double.DegreesToRadians(b.Dec);
        var sinDDec = Math.Sin((dec2 - dec1) / 2);
        var sinDRa = Math.Sin((ra2 - ra1) / 2);
        var h = sinDDec * sinDDec + Math.Cos(dec1) * Math.Cos(dec2) * sinDRa * sinDRa;
        return double.RadiansToDegrees(2 * Math.Asin(Math.Min(1.0, Math.Sqrt(h)))) * 3600.0;
    }

    /// <summary>
    /// How far apart, in pixels, the sky the seed WCS gives (<paramref name="seedX"/>, <paramref name="seedY"/>)
    /// and the sky the refined WCS gives the same point after the move, (<paramref name="liveX"/>, <paramref name="liveY"/>).
    /// </summary>
    private static double SkyErrorPx(WCS seed, WCS refined, double seedX, double seedY, double liveX, double liveY)
    {
        var before = seed.PixelToSky(seedX, seedY) ?? throw new InvalidOperationException("seed WCS cannot deproject");
        var after = refined.PixelToSky(liveX, liveY) ?? throw new InvalidOperationException("refined WCS cannot deproject");
        return SeparationArcsec(before, after) / PixelScaleArcsec;
    }

    /// <summary>
    /// The largest <see cref="SkyErrorPx"/> over every fifth star and the frame centre, each carried
    /// through the known move. Returns the count of points checked alongside.
    /// </summary>
    private static (double MaxErrorPx, int Checked) MaxSkyErrorAfterMove(WCS seed, WCS refined, (double X, double Y, double Flux)[] stars, double rotationDeg, double dx, double dy)
    {
        var (cx, cy) = MovePoint(CentreX, CentreY, rotationDeg, dx, dy);
        var maxErr = SkyErrorPx(seed, refined, CentreX, CentreY, cx, cy);
        var count = 1;
        for (int i = 0; i < stars.Length; i += 5)
        {
            var (x, y) = MovePoint(stars[i].X, stars[i].Y, rotationDeg, dx, dy);
            maxErr = Math.Max(maxErr, SkyErrorPx(seed, refined, stars[i].X, stars[i].Y, x, y));
            count++;
        }
        return (maxErr, count);
    }

    /// <summary>
    /// The live path as polar alignment runs it on a finely sampled camera: the seed bins the frame (2x
    /// here, 3.76 um at 1100 mm being 0.705"/px against the 1.5"/px target) and every refine bins again at
    /// the seed's factor, into planes rented from the pool and returned after the detection. Refining the
    /// very frame that seeded must hand back the seed's own solution.
    /// </summary>
    [Fact]
    public async Task ABinnedSeedAndARefineOfTheSameFrameGiveTheSeedsSolution()
    {
        var ct = TestContext.Current.CancellationToken;
        var frame = RenderFrame(focalLength: 1100);
        var wcs = MakeKnownWcs();
        var solver = new IncrementalSolver();

        var anchors = await solver.SeedAsync(frame, wcs, ct);

        anchors.ShouldBeGreaterThanOrEqualTo(solver.MinAnchors);
        solver.SeedDetectionScale.ShouldBe(2, "premise: this frame is binned");

        var result = await solver.RefineAsync(frame, ct);

        result.ShouldNotBeNull("the frame that seeded must refine");
        var refined = result.Value.Solution;
        refined.ShouldNotBeNull();
        var centreErr = SkyErrorPx(wcs, refined.Value, CentreX, CentreY, CentreX, CentreY);
        centreErr.ShouldBeLessThan(MaxSkyErrorPx, "the refined WCS must put the frame centre where the seed did");
        output.WriteLine($"seeded with {anchors} anchors at bin {solver.SeedDetectionScale}; refine matched {result.Value.MatchedStars} in {result.Value.Elapsed.TotalMilliseconds:F1} ms; centre off by {centreErr:F4} px");
    }

    [Fact]
    public async Task GivenNoSeed_WhenRefining_ThenReturnsNull()
    {
        var solver = new IncrementalSolver();
        var frame = RenderFrame();

        var result = await solver.RefineAsync(frame, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        solver.IsSeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task GivenStarFieldAndKnownWcs_WhenSeeding_ThenAnchorsAreCaptured()
    {
        var solver = new IncrementalSolver();
        var frame = RenderFrame();
        var wcs = MakeKnownWcs();

        var anchorCount = await solver.SeedAsync(frame, wcs, TestContext.Current.CancellationToken);

        anchorCount.ShouldBeGreaterThanOrEqualTo(solver.MinAnchors);
        solver.IsSeeded.ShouldBeTrue();
        solver.AnchorCount.ShouldBe(anchorCount);
        solver.AnchorCount.ShouldBeGreaterThanOrEqualTo(solver.MinAnchors);
        solver.CurrentWcs.ShouldBe(wcs, "the seed WCS is frozen as given");
        output.WriteLine($"Seeded with {anchorCount} anchors");
    }

    /// <summary>
    /// The unbinned twin of <see cref="ABinnedSeedAndARefineOfTheSameFrameGiveTheSeedsSolution"/>:
    /// at 1.55"/px the frame is already coarser than the 1.5"/px detection target, so the seed and the
    /// refine detect at full resolution, match the identical star list, and must hand back the seed's
    /// solution at every pixel, not only the centre.
    /// </summary>
    [Fact]
    public async Task GivenSeed_WhenRefiningIdenticalFrame_ThenWcsIsApproximatelyUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var solver = new IncrementalSolver();
        var frame = RenderFrame();
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(frame, wcs, ct);
        solver.SeedDetectionScale.ShouldBe(1, "premise: this frame is not binned");

        var result = await solver.RefineAsync(frame, ct);

        result.ShouldNotBeNull();
        var refined = result.Value.Solution;
        refined.ShouldNotBeNull();

        double maxErr = 0;
        foreach (var (x, y) in new[] { (CentreX, CentreY), (0.0, 0.0), (Width - 1.0, 0.0), (0.0, Height - 1.0), (Width - 1.0, Height - 1.0) })
        {
            maxErr = Math.Max(maxErr, SkyErrorPx(wcs, refined.Value, x, y, x, y));
        }
        maxErr.ShouldBeLessThan(MaxSkyErrorPx);
        // The linear part is the seed's: an identity affine leaves the CD matrix alone. 1e-8 deg is
        // 2.4e-5 of a pixel's 4.2e-4 deg.
        refined.Value.CD1_1.ShouldBe(wcs.CD1_1, tolerance: 1e-8);
        refined.Value.CD1_2.ShouldBe(wcs.CD1_2, tolerance: 1e-8);
        refined.Value.CD2_1.ShouldBe(wcs.CD2_1, tolerance: 1e-8);
        refined.Value.CD2_2.ShouldBe(wcs.CD2_2, tolerance: 1e-8);
        output.WriteLine($"Refine matched {result.Value.MatchedStars} stars in {result.Value.Elapsed.TotalMilliseconds:F1} ms; max sky error over centre + corners {maxErr:F4} px");
    }

    [Theory]
    [InlineData(2.0f, 0f)]    // 2 px shift in X only
    [InlineData(0f, 3.0f)]    // 3 px shift in Y only
    [InlineData(2.5f, -1.5f)] // diagonal sub-integer shift (typical knob nudge magnitude)
    [InlineData(-4.0f, 2.0f)] // negative-X mixed shift
    public async Task GivenSeed_WhenRefiningShiftedFrame_ThenWcsTracksShift(float dx, float dy)
    {
        var ct = TestContext.Current.CancellationToken;
        var solver = new IncrementalSolver();
        var stars = MakeStars(count: 60, seed: 99);
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(RenderStarsAt(stars), wcs, ct);
        solver.IsSeeded.ShouldBeTrue();

        // Field-shifted, mount-locked: every star moves by exactly (dx, dy). Fresh background noise,
        // so the live detections are not the seed's to the last bit.
        var result = await solver.RefineAsync(RenderStarsAt(Move(stars, 0, dx, dy), noiseSeed: 8), ct);

        result.ShouldNotBeNull();
        var refined = result.Value.Solution;
        refined.ShouldNotBeNull();

        var (maxErr, checkedPoints) = MaxSkyErrorAfterMove(wcs, refined.Value, stars, 0, dx, dy);
        maxErr.ShouldBeLessThan(MaxSkyErrorPx, $"a shifted star must keep its sky position under the refined WCS (max {maxErr:F4} px over {checkedPoints} points)");
        output.WriteLine($"Shift ({dx}, {dy}): max sky error {maxErr:F4} px over {checkedPoints} points; {result.Value.MatchedStars} stars in {result.Value.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>
    /// A shift alone no longer defeats the matcher (quad invariants do not care where a field sits), so
    /// the fallback case is a field that shares no quads with the seed: a different sky, as after an
    /// unannounced slew. The refine must return null so the orchestrator falls back to a full solve.
    /// </summary>
    [Fact]
    public async Task GivenSeed_WhenRefiningAnUnrelatedStarField_ThenReturnsNullForFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var solver = new IncrementalSolver();
        await solver.SeedAsync(RenderStarsAt(MakeStars(count: 60, seed: 99)), MakeKnownWcs(), ct);
        solver.IsSeeded.ShouldBeTrue();

        var unrelated = RenderStarsAt(MakeStars(count: 60, seed: 1234), noiseSeed: 8);
        var result = await solver.RefineAsync(unrelated, ct);

        result.ShouldBeNull("no quad of an unrelated field can match the seed's");
        solver.IsSeeded.ShouldBeTrue("a failed refine keeps the seed");
    }

    /// <summary>
    /// The drift-free property: knob nudges in sequence walk the field a pixel at a time, and every
    /// refine aligns to the frozen seed, not the previous refine. So the last frame of the walk must be
    /// solved exactly as well as the same frame refined once by a solver that saw nothing in between.
    /// </summary>
    [Fact]
    public async Task GivenSeed_WhenSequentiallyRefiningSmallShifts_ThenErrorDoesNotAccumulate()
    {
        const int Steps = 8;
        var ct = TestContext.Current.CancellationToken;
        var stars = MakeStars(count: 60, seed: 99);
        var seedFrame = RenderStarsAt(stars);
        var wcs = MakeKnownWcs();
        var solver = new IncrementalSolver();
        await solver.SeedAsync(seedFrame, wcs, ct);
        solver.IsSeeded.ShouldBeTrue();

        double lastErr = double.NaN;
        for (int step = 1; step <= Steps; step++)
        {
            var result = await solver.RefineAsync(RenderStarsAt(Move(stars, 0, step * 1.0, step * 0.5), noiseSeed: 100 + step), ct);

            result.ShouldNotBeNull($"step {step} must refine (cumulative shift {step * 1.0}, {step * 0.5})");
            var refined = result.Value.Solution ?? throw new InvalidOperationException("a refine result carries a solution");
            (lastErr, _) = MaxSkyErrorAfterMove(wcs, refined, stars, 0, step * 1.0, step * 0.5);
            lastErr.ShouldBeLessThan(MaxSkyErrorPx, $"step {step}");
            output.WriteLine($"step {step}: max sky error {lastErr:F4} px");
        }

        // The same last frame, refined once by a solver seeded identically.
        var fresh = new IncrementalSolver();
        await fresh.SeedAsync(seedFrame, wcs, ct);
        var single = await fresh.RefineAsync(RenderStarsAt(Move(stars, 0, Steps * 1.0, Steps * 0.5), noiseSeed: 100 + Steps), ct);
        single.ShouldNotBeNull();
        var singleRefined = single.Value.Solution ?? throw new InvalidOperationException("a refine result carries a solution");
        var (singleErr, _) = MaxSkyErrorAfterMove(wcs, singleRefined, stars, 0, Steps * 1.0, Steps * 0.5);

        lastErr.ShouldBeLessThanOrEqualTo(singleErr + 1e-9, $"frame {Steps} of a walk must be no worse than a single refine of it ({lastErr:F6} vs {singleErr:F6} px)");
        output.WriteLine($"after {Steps} refines: {lastErr:F6} px; a single refine of the same frame: {singleErr:F6} px");
    }

    [Theory]
    [InlineData(0.5, 0f, 0f)]      // pure rotation, half a degree
    [InlineData(0.25, 3.0f, 0f)]   // tiny rotation + X translation
    [InlineData(-0.4, -2.0f, 1.5f)] // negative rotation + diagonal translation
    public async Task GivenSeed_WhenRefiningRotatedAndShiftedFrame_ThenWcsMatchesAffine(
        double rotationDeg, float dx, float dy)
    {
        // The translation-only renderer can't express rotation, so build the pair by hand: frame 2
        // is frame 1 rotated about the image centre and then translated.
        var ct = TestContext.Current.CancellationToken;
        var solver = new IncrementalSolver();
        var stars = MakeStars(count: 60, seed: 99);
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(RenderStarsAt(stars), wcs, ct);
        solver.IsSeeded.ShouldBeTrue("seed must succeed for rotation test");

        var result = await solver.RefineAsync(RenderStarsAt(Move(stars, rotationDeg, dx, dy), noiseSeed: 8), ct);

        result.ShouldNotBeNull();
        var refined = result.Value.Solution ?? throw new InvalidOperationException("a refine result carries a solution");

        // Every moved star (and the moved frame centre) must sit on the sky the seed WCS gave it
        // before the move, which checks the CD matrix (rotation) and the reference pixel
        // (translation) together without deriving their closed forms here.
        var (maxErr, checkedPoints) = MaxSkyErrorAfterMove(wcs, refined, stars, rotationDeg, dx, dy);
        checkedPoints.ShouldBeGreaterThan(5);
        maxErr.ShouldBeLessThan(MaxSkyErrorPx, $"max sky error {maxErr:F4} px over {checkedPoints} points");

        // And the linear part carries the rotation: a live pixel is R * seed pixel + t, so the sky of a
        // live pixel is CD * R^-1 applied to its offset, which is the expected CD matrix.
        var (sin, cos) = Math.SinCos(double.DegreesToRadians(rotationDeg));
        var expectedCd11 = wcs.CD1_1 * cos - wcs.CD1_2 * sin;
        var expectedCd12 = wcs.CD1_1 * sin + wcs.CD1_2 * cos;
        var expectedCd21 = wcs.CD2_1 * cos - wcs.CD2_2 * sin;
        var expectedCd22 = wcs.CD2_1 * sin + wcs.CD2_2 * cos;
        // 1e-3 of a pixel's scale: the rotation is recovered to ~0.06 degree or better.
        var cdTolerance = wcs.CD1_1 * 1e-3;
        refined.CD1_1.ShouldBe(expectedCd11, tolerance: cdTolerance);
        refined.CD1_2.ShouldBe(expectedCd12, tolerance: cdTolerance);
        refined.CD2_1.ShouldBe(expectedCd21, tolerance: cdTolerance);
        refined.CD2_2.ShouldBe(expectedCd22, tolerance: cdTolerance);
        output.WriteLine($"Rotation {rotationDeg:F2} deg + ({dx}, {dy}) shift: max sky error {maxErr:F4} px over {checkedPoints} points (matched {result.Value.MatchedStars} stars)");
    }
}
