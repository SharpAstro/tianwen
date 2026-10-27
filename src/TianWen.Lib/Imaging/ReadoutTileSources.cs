using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Picks the <see cref="IReadoutTileSource"/> for a file by its format (#1856). Today a plain FITS only; a format
/// that can answer one sample without its whole image (a tiled <c>.fz</c>, a tiled TIFF) adds its own source to
/// the list here and nothing else changes.
/// </summary>
public static class ReadoutTileSources
{
    /// <summary>
    /// Opens a source for <paramref name="path"/>, or answers false when no source serves its format (gzip, <c>.fz</c>,
    /// TIFF, CR2 today), and the caller keeps reading the planes.
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

        source = null;
        return false;
    }
}
