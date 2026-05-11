using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A named spectral transmission or quantum efficiency curve loaded from the
/// SASP_data.fits filter/SED/sensor dataset.
/// </summary>
public readonly record struct FilterCurve(
    string Name,               // EXTNAME, e.g. "BAADER_R"
    string OriginFilename,     // source CSV, e.g. "Baader-R.csv"
    ImmutableArray<double> Wavelengths, // Angstroms, monotonic increasing
    ImmutableArray<double> Throughputs  // 0-1, same length as Wavelengths
)
{
    /// <summary>Number of data points in this curve.</summary>
    public int Count => Wavelengths.Length;

    /// <summary>
    /// Wavelength in Angstroms at index <paramref name="i"/>.
    /// </summary>
    public double WavelengthAt(int i) => Wavelengths[i];

    /// <summary>Throughput at index <paramref name="i"/>.</summary>
    public double ThroughputAt(int i) => Throughputs[i];

    /// <summary>
    /// Interpolates throughput at a given wavelength via linear interpolation.
    /// Returns 0 for wavelengths outside the curve's range.
    /// </summary>
    public double Interpolate(double wavelengthAngstrom)
    {
        var wl = Wavelengths;
        var tp = Throughputs;
        if (wl.Length == 0 || wavelengthAngstrom < wl[0] || wavelengthAngstrom > wl[^1])
            return 0;

        // Binary search for the lower bound
        var lo = 0;
        var hi = wl.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (wl[mid] <= wavelengthAngstrom) lo = mid;
            else hi = mid - 1;
        }

        if (lo >= wl.Length - 1) return tp[^1];

        var t = (wavelengthAngstrom - wl[lo]) / (wl[lo + 1] - wl[lo]);
        return tp[lo] + t * (tp[lo + 1] - tp[lo]);
    }

    /// <summary>
    /// Multiplies multiple spectral curves onto a common wavelength grid (1 Å step)
    /// covering the intersection of all curves' ranges. Returns a new combined curve.
    /// Useful for computing system throughput: T_sys(λ) = QE(λ) × filter(λ) × CFA(λ).
    /// </summary>
    public static FilterCurve Combine(string combinedName, ReadOnlySpan<FilterCurve> curves)
    {
        if (curves.Length == 0)
            throw new ArgumentException("At least one curve required", nameof(curves));
        if (curves.Length == 1)
            return curves[0];

        // Find the overlapping wavelength range
        var wlMin = double.MinValue;
        var wlMax = double.MaxValue;
        foreach (ref readonly var c in curves)
        {
            wlMin = Math.Max(wlMin, c.WavelengthAt(0));
            wlMax = Math.Min(wlMax, c.WavelengthAt(c.Count - 1));
        }

        if (wlMin >= wlMax)
            throw new ArgumentException("Curves have no overlapping wavelength range");

        // Build a common grid at 1 Å resolution
        var nPoints = (int)(wlMax - wlMin) + 1;
        var wavelengths = new double[nPoints];
        var throughputs = new double[nPoints];

        for (var i = 0; i < nPoints; i++)
        {
            var wl = wlMin + i;
            wavelengths[i] = wl;
            var tp = 1.0;
            foreach (ref readonly var c in curves)
                tp *= c.Interpolate(wl);
            throughputs[i] = tp;
        }

        return new FilterCurve(combinedName, OriginFilename: "",
            Wavelengths: ImmutableArray.Create(wavelengths),
            Throughputs: ImmutableArray.Create(throughputs));
    }

    /// <summary>
    /// Integrates SED(λ) × T_sys(λ) over the overlapping wavelength range
    /// using 1 Å trapezoidal steps. Returns the total flux detected through
    /// the system. The SED contains physical flux values (not throughput);
    /// T_sys is a combined throughput curve (e.g. from <see cref="Combine"/>).
    /// </summary>
    public static double IntegrateSedThroughput(FilterCurve sed, FilterCurve tsys)
    {
        var wlMin = Math.Max(sed.WavelengthAt(0), tsys.WavelengthAt(0));
        var wlMax = Math.Min(sed.WavelengthAt(sed.Count - 1), tsys.WavelengthAt(tsys.Count - 1));
        if (wlMin >= wlMax) return 0;

        var flux = 0.0;
        var nSteps = (int)(wlMax - wlMin);
        for (var i = 0; i < nSteps; i++)
        {
            var wl1 = wlMin + i;
            var wl2 = wl1 + 1;
            // Trapezoidal: average of endpoints × step width (1 Å)
            var sed1 = sed.Interpolate(wl1);
            var sed2 = sed.Interpolate(wl2);
            var tsys1 = tsys.Interpolate(wl1);
            var tsys2 = tsys.Interpolate(wl2);
            flux += (sed1 * tsys1 + sed2 * tsys2) * 0.5;
        }
        return flux;
    }
}
