using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="FrameRegistration.TryMatchAsync"/>'s similarity fallback. The affine fitted to quad centres must pass a
/// 1e-3 shape test (scale ratio, skew), which the quad stage's pixel of slop fails over a wide field: on the SV605CC
/// Sculptor of 2026-09-12 seven good subs were dropped at +0.0012 to +0.0024. A similarity, which cannot shear or
/// mirror, is fitted to the same inliers once every affine has failed.
/// </summary>
public class SimilarityFallbackMatchTests
{
    private const int StarCount = 120;
    private const float Field = 1000f;

    [Fact]
    public async Task AQuadMatchWhoseAffineFailsTheShapeTestRegistersAsASimilarity()
    {
        // 0.35 % more scale along x than y: an affine fits it and fails the 1e-3 shape test; a similarity is off by at
        // most 1.75 px at the field's edge, inside the fallback's 2 px RMS.
        var rotation = Matrix3x2.CreateRotation(0.1f * MathF.PI / 180f, new Vector2(Field / 2, Field / 2));
        using var reference = new SortedStarList(Stars(static p => p));
        using var light = new SortedStarList(Stars(p => Vector2.Transform(new Vector2(((p.X - (Field / 2)) * 1.0035f) + (Field / 2), p.Y), rotation) + new Vector2(5f, -3f)));

        (await light.FindOffsetAndRotationWithRmsAsync(reference, minimumCount: 6, quadTolerance: 0.05f, maxStars: 100)).Solution
            .ShouldBeNull("the affine's shape test is what drops such a sub today");

        var (solution, _, rms) = await FrameRegistration.TryMatchAsync(light, reference, quadStars: 100);
        var s = solution.ShouldNotBeNull();
        rms.ShouldBeLessThanOrEqualTo(FrameRegistration.SimilarityFallbackMaxRmsPx);
        var (scale, skew, _, _) = s.Decompose();
        (scale.X / scale.Y).ShouldBe(1f, 1e-5f, "a similarity has one scale");
        skew.X.ShouldBe(0f, 1e-5f);
    }

    [Fact]
    public async Task AMirroredFrameNeverRegistersThroughTheFallback()
    {
        using var reference = new SortedStarList(Stars(static p => p));
        using var mirrored = new SortedStarList(Stars(static p => new Vector2(Field - p.X, p.Y)));

        (await FrameRegistration.TryMatchAsync(mirrored, reference, quadStars: 100)).Solution.ShouldBeNull();
    }

    [Fact]
    public async Task ASubThatPassesTheAffineKeepsItsAffine()
    {
        using var reference = new SortedStarList(Stars(static p => p));
        using var light = new SortedStarList(Stars(static p => p + new Vector2(7f, 4f)));

        var direct = (await light.FindOffsetAndRotationWithRmsAsync(reference, minimumCount: 6, quadTolerance: 0.008f, maxStars: 100)).Solution;
        (await FrameRegistration.TryMatchAsync(light, reference, quadStars: 100)).Solution.ShouldBe(direct.ShouldNotBeNull());
    }

    /// <summary>A deterministic field of <see cref="StarCount"/> stars, brightest first, placed by <paramref name="place"/>.</summary>
    private static StarList Stars(Func<Vector2, Vector2> place)
    {
        var bag = new ConcurrentBag<ImagedStar>();
        for (var i = 0; i < StarCount; i++)
        {
            var h = ((uint)i * 2654435761u) ^ 0x9e3779b9u;
            var p = new Vector2(20f + ((h % 9601) / 9600f * (Field - 40f)), 20f + (((h >> 13) % 9601) / 9600f * (Field - 40f)));
            var q = place(p);
            bag.Add(new ImagedStar(2f, 2f, 100f, StarCount - i, q.X, q.Y, 0f));
        }
        return new StarList(bag);
    }
}
