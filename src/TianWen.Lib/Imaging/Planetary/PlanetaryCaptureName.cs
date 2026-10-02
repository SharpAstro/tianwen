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
            if (FirstWord(i == parts.Length - 1 ? Path.GetFileNameWithoutExtension(parts[i]) : parts[i], PlanetWord) is { } planet)
            {
                return planet;
            }
        }
        return null;
    }

    /// <summary>
    /// The body a free text names by the same words as <see cref="Planet"/>: a FITS <c>OBJECT</c> card, which capture software
    /// writes as the target (SharpCap's <c>Jupiter</c>) and a planetary stack writes as its planet. Null when none.
    /// </summary>
    public static CatalogIndex? Named(string? text)
        => string.IsNullOrEmpty(text) ? null : FirstWord(text, PlanetWord);

    /// <summary>
    /// The effective wavelength, nm, of the filter a mono capture's FILE name says it was taken through, the words matched whole
    /// as for the planet: red or R 650, green or G 530, blue or B 460, IR 750 (an IR-pass), L, Lum or luminance 550. Null when the
    /// name says none, where a mono capture is taken as broadband (550). The file name only: a folder's filter word is too often
    /// a session's, not this capture's.
    /// </summary>
    public static double? WavelengthNm(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FirstWord(Path.GetFileNameWithoutExtension(path), FilterWord);
    }

    // The first word of `text` (a run of letters) `match` answers for, or null.
    private static T? FirstWord<T>(string text, Func<string, T?> match) where T : struct
    {
        var start = -1;
        for (var j = 0; j <= text.Length; j++)
        {
            if (j < text.Length && char.IsLetter(text[j]))
            {
                if (start < 0)
                {
                    start = j;
                }
            }
            else if (start >= 0)
            {
                if (match(text[start..j]) is { } found)
                {
                    return found;
                }
                start = -1;
            }
        }
        return null;
    }

    private static bool Is(string word, string name) => word.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static CatalogIndex? PlanetWord(string word)
        => Is(word, "jupiter") || Is(word, "jup") ? CatalogIndex.Jupiter
            : Is(word, "saturn") || Is(word, "sat") ? CatalogIndex.Saturn
            : Is(word, "mars") ? CatalogIndex.Mars
            : Is(word, "venus") ? CatalogIndex.Venus
            : Is(word, "mercury") ? CatalogIndex.Mercury
            : Is(word, "uranus") ? CatalogIndex.Uranus
            : Is(word, "neptune") ? CatalogIndex.Neptune
            : Is(word, "moon") || Is(word, "luna") ? CatalogIndex.Moon
            : null;

    private static double? FilterWord(string word)
        => Is(word, "red") || Is(word, "r") ? 650
            : Is(word, "green") || Is(word, "g") ? 530
            : Is(word, "blue") || Is(word, "b") ? 460
            : Is(word, "ir") ? 750
            : Is(word, "l") || Is(word, "lum") || Is(word, "luminance") ? 550
            : null;
}
