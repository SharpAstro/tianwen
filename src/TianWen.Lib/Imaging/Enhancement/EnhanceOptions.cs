namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Which enhancer backend to use for the RC-servable roles (star removal, deblur,
/// non-stellar deconvolution, denoise). The SAS-only roles (stellar sharpen, gradient
/// correction) ignore this -- they have no RC-Astro equivalent.
/// </summary>
public enum EnhanceBackend
{
    /// <summary>RC-Astro when the CLI is present AND the product is licensed; otherwise
    /// the SAS ONNX fallback. The default, and the only behaviour before this option
    /// existed.</summary>
    Auto = 0,

    /// <summary>Use RC-Astro whenever the CLI binary is present, bypassing the
    /// <c>--license</c> probe (useful when the probe is flaky but the user knows the
    /// product is licensed). Falls back to SAS only when the binary is absent.</summary>
    ForceRcAstro = 1,

    /// <summary>Never use RC-Astro -- always the SAS ONNX enhancer, even when RC-Astro
    /// is present and licensed (reproducibility / speed / avoiding the subprocess).</summary>
    ForceSas = 2,
}

/// <summary>
/// Optional per-product strength overrides for the RC-Astro enhancers. Backend-agnostic
/// by design: RC-Astro maps each field to a native <c>rc-astro</c> CLI argument; the SAS
/// ONNX enhancers ignore them (they steer via the pipeline's post-hoc <c>Blend</c> lerp,
/// not native strength). A <c>null</c> field means "use the enhancer's own default",
/// which reproduces today's behaviour bit-for-bit.
/// </summary>
/// <param name="DeblurSharpen">RC BlurXTerminator non-stellar sharpen (<c>bxt --sn</c>) in
/// [0, 1]. Applies to both the full-image deblur and the starless-plate deconvolution.</param>
/// <param name="DenoiseStrength">RC NoiseXTerminator strength (<c>nxt --dn</c>) in [0, 1].
/// Overrides the noise-adaptive auto value when set.</param>
/// <param name="DenoiseIterations">RC NoiseXTerminator iterations (<c>nxt --it</c>).</param>
public sealed record EnhanceTuning(
    float? DeblurSharpen = null,
    float? DenoiseStrength = null,
    int? DenoiseIterations = null);

/// <summary>
/// Per-operation enhancement options, threaded immutably from the call site (CLI flags,
/// GUI state snapshot, or server request) through <see cref="SharpenPipeline.ProcessAsync(SharpenRequest, EnhanceOptions, System.IProgress{EnhanceProgress}, System.Threading.CancellationToken)"/>
/// to each enhancer. There is deliberately no shared mutable settings singleton -- callers
/// snapshot an immutable instance so concurrent enhances (e.g. parallel server requests)
/// can diverge without tearing.
/// </summary>
/// <param name="Backend">Backend selection for the RC-servable roles.</param>
/// <param name="Tuning">Optional RC-Astro per-product strength overrides.</param>
public sealed record EnhanceOptions(EnhanceBackend Backend = EnhanceBackend.Auto, EnhanceTuning? Tuning = null)
{
    /// <summary>Auto backend, no tuning overrides -- identical to the pre-option behaviour.</summary>
    public static readonly EnhanceOptions Default = new();
}

/// <summary>
/// One progress tick from <see cref="SharpenPipeline.ProcessAsync(SharpenRequest, EnhanceOptions, System.IProgress{EnhanceProgress}, System.Threading.CancellationToken)"/>,
/// stamped with the pipeline-owned step identity. The pipeline reports a tick at each step
/// boundary; backends that emit sub-step progress (RC-Astro via its NDJSON stream) raise
/// additional ticks with the same step identity and a rising <see cref="StepPercent"/>.
/// Mirrors the <c>StackingProgress</c> pattern.
/// </summary>
/// <param name="StepName">Human-readable step name (e.g. "denoise-starless").</param>
/// <param name="StepIndex">Zero-based index of the current step.</param>
/// <param name="StepCount">Total number of steps in the request.</param>
/// <param name="StepPercent">Progress within the current step in [0, 1]; 0 at step start.</param>
/// <param name="EtaSeconds">Estimated seconds remaining for the current step, or 0 when unknown.</param>
public sealed record EnhanceProgress(string StepName, int StepIndex, int StepCount, float StepPercent, double EtaSeconds);
