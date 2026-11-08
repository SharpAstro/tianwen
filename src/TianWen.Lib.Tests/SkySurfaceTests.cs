using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary><see cref="Image.SkySurface"/> (#1974): the sky's level, gradient and patchiness from one tile pass.</summary>
public class SkySurfaceTests
{
    private const int Size = 640;

    private static Image Tilted(float level, float gx, float gy)
    {
        var rng = new Random(1974);
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var noise = (float)(rng.NextDouble() - 0.5) * 4f;
                plane[y, x] = level + gx * (x / (float)Size - 0.5f) + gy * (y / (float)Size - 0.5f) + noise;
            }
        }
        return new Image([plane], BitDepth.Float32, 4000f, 0f, 0f, new ImageMeta());
    }

    [Fact]
    public void ATiltedPlaneGivesItsKnownGradientAndLevel()
    {
        var surface = Tilted(1000f, 300f, 400f).SkySurface(0);

        surface.Level.ShouldBe(1000f, 3f);
        surface.GradientStrength.ShouldBe(500f, 10f);
        surface.GradientAngleDeg.ShouldBe(MathF.Atan2(400f, 300f) * 180f / MathF.PI, 1f);
        surface.Patchiness.ShouldBeLessThan(5f);
    }

    [Fact]
    public void TheGradientPointsTowardsTheBrighterSide()
    {
        // Brightening to the left: along -X, 180 degrees. Brightening upwards (smaller rows): -90.
        MathF.Abs(Tilted(1000f, -250f, 0f).SkySurface(0).GradientAngleDeg).ShouldBe(180f, 1f);
        Tilted(1000f, 0f, -250f).SkySurface(0).GradientAngleDeg.ShouldBe(-90f, 1f);
    }

    [Fact]
    public void PatchinessIsTheSameFigureAsSkyPatchiness()
    {
        var image = Tilted(1000f, 120f, -80f);

        image.SkySurface(0).Patchiness.ShouldBe(image.SkyPatchiness(0));
    }

    [Fact]
    public void AFrameTooSmallToFitHasNoSurface()
    {
        var surface = new Image([new float[100, 100]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()).SkySurface(0);

        surface.Level.ShouldBe(float.NaN);
        surface.GradientStrength.ShouldBe(float.NaN);
        surface.GradientAngleDeg.ShouldBe(float.NaN);
        surface.Patchiness.ShouldBe(float.NaN);
    }
}
