using nom.tam.fits;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Represents world coordinates in J2000, but with <paramref name="CenterRA"/> in 24h.
/// </summary>
/// <param name="CenterRA">J2000.0 RA of the center pixel in 0..24h</param>
/// <param name="CenterDec">J2000.0 DEC of the center pixel in -90..+90 degrees</param>
public record struct WCS(double CenterRA, double CenterDec)
{
    /// <summary>
    /// Extract header values from FITS headers if available.
    /// </summary>
    /// <param name="fits"></param>
    /// <returns></returns>
    public static WCS? FromFits(Fits fits)
    {
        var hdu = fits.ReadHDU();
        if (hdu?.Header is { } header)
        {
            var ra = header.GetDoubleValue("CRVAL1");
            var dec = header.GetDoubleValue("CRVAL2");

            if (!double.IsNaN(ra) && !double.IsNaN(dec))
            {
                return new WCS(ra / 15.0, dec);
            }
        }

        return default;
    }
}
