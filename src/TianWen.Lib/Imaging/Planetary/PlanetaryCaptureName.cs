using System;
using System.Globalization;
using System.IO;
using System.Text;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;

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

    /// <summary>The bodies a planetary capture points at: the planets and the Moon.</summary>
    private static readonly CatalogIndex[] Bodies =
    [
        CatalogIndex.Mercury, CatalogIndex.Venus, CatalogIndex.Mars, CatalogIndex.Jupiter, CatalogIndex.Saturn,
        CatalogIndex.Uranus, CatalogIndex.Neptune, CatalogIndex.Moon,
    ];

    /// <summary>
    /// The body a mount pointing at (<paramref name="raHours"/>, <paramref name="decDeg"/>) is on at <paramref name="utc"/>, seen from
    /// (<paramref name="latitude"/>, <paramref name="longitude"/>): the nearest within <paramref name="toleranceDeg"/>, else null
    /// (#1179). Compared both of date and in J2000, so a mount reporting either is matched; a planet's disk is under a minute of arc
    /// across, so the tolerance is the mount's pointing and the Moon's size and parallax.
    /// </summary>
    public static CatalogIndex? PointedAt(double raHours, double decDeg, DateTimeOffset utc, double latitude, double longitude, double toleranceDeg = 1.5)
    {
        if (!double.IsFinite(raHours) || !double.IsFinite(decDeg))
        {
            return null;
        }
        CatalogIndex? nearest = null;
        var best = toleranceDeg;
        foreach (var body in Bodies)
        {
            var separation = double.PositiveInfinity;
            if (VSOP87a.Reduce(body, utc, latitude, longitude, out var ra, out var dec, out _, out _, out _))
            {
                separation = Separation(raHours, decDeg, ra, dec);
            }
            if (VSOP87a.ReduceJ2000(body, utc, out var raJ2000, out var decJ2000, out _))
            {
                separation = Math.Min(separation, Separation(raHours, decDeg, raJ2000, decJ2000));
            }
            if (separation <= best)
            {
                (nearest, best) = (body, separation);
            }
        }
        return nearest;
    }

    // The angle between two pointings, degrees.
    private static double Separation(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg)
    {
        var (ra1, dec1, ra2, dec2) = (ra1Hours * Math.PI / 12, dec1Deg * Math.PI / 180, ra2Hours * Math.PI / 12, dec2Deg * Math.PI / 180);
        var cos = (Math.Sin(dec1) * Math.Sin(dec2)) + (Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2));
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>
    /// The file name a TianWen recording takes (#1179): the planet and the filter first, as SharpCap and FireCapture name theirs and
    /// <see cref="Planet"/> and <see cref="WavelengthNm"/> read back, then the start in UTC and the OTA, as in
    /// <c>Jupiter_Red_2026-10-02T12_11_08_OTA1.ser</c>. A planet or a filter not known is left out; a filter's name keeps its letters
    /// and digits only.
    /// </summary>
    public static string RecordingFileName(CatalogIndex? planet, string? filterName, DateTimeOffset utc, int otaIndex)
    {
        var name = new StringBuilder();
        if (planet is { } body)
        {
            name.Append(body).Append('_');
        }
        var before = name.Length;
        foreach (var c in filterName ?? "")
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                name.Append(c);
            }
        }
        if (name.Length > before)
        {
            name.Append('_');
        }
        name.Append(utc.UtcDateTime.ToString("yyyy-MM-ddTHH_mm_ss", CultureInfo.InvariantCulture)).Append("_OTA").Append(otaIndex + 1).Append(".ser");
        return name.ToString();
    }

    /// <summary>The longest text a SER header field holds.</summary>
    public const int SerFieldLength = 40;

    /// <summary>
    /// What a recording writes in its SER header's Telescope field for <paramref name="ota"/> (#1179): its aperture, focal ratio and
    /// design, which <see cref="Telescope"/> reads back, then its name as far as the field's 40 characters go, as in
    /// <c>254 mm f/4.7 Newtonian, SW 250PDS</c>. Without an aperture, the name alone.
    /// </summary>
    public static string TelescopeField(in OTAData ota)
    {
        var name = ota.Name ?? "";
        if (ota.Aperture is not { } aperture || aperture <= 0)
        {
            return Ascii(name);
        }
        var optics = string.Create(CultureInfo.InvariantCulture, $"{aperture} mm");
        if (ota.FocalLength > 0)
        {
            optics += string.Create(CultureInfo.InvariantCulture, $" f/{(double)ota.FocalLength / aperture:0.#}");
        }
        if (ota.OpticalDesign is not OpticalDesign.Unknown)
        {
            optics += " " + ota.OpticalDesign;
        }
        return Ascii(name.Length == 0 ? optics : optics + ", " + name);
    }

    /// <summary>
    /// The aperture and the design a SER header's Telescope field names (#1179): a number before "mm" and a design's own word, as
    /// <see cref="TelescopeField"/> writes them and as a person types them. A null aperture and <see cref="OpticalDesign.Unknown"/>
    /// where it names neither.
    /// </summary>
    public static (int? ApertureMm, OpticalDesign Design) Telescope(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return (null, OpticalDesign.Unknown);
        }
        int? aperture = null;
        var mm = field.IndexOf("mm", StringComparison.OrdinalIgnoreCase);
        if (mm > 0)
        {
            var end = mm;
            while (end > 0 && field[end - 1] == ' ')
            {
                end--;
            }
            var start = end;
            while (start > 0 && char.IsAsciiDigit(field[start - 1]))
            {
                start--;
            }
            if (end > start && int.TryParse(field.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                aperture = value;
            }
        }
        return (aperture, FirstWord(field, DesignWord) ?? OpticalDesign.Unknown);
    }

    // A field's text kept to the printable ASCII a SER header holds, and to its length.
    private static string Ascii(string text)
    {
        var kept = new StringBuilder(Math.Min(text.Length, SerFieldLength));
        foreach (var c in text)
        {
            if (kept.Length == SerFieldLength)
            {
                break;
            }
            if (c is >= ' ' and <= '~')
            {
                kept.Append(c);
            }
        }
        return kept.ToString();
    }

    private static OpticalDesign? DesignWord(string word)
    {
        return word.ToLowerInvariant() switch
        {
            "refractor" => OpticalDesign.Refractor,
            "newtonian" => OpticalDesign.Newtonian,
            "newtoniancassegrain" => OpticalDesign.NewtonianCassegrain,
            "sct" => OpticalDesign.SCT,
            "cassegrain" => OpticalDesign.Cassegrain,
            "rasa" => OpticalDesign.RASA,
            "astrograph" => OpticalDesign.Astrograph,
            _ => null,
        };
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

    private static CatalogIndex? PlanetWord(string word)
    {
        return word.ToLowerInvariant() switch
        {
            "jupiter" or "jup" => CatalogIndex.Jupiter,
            "saturn" or "sat" => CatalogIndex.Saturn,
            "mars" => CatalogIndex.Mars,
            "venus" => CatalogIndex.Venus,
            "mercury" => CatalogIndex.Mercury,
            "uranus" => CatalogIndex.Uranus,
            "neptune" => CatalogIndex.Neptune,
            "moon" or "luna" => CatalogIndex.Moon,
            _ => null,
        };
    }

    private static double? FilterWord(string word)
    {
        return word.ToLowerInvariant() switch
        {
            "red" or "r" => 650,
            "green" or "g" => 530,
            "blue" or "b" => 460,
            "ir" => 750,
            "l" or "lum" or "luminance" => 550,
            _ => null,
        };
    }
}
