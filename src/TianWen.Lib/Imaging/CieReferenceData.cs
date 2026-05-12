using System.Collections.Immutable;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Standard CIE colorimetric reference data used to derive camera-RGB to sRGB
/// matrices from per-camera spectral response (QE × CFA) curves.
///
/// <para>The CIE 1931 2-degree standard observer colour-matching functions
/// (<see cref="X1931"/> / <see cref="Y1931"/> / <see cref="Z1931"/>) define how
/// human vision converts spectral energy to tristimulus XYZ; <see cref="D65"/>
/// is the standard "daylight at 6504 K" reference illuminant that sRGB's
/// primaries are referenced to. With these in hand,
/// <see cref="CameraColorMatrix.ComputeCamXyz(FilterCurve, FilterCurve, FilterCurve)"/>
/// integrates QE × CFA against each <c>(CIE primary × D65)</c> to build the
/// camera-to-XYZ matrix, which then converts to the camera-to-sRGB matrix
/// the render path wants.</para>
///
/// <para>All four curves are sampled at 5 nm intervals from 380 nm to 780 nm
/// (81 points). Values from CIE 015:2018 (the current standard reproduction)
/// via the CVRL canonical tabulation at
/// <c>http://www.cvrl.org/database/text/cmfs/ciexyz31_1.htm</c> for the
/// matching functions and CIE Standard Illuminant D65 (ISO/CIE 11664-2:2022)
/// for the illuminant. Wavelengths are in Angstroms to match the convention
/// used throughout <see cref="FilterCurve"/> and the SASP_data.fits import.</para>
/// </summary>
public static class CieReferenceData
{
    private const string CieOrigin = "CIE 015:2018 (CIE 1931 2-deg standard observer, 5 nm tabulation)";
    private const string D65Origin = "ISO/CIE 11664-2:2022 (CIE Standard Illuminant D65, 5 nm tabulation)";

    // CIE 1931 2-deg standard observer X-bar, 380-780 nm @ 5 nm (81 points).
    // Source: CVRL canonical tabulation, CIE 015:2018.
    // Declared before the FilterCurve properties so the static field init
    // order initialises these arrays before the property initialisers read them.
    private static readonly double[] XBarValues =
    [
        0.001368, 0.002236, 0.004243, 0.007650, 0.014310,
        0.023190, 0.043510, 0.077630, 0.134380, 0.214770,
        0.283900, 0.328500, 0.348280, 0.348060, 0.336200,
        0.318700, 0.290800, 0.251100, 0.195360, 0.142100,
        0.095640, 0.057950, 0.032010, 0.014700, 0.004900,
        0.002400, 0.009300, 0.029100, 0.063270, 0.109600,
        0.165500, 0.225750, 0.290400, 0.359700, 0.433450,
        0.512050, 0.594500, 0.678400, 0.762100, 0.842500,
        0.916300, 0.978600, 1.026300, 1.056700, 1.062200,
        1.045600, 1.002600, 0.938400, 0.854450, 0.751400,
        0.642400, 0.541900, 0.447900, 0.360800, 0.283500,
        0.218700, 0.164900, 0.121200, 0.087400, 0.063600,
        0.046770, 0.032900, 0.022700, 0.015840, 0.011359,
        0.008111, 0.005790, 0.004109, 0.002899, 0.002049,
        0.001440, 0.001000, 0.000690, 0.000476, 0.000332,
        0.000235, 0.000166, 0.000117, 0.000083, 0.000059,
        0.000042,
    ];

    // CIE 1931 2-deg standard observer Y-bar (= photopic V(lambda)),
    // 380-780 nm @ 5 nm (81 points). Source: CVRL canonical tabulation.
    private static readonly double[] YBarValues =
    [
        0.000039, 0.000064, 0.000120, 0.000217, 0.000396,
        0.000640, 0.001210, 0.002180, 0.004000, 0.007300,
        0.011600, 0.016840, 0.023000, 0.029800, 0.038000,
        0.048000, 0.060000, 0.073900, 0.090980, 0.112600,
        0.139020, 0.169300, 0.208020, 0.258600, 0.323000,
        0.407300, 0.503000, 0.608200, 0.710000, 0.793200,
        0.862000, 0.914850, 0.954000, 0.980300, 0.994950,
        1.000000, 0.995000, 0.978600, 0.952000, 0.915400,
        0.870000, 0.816300, 0.757000, 0.694900, 0.631000,
        0.566800, 0.503000, 0.441200, 0.381000, 0.321000,
        0.265000, 0.217000, 0.175000, 0.138200, 0.107000,
        0.081600, 0.061000, 0.044580, 0.032000, 0.023200,
        0.017000, 0.011920, 0.008210, 0.005723, 0.004102,
        0.002929, 0.002091, 0.001484, 0.001047, 0.000740,
        0.000520, 0.000361, 0.000249, 0.000172, 0.000120,
        0.000085, 0.000060, 0.000042, 0.000030, 0.000021,
        0.000015,
    ];

    // CIE 1931 2-deg standard observer Z-bar, 380-780 nm @ 5 nm (81 points).
    // Source: CVRL canonical tabulation, CIE 015:2018.
    private static readonly double[] ZBarValues =
    [
        0.006450, 0.010550, 0.020050, 0.036210, 0.067850,
        0.110200, 0.207400, 0.371300, 0.645600, 1.039050,
        1.385600, 1.622960, 1.747060, 1.782600, 1.772110,
        1.744100, 1.669200, 1.528100, 1.287640, 1.041900,
        0.812950, 0.616200, 0.465180, 0.353300, 0.272000,
        0.212300, 0.158200, 0.111700, 0.078250, 0.057250,
        0.042160, 0.029840, 0.020300, 0.013400, 0.008750,
        0.005750, 0.003900, 0.002750, 0.002100, 0.001800,
        0.001650, 0.001400, 0.001100, 0.001000, 0.000800,
        0.000600, 0.000340, 0.000240, 0.000190, 0.000100,
        0.000050, 0.000030, 0.000020, 0.000010, 0.000000,
        0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
        0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
        0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
        0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
        0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
        0.000000,
    ];

    // CIE Standard Illuminant D65 spectral power distribution,
    // 380-780 nm @ 5 nm (81 points). Source: ISO/CIE 11664-2:2022.
    // Values are relative spectral power (peak normalised to ~100 at ~560 nm).
    private static readonly double[] D65Values =
    [
         49.9755,  52.3118,  54.6482,  68.7015,  82.7549,
         87.1204,  91.4860,  92.4589,  93.4318,  90.0570,
         86.6823,  95.7736, 104.8650, 110.9360, 117.0080,
        117.4100, 117.8120, 116.3360, 114.8610, 115.3920,
        115.9230, 112.3670, 108.8110, 109.0820, 109.3540,
        108.5780, 107.8020, 106.2960, 104.7900, 106.2390,
        107.6890, 106.0470, 104.4050, 104.2250, 104.0460,
        102.0230, 100.0000,  98.1671,  96.3342,  96.0611,
         95.7880,  92.2368,  88.6856,  89.3459,  90.0062,
         89.8026,  89.5991,  88.6489,  87.6987,  85.4936,
         83.2886,  83.4939,  83.6992,  81.8630,  80.0268,
         80.1207,  80.2146,  81.2462,  82.2778,  80.2810,
         78.2842,  74.0027,  69.7213,  70.6652,  71.6091,
         72.9790,  74.3490,  67.9765,  61.6040,  65.7448,
         69.8856,  72.4863,  75.0870,  69.3398,  63.5927,
         55.0054,  46.4182,  56.6118,  66.8054,  65.0941,
         63.3828,
    ];

    /// <summary>Common wavelength sample grid in Angstroms (3800-7800 at 50 step).
    /// 81 points covering the visible spectrum from 380 nm to 780 nm at 5 nm
    /// intervals, matching the canonical CIE tabulation.</summary>
    private static readonly ImmutableArray<double> WavelengthsAngstroms = BuildWavelengthGrid();

    private static ImmutableArray<double> BuildWavelengthGrid()
    {
        var grid = new double[81];
        for (var i = 0; i < grid.Length; i++) grid[i] = 3800.0 + i * 50.0;
        return ImmutableArray.Create(grid);
    }

    /// <summary>CIE 1931 2-deg standard observer X-bar (red-ish) matching function.</summary>
    public static FilterCurve X1931 { get; } = MakeCurve(nameof(X1931), CieOrigin, XBarValues);

    /// <summary>CIE 1931 2-deg standard observer Y-bar (green-ish; identical to
    /// the photopic luminosity function V(lambda)) matching function. Normalised
    /// so the peak at 555 nm = 1.0.</summary>
    public static FilterCurve Y1931 { get; } = MakeCurve(nameof(Y1931), CieOrigin, YBarValues);

    /// <summary>CIE 1931 2-deg standard observer Z-bar (blue-ish) matching function.</summary>
    public static FilterCurve Z1931 { get; } = MakeCurve(nameof(Z1931), CieOrigin, ZBarValues);

    /// <summary>CIE Standard Illuminant D65 spectral power distribution.
    /// "Average daylight" with a correlated colour temperature of ~6504 K.
    /// The reference white point of the sRGB / Rec. 709 primaries.</summary>
    public static FilterCurve D65 { get; } = MakeCurve(nameof(D65), D65Origin, D65Values);

    private static FilterCurve MakeCurve(string name, string origin, double[] values)
    {
        // FilterCurve is the shared spectral carrier — wavelengths in Angstroms,
        // throughputs as doubles. Lifting the constants into a FilterCurve lets
        // ComputeCamXyz reuse FilterCurve.Combine + Interpolate without writing
        // a parallel resampler.
        return new FilterCurve(name, origin, WavelengthsAngstroms, ImmutableArray.Create(values));
    }
}
