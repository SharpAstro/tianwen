namespace TianWen.Lib.Imaging;

public readonly record struct Filter(string Name, string ShortName, Bandpass Bandpass)
{
    public static readonly Filter None = new(nameof(None), nameof(None), Bandpass.None);
    public static readonly Filter Unknown = new(nameof(Unknown), nameof(Unknown), Bandpass.None);
    public static readonly Filter Luminance = new(nameof(Luminance), "L", Bandpass.Luminance);
    public static readonly Filter Red = new(nameof(Red), "R", Bandpass.Red);
    public static readonly Filter Green = new(nameof(Green), "G", Bandpass.Green);
    public static readonly Filter Blue = new(nameof(Blue), "B", Bandpass.Blue);
    public static readonly Filter HydrogenAlpha = new(nameof(HydrogenAlpha), "H\u03B1", Bandpass.Ha);
    public static readonly Filter HydrogenBeta = new(nameof(HydrogenBeta), "H\u03B2", Bandpass.Hb);
    public static readonly Filter OxygenIII = new(nameof(OxygenIII), "OIII", Bandpass.OIII);
    public static readonly Filter SulphurII = new(nameof(SulphurII), "SII", Bandpass.SII);

    /// <summary>Parses a known set of filters or <see cref="Unknown"/> if none match</summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static Filter FromName(string name) => name?.Trim().Replace(" ", "").Replace("-", "").ToUpperInvariant() switch
    {
        "NONE" => None,
        "L" or "LUM" or "LUMINANCE" => Luminance,
        "R" or "RED" => Red,
        "B" or "BLUE" => Blue,
        "G" or "GREEN" => Green,
        "HALPHA" or "HA" or "HYDROGENALPHA" => HydrogenAlpha,
        "HBETA" or "HB" or "HYDROGENBETA" => HydrogenBeta,
        "OIII" or "O3" or "OIII" or "OXYIII" or "OXYGENIII" => OxygenIII,
        "SII" or "S2" or "SULPHURII" => SulphurII,
        null => None,
        _ => Unknown
    };

    public static implicit operator Filter(string name) => FromName(name);

    public override string ToString() => ShortName;
}
