using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Finds the offset and rotation between this image and another image by matching stars.
    /// </summary>
    /// <param name="other">Image to base rotation and offset on (i.e. reference image)</param>
    /// <param name="snrMin">Mininum signal to noise ratio to consider for stars</param>
    /// <param name="maxStars">Maximum number of stars to consider</param>
    /// <param name="maxRetries"></param>
    /// <param name="minStars">Mininum of stars required to find matching quads</param>
    /// <param name="quadTolerance">factor of how difference to consider quads matching.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <remarks>
    /// Returns null if not enough stars are found or no match is found.
    /// Note that the returned offset is in the coordinate system of this image, so it can be used to align this image to the other image.
    /// Currently only images of same pixel scale are supported.
    /// </remarks>
    public async Task<Matrix3x2?> FindOffsetAndRotationAsync(Image other, int channel, int otherChannel, float snrMin = 20f, int maxStars = 500, int maxRetries = 2, int minStars = 24, float quadTolerance = 0.008f, CancellationToken cancellationToken = default)
    {
        // FindOffsetAndRotation needs minStars correspondences for a stable
        // affine fit; pass that all the way through so the retry loop terminates
        // as soon as we have enough.
        var starList1Task = FindStarsAsync(channel, snrMin, maxStars, minStars, maxRetries, cancellationToken: cancellationToken);
        var starList2Task = other.FindStarsAsync(otherChannel, snrMin, maxStars, minStars, maxRetries, cancellationToken: cancellationToken);

        var starLists = await Task.WhenAll(starList1Task, starList2Task);

        if (starLists[0].Count >= minStars && starLists[1].Count >= minStars)
        {
            return await new SortedStarList(starLists[0]).FindOffsetAndRotationAsync(starLists[1], minStars / 4, quadTolerance);
        }

        return null;
    }

    /// <summary>
    /// Transforms the image using the given 3x2 affine transformation matrix. The output image will be large enough to contain the entire transformed image. The pixel values are calculated using bilinear interpolation. Note that the transformation is applied in reverse order, so the inverse of the given matrix is used to calculate the source pixel for each destination pixel. This allows for correct handling of rotations and scaling. If the transformation is not invertible, an exception is thrown. Note that this method can be computationally expensive for large images or complex transformations, so it should be used with caution. Also note that this method does not perform any cropping or padding.
    /// </summary>
    /// <param name="transform"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public Task<Image> TransformAsync(in Matrix3x2 transform, CancellationToken cancellationToken = default)
    {
        if (transform.IsIdentity)
        {
            return Task.FromResult(this);
        }

        var (_, width, height) = Shape;
        var tl_p = Vector2.Transform(Vector2.Zero, transform);
        var tr_p = Vector2.Transform(new Vector2(width, 0), transform);
        var bl_p = Vector2.Transform(new Vector2(0, height), transform);
        var br_p = Vector2.Transform(new Vector2(width, height), transform);

        var top = MathF.Min(MathF.Min(tl_p.Y, tr_p.Y), MathF.Min(bl_p.Y, br_p.Y));
        var left = MathF.Min(MathF.Min(tl_p.X, tr_p.X), MathF.Min(bl_p.X, br_p.X));
        var bottom = MathF.Max(MathF.Max(tl_p.Y, tr_p.Y), MathF.Max(bl_p.Y, br_p.Y));
        var right = MathF.Max(MathF.Max(tl_p.X, tr_p.X), MathF.Max(bl_p.X, br_p.X));

        return DoTransformationAsync(transform, new Vector2(left, top), new Vector2(right, bottom), cancellationToken);
    }

    private async Task<Image> DoTransformationAsync(Matrix3x2 transform, Vector2 tl, Vector2 br, CancellationToken cancellationToken = default)
    {
        var translated = transform * Matrix3x2.CreateTranslation(-tl);
        if (!Matrix3x2.Invert(translated, out var inverseTransform))
        {
            throw new ArgumentException("Transform is not invertible", nameof(transform));
        }

        var newWidth = (int)MathF.Ceiling(br.X - tl.X);
        var newHeight = (int)MathF.Ceiling(br.Y - tl.Y);

        var channelCount = ChannelCount;
        var width = Width;
        var height = Height;
        var transformedData = CreateChannelData(channelCount, newHeight, newWidth);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount * 4
        };

        for (var c = 0; c < channelCount; c++)
        {
            var channel = c;
            var dstChannel = transformedData[channel];
            await Parallel.ForAsync(0, newHeight, parallelOptions, async (y, ct) => await Task.Run(() =>
            {
                for (var x = 0; x < newWidth; x++)
                {
                    var sourcePos = Vector2.Transform(new Vector2(x, y), inverseTransform);
                    dstChannel[y, x] = sourcePos.X >= 0 && sourcePos.X < width && sourcePos.Y >= 0 && sourcePos.Y < height
                        ? SubpixelValue(channel, sourcePos.X, sourcePos.Y)
                        : float.NaN;
                }

                return ValueTask.CompletedTask;
            }, ct));
        }

        return new Image(transformedData, BitDepth.Float32, maxValue, minValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Returns a binned copy of this image where every <paramref name="factor"/> x
    /// <paramref name="factor"/> block of source pixels becomes one mean-pooled
    /// pixel in the output. Used by the plate solver to drop the per-pass cost
    /// of <see cref="FindStarsAsync"/> on heavily oversampled frames -- a 0.97"/px
    /// 9576x6388 polar preview binned at <c>factor=2</c> drops to 4788x3194
    /// (~4x fewer pixels, ~4x faster star detection) while keeping every
    /// detectable star intact.
    /// </summary>
    /// <remarks>
    /// <para>Mean-pool was chosen over sum-pool because it leaves the data
    /// scale and the existing Background / detection-level heuristics
    /// untouched -- callers can swap <see cref="FindStarsAsync"/> input
    /// without retuning thresholds.</para>
    /// <para>The output's <see cref="ImageMeta.PixelSizeX"/>,
    /// <see cref="ImageMeta.PixelSizeY"/>, <see cref="ImageMeta.BinX"/>,
    /// <see cref="ImageMeta.BinY"/> are scaled to match the binned scale, so
    /// <see cref="GetImageDim"/> on the result reports the correct pixel
    /// scale. Star centroids returned from a downsampled detection are in
    /// downsampled coordinates -- callers must multiply by
    /// <paramref name="factor"/> to get back to original pixel space (and
    /// account for the half-pixel offset, see code).</para>
    /// </remarks>
    /// <param name="factor">Bin factor (must be &gt;= 2). 1 returns the
    /// caller unchanged.</param>
    public Image Downsample(int factor)
    {
        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "factor must be >= 1");
        }
        if (factor == 1)
        {
            return this;
        }

        var (channelCount, srcWidth, srcHeight) = Shape;
        var dstWidth = srcWidth / factor;
        var dstHeight = srcHeight / factor;
        var blockArea = factor * factor;

        var dst = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            dst[c] = new float[dstHeight, dstWidth];
            var src = data[c];
            for (var y = 0; y < dstHeight; y++)
            {
                var srcY0 = y * factor;
                for (var x = 0; x < dstWidth; x++)
                {
                    var srcX0 = x * factor;
                    var sum = 0f;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        for (var dx = 0; dx < factor; dx++)
                        {
                            sum += src[srcY0 + dy, srcX0 + dx];
                        }
                    }
                    dst[c][y, x] = sum / blockArea;
                }
            }
        }

        // Update metadata so GetImageDim reports the binned pixel scale.
        // GetImageDim uses meta.PixelSizeX * meta.BinX as the effective pixel
        // pitch -- scale only PixelSizeX (the effective pitch on the binned
        // image truly is `factor` times larger), leave BinX alone since it
        // refers to camera hardware binning, not this software downsample.
        var newMeta = imageMeta with
        {
            PixelSizeX = imageMeta.PixelSizeX * factor,
            PixelSizeY = imageMeta.PixelSizeY * factor,
        };

        return new Image(dst, bitDepth, maxValue, minValue, pedestal, newMeta);
    }
}
