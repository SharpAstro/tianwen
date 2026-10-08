using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// R2d's D2 and D3 (docs/plans/star-remover-training.md): a starless background a star remover is taught on, starless at
/// every scale a star lives at. The plate builder's plates keep the faint stars it could not find (about 29 a 256 px tile),
/// so a target made from a plate asks the net to remove an injected faint star and keep one exactly like it.
/// </summary>
/// <remarks>
/// <para><b>What it keeps of the plate.</b> Its starlet scales from <see cref="KeepFromFwhm"/> PSF widths up and its
/// residual: the nebula's and the sky's large-scale shape, where no star survives.</para>
/// <para><b>What it draws anew under them</b> (D1's read, "the real sky is noise where it is smooth, and turbulent where it is
/// not"). Per replaced scale and channel, the plate's own local signal above its noise (<see cref="Amplitude"/>, read with
/// the plate's sources masked out) sets the texture's strength there: none on a smooth sky, where the scales are the
/// master's noise and nothing else, and as much as the plate had on a nebula. The texture's shape is one log-normal field of
/// power-law index <see cref="TextureIndex"/> a cell, shared by the channels as a nebula's shape is: emission and dust are
/// turbulent, and a log-normal brightness is what turbulence gives them. The noise is not drawn here: the exporter adds the
/// master's own, in its shape, as it does to an injected star.</para>
/// <para><b>The knots it adds (D3).</b> Compact structure that is not the PSF: round ones 1.6 to 4 times its width, elongated
/// ones 2.5 to 5 to one. Every star in a frame shares its PSF, and a knot does not, which is the cue a remover must learn and a
/// label no real plate can give: the knots are in the target, so the net is taught to keep them. With
/// <see cref="BrightKnotsWide"/> a knot under <see cref="BrightKnotMinWidths"/> PSF widths across peaks under
/// <see cref="BrightKnotSigma"/>, so nothing bright and compact is kept that a bright star could be taken for (R2d's knots
/// test: arm B, taught to keep knots up to 200 sigma at 1.6 widths, left bright stars' cores in).</para>
/// </remarks>
public sealed class SyntheticBackground
{
    /// <summary>The plate's starlet scales are kept from this many PSF widths up.</summary>
    public const double KeepFromFwhm = 4.0;

    /// <summary>The starlet scales the plate is read in: 1 to 32 px.</summary>
    public const int ScaleCount = 6;

    /// <summary>The texture's power-law index: D1 read 3.04 on the textured sky and 3.34 on the strongly textured.</summary>
    public const double TextureIndex = 3.1;

    /// <summary>The log-normal's width: a Gaussian field <c>g</c> becomes <c>exp(s g)</c>, emission's long bright tail.</summary>
    public const double LogNormalSigma = 0.8;

    /// <summary>The knots a cell holds, on average (Poisson).</summary>
    public const double KnotsPerCell = 3.0;

    /// <summary>The faintest and brightest knot's peak, in the cell's noise sigma.</summary>
    public const double KnotMinSigma = 5.0, KnotMaxSigma = 200.0;

    /// <summary>With <see cref="BrightKnotsWide"/>, a knot whose narrowest width is under <see cref="BrightKnotMinWidths"/>
    /// PSF widths peaks under this many noise sigma.</summary>
    public const double BrightKnotSigma = 20.0, BrightKnotMinWidths = 2.5;

    /// <summary>The oriented fields a <see cref="Steering"/> texture is blended from, their directions spread over 180 degrees.</summary>
    public const int SteerDirections = 8;

    /// <summary>
    /// R2e's S2 (docs/plans/star-remover-training.md, "S2: generate it"): the texture drawn along the plate's own coarse
    /// orientation, where S1 found the real sky's fine structure running along its coarse contours. Each oriented field passes
    /// the frequencies within <c>cos^(2 Exponent)</c> of its direction; <see cref="Strength"/> is the share of the texture's
    /// variance that follows the orientation where the coarse structure is fully coherent, scaled by the local coherence
    /// everywhere else. <see cref="Wander"/> (radians) turns the orientation drawn along away from the coarse one by a smooth
    /// random angle of that spread, correlated over the coarse window: S3 found the real sky's fine structure more one-way
    /// than a weak steer makes it and less tied to the coarse contours than a strong one. <see cref="Fine"/> (S2b) steers by
    /// the plate's OWN replaced scales where they hold signal: their orientation, and a steered share of <see cref="Fine"/>
    /// times their coherence, blended toward the coarse steer by how much of their energy is signal (S3 on M45: a
    /// reflection nebula's striations are far more one-way than an emission nebula's clumps, which one global steer cannot
    /// serve both).
    /// </summary>
    public readonly record struct Steering(double Strength, double Exponent, double Wander = 0, double Fine = 0);

    // The plate's coarse orientation (the structure tensor of its kept scales' luminance, SkyTexture's own), per frame pixel,
    // and with Steering.Fine its replaced scales' own.
    private sealed record Orientation(float[] Cos2, float[] Sin2, float[] Coherence, FineOrientation? Fine);

    // The replaced scales' structure where they hold signal: its orientation, its coherence with the noise's isotropic share
    // taken out, and the share of its energy that is signal (zero on a noise-only sky).
    private sealed record FineOrientation(float[] Cos2, float[] Sin2, float[] Coherence, float[] Weight);

    /// <summary>The threshold the plate's sources are masked from in the amplitude maps: under the 4 sigma the eval finds the
    /// kept sources at, so a faint star the finder only just misses raises no texture where it was.</summary>
    public const float AmplitudeMaskSigma = 3f;

    /// <summary>The smallest window the local signal is read over, in pixels (a Gaussian's sigma): a faint star's energy
    /// spread thin over it, a nebula's not.</summary>
    public const float AmplitudeWindowPx = 16f;

    /// <summary>The noise's variance is taken out of the local variance times this: a margin, so a sky that is noise and
    /// unresolved faint stars alone reads no texture.</summary>
    public const double NoiseMargin = 1.2;

    private readonly float[][] _coarse;      // [channel][y * width + x]
    private readonly float[][][] _amplitude; // [channel][scale < FirstKept][y * width + x]

    private readonly Orientation? _orientation;

    // With tails from the plate, the log-normal's width per frame pixel (TailsFromPlate); else LogNormalSigma everywhere.
    private readonly float[]? _logNormalWidth;

    /// <summary>The widest log-normal the plate's tails can ask for (its kurtosis grows steeply past it).</summary>
    public const double MaxLogNormalSigma = 1.6;

    private SyntheticBackground(int width, int height, int firstKept, double fwhm, bool brightKnotsWide, bool nebulaKnots, Steering? steering,
        Orientation? orientation, float[]? logNormalWidth, float[][] coarse, float[][][] amplitude)
    {
        BrightKnotsWide = brightKnotsWide;
        NebulaKnots = nebulaKnots;
        Steered = steering;
        _orientation = orientation;
        _logNormalWidth = logNormalWidth;
        Width = width;
        Height = height;
        FirstKept = firstKept;
        Fwhm = fwhm;
        _coarse = coarse;
        _amplitude = amplitude;
    }

    /// <summary>Whether a knot under <see cref="BrightKnotMinWidths"/> PSF widths across peaks under
    /// <see cref="BrightKnotSigma"/> (only the wide knots reach <see cref="KnotMaxSigma"/>).</summary>
    public bool BrightKnotsWide { get; }

    /// <summary>
    /// R2e's knots drawn as a nebula's (S3: D3's knots, three a cell at 5 to 200 noise sigma wherever the cell lay, added
    /// more tail than the real sky holds, and on M45 read as pink trails). A knot falls where the plate's own fine scales
    /// hold signal, in proportion to it (none on a noise-only sky); it peaks <see cref="NebulaKnotMin"/> to
    /// <see cref="NebulaKnotMax"/> times that signal's RMS, channel by channel, so it takes the nebula's colour there; and
    /// an elongated one lies along the local contour where the generator holds an orientation (a steer).
    /// </summary>
    public bool NebulaKnots { get; }

    /// <summary>A nebula knot's peak, as a multiple of the local fine-scale signal's RMS: a log-uniform draw between these.</summary>
    public const double NebulaKnotMin = 2.0, NebulaKnotMax = 8.0;

    /// <summary>The texture's steering, or null for the isotropic texture R2d's arm B was taught on.</summary>
    public Steering? Steered { get; }

    /// <summary>
    /// Whether the texture's tails follow the plate's own (S3's read of the tails): the log-normal's width set per pixel from
    /// the kurtosis the plate's replaced scales hold above their noise, and each drawn scale held to unit LOCAL RMS so the
    /// amplitude maps still set its strength where the width varies. Without it the width is <see cref="LogNormalSigma"/>
    /// everywhere and the scales are held to unit RMS over the cell, as R2d's arm B was drawn.
    /// </summary>
    public bool TailsFromPlate => _logNormalWidth is not null;

    /// <summary>The log-normal's width at a frame pixel.</summary>
    public double LogNormalWidthAt(int x, int y) => _logNormalWidth is { } w ? w[(y * Width) + x] : LogNormalSigma;

    /// <summary>The frame's width.</summary>
    public int Width { get; }

    /// <summary>The frame's height.</summary>
    public int Height { get; }

    /// <summary>The first starlet scale kept from the plate (2^FirstKept px); the finer ones are drawn anew.</summary>
    public int FirstKept { get; }

    /// <summary>The PSF width the scales were set from, in pixels.</summary>
    public double Fwhm { get; }

    /// <summary>The channels.</summary>
    public int Channels => _coarse.Length;

    /// <summary>The first starlet scale index kept for a PSF of <paramref name="fwhm"/> pixels: the smallest whose
    /// 2^j px reaches <see cref="KeepFromFwhm"/> widths.</summary>
    public static int FirstKeptScale(double fwhm) => Math.Clamp((int)Math.Ceiling(Math.Log2(KeepFromFwhm * Math.Max(0.5, fwhm))), 1, ScaleCount);

    /// <summary>S2b's read of the plate's own replaced scales at a pixel (zeros without a fine steer): the share of their
    /// energy that is signal, the signal's coherence, and its doubled-angle cosine.</summary>
    internal (float Weight, float Coherence, float Cos2) FineAt(int x, int y)
        => _orientation?.Fine is { } fine ? (fine.Weight[(y * Width) + x], fine.Coherence[(y * Width) + x], fine.Cos2[(y * Width) + x]) : default;

    /// <summary>The plate's local signal above its noise at replaced scale <paramref name="scale"/>, channel
    /// <paramref name="channel"/>, at a pixel.</summary>
    public float Amplitude(int channel, int scale, int x, int y) => _amplitude[channel][scale][(y * Width) + x];

    /// <summary>
    /// Reads <paramref name="unitPlate"/> (a plate on its master's unit scale) once per session: its coarse part, and per
    /// replaced scale and channel the local signal above the noise, with the plate's own sources (found as the builder finds
    /// them, <see cref="PlateSources.DefaultThresholdSigma"/>) and the canvas ring <paramref name="absent"/> left out.
    /// <paramref name="brightKnotsWide"/> keeps a compact knot faint (<see cref="BrightKnotsWide"/>); a
    /// <paramref name="steering"/> draws the texture along the plate's coarse orientation (<see cref="Steering"/>);
    /// <paramref name="tailsFromPlate"/> sets its tails from the plate's own (<see cref="TailsFromPlate"/>);
    /// <paramref name="nebulaKnots"/> draws the knots as a nebula's (<see cref="NebulaKnots"/>).
    /// </summary>
    public static SyntheticBackground Build(Image unitPlate, BitMatrix? absent, double fwhm, bool brightKnotsWide = false, Steering? steering = null,
        bool tailsFromPlate = false, bool nebulaKnots = false)
    {
        var (channels, width, height) = unitPlate.Shape;
        var n = width * height;
        var firstKept = FirstKeptScale(fwhm);

        var luminance = new float[n];
        for (var c = 0; c < channels; c++)
        {
            var plane = unitPlate.GetChannelSpan(c);
            for (var i = 0; i < n; i++)
            {
                luminance[i] += plane[i] / channels;
            }
        }
        var (sources, _) = PointSourceFinder.Find(luminance, width, height, absent, fwhm, AmplitudeMaskSigma);
        float[] Clean(float minSignificance)
        {
            var mask = new float[n];
            for (var i = 0; i < n; i++)
            {
                mask[i] = absent is { } a && a[i / width, i % width] ? 0f : 1f;
            }
            var reach = 2.0 * fwhm;
            var r = (int)Math.Ceiling(reach);
            foreach (var s in sources)
            {
                if (s.Significance < minSignificance)
                {
                    continue;
                }
                var cx = (int)Math.Round(s.X);
                var cy = (int)Math.Round(s.Y);
                for (var y = Math.Max(0, cy - r); y <= Math.Min(height - 1, cy + r); y++)
                {
                    for (var x = Math.Max(0, cx - r); x <= Math.Min(width - 1, cx + r); x++)
                    {
                        if (((x - s.X) * (x - s.X)) + ((y - s.Y) * (y - s.Y)) <= reach * reach)
                        {
                            mask[(y * width) + x] = 0f;
                        }
                    }
                }
            }
            return mask;
        }
        var clean = Clean(float.NegativeInfinity);

        var coarse = new float[channels][];
        var amplitude = new float[channels][][];
        for (var c = 0; c < channels; c++)
        {
            var plane = unitPlate.GetChannelSpan(c).ToArray();
            var finite = plane.Where(float.IsFinite).ToArray();
            var fill = finite.Length > 0 ? StatisticsHelper.NthSmallest(finite.AsSpan(), finite.Length / 2) : 0f;
            for (var i = 0; i < n; i++)
            {
                if (!float.IsFinite(plane[i]))
                {
                    plane[i] = fill;
                }
            }
            var decomposition = ATrousWaveletTransform.Decompose(plane, width, height, ScaleCount);
            var keep = decomposition.Residual.ToArray();
            for (var j = firstKept; j < ScaleCount; j++)
            {
                var detail = decomposition.Detail(j);
                for (var i = 0; i < n; i++)
                {
                    keep[i] += detail[i];
                }
            }
            coarse[c] = keep;

            amplitude[c] = new float[firstKept][];
            for (var j = 0; j < firstKept; j++)
            {
                amplitude[c][j] = SignalAmplitude(decomposition.Detail(j), clean, width, height, j);
            }
        }
        Orientation? orientation = null;
        if (steering is not null)
        {
            // Read as S1 reads the coarse structure: the kept scales and the residual, over a window twice the first kept scale.
            var luminanceCoarse = new float[n];
            for (var c = 0; c < channels; c++)
            {
                for (var i = 0; i < n; i++)
                {
                    luminanceCoarse[i] += coarse[c][i] / channels;
                }
            }
            var (coherence, cos2, sin2, _) = SkyTexture.StructureTensor(luminanceCoarse, width, height,
                (float)(SkyTexture.TensorWindowScales * (1 << firstKept)));
            orientation = new Orientation(cos2, sin2, coherence,
                steering.Value.Fine > 0 ? FineStructure(luminance, clean, width, height, firstKept) : null);
        }
        // The tails are read clear of the sources S1's measure masks (4 sigma), not the amplitude maps' 3: S3 judges the
        // texture through that measure, and masked from 3 sigma the read lost the clumps' cores and drew the texture too
        // smooth (eta Carinae's textured sky read kurtosis 1.5 to 2.5 against the plate's 3.1 to 3.8).
        var logNormalWidth = tailsFromPlate
            ? LogNormalWidthFromTails(luminance, Clean(PlateSources.DefaultThresholdSigma), width, height, firstKept)
            : null;
        return new SyntheticBackground(width, height, firstKept, fwhm, brightKnotsWide, nebulaKnots, steering, orientation, logNormalWidth, coarse, amplitude);
    }

    // S2b: the structure tensor of the luminance's replaced scales from 2 px up (1 px is the master's noise on every class,
    // S1), each scale's gradients over the clean pixels only (sources and the ring out, as the amplitude maps read them),
    // smoothed over AmplitudeWindowPx or the first kept scale, whichever is wider, and divided by that scale's own noise level
    // (the clean pixels' 20th percentile of its smoothed trace: smoothed, a noise-only sky's trace barely spreads, and a low
    // quantile holds while a fifth of the frame is that sky, where a median falls on a nebula filling half of it), so every
    // scale counts in noise units. Noise adds the
    // same to both eigenvalues and nothing to their difference, so the signal's coherence is the spread over the trace less
    // the scales' count. The weight the plate's own structure takes is its signal against the noise: the trace less
    // NoiseMargin times the count, over the count, so it is one where the signal's energy matches the noise of the scales
    // read and zero on a noise-only sky. A share of the total was not: a gradient lifts the noise, and stripes clear to the
    // eye (1.5 sigma a pixel) took 0.39 of it, which left the coarse orientation steering them.
    private static FineOrientation FineStructure(float[] luminance, float[] clean, int width, int height, int firstKept)
    {
        var n = width * height;
        var sigma = Math.Max(AmplitudeWindowPx, 1 << firstKept);
        var weight = Image.SeparableGaussianBlur(clean, width, height, sigma);
        var decomposition = ATrousWaveletTransform.Decompose(luminance, width, height, firstKept);
        var sxx = new float[n];
        var syy = new float[n];
        var sxy = new float[n];
        var scales = 0;
        var trace = new float[n];
        var sample = new float[n];
        for (var j = 1; j < firstKept; j++)
        {
            var detail = decomposition.Detail(j);
            var jxx = new float[n];
            var jyy = new float[n];
            var jxy = new float[n];
            for (var y = 0; y < height; y++)
            {
                var up = Math.Max(0, y - 1);
                var down = Math.Min(height - 1, y + 1);
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    var left = Math.Max(0, x - 1);
                    var right = Math.Min(width - 1, x + 1);
                    // A gradient is clean only where its every tap is.
                    var w = clean[i] * clean[(y * width) + left] * clean[(y * width) + right] * clean[(up * width) + x] * clean[(down * width) + x];
                    var gx = (detail[(y * width) + right] - detail[(y * width) + left]) / (right - left);
                    var gy = (detail[(down * width) + x] - detail[(up * width) + x]) / (down - up);
                    jxx[i] = w * gx * gx;
                    jyy[i] = w * gy * gy;
                    jxy[i] = w * gx * gy;
                }
            }
            jxx = Image.SeparableGaussianBlur(jxx, width, height, sigma);
            jyy = Image.SeparableGaussianBlur(jyy, width, height, sigma);
            jxy = Image.SeparableGaussianBlur(jxy, width, height, sigma);
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                var norm = weight[i] > 0.05f ? 1f / weight[i] : 0f;
                jxx[i] *= norm;
                jyy[i] *= norm;
                jxy[i] *= norm;
                trace[i] = jxx[i] + jyy[i];
                if (clean[i] > 0f && norm > 0f)
                {
                    sample[count++] = trace[i];
                }
            }
            var noise = count > 0 ? StatisticsHelper.NthSmallest(sample.AsSpan(0, count), count / 5) : 0f;
            if (noise <= 0f)
            {
                continue;
            }
            for (var i = 0; i < n; i++)
            {
                sxx[i] += jxx[i] / noise;
                syy[i] += jyy[i] / noise;
                sxy[i] += jxy[i] / noise;
            }
            scales++;
        }

        var cos2 = new float[n];
        var sin2 = new float[n];
        var coherence = new float[n];
        var share = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = sxx[i] + syy[i];
            var d = sxx[i] - syy[i];
            var spread = MathF.Sqrt((d * d) + (4f * sxy[i] * sxy[i]));
            var signal = t - scales;
            cos2[i] = spread > 0f ? d / spread : 0f;
            sin2[i] = spread > 0f ? 2f * sxy[i] / spread : 0f;
            coherence[i] = signal > 0f ? Math.Clamp(spread / signal, 0f, 1f) : 0f;
            share[i] = scales > 0 ? Math.Clamp((t - ((float)NoiseMargin * scales)) / scales, 0f, 1f) : 0f;
        }
        return new FineOrientation(cos2, sin2, coherence, share);
    }

    // The tails (TailsFromPlate): per replaced scale from 2 px up, the luminance's local second and fourth moments over the
    // clean pixels, smoothed as the amplitude maps are, with the noise's share taken out (Gaussian noise of variance N under a
    // signal of variance S adds 6 S N + 3 N^2 to the fourth moment), give the signal's own excess kurtosis wherever the
    // signal is at least the noise. Weighted by the signal over the scales and smoothed, it becomes the log-normal width whose
    // texture reads that kurtosis on those scales (TailTable); LogNormalSigma where no scale holds signal enough to read.
    private static float[] LogNormalWidthFromTails(float[] luminance, float[] clean, int width, int height, int firstKept)
    {
        var (kurtosis, scales) = SignalKurtosis(luminance, clean, width, height, firstKept);
        var table = TailTable.Value;
        var widths = new float[kurtosis.Length];
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = (float)(float.IsNaN(kurtosis[i]) ? LogNormalSigma : WidthForKurtosis(kurtosis[i], scales, table));
        }
        return Image.SeparableGaussianBlur(widths, width, height, 2 * AmplitudeWindowPx);
    }

    // The signal's excess kurtosis per pixel over the replaced scales from 2 px up, weighted by the signal (NaN where no scale
    // holds signal enough to read), and the scales read.
    internal static (float[] Kurtosis, int[] Scales) SignalKurtosis(float[] luminance, float[] clean, int width, int height, int firstKept)
    {
        var n = width * height;
        var decomposition = ATrousWaveletTransform.Decompose(luminance, width, height, firstKept);
        var sumK = new float[n];
        var sumS = new float[n];
        var sample = new float[n];
        var scales = Enumerable.Range(1, Math.Max(0, firstKept - 1)).ToArray();
        foreach (var j in scales)
        {
            var detail = decomposition.Detail(j);
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                if (clean[i] > 0f)
                {
                    sample[count++] = detail[i];
                }
            }
            if (count == 0)
            {
                continue;
            }
            var span = sample.AsSpan(0, count);
            var median = StatisticsHelper.NthSmallest(span, count / 2);
            for (var i = 0; i < count; i++)
            {
                span[i] = Math.Abs(span[i] - median);
            }
            var robust = 1.4826 * StatisticsHelper.NthSmallest(span, count / 2);
            var noise = robust * robust;

            var sigma = Math.Max(AmplitudeWindowPx, 2f * (1 << j));
            var d2 = new float[n];
            var d4 = new float[n];
            for (var i = 0; i < n; i++)
            {
                var v = clean[i] * detail[i] * detail[i];
                d2[i] = v;
                d4[i] = v * detail[i] * detail[i];
            }
            var weight = Image.SeparableGaussianBlur(clean, width, height, sigma);
            d2 = Image.SeparableGaussianBlur(d2, width, height, sigma);
            d4 = Image.SeparableGaussianBlur(d4, width, height, sigma);
            for (var i = 0; i < n; i++)
            {
                if (weight[i] <= 0.05f)
                {
                    continue;
                }
                var m2 = (double)d2[i] / weight[i];
                var m4 = (double)d4[i] / weight[i];
                var s = m2 - noise;
                if (s < noise)
                {
                    continue;
                }
                var kurtosis = ((m4 - (6 * s * noise) - (3 * noise * noise)) / (s * s)) - 3;
                sumK[i] += (float)(s * kurtosis);
                sumS[i] += (float)s;
            }
        }

        var kurtosisMap = new float[n];
        for (var i = 0; i < n; i++)
        {
            kurtosisMap[i] = sumS[i] > 0f ? sumK[i] / sumS[i] : float.NaN;
        }
        return (kurtosisMap, scales);
    }

    /// <summary>The texture's excess kurtosis per starlet scale at each tabled log-normal width (0 to
    /// <see cref="MaxLogNormalSigma"/> in steps of 0.1).</summary>
    internal static double[][] TailTableForTests => TailTable.Value;

    // The log-normal widths TailTable is read at: 0 to MaxLogNormalSigma in steps of TailTableStep.
    private const double TailTableStep = 0.1;

    // Per starlet scale, the texture's LOCAL excess kurtosis at each width, read as SignalKurtosis reads a plate's: the
    // coefficients' second and fourth moments smoothed over the same window, a kurtosis per pixel, averaged with the local
    // variance as its weight. One field of TextureIndex (a fixed seed), exp(width g), read inside a margin; width zero is
    // the Gaussian limit. A whole-field kurtosis was the first form, and it was not this: the log-normal's envelope swings
    // through its whole range over a field (17 at width 0.8, 72 at 1.2) and barely over a window, where a plate is read,
    // so every plate mapped to a width near zero. The amplitude maps draw the envelope.
    private static readonly Lazy<double[][]> TailTable = new(static () =>
    {
        const int size = 512;
        const int margin = 48;
        var field = PowerLawField(size, TextureIndex, new Random(1));
        var mean = field.Average();
        var sd = Math.Sqrt(field.Sum(v => (v - mean) * (v - mean)) / field.Length);
        var steps = (int)Math.Round(MaxLogNormalSigma / TailTableStep) + 1;
        var table = new double[ScaleCount][];
        for (var j = 0; j < ScaleCount; j++)
        {
            table[j] = new double[steps];
        }
        for (var k = 1; k < steps; k++)
        {
            var w = k * TailTableStep;
            var logNormal = new float[field.Length];
            for (var i = 0; i < field.Length; i++)
            {
                logNormal[i] = (float)Math.Exp(w * (field[i] - mean) / sd);
            }
            var decomposition = ATrousWaveletTransform.Decompose(logNormal, size, size, ScaleCount);
            for (var j = 0; j < ScaleCount; j++)
            {
                var detail = decomposition.Detail(j);
                var d2 = new float[detail.Length];
                var d4 = new float[detail.Length];
                for (var i = 0; i < detail.Length; i++)
                {
                    d2[i] = detail[i] * detail[i];
                    d4[i] = d2[i] * d2[i];
                }
                var sigma = Math.Max(AmplitudeWindowPx, 2f * (1 << j));
                d2 = Image.SeparableGaussianBlur(d2, size, size, sigma);
                d4 = Image.SeparableGaussianBlur(d4, size, size, sigma);
                double sumK = 0, sumW = 0;
                for (var y = margin; y < size - margin; y++)
                {
                    for (var x = margin; x < size - margin; x++)
                    {
                        var i = (y * size) + x;
                        if (d2[i] > 0f)
                        {
                            sumK += d2[i] * ((d4[i] / ((double)d2[i] * d2[i])) - 3);
                            sumW += d2[i];
                        }
                    }
                }
                table[j][k] = sumW > 0 ? sumK / sumW : 0;
            }
        }
        return table;
    });

    // The width whose texture's kurtosis, averaged over the scales read, is the one asked for: linear between the table's
    // widths, clamped to its range.
    private static double WidthForKurtosis(double kurtosis, int[] scales, double[][] table)
    {
        double Mean(int k) => scales.Length == 0 ? 0 : scales.Average(j => table[j][k]);
        var steps = table[0].Length;
        if (kurtosis <= Mean(0))
        {
            return 0;
        }
        for (var k = 1; k < steps; k++)
        {
            var (lo, hi) = (Mean(k - 1), Mean(k));
            if (kurtosis <= hi)
            {
                return TailTableStep * ((k - 1) + (hi > lo ? (kurtosis - lo) / (hi - lo) : 0));
            }
        }
        return MaxLogNormalSigma;
    }

    // The local RMS of a detail plane over the clean pixels, smoothed at twice the scale or AmplitudeWindowPx, less its noise
    // (the clean pixels' robust variance, which a smooth sky dominates, with NoiseMargin), floored at zero: the signal the
    // plate has at that scale there.
    private static float[] SignalAmplitude(ReadOnlySpan<float> detail, float[] clean, int width, int height, int scale)
    {
        var n = detail.Length;
        var energy = new float[n];
        var sample = new float[n];
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            energy[i] = clean[i] * detail[i] * detail[i];
            if (clean[i] > 0f)
            {
                sample[count++] = detail[i];
            }
        }
        var noiseVariance = 0.0;
        if (count > 0)
        {
            var span = sample.AsSpan(0, count);
            var median = StatisticsHelper.NthSmallest(span, count / 2);
            for (var i = 0; i < count; i++)
            {
                span[i] = Math.Abs(span[i] - median);
            }
            var robust = 1.4826 * StatisticsHelper.NthSmallest(span, count / 2);
            noiseVariance = robust * robust;
        }
        var sigma = Math.Max(AmplitudeWindowPx, 2f * (1 << scale));
        var local = Image.SeparableGaussianBlur(energy, width, height, sigma);
        var weight = Image.SeparableGaussianBlur(clean, width, height, sigma);
        var amplitude = new float[n];
        for (var i = 0; i < n; i++)
        {
            var variance = weight[i] > 0.05f ? local[i] / weight[i] : 0f;
            amplitude[i] = (float)Math.Sqrt(Math.Max(0.0, variance - (NoiseMargin * noiseVariance)));
        }
        return amplitude;
    }

    /// <summary>
    /// A plate's own pixel noise per channel, from the pixels' differences (<see cref="PointSourceFinder.DifferenceNoise"/>,
    /// the canvas ring <paramref name="absent"/> left out): the sigma a preview's knots are scaled by and its noise drawn at,
    /// where the exporter has the master's calibrated noise instead. NaN for a channel the differences do not fit.
    /// </summary>
    public static double[] PlateNoise(Image unitPlate, BitMatrix? absent)
    {
        var (channels, width, height) = unitPlate.Shape;
        var sigma = new double[channels];
        for (var c = 0; c < channels; c++)
        {
            sigma[c] = PointSourceFinder.DifferenceNoise(unitPlate.GetChannelSpan(c), width, height, absent).PixelSigma;
        }
        return sigma;
    }

    /// <summary>
    /// A preview of what the exporter draws, to set beside the plate it was drawn from: the <paramref name="size"/> px cell
    /// CENTRED on (<paramref name="centreX"/>, <paramref name="centreY"/>), and, when <paramref name="noisy"/>, white noise at
    /// <paramref name="noiseSigma"/> per channel on it. The exporter's noise is the master's own, in its shape; a preview's is
    /// white, so its grain is finer than a demosaiced master's. Without <paramref name="withKnots"/> the texture alone (S3's
    /// read of its tails, which the knots' own would confound; the exporter always draws them).
    /// </summary>
    public float[][] Preview(int centreX, int centreY, int size, ReadOnlySpan<double> noiseSigma, bool noisy, Random random, bool withKnots = true)
    {
        var planes = Cell(centreX - (size / 2), centreY - (size / 2), size, noiseSigma, random, out _, withKnots);
        if (noisy)
        {
            for (var c = 0; c < planes.Length; c++)
            {
                var sigma = c < noiseSigma.Length ? noiseSigma[c] : noiseSigma[^1];
                for (var i = 0; i < planes[c].Length; i++)
                {
                    planes[c][i] += (float)(sigma * Gaussian(random));
                }
            }
        }
        return planes;
    }

    /// <summary>
    /// The drawn texture alone over the <paramref name="size"/> px cell at (<paramref name="x0"/>, <paramref name="y0"/>),
    /// per channel: the replaced scales' amplitude times the texture, with no coarse part and no knots. What R2e's S4 fill
    /// draws its structure from (<see cref="TexturedHoleFill"/>).
    /// </summary>
    internal float[][] TextureOnly(int x0, int y0, int size, Random random)
    {
        var texture = TextureScales(x0, y0, size, random);
        var planes = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            var plane = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                var fy = Math.Clamp(y0 + y, 0, Height - 1);
                for (var x = 0; x < size; x++)
                {
                    var f = (fy * Width) + Math.Clamp(x0 + x, 0, Width - 1);
                    var v = 0.0;
                    for (var j = 0; j < FirstKept; j++)
                    {
                        v += _amplitude[c][j][f] * texture[j][(y * size) + x];
                    }
                    plane[(y * size) + x] = (float)v;
                }
            }
            planes[c] = plane;
        }
        return planes;
    }

    /// <summary>One knot a cell holds, in the cell's coordinates, its peak per channel in the plate's units.</summary>
    public readonly record struct Knot(double X, double Y, double SigmaMajorPx, double SigmaMinorPx, double AngleRad, double[] Peak);

    /// <summary>
    /// The noise-free background of the <paramref name="size"/> px cell at (<paramref name="x0"/>, <paramref name="y0"/>):
    /// the plate's coarse part, the texture at the replaced scales, and the knots, each channel's knots peaking at a draw
    /// between <see cref="KnotMinSigma"/> and <see cref="KnotMaxSigma"/> times <paramref name="noiseSigma"/> (the cell's noise
    /// per channel, the plate's units). Pixels outside the frame take the nearest one's. Without <paramref name="withKnots"/>
    /// none are drawn.
    /// </summary>
    public float[][] Cell(int x0, int y0, int size, ReadOnlySpan<double> noiseSigma, Random random, out ImmutableArray<Knot> knots,
        bool withKnots = true)
    {
        var texture = TextureScales(x0, y0, size, random);
        var planes = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            var plane = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                var fy = Math.Clamp(y0 + y, 0, Height - 1);
                for (var x = 0; x < size; x++)
                {
                    var fx = Math.Clamp(x0 + x, 0, Width - 1);
                    var f = (fy * Width) + fx;
                    var v = (double)_coarse[c][f];
                    for (var j = 0; j < FirstKept; j++)
                    {
                        v += _amplitude[c][j][f] * texture[j][(y * size) + x];
                    }
                    plane[(y * size) + x] = (float)v;
                }
            }
            planes[c] = plane;
        }
        knots = !withKnots ? [] : NebulaKnots ? AddNebulaKnots(planes, x0, y0, size, noiseSigma, random) : AddKnots(planes, size, noiseSigma, random);
        return planes;
    }

    // One log-normal field of index TextureIndex at twice the cell's size (so no scale meets a periodic edge), decomposed into
    // the replaced starlet scales, each cut to the cell and scaled to unit robust RMS. Steered, the Gaussian field under the
    // log-normal follows the plate's coarse orientation over the whole grid, the cell at its middle.
    private float[][] TextureScales(int x0, int y0, int size, Random random)
    {
        var n = 1;
        while (n < 2 * size)
        {
            n <<= 1;
        }
        var offset = (n - size) / 2;
        var field = Steered is { } steering && _orientation is { } orientation
            ? SteeredField(n, x0 - offset, y0 - offset, steering, orientation, random)
            : PowerLawField(n, TextureIndex, random);
        var mean = 0.0;
        foreach (var v in field)
        {
            mean += v;
        }
        mean /= field.Length;
        var sd = Math.Sqrt(field.Sum(v => (v - mean) * (v - mean)) / field.Length);
        var logNormal = new float[field.Length];
        if (_logNormalWidth is { } widths)
        {
            // The plate's own tails: each grid pixel's width read at its frame pixel, the cell at the grid's middle.
            for (var v = 0; v < n; v++)
            {
                var fy = Math.Clamp(y0 - offset + v, 0, Height - 1);
                for (var u = 0; u < n; u++)
                {
                    var i = (v * n) + u;
                    var w = widths[(fy * Width) + Math.Clamp(x0 - offset + u, 0, Width - 1)];
                    logNormal[i] = (float)Math.Exp(w * (field[i] - mean) / (sd > 0 ? sd : 1.0));
                }
            }
        }
        else
        {
            for (var i = 0; i < field.Length; i++)
            {
                logNormal[i] = (float)Math.Exp(LogNormalSigma * (field[i] - mean) / (sd > 0 ? sd : 1.0));
            }
        }
        var decomposition = ATrousWaveletTransform.Decompose(logNormal, n, n, ScaleCount);
        var scales = new float[FirstKept][];
        for (var j = 0; j < FirstKept; j++)
        {
            var detail = decomposition.Detail(j);
            var cut = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    cut[(y * size) + x] = detail[((y + offset) * n) + x + offset];
                }
            }
            if (TailsFromPlate)
            {
                // Unit LOCAL RMS, over the amplitude maps' own window: where the width varies, so does the log-normal's
                // spread, and a cell-wide scale would leave it in the texture's strength, which the amplitude maps set.
                var energy = new float[cut.Length];
                for (var i = 0; i < cut.Length; i++)
                {
                    energy[i] = cut[i] * cut[i];
                }
                energy = Image.SeparableGaussianBlur(energy, size, size, Math.Max(AmplitudeWindowPx, 2f * (1 << j)));
                for (var i = 0; i < cut.Length; i++)
                {
                    cut[i] = energy[i] > 0f ? cut[i] / MathF.Sqrt(energy[i]) : 0f;
                }
            }
            else
            {
                var abs = cut.Select(static v => Math.Abs(v)).ToArray();
                var mad = StatisticsHelper.NthSmallest(abs.AsSpan(), abs.Length / 2);
                var scale = mad > 0 ? 1.0 / (1.4826 * mad) : 0.0;
                for (var i = 0; i < cut.Length; i++)
                {
                    cut[i] = (float)(cut[i] * scale);
                }
            }
            scales[j] = cut;
        }
        return scales;
    }

    /// <summary>A Gaussian random field on an <paramref name="n"/> x <paramref name="n"/> grid (a power of two) whose power falls
    /// as <c>k^-beta</c>, zero mean.</summary>
    internal static double[] PowerLawField(int n, double beta, Random random)
    {
        var c = new Complex[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var fy = ky < n / 2 ? ky : ky - n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = kx < n / 2 ? kx : kx - n;
                var k = Math.Sqrt((fx * fx) + (fy * fy));
                c[(ky * n) + kx] = k == 0
                    ? Complex.Zero
                    : new Complex(Gaussian(random), Gaussian(random)) * Math.Pow(k, -beta / 2.0);
            }
        }
        Fft2D.Inverse(c, n, n);
        return [.. c.Select(static z => z.Real)];
    }

    // R2e's S2: PowerLawField's spectrum (the same draws) through the isotropic window and SteerDirections angular ones, each
    // passing cos^(2 Exponent) of the angle between a frequency and its direction (a frequency along a direction is a gradient
    // along it, which is what the structure tensor reads). Per grid pixel the two directions either side of the coarse
    // orientation at the frame pixel (fx0 + u, fy0 + v) are blended by angle, and that is mixed with the isotropic field by
    // Strength times the coarse coherence. The fields share one noise, so the mix's variance is read off the fields'
    // covariances and divided out: the texture's variance does not follow the orientation.
    private double[] SteeredField(int n, int fx0, int fy0, Steering steering, Orientation orientation, Random random)
    {
        var spectrum = new Complex[n * n];
        var psi = new double[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var fy = ky < n / 2 ? ky : ky - n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = kx < n / 2 ? kx : kx - n;
                var k = Math.Sqrt((fx * fx) + (fy * fy));
                spectrum[(ky * n) + kx] = k == 0
                    ? Complex.Zero
                    : new Complex(Gaussian(random), Gaussian(random)) * Math.Pow(k, -TextureIndex / 2.0);
                psi[(ky * n) + kx] = Math.Atan2(fy, fx);
            }
        }

        // The wander: white noise smoothed over the coarse orientation's own window, at unit spread, times Wander. Drawn after
        // the spectrum and only when asked, so a steer without it draws what it drew before.
        float[]? wander = null;
        if (steering.Wander > 0)
        {
            var white = new float[n * n];
            for (var i = 0; i < white.Length; i++)
            {
                white[i] = (float)Gaussian(random);
            }
            wander = Image.SeparableGaussianBlur(white, n, n, (float)(SkyTexture.TensorWindowScales * (1 << FirstKept)));
            var spread = Math.Sqrt(wander.Sum(static w => (double)w * w) / wander.Length);
            for (var i = 0; i < wander.Length; i++)
            {
                wander[i] = (float)(steering.Wander * wander[i] / spread);
            }
        }

        // The window's mean over angle is one, so every field keeps the isotropic one's radial law.
        var norm = 1.0 / MeanCosPower(steering.Exponent);
        var fields = new double[SteerDirections + 1][];
        for (var d = 0; d <= SteerDirections; d++)
        {
            var filtered = new Complex[n * n];
            var direction = d * Math.PI / SteerDirections;
            for (var i = 0; i < filtered.Length; i++)
            {
                filtered[i] = d == SteerDirections
                    ? spectrum[i]
                    : spectrum[i] * Math.Sqrt(norm * Math.Pow(Math.Abs(Math.Cos(psi[i] - direction)), 2 * steering.Exponent));
            }
            Fft2D.Inverse(filtered, n, n);
            fields[d] = [.. filtered.Select(static z => z.Real)];
        }
        var iso = fields[SteerDirections];

        // Covariances over the grid (every field is zero-mean: no power at k = 0).
        double Cov(double[] a, double[] b)
        {
            var s = 0.0;
            for (var i = 0; i < a.Length; i++)
            {
                s += a[i] * b[i];
            }
            return s / a.Length;
        }
        var varIso = Cov(iso, iso);
        var varDir = new double[SteerDirections];
        var covIso = new double[SteerDirections];
        var covNext = new double[SteerDirections];
        for (var d = 0; d < SteerDirections; d++)
        {
            varDir[d] = Cov(fields[d], fields[d]);
            covIso[d] = Cov(iso, fields[d]);
            covNext[d] = Cov(fields[d], fields[(d + 1) % SteerDirections]);
        }

        var field = new double[n * n];
        for (var v = 0; v < n; v++)
        {
            var fy = Math.Clamp(fy0 + v, 0, Height - 1);
            for (var u = 0; u < n; u++)
            {
                var f = (fy * Width) + Math.Clamp(fx0 + u, 0, Width - 1);
                double cos2 = orientation.Cos2[f], sin2 = orientation.Sin2[f];
                var s = Math.Clamp(steering.Strength * orientation.Coherence[f], 0.0, 1.0);
                var wandering = 1.0;
                if (orientation.Fine is { } fine)
                {
                    // Where the plate's own replaced scales hold signal, their orientation and their coherence steer.
                    var signal = (double)fine.Weight[f];
                    cos2 = (signal * fine.Cos2[f]) + ((1 - signal) * cos2);
                    sin2 = (signal * fine.Sin2[f]) + ((1 - signal) * sin2);
                    s = (signal * Math.Clamp(steering.Fine * fine.Coherence[f], 0.0, 1.0)) + ((1 - signal) * s);
                    wandering = 1 - signal;
                }
                var theta = (0.5 * Math.Atan2(sin2, cos2)) + (wandering * (wander?[(v * n) + u] ?? 0.0));
                theta -= Math.PI * Math.Floor(theta / Math.PI);
                var t = theta / (Math.PI / SteerDirections);
                var d1 = (int)Math.Floor(t) % SteerDirections;
                var d2 = (d1 + 1) % SteerDirections;
                var (w2, w1) = Math.SinCos((t - Math.Floor(t)) * Math.PI / 2);
                var a = Math.Sqrt(1 - s);
                var b = Math.Sqrt(s);
                var variance = (a * a * varIso)
                    + (b * b * ((w1 * w1 * varDir[d1]) + (w2 * w2 * varDir[d2]) + (2 * w1 * w2 * covNext[d1])))
                    + (2 * a * b * ((w1 * covIso[d1]) + (w2 * covIso[d2])));
                var i = (v * n) + u;
                var mix = (a * iso[i]) + (b * ((w1 * fields[d1][i]) + (w2 * fields[d2][i])));
                field[i] = variance > 0 ? mix * Math.Sqrt(varIso / variance) : iso[i];
            }
        }
        return field;
    }

    // The mean over angle of |cos|^(2m) (Gamma(m + 1/2) / (sqrt(pi) Gamma(m + 1))), summed over the half turn so any m serves.
    private static double MeanCosPower(double m)
    {
        var sum = 0.0;
        const int steps = 4096;
        for (var i = 0; i < steps; i++)
        {
            sum += Math.Pow(Math.Abs(Math.Cos((i + 0.5) * Math.PI / steps)), 2 * m);
        }
        return sum / steps;
    }

    // The knots: a Poisson count, each round and wider than the PSF or elongated, inside the cell's 16 px rim, its peak per
    // channel a log-uniform draw in noise sigma: up to KnotMaxSigma, or with BrightKnotsWide up to BrightKnotSigma where the
    // knot is narrower than BrightKnotMinWidths PSF widths (the same draws either way, so the option moves no other knot).
    private ImmutableArray<Knot> AddKnots(float[][] planes, int size, ReadOnlySpan<double> noiseSigma, Random random)
    {
        var psfSigma = Fwhm / 2.3548;
        var count = Poisson(KnotsPerCell, random);
        var knots = ImmutableArray.CreateBuilder<Knot>(count);
        for (var k = 0; k < count; k++)
        {
            double major, minor;
            if (random.NextDouble() < 0.5)
            {
                major = minor = psfSigma * (1.6 + (2.4 * random.NextDouble()));
            }
            else
            {
                minor = psfSigma * (1.0 + (0.5 * random.NextDouble()));
                major = minor * (2.5 + (2.5 * random.NextDouble()));
            }
            var angle = random.NextDouble() * Math.PI;
            var cx = 16 + (random.NextDouble() * (size - 32));
            var cy = 16 + (random.NextDouble() * (size - 32));
            var ceiling = BrightKnotsWide && minor < BrightKnotMinWidths * psfSigma ? BrightKnotSigma : KnotMaxSigma;
            var level = KnotMinSigma * Math.Pow(ceiling / KnotMinSigma, random.NextDouble());
            var peak = new double[planes.Length];
            for (var c = 0; c < planes.Length; c++)
            {
                peak[c] = level * (c < noiseSigma.Length ? noiseSigma[c] : noiseSigma[^1]);
            }
            DrawKnot(planes, size, cx, cy, major, minor, angle, peak);
            knots.Add(new Knot(cx, cy, major, minor, angle, peak));
        }
        return knots.MoveToImmutable();
    }

    // One knot: an elliptical Gaussian of sigmas major along angle and minor across it, at peak per channel.
    private static void DrawKnot(float[][] planes, int size, double cx, double cy, double major, double minor, double angle, double[] peak)
    {
        var (sin, cos) = Math.SinCos(angle);
        var reach = (int)Math.Ceiling(4 * major);
        for (var y = Math.Max(0, (int)cy - reach); y <= Math.Min(size - 1, (int)cy + reach); y++)
        {
            for (var x = Math.Max(0, (int)cx - reach); x <= Math.Min(size - 1, (int)cx + reach); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var u = ((dx * cos) + (dy * sin)) / major;
                var v = ((-dx * sin) + (dy * cos)) / minor;
                var g = Math.Exp(-0.5 * ((u * u) + (v * v)));
                for (var c = 0; c < planes.Length; c++)
                {
                    planes[c][(y * size) + x] += (float)(peak[c] * g);
                }
            }
        }
    }

    // R2e's nebula knots (NebulaKnots). T_c is the plate's fine-scale signal RMS in channel c at a pixel (its amplitude
    // maps over the replaced scales), and rho, the channels' mean of T_c over their noise, capped at one, how much of that
    // pixel is nebula. The count is a Poisson draw of KnotsPerCell times rho's mean over the cell, each knot placed by
    // rejection on rho (none where it is zero); its peak a log-uniform NebulaKnotMin to NebulaKnotMax times T_c, channel
    // by channel, so its colour is the nebula's there; its shapes D3's, an elongated one laid along the local contour where
    // a steer gives an orientation, at a random angle otherwise. BrightKnotsWide still caps a compact one.
    private ImmutableArray<Knot> AddNebulaKnots(float[][] planes, int x0, int y0, int size, ReadOnlySpan<double> noiseSigma, Random random)
    {
        var psfSigma = Fwhm / 2.3548;
        var channels = planes.Length;
        var noise = new double[channels];
        for (var c = 0; c < channels; c++)
        {
            noise[c] = c < noiseSigma.Length ? noiseSigma[c] : noiseSigma[^1];
        }
        int FrameIndex(double x, double y)
            => (Math.Clamp(y0 + (int)y, 0, Height - 1) * Width) + Math.Clamp(x0 + (int)x, 0, Width - 1);
        double SignalRms(int c, int f)
        {
            var sum = 0.0;
            for (var j = 0; j < FirstKept; j++)
            {
                var a = (double)_amplitude[c][j][f];
                sum += a * a;
            }
            return Math.Sqrt(sum);
        }
        double Rho(int f)
        {
            var sum = 0.0;
            for (var c = 0; c < channels; c++)
            {
                sum += noise[c] > 0 ? SignalRms(c, f) / noise[c] : 0;
            }
            return Math.Min(1.0, sum / channels);
        }

        var (rhoSum, samples) = (0.0, 0);
        for (var y = 0; y < size; y += 8)
        {
            for (var x = 0; x < size; x += 8)
            {
                rhoSum += Rho(FrameIndex(x, y));
                samples++;
            }
        }
        var count = Poisson(KnotsPerCell * (samples > 0 ? rhoSum / samples : 0), random);
        var knots = ImmutableArray.CreateBuilder<Knot>(count);
        for (var k = 0; k < count; k++)
        {
            double cx = 0, cy = 0;
            var placed = false;
            for (var attempt = 0; attempt < 64 && !placed; attempt++)
            {
                cx = 16 + (random.NextDouble() * (size - 32));
                cy = 16 + (random.NextDouble() * (size - 32));
                placed = random.NextDouble() < Rho(FrameIndex(cx, cy));
            }
            if (!placed)
            {
                continue;
            }
            double major, minor;
            var round = random.NextDouble() < 0.5;
            if (round)
            {
                major = minor = psfSigma * (1.6 + (2.4 * random.NextDouble()));
            }
            else
            {
                minor = psfSigma * (1.0 + (0.5 * random.NextDouble()));
                major = minor * (2.5 + (2.5 * random.NextDouble()));
            }
            var f = FrameIndex(cx, cy);
            var randomAngle = random.NextDouble() * Math.PI;
            // The major axis along the contour: a quarter turn from the gradient the orientation reads.
            var angle = !round && GradientAngle(f) is { } gradient ? gradient + (Math.PI / 2) : randomAngle;
            var level = NebulaKnotMin * Math.Pow(NebulaKnotMax / NebulaKnotMin, random.NextDouble());
            var peak = new double[channels];
            for (var c = 0; c < channels; c++)
            {
                peak[c] = level * SignalRms(c, f);
                if (BrightKnotsWide && minor < BrightKnotMinWidths * psfSigma)
                {
                    peak[c] = Math.Min(peak[c], BrightKnotSigma * noise[c]);
                }
            }
            DrawKnot(planes, size, cx, cy, major, minor, angle, peak);
            knots.Add(new Knot(cx, cy, major, minor, angle, peak));
        }
        return knots.ToImmutable();
    }

    // The gradient's angle the texture is steered by at a frame pixel (the coarse orientation, blended toward the plate's own
    // fine one where it holds signal, as SteeredField blends them), or null without a steer.
    private double? GradientAngle(int f)
    {
        if (_orientation is not { } o)
        {
            return null;
        }
        double cos2 = o.Cos2[f], sin2 = o.Sin2[f];
        if (o.Fine is { } fine)
        {
            var w = (double)fine.Weight[f];
            cos2 = (w * fine.Cos2[f]) + ((1 - w) * cos2);
            sin2 = (w * fine.Sin2[f]) + ((1 - w) * sin2);
        }
        return 0.5 * Math.Atan2(sin2, cos2);
    }

    private static int Poisson(double mean, Random random)
    {
        var limit = Math.Exp(-mean);
        var product = random.NextDouble();
        var count = 0;
        while (product > limit)
        {
            count++;
            product *= random.NextDouble();
        }
        return count;
    }

    private static double Gaussian(Random random)
        => Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
}
