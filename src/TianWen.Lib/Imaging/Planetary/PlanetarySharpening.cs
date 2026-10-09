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
/// What the derived sharpening restores toward (#1366). The twins' truth is the OPAL map through the pupil's own diffraction, and so is the
/// default target: the gains undo the air and the stack, never the telescope, and a capture's own post sits at about 1.5 to 2 times it
/// (#1251), much as the gain that undoes a 23 % obstructed pupil's diffraction (1.6 at a quarter of its cutoff, 2.8 at half).
/// </summary>
public enum PlanetarySharpenTarget
{
    /// <summary>The planet through the pupil's own diffraction: what a perfect telescope of this aperture would show.</summary>
    Telescope,

    /// <summary>
    /// The planet itself, band-limited to the pupil's cutoff by <see cref="PlanetaryFinishing.ApertureTaper"/>: the telescope's diffraction
    /// undone too, as far as the stack's noise floor lets the Wiener target go. Past the cutoff nothing of the scene reaches the stack.
    /// </summary>
    Aperture,
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

    /// <summary>
    /// How far past the truth the mid scales are taken (#1251, the owner's call: the truth by default, more as an option). Derived, the
    /// gains are fitted to a texture target with bands 2 and 3 at this many times the truth (<see cref="PlanetaryWaveletGains.Boost"/>), the
    /// disk still to its own sharp model; on the preset, which has no truth to be past, its gains of bands 2 and 3 are multiplied by it
    /// (<see cref="PlanetarySharpening.Strengthened"/>). One, the default, is the derived sharpening, at the truth in bands 2 to 4 on both
    /// twins; a capture's own post sits at about 1.5 to 2 times it there. The limb is still kept from ringing (<see cref="Fix"/>).
    /// </summary>
    public double Strength { get; init; } = 1;

    /// <summary>
    /// What the derived gains restore toward (<see cref="PlanetarySharpenTarget"/>, #1366): the planet through the pupil's diffraction by
    /// default, or the planet itself band-limited to the pupil's cutoff. The edge is still read against the limb model through the pupil (the
    /// stack's blur is the air's over the telescope's), and the model drawn outside the limb is the target's. The batch sharpening's option:
    /// a live view's limb is drawn through the pupil.
    /// </summary>
    public PlanetarySharpenTarget Target { get; init; }

    /// <summary>
    /// Gains to apply in place of the derived ones, finest first, one set per channel (the last repeated for any channel beyond them): a
    /// layer past a set's end is held at 1. Everything else is as derived (the limb fit, its window and the model drawn outside it), so a
    /// set fitted elsewhere, a twin's own best gains, say, is applied as the derived ones would be (#817, whether a twin's gains transfer).
    /// Empty, the default, derives them.
    /// </summary>
    public ImmutableArray<ImmutableArray<double>> FixedGains { get; init; } = [];

    /// <summary>
    /// Strengths the first channel's derived gains are also fitted at (<see cref="PlanetarySharpenResult.Stops"/>), the sharpening itself
    /// applied at <see cref="Strength"/>'s: a live view derives once and switches between them at the cost of a wavelet pass (#1314). Each
    /// is one more gain fit, after the limb fit, the edge and the stack's power, which every strength shares. Empty, the default, fits none;
    /// ignored on the preset and by the non-negative fit.
    /// </summary>
    public ImmutableArray<double> FitStops { get; init; } = [];

    /// <summary>
    /// A finishing step after the derived sharpening (#1279, <see cref="PlanetaryFinishing"/>): none by default; the pupil-cutoff low-pass,
    /// the contrast-adaptive weighting at matched noise, or Kolivas's own damped step as a reference. Only a derived sharpening (a pupil
    /// given) is finished. Measured: the low-pass raised band 1's correlation with the post on all four real captures and changed nothing on
    /// the twins, which are all sampled coarser than their cutoff, so it waits on an oversampled twin (#1281); the adaptive weighting doubled
    /// the band error on every twin and is not adopted, nor is the fitted Wiener low-pass, which shrinks again the noise the derived gains
    /// already shrank (no twin better by 5 %, three captures of four further from their posts).
    /// </summary>
    public PlanetaryFinish Finish { get; init; } = PlanetaryFinish.None;

    /// <summary>The amount <see cref="PlanetaryFinish.Kolivas"/> takes, as his tool's slider (his PlanetRecon judges at 15.6).</summary>
    public double KolivasAmount { get; init; } = 15.6;

    /// <summary>
    /// Sharpen a colour master's detail once, on its luminance (the mean of its planes, through the mean of the channels' wavelengths), and
    /// give every plane the stack's own colour at each pixel, rather than sharpen each channel through its own diffraction (#1295). Per
    /// channel, thin detail changed colour on real Saturn captures: the rings' chroma as a share of the globe's rose from 0.38 to 0.57 and the
    /// gap between the globe and the inner ring turned teal. Opt-in: on the colour twins sharpening each channel moved the globe, the rings and
    /// the gap TOWARD the truth (it unmixes the colours the blur mixed), which one luminance cannot; and without an ADC each colour carries a
    /// smear of its own within its band (blue's the most), which the stack's limb alignment of the colours (#1202) moves but cannot undo and
    /// only a channel's own sharpening treats. No effect on a mono master.
    /// </summary>
    public bool LuminanceOnly { get; init; }

    /// <summary>
    /// The master's two halves (<see cref="PlanetaryStackOptions.Halves"/>), when given: each channel's window is shrunk band by band
    /// against the noise of half their difference (<see cref="PlanetaryBandShrink"/>, #1313) before anything is read off it, so the gains
    /// are derived on the shrunk master and fitted against its own white noise floor. Both on the master's grid, with its channels, in its
    /// units. Null, the default, shrinks nothing.
    /// </summary>
    public PlanetaryStackHalves? ShrinkHalves { get; init; }

    // The master being sharpened is a colour master's luminance (LuminanceOnly): its finest band holds the colour filter's residue as the
    // colour planes do, so it follows ColourFinestBand as they would (#1187).
    internal bool OfColour { get; init; }
}

/// <summary>A sharpened master and how it was sharpened: the first channel's derived gains (empty for the preset) and its edge's transfer.</summary>
public sealed record PlanetarySharpenResult(Image Sharpened, bool Derived, PlanetaryLimbFix Fix, ImmutableArray<double> Gains, double EdgeAtTenth, double EdgeAtThreeTenths)
{
    /// <summary>
    /// The limb this sharpening drew, kept for a live view to draw every later master's limb alike (#1201): the fit and the planet's model
    /// through the pupil for each channel. Null where the gains were not derived (no pupil).
    /// </summary>
    public PlanetaryLiveLimb? Limb { get; init; }

    /// <summary>
    /// Each channel's pupil cutoff in cycles a pixel (<see cref="PlanetaryFinishing.CutoffCyclesPerPixel"/>): past it the scene holds nothing.
    /// One at or past 0.5 is a master sampled coarser than its optics resolve (undersampled), where nothing lies past the cutoff to take
    /// out. Empty where the gains were not derived (no pupil).
    /// </summary>
    public ImmutableArray<double> Cutoffs { get; init; } = [];

    /// <summary>
    /// Each channel's fitted Wiener low-pass (<see cref="PlanetaryFinish.Wiener"/>, <see cref="PlanetaryFinishing.WienerLowPass"/>): where
    /// it starts and ends falling, in cycles a pixel, a start of 0.5 no cut. Empty where it was not asked.
    /// </summary>
    public ImmutableArray<(double From, double To)> WienerCuts { get; init; } = [];

    /// <summary>
    /// The first channel's derived gains at each of <see cref="PlanetarySharpenOptions.FitStops"/>, in its order: each exactly the gains a
    /// sharpening at that <see cref="PlanetarySharpenOptions.Strength"/> derives. Empty where none were asked or nothing was derived.
    /// </summary>
    public ImmutableArray<GainStop> Stops { get; init; } = [];

    /// <summary>
    /// Every channel's gains at <see cref="PlanetarySharpenOptions.Strength"/>, finest first: <see cref="Gains"/> is the first of them. A
    /// colour master's channels each derive their own, through their own diffraction (#1314).
    /// </summary>
    public ImmutableArray<ImmutableArray<double>> ChannelGains { get; init; } = [];

    /// <summary>
    /// Each channel's band readings where it was shrunk (<see cref="PlanetarySharpenOptions.ShrinkHalves"/>, #1313), finest first, in the
    /// window's units (the disk 1 above its sky); empty where nothing was.
    /// </summary>
    public ImmutableArray<ImmutableArray<BandShrinkReading>> Shrinks { get; init; } = [];
}

/// <summary>
/// A strength of the derived sharpening (<see cref="PlanetarySharpenOptions.Strength"/>) and the a trous gains it derives for each channel,
/// finest first (#1314).
/// </summary>
public readonly record struct GainStop(double Strength, ImmutableArray<ImmutableArray<double>> Channels)
{
    /// <summary>The first channel's gains, the ones the dials show.</summary>
    public ImmutableArray<double> Gains => Channels.IsDefaultOrEmpty ? [] : Channels[0];
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
        if (options.ShrinkHalves is { } given && (!OnGridOf(given.A, master) || !OnGridOf(given.B, master)))
        {
            throw new ArgumentException("The halves must lie on the master's grid, with its channels.", nameof(options));
        }
        if (options.LuminanceOnly && master.ChannelCount == 3)
        {
            return SharpenLuminance(master, options);
        }
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
        // Every channel's gains, at the strength asked and at each stop (#1314): a colour master's channels each derive their own.
        var channelGains = new ImmutableArray<double>[master.ChannelCount];
        var stopGains = new ImmutableArray<double>[options.FitStops.Length, master.ChannelCount];
        // The contrast-adaptive finish reads the STACK's luminance: the mean of every channel's window, each normalised on the disk.
        var contrastFrom = options.Pupil is not null && options.Finish.HasFlag(PlanetaryFinish.Adaptive) ? LuminanceWindow(master, limbWindow, own) : null;
        var cutoffs = new double[options.Pupil is null ? 0 : master.ChannelCount];
        var wienerCuts = new (double From, double To)[options.Pupil is not null && options.Finish.HasFlag(PlanetaryFinish.Wiener) ? master.ChannelCount : 0];
        var shrinks = new ImmutableArray<BandShrinkReading>[options.ShrinkHalves is null ? 0 : master.ChannelCount];
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var plane = master.GetChannelSpan(c);
            var (level, scale) = PlanetaryMetrics.NormalisationLevels(plane, width, height, own);
            var window = limbWindow.Cut(plane, width, height, level, scale);
            if (options.ShrinkHalves is { } halves)
            {
                // The halves cut as the master is, so their difference is the master's noise in the window's own units (#1313).
                var shrink = PlanetaryBandShrink.Shrink(window, limbWindow.Cut(halves.A.GetChannelSpan(c), width, height, level, scale),
                    limbWindow.Cut(halves.B.GetChannelSpan(c), width, height, level, scale), size, size, disk);
                (window, shrinks[c]) = (shrink.Shrunk, shrink.Bands);
            }
            float[] sharpened;
            // A moon beyond the window takes the same gains, in a window of its own (#1211).
            Func<float[], float[]> sharpenMoon;
            if (options.Pupil is { } pupil)
            {
                var wavelengthNm = options.WavelengthsNm[Math.Min(c, options.WavelengthsNm.Length - 1)];
                var diffraction = limbWindow.Diffraction(pupil, wavelengthNm);
                // The model through the pupil is what the edge is read against (the stack's blur is the air's over the telescope's); the
                // target is what the gains restore toward, the same model unless the telescope is to be undone too (#1366).
                var throughPupil = limbWindow.Through(diffraction);
                var target = options.Target is PlanetarySharpenTarget.Aperture
                    ? PlanetaryFinishing.ApertureTarget(pupil, wavelengthNm, limbWindow.ArcsecPerPixel)
                    : diffraction;
                var diskTarget = ReferenceEquals(target, diffraction) ? throughPupil : limbWindow.Through(target);
                (models[c], diffractions[c]) = (diskTarget, target);
                var edge = PlanetaryFinestBand.LimbEdge(window, throughPupil, size, size, disk, fit, aspect);
                if (options.RingEdge && disk.Rings is not null)
                {
                    edge = EdgeProfile.Pooled(edge, PlanetaryFinestBand.RingEdge(window, throughPupil, size, size, disk));
                }
                var physical = options.EdgeReach is { } reach
                    ? PlanetaryFinestBand.FitPhysical(edge, pupil.DiameterM / (wavelengthNm * 1e-9) / ShortExposurePsf.ArcsecPerRadian * limbWindow.ArcsecPerPixel, 0.02, reach)
                    : (PhysicalKernel?)null;
                var kernel = Tabulated(f => Math.Clamp(physical is { } p ? p.TransferAt(f) : edge.TransferAt(f), 0, 1));
                var power = PlanetaryWaveletGains.StackPower(window, size, size, disk);
                var white = PlanetaryInverse.WhiteNoise(PlanetaryWaveletGains.Interior(window, size, size, disk), size, size);
                var noise = ImmutableArray.CreateRange(Enumerable.Repeat(white, power.Length));
                var wiener = ReferenceEquals(target, diffraction)
                    ? PlanetaryWaveletGains.Wiener(power, noise, kernel)
                    : PlanetaryWaveletGains.Wiener(power, noise, f => kernel(f) * diffraction.At(f), target.At);
                var blurredDisk = PlanetaryInverse.Apply(throughPupil, size, size, kernel);
                var finestHeld = FinestHeld(options.OfColour ? 3 : master.ChannelCount, options.OfColour ? 1 : c, options.ColourFinestBand);
                var truth = options.NonNegative
                    ? PlanetaryWaveletGains.FitNonNegative(power, wiener, diskTarget, blurredDisk, size, size, disk, kernel, strength: options.Strength)
                    : PlanetaryWaveletGains.Fit(power, wiener, diskTarget, blurredDisk, size, size, disk, held: finestHeld ? 1 : 0);
                // A strength lifts the mid scales only (#1251): the finest band, mostly noise at 8 bits, keeps the gain the truth gave it (as
                // stacked on a colour master), and the rest are fitted around it to the boosted target. Fitted freely, it rose with them, 1.04
                // to 1.36 of the truth on the mono twins at 1.5.
                ImmutableArray<double> AtStrength(double strength)
                {
                    return strength == 1 || options.NonNegative
                        ? truth
                        : PlanetaryWaveletGains.Fit(power, wiener, diskTarget, blurredDisk, size, size, disk, held: 1, strength: strength,
                            heldAt: finestHeld ? 1 : truth[0]);
                }
                var gains = options.FixedGains.IsDefaultOrEmpty ? AtStrength(options.Strength) : Fixed(options.FixedGains[Math.Min(c, options.FixedGains.Length - 1)], truth.Length);
                channelGains[c] = gains;
                if (!options.NonNegative)
                {
                    // The stops a live view switches between (#1314), each the gains a sharpening at that strength derives.
                    for (var k = 0; k < options.FitStops.Length; k++)
                    {
                        stopGains[k, c] = options.FitStops[k] == options.Strength ? gains : AtStrength(options.FitStops[k]);
                    }
                }
                sharpened = Apply(window, size, disk, sharp, f => kernel(f) * diffraction.At(f), target.At, gains.AsSpan(), [], options.Fix, diskTarget, blurredDisk);
                cutoffs[c] = PlanetaryFinishing.CutoffCyclesPerPixel(pupil, wavelengthNm, limbWindow.ArcsecPerPixel);
                var sharpening = gains;
                (sharpened, var wienerCut) = Finished(window, sharpened, contrastFrom ?? window, size, disk, options, cutoffs[c], white,
                    f => PlanetaryWaveletGains.Transfer(sharpening.AsSpan(), f));
                if (wienerCuts.Length > 0)
                {
                    wienerCuts[c] = wienerCut;
                }
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
                var gains = Strengthened([.. preset.Gains.Select(g => (double)g)], options.Strength).ToArray();
                var thresholds = new double[preset.Gains.Length];
                for (var j = 0; j < gains.Length; j++)
                {
                    // The preset's thresholds are in a master's [0, 1] units; the window's are the disk's.
                    thresholds[j] = j < preset.DenoiseThresholds.Length ? preset.DenoiseThresholds[j] / scale : 0;
                }
                channelGains[c] = [.. gains];
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
        ImmutableArray<GainStop> stops = derived && !options.NonNegative
            ? [.. options.FitStops.Select((s, k) => new GainStop(s, [.. Enumerable.Range(0, master.ChannelCount).Select(c => stopGains[k, c])]))]
            : [];
        return new PlanetarySharpenResult(image, derived, derived ? options.Fix : PlanetaryLimbFix.LimbChannel, firstGains, edgeAt01, edgeAt03)
        {
            ChannelGains = [.. channelGains],
            Limb = options.Pupil is { } kept ? PlanetaryLiveLimb.Kept(master, fit, limbOptions, aspect, limbWindow, kept, options.WavelengthsNm, [.. models], [.. diffractions]) : null,
            Cutoffs = [.. cutoffs],
            WienerCuts = [.. wienerCuts],
            Stops = stops,
            Shrinks = [.. shrinks],
        };
    }

    private static bool OnGridOf(Image half, Image master) =>
        half.Width == master.Width && half.Height == master.Height && half.ChannelCount == master.ChannelCount;

    /// <summary>
    /// The strengths a viewer offers and switches between once derived (#1314, the owner's four stops): the truth, then the posts' range
    /// past it (#1251, the posts sat at about 1.5 to 2.5 of the truth in bands 2 and 3).
    /// </summary>
    public static readonly ImmutableArray<double> StrengthStops = [1, 1.5, 2, 2.5];

    // A colour master sharpened on its luminance alone (PlanetarySharpenOptions.LuminanceOnly, #1295): the mean of its planes sharpened as a
    // mono master is, at the mean of the channels' wavelengths, then every plane rebuilt from it with the stack's own colour. The limb kept
    // for a live view is the luminance's, one channel, so none is kept here.
    private static PlanetarySharpenResult? SharpenLuminance(Image master, PlanetarySharpenOptions options)
    {
        var (width, height) = (master.Width, master.Height);
        var luminance = MeanOfPlanes(master);
        // Halves to shrink by are the luminance's too (#1313).
        var halves = options.ShrinkHalves is { } given ? new PlanetaryStackHalves(MeanOfPlanes(given.A), MeanOfPlanes(given.B)) : null;
        var wavelengthNm = (options.WavelengthsNm[0] + options.WavelengthsNm[Math.Min(1, options.WavelengthsNm.Length - 1)]
            + options.WavelengthsNm[Math.Min(2, options.WavelengthsNm.Length - 1)]) / 3;
        PlanetarySharpenResult? sharpened;
        try
        {
            sharpened = Sharpen(luminance, options with { LuminanceOnly = false, OfColour = true, WavelengthsNm = [wavelengthNm], ShrinkHalves = halves });
        }
        finally
        {
            luminance.Release();
            halves?.A.Release();
            halves?.B.Release();
        }
        if (sharpened is null)
        {
            return null;
        }
        try
        {
            // The disk the luminance was sharpened on, Saturn's rings in it, so each plane's sky lies past them; a plain start without a pupil.
            var disk = sharpened.Limb?.Disk
                ?? (PlanetaryLimbFit.Start(sharpened.Sharpened.GetChannelSpan(0), width, height, 1) is { } start
                    ? new MetricDisk(start.X, start.Y, start.Radius)
                    : new MetricDisk(width / 2.0, height / 2.0, Math.Min(width, height) / 4.0));
            return sharpened with { Sharpened = WithStackColour(master, sharpened.Sharpened, disk), Limb = null };
        }
        finally
        {
            sharpened.Sharpened.Release();
        }
    }

    // The mean of a colour image's three planes, a one-channel image on its grid.
    private static Image MeanOfPlanes(Image image)
    {
        var (width, height) = (image.Width, image.Height);
        var mean = new float[height, width];
        for (var c = 0; c < 3; c++)
        {
            var plane = image.GetChannelSpan(c);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    mean[y, x] += plane[(y * width) + x] / 3;
                }
            }
        }
        return new Image([mean], BitDepth.Float32, image.MaxValue, image.MinValue, image.Pedestal, image.ImageMeta);
    }

    // Every plane of `stack` rebuilt from `luminance`, a sharpened mean of its planes: each pixel above its plane's sky is the luminance above
    // the mean's sky times that plane's share of the stack's mean there, so the stack's colour (the ratios of its planes) is kept at every
    // pixel and every scale of the detail is the luminance's. The shares are taken over the sky plus a floor of a fiftieth of the disk, so
    // where the planet gives no light (the sky, a gap) they fall to the planes' mean share and no noise is divided by nothing.
    internal static Image WithStackColour(Image stack, Image luminance, in MetricDisk disk)
    {
        var (width, height) = (stack.Width, stack.Height);
        var y = luminance.GetChannelSpan(0);
        // Each plane's sky and disk levels, as the sharpening normalises it.
        var levels = new (double Sky, double Disk)[3];
        double meanSky = 0, meanDisk = 0;
        for (var c = 0; c < 3; c++)
        {
            var (level, scale) = PlanetaryMetrics.NormalisationLevels(stack.GetChannelSpan(c), width, height, disk);
            levels[c] = (level, scale);
            (meanSky, meanDisk) = (meanSky + (level / 3), meanDisk + (scale / 3));
        }
        var floor = meanDisk / 50;
        var planes = new float[3][,];
        var r = stack.GetChannelSpan(0);
        var g = stack.GetChannelSpan(1);
        var b = stack.GetChannelSpan(2);
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[height, width];
        }
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (row * width) + x;
                var (ar, ag, ab) = (r[i] - levels[0].Sky, g[i] - levels[1].Sky, b[i] - levels[2].Sky);
                // The mean share of each plane on the disk, which the floor pulls a dark pixel's shares toward.
                var (fr, fg, fb) = (floor * levels[0].Disk / meanDisk, floor * levels[1].Disk / meanDisk, floor * levels[2].Disk / meanDisk);
                var total = ((ar + fr) + (ag + fg) + (ab + fb)) / 3;
                var above = y[i] - meanSky;
                planes[0][row, x] = (float)(levels[0].Sky + (above * (ar + fr) / total));
                planes[1][row, x] = (float)(levels[1].Sky + (above * (ag + fg) / total));
                planes[2][row, x] = (float)(levels[2].Sky + (above * (ab + fb) / total));
            }
        }
        var (max, min) = (float.MinValue, float.MaxValue);
        foreach (var plane in planes)
        {
            foreach (var v in plane)
            {
                (max, min) = (Math.Max(max, v), Math.Min(min, v));
            }
        }
        return new Image(planes, BitDepth.Float32, max, min, stack.Pedestal, stack.ImageMeta);
    }

    // The finishing steps the options ask for, on one channel's window: the adaptive weighting first, Kolivas's reference step next, the
    // fitted Wiener low-pass after them, the low-pass at the cutoff last (it takes out whatever the others raised past the cutoff too).
    // The Wiener low-pass's fitted start and end come back beside it (NaN where it was not asked).
    private static (float[] Window, (double From, double To) WienerCut) Finished(float[] stack, float[] sharpened, float[] contrastFrom, int size,
        in MetricDisk disk, PlanetarySharpenOptions options, double cutoff, double white, Func<double, double> sharpening)
    {
        var (finished, wienerCut) = (sharpened, (double.NaN, double.NaN));
        if (options.Finish.HasFlag(PlanetaryFinish.Adaptive))
        {
            finished = PlanetaryFinishing.ContrastWeighted(stack, finished, contrastFrom, size, disk);
        }
        if (options.Finish.HasFlag(PlanetaryFinish.Kolivas))
        {
            finished = PlanetaryFinishing.KolivasStep(finished, size, options.KolivasAmount);
        }
        if (options.Finish.HasFlag(PlanetaryFinish.Wiener))
        {
            (finished, var from, var to) = PlanetaryFinishing.WienerLowPass(finished, size, disk, white, sharpening);
            wienerCut = (from, to);
        }
        if (options.Finish.HasFlag(PlanetaryFinish.Cutoff))
        {
            finished = PlanetaryFinishing.LowPassAtCutoff(finished, size, cutoff);
        }
        return (finished, wienerCut);
    }

    // Every channel's window, each normalised on the disk, averaged: the stack's luminance as the window sees it.
    private static float[] LuminanceWindow(Image master, PlanetaryLimbWindow limbWindow, MetricDisk own)
    {
        float[]? sum = null;
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var plane = master.GetChannelSpan(c);
            var (level, scale) = PlanetaryMetrics.NormalisationLevels(plane, master.Width, master.Height, own);
            var window = limbWindow.Cut(plane, master.Width, master.Height, level, scale);
            sum ??= new float[window.Length];
            for (var i = 0; i < window.Length; i++)
            {
                sum[i] += window[i] / master.ChannelCount;
            }
        }
        return sum ?? [];
    }

    /// <summary>The bands a strength takes past the truth, finest first from 0: bands 2 and 3, where a post holds its extra detail (#1251).</summary>
    public static readonly Range StrengthBands = 1..3;

    /// <summary>
    /// <paramref name="gains"/> with bands 2 and 3 (<see cref="StrengthBands"/>) multiplied by <paramref name="strength"/>: a strength on a
    /// preset, which has no truth to be past (<see cref="PlanetarySharpenOptions.Strength"/>; a derived sharpening is fitted to it instead).
    /// The gains themselves at a strength of one.
    /// </summary>
    public static ImmutableArray<double> Strengthened(ImmutableArray<double> gains, double strength)
    {
        if (strength == 1 || gains.IsDefaultOrEmpty)
        {
            return gains;
        }
        var (offset, length) = StrengthBands.GetOffsetAndLength(int.MaxValue);
        var builder = gains.ToBuilder();
        for (var b = offset; b < Math.Min(offset + length, builder.Count); b++)
        {
            builder[b] *= strength;
        }
        return builder.MoveToImmutable();
    }

    // A fixed gain set as many layers long as the derived ones, the layers past its end held at 1.
    private static ImmutableArray<double> Fixed(ImmutableArray<double> given, int layers)
    {
        var gains = ImmutableArray.CreateBuilder<double>(layers);
        for (var i = 0; i < layers; i++)
        {
            gains.Add(i < given.Length ? given[i] : 1);
        }
        return gains.MoveToImmutable();
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
