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
public static partial class PlanetaryCaptureName
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
        return FilterNm(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// The effective wavelength, nm, of the filter a free text names by the same words as <see cref="WavelengthNm"/>: a capture program's
    /// filter setting (FireCapture's <c>Filter=R</c>, SharpCap's filter wheel's <c>Red</c>), a FITS <c>FILTER</c> card, a filter's own name
    /// (<c>Baader R</c>). An IR-cut filter (<c>UV/IR cut</c>) passes the visible, so it reads as a luminance, 550; a word for an IR-pass reads
    /// 750. Null when the text names no filter.
    /// </summary>
    public static double? FilterNm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var letters = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiLetter(c))
            {
                letters.Append(char.ToLowerInvariant(c));
            }
        }
        return letters.ToString().Contains("ircut", StringComparison.Ordinal) ? 550 : FirstWord(text, FilterWord);
    }

    /// <summary>
    /// The instant a WinJUPOS-style FILE name gives, <c>yyyy-MM-dd-HHmm_t</c> in UTC with the minute's tenths after the underscore
    /// (<c>2026-09-01-0706_4</c> is 07:06:24 UTC), the capture's middle by WinJUPOS' convention, which FireCapture and AutoStakkert follow.
    /// Null when the file name carries none, or one that is no instant.
    /// </summary>
    public static DateTimeOffset? Instant(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (WinJuposTime().Match(Path.GetFileNameWithoutExtension(path)) is not { Success: true } match)
        {
            return null;
        }
        var inv = CultureInfo.InvariantCulture;
        var (year, month, day) = (int.Parse(match.Groups[1].Value, inv), int.Parse(match.Groups[2].Value, inv), int.Parse(match.Groups[3].Value, inv));
        var (hour, minute, tenth) = (int.Parse(match.Groups[4].Value, inv), int.Parse(match.Groups[5].Value, inv), int.Parse(match.Groups[6].Value, inv));
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
        {
            return null;
        }
        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).AddSeconds(6 * tenth);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})-(\d{2})(\d{2})_(\d)(?!\d)")]
    private static partial System.Text.RegularExpressions.Regex WinJuposTime();

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
        => BodiesPointedAt(raHours, decDeg, utc, latitude, longitude, toleranceDeg) is [var nearest, ..] ? nearest : null;

    /// <summary>
    /// Every body within <paramref name="toleranceDeg"/> of a mount's pointing, nearest first, by the comparison <see cref="PointedAt"/> makes
    /// (which takes the first). More than one is a pointing that cannot say which (A4, #1391): on 2024-09-17 the Moon passed over Saturn,
    /// and a capture of the Moon filed under <c>Moon</c> was pointed nearer Saturn.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<CatalogIndex> BodiesPointedAt(double raHours, double decDeg, DateTimeOffset utc, double latitude,
        double longitude, double toleranceDeg = 1.5)
    {
        if (!double.IsFinite(raHours) || !double.IsFinite(decDeg))
        {
            return [];
        }
        var near = new System.Collections.Generic.List<(CatalogIndex Body, double Separation)>();
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
            if (separation <= toleranceDeg)
            {
                near.Add((body, separation));
            }
        }
        near.Sort((a, b) => a.Separation.CompareTo(b.Separation));
        return [.. near.ConvertAll(n => n.Body)];
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
    /// and digits, its words joined by a hyphen, so <c>Baader R</c> is written <c>Baader-R</c> and reads back as red (run together, as
    /// <c>BaaderR</c>, it named no filter the reader knows).
    /// </summary>
    public static string RecordingFileName(CatalogIndex? planet, string? filterName, DateTimeOffset utc, int otaIndex)
    {
        var name = new StringBuilder();
        if (planet is { } body)
        {
            name.Append(body).Append('_');
        }
        var before = name.Length;
        var gap = false;
        foreach (var c in filterName ?? "")
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                gap = name.Length > before;
                continue;
            }
            if (gap)
            {
                name.Append('-');
                gap = false;
            }
            name.Append(c);
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
    /// The aperture and the design a text names (#1179, A4 #1391): a SER header's Telescope field as <see cref="TelescopeField"/> writes
    /// it, a capture program's own (FireCapture's <c>Scope=30cm SCT</c>), or a capture's file or folder name as a person writes one. The
    /// aperture is read, the first that answers: a number before "mm", before "cm", or before "inch" or a double quote; Celestron's names
    /// (<c>EdgeHD 11</c>, <c>edgehd11</c>, the <c>1100 EdgeHD</c>, <c>C9.25</c>, <c>C11</c>); Sky-Watcher's (<c>Skymax 102</c>,
    /// <c>250PDS</c>, <c>200P</c>); a number beside a design's word, inches up to 24 and millimetres from 50 (<c>Meade 16 SCT</c>,
    /// <c>12-SCT</c>, <c>102 Mak</c>). The design from its own word, else the one the product's name implies. A null aperture and
    /// <see cref="OpticalDesign.Unknown"/> where it names neither.
    /// </summary>
    public static (int? ApertureMm, OpticalDesign Design) Telescope(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return (null, OpticalDesign.Unknown);
        }
        var (aperture, implied) = ApertureOf(field);
        return (aperture, FirstWord(field, DesignWord) ?? implied);
    }

    /// <summary>
    /// The telescope <paramref name="path"/> names by the words of <see cref="Telescope"/>: its file name first, then each folder outward,
    /// the nearest that gives an aperture (a capture filed under <c>Jupiter-12-inch SCT-ASI224MC</c> is a 305 mm SCT's). A null aperture and
    /// <see cref="OpticalDesign.Unknown"/> where nothing on the path gives one.
    /// </summary>
    public static (int? ApertureMm, OpticalDesign Design) TelescopeOfPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = Path.GetFullPath(path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (Telescope(i == parts.Length - 1 ? Path.GetFileNameWithoutExtension(parts[i]) : parts[i]) is { ApertureMm: not null } named)
            {
                return named;
            }
        }
        return (null, OpticalDesign.Unknown);
    }

    // The aperture a text gives, mm, and the design its product's name implies, by the first of TelescopeWords that answers.
    private static (int? ApertureMm, OpticalDesign Implied) ApertureOf(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var (pattern, unit, implied) in TelescopeWords)
        {
            if (pattern.Match(text) is not { Success: true } match
                || !double.TryParse(match.Groups["n"].Value, NumberStyles.AllowDecimalPoint, inv, out var number) || number <= 0)
            {
                continue;
            }
            var mm = unit switch
            {
                ApertureUnit.Millimetres => number,
                ApertureUnit.Centimetres => number * 10,
                ApertureUnit.Inches => number * 25.4,
                // Celestron numbers its EdgeHDs by a hundred times the inches: the 1100 EdgeHD is 11 inches.
                ApertureUnit.HundredthsOfAnInch => number / 100 * 25.4,
                // Beside a design's word, a small number is inches (Meade's 16 SCT) and a large one millimetres (a 102 Mak).
                _ => number <= 24 ? number * 25.4 : number >= 50 ? number : double.NaN,
            };
            // A Celestron C925 is the C9.25.
            if (unit == ApertureUnit.Inches && implied == OpticalDesign.SCT && number > 100)
            {
                mm = number / 100 * 25.4;
            }
            if (mm is >= 40 and <= 1100)
            {
                return ((int)Math.Round(mm, MidpointRounding.AwayFromZero), implied);
            }
        }
        return (null, OpticalDesign.Unknown);
    }

    private enum ApertureUnit { Millimetres, Centimetres, Inches, HundredthsOfAnInch, BesideADesign }

    // The ways a text gives an aperture, in the order they are asked: a unit first, then a product's name, then a number beside a design.
    private static readonly (System.Text.RegularExpressions.Regex Pattern, ApertureUnit Unit, OpticalDesign Implied)[] TelescopeWords =
    [
        (Words(@"(?<![\d.])(?<n>\d{2,4})\s*mm(?![a-z])"), ApertureUnit.Millimetres, OpticalDesign.Unknown),
        (Words(@"(?<![\d.])(?<n>\d{1,3}(?:\.\d)?)\s*cm(?![a-z])"), ApertureUnit.Centimetres, OpticalDesign.Unknown),
        (Words(@"(?<![\d.])(?<n>\d{1,2}(?:\.\d{1,2})?)\s*-?\s*(?:inch(?:es)?(?![a-z])|in(?![a-z])|"")"), ApertureUnit.Inches, OpticalDesign.Unknown),
        (Words(@"edge\s*-?\s*hd\s*-?\s*(?<n>\d{1,2}(?:\.\d{1,2})?)(?![\d.])"), ApertureUnit.Inches, OpticalDesign.SCT),
        (Words(@"(?<![\d.])(?<n>\d{3,4})\s*-?\s*edge\s*-?\s*hd"), ApertureUnit.HundredthsOfAnInch, OpticalDesign.SCT),
        (Words(@"(?<![a-z\d])c\s?(?<n>5|6|8|9\.25|925|11|14)(?![\d.a-z])"), ApertureUnit.Inches, OpticalDesign.SCT),
        (Words(@"(?:skymax|maksutov|mak)\s*-?\s*(?<n>\d{2,3})(?![\d.])"), ApertureUnit.Millimetres, OpticalDesign.Cassegrain),
        (Words(@"(?<![\d.])(?<n>\d{3})\s*-?\s*p(?:ds)?(?![a-z\d])"), ApertureUnit.Millimetres, OpticalDesign.Newtonian),
        // Never a focal ratio's number (f/4.7, f14) nor one inside a word: only a number standing on its own beside the design.
        (Words(@"(?<![\d./a-z])(?<n>\d{1,3}(?:\.\d{1,2})?)\s*[-_ ]?\s*(?:sct|maksutov|mak|cassegrain|newtonian|newton|dobsonian|dob|refractor|apo|rasa)(?![a-z])"),
            ApertureUnit.BesideADesign, OpticalDesign.Unknown),
        (Words(@"(?<![a-z])(?:sct|maksutov|mak|cassegrain|newtonian|newton|dobsonian|dob|refractor|apo|rasa)\s*[-_ ]?\s*(?<n>\d{1,3}(?:\.\d{1,2})?)(?![\d.a-z])"),
            ApertureUnit.BesideADesign, OpticalDesign.Unknown),
    ];

    private static System.Text.RegularExpressions.Regex Words(string pattern)
        => new(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
            "refractor" or "apo" => OpticalDesign.Refractor,
            "newtonian" or "newton" or "dob" or "dobsonian" => OpticalDesign.Newtonian,
            "newtoniancassegrain" => OpticalDesign.NewtonianCassegrain,
            "sct" or "edgehd" => OpticalDesign.SCT,
            "cassegrain" or "mak" or "maksutov" or "skymax" => OpticalDesign.Cassegrain,
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
