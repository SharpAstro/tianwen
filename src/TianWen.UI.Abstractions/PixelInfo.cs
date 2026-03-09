namespace TianWen.UI.Abstractions;

/// <summary>
/// Information about a pixel at a given position, including sky coordinates if plate-solved.
/// </summary>
/// <param name="X">Pixel X (0-based).</param>
/// <param name="Y">Pixel Y (0-based).</param>
/// <param name="Values">Per-channel pixel values at (X, Y).</param>
/// <param name="RA">RA in hours if plate-solved, otherwise <c>null</c>.</param>
/// <param name="Dec">Dec in degrees if plate-solved, otherwise <c>null</c>.</param>
public record struct PixelInfo(int X, int Y, float[] Values, double? RA, double? Dec);
