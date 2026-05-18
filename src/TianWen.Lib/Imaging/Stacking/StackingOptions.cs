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
    bool SplitByPierSide = false);
