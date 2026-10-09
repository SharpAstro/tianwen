using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The denoiser's per-pixel noise plane (<see cref="StretchedNoise"/>) against the thing it models: shot noise
/// injected by the exporter's own <see cref="LinearDegradation.AddNoiseInPlace"/>, pushed through the NAFNet input
/// stretch, measured back by level. The plane has to be right where the scalar was wrong, in bright structure,
/// so the test frame has a sky and a bright nebula and every level between.
/// </summary>
[Collection("Imaging")]
public class StretchedNoiseTests(ITestOutputHelper output)
{
    private const int Size = 384;
    private const int Channels = 3;

    /// <summary>A sky of 0.02 with a broad Gaussian nebula peaking at 0.6, identical in every channel.</summary>
    private static float[][] CleanFrame()
    {
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = x - (Size / 2.0);
                var dy = y - (Size / 2.0);
                plane[(y * Size) + x] = (float)(0.02 + (0.58 * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * 70.0 * 70.0))));
            }
        }
        return [.. Enumerable.Range(0, Channels).Select(_ => plane.AsSpan().ToArray())];
    }

    /// <summary>
    /// E16c step 2: the plane from a MEASURED standard error is the plane from the noise model with the model replaced by
    /// the measurement. On a flat tile whose measurement is what the model gives there, the two are the same plane; where
    /// the measurement doubles, so does the plane.
    /// </summary>
    [Fact]
    public void AMeasuredPlaneIsTheModelsPlaneWithTheMeasurementInItsPlace()
    {
        const int size = 64;
        var stretch = new StretchedNoise.ChannelStretch(0.08, 0.001);
        var calibration = new LinearDegradation.NoiseCalibration(0.0, 0.02, 0.004, 1);
        const float level = 0.3f;
        var stretched = Enumerable.Repeat(level, size * size).ToArray();
        var linear = Image.MidtonesTransferFunction(1.0 - stretch.MidtonesBalance, level) + stretch.OrigMin;
        var modelSigma = (float)calibration.SigmaAt(linear, 1.0);

        var fromModel = StretchedNoise.Plane([stretched], size, size, [stretch], [calibration], 1.0);
        var fromMeasurement = StretchedNoise.MeasuredPlane([stretched], [Enumerable.Repeat(modelSigma, size * size).ToArray()], size, size, [stretch]);
        for (var i = 0; i < fromModel.Length; i++)
        {
            fromMeasurement[i].ShouldBe(fromModel[i], fromModel[i] * 1e-5f);
        }

        var doubled = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                doubled[(y * size) + x] = x < size / 2 ? modelSigma : 2 * modelSigma;
            }
        }
        var plane = StretchedNoise.MeasuredPlane([stretched], [doubled], size, size, [stretch]);
        var row = (size / 2) * size;
        (plane[row + size - 4] / plane[row + 4]).ShouldBe(2f, 0.01f, "well clear of the step, the plane doubles where the measurement does");
    }

    /// <summary>
    /// E16c: the fine-scale estimator's ratio for uncorrelated noise is Starck and Murtagh's, so it has to be the sigma
    /// <see cref="ATrousWaveletTransform"/>'s finest scale itself gives unit white noise.
    /// </summary>
    [Fact]
    public void TheFinestScalesWhiteRatioIsTheStarletsOwn()
    {
        const int size = 512;
        var noise = NoiseField.White(size, size, new Random(11));
        var measured = Spread(ATrousWaveletTransform.Decompose(noise, size, size, 1).Detail(0).ToArray(), size) / Spread(noise, size);
        output.WriteLine($"finest scale: {measured:F4} against {StretchedNoise.WhiteNoiseFineScaleRatio:F4}");
        measured.ShouldBe(StretchedNoise.WhiteNoiseFineScaleRatio, StretchedNoise.WhiteNoiseFineScaleRatio * 0.01);
    }

    /// <summary>
    /// E16c's candidate on the frame that defeats the shipped estimator: a mono frame filled edge to edge with thin
    /// filaments of a few sigma, so no block of it is clean sky (the ASI1600MM's H-alpha eta Car, read at 0.31 of its half
    /// pairs' truth). The shipped estimator must over-read it, or the frame cannot tell them apart; the fine-scale one,
    /// given the finest scale's ratio of this noise (what a calibration per integration supplies), must read it far nearer
    /// the injected noise. On the same frame without filaments it must find the noise.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFineScaleFindsTheNoiseUnderFilamentsThatFillTheFrame(bool warped)
    {
        const int size = 512;
        var truth = new LinearDegradation.NoiseCalibration(PedestalAdu: 0.0, BackgroundAdu: 0.02, OneSubSigmaAdu: 0.004, StackedFrames: 1);
        var rng = new Random(7);
        var filaments = FilamentField(size, rng, count: 900, amplitude: 3.0 * truth.OneSubSigmaAdu);
        var shape = warped ? NoiseField.Warped(size, size, 8, rng, 0.5) : NoiseField.White(size, size, rng);
        // The finest scale's ratio of THIS noise field.
        var ratio = Spread(ATrousWaveletTransform.Decompose(shape, size, size, 1).Detail(0).ToArray(), size) / Spread(shape, size);

        foreach (var filled in new[] { true, false })
        {
            var p = new float[size * size];
            for (var i = 0; i < p.Length; i++)
            {
                p[i] = (float)(truth.BackgroundAdu + (filled ? filaments[i] : 0.0));
            }
            LinearDegradation.AddNoiseInPlace(p, shape, truth, 1.0);
            var plane = new float[size, size];
            Buffer.BlockCopy(p, 0, plane, 0, p.Length * sizeof(float));
            var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });
            var (origMin, balances) = image.MtfStretchParameters(0.25);
            StretchedNoise.ChannelStretch[] stretches = [new StretchedNoise.ChannelStretch(balances[0], origMin[0])];

            StretchedNoise.TryEstimateCalibration(image, stretches, null, StretchedNoise.NoiseEstimator.Blocks, null, out var blocks).ShouldBeTrue();
            StretchedNoise.TryEstimateCalibration(image, stretches, null, StretchedNoise.NoiseEstimator.FineScale, [ratio], out var fine).ShouldBeTrue();
            // Each calibration's anchor is the noise at ITS background, which on a frame with no clean sky sits above the
            // true one; so they are compared as noise.check compares them, by the noise each predicts at a level, the sky's.
            var sky = truth.BackgroundAdu;
            var blocksRatio = blocks[0].SigmaAt(sky, 1.0) / truth.SigmaAt(sky, 1.0);
            var fineRatio = fine[0].SigmaAt(sky, 1.0) / truth.SigmaAt(sky, 1.0);
            output.WriteLine($"warped {warped}, filaments {filled}: finest-scale ratio {ratio:F3}; at the sky blocks {blocksRatio:F3}x, fine {fineRatio:F3}x " +
                             $"(backgrounds {blocks[0].BackgroundAdu:F4} and {fine[0].BackgroundAdu:F4} against {sky:F4})");
            if (filled)
            {
                // Measured 2026-10-05: the blocks 1.55 to 1.57 times, the fine scale 1.05 (white) and 1.09 (warped).
                blocksRatio.ShouldBeGreaterThan(1.3, "the frame must defeat the shipped estimator, or it tests nothing");
                fineRatio.ShouldBeLessThan(1.15);
                fineRatio.ShouldBeLessThan(blocksRatio - 0.3);
            }
            else
            {
                fineRatio.ShouldBe(1.0, 0.06);
            }
        }
    }

    // The sample standard deviation inside a 32 px rim, clear of the mirror boundary.
    private static double Spread(float[] plane, int side)
    {
        double sum = 0, sumSq = 0;
        var n = 0;
        for (var y = 32; y < side - 32; y++)
        {
            for (var x = 32; x < side - 32; x++)
            {
                double v = plane[(y * side) + x];
                sum += v;
                sumSq += v * v;
                n++;
            }
        }
        var mean = sum / n;
        return Math.Sqrt((sumSq / n) - (mean * mean));
    }

    /// <summary>Thin filaments (a Gaussian profile across, 1.2 to 2.5 px wide, 40 to 200 px long) at random places and
    /// angles, each 0.5 to 1.5 times <paramref name="amplitude"/> at its crest.</summary>
    internal static double[] FilamentField(int size, Random rng, int count, double amplitude)
    {
        var field = new double[size * size];
        for (var k = 0; k < count; k++)
        {
            var (cx, cy) = (rng.NextDouble() * size, rng.NextDouble() * size);
            var theta = rng.NextDouble() * Math.PI;
            var (ux, uy) = (Math.Cos(theta), Math.Sin(theta));
            var width = 1.2 + (rng.NextDouble() * 1.3);
            var length = 40 + (rng.NextDouble() * 160);
            var crest = amplitude * (0.5 + rng.NextDouble());
            var reach = (length / 2) + (4 * width);
            for (var y = Math.Max(0, (int)(cy - reach)); y < Math.Min(size, (int)(cy + reach) + 1); y++)
            {
                for (var x = Math.Max(0, (int)(cx - reach)); x < Math.Min(size, (int)(cx + reach) + 1); x++)
                {
                    var along = ((x - cx) * ux) + ((y - cy) * uy);
                    var across = (-(x - cx) * uy) + ((y - cy) * ux);
                    if (Math.Abs(along) <= length / 2 && Math.Abs(across) <= 4 * width)
                    {
                        field[(y * size) + x] += crest * Math.Exp(-(across * across) / (2 * width * width));
                    }
                }
            }
        }
        return field;
    }

    private static Image ToImage(float[][] channels, int size = Size)
    {
        var data = new float[channels.Length][,];
        for (var c = 0; c < channels.Length; c++)
        {
            var p = new float[size, size];
            Buffer.BlockCopy(channels[c], 0, p, 0, channels[c].Length * sizeof(float));
            data[c] = p;
        }
        return new Image(data, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
    }

    private static float[] Channel(Image img, int c) => img.GetChannelSpan(c).ToArray();

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.25)]
    public void ThePlaneMatchesTheInjectedNoiseAtEveryLevelAfterTheStretch(double depthScale)
    {
        var clean = CleanFrame();
        var cleanImage = ToImage(clean);
        var (origMin, balances) = cleanImage.MtfStretchParameters(0.25);
        var calibration = new LinearDegradation.NoiseCalibration(PedestalAdu: 0.0, BackgroundAdu: 0.02, OneSubSigmaAdu: 0.004, StackedFrames: 16);

        var rng = new Random(7);
        var noisy = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            noisy[c] = [.. clean[c]];
            LinearDegradation.AddNoiseInPlace(noisy[c], NoiseField.White(Size, Size, rng), calibration, depthScale);
        }
        var cleanStretched = cleanImage.MtfStretchWith(origMin, balances);
        var noisyStretched = ToImage(noisy).MtfStretchWith(origMin, balances);

        // What the model predicts, computed as a runner would: from the NOISY stretched tile it feeds the net.
        var stretches = Enumerable.Range(0, Channels).Select(c => new StretchedNoise.ChannelStretch(balances[c], origMin[c])).ToArray();
        var plane = StretchedNoise.Plane(
            [.. Enumerable.Range(0, Channels).Select(c => Channel(noisyStretched, c))], Size, Size, stretches, calibration, depthScale);

        // What happened: the luminance of (noisy - clean) after the stretch, its sigma per clean-level bin.
        var levelEdges = new[] { 0.20, 0.30, 0.45, 0.60, 0.72, 0.80 };
        var cleanLum = new double[Size * Size];
        var diffLum = new double[Size * Size];
        for (var c = 0; c < Channels; c++)
        {
            var cs = cleanStretched.GetChannelSpan(c);
            var ns = noisyStretched.GetChannelSpan(c);
            for (var i = 0; i < cleanLum.Length; i++)
            {
                cleanLum[i] += cs[i] / Channels;
                diffLum[i] += (ns[i] - cs[i]) / Channels;
            }
        }

        var skyPredicted = double.NaN;
        var brightPredicted = double.NaN;
        for (var b = 0; b + 1 < levelEdges.Length; b++)
        {
            var measured = new List<double>();
            var predicted = new List<double>();
            for (var i = 0; i < cleanLum.Length; i++)
            {
                if (cleanLum[i] >= levelEdges[b] && cleanLum[i] < levelEdges[b + 1])
                {
                    measured.Add(diffLum[i]);
                    predicted.Add(plane[i] / StretchedNoise.PlaneScale);
                }
            }
            if (measured.Count < 2000)
            {
                continue;
            }
            var mean = measured.Average();
            var sigma = Math.Sqrt(measured.Sum(v => (v - mean) * (v - mean)) / (measured.Count - 1));
            var model = predicted.Average();
            output.WriteLine($"depth {depthScale}: level {levelEdges[b]:F2}-{levelEdges[b + 1]:F2}  {measured.Count,6} px  measured {sigma:E3}  plane {model:E3}  ratio {model / sigma:F3}");
            (model / sigma).ShouldBe(1.0, 0.08, $"level {levelEdges[b]:F2} to {levelEdges[b + 1]:F2}");
            if (b == 0)
            {
                skyPredicted = model;
            }
            brightPredicted = model;
        }

        // The point of the plane: the brightest level is told far less noise than the sky.
        brightPredicted.ShouldBeLessThan(skyPredicted * 0.7);
    }

    /// <summary>
    /// The inference-side estimate on the frame that broke the scalar: most of it covered in fine nebular texture.
    /// The darkest-half MAD of such a frame reads the texture as noise; the block estimate, each block divided by
    /// what the model says its level carries and the quiet quantile taken, has to find the injected sigma.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEstimateFindsTheNoiseUnderNebularTexture(bool warped)
    {
        const int size = 512;
        var truth = new LinearDegradation.NoiseCalibration(PedestalAdu: 0.0, BackgroundAdu: 0.02, OneSubSigmaAdu: 0.004, StackedFrames: 16);
        var rng = new Random(3);
        var channels = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            var p = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // Sky at 0.02; over three quarters of the frame a nebula whose level rises to 0.25 and whose
                    // texture, at 1 to 3 px, is several times the noise.
                    var neb = x > size / 4 ? 0.23 * Math.Min(1.0, (x - (size / 4)) / 200.0) : 0.0;
                    var texture = neb > 0 ? 0.015 * Math.Sin(x / 1.3) * Math.Sin(y / 1.7) + (0.01 * Math.Sin((x + y) / 2.9)) : 0.0;
                    p[(y * size) + x] = (float)(0.02 + neb + texture);
                }
            }
            var shape = warped ? NoiseField.Warped(size, size, 8, rng, 0.5) : NoiseField.White(size, size, rng);
            LinearDegradation.AddNoiseInPlace(p, shape, truth, 1.0);
            channels[c] = p;
        }
        var data = new float[Channels][,];
        for (var c = 0; c < Channels; c++)
        {
            var plane = new float[size, size];
            Buffer.BlockCopy(channels[c], 0, plane, 0, channels[c].Length * sizeof(float));
            data[c] = plane;
        }
        var image = new Image(data, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
        var (origMin, balances) = image.MtfStretchParameters(0.25);
        var stretches = Enumerable.Range(0, Channels).Select(c => new StretchedNoise.ChannelStretch(balances[c], origMin[c])).ToArray();

        var estimated = StretchedNoise.EstimateCalibration(image, stretches);

        // Each channel is its own noise draw under the same texture, so the three readings scatter about the
        // estimator's answer: their mean is held to what one channel was held to before each had its own estimate,
        // and each to a little more.
        estimated.Length.ShouldBe(Channels);
        var ratios = new double[Channels];
        for (var c = 0; c < Channels; c++)
        {
            ratios[c] = estimated[c].OneSubSigmaAdu / truth.OneSubSigmaAdu;
            output.WriteLine($"warped {warped}, channel {c}: estimated one-sub sigma {estimated[c].OneSubSigmaAdu:E3} against {truth.OneSubSigmaAdu:E3} " +
                             $"({ratios[c]:F3}x), background {estimated[c].BackgroundAdu:F4} against {truth.BackgroundAdu:F4}");
            ratios[c].ShouldBe(1.0, 0.15, $"channel {c}");
            estimated[c].BackgroundAdu.ShouldBe(truth.BackgroundAdu, 0.004, $"channel {c}");
        }
        ratios.Average().ShouldBe(1.0, 0.12);
    }

    /// <summary>
    /// A colour frame whose channels do NOT share one noise-per-level, as a real one does not: the sky sits at a
    /// different level in each (SMC 2026's measured floors, red lowest), and green, built from twice the photosites
    /// by a Bayer drizzle, carries half the variance per unit of signal. Each channel's own estimate has to find its
    /// own noise, and each channel's plane has to match the noise that was injected into it. Channel 0's calibration
    /// carried to every channel, which the estimator did before, reads green high by sqrt 2, so this test fails on
    /// the old estimator rather than passing on a frame that cannot tell. (It is asserted per channel, not on the
    /// luminance: after the stretch the darkest channel's noise dominates the luminance, and on this frame green's
    /// error moves it by only a few percent.)
    /// </summary>
    [Fact]
    public void EachChannelIsAnchoredOnItsOwnNoise()
    {
        const int size = 512;
        var skies = new[] { 0.012, 0.046, 0.032 };
        var photosites = new[] { 1.0, 2.0, 1.0 };
        // Red's sky at a signal-to-noise of 30, the rest following shot noise from the same collection rate.
        const double perUnitVariance = 4e-4 * 4e-4 / 0.012;
        var truths = new LinearDegradation.NoiseCalibration[Channels];
        var clean = new float[Channels][];
        var noisy = new float[Channels][];
        var rng = new Random(5);
        for (var c = 0; c < Channels; c++)
        {
            truths[c] = new LinearDegradation.NoiseCalibration(0.0, skies[c], Math.Sqrt(perUnitVariance * skies[c] / photosites[c]), 1);
            var p = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - (size / 2.0);
                    var dy = y - (size / 2.0);
                    p[(y * size) + x] = (float)(skies[c] * (1.0 + (4.0 * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * 90.0 * 90.0)))));
                }
            }
            clean[c] = p;
            noisy[c] = [.. p];
            LinearDegradation.AddNoiseInPlace(noisy[c], NoiseField.White(size, size, rng), truths[c], 1.0);
        }
        var noisyImage = ToImage(noisy, size);
        var (origMin, balances) = noisyImage.MtfStretchParameters(0.25);
        var stretches = Enumerable.Range(0, Channels).Select(c => new StretchedNoise.ChannelStretch(balances[c], origMin[c])).ToArray();

        var estimated = StretchedNoise.EstimateCalibration(noisyImage, stretches);

        for (var c = 0; c < Channels; c++)
        {
            output.WriteLine($"channel {c}: sigma {estimated[c].OneSubSigmaAdu:E3} against {truths[c].OneSubSigmaAdu:E3} " +
                             $"({estimated[c].OneSubSigmaAdu / truths[c].OneSubSigmaAdu:F3}x), background {estimated[c].BackgroundAdu:F4} against {skies[c]:F4}");
            (estimated[c].OneSubSigmaAdu / truths[c].OneSubSigmaAdu).ShouldBe(1.0, 0.08, $"channel {c}");
            estimated[c].BackgroundAdu.ShouldBe(skies[c], skies[c] * 0.1, $"channel {c}");
        }

        // Each channel's plane over the sky, from its own estimate and from channel 0's carried to it.
        var cleanStretched = ToImage(clean, size).MtfStretchWith(origMin, balances);
        var noisyStretched = noisyImage.MtfStretchWith(origMin, balances);
        for (var c = 0; c < Channels; c++)
        {
            var channel = new[] { noisyStretched.GetChannelSpan(c).ToArray() };
            var own = StretchedNoise.Plane(channel, size, size, [stretches[c]], [estimated[c]], 1.0);
            var carried = StretchedNoise.Plane(channel, size, size, [stretches[c]], [estimated[0]], 1.0);
            var diff = new List<double>();
            var ownSky = new List<double>();
            var carriedSky = new List<double>();
            var ns = noisyStretched.GetChannelSpan(c);
            var cs = cleanStretched.GetChannelSpan(c);
            for (var y = 16; y < size - 16; y++)
            {
                for (var x = 16; x < size - 16; x++)
                {
                    var dx = x - (size / 2.0);
                    var dy = y - (size / 2.0);
                    if ((dx * dx) + (dy * dy) < 240.0 * 240.0)
                    {
                        continue;
                    }
                    var i = (y * size) + x;
                    diff.Add(ns[i] - cs[i]);
                    ownSky.Add(own[i] / StretchedNoise.PlaneScale);
                    carriedSky.Add(carried[i] / StretchedNoise.PlaneScale);
                }
            }
            var mean = diff.Average();
            var measured = Math.Sqrt(diff.Sum(v => (v - mean) * (v - mean)) / (diff.Count - 1));
            var ownRatio = ownSky.Average() / measured;
            var carriedRatio = carriedSky.Average() / measured;
            output.WriteLine($"channel {c} sky: measured {measured:E3}, own plane {ownRatio:F3}x, channel 0's carried {carriedRatio:F3}x");
            ownRatio.ShouldBe(1.0, 0.08, $"channel {c}");
            if (c == 1)
            {
                carriedRatio.ShouldBeGreaterThan(1.25, "green carried channel 0's calibration");
            }
        }
    }

    [Fact]
    public void AFlatSkyPlaneIsFlatAndInTheScalarsUnits()
    {
        // A frame of pure sky: the plane is one value, and PlaneScale makes it the trainer's scalar for pure noise.
        var sky = Enumerable.Range(0, Channels).Select(_ => Enumerable.Repeat(0.02f, Size * Size).ToArray()).ToArray();
        var calibration = new LinearDegradation.NoiseCalibration(0.0, 0.02, 0.004, 16);
        var rng = new Random(11);
        var noisy = sky.Select(p =>
        {
            float[] copy = [.. p];
            LinearDegradation.AddNoiseInPlace(copy, NoiseField.White(Size, Size, rng), calibration, 1.0);
            return copy;
        }).ToArray();
        var (origMin, balances) = ToImage(noisy).MtfStretchParameters(0.25);
        var stretched = ToImage(noisy).MtfStretchWith(origMin, balances);
        var stretches = Enumerable.Range(0, Channels).Select(c => new StretchedNoise.ChannelStretch(balances[c], origMin[c])).ToArray();
        var plane = StretchedNoise.Plane([.. Enumerable.Range(0, Channels).Select(c => Channel(stretched, c))], Size, Size, stretches, calibration, 1.0);

        var interior = new List<float>();
        for (var y = 16; y < Size - 16; y++)
        {
            for (var x = 16; x < Size - 16; x++)
            {
                interior.Add(plane[(y * Size) + x]);
            }
        }
        var spread = (double)(interior.Max() - interior.Min()) / interior.Average();
        output.WriteLine($"flat sky plane: mean {interior.Average():F4}, spread {spread:P2}");
        spread.ShouldBeLessThan(0.05);
    }
}
