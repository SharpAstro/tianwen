using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// E16c step 2 (docs/plans/denoiser-training.md): a master's standard-error plane is the scatter of the samples the
/// combine kept, over their count, so it has to predict the master's OWN error, the thing the half pairs measure and every
/// single-frame estimate of it got wrong on structured fields. Frames with a known truth and known noise, a different
/// noise per channel, margins only some frames reach, and outliers a clip removes.
/// </summary>
[Collection("Imaging")]
public class StandardErrorPlaneTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private const int Frames = 40;
    private const float Truth = 0.2f;

    [Fact]
    public void ItIsEveryFiniteSamplesRobustSpreadOverTheKeptCount()
    {
        float[] column = [1f, 2f, 4f, 7f, float.NaN, 100f];
        float[] mask = [1f, 1f, 1f, 1f, 1f, 0f];
        // Finite: 1, 2, 4, 7, 100, median 4, deviations 3, 2, 0, 3, 96, MAD 3: the outlier the clip removed cannot
        // inflate it. Kept and finite: four. SE = 1.4826 * 3 / sqrt 4, times the bias correction for five samples.
        StandardErrorPlane.Of(column, mask).ShouldBe((float)(1.4826 * StandardErrorPlane.MadBiasCorrection(5) * 3 / 2), 1e-5f);

        StandardErrorPlane.Of([5f, float.NaN, 9f], [1f, 1f, 0f]).ShouldBe(float.NaN, "one kept sample is no measurement");
        StandardErrorPlane.Of([3f, 3f, 3f], [1f, 1f, 1f]).ShouldBe(0f);
    }

    /// <summary>
    /// The correction makes the spread unbiased in the MEAN over Gaussian columns of every length a column can have, which
    /// is what a blurred plane needs; uncorrected, four samples read 0.735 of the truth and sixteen 0.949.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(40)]
    public void TheSpreadIsUnbiasedInTheMeanForAnyCount(int n)
    {
        const int Columns = 200_000;
        var rng = new Random(29 + n);
        var column = new float[n];
        var mask = new float[n];
        Array.Fill(mask, 1f);
        var sum = 0.0;
        for (var i = 0; i < Columns; i++)
        {
            for (var k = 0; k < n; k++)
            {
                column[k] = (float)Gaussian(rng);
            }
            // The standard error over the root of n back to one sample's spread, whose truth is 1.
            sum += StandardErrorPlane.Of(column, mask) * Math.Sqrt(n);
        }
        var mean = sum / Columns;
        output.WriteLine($"n {n}: mean spread {mean:F4}");
        mean.ShouldBe(1.0, 0.01);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ThePlanePredictsTheMastersOwnError(bool streaming, bool clipped)
    {
        var sigmas = new[] { 0.01, 0.02, 0.005 };
        var rng = new Random(13);
        var images = new List<Image>(Frames);
        for (var f = 0; f < Frames; f++)
        {
            var planes = new float[sigmas.Length][,];
            for (var c = 0; c < sigmas.Length; c++)
            {
                var plane = new float[Size, Size];
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        // Columns 0-3 are reached by one frame only, as a dither margin on a union canvas.
                        if (x < 4 && f > 0)
                        {
                            plane[y, x] = float.NaN;
                            continue;
                        }
                        var v = Truth + (sigmas[c] * Gaussian(rng));
                        // A clipped run carries hot outliers in 3 percent of samples, which the clip must remove.
                        if (clipped && rng.NextDouble() < 0.03)
                        {
                            v += 1.0;
                        }
                        plane[y, x] = (float)v;
                    }
                }
                planes[c] = plane;
            }
            images.Add(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()));
        }
        var options = new IntegrationOptions(Rejector: clipped ? new SigmaClipRejector() : null, ApplyNormalization: false);

        IntegrationResult result;
        var staged = streaming
            ? images.Select(i => new StagedAlignedFrame(StreamingFrameReader.InMemoryOnly(i), i.ImageMeta, i.MaxValue, i.Pedestal, null, null)).ToList()
            : null;
        try
        {
            result = staged is { } s ? StreamingIntegrator.Integrate(s, options) : Integrator.Integrate(images, options);
        }
        finally
        {
            foreach (var f in staged ?? [])
            {
                f.Dispose();
            }
        }

        output.WriteLine($"streaming {streaming}, clipped {clipped}: rejection rate {result.MeanRejectionRate:P2}");
        var standardError = result.StandardError.ShouldNotBeNull("both integrators measure it");
        standardError.ChannelCount.ShouldBe(sigmas.Length, "one plane per channel, never averaged like coverage");
        for (var c = 0; c < sigmas.Length; c++)
        {
            var z = new List<double>();
            double errorSquares = 0, planeSquares = 0;
            for (var y = 0; y < Size; y++)
            {
                standardError[c, y, 2].ShouldBe(float.NaN, "a pixel one frame reached has no scatter to measure");
                for (var x = 4; x < Size; x++)
                {
                    var se = standardError[c, y, x];
                    var error = result.Master[c, y, x] - Truth;
                    z.Add(error / se);
                    errorSquares += error * error;
                    planeSquares += se * se;
                }
            }
            // What the plane is used for: the noise over a region (it is low-passed before any model reads it), so the
            // master's error variance over the plane's, over the frame, is the measure; the per-pixel quotient's robust
            // spread is reported beside it (its RMS inflates with any per-pixel reading noise, t-like).
            var varianceRatio = Math.Sqrt(errorSquares / planeSquares);
            var sorted = z.Select(Math.Abs).OrderBy(v => v).ToArray();
            var robust = 1.4826 * sorted[sorted.Length / 2];
            output.WriteLine($"streaming {streaming}, clipped {clipped}, channel {c}: the master's error over the plane, RMS over RMS " +
                             $"{varianceRatio:F3}; per pixel robust {robust:F3}, mean {z.Average():F3}, max |z| {sorted[^1]:F1}");
            // Measured 2026-10-05: RMS over RMS 0.974 to 1.003 in every case (0.993 to 1.024 before the MAD's finite-sample
            // correction: a plane unbiased in the mean has an RMS a little above the truth); per pixel 0.95 to 1.05, a MAD of
            // 40 frames reading one pixel's noise to about 18 percent.
            varianceRatio.ShouldBe(1.0, 0.04, $"channel {c}");
            robust.ShouldBe(1.0, 0.10, $"channel {c}");
        }
    }

    /// <summary>
    /// What a real night adds and the plane must not read as noise (E16c S4's pilot): each frame's sky offset, a gradient
    /// that turns, and a transparency that scales an extended nebula, at the sizes the pilot's nights measured (the sky
    /// drifting two of one frame's noise). Unnormalised, as the bake integrates. The plane is judged against the master's
    /// own NOISE, its error less the frames' mean drift, which the fixture knows exactly: read about each frame's drift
    /// (<see cref="FrameDrift"/>) it predicts it, read raw it over-reads.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ThePlaneIsReadAboutEachFramesDrift(bool measureDrift, bool clipped)
    {
        const int size = 128;
        const int frames = 40;
        const double sigma = 0.01;
        const double nebula = 0.5;
        var rng = new Random(41);
        var meanDrift = new double[size, size];
        var images = new List<Image>(frames);
        for (var f = 0; f < frames; f++)
        {
            var offset = 2 * sigma * Gaussian(rng);
            var gradient = sigma * Gaussian(rng);
            var transparency = 0.03 * Gaussian(rng);
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - (size / 2.0);
                    var dy = y - (size / 2.0);
                    var blob = nebula * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * 30.0 * 30.0));
                    var drift = offset + (gradient * ((x / (double)size) - 0.5)) + (blob * transparency);
                    meanDrift[y, x] += drift / frames;
                    plane[y, x] = (float)(Truth + blob + drift + (sigma * Gaussian(rng)));
                }
            }
            images.Add(new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()));
        }

        var staged = images.Select(i => new StagedAlignedFrame(StreamingFrameReader.InMemoryOnly(i), i.ImageMeta, i.MaxValue, i.Pedestal, null, null)
        {
            DriftBlocks = measureDrift ? FrameDrift.MeasureBlocks(i) : null,
        }).ToList();
        IntegrationResult result;
        try
        {
            result = StreamingIntegrator.Integrate(staged, new IntegrationOptions(Rejector: clipped ? new SigmaClipRejector() : null, ApplyNormalization: false));
        }
        finally
        {
            foreach (var f in staged)
            {
                f.Dispose();
            }
        }

        var standardError = result.StandardError.ShouldNotBeNull();
        double noiseSquares = 0, planeSquares = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - (size / 2.0);
                var dy = y - (size / 2.0);
                var blob = nebula * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * 30.0 * 30.0));
                var noise = result.Master[0, y, x] - Truth - blob - meanDrift[y, x];
                var se = standardError[0, y, x];
                noiseSquares += noise * noise;
                planeSquares += se * se;
            }
        }
        var ratio = Math.Sqrt(noiseSquares / planeSquares);
        output.WriteLine($"drift measured {measureDrift}, clipped {clipped}: the master's noise over the plane, RMS over RMS {ratio:F3}; " +
                         $"rejection {result.MeanRejectionRate:P2}");
        if (measureDrift)
        {
            ratio.ShouldBe(1.0, 0.05);
        }
        else
        {
            ratio.ShouldBeLessThan(0.7, "read raw, the scatter holds the drift the halves cancel");
        }
    }

    /// <summary>
    /// The drizzle's plane (<see cref="DrizzleScatter"/>), on the bake's terms: a flat RGGB sky of known noise, 60
    /// dithered frames, the stack's own rejector and no normalisation, so the master is the sky over the source's full
    /// scale. Each channel's error over the covered interior, RMS over the plane's RMS.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task TheDrizzlePlanePredictsTheMastersOwnError()
    {
        const int frameSize = 64;
        const int margin = 6;
        const int canvas = frameSize + (2 * margin);
        const int count = 60;
        const float sky = 1000f;
        const float sigma = 8f;
        var frames = new List<RawBayerFrame>(count);
        for (var f = 0; f < count; f++)
        {
            var rng = new Random(77 + f);
            var (dx, dy) = ((float)((rng.NextDouble() * 6) - 3), (float)((rng.NextDouble() * 6) - 3));
            var plane = new float[frameSize, frameSize];
            for (var y = 0; y < frameSize; y++)
            {
                for (var x = 0; x < frameSize; x++)
                {
                    plane[y, x] = sky + (sigma * (float)Gaussian(rng));
                }
            }
            var meta = new ImageMeta { Instrument = "synth-drizzle-stderr", SensorType = SensorType.RGGB };
            var image = new Image([plane], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f, imageMeta: meta);
            frames.Add(new RawBayerFrame(image, System.Numerics.Matrix3x2.CreateTranslation(dx + margin, dy + margin)));
        }
        var options = new IntegrationOptions(Rejector: StackingPipeline.BuildRejector(count), ApplyNormalization: false);

        var result = await new DrizzleStrategy().RunAsync(
            DrizzleOutlierRejectionTests.BuildJob(frames, options, canvas, canvas), TestContext.Current.CancellationToken);

        var standardError = result.StandardError.ShouldNotBeNull("a rejecting drizzle measures it");
        const float truth = sky / 65535f;
        for (var c = 0; c < 3; c++)
        {
            double errorSquares = 0, planeSquares = 0;
            var n = 0;
            for (var y = margin + 4; y < canvas - margin - 4; y++)
            {
                for (var x = margin + 4; x < canvas - margin - 4; x++)
                {
                    var se = standardError[c, y, x];
                    var master = result.Master[c, y, x];
                    if (!float.IsFinite(se) || !float.IsFinite(master))
                    {
                        continue;
                    }
                    var error = master - truth;
                    errorSquares += error * error;
                    planeSquares += se * se;
                    n++;
                }
            }
            var ratio = Math.Sqrt(errorSquares / planeSquares);
            output.WriteLine($"drizzle channel {c}: {n} cells, the master's error over the plane, RMS over RMS {ratio:F3}");
            n.ShouldBeGreaterThan(1000);
            ratio.ShouldBe(1.0, 0.05, $"channel {c}");
        }
    }

    /// <summary>The two-pass strategy's plane: the kept samples' scatter from pass B against pass A's mean, over the
    /// kept count, on frames of known noise per channel.</summary>
    [Fact]
    public async System.Threading.Tasks.Task TheTwoPassPlanePredictsTheMastersOwnError()
    {
        var sigmas = new[] { 0.01, 0.02, 0.005 };
        var rng = new Random(29);
        var images = new List<Image>(Frames);
        for (var f = 0; f < Frames; f++)
        {
            var planes = new float[sigmas.Length][,];
            for (var c = 0; c < sigmas.Length; c++)
            {
                var plane = new float[Size, Size];
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        plane[y, x] = (float)(Truth + (sigmas[c] * Gaussian(rng)));
                    }
                }
                planes[c] = plane;
            }
            images.Add(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()));
        }
        var job = new IntegrationJob(
            WarpedFrames: _ => Enumerate(images),
            ExpectedFrameCount: images.Count,
            Options: new IntegrationOptions(Rejector: new SigmaClipRejector(), ApplyNormalization: false),
            StagingDir: System.IO.Path.GetTempPath(),
            StatsRect: TianWen.Lib.Geometry.PixelRect.Empty);

        var result = await new ChunkedTwoPassStrategy().RunAsync(job, TestContext.Current.CancellationToken);

        var standardError = result.StandardError.ShouldNotBeNull();
        for (var c = 0; c < sigmas.Length; c++)
        {
            double errorSquares = 0, planeSquares = 0;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var error = result.Master[c, y, x] - Truth;
                    errorSquares += error * error;
                    planeSquares += standardError[c, y, x] * standardError[c, y, x];
                }
            }
            var ratio = Math.Sqrt(errorSquares / planeSquares);
            output.WriteLine($"two-pass channel {c}: the master's error over the plane, RMS over RMS {ratio:F3}");
            ratio.ShouldBe(1.0, 0.04, $"channel {c}");
        }

        static async IAsyncEnumerable<Image> Enumerate(List<Image> frames)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            foreach (var f in frames)
            {
                yield return f;
            }
        }
    }

    [Fact]
    public void TheSidecarKeepsEveryLevelAndSaysWhereNothingWasMeasured()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stderr-sidecar-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var masterPath = System.IO.Path.Combine(dir, "master_test.fits");
            // A sky-level error beside a star core's 10^5 times larger, an exact zero, and an absent pixel, in two
            // channels: a linear span to the peak would round the sky to nothing, and read the absent pixel as 0.
            var planes = new float[2][,];
            for (var c = 0; c < 2; c++)
            {
                var plane = new float[4, 4];
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        plane[y, x] = (float)(3.7e-5 * (c + 1) * (1 + (y * 4) + x));
                    }
                }
                plane[0, 0] = float.NaN;
                plane[0, 1] = 0f;
                plane[3, 3] = 4.2f;
                planes[c] = plane;
            }
            var standardError = new Image(planes, BitDepth.Float32, 4.2f, 0f, 0f, new ImageMeta());

            IntegrationFitsWriter.WriteStandardErrorMap(masterPath, standardError, frameCount: 40);
            var written = IntegrationFitsWriter.CompressedPathFor(IntegrationFitsWriter.StandardErrorPathFor(masterPath));
            System.IO.File.Exists(written).ShouldBeTrue();
            IntegrationFitsWriter.IsMapSidecarPath(written).ShouldBeTrue("a folder walk must not count it as a master");

            IntegrationFitsWriter.TryReadStandardErrorMap(masterPath, out var read).ShouldBeTrue();
            read.ChannelCount.ShouldBe(2);
            for (var c = 0; c < 2; c++)
            {
                read[c, 0, 0].ShouldBe(float.NaN, "absent stays absent");
                read[c, 0, 1].ShouldBeLessThan(1e-13f, "an exact zero reads back as zero, not as absent");
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        if (y == 0 && x < 2)
                        {
                            continue;
                        }
                        var expected = planes[c][y, x];
                        (read[c, y, x] / expected).ShouldBe(1f, 0.001f, $"channel {c} ({x}, {y})");
                    }
                }
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static double Gaussian(Random rng)
        => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
}
