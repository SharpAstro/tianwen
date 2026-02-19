using System;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging;

// track minValue and blackLevel (offset) independently
public partial class Image(float[,,] data, BitDepth bitDepth, float maxValue, float minValue, float blackLevel, ImageMeta imageMeta)
{
    public int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data.GetLength(2);

    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data.GetLength(1);

    public int ChannelCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = data.GetLength(0);

    public (int ChannelCount, int Width, int Height) Shape => (ChannelCount, Width, Height);

    public BitDepth BitDepth => bitDepth;
    public float MaxValue => maxValue;
    public float MinValue => minValue;
    /// <summary>
    /// Image metadata such as instrument, exposure time, focal length, pixel size, ...
    /// </summary>
    public ImageMeta ImageMeta => imageMeta;

    /// <summary>
    /// Read-only indexer to get a pixel value.
    /// </summary>
    /// <param name="h"></param>
    /// <param name="w"></param>
    /// <returns></returns>
    public float this[int c, int h, int w] => data[c, h, w];

    /// <summary>
    /// calculate image pixel value on subpixel level
    /// </summary>
    /// <param name="x1"></param>
    /// <param name="y1"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private float SubpixelValue(int channel, float x1, float y1)
    {
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
            return data[channel, y_trunc, x_trunc];
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

            pixels[tl] = data[channel, y_trunc, x_trunc];
            if (x_trunc < width - 1)
            {
                pixels[tr] = data[channel, y_trunc, x_trunc + 1];
            }

            if (y_trunc < height - 1)
            {
                pixels[bl] = data[channel, y_trunc + 1, x_trunc];
            }

            if (x_trunc < width - 1 && y_trunc < height - 1)
            {
                pixels[br] = data[channel, y_trunc + 1, x_trunc + 1];
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
        var denormalized = new float[channelCount, height, width];

        for (var c = 0; c < channelCount; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var value = data[c, y, x];
                    if (!float.IsNaN(value))
                    {
                        denormalized[c, y, x] = value * newMaxValue;
                    }
                    else
                    {
                        denormalized[c, y, x] = missingValue;
                    }
                }
            }
        }

        return new Image(denormalized, BitDepth.Float32, newMaxValue, minValue * newMaxValue, blackLevel * newMaxValue, imageMeta);
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
        var normalized = new float[channelCount, height, width];

        for (var c = 0; c < channelCount; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var value = data[c, y, x];
                    if (!float.IsNaN(value))
                    {
                        normalized[c, y, x] = value / MaxValue;
                    }
                    else
                    {
                        normalized[c, y, x] = missingValue;
                    }
                }
            }
        }

        return new Image(normalized, BitDepth.Float32, 1.0f, blackLevel / maxValue, blackLevel / maxValue, imageMeta);
    }
}