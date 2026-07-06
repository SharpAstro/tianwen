using System;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Splits a single-channel Bayer CFA mosaic into its four colour sub-planes, each at half
    /// resolution (<c>Width/2 x Height/2</c>). Channels are returned in the fixed order
    /// <c>[R, G1, G2, B]</c>, where <c>G1</c> is the green sharing red's row and <c>G2</c> the green
    /// sharing blue's row. The CFA position of red is taken from <see cref="ImageMeta.BayerOffsetX"/> /
    /// <see cref="ImageMeta.BayerOffsetY"/> (the parity convention used throughout the codebase: red sits
    /// at <c>(x, y)</c> where <c>(x - offsetX)</c> and <c>(y - offsetY)</c> are both even).
    /// <para>
    /// The returned image keeps <see cref="SensorType.RGGB"/> + the offsets so the planes can be
    /// reassembled later, but its channels are the four CFA sub-planes -- it is NOT an RGB image. This is
    /// the "split" half of the split-CFA / Bayer-drizzle lucky-imaging seam (the deep-sky plan's reserved
    /// item K): each photosite colour is aligned + stacked independently, and the integrated CFA is
    /// demosaiced exactly once after stacking. An odd final row/column (an odd <see cref="Width"/> /
    /// <see cref="Height"/>) is dropped -- a CFA mosaic is even-sized by construction.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The image is not a single-channel Bayer mosaic.</exception>
    public Image SplitBayerChannels()
    {
        if (ChannelCount != 1 || imageMeta.SensorType is not SensorType.RGGB)
        {
            throw new InvalidOperationException(
                $"SplitBayerChannels requires a single-channel Bayer mosaic (SensorType.RGGB); got {ChannelCount} channel(s), {imageMeta.SensorType}.");
        }

        var ox = imageMeta.BayerOffsetX & 1;
        var oy = imageMeta.BayerOffsetY & 1;
        var pw = Width / 2;
        var ph = Height / 2;
        var planes = CreateChannelData(4, ph, pw); // [R, G1, G2, B]
        var src = channels[0].Data;
        float[,] r = planes[0], g1 = planes[1], g2 = planes[2], b = planes[3];

        for (var sy = 0; sy < ph; sy++)
        {
            var yR = (sy * 2) + oy;       // red / G1 row (yp == 0)
            var yB = (sy * 2) + (1 - oy); // blue / G2 row (yp == 1)
            for (var sx = 0; sx < pw; sx++)
            {
                var xR = (sx * 2) + ox;       // red / G2 column (xp == 0)
                var xB = (sx * 2) + (1 - ox); // blue / G1 column (xp == 1)
                r[sy, sx] = src[yR, xR];
                g1[sy, sx] = src[yR, xB];
                g2[sy, sx] = src[yB, xR];
                b[sy, sx] = src[yB, xB];
            }
        }

        // Reuse the source metadata verbatim: SensorType.RGGB + the offsets stay so a later merge knows
        // the original pattern; the channel layout is the documented [R, G1, G2, B] sub-plane contract.
        return new Image(planes, BitDepth.Float32, MaxValue, MinValue, Pedestal, imageMeta);
    }

    /// <summary>
    /// Reassembles a four-channel <c>[R, G1, G2, B]</c> CFA sub-plane image (as produced by
    /// <see cref="SplitBayerChannels"/> and then independently stacked) back into a single full-resolution
    /// Bayer mosaic, placing each sub-plane sample at its CFA photosite per the image's
    /// <see cref="ImageMeta.BayerOffsetX"/> / <see cref="ImageMeta.BayerOffsetY"/>. The result is a
    /// single-channel <see cref="SensorType.RGGB"/> mosaic (<c>2*width x 2*height</c>) ready for a single
    /// final demosaic -- the "merge" half of the split-CFA stacking seam.
    /// </summary>
    /// <exception cref="InvalidOperationException">The image is not a four-channel Bayer sub-plane set.</exception>
    public Image MergeBayerChannels()
    {
        if (ChannelCount != 4 || imageMeta.SensorType is not SensorType.RGGB)
        {
            throw new InvalidOperationException(
                $"MergeBayerChannels requires a four-channel Bayer sub-plane image (SensorType.RGGB); got {ChannelCount} channel(s), {imageMeta.SensorType}.");
        }

        var ox = imageMeta.BayerOffsetX & 1;
        var oy = imageMeta.BayerOffsetY & 1;
        var pw = Width;
        var ph = Height;
        var mosaic = CreateChannelData(1, ph * 2, pw * 2);
        var dst = mosaic[0];
        float[,] r = channels[0].Data, g1 = channels[1].Data, g2 = channels[2].Data, b = channels[3].Data;

        for (var sy = 0; sy < ph; sy++)
        {
            var yR = (sy * 2) + oy;
            var yB = (sy * 2) + (1 - oy);
            for (var sx = 0; sx < pw; sx++)
            {
                var xR = (sx * 2) + ox;
                var xB = (sx * 2) + (1 - ox);
                dst[yR, xR] = r[sy, sx];
                dst[yR, xB] = g1[sy, sx];
                dst[yB, xR] = g2[sy, sx];
                dst[yB, xB] = b[sy, sx];
            }
        }

        return new Image(mosaic, BitDepth.Float32, MaxValue, MinValue, Pedestal, imageMeta);
    }
}
