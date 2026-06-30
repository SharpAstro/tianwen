using System;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Inputs to <see cref="StackingPipeline.RunAsync"/>. Defaults match the
/// constants used by the original end-to-end manual test against the SoL
/// dataset; the CLI exposes overrides for the knobs that practically vary
/// between sessions (which strategy to force, which groups to include /
/// exclude, debayer algorithms per pass).
/// </summary>
/// <param name="DataRoot">Recursively scanned for FITS files. Anything under
/// <paramref name="OutputDir"/> is filtered out so previous-run masters
/// aren't re-ingested as lights.</param>
/// <param name="OutputDir">Where <c>master_*.fits</c>, <c>master_*.png</c>,
/// <c>master_*_autocrop.fits</c>, the per-run audit log
/// <c>stack-run.log</c>, the <c>masters/</c> cache, and the per-group
/// <c>_staging/</c> scratch live.</param>
/// <param name="GroupFilter">Substring of <see cref="LightGroupKey.Slug"/>
/// to keep. Empty matches all.</param>
/// <param name="GroupExclude">Substring of <see cref="LightGroupKey.Slug"/>
/// to drop. Empty drops nothing. Applied before <paramref name="GroupFilter"/>.</param>
/// <param name="ForcedStrategy">If set, <see cref="IntegrationStrategySelector"/>
/// uses this as its preferred kind instead of letting the cost model pick.
/// Production runs leave this null; useful for benchmarking + bug repros.</param>
/// <param name="CentroidDebayerAlg">Debayer algorithm for the registration
/// pass (star-detection accuracy matters; speed second).</param>
/// <param name="StackDebayerAlg">Debayer algorithm for the integration pass
/// (color fidelity matters; per-frame cost stacks up).</param>
/// <param name="SnrMin">FindStarsAsync SNR floor.</param>
/// <param name="MinStars">FindStarsAsync retry floor -- forces a second
/// pass at a lower detection level when the first returns fewer than this.</param>
/// <param name="QuadStars">Top-K brightest stars used for quad fingerprints
/// in <see cref="Registrator"/>'s match step.</param>
/// <param name="DrizzleOptions">Knobs for <see cref="DrizzleStrategy"/> when
/// <paramref name="ForcedStrategy"/> is <see cref="IntegrationStrategyKind.BayerDrizzle"/>.
/// Null falls back to <see cref="Stacking.DrizzleOptions"/> defaults
/// (Pixfrac=1.0, OutputScale=10, MinFrameCount=60). Ignored unless drizzle
/// is the chosen strategy.</param>
/// <param name="SplitByPierSide">When true, sub-partition each light group
/// by the per-frame FITS <c>PIERSIDE</c> header so pre-meridian-flip and
/// post-flip frames integrate into separate masters. Useful for diagnostics
/// (mirrored optical aberrations + flipped Bayer offsets across the flip
/// can produce drizzle streaks that disappear when each side is integrated
/// alone) and as a workaround for capture software that doesn't update
/// BayerOffsetX/Y post-flip. Frames missing PIERSIDE land in their own
/// "pierUnknown" sub-group rather than being silently merged with East. Off
/// by default -- a unified master across the meridian is the production
/// norm.</param>
/// <param name="HotPixelSigma">Threshold (in Gaussian sigmas above the dark
/// master's per-channel median) for flagging hot pixels. The flagged
/// positions get NaN'd in the calibrated light frames so downstream
/// integration ignores them entirely -- dark subtraction alone removes
/// the average per-pixel offset but leaves per-frame shot-noise variance
/// from genuinely-hot pixels, which then survives into the master (most
/// visible as single bright pixels in drizzle output where there is no
/// per-cell averaging). Default 8 is conservative; hot pixels typically
/// score 100+ sigma. Pass 0 to disable masking (legacy behaviour).
/// Ignored when no dark master is matched to the light group.</param>
/// <param name="QualityRejectSigma">When set, runs the post-registration
/// frame quality filter at this sigma threshold: a frame is dropped from
/// integration if its median HFD or ellipticity exceeds
/// <c>median + sigma · 1.4826 · MAD</c> of the session's matched-frame
/// distribution. An 80% keep floor caps rejection at the worst 20% by
/// severity when the MAD threshold would over-cut. Null disables the
/// filter (default; preserves the pre-this-feature behaviour). 3.0 is the
/// recommended starting value -- conservative, catches clear outliers
/// (low-altitude bloated frames, wind-trailed frames) without biting
/// into the body of the distribution. See <see cref="FrameQualityFilter"/>.</param>
/// <param name="ReferenceFrameHint">Debug knob: when set, pins the
/// reference frame for each light group to the first candidate whose
/// path contains this case-insensitive substring. Null falls back to
/// the composite-quality score reference picker (the production
/// default). Use to isolate per-frame Bayer-drizzle artifacts that
/// correlate with reference choice -- pinning to a frame near the
/// temporal MIDDLE of a session makes per-frame rotation residuals
/// symmetric around zero, balancing per-channel drizzle coverage. Off
/// by default.</param>
/// <param name="IncludeIntegrations">When true, the scan keeps frames with a
/// non-zero FITS <c>STACK_N</c> header (integrated masters from a previous
/// run) and feeds them into the next-stage grouping. Two-stage mosaic
/// stacking is the canonical use case: each panel is integrated separately,
/// then the resulting masters are re-stacked as the panel-aligned final
/// mosaic. Off by default -- stale masters in adjacent <c>output-*/</c> dirs
/// from prior runs would otherwise pollute the next session's lights. The
/// <c>.rejection.fits</c> sidecar is ALWAYS dropped regardless of this flag:
/// it's a per-pixel rejection-fraction map, not an image suitable for
/// further integration.</param>
/// <param name="DisableBayerDrizzle">Opt out of drizzle auto-selection.
/// Drizzle (both <see cref="IntegrationStrategyKind.BayerDrizzle"/> and
/// <see cref="IntegrationStrategyKind.TilePipelinedDrizzle"/>) is
/// auto-pickable when the sensor is RGGB and the matched-frame count
/// meets the minimum coverage threshold (default 60). The 3-5x wall-
/// time advantage usually beats the standard path under the Balanced
/// ranking policy on RGGB input. Set this when you want the AHD-
/// debayered + warp-rejected master instead -- e.g. for SPCC
/// validation against a reference master, or when you specifically
/// want the standard rejection kernel's outlier handling rather than
/// drizzle's per-cell coverage map. <see cref="ForcedStrategy"/>
/// overrides this either way (forcing BayerDrizzle still routes to
/// the drizzle path even when this flag is true).</param>
public sealed record StackingOptions(
    string DataRoot,
    string OutputDir,
    string GroupFilter = "",
    string GroupExclude = "",
    IntegrationStrategyKind? ForcedStrategy = null,
    DebayerAlgorithm CentroidDebayerAlg = DebayerAlgorithm.VNG,
    DebayerAlgorithm StackDebayerAlg = DebayerAlgorithm.AHD,
    float SnrMin = 5f,
    int MinStars = 2000,
    int QuadStars = 500,
    DrizzleOptions? DrizzleOptions = null,
    bool SplitByPierSide = false,
    float HotPixelSigma = 8.0f,
    float? QualityRejectSigma = null,
    string? ReferenceFrameHint = null,
    bool DisableBayerDrizzle = false,
    bool IncludeIntegrations = false,
    bool Enhance = false,
    float EnhanceBlend = 1.0f,
    bool SplitPlates = false,
    bool RenderPreviewPng = true);
