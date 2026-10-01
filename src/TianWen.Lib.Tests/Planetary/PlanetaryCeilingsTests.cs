using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R8 part 1's ceilings (docs/plans/planetary-restoration.md): a synthetic capture's PSFs written and read back, a twin's frames
/// following their true transfers times the truth, and the multi-frame Wiener gaining over shift-and-add exactly where its frames'
/// blur differs from one to the next.
/// </summary>
public class PlanetaryCeilingsTests
{
    private static readonly DateTimeOffset Night = new DateTimeOffset(2022, 9, 3, 12, 10, 0, TimeSpan.Zero);

    [Fact]
    public void APsfFileReadsBackWhatWasWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tianwen-psf-{Guid.NewGuid():N}.psf");
        try
        {
            const int grid = 8;
            var header = new SyntheticPsfHeader(grid, 2, 0.5, 650e-9, 0.05, 5, 16.8, 0.2, 32, [.. Enumerable.Range(0, grid * grid).Select(i => i / 100.0)]);
            using (var writer = new SyntheticPsfFile.Writer(path, header))
            {
                writer.Append(new SyntheticFrameOptics([.. Enumerable.Range(0, grid * grid).Select(i => i / 64.0)], 1.5, -2.25, 0.9));
                writer.Append(new SyntheticFrameOptics(new double[grid * grid], 0, 0, 1));
            }
            using var reader = SyntheticPsfFile.Reader.Open(path).ShouldNotBeNull();
            reader.Header.Oversample.ShouldBe(2);
            reader.Header.ScatterCoreArcsec.ShouldBe(5);
            reader.Header.Diffraction[10].ShouldBe(0.10, 1e-12);
            var psf = new double[grid * grid];
            reader.TryRead(psf, out var x, out var y, out var b).ShouldBeTrue();
            (x, y, b).ShouldBe((1.5, -2.25, 0.9));
            psf[32].ShouldBe(0.5, 1e-6);
            reader.TryRead(psf, out _, out _, out _).ShouldBeTrue();
            reader.TryRead(psf, out _, out _, out _).ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task ATwinsFramesAreTheirTrueTransferTimesTheTruth()
    {
        // A small twin with the still layer and the scatter on, its PSFs taken as it is made, and the truth rendered as
        // planetary-degrade renders it, at the instant the twin's first render is.
        const int size = 128;
        const double scale = 0.49;
        var map = PlanetaryDegradeTests.BandedMap();
        var times = Enumerable.Range(0, 12).Select(i => Night + TimeSpan.FromMilliseconds(5 * i)).ToImmutableArray();
        var pupil = new Pupil(0.254, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.001);
        var options = new DegradeOptions(pupil, 650e-9)
        {
            R0M = 0.1,
            LocalR0M = 0.05,
            ScatterFraction = 0.05,
            FullScaleAdu = 65535,
            OffsetAdu = 100,
            ReadNoiseAdu = 2,
            ElectronsPerAdu = 4,
            DiskLevelAdu = 2000,
            ScreenSamples = 128,
            KeepScreenTilt = true,
        };
        var reference = new DiskPlacement((size / 2) - 0.3, (size / 2) + 0.2, 20, NorthAngleDeg: -80);
        var none = ImmutableArray.CreateRange(Enumerable.Repeat(0.0, times.Length));
        var frames = new ushort[times.Length][];
        var optics = new (double[] Psf, double X, double Y, double B)[times.Length];
        await PlanetaryDegrade.MakeAsync(map, CatalogIndex.Jupiter, times, reference, scale, none, none, [], size, size, options,
            (index, samples) => frames[index] = samples, optics: (index, o) => optics[index] = ((double[])o.Psf.Clone(), o.ShiftX, o.ShiftY, o.Brightness),
            cancellationToken: TestContext.Current.CancellationToken);

        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night + TimeSpan.FromSeconds(options.RenderEverySeconds / 2));
        var rendered = PlanetaryRender.RenderDiffracted(map, aspect, reference, size, size, options.MinnaertK, pupil, options.WavelengthM, scale);
        var truth = ScaledLikeTheTwin(rendered, size, reference, options.DiskLevelAdu);

        var bound = new MultiFrameBound(SyntheticPsfHeader.For(options, scale), size, 0, 0);
        var check = bound.NewModelCheck(bound.Window(truth, size, size));
        for (var i = 0; i < frames.Length; i++)
        {
            check.Add(bound.Prepare(frames[i], size, size, optics[i].Psf, optics[i].X, optics[i].Y, optics[i].B));
        }
        var ratios = check.ScaledResidualOverNoise;
        TestContext.Current.TestOutputHelper?.WriteLine($"residual over noise, bands 1 to 4: {string.Join(", ", check.ResidualOverNoise.Select(r => r.ToString("0.000")))}; scale {check.Scale:0.0000}; scaled {string.Join(", ", ratios.Select(r => r.ToString("0.000")))}");
        check.Scale.ShouldBe(1, 0.02);
        for (var band = 1; band < 4; band++)
        {
            ratios[band].ShouldBeLessThan(1.5, $"band {band + 1}");
        }
    }

    [Fact]
    public void TheMultiFrameWienerGainsOverShiftAndAddOnlyWhereTheFramesBlurDiffers()
    {
        // Frames of a known texture through Gaussian PSFs (the perfect telescope a point, so the transfer is the PSF's alone): the
        // same PSF in every frame, then half the frames sharp and half blurred.
        const int size = 64;
        var truth = Texture(size);
        var same = Restorations(truth, size, Enumerable.Repeat(1.6, 16).ToArray());
        var mixed = Restorations(truth, size, Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? 0.7 : 2.8).ToArray());
        TestContext.Current.TestOutputHelper?.WriteLine($"one PSF: shift-and-add {same.ShiftAndAdd:0.0000}, multi-frame {same.MultiFrame:0.0000}; two: shift-and-add {mixed.ShiftAndAdd:0.0000}, multi-frame {mixed.MultiFrame:0.0000}");
        same.MultiFrame.ShouldBe(same.ShiftAndAdd, same.ShiftAndAdd * 0.02);
        mixed.MultiFrame.ShouldBeLessThan(mixed.ShiftAndAdd * 0.9);
    }

    [Fact]
    public void EqualWeightsGatherAsPlainShiftAndAdd()
    {
        // #1083's weighted gathering with every weight one is shift-and-add, restored the same way.
        const int size = 64;
        var (bound, frames, prior) = Frames(Texture(size), size, Enumerable.Range(0, 8).Select(i => 0.8 + (0.25 * i)).ToArray());
        var (plain, weighted) = (bound.NewSums(), bound.NewWeightedSums((_, _) => 1));
        foreach (var frame in frames)
        {
            var back = bound.BackShift(frame);
            plain.Add(frame, back);
            weighted.Add(frame, back);
        }
        var (a, b) = (plain.ShiftAndAdd(prior), weighted.Restored(prior));
        for (var i = 0; i < a.Length; i++)
        {
            (a[i] - b[i]).Magnitude.ShouldBeLessThan(1e-9 * (1 + a[i].Magnitude));
        }
    }

    [Fact]
    public void PerFrequencySelectionGainsNothingWhereTheSharpestFramesAreSharpestEverywhere()
    {
        // Round Gaussians are ordered the same at every frequency, so the sharpest frames by the whole frame are the best at each one; the
        // blurriest instead leave the per-frequency choice a gain.
        const int size = 64;
        var sigmas = Enumerable.Range(0, 12).Select(i => 0.7 + (0.2 * i)).ToArray();
        var (bound, frames, _) = Frames(Texture(size), size, sigmas);
        var (sharpest, blurriest) = (bound.NewSelectionCeiling(0.25, 0.5, 3), bound.NewSelectionCeiling(0.25, 0.5, 3));
        for (var i = 0; i < frames.Length; i++)
        {
            sharpest.Add(frames[i], i < 3);
            blurriest.Add(frames[i], i >= frames.Length - 3);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"over the sharpest three {sharpest.Ratio:0.0000}, over the blurriest {blurriest.Ratio:0.0000}");
        sharpest.Ratio.ShouldBe(1, 1e-6);
        blurriest.Ratio.ShouldBeGreaterThan(2);
    }

    // Frames of `truth` through Gaussian PSFs of `sigmas` (a perfect telescope a point), each moved a little, prepared for a bound, with
    // the truth's prior.
    private static (MultiFrameBound Bound, MultiFrameBound.Frame[] Frames, double[] Prior) Frames(float[] truth, int size, double[] sigmas)
    {
        const int grid = 32;
        var delta = new double[grid * grid];
        delta[((grid / 2) * grid) + (grid / 2)] = 1;
        var bound = new MultiFrameBound(new SyntheticPsfHeader(grid, 1, 0.5, 650e-9, 0, 5, 100, 2, 1e6, delta), size, 0, 0);
        var random = new Random(5);
        var frames = new MultiFrameBound.Frame[sigmas.Length];
        for (var i = 0; i < sigmas.Length; i++)
        {
            var psf = Gaussian(grid, sigmas[i]);
            var (dx, dy) = ((random.NextDouble() * 2) - 1, (random.NextDouble() * 2) - 1);
            var plane = Convolve(truth, size, psf, grid, dx, dy);
            var samples = plane.Select(v => (ushort)Math.Clamp(Math.Round(100 + v), 0, 65535)).ToArray();
            frames[i] = bound.Prepare(samples, size, size, psf, dx, dy, 1);
        }
        return (bound, frames, bound.Prior(truth, size, size));
    }

    [Fact]
    public void GainsFittedJointlyOverTheBandsLeaveLessErrorThanOneBandAtATime()
    {
        // A trous bands overlap in frequency, so the band-by-band least-squares gains are not the best set for the bands of the result.
        const int size = 128;
        var disk = new MetricDisk(63.5, 63.5, 44);
        var truth = Texture(size, 1000, 44);
        var blurred = PlanetaryInverse.Apply(truth, size, size, f => Math.Exp(-2 * Math.PI * Math.PI * 1.6 * 1.6 * f * f));
        var random = new Random(5);
        var noisy = blurred.Select(v => (float)(v + (3 * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble())))).ToArray();
        var (oneAtATime, gains) = PlanetaryCeilings.PerBandOracle(noisy, truth, size, size, disk, 4);
        var (joint, jointGains) = PlanetaryCeilings.PerBandJointOracle(noisy, truth, size, size, disk, 4);
        var e1 = PlanetaryMetrics.Fidelity(oneAtATime, truth, size, size, disk, 4).Sum(f => f.Error);
        var e2 = PlanetaryMetrics.Fidelity(joint, truth, size, size, disk, 4).Sum(f => f.Error);
        TestContext.Current.TestOutputHelper?.WriteLine($"one band at a time {string.Join(", ", gains.Select(g => g.ToString("0.00")))}: {e1:0.000}; jointly {string.Join(", ", jointGains.Select(g => g.ToString("0.00")))}: {e2:0.000}");
        e2.ShouldBeLessThan(e1);
    }

    // The RMS error over the window of shift-and-add's Wiener and the multi-frame Wiener, frames through Gaussians of `sigmas`, shifted
    // a little each, with read noise.
    private static (double ShiftAndAdd, double MultiFrame) Restorations(float[] truth, int size, double[] sigmas)
    {
        const int grid = 32;
        var delta = new double[grid * grid];
        delta[((grid / 2) * grid) + (grid / 2)] = 1;
        var header = new SyntheticPsfHeader(grid, 1, 0.5, 650e-9, 0, 5, 100, 2, 1e6, delta);
        var bound = new MultiFrameBound(header, size, 0, 0);
        var sums = bound.NewSums();
        var random = new Random(3);
        for (var i = 0; i < sigmas.Length; i++)
        {
            var psf = Gaussian(grid, sigmas[i]);
            var (dx, dy) = ((random.NextDouble() * 2) - 1, (random.NextDouble() * 2) - 1);
            var spectrumPlane = Convolve(truth, size, psf, grid, dx, dy);
            var samples = new ushort[size * size];
            for (var p = 0; p < samples.Length; p++)
            {
                var noise = 2 * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
                samples[p] = (ushort)Math.Clamp(Math.Round(100 + spectrumPlane[p] + noise), 0, 65535);
            }
            sums.Add(bound.Prepare(samples, size, size, psf, dx, dy, 1));
        }
        var prior = bound.Prior(truth, size, size);
        double Rms(System.Numerics.Complex[] spectrum)
        {
            var plane = bound.ToDetector(spectrum, size, size);
            return Math.Sqrt(plane.Select((v, p) => (double)(v - truth[p]) * (v - truth[p])).Average());
        }
        return (Rms(sums.ShiftAndAdd(prior)), Rms(sums.MultiFrame(prior)));
    }

    // A plane through a PSF centred on its grid's middle, moved by (dx, dy), periodically, in the Fourier domain.
    private static float[] Convolve(float[] plane, int size, double[] psf, int grid, double dx, double dy)
    {
        var field = new System.Numerics.Complex[size * size];
        var kernel = new System.Numerics.Complex[size * size];
        for (var i = 0; i < plane.Length; i++)
        {
            field[i] = plane[i];
        }
        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                kernel[((((y - (grid / 2)) + size) % size) * size) + (((x - (grid / 2)) + size) % size)] = psf[(y * grid) + x];
            }
        }
        Fft2D.Forward(field, size, size);
        Fft2D.Forward(kernel, size, size);
        for (var ky = 0; ky < size; ky++)
        {
            var fy = (ky < size / 2 ? ky : ky - size) / (double)size;
            for (var kx = 0; kx < size; kx++)
            {
                var fx = (kx < size / 2 ? kx : kx - size) / (double)size;
                var i = (ky * size) + kx;
                field[i] *= kernel[i] * System.Numerics.Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * dx) + (fy * dy)));
            }
        }
        Fft2D.Inverse(field, size, size);
        return [.. field.Select(c => (float)c.Real)];
    }

    private static double[] Gaussian(int grid, double sigma)
    {
        var psf = new double[grid * grid];
        double sum = 0;
        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                var (u, v) = (x - (grid / 2), y - (grid / 2));
                sum += psf[(y * grid) + x] = Math.Exp(-((u * u) + (v * v)) / (2 * sigma * sigma));
            }
        }
        for (var i = 0; i < psf.Length; i++)
        {
            psf[i] /= sum;
        }
        return psf;
    }

    // A texture of blobs on a disk of `radius`, in ADU over the sky.
    private static float[] Texture(int size, double level = 1000, double radius = 20)
    {
        var random = new Random(11);
        var c = (size / 2) - 0.5;
        var spread = 1.5 * radius;
        var blobs = Enumerable.Range(0, 60).Select(_ => (X: c + (random.NextDouble() * spread) - (spread / 2), Y: c + (random.NextDouble() * spread) - (spread / 2), A: (random.NextDouble() * 300) - 150, S: 0.8 + (random.NextDouble() * 2))).ToArray();
        var plane = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = Math.Sqrt(((x - c) * (x - c)) + ((y - c) * (y - c)));
                var v = level;
                foreach (var b in blobs)
                {
                    v += b.A * Math.Exp(-(((x - b.X) * (x - b.X)) + ((y - b.Y) * (y - b.Y))) / (2 * b.S * b.S));
                }
                plane[(y * size) + x] = (float)(r < radius ? v : 0);
            }
        }
        return plane;
    }

    // The render scaled so its disk's mean inside 0.8 radii is the disk level, as planetary-degrade writes its truth.
    private static float[] ScaledLikeTheTwin(float[] render, int size, DiskPlacement placement, double diskLevel)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (dx, dy) = (x - placement.CenterX, y - placement.CenterY);
                if ((dx * dx) + (dy * dy) < 0.64 * placement.EquatorialRadius * placement.EquatorialRadius)
                {
                    sum += render[(y * size) + x];
                    count++;
                }
            }
        }
        var gain = diskLevel / (sum / count);
        return [.. render.Select(v => (float)(v * gain))];
    }
}
