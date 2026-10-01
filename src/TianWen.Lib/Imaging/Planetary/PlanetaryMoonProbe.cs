using System;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A moon read as a near-point source: where it is in the plane, its flux there, and the plane's transfer ring by ring over the
/// moon's own disk.
/// </summary>
/// <param name="X">The moon's centre, x, in the plane's pixels.</param>
/// <param name="Y">The moon's centre, y.</param>
/// <param name="Flux">The moon's light inside the read's square, its rim's plane taken off, in the plane's units.</param>
/// <param name="Transfer">The kernel's transfer over the model's, ring by ring, at one at zero frequency.</param>
/// <param name="Model">The model's own transfer ring by ring (the disk, its drift and the diffraction): where it is small, the read is noise.</param>
public sealed record MoonRead(double X, double Y, double Flux, RadialTransfer Transfer, RadialTransfer Model);

/// <summary>
/// A Galilean moon beside the planet's disk as a probe of the kernel's finest band (docs/plans/planetary-restoration.md, R8 follow-up 4).
/// At 0.5"/px a moon is two to four pixels across, close enough to a point that its blurred image reads the kernel where a limb's
/// edge is noise: Europa's disk passes power to 0.57 cycles a pixel, past Nyquist. The read is a square of
/// <see cref="Half"/> px about the moon, its rim's plane taken off, under a round taper flat to <see cref="TaperFlat"/> px; the model a
/// uniform disk of the ephemeris' diameter, smeared along its drift over the frames and through the pupil's diffraction; the place the
/// cross-correlation's climbed peak; and the transfer in each ring the cross-spectrum's real part over the model's power, over the
/// read's own flux. So it is the kernel within 16 px, as <see cref="PlanetaryFinestBand.Edge"/> reads the limb's.
/// </summary>
public static class PlanetaryMoonProbe
{
    /// <summary>The square's half side, px.</summary>
    public const int Half = 16;

    /// <summary>The radius to which the taper is flat, px; it falls to zero at <see cref="Half"/>.</summary>
    public const double TaperFlat = 12;

    /// <summary>The inner radius of the rim the background plane is fitted to, px; it runs to <see cref="Half"/>.</summary>
    public const double RimFrom = 13;

    // The square padded for its transform.
    private const int Grid = 64;

    /// <summary>
    /// Reads the moon of radius <paramref name="radiusPx"/> near (<paramref name="x"/>, <paramref name="y"/>) in
    /// <paramref name="plane"/>: found within <paramref name="searchPx"/> of there, or, with zero, taken to be exactly there (a half
    /// stack read where its whole stack found the moon). Null when its square leaves the plane or no peak is found.
    /// </summary>
    /// <param name="plane">The stack, row-major.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="x">Where the moon is expected, x.</param>
    /// <param name="y">Where the moon is expected, y.</param>
    /// <param name="radiusPx">The moon's radius, px.</param>
    /// <param name="driftX">How far the moon moved over the planet's disk across the frames, x, px.</param>
    /// <param name="driftY">The same in y.</param>
    /// <param name="diffraction">The pupil's diffraction transfer, by cycles a pixel.</param>
    /// <param name="searchPx">How far from (<paramref name="x"/>, <paramref name="y"/>) to look, px; zero to read it there.</param>
    /// <param name="limbDarkening">The model disk's brightness as mu to this power (zero for uniform): what a moon's own limb darkening
    /// would move the read by.</param>
    public static MoonRead? Read(ReadOnlySpan<float> plane, int width, int height, double x, double y, double radiusPx, double driftX, double driftY,
        Func<double, double> diffraction, int searchPx = 3, double limbDarkening = 0)
    {
        ArgumentNullException.ThrowIfNull(diffraction);
        var model = Model(radiusPx, driftX, driftY, diffraction, limbDarkening);
        var (atX, atY) = (x, y);
        for (var pass = 0; pass < 3; pass++)
        {
            var (cx, cy) = ((int)Math.Round(atX), (int)Math.Round(atY));
            if (Cut(plane, width, height, cx, cy) is not { } spectrum)
            {
                return null;
            }
            double dx, dy;
            if (searchPx > 0)
            {
                var cross = new Complex[Grid * Grid];
                for (var i = 0; i < cross.Length; i++)
                {
                    cross[i] = spectrum[i] * Complex.Conjugate(model[i]);
                }
                if (Peak(cross, searchPx + 1) is not { } start)
                {
                    return null;
                }
                (dx, dy) = PhaseCorrelation.ClimbPeak(cross, Grid, Grid, start.X, start.Y);
                // Off its square's centre by more than a pixel, the square is cut again about where the moon is.
                if (pass < 2 && (Math.Abs(dx) > 1 || Math.Abs(dy) > 1))
                {
                    (atX, atY) = (cx + dx, cy + dy);
                    continue;
                }
            }
            else
            {
                (dx, dy) = (atX - cx, atY - cy);
            }
            return Transfer(spectrum, model, cx + dx, cy + dy, dx, dy);
        }
        return null;
    }

    // The square about (cx, cy) less its rim's plane, under the taper, padded and transformed; null where it leaves the plane.
    private static Complex[]? Cut(ReadOnlySpan<float> plane, int width, int height, int cx, int cy)
    {
        if (cx - Half < 0 || cy - Half < 0 || cx + Half >= width || cy + Half >= height)
        {
            return null;
        }
        // The plane a + b dx + c dy through the rim, by least squares (the rim is symmetric, so the three terms are independent).
        double s1 = 0, sx = 0, sy = 0, sxx = 0, syy = 0, n = 0;
        for (var j = -Half; j <= Half; j++)
        {
            for (var i = -Half; i <= Half; i++)
            {
                var r = Math.Sqrt((i * i) + (j * j));
                if (r >= RimFrom && r <= Half)
                {
                    var v = plane[((cy + j) * width) + cx + i];
                    s1 += v;
                    sx += v * i;
                    sy += v * j;
                    sxx += i * i;
                    syy += j * j;
                    n++;
                }
            }
        }
        var (a, b, c) = (s1 / n, sx / sxx, sy / syy);
        var spectrum = new Complex[Grid * Grid];
        for (var j = -Half; j <= Half; j++)
        {
            for (var i = -Half; i <= Half; i++)
            {
                var r = Math.Sqrt((i * i) + (j * j));
                var taper = r <= TaperFlat ? 1 : r >= Half ? 0 : 0.5 * (1 + Math.Cos(Math.PI * (r - TaperFlat) / (Half - TaperFlat)));
                var v = plane[((cy + j) * width) + cx + i] - (a + (b * i) + (c * j));
                spectrum[(((j + Grid) % Grid) * Grid) + ((i + Grid) % Grid)] = v * taper;
            }
        }
        Fft2D.Forward(spectrum, Grid, Grid);
        return spectrum;
    }

    // The model's spectrum at the grid's origin, analytic so that it carries no aliases: the disk's own transfer (uniform, or darkened to
    // its limb as mu to `limbDarkening`), the pixel's box, the drift's box and the diffraction. A disk drawn in pixels and moved by a
    // phase is not the disk drawn where it is: an undersampled disk's aliases move with other phases, which read a Gaussian's transfer
    // 4 % high at 0.2 cycles a pixel. A camera's pixels sample the image after the kernel has blurred it, so the data's aliases are the
    // kernel's to damp, never the model's to carry.
    private static Complex[] Model(double radiusPx, double driftX, double driftY, Func<double, double> diffraction, double limbDarkening)
    {
        var disk = DiskTransfer(radiusPx, limbDarkening);
        var field = new Complex[Grid * Grid];
        for (var ky = 0; ky < Grid; ky++)
        {
            var fy = (ky < Grid / 2 ? ky : ky - Grid) / (double)Grid;
            for (var kx = 0; kx < Grid; kx++)
            {
                var fx = (kx < Grid / 2 ? kx : kx - Grid) / (double)Grid;
                var f = Math.Sqrt((fx * fx) + (fy * fy));
                field[(ky * Grid) + kx] = disk(f) * Sinc(fx) * Sinc(fy) * Sinc((fx * driftX) + (fy * driftY)) * diffraction(f);
            }
        }
        return field;
    }

    // A disk's radial transfer by its Hankel transform, at unit flux: the brightness w(rho) = (1 - rho^2)^(e / 2) over rings of the
    // unit disk, each through J0, tabulated finely enough to read between.
    private static Func<double, double> DiskTransfer(double radiusPx, double limbDarkening)
    {
        const int rings = 256;
        const double step = 0.0025;
        var table = new double[(int)(1 / step) + 2];
        var weights = new double[rings];
        double total = 0;
        for (var r = 0; r < rings; r++)
        {
            var rho = (r + 0.5) / rings;
            weights[r] = (limbDarkening == 0 ? 1 : Math.Pow(1 - (rho * rho), limbDarkening / 2)) * rho;
            total += weights[r];
        }
        for (var i = 0; i < table.Length; i++)
        {
            double sum = 0;
            for (var r = 0; r < rings; r++)
            {
                sum += weights[r] * BesselJ0(2 * Math.PI * i * step * radiusPx * (r + 0.5) / rings);
            }
            table[i] = sum / total;
        }
        return f =>
        {
            var at = f / step;
            var i = (int)at;
            if (i >= table.Length - 1)
            {
                return table[^1];
            }
            var t = at - i;
            return (table[i] * (1 - t)) + (table[i + 1] * t);
        };
    }

    // J0(x) = (1 / pi) times the integral over [0, pi] of cos(x sin theta), by the midpoint rule, exact to 1e-10 here (x under 12).
    private static double BesselJ0(double x)
    {
        const int steps = 64;
        double sum = 0;
        for (var k = 0; k < steps; k++)
        {
            sum += Math.Cos(x * Math.Sin(Math.PI * (k + 0.5) / steps));
        }
        return sum / steps;
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>
    /// Which way round the moons say the disk is (docs/plans/planetary-restoration.md, R8 follow-up 4): of the two ends of
    /// <paramref name="placement"/>'s axis taken as north, each mirrored or not, the placement under which <paramref name="moons"/>'
    /// places off the disk hold the most light, each the brightest pixel within <paramref name="searchPx"/> of where it would be. A limb
    /// fit gives the axis; which end is north, and whether the image is mirrored, three moons decide.
    /// </summary>
    public static DiskPlacement Orient(ReadOnlySpan<float> plane, int width, int height, DiskPlacement placement, ImmutableArray<MoonDisk> moons, int searchPx = 6)
    {
        var best = placement;
        var top = double.NegativeInfinity;
        foreach (var turn in (ReadOnlySpan<double>)[0, 180])
        {
            foreach (var mirrored in (ReadOnlySpan<bool>)[false, true])
            {
                var candidate = placement with { NorthAngleDeg = placement.NorthAngleDeg + turn, Mirrored = mirrored };
                double light = 0;
                foreach (var moon in moons)
                {
                    var (mx, my) = candidate.ImagePoint(moon.X, moon.Y);
                    if (Math.Sqrt(((mx - placement.CenterX) * (mx - placement.CenterX)) + ((my - placement.CenterY) * (my - placement.CenterY))) < 1.2 * placement.EquatorialRadius)
                    {
                        continue;
                    }
                    var brightest = 0.0;
                    for (var y = Math.Max(0, (int)Math.Round(my) - searchPx); y <= Math.Min(height - 1, (int)Math.Round(my) + searchPx); y++)
                    {
                        for (var x = Math.Max(0, (int)Math.Round(mx) - searchPx); x <= Math.Min(width - 1, (int)Math.Round(mx) + searchPx); x++)
                        {
                            brightest = Math.Max(brightest, plane[(y * width) + x]);
                        }
                    }
                    light += brightest;
                }
                if (light > top)
                {
                    (top, best) = (light, candidate);
                }
            }
        }
        return best with { NorthAngleDeg = ((best.NorthAngleDeg % 360) + 360) % 360 };
    }

    // The correlation surface's highest sample within `reach` of the origin, as a start for the climb.
    private static (double X, double Y)? Peak(Complex[] cross, int reach)
    {
        var surface = (Complex[])cross.Clone();
        Fft2D.Inverse(surface, Grid, Grid);
        (double X, double Y)? best = null;
        var top = double.NegativeInfinity;
        for (var j = -reach; j <= reach; j++)
        {
            for (var i = -reach; i <= reach; i++)
            {
                var v = surface[(((j + Grid) % Grid) * Grid) + ((i + Grid) % Grid)].Real;
                if (v > top)
                {
                    (top, best) = (v, (i, j));
                }
            }
        }
        return best;
    }

    // The transfer ring by ring: the cross-spectrum with the model moved to (dx, dy), its real part over the model's power, over the
    // read's flux; and the model's own mean transfer in each ring.
    private static MoonRead Transfer(Complex[] spectrum, Complex[] model, double x, double y, double dx, double dy)
    {
        const int rings = (Grid / 2) + 1;
        var (cross, power, magnitude, count) = (new double[rings], new double[rings], new double[rings], new int[rings]);
        for (var ky = 0; ky < Grid; ky++)
        {
            var sy = ky < Grid / 2 ? ky : ky - Grid;
            for (var kx = 0; kx < Grid; kx++)
            {
                var sx = kx < Grid / 2 ? kx : kx - Grid;
                var ring = (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
                if (ring >= rings)
                {
                    continue;
                }
                var i = (ky * Grid) + kx;
                var moved = model[i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((sx * dx) + (sy * dy)) / Grid);
                cross[ring] += (spectrum[i] * Complex.Conjugate(moved)).Real;
                power[ring] += moved.Magnitude * moved.Magnitude;
                magnitude[ring] += moved.Magnitude;
                count[ring]++;
            }
        }
        var flux = spectrum[0].Real;
        var transfer = ImmutableArray.CreateBuilder<double>(rings);
        var own = ImmutableArray.CreateBuilder<double>(rings);
        for (var r = 0; r < rings; r++)
        {
            transfer.Add(power[r] > 0 && flux != 0 ? cross[r] / power[r] / flux : 0);
            own.Add(count[r] > 0 ? magnitude[r] / count[r] : 0);
        }
        return new MoonRead(x, y, flux, new RadialTransfer(transfer.MoveToImmutable(), Grid), new RadialTransfer(own.MoveToImmutable(), Grid));
    }
}
