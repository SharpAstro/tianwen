using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>A ghost fitted beyond a planet: a copy of it, scaled, moved and spread by an elliptical defocus annulus, beside a free round glow and the sky.</summary>
/// <param name="Strength">The copy's share of the planet's light, a: fixed only together with its shape beside a free round glow.</param>
/// <param name="ShiftX">The copy's offset, px.</param>
/// <param name="ShiftY">The copy's offset, px.</param>
/// <param name="Radius">The defocus disk's semi-major axis, px.</param>
/// <param name="AxisRatio">The disk's minor axis over its major, one for a round disk.</param>
/// <param name="AngleDeg">The major axis's angle from +x, degrees, toward +y (down the image).</param>
/// <param name="Obstruction">The annulus's hole over its outside, a defocused Newtonian pupil's secondary; zero for a filled disk.</param>
/// <param name="Glow">The glow's share at each of <see cref="PlanetaryGhost.Source.GlowKnots"/>: its kernel is their tents, so weighted.</param>
/// <param name="Sky">The sky left after the plane's own zero.</param>
/// <param name="Rms">The residual's RMS over the fitted pixels.</param>
public readonly record struct GhostFit(double Strength, double ShiftX, double ShiftY, double Radius, double AxisRatio, double AngleDeg, double Obstruction, ImmutableArray<double> Glow, double Sky, double Rms);

/// <summary>The glow and sky alone, no copy and no flare: what a ghost or coma has to improve on.</summary>
public readonly record struct GlowFit(ImmutableArray<double> Glow, double Sky, double Rms);

/// <summary>Coma, the alternative: a flare from the planet, a uniform line of a length at an angle, beside the same free glow and sky.</summary>
public readonly record struct ComaFit(double Strength, double Length, double AngleDeg, ImmutableArray<double> Glow, double Sky, double Rms);

/// <summary>A non-round part's quadrupole in a band of distance outside the object: its cos 2 theta amplitude and its long axis.</summary>
public readonly record struct Quadrupole(double From, double To, double Amplitude, double AxisDeg);

/// <summary>
/// R7a's ghost (docs/plans/planetary-restoration.md): a faint, sharp-edged, lopsided shell the ASI290MM's stacks carry around a planet,
/// modelled as the comet work separated a comet from its stars, a copy of the planet itself scaled, moved and blurred by a defocus disk,
/// beside a broad glow, both fitted beyond the planet where they are alone, and the copy subtracted everywhere, the disk included. Coma
/// is fitted beside it as an alternative: a flare from the planet itself. The copy's disk is an ELLIPTICAL annulus (what the 2022-09-03 stacks
/// show once their round part is taken out: a quadrupole, ranked by filter, and almost no dipole), and the glow is any ROUND kernel
/// (tents in radius, solved linearly). No round glow tells a copy's round part from scatter, so the copy's strength is fixed only
/// together with its shape, and what is taken out is the copy's NON-ROUND part (<see cref="NonRound"/>), beyond the object: the
/// shell, which is what can be told from the halo. A power law, one or two, left the twin's halo to a copy or fell apart (revisions 1,
/// 2 and 4); the fit starts 8 px out, past the blurred limb the source misses and the elongated blur of every 2022 filter. The copy is
/// of the planet alone (a moon's copy is a streak the data does not have), and its disk has a hole (a filled one put L's shell too
/// near the limb, revision 6; a defocused Newtonian pupil is an annulus).
/// Planes here have the sky at zero and the planet's 99.5th percentile at one (<see cref="Normalise"/>).
/// </summary>
public static class PlanetaryGhost
{
    /// <summary>The copy's source: the stack where it stands above this share of its peak (the planet and Saturn's rings alike).</summary>
    public const double ObjectThreshold = 0.02;

    /// <summary>
    /// Pixels nearer the object than this are left out of the fit by default. Nearer, the source misses the planet's own blurred limb
    /// below <see cref="ObjectThreshold"/> (a copy and a glow then fight over it), and on 2022-09-03 every filter's blur is elongated there.
    /// </summary>
    public const double FitMargin = 8;

    private const int Border = 8;

    /// <summary>
    /// Pixels nearer another object than this (a moon) are left out of every fit and read: its own halo is not the planet's, and the
    /// moons lie along the equator, so their surroundings read as a quadrupole of the planet's halo.
    /// </summary>
    public const double MoonClearance = 10;

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

    /// <summary>The largest 8-connected piece of <paramref name="mask"/>: the planet, without its moons.</summary>
    public static bool[] LargestPiece(bool[] mask, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var label = new int[mask.Length];
        var (best, bestSize, next) = (0, 0, 0);
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0)
            {
                continue;
            }
            next++;
            var size = 0;
            label[start] = next;
            stack.Push(start);
            while (stack.TryPop(out var i))
            {
                size++;
                var (x, y) = (i % width, i / width);
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (u, v) = (x + dx, y + dy);
                        if (u < 0 || v < 0 || u >= width || v >= height)
                        {
                            continue;
                        }
                        var j = (v * width) + u;
                        if (mask[j] && label[j] == 0)
                        {
                            label[j] = next;
                            stack.Push(j);
                        }
                    }
                }
            }
            if (size > bestSize)
            {
                (best, bestSize) = (next, size);
            }
        }
        var planet = new bool[mask.Length];
        for (var i = 0; i < planet.Length; i++)
        {
            planet[i] = best != 0 && label[i] == best;
        }
        return planet;
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
        private readonly Complex[] _planet;
        private readonly int _n;
        private float[][]? _glowShapes;

        /// <summary>
        /// The source's object (the stack above <see cref="ObjectThreshold"/>), its mask, the planet among it (the largest piece), every
        /// pixel's distance outside the planet, and the pixels fitted, from <paramref name="margin"/> px outside it and clear of the rest.
        /// </summary>
        public Source(ReadOnlySpan<float> plane, int width, int height, double margin = FitMargin)
        {
            (Width, Height) = (width, height);
            Mask = ObjectMask(plane);
            Planet = LargestPiece(Mask, width, height);
            Distance = DistanceOutside(Planet, width, height);
            var others = new bool[Mask.Length];
            for (var i = 0; i < others.Length; i++)
            {
                others[i] = Mask[i] && !Planet[i];
            }
            var fromOthers = DistanceOutside(others, width, height);
            Clear = new bool[Mask.Length];
            for (var y = Border; y < height - Border; y++)
            {
                for (var x = Border; x < width - Border; x++)
                {
                    var i = (y * width) + x;
                    Clear[i] = !Planet[i] && fromOthers[i] >= MoonClearance;
                }
            }
            var objectPlane = new float[plane.Length];
            for (var i = 0; i < objectPlane.Length; i++)
            {
                objectPlane[i] = Mask[i] ? plane[i] : 0;
            }
            _n = PlanetaryInverse.GridFor(width, height, 96);
            _object = PlanetaryInverse.Transform(objectPlane, width, height, _n);
            for (var i = 0; i < objectPlane.Length; i++)
            {
                objectPlane[i] = Planet[i] ? plane[i] : 0;
            }
            _planet = PlanetaryInverse.Transform(objectPlane, width, height, _n);
            var fitted = new List<int>();
            for (var y = Border; y < height - Border; y++)
            {
                for (var x = Border; x < width - Border; x++)
                {
                    if (Clear[(y * width) + x] && Distance[(y * width) + x] >= margin)
                    {
                        fitted.Add((y * width) + x);
                    }
                }
            }
            Fitted = [.. fitted];
            var reach = 0.4 * Math.Max(width, height);
            GlowKnots = [.. new[] { 0, 1.5, 3, 4.5, 6, 8, 10, 13, 16, 20, 25, 32, 40, 50, 64, 80, 100, 128, 160, 200, 256, 320, 400, 512 }.Where(k => k <= reach)];
            double cx = 0, cy = 0;
            var count = 0;
            for (var i = 0; i < Planet.Length; i++)
            {
                if (Planet[i])
                {
                    (cx, cy, count) = (cx + (i % width), cy + (i / width), count + 1);
                }
            }
            Centre = (cx / Math.Max(count, 1), cy / Math.Max(count, 1));
        }

        /// <summary>The free glow's knots, px: a tent in radius at each, closer near the object where its blur changes fastest.</summary>
        public ImmutableArray<double> GlowKnots { get; }

        /// <summary>The planet's centroid, px.</summary>
        public (double X, double Y) Centre { get; }

        /// <summary>The object through each knot's tent, unit sum: the glow is their sum weighted by its shares.</summary>
        internal float[][] GlowShapes => _glowShapes ??= [.. Enumerable.Range(0, GlowKnots.Length).Select(TentShape)];

        /// <summary>The plane's width, px.</summary>
        public int Width { get; }

        /// <summary>The plane's height, px.</summary>
        public int Height { get; }

        /// <summary>Where the object is, the planet and its moons alike: the copy's source.</summary>
        public bool[] Mask { get; }

        /// <summary>Where the planet is: the largest piece of the object.</summary>
        public bool[] Planet { get; }

        /// <summary>Each pixel's distance outside the planet, px.</summary>
        public float[] Distance { get; }

        /// <summary>The pixels outside the planet, clear of the rest of the object by <see cref="MoonClearance"/> and inside the border band.</summary>
        public bool[] Clear { get; }

        /// <summary>The pixels a fit is made over: the clear ones at least the margin outside the planet.</summary>
        public ImmutableArray<int> Fitted { get; }

        /// <summary>
        /// The ghost alone: <paramref name="strength"/> times the planet through a uniform elliptical annulus of semi-major axis
        /// <paramref name="radius"/>, <paramref name="axisRatio"/>, <paramref name="angleDeg"/> and a hole <paramref name="obstruction"/>
        /// of its size, moved.
        /// </summary>
        public float[] Ghost(double strength, double shiftX, double shiftY, double radius, double axisRatio = 1, double angleDeg = 0, double obstruction = 0)
        {
            var (c, s) = (Math.Cos(angleDeg * Math.PI / 180), Math.Sin(angleDeg * Math.PI / 180));
            var hole = obstruction * obstruction;
            return Through(_planet, (fx, fy) =>
            {
                var (along, across) = ((fx * c) + (fy * s), (-fx * s) + (fy * c));
                var f = Math.Sqrt((along * along) + (axisRatio * axisRatio * across * across));
                var annulus = (DiskTransfer(radius, f) - (hole * DiskTransfer(obstruction * radius, f))) / (1 - hole);
                return strength * annulus * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * shiftX) + (fy * shiftY)));
            });
        }

        /// <summary>The flare alone: <paramref name="strength"/> times the planet through a uniform line from the centre.</summary>
        public float[] Flare(double strength, double length, double angleDeg)
        {
            var (c, s) = (Math.Cos(angleDeg * Math.PI / 180), Math.Sin(angleDeg * Math.PI / 180));
            return Through(_planet, (fx, fy) =>
            {
                var u = Math.PI * ((fx * c) + (fy * s)) * length;
                var sinc = Math.Abs(u) < 1e-9 ? 1 : Math.Sin(u) / u;
                return strength * sinc * Complex.FromPolarCoordinates(1, -u);
            });
        }

        /// <summary>The free glow: the object through each knot's tent weighted by <paramref name="shares"/>.</summary>
        public float[] Glow(ReadOnlySpan<double> shares)
        {
            var shapes = GlowShapes;
            var result = new float[Width * Height];
            for (var k = 0; k < shapes.Length; k++)
            {
                var (share, shape) = ((float)shares[k], shapes[k]);
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] += share * shape[i];
                }
            }
            return result;
        }

        private float[] TentShape(int k)
        {
            var knots = GlowKnots;
            var (left, centre, right) = (k > 0 ? knots[k - 1] : 0, knots[k], k < knots.Length - 1 ? knots[k + 1] : knots[k]);
            return Convolve(r => r <= left || r >= right ? (r == centre ? 1 : 0) : r < centre ? (r - left) / (centre - left) : (right - r) / (right - centre), 1);
        }

        /// <summary>
        /// A power-law glow: <paramref name="strength"/> times the object through (1 + (r / <paramref name="core"/>)^2)^(-<paramref name="exponent"/> / 2),
        /// unit sum, flat to its core and falling as r^-q beyond it.
        /// </summary>
        public float[] Glow(double strength, double exponent, double core) =>
            Convolve(r => Math.Pow(1 + (r * r / (core * core)), -exponent / 2), strength);

        // The object through a round kernel given by its value at each radius, normalised to unit sum and scaled by strength.
        private float[] Convolve(Func<double, double> kernelAt, double strength)
        {
            var kernel = new Complex[_n * _n];
            double sum = 0;
            for (var y = 0; y < _n; y++)
            {
                var dy = y < _n / 2 ? y : y - _n;
                for (var x = 0; x < _n; x++)
                {
                    var dx = x < _n / 2 ? x : x - _n;
                    var v = kernelAt(Math.Sqrt((dx * dx) + (dy * dy)));
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

        private float[] Through(Complex[] source, Func<double, double, Complex> transfer)
        {
            var field = new Complex[source.Length];
            for (var ky = 0; ky < _n; ky++)
            {
                var fy = (ky < _n / 2 ? ky : ky - _n) / (double)_n;
                for (var kx = 0; kx < _n; kx++)
                {
                    var fx = (kx < _n / 2 ? kx : kx - _n) / (double)_n;
                    var i = (ky * _n) + kx;
                    field[i] = source[i] * transfer(fx, fy);
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

    /// <summary>The free glow and sky alone that bring <paramref name="source"/>'s object nearest <paramref name="plane"/>, solved linearly.</summary>
    public static GlowFit FitGlow(ReadOnlySpan<float> plane, Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (coefficients, cost) = new Projection(plane.ToArray(), source).Solve(null);
        return new GlowFit([.. coefficients[..^1]], coefficients[^1], Math.Sqrt(cost / source.Fitted.Length));
    }

    /// <summary>
    /// The ghost, free glow and sky that bring <paramref name="source"/>'s object nearest <paramref name="plane"/> over its fitted pixels:
    /// a coarse search over the copy's shift and elliptical disk, then Levenberg-Marquardt over those five, the copy's strength, the
    /// glow's shares and the sky solved linearly at each step.
    /// </summary>
    public static GhostFit FitGhost(ReadOnlySpan<float> plane, Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var projection = new Projection(plane.ToArray(), source);
        double[] best = [];
        var bestCost = double.PositiveInfinity;
        var disks = new[] { (0.95, 0.0) }.Concat(new[] { 0.0, 45, 90, 135 }.Select(a => (0.6, a))).ToArray();
        var holes = new[] { 0.05, 0.5 };
        foreach (var dx in new[] { -8.0, 0, 8 })
        {
            foreach (var dy in new[] { -8.0, 0, 8 })
            {
                foreach (var radius in new[] { 8.0, 16, 24, 32 })
                {
                    foreach (var (ratio, angle) in disks)
                    {
                        foreach (var hole in holes)
                        {
                            var (_, cost) = projection.Solve(source.Ghost(1, dx, dy, radius, ratio, angle, hole));
                            if (cost < bestCost)
                            {
                                (bestCost, best) = (cost, [dx, dy, radius, Math.Log((ratio - MinRatio) / (1 - ratio)), angle, Math.Log(hole / (MaxObstruction - hole))]);
                            }
                        }
                    }
                }
            }
        }
        float[] Shape(ReadOnlySpan<double> p) => source.Ghost(1, p[0], p[1], p[2], Ratio(p[3]), p[4], Obstruction(p[5]));
        var result = LevenbergMarquardt.Fit(best, source.Fitted.Length, (p, residuals) =>
        {
            var shape = Shape(p);
            projection.Residuals(shape, projection.Solve(shape).Coefficients, residuals);
        }, [1e-3, 1e-3, 1e-3, 1e-4, 1e-2, 1e-4], maxIterations: 60);
        var q = result.Parameters;
        var (c, final) = projection.Solve(Shape(q));
        return new GhostFit(c[0], q[0], q[1], Math.Abs(q[2]), Ratio(q[3]), ((q[4] % 180) + 180) % 180, Obstruction(q[5]), [.. c[1..^1]], c[^1],
            Math.Sqrt(final / source.Fitted.Length));
    }

    /// <summary>Coma in the ghost's place: the flare, free glow and sky that bring the object nearest <paramref name="plane"/>.</summary>
    public static ComaFit FitComa(ReadOnlySpan<float> plane, Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var projection = new Projection(plane.ToArray(), source);
        double[] best = [];
        var bestCost = double.PositiveInfinity;
        for (var angle = 0.0; angle < 360; angle += 45)
        {
            foreach (var length in new[] { 6.0, 15, 30 })
            {
                var (_, cost) = projection.Solve(source.Flare(1, length, angle));
                if (cost < bestCost)
                {
                    (bestCost, best) = (cost, [length, angle]);
                }
            }
        }
        var result = LevenbergMarquardt.Fit(best, source.Fitted.Length, (p, residuals) =>
        {
            var shape = source.Flare(1, p[0], p[1]);
            projection.Residuals(shape, projection.Solve(shape).Coefficients, residuals);
        }, [1e-3, 1e-3], maxIterations: 60);
        var q = result.Parameters;
        var (c, final) = projection.Solve(source.Flare(1, q[0], q[1]));
        return new ComaFit(c[0], q[0], q[1], [.. c[1..^1]], c[^1], Math.Sqrt(final / source.Fitted.Length));
    }

    // The fitted ratio's floor: twenty times longer than wide is a smear along one axis, which 2022-09-03's L shell is (it ran to 0.2).
    private const double MinRatio = 0.05;

    private static double Ratio(double w) => MinRatio + ((1 - MinRatio) / (1 + Math.Exp(-w)));

    // The fitted hole's ceiling: past nine tenths an annulus is a thin ring, which no secondary shadows.
    private const double MaxObstruction = 0.9;

    private static double Obstruction(double v) => MaxObstruction / (1 + Math.Exp(-v));

    /// <summary>
    /// What of <paramref name="plane"/> is not round about <paramref name="source"/>'s object: beyond the object, each pixel less the mean
    /// of the plane at its distance outside it (in half-pixel steps, out to <paramref name="reach"/> px); zero on the object and past
    /// the reach. A ghost's non-round part is the shell a free round glow leaves, and what is taken out.
    /// </summary>
    public static float[] NonRound(ReadOnlySpan<float> plane, Source source, double reach = 80)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bins = (int)(reach * 2) + 1;
        var (sum, count) = (new double[bins], new int[bins]);
        for (var i = 0; i < plane.Length; i++)
        {
            var d = source.Distance[i];
            if (source.Clear[i] && d < reach)
            {
                var b = (int)(d * 2);
                (sum[b], count[b]) = (sum[b] + plane[i], count[b] + 1);
            }
        }
        var result = new float[plane.Length];
        for (var i = 0; i < plane.Length; i++)
        {
            var d = source.Distance[i];
            if (source.Clear[i] && d < reach)
            {
                var b = (int)(d * 2);
                result[i] = (float)(plane[i] - (sum[b] / count[b]));
            }
        }
        return result;
    }

    /// <summary>
    /// The quadrupole of <paramref name="plane"/>'s non-round part (<see cref="NonRound"/>) in each band of distance outside the object:
    /// twice the mean of the non-round part times cos 2 theta and sin 2 theta about the object's centroid, as an amplitude and a long axis.
    /// </summary>
    public static ImmutableArray<Quadrupole> Quadrupoles(ReadOnlySpan<float> plane, Source source, ReadOnlySpan<(double From, double To)> bands)
    {
        ArgumentNullException.ThrowIfNull(source);
        var nonRound = NonRound(plane, source);
        var result = ImmutableArray.CreateBuilder<Quadrupole>(bands.Length);
        foreach (var (from, to) in bands)
        {
            double c = 0, s = 0;
            var n = 0;
            for (var i = 0; i < nonRound.Length; i++)
            {
                var d = source.Distance[i];
                if (!source.Clear[i] || d < from || d >= to)
                {
                    continue;
                }
                var theta = Math.Atan2((i / source.Width) - source.Centre.Y, (i % source.Width) - source.Centre.X);
                (c, s, n) = (c + (nonRound[i] * Math.Cos(2 * theta)), s + (nonRound[i] * Math.Sin(2 * theta)), n + 1);
            }
            (c, s) = (2 * c / Math.Max(n, 1), 2 * s / Math.Max(n, 1));
            result.Add(new Quadrupole(from, to, Math.Sqrt((c * c) + (s * s)), ((Math.Atan2(s, c) * 90 / Math.PI) + 180) % 180));
        }
        return result.MoveToImmutable();
    }

    /// <summary>
    /// The residual's mean in <paramref name="count"/> sectors around the object's centre, over the pixels <paramref name="from"/> to
    /// <paramref name="to"/> px outside it, each with its standard error: a lopsided shell the fit left, which no ring's mean can see
    /// beside a round glow that takes any ring's mean.
    /// </summary>
    public static ImmutableArray<(double AngleDeg, double Mean, double StandardError)> Sectors(ReadOnlySpan<float> residual, Source source, double from = 3, double to = 20, int count = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (cx, cy) = source.Centre;
        var (sum, sum2, counts) = (new double[count], new double[count], new int[count]);
        foreach (var i in source.Fitted)
        {
            var d = source.Distance[i];
            if (d < from || d >= to)
            {
                continue;
            }
            var angle = Math.Atan2((i / source.Width) - cy, (i % source.Width) - cx) * 180 / Math.PI;
            var sector = (int)(((angle + 360 + (180.0 / count)) % 360) / (360.0 / count)) % count;
            (sum[sector], sum2[sector], counts[sector]) = (sum[sector] + residual[i], sum2[sector] + ((double)residual[i] * residual[i]), counts[sector] + 1);
        }
        var sectors = ImmutableArray.CreateBuilder<(double, double, double)>(count);
        for (var k = 0; k < count; k++)
        {
            var mean = counts[k] > 0 ? sum[k] / counts[k] : double.NaN;
            var spread = counts[k] > 1 ? Math.Sqrt(Math.Max(0, (sum2[k] / counts[k]) - (mean * mean))) : double.NaN;
            sectors.Add((k * 360.0 / count, mean, spread / Math.Sqrt(Math.Max(counts[k], 1))));
        }
        return sectors.MoveToImmutable();
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
                    if (source.Clear[i] && d >= r && d < r + width)
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

    // The linear half of every fit: the glow's shares and the sky solved by least squares over the fitted pixels, beside one shape whose
    // nonlinear numbers the caller is fitting (a copy, a flare, or none). The glow's Gram matrix over the fitted pixels is taken once.
    private sealed class Projection
    {
        private readonly float[] _target;
        private readonly ImmutableArray<int> _fitted;
        private readonly float[][] _shapes;
        private readonly double[,] _gram;
        private readonly double[] _rhs;
        private readonly double _targetSquared;

        public Projection(float[] target, Source source)
        {
            (_target, _fitted, _shapes) = (target, source.Fitted, source.GlowShapes);
            var m = _shapes.Length + 1;
            (_gram, _rhs) = (new double[m, m], new double[m]);
            var row = new double[m];
            foreach (var i in _fitted)
            {
                Row(i, row);
                double t = target[i];
                _targetSquared += t * t;
                for (var j = 0; j < m; j++)
                {
                    _rhs[j] += row[j] * t;
                    for (var k = j; k < m; k++)
                    {
                        _gram[j, k] += row[j] * row[k];
                    }
                }
            }
            for (var j = 0; j < m; j++)
            {
                for (var k = 0; k < j; k++)
                {
                    _gram[j, k] = _gram[k, j];
                }
            }
        }

        // The coefficients, the shape's first when there is one, then the glow's shares and the sky, and the residual's sum of squares.
        public (double[] Coefficients, double Cost) Solve(float[]? shape)
        {
            var g = _gram.GetLength(0);
            var offset = shape is null ? 0 : 1;
            var m = g + offset;
            var (a, b) = (new double[m, m], new double[m]);
            for (var j = 0; j < g; j++)
            {
                b[j + offset] = _rhs[j];
                for (var k = 0; k < g; k++)
                {
                    a[j + offset, k + offset] = _gram[j, k];
                }
            }
            if (shape is not null)
            {
                var row = new double[g];
                foreach (var i in _fitted)
                {
                    Row(i, row);
                    double s = shape[i];
                    a[0, 0] += s * s;
                    b[0] += s * _target[i];
                    for (var j = 0; j < g; j++)
                    {
                        a[0, j + 1] += s * row[j];
                    }
                }
                for (var j = 1; j < m; j++)
                {
                    a[j, 0] = a[0, j];
                }
            }
            // A tent that never reaches a fitted pixel is a zero column: a ridge a ten-billionth of the largest diagonal keeps it at zero.
            var ridged = (double[,])a.Clone();
            var largest = 0.0;
            for (var j = 0; j < m; j++)
            {
                largest = Math.Max(largest, a[j, j]);
            }
            for (var j = 0; j < m; j++)
            {
                ridged[j, j] += 1e-10 * largest;
            }
            var c = PlanetaryCeilings.Solve(ridged, b);
            var cost = _targetSquared;
            for (var j = 0; j < m; j++)
            {
                cost -= 2 * c[j] * b[j];
                for (var k = 0; k < m; k++)
                {
                    cost += c[j] * a[j, k] * c[k];
                }
            }
            return (c, Math.Max(cost, 0));
        }

        public void Residuals(float[]? shape, double[] coefficients, Span<double> residuals)
        {
            var offset = shape is null ? 0 : 1;
            for (var n = 0; n < _fitted.Length; n++)
            {
                var i = _fitted[n];
                var model = coefficients[^1] + (shape is null ? 0 : coefficients[0] * shape[i]);
                for (var j = 0; j < _shapes.Length; j++)
                {
                    model += coefficients[j + offset] * _shapes[j][i];
                }
                residuals[n] = _target[i] - model;
            }
        }

        private void Row(int i, double[] row)
        {
            for (var j = 0; j < _shapes.Length; j++)
            {
                row[j] = _shapes[j][i];
            }
            row[_shapes.Length] = 1;
        }
    }

}
