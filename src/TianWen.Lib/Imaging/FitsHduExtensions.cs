using nom.tam.fits;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Finding the HDU that actually carries the image.
/// </summary>
/// <remarks>
/// <para>A FITS file does not have to put its image in the primary HDU, and a great many do
/// not. Anything <c>fpack</c> compressed (<c>.fz</c>) <b>cannot</b>: the tile-compressed image
/// is a binary table, which is only legal as an extension, so such a file always opens with an
/// empty primary (<c>NAXIS = 0</c>) and carries the image in HDU 1. Ordinary multi-extension
/// FITS from survey archives and from other capture software does the same by choice.</para>
///
/// <para>Reading HDU 0 and giving up if it holds no array therefore rejects a valid file for a
/// reason that has nothing to do with its contents. Every reader here walks instead.</para>
///
/// <para>This is NOT the right thing everywhere: a plate solver's <c>.wcs</c> output is a
/// header with <c>NAXIS = 0</c> and no data at all, and reading it means reading exactly that
/// first header. <see cref="Astrometry.WCS.FromFits"/> keeps its single <c>ReadHDU</c> for that
/// reason.</para>
/// </remarks>
public static class FitsHduExtensions
{
    extension(Fits fits)
    {
        /// <summary>Read forward to the first HDU carrying an image array, skipping HDUs that
        /// hold no data (the empty primary of a compressed file) or hold something other than an
        /// image (a catalogue table). Returns null at end of file.</summary>
        /// <remarks>Reading forward is cheap: on a seekable stream an image HDU's pixels are
        /// skipped rather than read, and a tile-compressed one defers its tiles the same way, so
        /// the walk costs one header parse per HDU passed over.</remarks>
        public BasicHDU? ReadFirstImageHdu() => FindFirstImageHdu(fits, headerOnly: false);

        /// <summary>As <see cref="ReadFirstImageHdu"/>, but skipping the data block of every HDU
        /// it passes, for callers that only want the header.</summary>
        public BasicHDU? ReadFirstImageHduHeaderOnly() => FindFirstImageHdu(fits, headerOnly: true);
    }

    private static BasicHDU? FindFirstImageHdu(Fits fits, bool headerOnly)
    {
        while ((headerOnly ? fits.ReadHDUHeaderOnly() : fits.ReadHDU()) is { } hdu)
        {
            // Axes is null for NAXIS = 0. A tile-compressed image satisfies both tests, since
            // FITS.Lib 5.0 surfaces it as an ImageHDU whose header has been translated back into
            // the image's own BITPIX / NAXIS / NAXISn.
            if (hdu is ImageHDU && hdu.Axes?.Length > 0)
            {
                return hdu;
            }
        }

        return null;
    }
}
