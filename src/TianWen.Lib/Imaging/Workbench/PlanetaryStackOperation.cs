using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Lib.Imaging.Workbench;

// Every settings property is settable and carries its default (see OperationSettings.cs).

/// <summary>
/// What a project says a planetary capture or master is, in place of what it is read to be: the settings an AUTO step
/// takes, and all of them. AUTO's stack and its sharpening are the measured defaults.
/// </summary>
public abstract record PlanetaryIdentitySettings
{
    /// <summary>The planet, by its name (<c>--planet</c>: jupiter, saturn, mars); left out, read off the capture or
    /// the master.</summary>
    public string? Planet { get; set; }

    /// <summary>A mono capture's filter, as its wavelength in nanometres (<c>--wavelength</c>); left out, read off the
    /// capture or the master.</summary>
    public double? FilterNm { get; set; }

    /// <summary>The telescope's aperture in millimetres (<c>--aperture-mm</c>); left out, read off the capture or the
    /// master.</summary>
    public int? ApertureMm { get; set; }

    /// <summary>The design of the telescope <see cref="ApertureMm"/> gives (<c>--design</c>), whose usual central
    /// obstruction the sharpening takes.</summary>
    public OpticalDesign Design { get; set; } = OpticalDesign.Unknown;

    /// <summary>Why <paramref name="settings"/> do not say a capture or a master, in words; null when they do.</summary>
    public static string? RefusalOf(PlanetaryIdentitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Planet is { } planet && PlanetaryCaptureName.Named(planet) is null)
        {
            return $"planet '{planet}' is not a planet's name: jupiter, saturn, mars or another by its name";
        }

        if (settings.FilterNm is { } nm && !PlanetaryIdentityGiven.IsWavelengthNm(nm))
        {
            return $"filterNm is a wavelength in nanometres, more than {PlanetaryIdentityGiven.WavelengthAboveNm:0} and less than {PlanetaryIdentityGiven.WavelengthBelowNm:0}";
        }

        if (settings.ApertureMm is <= 0)
        {
            return "apertureMm is millimetres, more than 0";
        }

        return settings.ApertureMm is null && settings.Design is not OpticalDesign.Unknown
            ? "design is the design of the telescope apertureMm gives: give its aperture too"
            : null;
    }

    /// <summary>What <paramref name="settings"/> say, as the identification takes it.</summary>
    public static PlanetaryIdentityGiven GivenOf(PlanetaryIdentitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.Planet is { } planet ? PlanetaryCaptureName.Named(planet) : null, settings.FilterNm, settings.ApertureMm, settings.Design);
    }

    /// <summary>The options that say <paramref name="settings"/> on a step's command line, which the AUTO verbs share.</summary>
    public static void AddTo(ImmutableArray<string>.Builder line, PlanetaryIdentitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Planet is { } planet)
        {
            line.AddRange("--planet", planet);
        }

        if (settings.FilterNm is { } nm)
        {
            line.AddRange("--wavelength", StepCommandLine.Number(nm));
        }

        if (settings.ApertureMm is { } mm)
        {
            line.AddRange("--aperture-mm", StepCommandLine.Number(mm));
            if (settings.Design is not OpticalDesign.Unknown)
            {
                line.AddRange("--design", settings.Design.ToString().ToLowerInvariant());
            }
        }
    }
}

/// <summary>
/// The settings of <see cref="PlanetaryStackOperation"/>: what is said of the capture, in place of what it is read to
/// be, and AUTO's three choices about the finished masters (<see cref="PlanetaryAutoChoices"/>). The stack itself is
/// the measured defaults, and takes no setting.
/// </summary>
/// <remarks>A changed setting here stacks the capture again. A strength is tried on the master by
/// <see cref="PlanetarySharpenOperation"/>, which only sharpens.</remarks>
public sealed record PlanetaryStackSettings : PlanetaryIdentitySettings
{
    /// <summary>How far past the truth the derived sharpening takes bands 2 and 3 (<c>--strength</c>): 1 is the
    /// derived sharpening.</summary>
    public double Strength { get; set; } = 1;

    /// <summary>Sharpen a colour master's luminance once and keep the stack's own colour
    /// (<c>--sharpen-luminance</c>).</summary>
    public bool LuminanceOnly { get; set; }

    /// <summary>Balance a colour master to the planet's own colour (off: <c>--no-colour-balance</c>).</summary>
    public bool ColourBalance { get; set; } = true;

    /// <summary>The saturation the colour balance is made at (<c>--colour-saturation</c>).</summary>
    public double ColourSaturation { get; set; } = PlanetaryColourBalance.DefaultSaturation;
}

/// <summary>
/// AUTO (<c>tianwen planetary stack --auto</c>, <see cref="PlanetaryAuto"/>): a recorded capture identified, and
/// stacked and sharpened at the measured defaults. Its own image is the capture (a SER file); its result is the linear
/// master, and the sharpened master is its extra result.
/// </summary>
/// <remarks>
/// <para>It runs the three routines the verb and the viewer's Auto view run, in their order, and nothing between:
/// <see cref="PlanetaryIdentification.IdentifyAsync"/>, <c>PlanetaryAuto.RunAsync</c> and
/// <see cref="PlanetaryAuto.Write"/>.</para>
/// <para><b>A step never asks what telescope a camera was last said to be on</b>
/// (<see cref="PlanetaryTelescopeMemory"/>): that is this computer's memory, and a step's result follows from its
/// capture and its settings. The verb's <c>--no-telescope-memory</c> is the same, and a step's command line says it.</para>
/// <para><b>What the capture says of itself is more than its bytes.</b> A planet, a filter and a telescope that the
/// settings do not state are read off the capture's header, its capture program's settings file beside it, and its
/// file's and folders' names. The step's key holds the capture's bytes and the settings, so a capture moved to a folder
/// named for another telescope keeps its key: state in the settings what the result must not depend on.</para>
/// </remarks>
public sealed class PlanetaryStackOperation : MultiResultOperation<PlanetaryStackSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "planetary-stack";

    /// <summary>The name of the extra result that is the sharpened master.</summary>
    public const string Sharpened = "sharpened";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Planetary stack", Verb: "planetary stack --auto", OperationOutputKind.Stored, ExtraInputs: [],
        Settings:
        [
            new OperationSetting("planet", "The planet, by its name; left out, read off the capture."),
            new OperationSetting("filterNm", "A mono capture's filter, as its wavelength in nanometres; left out, read off the capture."),
            new OperationSetting("apertureMm", "The telescope's aperture in millimetres; left out, read off the capture."),
            new OperationSetting("design", "The design of the telescope apertureMm gives, whose usual central obstruction the sharpening takes."),
            new OperationSetting("strength", "How far past the truth the derived sharpening takes bands 2 and 3; 1 is the derived sharpening."),
            new OperationSetting("luminanceOnly", "Sharpen a colour master's luminance once and keep the stack's own colour."),
            new OperationSetting("colourBalance", "Balance a colour master to the planet's own colour."),
            new OperationSetting("colourSaturation", "The saturation the colour balance is made at."),
        ])
    { Summary = "Stacks the best frames of a planetary video into one sharp image.",
        ExtraResults = [new OperationResultRole(Sharpened, "The master sharpened by the gains derived through its limb.")],
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<PlanetaryStackSettings> SettingsInfo => OperationSettingsJsonContext.Default.PlanetaryStackSettings;

    /// <inheritdoc/>
    public override string? Refusal(PlanetaryStackSettings settings)
    {
        if ((PlanetaryIdentitySettings.RefusalOf(settings) ?? PlanetarySharpenOperation.StrengthRefusal(settings.Strength)) is { } refusal)
        {
            return refusal;
        }

        if (!(settings.ColourSaturation >= 0 && double.IsFinite(settings.ColourSaturation)))
        {
            return "colourSaturation is 0 or more";
        }

        // A saturation with no balance would be a setting that changes a step's key and none of its pixels.
        return !settings.ColourBalance && settings.ColourSaturation != PlanetaryColourBalance.DefaultSaturation
            ? "colourSaturation is the colour balance's: with colourBalance off there is nothing it saturates"
            : null;
    }

    /// <summary>What the settings say of the capture, as the identification takes it.</summary>
    public static PlanetaryIdentityGiven GivenOf(PlanetaryStackSettings settings) => PlanetaryIdentitySettings.GivenOf(settings);

    /// <summary>What the settings choose of AUTO's result.</summary>
    public static PlanetaryAutoChoices ChoicesOf(PlanetaryStackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PlanetaryAutoChoices
        {
            Strength = settings.Strength,
            LuminanceOnly = settings.LuminanceOnly,
            ColourSaturation = settings.ColourBalance ? settings.ColourSaturation : null,
        };
    }

    /// <summary>Where the two masters go: the linear one as the main result, the sharpened one as the extra one.</summary>
    public static StepOutputs Outputs(string master, string sharpened)
        => new(master) { Extra = ImmutableDictionary<string, string>.Empty.Add(Sharpened, sharpened) };

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(PlanetaryStackSettings settings, IReadOnlyList<string> inputs, StepOutputs outputs)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.AddRange(StepCommandLine.Program, "planetary", "stack", inputs[0], "--auto", "--no-telescope-memory", "--no-png", "--master", outputs.Path);
        if (outputs.Extra.TryGetValue(Sharpened, out var sharpened))
        {
            line.AddRange("--sharpened", sharpened);
        }

        // A choice is on the line wherever it is not the verb's own default, so the line replays what the step ran.
        if (settings.Strength != 1)
        {
            line.AddRange("--strength", StepCommandLine.Number(settings.Strength));
        }

        if (settings.LuminanceOnly)
        {
            line.Add("--sharpen-luminance");
        }

        if (!settings.ColourBalance)
        {
            line.Add("--no-colour-balance");
        }
        else if (settings.ColourSaturation != PlanetaryColourBalance.DefaultSaturation)
        {
            line.AddRange("--colour-saturation", StepCommandLine.Number(settings.ColourSaturation));
        }

        PlanetaryIdentitySettings.AddTo(line, settings);
        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(PlanetaryStackSettings settings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        if (inputs[0].Path is not { } capture)
        {
            return OperationOutcome.BadInput($"'{Id}' takes a capture file, and {inputs[0].Name} has none");
        }

        if (!outputs.Extra.TryGetValue(Sharpened, out var sharpened))
        {
            return OperationOutcome.BadInput($"'{Id}' writes its master and the sharpened one, and was given a place for the master alone");
        }

        PlanetaryBestStackResult result;
        try
        {
            // Never the telescope a camera was last said to be on: a step follows from its capture and its settings.
            var identity = await PlanetaryIdentification.IdentifyAsync(capture, GivenOf(settings), external: null, cancellationToken);
            say?.Invoke($"AUTO: {identity.Describe()}");
            using var stream = SerFrameStream.Open(capture);
            say?.Invoke($"{stream.FrameCount} frames, {stream.Width}x{stream.Height}, layout {stream.Layout}, at the measured defaults...");
            result = await PlanetaryAuto.RunAsync(stream, identity, ChoicesOf(settings), progress: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A file that is no capture, or one whose frames cannot be stacked, is this step's refusal and not the end
            // of the run: what the reader or the stacker said is the reason.
            return OperationOutcome.BadInput($"{inputs[0].Name} could not be stacked as a planetary capture: {ex.Message}");
        }

        try
        {
            say?.Invoke($"stacked {result.Stack.FramesUsed}/{result.Stack.FramesGraded} frames");
            PlanetaryAuto.Write(result, (outputs.Path, sharpened));
            say?.Invoke($"wrote {outputs.Path} (linear master) and {sharpened} ({result.HowSharpened})");
            if (result.Stack.Master.ChannelCount == 3)
            {
                say?.Invoke(result.HowBalanced);
            }

            return OperationOutcome.Done;
        }
        finally
        {
            result.Stack.Master.Release();
            result.Sharpened.Release();
            result.Layer?.Master.Release();
        }
    }
}
