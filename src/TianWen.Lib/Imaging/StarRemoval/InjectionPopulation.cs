using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>Where injected stars go (docs/plans/star-remover-training.md, H3).</summary>
public enum InjectionPlacement
{
    /// <summary>Uniformly at random on present pixels, at least <see cref="InjectionPopulation.ExclusionFwhm"/> FWHM from
    /// every site the plate builder subtracted: the arm the bootstrap trains on.</summary>
    Random = 0,

    /// <summary>On the subtracted sites themselves: the control that shows whether a net learns the plate's own
    /// artefacts. Run once.</summary>
    AtSite = 1,
}

/// <summary>One cell's injection: the stars in frame coordinates, how many were asked for and how many were placed.</summary>
public sealed record InjectionPlan(ImmutableArray<InjectedStar> Stars, int Requested, int Placed, bool SaturatedFallback);

/// <summary>
/// A master's own star population, read once from its starless plate's catalogue, from which each cell's injected stars are
/// drawn (docs/plans/star-remover-training.md, R1): as many as the plate builder subtracted in the cell, each a real star's
/// per-channel amplitudes taken whole (so the brightness distribution and the colours are the field's), each channel's
/// profile from the master's measured PSF, elongated as the real stars within <see cref="MomentReachPx"/> are; and, on
/// request, one saturated star with the clip levels and the wing amplitude of one of the master's own saturated stars.
/// </summary>
public sealed class InjectionPopulation
{
    /// <summary>A random-arm star keeps at least this many FWHM from every subtracted site.</summary>
    public const double ExclusionFwhm = 3.0;

    /// <summary>Tries for a random-arm position before the star is dropped and counted.</summary>
    public const int PlacementTries = 200;

    /// <summary>How far the stars an injected star takes its elongation from may be.</summary>
    public const double MomentReachPx = 384.0;

    /// <summary>At most this many of the nearest measured stars set an injected star's elongation.</summary>
    public const int MomentStars = 8;

    /// <summary>A master with no saturated star of its own clips an injected one at this fraction of each channel's brightest
    /// pixel.</summary>
    public const double FallbackClipFraction = 0.95;

    /// <summary>
    /// A saturated star clipped a channel where the amplitude its wings were fitted with passes its core's height above the
    /// plate by this factor; otherwise that channel takes no clip. A core is only a clip level where it was cut: a line
    /// filter's dark channel holds the star at the sky, and as a level there it flattened every neighbour under the
    /// footprint, and an unclipped bright channel's core is only the star's own peak.
    /// </summary>
    public const double ClippedWingExcess = 1.2;

    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly BitMatrix? _absent;
    private readonly (double Fwhm, double Beta)[] _psf;
    private readonly double _fieldFwhm;
    private readonly (double X, double Y)[] _sites;
    private readonly SiteGrid _siteGrid;
    private readonly ImmutableArray<double>[] _amplitudes;
    private readonly (ImmutableArray<double> Amplitudes, ImmutableArray<double> Clips)[] _saturated;
    private readonly ImmutableArray<double> _fallbackClips;
    private readonly (double X, double Y, double E, double Theta)[] _moments;

    private InjectionPopulation(
        int width, int height, int channels, BitMatrix? absent, (double Fwhm, double Beta)[] psf, (double X, double Y)[] sites,
        ImmutableArray<double>[] amplitudes, (ImmutableArray<double>, ImmutableArray<double>)[] saturated, ImmutableArray<double> fallbackClips,
        (double X, double Y, double E, double Theta)[] moments)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _absent = absent;
        _psf = psf;
        _fieldFwhm = psf.Average(static p => p.Fwhm);
        _sites = sites;
        _siteGrid = new SiteGrid(sites, Math.Max(1.0, ExclusionFwhm * _fieldFwhm));
        _amplitudes = amplitudes;
        _saturated = saturated;
        _fallbackClips = fallbackClips;
        _moments = moments;
    }

    /// <summary>Subtracted sites the population knows.</summary>
    public int Sites => _sites.Length;

    /// <summary>Unsaturated stars the brightness is drawn from.</summary>
    public int AmplitudePool => _amplitudes.Length;

    /// <summary>Saturated stars a saturated injection is drawn from (0 means the fallback clip).</summary>
    public int SaturatedPool => _saturated.Length;

    /// <summary>Stars the elongation is measured on.</summary>
    public int MomentPool => _moments.Length;

    /// <summary>
    /// Reads a master's population. <paramref name="master"/> and <paramref name="plate"/> are the master and its starless
    /// plate as stored (the catalogue's units); <paramref name="scale"/> takes amplitudes and clip levels into the units the
    /// stars will be rendered in (the exporter's unit range, 1 over its divisor); <paramref name="psf"/> is each channel's
    /// FWHM and Moffat beta, the master's own measurement. <paramref name="absent"/> is the frame's absent canvas.
    /// </summary>
    public static InjectionPopulation Build(
        ImmutableArray<FittedStar> catalogue, Image master, Image plate, double scale, IReadOnlyList<(double Fwhm, double Beta)> psf, BitMatrix? absent)
    {
        var channels = master.ChannelCount;
        if (psf.Count != channels)
        {
            throw new ArgumentException($"{psf.Count} channel PSFs for a {channels}-channel master", nameof(psf));
        }
        var width = master.Width;
        var height = master.Height;
        var fieldFwhm = psf.Average(static p => p.Fwhm);

        var sites = catalogue.Where(static s => s.Outcome == StarFitOutcome.Subtracted).Select(static s => ((double)s.X, (double)s.Y)).ToArray();
        static bool Usable(FittedStar s, int channels)
            => s.Outcome == StarFitOutcome.Subtracted && s.Model == StarFitModel.Moffat && s.ChannelAmplitudes.Length == channels
                && s.ChannelAmplitudes.All(float.IsFinite);
        var amplitudes = catalogue
            .Where(s => Usable(s, channels) && !s.Saturated && s.ChannelAmplitudes.Max() > 0)
            .Select(s => s.ChannelAmplitudes.Select(a => Math.Max(0.0, a) * scale).ToImmutableArray())
            .ToArray();
        if (amplitudes.Length == 0)
        {
            throw new InvalidOperationException(
                "the plate's catalogue has no unsaturated Moffat-subtracted star with per-channel amplitudes to draw from (a catalogue written before R1 has none)");
        }

        var planes = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            planes[c] = master.GetChannelSpan(c).ToArray();
        }
        var platePlanes = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            platePlanes[c] = plate.GetChannelSpan(c).ToArray();
        }
        var saturated = catalogue
            .Where(s => Usable(s, channels) && s.Saturated)
            .Select(s => SaturatedEntry(s, planes, platePlanes, width, height, scale))
            .Where(static t => t.Clips.Any(double.IsFinite) && t.Clips.All(static v => v > 0))
            .ToArray();
        var fallbackClips = Enumerable.Range(0, channels)
            .Select(c => FallbackClipFraction * planes[c].Where(float.IsFinite).DefaultIfEmpty(0f).Max() * scale)
            .ToImmutableArray();

        // Elongation off the stars themselves: the master less its plate holds the stars alone, no nebula, so a windowed
        // second moment there is the star's shape. Bright, unsaturated, isolated, Moffat-subtracted stars only.
        var starLum = new float[width * height];
        for (var c = 0; c < channels; c++)
        {
            var p = plate.GetChannelSpan(c);
            for (var i = 0; i < starLum.Length; i++)
            {
                var v = planes[c][i] - p[i];
                starLum[i] += float.IsFinite(v) ? v / channels : 0f;
            }
        }
        var isolation = new SiteGrid(catalogue.Select(static s => ((double)s.X, (double)s.Y)).ToArray(), Math.Max(1.0, 3.0 * fieldFwhm));
        var momentRadius = Math.Max(3.0, 2.0 * fieldFwhm);
        var windowed = WindowedEllipticities(fieldFwhm, psf.Average(static p => p.Beta), momentRadius);
        var moments = new List<(double, double, double, double)>();
        for (var k = 0; k < catalogue.Length; k++)
        {
            var s = catalogue[k];
            if (!Usable(s, channels) || s.Saturated || s.Significance is < 30f or > 1000f
                || isolation.AnyWithin(s.X, s.Y, 3.0 * fieldFwhm, except: k))
            {
                continue;
            }
            if (Moment(starLum, width, height, s.X, s.Y, momentRadius) is { } m)
            {
                moments.Add((s.X, s.Y, UnwindowedEllipticity(m.E, windowed), m.Theta));
            }
        }

        return new InjectionPopulation(width, height, channels, absent, psf.ToArray(), sites, amplitudes, saturated, fallbackClips, moments.ToArray());
    }

    /// <summary>
    /// Draws one cell's stars: the cell spans [<paramref name="cellX"/>, + <paramref name="size"/>) on each axis and the stars
    /// are placed over it and <paramref name="margin"/> pixels around it, so a tile's edge cuts stars as a real one's does.
    /// </summary>
    public InjectionPlan Plan(
        int cellX, int cellY, int size, int margin, InjectionPlacement placement, StarProfileFamily family, double saturatedFraction, Random rng)
    {
        var x0 = Math.Max(0, cellX - margin);
        var y0 = Math.Max(0, cellY - margin);
        var x1 = Math.Min(_width, cellX + size + margin);
        var y1 = Math.Min(_height, cellY + size + margin);
        var inCell = 0;
        var regionSites = new List<(double X, double Y)>();
        foreach (var (sx, sy) in _sites)
        {
            if (sx >= cellX && sx < cellX + size && sy >= cellY && sy < cellY + size)
            {
                inCell++;
            }
            if (sx >= x0 && sx < x1 && sy >= y0 && sy < y1)
            {
                regionSites.Add((sx, sy));
            }
        }

        var requested = placement == InjectionPlacement.AtSite
            ? regionSites.Count
            : (int)Math.Round(inCell * ((double)(x1 - x0) * (y1 - y0)) / ((double)size * size));
        var stars = ImmutableArray.CreateBuilder<InjectedStar>(requested + 1);
        var placed = 0;
        for (var i = 0; i < requested; i++)
        {
            var at = placement == InjectionPlacement.AtSite ? Jitter(regionSites[i], rng) : RandomPosition(x0, y0, x1, y1, rng);
            if (at is not { } p)
            {
                continue;
            }
            var amplitudes = _amplitudes[rng.Next(_amplitudes.Length)];
            stars.Add(new InjectedStar(p.X, p.Y, amplitudes, ProfilesAt(p.X, p.Y, family), Saturated: false, ImmutableArray<double>.Empty));
            placed++;
        }

        var fallback = false;
        if (saturatedFraction > 0 && rng.NextDouble() < saturatedFraction)
        {
            var at = placement == InjectionPlacement.AtSite
                ? regionSites.Count > 0 ? Jitter(regionSites[rng.Next(regionSites.Count)], rng) : null
                : RandomPosition(x0, y0, x1, y1, rng);
            if (at is { } p)
            {
                ImmutableArray<double> amplitudes;
                ImmutableArray<double> clips;
                if (_saturated.Length > 0)
                {
                    (amplitudes, clips) = _saturated[rng.Next(_saturated.Length)];
                }
                else
                {
                    // No saturated star of its own to copy: clip at the fallback level, a real star's colour taken past it.
                    fallback = true;
                    clips = _fallbackClips;
                    var colour = _amplitudes[rng.Next(_amplitudes.Length)];
                    var peak = colour.Max();
                    var over = 2.0 + rng.NextDouble() * 18.0;
                    var target = clips.Max() * over;
                    amplitudes = colour.Select(a => peak > 0 ? a / peak * target : target).ToImmutableArray();
                }
                stars.Add(new InjectedStar(p.X, p.Y, amplitudes, ProfilesAt(p.X, p.Y, family), Saturated: true, clips));
            }
        }
        return new InjectionPlan(stars.ToImmutable(), requested, placed, fallback);
    }

    private (double X, double Y)? Jitter((double X, double Y) site, Random rng)
        => (site.X + (rng.NextDouble() - 0.5) * 0.5, site.Y + (rng.NextDouble() - 0.5) * 0.5);

    private (double X, double Y)? RandomPosition(int x0, int y0, int x1, int y1, Random rng)
    {
        var exclusion = ExclusionFwhm * _fieldFwhm;
        for (var t = 0; t < PlacementTries; t++)
        {
            var x = x0 - 0.5 + rng.NextDouble() * (x1 - x0);
            var y = y0 - 0.5 + rng.NextDouble() * (y1 - y0);
            var px = (int)Math.Round(x);
            var py = (int)Math.Round(y);
            if (px < 0 || py < 0 || px >= _width || py >= _height || (_absent is { } a && a[py, px]))
            {
                continue;
            }
            if (_siteGrid.AnyWithin(x, y, exclusion))
            {
                continue;
            }
            return (x, y);
        }
        return null;
    }

    /// <summary>Each channel's profile at (<paramref name="x"/>, <paramref name="y"/>), elongated as the nearby stars are.</summary>
    public ImmutableArray<StarProfile> ProfilesAt(double x, double y, StarProfileFamily family)
    {
        var (q, theta) = ElongationAt(x, y);
        var profiles = ImmutableArray.CreateBuilder<StarProfile>(_channels);
        for (var c = 0; c < _channels; c++)
        {
            profiles.Add(new StarProfile(family, _psf[c].Fwhm, _psf[c].Beta, q, theta));
        }
        return profiles.MoveToImmutable();
    }

    /// <summary>
    /// The axis ratio and major-axis angle at a point: the mean of the nearest measured stars' ellipticities as spin-2
    /// vectors (so orientations average, and random ones cancel), turned back into a ratio; round where none is near.
    /// </summary>
    public (double AxisRatio, double AngleRad) ElongationAt(double x, double y)
    {
        if (_moments.Length == 0)
        {
            return (1.0, 0.0);
        }
        var nearest = _moments
            .Select(m => (m, D2: (m.X - x) * (m.X - x) + (m.Y - y) * (m.Y - y)))
            .Where(static t => t.D2 <= MomentReachPx * MomentReachPx)
            .OrderBy(static t => t.D2)
            .Take(MomentStars)
            .ToArray();
        if (nearest.Length == 0)
        {
            return (1.0, 0.0);
        }
        double re = 0, im = 0;
        foreach (var (m, _) in nearest)
        {
            re += m.E * Math.Cos(2.0 * m.Theta);
            im += m.E * Math.Sin(2.0 * m.Theta);
        }
        re /= nearest.Length;
        im /= nearest.Length;
        var e = Math.Min(0.95, Math.Sqrt(re * re + im * im));
        return (Math.Sqrt(1.0 - e * e), 0.5 * Math.Atan2(im, re));
    }

    // A saturated star's amplitudes and, per channel, its clip: the core's height where the wings extrapolate past it
    // (ClippedWingExcess), else none (positive infinity). A core that does not stand above the plate is no level either.
    private static (ImmutableArray<double> Amplitudes, ImmutableArray<double> Clips) SaturatedEntry(
        FittedStar s, float[][] planes, float[][] platePlanes, int width, int height, double scale)
    {
        var channels = planes.Length;
        var amplitudes = s.ChannelAmplitudes.Select(a => Math.Max(0.0, a) * scale).ToImmutableArray();
        var clips = ImmutableArray.CreateBuilder<double>(channels);
        var ix = Math.Clamp((int)Math.Round(s.X), 0, width - 1);
        var iy = Math.Clamp((int)Math.Round(s.Y), 0, height - 1);
        for (var c = 0; c < channels; c++)
        {
            var core = CoreMax(planes[c], width, height, s.X, s.Y, 3.0) * scale;
            var floor = platePlanes[c][(iy * width) + ix] * scale;
            clips.Add(double.IsFinite(core) && double.IsFinite(floor) && core > floor && amplitudes[c] > ClippedWingExcess * (core - floor)
                ? core
                : double.PositiveInfinity);
        }
        return (amplitudes, clips.MoveToImmutable());
    }

    // The brightest finite pixel within r of a point.
    private static double CoreMax(float[] plane, int width, int height, double cx, double cy, double r)
    {
        var best = double.NegativeInfinity;
        var ri = (int)Math.Ceiling(r);
        var x0 = (int)Math.Round(cx);
        var y0 = (int)Math.Round(cy);
        for (var y = Math.Max(0, y0 - ri); y <= Math.Min(height - 1, y0 + ri); y++)
        {
            for (var x = Math.Max(0, x0 - ri); x <= Math.Min(width - 1, x0 + ri); x++)
            {
                var v = plane[y * width + x];
                if (v > best && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                {
                    best = v;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// A star's ellipticity e = sqrt(1 - b^2/a^2) and major-axis angle from its flux-weighted second moments within
    /// <paramref name="radius"/> (weights the positive light only); null where the light is not enough to read.
    /// </summary>
    internal static (double E, double Theta)? Moment(float[] plane, int width, int height, double cx, double cy, double radius)
    {
        double sw = 0, sxx = 0, syy = 0, sxy = 0;
        var ri = (int)Math.Ceiling(radius);
        var x0 = (int)Math.Round(cx);
        var y0 = (int)Math.Round(cy);
        for (var y = Math.Max(0, y0 - ri); y <= Math.Min(height - 1, y0 + ri); y++)
        {
            for (var x = Math.Max(0, x0 - ri); x <= Math.Min(width - 1, x0 + ri); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy > radius * radius)
                {
                    continue;
                }
                var w = Math.Max(0f, plane[y * width + x]);
                sw += w;
                sxx += w * dx * dx;
                syy += w * dy * dy;
                sxy += w * dx * dy;
            }
        }
        if (!(sw > 0))
        {
            return null;
        }
        sxx /= sw;
        syy /= sw;
        sxy /= sw;
        var mean = 0.5 * (sxx + syy);
        var spread = Math.Sqrt(0.25 * (sxx - syy) * (sxx - syy) + sxy * sxy);
        var major = mean + spread;
        var minor = mean - spread;
        if (!(major > 0) || !(minor >= 0))
        {
            return null;
        }
        return (Math.Sqrt(Math.Max(0.0, 1.0 - minor / major)), 0.5 * Math.Atan2(2.0 * sxy, sxx - syy));
    }

    /// <summary>The axis ratios <see cref="WindowedEllipticities"/> tabulates, round first.</summary>
    private const double CalibrationRatioStep = 0.01;

    private const double CalibrationRatioMin = 0.2;

    /// <summary>
    /// What <see cref="Moment"/> reads, through a window of <paramref name="radius"/>, on the master's model profile at each
    /// axis ratio from 1 down to <see cref="CalibrationRatioMin"/>, averaged over four sub-pixel centres. A circular window
    /// cuts an elongated star's wings harder along its major axis than its minor, so a windowed moment reads every star
    /// rounder than it is: a star of axis ratio 0.6 read 0.68 through a window of two FWHM. Reading the measurement back
    /// through this table (<see cref="UnwindowedEllipticity"/>) takes the window's rounding out.
    /// </summary>
    private static double[] WindowedEllipticities(double fwhm, double beta, double radius)
    {
        var half = (int)Math.Ceiling(radius) + 2;
        var size = (2 * half) + 1;
        var count = (int)Math.Round((1.0 - CalibrationRatioMin) / CalibrationRatioStep) + 1;
        var table = new double[count];
        var plane = new float[size * size];
        ReadOnlySpan<(double X, double Y)> offsets = [(0.0, 0.0), (0.5, 0.0), (0.0, 0.5), (0.5, 0.5)];
        for (var k = 0; k < count; k++)
        {
            var profile = new StarProfile(StarProfileFamily.Moffat, fwhm, beta, 1.0 - (k * CalibrationRatioStep), 0.0);
            var sum = 0.0;
            foreach (var (ox, oy) in offsets)
            {
                var cx = half + ox;
                var cy = half + oy;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        plane[(y * size) + x] = (float)profile.PixelMean(x, y, cx, cy);
                    }
                }
                sum += Moment(plane, size, size, cx, cy, radius) is { } m ? m.E : 0.0;
            }
            table[k] = sum / offsets.Length;
        }
        return table;
    }

    /// <summary>The ellipticity whose windowed reading is <paramref name="measured"/>, by <paramref name="windowed"/>.</summary>
    private static double UnwindowedEllipticity(double measured, double[] windowed)
    {
        if (!(measured > windowed[0]))
        {
            return 0.0;
        }
        for (var k = 1; k < windowed.Length; k++)
        {
            if (measured <= windowed[k])
            {
                var t = (measured - windowed[k - 1]) / (windowed[k] - windowed[k - 1]);
                var q = 1.0 - ((k - 1 + t) * CalibrationRatioStep);
                return Math.Sqrt(1.0 - (q * q));
            }
        }
        return Math.Sqrt(1.0 - (CalibrationRatioMin * CalibrationRatioMin));
    }

    /// <summary>Points bucketed by position, for "is anything within r of here".</summary>
    private sealed class SiteGrid
    {
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private readonly (double X, double Y)[] _points;
        private readonly double _cell;

        public SiteGrid((double X, double Y)[] points, double cell)
        {
            _points = points;
            _cell = cell;
            for (var i = 0; i < points.Length; i++)
            {
                var key = Key((int)Math.Floor(points[i].X / cell), (int)Math.Floor(points[i].Y / cell));
                if (!_cells.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    _cells[key] = list;
                }
                list.Add(i);
            }
        }

        private static long Key(int cx, int cy) => ((long)cy << 32) ^ (uint)cx;

        public bool AnyWithin(double x, double y, double radius, int except = -1)
        {
            var reach = (int)Math.Ceiling(radius / _cell);
            var cx = (int)Math.Floor(x / _cell);
            var cy = (int)Math.Floor(y / _cell);
            var r2 = radius * radius;
            for (var gy = cy - reach; gy <= cy + reach; gy++)
            {
                for (var gx = cx - reach; gx <= cx + reach; gx++)
                {
                    if (!_cells.TryGetValue(Key(gx, gy), out var list))
                    {
                        continue;
                    }
                    foreach (var i in list)
                    {
                        var dx = _points[i].X - x;
                        var dy = _points[i].Y - y;
                        if (i != except && dx * dx + dy * dy < r2)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
