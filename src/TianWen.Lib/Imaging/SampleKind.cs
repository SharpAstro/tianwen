namespace TianWen.Lib.Imaging;

public enum SampleKind : byte
{
    None = 0,
    HFD = 1,
    FWHM = 2,
    /// <summary>Moment-based ellipticity (0 = circular, → 1 = elongated).</summary>
    Ellipticity = 3,
}
