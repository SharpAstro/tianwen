using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Which enhancer backend to use for the RC-servable roles (star removal, deblur,
/// non-stellar deconvolution, denoise). Gradient correction ignores this: it has no RC-Astro
/// equivalent (GraXpert, else the classical fit).
/// </summary>
/// <remarks>
/// The values are numeric on the wire (the hosting API sends enums as numbers), so they never
/// renumber: <c>2</c> was the SETI Astro backend, removed on 2026-09-26 with the SAS tier (SETI
/// Astro's model licence of 2026-09-24 allows use only within SASpro), and stays unassigned.
/// </remarks>
public enum EnhanceBackend
{
    /// <summary>RC-Astro when the CLI is present AND the product is licensed; otherwise the in-house
    /// TianWen model where the role has one (the denoise role's N2N model, 3-channel input), and
    /// otherwise the role has no backend and the canonical program leaves it out
    /// (<see cref="SharpenPipeline.CanonicalProgram"/>). The default.</summary>
    Auto = 0,

    /// <summary>Use RC-Astro whenever the CLI binary is present, bypassing the
    /// <c>--license</c> probe (useful when the probe is flaky but the user knows the
    /// product is licensed). Falls back to the in-house model only when the binary is absent.</summary>
    ForceRcAstro = 1,

    /// <summary>Prefer the in-house TianWen model for any role that has one -- the DENOISE role (the
    /// <c>tianwen_denoise_osc_convmapb_s2</c> net) and the whole-frame DEBLUR (E3.4d's deconvolution
    /// operator, which also needs its kernel stated, <see cref="EnhanceTuning.Deconvolution"/>) -- and
    /// behave as <see cref="Auto"/> for every other role. Scoped this way because one options record
    /// threads through every step of a pipeline run, so the star remover sees this value too and
    /// must keep working. Both models are OSC-only. The deconvolver is served ONLY under this value:
    /// <see cref="Auto"/> passes it over until it can find its own kernel and decline a frame with
    /// nothing to remove (#741).</summary>
    TianWen = 3,
}

/// <summary>
/// Optional per-role strength overrides, threaded to whichever backend serves the role.
/// Backend-agnostic by design: each backend maps a field onto its own native dial --
/// RC-Astro onto an <c>rc-astro</c> CLI argument, the in-house N2N denoiser onto its blend.
/// A <c>null</c> field means "use the enhancer's own default",
/// which reproduces the un-tuned behaviour bit-for-bit.
/// </summary>
/// <param name="DeblurSharpen">Non-stellar deblur/deconvolution sharpen in [0, 1], applied
/// to both the full-image deblur and the starless-plate deconvolution. RC maps it to
/// <c>bxt --sn</c>.</param>
/// <param name="DenoiseStrength">Denoise strength in [0, 1]. RC maps it to <c>nxt --dn</c>
/// (overriding the noise-adaptive auto value); the N2N backend maps it to its blend dial.</param>
/// <param name="DenoiseIterations">Denoiser iterations; RC maps it to <c>nxt --it</c>.</param>
/// <param name="Deconvolution">The blur TianWen's own deconvolver is asked to remove (<see cref="DeconvolutionKernel"/>):
/// it takes its kernel as an input and serves only a run that states one, since nothing yet finds a kernel in a single
/// frame (#741). RC-Astro measures its own and ignores this.</param>
public sealed record EnhanceTuning(
    float? DeblurSharpen = null,
    float? DenoiseStrength = null,
    int? DenoiseIterations = null,
    DeconvolutionKernel? Deconvolution = null);

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

    /// <summary>
    /// Parses an immutable <see cref="EnhanceOptions"/> from a backend string and per-product
    /// strength overrides. The single source of truth for the <c>auto</c>/<c>rc</c>/<c>tianwen</c>
    /// mapping and the "null override =&gt; enhancer default" tuning gate, shared by the CLI
    /// (<c>image sharpen</c>, <c>stack --enhance</c>) and the server enhance endpoint so they
    /// never drift. Callers convert their own sentinels (e.g. the CLI's <c>-1</c> "unset") to a
    /// <c>null</c> before calling.
    /// </summary>
    /// <param name="backend"><c>auto</c> (<c>null</c>/empty =&gt; auto), <c>rc</c>/<c>rcastro</c>/<c>rc-astro</c>,
    /// or <c>tianwen</c> (case-insensitive). Anything else =&gt; <c>false</c> with <paramref name="error"/> set; <c>sas</c>
    /// gets its own message, because a script written before 2026-09-26 still says it.</param>
    /// <param name="deblurSharpen">RC <c>bxt --sn</c> override, or <c>null</c> for the enhancer default.</param>
    /// <param name="denoiseStrength">Denoise strength in <c>[0, 1]</c>: RC maps it to <c>nxt --dn</c>
    /// (<c>null</c> = noise-adaptive auto); the N2N backend maps it to its blend dial
    /// (<c>out = in + s*(den - in)</c>, <c>null</c> = 1.0, and 0 is rejected there -- run without the
    /// denoise step instead of asking a denoiser to do nothing).</param>
    /// <param name="denoiseIterations">RC <c>nxt --it</c> override, or <c>null</c> for the enhancer default.</param>
    /// <param name="deconvolution">The kernel TianWen's own deconvolver is asked to remove
    /// (<see cref="DeconvolutionKernel.TryParse"/>), or <c>null</c> where none was stated.</param>
    /// <param name="options">The parsed options (<see cref="Default"/> when this returns <c>false</c>).</param>
    /// <param name="error">A human-readable reason when this returns <c>false</c>; otherwise <c>null</c>.</param>
    public static bool TryParse(
        string? backend,
        float? deblurSharpen,
        float? denoiseStrength,
        int? denoiseIterations,
        DeconvolutionKernel? deconvolution,
        out EnhanceOptions options,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        EnhanceBackend parsed;
        switch ((backend ?? "auto").Trim().ToLowerInvariant())
        {
            case "" or "auto": parsed = EnhanceBackend.Auto; break;
            case "rc" or "rcastro" or "rc-astro": parsed = EnhanceBackend.ForceRcAstro; break;
            case "tianwen": parsed = EnhanceBackend.TianWen; break;
            case "sas":
                options = Default;
                error = "The SETI Astro (SAS) backend was removed on 2026-09-26: its model licence allows use only within SASpro. " +
                        "Use 'auto' (RC-Astro where licensed, else TianWen's own models), 'rc', or 'tianwen'.";
                return false;
            default:
                options = Default;
                error = $"Unknown AI backend '{backend}' (expected 'auto', 'rc', or 'tianwen'; 'n2n' was renamed 'tianwen' on 2026-10-09)";
                return false;
        }

        // A null on every override means "use each enhancer's own default", which is exactly
        // EnhanceTuning == null (no per-product steering) -- bit-identical to the pre-option path.
        var tuning = deblurSharpen.HasValue || denoiseStrength.HasValue || denoiseIterations.HasValue || deconvolution is not null
            ? new EnhanceTuning(deblurSharpen, denoiseStrength, denoiseIterations, deconvolution)
            : null;
        options = new EnhanceOptions(parsed, tuning);
        return true;
    }
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
