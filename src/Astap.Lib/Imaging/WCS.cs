using nom.tam.fits;

namespace Astap.Lib.Imaging;

public record struct WCS(double CenterRA, double CenterDec)
{
    public static WCS? FromFits(Fits fits)
    {
        var hdu = fits.ReadHDU();
        if (hdu?.Header is Header header)
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
