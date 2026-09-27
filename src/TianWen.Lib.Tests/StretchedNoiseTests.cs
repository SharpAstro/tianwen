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

    private static Image ToImage(float[][] channels)
    {
        var data = new float[channels.Length][,];
        for (var c = 0; c < channels.Length; c++)
        {
            var p = new float[Size, Size];
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
