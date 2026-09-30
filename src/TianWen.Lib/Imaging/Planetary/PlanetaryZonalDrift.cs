using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What moves against System III (docs/plans/planetary-restoration.md, R6 part 3): two stacks of one run, each projected onto
/// planetographic latitude and System III west longitude at its own instant (<see cref="Project"/>), so that whatever the
/// rotation alone carries lies where it lay, and the shift along each band of latitude between them (<see cref="Drift"/>),
/// the zonal wind's over the time between. A de-rotation takes the rotation out and leaves this, which R6 measures rather
/// than fits.
/// </summary>
public static class PlanetaryZonalDrift
{
    /// <summary>
    /// <paramref name="plane"/> as a map of albedo: rows of planetographic latitude from <paramref name="minLatitude"/> by
    /// <paramref name="stepDeg"/>, columns of System III west longitude from <paramref name="firstWest"/> by the same step,
    /// each cell sampled where <paramref name="projection"/> puts it and divided by Minnaert's lighting there with
    /// <paramref name="minnaertK"/>; NaN where the cell is off the visible disk or its emission cosine is under
    /// <paramref name="minMu"/>.
    /// </summary>
    public static float[,] Project(Image plane, in PlanetaryProjection projection, double minnaertK, double minLatitude, int rows, double firstWest, int columns,
        double stepDeg, double minMu = 0.5)
    {
        ArgumentNullException.ThrowIfNull(plane);
        var (width, height) = (plane.Width, plane.Height);
        var values = plane.GetChannelSpan(0).ToArray();
        var map = new float[rows, columns];
        var target = projection;
        ParallelFor.Run(rows, r =>
        {
            var latitude = minLatitude + (r * stepDeg);
            for (var c = 0; c < columns; c++)
            {
                map[r, c] = float.NaN;
                if (!target.TryProject(latitude, firstWest + (c * stepDeg), out var x, out var y)
                    || !target.TrySurface(x, y, out _, out _, out var mu, out var mu0) || mu <= minMu || mu0 <= 0)
                {
                    continue;
                }
                var sample = Image.Lanczos3Value(values, width, height, (float)x, (float)y);
                if (float.IsFinite(sample))
                {
                    map[r, c] = (float)(sample / (Math.Pow(mu0, minnaertK) * Math.Pow(mu, minnaertK - 1)));
                }
            }
        });
        return map;
    }

    /// <summary>
    /// The shift in west longitude, degrees, of each row of <paramref name="later"/> against the same row of
    /// <paramref name="earlier"/> (two maps of one grid, <see cref="Project"/>): the lag, searched to <paramref name="reachDeg"/>
    /// either way, that best correlates the two rows where both have values, each with its running mean over
    /// <paramref name="highPassDeg"/> taken out so a band's level and its limb darkening's leftovers do not decide it, and the
    /// peak placed between steps by a parabola. With it the peak correlation; NaN for a row with too little overlap.
    /// </summary>
    public static ImmutableArray<(double ShiftDeg, double Correlation)> Drift(float[,] earlier, float[,] later, double stepDeg, double reachDeg = 2, double highPassDeg = 10)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);
        var (rows, columns) = (earlier.GetLength(0), earlier.GetLength(1));
        var reach = (int)Math.Ceiling(reachDeg / stepDeg);
        var window = Math.Max(1, (int)Math.Round(highPassDeg / stepDeg / 2));
        var result = ImmutableArray.CreateBuilder<(double, double)>(rows);
        for (var r = 0; r < rows; r++)
        {
            var (a, b) = (HighPass(earlier, r, window), HighPass(later, r, window));
            var correlations = new double[(2 * reach) + 1];
            for (var lag = -reach; lag <= reach; lag++)
            {
                double ab = 0, aa = 0, bb = 0;
                var n = 0;
                for (var c = 0; c < columns; c++)
                {
                    var d = c + lag;
                    if (d < 0 || d >= columns || !double.IsFinite(a[c]) || !double.IsFinite(b[d]))
                    {
                        continue;
                    }
                    (ab, aa, bb, n) = (ab + (a[c] * b[d]), aa + (a[c] * a[c]), bb + (b[d] * b[d]), n + 1);
                }
                correlations[lag + reach] = n >= 20 && aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : double.NaN;
            }
            var best = -1;
            for (var i = 0; i < correlations.Length; i++)
            {
                if (double.IsFinite(correlations[i]) && (best < 0 || correlations[i] > correlations[best]))
                {
                    best = i;
                }
            }
            if (best <= 0 || best >= correlations.Length - 1 || !double.IsFinite(correlations[best - 1]) || !double.IsFinite(correlations[best + 1]))
            {
                result.Add((double.NaN, best >= 0 ? correlations[best] : double.NaN));
                continue;
            }
            var (m, z, p) = (correlations[best - 1], correlations[best], correlations[best + 1]);
            var curvature = m - (2 * z) + p;
            var fraction = curvature == 0 ? 0 : 0.5 * (m - p) / curvature;
            result.Add(((best - reach + fraction) * stepDeg, z));
        }
        return result.MoveToImmutable();
    }

    /// <summary>
    /// The eastward wind, m/s, a shift in west longitude of <paramref name="shiftDeg"/> over <paramref name="seconds"/> means at
    /// <paramref name="planetographicLatitude"/> on a spheroid of <paramref name="equatorialKm"/> and <paramref name="polarKm"/>:
    /// a feature moving east comes to a smaller west longitude, so the wind is the shift's opposite, along the circle of latitude
    /// the planetocentric latitude gives.
    /// </summary>
    public static double WindOf(double shiftDeg, double seconds, double planetographicLatitude, double equatorialKm = 71492, double polarKm = 66854)
    {
        var q = polarKm / equatorialKm;
        var centric = Math.Atan(q * q * Math.Tan(planetographicLatitude * Math.PI / 180));
        // The circle of latitude's radius on the spheroid, from its planetocentric latitude.
        var radiusKm = equatorialKm * polarKm / Math.Sqrt((polarKm * polarKm * Math.Cos(centric) * Math.Cos(centric)) + (equatorialKm * equatorialKm * Math.Sin(centric) * Math.Sin(centric))) * Math.Cos(centric);
        return -shiftDeg * Math.PI / 180 * radiusKm * 1000 / seconds;
    }

    // A map row with its running mean over 2 * window + 1 columns taken out (NaN kept NaN).
    private static double[] HighPass(float[,] map, int row, int window)
    {
        var columns = map.GetLength(1);
        var result = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            if (!float.IsFinite(map[row, c]))
            {
                result[c] = double.NaN;
                continue;
            }
            double sum = 0;
            var n = 0;
            for (var d = Math.Max(0, c - window); d <= Math.Min(columns - 1, c + window); d++)
            {
                if (float.IsFinite(map[row, d]))
                {
                    sum += map[row, d];
                    n++;
                }
            }
            result[c] = map[row, c] - (sum / n);
        }
        return result;
    }
}
