using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A luminance plane made from mono colour masters on one grid: no photon a real luminance filter would add, but the
/// brightness the channels share, at the best signal to noise their sum can give, in ONE plane where detail can be worked
/// (deconvolved, sharpened, stretched) once instead of three times, while the colour is taken from the channels and may be
/// smoothed hard, the eye seeing far less detail in colour than in brightness.
/// </summary>
/// <remarks>
/// <para><b>One photometric scale, then weights by noise.</b> Each channel is first put on the reference channel's scale
/// by its stars, the median of their flux ratios (<see cref="ContinuumSubtractor.PhotometricScale"/>), so a star of the
/// field's typical colour reads the same in every scaled channel. On that scale each channel's pixel noise
/// (<see cref="PixelNoise.FromNeighbours"/>) sets its weight, the inverse of its variance: the combination with the least
/// noise for anything of that colour, which is what a noise-weighted integration of the channels does. A fixed recipe
/// (the eye's luminance, heavy in green) weights a noisy channel as much as a clean one.</para>
/// <para><b>Noise read over blocks, never pixel to pixel.</b> A neighbour difference reads the noise at the finest scale,
/// and anything that correlates neighbours lowers it there while leaving the noise of the signal's scales alone: a 0.55 px
/// Gaussian read 0.45 of white noise's true level and a Lanczos-3 shift 0.73, so a blurred or resampled channel took up to
/// five times its weight (LDN 1622's blurred green read 10.8 where unblurred it reads 16.0). Over
/// <see cref="NoiseBlockPx"/> px block means the same two read 0.85 and 0.97 (<see cref="PixelNoise.FromBlocks"/>).</para>
/// <para><b>Stars at one width</b>: a star's flux here is a fixed-aperture sum, which a wider channel reads low. Deblur
/// every channel (<see cref="NarrowbandCombination.DeblurAsync"/>), or measure on PSF-matched copies (<see cref="PsfMatch"/>,
/// <c>noisePlanes</c>) while the luminance itself is made from the unblurred ones.</para>
/// </remarks>
public static class SyntheticLuminance
{
    /// <summary>One channel's part: its scale onto the reference, its background and noise on its own scale, the noise on
    /// the reference's scale, its weight (they sum to one) and how many stars set the scale.</summary>
    public readonly record struct Channel(double Scale, double Background, double Noise, double ScaledNoise, double Weight, int Stars);

    /// <summary>The block a channel's noise is read over, in pixels: past the reach of a sub-pixel blur or a resample.</summary>
    public const int NoiseBlockPx = 4;

    /// <summary>
    /// The luminance of <paramref name="masters"/> on <paramref name="reference"/>'s scale and background, absent where any
    /// master is; the channels' parts; and the luminance's own pixel noise, to set against the reference's.
    /// </summary>
    public static async Task<(Image Luminance, Channel[] Channels, double Noise)> BuildAsync(
        IReadOnlyList<Image> masters, int reference = 0, CancellationToken cancellationToken = default)
    {
        var channels = await MeasureAsync(masters, reference, null, cancellationToken);
        var luminance = Combine(masters, channels, reference);
        return (luminance, channels, BlockNoise(luminance));
    }

    /// <summary>A plane's pixel noise from its neighbours' differences (<see cref="PixelNoise.FromNeighbours"/>): the noise
    /// at the finest scale, which a blur lowers.</summary>
    public static double Noise(Image plane) => PixelNoise.FromNeighbours(plane.GetChannelSpan(0), plane.Width);

    /// <summary>A plane's noise per pixel read over <see cref="NoiseBlockPx"/> px blocks (<see cref="PixelNoise.FromBlocks"/>):
    /// what the luminance weights are set by.</summary>
    public static double BlockNoise(Image plane) => PixelNoise.FromBlocks(plane.GetChannelSpan(0), plane.Width, NoiseBlockPx);

    /// <summary>
    /// Each master's part: its scale onto <paramref name="reference"/> by its stars, its background and noise, and its
    /// weight. Measured on masters WITH their stars, which the scale is read from; <see cref="Combine"/> then applies the
    /// parts to any planes on that grid, starless ones included. The noise is read on <paramref name="noisePlanes"/> where
    /// given: the unblurred planes, when the scales come from PSF-matched copies.
    /// </summary>
    public static async Task<Channel[]> MeasureAsync(
        IReadOnlyList<Image> masters, int reference = 0, IReadOnlyList<Image>? noisePlanes = null, CancellationToken cancellationToken = default)
    {
        if (noisePlanes is not null && noisePlanes.Count != masters.Count)
        {
            throw new ArgumentException("one noise plane per master", nameof(noisePlanes));
        }
        if (masters.Count == 0)
        {
            throw new ArgumentException("no masters", nameof(masters));
        }
        var width = masters[0].Width;
        var height = masters[0].Height;
        foreach (var m in masters)
        {
            if (m.Width != width || m.Height != height)
            {
                throw new ArgumentException("the masters must be on one grid (MasterAlignment, tianwen image align)", nameof(masters));
            }
        }

        var stars = new StarList[masters.Count];
        for (var i = 0; i < masters.Count; i++)
        {
            stars[i] = await MasterAlignment.FindStarsAsync(masters[i], cancellationToken);
        }

        var channels = new Channel[masters.Count];
        var inverseVariance = new double[masters.Count];
        double total = 0;
        for (var i = 0; i < masters.Count; i++)
        {
            var plane = masters[i].GetChannelSpan(0);
            var (scale, matched, _) = i == reference
                ? (1.0, stars[i].Count, 0.0)
                : ContinuumSubtractor.PhotometricScale(stars[reference], stars[i]);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                throw new InvalidOperationException($"master {i}: no photometric scale onto the reference ({matched} stars matched)");
            }
            var background = PixelNoise.FiniteMedian(plane);
            var noise = BlockNoise(noisePlanes is null ? masters[i] : noisePlanes[i]);
            var scaledNoise = scale * noise;
            inverseVariance[i] = 1.0 / (scaledNoise * scaledNoise);
            total += inverseVariance[i];
            channels[i] = new Channel(scale, background, noise, scaledNoise, 0, matched);
        }
        for (var i = 0; i < masters.Count; i++)
        {
            channels[i] = channels[i] with { Weight = inverseVariance[i] / total };
        }
        return channels;
    }

    /// <summary>
    /// The luminance of <paramref name="planes"/> by <paramref name="channels"/> (<see cref="MeasureAsync"/>): each taken
    /// about its own median, scaled and weighted, on the reference's background; absent where any plane is.
    /// </summary>
    public static Image Combine(IReadOnlyList<Image> planes, IReadOnlyList<Channel> channels, int reference = 0)
    {
        var width = planes[0].Width;
        var height = planes[0].Height;
        // Each plane resolved once for the whole walk, never per sample; each taken about its OWN median, so a starless
        // plane, whose median is its sky as a master's is, lands on the same background.
        var arrays = new float[planes.Count][,];
        var medians = new double[planes.Count];
        for (var i = 0; i < planes.Count; i++)
        {
            arrays[i] = planes[i].GetChannelArray(0);
            medians[i] = PixelNoise.FiniteMedian(planes[i].GetChannelSpan(0));
        }
        var luminance = new float[height, width];
        var referenceBackground = medians[reference];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                var absent = false;
                for (var i = 0; i < planes.Count; i++)
                {
                    var v = arrays[i][y, x];
                    if (!float.IsFinite(v))
                    {
                        absent = true;
                        break;
                    }
                    sum += channels[i].Weight * channels[i].Scale * (v - medians[i]);
                }
                luminance[y, x] = absent ? float.NaN : (float)(sum + referenceBackground);
            }
        }
        var refImage = planes[reference];
        return new Image([luminance], BitDepth.Float32, refImage.MaxValue, refImage.MinValue, refImage.Pedestal,
            refImage.ImageMeta with { Filter = Filter.Luminance });
    }
}

/// <summary>
/// A channel given its fine detail from a luminance (<see cref="SyntheticLuminance"/>): <c>blur(channel) + (L - blur(L)) /
/// scale</c>, the channel's own colour at the scales the blur keeps and the luminance's detail, in the channel's units,
/// below them. It is LRGB in linear form, with no division near the background where a ratio is noise: the colour noise a
/// channel carries at fine scales is replaced by the luminance's lower noise, and the eye, which sees little detail in
/// colour, does not miss the colour detail the blur takes.
/// <para><b>Not on stars.</b> A high-pass rings negative around every peak, and each channel takes the luminance's detail
/// in the field's typical star colour while its blurred part keeps the star's own, so at each radius the channels disagree:
/// on LDN 1622 every star grew a coloured core and a dark rim. Apply it to STARLESS channels, where the colour noise is, and
/// put the stars back after (<see cref="NarrowbandCombination.WithStars"/>).</para>
/// </summary>
public static class LuminanceDetail
{
    /// <summary>The default blur the colour is kept above, in pixels.</summary>
    public const float DefaultColourSigma = 1.5f;

    /// <summary><paramref name="channel"/> (one plane) with <paramref name="luminance"/>'s detail below
    /// <paramref name="colourSigma"/>; <paramref name="scale"/> takes the channel onto the luminance's scale
    /// (<see cref="SyntheticLuminance.Channel.Scale"/>). Absent where either is.</summary>
    public static Image Apply(Image channel, Image luminance, double scale, float colourSigma = DefaultColourSigma)
    {
        if (channel.Width != luminance.Width || channel.Height != luminance.Height)
        {
            throw new ArgumentException("the channel and the luminance must be on one grid", nameof(luminance));
        }
        var softChannel = PsfMatch.BlurKeepingAbsent(channel, colourSigma).GetChannelSpan(0);
        var softLuminance = PsfMatch.BlurKeepingAbsent(luminance, colourSigma).GetChannelSpan(0);
        var x = channel.GetChannelSpan(0);
        var l = luminance.GetChannelSpan(0);
        var (_, width, height) = channel.Shape;
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var px = 0; px < width; px++)
            {
                var at = (y * width) + px;
                plane[y, px] = float.IsFinite(x[at]) && float.IsFinite(l[at])
                    ? (float)(softChannel[at] + ((l[at] - softLuminance[at]) / scale))
                    : float.NaN;
            }
        }
        return new Image([plane], BitDepth.Float32, channel.MaxValue, channel.MinValue, channel.Pedestal, channel.ImageMeta);
    }
}

/// <summary>A plane's pixel noise and background, read so that structure does not inflate them.</summary>
internal static class PixelNoise
{
    /// <summary>The noise from the differences of horizontal neighbours, which cancel anything smooth: 1.4826 MAD over root
    /// 2. NaN with no finite pair.</summary>
    internal static double FromNeighbours(ReadOnlySpan<float> values, int width)
    {
        var differences = new float[values.Length];
        var n = 0;
        for (var i = 0; i + 1 < values.Length; i++)
        {
            if ((i + 1) % width != 0 && float.IsFinite(values[i]) && float.IsFinite(values[i + 1]))
            {
                differences[n++] = values[i + 1] - values[i];
            }
        }
        if (n == 0)
        {
            return double.NaN;
        }
        var (_, mad) = StatisticsHelper.MedianAndMad(differences.AsSpan(0, n));
        return 1.4826 * mad / Math.Sqrt(2.0);
    }

    /// <summary>
    /// The noise per pixel read over <paramref name="block"/> px blocks: each block's mean (a block with an absent pixel is
    /// absent), the neighbouring means' differences read as <see cref="FromNeighbours"/> reads pixels, times the block's
    /// side. White noise reads the same either way; a blur or a resample that correlates neighbours lowers the pixel
    /// reading and leaves this one nearly alone. NaN with no finite pair of blocks.
    /// </summary>
    /// <remarks>
    /// A star stands out of a block mean's noise by the block's side more than out of a pixel's, so a starry field read
    /// 1.2 times its noise over blocks. A second pass leaves out every block whose brightest pixel stands more than
    /// <see cref="StarBlockSigma"/> of the first reading above the block's mean: sixteen samples of noise never do.
    /// </remarks>
    internal static double FromBlocks(ReadOnlySpan<float> values, int width, int block)
    {
        if (block <= 1)
        {
            return FromNeighbours(values, width);
        }
        var height = values.Length / width;
        var bw = width / block;
        var bh = height / block;
        if (bw < 2 || bh < 1)
        {
            return double.NaN;
        }
        var means = new float[bw * bh];
        var peaks = new float[bw * bh];
        var area = block * block;
        for (var by = 0; by < bh; by++)
        {
            for (var bx = 0; bx < bw; bx++)
            {
                double sum = 0;
                var peak = float.NegativeInfinity;
                var absent = false;
                for (var y = by * block; y < (by + 1) * block && !absent; y++)
                {
                    var row = values.Slice((y * width) + (bx * block), block);
                    foreach (var v in row)
                    {
                        if (!float.IsFinite(v))
                        {
                            absent = true;
                            break;
                        }
                        sum += v;
                        peak = Math.Max(peak, v);
                    }
                }
                var at = (by * bw) + bx;
                means[at] = absent ? float.NaN : (float)(sum / area);
                peaks[at] = absent ? float.NaN : peak - means[at];
            }
        }
        var first = FromNeighbours(means, bw) * block;
        if (!double.IsFinite(first))
        {
            return first;
        }
        var starless = 0;
        for (var i = 0; i < means.Length; i++)
        {
            if (peaks[i] > StarBlockSigma * first)
            {
                means[i] = float.NaN;
            }
            else if (float.IsFinite(means[i]))
            {
                starless++;
            }
        }
        var second = starless >= 2 ? FromNeighbours(means, bw) * block : double.NaN;
        return double.IsFinite(second) ? second : first;
    }

    /// <summary>How far above its block's mean, in the first reading's noise, a pixel marks the block as holding a star.</summary>
    internal const double StarBlockSigma = 6.0;

    /// <summary>The median of the finite values; NaN with none.</summary>
    internal static double FiniteMedian(ReadOnlySpan<float> values)
    {
        var finite = new float[values.Length];
        var n = 0;
        foreach (var v in values)
        {
            if (float.IsFinite(v))
            {
                finite[n++] = v;
            }
        }
        return n > 0 ? StatisticsHelper.MedianFast(finite.AsSpan(0, n)) : double.NaN;
    }
}
