using System;
using System.IO;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Which body a planetary capture shows, read off its path: a SER header has no field for it, and capture software names the
/// file or its folder after the target (SharpCap's <c>Jupiter/Light/...</c>, FireCapture's <c>..._Jup_...</c>, a user's
/// <c>Saturn/</c>). The name nearest the file wins, so a Saturn capture filed under a year's <c>Jupiter</c> folder is Saturn.
/// </summary>
public static class PlanetaryCaptureName
{
    /// <summary>
    /// The body <paramref name="path"/> names: its file name first, then each folder outward, each split into words at anything
    /// that is not a letter and a word matched whole, ignoring case (<c>Jupiter</c> or <c>Jup</c>, <c>Saturn</c> or <c>Sat</c>, and
    /// the other planets and the Moon by their names). Null when nothing on the path names one.
    /// </summary>
    public static CatalogIndex? Planet(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = Path.GetFullPath(path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var part = i == parts.Length - 1 ? Path.GetFileNameWithoutExtension(parts[i]) : parts[i];
            var start = -1;
            for (var j = 0; j <= part.Length; j++)
            {
                if (j < part.Length && char.IsLetter(part[j]))
                {
                    if (start < 0)
                    {
                        start = j;
                    }
                }
                else if (start >= 0)
                {
                    if (Named(part.AsSpan(start, j - start)) is { } planet)
                    {
                        return planet;
                    }
                    start = -1;
                }
            }
        }
        return null;
    }

    private static CatalogIndex? Named(ReadOnlySpan<char> word)
    {
        static bool Is(ReadOnlySpan<char> word, string name) => word.Equals(name, StringComparison.OrdinalIgnoreCase);

        return Is(word, "jupiter") || Is(word, "jup") ? CatalogIndex.Jupiter
            : Is(word, "saturn") || Is(word, "sat") ? CatalogIndex.Saturn
            : Is(word, "mars") ? CatalogIndex.Mars
            : Is(word, "venus") ? CatalogIndex.Venus
            : Is(word, "mercury") ? CatalogIndex.Mercury
            : Is(word, "uranus") ? CatalogIndex.Uranus
            : Is(word, "neptune") ? CatalogIndex.Neptune
            : Is(word, "moon") || Is(word, "luna") ? CatalogIndex.Moon
            : null;
    }
}
