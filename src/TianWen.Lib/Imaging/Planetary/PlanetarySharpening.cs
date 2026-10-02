using System;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// How a derived sharpening keeps the limb from ringing (docs/plans/planetary-restoration.md, R8 follow-up 2): the plane sharpened as it
/// is, held at the sky, with the limb as its own channel (the limb fit's model through the stack's blur taken out, the residual sharpened,
/// the model put back through the pupil's diffraction alone), with each layer's gain feathered to nothing at the limb, or held at the sky
/// and, outside the limb, at or below the stack (#1168).
/// </summary>
public enum PlanetaryLimbFix
{
    Plain,
    Floored,
    LimbChannel,
    Feathered,
    Bounded,
}

/// <summary>
/// What a planetary master is sharpened against: the planet and the instant its aspect is read at, and the telescope's pupil and each
/// channel's wavelength, which set the diffraction the limb's edge is read over (R8 follow-up 3). Without a pupil the gains cannot be
/// derived, and <see cref="PlanetarySharpening.Sharpen"/> sharpens by <see cref="WaveletSharpenOptions.PlanetaryDefault"/> with the limb kept
/// as stacked.
/// </summary>
/// <param name="Planet">The planet, whose limb fit and belts the edge is read with.</param>
/// <param name="When">The instant the master shows the planet at (the capture's middle).</param>
/// <param name="Pupil">The telescope's pupil; null for none known.</param>
public sealed record PlanetarySharpenOptions(CatalogIndex Planet, DateTimeOffset When, Pupil? Pupil)
{
    /// <summary>Each channel's effective wavelength, nm, the last repeated for any channel beyond it (550 nm, a broadband luminance).</summary>
    public ImmutableArray<double> WavelengthsNm { get; init; } = [550];

    /// <summary>
    /// How the limb is kept from ringing: floored at the sky, the measured choice (docs/plans/planetary-restoration.md, the enhanced
    /// pipeline): plain and floored tied for the least error over the three twins (1.776, the limb as its own channel 1.803, feathered
    /// 1.928), every one under 0.02 of undershoot on the real capture, and the floor can only take away what digs below the sky.
    /// </summary>
    public PlanetaryLimbFix Fix { get; init; } = PlanetaryLimbFix.Floored;

    /// <summary>
    /// Fit the gains with their composite through the kernel held non-negative (<see cref="PlanetaryWaveletGains.FitNonNegative"/>, R8
    /// follow-up 1) rather than free. Measured and not adopted: the free fit meets a steep Wiener boost with one large gain and a negative
    /// one beside it (9.70 and -0.65 on the warped twin) yet lands near the truth, while the held fit switches the finest band off and
    /// blurs (1.840 to 1.992 against the stack's 1.500 to 1.667 on the twins).
    /// </summary>
    public bool NonNegative { get; init; }
}

/// <summary>A sharpened master and how it was sharpened: the first channel's derived gains (empty for the preset) and its edge's transfer.</summary>
public sealed record PlanetarySharpenResult(Image Sharpened, bool Derived, PlanetaryLimbFix Fix, ImmutableArray<double> Gains, double EdgeAtTenth, double EdgeAtThreeTenths);

/// <summary>
/// The planetary master's sharpening (the enhanced pipeline, #1159): a trous gains derived from the stack's own power, its white noise floor,
/// the planet's disk and its blur read off the limb's edge against the limb fit's sharp model through the pupil's diffraction (R8 part 3 and
/// follow-up 3), applied with the limb kept from ringing (<see cref="PlanetaryLimbFix"/>). Each channel is derived on its own, inside a
/// power-of-two window about the planet, normalised on the disk (sky 0, disk 1) and mapped back into the master's units; outside the
/// window the master is as stacked. Null when the planet's limb cannot be fitted.
/// </summary>
public static class PlanetarySharpening
{
    // How far past the limb the window reaches, px: the edge reads 16 px either side (PlanetaryFinestBand.EdgeReach), the coarsest band's
    // support about as much again.
    private const int Margin = 48;

    /// <summary>The sharpened master, a new image the caller owns, and what it took; null when the limb cannot be fitted.</summary>
    public static PlanetarySharpenResult? Sharpen(Image master, PlanetarySharpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(options);
        var aspect = PhysicalEphemeris.Compute(options.Planet, options.When);
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        if (PlanetaryLimbFit.Fit(master, limbOptions) is not { } fit)
        {
            return null;
        }
        var (width, height) = (master.Width, master.Height);
        var own = MetricDisk.From(fit, limbOptions.AxisRatio);
        var size = Math.Max(128, NextPowerOfTwo((int)Math.Ceiling(2 * (fit.EquatorialRadius + Margin))));
        var (x0, y0) = ((int)Math.Round(fit.CenterX) - (size / 2), (int)Math.Round(fit.CenterY) - (size / 2));
        var disk = own with { X = own.X - x0, Y = own.Y - y0 };
        var sharpFull = PlanetaryLimbFit.SharpModel(fit, limbOptions, width, height);
        var sharp = Cut(PlanetaryMetrics.Normalise(sharpFull, width, height, own), width, height, x0, y0, size);
        var arcsecPerPixel = aspect.AngularDiameterArcsec / 2 / fit.EquatorialRadius;

        var planes = Image.CreateChannelData(master.ChannelCount, height, width);
        var (derived, firstGains, edgeAt01, edgeAt03) = (options.Pupil is not null, ImmutableArray<double>.Empty, double.NaN, double.NaN);
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var plane = master.GetChannelSpan(c);
            var (level, scale) = PlanetaryMetrics.NormalisationLevels(plane, width, height, own);
            var window = Cut(PlanetaryMetrics.Normalise(plane, width, height, own), width, height, x0, y0, size);
            float[] sharpened;
            if (options.Pupil is { } pupil)
            {
                var wavelength = options.WavelengthsNm[Math.Min(c, options.WavelengthsNm.Length - 1)] * 1e-9;
                var diffraction = PlanetaryInverse.Diffraction(pupil, wavelength, arcsecPerPixel);
                var diskTarget = PlanetaryInverse.Apply(sharp, size, size, diffraction.At);
                var edge = PlanetaryFinestBand.LimbEdge(window, diskTarget, size, size, disk, fit, aspect);
                var kernel = Tabulated(f => Math.Clamp(edge.TransferAt(f), 0, 1));
                var power = PlanetaryWaveletGains.StackPower(window, size, size, disk);
                var white = PlanetaryInverse.WhiteNoise(PlanetaryWaveletGains.Interior(window, size, size, disk), size, size);
                var noise = ImmutableArray.CreateRange(Enumerable.Repeat(white, power.Length));
                var wiener = PlanetaryWaveletGains.Wiener(power, noise, kernel);
                var blurredDisk = PlanetaryInverse.Apply(diskTarget, size, size, kernel);
                var gains = options.NonNegative
                    ? PlanetaryWaveletGains.FitNonNegative(power, wiener, diskTarget, blurredDisk, size, size, disk, kernel)
                    : PlanetaryWaveletGains.Fit(power, wiener, diskTarget, blurredDisk, size, size, disk);
                sharpened = Apply(window, size, disk, sharp, f => kernel(f) * diffraction.At(f), diffraction.At, gains.AsSpan(), [], options.Fix);
                if (c == 0)
                {
                    (firstGains, edgeAt01, edgeAt03) = (gains, edge.TransferAt(0.1), edge.TransferAt(0.3));
                }
            }
            else
            {
                // No pupil, no diffraction to read the edge over: the preset, with the limb's model through the stack's own blur (its edge
                // against the sharp model itself) taken out and put back as it was, so the limb is kept as stacked and cannot ring.
                var edge = PlanetaryFinestBand.LimbEdge(window, sharp, size, size, disk, fit, aspect);
                var blur = Tabulated(f => Math.Clamp(edge.TransferAt(f), 0, 1));
                var preset = WaveletSharpenOptions.PlanetaryDefault;
                var gains = new double[preset.Gains.Length];
                var thresholds = new double[preset.Gains.Length];
                for (var j = 0; j < gains.Length; j++)
                {
                    gains[j] = preset.Gains[j];
                    // The preset's thresholds are in a master's [0, 1] units; the window's are the disk's.
                    thresholds[j] = j < preset.DenoiseThresholds.Length ? preset.DenoiseThresholds[j] / scale : 0;
                }
                sharpened = PlanetaryDering.LimbChannel(window, size, size, sharp, blur, blur, p => PlanetaryDering.Sharpen(p, size, size, gains, thresholds));
                if (c == 0)
                {
                    (firstGains, edgeAt01, edgeAt03) = ([.. gains], edge.TransferAt(0.1), edge.TransferAt(0.3));
                }
            }
            Paste(plane, sharpened, planes[c], width, height, x0, y0, size, level, scale);
        }
        var image = new Image(planes, BitDepth.Float32, master.MaxValue, master.MinValue, master.Pedestal, master.ImageMeta);
        return new PlanetarySharpenResult(image, derived, derived ? options.Fix : PlanetaryLimbFix.LimbChannel, firstGains, edgeAt01, edgeAt03);
    }

    // The window sharpened by the gains, the limb kept from ringing as asked.
    private static float[] Apply(float[] window, int size, MetricDisk disk, float[] sharp, Func<double, double> total, Func<double, double> diffraction,
        ReadOnlySpan<double> gains, ReadOnlySpan<double> thresholds, PlanetaryLimbFix fix)
    {
        var (g, t) = (gains.ToArray(), thresholds.ToArray());
        return fix switch
        {
            PlanetaryLimbFix.Floored => PlanetaryDering.Floor(PlanetaryDering.Sharpen(window, size, size, g, t)),
            PlanetaryLimbFix.LimbChannel => PlanetaryDering.LimbChannel(window, size, size, sharp, total, diffraction, p => PlanetaryDering.Sharpen(p, size, size, g, t)),
            PlanetaryLimbFix.Feathered => PlanetaryDering.Feathered(window, size, size, disk, g, t),
            PlanetaryLimbFix.Bounded => PlanetaryDering.Bounded(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk),
            _ => PlanetaryDering.Sharpen(window, size, size, g, t),
        };
    }

    // A transfer sampled once on a fine grid of frequencies and read back linearly: an edge's transfer is a sum over its bins, and the
    // gains' fit and the inverse read it at every frequency of the window.
    private static Func<double, double> Tabulated(Func<double, double> transfer)
    {
        const int samples = 512;
        const double reach = 0.75;
        var table = new double[samples + 1];
        for (var i = 0; i <= samples; i++)
        {
            table[i] = transfer(reach * i / samples);
        }
        return f =>
        {
            var x = Math.Clamp(f / reach * samples, 0, samples);
            var i = Math.Min((int)x, samples - 1);
            return table[i] + ((x - i) * (table[i + 1] - table[i]));
        };
    }

    // The square of `size` at (x0, y0) of a full-frame plane, zero outside the frame (the normalised sky).
    private static float[] Cut(ReadOnlySpan<float> plane, int width, int height, int x0, int y0, int size)
    {
        var window = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            var sy = y0 + y;
            if (sy < 0 || sy >= height)
            {
                continue;
            }
            for (var x = 0; x < size; x++)
            {
                var sx = x0 + x;
                if (sx >= 0 && sx < width)
                {
                    window[(y * size) + x] = plane[(sy * width) + sx];
                }
            }
        }
        return window;
    }

    // The master's plane with the sharpened window put back in its units.
    private static void Paste(ReadOnlySpan<float> plane, float[] window, float[,] into, int width, int height, int x0, int y0, int size, double level, double scale)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (wx, wy) = (x - x0, y - y0);
                into[y, x] = wx >= 0 && wx < size && wy >= 0 && wy < size
                    ? (float)(level + (window[(wy * size) + wx] * scale))
                    : plane[(y * width) + x];
            }
        }
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
