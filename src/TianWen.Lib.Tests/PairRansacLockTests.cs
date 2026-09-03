using Shouldly;
using System;
using System.Numerics;
using TianWen.Lib.Astrometry.PlateSolve;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for the two-star similarity lock that seeds <see cref="CatalogPlateSolver"/>.
/// The synthetic fields deliberately model the conditions that killed the alternatives:
/// partial membership between the two lists (the population churn that defeats quad matching)
/// and a dense unrelated field (the nearest-neighbour noise that defeats proximity matching).
/// </summary>
public class PairRansacLockTests(ITestOutputHelper output)
{
    private const int W = 3000, H = 3000;

    /// <summary>Deterministic scattered field; brightness order is the array order.</summary>
    private static Vector2[] MakeField(int n, int seed)
    {
        var rng = new Random(seed);
        var pts = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            pts[i] = new Vector2(rng.Next(20, W - 20), rng.Next(20, H - 20));
        }
        return pts;
    }

    [Theory]
    [InlineData(0.0, 1.0)]     // pure translation
    [InlineData(2.5, 1.0)]     // small rotation (a slightly skewed camera)
    [InlineData(-8.0, 1.02)]   // rotation + 2% scale error (wrong FOCALLEN header)
    [InlineData(90.0, 1.0)]    // arbitrary camera angle -- the case the proximity loop cannot converge on at all
    public void RecoversKnownSimilarity(double rotDeg, double scale)
    {
        var catalog = MakeField(400, seed: 42);

        var rot = double.DegreesToRadians(rotDeg);
        var re = (float)(scale * Math.Cos(rot));
        var im = (float)(scale * Math.Sin(rot));
        const float tx = 37f, ty = -22f;
        var truth = new Matrix3x2(re, im, -im, re, tx, ty);

        // Detected = 75% membership subset of the catalog, transformed + jittered, plus
        // artifacts with no catalog counterpart. Models saturation dropping bright stars
        // and hot pixels adding fake ones.
        var rng = new Random(7);
        var detected = new System.Collections.Generic.List<Vector2>();
        foreach (var (c, i) in System.Linq.Enumerable.Select(catalog, (c, i) => (c, i)))
        {
            if (i % 4 == 0)
            {
                continue;
            }
            var t = Vector2.Transform(c, truth);
            detected.Add(new Vector2(
                t.X + (float)(rng.NextDouble() - 0.5),
                t.Y + (float)(rng.NextDouble() - 0.5)));
        }
        for (var i = 0; i < 50; i++)
        {
            detected.Add(new Vector2(rng.Next(0, W), rng.Next(0, H)));
        }
        var detectedArr = detected.ToArray();

        var locked = PairRansacLock.TryLock(catalog, detectedArr, detectedArr, W, H, scaleTolerance: 0.04f, out var diagnostics,
            cancellationToken: TestContext.Current.CancellationToken);

        locked.ShouldNotBeNull($"should lock at rot={rotDeg} deg scale={scale}; {diagnostics}");
        output.WriteLine($"rot={rotDeg} scale={scale}: hits={locked.Value.Hits}/{locked.Value.Census} " +
            $"(chance {locked.Value.ExpectedChanceHits:F1}) after {locked.Value.Hypotheses} hypotheses");

        // The recovered transform must agree with the truth across the whole frame, not
        // just at the anchor pair -- probe the four corners.
        var m = locked.Value.Transform;
        foreach (var corner in new[] { new Vector2(0, 0), new Vector2(W, 0), new Vector2(0, H), new Vector2(W, H) })
        {
            var expected = Vector2.Transform(corner, truth);
            var actual = Vector2.Transform(corner, m);
            Vector2.Distance(expected, actual).ShouldBeLessThan(2.0f,
                $"corner {corner} maps {actual} vs truth {expected}");
        }
    }

    [Fact]
    public void RejectsUnrelatedFields()
    {
        // Two dense fields with NO correspondence at all -- the Panel 3 failure regime,
        // where the proximity matcher found 1,434 chance "matches" and reported success.
        var catalog = MakeField(400, seed: 42);
        var detected = MakeField(1500, seed: 1337);

        var locked = PairRansacLock.TryLock(catalog, detected, detected, W, H, scaleTolerance: 0.04f, out var diagnostics,
            cancellationToken: TestContext.Current.CancellationToken);

        locked.ShouldBeNull($"no hypothesis over unrelated fields may beat the chance model, but got {diagnostics}");
        output.WriteLine($"unrelated fields: {diagnostics}");
    }

    [Fact]
    public void RejectsMirroredField()
    {
        // A mirrored field must NOT lock: the similarity hypothesis is chirality-preserving
        // by construction, so parity stays separated in the caller's per-xSign attempts.
        var catalog = MakeField(400, seed: 42);
        var rng = new Random(7);
        var detected = new Vector2[catalog.Length];
        for (var i = 0; i < catalog.Length; i++)
        {
            detected[i] = new Vector2(
                W - catalog[i].X + (float)(rng.NextDouble() - 0.5),
                catalog[i].Y + (float)(rng.NextDouble() - 0.5));
        }

        var locked = PairRansacLock.TryLock(catalog, detected, detected, W, H, scaleTolerance: 0.04f, out var diagnostics,
            cancellationToken: TestContext.Current.CancellationToken);

        locked.ShouldBeNull($"a mirror flip is not expressible as rotation + positive scale and must not verify, but got {diagnostics}");
        output.WriteLine($"mirrored field: {diagnostics}");
    }
}
