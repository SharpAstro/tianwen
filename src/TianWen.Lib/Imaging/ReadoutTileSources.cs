using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Picks the <see cref="IReadoutTileSource"/> for a file by its format (#1856). A plain FITS
/// (<see cref="FitsReadoutTiles"/>) and a tile-compressed <c>.fz</c> (<see cref="FpackReadoutTiles"/>) today; a format
/// that can answer one sample without its whole image (a TIFF, stripped or tiled, #1853) adds its own source to the list here and
/// nothing else changes.
/// </summary>
public static class ReadoutTileSources
{
    /// <summary>
    /// Opens a source for <paramref name="path"/>, or answers false when no source serves its format (gzip,
    /// CR2 today: neither can be read at an offset), and the caller keeps reading the planes.
    /// </summary>
    /// <param name="path">The file the document was opened from.</param>
    /// <param name="unitScale">The factor the import multiplied every sample by, or null where it scaled nothing.</param>
    /// <param name="source">The source, or null.</param>
    public static bool TryOpen(string path, float? unitScale, [NotNullWhen(true)] out IReadoutTileSource? source)
    {
        if (FitsReadoutTiles.TryOpen(path, unitScale, out var fits))
        {
            source = fits;
            return true;
        }

        if (FpackReadoutTiles.TryOpen(path, unitScale, out var fpack))
        {
            source = fpack;
            return true;
        }

        if (TiffReadoutTiles.TryOpen(path, unitScale, out var tiff))
        {
            source = tiff;
            return true;
        }

        source = null;
        return false;
    }
}
