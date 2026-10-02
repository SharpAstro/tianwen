using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Where a planet's disk lies in a plane, for the metrics to look: its centre and equatorial radius in pixels, its apparent
/// polar over equatorial radius, and the direction of its axis (from +x toward +y, degrees), so a distance from it is
/// counted in radii of the ellipse and Jupiter's polar limb, 6.5 % inside its equatorial one, is a limb and not the sky.
/// </summary>
public readonly record struct MetricDisk(double X, double Y, double Radius, double AxisRatio = 1, double AxisAngleDeg = 0)
{
    /// <summary>A limb fit's disk, with the ephemeris' axis ratio.</summary>
    public static MetricDisk From(in LimbFit fit, double axisRatio) => new(fit.CenterX, fit.CenterY, fit.EquatorialRadius, axisRatio, fit.AxisAngleDeg);

    /// <summary>Pixel (<paramref name="x"/>, <paramref name="y"/>)'s distance from the centre in radii of the ellipse: 1 on the limb.</summary>
    public double RadiiAt(double x, double y)
    {
        var (dx, dy) = (x - X, y - Y);
        var angle = AxisAngleDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var along = (dx * cos) + (dy * sin);
        var across = (-dx * sin) + (dy * cos);
        return Math.Sqrt((across * across / (Radius * Radius)) + (along * along / (AxisRatio * AxisRatio * Radius * Radius)));
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
    /// What <see cref="Normalise"/> divides by: the sky's level (the median past the sky's radii) and the disk's mean above it (inside
    /// 0.8 radii); a normalised value v is <c>level + (v * scale)</c> in the plane's own units again.
    /// </summary>
    public static (double Level, double Scale) NormalisationLevels(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var sky = new List<double>();
        double diskSum = 0;
        var diskCount = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = disk.RadiiAt(x, y);
                var v = plane[(y * width) + x];
                if (r >= SkyRadii)
                {
                    sky.Add(v);
                }
                else if (r < 0.8)
                {
                    diskSum += v;
                    diskCount++;
                }
            }
        }
        var level = sky.Count > 0 ? StatisticsHelper.MedianFast(sky.ToArray()) : 0;
        var scale = diskCount > 0 ? (diskSum / diskCount) - level : 1;
        return (level, scale);
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
        var profile = Profile(plane, width, height, disk, 1.0, 1.3);
        var sky = new List<double>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.RadiiAt(x, y) >= SkyRadii)
                {
                    sky.Add(plane[(y * width) + x]);
                }
            }
        }
        if (sky.Count == 0 || profile.Length == 0)
        {
            return double.NaN;
        }
        var median = StatisticsHelper.MedianFast(sky.ToArray());
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
    /// The limb's rebound: the most <paramref name="plane"/>'s azimuthal profile (normalised, the sky zero and the disk one) climbs back
    /// above its own running minimum going out from 1.0 to 1.3 radii. A planet's profile only falls outside its limb (the glow of its
    /// diffraction and its blur), a stack's with it, so a rise there is a ring a sharpening put in (#1168); zero on a profile that never
    /// climbs. The truth-free reading of what <see cref="LimbUndershoot"/>, which reads below the sky only, cannot see.
    /// </summary>
    public static double LimbRebound(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var profile = Profile(plane, width, height, disk, 1.0, 1.3);
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
    /// all but noiseless still needs (#1181: the bounded limb fix lets these keep their sharpening, and the limb fixes are scored by them).
    /// </summary>
    public static ImmutableArray<(int X, int Y)> CompactSources(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk, int count = 3,
        double sigmas = 20, int apartPx = 12, double minimumPeak = 0.01)
    {
        var sky = new List<float>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (disk.RadiiAt(x, y) >= SkyRadii)
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
                if (v <= median + Math.Max(sigmas * noise, minimumPeak) || disk.RadiiAt(x, y) <= 1.05 || !IsLocalMaximum(plane, width, x, y))
                {
                    continue;
                }
                var (peak, _) = SourcePeak(plane, width, height, x, y);
                if (peak > Math.Max(sigmas * noise, minimumPeak))
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
    public static double LimbProfileError(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk)
    {
        var (s, t) = (Profile(stack, width, height, disk, 0.8, 1.2), Profile(truth, width, height, disk, 0.8, 1.2));
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
                if (ring >= 0 && ring < rings)
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
