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

    /// <summary>
    /// Outside the limb the glow the truth has there, never the sharpening: the stack times the share of its glow the planet's own model
    /// keeps through the pupil alone, but for its moons (#1171). The sharpening's side lobes outside the limb are what made the dark band
    /// (the negative lobe floored at the sky) and the faint bright arcs beyond it (the positive ones, let through up to the stack's glow).
    /// </summary>
    ModelGlow,

    /// <summary>
    /// Outside the limb the planet's own model through the pupil alone, the smooth diffraction glow the truth has there, but for its moons
    /// (#1171): neither the sharpening's side lobes nor the stack's seeing glow, and nothing divided by a model that falls to nothing.
    /// </summary>
    ModelOutside,

    /// <summary>
    /// Outside the limb the stack with its glow swapped: the planet's model through the stack's blur taken out and the same model through
    /// the pupil alone put in, but for its moons (#1171). The stack's noise, its sky and anything faint beyond the planet stay, so the
    /// sharpening's window leaves no seam, while the seeing glow and the sharpening's side lobes go.
    /// </summary>
    GlowSwapped,

    /// <summary>
    /// <see cref="ModelOutside"/> out to 1.5 radii, blended to the stack as it is by the sharpening window's inscribed circle (at most
    /// 2.5 radii), but for its moons (#1171): the model's clean limb without the square seam the window's edge left at a deep stretch.
    /// </summary>
    ModelFeathered,
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
    /// How the limb is kept from ringing: outside the limb the planet's own model through the pupil, feathered back to the stack far out,
    /// but for its moons (#1171; docs/plans/planetary-restoration.md, "The trough at the limb"). The sharpening's side lobes outside the
    /// limb were the cause of the dark limb: bounded (#1168) held the negative one at the sky, a black band, and let the positive ones
    /// through up to the stack's seeing glow, faint arcs, strongest on the lit side. Drawing no sharpening there and the truth's smooth
    /// diffraction glow instead took the twins' limb profile error from 0.0252 to 0.0159 at a band error of 1.929 against 1.939, with no
    /// band, no ring and no seam by eye on the twins, 2022-09-03 Red and both colour captures.
    /// </summary>
    public PlanetaryLimbFix Fix { get; init; } = PlanetaryLimbFix.ModelFeathered;

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

    /// <summary>
    /// When given, the kernel is the physical one (<see cref="PlanetaryFinestBand.FitPhysical"/>) fitted to the limb's edge from 0.02 cycles
    /// a pixel to this, and carried by its physics to the cutoff, rather than the edge as read at every frequency. Measured on Saturn's twin
    /// and not adopted (S4, #1184): its clear limb, the two polar arcs, reads true only to about 0.15 cycles a pixel, yet at reaches of 0.12
    /// to 0.3 the bands summed 4.15 to 4.48 over the three colours against the raw edge's 4.06.
    /// </summary>
    public double? EdgeReach { get; init; }

    /// <summary>
    /// Read Saturn's edge off its rings' outer rim as well as its polar limb (<see cref="PlanetaryFinestBand.RingEdge"/>, pooled bin by bin,
    /// #1256): the polar arcs alone read the finest bands true only to about 0.15 cycles a pixel. No effect without rings.
    /// </summary>
    public bool RingEdge { get; init; }
}

/// <summary>A sharpened master and how it was sharpened: the first channel's derived gains (empty for the preset) and its edge's transfer.</summary>
public sealed record PlanetarySharpenResult(Image Sharpened, bool Derived, PlanetaryLimbFix Fix, ImmutableArray<double> Gains, double EdgeAtTenth, double EdgeAtThreeTenths)
{
    /// <summary>
    /// The limb this sharpening drew, kept for a live view to draw every later master's limb alike (#1201): the fit and the planet's model
    /// through the pupil for each channel. Null where the gains were not derived (no pupil).
    /// </summary>
    public PlanetaryLiveLimb? Limb { get; init; }
}

/// <summary>
/// The planetary master's sharpening (the enhanced pipeline, #1159): a trous gains derived from the stack's own power, its white noise floor,
/// the planet's disk and its blur read off the limb's edge against the limb fit's sharp model through the pupil's diffraction (R8 part 3 and
/// follow-up 3), applied with the limb kept from ringing (<see cref="PlanetaryLimbFix"/>). Each channel is derived on its own, inside a
/// power-of-two window about the planet, normalised on the disk (sky 0, disk 1) and mapped back into the master's units; outside the
/// window the master is as stacked but for its moons, each sharpened by the same gains in a window of its own (#1211). Null when the planet's
/// limb cannot be fitted.
/// </summary>
public static class PlanetarySharpening
{
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
        var limbWindow = PlanetaryLimbWindow.Of(fit, limbOptions, aspect, width, height);
        var (own, disk, size, sharp) = (limbWindow.Own, limbWindow.Disk, limbWindow.Size, limbWindow.Sharp);
        var models = new float[master.ChannelCount][];
        var diffractions = new RadialTransfer[master.ChannelCount];

        var planes = Image.CreateChannelData(master.ChannelCount, height, width);
        var (derived, firstGains, edgeAt01, edgeAt03) = (options.Pupil is not null, ImmutableArray<double>.Empty, double.NaN, double.NaN);
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var plane = master.GetChannelSpan(c);
            var (level, scale) = PlanetaryMetrics.NormalisationLevels(plane, width, height, own);
            var window = limbWindow.Cut(plane, width, height, level, scale);
            float[] sharpened;
            // A moon beyond the window takes the same gains, in a window of its own (#1211).
            Func<float[], float[]> sharpenMoon;
            if (options.Pupil is { } pupil)
            {
                var wavelengthNm = options.WavelengthsNm[Math.Min(c, options.WavelengthsNm.Length - 1)];
                var diffraction = limbWindow.Diffraction(pupil, wavelengthNm);
                var diskTarget = limbWindow.Through(diffraction);
                (models[c], diffractions[c]) = (diskTarget, diffraction);
                var edge = PlanetaryFinestBand.LimbEdge(window, diskTarget, size, size, disk, fit, aspect);
                if (options.RingEdge && disk.Rings is not null)
                {
                    edge = EdgeProfile.Pooled(edge, PlanetaryFinestBand.RingEdge(window, diskTarget, size, size, disk));
                }
                var physical = options.EdgeReach is { } reach
                    ? PlanetaryFinestBand.FitPhysical(edge, pupil.DiameterM / (wavelengthNm * 1e-9) / ShortExposurePsf.ArcsecPerRadian * limbWindow.ArcsecPerPixel, 0.02, reach)
                    : (PhysicalKernel?)null;
                var kernel = Tabulated(f => Math.Clamp(physical is { } p ? p.TransferAt(f) : edge.TransferAt(f), 0, 1));
                var power = PlanetaryWaveletGains.StackPower(window, size, size, disk);
                var white = PlanetaryInverse.WhiteNoise(PlanetaryWaveletGains.Interior(window, size, size, disk), size, size);
                var noise = ImmutableArray.CreateRange(Enumerable.Repeat(white, power.Length));
                var wiener = PlanetaryWaveletGains.Wiener(power, noise, kernel);
                var blurredDisk = PlanetaryInverse.Apply(diskTarget, size, size, kernel);
                var gains = options.NonNegative
                    ? PlanetaryWaveletGains.FitNonNegative(power, wiener, diskTarget, blurredDisk, size, size, disk, kernel)
                    : PlanetaryWaveletGains.Fit(power, wiener, diskTarget, blurredDisk, size, size, disk, held: FinestHeld(master.ChannelCount, c, options.ColourFinestBand) ? 1 : 0);
                sharpened = Apply(window, size, disk, sharp, f => kernel(f) * diffraction.At(f), diffraction.At, gains.AsSpan(), [], options.Fix, diskTarget, blurredDisk);
                sharpenMoon = w => PlanetaryDering.Sharpen(w, PlanetaryLimbWindow.MoonWindowSize, PlanetaryLimbWindow.MoonWindowSize, gains.AsSpan());
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
                sharpenMoon = w => PlanetaryDering.Sharpen(w, PlanetaryLimbWindow.MoonWindowSize, PlanetaryLimbWindow.MoonWindowSize, gains, thresholds);
                if (c == 0)
                {
                    (firstGains, edgeAt01, edgeAt03) = ([.. gains], edge.TransferAt(0.1), edge.TransferAt(0.3));
                }
            }
            limbWindow.Paste(plane, sharpened, planes[c], width, height, level, scale);
            var channel = c;
            limbWindow.PasteMoonsBeyond(plane, planes[c], width, height, level, scale, (x0, y0) => sharpenMoon(PlanetaryLimbWindow.CutAt(
                master.GetChannelSpan(channel), width, height, x0, y0, PlanetaryLimbWindow.MoonWindowSize, level, scale)));
        }
        var image = new Image(planes, BitDepth.Float32, master.MaxValue, master.MinValue, master.Pedestal, master.ImageMeta);
        return new PlanetarySharpenResult(image, derived, derived ? options.Fix : PlanetaryLimbFix.LimbChannel, firstGains, edgeAt01, edgeAt03)
        {
            Limb = options.Pupil is { } kept ? PlanetaryLiveLimb.Kept(master, fit, limbOptions, aspect, limbWindow, kept, options.WavelengthsNm, [.. models], [.. diffractions]) : null,
        };
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
            PlanetaryLimbFix.ModelGlow => PlanetaryDering.Outside(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk, PlanetaryDering.OutsideLimb.ModelGlow,
                PlanetaryDering.GlowShare(diskTarget, blurredDisk)),
            PlanetaryLimbFix.ModelOutside => PlanetaryDering.Outside(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk, PlanetaryDering.OutsideLimb.Model,
                model: diskTarget),
            PlanetaryLimbFix.GlowSwapped => PlanetaryDering.Outside(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk, PlanetaryDering.OutsideLimb.GlowSwapped,
                model: diskTarget, blurredModel: blurredDisk),
            PlanetaryLimbFix.ModelFeathered => PlanetaryDering.Outside(PlanetaryDering.Sharpen(window, size, size, g, t), window, size, size, disk, PlanetaryDering.OutsideLimb.ModelFeathered,
                model: diskTarget),
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
}
