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
    /// <para><b>All four Bayer patterns work.</b> <see cref="SensorType.RGGB"/> is the enum's name for
    /// "this is a Bayer CFA", NOT a claim that the pattern is literally RGGB: the rotation rides on the
    /// offsets, which <see cref="SensorTypeEx"/> sets from the file's own BAYERPAT (RGGB 0,0 / GRBG 1,0 /
    /// GBRG 0,1 / BGGR 1,1). Verified on a GRBG SV605CC frame: with the mapped offsets the two green
    /// sub-planes agree to 0.00%, and read as plain RGGB they disagree by 38.4% -- which is the cheap
    /// test for whether any pattern assignment is right.</para>
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
                $"SplitBayerChannels requires a single-channel Bayer CFA mosaic; got {ChannelCount} channel(s), "
                + $"{imageMeta.SensorType}. Every Bayer PATTERN is supported -- SensorType.RGGB names the CFA, "
                + "and GRBG / GBRG / BGGR arrive as that type with the matching BayerOffsetX/Y.");
        }

        var ox = imageMeta.BayerOffsetX & 1;
        var oy = imageMeta.BayerOffsetY & 1;
        var pw = Width / 2;
        var ph = Height / 2;
        var planes = CreateChannelData(4, ph, pw); // [R, G1, G2, B]
        var src = Planes[0].Data;
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
    /// A single-channel Bayer CFA mosaic: the frame a <see cref="CfaChannel"/> statistic is taken on.
    /// </summary>
    public bool IsCfaMosaic => ChannelCount == 1 && imageMeta.SensorType is SensorType.RGGB;

    /// <summary>
    /// Where each traversal of a CFA colour starts on this mosaic, as (row, column): red and blue are
    /// ONE phase each, green is TWO (it shares red's row and blue's row), and <c>null</c> -- no CFA
    /// colour -- is the plain frame from (0, 0). Parity is relative to <see cref="ImageMeta.BayerOffsetX"/>
    /// / <see cref="ImageMeta.BayerOffsetY"/>, the convention <see cref="SplitBayerChannels"/> states,
    /// so the four Bayer patterns are one arithmetic. Returns how many starts were written.
    /// </summary>
    /// <remarks>
    /// This is what lets a statistic be taken per colour WITHOUT <see cref="SplitBayerChannels"/>: the
    /// histogram wants a traversal of the photosites, not a copy of them, and the split would write a
    /// quarter-frame per colour to be read back exactly once.
    /// </remarks>
    internal int CfaPhaseStarts(CfaChannel? cfa, Span<(int Row, int Col)> starts)
    {
        if (cfa is null)
        {
            starts[0] = (0, 0);
            return 1;
        }
        if (!IsCfaMosaic)
        {
            throw new InvalidOperationException(
                $"A {cfa} statistic needs a single-channel Bayer CFA mosaic; got {ChannelCount} channel(s), {imageMeta.SensorType}.");
        }

        var ox = imageMeta.BayerOffsetX & 1;
        var oy = imageMeta.BayerOffsetY & 1;
        switch (cfa)
        {
            case CfaChannel.Red:
                starts[0] = (oy, ox);
                return 1;
            case CfaChannel.Blue:
                starts[0] = (1 - oy, 1 - ox);
                return 1;
            default:
                starts[0] = (oy, 1 - ox);     // G1: red's row, blue's column
                starts[1] = (1 - oy, ox);     // G2: blue's row, red's column
                return 2;
        }
    }

    /// <summary>
    /// The step a traversal takes: a CFA phase is a parity, and an odd stride would walk off it, so
    /// an odd stride is doubled for a CFA colour. The plain frame keeps the stride as given.
    /// </summary>
    internal static int CfaStep(CfaChannel? cfa, int stride)
        => cfa is null || (stride & 1) == 0 ? stride : stride * 2;

    /// <summary>
    /// One channel of this image as a standalone single-channel <see cref="Image"/>, for handing a
    /// single CFA sub-plane to something that takes whole images -- a star remover, in particular,
    /// which must see one smooth plane rather than an interleaved mosaic.
    ///
    /// <remarks>
    /// The plane array is SHARED, not copied: the view is for reading, and a copy of a half-frame
    /// per call would be pure waste on a path that already runs once per light. Because it shares,
    /// the view carries NO channel buffer -- release responsibility stays with this image, which is
    /// the same rule a rewrap like <see cref="ScaleFloatValuesToUnitInPlace"/> follows. Do not
    /// release the returned image.
    /// </remarks>
    /// </summary>
    /// <param name="index">Channel to expose; for a split mosaic, 0=R, 1=G1, 2=G2, 3=B.</param>
    internal Image AsSingleChannel(int index)
        => new([GetChannelArray(index)], BitDepth, MaxValue, MinValue, Pedestal, imageMeta);

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
                $"MergeBayerChannels requires a four-channel Bayer sub-plane image; got {ChannelCount} channel(s), "
                + $"{imageMeta.SensorType}. As with the split, SensorType.RGGB names the CFA rather than the pattern.");
        }

        var ox = imageMeta.BayerOffsetX & 1;
        var oy = imageMeta.BayerOffsetY & 1;
        var pw = Width;
        var ph = Height;
        var mosaic = CreateChannelData(1, ph * 2, pw * 2);
        var dst = mosaic[0];
        float[,] r = Planes[0].Data, g1 = Planes[1].Data, g2 = Planes[2].Data, b = Planes[3].Data;

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
