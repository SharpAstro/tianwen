namespace TianWen.Lib.Imaging;

/// <summary>
/// Information about a pixel at a given position, including sky coordinates if plate-solved.
/// </summary>
/// <param name="X">Pixel X (0-based).</param>
/// <param name="Y">Pixel Y (0-based).</param>
/// <param name="Values">Per-channel pixel values at (X, Y).</param>
/// <param name="RA">RA in hours if plate-solved, otherwise <c>null</c>.</param>
/// <param name="Dec">Dec in degrees if plate-solved, otherwise <c>null</c>.</param>
public record struct PixelInfo(int X, int Y, float[] Values, double? RA, double? Dec)
{
    /// <summary>
    /// What a value of 1 stands for in the units the source recorded (255 for an 8-bit capture, the divisor a
    /// 16-bit frame was scaled by), or <c>null</c> where the samples were on [0, 1] to begin with and there is
    /// no recorded count to quote. A readout multiplies by it; it never assumes 65535, which named an 8-bit
    /// capture's 74 as 19018.
    /// </summary>
    public float? FullScaleAdu { get; init; }
}
