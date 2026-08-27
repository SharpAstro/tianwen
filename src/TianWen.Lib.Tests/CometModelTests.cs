using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <see cref="CometModel"/>, the body's own light isolated so it can be subtracted from every
    /// frame of the star layer and added back once onto the composite. Every failure this guards
    /// against is silent: a model cut short leaves a band, a model half a pixel off subtracts a
    /// dipole, an amplitude biased by field stars leaves a ridge, and each of those integrates into a
    /// master that looks entirely plausible.
    /// </summary>
    public class CometModelTests
    {
        private static readonly DateTimeOffset Epoch = new(2025, 10, 18, 10, 15, 30, TimeSpan.Zero);

        /// <summary>SWAN's channel balance: a gas-rich comet, green three times red.</summary>
        private static readonly float[] Peaks = [0.10f, 0.30f, 0.18f];

        /// <summary>RGGB as the pipeline states it: channel index per photosite, <c>[y &amp; 1, x &amp; 1]</c>.</summary>
        private static readonly int[,] Rggb = { { 0, 1 }, { 1, 2 } };

        private const float ComaCoreRadiusPx = 15f;

        /// <summary>A coma with 1/r^2 wings: at r = 10 r0 it is still 1% of the peak, at 20 r0 0.25%.</summary>
        private static float Coma(float r) => 1f / (1f + (r / ComaCoreRadiusPx) * (r / ComaCoreRadiusPx));

        private static float Gaussian(Random rng)
        {
            var sum = 0.0;
            for (var i = 0; i < 12; i++) { sum += rng.NextDouble(); }
            return (float)(sum - 6.0);
        }

        private static ImageMeta Meta(SensorType sensor) => new()
        {
            Instrument = "synth",
            ExposureStartTime = Epoch,
            ExposureDuration = TimeSpan.FromSeconds(30),
            FrameType = FrameType.Light,
            SensorType = sensor,
        };

        /// <summary>A comet-aligned, starless master: sky plus the coma, per channel, plus noise.
        /// <paramref name="coreDeficit"/> scales the coma inside 8 px, standing in for the central
        /// condensation a star remover takes out of the plates the master was stacked from;
        /// <paramref name="gradient"/> tilts the sky, per pixel from the body, the way the field under a
        /// comet is tilted on the comet-aligned canvas.</summary>
        private static Image StarlessCometMaster(
            int size, Vector2 body, float sky, float noise, int seed, float coreDeficit = 1f, Vector2 gradient = default)
        {
            var rng = new Random(seed);
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                var plane = new float[size, size];
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var r = MathF.Sqrt((x - body.X) * (x - body.X) + (y - body.Y) * (y - body.Y));
                        var coma = Peaks[c] * Coma(r);
                        if (r < 8f) { coma *= coreDeficit; }
                        var field = sky + gradient.X * (x - body.X) + gradient.Y * (y - body.Y);
                        plane[y, x] = field + coma + noise * Gaussian(rng);
                    }
                }
                planes[c] = plane;
            }
            return new Image(planes, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: Meta(SensorType.Color));
        }

        /// <summary>
        /// A calibrated RGGB mosaic frame holding sky, the coma at <paramref name="amplitude"/> times the
        /// master's coma, noise, and field stars kept clear of the core.
        /// </summary>
        private static Image MosaicFrame(
            int width, int height, Vector2 bodyInFrame, float[] skyPerChannel, float amplitude,
            float noise, int stars, int seed, float starClearancePx = 80f, (Vector2 Offset, float Amp, float Sigma)? haloStar = null)
        {
            var rng = new Random(seed);
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var c = Rggb[y & 1, x & 1];
                    var r = MathF.Sqrt((x - bodyInFrame.X) * (x - bodyInFrame.X) + (y - bodyInFrame.Y) * (y - bodyInFrame.Y));
                    plane[y, x] = skyPerChannel[c] + amplitude * Peaks[c] * Coma(r) + noise * Gaussian(rng);
                }
            }
            void AddStar(float sx, float sy, float amp, float sigma)
            {
                var reach = (int)MathF.Ceiling(4f * sigma);
                for (var y = Math.Max(0, (int)sy - reach); y <= Math.Min(height - 1, (int)sy + reach); y++)
                {
                    for (var x = Math.Max(0, (int)sx - reach); x <= Math.Min(width - 1, (int)sx + reach); x++)
                    {
                        var d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                        plane[y, x] += amp * MathF.Exp(-d2 / (2f * sigma * sigma));
                    }
                }
            }
            for (var s = 0; s < stars; s++)
            {
                float sx, sy;
                do
                {
                    sx = (float)(rng.NextDouble() * (width - 20) + 10);
                    sy = (float)(rng.NextDouble() * (height - 20) + 10);
                }
                while (Vector2.Distance(new Vector2(sx, sy), bodyInFrame) < starClearancePx);
                AddStar(sx, sy, (float)(3000 + rng.NextDouble() * 20000), 1.5f);
            }
            if (haloStar is { } halo)
            {
                AddStar(bodyInFrame.X + halo.Offset.X, bodyInFrame.Y + halo.Offset.Y, halo.Amp, halo.Sigma);
            }
            return new Image([plane], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f, imageMeta: Meta(SensorType.RGGB));
        }

        private static Task<CometModel?> BuildAsync(Image master, Vector2 body) => CometModel.TryBuildAsync(
            master, alreadyStarless: true, body, trailDirection: new Vector2(-241.7f, -41.4f),
            remover: null, NullLogger.Instance, CancellationToken.None);

        [Fact]
        public async Task TheReachFollowsEachChannelsOwnProfileNotChannelZero()
        {
            var body = new Vector2(450.4f, 449.7f);
            var master = StarlessCometMaster(900, body, sky: 0.5f, noise: 0.005f, seed: 7);

            var model = await BuildAsync(master, body);

            model.ShouldNotBeNull();
            // Every channel holds a comet, and the reach is read per channel: the brightest one
            // runs well past the radius where it has fallen to 1% of its peak (150 px for this
            // profile), which is where a single relative floor on the faintest channel used to cut
            // the whole model off.
            model.ReachPerChannelPx.Length.ShouldBe(3);
            model.ReachPerChannelPx[1].ShouldBeGreaterThan(200f);
            model.ReachPerChannelPx[0].ShouldBeGreaterThan(80f);
            model.ReachPx.ShouldBe(Math.Max(model.ReachPerChannelPx[0], Math.Max(model.ReachPerChannelPx[1], model.ReachPerChannelPx[2])));
            // The core is above the sky by the peak itself: the pedestal came off.
            for (var c = 0; c < 3; c++)
            {
                model.ValueAt(c, Vector2.Zero).ShouldBe(Peaks[c], Peaks[c] * 0.08f);
            }
            // And well inside the wings the model still carries the coma rather than zero.
            model.ValueAt(1, new Vector2(120f, 0f)).ShouldBe(Peaks[1] * Coma(120f), Peaks[1] * 0.02f);
            model.FitRadiusPx.ShouldBeInRange(25f, model.ReachPx);
        }

        [Fact]
        public async Task TheFieldsGradientIsNotPartOfTheModel()
        {
            // SWAN's field: the sky under the comet rose by ~40e-4 over 400 px towards the upper right of
            // the crop, and the model kept that slope as a dipole out to its reach, cut hard there --
            // the composite's "halo". Here the slope is 1.2e-5 per px, about 4e-3 (1.3 noise) across
            // the crop, on a coma whose green wings are 1/r^2.
            var body = new Vector2(700.4f, 699.6f);
            var gradient = new Vector2(1.0e-5f, -0.6e-5f);
            const float Noise = 0.003f;
            var flat = await BuildAsync(StarlessCometMaster(1400, body, sky: 0.5f, noise: Noise, seed: 7), body);
            var sloped = await BuildAsync(StarlessCometMaster(1400, body, sky: 0.5f, noise: Noise, seed: 7, gradient: gradient), body);
            flat.ShouldNotBeNull();
            sloped.ShouldNotBeNull();

            for (var c = 0; c < 3; c++)
            {
                // The slope is recovered per channel, and a flat field does not invent one.
                sloped.BackgroundGradientPerChannel[c].X.ShouldBe(gradient.X, tolerance: 0.15f * MathF.Abs(gradient.X));
                sloped.BackgroundGradientPerChannel[c].Y.ShouldBe(gradient.Y, tolerance: 0.15f * MathF.Abs(gradient.Y));
                flat.BackgroundGradientPerChannel[c].Length().ShouldBeLessThan(0.15f * gradient.Length());
            }

            // Render the sloped model and read its outer 40 px in 15-degree sectors, per channel. With the
            // slope left in, the sectors along the gradient would sit at +-(slope x reach) ~ +-4e-3; a coma
            // is azimuthally symmetric, so every sector at the edge must be at the pedestal, which is zero.
            const int Size = 1400;
            var canvas = new float[3][,];
            for (var c = 0; c < 3; c++) { canvas[c] = new float[Size, Size]; }
            sloped.AddTo(canvas, body, [1f, 1f, 1f]).ShouldBeGreaterThan(0);
            for (var c = 0; c < 3; c++)
            {
                var reach = sloped.ReachPerChannelPx[c];
                reach.ShouldBeGreaterThan(100f, $"channel {c} should reach well past the core");
                var sectors = new List<float>[24];
                for (var s = 0; s < 24; s++) { sectors[s] = new List<float>(4096); }
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        var dx = x - body.X;
                        var dy = y - body.Y;
                        var r = MathF.Sqrt(dx * dx + dy * dy);
                        if (r < reach - 40f || r >= reach) { continue; }
                        var s = (int)((MathF.Atan2(dy, dx) + MathF.PI) / (2f * MathF.PI) * 24f) % 24;
                        sectors[s].Add(canvas[c][y, x]);
                    }
                }
                for (var s = 0; s < 24; s++)
                {
                    sectors[s].Sort();
                    var median = sectors[s][sectors[s].Count / 2];
                    // A quarter of the plate noise: the uncorrected dipole is ~1.3 noise at the edge.
                    MathF.Abs(median).ShouldBeLessThan(0.25f * Noise,
                        $"channel {c} sector {s * 15} deg at the edge of the model (r {reach - 40:F0}-{reach:F0}) reads {median:F5}: the field's slope is still in the model");
                }
            }
        }

        [Fact]
        public async Task AFlatPlateIsNotAComet()
        {
            var body = new Vector2(300f, 300f);
            var rng = new Random(3);
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                planes[c] = new float[600, 600];
                for (var y = 0; y < 600; y++)
                {
                    for (var x = 0; x < 600; x++)
                    {
                        planes[c][y, x] = 0.5f + 0.005f * Gaussian(rng);
                    }
                }
            }
            var flat = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, Meta(SensorType.Color));

            (await BuildAsync(flat, body)).ShouldBeNull();
        }

        [Fact]
        public void TheAsymptoteIsWhereTheProfileStopsFalling()
        {
            // Falls, flattens, then rises (a sky gradient): the reach ends at the minimum, and the
            // minimum is the pedestal. The later dip to 0.10 is never reached.
            float[] profile = [1.0f, 0.5f, 0.3f, 0.2f, 0.15f, 0.12f, 0.13f, 0.14f, 0.15f, 0.10f];
            var (reach, asymptote) = CometModel.FindAsymptote(profile, 10);
            reach.ShouldBe(60f);
            asymptote.ShouldBe(0.12f);

            // Monotone to the edge: the whole profile is coma.
            float[] monotone = [1.0f, 0.5f, 0.3f, 0.2f, 0.15f, 0.12f, 0.10f, 0.09f];
            CometModel.FindAsymptote(monotone, 10).ShouldBe((80f, 0.09f));
        }

        [Fact]
        public async Task TheAmplitudeIsRecoveredFromAFrameFullOfStarsAndTheBodySubtractsOut()
        {
            var body = new Vector2(450.4f, 449.7f);
            var master = StarlessCometMaster(900, body, sky: 0.5f, noise: 0.002f, seed: 11);
            var model = await BuildAsync(master, body);
            model.ShouldNotBeNull();

            // The frame is rotated and dithered against the reference grid; its star solution
            // undoes that. Composed with the drift it is the frame's transform onto the comet grid,
            // and the body's position on that grid is what the pipeline hands the model.
            var toCometGrid = Matrix3x2.CreateRotation(0.012f) * Matrix3x2.CreateTranslation(37.3f, -12.6f);
            var bodyInFrame = new Vector2(480.3f, 510.6f);
            var bodyOnGrid = Vector2.Transform(bodyInFrame, toCometGrid);
            const float amplitude = 2000f;
            float[] sky = [1000f, 1400f, 1200f];
            var frame = MosaicFrame(1000, 1000, bodyInFrame, sky, amplitude, noise: 3f, stars: 60, seed: 5);

            var fitted = model.FitScale(frame, toCometGrid, bodyOnGrid, Rggb);
            fitted.ShouldBe(amplitude, amplitude * 0.02f);

            var touched = model.SubtractFrom(frame, toCometGrid, bodyOnGrid, Rggb, fitted);
            touched.ShouldBeGreaterThan((int)(MathF.PI * 100f * 100f));

            // What is left within the core is sky plus noise: no ridge, no dipole. Read per CFA
            // colour, because the sky differs between them.
            var plane = frame.GetChannelArray(0);
            Span<double> sum = stackalloc double[3];
            Span<double> sumSq = stackalloc double[3];
            Span<int> n = stackalloc int[3];
            for (var y = 0; y < frame.Height; y++)
            {
                for (var x = 0; x < frame.Width; x++)
                {
                    if (Vector2.Distance(new Vector2(x, y), bodyInFrame) > 60f) { continue; }
                    var c = Rggb[y & 1, x & 1];
                    var d = plane[y, x] - sky[c];
                    sum[c] += d;
                    sumSq[c] += d * d;
                    n[c]++;
                }
            }
            for (var c = 0; c < 3; c++)
            {
                var mean = sum[c] / n[c];
                var rms = Math.Sqrt(sumSq[c] / n[c]);
                // Bias under 1% of the core's own amplitude; scatter no worse than the frame's noise
                // plus the model's own (0.002 x 2000 = 4 ADU in the verbatim core).
                Math.Abs(mean).ShouldBeLessThan(0.01 * amplitude * Peaks[c]);
                rms.ShouldBeLessThan(12.0);
            }
        }

        [Fact]
        public async Task AMissingNucleusAndABrightNeighbourDoNotInflateTheAmplitude()
        {
            // The 10P case. The star remover took the central condensation out of the plates, so the
            // model is 30% short inside 8 px while every frame has the full core; and a bright star
            // with a broad halo sits 40 px from the body, inside any annulus the fit could use. A
            // least-squares fit over the core answered 15% high on the real data and dug a bowl round
            // the track; the annular median must not.
            var body = new Vector2(450.4f, 449.7f);
            var master = StarlessCometMaster(900, body, sky: 0.5f, noise: 0.002f, seed: 17, coreDeficit: 0.7f);
            var model = await BuildAsync(master, body);
            model.ShouldNotBeNull();

            var toCometGrid = Matrix3x2.CreateTranslation(-21.3f, 8.6f);
            var bodyInFrame = new Vector2(500.3f, 470.6f);
            var bodyOnGrid = Vector2.Transform(bodyInFrame, toCometGrid);
            const float amplitude = 3500f;
            float[] sky = [1000f, 1400f, 1200f];
            var frame = MosaicFrame(1000, 1000, bodyInFrame, sky, amplitude, noise: 3f, stars: 40, seed: 9,
                haloStar: (new Vector2(28f, 29f), 12000f, 6f));

            var fitted = model.FitScale(frame, toCometGrid, bodyOnGrid, Rggb);
            fitted.ShouldBe(amplitude, amplitude * 0.03f);
        }

        [Fact]
        public async Task TheNucleusIsRestoredFromTheRawCoreInTheModelsOwnUnits()
        {
            var body = new Vector2(450.4f, 449.7f);
            var master = StarlessCometMaster(900, body, sky: 0.5f, noise: 0.002f, seed: 21, coreDeficit: 0.7f);
            var model = await BuildAsync(master, body);
            model.ShouldNotBeNull();
            // Short by 30% at the centre, as a starless plate is.
            model.ValueAt(1, Vector2.Zero).ShouldBeLessThan(Peaks[1] * 0.8f);

            // The raw core stack: the FULL coma in frame units (a gain of 3500 and a sky offset), noisy.
            const int radius = 40;
            const float gain = 3500f;
            float[] sky = [1000f, 1400f, 1200f];
            var rng = new Random(23);
            var rawCore = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                rawCore[c] = new float[2 * radius + 1, 2 * radius + 1];
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var r = MathF.Sqrt(dx * dx + dy * dy);
                        rawCore[c][radius + dy, radius + dx] = sky[c] + gain * Peaks[c] * Coma(r) + 2f * Gaussian(rng);
                    }
                }
            }

            var fits = model.SpliceCore(rawCore, innerPx: 12f, featherPx: 6f, NullLogger.Instance);

            for (var c = 0; c < 3; c++)
            {
                fits[c].ShouldNotBeNull();
                fits[c]!.Value.Gain.ShouldBe(gain, gain * 0.03f);
                fits[c]!.Value.Offset.ShouldBe(sky[c], 30f);
                // The centre is back to the full coma, and the wings were not touched.
                model.ValueAt(c, Vector2.Zero).ShouldBe(Peaks[c], Peaks[c] * 0.05f);
                model.ValueAt(c, new Vector2(40f, 0f)).ShouldBe(Peaks[c] * Coma(40f), Peaks[c] * 0.03f);
            }
            model.CoreRadiusPx.ShouldBe(12f);

            // A frame whose nucleus is 40% brighter than the session's median (better seeing) while
            // its coma is at the fitted amplitude: the core reads its own scale, and the subtraction
            // uses it inside the core and the coma's outside.
            var toCometGrid = Matrix3x2.CreateTranslation(15.5f, -7.25f);
            var bodyInFrame = new Vector2(480.3f, 510.6f);
            var bodyOnGrid = Vector2.Transform(bodyInFrame, toCometGrid);
            var frame = MosaicFrame(1000, 1000, bodyInFrame, sky, gain, noise: 3f, stars: 0, seed: 27);
            var plane = frame.GetChannelArray(0);
            for (var y = 0; y < frame.Height; y++)
            {
                for (var x = 0; x < frame.Width; x++)
                {
                    var r = Vector2.Distance(new Vector2(x, y), bodyInFrame);
                    if (r < 12f)
                    {
                        var c = Rggb[y & 1, x & 1];
                        plane[y, x] += 0.4f * gain * Peaks[c] * Coma(r);
                    }
                }
            }
            var comaScale = model.FitScale(frame, toCometGrid, bodyOnGrid, Rggb);
            comaScale.ShouldBe(gain, gain * 0.03f);
            var coreScale = model.FitCoreScale(frame, toCometGrid, bodyOnGrid, Rggb, comaScale);
            coreScale.ShouldBe(gain * 1.4f, gain * 0.08f);

            model.SubtractFrom(frame, toCometGrid, bodyOnGrid, Rggb, comaScale, coreScale);
            double sum = 0; var n = 0;
            for (var y = (int)bodyInFrame.Y - 6; y <= (int)bodyInFrame.Y + 6; y++)
            {
                for (var x = (int)bodyInFrame.X - 6; x <= (int)bodyInFrame.X + 6; x++)
                {
                    if (Vector2.Distance(new Vector2(x, y), bodyInFrame) > 6f) { continue; }
                    sum += plane[y, x] - sky[Rggb[y & 1, x & 1]];
                    n++;
                }
            }
            // The brightened nucleus came out along with the coma: under 3% of the core's excess left.
            Math.Abs(sum / n).ShouldBeLessThan(0.03 * 1.4 * gain * Peaks[1]);
        }

        [Fact]
        public async Task EachChannelGetsItsOwnAmplitude()
        {
            // The comet layer normalised each channel to its own sky, so the model's channels are in
            // different units: a frame whose comet is red 1500, green 2000, blue 2500 in the model's
            // per-channel units must come out with three amplitudes, not one. Pooled, SWAN's red was
            // over-subtracted by a third and its blue under-subtracted by a fifth.
            var body = new Vector2(450.4f, 449.7f);
            var master = StarlessCometMaster(900, body, sky: 0.5f, noise: 0.002f, seed: 41);
            var model = await BuildAsync(master, body);
            model.ShouldNotBeNull();

            var toCometGrid = Matrix3x2.CreateTranslation(-9.5f, 4.25f);
            var bodyInFrame = new Vector2(500.3f, 470.6f);
            var bodyOnGrid = Vector2.Transform(bodyInFrame, toCometGrid);
            float[] sky = [1000f, 1400f, 1200f];
            float[] perChannel = [1500f, 2000f, 2500f];
            var frame = MosaicFrame(1000, 1000, bodyInFrame, sky, amplitude: 1f, noise: 3f, stars: 30, seed: 43);
            var plane = frame.GetChannelArray(0);
            for (var y = 0; y < frame.Height; y++)
            {
                for (var x = 0; x < frame.Width; x++)
                {
                    var c = Rggb[y & 1, x & 1];
                    var r = Vector2.Distance(new Vector2(x, y), bodyInFrame);
                    // MosaicFrame put the coma in at amplitude 1; restate it at this channel's amplitude.
                    plane[y, x] += (perChannel[c] - 1f) * Peaks[c] * Coma(r);
                }
            }

            var scales = new float[3];
            model.FitScales(frame, toCometGrid, bodyOnGrid, Rggb, scales).ShouldBeTrue();
            for (var c = 0; c < 3; c++)
            {
                scales[c].ShouldBe(perChannel[c], perChannel[c] * 0.03f);
            }

            model.SubtractFrom(frame, toCometGrid, bodyOnGrid, Rggb, scales, scales);
            Span<double> sum = stackalloc double[3];
            Span<int> n = stackalloc int[3];
            for (var y = 0; y < frame.Height; y++)
            {
                for (var x = 0; x < frame.Width; x++)
                {
                    if (Vector2.Distance(new Vector2(x, y), bodyInFrame) > 50f) { continue; }
                    var c = Rggb[y & 1, x & 1];
                    sum[c] += plane[y, x] - sky[c];
                    n[c]++;
                }
            }
            for (var c = 0; c < 3; c++)
            {
                // Under 1% of that channel's own core left, in every colour.
                Math.Abs(sum[c] / n[c]).ShouldBeLessThan(0.01 * perChannel[c] * Peaks[c]);
            }
        }

        [Fact]
        public async Task TheModelIsPlacedSubPixelWhenAddedBack()
        {
            var body = new Vector2(450.4f, 449.7f);
            var master = StarlessCometMaster(900, body, sky: 0.5f, noise: 0.001f, seed: 13);
            var model = await BuildAsync(master, body);
            model.ShouldNotBeNull();

            var canvas = new float[3][,];
            for (var c = 0; c < 3; c++) { canvas[c] = new float[1000, 1200]; }
            var target = new Vector2(700.35f, 380.8f);
            float[] gains = [1f, 1f, 1f];
            var touched = model.AddTo(canvas, target, gains);
            touched.ShouldBeGreaterThan(0);

            // Core-weighted centroid of what was added, in the green channel: lands on the target
            // to well under a pixel, which a whole-pixel crop centre could not manage.
            double wx = 0, wy = 0, w = 0;
            for (var y = 350; y < 412; y++)
            {
                for (var x = 670; x < 732; x++)
                {
                    var v = canvas[1][y, x];
                    if (v <= 0f) { continue; }
                    wx += x * (double)v * v;
                    wy += y * (double)v * v;
                    w += (double)v * v;
                }
            }
            (wx / w).ShouldBe(target.X, 0.15);
            (wy / w).ShouldBe(target.Y, 0.15);
            canvas[1][381, 700].ShouldBe(Peaks[1], Peaks[1] * 0.1f);

            // A NaN canvas pixel (outside coverage) stays NaN rather than becoming the model.
            canvas[0][380, 700] = float.NaN;
            model.AddTo(canvas, target, gains);
            float.IsNaN(canvas[0][380, 700]).ShouldBeTrue();
        }

        [Fact]
        public async Task ABodyTooCloseToTheEdgeIsRefused()
        {
            var body = new Vector2(20f, 300f);
            var master = StarlessCometMaster(600, body, sky: 0.5f, noise: 0.002f, seed: 1);
            (await BuildAsync(master, body)).ShouldBeNull();
        }
    }
}
