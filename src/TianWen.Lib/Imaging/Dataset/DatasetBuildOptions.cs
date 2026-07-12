using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Options for the training-dataset builder (<c>tianwen dataset build</c>). See
/// docs/plans/ai-denoise-deconv.md §2.4. Contract: NOTHING here defaults to a
/// machine-specific value — archive locations are required parameters supplied by the
/// caller, behavioural knobs carry portable defaults only.
/// </summary>
public sealed record DatasetBuildOptions
{
    /// <summary>Archive roots to scan (required, repeatable on the CLI). No default.</summary>
    public required ImmutableArray<string> ArchiveRoots { get; init; }

    /// <summary>Output root for tiles/manifest/masters-cache/stats (required). No default.</summary>
    public required string OutputDir { get; init; }

    /// <summary>Lights below this exposure are excluded (planetary/lucky bursts). Default 10 s.</summary>
    public TimeSpan MinExposure { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Lights above this exposure are excluded (live-stack products report the
    /// accumulated exposure, e.g. SharpCap AutoSave stacks at hours). Default 300 s.</summary>
    public TimeSpan MaxExposure { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Case-insensitive wildcard on INSTRUME; matching frames are excluded.
    /// Default excludes simulator cameras (synthetic frames would poison the noise model —
    /// a real N.I.N.A. "Camera V3 simulator" session was found in the reference archive).</summary>
    public string ExcludeInstrumePattern { get; init; } = "*simulator*";

    /// <summary>Case-insensitive wildcard on OBJECT; matching lights are excluded. Empty (the
    /// default) disables the gate. Sessions are grouped by target (see
    /// <see cref="ImagingSession.Target"/>), so this drops one target cleanly even when it shares
    /// a dated LIGHT folder with other pointings — e.g. <c>*vela*</c> removes the Vela SNR frames
    /// that live alongside HD 71272 + RCW 27 in one N.I.N.A. night.</summary>
    public string ExcludeObjectPattern { get; init; } = "";

    /// <summary>Case-insensitive wildcards matched against each PATH SEGMENT; a frame under a
    /// matching directory is excluded. Belt-and-braces on top of the header gates for
    /// processed-data directories whose frames still carry Light-like headers
    /// (SharpCap AutoSave live stacks, PixInsight workspaces).</summary>
    public ImmutableArray<string> ExcludePathSegments { get; init; } =
        ["autosave", "proc*", "reproc", "pixinsight", "pi_swap"];

    /// <summary>Sessions with fewer gated lights than this are skipped (too few for
    /// registration + a meaningful master). Default 10.</summary>
    public int MinSubsPerSession { get; init; } = 10;

    /// <summary>MAD threshold (standard-deviation-equivalent units) for the session-relative
    /// quality gate (<see cref="SessionFrameAnalyzer.ApplyGate"/>); the stacker's
    /// <c>--quality-reject-sigma</c> semantics. 0 disables the relative gate
    /// (zero-star frames are still rejected). Default 3.</summary>
    public float QualityRejectSigma { get; init; } = 3f;

    /// <summary>Keep-floor for the quality gate: the maximum fraction of a session's frames the
    /// gate may reject before the severity-ranked floor engages. Higher than the stacker's 0.20
    /// because dataset building favours purity over yield (there are 20k+ subs to draw from, so
    /// dropping a few good frames to keep clouded ones out is the right trade). Default 0.5.</summary>
    public float QualityMaxRejectFraction { get; init; } = 0.5f;

    /// <summary>Tile edge length in pixels. Must match the inference tiling contract
    /// (<c>ChunkedInference</c> default 256).</summary>
    public int TileSize { get; init; } = 256;

    /// <summary>Upper bound of sampled grid cells per session (structure-biased sampling).</summary>
    public int CellsPerSession { get; init; } = 300;

    /// <summary>Sub tiles exported per sampled cell (bounds dataset size; any two subs of a
    /// cell form a Noise2Noise pair).</summary>
    public int SubsPerCell { get; init; } = 8;

    /// <summary>Fraction of sessions held out as the pinned TEST split (<see cref="DatasetSplitWriter"/>).
    /// By session, never by tile. Default 0.15.</summary>
    public double TestFraction { get; init; } = 0.15;
}
