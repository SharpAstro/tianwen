using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The limb a live view keeps (#1201, docs/plans/planetary-restoration.md, "The feathered limb on the live view"): Derive's limb fit and the
/// planet's sharp model through the pupil's diffraction for each channel, so that every later master, sharpened by the wavelet dials, is
/// drawn outside the limb as the batch's derived sharpening draws it (<see cref="PlanetaryLimbFix.ModelFeathered"/>): no sharpening there,
/// the model's clean limb feathered back to the stack by the window's inscribed circle, the moons free, the stack as it is beyond the window.
/// Made by <see cref="PlanetarySharpening.Sharpen"/>, from the fit and the models it drew with (<see cref="PlanetarySharpenResult.Limb"/>).
/// Immutable: a disk that moved gives a new one (<see cref="FollowedTo"/>).
/// </summary>
public sealed class PlanetaryLiveLimb
{
    /// <summary>How far, px, the disk may move from where the kept fit was found before the model is drawn again at its new centre.</summary>
    public const double MovePx = 0.25;

    /// <summary>How far the disk's start radius may stray, as a share, before the fit is made again.</summary>
    public const double RadiusShare = 0.05;

    private readonly LimbFitOptions _limbOptions;
    private readonly PlanetAspect _aspect;
    private readonly PlanetaryLimbWindow _window;
    private readonly Pupil _pupil;
    private readonly ImmutableArray<double> _wavelengthsNm;
    private readonly ImmutableArray<float[]> _models;
    private readonly ImmutableArray<RadialTransfer> _diffractions;
    private readonly (double X, double Y, double Radius) _start;
    private readonly (int Width, int Height) _frame;

    private PlanetaryLiveLimb(LimbFit fit, LimbFitOptions limbOptions, PlanetAspect aspect, PlanetaryLimbWindow window, Pupil pupil,
        ImmutableArray<double> wavelengthsNm, ImmutableArray<float[]> models, ImmutableArray<RadialTransfer> diffractions,
        (double X, double Y, double Radius) start, (int Width, int Height) frame)
    {
        Fit = fit;
        _limbOptions = limbOptions;
        _aspect = aspect;
        _window = window;
        _pupil = pupil;
        _wavelengthsNm = wavelengthsNm;
        _models = models;
        _diffractions = diffractions;
        _start = start;
        _frame = frame;
    }

    /// <summary>The limb fit the model is drawn from, where the disk is now.</summary>
    public LimbFit Fit { get; }

    /// <summary>The fit's disk in the frame, with the ephemeris' axis ratio: what "outside the limb" means here (beyond one of its radii).</summary>
    public MetricDisk Disk => _window.Own;

    /// <summary>
    /// The limb <see cref="PlanetarySharpening.Sharpen"/> drew on <paramref name="master"/>, kept: its fit and window, the models and the
    /// diffraction each channel was drawn through, and the fit's own start on the master, which a later master's disk is followed from.
    /// </summary>
    internal static PlanetaryLiveLimb Kept(Image master, in LimbFit fit, LimbFitOptions limbOptions, in PlanetAspect aspect, PlanetaryLimbWindow window,
        Pupil pupil, ImmutableArray<double> wavelengthsNm, ImmutableArray<float[]> models, ImmutableArray<RadialTransfer> diffractions)
    {
        // Where the start places the disk the fit was made on, which a later master's start is set against. The fit itself where the start
        // finds nothing, which cannot happen on the master the fit started from.
        var start = PlanetaryLimbFit.Start(PlanetaryLimbFit.Luminance(master), master.Width, master.Height, limbOptions.AxisRatio)
            ?? (fit.CenterX, fit.CenterY, fit.EquatorialRadius);
        return new PlanetaryLiveLimb(fit, limbOptions, aspect, window, pupil, wavelengthsNm, models, diffractions, start, (master.Width, master.Height));
    }

    /// <summary>
    /// This limb where <paramref name="master"/>'s disk is: itself when the disk has not moved past <see cref="MovePx"/>; the model drawn
    /// again at the new centre when it has; the fit made again, as the batch makes it, when the disk's size strayed past
    /// <see cref="RadiusShare"/> or the frame changed. Null when the start finds no disk or the fit fails, and the master is then shown as
    /// the dials make it.
    /// </summary>
    public PlanetaryLiveLimb? FollowedTo(Image master)
    {
        ArgumentNullException.ThrowIfNull(master);
        var (width, height) = (master.Width, master.Height);
        var luminance = PlanetaryLimbFit.Luminance(master);
        if (PlanetaryLimbFit.Start(luminance, width, height, _limbOptions.AxisRatio) is not { } start)
        {
            return null;
        }
        if ((width, height) != _frame || Math.Abs((start.Radius / _start.Radius) - 1) > RadiusShare)
        {
            // Cold, from the start, as the batch fits (PlanetaryLimbFit.Fit(Image)), so the drawing is the batch's again: a fit warmed from
            // the kept one landed 0.09 px of radius from it on the sharpening's fixture.
            return PlanetaryLimbFit.Fit(luminance, width, height, start.X, start.Y, start.Radius, _limbOptions) is { } refit
                ? Redrawn(refit, start, (width, height), keepDiffractions: false)
                : null;
        }
        var (dx, dy) = (start.X - _start.X, start.Y - _start.Y);
        return (dx * dx) + (dy * dy) <= MovePx * MovePx
            ? this
            : Redrawn(Fit with { CenterX = Fit.CenterX + dx, CenterY = Fit.CenterY + dy }, start, _frame, keepDiffractions: true);
    }

    // The limb drawn again for `fit`: its window and each channel's model, through the kept diffraction when only the centre moved (the
    // plate scale, which the diffraction is drawn at, is the radius's).
    private PlanetaryLiveLimb Redrawn(in LimbFit fit, (double X, double Y, double Radius) start, (int Width, int Height) frame, bool keepDiffractions)
    {
        var window = PlanetaryLimbWindow.Of(fit, _limbOptions, _aspect, frame.Width, frame.Height);
        var models = new float[_models.Length][];
        var diffractions = new RadialTransfer[_models.Length];
        for (var c = 0; c < models.Length; c++)
        {
            diffractions[c] = keepDiffractions ? _diffractions[c] : window.Diffraction(_pupil, _wavelengthsNm[Math.Min(c, _wavelengthsNm.Length - 1)]);
            models[c] = window.Through(diffractions[c]);
        }
        return new PlanetaryLiveLimb(fit, _limbOptions, _aspect, window, _pupil, _wavelengthsNm, [.. models], [.. diffractions], start, frame);
    }

    /// <summary>
    /// <paramref name="sharpened"/>, <paramref name="stacked"/> as the dials sharpened it, drawn outside the limb as the batch's derived
    /// sharpening draws it: inside the limb the sharpening held at the sky, outside it the planet's model through the pupil out to 1.5
    /// radii, feathered back to the stack by the window's inscribed circle, the moons sharpened, and the stack as it is beyond the window
    /// (<see cref="PlanetaryDering.Outside"/> with <see cref="PlanetaryDering.OutsideLimb.ModelFeathered"/>, in the window and the units the
    /// batch uses). A new image the caller owns; <paramref name="stacked"/> must be the master this limb was followed to.
    /// </summary>
    public Image Draw(Image stacked, Image sharpened)
    {
        ArgumentNullException.ThrowIfNull(stacked);
        ArgumentNullException.ThrowIfNull(sharpened);
        var (width, height, channels) = (stacked.Width, stacked.Height, stacked.ChannelCount);
        if ((width, height) != _frame || (sharpened.Width, sharpened.Height, sharpened.ChannelCount) != (width, height, channels))
        {
            throw new ArgumentException($"a {width}x{height} master drawn with a limb kept for {_frame.Width}x{_frame.Height}, or sharpened to another shape");
        }
        var planes = Image.CreateChannelData(channels, height, width);
        for (var c = 0; c < channels; c++)
        {
            var plane = stacked.GetChannelSpan(c);
            var (level, scale) = PlanetaryMetrics.NormalisationLevels(plane, width, height, _window.Own);
            var stack = _window.Cut(plane, width, height, level, scale);
            var slid = _window.Cut(sharpened.GetChannelSpan(c), width, height, level, scale);
            var drawn = PlanetaryDering.Outside(slid, stack, _window.Size, _window.Size, _window.Disk, PlanetaryDering.OutsideLimb.ModelFeathered,
                model: _models[Math.Min(c, _models.Length - 1)]);
            _window.Paste(plane, drawn, planes[c], width, height, level, scale);
        }
        return new Image(planes, sharpened.BitDepth, sharpened.MaxValue, sharpened.MinValue, sharpened.Pedestal, sharpened.ImageMeta, sharpened.SamplesAreUnitReferred);
    }
}

/// <summary>
/// The square window about a fitted planet that a derived sharpening works in (<see cref="PlanetarySharpening"/>), ONE for the batch and
/// a live view's kept limb (<see cref="PlanetaryLiveLimb"/>): a power of two past the limb by the edge's reach and the coarsest band's,
/// the fit's disk in the frame and in the window, and the planet's sharp model, normalised on its own disk.
/// </summary>
internal sealed record PlanetaryLimbWindow(int Size, int X0, int Y0, MetricDisk Own, MetricDisk Disk, float[] Sharp, double ArcsecPerPixel)
{
    // How far past the limb the window reaches, px: the edge reads 16 px either side (PlanetaryFinestBand.EdgeReach), the coarsest band's
    // support about as much again.
    private const int Margin = 48;

    /// <summary>The window about <paramref name="fit"/>'s disk in a <paramref name="width"/> by <paramref name="height"/> frame.</summary>
    public static PlanetaryLimbWindow Of(in LimbFit fit, LimbFitOptions limbOptions, in PlanetAspect aspect, int width, int height)
    {
        var own = MetricDisk.From(fit, limbOptions.AxisRatio);
        var size = Math.Max(128, NextPowerOfTwo((int)Math.Ceiling(2 * (fit.EquatorialRadius + Margin))));
        var (x0, y0) = ((int)Math.Round(fit.CenterX) - (size / 2), (int)Math.Round(fit.CenterY) - (size / 2));
        var sharpFull = PlanetaryLimbFit.SharpModel(fit, limbOptions, width, height);
        var window = new PlanetaryLimbWindow(size, x0, y0, own, own with { X = own.X - x0, Y = own.Y - y0 }, [], aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius);
        return window with { Sharp = window.Cut(sharpFull, width, height, PlanetaryMetrics.NormalisationLevels(sharpFull, width, height, own)) };
    }

    /// <summary>The pupil's diffraction at <paramref name="wavelengthNm"/>, at this window's plate scale.</summary>
    public RadialTransfer Diffraction(Pupil pupil, double wavelengthNm) => PlanetaryInverse.Diffraction(pupil, wavelengthNm * 1e-9, ArcsecPerPixel);

    /// <summary>The planet's sharp model through <paramref name="diffraction"/>: the disk the truth has, in the window's units.</summary>
    public float[] Through(RadialTransfer diffraction) => PlanetaryInverse.Apply(Sharp, Size, Size, diffraction.At);

    /// <summary>The window cut from a full-frame plane and normalised by <paramref name="levels"/> (sky 0, disk 1).</summary>
    public float[] Cut(ReadOnlySpan<float> plane, int width, int height, (double Level, double Scale) levels) => Cut(plane, width, height, levels.Level, levels.Scale);

    /// <summary>
    /// The window cut from a full-frame plane, each sample normalised as <see cref="PlanetaryMetrics.Normalise"/> does, by
    /// <paramref name="level"/> and <paramref name="scale"/>; the frame mirrored about its edges where the window runs past them. Never padded
    /// with zeros: a 200 px crop of a 150 px disk sits in a 256 px window, and zeros there were a step at the frame's edge for the sharpening
    /// to ring on and a sky without noise for the moons' threshold, which then took the frame's edge for 16 moons a channel and freed it
    /// unbounded (the real-capture validation, 2026-10-03).
    /// </summary>
    public float[] Cut(ReadOnlySpan<float> plane, int width, int height, double level, double scale)
    {
        var window = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            var sy = Mirrored(Y0 + y, height);
            for (var x = 0; x < Size; x++)
            {
                window[(y * Size) + x] = (float)((plane[(sy * width) + Mirrored(X0 + x, width)] - level) / scale);
            }
        }
        return window;
    }

    /// <summary><paramref name="plane"/> with <paramref name="window"/> put back in its units, written into <paramref name="into"/>.</summary>
    public void Paste(ReadOnlySpan<float> plane, float[] window, float[,] into, int width, int height, double level, double scale)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (wx, wy) = (x - X0, y - Y0);
                into[y, x] = wx >= 0 && wx < Size && wy >= 0 && wy < Size
                    ? (float)(level + (window[(wy * Size) + wx] * scale))
                    : plane[(y * width) + x];
            }
        }
    }

    // An index mirrored into [0, n), the edge sample repeated (..., 1, 0 | 0, 1, ..., n - 1 | n - 1, n - 2, ...).
    private static int Mirrored(int i, int n)
    {
        var m = ((i % (2 * n)) + (2 * n)) % (2 * n);
        return m < n ? m : (2 * n) - 1 - m;
    }

    private static int NextPowerOfTwo(int value)
    {
        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }
        return p;
    }
}
