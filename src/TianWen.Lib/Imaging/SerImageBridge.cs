using System;
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

        var (sensor, ox, oy) = reader.ColorId.ToSensorType();
        int w = reader.Width, h = reader.Height;
        var raw = new ushort[reader.SamplesPerFrame];
        reader.ReadFrame16(index, raw);
        var scale = 1f / reader.MaxSampleValue;
        var meta = new ImageMeta { SensorType = sensor, BayerOffsetX = ox, BayerOffsetY = oy };

        if (reader.ColorId.IsColor)
        {
            var bgr = reader.ColorId == SerColorId.Bgr;
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
