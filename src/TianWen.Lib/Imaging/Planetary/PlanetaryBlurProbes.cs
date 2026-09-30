using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What R7's second and third probes read a stack's blur as (docs/plans/planetary-restoration.md, R7 part 2): the limb fit's
/// kernel, its core and its Gaussian halo fitted together, applied to a plane; and a kernel's width, the Gaussian whose band
/// transfers fit its own best, so a transfer read one way and a kernel fitted another can be put side by side.
/// </summary>
public static class PlanetaryBlurProbes
{
    /// <summary>
    /// <paramref name="plane"/> blurred by <paramref name="fit"/>'s kernel: (1 - h) of a Gaussian of its core's sigma and h of one
    /// of its halo's, h its halo's share (<see cref="LimbFit.HaloFraction"/>), as the fit's own model blurs its disk.
    /// </summary>
    public static float[] BlurByLimbKernel(float[] plane, int width, int height, in LimbFit fit)
    {
        ArgumentNullException.ThrowIfNull(plane);
        var core = Image.SeparableGaussianBlur(plane, width, height, (float)fit.PsfSigma);
        if (fit.HaloFraction <= 0 || fit.HaloWidth <= 0)
        {
            return core;
        }
        var halo = Image.SeparableGaussianBlur(plane, width, height, (float)fit.HaloWidth);
        var h = (float)fit.HaloFraction;
        for (var i = 0; i < core.Length; i++)
        {
            core[i] = ((1 - h) * core[i]) + (h * halo[i]);
        }
        return core;
    }

    /// <summary>
    /// The sigma, pixels, of the Gaussian whose transfer through <paramref name="plane"/> (a normalised object, R3's
    /// <see cref="PlanetaryMetrics.Fidelity"/> of it blurred against itself) fits <paramref name="transfers"/> best in bands
    /// <paramref name="firstBand"/> to <paramref name="lastBand"/> (1-based, the finest first), by least squares and a golden
    /// section between 0.05 and <paramref name="maxSigma"/>. A band whose transfer is not a number is left out.
    /// </summary>
    public static double EquivalentGaussianSigma(ImmutableArray<double> transfers, float[] plane, int width, int height, MetricDisk disk, int firstBand = 1, int lastBand = 4,
        double maxSigma = 8)
    {
        ArgumentNullException.ThrowIfNull(plane);
        double Misfit(double sigma)
        {
            var blurred = Image.SeparableGaussianBlur(plane, width, height, (float)sigma);
            var gaussian = PlanetaryMetrics.Fidelity(blurred, plane, width, height, disk, lastBand);
            double sum = 0;
            for (var b = firstBand; b <= lastBand; b++)
            {
                var measured = transfers[b - 1];
                if (double.IsFinite(measured))
                {
                    var d = measured - gaussian[b - 1].Transfer;
                    sum += d * d;
                }
            }
            return sum;
        }
        var (lo, hi) = (0.05, maxSigma);
        var golden = (Math.Sqrt(5) - 1) / 2;
        var (c, d) = (hi - (golden * (hi - lo)), lo + (golden * (hi - lo)));
        var (fc, fd) = (Misfit(c), Misfit(d));
        for (var iteration = 0; iteration < 30; iteration++)
        {
            if (fc < fd)
            {
                (hi, d, fd) = (d, c, fc);
                c = hi - (golden * (hi - lo));
                fc = Misfit(c);
            }
            else
            {
                (lo, c, fc) = (c, d, fd);
                d = lo + (golden * (hi - lo));
                fd = Misfit(d);
            }
        }
        return (lo + hi) / 2;
    }
}
