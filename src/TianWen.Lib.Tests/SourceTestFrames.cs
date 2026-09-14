using System;

namespace TianWen.Lib.Tests;

/// <summary>Synthetic frames whose truth is known, shared by the source-detection tests.</summary>
internal static class SourceTestFrames
{
    /// <summary>Adds a Gaussian star of <paramref name="sigma"/> px to a row-major plane, clipped at the edges.</summary>
    public static void AddStar(float[] plane, int width, int height, int cx, int cy, float amplitude, float sigma)
    {
        var r = (int)MathF.Ceiling(4f * sigma);
        var twoSigmaSq = 2f * sigma * sigma;
        for (var dy = -r; dy <= r; dy++)
        {
            var y = cy + dy;
            if (y < 0 || y >= height)
            {
                continue;
            }

            for (var dx = -r; dx <= r; dx++)
            {
                var x = cx + dx;
                if (x < 0 || x >= width)
                {
                    continue;
                }

                plane[y * width + x] += amplitude * MathF.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
            }
        }
    }

    /// <summary>A standard normal deviate (Box-Muller).</summary>
    public static float Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    /// <summary>
    /// A flat sky with noise, <paramref name="stars"/> Gaussian stars of 1.5 px sigma and one 25 px
    /// nebula at the centre, as a row-major plane.
    /// </summary>
    public static float[] StarFieldWithNebula(int width, int height, float sky, float noise, int stars, int seed)
    {
        var rng = new Random(seed);
        var plane = new float[width * height];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = sky + noise * Gaussian(rng);
        }

        for (var i = 0; i < stars; i++)
        {
            AddStar(plane, width, height, rng.Next(8, width - 8), rng.Next(8, height - 8), 0.02f + 0.3f * rng.NextSingle(), 1.5f);
        }

        AddStar(plane, width, height, width / 2, height / 2, 15f * noise, 25f);
        return plane;
    }
}
