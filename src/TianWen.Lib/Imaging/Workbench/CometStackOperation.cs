using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Workbench;

// Every settings property is settable and carries its default (see OperationSettings.cs).

/// <summary>
/// The settings of <see cref="CometStackOperation"/>: the stack's own (<see cref="StackSettings"/>), and the comet run's
/// options of <c>tianwen stack</c>, each with the option's default.
/// </summary>
public sealed record CometStackSettings : StackSettings
{
    // The record the verb's options default to, so a default is said once.
    private static readonly StackingOptions CometDefaults = new(string.Empty, string.Empty);

    /// <summary>The comet's motion across the reference frame's pixel grid, in pixels an hour along x (with
    /// <see cref="RateY"/>, <c>--comet-rate dx,dy</c>). Left out, the comet's track (an image the step takes) places
    /// it. A rate carries no position, so a step with one makes no star layer and no composite.</summary>
    public float? RateX { get; set; }

    /// <summary>The rate along y, given with <see cref="RateX"/> or not at all.</summary>
    public float? RateY { get; set; }

    /// <summary>Write the star layer beside the comet master: the same frames registered on the stars, the body taken
    /// out of each (off: <c>--no-star-layer</c>).</summary>
    public bool StarLayer { get; set; } = CometDefaults.CometStarLayer;

    /// <summary>Write the composite: the star layer with the body added back once, where the body was subtracted
    /// (off: <c>--no-composite</c>).</summary>
    public bool Composite { get; set; } = CometDefaults.CometComposite;

    /// <summary>The radius taken out of the star layer around the body, in arcseconds, where no star remover serves to
    /// subtract it (<c>--comet-mask-arcsec</c>).</summary>
    public float MaskArcsec { get; set; } = CometDefaults.CometMaskArcsec;
}

/// <summary>
/// A comet's night stacked on the comet (<c>tianwen stack --comet-track</c>, #2489): the frames registered on the body
/// so it integrates sharp and the stars trail, and, beside it, the same frames registered on the stars with the body
/// taken out, and the two put together. Its image is the folder of frames; the comet's track is an image it takes, the
/// file <c>tianwen comet track</c> wrote. Its results, for each group the scan finds: the comet master under the group's
/// name, the star layer under the name with <see cref="StarsEnding"/>, and the composite with
/// <see cref="CompositeEnding"/>.
/// </summary>
/// <remarks>
/// <para><b>The track is a file, never a question asked while the step runs</b> (the owner's call of 2026-11-25). A
/// step's key is fixed before it runs and its result is reused under it, and Horizons' answer is not fixed. The file is
/// an original like any other, read by its bytes, so the key holds the ephemeris the comet was placed by
/// (<see cref="CometTrack"/>).</para>
/// <para><b>The reference frame is plate-solved, and nothing else is.</b> The track is projected onto the reference
/// frame's pixels through its plate solution, which needs the star catalogue; the masters are not solved
/// (<see cref="StackingOptions.SolveMaster"/> off), as no step's master is: a solve is a step of its own.</para>
/// <para><b>What serves it is in its key:</b> where a star remover serves, the star layer SUBTRACTS the body it took
/// out of the comet master, and the composite adds it back; where none does, the star layer EXCLUDES a disc around the
/// body and there is no composite. Different pixels, so the key says which.</para>
/// <para>Not yet steps of it (#2489's second leg): stars removed from every frame (<c>--remove-stars</c>), a run driven
/// by an earlier run's manifest (<c>--manifest</c>), and a white balance taken from another master
/// (<c>--inherit-wb</c>).</para>
/// </remarks>
/// <param name="logger">Takes the pipeline's own lines, or null.</param>
/// <param name="catalogDb">The star catalogue the reference frame is plate-solved against; with none, a step with a
/// track is not served.</param>
/// <param name="starRemover">The remover the star layer subtracts the body with, or null.</param>
public sealed class CometStackOperation(ILogger<StackingPipeline>? logger = null, ICelestialObjectDB? catalogDb = null, IStarRemover? starRemover = null)
    : RunNamedOperation<CometStackSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "comet-stack";

    /// <summary>What a group's star layer is named by: the group's name with this after it.</summary>
    public const string StarsEnding = "_stars";

    /// <summary>What a group's composite is named by: the group's name with this after it.</summary>
    public const string CompositeEnding = "_composite";

    private static readonly CometStackSettings Defaults = new();

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Comet stack", Verb: "stack", OperationOutputKind.Stored,
        ExtraInputs: [new OperationInputRole("track", "The comet's track for these frames: the file tianwen comet track wrote. Left out, rateX and rateY place the comet.", Optional: true)],
        Settings:
        [
            .. StackOperation.SettingsListed,
            new OperationSetting("rateX", "The comet's motion across the reference frame's pixels, in pixels an hour along x; left out, the track places it.",
                "A rate carries no position, so it makes no star layer and no composite: the track is the way to both."),
            new OperationSetting("rateY", "The comet's motion in pixels an hour along y, given with rateX or not at all."),
            new OperationSetting("starLayer", "Write the star layer beside the comet master: the same frames registered on the stars, the body taken out of each.",
                "Built in the same run as the comet master, the only way the two share a reference, a canvas, a debayer and a frame set."),
            new OperationSetting("composite", "Write the composite: the star layer with the body added back once, where it was subtracted."),
            new OperationSetting("maskArcsec", "The radius taken out of the star layer around the body, in arcseconds, where no star remover serves to subtract it.",
                "240\": where C/2025 R2's coma reached the background (StackingOptions.CometMaskArcsec)."),
        ])
    {
        NamedByRun = true,
        TakesFrameSet = true,
    };

    /// <inheritdoc/>
    protected override JsonTypeInfo<CometStackSettings> SettingsInfo => OperationSettingsJsonContext.Default.CometStackSettings;

    /// <summary>The rate the settings give, or null where they give none.</summary>
    public static Vector2? RateOf(CometStackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings is { RateX: { } x, RateY: { } y } ? new Vector2(x, y) : null;
    }

    /// <inheritdoc/>
    public override string? Refusal(CometStackSettings settings)
    {
        if (StackOperation.RefusalOf(settings) is { } stack)
        {
            return stack;
        }

        if (settings.RateX.HasValue != settings.RateY.HasValue)
        {
            return "rateX and rateY are one rate: give both or neither";
        }

        if (RateOf(settings) is { } rate && !(float.IsFinite(rate.X) && float.IsFinite(rate.Y)))
        {
            return "rateX and rateY are pixels an hour, each a number";
        }

        if (!(settings.MaskArcsec > 0 && float.IsFinite(settings.MaskArcsec)))
        {
            return "maskArcsec is arcseconds, more than 0";
        }

        // A setting that would change a step's key and none of its pixels is refused: the star layer's own settings
        // where no star layer is made, with it switched off or with a rate, which carries no position to make one by.
        var layerSettingsMoved = settings.Composite != Defaults.Composite || settings.MaskArcsec != Defaults.MaskArcsec;
        if (!settings.StarLayer && layerSettingsMoved)
        {
            return "composite and maskArcsec are the star layer's, and starLayer is off: there is nothing they change";
        }

        return RateOf(settings) is not null && (layerSettingsMoved || !settings.StarLayer)
            ? "a rate carries no position, so a step with one makes no star layer: starLayer, composite and maskArcsec change nothing. Give the track instead"
            : null;
    }

    /// <summary>The star remover the star layer subtracts the body with, as the key holds it; or that the body is
    /// excluded because none serves. Nothing where no star layer is asked for.</summary>
    public override (string By, string Tag) Served(CometStackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.StarLayer || RateOf(settings) is not null)
        {
            return (string.Empty, string.Empty);
        }

        if (!IEnhancerAvailability.ServesAny(starRemover, EnhanceOptions.Default))
        {
            return ("star layer: the body excluded, no star remover serves", string.Empty);
        }

        var (name, tag) = IEnhancerAvailability.ServedOf(starRemover, EnhanceOptions.Default);
        return ($"star layer: the body subtracted by {name}", tag);
    }

    /// <inheritdoc/>
    public override string ServedBy(CometStackSettings settings) => Served(settings).By;

    /// <inheritdoc/>
    public override string ServedTag(CometStackSettings settings) => Served(settings).Tag;

    /// <summary>
    /// The pipeline's options for <paramref name="settings"/> and the comet's <paramref name="track"/> (null where the
    /// settings give a rate): the stack's (<see cref="StackOperation.OptionsOf"/>) and the comet run's.
    /// </summary>
    public static StackingOptions OptionsOf(CometStackSettings settings, string dataRoot, string outputDir, CometTrack? track)
        => StackOperation.OptionsOf(settings, dataRoot, outputDir) with
        {
            CometTrack = track,
            CometRatePxPerHour = RateOf(settings),
            CometStarLayer = settings.StarLayer,
            CometComposite = settings.Composite,
            CometMaskArcsec = settings.MaskArcsec,
            // The reference frame is solved to place the track; a master never is here (a solve is a step of its own).
            SolveMaster = false,
        };

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(CometStackSettings settings, IReadOnlyList<string> inputs, string output)
    {
        // The stack's line, then the comet run's options: where its track is, or its rate, and what the star layer is.
        var line = StackOperation.LineOf(settings, inputs[0], output);
        if (inputs.Count > 1)
        {
            line.AddRange("--comet-track", inputs[1]);
        }

        if (RateOf(settings) is { } rate)
        {
            line.AddRange("--comet-rate", $"{StepCommandLine.Number(rate.X)},{StepCommandLine.Number(rate.Y)}");
        }

        if (!settings.StarLayer)
        {
            line.Add("--no-star-layer");
        }

        if (!settings.Composite)
        {
            line.Add("--no-composite");
        }

        if (settings.MaskArcsec != Defaults.MaskArcsec)
        {
            line.AddRange("--comet-mask-arcsec", StepCommandLine.Number(settings.MaskArcsec));
        }

        return line.ToImmutable();
    }

    /// <inheritdoc/>
    public override async ValueTask<NamedResults> RunIntoAsync(CometStackSettings settings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        CancellationToken cancellationToken)
    {
        if (inputs[0].Path is not { } frames || !Directory.Exists(frames))
        {
            return NamedResults.Refused(OperationOutcome.BadInput($"'{Id}' takes a folder of frames, and {inputs[0].Name} is none"));
        }

        CometTrack? track = null;
        if (inputs.Count > 1)
        {
            if (inputs[1].Path is not { } trackFile)
            {
                return NamedResults.Refused(OperationOutcome.BadInput($"'{Id}' takes the comet's track as a file, and {inputs[1].Name} is none"));
            }

            if (!CometTrack.TryRead(trackFile, out var read, out var unread))
            {
                return NamedResults.Refused(OperationOutcome.BadInput(unread));
            }

            track = read;
        }

        var rate = RateOf(settings);
        if ((track is null) == (rate is null))
        {
            return NamedResults.Refused(OperationOutcome.BadInput(track is null
                ? $"'{Id}' places the comet by its track (an image that is the file tianwen comet track wrote) or by rateX and rateY: give one"
                : $"'{Id}' is given the comet's track and rateX and rateY: say one"));
        }

        if (track is not null && catalogDb is null)
        {
            return NamedResults.Refused(OperationOutcome.NotServed(
                $"'{Id}' plate-solves the reference frame to place the track of {track.Designation}, and there is no star catalogue here"));
        }

        ILogger pipelineLogger = logger is null ? NullLogger.Instance : logger;
        var pipeline = new StackingPipeline(
            OptionsOf(settings, frames, folder, track),
            pipelineLogger,
            // The catalogue only where the track has to be placed, as the verb hands it only then.
            catalogDb: track is null ? null : catalogDb,
            progress: say is null ? null : new StackOperation.ScanReports(say),
            // The star layer subtracts the body with the remover where one serves (asked by the pipeline itself).
            starRemover: settings.StarLayer ? starRemover : null,
            // No data folder: a step never reads the clear nights this computer keeps.
            external: null);

        var results = ImmutableArray.CreateBuilder<NamedResult>();
        var skipped = new List<string>();
        try
        {
            await foreach (var group in pipeline.RunAsync(cancellationToken))
            {
                say?.Invoke(StackOperation.GroupLine(group));
                if (group.MasterFitsPath is not { } master || !string.IsNullOrEmpty(group.SkipReason))
                {
                    skipped.Add($"{group.GroupSlug} ({group.SkipReason})");
                    continue;
                }

                results.Add(new NamedResult(group.GroupSlug, master, StackOperation.BesideOf(master)));
                if (group.StarLayerFitsPath is { } stars && File.Exists(stars))
                {
                    say?.Invoke($"{group.GroupSlug}: star layer {Path.GetFileName(stars)}");
                    results.Add(new NamedResult(group.GroupSlug + StarsEnding, stars, StackOperation.BesideOf(stars)));
                }

                if (group.CompositeFitsPath is { } composite && File.Exists(composite))
                {
                    say?.Invoke($"{group.GroupSlug}: composite {Path.GetFileName(composite)}");
                    results.Add(new NamedResult(group.GroupSlug + CompositeEnding, composite, StackOperation.BesideOf(composite)));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A set that cannot be stacked is this step's refusal and not the end of the run, as for a stack.
            pipelineLogger.LogError(ex, "The comet stack of {Frames} failed", frames);
            return NamedResults.Refused(OperationOutcome.Failed($"the comet stack of {inputs[0].Name} failed: {ex.Message}"));
        }

        if (results.Count == 0)
        {
            return NamedResults.Refused(OperationOutcome.BadInput(skipped.Count == 0
                ? $"{inputs[0].Name} holds no group of lights to stack"
                : $"no group of {inputs[0].Name} gave a master: {string.Join("; ", skipped)}"));
        }

        return new NamedResults(OperationOutcome.Done, results.ToImmutable());
    }
}
