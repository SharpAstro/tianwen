using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R2d's D2 and D3 generator on plates whose answers are known: a texture field of its own index, no texture on a smooth
/// sky, no star surviving from the plate, and knots that are never the PSF.
/// </summary>
public sealed class SyntheticBackgroundTests
{
    private const int Size = 512;
    private const double Fwhm = 2.5;
    private const float Noise = 0.002f;

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    // A one-channel plate: a smooth gradient, white noise, and what the test adds.
    private static Image Plate(int seed, Action<float[,]>? add = null)
    {
        var rng = new Random(seed);
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = (float)(0.1 + (0.02 * x / Size) + (Noise * Gaussian(rng)));
            }
        }
        add?.Invoke(plane);
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    [Fact]
    public void TheTextureFieldHasTheIndexItWasDrawnWith()
    {
        var field = SyntheticBackground.PowerLawField(Size, SyntheticBackground.TextureIndex, new Random(1)).Select(static v => (float)v).ToArray();

        SkyTexture.Measure(field, Size, Size, absent: null, fwhm: Fwhm).SpectralIndex.ShouldBe(SyntheticBackground.TextureIndex, 0.4);
    }

    /// <summary>
    /// F1 of #1400: a width is the same texture on every grid. The field's spread is set by its coarsest modes, so taken
    /// against the whole grid a 1,024 px grid's fine scales were weaker than the export's 512 px grid's (0.65 over sixteen
    /// draws, the n^-0.55 the review derived); with the modes coarser than <see cref="SyntheticBackground.TextureGridPx"/>
    /// taken out they read 1.07. What is left is the lattice at the cutoff: the 512 px grid samples its coarsest modes four
    /// times more sparsely than the 1,024 px one samples the same band, and a red spectrum's spread is set there.
    /// </summary>
    [Fact]
    public void AWidthDrawsTheSameFineScalesOnTheExportsGridAndOnALargerOne()
    {
        static double FineRms(int n, int? coarsest)
        {
            var sum = 0.0;
            const int draws = 16;
            for (var seed = 0; seed < draws; seed++)
            {
                var field = SyntheticBackground.UnitSpread(SyntheticBackground.PowerLawField(n, SyntheticBackground.TextureIndex, new Random(seed), coarsest));
                var detail = ATrousWaveletTransform.Decompose(field.Select(static v => (float)v).ToArray(), n, n, 3).Detail(1).ToArray();
                sum += Math.Sqrt(detail.Average(static v => (double)v * v));
            }
            return sum / draws;
        }

        var atExport = FineRms(SyntheticBackground.TextureGridPx, SyntheticBackground.TextureGridPx);
        var cut = FineRms(2 * SyntheticBackground.TextureGridPx, SyntheticBackground.TextureGridPx) / atExport;
        var whole = FineRms(2 * SyntheticBackground.TextureGridPx, coarsest: null) / atExport;
        TestContext.Current.TestOutputHelper?.WriteLine($"fine RMS on twice the grid over the export's: {cut:F3} with the cutoff, {whole:F3} without");
        cut.ShouldBe(1.0, 0.1);
        whole.ShouldBeLessThan(0.8, "the whole grid's spread is what moved it");
    }

    [Theory]
    [InlineData(23, 512)]
    [InlineData(256, 512)]
    [InlineData(384, 1024)]
    public void EveryCutIsDrawnOnAtLeastTheExportsGrid(int size, int grid)
        => SyntheticBackground.TextureGridFor(size).ShouldBe(grid);

    /// <summary>
    /// F2 of #1400: each drawn band is at unit PLAIN RMS, as the amplitude it is multiplied by is a plain RMS, so its energy
    /// is the plate's. Its robust spread is lower, by the log-normal's tails, which is the overshoot scaling by it drew.
    /// </summary>
    [Fact]
    public void ADrawnBandHasThePlainRmsItsAmplitudeAssumes()
    {
        var background = SyntheticBackground.Build(Plate(2), absent: null, Fwhm);

        var scales = background.TextureScales(0, 0, 256, new Random(3));

        for (var j = 0; j < scales.Length; j++)
        {
            Math.Sqrt(scales[j].Average(static v => (double)v * v)).ShouldBe(1.0, 1e-4, $"{1 << j} px");
            var abs = scales[j].Select(static v => Math.Abs(v)).Order().ToArray();
            (1.4826 * abs[abs.Length / 2]).ShouldBeLessThan(0.95, $"the log-normal's tails at {1 << j} px");
        }
    }

    /// <summary>
    /// F7 of #1400: where the fine structure runs across the coarse one, their doubled-angle vectors are opposite, and at half
    /// and half the blend is nothing while the angle jumps through 90 degrees. The steer's strength follows the blend's
    /// length, so it fades there instead of steering hard along an angle that flips: no seam.
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.0)]   // across, half and half: no steer
    [InlineData(0.25, 0.45)] // across, mostly coarse: the coarse orientation, weakened by the disagreement
    [InlineData(1.0, 0.9)]   // the fine alone: its own strength
    [InlineData(0.0, 0.9)]   // the coarse alone: its own
    public void TheSteerFadesWhereTheFineAndCoarseOrientationsCancel(double signal, double strength)
    {
        // Coarse along x (2 theta = 0), fine along y (2 theta = 180 degrees), both at strength 0.9.
        var (cos2, sin2, s, wandering) = SyntheticBackground.SteerAt(1, 0, 0.9, -1, 0, 0.9, signal);

        s.ShouldBe(strength, 1e-12);
        wandering.ShouldBe(1 - signal, 1e-12);
        if (strength > 0)
        {
            Math.Sign(cos2).ShouldBe(signal < 0.5 ? 1 : -1, "the orientation is the stronger share's");
        }
        sin2.ShouldBe(0, 1e-12);
    }

    [Fact]
    public void ASteerAlongOneOrientationKeepsItsBlendedStrength()
        => SyntheticBackground.SteerAt(0, 1, 0.4, 0, 1, 0.8, 0.5).Strength.ShouldBe(0.6, 1e-12);

    [Fact]
    public void ASmoothSkyHasNoTextureToDraw()
    {
        var background = SyntheticBackground.Build(Plate(2), absent: null, Fwhm);

        for (var j = 0; j < background.FirstKept; j++)
        {
            var amplitudes = Enumerable.Range(0, 200).Select(k => background.Amplitude(0, j, 64 + k, 256)).ToArray();
            ((double)amplitudes.Average()).ShouldBeLessThan(0.3 * Noise, $"the plate holds only noise at {1 << j} px");
        }
    }

    [Fact]
    public void AStarInThePlateDoesNotSurviveIntoTheBackground()
    {
        // A faint star the plate builder would have left: 12 noise sigma at its peak, at the PSF's width.
        var s = Fwhm / 2.3548;
        var image = Plate(3, plane =>
        {
            for (var y = 230; y < 282; y++)
            {
                for (var x = 230; x < 282; x++)
                {
                    plane[y, x] += (float)(12 * Noise * Math.Exp(-(((x - 256.0) * (x - 256.0)) + ((y - 256.0) * (y - 256.0))) / (2 * s * s)));
                }
            }
        });
        var background = SyntheticBackground.Build(image, absent: null, Fwhm);

        var cell = background.Cell(128, 128, 256, [Noise], new Random(4), out var knots);

        // The star sat at (128, 128) in the cell; the background there is the coarse sky and whatever knot fell on it.
        knots.Any(k => Math.Abs(k.X - 128) < 4 * k.SigmaMajorPx + 12 && Math.Abs(k.Y - 128) < 4 * k.SigmaMajorPx + 12)
            .ShouldBeFalse("this seed puts no knot near the star; pick another if the generator's draws change");
        var peak = cell[0][(128 * 256) + 128];
        var around = cell[0][(128 * 256) + 128 + 12];
        ((double)(peak - around)).ShouldBeLessThan(2.0 * Noise, "the star's 12 sigma peak is gone: no scale it lives at is kept");
    }

    [Fact]
    public void KnotsAreNeverThePsf()
    {
        var background = SyntheticBackground.Build(Plate(5), absent: null, Fwhm);
        var psfSigma = Fwhm / 2.3548;
        var seen = 0;
        for (var draw = 0; draw < 20; draw++)
        {
            background.Cell(0, 0, 256, [Noise], new Random(100 + draw), out var knots);
            foreach (var k in knots)
            {
                seen++;
                var round = Math.Abs(k.SigmaMajorPx - k.SigmaMinorPx) < 1e-9;
                if (round)
                {
                    k.SigmaMajorPx.ShouldBeGreaterThanOrEqualTo(1.6 * psfSigma, "a round knot is wider than the PSF");
                }
                else
                {
                    (k.SigmaMajorPx / k.SigmaMinorPx).ShouldBeGreaterThanOrEqualTo(2.5, "an elongated knot is far from round");
                }
                k.Peak[0].ShouldBeInRange(SyntheticBackground.KnotMinSigma * Noise * 0.999, SyntheticBackground.KnotMaxSigma * Noise * 1.001);
            }
        }
        seen.ShouldBeGreaterThan(20, "about three knots a cell");
    }

    [Fact]
    public void WithBrightKnotsWideOnlyAWideKnotIsBrightAndNoKnotMoves()
    {
        var plate = Plate(5);
        var plain = SyntheticBackground.Build(plate, absent: null, Fwhm);
        var wide = SyntheticBackground.Build(plate, absent: null, Fwhm, brightKnotsWide: true);
        var psfSigma = Fwhm / 2.3548;
        var (compact, bright) = (0, 0);
        for (var draw = 0; draw < 20; draw++)
        {
            plain.Cell(0, 0, 256, [Noise], new Random(100 + draw), out var before);
            wide.Cell(0, 0, 256, [Noise], new Random(100 + draw), out var after);
            after.Length.ShouldBe(before.Length);
            for (var k = 0; k < after.Length; k++)
            {
                // The same draws: where, how wide and which way are the plain generator's.
                (after[k].X, after[k].Y, after[k].SigmaMinorPx, after[k].SigmaMajorPx, after[k].AngleRad)
                    .ShouldBe((before[k].X, before[k].Y, before[k].SigmaMinorPx, before[k].SigmaMajorPx, before[k].AngleRad));
                if (after[k].SigmaMinorPx < SyntheticBackground.BrightKnotMinWidths * psfSigma)
                {
                    compact++;
                    after[k].Peak[0].ShouldBeLessThanOrEqualTo(SyntheticBackground.BrightKnotSigma * Noise * 1.001, "a compact knot stays faint");
                }
                else if (after[k].Peak[0] > SyntheticBackground.BrightKnotSigma * Noise)
                {
                    bright++;
                    after[k].Peak[0].ShouldBe(before[k].Peak[0], 1e-12, "a wide knot keeps its brightness");
                }
            }
        }
        compact.ShouldBeGreaterThan(10, "most knots are compact");
        bright.ShouldBeGreaterThan(3, "and the wide ones still reach past 20 sigma");
    }

    // A frame whose coarse structure is a wave along y (its gradient vertical everywhere) and whose right half holds a texture
    // above the noise, so the generator has a signal to draw there and one orientation to draw it along.
    private static Image WavePlate()
    {
        const int size = 1024;
        var rng = new Random(11);
        var texture = SyntheticBackground.PowerLawField(size, 1.0, new Random(12));
        var sd = Math.Sqrt(texture.Sum(static v => v * v) / texture.Length);
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var fine = x >= size / 2 ? 5 * Noise * texture[(y * size) + x] / sd : 0.0;
                plane[y, x] = (float)(0.1 + (0.2 * Math.Sin(2 * Math.PI * y / 400.0)) + (Noise * Gaussian(rng)) + fine);
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    [Fact]
    public void ASteeredTextureRunsAlongThePlatesCoarseContoursWhereAnIsotropicOneDoesNot()
    {
        var plate = WavePlate();
        var isotropic = SyntheticBackground.Build(plate, absent: null, Fwhm);
        var steered = SyntheticBackground.Build(plate, absent: null, Fwhm, steering: new SyntheticBackground.Steering(Strength: 1.0, Exponent: 4.0));

        SkyTexture.Measurement Read(SyntheticBackground background)
        {
            const int size = 384;
            var cell = background.Preview(768, 512, size, [Noise], noisy: true, new Random(21));
            return SkyTexture.Measure(cell[0], size, size, absent: null, fwhm: Fwhm);
        }
        var plain = Read(isotropic);
        var along = Read(steered);

        // The drawn scales at 2 and 4 px, read against the plate's own coarse wave by S1's measure.
        foreach (var j in new[] { 1, 2 })
        {
            Math.Abs(plain.Scales[j].Alignment).ShouldBeLessThan(0.15, $"an isotropic texture has no orientation at {1 << j} px");
            along.Scales[j].Alignment.ShouldBeGreaterThan(0.4, $"the steered one runs along the coarse contours at {1 << j} px");
            along.Scales[j].Coherence.ShouldBeGreaterThan(plain.Scales[j].Coherence + 0.05, $"and runs one way at {1 << j} px");
        }
    }

    [Fact]
    public void AWanderLoosensTheTieToTheCoarseContoursButKeepsTheTextureOneWay()
    {
        var plate = WavePlate();
        var isotropic = SyntheticBackground.Build(plate, absent: null, Fwhm);
        var tied = SyntheticBackground.Build(plate, absent: null, Fwhm, steering: new SyntheticBackground.Steering(1.0, 4.0));
        var loose = SyntheticBackground.Build(plate, absent: null, Fwhm, steering: new SyntheticBackground.Steering(1.0, 4.0, Wander: 1.0));

        SkyTexture.Measurement Read(SyntheticBackground background)
        {
            const int size = 384;
            var cell = background.Preview(768, 512, size, [Noise], noisy: true, new Random(21));
            return SkyTexture.Measure(cell[0], size, size, absent: null, fwhm: Fwhm);
        }
        var (plain, along, wandering) = (Read(isotropic), Read(tied), Read(loose));

        foreach (var j in new[] { 1, 2 })
        {
            wandering.Scales[j].Alignment.ShouldBeLessThan(along.Scales[j].Alignment - 0.2, $"a wander turns it off the coarse contours at {1 << j} px");
            wandering.Scales[j].Alignment.ShouldBeGreaterThan(plain.Scales[j].Alignment, $"but not against them at {1 << j} px");
            wandering.Scales[j].Coherence.ShouldBeGreaterThan(plain.Scales[j].Coherence + 0.05, $"and it still runs one way at {1 << j} px");
        }
    }

    [Fact]
    public void AFineSteerFollowsThePlatesOwnFineStructureWhereItDisagreesWithTheCoarse()
    {
        // The coarse wave's gradient is vertical; the right half's fine stripes vary along x, so their gradient is horizontal:
        // fine structure ACROSS the coarse contours, which a coarse steer would turn the other way.
        const int size = 1024;
        var rng = new Random(13);
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var stripes = x >= size / 2 ? 1.5 * Noise * Math.Sin(2 * Math.PI * x / 8.0) : 0.0;
                plane[y, x] = (float)(0.1 + (0.2 * Math.Sin(2 * Math.PI * y / 400.0)) + (Noise * Gaussian(rng)) + stripes);
            }
        }
        var plate = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var coarse = SyntheticBackground.Build(plate, absent: null, Fwhm, steering: new SyntheticBackground.Steering(1.0, 4.0));
        var fine = SyntheticBackground.Build(plate, absent: null, Fwhm, steering: new SyntheticBackground.Steering(1.0, 4.0, Fine: 1.0));

        SkyTexture.Measurement Read(SyntheticBackground background)
        {
            const int cell = 384;
            var planes = background.Preview(768, 512, cell, [Noise], noisy: true, new Random(23));
            return SkyTexture.Measure(planes[0], cell, cell, absent: null, fwhm: Fwhm);
        }
        // The fine read sees the stripes where they are (signal, one-way, gradient along x) and nothing where they are not.
        var (weight, coherence, cos2) = fine.FineAt(768, 512);
        weight.ShouldBeGreaterThan(0.5f, $"the stripes are signal (coherence {coherence}, cos2 {cos2}, amplitude at 4 px {fine.Amplitude(0, 2, 768, 512)})");
        coherence.ShouldBeGreaterThan(0.5f, "and run one way");
        cos2.ShouldBeGreaterThan(0.5f, "with their gradient along x");
        fine.FineAt(256, 512).Weight.ShouldBeLessThan(0.2f, "while the noise-only half holds none");

        var (byCoarse, byFine) = (Read(coarse), Read(fine));

        // At 4 px, where the 8 px stripes' energy is: against the coarse wave, the coarse steer runs along it and the fine
        // steer across it, as the plate's stripes do.
        byCoarse.Scales[2].Alignment.ShouldBeGreaterThan(0.3, "a coarse steer runs along the coarse contours");
        byFine.Scales[2].Alignment.ShouldBeLessThan(-0.3, "a fine steer follows the plate's own stripes across them");
    }

    // A frame whose right half holds a fine texture above the noise: a Gaussian field, or the same field's log-normal, a
    // nebula's clumps, at the same RMS.
    private static Image TexturedPlate(bool clumpy)
    {
        const int size = 1024;
        var rng = new Random(15);
        // Index 2.5: clumps wider than the PSF, which the source finder does not take for stars (the tails are read off the
        // pixels clear of the plate's sources, as the amplitude maps are).
        var field = SyntheticBackground.PowerLawField(size, 2.5, new Random(16));
        var sd = Math.Sqrt(field.Sum(static v => v * v) / field.Length);
        var texture = field.Select(v => clumpy ? Math.Exp(1.2 * v / sd) : v / sd).ToArray();
        var mean = texture.Average();
        var rms = Math.Sqrt(texture.Sum(v => (v - mean) * (v - mean)) / texture.Length);
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var fine = x >= size / 2 ? 4 * Noise * (texture[(y * size) + x] - mean) / rms : 0.0;
                plane[y, x] = (float)(0.1 + (0.02 * x / size) + (Noise * Gaussian(rng)) + fine);
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    [Fact]
    public void TheTailsReadRecoversATexturesLocalKurtosisAboveTheNoise()
    {
        // Every pixel clean: the read alone, without the source finder's mask (which takes a bright clump for a star).
        static float[] Luminance(Image image) => image.GetChannelSpan(0).ToArray();
        const int size = 1024;
        var clean = Enumerable.Repeat(1f, size * size).ToArray();
        var (smooth, _) = SyntheticBackground.SignalKurtosis(Luminance(TexturedPlate(clumpy: false)), clean, size, size, firstKept: 4);
        var (clumpy, _) = SyntheticBackground.SignalKurtosis(Luminance(TexturedPlate(clumpy: true)), clean, size, size, firstKept: 4);

        static double MedianOver(float[] map, int x0, int x1)
        {
            var v = Enumerable.Range(64, 1024 - 128).SelectMany(y => Enumerable.Range(x0, x1 - x0).Select(x => map[(y * 1024) + x]))
                .Where(static k => !float.IsNaN(k)).Order().ToArray();
            return v.Length > 0 ? v[v.Length / 2] : double.NaN;
        }
        MedianOver(smooth, 576, 960).ShouldBeLessThan(1.0, "a Gaussian texture's signal has no excess kurtosis");
        MedianOver(clumpy, 576, 960).ShouldBeGreaterThan(2.5, "a log-normal's clumps have (a width of 1.2 reads 2 to 7 per scale locally)");
        var unread = Enumerable.Range(64, 1024 - 128).SelectMany(y => Enumerable.Range(64, 384).Select(x => smooth[(y * 1024) + x]))
            .Count(static k => float.IsNaN(k));
        ((double)unread / ((1024 - 128) * 384)).ShouldBeGreaterThan(0.9, "and a noise-only sky holds no signal to read");
    }

    [Fact]
    public void TailsFromASmoothPlateDrawASmootherTextureThanTheOneWidth()
    {
        var plate = TexturedPlate(clumpy: false);
        var oneWidth = SyntheticBackground.Build(plate, absent: null, Fwhm);
        var fromPlate = SyntheticBackground.Build(plate, absent: null, Fwhm, tailsFromPlate: true);
        fromPlate.LogNormalWidthAt(768, 512).ShouldBeLessThan(SyntheticBackground.LogNormalSigma - 0.3, "the plate's texture is near Gaussian");

        double Kurtosis(SyntheticBackground background)
        {
            const int cell = 384;
            var planes = background.Preview(768, 512, cell, [Noise], noisy: true, new Random(25));
            return SkyTexture.Measure(planes[0], cell, cell, absent: null, fwhm: Fwhm).Scales[2].Kurtosis;
        }
        Kurtosis(fromPlate).ShouldBeLessThan(Kurtosis(oneWidth) - 1, "so its drawn texture has lighter tails at 4 px than the one width's");
    }

    [Fact]
    public void NebulaKnotsFallWhereThePlateHasNebulaAtAFewTimesItsSignalAndInItsColour()
    {
        // Two channels, the second's texture half the first's: the nebula's colour.
        const int size = 1024;
        var rng = new Random(17);
        var field = SyntheticBackground.PowerLawField(size, 2.5, new Random(18));
        var sd = Math.Sqrt(field.Sum(static v => v * v) / field.Length);
        var (red, green) = (new float[size, size], new float[size, size]);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var fine = x >= size / 2 ? 4 * Noise * field[(y * size) + x] / sd : 0.0;
                red[y, x] = (float)(0.1 + (Noise * Gaussian(rng)) + fine);
                green[y, x] = (float)(0.1 + (Noise * Gaussian(rng)) + (0.5 * fine));
            }
        }
        var background = SyntheticBackground.Build(new Image([red, green], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()), absent: null, Fwhm,
            nebulaKnots: true);
        var noise = new double[] { Noise, Noise };

        var (onSky, onNebula) = (0, 0);
        for (var draw = 0; draw < 20; draw++)
        {
            background.Cell(64, 384, 256, noise, new Random(200 + draw), out var sky);
            onSky += sky.Length;
            background.Cell(640, 384, 256, noise, new Random(300 + draw), out var nebula);
            foreach (var k in nebula)
            {
                onNebula++;
                var (fx, fy) = (640 + (int)k.X, 384 + (int)k.Y);
                double Rms(int c) => Math.Sqrt(Enumerable.Range(0, background.FirstKept).Sum(j => Math.Pow(background.Amplitude(c, j, fx, fy), 2)));
                (k.Peak[0] / Rms(0)).ShouldBeInRange(SyntheticBackground.NebulaKnotMin * 0.999, SyntheticBackground.NebulaKnotMax * 1.001,
                    "a knot is a few times the nebula's own signal there");
                (k.Peak[0] / k.Peak[1]).ShouldBe(Rms(0) / Rms(1), 1e-6, "in the nebula's colour there");
                (k.Peak[0] / k.Peak[1]).ShouldBeGreaterThan(1.5, "which is redder than green, as the plate is");
            }
        }
        onSky.ShouldBe(0, "a noise-only sky holds no nebula to hold a knot");
        onNebula.ShouldBeGreaterThan(20, "about three knots a cell of nebula");
    }

    [Fact]
    public void AnElongatedNebulaKnotLiesAlongTheContourItIsSteeredBy()
    {
        // The wave's gradient is vertical everywhere, so its contours, and an elongated knot, run along x.
        var background = SyntheticBackground.Build(WavePlate(), absent: null, Fwhm, steering: new SyntheticBackground.Steering(1.0, 4.0),
            nebulaKnots: true);
        var elongated = 0;
        for (var draw = 0; draw < 20; draw++)
        {
            background.Cell(640, 384, 256, [Noise], new Random(400 + draw), out var knots);
            foreach (var k in knots.Where(static k => k.SigmaMajorPx > 2 * k.SigmaMinorPx))
            {
                elongated++;
                var off = Math.Abs(Math.Sin(k.AngleRad));
                off.ShouldBeLessThan(0.3, $"along the contour, not across it (angle {k.AngleRad:F2} rad)");
            }
        }
        elongated.ShouldBeGreaterThan(5);
    }

    [Fact]
    public void APreviewCellsSeedIsTheSameInEveryProcess()
    {
        // HashCode.Combine is seeded afresh per process, so a cell seeded by it drew another texture on every run; a value
        // pinned here fails in any process if it comes back.
        // 1 ^ (3881 * 73856093) ^ (961 * 19349663), wrapped to 32 bits.
        SyntheticBackgroundPreview.CellSeed(1, 3881, 961).ShouldBe(-392756933);
    }

    [Fact]
    public void APreviewReadsThePlatesNoiseAndPutsItBackOnItsCentredCell()
    {
        var plate = Plate(6);
        var noise = SyntheticBackground.PlateNoise(plate, absent: null);
        noise[0].ShouldBe(Noise, 0.1 * Noise, "the plate's white noise, read off its differences");

        var background = SyntheticBackground.Build(plate, absent: null, Fwhm);
        var quiet = background.Preview(300, 200, 128, noise, noisy: false, new Random(7));
        var noisy = background.Preview(300, 200, 128, noise, noisy: true, new Random(7));

        // Centred: the cell's median is the gradient's 0.1 + 0.02 x / Size at the frame's x = 300 (a median, so a knot that
        // fell on the middle does not move it).
        var sorted = quiet[0].Order().ToArray();
        ((double)sorted[sorted.Length / 2]).ShouldBe(0.1 + (0.02 * 300 / Size), Noise);
        var added = noisy[0].Zip(quiet[0], static (a, b) => (double)(a - b)).ToArray();
        var sd = Math.Sqrt(added.Select(static d => d * d).Average());
        sd.ShouldBe(noise[0], 0.1 * noise[0], "the preview's grain is the plate's noise");
    }
}
