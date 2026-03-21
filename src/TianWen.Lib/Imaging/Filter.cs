using System.Text.RegularExpressions;

namespace TianWen.Lib.Imaging;

public readonly partial record struct Filter(string Name, string ShortName, string DisplayName, Bandpass Bandpass)
{
    /// <summary>Backwards-compatible constructor (DisplayName defaults to Name).</summary>
    public Filter(string Name, string ShortName, Bandpass Bandpass)
        : this(Name, ShortName, Name, Bandpass) { }

    public static readonly Filter None = new(nameof(None), nameof(None), nameof(None), Bandpass.None);
    public static readonly Filter Unknown = new(nameof(Unknown), nameof(Unknown), nameof(Unknown), Bandpass.None);
    public static readonly Filter Luminance = new(nameof(Luminance), "L", "Luminance", Bandpass.Luminance);
    public static readonly Filter Red = new(nameof(Red), "R", "Red", Bandpass.Red);
    public static readonly Filter Green = new(nameof(Green), "G", "Green", Bandpass.Green);
    public static readonly Filter Blue = new(nameof(Blue), "B", "Blue", Bandpass.Blue);
    public static readonly Filter HydrogenAlpha = new(nameof(HydrogenAlpha), "H\u03B1", "H-Alpha", Bandpass.Ha);
    public static readonly Filter HydrogenBeta = new(nameof(HydrogenBeta), "H\u03B2", "H-Beta", Bandpass.Hb);
    public static readonly Filter OxygenIII = new(nameof(OxygenIII), "OIII", "OIII", Bandpass.OIII);
    public static readonly Filter SulphurII = new(nameof(SulphurII), "SII", "SII", Bandpass.SII);
    // dual band filters
    public static readonly Filter HydrogenAlphaOxygenIII = new(nameof(HydrogenAlphaOxygenIII), "H\u03B1+OIII", "H-Alpha + OIII", Bandpass.Ha | Bandpass.OIII);
    public static readonly Filter SulphurIIOxygenIII = new(nameof(SulphurIIOxygenIII), "SII+OIII", "SII + OIII", Bandpass.SII | Bandpass.OIII);

    // Dual-band patterns must be tested before single-band to avoid partial matches (e.g. "Ha+OIII" matching "Ha")
    [GeneratedRegex(@"^\s*(?:h(?:ydrogen)?[\s\-]*(?:a(?:lpha)?|\u03B1)|ha|h\u03B1)[\s\+]*o(?:xygen)?[\s\-]*iii\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HAlphaOIIIPattern();

    [GeneratedRegex(@"^\s*s(?:ulphur)?[\s\-]*(?:ii|2)[\s\+]*o(?:xygen)?[\s\-]*iii\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SIIOIIIPattern();

    [GeneratedRegex(@"^\s*(?:l(?:um(?:inance)?)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LuminancePattern();

    [GeneratedRegex(@"^\s*(?:r(?:ed)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RedPattern();

    [GeneratedRegex(@"^\s*(?:g(?:reen)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GreenPattern();

    [GeneratedRegex(@"^\s*(?:b(?:lue)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BluePattern();

    [GeneratedRegex(@"^\s*(?:h(?:ydrogen)?[\s\-]*(?:a(?:lpha)?|\u03B1)|ha|h\u03B1)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HAlphaPattern();

    [GeneratedRegex(@"^\s*(?:h(?:ydrogen)?[\s\-]*(?:b(?:eta)?|\u03B2)|hb|h\u03B2)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HBetaPattern();

    [GeneratedRegex(@"^\s*(?:o(?:xygen)?[\s\-]*iii|o3|oiii)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OIIIPattern();

    [GeneratedRegex(@"^\s*(?:s(?:ulphur)?[\s\-]*(?:ii|2)|sii|s2)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SIIPattern();

    [GeneratedRegex(@"^\s*none\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NonePattern();

    /// <summary>Parses a known set of filters or <see cref="Unknown"/> if none match.</summary>
    public static Filter FromName(string? name)
    {
        if (name is null)
        {
            return None;
        }

        // Dual-band patterns first (contain single-band substrings)
        if (HAlphaOIIIPattern().IsMatch(name)) return HydrogenAlphaOxygenIII;
        if (SIIOIIIPattern().IsMatch(name)) return SulphurIIOxygenIII;

        // Single-band
        if (NonePattern().IsMatch(name)) return None;
        if (LuminancePattern().IsMatch(name)) return Luminance;
        if (RedPattern().IsMatch(name)) return Red;
        if (GreenPattern().IsMatch(name)) return Green;
        if (BluePattern().IsMatch(name)) return Blue;
        if (HAlphaPattern().IsMatch(name)) return HydrogenAlpha;
        if (HBetaPattern().IsMatch(name)) return HydrogenBeta;
        if (OIIIPattern().IsMatch(name)) return OxygenIII;
        if (SIIPattern().IsMatch(name)) return SulphurII;

        return Unknown;
    }

    public static implicit operator Filter(string name) => FromName(name);

    public override readonly string ToString() => ShortName;
}
