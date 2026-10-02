using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A stack's transfer in the finest band read without a truth (docs/plans/planetary-restoration.md, R8 follow-up 3): off the limb, as an
/// oversampled edge against the limb fit's sharp model, and off the disk's texture, as its spectrum against another year's map. The
/// limb fit's own kernel is a model whose finest band is its tail (#1120); these read the band itself.
/// </summary>
public static class PlanetaryFinestBand
{
    /// <summary>How far either side of the limb the edge is read, px.</summary>
    public const double EdgeReach = 16;

    /// <summary>The share of the reach, at either end, the edge's window falls to zero over (a Tukey window, flat in the middle).</summary>
    public const double EdgeTaper = 0.5;

    /// <summary>The edge's bins across the limb, px: a tenth of a pixel, so the pixels at every distance from it sample the edge finely.</summary>
    public const double EdgeBin = 0.1;

    /// <summary>The arc either side of the terminator's end of the equator the edge leaves out, degrees: the phase bends the limb there.</summary>
    public const double TerminatorArcDeg = 45;

    /// <summary>
    /// The edge across the limb of a planet as a stack shows it (R8 follow-up 3): <paramref name="plane"/> against <paramref name="reference"/>
    /// (the limb fit's sharp model through the pupil's diffraction), both normalised and on <paramref name="disk"/>, each limb point flattened
    /// by its own plane's zonal brightness at the latitude it lies across from (the belts, read off the plane through the planet's aspect).
    /// The one read the CLI's measurements and the sharpening (<see cref="PlanetarySharpening"/>) share.
    /// </summary>
    /// <param name="holdPoles">Flatten a limb point past the zonal profile's reach by the nearest latitude it reads, rather than leave it out:
    /// the polar limb, which a sector along the axis is made of (R8 follow-up 4 part 2).</param>
    public static EdgeProfile LimbEdge(ReadOnlySpan<float> plane, ReadOnlySpan<float> reference, int width, int height, MetricDisk disk, in LimbFit fit,
        in PlanetAspect aspect, double? sectorDeg = null, bool holdPoles = false)
    {
        var projection = new PlanetaryProjection(aspect, new DiskPlacement(disk.X, disk.Y, fit.EquatorialRadius, fit.NorthAngleDeg));
        return Edge(plane, reference, width, height, disk, fit.SunSide,
            Flatten(plane, width, height, projection, aspect.CentralMeridianIII, fit.LimbDarkening, holdPoles),
            Flatten(reference, width, height, projection, aspect.CentralMeridianIII, fit.LimbDarkening, holdPoles), sectorDeg);
    }

    // Each limb point's brightness relative to the plane's mean, by the zonal brightness at its latitude.
    private static Func<double, double, double> Flatten(ReadOnlySpan<float> plane, int width, int height, PlanetaryProjection projection, double centralMeridian,
        double limbDarkening, bool holdPoles)
    {
        var zonal = PlanetaryBelts.FromImage(plane, width, height, projection, centralMeridian, limbDarkening);
        var mean = zonal.Albedo.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        return (x, y) => projection.TrySurface(x, y, out var latitude, out _, out _, out _) ? (holdPoles ? zonal.Held(latitude) : zonal.At(latitude)) / mean : double.NaN;
    }

    /// <summary>
    /// The edge across the limb: the mean of <paramref name="plane"/> and of <paramref name="reference"/> (the limb's sharp model through
    /// whatever the read is to be over, such as the pupil's diffraction) in bins of <see cref="EdgeBin"/> by signed distance from
    /// <paramref name="disk"/>'s outline, along the ray from its centre, out to <see cref="EdgeReach"/> either side.
    /// </summary>
    /// <param name="sunSide">Which end of the equator the sun lights (the limb fit's); the arc about the other end is left out. Zero leaves nothing out.</param>
    /// <param name="flatten">Each pixel's brightness relative to the planet's mean at the latitude of the limb point it lies across from (the
    /// belts): the plane is divided by it, the reference by <paramref name="flattenReference"/>; null for none.</param>
    /// <param name="sectorDeg">When given, only the limb points whose position angle (from +x toward +y) lies within <paramref name="sectorHalfWidthDeg"/>
    /// of it or of its opposite: the edge, and so the kernel's profile, along one direction.</param>
    public static EdgeProfile Edge(ReadOnlySpan<float> plane, ReadOnlySpan<float> reference, int width, int height, MetricDisk disk, int sunSide,
        Func<double, double, double>? flatten = null, Func<double, double, double>? flattenReference = null, double? sectorDeg = null, double sectorHalfWidthDeg = 30)
    {
        var bins = (int)Math.Round(2 * EdgeReach / EdgeBin);
        var (sum, sumReference, count) = (new double[bins], new double[bins], new int[bins]);
        var axis = disk.AxisAngleDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(axis), Math.Sin(axis));
        var terminator = Math.Cos(TerminatorArcDeg * Math.PI / 180);
        var sector = Math.Cos(sectorHalfWidthDeg * Math.PI / 180);
        var (sectorX, sectorY) = sectorDeg is { } s ? (Math.Cos(s * Math.PI / 180), Math.Sin(s * Math.PI / 180)) : (0.0, 0.0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (dx, dy) = (x - disk.X, y - disk.Y);
                var r = Math.Sqrt((dx * dx) + (dy * dy));
                var rho = disk.RadiiAt(x, y);
                if (r < 1 || rho <= 0)
                {
                    continue;
                }
                var d = r * (1 - (1 / rho));
                var bin = (int)Math.Floor((d + EdgeReach) / EdgeBin);
                if (bin < 0 || bin >= bins)
                {
                    continue;
                }
                var (ux, uy) = (dx / r, dy / r);
                // The equatorial direction is across the axis; the sun lights the end sunSide points to.
                if (sunSide != 0 && (((-ux * sin) + (uy * cos)) * -sunSide) > terminator)
                {
                    continue;
                }
                if (sectorDeg is not null && Math.Abs((ux * sectorX) + (uy * sectorY)) < sector)
                {
                    continue;
                }
                var (lx, ly) = (disk.X + (dx / rho * 0.995), disk.Y + (dy / rho * 0.995));
                var (f, fr) = (flatten?.Invoke(lx, ly) ?? 1, flattenReference?.Invoke(lx, ly) ?? 1);
                if (!(f > 0) || !(fr > 0))
                {
                    continue;
                }
                var i = (y * width) + x;
                sum[bin] += plane[i] / f;
                sumReference[bin] += reference[i] / fr;
                count[bin]++;
            }
        }
        var (profile, profileReference) = (ImmutableArray.CreateBuilder<double>(bins), ImmutableArray.CreateBuilder<double>(bins));
        for (var b = 0; b < bins; b++)
        {
            profile.Add(count[b] > 0 ? sum[b] / count[b] : double.NaN);
            profileReference.Add(count[b] > 0 ? sumReference[b] / count[b] : double.NaN);
        }
        return new EdgeProfile(profile.MoveToImmutable(), profileReference.MoveToImmutable(), [.. count]);
    }

    /// <summary>The scale of the smooth disk taken out before a spectrum is read, px (a Gaussian's sigma): past 0.05 cycles a pixel it passes all.</summary>
    public const double SmoothSigma = 6;

    /// <summary>
    /// <paramref name="plane"/>'s texture power inside <paramref name="disk"/>, ring by ring: the plane less itself smoothed by a Gaussian of
    /// <see cref="SmoothSigma"/>, then <see cref="PlanetaryWaveletGains.StackPower"/>. The disk's own shape holds most of its power, and
    /// through the taper it leaks into every ring alike; taken out first, it cannot pull a stack's ring toward its object's.
    /// </summary>
    public static ImmutableArray<double> TexturePower(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var smooth = PlanetaryInverse.Apply(plane, width, height, f => Math.Exp(-2 * Math.PI * Math.PI * SmoothSigma * SmoothSigma * f * f));
        var texture = new float[plane.Length];
        for (var i = 0; i < texture.Length; i++)
        {
            texture[i] = plane[i] - smooth[i];
        }
        return PlanetaryWaveletGains.StackPower(texture, width, height, disk);
    }

    /// <summary>
    /// A spectrum's transfer, ring by ring: the root of (<paramref name="stackPower"/> less <paramref name="noise"/>) over
    /// <paramref name="objectPower"/>, each ring averaged with its <paramref name="half"/> neighbours either side first, as power is
    /// noisy ring to ring; zero where nothing is left over the noise.
    /// </summary>
    public static RadialTransfer Spectrum(ImmutableArray<double> stackPower, ImmutableArray<double> noise, ImmutableArray<double> objectPower, int half = 2)
    {
        var n = stackPower.Length;
        var values = ImmutableArray.CreateBuilder<double>(n);
        for (var r = 0; r < n; r++)
        {
            double signal = 0, model = 0;
            for (var k = Math.Max(1, r - half); k <= Math.Min(n - 1, r + half); k++)
            {
                (signal, model) = (signal + stackPower[k] - noise[k], model + objectPower[k]);
            }
            values.Add(r == 0 ? 1 : model > 0 && signal > 0 ? Math.Sqrt(signal / model) : 0);
        }
        return new RadialTransfer(values.MoveToImmutable(), n);
    }

    /// <summary>
    /// (a) The physical kernel nearest the limb's <paramref name="edge"/> (<see cref="EdgeProfile.TransferAt"/>) from
    /// <paramref name="fromCyclesPerPixel"/> to <paramref name="toCyclesPerPixel"/> in steps of a hundredth: a coarse search over the seeing
    /// and the Gaussian, then Levenberg-Marquardt over all four numbers. The edge is read where it is good; the kernel carries it on to the
    /// cutoff in the shape its physics gives, where the edge is noise.
    /// </summary>
    public static PhysicalKernel FitPhysical(EdgeProfile edge, double cutoffCyclesPerPixel, double fromCyclesPerPixel = 0.02, double toCyclesPerPixel = 0.35)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var count = (int)Math.Round((toCyclesPerPixel - fromCyclesPerPixel) / 0.01) + 1;
        var (frequencies, read) = (new double[count], new double[count]);
        for (var i = 0; i < count; i++)
        {
            frequencies[i] = fromCyclesPerPixel + (i * 0.01);
            read[i] = edge.TransferAt(frequencies[i]);
        }
        PhysicalKernel Kernel(ReadOnlySpan<double> p) =>
            new PhysicalKernel(Math.Exp(p[0]), Math.Abs(p[1]), 0.5 / (1 + Math.Exp(-p[2])), Math.Exp(p[3]), cutoffCyclesPerPixel);
        double[] best = [];
        var bestCost = double.PositiveInfinity;
        foreach (var seeing in new[] { 0.5, 2, 8, 30 })
        {
            foreach (var sigma in new[] { 0.2, 0.6, 1.2 })
            {
                double[] start = [Math.Log(seeing), sigma, Math.Log(0.05 / 0.45), Math.Log(10)];
                var kernel = Kernel(start);
                var cost = 0.0;
                for (var i = 0; i < count; i++)
                {
                    // A frequency the edge does not read is left out here as in the fit below; counted, it made every start's cost NaN.
                    if (double.IsFinite(read[i]))
                    {
                        var r = read[i] - kernel.TransferAt(frequencies[i]);
                        cost += r * r;
                    }
                }
                if (cost < bestCost)
                {
                    (bestCost, best) = (cost, start);
                }
            }
        }
        if (best.Length == 0)
        {
            throw new ArgumentException("The edge reads nothing finite to fit a kernel to: no limb point was read.", nameof(edge));
        }
        var result = Stat.LevenbergMarquardt.Fit(best, count, (p, residuals) =>
        {
            var kernel = Kernel(p);
            for (var i = 0; i < count; i++)
            {
                residuals[i] = double.IsFinite(read[i]) ? read[i] - kernel.TransferAt(frequencies[i]) : 0;
            }
        }, [1e-4, 1e-4, 1e-4, 1e-4], maxIterations: 200);
        return Kernel(result.Parameters);
    }

    /// <summary>
    /// <paramref name="a"/>'s ring power over <paramref name="b"/>'s, averaged over the rings from <paramref name="from"/> to
    /// <paramref name="to"/> cycles a pixel: one map's texture against another's, the spectrum's kill line.
    /// </summary>
    public static double PowerRatio(ImmutableArray<double> a, ImmutableArray<double> b, double from, double to)
    {
        var n = a.Length;
        double sa = 0, sb = 0;
        for (var r = (int)Math.Ceiling(from * n); r <= Math.Min(n - 1, (int)Math.Floor(to * n)); r++)
        {
            (sa, sb) = (sa + a[r], sb + b[r]);
        }
        return sb > 0 ? sa / sb : double.NaN;
    }

    /// <summary>
    /// The extra Gaussian jitter <paramref name="across"/> carries over <paramref name="along"/> (R8 follow-up 4, part 2): least squares
    /// on ln(across / along) = c - 2 pi^2 sigma^2 f^2 from <paramref name="from"/> to <paramref name="to"/> cycles a pixel, in steps of
    /// 0.01, where both read above <paramref name="floor"/>. The intercept c takes a level the two reads differ by (a sector's albedo held
    /// from the nearest latitude the zonal profile reads), so it cannot pass for a jitter; zero where the slope is not a jitter's or fewer
    /// than three frequencies read.
    /// </summary>
    public static double JitterSigma(Func<double, double> along, Func<double, double> across, double from = 0.08, double to = 0.3, double floor = 0.05)
    {
        ArgumentNullException.ThrowIfNull(along);
        ArgumentNullException.ThrowIfNull(across);
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var f = from; f <= to + 1e-9; f += 0.01)
        {
            var (a, c) = (along(f), across(f));
            if (a > floor && c > floor)
            {
                var (x, y) = (2 * Math.PI * Math.PI * f * f, Math.Log(c / a));
                (n, sx, sy, sxx, sxy) = (n + 1, sx + x, sy + y, sxx + (x * x), sxy + (x * y));
            }
        }
        var slope = n >= 3 ? ((n * sxy) - (sx * sy)) / ((n * sxx) - (sx * sx)) : 0;
        return slope < 0 ? Math.Sqrt(-slope) : 0;
    }

    /// <summary>
    /// (e), the elongated kernel off the limb's edge: the physical kernel fitted to the edge read along the planet's axis
    /// (<paramref name="axis"/>, the polar limb), times the jitter the edge read along the equator (<paramref name="equator"/>, the sunlit
    /// limb) carries over it, along <paramref name="equatorDeg"/>.
    /// </summary>
    public static ElongatedKernel FitElongated(EdgeProfile axis, EdgeProfile equator, double cutoffCyclesPerPixel, double equatorDeg)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(equator);
        return new ElongatedKernel(FitPhysical(axis, cutoffCyclesPerPixel), JitterSigma(axis.TransferAt, equator.TransferAt), equatorDeg);
    }

    /// <summary>
    /// A 2-D transfer from its reads along two directions, interpolated as a jitter would make it: its logarithm linear in the squared
    /// sine of the frequency's angle from <paramref name="axisDeg"/>, <paramref name="along"/> there and <paramref name="across"/> a
    /// quarter turn away (each held above 1e-4, so the logarithm is defined).
    /// </summary>
    public static Func<double, double, double> TwoDirections(Func<double, double> along, Func<double, double> across, double axisDeg)
    {
        ArgumentNullException.ThrowIfNull(along);
        ArgumentNullException.ThrowIfNull(across);
        return (fx, fy) =>
        {
            var f = Math.Sqrt((fx * fx) + (fy * fy));
            var (a, c) = (Math.Max(along(f), 1e-4), Math.Max(across(f), 1e-4));
            var sine = Math.Sin(Math.Atan2(fy, fx) - (axisDeg * Math.PI / 180));
            return Math.Exp(Math.Log(a) + ((Math.Log(c) - Math.Log(a)) * sine * sine));
        };
    }
}

/// <summary>
/// (a) A stack's kernel over the pupil's diffraction from what is known of its physics (R8 follow-up 3): a lucky stack's residual seeing as
/// a short-exposure atmospheric transfer, exp(-A u^(5/3) (1 - u^(1/3))) with u the frequency over the cutoff D / lambda (Fried 1966, the
/// tilt taken out), times a Gaussian for what the alignment leaves (the warp, the registration's scatter), beside a share of the light in a
/// wide halo. The seeing term falls more slowly than a Gaussian toward the cutoff, which is where the limb fit's Gaussian models read band 1
/// low.
/// </summary>
/// <param name="Seeing">A, 3.44 (D / r0)^(5/3) for the r0 the kept frames see.</param>
/// <param name="SigmaPx">The Gaussian's sigma, px.</param>
/// <param name="Halo">The halo's share of the light.</param>
/// <param name="HaloWidthPx">The halo's Gaussian sigma, px.</param>
/// <param name="CutoffCyclesPerPixel">The pupil's cutoff, D / lambda, in cycles a pixel.</param>
public readonly record struct PhysicalKernel(double Seeing, double SigmaPx, double Halo, double HaloWidthPx, double CutoffCyclesPerPixel)
{
    /// <summary>The transfer at <paramref name="cyclesPerPixel"/>.</summary>
    public double TransferAt(double cyclesPerPixel)
    {
        var u = Math.Clamp(cyclesPerPixel / CutoffCyclesPerPixel, 0, 1);
        var seeing = Math.Exp(-Seeing * Math.Pow(u, 5.0 / 3) * (1 - Math.Pow(u, 1.0 / 3)));
        var f2 = cyclesPerPixel * cyclesPerPixel;
        var core = Math.Exp(-2 * Math.PI * Math.PI * SigmaPx * SigmaPx * f2);
        var halo = Math.Exp(-2 * Math.PI * Math.PI * HaloWidthPx * HaloWidthPx * f2);
        return ((1 - Halo) * seeing * core) + (Halo * halo);
    }

    /// <summary>D / r0 for the r0 the seeing term says: (A / 3.44)^(3/5).</summary>
    public double ApertureOverR0 => Math.Pow(Seeing / 3.44, 0.6);
}

/// <summary>
/// An elongated kernel (docs/plans/planetary-restoration.md, R8 follow-up 4, part 2): a round one (step 3's physical kernel, fitted to the
/// limb's edge along the planet's axis) times a Gaussian jitter of <paramref name="SigmaPx"/> along <paramref name="EquatorDeg"/>, the
/// direction a frame is placed only by the limb, so a stack's registration leaves its error wider there.
/// </summary>
/// <param name="Round">The kernel along the axis.</param>
/// <param name="SigmaPx">The extra jitter's sigma along the equator, px.</param>
/// <param name="EquatorDeg">The equator's direction in the image, degrees from +x toward +y.</param>
public readonly record struct ElongatedKernel(PhysicalKernel Round, double SigmaPx, double EquatorDeg)
{
    /// <summary>The transfer at the frequency (<paramref name="fx"/>, <paramref name="fy"/>), cycles a pixel.</summary>
    public double TransferAt(double fx, double fy)
    {
        var along = (fx * Math.Cos(EquatorDeg * Math.PI / 180)) + (fy * Math.Sin(EquatorDeg * Math.PI / 180));
        return Round.TransferAt(Math.Sqrt((fx * fx) + (fy * fy))) * Math.Exp(-2 * Math.PI * Math.PI * SigmaPx * SigmaPx * along * along);
    }
}

/// <summary>
/// An edge across a planet's limb, in bins of <see cref="PlanetaryFinestBand.EdgeBin"/> from <see cref="PlanetaryFinestBand.EdgeReach"/>
/// inside it to as far outside: the plane's mean, the reference's (its sharp model through what the read is over) and how many pixels each
/// bin holds.
/// </summary>
public sealed record EdgeProfile(ImmutableArray<double> Plane, ImmutableArray<double> Reference, ImmutableArray<int> Counts)
{
    /// <summary>
    /// The transfer at <paramref name="cyclesPerPixel"/>: the Fourier transform of the plane's line spread (the edge differentiated) over the
    /// reference's, both under one Hann window across the reach, in magnitude so an outline a fraction of a pixel off does not turn it.
    /// For a round kernel this is its radial transfer; the pixel, in both, cancels.
    /// </summary>
    public double TransferAt(double cyclesPerPixel)
    {
        var (plane, reference) = (Fill(Plane), Fill(Reference));
        Complex a = 0, b = 0;
        var n = plane.Length - 1;
        for (var i = 0; i < n; i++)
        {
            var d = ((i + 1) * PlanetaryFinestBand.EdgeBin) - PlanetaryFinestBand.EdgeReach;
            var outer = Math.Abs(d) / PlanetaryFinestBand.EdgeReach;
            var flat = 1 - PlanetaryFinestBand.EdgeTaper;
            var window = outer <= flat ? 1 : 0.5 * (1 + Math.Cos(Math.PI * (outer - flat) / PlanetaryFinestBand.EdgeTaper));
            var phase = Complex.FromPolarCoordinates(1, -2 * Math.PI * cyclesPerPixel * d);
            a += (plane[i + 1] - plane[i]) * window * phase;
            b += (reference[i + 1] - reference[i]) * window * phase;
        }
        return b.Magnitude > 0 ? a.Magnitude / b.Magnitude : double.NaN;
    }

    // The profile with any empty bin filled from its neighbours, so a gap is not a step.
    private static double[] Fill(ImmutableArray<double> profile)
    {
        double[] filled = [.. profile];
        for (var i = 0; i < filled.Length; i++)
        {
            if (double.IsFinite(filled[i]))
            {
                continue;
            }
            var (left, right) = (i - 1, i + 1);
            while (left >= 0 && !double.IsFinite(filled[left]))
            {
                left--;
            }
            while (right < filled.Length && !double.IsFinite(profile[right]))
            {
                right++;
            }
            filled[i] = left >= 0 && right < filled.Length ? (filled[left] + profile[right]) / 2 : left >= 0 ? filled[left] : right < filled.Length ? profile[right] : 0;
        }
        return filled;
    }
}
