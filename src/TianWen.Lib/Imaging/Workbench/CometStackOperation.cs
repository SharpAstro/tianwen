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

    /// <summary>Remove the stars from every frame after registration and before integration, so the comet master is the
    /// comet layer with no star trails in it (<c>--remove-stars</c>). Needs a star remover that serves.</summary>
    public bool RemoveStars { get; set; } = CometDefaults.RemoveStarsPerFrame;

    /// <summary>How a Bayer frame is shaped for the star remover where <see cref="RemoveStars"/> is on
    /// (<c>--star-removal-mode</c>): whole as a mosaic, which keeps the coma, or split into its photosite planes.</summary>
    public StarRemovalMode StarRemovalMode { get; set; } = CometDefaults.StarRemovalMode;

    /// <summary>Take the white balance of the master the run follows (<c>--inherit-wb</c>), so two layers meant to be
    /// put together share one; a starless layer has no stars to solve its own on. Only a followed master that states a
    /// balance gives one: an original the stack verb wrote with a picture or with an inherited balance
    /// (<c>--inherit-wb</c>), or the master of a run that itself inherited one. A master a workbench step wrote states
    /// no balance of its own, and a run asked to take one from a master that states none is refused.</summary>
    public bool InheritWhiteBalance { get; set; }
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
/// <para><b>A run that follows another</b> (#2489's second leg, the owner's call of 2026-11-25): its second extra
/// image, after the track, is an earlier stack's master. Its manifest, which the store keeps beside it, drives the run
/// (<c>--manifest</c>: the frames, the reference and each frame's star transform), so a starless comet layer
/// (<see cref="CometStackSettings.RemoveStars"/>, <c>--remove-stars</c>) shares the comet stack's canvas; with
/// <see cref="CometStackSettings.InheritWhiteBalance"/> it takes that master's white balance too (<c>--inherit-wb</c>).
/// One image for both, so the master that is followed must also state a balance where one is taken: an original the
/// stack verb wrote with a picture or with an inherited balance (<c>--inherit-wb</c>) does, and so does the master of a
/// run that itself inherited one. A master a workbench step wrote states no balance of its own (no step renders or
/// solves its master), so a step-made master that inherited none is refused in words. A run that follows names its
/// track first, so a bare rate cannot follow.</para>
/// </remarks>
/// <param name="logger">Takes the pipeline's own lines, or null.</param>
/// <param name="catalogDb">The star catalogue the reference frame is plate-solved against; with none, a step with a
/// track is not served.</param>
/// <param name="starRemover">The remover the star layer subtracts the body with and, with
/// <see cref="CometStackSettings.RemoveStars"/>, each frame's stars are removed by; or null.</param>
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
        ExtraInputs:
        [
            new OperationInputRole("track", "The comet's track for these frames: the file tianwen comet track wrote. Left out, rateX and rateY place the comet.", Optional: true),
            new OperationInputRole("follows", "An earlier stack's master this run follows: its manifest gives the frames, the reference and each frame's star transform, "
                + "and with inheritWhiteBalance its white balance is taken, which only a master that states one can give (an original the stack verb wrote with a picture or with an inherited balance, "
                + "or the master of a run that itself inherited one; a workbench step's master has none of its own). Named after the track.", Optional: true),
        ],
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
            new OperationSetting("removeStars", "Remove the stars from every frame before integration, so the comet master has no star trails: the comet layer. Needs a star remover.",
                "Per frame, after registration: rejection afterwards cannot take a trail out (docs/plans/comet-integration.md)."),
            new OperationSetting("starRemovalMode", "How a Bayer frame is shaped for the star remover: mosaic (whole) or splitCfa (its photosite planes).",
                "Mosaic: splitting took 10P's comet from 85.7% of its flux to 22.4% (StarRemovalMode)."),
            new OperationSetting("inheritWhiteBalance", "Take the white balance of the master the run follows, so the two layers share one. Only a master that states one gives it: an original the stack verb wrote with a picture or with an inherited balance (--inherit-wb), or the master of a run that itself inherited one. A workbench step's master has none of its own, and a master that states none is refused."),
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

        if (RateOf(settings) is not null && (layerSettingsMoved || !settings.StarLayer))
        {
            return "a rate carries no position, so a step with one makes no star layer: starLayer, composite and maskArcsec change nothing. Give the track instead";
        }

        return !settings.RemoveStars && settings.StarRemovalMode != Defaults.StarRemovalMode
            ? "starRemovalMode is how a frame is handed to the star remover, and removeStars is off: there is nothing it changes"
            : null;
    }

    /// <summary>What serves the step, as the key holds it: the star remover each frame's stars are removed by, where
    /// <see cref="CometStackSettings.RemoveStars"/> is on, and the one the star layer subtracts the body with, or that the
    /// body is excluded because none serves. Nothing where neither is asked for.</summary>
    public override (string By, string Tag) Served(CometStackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var removeStars = settings.RemoveStars;
        var starLayer = settings.StarLayer && RateOf(settings) is null;
        if (!removeStars && !starLayer)
        {
            return (string.Empty, string.Empty);
        }

        // Asked per channel count, as the pipeline asks it of each master (StackingPipeline: Serves on the master's own
        // channels): the frames are not read here, so the key says what happens to a mono master and to a colour one.
        var mono = IEnhancerAvailability.Serves(starRemover, 1, EnhanceOptions.Default);
        var colour = IEnhancerAvailability.Serves(starRemover, 3, EnhanceOptions.Default);
        var (name, tag) = mono || colour ? IEnhancerAvailability.ServedOf(starRemover, EnhanceOptions.Default) : (string.Empty, string.Empty);
        var parts = new List<string>(2);
        if (removeStars)
        {
            // The per-frame path asks whether the remover serves either kind (StackingPipeline, RemoveStarsPerFrame).
            parts.Add(mono || colour
                ? $"each frame's stars removed by {name} ({StackOperation.Word(settings.StarRemovalMode)})"
                : "each frame's stars removed, and no star remover serves");
        }

        if (starLayer)
        {
            var subtracted = $"the body subtracted by {name}";
            parts.Add("star layer: " + (!mono && !colour ? "the body excluded, no star remover serves"
                : mono == colour ? subtracted
                : mono ? $"{subtracted} on a mono master, excluded on a colour one"
                : $"{subtracted} on a colour master, excluded on a mono one"));
        }

        return (string.Join("; ", parts), tag);
    }

    /// <inheritdoc/>
    public override string ServedBy(CometStackSettings settings) => Served(settings).By;

    /// <inheritdoc/>
    public override string ServedTag(CometStackSettings settings) => Served(settings).Tag;

    /// <summary>
    /// The pipeline's options for <paramref name="settings"/> and the comet's <paramref name="track"/> (null where the
    /// settings give a rate): the stack's (<see cref="StackOperation.OptionsOf"/>) and the comet run's. A run that follows
    /// another is given that master's manifest (<paramref name="manifestPath"/>) and, where it inherits it, its white
    /// balance (<paramref name="inheritedWhiteBalance"/>).
    /// </summary>
    public static StackingOptions OptionsOf(CometStackSettings settings, string dataRoot, string outputDir, CometTrack? track,
        string? manifestPath = null, ColourCalibration? inheritedWhiteBalance = null)
        => StackOperation.OptionsOf(settings, dataRoot, outputDir) with
        {
            CometTrack = track,
            CometRatePxPerHour = RateOf(settings),
            CometStarLayer = settings.StarLayer,
            CometComposite = settings.Composite,
            CometMaskArcsec = settings.MaskArcsec,
            RemoveStarsPerFrame = settings.RemoveStars,
            StarRemovalMode = settings.StarRemovalMode,
            ManifestPath = manifestPath,
            InheritedWhiteBalance = inheritedWhiteBalance,
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

        if (settings.RemoveStars)
        {
            line.Add("--remove-stars");
        }

        if (settings.StarRemovalMode != Defaults.StarRemovalMode)
        {
            line.AddRange("--star-removal-mode", StackOperation.Word(settings.StarRemovalMode));
        }

        // The master the run follows: its manifest beside it, and its white balance where it is inherited.
        if (inputs.Count > 2)
        {
            line.AddRange("--manifest", StackManifest.PathFor(inputs[2]));
            if (settings.InheritWhiteBalance)
            {
                line.AddRange("--inherit-wb", inputs[2]);
            }
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

            // The inputs are by place: a master named first, where the track goes, is told so in words and never read
            // as a track that will not parse.
            if (Image.IsFitsPath(trackFile))
            {
                return NamedResults.Refused(OperationOutcome.BadInput(
                    $"'{Id}' takes the comet's track first and the master it follows after it, and {inputs[1].Name} is a FITS image: "
                    + "name the track before it (a run that follows another is placed by its track)"));
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

        // The master the run follows: its manifest has to be beside it, and its white balance in it where it is taken.
        string? manifestPath = null;
        ColourCalibration? inherited = null;
        if (inputs.Count > 2)
        {
            if (inputs[2].Path is not { } followed || !Image.IsFitsPath(followed))
            {
                return NamedResults.Refused(OperationOutcome.BadInput($"'{Id}' follows an earlier stack's master, and {inputs[2].Name} is no FITS image"));
            }

            manifestPath = StackManifest.PathFor(followed);
            if (!File.Exists(manifestPath))
            {
                return NamedResults.Refused(OperationOutcome.BadInput(
                    $"{inputs[2].Name} has no manifest beside it ({Path.GetFileName(manifestPath)}): a run follows a master a stack wrote, which keeps its manifest beside it"));
            }

            if (settings.InheritWhiteBalance)
            {
                if (!Image.TryReadFitsHeader(followed, out var donor) || donor.Meta.ColourCalibration is not { } balance)
                {
                    return NamedResults.Refused(OperationOutcome.BadInput(
                        $"{inputs[2].Name} states no white balance (WBRED, WBGREEN, WBBLUE): only an original the stack verb wrote with a picture or with an inherited balance (--inherit-wb), "
                        + "or the master of a run that itself inherited one, carries one to follow; a master a workbench step wrote has none of its own, nor does a mono one"));
                }

                inherited = balance;
            }
        }
        else if (settings.InheritWhiteBalance)
        {
            return NamedResults.Refused(OperationOutcome.BadInput(
                $"inheritWhiteBalance takes the white balance of the master the run follows, and '{Id}' is named none after its track"));
        }

        if (track is not null && catalogDb is null)
        {
            return NamedResults.Refused(OperationOutcome.NotServed(
                $"'{Id}' plate-solves the reference frame to place the track of {track.Designation}, and there is no star catalogue here"));
        }

        // Asked as the per-frame path asks it, before any frame is read (the verb's own check).
        if (settings.RemoveStars && !IEnhancerAvailability.ServesAny(starRemover, EnhanceOptions.Default))
        {
            return NamedResults.Refused(OperationOutcome.NotServed(
                $"'{Id}' removes the stars from every frame, and no star remover serves here: RC-Astro StarXTerminator installed and licensed is one"));
        }

        ILogger pipelineLogger = logger is null ? NullLogger.Instance : logger;
        var pipeline = new StackingPipeline(
            OptionsOf(settings, frames, folder, track, manifestPath, inherited),
            pipelineLogger,
            // The catalogue only where the track has to be placed, as the verb hands it only then.
            catalogDb: track is null ? null : catalogDb,
            progress: say is null ? null : new StackOperation.ScanReports(say),
            // The star layer subtracts the body with the remover where one serves (asked by the pipeline itself), and
            // each frame's stars are removed by it where that is asked: the verb hands it for either.
            starRemover: settings.StarLayer || settings.RemoveStars ? starRemover : null,
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
