using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Workbench;

// Every settings property is settable and carries its default (see OperationSettings.cs).

/// <summary>
/// The settings of <see cref="StackOperation"/>: which frames and groups of a set are stacked, how they are registered
/// and how they are integrated. Each is an option of <c>tianwen stack</c>, and its default is the option's, read off
/// <see cref="StackingOptions"/> where the option reads it.
/// </summary>
/// <remarks>
/// What <c>tianwen stack</c> does besides is no setting here. A plate solve is a step of its own
/// (<see cref="SolveOperation"/>), an enhance is one too, and a picture of a master is how a result is shown. A comet
/// run is an operation of its own (<see cref="CometStackOperation"/>, #2489), whose settings are these and its own.
/// </remarks>
public record StackSettings
{
    // The record the verb's options default to, so a default is said once.
    private static readonly StackingOptions Defaults = new(string.Empty, string.Empty);

    private static readonly DrizzleOptions DrizzleDefaults = new();

    /// <summary>Stack only the groups whose name holds this (<c>--group-filter</c>); left out, every group.</summary>
    public string? GroupFilter { get; set; }

    /// <summary>Leave out the groups whose name holds this (<c>--group-exclude</c>); left out, none.</summary>
    public string? GroupExclude { get; set; }

    /// <summary>How far apart in sensor temperature, in degrees, two lights of one target may be and still go into one
    /// master (<c>--group-temp-tolerance</c>). 0 groups by the temperature rounded to the degree.</summary>
    public double GroupTemperatureTolerance { get; set; } = Defaults.LightGroupTemperatureToleranceC;

    /// <summary>Stack the frames of each side of the meridian apart (<c>--split-by-pierside</c>).</summary>
    public bool SplitByPierSide { get; set; } = Defaults.SplitByPierSide;

    /// <summary>Take the frames a session named as suspected cloud too (<c>--include-maybe-cloud</c>).</summary>
    public bool IncludeMaybeCloud { get; set; } = Defaults.IncludeSuspectedCloud;

    /// <summary>Take masters of an earlier stack as frames (<c>--include-integrations</c>): a mosaic's second stage.</summary>
    public bool IncludeIntegrations { get; set; } = Defaults.IncludeIntegrations;

    /// <summary>The sigma of the frame quality gate (<c>--quality-reject-sigma</c>); 0 runs no relative gate.</summary>
    public float QualityRejectSigma { get; set; } = Defaults.QualityRejectSigma ?? 0f;

    /// <summary>Leave out the lights the grade calls cloud or twilight (<c>--reject-cloud</c>).</summary>
    public bool RejectCloud { get; set; } = Defaults.RejectCloud;

    /// <summary>Refuse a dark whose gain is known and is not the lights' (<c>--require-gain-match</c>).</summary>
    public bool RequireGainMatch { get; set; } = Defaults.RequireGainMatch;

    /// <summary>The debayer the registration measures stars on (<c>--centroid-debayer</c>).</summary>
    public DebayerAlgorithm CentroidDebayer { get; set; } = Defaults.CentroidDebayerAlg;

    /// <summary>The least signal to noise a star is detected at (<c>--snr-min</c>).</summary>
    public float SnrMin { get; set; } = Defaults.SnrMin;

    /// <summary>Under this many stars the detection runs again at a lower level (<c>--min-stars</c>).</summary>
    public int MinStars { get; set; } = Defaults.MinStars;

    /// <summary>How many of the brightest stars the registration's quads are made of (<c>--quad-stars</c>).</summary>
    public int QuadStars { get; set; } = Defaults.QuadStars;

    /// <summary>Register on the first frame whose path holds this (<c>--reference-frame</c>); left out, the stack
    /// chooses its reference.</summary>
    public string? ReferenceFrame { get; set; }

    /// <summary>The kernel that places each frame on the reference's grid (<c>--warp-interpolation</c>).</summary>
    public WarpInterpolation WarpInterpolation { get; set; } = Defaults.WarpInterpolation;

    /// <summary>Place red and blue on green per frame (off: <c>--no-colour-align</c>).</summary>
    public bool ColourAlign { get; set; } = Defaults.AlignColours;

    /// <summary>The integration strategy (<c>--strategy</c>); left out, the stack chooses one by the frames alone, against a fixed nominal computer, and refuses where this computer cannot fit it.</summary>
    public IntegrationStrategyKind? Strategy { get; set; }

    /// <summary>The debayer the integration runs on (<c>--stack-debayer</c>).</summary>
    public DebayerAlgorithm StackDebayer { get; set; } = Defaults.StackDebayerAlg;

    /// <summary>The pixel rejector's low threshold (<c>--reject-low-sigma</c>); left out, its own for the frame count.</summary>
    public float? RejectLowSigma { get; set; }

    /// <summary>The pixel rejector's high threshold (<c>--reject-high-sigma</c>); left out, its own for the frame count.</summary>
    public float? RejectHighSigma { get; set; }

    /// <summary>The sigma of the bad pixel mask (<c>--hot-pixel-sigma</c>); 0 masks no pixel.</summary>
    public float HotPixelSigma { get; set; } = Defaults.HotPixelSigma;

    /// <summary>A drizzle drop's size, 0 to 1 (<c>--drizzle-pixfrac</c>).</summary>
    public float DrizzlePixfrac { get; set; } = DrizzleDefaults.Pixfrac;

    /// <summary>The least matched frames a drizzle runs on (<c>--drizzle-min-frames</c>).</summary>
    public int DrizzleMinFrames { get; set; } = DrizzleDefaults.MinFrameCount;

    /// <summary>Let the stack choose a drizzle where the frames allow one (off: <c>--no-bayer-drizzle</c>).</summary>
    public bool BayerDrizzle { get; set; } = !Defaults.DisableBayerDrizzle;
}

/// <summary>
/// The stack of a set of frames (<c>tianwen stack</c>, <see cref="StackingPipeline"/>): a night's lights with its
/// calibration frames, scanned, grouped, calibrated, registered and integrated. Its image is the folder; its results
/// are the masters, one for each group the scan finds, each under its group's name with its map sidecars and its
/// manifest beside it.
/// </summary>
/// <remarks>
/// <para>It builds the pipeline's options from its settings (<see cref="OptionsOf"/>, which the verb builds its own
/// from) and runs the pipeline, and nothing between.</para>
/// <para><b>A step never asks what this computer remembers.</b> The verb judges cloud against the clear nights the
/// viewer recorded on this computer (<c>LightGradeReferenceStore</c>); a step judges on the group's own baselines
/// (<see cref="StackingOptions.ClearNightReferences"/> off), so its result follows from its frames and its settings.
/// The verb's <c>--no-clear-night-references</c> is the same, and a step's command line says it.</para>
/// <para><b>No plate solve, and no picture.</b> A solve is a step of its own, so a master here has no plate solution,
/// and nothing is rendered: the line says <c>--no-plate-solve --output-format none</c>.</para>
/// <para><b>A reference named by <c>referenceFrame</c> is found in a frame's whole path</b>, as the verb finds it, and
/// the set's key holds each frame's place inside the folder only: name the frame by its file name, which the key
/// holds, and never by a folder above the set.</para>
/// <para>The calibration masters a run builds are in the folder it writes into, which the store does not keep: a
/// stack that runs again builds them again.</para>
/// </remarks>
/// <param name="logger">Takes the pipeline's own lines, or null.</param>
public sealed class StackOperation(ILogger<StackingPipeline>? logger = null) : RunNamedOperation<StackSettings>
{
    /// <summary>The operation's id.</summary>
    public const string Id = "stack";

    private static readonly StackSettings Defaults = new();

    /// <inheritdoc/>
    public override OperationDescription Description { get; } = new(
        Id, Version: 1, Title: "Stack", Verb: "stack", OperationOutputKind.Stored, ExtraInputs: [], Settings: SettingsListed)
    {
        Summary = "Aligns and stacks a night's light frames with their calibration frames into one master for each group.",
        NamedByRun = true,
        TakesFrameSet = true,
    };

    /// <summary>The settings of <see cref="StackSettings"/> as a listing shows them; the comet stack lists these first.</summary>
    internal static ImmutableArray<OperationSetting> SettingsListed { get; } =
        [
            new OperationSetting("groupFilter", "Stack only the groups whose name holds this; left out, every group."),
            new OperationSetting("groupExclude", "Leave out the groups whose name holds this; left out, none."),
            new OperationSetting("groupTemperatureTolerance",
                "How far apart in sensor temperature, in degrees, two lights of one target may be and still go into one master.",
                "0 groups by the temperature rounded to the degree; 2 covers a cooled camera's drift over a night (docs/plans/stacking.md)."),
            new OperationSetting("splitByPierSide", "Stack the frames of each side of the meridian apart."),
            new OperationSetting("includeMaybeCloud", "Take the frames a session named as suspected cloud too."),
            new OperationSetting("includeIntegrations", "Take masters of an earlier stack as frames: a mosaic's second stage."),
            new OperationSetting("qualityRejectSigma", "The sigma of the frame quality gate; 0 runs no relative gate.",
                "3, the value the dataset bake uses, so one session stacked either way is admitted by the same rule."),
            new OperationSetting("rejectCloud", "Leave out the lights the grade calls cloud or twilight.",
                "On since two labelled nights read the same through the stacker as through the viewer (docs/plans/stacking.md, section O)."),
            new OperationSetting("requireGainMatch", "Refuse a dark whose gain is known and is not the lights'."),
            new OperationSetting("centroidDebayer", "The debayer the registration measures stars on."),
            new OperationSetting("snrMin", "The least signal to noise a star is detected at."),
            new OperationSetting("minStars", "Under this many stars the detection runs again at a lower level."),
            new OperationSetting("quadStars", "How many of the brightest stars the registration's quads are made of.",
                "100: a quad needs the same four stars in both frames, and detections reproduce at the bright end."),
            new OperationSetting("referenceFrame", "Register on the first frame whose path holds this; left out, the stack chooses its reference."),
            new OperationSetting("warpInterpolation", "The kernel that places each frame on the reference's grid.",
                "Clamped Lanczos-3 keeps a 2 px star's width where bilinear adds about a pixel (docs/plans/deconvolver-training.md, R1)."),
            new OperationSetting("colourAlign", "Place red and blue on green per frame."),
            new OperationSetting("strategy", "The integration strategy; left out, the stack chooses one by the frames alone, against a fixed nominal computer, and refuses where this computer cannot fit it."),
            new OperationSetting("stackDebayer", "The debayer the integration runs on."),
            new OperationSetting("rejectLowSigma", "The pixel rejector's low threshold; left out, its own for the frame count."),
            new OperationSetting("rejectHighSigma", "The pixel rejector's high threshold; left out, its own for the frame count."),
            new OperationSetting("hotPixelSigma", "The sigma of the bad pixel mask; 0 masks no pixel."),
            new OperationSetting("drizzlePixfrac", "A drizzle drop's size, 0 to 1."),
            new OperationSetting("drizzleMinFrames", "The least matched frames a drizzle runs on."),
            new OperationSetting("bayerDrizzle", "Let the stack choose a drizzle where the frames allow one."),
        ];

    /// <inheritdoc/>
    protected override JsonTypeInfo<StackSettings> SettingsInfo => OperationSettingsJsonContext.Default.StackSettings;

    /// <inheritdoc/>
    public override string? Refusal(StackSettings settings) => RefusalOf(settings);

    /// <summary>Why <paramref name="settings"/> cannot be run, in words; null when they can. The comet stack refuses
    /// its share of its settings by this too.</summary>
    public static string? RefusalOf(StackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Each thing is said one way: a filter that filters nothing is left out, not written empty.
        if (settings.GroupFilter is { Length: 0 })
        {
            return "groupFilter is part of a group's name: leave it out to stack every group";
        }

        if (settings.GroupExclude is { Length: 0 })
        {
            return "groupExclude is part of a group's name: leave it out to leave no group out";
        }

        if (settings.ReferenceFrame is { Length: 0 })
        {
            return "referenceFrame is part of a frame's name: leave it out to let the stack choose its reference";
        }

        if (!(settings.GroupTemperatureTolerance >= 0 && double.IsFinite(settings.GroupTemperatureTolerance)))
        {
            return "groupTemperatureTolerance is degrees, 0 or more";
        }

        if (!(settings.QualityRejectSigma >= 0 && float.IsFinite(settings.QualityRejectSigma)))
        {
            return "qualityRejectSigma is 0 or more; 0 runs no relative gate";
        }

        if (!(settings.SnrMin >= 0 && float.IsFinite(settings.SnrMin)))
        {
            return "snrMin is 0 or more";
        }

        if (settings.MinStars < 0)
        {
            return "minStars is 0 or more";
        }

        if (settings.QuadStars < 4)
        {
            return "quadStars is 4 or more: a quad is four stars";
        }

        if (settings.RejectLowSigma is { } low && !(low > 0 && float.IsFinite(low)))
        {
            return "rejectLowSigma is more than 0; leave it out for the rejector's own";
        }

        if (settings.RejectHighSigma is { } high && !(high > 0 && float.IsFinite(high)))
        {
            return "rejectHighSigma is more than 0; leave it out for the rejector's own";
        }

        if (!(settings.HotPixelSigma >= 0 && float.IsFinite(settings.HotPixelSigma)))
        {
            return "hotPixelSigma is 0 or more; 0 masks no pixel";
        }

        if (!(settings.DrizzlePixfrac >= 0 && settings.DrizzlePixfrac <= 1))
        {
            return "drizzlePixfrac is a drop's size, 0 to 1";
        }

        if (settings.DrizzleMinFrames < 1)
        {
            return "drizzleMinFrames is 1 or more";
        }

        // A setting that would change a step's key and none of its pixels is refused, where the verb warns and goes on:
        // a drizzle's settings where no drizzle can run, under a stated strategy that is none or with the choice of
        // one switched off.
        var noDrizzle = IgnoresDrizzle(settings) || settings is { Strategy: null, BayerDrizzle: false };
        if (noDrizzle && (settings.DrizzlePixfrac != Defaults.DrizzlePixfrac || settings.DrizzleMinFrames != Defaults.DrizzleMinFrames))
        {
            return "drizzlePixfrac and drizzleMinFrames are a drizzle's, and these settings run none: there is nothing they change";
        }

        return settings is { BayerDrizzle: false, Strategy: IntegrationStrategyKind.BayerDrizzle or IntegrationStrategyKind.TilePipelinedDrizzle }
            ? $"bayerDrizzle off keeps the stack from choosing a drizzle, and strategy {settings.Strategy} is one: say one of the two"
            : null;
    }

    /// <summary>True where the strategy is stated and is no drizzle, so the two drizzle settings change nothing.</summary>
    public static bool IgnoresDrizzle(StackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Strategy is { } forced
            && forced is not (IntegrationStrategyKind.BayerDrizzle or IntegrationStrategyKind.TilePipelinedDrizzle);
    }

    /// <summary>
    /// The pipeline's options for <paramref name="settings"/>: what a step runs with, and what the verb starts from
    /// before it adds what only it does (a rendition, an enhance, a comet run, this computer's clear nights).
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="dataRoot">The folder of frames.</param>
    /// <param name="outputDir">The folder the masters are written into.</param>
    public static StackingOptions OptionsOf(StackSettings settings, string dataRoot, string outputDir)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new StackingOptions(
            DataRoot: dataRoot,
            OutputDir: outputDir,
            GroupFilter: settings.GroupFilter ?? string.Empty,
            GroupExclude: settings.GroupExclude ?? string.Empty,
            ForcedStrategy: settings.Strategy,
            CentroidDebayerAlg: settings.CentroidDebayer,
            StackDebayerAlg: settings.StackDebayer,
            SnrMin: settings.SnrMin,
            MinStars: settings.MinStars,
            QuadStars: settings.QuadStars,
            // The drizzle's options wherever a drizzle could be chosen: under a stated strategy that is none they are
            // left out, as the verb leaves them out.
            DrizzleOptions: IgnoresDrizzle(settings) ? null : new DrizzleOptions(Pixfrac: settings.DrizzlePixfrac, MinFrameCount: settings.DrizzleMinFrames),
            SplitByPierSide: settings.SplitByPierSide,
            RequireGainMatch: settings.RequireGainMatch,
            HotPixelSigma: settings.HotPixelSigma,
            QualityRejectSigma: settings.QualityRejectSigma,
            RejectLowSigma: settings.RejectLowSigma,
            RejectHighSigma: settings.RejectHighSigma,
            ReferenceFrameHint: settings.ReferenceFrame,
            DisableBayerDrizzle: !settings.BayerDrizzle,
            IncludeIntegrations: settings.IncludeIntegrations,
            // No picture of a master: a result is shown by what shows a result.
            RenderOutputs: MasterRenderOutputs.None,
            LightGroupTemperatureToleranceC: settings.GroupTemperatureTolerance,
            WarpInterpolation: settings.WarpInterpolation,
            IncludeSuspectedCloud: settings.IncludeMaybeCloud,
            RejectCloud: settings.RejectCloud,
            AlignColours: settings.ColourAlign)
        {
            // Never the clear nights this computer keeps: a step follows from its frames and its settings.
            ClearNightReferences = false,
            // The strategy by the frames alone, never by this computer's free memory.
            NominalProbe = true,
            // No master is solved: a solve is a step of its own (the line's --no-plate-solve).
            SolveMaster = false,
        };
    }

    /// <summary>What a scan found and left out, in the words the verb prints.</summary>
    public static string ScanLine(ScanSummary scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        // Always the scanned count; then what was dropped, so a product read again as a light is never invisible.
        var summary = $"scanned: {scan.FramesScanned} FITS";
        if (scan.ProductsSkipped > 0)
        {
            summary += $", ignored {scan.ProductsSkipped} TianWen product(s) (pass --include-integrations to keep)";
        }

        if (scan.RejectionMapsSkipped > 0)
        {
            summary += $", ignored {scan.RejectionMapsSkipped} rejection map(s)";
        }

        if (scan.SuspectedCloudSkipped > 0)
        {
            summary += $", left out {scan.SuspectedCloudSkipped} suspected-cloud frame(s) named MAYBE_CLOUD_ (pass --include-maybe-cloud to keep)";
        }

        if (scan.ProductsKept > 0)
        {
            summary += $", kept {scan.ProductsKept} integration(s) as input";
        }

        return summary;
    }

    /// <summary>What became of a group, in the words the verb prints.</summary>
    public static string GroupLine(GroupResult group)
    {
        ArgumentNullException.ThrowIfNull(group);
        // The strategy is named as --strategy takes it, with where it came from: an unstated one says what it resolved to.
        var strategy = group.Strategy is { } kind ? $"integrated as {kind} ({group.StrategyFrom}), " : string.Empty;
        return string.IsNullOrEmpty(group.SkipReason)
            ? $"{group.GroupSlug}: {group.FramesMatched}/{group.FramesAttempted} matched, {strategy}wrote {Path.GetFileName(group.MasterFitsPath)} in {group.Elapsed.TotalSeconds:F1}s"
            : $"{group.GroupSlug}: SKIPPED ({group.SkipReason})";
    }

    /// <inheritdoc/>
    public override ImmutableArray<string> CommandLine(StackSettings settings, IReadOnlyList<string> inputs, string output)
        => LineOf(settings, inputs[0], output).ToImmutable();

    /// <summary>
    /// The <c>tianwen stack</c> line that stacks <paramref name="frames"/> into <paramref name="output"/> as
    /// <paramref name="settings"/> say: the step's own, and the start of the comet stack's, which adds its options after.
    /// </summary>
    public static ImmutableArray<string>.Builder LineOf(StackSettings settings, string frames, string output)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var line = ImmutableArray.CreateBuilder<string>();
        // No plate solve (a solve is a step of its own), no picture, never this computer's clear nights and never
        // this computer's free memory in the choice of a strategy.
        line.AddRange(StepCommandLine.Program, "stack", frames, "--output", output, "--no-plate-solve", "--output-format", "none",
            "--no-clear-night-references", "--nominal-host");

        // A setting is on the line wherever it is not the verb's own default, so the line replays what the step ran.
        if (settings.GroupFilter is { } filter)
        {
            line.AddRange("--group-filter", filter);
        }

        if (settings.GroupExclude is { } exclude)
        {
            line.AddRange("--group-exclude", exclude);
        }

        if (settings.GroupTemperatureTolerance != Defaults.GroupTemperatureTolerance)
        {
            line.AddRange("--group-temp-tolerance", StepCommandLine.Number(settings.GroupTemperatureTolerance));
        }

        if (settings.SplitByPierSide)
        {
            line.Add("--split-by-pierside");
        }

        if (settings.IncludeMaybeCloud)
        {
            line.Add("--include-maybe-cloud");
        }

        if (settings.IncludeIntegrations)
        {
            line.Add("--include-integrations");
        }

        if (settings.QualityRejectSigma != Defaults.QualityRejectSigma)
        {
            line.AddRange("--quality-reject-sigma", StepCommandLine.Number(settings.QualityRejectSigma));
        }

        if (settings.RejectCloud != Defaults.RejectCloud)
        {
            line.AddRange("--reject-cloud", Word(settings.RejectCloud));
        }

        if (settings.RequireGainMatch != Defaults.RequireGainMatch)
        {
            line.AddRange("--require-gain-match", Word(settings.RequireGainMatch));
        }

        if (settings.CentroidDebayer != Defaults.CentroidDebayer)
        {
            line.AddRange("--centroid-debayer", Word(settings.CentroidDebayer));
        }

        if (settings.SnrMin != Defaults.SnrMin)
        {
            line.AddRange("--snr-min", StepCommandLine.Number(settings.SnrMin));
        }

        if (settings.MinStars != Defaults.MinStars)
        {
            line.AddRange("--min-stars", StepCommandLine.Number(settings.MinStars));
        }

        if (settings.QuadStars != Defaults.QuadStars)
        {
            line.AddRange("--quad-stars", StepCommandLine.Number(settings.QuadStars));
        }

        if (settings.ReferenceFrame is { } reference)
        {
            line.AddRange("--reference-frame", reference);
        }

        if (settings.WarpInterpolation != Defaults.WarpInterpolation)
        {
            line.AddRange("--warp-interpolation", Word(settings.WarpInterpolation));
        }

        if (!settings.ColourAlign)
        {
            line.Add("--no-colour-align");
        }

        if (settings.Strategy is { } strategy)
        {
            line.AddRange("--strategy", Word(strategy));
        }

        if (settings.StackDebayer != Defaults.StackDebayer)
        {
            line.AddRange("--stack-debayer", Word(settings.StackDebayer));
        }

        if (settings.RejectLowSigma is { } low)
        {
            line.AddRange("--reject-low-sigma", StepCommandLine.Number(low));
        }

        if (settings.RejectHighSigma is { } high)
        {
            line.AddRange("--reject-high-sigma", StepCommandLine.Number(high));
        }

        if (settings.HotPixelSigma != Defaults.HotPixelSigma)
        {
            line.AddRange("--hot-pixel-sigma", StepCommandLine.Number(settings.HotPixelSigma));
        }

        if (settings.DrizzlePixfrac != Defaults.DrizzlePixfrac)
        {
            line.AddRange("--drizzle-pixfrac", StepCommandLine.Number(settings.DrizzlePixfrac));
        }

        if (settings.DrizzleMinFrames != Defaults.DrizzleMinFrames)
        {
            line.AddRange("--drizzle-min-frames", StepCommandLine.Number(settings.DrizzleMinFrames));
        }

        if (!settings.BayerDrizzle)
        {
            line.Add("--no-bayer-drizzle");
        }

        return line;
    }

    // An option's value as a word: an enum's name or a switch's state, in lower case as a person types it.
    internal static string Word<T>(T value)
        where T : struct
        => value.ToString()?.ToLowerInvariant() ?? string.Empty;

    /// <inheritdoc/>
    public override ValueTask<NamedResults> RunIntoAsync(StackSettings settings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        CancellationToken cancellationToken)
        => RunIntoAsync(settings, inputs, folder, say, progress: null, cancellationToken);

    /// <summary>The verb's routine, with the pipeline's progress turned into one fraction in [0, 1] (<see cref="FractionReports"/>).</summary>
    public override async ValueTask<NamedResults> RunIntoAsync(StackSettings settings, IReadOnlyList<OperationInput> inputs, string folder, Action<string>? say,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (inputs[0].Path is not { } frames || !Directory.Exists(frames))
        {
            return NamedResults.Refused(OperationOutcome.BadInput($"'{Id}' takes a folder of frames, and {inputs[0].Name} is none"));
        }

        ILogger pipelineLogger = logger is null ? NullLogger.Instance : logger;
        var pipeline = new StackingPipeline(
            OptionsOf(settings, frames, folder),
            pipelineLogger,
            // No plate solve: a solve is a step of its own.
            catalogDb: null,
            progress: say is null && progress is null ? null : new FractionReports(say is null ? null : new ScanReports(say), progress),
            // No data folder: a step never reads the clear nights this computer keeps.
            external: null);

        var results = ImmutableArray.CreateBuilder<NamedResult>();
        var skipped = new List<string>();
        try
        {
            await foreach (var group in pipeline.RunAsync(cancellationToken))
            {
                say?.Invoke(GroupLine(group));
                if (group.MasterFitsPath is not { } master || !string.IsNullOrEmpty(group.SkipReason))
                {
                    skipped.Add($"{group.GroupSlug} ({group.SkipReason})");
                    continue;
                }

                results.Add(new NamedResult(group.GroupSlug, master, BesideOf(master)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A set that cannot be stacked is this step's refusal and not the end of the run; what the stack said is
            // the reason, and its stack trace goes to the log.
            pipelineLogger.LogError(ex, "The stack of {Frames} failed", frames);
            return NamedResults.Refused(OperationOutcome.Failed($"the stack of {inputs[0].Name} failed: {ex.Message}"));
        }

        if (results.Count == 0)
        {
            return NamedResults.Refused(OperationOutcome.BadInput(skipped.Count == 0
                ? $"{inputs[0].Name} holds no group of lights to stack"
                : $"no group of {inputs[0].Name} gave a master: {string.Join("; ", skipped)}"));
        }

        return new NamedResults(OperationOutcome.Done, results.ToImmutable());
    }

    // What belongs beside a master and is found by its name: its map sidecars and its manifest, where the run wrote them.
    internal static ImmutableArray<string> BesideOf(string master)
    {
        var beside = ImmutableArray.CreateBuilder<string>();
        beside.AddRange(IntegrationFitsWriter.ExistingSidecarsOf(master));
        if (StackManifest.PathFor(master) is var manifest && File.Exists(manifest))
        {
            beside.Add(manifest);
        }

        return beside.ToImmutable();
    }

    /// <summary>
    /// The pipeline's phases as one fraction in [0, 1] that never goes down: each phase has a share, and an item-paced
    /// phase fills its share as its items finish. A night with several groups runs the phases again for each, so a
    /// later group's early phase is held at what was reported already. Also passes the scan line on. Ticks come from
    /// many threads; the fraction is kept with an interlocked maximum.
    /// </summary>
    internal sealed class FractionReports(IProgress<StackingProgress>? inner, IProgress<double>? fraction) : IProgress<StackingProgress>
    {
        private long _best;

        public void Report(StackingProgress value)
        {
            inner?.Report(value);
            if (fraction is null)
            {
                return;
            }

            var (from, span) = value.Phase switch
            {
                StackingPhase.Scanning => (0.0, 0.0),
                StackingPhase.BuildingMasters => (0.0, 0.1),
                StackingPhase.Registering => (0.1, 0.4),
                StackingPhase.Integrating => (0.5, 0.4),
                _ => (0.95, 0.0),
            };
            var within = value.TotalItems > 0 ? Math.Clamp(value.CompletedItems / (double)value.TotalItems, 0, 1) : 0.0;
            var next = Math.Clamp(from + span * within, 0, 1);
            // The fraction as an integer of millionths, so an interlocked maximum keeps it from going back.
            var scaled = (long)(next * 1_000_000);
            long seen;
            do
            {
                seen = Interlocked.Read(ref _best);
                if (scaled <= seen)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _best, scaled, seen) != seen);

            fraction.Report(scaled / 1_000_000.0);
        }
    }

    // The scan's summary alone, said where it is reported: the one report that comes once and from the run's own
    // flow. A frame's and a strip's ticks come from many threads, and a step's lines are no place for them.
    internal sealed class ScanReports(Action<string> say) : IProgress<StackingProgress>
    {
        public void Report(StackingProgress value)
        {
            if (value is { Phase: StackingPhase.Scanning, Scan: { } scan })
            {
                say(ScanLine(scan));
            }
        }
    }
}
