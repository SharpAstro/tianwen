using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>A ghost fitted beyond a planet: a copy of it, scaled, moved and spread by a defocus disk, beside a scatter glow and the sky.</summary>
/// <param name="Strength">The copy's share of the planet's light, a.</param>
/// <param name="ShiftX">The copy's offset, px.</param>
/// <param name="ShiftY">The copy's offset, px.</param>
/// <param name="Radius">The defocus disk's radius, px.</param>
/// <param name="Glow">The glow's share, b.</param>
/// <param name="GlowExponent">The glow kernel's power, (1 + r)^-q.</param>
/// <param name="Sky">The sky left after the plane's own zero.</param>
/// <param name="Rms">The residual's RMS over the fitted pixels.</param>
public readonly record struct GhostFit(double Strength, double ShiftX, double ShiftY, double Radius, double Glow, double GlowExponent, double Sky, double Rms);

/// <summary>Coma, the alternative: a flare from the planet, a uniform line of a length at an angle, beside the same glow and sky.</summary>
public readonly record struct ComaFit(double Strength, double Length, double AngleDeg, double Glow, double GlowExponent, double Sky, double Rms);

/// <summary>
/// R7a's ghost (docs/plans/planetary-restoration.md): a faint, sharp-edged, lopsided shell the ASI290MM's stacks carry around a planet,
/// modelled as the comet work separated a comet from its stars, a copy of the planet itself scaled, moved and blurred by a defocus disk,
/// beside a broad glow (the planet through a power law, the usual shape of scatter), both fitted beyond the planet where they are alone,
/// and the copy subtracted everywhere, the disk included. Coma is fitted beside it as an alternative: a flare from the planet itself.
/// Planes here have the sky at zero and the planet's 99.5th percentile at one (<see cref="Normalise"/>).
/// </summary>
public static class PlanetaryGhost
{
    /// <summary>The copy's source: the stack where it stands above this share of its peak (the planet and Saturn's rings alike).</summary>
    public const double ObjectThreshold = 0.02;

    /// <summary>Pixels nearer the object than this are left out of the fit (the object's own edge).</summary>
    public const double FitMargin = 3;

    private const int Border = 8;

    /// <summary><paramref name="plane"/> with the median of its 8 px border at zero and its 99.5th percentile at one.</summary>
    public static float[] Normalise(ReadOnlySpan<float> plane, int width, int height)
    {
        var border = new List<float>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x < Border || y < Border || x >= width - Border || y >= height - Border)
                {
                    border.Add(plane[(y * width) + x]);
                }
            }
        }
        border.Sort();
        var sky = border[border.Count / 2];
        var sorted = plane.ToArray();
        Array.Sort(sorted);
        var peak = sorted[(int)(0.995 * (sorted.Length - 1))] - sky;
        var result = new float[plane.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (plane[i] - sky) / peak;
        }
        return result;
    }

    /// <summary>The object: where <paramref name="plane"/> stands above <see cref="ObjectThreshold"/>.</summary>
    public static bool[] ObjectMask(ReadOnlySpan<float> plane)
    {
        var mask = new bool[plane.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = plane[i] > ObjectThreshold;
        }
        return mask;
    }

    /// <summary>Each pixel's distance outside <paramref name="mask"/>, px (zero inside), by a 3-4 chamfer over two passes.</summary>
    public static float[] DistanceOutside(bool[] mask, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var d = new float[mask.Length];
        const float Far = 1e9f;
        for (var i = 0; i < d.Length; i++)
        {
            d[i] = mask[i] ? 0 : Far;
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (x > 0)
                {
                    d[i] = Math.Min(d[i], d[i - 1] + 3);
                }
                if (y > 0)
                {
                    d[i] = Math.Min(d[i], d[i - width] + 3);
                }
                if (x > 0 && y > 0)
                {
                    d[i] = Math.Min(d[i], d[i - width - 1] + 4);
                }
                if (x < width - 1 && y > 0)
                {
                    d[i] = Math.Min(d[i], d[i - width + 1] + 4);
                }
            }
        }
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var i = (y * width) + x;
                if (x < width - 1)
                {
                    d[i] = Math.Min(d[i], d[i + 1] + 3);
                }
                if (y < height - 1)
                {
                    d[i] = Math.Min(d[i], d[i + width] + 3);
                }
                if (x < width - 1 && y < height - 1)
                {
                    d[i] = Math.Min(d[i], d[i + width + 1] + 4);
                }
                if (x > 0 && y < height - 1)
                {
                    d[i] = Math.Min(d[i], d[i + width - 1] + 4);
                }
            }
        }
        for (var i = 0; i < d.Length; i++)
        {
            d[i] /= 3;
        }
        return d;
    }

    /// <summary>The planes a ghost, a flare and a glow are made from, for one stack.</summary>
    public sealed class Source
    {
        private readonly Complex[] _object;
        private readonly int _n;

        /// <summary>The source's object (the stack above <see cref="ObjectThreshold"/>), its mask and every pixel's distance outside it.</summary>
        public Source(ReadOnlySpan<float> plane, int width, int height)
        {
            (Width, Height) = (width, height);
            Mask = ObjectMask(plane);
            Distance = DistanceOutside(Mask, width, height);
            var objectPlane = new float[plane.Length];
            for (var i = 0; i < objectPlane.Length; i++)
            {
                objectPlane[i] = Mask[i] ? plane[i] : 0;
            }
            _n = PlanetaryInverse.GridFor(width, height, 96);
            _object = PlanetaryInverse.Transform(objectPlane, width, height, _n);
            var fitted = new List<int>();
            for (var y = Border; y < height - Border; y++)
            {
                for (var x = Border; x < width - Border; x++)
                {
                    if (Distance[(y * width) + x] >= FitMargin)
                    {
                        fitted.Add((y * width) + x);
                    }
                }
            }
            Fitted = [.. fitted];
        }

        /// <summary>The plane's width, px.</summary>
        public int Width { get; }

        /// <summary>The plane's height, px.</summary>
        public int Height { get; }

        /// <summary>Where the object is.</summary>
        public bool[] Mask { get; }

        /// <summary>Each pixel's distance outside the object, px.</summary>
        public float[] Distance { get; }

        /// <summary>The pixels a fit is made over: at least <see cref="FitMargin"/> outside the object, inside the border band.</summary>
        public ImmutableArray<int> Fitted { get; }

        /// <summary>The ghost alone: <paramref name="strength"/> times the object through a uniform disk of <paramref name="radius"/>, moved.</summary>
        public float[] Ghost(double strength, double shiftX, double shiftY, double radius) =>
            Through((fx, fy) => strength * DiskTransfer(radius, Math.Sqrt((fx * fx) + (fy * fy))) * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * shiftX) + (fy * shiftY))));

        /// <summary>The flare alone: <paramref name="strength"/> times the object through a uniform line from the centre.</summary>
        public float[] Flare(double strength, double length, double angleDeg)
        {
            var (c, s) = (Math.Cos(angleDeg * Math.PI / 180), Math.Sin(angleDeg * Math.PI / 180));
            return Through((fx, fy) =>
            {
                var u = Math.PI * ((fx * c) + (fy * s)) * length;
                var sinc = Math.Abs(u) < 1e-9 ? 1 : Math.Sin(u) / u;
                return strength * sinc * Complex.FromPolarCoordinates(1, -u);
            });
        }

        /// <summary>The glow alone: <paramref name="strength"/> times the object through (1 + r)^-<paramref name="exponent"/>, unit sum.</summary>
        public float[] Glow(double strength, double exponent)
        {
            var kernel = new Complex[_n * _n];
            double sum = 0;
            for (var y = 0; y < _n; y++)
            {
                var dy = y < _n / 2 ? y : y - _n;
                for (var x = 0; x < _n; x++)
                {
                    var dx = x < _n / 2 ? x : x - _n;
                    var v = Math.Pow(1 + Math.Sqrt((dx * dx) + (dy * dy)), -exponent);
                    kernel[(y * _n) + x] = v;
                    sum += v;
                }
            }
            Fft2D.Forward(kernel, _n, _n);
            var field = new Complex[_object.Length];
            for (var i = 0; i < field.Length; i++)
            {
                field[i] = _object[i] * kernel[i] * (strength / sum);
            }
            return Back(field);
        }

        private float[] Through(Func<double, double, Complex> transfer)
        {
            var field = new Complex[_object.Length];
            for (var ky = 0; ky < _n; ky++)
            {
                var fy = (ky < _n / 2 ? ky : ky - _n) / (double)_n;
                for (var kx = 0; kx < _n; kx++)
                {
                    var fx = (kx < _n / 2 ? kx : kx - _n) / (double)_n;
                    var i = (ky * _n) + kx;
                    field[i] = _object[i] * transfer(fx, fy);
                }
            }
            return Back(field);
        }

        private float[] Back(Complex[] field)
        {
            Fft2D.Inverse(field, _n, _n);
            var result = new float[Width * Height];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    result[(y * Width) + x] = (float)field[(y * _n) + x].Real;
                }
            }
            return result;
        }
    }

    /// <summary>
    /// The ghost, glow and sky that bring <paramref name="source"/>'s object nearest <paramref name="plane"/> over its fitted pixels: a
    /// coarse search over the shift and the disk with the linear terms solved at each, then Levenberg-Marquardt over all seven.
    /// </summary>
    public static GhostFit FitGhost(ReadOnlySpan<float> plane, Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = plane.ToArray();
        var fitted = source.Fitted;
        double[] best = [];
        var bestCost = double.PositiveInfinity;
        var glowShape = source.Glow(1, 3);
        foreach (var dx in new[] { -8.0, 0, 8 })
        {
            foreach (var dy in new[] { -8.0, 0, 8 })
            {
                foreach (var radius in new[] { 8.0, 16, 24 })
                {
                    var ghostShape = source.Ghost(1, dx, dy, radius);
                    var (coefficients, cost) = Linear(target, fitted, ghostShape, glowShape);
                    if (cost < bestCost)
                    {
                        (bestCost, best) = (cost, [coefficients[0], dx, dy, radius, coefficients[1], 3, coefficients[2]]);
                    }
                }
            }
        }
        var result = LevenbergMarquardt.Fit(best, fitted.Length, (p, residuals) =>
        {
            var ghost = source.Ghost(p[0], p[1], p[2], p[3]);
            var glow = source.Glow(p[4], p[5]);
            for (var k = 0; k < fitted.Length; k++)
            {
                var i = fitted[k];
                residuals[k] = target[i] - (ghost[i] + glow[i] + p[6]);
            }
        }, [1e-5, 1e-3, 1e-3, 1e-3, 1e-5, 1e-4, 1e-6], maxIterations: 60);
        var q = result.Parameters;
        return new GhostFit(q[0], q[1], q[2], q[3], q[4], q[5], q[6], Math.Sqrt(2 * result.Cost / fitted.Length));
    }

    /// <summary>Coma in the ghost's place: the flare, glow and sky that bring the object nearest <paramref name="plane"/>.</summary>
    public static ComaFit FitComa(ReadOnlySpan<float> plane, Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = plane.ToArray();
        var fitted = source.Fitted;
        double[] best = [];
        var bestCost = double.PositiveInfinity;
        var glowShape = source.Glow(1, 3);
        for (var angle = 0.0; angle < 360; angle += 45)
        {
            foreach (var length in new[] { 6.0, 15, 30 })
            {
                var flareShape = source.Flare(1, length, angle);
                var (coefficients, cost) = Linear(target, fitted, flareShape, glowShape);
                if (cost < bestCost)
                {
                    (bestCost, best) = (cost, [coefficients[0], length, angle, coefficients[1], 3, coefficients[2]]);
                }
            }
        }
        var result = LevenbergMarquardt.Fit(best, fitted.Length, (p, residuals) =>
        {
            var flare = source.Flare(p[0], p[1], p[2]);
            var glow = source.Glow(p[3], p[4]);
            for (var k = 0; k < fitted.Length; k++)
            {
                var i = fitted[k];
                residuals[k] = target[i] - (flare[i] + glow[i] + p[5]);
            }
        }, [1e-5, 1e-3, 1e-3, 1e-5, 1e-4, 1e-6], maxIterations: 60);
        var q = result.Parameters;
        return new ComaFit(q[0], q[1], q[2], q[3], q[4], q[5], Math.Sqrt(2 * result.Cost / fitted.Length));
    }

    /// <summary>
    /// The residual's mean in rings <paramref name="width"/> px wide outside the object, from <paramref name="from"/> to
    /// <paramref name="to"/> px, each with its standard error (the ring's scatter over the root of its pixel count).
    /// </summary>
    public static ImmutableArray<(double Distance, double Mean, double StandardError)> Rings(ReadOnlySpan<float> residual, Source source, double from = 3, double to = 40, double width = 2)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rings = ImmutableArray.CreateBuilder<(double, double, double)>();
        for (var r = from; r < to; r += width)
        {
            double sum = 0, sum2 = 0;
            var count = 0;
            for (var y = Border; y < source.Height - Border; y++)
            {
                for (var x = Border; x < source.Width - Border; x++)
                {
                    var i = (y * source.Width) + x;
                    var d = source.Distance[i];
                    if (d >= r && d < r + width)
                    {
                        (sum, sum2, count) = (sum + residual[i], sum2 + ((double)residual[i] * residual[i]), count + 1);
                    }
                }
            }
            if (count > 1)
            {
                var mean = sum / count;
                var spread = Math.Sqrt(Math.Max(0, (sum2 / count) - (mean * mean)));
                rings.Add((r + (width / 2), mean, spread / Math.Sqrt(count)));
            }
        }
        return rings.ToImmutable();
    }

    /// <summary>The RMS of <paramref name="residual"/> over the pixels between <paramref name="from"/> and <paramref name="to"/> px outside the object.</summary>
    public static double Rms(ReadOnlySpan<float> residual, Source source, double from, double to)
    {
        ArgumentNullException.ThrowIfNull(source);
        double sum = 0;
        var count = 0;
        foreach (var i in source.Fitted)
        {
            var d = source.Distance[i];
            if (d >= from && d < to)
            {
                sum += (double)residual[i] * residual[i];
                count++;
            }
        }
        return count > 0 ? Math.Sqrt(sum / count) : double.NaN;
    }

    /// <summary>A uniform disk's transfer at <paramref name="f"/> cycles a pixel: 2 J1(2 pi rho f) / (2 pi rho f), one at zero.</summary>
    internal static double DiskTransfer(double radius, double f)
    {
        var x = 2 * Math.PI * radius * f;
        return x < 1e-9 ? 1 : 2 * BesselJ1(x) / x;
    }

    // J1 by Abramowitz and Stegun 9.4.4 (|x| <= 3, error under 1.3e-8 in J1(x) / x) and 9.4.6 (x >= 3, error under 1.3e-8 in f1, 9e-8
    // in theta1).
    internal static double BesselJ1(double x)
    {
        var ax = Math.Abs(x);
        if (ax <= 3)
        {
            return x * Polynomial((x / 3) * (x / 3), 0.5, -0.56249985, 0.21093573, -0.03954289, 0.00443319, -0.00031761, 0.00001109);
        }
        var u = 3 / ax;
        var f1 = Polynomial(u, 0.79788456, 0.00000156, 0.01659667, 0.00017105, -0.00249511, 0.00113653, -0.00020033);
        var theta = ax + Polynomial(u, -2.35619449, 0.12499612, 0.00005650, -0.00637879, 0.00074348, 0.00079824, -0.00029166);
        var value = f1 * Math.Cos(theta) / Math.Sqrt(ax);
        return x < 0 ? -value : value;
    }

    // c0 + c1 t + c2 t^2 + ..., by Horner's rule.
    private static double Polynomial(double t, params ReadOnlySpan<double> coefficients)
    {
        var result = 0.0;
        for (var i = coefficients.Length - 1; i >= 0; i--)
        {
            result = (result * t) + coefficients[i];
        }
        return result;
    }

    // The least squares of `target` over `fitted` on a, b and a constant: a * first + b * second + c.
    private static (double[] Coefficients, double Cost) Linear(float[] target, ImmutableArray<int> fitted, float[] first, float[] second)
    {
        var a = new double[3, 3];
        var b = new double[3];
        foreach (var i in fitted)
        {
            double[] row = [first[i], second[i], 1];
            for (var j = 0; j < 3; j++)
            {
                b[j] += row[j] * target[i];
                for (var k = 0; k < 3; k++)
                {
                    a[j, k] += row[j] * row[k];
                }
            }
        }
        var solved = PlanetaryCeilings.Solve(a, b);
        double cost = 0;
        foreach (var i in fitted)
        {
            var r = target[i] - ((solved[0] * first[i]) + (solved[1] * second[i]) + solved[2]);
            cost += r * r;
        }
        return (solved, cost);
    }
}
