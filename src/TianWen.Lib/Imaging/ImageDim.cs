namespace TianWen.Lib.Imaging;

public record struct ImageDim(
    double PixelScale, // arcsec per pixel
    int Width, // pixel
    int Height /* pixel */)
{
    const double ArcSecToDeg = 1.0 / 60.0 * 1.0 / 60.0;

    /// <summary>
    /// Returns field of view in degrees
    /// </summary>
    public readonly (double width, double height) FieldOfView => (ArcSecToDeg * PixelScale * Width, ArcSecToDeg * PixelScale * Height);
}
