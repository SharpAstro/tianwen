using System;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Degradation
{
    /// <summary>
    /// Unit-variance noise FIELDS of a chosen spatial shape, and the band decomposition that measures
    /// the shape back. Level and shape are separated on purpose: this type answers "what does the
    /// noise look like", <see cref="LinearDegradation"/> answers "how much of it is there at this
    /// pixel", and only the second one is signal-dependent.
    ///
    /// <para><b>Why shape is a first-class question</b> (docs/plans/denoiser-training.md H2): measured
    /// scene-free, every sub-derived regime shares one band1/band0 power ratio near 0.60 while a
    /// half-master reads 0.32, so a master's noise is a different distribution from a sub's and not
    /// merely a quieter one. White noise dropped onto a master therefore has the wrong shape for the
    /// input the denoiser is deployed on, and an injector that cannot move that ratio is not testing
    /// H2 at all. Both shapes are here so the arms differ in exactly one thing.</para>
    /// </summary>
    public static class NoiseField
    {
        /// <summary>The difference-of-Gaussians bands the conditioning plane and the shape metric share
        /// (<c>COND_BAND_SIGMAS</c> in the trainer): 0 to 1 px, 1 to 2 px, 2 to 4 px. A sigma of 0 means
        /// the image itself, so band 0 is <c>img - blur(1)</c>.</summary>
        public static ReadOnlySpan<double> BandSigmas => [0.0, 1.0, 2.0, 4.0];

        /// <summary>
        /// Uncorrelated Gaussian noise, mean 0 and variance 1. The S-white arm.
        /// </summary>
        public static float[] White(int width, int height, Random rng)
        {
            var field = new float[width * height];
            for (var i = 0; i < field.Length; i++)
            {
                field[i] = (float)NextGaussian(rng);
            }
            return Normalise(field);
        }

        /// <summary>
        /// Noise with the correlation a stack of registered subs has: <paramref name="realisations"/>
        /// independent white fields, each bilinearly resampled by its own random sub-pixel offset (the
        /// registrar's warp, which is what correlates neighbouring pixels), averaged, then renormalised
        /// to unit variance. The S-warped arm.
        /// </summary>
        /// <remarks>
        /// Renormalising at the end is what makes this a SHAPE generator rather than a depth model:
        /// averaging N fields divides the variance by about N, and that factor is the level, which the
        /// caller sets from the electron model instead. What survives the renormalisation is the
        /// correlation, which is the thing under test.
        /// <para>The averaged correlation converges quickly in N because each realisation contributes
        /// the same bilinear kernel at a different phase; a handful of realisations already samples the
        /// phase distribution, so a session's true sub count is not needed for the shape (it is still
        /// the honest value to pass).</para>
        /// </remarks>
        /// <param name="resampleSigma">Extra smoothing applied to each realisation before it is
        /// accumulated, in pixels, standing in for a resampling kernel wider than plain bilinear (a
        /// drizzle kernel at pixfrac &lt; 1 is narrower, a Lanczos or a debayer wider). Zero is bilinear
        /// alone. This is the knob that CALIBRATES the arm: bilinear alone does not reach the shape a
        /// real frame has, and the honest way to set it is to measure the ratio and match, not to pick a
        /// number that sounds like a resampler.</param>
        public static float[] Warped(int width, int height, int realisations, Random rng, double resampleSigma = 0.0)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(realisations);
            ArgumentOutOfRangeException.ThrowIfNegative(resampleSigma);
            var acc = new float[width * height];
            var one = new float[width * height];
            for (var r = 0; r < realisations; r++)
            {
                for (var i = 0; i < one.Length; i++)
                {
                    one[i] = (float)NextGaussian(rng);
                }
                var dx = rng.NextDouble() - 0.5;
                var dy = rng.NextDouble() - 0.5;
                var field = resampleSigma > 0 ? Blur(one, width, height, resampleSigma) : one;
                ShiftBilinearInto(field, width, height, dx, dy, acc);
            }
            return Normalise(acc);
        }

        /// <summary>
        /// Per-band robust sigma (1.4826 x MAD) of a field, over the bands in <see cref="BandSigmas"/>.
        /// </summary>
        /// <remarks>
        /// This is the scene-free form: it assumes <paramref name="field"/> holds noise and nothing
        /// else, which is true of a generated field and of the DIFFERENCE of two frames of one scene.
        /// It is deliberately NOT the trainer's single-image estimator (which masks to the faintest
        /// quarter to keep nebulosity out of the bands), because when the scene is known to be absent
        /// that masking only throws away samples.
        /// </remarks>
        public static double[] BandSigmasOf(ReadOnlySpan<float> field, int width, int height)
        {
            var bands = BandSigmas;
            var blurred = new float[bands.Length][];
            for (var b = 0; b < bands.Length; b++)
            {
                blurred[b] = bands[b] <= 0.0 ? field.ToArray() : Blur(field, width, height, bands[b]);
            }

            var result = new double[bands.Length - 1];
            var scratch = new float[field.Length];
            for (var b = 0; b < result.Length; b++)
            {
                var lo = blurred[b];
                var hi = blurred[b + 1];
                for (var i = 0; i < scratch.Length; i++)
                {
                    scratch[i] = lo[i] - hi[i];
                }
                result[b] = StatisticsHelper.MedianAndMad(scratch.AsSpan()).Mad * 1.4826;
            }
            return result;
        }

        /// <summary>
        /// The one number H2 turns on: band1 over band0 of <paramref name="field"/>. Real sub-derived
        /// regimes read about 0.60 and a real half-master 0.32.
        /// </summary>
        public static double BandRatio(ReadOnlySpan<float> field, int width, int height)
        {
            var sigmas = BandSigmasOf(field, width, height);
            return sigmas[0] > 0 ? sigmas[1] / sigmas[0] : double.NaN;
        }

        /// <summary>Standard normal deviate (Box-Muller), matching the fake camera's generator.</summary>
        internal static double NextGaussian(Random rng)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static float[] Normalise(float[] field)
        {
            var mean = 0.0;
            for (var i = 0; i < field.Length; i++)
            {
                mean += field[i];
            }
            mean /= field.Length;

            var sumSq = 0.0;
            for (var i = 0; i < field.Length; i++)
            {
                var d = field[i] - mean;
                sumSq += d * d;
            }
            var sd = Math.Sqrt(sumSq / field.Length);
            var inv = sd > 0 ? 1.0 / sd : 1.0;
            for (var i = 0; i < field.Length; i++)
            {
                field[i] = (float)((field[i] - mean) * inv);
            }
            return field;
        }

        /// <summary>Adds <paramref name="src"/>, shifted by a sub-pixel offset with bilinear weights and
        /// edge clamping, into <paramref name="acc"/>.</summary>
        private static void ShiftBilinearInto(ReadOnlySpan<float> src, int width, int height, double dx, double dy, Span<float> acc)
        {
            var fx = (float)(dx - Math.Floor(dx));
            var fy = (float)(dy - Math.Floor(dy));
            var ix = (int)Math.Floor(dx);
            var iy = (int)Math.Floor(dy);
            var w00 = (1 - fx) * (1 - fy);
            var w10 = fx * (1 - fy);
            var w01 = (1 - fx) * fy;
            var w11 = fx * fy;

            for (var y = 0; y < height; y++)
            {
                var sy0 = Math.Clamp(y + iy, 0, height - 1);
                var sy1 = Math.Clamp(y + iy + 1, 0, height - 1);
                for (var x = 0; x < width; x++)
                {
                    var sx0 = Math.Clamp(x + ix, 0, width - 1);
                    var sx1 = Math.Clamp(x + ix + 1, 0, width - 1);
                    acc[(y * width) + x] +=
                        (src[(sy0 * width) + sx0] * w00) + (src[(sy0 * width) + sx1] * w10)
                        + (src[(sy1 * width) + sx0] * w01) + (src[(sy1 * width) + sx1] * w11);
                }
            }
        }

        /// <summary>Separable Gaussian blur with edge clamping; the band decomposition's low-pass.</summary>
        private static float[] Blur(ReadOnlySpan<float> src, int width, int height, double sigma)
        {
            var radius = Math.Max(1, (int)Math.Ceiling(3.0 * sigma));
            var size = (2 * radius) + 1;
            var kernel = new double[size];
            var twoSigma2 = 2.0 * sigma * sigma;
            var sum = 0.0;
            for (var i = -radius; i <= radius; i++)
            {
                var w = Math.Exp(-(i * i) / twoSigma2);
                kernel[i + radius] = w;
                sum += w;
            }
            for (var i = 0; i < size; i++)
            {
                kernel[i] /= sum;
            }

            var tmp = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                var rowOff = y * width;
                for (var x = 0; x < width; x++)
                {
                    var acc = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        acc += src[rowOff + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
                    }
                    tmp[rowOff + x] = (float)acc;
                }
            }

            var dst = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var acc = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        acc += tmp[(Math.Clamp(y + k, 0, height - 1) * width) + x] * kernel[k + radius];
                    }
                    dst[(y * width) + x] = (float)acc;
                }
            }
            return dst;
        }
    }
}
