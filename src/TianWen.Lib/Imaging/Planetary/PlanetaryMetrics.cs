using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Where a planet's disk lies in a plane, for the metrics to look: its centre and equatorial radius in pixels, its apparent
/// polar over equatorial radius, and the direction of its axis (from +x toward +y, degrees), so a distance from it is
/// counted in radii of the ellipse and Jupiter's polar limb, 6.5 % inside its equatorial one, is a limb and not the sky.
/// </summary>
public readonly record struct MetricDisk(double X, double Y, double Radius, double AxisRatio = 1, double AxisAngleDeg = 0)
{
    // The axis's direction, worked out once with the angle rather than at every pixel: RadiiAt runs at every pixel of every metric, and its
    // cosine and sine were most of a live master's limb drawing (#1201). Set again by the angle's own init, so a `with` that turns the disk
    // turns them too.
    private readonly double _axisAngleDeg = AxisAngleDeg;
    private readonly (double Cos, double Sin) _axis = Axis(AxisAngleDeg);

    /// <summary>The direction of the planet's axis in the image, degrees from +x toward +y.</summary>
    public double AxisAngleDeg
    {
        get => _axisAngleDeg;
        init => (_axisAngleDeg, _axis) = (value, Axis(value));
    }

    /// <summary>
    /// Saturn's rings about the disk, or none (S4 of docs/plans/planetary-restoration.md, #1184): what <see cref="ClearRadiiAt"/> and
    /// <see cref="RingTouched"/> read, so a metric's sky lies past the rings and its limb is read where they leave it clear.
    /// </summary>
    public DiskRings? Rings { get; init; }

    /// <summary>A limb fit's disk, with the ephemeris' axis ratio.</summary>
    public static MetricDisk From(in LimbFit fit, double axisRatio) => new(fit.CenterX, fit.CenterY, fit.EquatorialRadius, axisRatio, fit.AxisAngleDeg);

    /// <summary>A limb fit's disk with what its options say of it: the axis ratio and, for Saturn, its rings (<see cref="DiskRings.Of"/>).</summary>
    public static MetricDisk From(in LimbFit fit, LimbFitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return From(fit, options.AxisRatio) with { Rings = options.Rings is { } rings ? DiskRings.Of(fit, options, rings) : null };
    }

    /// <summary>Pixel (<paramref name="x"/>, <paramref name="y"/>)'s distance from the centre in radii of the ellipse: 1 on the limb.</summary>
    public double RadiiAt(double x, double y)
    {
        var (dx, dy) = (x - X, y - Y);
        var (cos, sin) = _axis;
        var along = (dx * cos) + (dy * sin);
        var across = (-dx * sin) + (dy * cos);
        return Math.Sqrt((across * across / (Radius * Radius)) + (along * along / (AxisRatio * AxisRatio * Radius * Radius)));
    }

    /// <summary>
    /// Pixel (<paramref name="x"/>, <paramref name="y"/>)'s distance from the planet in its radii, the rings counted: the nearer of
    /// <see cref="RadiiAt"/> and its ring-plane radius over the outer ring's, so 1 is the globe's limb along the axis and the A ring's edge
    /// along the equator, and the sky lies past both. <see cref="RadiiAt"/> without rings.
    /// </summary>
    public double ClearRadiiAt(double x, double y)
    {
        var r = RadiiAt(x, y);
        return Rings is { } rings ? Math.Min(r, RingPlaneRadiiAt(x, y, rings) / rings.OuterRadii) : r;
    }

    /// <summary>
    /// Whether a ring the observer sees lies within <paramref name="marginPx"/> of pixel (<paramref name="x"/>, <paramref name="y"/>):
    /// off the globe, or across it on the near side. Read at the pixel and eight points <paramref name="marginPx"/> about it. The rings'
    /// shadow on the globe lies within a pixel or two of their near half at Saturn's small phase, inside such a margin. False without rings.
    /// </summary>
    public bool RingTouched(double x, double y, double marginPx = 2)
    {
        if (Rings is not { } rings)
        {
            return false;
        }
        for (var j = -1; j <= 1; j++)
        {
            for (var i = -1; i <= 1; i++)
            {
                var (px, py) = (x + (i * marginPx), y + (j * marginPx));
                var rho = RingPlaneRadiiAt(px, py, rings);
                if (rho >= rings.InnerRadii && rho <= rings.OuterRadii && (RadiiAt(px, py) > 1 || Axes(px, py).Along * rings.NearSign > 0))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Pixel (<paramref name="x"/>, <paramref name="y"/>)'s radius in the rings' plane, in equatorial radii; NaN without rings.</summary>
    public double RingPlaneRadiiAt(double x, double y) => Rings is { } rings ? RingPlaneRadiiAt(x, y, rings) : double.NaN;

    // A point's ring-plane radius in equatorial radii: the plane is the equator's, seen at the rings' tilt.
    private double RingPlaneRadiiAt(double x, double y, DiskRings rings)
    {
        var (along, across) = Axes(x, y);
        var v = along / rings.SinB;
        return Math.Sqrt((across * across) + (v * v));
    }

    // A point's offsets along the axis and across it, in equatorial radii.
    private (double Along, double Across) Axes(double x, double y)
    {
        var (dx, dy) = (x - X, y - Y);
        var (cos, sin) = _axis;
        return (((dx * cos) + (dy * sin)) / Radius, ((-dx * sin) + (dy * cos)) / Radius);
    }

    private static (double Cos, double Sin) Axis(double angleDeg)
    {
        var angle = angleDeg * Math.PI / 180;
        return (Math.Cos(angle), Math.Sin(angle));
    }
}

/// <summary>
/// Saturn's rings as a <see cref="MetricDisk"/> reads them: their inner and outer edges in the planet's equatorial radii, the sine of their
/// tilt to the line of sight, and the side of the axis their near half crosses the globe on (+1 along the axis's direction, -1 against).
/// </summary>
public readonly record struct DiskRings(double InnerRadii, double OuterRadii, double SinB, int NearSign)
{
    /// <summary>The rings about a ringed limb fit's globe (S2), its north the end the rings told it.</summary>
    public static DiskRings Of(in LimbFit fit, LimbFitOptions options, SaturnRings rings) => Of(fit.NorthAngleDeg, fit.AxisAngleDeg, options, rings);

    /// <summary>
    /// The rings about a globe whose north points to <paramref name="northAngleDeg"/> and whose disk's axis lies at <paramref name="axisAngleDeg"/>
    /// (a twin's truth: its NORTHANG is both).
    /// </summary>
    public static DiskRings Of(double northAngleDeg, double axisAngleDeg, LimbFitOptions options, SaturnRings rings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rings);
        var inner = double.PositiveInfinity;
        foreach (var ring in rings.Rings)
        {
            inner = Math.Min(inner, ring.InnerKm);
        }
        var tilt = options.SubObserverLatitudeDeg * Math.PI / 180;
        // The near half crosses the globe on the side away from the pole the observer sees, the north when the tilt is positive.
        var north = Math.Cos((northAngleDeg - axisAngleDeg) * Math.PI / 180) >= 0 ? 1 : -1;
        return new DiskRings(inner / PhysicalEphemeris.Radii(CatalogIndex.Saturn).Equatorial, rings.OuterRadii, Math.Max(Math.Abs(Math.Sin(tilt)), 1e-3),
            tilt >= 0 ? -north : north);
    }
}

/// <summary>
/// One a trous band of a stack against the truth (docs/plans/planetary-restoration.md, R3): how much of the truth's detail
/// came back, the least-squares gain of the stack's band on the truth's (<paramref name="Transfer"/>, the pipeline's MTF in
/// that band), and what else is there, the band's RMS difference over the truth band's RMS (<paramref name="Error"/>).
/// </summary>
public readonly record struct BandFidelity(int Band, double Transfer, double Error);

/// <summary>
/// One a trous band of two stacks of the same capture from disjoint frames (T2): their correlation, the power both hold (the
/// mean of the product) and the noise (half the mean square of their difference). What both hold is detail; what one holds and
/// the other lacks is not.
/// </summary>
public readonly record struct BandAgreement(int Band, double Correlation, double Shared, double Noise);

/// <summary>
/// The metrics a planetary stack is judged by (docs/plans/planetary-restoration.md, R3), each on planes normalised by
/// <see cref="Normalise"/> (the sky 0, the disk's mean 1), so stacks of any scale compare: fidelity against a truth per wavelet
/// band, agreement between two halves per band (its truth-free twin), the limb's undershoot below the sky and its profile
/// against the truth's (ringing), and the power above a cutoff (fabrication).
/// </summary>
public static class PlanetaryMetrics
{
    /// <summary>How many a trous bands the metrics are read in, finest first.</summary>
    public const int Bands = 5;

    // Bands are compared inside this many radii, clear of the limb, whose edge the ringing metrics read instead.
    internal const double InnerRadii = 0.9;

    // The sky is read beyond this many radii, past the halo.
    private const double SkyRadii = 2.5;

    /// <summary>
    /// <paramref name="plane"/> less its sky (the median beyond 2.5 radii), over its disk's mean inside 0.8 radii: the disk about 1
    /// and the sky about 0, so a stack in any units and a truth in ADU compare.
    /// </summary>
    public static float[] Normalise(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var (level, scale) = NormalisationLevels(plane, width, height, disk);
        var result = new float[width * height];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (float)((plane[i] - level) / scale);
        }
        return result;
    }

    /// <summary>
    /// What <see cref="Normalise"/> divides by: the sky's level (<see cref="SkyLevel"/>, zero where none) and the disk's mean above it (inside
    /// 0.8 radii); a normalised value v is <c>level + (v * scale)</c> in the plane's own units again.
    /// </summary>
    public static (double Level, double Scale) NormalisationLevels(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        double diskSum = 0;
        var diskCount = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.RadiiAt(x, y) < 0.8)
                {
                    diskSum += plane[(y * width) + x];
                    diskCount++;
                }
            }
        }
        var level = SkyLevel(plane, width, height, disk) ?? 0;
        var scale = diskCount > 0 ? (diskSum / diskCount) - level : 1;
        return (level, scale);
    }

    // How far out the limb's profile is read (LimbUndershoot, LimbRebound), in equatorial radii.
    private const double ProfileReach = 1.3;

    /// <summary>
    /// The sky's level in <paramref name="plane"/>: the median past the sky's radii (2.5). A tight crop can leave no pixel there (a
    /// 200 px PIPP crop of 2022-10-09's 150 px Jupiter), and then it is the median of the farthest tenth of the pixels past the
    /// limb's profile (1.3 radii), which the planet's halo still lifts, so an undershoot read against it errs large, never small
    /// (set down 2026-10-03 after the pipeline's real-capture validation read NaN there). Null with no pixel past 1.3 radii.
    /// </summary>
    public static double? SkyLevel(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var sky = new List<double>();
        var outer = new List<(double Radii, double Value)>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Past Saturn's rings as well as its globe (S4).
                var r = disk.ClearRadiiAt(x, y);
                if (r >= SkyRadii)
                {
                    sky.Add(plane[(y * width) + x]);
                }
                else if (r >= ProfileReach)
                {
                    outer.Add((r, plane[(y * width) + x]));
                }
            }
        }
        if (sky.Count > 0)
        {
            return StatisticsHelper.MedianFast(sky.ToArray());
        }
        if (outer.Count == 0)
        {
            return null;
        }
        outer.Sort((a, b) => b.Radii.CompareTo(a.Radii));
        var farthest = new double[Math.Max(1, outer.Count / 10)];
        for (var i = 0; i < farthest.Length; i++)
        {
            farthest[i] = outer[i].Value;
        }
        return StatisticsHelper.MedianFast(farthest);
    }

    /// <summary>
    /// <paramref name="plane"/> moved by (<paramref name="dx"/>, <paramref name="dy"/>) pixels by the Fourier shift theorem, on a grid
    /// padded with zeros to a power of two: exact for a band-limited plane whose sky is zero (a normalised one), where interpolating
    /// would blur its finest band.
    /// </summary>
    public static float[] Shift(ReadOnlySpan<float> plane, int width, int height, double dx, double dy)
    {
        var n = 1;
        while (n < Math.Max(width, height) + (2 * (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)))))
        {
            n <<= 1;
        }
        var field = new Complex[n * n];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                field[(y * n) + x] = plane[(y * width) + x];
            }
        }
        Fft2D.Forward(field, n, n);
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                field[(ky * n) + kx] *= Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * dx) + (fy * dy)));
            }
        }
        Fft2D.Inverse(field, n, n);
        var result = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[(y * width) + x] = (float)field[(y * n) + x].Real;
            }
        }
        return result;
    }

    /// <summary>
    /// The 2-pixel lattice a demosaic leaves of the CFA's residue (#1187, #1376), inside 0.9 radii, in the <see cref="Normalise"/>d disk's
    /// level: the mean over the 2 by 2 blocks the sensor's grid makes (even x and y, all four pixels inside) of each block's part that
    /// alternates along x, along y and on the diagonal. The lattice is locked to the sensor, so its sign holds over the disk and the mean
    /// keeps it, while the planet's own detail at those frequencies has no fixed phase and averages away. Read over whole blocks, a level
    /// cancels in each; a sign summed pixel by pixel over the disk did not, since a row inside it with an odd count leaves one pixel over
    /// (a hundredth planted read 0.0122). Read it per channel: it cancels in the luminance.
    /// </summary>
    public static (double AlongX, double AlongY, double Diagonal) Lattice(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var normalised = Normalise(plane, width, height, disk);
        double x = 0, y = 0, diagonal = 0;
        var blocks = 0;
        for (var row = 0; row + 1 < height; row += 2)
        {
            for (var column = 0; column + 1 < width; column += 2)
            {
                if (disk.RadiiAt(column, row) >= InnerRadii || disk.RadiiAt(column + 1, row) >= InnerRadii
                    || disk.RadiiAt(column, row + 1) >= InnerRadii || disk.RadiiAt(column + 1, row + 1) >= InnerRadii)
                {
                    continue;
                }
                var i = (row * width) + column;
                double a = normalised[i], b = normalised[i + 1], c = normalised[i + width], d = normalised[i + width + 1];
                x += (a - b + c - d) / 4;
                y += (a + b - c - d) / 4;
                diagonal += (a - b - c + d) / 4;
                blocks++;
            }
        }
        var n = Math.Max(blocks, 1);
        return (Math.Abs(x / n), Math.Abs(y / n), Math.Abs(diagonal / n));
    }

    /// <summary>
    /// A stack's fidelity against the truth, band by band, inside 0.9 radii (both normalised and registered on the same disk):
    /// the pipeline's transfer in each band and the error it left.
    /// </summary>
    public static ImmutableArray<BandFidelity> Fidelity(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk, int bands = Bands)
    {
        var s = ATrousWaveletTransform.Decompose(stack, width, height, bands);
        var t = ATrousWaveletTransform.Decompose(truth, width, height, bands);
        var inside = Inside(width, height, disk, InnerRadii);
        var result = ImmutableArray.CreateBuilder<BandFidelity>(bands);
        for (var j = 0; j < bands; j++)
        {
            var sj = s.Detail(j);
            var tj = t.Detail(j);
            double st = 0, tt = 0, dd = 0;
            foreach (var i in inside)
            {
                st += (double)sj[i] * tj[i];
                tt += (double)tj[i] * tj[i];
                dd += ((double)sj[i] - tj[i]) * (sj[i] - tj[i]);
            }
            result.Add(new BandFidelity(j + 1, tt > 0 ? st / tt : double.NaN, tt > 0 ? Math.Sqrt(dd / tt) : double.NaN));
        }
        return result.MoveToImmutable();
    }

    /// <summary>
    /// <paramref name="stack"/>'s transfer over another's in each band inside 0.9 radii, that other read as two stacks of disjoint
    /// frames, <paramref name="half1"/> and <paramref name="half2"/>: the cross term of the stack with the first half over that of
    /// the two halves (docs/plans/planetary-restoration.md, R7 part 2). Independent noise enters neither, so the ratio is the
    /// stack's extra blur over the halves' frames with no floor to estimate, provided the stack shares no frame with either half.
    /// All three normalised and registered on the same disk.
    /// </summary>
    public static ImmutableArray<double> CrossTransfer(ReadOnlySpan<float> stack, ReadOnlySpan<float> half1, ReadOnlySpan<float> half2, int width, int height, MetricDisk disk,
        int bands = Bands)
    {
        var s = ATrousWaveletTransform.Decompose(stack, width, height, bands);
        var a = ATrousWaveletTransform.Decompose(half1, width, height, bands);
        var b = ATrousWaveletTransform.Decompose(half2, width, height, bands);
        var inside = Inside(width, height, disk, InnerRadii);
        var result = ImmutableArray.CreateBuilder<double>(bands);
        for (var j = 0; j < bands; j++)
        {
            var sj = s.Detail(j);
            var aj = a.Detail(j);
            var bj = b.Detail(j);
            double sa = 0, ab = 0;
            foreach (var i in inside)
            {
                sa += (double)sj[i] * aj[i];
                ab += (double)aj[i] * bj[i];
            }
            result.Add(ab > 0 ? sa / ab : double.NaN);
        }
        return result.MoveToImmutable();
    }

    /// <summary>
    /// Two stacks from disjoint halves of a capture, band by band, inside 0.9 radii (both normalised and registered on the same
    /// disk): the truth-free twin of <see cref="Fidelity"/>.
    /// </summary>
    public static ImmutableArray<BandAgreement> SplitHalf(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int width, int height, MetricDisk disk, int bands = Bands)
    {
        var da = ATrousWaveletTransform.Decompose(a, width, height, bands);
        var db = ATrousWaveletTransform.Decompose(b, width, height, bands);
        var inside = Inside(width, height, disk, InnerRadii);
        var result = ImmutableArray.CreateBuilder<BandAgreement>(bands);
        for (var j = 0; j < bands; j++)
        {
            var aj = da.Detail(j);
            var bj = db.Detail(j);
            double ab = 0, aa = 0, bb = 0, diff = 0;
            foreach (var i in inside)
            {
                ab += (double)aj[i] * bj[i];
                aa += (double)aj[i] * aj[i];
                bb += (double)bj[i] * bj[i];
                diff += ((double)aj[i] - bj[i]) * (aj[i] - bj[i]);
            }
            var count = Math.Max(1, inside.Count);
            result.Add(new BandAgreement(j + 1, aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : double.NaN, ab / count, diff / count / 2));
        }
        return result.MoveToImmutable();
    }

    /// <summary>
    /// How far below the sky the limb's profile falls just outside it (1 to 1.3 radii), in the disk's brightness (a normalised
    /// plane's units), zero when it never does: the dark ring a sharpener or a deconvolution leaves outside a bright edge.
    /// Measured against the sky's median beyond 2.5 radii. Counted in the sky's noise instead, as the plan first had it, it
    /// read the stack's noise as much as its ringing: one sharpening on 2022-09-03's synthetic twin went from 840 to 5,700
    /// sky sigmas as the frames stacked went from 60 to 3,000, because the sky's noise fell and the ring did not.
    /// </summary>
    public static double LimbUndershoot(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var profile = Profile(plane, width, height, disk, 1.0, ProfileReach);
        if (SkyLevel(plane, width, height, disk) is not { } median || profile.Length == 0)
        {
            return double.NaN;
        }
        var lowest = double.PositiveInfinity;
        foreach (var v in profile)
        {
            if (double.IsFinite(v))
            {
                lowest = Math.Min(lowest, v);
            }
        }
        return double.IsFinite(lowest) ? Math.Max(0, median - lowest) : double.NaN;
    }

    /// <summary>
    /// The limb's trough (#1171): how far <paramref name="plane"/>'s azimuthal profile falls below <paramref name="reference"/>'s between
    /// 1.0 and 1.1 radii at most, zero where it never does. Against a truth, a sharpening dark just outside the limb; against the stack it
    /// was sharpened from, the truth-free reading, which also counts the glow a sharpening rightly takes back inward.
    /// </summary>
    public static double LimbTrough(ReadOnlySpan<float> plane, ReadOnlySpan<float> reference, int width, int height, MetricDisk disk)
    {
        var (p, r) = (Profile(plane, width, height, disk, 1.0, 1.1), Profile(reference, width, height, disk, 1.0, 1.1));
        var deepest = 0.0;
        for (var i = 0; i < Math.Min(p.Length, r.Length); i++)
        {
            if (double.IsFinite(p[i]) && double.IsFinite(r[i]))
            {
                deepest = Math.Max(deepest, r[i] - p[i]);
            }
        }
        return deepest;
    }

    /// <summary>
    /// The limb read sector by sector (#1171): for each of <paramref name="sectors"/> position angles (sector k centred on
    /// <c>(k + 0.5) * 360 / sectors</c> degrees, from +x toward +y), the radius in radii of <paramref name="disk"/> at which
    /// <paramref name="plane"/>'s profile in that sector falls through half its level just inside the limb (0.85 to 0.9 radii), read
    /// outward from 0.9 radii and interpolated between rings of 0.02 radii; NaN where it never falls that far. A correct outline reads
    /// alike in every sector; one off the planet's centre reads a first harmonic (<see cref="Harmonic"/>), a misshapen one a second.
    /// </summary>
    public static double[] SectorHalfLevelRadii(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, int sectors)
    {
        const double inner = 0.8, outer = 1.4, step = 0.02;
        var profiles = SectorProfiles(plane, width, height, disk, sectors, inner, outer, step);
        var radii = new double[sectors];
        for (var k = 0; k < sectors; k++)
        {
            var profile = profiles[k];
            var (levelSum, levelCount) = (0.0, 0);
            for (var i = 0; i < profile.Length; i++)
            {
                var r = inner + ((i + 0.5) * step);
                if (r >= 0.85 && r < 0.9 && double.IsFinite(profile[i]))
                {
                    (levelSum, levelCount) = (levelSum + profile[i], levelCount + 1);
                }
            }
            radii[k] = double.NaN;
            if (levelCount == 0)
            {
                continue;
            }
            var half = levelSum / levelCount / 2;
            for (var i = 1; i < profile.Length; i++)
            {
                var (r0, r1) = (inner + ((i - 0.5) * step), inner + ((i + 0.5) * step));
                if (r0 < 0.9 || !double.IsFinite(profile[i - 1]) || !double.IsFinite(profile[i]))
                {
                    continue;
                }
                if (profile[i - 1] >= half && profile[i] < half)
                {
                    radii[k] = r0 + ((r1 - r0) * (profile[i - 1] - half) / (profile[i - 1] - profile[i]));
                    break;
                }
            }
        }
        return radii;
    }

    /// <summary>The limb's trough (<see cref="LimbTrough"/>) sector by sector, the sectors as <see cref="SectorHalfLevelRadii"/> takes them.</summary>
    public static double[] SectorTroughs(ReadOnlySpan<float> plane, ReadOnlySpan<float> reference, int width, int height, MetricDisk disk, int sectors)
    {
        var (p, r) = (SectorProfiles(plane, width, height, disk, sectors, 1.0, 1.1, 0.01), SectorProfiles(reference, width, height, disk, sectors, 1.0, 1.1, 0.01));
        var troughs = new double[sectors];
        for (var k = 0; k < sectors; k++)
        {
            for (var i = 0; i < p[k].Length; i++)
            {
                if (double.IsFinite(p[k][i]) && double.IsFinite(r[k][i]))
                {
                    troughs[k] = Math.Max(troughs[k], r[k][i] - p[k][i]);
                }
            }
        }
        return troughs;
    }

    /// <summary>
    /// The <paramref name="order"/>-th harmonic of <paramref name="values"/> read at evenly spaced position angles (value k at
    /// <c>(k + 0.5) * 360 / n</c> degrees): its amplitude (half the peak-to-peak of that harmonic alone) and the angle, degrees, of its
    /// first maximum. Values that are NaN are left out.
    /// </summary>
    public static (double Amplitude, double AngleDeg) Harmonic(ReadOnlySpan<double> values, int order)
    {
        var (re, im, count) = (0.0, 0.0, 0);
        for (var k = 0; k < values.Length; k++)
        {
            if (!double.IsFinite(values[k]))
            {
                continue;
            }
            var theta = order * (k + 0.5) * 2 * Math.PI / values.Length;
            (re, im, count) = (re + (values[k] * Math.Cos(theta)), im + (values[k] * Math.Sin(theta)), count + 1);
        }
        if (count == 0)
        {
            return (double.NaN, double.NaN);
        }
        var angle = Math.Atan2(im, re) * 180 / Math.PI / order;
        return (2 * Math.Sqrt((re * re) + (im * im)) / count, ((angle % (360.0 / order)) + (360.0 / order)) % (360.0 / order));
    }

    // Each sector's azimuthal profile from `inner` to `outer` radii in rings of `step`, sector k holding the position angles from
    // k * 360 / sectors to (k + 1) * 360 / sectors degrees (from +x toward +y).
    private static double[][] SectorProfiles(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, int sectors, double inner, double outer, double step)
    {
        var rings = (int)Math.Round((outer - inner) / step);
        var (sum, count) = (new double[sectors, rings], new int[sectors, rings]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var ring = (int)Math.Floor((disk.RadiiAt(x, y) - inner) / step);
                if (ring < 0 || ring >= rings || disk.RingTouched(x, y))
                {
                    continue;
                }
                var angle = Math.Atan2(y - disk.Y, x - disk.X) * 180 / Math.PI;
                var sector = Math.Min(sectors - 1, (int)Math.Floor(((angle + 360) % 360) / (360.0 / sectors)));
                sum[sector, ring] += plane[(y * width) + x];
                count[sector, ring]++;
            }
        }
        var profiles = new double[sectors][];
        for (var k = 0; k < sectors; k++)
        {
            profiles[k] = new double[rings];
            for (var i = 0; i < rings; i++)
            {
                profiles[k][i] = count[k, i] > 0 ? sum[k, i] / count[k, i] : double.NaN;
            }
        }
        return profiles;
    }

    /// <summary>
    /// The limb's rebound: the most <paramref name="plane"/>'s azimuthal profile (normalised, the sky zero and the disk one) climbs back
    /// above its own running minimum going out from 1.0 to 1.3 radii. A planet's profile only falls outside its limb (the glow of its
    /// diffraction and its blur), a stack's with it, so a rise there is a ring a sharpening put in (#1168); zero on a profile that never
    /// climbs. The truth-free reading of what <see cref="LimbUndershoot"/>, which reads below the sky only, cannot see.
    /// </summary>
    public static double LimbRebound(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var profile = Profile(plane, width, height, disk, 1.0, ProfileReach);
        var (lowest, rebound) = (double.PositiveInfinity, 0.0);
        foreach (var v in profile)
        {
            if (double.IsFinite(v))
            {
                lowest = Math.Min(lowest, v);
                rebound = Math.Max(rebound, v - lowest);
            }
        }
        return double.IsFinite(lowest) ? rebound : double.NaN;
    }

    /// <summary>
    /// The brightest compact sources outside <paramref name="disk"/>'s limb (a moon, a star), brightest first and at least
    /// <paramref name="apartPx"/> apart: local maxima beyond 1.05 radii whose peak stands above the median of the box about them
    /// (<see cref="SourcePeak"/>) by more than <paramref name="sigmas"/> times the sky's robust noise beyond <see cref="SkyRadii"/>, and by
    /// at least <paramref name="minimumPeak"/> (in the plane's units; the disk is one in a normalised plane), which a stack whose sky is
    /// all but noiseless still needs (#1181: the bounded limb fix lets these keep their sharpening, and the limb fixes are scored by them);
    /// and above the plane their surroundings make by as much (<see cref="PeakAbovePlane"/>), since on a planet's steep glow a noise
    /// maximum stands above its box's median too (#1301).
    /// </summary>
    public static ImmutableArray<(int X, int Y)> CompactSources(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, int count = 3,
        double sigmas = 20, int apartPx = 12, double minimumPeak = 0.01)
    {
        var sky = new List<float>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.ClearRadiiAt(x, y) >= SkyRadii)
                {
                    sky.Add(plane[(y * width) + x]);
                }
            }
        }
        if (sky.Count == 0)
        {
            return [];
        }
        var values = sky.ToArray();
        var median = StatisticsHelper.MedianFast(values);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Math.Abs(values[i] - median);
        }
        var noise = 1.4826 * StatisticsHelper.MedianFast(values);
        var candidates = new List<(int X, int Y, double Peak)>();
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var v = plane[(y * width) + x];
                // Saturn's ring ansae are local maxima too, and no moons (S4).
                if (v <= median + Math.Max(sigmas * noise, minimumPeak) || disk.ClearRadiiAt(x, y) <= 1.05 || !IsLocalMaximum(plane, width, x, y))
                {
                    continue;
                }
                var (peak, _) = SourcePeak(plane, width, height, x, y);
                var threshold = Math.Max(sigmas * noise, minimumPeak);
                if (peak > threshold && PeakAbovePlane(plane, width, height, x, y) > threshold)
                {
                    candidates.Add((x, y, peak));
                }
            }
        }
        var chosen = ImmutableArray.CreateBuilder<(int X, int Y)>();
        foreach (var c in candidates.OrderByDescending(c => c.Peak))
        {
            if (chosen.Count == count)
            {
                break;
            }
            if (chosen.All(s => Math.Abs(s.X - c.X) >= apartPx || Math.Abs(s.Y - c.Y) >= apartPx))
            {
                chosen.Add((c.X, c.Y));
            }
        }
        return chosen.ToImmutable();
    }

    /// <summary>
    /// A compact source's peak at (<paramref name="x"/>, <paramref name="y"/>), the brightest pixel within 2 px, above the median of the
    /// 25 px box about it, and how many pixels of the 13 px box stand above half that peak: its height and its width, in the plane's units.
    /// </summary>
    public static (double Peak, int HalfPeakPixels) SourcePeak(ReadOnlySpan<float> plane, int width, int height, int x, int y)
    {
        var box = new List<float>();
        for (var dy = -12; dy <= 12; dy++)
        {
            for (var dx = -12; dx <= 12; dx++)
            {
                var (sx, sy) = (x + dx, y + dy);
                if (sx >= 0 && sx < width && sy >= 0 && sy < height)
                {
                    box.Add(plane[(sy * width) + sx]);
                }
            }
        }
        var level = StatisticsHelper.MedianFast(box.ToArray());
        var top = float.NegativeInfinity;
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var (sx, sy) = (x + dx, y + dy);
                if (sx >= 0 && sx < width && sy >= 0 && sy < height)
                {
                    top = Math.Max(top, plane[(sy * width) + sx]);
                }
            }
        }
        var peak = top - level;
        var above = 0;
        for (var dy = -6; dy <= 6; dy++)
        {
            for (var dx = -6; dx <= 6; dx++)
            {
                var (sx, sy) = (x + dx, y + dy);
                if (sx >= 0 && sx < width && sy >= 0 && sy < height && plane[(sy * width) + sx] - level > peak / 2)
                {
                    above++;
                }
            }
        }
        return (peak, above);
    }

    /// <summary>The side of the square about a compact source its surroundings are read in: <see cref="SourcePeak"/>'s 25 px box.</summary>
    public const int SourceBox = 25;

    /// <summary>
    /// How far the compact source at (<paramref name="x"/>, <paramref name="y"/>) stands above the plane its surroundings make: the brightest
    /// pixel within 2 px, as <see cref="SourcePeak"/> takes it, less <see cref="SurroundPlane"/> there. A box's median lies on the dark side of
    /// a glow, so a noise maximum on its bright side stands above the median as a source does (#1301: blue's beside Saturn's ring tip, 0.0136
    /// of the disk); above the plane it does not.
    /// </summary>
    public static double PeakAbovePlane(ReadOnlySpan<float> plane, int width, int height, int x, int y)
    {
        var patch = Patch(plane, width, height, x, y);
        if (SurroundPlane(patch) is not { } surround)
        {
            return double.NaN;
        }
        var (top, tx, ty) = (double.NegativeInfinity, 0, 0);
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var v = patch[((dy + (SourceBox / 2)) * SourceBox) + dx + (SourceBox / 2)];
                if (v > top)
                {
                    (top, tx, ty) = (v, dx, dy);
                }
            }
        }
        return top - (surround.A + (surround.B * tx) + (surround.C * ty));
    }

    /// <summary>The <see cref="SourceBox"/> square of <paramref name="plane"/> about (<paramref name="x"/>, <paramref name="y"/>), row by row, NaN off the frame.</summary>
    public static float[] Patch(ReadOnlySpan<float> plane, int width, int height, int x, int y)
    {
        const int half = SourceBox / 2;
        var patch = new float[SourceBox * SourceBox];
        for (var dy = -half; dy <= half; dy++)
        {
            for (var dx = -half; dx <= half; dx++)
            {
                var (sx, sy) = (x + dx, y + dy);
                patch[((dy + half) * SourceBox) + dx + half] = sx >= 0 && sx < width && sy >= 0 && sy < height ? plane[(sy * width) + sx] : float.NaN;
            }
        }
        return patch;
    }

    /// <summary>
    /// The plane z = A + B dx + C dy, (dx, dy) from the middle, fitted by least squares to the pixels of <paramref name="patch"/> (a
    /// <see cref="SourceBox"/> square, NaN skipped) beyond <see cref="PlanetaryDering.MoonReachPx"/> of its middle: the surface a source's
    /// surroundings make there. Null with too few to fix one.
    /// </summary>
    public static (double A, double B, double C)? SurroundPlane(ReadOnlySpan<float> patch)
    {
        const int half = SourceBox / 2;
        const int reach = PlanetaryDering.MoonReachPx;
        double n = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sz = 0, sxz = 0, syz = 0;
        for (var dy = -half; dy <= half; dy++)
        {
            for (var dx = -half; dx <= half; dx++)
            {
                var z = patch[((dy + half) * SourceBox) + dx + half];
                if ((dx * dx) + (dy * dy) <= reach * reach || !float.IsFinite(z))
                {
                    continue;
                }
                (n, sx, sy, sxx, syy, sxy) = (n + 1, sx + dx, sy + dy, sxx + (dx * dx), syy + (dy * dy), sxy + (dx * dy));
                (sz, sxz, syz) = (sz + z, sxz + (dx * z), syz + (dy * z));
            }
        }
        // The normal equations [n sx sy; sx sxx sxy; sy sxy syy] (A B C) = (sz sxz syz), through the one normal-equation solve.
        if (n < 3)
        {
            return null;
        }
        var normal = new double[,] { { n, sx, sy }, { sx, sxx, sxy }, { sy, sxy, syy } };
        return PolynomialLeastSquares.SolveNormalEquations(normal, [sz, sxz, syz]) is { } abc ? (abc[0], abc[1], abc[2]) : null;
    }

    private static bool IsLocalMaximum(ReadOnlySpan<float> plane, int width, int x, int y)
    {
        var v = plane[(y * width) + x];
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx != 0 || dy != 0) && plane[((y + dy) * width) + x + dx] > v)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The RMS difference between a stack's limb profile and the truth's over 0.8 to 1.2 radii (both normalised and registered):
    /// a limb that rings, or is softer or sharper than the truth's.
    /// </summary>
    public static double LimbProfileError(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk) =>
        LimbProfileError(stack, truth, width, height, disk, 0.8, 1.2);

    /// <summary>
    /// The reach either side of the limb, px, over which planets of different sizes in pixels compare: a profile from 0.8 to 1.2 radii is
    /// 12 px across a 30 px Saturn, nearly all of it the edge, and 19 across a 48 px Jupiter, whose flatter bins dilute its RMS (S4, #1184).
    /// </summary>
    public const double LimbReachPx = 6;

    /// <summary>The RMS difference between a stack's limb profile and the truth's from <paramref name="inner"/> to <paramref name="outer"/> radii.</summary>
    public static double LimbProfileError(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk, double inner, double outer)
    {
        var (s, t) = (Profile(stack, width, height, disk, inner, outer), Profile(truth, width, height, disk, inner, outer));
        double squares = 0;
        var count = 0;
        for (var i = 0; i < Math.Min(s.Length, t.Length); i++)
        {
            if (double.IsFinite(s[i]) && double.IsFinite(t[i]))
            {
                squares += (s[i] - t[i]) * (s[i] - t[i]);
                count++;
            }
        }
        return count > 0 ? Math.Sqrt(squares / count) : double.NaN;
    }

    /// <summary>
    /// Saturn's rings against the truth's (S4 of docs/plans/planetary-restoration.md, #1184), each read as a radial profile in the rings'
    /// plane off the globe (both ansae, in steps of a fiftieth of an equatorial radius): the RMS difference across the rings, from the C
    /// ring's inner edge to the A ring's outer one (<paramref name="Rings"/>), and the most the plane stands above the truth past their
    /// edge, out to 1.3 times it (<paramref name="PastTheEdge"/>, what a sharpening drew in the sky clear of them). NaN without rings.
    /// </summary>
    public static (double Rings, double PastTheEdge) RingProfileError(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk)
    {
        if (disk.Rings is not { } rings)
        {
            return (double.NaN, double.NaN);
        }
        const double Step = 0.02;
        var (inner, outer) = (rings.InnerRadii, rings.OuterRadii * 1.3);
        var (s, t) = (RingProfile(stack, width, height, disk, inner, outer, Step), RingProfile(truth, width, height, disk, inner, outer, Step));
        double squares = 0, past = double.NegativeInfinity;
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (!double.IsFinite(s[i]) || !double.IsFinite(t[i]))
            {
                continue;
            }
            if (inner + ((i + 0.5) * Step) <= rings.OuterRadii)
            {
                squares += (s[i] - t[i]) * (s[i] - t[i]);
                count++;
            }
            else
            {
                past = Math.Max(past, s[i] - t[i]);
            }
        }
        return (count > 0 ? Math.Sqrt(squares / count) : double.NaN, double.IsFinite(past) ? past : double.NaN);
    }

    // The mean of `plane` in bins of ring-plane radius, from `inner` to `outer` equatorial radii, over the pixels off the globe.
    private static double[] RingProfile(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, double inner, double outer, double step)
    {
        var bins = (int)Math.Ceiling((outer - inner) / step);
        var (sum, count) = (new double[bins], new int[bins]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.RadiiAt(x, y) <= 1.05)
                {
                    continue;
                }
                var bin = (int)Math.Floor((disk.RingPlaneRadiiAt(x, y) - inner) / step);
                if (bin >= 0 && bin < bins)
                {
                    sum[bin] += plane[(y * width) + x];
                    count[bin]++;
                }
            }
        }
        var profile = new double[bins];
        for (var i = 0; i < bins; i++)
        {
            profile[i] = count[i] > 0 ? sum[i] / count[i] : double.NaN;
        }
        return profile;
    }

    /// <summary>
    /// The share of a disk's power above <paramref name="cutoff"/> cycles a pixel, in a cosine-tapered square of 2.6 radii around
    /// it, less the mean: what a restoration must not raise past the input's, since no telescope passes detail beyond its
    /// cutoff. NaN when the grid samples nothing past the cutoff (a prime-focus capture whose cutoff is past Nyquist).
    /// </summary>
    public static double PowerAbove(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, double cutoff)
    {
        if (cutoff >= Math.Sqrt(0.5))
        {
            return double.NaN;
        }
        var half = (int)Math.Ceiling(1.3 * disk.Radius);
        var n = 1;
        while (n < 2 * half)
        {
            n <<= 1;
        }
        var field = new Complex[n * n];
        var (x0, y0) = ((int)Math.Round(disk.X) - (n / 2), (int)Math.Round(disk.Y) - (n / 2));
        double mean = 0;
        var count = 0;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var (px, py) = (x + x0, y + y0);
                if (px >= 0 && py >= 0 && px < width && py < height)
                {
                    mean += plane[(py * width) + px];
                    count++;
                }
            }
        }
        mean = count > 0 ? mean / count : 0;
        for (var y = 0; y < n; y++)
        {
            var wy = 0.5 - (0.5 * Math.Cos(2 * Math.PI * (y + 0.5) / n));
            for (var x = 0; x < n; x++)
            {
                var (px, py) = (x + x0, y + y0);
                var v = px >= 0 && py >= 0 && px < width && py < height ? plane[(py * width) + px] - mean : 0;
                field[(y * n) + x] = v * wy * (0.5 - (0.5 * Math.Cos(2 * Math.PI * (x + 0.5) / n)));
            }
        }
        Fft2D.Forward(field, n, n);
        double above = 0, total = 0;
        for (var ky = 0; ky < n; ky++)
        {
            var fy = (ky < n / 2 ? ky : ky - n) / (double)n;
            for (var kx = 0; kx < n; kx++)
            {
                var fx = (kx < n / 2 ? kx : kx - n) / (double)n;
                if (kx == 0 && ky == 0)
                {
                    continue;
                }
                var power = field[(ky * n) + kx].Magnitude * field[(ky * n) + kx].Magnitude;
                total += power;
                if (Math.Sqrt((fx * fx) + (fy * fy)) > cutoff)
                {
                    above += power;
                }
            }
        }
        return total > 0 ? above / total : double.NaN;
    }

    // The pixels inside `radii` of the disk, row-major indices.
    internal static List<int> Inside(int width, int height, MetricDisk disk, double radii)
    {
        var inside = new List<int>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.RadiiAt(x, y) < radii)
                {
                    inside.Add((y * width) + x);
                }
            }
        }
        return inside;
    }

    // The mean of the plane over rings from `inner` to `outer` radii, a hundredth of a radius wide; NaN in a ring with no pixel.
    private static double[] Profile(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, double inner, double outer)
    {
        var rings = (int)Math.Round((outer - inner) * 100);
        var (sum, count) = (new double[rings], new int[rings]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var ring = (int)Math.Floor((disk.RadiiAt(x, y) - inner) * 100);
                if (ring >= 0 && ring < rings && !disk.RingTouched(x, y))
                {
                    sum[ring] += plane[(y * width) + x];
                    count[ring]++;
                }
            }
        }
        var profile = new double[rings];
        for (var i = 0; i < rings; i++)
        {
            profile[i] = count[i] > 0 ? sum[i] / count[i] : double.NaN;
        }
        return profile;
    }
}
