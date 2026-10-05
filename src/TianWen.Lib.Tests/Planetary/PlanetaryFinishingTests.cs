using System;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The finishing steps #1279 measures (<see cref="PlanetaryFinishing"/>): the pupil's cutoff, the low-pass at it, and the contrast-adaptive
/// weighting at matched noise.
/// </summary>
public class PlanetaryFinishingTests
{
    [Fact]
    public void ThePupilsCutoffIsItsDiameterOverTheWavelengthInThePixelsOwnAngle()
    {
        // 28 cm at 550 nm passes 509,091 cycles a radian; a pixel of 0.1 arcsec is 4.848e-7 rad, so 0.2468 cycles a pixel.
        PlanetaryFinishing.CutoffCyclesPerPixel(new Pupil(0.28), 550, 0.1).ShouldBe(0.28 / 550e-9 * 0.1 / 206264.806, 1e-9);
        PlanetaryFinishing.CutoffTransfer(0.5 * 0.25, 0.25).ShouldBe(1, "well below the cutoff");
        PlanetaryFinishing.CutoffTransfer(PlanetaryFinishing.CutoffStart * 0.25, 0.25).ShouldBe(1, 1e-12, "where the fall starts");
        PlanetaryFinishing.CutoffTransfer(0.25, 0.25).ShouldBe(0, 1e-12, "at the cutoff");
        PlanetaryFinishing.CutoffTransfer(0.4, 0.25).ShouldBe(0, "past it");
        var half = PlanetaryFinishing.CutoffTransfer((1 + PlanetaryFinishing.CutoffStart) / 2 * 0.25, 0.25);
        half.ShouldBe(0.5, 1e-9, "a smootherstep crosses one half at its middle");
    }

    [Fact]
    public void TheLowPassKeepsAWaveBelowTheCutoffAndTakesOutOnePastIt()
    {
        const int size = 128;
        const double cutoff = 0.25;
        var low = Wave(size, 0.1);
        var high = Wave(size, 0.4);
        var lowOut = PlanetaryFinishing.LowPassAtCutoff(low, size, cutoff);
        var highOut = PlanetaryFinishing.LowPassAtCutoff(high, size, cutoff);
        Rms(lowOut).ShouldBe(Rms(low), Rms(low) * 0.02, "0.1 cycles a pixel lies below 0.85 of the cutoff");
        Rms(highOut).ShouldBeLessThan(Rms(high) * 0.02, "0.4 cycles a pixel lies past the cutoff");
    }

    [Fact]
    public void TheAdaptiveWeightingLeavesThePlanetsOutsideAsSharpenedAndFollowsTheStacksContrast()
    {
        const int size = 96;
        var disk = new MetricDisk(47.5, 47.5, 30);
        var random = new Random(1273);
        var (stack, sharpened) = (new float[size * size], new float[size * size]);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size) + x;
                var r = disk.ClearRadiiAt(x, y);
                // A disk with a belt pattern on one side and flat on the other, a little noise everywhere.
                var detail = x < 47 && r < 1 ? 0.2f * MathF.Sin(y * 0.6f) : 0f;
                stack[i] = (r < 1 ? 1f : 0f) + detail + (float)(0.01 * (random.NextDouble() - 0.5));
                // A sharpening that raises the detail and the noise, and draws something else outside the planet.
                sharpened[i] = (r < 1 ? 1f : 0.05f) + (2 * detail) + (float)(0.03 * (random.NextDouble() - 0.5));
            }
        }
        var result = PlanetaryFinishing.ContrastWeighted(stack, sharpened, stack, size, disk);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (disk.ClearRadiiAt(x, y) >= 1)
                {
                    var i = (y * size) + x;
                    result[i].ShouldBe(sharpened[i], 1e-6f, "from the outline out, the sharpening's own drawing");
                }
            }
        }
        // The belt side, where the contrast is, keeps more of the change than the flat side.
        var (belt, flat) = (ChangeAt(result, stack, size, 30, 47), ChangeAt(result, stack, size, 64, 47));
        belt.ShouldBeGreaterThan(flat, "the change follows the stack's contrast");
    }

    [Fact]
    public void TheWienerLowPassCutsWhereTheNoiseOutweighsThePowerAndNowhereWithoutNoise()
    {
        const int size = 96;
        var disk = new MetricDisk(47.5, 47.5, 40);
        var random = new Random(1279);
        var (clean, noisy) = (new float[size * size], new float[size * size]);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size) + x;
                // A disk with a slow belt pattern, and white noise over the whole window.
                clean[i] = disk.ClearRadiiAt(x, y) < 1 ? 1f + (0.2f * MathF.Cos(2 * MathF.PI * 0.05f * x)) : 0f;
                noisy[i] = clean[i] + (float)(0.2 * (random.NextDouble() - 0.5));
            }
        }
        var white = PlanetaryInverse.WhiteNoise(PlanetaryWaveletGains.Interior(noisy, size, size, disk), size, size);
        var (filtered, from, to) = PlanetaryFinishing.WienerLowPass(noisy, size, disk, white, _ => 1);
        from.ShouldBeLessThan(0.5, "white noise outweighs a slow pattern's power at the finest scales");
        to.ShouldBeGreaterThan(from);
        InsideError(filtered, clean, size, disk).ShouldBeLessThan(InsideError(noisy, clean, size, disk), "the cut takes out more noise than signal");

        var (same, noCut, _) = PlanetaryFinishing.WienerLowPass(clean, size, disk, 0, _ => 1);
        noCut.ShouldBe(0.5, "with no noise the target is one everywhere");
        same.ShouldBe(clean);
    }

    // The RMS difference inside 0.8 radii.
    private static double InsideError(float[] a, float[] b, int size, MetricDisk disk)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (disk.ClearRadiiAt(i % size, i / size) < 0.8)
            {
                sum += (a[i] - b[i]) * (a[i] - b[i]);
                count++;
            }
        }
        return Math.Sqrt(sum / count);
    }

    private static float[] Wave(int size, double cyclesPerPixel)
    {
        var plane = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[(y * size) + x] = (float)Math.Cos(2 * Math.PI * cyclesPerPixel * x);
            }
        }
        return plane;
    }

    private static double Rms(ReadOnlySpan<float> plane)
    {
        double sum = 0;
        // The middle half, clear of the edge's handling.
        var size = (int)Math.Sqrt(plane.Length);
        var count = 0;
        for (var y = size / 4; y < 3 * size / 4; y++)
        {
            for (var x = size / 4; x < 3 * size / 4; x++)
            {
                sum += plane[(y * size) + x] * plane[(y * size) + x];
                count++;
            }
        }
        return Math.Sqrt(sum / count);
    }

    // The RMS change from the stack over a 9-pixel-high strip about (x, y).
    private static double ChangeAt(float[] result, float[] stack, int size, int x, int y)
    {
        double sum = 0;
        for (var dy = -4; dy <= 4; dy++)
        {
            var i = ((y + dy) * size) + x;
            sum += (result[i] - stack[i]) * (result[i] - stack[i]);
        }
        return Math.Sqrt(sum / 9);
    }
}
