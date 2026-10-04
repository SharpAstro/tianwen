using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planet's zonal profile, its albedo averaged along each band of planetographic latitude, and the edges of its belts
/// (docs/plans/planetary-restoration.md, R6 part 3): what a stack's projection onto the spheroid is checked by against a map
/// such as OPAL's, since a belt's edge is where the planet itself says a latitude is. A stack's profile divides each pixel by
/// Minnaert's lighting, as OPAL's maps are, and keeps to the middle of the disk, where a pixel is least blurred along the
/// latitude; a map's is blurred to the stack's own resolution before the two are compared.
/// </summary>
public static class PlanetaryBelts
{
    /// <summary>A profile's bin, degrees of planetographic latitude.</summary>
    public const double Step = 0.25;

    /// <summary>The profile's reach, degrees either side of the equator: past it, OPAL's maps and a disk's limb both thin out.</summary>
    public const double Reach = 70;

    private static int Bins => (int)Math.Round(2 * Reach / Step) + 1;

    /// <summary>
    /// A stack's zonal albedo: each pixel of <paramref name="plane"/> whose surface lies within
    /// <paramref name="maxFromCentralMeridianDeg"/> of the central meridian and whose emission cosine exceeds
    /// <paramref name="minMu"/>, divided by Minnaert's lighting with <paramref name="minnaertK"/>, averaged in its latitude's bin.
    /// </summary>
    public static ZonalProfile FromImage(ReadOnlySpan<float> plane, int width, int height, in PlanetaryProjection projection, double centralMeridian,
        double minnaertK, double maxFromCentralMeridianDeg = 40, double minMu = 0.5, Func<int, int, bool>? excluded = null)
    {
        var (sum, count) = (new double[Bins], new int[Bins]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!projection.TrySurface(x, y, out var latitude, out var west, out var mu, out var mu0) || mu <= minMu || mu0 <= 0
                    || Math.Abs(Math.IEEERemainder(west - centralMeridian, 360)) > maxFromCentralMeridianDeg || BinOf(latitude) is not { } bin
                    || (excluded is not null && excluded(x, y)))
                {
                    continue;
                }
                var lighting = Math.Pow(mu0, minnaertK) * Math.Pow(mu, minnaertK - 1);
                sum[bin] += plane[(y * width) + x] / lighting;
                count[bin]++;
            }
        }
        return Finish(sum, count);
    }

    /// <summary>
    /// A map's zonal mean over every longitude, blurred in latitude by a Gaussian of <paramref name="blurSigmaDeg"/> (none for 0):
    /// the resolution of the stack it is compared with.
    /// </summary>
    public static ZonalProfile FromMap(PlanetMap map, double blurSigmaDeg) => FromMap(map, blurSigmaDeg, 0, 0);

    /// <summary>
    /// <see cref="FromMap(PlanetMap, double)"/> blurred in latitude by a kernel with a halo, the limb fit's own (R7 part 3): (1 - h) of a
    /// Gaussian of <paramref name="coreSigmaDeg"/> and h, <paramref name="haloFraction"/>, of one of <paramref name="haloSigmaDeg"/>.
    /// R6 part 3 took the core alone, and the limb fit puts half of a stack's blur in its halo.
    /// </summary>
    public static ZonalProfile FromMap(PlanetMap map, double coreSigmaDeg, double haloFraction, double haloSigmaDeg)
    {
        ArgumentNullException.ThrowIfNull(map);
        var (sum, count) = (new double[Bins], new int[Bins]);
        for (var b = 0; b < sum.Length; b++)
        {
            var latitude = LatitudeOf(b);
            for (var column = 0; column < map.Width; column++)
            {
                var value = map.Sample(latitude, 360.0 * (column + 0.5) / map.Width);
                if (double.IsFinite(value))
                {
                    sum[b] += value;
                    count[b]++;
                }
            }
        }
        var profile = Finish(sum, count);
        var core = coreSigmaDeg > 0 ? Smooth(profile.Albedo, coreSigmaDeg / Step) : profile.Albedo;
        if (haloFraction <= 0 || haloSigmaDeg <= 0)
        {
            return profile with { Albedo = core };
        }
        var halo = Smooth(profile.Albedo, haloSigmaDeg / Step);
        var mixed = new double[core.Length];
        for (var b = 0; b < mixed.Length; b++)
        {
            mixed[b] = ((1 - haloFraction) * core[b]) + (haloFraction * halo[b]);
        }
        return profile with { Albedo = mixed };
    }

    /// <summary>
    /// The belts' edges in <paramref name="profile"/>: the extrema of its latitude derivative, smoothed over
    /// <paramref name="smoothSigmaDeg"/>, between <paramref name="minLatitude"/> and <paramref name="maxLatitude"/>, at least
    /// <paramref name="minStrength"/> of the strongest there; each placed between bins by a parabola.
    /// </summary>
    public static ImmutableArray<BeltEdge> Edges(ZonalProfile profile, double smoothSigmaDeg = 0.75, double minLatitude = -40, double maxLatitude = 40, double minStrength = 0.2)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var slope = Derivative(profile, smoothSigmaDeg);
        var (first, last) = (BinOf(minLatitude) ?? 1, BinOf(maxLatitude) ?? (slope.Length - 2));
        var strongest = 0.0;
        for (var b = first; b <= last; b++)
        {
            if (double.IsFinite(slope[b]))
            {
                strongest = Math.Max(strongest, Math.Abs(slope[b]));
            }
        }
        var edges = ImmutableArray.CreateBuilder<BeltEdge>();
        for (var b = Math.Max(first, 1); b <= Math.Min(last, slope.Length - 2); b++)
        {
            var (before, here, after) = (Math.Abs(slope[b - 1]), Math.Abs(slope[b]), Math.Abs(slope[b + 1]));
            if (!double.IsFinite(before) || !double.IsFinite(here) || !double.IsFinite(after) || here < before || here < after || here < minStrength * strongest
                || Math.Sign(slope[b - 1]) != Math.Sign(slope[b]) || Math.Sign(slope[b + 1]) != Math.Sign(slope[b]))
            {
                continue;
            }
            var curvature = before - (2 * here) + after;
            var offset = curvature == 0 ? 0 : 0.5 * (before - after) / curvature;
            edges.Add(new BeltEdge(LatitudeOf(b) + (offset * Step), slope[b]));
        }
        return edges.ToImmutable();
    }

    /// <summary>
    /// The latitude shift, degrees, that best lays <paramref name="profile"/>'s derivative on <paramref name="reference"/>'s between
    /// <paramref name="minLatitude"/> and <paramref name="maxLatitude"/> (their correlation, searched to <paramref name="reachDeg"/>
    /// either way in steps of a tenth of a bin), and that correlation: a positive shift means the profile's belts lie north of
    /// the reference's.
    /// </summary>
    public static (double ShiftDeg, double Correlation) Offset(ZonalProfile profile, ZonalProfile reference, double smoothSigmaDeg = 0.75,
        double minLatitude = -40, double maxLatitude = 40, double reachDeg = 5)
    {
        var (a, b) = (Derivative(profile, smoothSigmaDeg), Derivative(reference, smoothSigmaDeg));
        var best = (ShiftDeg: 0.0, Correlation: double.NegativeInfinity);
        for (var shift = -reachDeg; shift <= reachDeg + 1e-9; shift += Step / 10)
        {
            double ab = 0, aa = 0, bb = 0;
            for (var latitude = minLatitude; latitude <= maxLatitude; latitude += Step)
            {
                var (va, vb) = (Interpolate(a, latitude + shift), Interpolate(b, latitude));
                if (double.IsFinite(va) && double.IsFinite(vb))
                {
                    (ab, aa, bb) = (ab + (va * vb), aa + (va * va), bb + (vb * vb));
                }
            }
            var correlation = aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : double.NaN;
            if (correlation > best.Correlation)
            {
                best = (shift, correlation);
            }
        }
        return best;
    }

    /// <summary>The latitude a profile's bin <paramref name="bin"/> is centred on.</summary>
    public static double LatitudeOf(int bin) => -Reach + (bin * Step);

    internal static int? BinOf(double latitude)
    {
        var bin = (int)Math.Round((latitude + Reach) / Step);
        return bin >= 0 && bin < Bins ? bin : null;
    }

    private static ZonalProfile Finish(double[] sum, int[] count)
    {
        var albedo = new double[sum.Length];
        for (var b = 0; b < albedo.Length; b++)
        {
            albedo[b] = count[b] > 0 ? sum[b] / count[b] : double.NaN;
        }
        return new ZonalProfile(albedo);
    }

    // The profile's latitude derivative, per degree, on its own mean level (so two profiles' slopes compare), smoothed first.
    private static double[] Derivative(ZonalProfile profile, double smoothSigmaDeg)
    {
        var smooth = Smooth(profile.Albedo, smoothSigmaDeg / Step);
        double level = 0;
        var n = 0;
        foreach (var v in smooth)
        {
            if (double.IsFinite(v))
            {
                level += v;
                n++;
            }
        }
        level = n > 0 ? level / n : double.NaN;
        var slope = new double[smooth.Length];
        slope[0] = slope[^1] = double.NaN;
        for (var b = 1; b < smooth.Length - 1; b++)
        {
            slope[b] = (smooth[b + 1] - smooth[b - 1]) / (2 * Step) / level;
        }
        return slope;
    }

    // A Gaussian of `sigmaBins` along the profile, over the bins that have a value.
    private static double[] Smooth(double[] values, double sigmaBins)
    {
        if (sigmaBins <= 0)
        {
            return (double[])values.Clone();
        }
        var reach = (int)Math.Ceiling(3 * sigmaBins);
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                result[i] = double.NaN;
                continue;
            }
            double sum = 0, weight = 0;
            for (var j = Math.Max(0, i - reach); j <= Math.Min(values.Length - 1, i + reach); j++)
            {
                if (double.IsFinite(values[j]))
                {
                    var w = Math.Exp(-0.5 * (j - i) * (j - i) / (sigmaBins * sigmaBins));
                    (sum, weight) = (sum + (w * values[j]), weight + w);
                }
            }
            result[i] = sum / weight;
        }
        return result;
    }

    private static double Interpolate(double[] values, double latitude)
    {
        var position = (latitude + Reach) / Step;
        var low = (int)Math.Floor(position);
        if (low < 0 || low + 1 >= values.Length)
        {
            return double.NaN;
        }
        var fraction = position - low;
        return (values[low] * (1 - fraction)) + (values[low + 1] * fraction);
    }
}

/// <summary>A zonal profile, one albedo a <see cref="PlanetaryBelts.Step"/>-degree bin from 70 S (NaN where nothing fell).</summary>
public sealed record ZonalProfile(double[] Albedo)
{
    /// <summary>The albedo in the bin <paramref name="latitude"/> falls in; NaN where that bin is empty or past the reach.</summary>
    public double At(double latitude) => PlanetaryBelts.BinOf(latitude) is { } bin ? Albedo[bin] : double.NaN;

    /// <summary>
    /// The albedo at <paramref name="latitude"/>, or past the profile's reach the nearest latitude it reads (the polar limb, which a profile
    /// read inside 0.9 radii never reaches; R8 follow-up 4 part 2); NaN only where the profile reads nothing at all.
    /// </summary>
    public double Held(double latitude)
    {
        if (At(latitude) is var value && double.IsFinite(value))
        {
            return value;
        }
        var nearest = double.NaN;
        var distance = double.PositiveInfinity;
        foreach (var (at, albedo) in Samples())
        {
            if (Math.Abs(at - latitude) < distance)
            {
                (distance, nearest) = (Math.Abs(at - latitude), albedo);
            }
        }
        return nearest;
    }

    /// <summary>The profile's samples as (planetographic latitude, albedo), the empty bins left out.</summary>
    public IEnumerable<(double Latitude, double Albedo)> Samples()
    {
        for (var b = 0; b < Albedo.Length; b++)
        {
            if (double.IsFinite(Albedo[b]))
            {
                yield return (PlanetaryBelts.LatitudeOf(b), Albedo[b]);
            }
        }
    }
}

/// <summary>A belt's edge: its planetographic latitude, and the profile's slope there (per degree, on its mean level), positive where the planet brightens to the north.</summary>
public readonly record struct BeltEdge(double Latitude, double Slope);
