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
