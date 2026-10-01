using System;
using System.Linq;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R8 follow-up 1's kernel reading (docs/plans/planetary-restoration.md): a known kernel fitted back from a plane and its filtered copy,
/// the wavelet filter's transfer against the a trous transform itself, and a composite kernel's negative mass, zero for a blur and not
/// for a filter that lifts a frequency past one.
/// </summary>
public class PlanetaryKernelFitTests
{
    [Fact]
    public void AKnownKernelIsFittedBackFromAPlaneAndItsFilteredCopy()
    {
        const int size = 64;
        var random = new Random(3);
        var plane = Enumerable.Range(0, size * size).Select(_ => (float)random.NextDouble()).ToArray();
        double[] kernel = [0, -0.1, 0, -0.1, 1.5, -0.1, 0, -0.1, 0.0];
        var filtered = PlanetaryKernelFit.Apply(plane, size, size, kernel, 1, constant: 0.25);
        var (fitted, constant) = PlanetaryKernelFit.Fit(plane, filtered, size, size, 1);
        for (var i = 0; i < kernel.Length; i++)
        {
            fitted[i].ShouldBe(kernel[i], 1e-6);
        }
        constant.ShouldBe(0.25, 1e-6);
    }

    [Fact]
    public void TheWaveletTransferIsWhatTheATrousGainsDo()
    {
        // A disk well inside the window, so neither the mirror nor the period reaches it in three layers.
        const int size = 128;
        var plane = new float[size * size];
        var random = new Random(5);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = Math.Sqrt(((x - 63.5) * (x - 63.5)) + ((y - 63.5) * (y - 63.5)));
                plane[(y * size) + x] = r < 30 ? (float)(1 + (0.2 * random.NextDouble())) : 0;
            }
        }
        double[] gains = [2.0, 1.5, 0.7];
        var applied = PlanetaryWaveletGains.Apply(plane, size, size, gains);
        var filtered = PlanetaryKernelFit.Filter(plane, size, size, (fx, fy) => PlanetaryKernelFit.WaveletTransfer(gains, fx, fy));
        var rms = Math.Sqrt(plane.Average(v => (double)v * v));
        var difference = Math.Sqrt(applied.Select((v, i) => (double)(v - filtered[i]) * (v - filtered[i])).Average());
        TestContext.Current.TestOutputHelper?.WriteLine($"difference {difference:G3} of an RMS {rms:G3}");
        difference.ShouldBeLessThan(1e-4 * rms);
    }

    [Fact]
    public void ABlurHasNoNegativeMassAndAFilterPastOneHas()
    {
        static double Gaussian(double sigma, double f) => Math.Exp(-2 * Math.PI * Math.PI * sigma * sigma * f * f);
        // A blur alone, and the same blur restored to nothing more than a narrower blur: both non-negative, to the transform's rounding
        // and the narrower one's own transfer at Nyquist (a Gaussian much under a pixel is not band-limited on the grid, and ripples).
        var blur = PlanetaryKernelFit.NegativeMass((_, _) => Complex.One, f => Gaussian(1.5, f));
        var narrower = PlanetaryKernelFit.NegativeMass((fx, fy) => Gaussian(1.2, Math.Sqrt((fx * fx) + (fy * fy))) / Gaussian(2.0, Math.Sqrt((fx * fx) + (fy * fy))),
            f => Gaussian(2.0, f));
        // A filter that lifts the mid frequencies past the truth: a negative lobe.
        var lifted = PlanetaryKernelFit.NegativeMass((fx, fy) => 1 + (3 * Math.Exp(-Math.Pow((Math.Sqrt((fx * fx) + (fy * fy)) - 0.1) / 0.04, 2))), f => Gaussian(1.0, f));
        TestContext.Current.TestOutputHelper?.WriteLine($"negative mass: the blur {blur:G3}, restored to a narrower blur {narrower:G3}, lifted past one {lifted:G3}");
        blur.ShouldBeLessThan(1e-4);
        narrower.ShouldBeLessThan(1e-3);
        lifted.ShouldBeGreaterThan(0.01);
    }
}
