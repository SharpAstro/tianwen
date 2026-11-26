using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Lib.Imaging.Workbench;

// Every settings property is settable and carries its default (see OperationSettings.cs).

/// <summary>The settings of <see cref="PlanetaryComposeOperation"/>.</summary>
public sealed record PlanetaryComposeSettings
{
    /// <summary>The planet every stack is of (<c>--planet</c>); left out, each stack's own header or name says it.</summary>
    public string? Planet { get; set; }

    /// <summary>Carry the IR or L stacks' detail into the colour planes (<c>--lrgb</c>).</summary>
    public bool Lrgb { get; set; }

    /// <summary>Weigh each de-rotated stack's pixels by how it saw them (<c>--viewing-weights</c>).</summary>
    public PlanetaryViewingWeights ViewingWeights { get; set; } = PlanetaryViewingWeights.None;

    /// <summary>Join each filter's stacks band by band (<c>--band-weights</c>).</summary>
    public PlanetaryBandWeights BandWeights { get; set; } = PlanetaryBandWeights.None;

    /// <summary>How the de-rotation relights what it carries (<c>--relight</c>).</summary>
    public PlanetaryTurnRelight Relight { get; set; } = PlanetaryTurnRelight.Blurred;

    /// <summary>Where the turn starts to fade in beside Saturn's rings, in multiples of each stack's core blur
    /// (<c>--ring-feather</c>'s first number). With <see cref="RingFeatherOuter"/>, or both left out for no feather.</summary>
    public double? RingFeatherInner { get; set; }

    /// <summary>Where the turn is taken whole (<c>--ring-feather</c>'s second number).</summary>
    public double? RingFeatherOuter { get; set; }

    /// <summary>Balance the colour master to the planet's own colour (off: <c>--no-colour-balance</c>).</summary>
    public bool ColourBalance { get; set; } = true;
}

/// <summary>
/// A colour master from a mono camera's planetary stacks (<c>tianwen planetary compose</c>,
/// <see cref="PlanetaryComposition.Run"/>): each stack moved onto one disk by its limb, de-rotated to one instant, each
/// filter's stacks averaged, and red, green and blue joined. The stacks through an IR or L filter are the luminance,
/// which is the step's extra result where there is one.
/// </summary>
/// <remarks>
/// <para><b>It takes any number of stacks, as a set</b> (<see cref="OperationDescription.AnyNumber"/>): what each one
/// is, its planet, instant and filter, is in its own header or name, and never its place among the others.</para>
/// <para>It runs the routines the verb runs, in their order: <see cref="PlanetaryComposition.Ingest"/> for each stack,
/// <see cref="PlanetaryComposition.Run"/>, and <see cref="WriteAsync"/>, which the verb calls too. The verb's preview
/// picture is its own.</para>
/// </remarks>
public sealed class PlanetaryComposeOperation : MultiResultOperation<PlanetaryComposeSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "planetary-compose";

    /// <summary>The name of the extra result that is the luminance, given where an IR or L stack was among the stacks.</summary>
    public const string Luminance = "luminance";

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Planetary compose", Verb: "planetary compose", OperationOutputKind.Stored, ExtraInputs: [],
        Settings:
        [
            new OperationSetting("planet", "The planet every stack is of; left out, each stack's own header or name says it."),
            new OperationSetting("lrgb", "Carry the IR or L stacks' detail into the colour planes."),
            new OperationSetting("viewingWeights", "Weigh each de-rotated stack's pixels by how it saw them: none, source or limb."),
            new OperationSetting("bandWeights", "Join each filter's stacks band by band: none, matched or wiener."),
            new OperationSetting("relight", "How the de-rotation relights what it carries: blurred or minnaert."),
            new OperationSetting("ringFeatherInner", "Where the turn starts to fade in beside Saturn's rings, in multiples of each stack's core blur. With ringFeatherOuter."),
            new OperationSetting("ringFeatherOuter", "Where the turn is taken whole."),
            new OperationSetting("colourBalance", "Balance the colour master to the planet's own colour."),
        ])
    { Summary = "Joins the planetary stacks of the colour channels into one colour master and a luminance.",
        AnyNumber = true,
        ExtraResults = [new OperationResultRole(Luminance, "The IR or L stacks joined, where there was one.", Optional: true)],
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<PlanetaryComposeSettings> SettingsInfo => OperationSettingsJsonContext.Default.PlanetaryComposeSettings;

    /// <inheritdoc/>
    public override string? Refusal(PlanetaryComposeSettings settings)
    {
        if (settings.Planet is { } planet && PlanetaryCaptureName.Named(planet) is null)
        {
            return $"planet '{planet}' is not a planet's name: jupiter or saturn";
        }

        if ((settings.RingFeatherInner is null) != (settings.RingFeatherOuter is null))
        {
            return "a ring feather is two numbers, ringFeatherInner and ringFeatherOuter: state both, or neither";
        }

        return settings is { RingFeatherInner: { } inner, RingFeatherOuter: { } outer } && !(inner >= 0 && outer > inner)
            ? "the ring feather's inner is 0 or more and its outer is past it"
            : null;
    }

    /// <summary>The ring feather the settings state, or null for none.</summary>
    public static (double Inner, double Outer)? RingFeatherOf(PlanetaryComposeSettings settings)
        => settings is { RingFeatherInner: { } inner, RingFeatherOuter: { } outer } ? (inner, outer) : null;

    /// <summary>Where the two results go: the colour master as the main result, the luminance as the extra one.</summary>
    public static StepOutputs Outputs(string master, string luminance)
        => new(master) { Extra = ImmutableDictionary<string, string>.Empty.Add(Luminance, luminance) };

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(PlanetaryComposeSettings settings, IReadOnlyList<string> inputs, StepOutputs outputs)
    {
        var line = ImmutableArray.CreateBuilder<string>();
        line.AddRange(StepCommandLine.Program, "planetary", "compose");
        line.AddRange(inputs);
        line.AddRange("-o", outputs.Path);
        if (outputs.Extra.TryGetValue(Luminance, out var luminance))
        {
            line.AddRange("--luminance-output", luminance);
        }

        if (settings.Planet is { } planet)
        {
            line.AddRange("--planet", planet);
        }

        if (settings.Lrgb)
        {
            line.Add("--lrgb");
        }

        if (settings.ViewingWeights is not PlanetaryViewingWeights.None)
        {
            line.AddRange("--viewing-weights", settings.ViewingWeights.ToString().ToLowerInvariant());
        }

        if (settings.BandWeights is not PlanetaryBandWeights.None)
        {
            line.AddRange("--band-weights", settings.BandWeights.ToString().ToLowerInvariant());
        }

        line.AddRange("--relight", settings.Relight.ToString().ToLowerInvariant());
        if (RingFeatherOf(settings) is { } feather)
        {
            line.AddRange("--ring-feather", $"{StepCommandLine.Number(feather.Inner)},{StepCommandLine.Number(feather.Outer)}");
        }

        if (!settings.ColourBalance)
        {
            line.Add("--no-colour-balance");
        }

        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<OperationOutcome> RunAsync(PlanetaryComposeSettings settings, IReadOnlyList<OperationInput> inputs, StepOutputs outputs,
        Action<string>? say, CancellationToken cancellationToken)
    {
        var planet = settings.Planet is { } named ? PlanetaryCaptureName.Named(named) : null;
        var stacks = new List<PlanetaryMonoStack>(inputs.Count);
        foreach (var input in inputs)
        {
            // A file is read as the verb reads it, so a TIFF another stacker wrote is a stack too; what a derived
            // step hands over has no file, and is taken as it is.
            Image? image;
            if (input.Path is { } path)
            {
                if (!Image.TryReadImageFile(path, out image))
                {
                    return OperationOutcome.BadInput($"{path}: not a readable image");
                }
            }
            else if (!input.TryRead(out image, out _, out var refusal))
            {
                return OperationOutcome.BadInput(refusal);
            }

            // A stack's labels are its header's, else its file name's: the name of a file is all a stack another
            // program wrote may carry.
            var (stack, why) = PlanetaryComposition.Ingest(input.Path ?? input.Name, image, planet);
            if (stack is null)
            {
                return OperationOutcome.BadInput(why ?? $"{input.Name}: not a labelled mono stack");
            }

            stacks.Add(stack);
        }

        var (composed, derotation, refused) = await Task.Run(
            () => PlanetaryComposition.Run(stacks, say, settings.Lrgb, settings.ViewingWeights, settings.BandWeights, settings.Relight, RingFeatherOf(settings)),
            cancellationToken);
        if (composed is null || derotation is null)
        {
            return OperationOutcome.Failed(refused ?? "nothing composed");
        }

        await WriteAsync(composed, outputs.Path, outputs.Extra.TryGetValue(Luminance, out var luminance) ? luminance : null, settings.ColourBalance, say);
        return OperationOutcome.Done;
    }

    /// <summary>
    /// The composed master written: balanced to the planet's own colour unless asked not to, as
    /// <c>planetary stack</c> balances its masters (#1580), with the balance's cards; and the luminance beside it where
    /// there is one and a place for it. <c>planetary compose</c>, <c>join</c> and <c>lrgb</c> write through this.
    /// </summary>
    /// <param name="composed">What the composition gave.</param>
    /// <param name="masterPath">The colour master's file.</param>
    /// <param name="luminancePath">The luminance's file, or null to write none.</param>
    /// <param name="balanceColour">Balance a three-plane master to the planet's own colour.</param>
    /// <param name="say">Takes each line of what was written.</param>
    /// <param name="withWritten">Handed the image as it was written, before it is released: a caller's preview of it.</param>
    public static async Task WriteAsync(PlanetaryComposed composed, string masterPath, string? luminancePath, bool balanceColour, Action<string>? say,
        Func<Image, Task>? withWritten = null)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var (balanced, balance, how) = balanceColour && composed.Master.ChannelCount == 3
            ? PlanetaryComposition.BalancedToPlanet(composed.Master)
            : (null, null, "colours left as the filters gave them");
        var written = balanced ?? composed.Master;
        try
        {
            written.WriteToFitsFile(masterPath, null, balance?.HeaderCards());
            say?.Invoke($"wrote {masterPath}");
            if (withWritten is not null)
            {
                await withWritten(written);
            }

            if (composed.Master.ChannelCount == 3)
            {
                say?.Invoke(how);
            }
        }
        finally
        {
            balanced?.Release();
        }

        if (composed.Luminance is { } luminance && luminancePath is not null)
        {
            luminance.WriteToFitsFile(luminancePath);
            say?.Invoke($"wrote the luminance {luminancePath}");
        }
    }
}
