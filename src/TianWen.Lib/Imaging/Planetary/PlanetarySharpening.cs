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

    /// <summary>Outside the limb the stack as it is, but for its moons: the sharpening reaches the limb and no further (#1171).</summary>
    HeldOutside,

    /// <summary>
    /// Bounded, and outside the limb at or above the stack times the share of its glow the planet's own model keeps through the pupil
    /// alone (the limb model through the diffraction over the model through the stack's blur): the glow the truth has there (#1171).
    /// </summary>
    ModelFloor,

    /// <summary>Bounded at the limb, blended to the stack as it is by 1.1 radii (#1171).</summary>
    Blended,
}

/// <summary>
/// What a colour master's finest a trous band (0.25 to 0.5 cycles a pixel) gets (#1187): its derived gain, or held at 1, as stacked, on
/// every colour, or on red and blue only. That band lies above a colour plane's own Nyquist (a red or blue photosite every second pixel,
/// green's quincunx somewhat finer), where a colour stack holds little but noise and the CFA's residue, which a derived gain of 12 to 20
/// lifted into a 2-pixel lattice over the disk (2024-12-15 Uranus-C, 2022-10-09, the colour twin; a mono master has none).
/// </summary>
public enum PlanetaryColourFinestBand
{
    Derived,
    Held,
    HeldButGreen,
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
    /// How the limb is kept from ringing: bounded, held at the sky and outside the limb never brighter than the stack but for its moons
    /// (the owner's choice of 2026-10-02, #1168, the moons #1181; docs/plans/planetary-restoration.md, "The sharpening's ring outside the limb"). Floored alone left
    /// a ring above the sky; the limb as its own channel read a little truer on the twins (limb profile error 0.0254 against 0.0270)
    /// but rang out to 1.3 radii on 2022-09-03 Red; bounded keeps floored's band error (1.777 against 1.776) without the outer ring.
    /// </summary>
    public PlanetaryLimbFix Fix { get; init; } = PlanetaryLimbFix.Bounded;

    /// <summary>
    /// Fit the gains with their composite through the kernel held non-negative (<see cref="PlanetaryWaveletGains.FitNonNegative"/>, R8
    /// follow-up 1) rather than free. Measured and not adopted: the free fit meets a steep Wiener boost with one large gain and a negative
    /// one beside it (9.70 and -0.65 on the warped twin) yet lands near the truth, while the held fit switches the finest band off and
    /// blurs (1.840 to 1.992 against the stack's 1.500 to 1.667 on the twins).
    /// </summary>
    public bool NonNegative { get; init; }

    /// <summary>
    /// What a colour (three-channel) master's finest band gets (<see cref="PlanetaryColourFinestBand"/>); a mono master's is always derived.
    /// Held, the other gains are fitted around it (<see cref="PlanetaryWaveletGains.Fit"/>'s <c>held</c>), never set after a free fit.
    /// Held on every colour by default (#1187, docs/plans/planetary-restoration.md, "A colour master's finest band"): on the colour twin's
    /// two seeds it left 4.37 and 4.67 of error over bands 1 to 4 and the three colours where the derived band left 6.31 and 7.05 (the
    /// stack 6.53 and 6.37), and the lattice on the real colour captures went with it.
    /// </summary>
    public PlanetaryColourFinestBand ColourFinestBand { get; init; } = PlanetaryColourFinestBand.Held;
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
                    : PlanetaryWaveletGains.Fit(power, wiener, diskTarget, blurredDisk, size, size, disk, held: FinestHeld(master.ChannelCount, c, options.ColourFinestBand) ? 1 : 0);
                sharpened = Apply(window, size, disk, sharp, f => kernel(f) * diffraction.At(f), diffraction.At, gains.AsSpan(), [], options.Fix, diskTarget, blurredDisk);
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

    // Whether channel `c` of a master of `channels` keeps its finest band as stacked.
    private static bool FinestHeld(int channels, int c, PlanetaryColourFinestBand finest) => channels == 3 && finest switch
    {
        PlanetaryColourFinestBand.Held => true,
        PlanetaryColourFinestBand.HeldButGreen => c != 1,
        _ => false,
    };

    // The window sharpened by the gains, the limb kept from ringing as asked.
    private static float[] Apply(float[] window, int size, MetricDisk disk, float[] sharp, Func<double, double> total, Func<double, double> diffraction,
        ReadOnlySpan<double> gains, ReadOnlySpan<double> thresholds, PlanetaryLimbFix fix, float[] diskTarget, float[] blurredDisk)
    {
        var (g, t) = (gains.ToArray(), thresholds.ToArray());
        return fix switch
        {
            PlanetaryLimbFix.Floored => PlanetaryDering.Floor(PlanetaryDering.Sharpen(window, size, size, g, t)),
            PlanetaryLimbFix.LimbChannel => PlanetaryDering.LimbChannel(window, size, size, sharp, total, diffraction, p => PlanetaryDering.Sharpen(p, size, size, g, t)),
            PlanetaryLimbFix.Feathered => PlanetaryDering.Feathered(window, size, size, disk, g, t),
            PlanetaryLimbFix.Bounded => PlanetaryDering.Bounded(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk),
            PlanetaryLimbFix.HeldOutside => PlanetaryDering.Outside(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk, PlanetaryDering.OutsideLimb.Stack),
            PlanetaryLimbFix.ModelFloor => PlanetaryDering.Outside(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk, PlanetaryDering.OutsideLimb.ModelFloor,
                PlanetaryDering.GlowShare(diskTarget, blurredDisk)),
            PlanetaryLimbFix.Blended => PlanetaryDering.Outside(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk, PlanetaryDering.OutsideLimb.Blended),
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

    // The square of `size` at (x0, y0) of a full-frame plane, the frame mirrored about its edges where the square runs past them. Never
    // padded with zeros: a 200 px crop of a 150 px disk sits in a 256 px window, and zeros there were a step at the frame's edge for
    // the sharpening to ring on and a sky without noise for the moons' threshold, which then took the frame's edge for 16 moons a
    // channel and freed it unbounded (the real-capture validation, 2026-10-03).
    private static float[] Cut(ReadOnlySpan<float> plane, int width, int height, int x0, int y0, int size)
    {
        var window = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            var sy = Mirrored(y0 + y, height);
            for (var x = 0; x < size; x++)
            {
                window[(y * size) + x] = plane[(sy * width) + Mirrored(x0 + x, width)];
            }
        }
        return window;
    }

    // An index mirrored into [0, n), the edge sample repeated (..., 1, 0 | 0, 1, ..., n - 1 | n - 1, n - 2, ...).
    private static int Mirrored(int i, int n)
    {
        var m = ((i % (2 * n)) + (2 * n)) % (2 * n);
        return m < n ? m : (2 * n) - 1 - m;
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
