using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace TianWen.Lib.Imaging;

// track minValue and pedestal independently
public partial class Image(float[][,] data, BitDepth bitDepth, float maxValue, float minValue, float pedestal, ImageMeta imageMeta)
{
    public int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data[0].GetLength(1);

    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data[0].GetLength(0);

    public int ChannelCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data.Length;

    public (int ChannelCount, int Width, int Height) Shape => (ChannelCount, Width, Height);

    public BitDepth BitDepth => bitDepth;
    public float MaxValue => maxValue;
    public float MinValue => minValue;
    /// <summary>
    /// Image metadata such as instrument, exposure time, focal length, pixel size, ...
    /// </summary>
    public ImageMeta ImageMeta => imageMeta;

    /// <summary>
    /// Computes <see cref="ImageDim"/> from image dimensions and metadata (pixel size, binning, focal length).
    /// </summary>
    /// <returns>Image dimensions with pixel scale, or <c>null</c> if metadata is insufficient.</returns>
    public ImageDim? GetImageDim()
    {
        var meta = ImageMeta;
        if (meta.PixelSizeX > 0 && meta.FocalLength > 0 && meta.BinX > 0)
        {
            var pixelScale = meta.PixelSizeX * meta.BinX / meta.FocalLength * 206.265;
            return new ImageDim(pixelScale, Width, Height);
        }
        return null;
    }

    /// <summary>
    /// Read-only indexer to get a pixel value.
    /// </summary>
    /// <param name="h"></param>
    /// <param name="w"></param>
    /// <returns></returns>
    public float this[int c, int h, int w] => data[c][h, w];

    /// <summary>
    /// Returns a flat span over the pixel data for a single channel plane (height * width floats).
    /// </summary>
    public ReadOnlySpan<float> GetChannelSpan(int channel)
        => MemoryMarshal.CreateReadOnlySpan(ref data[channel][0, 0], data[channel].Length);

    /// <summary>
    /// SIMD-accelerated element-wise multiply: <c>dst[i] = src[i] * scalar</c>.
    /// Supports in-place operation (src and dst may alias).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MultiplyScalar(ReadOnlySpan<float> src, float scalar, Span<float> dst)
        => TensorPrimitives.Multiply(src, scalar, dst);

    /// <summary>
    /// Creates a jagged channel array structure: an array of 2D float arrays, one per channel.
    /// This avoids a single huge LOH allocation for multi-channel images.
    /// </summary>
    internal static float[][,] CreateChannelData(int channelCount, int height, int width)
    {
        var channels = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            channels[c] = Array2DPool<float>.Rent(height, width);
        }
        return channels;
    }

    private volatile bool _channelsReturned;

    /// <summary>
    /// Returns all channel arrays to <see cref="Array2DPool{T}"/> for reuse.
    /// Safe to call multiple times — only the first call returns arrays to the pool.
    /// </summary>
    internal void ReturnChannelData()
    {
        if (_channelsReturned) return;
        _channelsReturned = true;

        for (var c = 0; c < data.Length; c++)
        {
            if (data[c] is { } channel)
            {
                Array2DPool<float>.Return(channel);
            }
        }
    }

    ~Image()
    {
        if (!_channelsReturned)
        {
            Interlocked.Increment(ref _finalizerReturnCount);
        }
        ReturnChannelData();
    }

    private static long _finalizerReturnCount;

    /// <summary>Number of times the finalizer had to return channels (missed eager returns).</summary>
    public static long FinalizerReturnCount => Volatile.Read(ref _finalizerReturnCount);

    /// <summary>
    /// calculate image pixel value on subpixel level
    /// </summary>
    /// <param name="x1"></param>
    /// <param name="y1"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private float SubpixelValue(int channel, float x1, float y1)
    {
        var channelData = data[channel];
        var width = Width;
        var height = Height;

        // assumes that maxVal < long.MaxValue
        var x_trunc = (long)MathF.Truncate(x1);
        var y_trunc = (long)MathF.Truncate(y1);

        if (x_trunc < 0 || x_trunc >= width || y_trunc < 0 || y_trunc >= height)
        {
            return float.NaN;
        }
        else if (x_trunc == x1 && y_trunc == y1)
        {
            return channelData[y_trunc, x_trunc];
        }

        var x_frac = x1 - x_trunc;
        var y_frac = y1 - y_trunc;
        try
        {
            const int tl = 0;
            const int tr = 1;
            const int bl = 2;
            const int br = 3;

            byte mask = 0;
            Span<float> pixels = stackalloc float[4];
            pixels.Fill(float.NaN);

            pixels[tl] = channelData[y_trunc, x_trunc];
            if (x_trunc < width - 1)
            {
                pixels[tr] = channelData[y_trunc, x_trunc + 1];
            }

            if (y_trunc < height - 1)
            {
                pixels[bl] = channelData[y_trunc + 1, x_trunc];
            }

            if (x_trunc < width - 1 && y_trunc < height - 1)
            {
                pixels[br] = channelData[y_trunc + 1, x_trunc + 1];
            }

            for (var i = 0; i < 4; i++)
            {
                if (!float.IsNaN(pixels[i]))
                {
                    mask |= (byte)(1 << i);
                }
            }

            if ((mask & 0b1111) == 0b1111)
            {
                return pixels[tl] * (1 - x_frac) * (1 - y_frac)
                    + pixels[tr] * x_frac * (1 - y_frac)
                    + pixels[bl] * (1 - x_frac) * y_frac
                    + pixels[br] * x_frac * y_frac;
            }
            else
            {
                int main;
                if (x_frac <= 0.5f && y_frac <= 0.5f)
                {
                    main = tl;
                }
                else if (x_frac > 0.5f && y_frac <= 0.5f)
                {
                    main = tr;
                }
                else if (x_frac <= 0.5f && y_frac > 0.5f)
                {
                    main = bl;
                }
                else
                {
                    main = br;
                }

                // if the main pixel is not lit, return NaN
                if ((mask & (1 << main)) == (1 << main))
                {
                    return pixels[main];
                }
                // for now, return NaN if any non-main pixel is NaN, a better approach would be to interpolate using only the available pixels
                else
                {
                    return float.NaN;
                }
            }
        }
        catch (Exception ex) when (Environment.UserInteractive)
        {
            GC.KeepAlive(ex);
            throw;
        }
        catch
        {
            return float.NaN;
        }
    }

    /// <summary>
    /// Scales the floating-point values of the image data to a specified maximum value.
    /// </summary>
    /// <param name="missingValue">Use this value for missing pixels</param>
    /// <remarks>This method is intended for images that have been obtained via <see cref="ScaleFloatValuesToUnit"/> floating-point data (i.e., Float32 bit
    /// depth and a maximum value of 1.0). If the image is already denormalized or uses a different bit depth, no
    /// scaling is performed.</remarks>
    /// <param name="newMaxValue">The new maximum value to which the floating-point values will be scaled. Must be greater than zero.</param>
    /// <returns>An Image instance containing the denormalized data, with values scaled to the specified maximum value. If the
    /// image is already denormalized or not in Float32 bit depth, the original image is returned unchanged.</returns>
    public Image ScaleFloatValues(float newMaxValue, float missingValue = float.NaN)
    {
        if (BitDepth != BitDepth.Float32 || (newMaxValue != MaxValue && MaxValue > 1.0f + float.Epsilon))
        {
            return ScaleFloatValuesToUnit().ScaleFloatValues(newMaxValue);
        }

        var (channelCount, width, height) = Shape;
        var denormalized = CreateChannelData(channelCount, height, width);

        for (var c = 0; c < channelCount; c++)
        {
            var src = MemoryMarshal.CreateReadOnlySpan(ref data[c][0, 0], data[c].Length);
            var dst = MemoryMarshal.CreateSpan(ref denormalized[c][0, 0], denormalized[c].Length);
            MultiplyScalar(src, newMaxValue, dst);
        }

        return new Image(denormalized, BitDepth.Float32, newMaxValue, minValue * newMaxValue, pedestal * newMaxValue, imageMeta);
    }

    /// <summary>
    /// Divides image by <see cref="MaxValue"/>, thus scaling the floating-point values to a maximum of 1.0.
    /// </summary>
    /// <param name="missingValue">Use this value for missing pixels</param>
    /// <returns></returns>
    public Image ScaleFloatValuesToUnit(float missingValue = float.NaN)
    {
        // NO-OP for already normalized images
        if (MaxValue <= 1.0f)
        {
            return this;
        }

        var (channelCount, width, height) = Shape;
        var normalized = CreateChannelData(channelCount, height, width);
        var invMax = 1.0f / MaxValue;

        for (var c = 0; c < channelCount; c++)
        {
            var src = MemoryMarshal.CreateReadOnlySpan(ref data[c][0, 0], data[c].Length);
            var dst = MemoryMarshal.CreateSpan(ref normalized[c][0, 0], normalized[c].Length);
            MultiplyScalar(src, invMax, dst);
        }

        return new Image(normalized, BitDepth.Float32, 1.0f, minValue / maxValue, pedestal / maxValue, imageMeta);
    }

    /// <summary>
    /// In-place version of <see cref="ScaleFloatValuesToUnit"/>: divides all pixel values by <see cref="MaxValue"/>,
    /// mutating the underlying channel arrays. Returns a new <see cref="Image"/> wrapping the same arrays.
    /// </summary>
    /// <remarks>Internal only — callers must ensure the source image is not retained elsewhere.</remarks>
    internal Image ScaleFloatValuesToUnitInPlace(float missingValue = float.NaN)
    {
        if (MaxValue <= 1.0f)
        {
            return this;
        }

        var invMax = 1.0f / MaxValue;

        for (var c = 0; c < ChannelCount; c++)
        {
            // NaN * invMax = NaN, so NaN values are preserved without branching.
            var span = MemoryMarshal.CreateSpan(ref data[c][0, 0], data[c].Length);
            MultiplyScalar(span, invMax, span);
        }

        return new Image(data, BitDepth.Float32, 1.0f, minValue / maxValue, pedestal / maxValue, imageMeta);
    }
}
