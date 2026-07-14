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

    /// <summary>When true, a session that resolves NO master dark is skipped rather than registered
    /// uncalibrated. An uncalibrated N2N pair shares the sensor's fixed-pattern dark signal, which
    /// correlates between the two subs and violates the noise-independence assumption — so it is not
    /// a valid training sample. Drops e.g. a camera with no matching dark library in the archive (a
    /// Newtonian rig whose darks were never shot). A resolved dark that is only an imperfect match
    /// (wrong gain, or a shorter exposure than the light) still counts as calibrated — this gate is
    /// about the presence of a dark, not its quality. Default false (preserve the prior
    /// register-everything behaviour + existing tests).</summary>
    public bool RequireDarkCalibration { get; init; } = false;

    /// <summary>When true, a dark whose gain is KNOWN and differs from the session's lights is
    /// rejected outright (not merely score-penalised), so a wrong-gain dark is never silently
    /// substituted — the fixed-pattern amplitude a dark subtracts is gain-dependent, so a
    /// mismatched-gain dark mis-scales it and weakens N2N validity. An unknown gain on either side
    /// stays a wildcard (a header-less library is not dropped). Pairs naturally with
    /// <see cref="RequireDarkCalibration"/>: strict-gain narrows the candidates, require-dark then
    /// skips a session left with none. Flats are unaffected (flat division normalises gain away).
    /// Default false.</summary>
    public bool RequireGainMatch { get; init; } = false;

    /// <summary>Case-insensitive wildcard on the SWCREATE header; when non-empty, only LIGHTS whose
    /// creating software matches are kept (e.g. <c>*N.I.N.A.*</c> to exclude SharpCap planetary/EAA
    /// captures that carry Light-like headers but were never meant for deep-sky training).
    /// <b>Applies to lights only</b> — calibration frames are matched by sensor/optics headers
    /// regardless of authoring software, so a master dark authored by any tool still resolves. Empty
    /// (the default) disables the gate.</summary>
    public string SoftwareIncludePattern { get; init; } = "";

    /// <summary>When true, a stopped run continues where it left off: the existing manifest is the
    /// checkpoint (kept, not regenerated), and any session whose tiles are already listed in it is
    /// skipped wholesale — a session's rows are appended in one block as the LAST step of its
    /// export, so "rows present" means "fully exported". The session a stop interrupted mid-export
    /// has no rows and re-runs cleanly (deterministic tile names overwrite its partial files).
    /// Assumes the SAME archive roots and gates as the interrupted run — changed options make the
    /// checkpoint's session set stale. The PSF/noise report of a resumed run covers only the
    /// sessions registered in that run. Default false (fresh manifest, prior behaviour).</summary>
    public bool Resume { get; init; } = false;
}
