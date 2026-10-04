using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What the best stack of a recorded capture is made with: the planet (its de-rotation and the derived sharpening's limb fit), the
/// telescope's pupil (the derived sharpening's diffraction), and, left at their defaults, the measured best of everything else.
/// </summary>
/// <param name="Planet">The planet, from the capture's name (<see cref="PlanetaryCaptureName"/>) or the user; null when neither says.</param>
/// <param name="Telescope">The telescope's pupil (<see cref="PlanetaryBestStack.PupilFor"/>); null sharpens by the preset with the limb kept.</param>
public sealed record PlanetaryBestStackOptions(CatalogIndex? Planet, Pupil? Telescope)
{
    /// <summary>The batch stack's options; its de-rotation is set from <see cref="Planet"/> unless one is given here.</summary>
    public PlanetaryStackOptions Stack { get; init; } = new PlanetaryStackOptions();

    /// <summary>Each channel's effective wavelength, nm; empty for 550 on a mono capture and 610, 530, 460 on a colour one.</summary>
    public ImmutableArray<double> WavelengthsNm { get; init; } = [];

    /// <summary>The limb fix the derived sharpening applies; null for <see cref="PlanetarySharpenOptions.Fix"/>'s default.</summary>
    public PlanetaryLimbFix? Fix { get; init; }

    /// <summary>
    /// The saturation a colour master of Jupiter or Saturn is balanced at (<see cref="PlanetaryColourBalance"/>, #1212, #1235); null leaves its colours
    /// as the camera recorded them.
    /// </summary>
    public double? ColourSaturation { get; init; } = PlanetaryColourBalance.DefaultSaturation;
}

/// <summary>The best stack of a capture: the stack as integrated (linear) and as sharpened, and how it was sharpened, in words.</summary>
public sealed record PlanetaryBestStackResult(PlanetaryStackResult Stack, Image Sharpened, string HowSharpened)
{
    /// <summary>The colour balance both masters were given (<see cref="PlanetaryColourBalance"/>), null when none was.</summary>
    public ColourBalance? Balance { get; init; }

    /// <summary>The colour balance in words, or why there was none.</summary>
    public string HowBalanced { get; init; } = "";
}

/// <summary>
/// The enhanced pipeline's batch stack and sharpening as ONE routine (#1159, docs/plans/planetary-restoration.md, "The enhanced
/// pipeline"): <c>tianwen planetary-stack</c> and the GUI's "Best stack" of a recorded capture both run it, so the two cannot drift.
/// The alignment-point stack at the measured defaults (<see cref="PlanetaryStackOptions"/>), de-rotated once the planet's turn moves
/// its disk's middle a pixel, then sharpened by gains derived through the limb's edge (<see cref="PlanetarySharpening"/>) when the
/// planet, the capture's time and the telescope are known, else by the preset.
/// </summary>
public static class PlanetaryBestStack
{
    /// <summary>A turn smaller than this, the disk's middle moved over the run, is not worth de-rotating unasked.</summary>
    public const double TurnWorthDerotatingPx = 1;

    /// <summary>
    /// The de-rotation a capture of <paramref name="planet"/> gets: every run when <paramref name="always"/>, else once its turn moves
    /// the disk's middle <see cref="TurnWorthDerotatingPx"/>; null for a planet with no rotation model (or none named). Saturn's globe
    /// turns under rings that stay where they lie (S5, #1234).
    /// </summary>
    public static PlanetaryDerotationOptions? DerotationFor(CatalogIndex? planet, bool always = false)
        => planet is { } turning && PhysicalEphemeris.Supports(turning)
            ? new PlanetaryDerotationOptions(turning) { MinimumTurnPx = always ? 0 : TurnWorthDerotatingPx }
            : null;

    /// <summary>
    /// A telescope's pupil from what a profile says of it: the aperture, and the central obstruction its design usually has
    /// (the owner's choice of 2026-10-02: a Newtonian 0.25 with its four-vane spider, a Schmidt or Maksutov Cassegrain 0.33, a
    /// Newtonian-Cassegrain 0.3, a RASA 0.4, a refractor, an astrograph or an unknown design none). Null with no aperture.
    /// </summary>
    public static Pupil? PupilFor(int? apertureMm, OpticalDesign design)
    {
        if (apertureMm is not { } mm || mm <= 0)
        {
            return null;
        }
        var diameter = mm / 1000.0;
        return design switch
        {
            OpticalDesign.Newtonian => new Pupil(diameter, ObstructionRatio: 0.25, Vanes: 4, VaneWidthM: 0.001),
            OpticalDesign.NewtonianCassegrain => new Pupil(diameter, ObstructionRatio: 0.3),
            OpticalDesign.SCT or OpticalDesign.Cassegrain => new Pupil(diameter, ObstructionRatio: 0.33),
            OpticalDesign.RASA => new Pupil(diameter, ObstructionRatio: 0.4),
            _ => new Pupil(diameter),
        };
    }

    /// <summary>
    /// The pupil a capture's header names (#1179): a TianWen recording writes its OTA's aperture and design in the SER header's
    /// Telescope field (<see cref="PlanetaryCaptureName.TelescopeField"/>); null where the field names no aperture.
    /// </summary>
    public static Pupil? PupilOf(string? telescopeField)
    {
        var (apertureMm, design) = PlanetaryCaptureName.Telescope(telescopeField);
        return PupilFor(apertureMm, design);
    }

    /// <summary>
    /// The stack of <paramref name="stream"/> by <paramref name="options"/>, sharpened, with its progress (0 to 1, read off the frames
    /// it has loaded against those it will) reported to <paramref name="progress"/>. The caller owns both images of the result.
    /// </summary>
    public static async Task<PlanetaryBestStackResult> RunAsync(IPlanetaryFrameStream stream, PlanetaryBestStackOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var stackOptions = options.Stack.Derotation is null ? options.Stack with { Derotation = DerotationFor(options.Planet) } : options.Stack;
        stackOptions = stackOptions with { Planet = stackOptions.Planet ?? options.Planet };
        using var counted = new ProgressFrameStream(stream, ExpectedLoads(stream.FrameCount, stackOptions), progress);
        var result = await new LuckyImagingStacker().StackAsync(counted, stackOptions, cancellationToken).ConfigureAwait(false);
        // The master is handed on with the result; only a sharpening that throws leaves it here to release.
        var handedOn = false;
        try
        {
            var (sharpened, how) = Sharpen(result.Master, options.Planet, result.Epoch, options.Telescope, options.WavelengthsNm, options.Fix);
            // The balance comes after the sharpening, which reads each channel's edge through that channel's own diffraction: the
            // saturation mixes the channels.
            var (balance, howBalanced) = options.ColourSaturation is { } saturation
                ? PlanetaryColourBalance.For(result.Master, options.Planet, result.Epoch, saturation)
                : (null, "colours left as captured");
            if (balance is not null)
            {
                var (balancedMaster, balancedSharpened) = (balance.Apply(result.Master), balance.Apply(sharpened));
                result.Master.Release();
                sharpened.Release();
                (result, sharpened) = (result with { Master = balancedMaster }, balancedSharpened);
            }
            handedOn = true;
            progress?.Report(1);
            return new PlanetaryBestStackResult(result, sharpened, how) { Balance = balance, HowBalanced = howBalanced };
        }
        finally
        {
            if (!handedOn)
            {
                result.Master.Release();
            }
        }
    }

    /// <summary>
    /// <paramref name="master"/> sharpened as the pipeline sharpens it: by gains derived through the limb's edge when the planet (one
    /// with a rotation model: Jupiter, or Saturn read around its rings since S4, #1184), the instant it shows (<paramref name="epoch"/>,
    /// else its own DATE-OBS and EXPTIME's middle) and the telescope are known and its limb fits; by
    /// <see cref="WaveletSharpenOptions.PlanetaryDefault"/> with the limb kept as stacked when only the telescope is missing; by the preset
    /// alone when the planet or the time is unknown or the limb does not fit. The words say which, and why. The caller owns the image.
    /// </summary>
    public static (Image Sharpened, string How) Sharpen(Image master, CatalogIndex? planet, DateTimeOffset? epoch, Pupil? telescope,
        ImmutableArray<double> wavelengthsNm = default, PlanetaryLimbFix? fix = null)
    {
        if (SharpenOptionsFor(master, planet, epoch, telescope, wavelengthsNm) is not { } options)
        {
            return (WaveletSharpen.Sharpen(master, WaveletSharpenOptions.PlanetaryDefault),
                "PlanetaryDefault: the sharpening is derived only for a named Jupiter or Saturn with frame times");
        }
        if (fix is { } chosen)
        {
            options = options with { Fix = chosen };
        }
        if (PlanetarySharpening.Sharpen(master, options) is not { } result)
        {
            return (WaveletSharpen.Sharpen(master, WaveletSharpenOptions.PlanetaryDefault),
                "PlanetaryDefault: the planet's limb could not be fitted, so the sharpening cannot be derived");
        }
        var inv = CultureInfo.InvariantCulture;
        return (result.Sharpened, result.Derived
            ? string.Create(inv, $"gains {string.Join(", ", result.Gains.Select(g => g.ToString("0.00", inv)))} derived through the limb's edge, {Describe(result.Fix)}")
            : "PlanetaryDefault with the limb kept as stacked; the telescope's aperture gives the derived sharpening");
    }

    /// <summary>
    /// The gains <see cref="Sharpen"/> would derive for <paramref name="master"/>, finest scale first, for a live view's wavelet sliders
    /// to sharpen every later master with (<see cref="SliderOptions"/>), and the limb it drew, which those masters are drawn outside of as
    /// the batch draws it (<see cref="PlanetaryLiveLimb"/>, #1201): the derivation is the slow part (about 35 s), the gains and the limb are
    /// cheap to apply. Empty, with no limb and the reason in words, where nothing is derived: no telescope, no planet with a rotation
    /// model, no time, or a limb that does not fit. A colour master's gains are its first channel's.
    /// </summary>
    public static (ImmutableArray<float> Gains, string How, PlanetaryLiveLimb? Limb) DeriveGains(Image master, CatalogIndex? planet, DateTimeOffset? epoch,
        Pupil? telescope, ImmutableArray<double> wavelengthsNm = default)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (telescope is not { } pupil)
        {
            return ([], "the gains are derived only for a telescope: give its aperture", null);
        }
        // The gains and the limb do not depend on the limb fix, which only the batch sharpening applies; floored is the cheapest to make.
        if (SharpenOptionsFor(master, planet, epoch, telescope, wavelengthsNm) is not { } options)
        {
            return ([], "the gains are derived only for a named Jupiter or Saturn with frame times", null);
        }
        if (PlanetarySharpening.Sharpen(master, options with { Fix = PlanetaryLimbFix.Floored }) is not { } result)
        {
            return ([], "the planet's limb could not be fitted", null);
        }
        try
        {
            var inv = CultureInfo.InvariantCulture;
            return result.Derived && !result.Gains.IsDefaultOrEmpty
                ? ([.. result.Gains.Select(g => (float)g)], string.Create(inv,
                    $"derived for {options.Planet} through a {pupil.DiameterM * 1000:0} mm pupil at {options.WavelengthsNm[0]:0} nm"), result.Limb)
                : ([], "the gains could not be derived", null);
        }
        finally
        {
            result.Sharpened.Release();
        }
    }

    /// <summary>
    /// A live view's wavelet sliders set to derived <paramref name="gains"/>: the same a trous gains as the derived sharpening, no
    /// denoise (the derivation weighed the noise already), each channel held at its darkest level. Outside the limb a master is then drawn
    /// by the limb the derivation kept (<see cref="PlanetaryLiveLimb.Draw"/>, #1201), which holds the inside at the sky. One builder, so the
    /// GUI's sliders and <c>planetary-sharpen --sliders</c>, which measures them, sharpen alike.
    /// </summary>
    public static WaveletSharpenOptions SliderOptions(ImmutableArray<float> gains)
        => new WaveletSharpenOptions { Gains = gains, HoldAtDarkest = true };

    /// <summary>
    /// The instant <paramref name="master"/> shows its planet at: the run's <paramref name="epoch"/> when it was de-rotated to one, else
    /// the middle of its own DATE-OBS and EXPTIME, else null.
    /// </summary>
    public static DateTimeOffset? InstantOf(Image master, DateTimeOffset? epoch)
    {
        var meta = master.ImageMeta;
        return epoch ?? (meta.ExposureStartTime.Year > 1 ? meta.ExposureStartTime + (meta.ExposureDuration / 2) : null);
    }

    // The derived sharpening's options for a master, or null where it cannot be derived: no planet with a rotation model, or no
    // instant (InstantOf). Its wavelengths default to 550 nm on a mono master and 610, 530, 460 on a colour one.
    private static PlanetarySharpenOptions? SharpenOptionsFor(Image master, CatalogIndex? planet, DateTimeOffset? epoch, Pupil? telescope,
        ImmutableArray<double> wavelengthsNm)
    {
        if (planet is not { } body || !PhysicalEphemeris.Supports(body) || InstantOf(master, epoch) is not { } instant)
        {
            return null;
        }
        ImmutableArray<double> wavelengths = !wavelengthsNm.IsDefaultOrEmpty ? wavelengthsNm : master.ChannelCount == 3 ? [610, 530, 460] : [550];
        return new PlanetarySharpenOptions(body, instant, telescope) { WavelengthsNm = wavelengths };
    }

    /// <summary>
    /// Where a best stack of <paramref name="baseName"/> is written in <paramref name="outputDir"/>: the linear master and the sharpened
    /// one, under the names <c>planetary-stack</c> gives them (<paramref name="prefix"/> its <c>--label</c>).
    /// </summary>
    public static (string Master, string Sharpened) OutputPaths(string outputDir, string baseName, string prefix = "")
        => (System.IO.Path.Combine(outputDir, $"{prefix}master_{baseName}.fits"), System.IO.Path.Combine(outputDir, $"{prefix}master_{baseName}_sharpened.fits"));

    /// <summary>A limb fix in words.</summary>
    public static string Describe(PlanetaryLimbFix fix) => fix switch
    {
        PlanetaryLimbFix.LimbChannel => "the limb as its own channel",
        PlanetaryLimbFix.Floored => "floored at the sky",
        PlanetaryLimbFix.Feathered => "feathered at the limb",
        PlanetaryLimbFix.Bounded => "bounded by the stack outside the limb but for its moons",
        PlanetaryLimbFix.HeldOutside => "the stack as it is outside the limb but for its moons",
        PlanetaryLimbFix.ModelFloor => "bounded outside the limb, at or above the glow the planet's model keeps",
        PlanetaryLimbFix.Blended => "bounded at the limb, blended to the stack by 1.1 radii",
        PlanetaryLimbFix.ModelGlow => "outside the limb the glow the planet's model keeps, none of the sharpening",
        PlanetaryLimbFix.ModelOutside => "outside the limb the planet's model through the pupil",
        PlanetaryLimbFix.GlowSwapped => "outside the limb the stack with the seeing glow swapped for the pupil's",
        PlanetaryLimbFix.ModelFeathered => "outside the limb the planet's model through the pupil, feathered to the stack far out",
        _ => "plain",
    };

    // The frames the alignment-point stack loads: every frame graded, the reference's best frames stacked, the kept frames folded.
    // A de-rotated run loads more for its limb fits; the progress simply holds near the end there.
    private static long ExpectedLoads(int frames, PlanetaryStackOptions options)
        => frames + Math.Min(options.ReferenceFrames, frames) + (long)Math.Ceiling(frames * options.KeepFraction);

    // The stream the stack reads, counting its loads into a progress.
    private sealed class ProgressFrameStream(IPlanetaryFrameStream inner, long expected, IProgress<double>? progress) : IPlanetaryFrameStream
    {
        private long _loads;
        private int _lastPercent = -1;

        public int FrameCount => inner.FrameCount;
        public int Width => inner.Width;
        public int Height => inner.Height;
        public PlanetaryFrameLayout Layout => inner.Layout;
        public bool HasTimestamps => inner.HasTimestamps;
        public DateTimeOffset? TimestampOf(int index) => inner.TimestampOf(index);

        public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
        {
            var loads = Interlocked.Increment(ref _loads);
            if (progress is { } report)
            {
                // A percent at a time, and never the end: that is reported once the sharpening is done.
                var percent = (int)Math.Min(99, loads * 100 / Math.Max(1, expected));
                if (Interlocked.Exchange(ref _lastPercent, percent) != percent)
                {
                    report.Report(percent / 100.0);
                }
            }
            return inner.LoadAsync(index, cancellationToken);
        }

        // The stream belongs to the caller.
        public void Dispose()
        {
        }
    }
}
