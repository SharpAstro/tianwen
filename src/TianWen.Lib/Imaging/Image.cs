using System;
using System.Collections.Immutable;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Multi-channel image: an immutable view over per-channel <see cref="Channel"/>s (each carrying
/// its own plane, filter, min/max, and optional ref-counted camera buffer) plus <see cref="ImageMeta"/>.
/// The image-wide <see cref="MaxValue"/>/<see cref="MinValue"/> are derived across the channels;
/// the raw-array constructor overload wraps legacy <c>float[][,]</c> call sites.
/// </summary>
public partial class Image(ImmutableArray<Channel> channels, BitDepth bitDepth, float pedestal, ImageMeta imageMeta)
{
    public int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = ValidateSameShape(channels)[0].Width;

    public int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = channels[0].Height;

    public int ChannelCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get;
    } = channels.Length;

    public (int ChannelCount, int Width, int Height) Shape => (ChannelCount, Width, Height);

    public BitDepth BitDepth => bitDepth;

    /// <summary>
    /// Image-wide full-scale value, derived as the maximum over the channels' <see cref="Channel.MaxValue"/>.
    /// Legacy raw-array constructions stamp the same image-wide value on every channel, so this reads
    /// back exactly what was passed in; channel-typed constructions keep per-channel maxima intact
    /// (reachable via <see cref="GetChannel"/>).
    /// </summary>
    public float MaxValue { get; } = DeriveMax(channels);

    /// <summary>Image-wide minimum, derived as the minimum over the channels' <see cref="Channel.MinValue"/>.</summary>
    public float MinValue { get; } = DeriveMin(channels);

    /// <summary>
    /// Legacy raw-array overload: wraps each plane in a <see cref="Channel"/> carrying the
    /// image-wide <paramref name="maxValue"/>/<paramref name="minValue"/> (no per-channel stats,
    /// no buffers). Prefer the <see cref="ImmutableArray{T}"/>-of-<see cref="Channel"/> constructor
    /// for new code — it keeps per-channel min/max and lets a camera buffer travel with its channel.
    /// </summary>
    public Image(float[][,] data, BitDepth bitDepth, float maxValue, float minValue, float pedestal, ImageMeta imageMeta)
        : this(WrapRawPlanes(data, minValue, maxValue), bitDepth, pedestal, imageMeta)
    {
    }

    private static ImmutableArray<Channel> WrapRawPlanes(float[][,] data, float minValue, float maxValue)
    {
        var builder = ImmutableArray.CreateBuilder<Channel>(data.Length);
        for (var c = 0; c < data.Length; c++)
        {
            builder.Add(new Channel(data[c], default, minValue, maxValue, (byte)c));
        }
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<Channel> ValidateSameShape(ImmutableArray<Channel> channels)
    {
        if (channels.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An image needs at least one channel.", nameof(channels));
        }
        var (h, w) = (channels[0].Height, channels[0].Width);
        for (var c = 1; c < channels.Length; c++)
        {
            if (channels[c].Height != h || channels[c].Width != w)
            {
                throw new ArgumentException(
                    $"Channel {c} is {channels[c].Width}x{channels[c].Height} but channel 0 is {w}x{h}.", nameof(channels));
            }
        }
        return channels;
    }

    // MathF.Max/Min propagate NaN, preserving the legacy behaviour where an image constructed
    // with maxValue = float.NaN (e.g. FromChannel's default) reads MaxValue = NaN.
    private static float DeriveMax(ImmutableArray<Channel> channels)
    {
        var max = channels[0].MaxValue;
        for (var c = 1; c < channels.Length; c++)
        {
            max = MathF.Max(max, channels[c].MaxValue);
        }
        return max;
    }

    private static float DeriveMin(ImmutableArray<Channel> channels)
    {
        var min = channels[0].MinValue;
        for (var c = 1; c < channels.Length; c++)
        {
            min = MathF.Min(min, channels[c].MinValue);
        }
        return min;
    }
    /// <summary>
    /// ADU pedestal added to pixel values to keep them non-negative after
    /// calibration subtraction (see <see cref="ImageMeta"/> remarks for the
    /// OFFSET / PEDESTAL / BZERO distinction). 0 for raw frames; the
    /// calibration <see cref="Subtract"/> path accumulates the user-supplied
    /// offset here so downstream stretch / stats can subtract it back out.
    /// </summary>
    public float Pedestal => pedestal;
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
            var pixelScale = Astrometry.CoordinateUtils.PixelScaleArcsec(meta.PixelSizeX * meta.BinX, meta.FocalLength);
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
    public float this[int c, int h, int w] => channels[c].Data[h, w];

    /// <summary>
    /// Returns the typed <see cref="Channel"/> for a plane — per-channel filter/min/max travel here
    /// (the image-wide <see cref="MaxValue"/>/<see cref="MinValue"/> are the derived extrema).
    /// </summary>
    public Channel GetChannel(int channel) => channels[channel];

    /// <summary>
    /// Returns a flat span over the pixel data for a single channel plane (height * width floats).
    /// </summary>
    public ReadOnlySpan<float> GetChannelSpan(int channel)
    {
        var plane = channels[channel].Data;
        return MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
    }

    /// <summary>
    /// Returns the raw backing <c>float[,]</c> for a channel. Internal — use for low-level
    /// interop (guider tracker, FITS write) where span access is insufficient.
    /// </summary>
    internal float[,] GetChannelArray(int channel) => channels[channel].Data;

    /// <summary>
    /// Wraps a single mono <c>float[,]</c> channel in an <see cref="Image"/> with default metadata.
    /// Convenience for guider frames and test helpers.
    /// </summary>
    public static Image FromChannel(float[,] channel, float maxValue = float.NaN, float minValue = float.NaN)
        => new Image([channel], BitDepth.Float32, maxValue, minValue, 0f, new ImageMeta { SensorType = SensorType.Monochrome });

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
            channels[c] = new float[height, width];
        }
        return channels;
    }

    /// <summary>
    /// Ref-counted channel buffers, harvested from the channels' <see cref="Channel.Buffer"/> at
    /// construction — set when the image wraps camera-owned data (the buffer travels WITH its
    /// channel; there is no attach-after-construct step). Null for images whose channels own their
    /// arrays outright (debayer/normalize output, tests, file loads).
    /// </summary>
    private ChannelBuffer?[]? _channelBuffers = HarvestBuffers(channels);

    private static ChannelBuffer?[]? HarvestBuffers(ImmutableArray<Channel> channels)
    {
        ChannelBuffer?[]? buffers = null;
        for (var c = 0; c < channels.Length; c++)
        {
            if (channels[c].Buffer is { } buffer)
            {
                buffers ??= new ChannelBuffer?[channels.Length];
                buffers[c] = buffer;
            }
        }
        return buffers;
    }

    /// <summary>
    /// Releases all ref-counted channel buffers. When all holders release,
    /// the backing <c>float[,]</c> returns to the camera for reuse.
    /// Safe to call multiple times — idempotent.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _channelBuffers, null) is { } buffers)
        {
            for (var c = 0; c < buffers.Length; c++)
            {
                buffers[c]?.Release();
            }
        }
    }

    /// <summary>
    /// calculate image pixel value on subpixel level
    /// </summary>
    /// <param name="x1"></param>
    /// <param name="y1"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private float SubpixelValue(int channel, float x1, float y1)
    {
        var channelData = channels[channel].Data;
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
    /// Scales the floating-point values of the image data so the unit-space FULL SCALE (1.0) maps to
    /// <paramref name="newMaxValue"/>.
    /// </summary>
    /// <param name="missingValue">Use this value for missing pixels</param>
    /// <remarks>This method is intended for images that have been obtained via <see cref="ScaleFloatValuesToUnit"/> floating-point data (i.e., Float32 bit
    /// depth and values in [0, 1]). If the image is already denormalized or uses a different bit depth, no
    /// scaling is performed. Note the mapping is full-scale-to-full-scale, not peak-to-peak: a
    /// full-scale-normalised image (see <see cref="UnitScaleDivisor"/>) whose observed peak sits below 1.0
    /// lands with its peak proportionally below <paramref name="newMaxValue"/> -- which round-trips the
    /// original ADU values, rather than stretching the frame's own peak to <paramref name="newMaxValue"/>.</remarks>
    /// <param name="newMaxValue">The value the unit-space full scale (1.0) is mapped to. Must be greater than zero.</param>
    /// <returns>An Image instance containing the denormalized data. If the
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
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref denormalized[c][0, 0], denormalized[c].Length);
            MultiplyScalar(src, newMaxValue, dst);
        }

        // The stamped MaxValue is the OBSERVED peak scaled by the same factor as the pixels (for a
        // legacy peak-normalised input MaxValue == 1 this is exactly newMaxValue, as before).
        return new Image(denormalized, BitDepth.Float32, MaxValue * newMaxValue, MinValue * newMaxValue, pedestal * newMaxValue, RescaleMeta(newMaxValue));
    }

    /// <summary>
    /// Divides image by the sensor's fixed ADU full-scale when known (<see cref="ImageMeta.SensorFullScaleAdu"/>),
    /// otherwise by <see cref="MaxValue"/> -- scaling the floating-point values into <c>[0, 1]</c>. A live
    /// camera capture normalises against its sensor's true saturation point rather than its own observed
    /// peak, so an under-exposed frame correctly lands below 1.0 instead of always stretching its own max
    /// to exactly 1.0; a source without that metadata (file imports, calibration masters, ...) falls back
    /// to the prior observed-peak behaviour unchanged.
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
        var invMax = 1.0f / UnitScaleDivisor;

        for (var c = 0; c < channelCount; c++)
        {
            var src = GetChannelSpan(c);
            var dst = MemoryMarshal.CreateSpan(ref normalized[c][0, 0], normalized[c].Length);
            MultiplyScalar(src, invMax, dst);
        }

        return new Image(normalized, BitDepth.Float32, MaxValue * invMax, MinValue * invMax, pedestal * invMax, RescaleMeta(invMax));
    }

    /// <summary>
    /// The divisor the canonical [0, 1] normalisation uses: the sensor's fixed ADU full-scale when
    /// known (<see cref="ImageMeta.SensorFullScaleAdu"/>, e.g. from a live camera's MaxADU or a FITS
    /// SATURATE card) so the conversion is stable across frames, otherwise the observed peak
    /// (<see cref="MaxValue"/>). Never below the observed peak -- a hot pixel or calibration artifact
    /// above the nominal full-scale must not map above 1.0. Single source of truth shared by
    /// <see cref="ScaleFloatValuesToUnit"/>, <see cref="ScaleFloatValuesToUnitInPlace"/>, and the
    /// TIFF export normalisation (a private divisor in any one of them drifts out of agreement with
    /// the others -- the TiffRoundTripTests comparison is the regression guard).
    /// </summary>
    internal float UnitScaleDivisor => imageMeta.SensorFullScaleAdu is { } adu ? MathF.Max(adu, MaxValue) : MaxValue;

    /// <summary>
    /// Convenience over <see cref="ImageMeta.Rescale"/> (the single implementation): rescales the
    /// scale-dependent metadata by the same factor applied to the pixel values. Without this,
    /// writing a normalised image to FITS would stamp a stale ADU-scale SATURATE against [0,1] data.
    /// </summary>
    private ImageMeta RescaleMeta(float pixelScaleFactor)
    {
        return imageMeta.Rescale(pixelScaleFactor);
    }

    /// <summary>
    /// In-place version of <see cref="ScaleFloatValuesToUnit"/>: divides all pixel values by the sensor's
    /// fixed ADU full-scale when known, otherwise by <see cref="MaxValue"/> (see <see cref="ScaleFloatValuesToUnit"/>),
    /// mutating the underlying channel arrays. Returns a new <see cref="Image"/> wrapping the same arrays.
    /// </summary>
    /// <remarks>Internal only — callers must ensure the source image is not retained elsewhere.</remarks>
    internal Image ScaleFloatValuesToUnitInPlace(float missingValue = float.NaN)
    {
        if (MaxValue <= 1.0f)
        {
            return this;
        }

        var invMax = 1.0f / UnitScaleDivisor;

        for (var c = 0; c < ChannelCount; c++)
        {
            // NaN * invMax = NaN, so NaN values are preserved without branching.
            var plane = channels[c].Data;
            var span = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
            MultiplyScalar(span, invMax, span);
        }

        // Rewrap the SAME arrays with rescaled per-channel min/max. Buffer deliberately NOT
        // carried over: the ref-counted release responsibility stays with the original Image
        // (callers treat the source as consumed but its Release() still owns the recycle) —
        // carrying the ref here would double-release a refcount-1 buffer.
        var rescaled = ImmutableArray.CreateBuilder<Channel>(channels.Length);
        foreach (var channel in channels)
        {
            rescaled.Add(channel with
            {
                MinValue = channel.MinValue * invMax,
                MaxValue = channel.MaxValue * invMax,
                Buffer = null,
            });
        }

        return new Image(rescaled.MoveToImmutable(), BitDepth.Float32, pedestal * invMax, RescaleMeta(invMax));
    }
}
