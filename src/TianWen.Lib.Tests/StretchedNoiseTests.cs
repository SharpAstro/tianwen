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
        return [.. Enumerable.Range(0, Channels).Select(_ => (float[])plane.Clone())];
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
            noisy[c] = (float[])clean[c].Clone();
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
            noisy[c] = (float[])p.Clone();
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
            var copy = (float[])p.Clone();
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
