using System;
using System.Drawing;
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

    /// <summary>
    /// Warps this image into a fixed-size reference grid by inverse-mapping each
    /// output pixel through <paramref name="transform"/> and sampling the source
    /// via bilinear interpolation. Out-of-source-bounds output pixels are NaN.
    /// <para>
    /// Unlike <see cref="TransformAsync"/> (which sizes the output to contain
    /// the rotated source extents), this method's output is exactly
    /// <paramref name="refWidth"/> by <paramref name="refHeight"/> — required
    /// by the stacking integrator so every aligned frame samples the same
    /// reference grid and pixel index N maps to the same sky position across
    /// frames.
    /// </para>
    /// </summary>
    /// <param name="transform">Affine source -> reference mapping as returned
    /// by <see cref="FindOffsetAndRotationAsync"/>. The inverse is applied
    /// per output pixel.</param>
    /// <param name="refWidth">Output (reference grid) width in pixels.</param>
    /// <param name="refHeight">Output (reference grid) height in pixels.</param>
    /// <exception cref="ArgumentException"><paramref name="transform"/> is not invertible.</exception>
    public async Task<Image> WarpToReferenceGridAsync(Matrix3x2 transform, int refWidth, int refHeight, CancellationToken cancellationToken = default)
    {
        if (!Matrix3x2.Invert(transform, out var inverseTransform))
        {
            throw new ArgumentException("Transform is not invertible", nameof(transform));
        }

        var channelCount = ChannelCount;
        var srcW = Width;
        var srcH = Height;
        var output = CreateChannelData(channelCount, refHeight, refWidth);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount * 4
        };

        for (var c = 0; c < channelCount; c++)
        {
            var channel = c;
            var dstChannel = output[channel];
            await Parallel.ForAsync(0, refHeight, parallelOptions, async (y, ct) => await Task.Run(() =>
            {
                for (var x = 0; x < refWidth; x++)
                {
                    var srcPos = Vector2.Transform(new Vector2(x, y), inverseTransform);
                    dstChannel[y, x] = srcPos.X >= 0 && srcPos.X < srcW && srcPos.Y >= 0 && srcPos.Y < srcH
                        ? SubpixelValue(channel, srcPos.X, srcPos.Y)
                        : float.NaN;
                }
                return ValueTask.CompletedTask;
            }, ct));
        }

        return new Image(output, BitDepth.Float32, maxValue, minValue, pedestal, imageMeta);
    }

    /// <summary>
    /// Warp just <paramref name="canvasRegion"/> (a sub-rectangle of the
    /// reference canvas) instead of the full canvas. Output is a new
    /// <see cref="Image"/> whose dimensions equal <c>canvasRegion.Width</c> x
    /// <c>canvasRegion.Height</c>; output pixel <c>(c, r)</c> corresponds to
    /// canvas pixel <c>(canvasRegion.X + c, canvasRegion.Y + r)</c>.
    /// Out-of-source-bounds output pixels are <see cref="float.NaN"/>.
    /// <para>
    /// Same inverse-mapped bilinear sampling as
    /// <see cref="WarpToReferenceGridAsync"/>, but the output never holds the
    /// full canvas in memory — used by
    /// <c>TilePipelinedStrategy</c> so peak RAM per frame is bounded by the
    /// strip size instead of the full canvas. <paramref name="canvasWidth"/>
    /// and <paramref name="canvasHeight"/> aren't strictly needed for the
    /// arithmetic (the inverse transform doesn't care about canvas bounds),
    /// but the strategy carries them through to keep the API parallel with
    /// <see cref="WarpToReferenceGridAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="transform">Affine source -> canvas mapping.</param>
    /// <param name="canvasRegion">Sub-rectangle of the canvas to materialize.
    /// Must lie within <c>[0, canvasWidth) x [0, canvasHeight)</c>.</param>
    /// <param name="canvasWidth">Full canvas width (informational; bounds-checked
    /// against <paramref name="canvasRegion"/>).</param>
    /// <param name="canvasHeight">Full canvas height (informational; bounds-checked
    /// against <paramref name="canvasRegion"/>).</param>
    public async Task<Image> WarpRegionAsync(
        Matrix3x2 transform,
        Rectangle canvasRegion,
        int canvasWidth,
        int canvasHeight,
        CancellationToken cancellationToken = default)
    {
        if (canvasRegion.X < 0 || canvasRegion.Y < 0
            || canvasRegion.Right > canvasWidth || canvasRegion.Bottom > canvasHeight
            || canvasRegion.Width <= 0 || canvasRegion.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasRegion),
                $"Region {canvasRegion} out of canvas bounds [0,0)-({canvasWidth},{canvasHeight}).");
        }
        if (!Matrix3x2.Invert(transform, out var inverseTransform))
        {
            throw new ArgumentException("Transform is not invertible", nameof(transform));
        }

        var channelCount = ChannelCount;
        var srcW = Width;
        var srcH = Height;
        var regionW = canvasRegion.Width;
        var regionH = canvasRegion.Height;
        var x0 = canvasRegion.X;
        var y0 = canvasRegion.Y;
        var output = CreateChannelData(channelCount, regionH, regionW);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
        };

        for (var c = 0; c < channelCount; c++)
        {
            var channel = c;
            var dstChannel = output[channel];
            Parallel.For(0, regionH, parallelOptions, dy =>
            {
                var canvasY = y0 + dy;
                for (var dx = 0; dx < regionW; dx++)
                {
                    var canvasX = x0 + dx;
                    var srcPos = Vector2.Transform(new Vector2(canvasX, canvasY), inverseTransform);
                    dstChannel[dy, dx] = srcPos.X >= 0 && srcPos.X < srcW && srcPos.Y >= 0 && srcPos.Y < srcH
                        ? SubpixelValue(channel, srcPos.X, srcPos.Y)
                        : float.NaN;
                }
            });
        }

        // Yield to keep the public API async-shaped — the body is fully
        // synchronous after parallel.for completion, but returning a Task
        // keeps callers consistent with WarpToReferenceGridAsync.
        await Task.CompletedTask;
        return new Image(output, BitDepth.Float32, maxValue, minValue, pedestal, imageMeta);
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
