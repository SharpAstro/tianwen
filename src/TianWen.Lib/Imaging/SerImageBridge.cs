using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpAstro.Ser;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Bridges SER planetary-video frames (<c>SharpAstro.Ser</c>) into TianWen's imaging model: maps the
/// SER colour id to <see cref="SensorType"/> + Bayer offsets, and decodes a frame's raw samples to
/// unit-range [0,1] floats. <see cref="FillUnitFloat"/> is the playback hot path -- it fills
/// caller-owned reused buffers so per-frame upload allocates nothing and a Bayer mosaic is left for a
/// downstream (GPU/CPU) debayer; <see cref="ToImage"/> materialises a full <see cref="Image"/> only for
/// snapshot / export / stacking, never per playback frame.
/// </summary>
public static class SerImageBridge
{
    extension(SerColorId colorId)
    {
        /// <summary>
        /// Maps a SER colour id to TianWen's <see cref="SensorType"/> + Bayer CFA offset (x, y).
        /// RGB/BGR -> <see cref="SensorType.Color"/>; the RGGB family -> <see cref="SensorType.RGGB"/>
        /// with the matching offset; mono and the (unmodelled) CYGM family -> <see cref="SensorType.Monochrome"/>.
        /// </summary>
        public (SensorType Sensor, int BayerOffsetX, int BayerOffsetY) ToSensorType()
            => colorId.IsColor ? (SensorType.Color, 0, 0)
             : colorId.BayerOffset is { } off ? (SensorType.RGGB, off.X, off.Y)
             : (SensorType.Monochrome, 0, 0);
    }

    /// <summary>
    /// The SER colour id a frame of <paramref name="channels"/> channels with <paramref name="meta"/> is written as, the
    /// inverse of <see cref="ToSensorType"/>: three channels are RGB, a one-channel RGGB-family mosaic the Bayer mode its
    /// offsets name, anything else mono.
    /// </summary>
    public static SerColorId SerColorOf(in ImageMeta meta, int channels)
        => channels >= 3 ? SerColorId.Rgb
         : meta.SensorType != SensorType.RGGB ? SerColorId.Mono
         : ((meta.BayerOffsetX & 1) == 1, (meta.BayerOffsetY & 1) == 1) switch
         {
             (false, false) => SerColorId.BayerRGGB,
             (true, false) => SerColorId.BayerGRBG,
             (false, true) => SerColorId.BayerGBRG,
             (true, true) => SerColorId.BayerBGGR,
         };

    /// <summary>
    /// The SER sample depth <paramref name="frame"/> is written in: 8 for a frame read out in 8 bits (its samples 0 to 255),
    /// else 16, which a colour frame already in [0, 1] and a 16-bit readout both keep. One rule for a recording and a
    /// conversion.
    /// </summary>
    public static int SerDepthOf(Image frame) => frame.BitDepth is BitDepth.Int8 && !frame.SamplesAreUnitReferred ? 8 : 16;

    /// <summary>
    /// Writes <paramref name="frame"/>'s samples into <paramref name="destination"/> as a SER frame: whole numbers in
    /// <paramref name="depth"/> bits (<see cref="SerDepthOf"/>), host order, interleaved per pixel for RGB, and the top row
    /// first, so a frame whose rows are stored bottom-up (<see cref="RowOrder.BottomUp"/>) is turned over.
    /// <paramref name="destination"/> is exactly the frame: width x height x planes x depth / 8 bytes.
    /// </summary>
    public static void FillSerFrame(Image frame, int depth, Span<byte> destination)
    {
        var channels = frame.ChannelCount >= 3 ? 3 : 1;
        var (width, height) = (frame.Width, frame.Height);
        var bottomUp = frame.ImageMeta.RowOrder == RowOrder.BottomUp;
        if (depth == 8)
        {
            Fill(frame, channels, width, height, bottomUp, destination, byte.MaxValue);
        }
        else
        {
            Fill(frame, channels, width, height, bottomUp, MemoryMarshal.Cast<byte, ushort>(destination), ushort.MaxValue);
        }
    }

    // The samples as the camera gave them, whole numbers 0 to the depth's maximum ([0, 1] frames scaled up to it).
    private static void Fill<T>(Image frame, int channels, int width, int height, bool bottomUp, Span<T> samples, float max)
        where T : unmanaged, System.Numerics.INumberBase<T>
    {
        var scale = frame.SamplesAreUnitReferred ? max : 1f;
        for (var c = 0; c < channels; c++)
        {
            var plane = frame.GetChannelSpan(c);
            for (var y = 0; y < height; y++)
            {
                var from = plane.Slice((bottomUp ? height - 1 - y : y) * width, width);
                var to = y * width * channels;
                for (var x = 0; x < width; x++)
                {
                    samples[to + (x * channels) + c] = T.CreateTruncating(Math.Clamp(MathF.Round(from[x] * scale), 0f, max));
                }
            }
        }
    }

    /// <summary>
    /// Decodes frame <paramref name="index"/> into unit-range [0,1] float channels, filling the
    /// caller-supplied buffers with no allocation. Bayer/mono write ONE channel (the raw mosaic, left
    /// for the debayer); RGB/BGR write THREE de-interleaved channels in R,G,B order (BGR is swapped).
    /// Each <paramref name="channels"/> buffer must be <c>Width*Height</c> long; <paramref name="rawScratch"/>
    /// must be <see cref="SerReader.SamplesPerFrame"/> long.
    /// </summary>
    /// <returns>The number of channels written: 1 for mono/Bayer, 3 for RGB/BGR.</returns>
    public static int FillUnitFloat(SerReader reader, int index, Span<ushort> rawScratch, float[][] channels)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(channels);

        reader.ReadFrame16(index, rawScratch);
        var pixels = reader.Width * reader.Height;
        var scale = 1f / reader.MaxSampleValue;

        if (reader.ColorId.IsColor)
        {
            // Interleaved 3 planes per pixel. Rgb stores R,G,B; Bgr stores B,G,R -> swap to R,G,B.
            var bgr = reader.ColorId == SerColorId.Bgr;
            var rIdx = bgr ? 2 : 0;
            var bIdx = bgr ? 0 : 2;
            float[] r = channels[0], g = channels[1], b = channels[2];
            for (var p = 0; p < pixels; p++)
            {
                var s = p * 3;
                r[p] = rawScratch[s + rIdx] * scale;
                g[p] = rawScratch[s + 1] * scale;
                b[p] = rawScratch[s + bIdx] * scale;
            }

            return 3;
        }

        var mono = channels[0];
        for (var i = 0; i < pixels; i++)
        {
            mono[i] = rawScratch[i] * scale;
        }

        return 1;
    }

    /// <summary>
    /// Materialises frame <paramref name="index"/> as a full <see cref="Image"/> in [0,1] Float32,
    /// carrying the mapped <see cref="SensorType"/> + Bayer offsets (a Bayer frame stays a single-plane
    /// mosaic for a downstream debayer). For snapshot / export / stacking -- NOT the playback loop.
    /// </summary>
    public static Image ToImage(SerReader reader, int index)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var raw = new ushort[reader.SamplesPerFrame];
        reader.ReadFrame16(index, raw);
        return ToImage(raw, reader.ColorId, reader.Width, reader.Height, reader.MaxSampleValue);
    }

    /// <summary>
    /// The same materialisation from a frame that arrived as BYTES rather than through a
    /// <see cref="SerReader"/>: <paramref name="frame"/> is exactly one frame (<see cref="SerHeader.FrameSizeBytes"/>)
    /// in the file's own byte order and sample width. This is the path a consumer with only a stream
    /// takes, the Explorer thumbnail handler being the first (the shell hands it an <c>IStream</c>, and the
    /// memory-mapped reader wants a path). The sample decode mirrors <see cref="SerReader.ReadFrame16"/>:
    /// 8-bit widened, 16-bit byte-swapped to host order when the header says so.
    /// </summary>
    public static Image ToImage(in SerHeader header, ReadOnlySpan<byte> frame)
    {
        if (frame.Length != header.FrameSizeBytes)
        {
            throw new ArgumentException($"Expected exactly one frame of {header.FrameSizeBytes} bytes, got {frame.Length}.", nameof(frame));
        }

        var samples = new ushort[header.Width * header.Height * header.PlaneCount];
        if (header.BytesPerSample == 1)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = frame[i];
            }
        }
        else
        {
            var src16 = MemoryMarshal.Cast<byte, ushort>(frame);
            if (header.DataLittleEndian == BitConverter.IsLittleEndian)
            {
                src16[..samples.Length].CopyTo(samples);
            }
            else
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReverseEndianness(src16[i]);
                }
            }
        }

        return ToImage(samples, header.ColorId, header.Width, header.Height, header.MaxSampleValue);
    }

    private static Image ToImage(ReadOnlySpan<ushort> raw, SerColorId colorId, int w, int h, int maxSampleValue)
    {
        var (sensor, ox, oy) = colorId.ToSensorType();
        var scale = 1f / maxSampleValue;
        var meta = new ImageMeta { SensorType = sensor, BayerOffsetX = ox, BayerOffsetY = oy };

        if (colorId.IsColor)
        {
            var bgr = colorId == SerColorId.Bgr;
            int rIdx = bgr ? 2 : 0, bIdx = bgr ? 0 : 2;
            var data = Image.CreateChannelData(3, h, w);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var s = (((y * w) + x) * 3);
                    data[0][y, x] = raw[s + rIdx] * scale;
                    data[1][y, x] = raw[s + 1] * scale;
                    data[2][y, x] = raw[s + bIdx] * scale;
                }
            }

            return new Image(data, BitDepth.Float32, 1f, 0f, 0f, meta);
        }
        else
        {
            var data = Image.CreateChannelData(1, h, w);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    data[0][y, x] = raw[(y * w) + x] * scale;
                }
            }

            return new Image(data, BitDepth.Float32, 1f, 0f, 0f, meta);
        }
    }
}
