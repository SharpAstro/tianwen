using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// AUTO (#817; A4, #1391): a recorded capture's best stack with nothing asked. The capture is identified
/// (<see cref="PlanetaryIdentification"/>: its planet, its filter, its telescope) and stacked and sharpened by
/// <see cref="PlanetaryBestStack"/> at the measured defaults, since A2 (#1389) found no per-capture tune that carries; the master says
/// what it was identified as. <c>planetary stack --auto</c> and the viewer's Auto view both run it and write it by
/// <see cref="Write"/>, so the two masters are the same bits.
/// </summary>
public static class PlanetaryAuto
{
    /// <summary>The best stack's options AUTO runs with, nothing chosen: <see cref="OptionsFor(PlanetaryIdentity, PlanetaryAutoChoices)"/> at the defaults.</summary>
    public static PlanetaryBestStackOptions OptionsFor(PlanetaryIdentity identity) => OptionsFor(identity, PlanetaryAutoChoices.Defaults);

    /// <summary>
    /// The best stack's options AUTO runs with: the measured defaults, the identity's planet, telescope and filter, the identity written
    /// into the master (<see cref="PlanetaryStackOptions.Optics"/>), and every strength stop fitted, which a viewer switches between without
    /// deriving again (the masters are the same either way). <paramref name="choices"/> are the three things a person may choose of the
    /// result (<see cref="PlanetaryAutoChoices"/>); none of them changes how the capture is stacked.
    /// </summary>
    public static PlanetaryBestStackOptions OptionsFor(PlanetaryIdentity identity, PlanetaryAutoChoices choices)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(choices);
        return new PlanetaryBestStackOptions(identity.Planet, identity.Telescope)
        {
            WavelengthsNm = identity.WavelengthsNm,
            FitStops = PlanetarySharpening.StrengthStops,
            Strength = choices.Strength,
            LuminanceOnly = choices.LuminanceOnly,
            ColourSaturation = choices.ColourSaturation,
            Stack = new PlanetaryStackOptions
            {
                Optics = new PlanetaryMasterOptics(identity.TelescopeName ?? "", identity.ApertureMm,
                    identity.Layout == PlanetaryFrameLayout.Mono ? identity.Filter : null, identity.Camera),
            },
        };
    }

    /// <summary>The best stack of <paramref name="stream"/> as AUTO makes it of a capture identified as <paramref name="identity"/>; the caller owns the result's images.</summary>
    public static Task<PlanetaryBestStackResult> RunAsync(IPlanetaryFrameStream stream, PlanetaryIdentity identity, IProgress<PlanetaryStackProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => PlanetaryBestStack.RunAsync(stream, OptionsFor(identity), progress, cancellationToken);

    /// <summary>As <see cref="RunAsync(IPlanetaryFrameStream, PlanetaryIdentity, IProgress{PlanetaryStackProgress}?, CancellationToken)"/>, with what a person chose of the result.</summary>
    public static Task<PlanetaryBestStackResult> RunAsync(IPlanetaryFrameStream stream, PlanetaryIdentity identity, PlanetaryAutoChoices choices,
        IProgress<PlanetaryStackProgress>? progress = null, CancellationToken cancellationToken = default)
        => PlanetaryBestStack.RunAsync(stream, OptionsFor(identity, choices), progress, cancellationToken);

    /// <summary>
    /// A written master sharpened again as AUTO sharpens the master it has just stacked: <see cref="PlanetaryBestStack.Sharpen"/> with what
    /// <see cref="OptionsFor(PlanetaryIdentity, PlanetaryAutoChoices)"/> gives for <paramref name="identity"/>, so the two cannot part. The instant is the master's own (its DATE-OBS
    /// and EXPTIME's middle). A colour master is sharpened as it was written, its colour balance already in it, where a stack sharpens
    /// before it balances. The words say how it was sharpened, and why where the gains could not be derived. The caller owns the image.
    /// Of <paramref name="choices"/> the strength and the luminance-only sharpening are taken; a written master is not balanced again.
    /// </summary>
    public static (Image Sharpened, string How) Sharpen(Image master, PlanetaryIdentity identity, PlanetaryAutoChoices? choices = null)
    {
        ArgumentNullException.ThrowIfNull(master);
        var options = OptionsFor(identity, choices ?? PlanetaryAutoChoices.Defaults);
        return PlanetaryBestStack.Sharpen(master, options.Planet, epoch: null, options.Telescope, options.WavelengthsNm, options.Fix, options.Strength,
            options.LuminanceOnly, options.Target);
    }

    /// <summary>
    /// Where AUTO's masters of a capture named <paramref name="baseName"/> (its file's name, without the extension) go in
    /// <paramref name="outputDir"/>: <c>master_&lt;capture&gt;_auto.fits</c> and its <c>_sharpened</c>, beside the masters a Best stack or a plain
    /// <c>planetary stack</c> writes, never over them.
    /// </summary>
    public static (string Master, string Sharpened) OutputPaths(string outputDir, string baseName)
        => PlanetaryBestStack.OutputPaths(outputDir, baseName + "_auto");

    /// <summary>Both of <paramref name="result"/>'s masters written to <paramref name="paths"/>, with their colour balance's cards: the one writer the CLI and the viewer share.</summary>
    public static void Write(PlanetaryBestStackResult result, (string Master, string Sharpened) paths)
    {
        ArgumentNullException.ThrowIfNull(result);
        var cards = result.Balance?.HeaderCards();
        result.Stack.Master.WriteToFitsFile(paths.Master, null, cards);
        result.Sharpened.WriteToFitsFile(paths.Sharpened, null, cards);
    }
}

/// <summary>
/// What a person may choose of AUTO's result (#2468, the owner's call of 2026-11-23). None of them tunes the stack to its capture, which
/// A2 (#1389) found not to carry: each is a matter of taste about the finished master, at the measured default when nothing is said. The
/// viewer's Auto view has set the strength and the saturation from its panel since #1880.
/// </summary>
public sealed record PlanetaryAutoChoices
{
    /// <summary>Nothing chosen: the measured defaults.</summary>
    public static PlanetaryAutoChoices Defaults { get; } = new PlanetaryAutoChoices();

    /// <summary>How far past the truth the derived sharpening takes bands 2 and 3 (<see cref="PlanetarySharpenOptions.Strength"/>, #1251): 1,
    /// the default, is the derived sharpening.</summary>
    public double Strength { get; init; } = 1;

    /// <summary>Sharpen a colour master's luminance once and keep the stack's own colour, in place of each channel through its own
    /// diffraction (<see cref="PlanetarySharpenOptions.LuminanceOnly"/>, #1295).</summary>
    public bool LuminanceOnly { get; init; }

    /// <summary>The saturation a colour master is balanced to the planet's own colour at (<see cref="PlanetaryColourBalance.DefaultSaturation"/>),
    /// or null to leave its colours as captured.</summary>
    public double? ColourSaturation { get; init; } = PlanetaryColourBalance.DefaultSaturation;
}
