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
/// The settings of <see cref="PlanetaryStackOperation"/>: what is said of the capture, in place of what it is read to
/// be. AUTO takes no other setting: its stack and its sharpening are the measured defaults.
/// </summary>
public sealed record PlanetaryStackSettings
{
    /// <summary>The planet, by its name (<c>--planet</c>: jupiter, saturn, mars); left out, read off the capture.</summary>
    public string? Planet { get; set; }

    /// <summary>A mono capture's filter, as its wavelength in nanometres (<c>--wavelength</c>); left out, read off the
    /// capture.</summary>
    public double? FilterNm { get; set; }

    /// <summary>The telescope's aperture in millimetres (<c>--aperture-mm</c>); left out, read off the capture.</summary>
    public int? ApertureMm { get; set; }

    /// <summary>The design of the telescope <see cref="ApertureMm"/> gives (<c>--design</c>), whose usual central
    /// obstruction the sharpening takes.</summary>
    public OpticalDesign Design { get; set; } = OpticalDesign.Unknown;
}

/// <summary>
/// AUTO (<c>tianwen planetary stack --auto</c>, <see cref="PlanetaryAuto"/>): a recorded capture identified, and
/// stacked and sharpened at the measured defaults. Its own image is the capture (a SER file); its result is the linear
/// master, and the sharpened master is its extra result.
/// </summary>
/// <remarks>
/// <para>It runs the three routines the verb and the viewer's Auto view run, in their order, and nothing between:
/// <see cref="PlanetaryIdentification.IdentifyAsync"/>, <see cref="PlanetaryAuto.RunAsync"/> and
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
        ])
    {
        ExtraResults = [new OperationResultRole(Sharpened, "The master sharpened by the gains derived through its limb.")],
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<PlanetaryStackSettings> SettingsInfo => OperationSettingsJsonContext.Default.PlanetaryStackSettings;

    /// <inheritdoc/>
    public override string? Refusal(PlanetaryStackSettings settings)
    {
        if (settings.Planet is { } planet && PlanetaryCaptureName.Named(planet) is null)
        {
            return $"planet '{planet}' is not a planet's name: jupiter, saturn, mars or another by its name";
        }

        if (settings.FilterNm is { } nm && !(nm > 0 && double.IsFinite(nm)))
        {
            return "filterNm is a wavelength in nanometres, more than 0";
        }

        if (settings.ApertureMm is <= 0)
        {
            return "apertureMm is millimetres, more than 0";
        }

        return settings.ApertureMm is null && settings.Design is not OpticalDesign.Unknown
            ? "design is the design of the telescope apertureMm gives: give its aperture too"
            : null;
    }

    /// <summary>What the settings say of the capture, as the identification takes it.</summary>
    public static PlanetaryIdentityGiven GivenOf(PlanetaryStackSettings settings)
        => new(settings.Planet is { } planet ? PlanetaryCaptureName.Named(planet) : null, settings.FilterNm, settings.ApertureMm, settings.Design);

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
            result = await PlanetaryAuto.RunAsync(stream, identity, progress: null, cancellationToken);
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
