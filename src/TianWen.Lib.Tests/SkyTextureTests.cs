using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R2d's D1 measure on fields whose spectra are known: white noise (index 0, no kurtosis) and a Kolmogorov field (11/3),
/// and a source the measure must not read.
/// </summary>
public sealed class SkyTextureTests
{
    private const int Size = 512;

    private static float[] WhiteNoise(int seed, double sigma)
    {
        var rng = new Random(seed);
        return [.. Enumerable.Range(0, Size * Size).Select(_ =>
            (float)(sigma * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble())))];
    }

    [Fact]
    public void WhiteNoiseReadsAFlatSpectrumAndNoKurtosis()
    {
        var m = SkyTexture.Measure(WhiteNoise(1, 0.01), Size, Size, absent: null, fwhm: 2.5);

        m.SpectralIndex.ShouldBe(0, 0.15, "white noise has the same power at every frequency");
        // Up to 8 px: a 384 px read square holds about 150 independent coefficients at 32 px, whose sample kurtosis
        // scatters by about 0.4, so the coarse scales are not held to it.
        foreach (var s in m.Scales.Where(static s => s.ScalePx <= 8))
        {
            s.Kurtosis.ShouldBe(0, 0.15, $"a Gaussian field has no excess kurtosis at {s.ScalePx} px");
            s.TailFraction.ShouldBeLessThan(1e-4, $"nor a tail past 5 sigma at {s.ScalePx} px");
        }
        m.ReadFraction.ShouldBeGreaterThan(0.5);
    }

    [Fact]
    public void AKolmogorovFieldReadsItsElevenThirds()
    {
        var phase = new double[Size * Size];
        PhaseScreen.Kolmogorov(phase, Size, spacingM: 0.01, r0M: 0.05, new Random(2));
        var field = phase.Select(static p => (float)p).ToArray();

        var m = SkyTexture.Measure(field, Size, Size, absent: null, fwhm: 2.5);

        m.SpectralIndex.ShouldBe(11.0 / 3.0, 0.4, "a Kolmogorov screen's power falls as k to the minus eleven thirds");
    }

    [Fact]
    public void ASourceThePlateKeptIsNotRead()
    {
        var noise = WhiteNoise(3, 0.01);
        var star = noise.ToArray();
        var s = 2.5 / 2.3548;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                star[(y * Size) + x] += (float)(0.5 * Math.Exp(-(((x - 256.0) * (x - 256.0)) + ((y - 256.0) * (y - 256.0))) / (2 * s * s)));
            }
        }

        var clean = SkyTexture.Measure(noise, Size, Size, absent: null, fwhm: 2.5);
        var kept = SkyTexture.Measure(star, Size, Size, absent: null, fwhm: 2.5);

        kept.Sources.ShouldBeGreaterThan(clean.Sources, "the star is found");
        kept.ReadFraction.ShouldBeLessThan(clean.ReadFraction, "and its surroundings are left out");
        kept.Scales[0].Kurtosis.ShouldBe(clean.Scales[0].Kurtosis, 0.2, "so the read sky has no star's tail in it");
    }

    [Fact]
    public void SparseSharpStructureFillsTheTailThatTheMadIgnores()
    {
        var noise = WhiteNoise(4, 0.01);
        var sparse = noise.ToArray();
        var rng = new Random(5);
        // Knots of a nebula, wider than the PSF so the finder (a matched filter at 1 px) is not asked to call them stars,
        // and too few to move the MAD: a tail the robust RMS does not see and the tail fraction does.
        for (var k = 0; k < 40; k++)
        {
            int cx = rng.Next(80, Size - 80), cy = rng.Next(80, Size - 80);
            for (var y = cy - 6; y <= cy + 6; y++)
            {
                for (var x = cx - 6; x <= cx + 6; x++)
                {
                    sparse[(y * Size) + x] += (float)(0.08 * Math.Exp(-(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / 18.0));
                }
            }
        }

        var flat = SkyTexture.Measure(noise, Size, Size, absent: null, fwhm: 1.0, thresholdSigma: 50f);
        var knotted = SkyTexture.Measure(sparse, Size, Size, absent: null, fwhm: 1.0, thresholdSigma: 50f);

        knotted.Scales[2].TailFraction.ShouldBeGreaterThan(10 * Math.Max(flat.Scales[2].TailFraction, 1e-6));
        knotted.Scales[2].RobustRms.ShouldBe(flat.Scales[2].RobustRms, 0.2 * flat.Scales[2].RobustRms);
    }

    [Fact]
    public void StripesReadFullyCoherentWithTheirGradientAcrossThem()
    {
        // Stripes that vary along y only: every gradient is vertical, so the doubled angle's cosine is -1.
        var stripes = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                stripes[(y * Size) + x] = (float)Math.Sin(2 * Math.PI * y / 8.0);
            }
        }

        var (coherence, cos2, _, _) = SkyTexture.StructureTensor(stripes, Size, Size, sigma: 4f);

        var i = (Size / 2 * Size) + (Size / 2);
        ((double)coherence[i]).ShouldBe(1.0, 1e-3);
        ((double)cos2[i]).ShouldBe(-1.0, 1e-3);
    }

    // White noise, a coarse wave along y (the structure the generator keeps) and fine stripes along x or y on it.
    private static float[] Striated(bool fineAcrossTheCoarse)
    {
        var field = WhiteNoise(7, 0.01);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var fine = fineAcrossTheCoarse ? Math.Sin(2 * Math.PI * x / 8.0) : Math.Sin(2 * Math.PI * y / 8.0);
                field[(y * Size) + x] += (float)((0.2 * Math.Sin(2 * Math.PI * y / 400.0)) + (0.03 * fine));
            }
        }
        return field;
    }

    [Fact]
    public void FineStructureAlongTheCoarseContoursReadsAlignedAndAcrossThemAntiAligned()
    {
        var along = SkyTexture.Measure(Striated(fineAcrossTheCoarse: false), Size, Size, absent: null, fwhm: 2.5);
        var across = SkyTexture.Measure(Striated(fineAcrossTheCoarse: true), Size, Size, absent: null, fwhm: 2.5);

        // Read at the scale the 8 px stripes live at, the one holding the most energy among 2 to 16 px.
        var j = Enumerable.Range(1, 4).MaxBy(k => along.Scales[k].RobustRms);
        along.Scales[j].Coherence.ShouldBeGreaterThan(along.NoiseCoherence[j] + 0.3, $"the stripes run one way at {1 << j} px");
        along.Scales[j].Alignment.ShouldBeGreaterThan(0.8, $"stripes along the coarse contours at {1 << j} px");
        across.Scales[j].Alignment.ShouldBeLessThan(-0.8, $"stripes across the coarse contours at {1 << j} px");
    }

    [Fact]
    public void WhiteNoiseReadsTheNullCoherenceAndNoAlignment()
    {
        var m = SkyTexture.Measure(WhiteNoise(8, 0.01), Size, Size, absent: null, fwhm: 2.5);

        foreach (var s in m.Scales.Where(static s => s.ScalePx <= 8))
        {
            var j = System.Numerics.BitOperations.Log2((uint)s.ScalePx);
            s.Coherence.ShouldBe(m.NoiseCoherence[j], 0.05, $"an isotropic field reads the null at {s.ScalePx} px");
            Math.Abs(s.Alignment).ShouldBeLessThan(0.1, $"and no preferred orientation at {s.ScalePx} px");
            Math.Abs(m.NoiseAlignment[j]).ShouldBeLessThan(0.1, $"two octaves apart, band overlap reads no alignment at {s.ScalePx} px");
        }
    }

    /// <summary>
    /// F4 of #1400: white noise is the wrong null for a red sky. An isotropic power-law field at the plates' index reads
    /// more coherence than white noise (a band's energy sits at its coarse edge, where fewer modes fit the window), so part
    /// of S1's "coherence over noise" is the spectrum, and the red null is what a plate's coherence is set against.
    /// </summary>
    [Fact]
    public void AnIsotropicRedFieldReadsMoreCoherenceThanWhiteNoise()
    {
        var (white, whiteAlignment) = SkyTexture.RedNull(0.0);
        var (red, redAlignment) = SkyTexture.RedNull(3.1);

        for (var j = 1; j <= 3; j++)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{1 << j} px: coherence white {white[j]:F3} red {red[j]:F3}; alignment white {whiteAlignment[j]:F3} red {redAlignment[j]:F3}");
            red[j].ShouldBeGreaterThan(white[j], $"at {1 << j} px");
        }
        SkyTexture.RedNull(double.NaN).Coherence.ShouldAllBe(static v => double.IsNaN(v));
    }
}
