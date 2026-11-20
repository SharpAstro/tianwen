using System;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>The side, in pixels, of the blocks <see cref="TryEstimateSkyNoise"/> reads a frame in.</summary>
    public const int SkyNoiseBlock = 64;

    /// <summary>The rank, among a frame's blocks ordered from the quietest, whose noise is taken for the sky's.</summary>
    public const double SkyNoiseQuantile = 0.20;

    /// <summary>
    /// One channel's sky noise: the pixel noise of the quiet part of the frame, wherever in the frame that is.
    /// </summary>
    /// <remarks>
    /// <para><b>How.</b> The frame is cut into <see cref="SkyNoiseBlock"/> px blocks. A block's noise is 1.4826 times the
    /// median absolute difference of horizontally neighbouring pixels, over root two: for Gaussian noise that is its
    /// sigma, and a slow gradient or a nebula's glow cancels in the difference. The answer is the
    /// <see cref="SkyNoiseQuantile"/> quantile over the blocks (linear between ranks), so a frame may be four fifths
    /// nebula and stars and still be read on its sky.</para>
    /// <para><b>Why not the middle of the frame.</b> A central crop is the target itself: on a Tarantula master it is 30
    /// Doradus, and read 1.2 to 1.6 times this figure on the masters tried (#2309).</para>
    /// <para><b>A block that touches an absent pixel is left out whole.</b> A difference counts only when both of its
    /// pixels are finite and outside <paramref name="exclude"/> (the canvas ring, <see cref="AbsentPixels"/>); one
    /// missing difference would otherwise hand a NaN to the block's median and through it to the whole quantile.</para>
    /// <para><b>It is the noise as the frame holds it.</b> Debayering correlates neighbours, so on a one-shot-colour
    /// master this reads under the photon noise. It is the scale a black point is set by, not a photometric figure.</para>
    /// </remarks>
    /// <param name="channel">The channel to read.</param>
    /// <param name="exclude">Pixels to leave out, <c>[row, column]</c> over this image's own geometry.</param>
    /// <param name="sigma">The sky noise in the image's own units; NaN when there is none to give.</param>
    /// <returns><see langword="false"/> when the frame holds no block free of absent pixels (it is smaller than a block,
    /// or the ring reaches every one), or when the quiet blocks are exactly flat.</returns>
    public bool TryEstimateSkyNoise(int channel, BitMatrix? exclude, out float sigma)
    {
        var (_, width, height) = Shape;
        if (exclude is { } shape && (shape.Rows != height || shape.Columns != width))
        {
            throw new ArgumentException(
                $"exclude is {shape.Columns}x{shape.Rows} and the image {width}x{height}", nameof(exclude));
        }

        sigma = float.NaN;
        // A row of a block holds SkyNoiseBlock differences, so it needs one pixel more than the block is wide.
        var blocksX = (width - 1) / SkyNoiseBlock;
        var blocksY = height / SkyNoiseBlock;
        if (blocksX <= 0 || blocksY <= 0)
        {
            return false;
        }

        const int perBlock = SkyNoiseBlock * SkyNoiseBlock;
        var src = GetChannelSpan(channel);
        using var differenceScratch = ArrayPoolHelper.Rent<float>(perBlock);
        using var sigmaScratch = ArrayPoolHelper.Rent<float>(blocksX * blocksY);
        var differences = differenceScratch.AsSpan(0, perBlock);
        var sigmas = sigmaScratch.AsSpan(0, blocksX * blocksY);
        var toSigma = 1.4826f / MathF.Sqrt(2f);
        var whole = 0;
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                var x0 = bx * SkyNoiseBlock;
                var filled = 0;
                var complete = true;
                for (var y = by * SkyNoiseBlock; complete && y < (by + 1) * SkyNoiseBlock; y++)
                {
                    var row = src.Slice(y * width + x0, SkyNoiseBlock + 1);
                    if (exclude is { } skip)
                    {
                        var words = skip.RowWords(y);
                        for (var x = 0; x <= SkyNoiseBlock; x++)
                        {
                            var column = x0 + x;
                            if ((words[column >> 6] & (1ul << (column & 63))) != 0)
                            {
                                complete = false;
                                break;
                            }
                        }
                    }
                    for (var x = 0; complete && x < SkyNoiseBlock; x++)
                    {
                        var difference = row[x + 1] - row[x];
                        if (float.IsFinite(difference))
                        {
                            differences[filled++] = MathF.Abs(difference);
                        }
                        else
                        {
                            complete = false;
                        }
                    }
                }
                if (complete)
                {
                    sigmas[whole++] = toSigma * MedianFast(differences);
                }
            }
        }
        if (whole == 0)
        {
            return false;
        }

        var ranked = sigmas[..whole];
        ranked.Sort();
        var rank = SkyNoiseQuantile * (whole - 1);
        var below = (int)Math.Floor(rank);
        var above = Math.Min(below + 1, whole - 1);
        sigma = (float)(ranked[below] + (rank - below) * (ranked[above] - ranked[below]));
        return float.IsFinite(sigma) && sigma > 0f;
    }
}
