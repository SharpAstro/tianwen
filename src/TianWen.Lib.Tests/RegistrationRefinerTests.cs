using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Coverage for both registration-refinement variants in
/// <see cref="RegistrationRefiner"/>: the legacy translation-only
/// <c>RefineTranslation</c> and the full 2D Procrustes <c>RefineRigid</c>
/// (rotation + isotropic scale + translation). All tests build
/// <see cref="SortedStarList"/> instances directly via the public
/// <c>SortedStarList(new StarList(new ConcurrentBag&lt;ImagedStar&gt;(...)))</c>
/// chain -- no FITS I/O needed, so the cases run in &lt; 10 ms each.
/// </summary>
[Collection("Imaging")]
public class RegistrationRefinerTests
{
    /// <summary>
    /// Build a <see cref="SortedStarList"/> from a sequence of 2D points.
    /// Star photometry fields (HFD / FWHM / SNR / Flux) are filled with
    /// plausible non-zero values so any future per-star quality filter
    /// inside the refiner doesn't accidentally reject the synthetic data.
    /// </summary>
    private static SortedStarList Stars(IEnumerable<Vector2> points)
    {
        var bag = new ConcurrentBag<ImagedStar>();
        foreach (var p in points)
        {
            bag.Add(new ImagedStar(HFD: 2f, StarFWHM: 2f, SNR: 100f, Flux: 1000f,
                XCentroid: p.X, YCentroid: p.Y, Ellipticity: 0f));
        }
        return new SortedStarList(new StarList(bag));
    }

    /// <summary>
    /// Seeded star field laid out on a coarse grid with small per-point
    /// jitter. The grid stride is 80 px so the typical inter-point
    /// distance is much larger than the 5 px nearest-neighbour matching
    /// tolerance — that prevents the synthetic test cases from
    /// accidentally pairing a light star to a non-partner ref star
    /// (which would inject phantom rotation/scale into the fit). Wide
    /// enough overall extent ([50..530]² nominal) that the cross-
    /// covariance accumulators have meaningful spread.
    /// </summary>
    private static Vector2[] SeededStarField(int count, int seed = 1234)
    {
        const float gridStride = 80f;
        const float jitter = 15f;
        const int cols = 6;
        var rng = new Random(seed);
        var points = new List<Vector2>(count);
        for (var row = 0; points.Count < count; row++)
        {
            for (var col = 0; col < cols && points.Count < count; col++)
            {
                var cx = 50f + (col + 0.5f) * gridStride + (float)(jitter * (rng.NextDouble() - 0.5));
                var cy = 50f + (row + 0.5f) * gridStride + (float)(jitter * (rng.NextDouble() - 0.5));
                points.Add(new Vector2(cx, cy));
            }
        }
        return points.ToArray();
    }

    [Fact]
    public void RefineRigid_PureTranslation_RecoversOffset()
    {
        // Ground truth: reference = light shifted by (3, -2) px. bulk = identity.
        var light = SeededStarField(30);
        var refPoints = light.Select(p => p + new Vector2(3f, -2f));

        var result = RegistrationRefiner.RefineRigid(
            Stars(light), Stars(refPoints), Matrix3x2.Identity);

        result.MatchedCount.ShouldBe(30);
        result.RotationDeg.ShouldBe(0f, tolerance: 1e-3f);
        result.Scale.ShouldBe(1f, tolerance: 1e-5f);
        result.Tx.ShouldBe(3f, tolerance: 1e-3f);
        result.Ty.ShouldBe(-2f, tolerance: 1e-3f);
        result.RmsResidualPx.ShouldBeLessThan(0.01f);
        result.Refined.M31.ShouldBe(3f, tolerance: 1e-3f);
        result.Refined.M32.ShouldBe(-2f, tolerance: 1e-3f);
    }

    [Fact]
    public void RefineRigid_PureRotation_RecoversAngle()
    {
        // 0.5° rotation about (200, 200). Bulk = identity.
        var light = SeededStarField(30);
        var rot = Matrix3x2.CreateRotation(0.5f * MathF.PI / 180f, new Vector2(200f, 200f));
        var refPoints = light.Select(p => Vector2.Transform(p, rot));

        var result = RegistrationRefiner.RefineRigid(
            Stars(light), Stars(refPoints), Matrix3x2.Identity);

        result.MatchedCount.ShouldBe(30);
        result.RotationDeg.ShouldBe(0.5f, tolerance: 1e-3f);
        result.Scale.ShouldBe(1f, tolerance: 1e-5f);
        result.RmsResidualPx.ShouldBeLessThan(0.01f);
    }

    [Fact]
    public void RefineRigid_RotationPlusScale_RecoversBoth()
    {
        // Realistic per-frame residual: 0.3° rotation + 1.0015× scale
        // around the field centroid. Combined into a single Matrix3x2
        // around the centre of the seeded field.
        var light = SeededStarField(30);
        var centroid = new Vector2(
            light.Average(p => p.X),
            light.Average(p => p.Y));
        var rot = Matrix3x2.CreateRotation(0.3f * MathF.PI / 180f, centroid);
        var scaleAround = Matrix3x2.CreateScale(1.0015f, centroid);
        var truth = rot * scaleAround;
        var refPoints = light.Select(p => Vector2.Transform(p, truth));

        var result = RegistrationRefiner.RefineRigid(
            Stars(light), Stars(refPoints), Matrix3x2.Identity);

        result.MatchedCount.ShouldBe(30);
        result.RotationDeg.ShouldBe(0.3f, tolerance: 1e-3f);
        result.Scale.ShouldBe(1.0015f, tolerance: 1e-5f);
        result.RmsResidualPx.ShouldBeLessThan(0.01f);
    }

    [Fact]
    public void RefineRigid_OnTopOfBulkAffine_ComposesCorrectly()
    {
        // bulkAffine carries a non-identity translation; ground-truth
        // adds a small rotation residual. The refined composed transform
        // should reproduce the full ground truth at the test points.
        var light = SeededStarField(30);
        var bulkAffine = Matrix3x2.CreateTranslation(10f, 5f);
        var residualRot = Matrix3x2.CreateRotation(0.2f * MathF.PI / 180f, new Vector2(210f, 205f));
        var residualTx = Matrix3x2.CreateTranslation(1f, 0.5f);
        var truthDelta = residualRot * residualTx;
        var truth = bulkAffine * truthDelta;
        var refPoints = light.Select(p => Vector2.Transform(p, truth));

        var result = RegistrationRefiner.RefineRigid(
            Stars(light), Stars(refPoints), bulkAffine);

        result.MatchedCount.ShouldBe(30);

        // Composed transform should map every test point to its ground-
        // truth position within 0.05 px (tighter than the matching
        // tolerance of 5 px so we know it's a real refit, not the
        // unrefined bulk affine).
        foreach (var p in light)
        {
            var refinedPos = Vector2.Transform(p, result.Refined);
            var truthPos = Vector2.Transform(p, truth);
            (refinedPos - truthPos).Length().ShouldBeLessThan(0.05f);
        }
    }

    [Fact]
    public void RefineRigid_FewerThanMinMatched_FallsBackToTranslation()
    {
        // 5 matched pairs (< minMatchedStars=8). Should fall back to
        // RefineTranslation -- still computes median (dx, dy) but reports
        // scale=1, rot=0, and rms=0 (translation-only fallback signature).
        var light = SeededStarField(5);
        var refPoints = light.Select(p => p + new Vector2(2f, 1f));

        var result = RegistrationRefiner.RefineRigid(
            Stars(light), Stars(refPoints), Matrix3x2.Identity);

        result.MatchedCount.ShouldBe(5);
        result.Scale.ShouldBe(1f);
        result.RotationDeg.ShouldBe(0f);
        // Translation-only fallback applies the median (dx, dy) directly
        // to M31/M32. The fallback path doesn't compute RMS so it stays
        // zero -- that's a deliberate marker that the rigid branch
        // didn't run.
        result.RmsResidualPx.ShouldBe(0f);
        result.Refined.M31.ShouldBe(2f, tolerance: 1e-3f);
        result.Refined.M32.ShouldBe(1f, tolerance: 1e-3f);
    }

    [Fact]
    public void RefineRigid_CollinearPoints_FallsBackGracefully()
    {
        // 20 stars on a horizontal line, perfect identity alignment.
        // sumPSq has y-variance ≈ 0 but x-variance > 0, so the algorithm
        // doesn't hit the degeneracy guard (norm is finite). What we
        // really want to assert: no NaN in any output field, and the
        // recovered rotation is essentially zero (perfectly aligned
        // colinear pairs CAN'T be rotated; the only meaningful answer
        // is identity).
        var light = Enumerable.Range(0, 20).Select(i => new Vector2(50f + i * 20f, 200f)).ToArray();
        var refPoints = light.ToArray();

        var result = RegistrationRefiner.RefineRigid(
            Stars(light), Stars(refPoints), Matrix3x2.Identity);

        result.MatchedCount.ShouldBe(20);
        float.IsNaN(result.RotationDeg).ShouldBeFalse();
        float.IsNaN(result.Scale).ShouldBeFalse();
        float.IsNaN(result.Tx).ShouldBeFalse();
        float.IsNaN(result.Ty).ShouldBeFalse();
        float.IsNaN(result.RmsResidualPx).ShouldBeFalse();
        result.RotationDeg.ShouldBe(0f, tolerance: 1e-3f);
        result.Scale.ShouldBe(1f, tolerance: 1e-4f);
    }

    [Fact]
    public void RefineRigid_With03PxIsotropicNoise_StaysBounded()
    {
        // 50 stars under identity mapping plus ±0.3 px isotropic
        // Gaussian noise. The fit must not amplify the noise into a
        // fictitious rotation or scale.
        var rng = new Random(5678);
        var light = SeededStarField(50, seed: 9999);
        var refPoints = light.Select(p =>
        {
            var noise = NextGaussian(rng, 0.3f);
            return new Vector2(p.X + noise.X, p.Y + noise.Y);
        }).ToArray();

        var result = RegistrationRefiner.RefineRigid(
            Stars(light), Stars(refPoints), Matrix3x2.Identity);

        result.MatchedCount.ShouldBe(50);
        // RMS should be on the order of the per-sample noise (with N=50
        // samples the centroid+fit absorbs some of it; 0.5 px is a
        // generous ceiling that still catches a runaway fit).
        result.RmsResidualPx.ShouldBeLessThan(0.5f);
        // Rotation and scale should stay near identity -- noise alone
        // mustn't drive a spurious rotation.
        MathF.Abs(result.RotationDeg).ShouldBeLessThan(0.05f);
        MathF.Abs(result.Scale - 1f).ShouldBeLessThan(5e-4f);
    }

    [Fact]
    public void RefineTranslation_PureTranslation_StillWorks()
    {
        // Control test: confirms the legacy RefineTranslation behaviour
        // is unchanged by the addition of RefineRigid. Same setup as
        // RefineRigid_PureTranslation_RecoversOffset.
        var light = SeededStarField(30);
        var refPoints = light.Select(p => p + new Vector2(3f, -2f));

        var (refined, dx, dy, matched) = RegistrationRefiner.RefineTranslation(
            Stars(light), Stars(refPoints), Matrix3x2.Identity);

        matched.ShouldBe(30);
        dx.ShouldBe(3f, tolerance: 1e-3f);
        dy.ShouldBe(-2f, tolerance: 1e-3f);
        refined.M31.ShouldBe(3f, tolerance: 1e-3f);
        refined.M32.ShouldBe(-2f, tolerance: 1e-3f);
    }

    /// <summary>
    /// Box-Muller transform: returns a 2D vector of zero-mean Gaussian
    /// samples with the requested standard deviation.
    /// </summary>
    private static Vector2 NextGaussian(Random rng, float stdDev)
    {
        var u1 = 1.0 - rng.NextDouble();  // avoid log(0)
        var u2 = 1.0 - rng.NextDouble();
        var r = Math.Sqrt(-2.0 * Math.Log(u1));
        var theta = 2.0 * Math.PI * u2;
        return new Vector2(
            (float)(stdDev * r * Math.Cos(theta)),
            (float)(stdDev * r * Math.Sin(theta)));
    }
}
