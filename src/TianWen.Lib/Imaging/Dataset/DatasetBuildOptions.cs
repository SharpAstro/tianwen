using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Options for the training-dataset builder (<c>tianwen dataset build</c>). See
/// docs/plans/ai-denoise-deconv.md §2.4. Contract: NOTHING here defaults to a
/// machine-specific value: archive locations are required parameters supplied by the
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
    /// Default excludes simulator cameras (synthetic frames would poison the noise model, 
    /// a real N.I.N.A. "Camera V3 simulator" session was found in the reference archive).</summary>
    public string ExcludeInstrumePattern { get; init; } = "*simulator*";

    /// <summary>Case-insensitive wildcard on OBJECT; matching lights are excluded. Empty (the
    /// default) disables the gate. Sessions are grouped by target (see
    /// <see cref="ImagingSession.Target"/>), so this drops one target cleanly even when it shares
    /// a dated LIGHT folder with other pointings, e.g. <c>*vela*</c> removes the Vela SNR frames
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

    /// <summary>
    /// Sigma above the master dark's own background at which a pixel is masked out of drizzle
    /// deposition (<see cref="Calibration.BadPixelDetection.BuildMaskFromDark"/>), matching the
    /// stacker's <c>--hot-pixel-sigma</c>. 0 disables. Default 8.
    /// <para><b>Not redundant with dark subtraction</b>, which is the assumption that let hot
    /// pixels into 45 of 64 drizzled masters. <see cref="Calibration.Calibrator.Apply"/> subtracts
    /// the dark UNSCALED, so a dark differing in exposure or sensor temperature leaves a residual
    /// exactly at the hot pixels; and many hot pixels are non-linear or telegraph-noise unstable,
    /// so no scaling would remove them anyway. Drizzle then has no rejection of its own
    /// (<c>DrizzleStrategy</c> says so explicitly), which leaves the mask as the only defence.</para>
    /// <para><b>A ceiling, not the threshold.</b> Sigma multiplies a quantized MAD and so is not
    /// portable between darks: 8 recovered 32.95% of one ASI533's consensus defect set from its
    /// gain-121 master dark and 74.77% from its gain-252 one. The detector walks this value DOWN to
    /// a defect budget, which brings both to 86-89%, so raising it does not tighten the mask the
    /// way it reads.</para>
    /// </summary>
    public float HotPixelSigma { get; init; } = 8f;

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
    /// correlates between the two subs and violates the noise-independence assumption, so it is not
    /// a valid training sample. Drops e.g. a camera with no matching dark library in the archive (a
    /// Newtonian rig whose darks were never shot). A resolved dark that is only an imperfect match
    /// (wrong gain, or a shorter exposure than the light) still counts as calibrated; this gate is
    /// about the presence of a dark, not its quality. Default false (preserve the prior
    /// register-everything behaviour + existing tests).</summary>
    public bool RequireDarkCalibration { get; init; } = false;

    /// <summary>When true, a dark whose gain is KNOWN and differs from the session's lights is
    /// rejected outright (not merely score-penalised), so a wrong-gain dark is never silently
    /// substituted: the fixed-pattern amplitude a dark subtracts is gain-dependent, so a
    /// mismatched-gain dark mis-scales it and weakens N2N validity. An unknown gain on either side
    /// stays a wildcard (a header-less library is not dropped). Pairs naturally with
    /// <see cref="RequireDarkCalibration"/>: strict-gain narrows the candidates, require-dark then
    /// skips a session left with none. Flats are unaffected (flat division normalises gain away).
    /// Default false.</summary>
    public bool RequireGainMatch { get; init; } = false;

    /// <summary>Maximum |dark - light| sensor temperature, in degrees C, for a dark to be a
    /// candidate at all. Null (default) keeps the prior behaviour, where temperature only weights
    /// the score and there is no cutoff.
    ///
    /// <para><b>Why a score is not enough.</b> Dark current roughly doubles per 6 C, so a dark shot
    /// far from the light's temperature under-subtracts by a large factor: 17 C off is about 7x.
    /// Every other axis (exposure, dimensions, camera, and with <see cref="RequireGainMatch"/> the
    /// gain) is a hard gate, so a same-gain, same-exposure dark 17 C too cold passes every gate and
    /// then wins on score, because it is the ONLY candidate. It is then recorded as calibrated. The
    /// residual fixed pattern that leaves behind is <b>correlated between both subs of an N2N
    /// pair</b>, which is precisely the independence the training objective assumes, so the sample
    /// is worse than useless: the model learns to reproduce the residual rather than average it
    /// away.</para>
    ///
    /// <para>Pairs with <see cref="RequireDarkCalibration"/> exactly as
    /// <see cref="RequireGainMatch"/> does: this narrows the candidates, require-dark then skips a
    /// session left with none. A frame with no temperature header on either side stays a wildcard,
    /// so a header-less library is not silently dropped.</para>
    ///
    /// <para>Portable default is null rather than a number: the right tolerance is a property of
    /// the sensor's dark current, not of this tool. The operator's runner picks it (the reference
    /// archive uses 1 C for the ASI533 and 3 C for the SV605CC).</para></summary>
    public double? MaxDarkTemperatureDelta { get; init; }

    /// <summary>Case-insensitive wildcard on the SWCREATE header; when non-empty, only LIGHTS whose
    /// creating software matches are kept (e.g. <c>*N.I.N.A.*</c> to exclude SharpCap planetary/EAA
    /// captures that carry Light-like headers but were never meant for deep-sky training).
    /// <b>Applies to lights only</b>: calibration frames are matched by sensor/optics headers
    /// regardless of authoring software, so a master dark authored by any tool still resolves. Empty
    /// (the default) disables the gate.</summary>
    public string SoftwareIncludePattern { get; init; } = "";

    /// <summary>When true, a stopped run continues where it left off: the existing manifest is the
    /// checkpoint (kept, not regenerated), and any session whose tiles are already listed in it is
    /// skipped wholesale: a session's rows are appended in one block as the LAST step of its
    /// export, so "rows present" means "fully exported". The session a stop interrupted mid-export
    /// has no rows and re-runs cleanly (deterministic tile names overwrite its partial files).
    /// Assumes the SAME archive roots and gates as the interrupted run; changed options make the
    /// checkpoint's session set stale. A skip additionally requires the tiles to still be PRESENT on
    /// disk (count matching the manifest); the manifest is a claim about the past, not proof, and a
    /// session whose tiles went missing is re-registered rather than silently counted. The PSF/noise
    /// report accumulates across runs via <see cref="DatasetPsfStore"/>, so a resumed run no longer
    /// narrows it; a session with tiles but no stored PSF record needs
    /// <see cref="RegenPsfForExportedSessions"/>. Default false (fresh manifest, prior behaviour).</summary>
    public bool Resume { get; init; } = false;

    /// <summary>
    /// When true, a session whose tiles are already exported but which has NO record in
    /// <see cref="DatasetPsfStore"/> is re-registered so its PSF/noise stats can be measured, and its
    /// tiles are left exactly as they are (no re-export, no manifest rows).
    ///
    /// <para>Opt-in because it is expensive and the cost is invisible from the outside: the
    /// field-radius profile is measured on the session MASTER, which only exists as the output of
    /// register + integrate, so recovering it costs the same as building the session in the first
    /// place. A plain resume must stay a fast skip, so it reports how many sessions are missing PSF
    /// records and leaves the choice to the caller. Default false.</para>
    /// </summary>
    public bool RegenPsfForExportedSessions { get; init; } = false;

    /// <summary>
    /// When true, each integrated session master is written to
    /// <c>&lt;out&gt;/session-masters/&lt;sessionSlug&gt;.fits</c> and kept.
    ///
    /// <para><b>The master is the only perishable artifact a run produces.</b> Scratch is wiped per
    /// session, so afterwards the master exists nowhere, and every statistic measured ON it (the
    /// field-radius PSF profile, which is the input to the deconvolver's position-varying sweep) can
    /// then only be recovered by registering the whole session again. That has cost two full 7h16m
    /// re-runs in two days: once for a star-detection fix, once for an FWHM estimator fix. Neither
    /// re-derived anything that needed the SUBS; both needed only the master, which the run had
    /// already built and thrown away.</para>
    ///
    /// <para>Costs about 108 MB per session (union canvas, RGB float32), so roughly 5.4 GB for a
    /// 50-session archive, against re-registration at ~9 minutes per session. Defaults to
    /// <c>true</c>: the asymmetry is stark enough that keeping it is the sane default, and a caller
    /// that genuinely cannot spare the disk can turn it off deliberately.</para>
    ///
    /// <para><b>What this does NOT cover:</b> the per-sub PSF distribution
    /// (<c>SessionPsf.SubFwhm</c>) comes from the analysis pass over every sub, not from the master,
    /// so re-deriving that half still needs a calibrate + detect sweep of the subs. Much cheaper than
    /// register + integrate, but not free. Retaining masters makes the field-radius half nearly free
    /// and leaves the per-sub half a separate question.</para>
    /// </summary>
    public bool RetainSessionMasters { get; init; } = true;

    /// <summary>
    /// Parent directory for the per-session warped-sub scratch. Empty (the default) puts it at
    /// <c>&lt;out&gt;/_scratch</c>, beside the tiles; otherwise scratch lands in
    /// <c>&lt;ScratchRoot&gt;/_scratch</c>. Only ever that subdirectory is created and deleted, so
    /// pointing this at an existing directory cannot take the directory itself with it.
    ///
    /// <para><b>Why this is worth a knob: scratch is the build's dominant I/O by a wide margin, and
    /// it is pure churn.</b> Registration warps every sub onto the union canvas and persists it as a
    /// float32 FITS (~117 MB each on a 3250x3060x3 canvas), the integrator reads them all back, and
    /// the lot is deleted when the session ends. A 200-sub session therefore writes and re-reads
    /// roughly 47 GB that nothing outside that session will ever look at.</para>
    ///
    /// <para>Measured on this dev box 2026-08-11, mid-run: the archive and output live on a USB
    /// <b>hard disk</b> sustaining ~37 MB/s with a disk queue length of 1.67 (saturated, requests
    /// waiting), while the process used <b>12% of a 16-core CPU</b>. The build was entirely I/O-bound
    /// on spinning rust, at ~11 minutes per session. Pointing scratch at an NVMe volume moves the
    /// churn off the slow disk and leaves only the archive reads and the tile writes there. Size the
    /// target for the largest single session, not the archive: peak is subs x canvas bytes, so allow
    /// ~40 GB for a 300-sub session.</para>
    ///
    /// <para>Confirmed the same day by sampling both disks through one session with scratch on NVMe:
    /// the warp phase wrote at <b>400-564 MB/s</b> (against the 37 MB/s the same writes got on the
    /// hard disk) with the slow disk completely idle, the integrator then read scratch back at
    /// ~300 MB/s, and the slow disk went busy again only for the next session's raw lights at
    /// 22-38 MB/s. <b>So the remaining slow-disk traffic is the archive reads, which is irreducible:
    /// the lights live there.</b> Do not read a busy archive disk as evidence this setting is not
    /// working; check that the scratch volume is the one taking the write burst.</para>
    /// </summary>
    public string ScratchRoot { get; init; } = "";

    /// <summary>
    /// Re-measure the PSF/noise record for exported sessions <b>even when one already exists</b>,
    /// replacing it. Tiles are left untouched.
    ///
    /// <para>Distinct from <see cref="RegenPsfForExportedSessions"/>, which fills GAPS: that one only
    /// touches sessions the report does not cover, so it is idempotent, converges, and costs nothing
    /// on a second run. It cannot express the case this exists for, because the sessions whose
    /// records are wrong are precisely the ones that HAVE a record.</para>
    ///
    /// <para><b>Use it when the measurement itself changed</b>, which is the only time it is correct:
    /// a new FWHM estimator, a different star-detection path, a changed radius binning. It costs a
    /// full re-registration of every exported session, so it is never implied by anything and
    /// defaults off. Before this existed the only way to force a re-measure was to delete
    /// <c>stats/psf-sessions.jsonl</c> by hand, which also threw away the records of sessions that
    /// were still fine.</para>
    ///
    /// <para>Safe to append rather than rewrite: <c>DatasetPsfStore</c> is last-wins by session id by
    /// design, so a re-measure adds a line and the earlier record stays readable for comparison.</para>
    /// </summary>
    public bool ForcePsfRemeasure { get; init; }

    /// <summary>
    /// Re-render the PSF/noise report from <see cref="DatasetPsfStore"/> and stop. No archive scan,
    /// no registration, no export; nothing is measured and no tile is touched.
    ///
    /// <para><b>Why it has to skip discovery.</b> A normal run filters the report to the sessions
    /// the current scan found, which is why it must walk every FITS header first -- ~19k of them on
    /// a USB spindle, seek-bound and the dominant cost of a re-run. Report-only takes its session
    /// set from the tile manifest instead: that is the record of what was actually exported into
    /// this output directory, which is exactly what the report should describe, and reading it is
    /// one sequential pass over a single file.</para>
    ///
    /// <para>It exists because the report is derived state with inputs that legitimately change
    /// without the measurements changing -- a telescope alias (<see cref="TelescopeAliases"/>), a
    /// rendering fix, a re-tuned bin count. Before this, correcting any of those cost a full
    /// archive scan, so "just re-render the report" was true in principle and expensive in
    /// practice.</para>
    ///
    /// <para>Sessions with tiles but no stored PSF record are reported as missing exactly as a
    /// normal run reports them; report-only cannot fix that, because measuring needs the master.</para>
    /// </summary>
    public bool ReportOnly { get; init; }
}
