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
/// Verifies the polar-align refining fast path: seed an
/// <see cref="IncrementalSolver"/> from a known WCS, render a shifted
/// synthetic star field, and confirm that
/// <see cref="IncrementalSolver.Refine"/> recovers a WCS whose CRPix has
/// moved by the same shift (sub-pixel accuracy). Also pins the residual-spike
/// fallback path: a wildly shifted frame returns null so the orchestrator can
/// fall back to a full hinted solve.
/// </summary>
[Collection("Imaging")]
public class IncrementalSolverTests(ITestOutputHelper output)
{
    private const int Width = 512;
    private const int Height = 512;
    private const int Seed = 42;
    private const double Exposure = 1.0;

    /// <summary>Pixel scale of 1.5 arcsec/pixel ≈ a typical polar-align main camera at 200mm focal length.</summary>
    private const double PixelScaleArcsec = 1.5;

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

    private static Image RenderFrame(float offsetX = 0, float offsetY = 0, int starCount = 60)
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
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
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
            // 1-based FITS reference pixel at the image centre.
            CRPix1 = (Width + 1) / 2.0,
            CRPix2 = (Height + 1) / 2.0,
            CD1_1 = pixelScaleDeg,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = pixelScaleDeg,
        };
    }

    [Fact]
    public async Task GivenNoSeed_WhenRefining_ThenReturnsNull()
    {
        var solver = new IncrementalSolver();
        var frame = RenderFrame();

        var result = solver.Refine(frame, TestContext.Current.CancellationToken);

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
        output.WriteLine($"Seeded with {anchorCount} anchors");
    }

    [Fact]
    public async Task GivenSeed_WhenRefiningIdenticalFrame_ThenWcsIsApproximatelyUnchanged()
    {
        var solver = new IncrementalSolver();
        var seedFrame = RenderFrame();
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(seedFrame, wcs, TestContext.Current.CancellationToken);

        // Identical frame: anchors should re-centroid to the same positions, M
        // should be near-identity, recovered WCS should match the seed WCS.
        var refineFrame = RenderFrame();
        var result = solver.Refine(refineFrame, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var refined = result.Value.Solution;
        refined.ShouldNotBeNull();

        // Identical frame: the canonicalised WCS should match the seed WCS
        // within centroid noise. Verify by checking that PixelToSky(centre)
        // returns the same sky coords.
        refined.Value.CenterRA.ShouldBe(wcs.CenterRA, tolerance: 1e-5);
        refined.Value.CenterDec.ShouldBe(wcs.CenterDec, tolerance: 1e-5);
        refined.Value.CD1_1.ShouldBe(wcs.CD1_1, tolerance: 1e-6);
        refined.Value.CD2_2.ShouldBe(wcs.CD2_2, tolerance: 1e-6);
        output.WriteLine($"Refine matched {result.Value.MatchedStars} anchors in {result.Value.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Theory]
    [InlineData(2.0f, 0f)]    // 2 px shift in X only
    [InlineData(0f, 3.0f)]    // 3 px shift in Y only
    [InlineData(2.5f, -1.5f)] // diagonal sub-integer shift (typical knob nudge magnitude)
    [InlineData(-4.0f, 2.0f)] // negative-X mixed shift
    public async Task GivenSeed_WhenRefiningShiftedFrame_ThenWcsTracksShift(float dx, float dy)
    {
        var solver = new IncrementalSolver();
        var seedFrame = RenderFrame();
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(seedFrame, wcs, TestContext.Current.CancellationToken);

        // Render the same star field shifted by (dx, dy). Field-shifted, mount-locked
        // is the model: every anchor moves by exactly (dx, dy).
        var refineFrame = RenderFrame(offsetX: dx, offsetY: dy);
        var result = solver.Refine(refineFrame, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var refined = result.Value.Solution;
        refined.ShouldNotBeNull();

        // Verify via reprojection: the seed-frame centre sky should now project
        // to the shifted pixel (frameCenter + (dx, dy)) under the refined WCS.
        // CRPix is canonicalised back to frame centre, so we can't use a CRPix
        // delta check; reprojection is the real invariant we care about
        // (it's what the orchestrator uses for axis recovery).
        var seedCentreSky = wcs.PixelToSky(wcs.CRPix1, wcs.CRPix2);
        seedCentreSky.ShouldNotBeNull();
        var (ra, dec) = seedCentreSky.Value;
        var predicted = refined.Value.SkyToPixel(ra, dec);
        predicted.ShouldNotBeNull();
        var (px, py) = predicted.Value;
        // Sub-pixel accuracy: centroid is photon-noise limited but the affine
        // fit averages ~30 anchors. 0.5 px is roughly 1 arcsec at our scale,
        // well below the polar-align gates.
        px.ShouldBe(wcs.CRPix1 + dx, tolerance: 0.5);
        py.ShouldBe(wcs.CRPix2 + dy, tolerance: 0.5);
        output.WriteLine($"Shift ({dx}, {dy}): seed-centre sky projects to ({px - wcs.CRPix1:F2}, {py - wcs.CRPix2:F2}) px shift; {result.Value.MatchedStars} anchors matched in {result.Value.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task GivenSeed_WhenRefiningHugelyShiftedFrame_ThenReturnsNullForFallback()
    {
        var solver = new IncrementalSolver();
        var seedFrame = RenderFrame();
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(seedFrame, wcs, TestContext.Current.CancellationToken);

        // Shift larger than the ROI half-size: every anchor's prior is now in
        // a completely different patch of sky, so most ROIs will fail the SNR
        // gate. The few that survive (random hits) should fail the residual
        // ceiling. Either way the orchestrator gets null and falls back to a
        // full hinted solve.
        var refineFrame = RenderFrame(offsetX: 50.0f, offsetY: 50.0f);
        var result = solver.Refine(refineFrame, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        output.WriteLine("Huge-shift refine correctly returned null (fallback path).");
    }

    [Fact]
    public async Task GivenSeed_WhenSequentiallyRefiningSmallShifts_ThenAnchorsTrackTheField()
    {
        // Sub-arcmin knob nudges in sequence: each frame shifts ~1 px from the
        // previous. The anchor list updates after every refine, so the
        // *cumulative* shift from the original seed can grow well past the ROI
        // half-size as long as each step is small. Mirrors what happens during
        // a real polar-align refine session: the user turns the knob, the
        // field drifts a pixel at a time, the solver tracks.
        var solver = new IncrementalSolver();
        var seedFrame = RenderFrame();
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(seedFrame, wcs, TestContext.Current.CancellationToken);

        var seedCentreSky = wcs.PixelToSky(wcs.CRPix1, wcs.CRPix2);
        seedCentreSky.ShouldNotBeNull();
        var (seedRa, seedDec) = seedCentreSky.Value;

        float cumulativeX = 0;
        float cumulativeY = 0;
        for (int step = 1; step <= 8; step++)
        {
            cumulativeX += 1.0f;
            cumulativeY += 0.5f;
            var refineFrame = RenderFrame(offsetX: cumulativeX, offsetY: cumulativeY);
            var result = solver.Refine(refineFrame, TestContext.Current.CancellationToken);

            result.ShouldNotBeNull($"Step {step} should still refine successfully (cumulative shift {cumulativeX}, {cumulativeY})");
            var refined = result.Value.Solution!.Value;
            // The seed-frame centre sky should now project to the cumulative shifted pixel.
            var predicted = refined.SkyToPixel(seedRa, seedDec);
            predicted.ShouldNotBeNull();
            var (px, py) = predicted.Value;
            px.ShouldBe(wcs.CRPix1 + cumulativeX, tolerance: 0.7,
                $"Step {step}: X drift exceeds tolerance");
            py.ShouldBe(wcs.CRPix2 + cumulativeY, tolerance: 0.7,
                $"Step {step}: Y drift exceeds tolerance");
        }
        output.WriteLine($"Tracked 8 sequential 1px nudges to total ({cumulativeX}, {cumulativeY}) without losing the anchor list.");
    }

    [Theory]
    [InlineData(0.5, 0f, 0f)]      // pure rotation, half a degree
    [InlineData(0.25, 3.0f, 0f)]   // tiny rotation + X translation
    [InlineData(-0.4, -2.0f, 1.5f)] // negative rotation + diagonal translation
    public async Task GivenSeed_WhenRefiningRotatedAndShiftedFrame_ThenWcsMatchesAffine(
        double rotationDeg, float dx, float dy)
    {
        // The translation-only renderer can't express rotation, so build the
        // synthetic pair by hand: place stars at known positions for frame 1,
        // then for frame 2 apply the affine (rotation around image centre +
        // translation) to each position and re-render.
        var solver = new IncrementalSolver();
        var stars = MakeStars(count: 60, seed: 99);
        var seedFrame = RenderStarsAt(stars);
        var wcs = MakeKnownWcs();
        await solver.SeedAsync(seedFrame, wcs, TestContext.Current.CancellationToken);
        solver.IsSeeded.ShouldBeTrue("seed must succeed for rotation test");

        var rotRad = rotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(rotRad);
        var sin = Math.Sin(rotRad);
        // Rotate around the image centre in 0-based pixel space (Width/2, Height/2),
        // matching the convention used elsewhere when reasoning about centred fields.
        var cxPixel = (Width - 1) / 2.0;
        var cyPixel = (Height - 1) / 2.0;
        var rotated = new (double X, double Y, double Flux)[stars.Length];
        for (int i = 0; i < stars.Length; i++)
        {
            var (sx, sy, sf) = stars[i];
            var rx = sx - cxPixel;
            var ry = sy - cyPixel;
            var nx = cxPixel + (rx * cos - ry * sin) + dx;
            var ny = cyPixel + (rx * sin + ry * cos) + dy;
            rotated[i] = (nx, ny, sf);
        }
        var refineFrame = RenderStarsAt(rotated);

        var result = solver.Refine(refineFrame, TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        var refined = result.Value.Solution!.Value;

        // Verification: pick a small set of catalog directions, project them
        // through the recovered WCS, and confirm the predicted pixel matches
        // where the rotated stars actually sit. This validates both the CD
        // matrix update (rotation) and the CRPix update (translation) in one
        // go without us having to derive their exact closed forms here.
        // We use the seed-frame WCS to back out each star's J2000 (RA, Dec)
        // from its frame-1 pixel, then check refined.SkyToPixel matches its
        // frame-2 pixel.
        int checkedStars = 0;
        double maxErr = 0;
        for (int i = 0; i < stars.Length; i += 5)
        {
            var (sx, sy, _) = stars[i];
            var sky = wcs.PixelToSky(sx + 1.0, sy + 1.0);
            sky.ShouldNotBeNull();
            var (ra, dec) = sky.Value;
            var predicted = refined.SkyToPixel(ra, dec);
            predicted.ShouldNotBeNull();
            var (predX, predY) = predicted.Value;
            // refined.SkyToPixel returns 1-based; rotated[i] is 0-based.
            var errX = predX - 1.0 - rotated[i].X;
            var errY = predY - 1.0 - rotated[i].Y;
            var err = Math.Sqrt(errX * errX + errY * errY);
            maxErr = Math.Max(maxErr, err);
            checkedStars++;
        }
        checkedStars.ShouldBeGreaterThan(5);
        // 1 px slack covers centroid noise (Gaussian PSF at sigma=1.6 + bg
        // photon noise, no shot noise) compounded across both frames.
        maxErr.ShouldBeLessThan(1.0, $"max sky-to-pixel reprojection error {maxErr:F2} px");
        output.WriteLine($"Rotation {rotationDeg:F2}° + ({dx}, {dy}) shift: refined WCS reprojects {checkedStars} stars within {maxErr:F2} px (matched {result.Value.MatchedStars} anchors)");
    }
}
