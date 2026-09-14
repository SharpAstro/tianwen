namespace TianWen.Lib.Imaging;

/// <summary>
/// One colour of a Bayer mosaic, for a statistic taken on the mosaic IN PLACE: the histogram walks
/// that colour's photosites and nothing else, so no sub-plane is ever materialised for it.
/// </summary>
/// <remarks>
/// Green is the two diagonal phases together -- one histogram over both, which is the exact green
/// statistic, not the mean of two half-plane ones. Which photosites are which follows
/// <see cref="ImageMeta.BayerOffsetX"/> / <see cref="ImageMeta.BayerOffsetY"/>, the same parity
/// convention <see cref="Image.SplitBayerChannels"/> states, so every Bayer pattern is covered.
/// The numeric values are the R, G, B slot a mosaic's statistics occupy.
/// </remarks>
public enum CfaChannel
{
    Red = 0,
    Green = 1,
    Blue = 2,
}
