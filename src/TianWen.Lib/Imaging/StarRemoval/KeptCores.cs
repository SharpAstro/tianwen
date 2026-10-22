using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// One galaxy-like core a cell's synthetic background holds (<see cref="KeptCores"/>), in the cell's coordinates: its
/// centre, its core's FWHM across both axes in pixels, its major axis's angle, the bulge's share of the peak and its scale
/// in core widths, the peak drawn in noise sigma (in the channel the core is brightest in against its noise), and the peak
/// per channel on the master's unit scale, after <see cref="Capped"/> held its top under the master's ceiling.
/// </summary>
public sealed record KeptCore(double X, double Y, double FwhmMinorPx, double FwhmMajorPx, double AngleRad, double BulgeShare, double BulgeScale,
    double DrawnSigma, ImmutableArray<double> Peak, bool Capped);

/// <summary>
/// #1658's training half (docs/plans/star-remover-training.md, "Kept cores"): a bright extended galaxy-like core drawn into a
/// <see cref="Share"/> of the synthetic background's cells, so it stands in the input and in the starless target alike and a
/// remover learns to keep it. Sized from M31's nucleus on the owner's Andromeda master: 6,127 / 4,760 / 3,951 noise sigma
/// (R / G / B) at its peak, half its peak about 3.5 px out (2.5 to 3 star widths across), 0.14 of its peak at 12 to 20 px,
/// 0.06 at 20 to 40 px and 0.02 at 40 to 80 px. A core is a Gaussian of FWHM <see cref="MinWidths"/> to
/// <see cref="MaxWidths"/> PSF widths across its minor axis (log-uniform), never narrower: R2d's knots test taught a model to
/// keep compact knots at 1.6 widths and it left bright stars' cores in (<see cref="SyntheticBackground.BrightKnotsWide"/>).
/// Under it a bulge, a Moffat of index <see cref="BulgeBeta"/> (M31's wing falls as r^-1.5 past its nucleus) of the same
/// ellipse, <see cref="MinBulgeShare"/> to <see cref="MaxBulgeShare"/> of the peak and <see cref="MinBulgeScale"/> to
/// <see cref="MaxBulgeScale"/> core widths in scale. Fitted to M31's three wing readings, a bulge of one core width (7 px)
/// holds 0.55 of the peak: its own profile reads 0.25, 0.11 and 0.04 at 16, 30 and 60 px, so 0.14, 0.06 and 0.02 of the
/// whole, as M31's does.
/// The peak is log-uniform from <see cref="MinPeakSigma"/> to <see cref="MaxPeakSigma"/> noise sigma; the colour one of the
/// master's redder stars' (a bulge is an old, red population). Every number comes from the caller's stream, a stream of
/// its own, so with the cores off every other draw of the export is the one it was.
/// </summary>
public sealed class KeptCores
{
    /// <summary>The share of cells that hold a core.</summary>
    public const double Share = 0.15;

    /// <summary>The faintest and brightest core's peak, in noise sigma (M31's nucleus stands 6,127 in red).</summary>
    public const double MinPeakSigma = 200.0, MaxPeakSigma = 6000.0;

    /// <summary>A core's FWHM across its minor axis, in PSF widths: never under <see cref="MinWidths"/>.</summary>
    public const double MinWidths = 2.5, MaxWidths = 6.0;

    /// <summary>The narrowest axis ratio (minor over major).</summary>
    public const double MinAxisRatio = 0.6;

    /// <summary>The bulge's share of the peak.</summary>
    public const double MinBulgeShare = 0.35, MaxBulgeShare = 0.6;

    /// <summary>The bulge's scale, in core widths along each axis.</summary>
    public const double MinBulgeScale = 1.0, MaxBulgeScale = 2.0;

    /// <summary>The bulge's Moffat index: its wing falls as r^(-2 beta).</summary>
    public const double BulgeBeta = 0.75;

    /// <summary>The whole profile is tapered to zero from <see cref="TaperFrom"/> to <see cref="TaperTo"/> bulge scales, so a
    /// core reaches no further than that.</summary>
    public const double TaperFrom = 8.0, TaperTo = 16.0;

    /// <summary>A core's top, its peak over the level beneath it, stays under this share of each channel's ceiling.</summary>
    public const double CeilingShare = 0.8;

    /// <summary>A core's centre keeps this far from the cell's edges.</summary>
    public const int RimPx = 32;

    /// <param name="psfFwhm">The master's PSF width in pixels.</param>
    /// <param name="colours">The master's redder stars' colours, each normalised to its brightest channel
    /// (<see cref="RedderColours"/>).</param>
    /// <param name="ceilings">Per channel the level a core's top stays under a share of, on the master's unit scale (the
    /// population's fallback clip); empty for none.</param>
    public KeptCores(double psfFwhm, ImmutableArray<ImmutableArray<double>> colours, ImmutableArray<double> ceilings)
    {
        if (!(psfFwhm > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(psfFwhm), psfFwhm, "a PSF width is positive");
        }
        PsfFwhm = psfFwhm;
        Colours = colours;
        Ceilings = ceilings.IsDefault ? [] : ceilings;
    }

    /// <summary>The master's PSF width in pixels.</summary>
    public double PsfFwhm { get; }

    /// <summary>The colours a core draws from.</summary>
    public ImmutableArray<ImmutableArray<double>> Colours { get; }

    /// <summary>Per channel the ceiling a core's top stays under <see cref="CeilingShare"/> of; empty for none.</summary>
    public ImmutableArray<double> Ceilings { get; }

    /// <summary>
    /// The redder half of a pool of star colours: those whose first channel over their last stands at or above the pool's
    /// median, each normalised to its brightest channel. A colour with a channel at or under zero is left out. A mono pool
    /// is one colour, its own.
    /// </summary>
    public static ImmutableArray<ImmutableArray<double>> RedderColours(IEnumerable<ImmutableArray<double>> colours, int channels)
    {
        if (channels < 2)
        {
            return [[1.0]];
        }
        var usable = colours.Where(a => a.Length == channels && a.All(static v => v > 0 && double.IsFinite(v))).ToArray();
        if (usable.Length == 0)
        {
            return [[.. Enumerable.Repeat(1.0, channels)]];
        }
        var ratios = usable.Select(static a => a[0] / a[^1]).Order().ToArray();
        var median = ratios[ratios.Length / 2];
        return [.. usable.Where(a => a[0] / a[^1] >= median).Select(static a =>
        {
            var max = a.Max();
            return a.Select(v => v / max).ToImmutableArray();
        })];
    }

    /// <summary>
    /// Draws a core into a <see cref="Share"/> of the cells from <paramref name="random"/>, adding it to
    /// <paramref name="planes"/> (one <paramref name="size"/> px cell per channel, the level it is capped against), its peak in
    /// <paramref name="noiseSigma"/> per channel. Returns the core, or null for a cell without one.
    /// </summary>
    public KeptCore? AddTo(float[][] planes, int size, ReadOnlySpan<double> noiseSigma, Random random)
    {
        if (random.NextDouble() >= Share)
        {
            return null;
        }
        var channels = planes.Length;
        var x = RimPx + (random.NextDouble() * Math.Max(0, size - (2 * RimPx)));
        var y = RimPx + (random.NextDouble() * Math.Max(0, size - (2 * RimPx)));
        var minor = PsfFwhm * MinWidths * Math.Pow(MaxWidths / MinWidths, random.NextDouble());
        var major = minor / (MinAxisRatio + ((1 - MinAxisRatio) * random.NextDouble()));
        var angle = random.NextDouble() * Math.PI;
        var bulgeShare = MinBulgeShare + ((MaxBulgeShare - MinBulgeShare) * random.NextDouble());
        var bulgeScale = MinBulgeScale + ((MaxBulgeScale - MinBulgeScale) * random.NextDouble());
        var drawnSigma = MinPeakSigma * Math.Pow(MaxPeakSigma / MinPeakSigma, random.NextDouble());
        var colour = Colours.Length > 0 ? Colours[random.Next(Colours.Length)] : [];

        // The peak in the channel the core stands brightest in against its noise is the drawn sigma; the others follow the colour.
        var weights = new double[channels];
        var standing = 0.0;
        for (var c = 0; c < channels; c++)
        {
            weights[c] = c < colour.Length ? colour[c] : 1.0;
            var noise = c < noiseSigma.Length ? noiseSigma[c] : noiseSigma[^1];
            standing = Math.Max(standing, noise > 0 ? weights[c] / noise : 0);
        }
        var peak = new double[channels];
        var scale = 1.0;
        var ix = Math.Clamp((int)Math.Round(x), 0, size - 1);
        var iy = Math.Clamp((int)Math.Round(y), 0, size - 1);
        for (var c = 0; c < channels; c++)
        {
            peak[c] = standing > 0 ? drawnSigma * weights[c] / standing : 0;
            if (c < Ceilings.Length && peak[c] > 0)
            {
                var room = Math.Max(0.0, (CeilingShare * Ceilings[c]) - planes[c][(iy * size) + ix]);
                scale = Math.Min(scale, room / peak[c]);
            }
        }
        for (var c = 0; c < channels; c++)
        {
            peak[c] *= scale;
        }
        var core = new KeptCore(x, y, minor, major, angle, bulgeShare, bulgeScale, drawnSigma, [.. peak], scale < 1.0);
        Draw(planes, size, core);
        return core;
    }

    /// <summary>A core's profile at a cell pixel, one at its centre: the Gaussian core and the bulge under it, tapered to zero
    /// from <see cref="TaperFrom"/> to <see cref="TaperTo"/> bulge scales.</summary>
    public static double Profile(KeptCore core, double x, double y)
    {
        var (sin, cos) = Math.SinCos(core.AngleRad);
        var dx = x - core.X;
        var dy = y - core.Y;
        // In FWHM units along each axis.
        var u = ((dx * cos) + (dy * sin)) / core.FwhmMajorPx;
        var v = ((-dx * sin) + (dy * cos)) / core.FwhmMinorPx;
        var r2 = (u * u) + (v * v);
        var b2 = r2 / (core.BulgeScale * core.BulgeScale);
        if (b2 >= TaperTo * TaperTo)
        {
            return 0.0;
        }
        var gaussian = Math.Exp(-4.0 * Math.Log(2.0) * r2);
        var bulge = Math.Pow(1.0 + b2, -BulgeBeta);
        var b = Math.Sqrt(b2);
        var taper = b <= TaperFrom ? 1.0 : Math.Pow(Math.Cos(0.5 * Math.PI * (b - TaperFrom) / (TaperTo - TaperFrom)), 2);
        return taper * (((1.0 - core.BulgeShare) * gaussian) + (core.BulgeShare * bulge));
    }

    /// <summary>How far a core reaches from its centre, in pixels: <see cref="TaperTo"/> bulge scales along its major axis.</summary>
    public static double ReachPx(KeptCore core) => TaperTo * core.BulgeScale * core.FwhmMajorPx;

    private static void Draw(float[][] planes, int size, KeptCore core)
    {
        var reach = (int)Math.Ceiling(ReachPx(core));
        for (var y = Math.Max(0, (int)core.Y - reach); y <= Math.Min(size - 1, (int)core.Y + reach); y++)
        {
            for (var x = Math.Max(0, (int)core.X - reach); x <= Math.Min(size - 1, (int)core.X + reach); x++)
            {
                var p = Profile(core, x, y);
                if (p <= 0)
                {
                    continue;
                }
                for (var c = 0; c < planes.Length; c++)
                {
                    planes[c][(y * size) + x] += (float)(core.Peak[c] * p);
                }
            }
        }
    }
}
