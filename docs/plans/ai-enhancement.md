# PLAN: AI image enhancement, star removal, deconvolution, sharpening, denoise

> **2026-09-26: the SETI Astro tier this plan wired in was REMOVED from the product.** SETI Astro's
> model licence of 2026-09-24 (`COSMIC_CLARITY_LICENSE.txt`) allows use only within SASpro, and the
> user dropped the tier rather than seek consent. `OnnxStarRemover`, `OnnxStellarSharpener` and
> `OnnxDenoiser` are deleted, `OnnxNonStellarDeconvolver` survives as the shell TianWen's own
> deconvolver ships through (nothing registers it until then), `ModelResolver` no longer searches
> SASpro's folder, and `--ai-backend sas` fails with a message saying why. What survives is this
> plan's ARCHITECTURE: the role interfaces, the step program, chunked inference, the stretch
> round-trip. The program is now shaped by what serves (`SharpenPipeline.CanonicalProgram`), and
> without RC-Astro it is whole-frame (gradient + the in-house N2N denoiser). Everything below is the
> history of the SAS integration and its measurements, kept as written; how SAS Pro FEEDS its models
> (read from its GPL-3.0 source, never from a model file) is `model-training-roadmap.md` section 8.

## Goal

Wire SetiAstroSuite Pro's AI4 NAFNet models (and a Walking Noise denoise variant) into
TianWen as a set of composable image enhancers. The headline pipeline is **sharpen**:
remove stars → sharpen the stars-only plate → PSF-deconvolve the starless plate →
optionally recombine. Each step must be usable standalone (starless export, deconv on
a pre-starless image, etc.), so the orchestrator composes atomic `IImageEnhancer`s
rather than baking the chain into one method.

Reference implementation: SetiAstroSuite Pro (Python, `github.com/setiastro/setiastrosuitepro`,
GPL-3.0 per its own README and the GitHub API; **no local checkout exists on this machine as of
2026-09-02**, the `~/source/repos/other/setiastrosuitepro` path older notes cite is stale, so read
it from GitHub).
Models distributed under their GitHub release `setiastro/setiastrosuitepro` tag
`benchmarkFIT` (2 zip assets, ~2.5 GB extracted). Same architecture (NAFNet),
same training distribution, same input conventions; we just port the orchestration
to .NET 10 with `Microsoft.ML.OnnxRuntime`.

## Non-goals (v1)

- **Walking Noise denoiser** (`deep_denoise_*_AI4_1w.onnx`). Specialized denoise for
  uncooled-sensor walking-noise patterns. Different problem domain; pick up after
  sharpen lands.
- **Standard denoise** (`deep_denoise_*_AI4.onnx`). Useful but orthogonal to the
  star-removal/deconv pipeline. Wire after sharpen.
- **Super-resolution** (`superres_2x/3x/4x.onnx`). Not on the critical path; defer.
- **Satellite trail detection + removal** (`satelliteRemovalAI4.onnx` +
  `satellite_trail_detector_*.onnx`). Logically belongs in the stacking pipeline as a
  pre-rejection filter, not here. Cross-reference [stacking.md](stacking.md)
  when the time comes.
- **Classical (non-ONNX) fallbacks** for every enhancer except `IStarRemover`.
  Lucy-Richardson, unsharp-mask, bilateral, etc. exist and are worth having later
  but are not the v1 priority. Star removal has no respectable classical analogue;
  no fallback there.
- **Hexagon NPU acceleration** on win-arm64. AI4 ships pure FP32; QNN HTP wants
  INT8/INT16 or a pre-compiled `.serialized.bin`. Real NPU accel requires either
  upstream re-export at INT8 or our own ORT QNN compile pass; separate workstream.
- **Runtime self-bootstrap of model files**. v1 uses the dev-only fetch script
  `tools/tianwen-ai-models-fetch.ps1`. Deploy story (in-app first-launch download
  with progress UI + CLI sub-command) is a follow-up plan, **for TianWen's own
  models only** (#516, #825): SETI Astro's AI4 weights have carried a licence since
  2026-09-24 that allows use only within SASpro without the author's written
  consent, so the product never fetches them or tells a user to (decided
  2026-09-25; the script stays, as a developer tool). They are read where SASpro
  put them.

## Architecture

### Project layout

```
TianWen.Lib/Imaging/                              -- zero AI dep
├── Image.Stretch.cs                               (EXTEND: add MtfStretch / MtfUnstretch / MidtonesBalanceFor next to existing MidtonesTransferFunction / StretchValue)
└── Enhancement/
    ├── IImageEnhancer.cs                          (already exists; Image -> Image base)
    ├── IStarRemover.cs                            (-> starless plate)
    ├── IStellarSharpener.cs                       (sharpens a stars-only plate)
    ├── INonStellarDeconvolver.cs                  (deconvolves a starless plate)
    ├── IPsfEstimator.cs                           (whole-image or per-chunk PSF scalar)
    ├── HfdPsfEstimator.cs                         (TianWen-native; uses FindStarsAsync)
    └── SharpenPipeline.cs                         (orchestrator; pure delegation + per-pixel math)

TianWen.AI.Imaging/                               -- ORT-backed concrete impls
├── AiNafnetInputs.cs                              (internal; static readonly TargetMedian = 0.25 + other shared NAFNet input constants)
├── ChunkedInference.cs                            (tile + overlap + 16-px border ignore stitch)
├── Onnx/
│   ├── OnnxStarRemover.cs                         (darkstar_color_AI4 / darkstar_mono_AI4)
│   ├── OnnxStellarSharpener.cs                    (deep_sharp_stellar_AI4)
│   └── OnnxNonStellarDeconvolver.cs               (deep_nonstellar_sharp_conditional_psf_AI4)
├── ModelResolver.cs                               (resolves %LOCALAPPDATA%/TianWen/models paths)
└── AiServiceCollectionExtensions.cs               (AddTianWenAi)
```

`TianWen.Lib` has no `ProjectReference` to `TianWen.AI*`. `SharpenPipeline` constructs
purely from `IStarRemover?` + `IStellarSharpener?` + `INonStellarDeconvolver?`,
all nullable so the orchestrator is constructible even when no concrete impl is
registered. Throws `InvalidOperationException` at `ProcessAsync` time if a requested
step's enhancer is missing.

### Interface contract: atomic + black box

Each `IImageEnhancer` impl owns its own:
- **Stretch / unstretch** via `Image.MtfStretch(AiNafnetInputs.TargetMedian, ...)` /
  `Image.MtfUnstretch(...)` (caller passes any `Image`, linear or stretched -- the
  enhancer auto-detects via the SAS Pro `median(image-min) < 0.125` rule and reverses
  on output).
- **Tiling** via `ChunkedInference` (`chunk_size` + `overlap` + 16-pixel border
  ignore on stitch -- same parameters as SAS Pro's `split_image_into_chunks_with_overlap`
  / `stitch_chunks_ignore_border`).
- **ONNX session** (lazy, cached, EP selection via `ExecutionProviderResolver`).

Output is in the same color space as the input. Caller does not need to know
anything about stretching, tiling, or PSF scalars.

### Stretch pipeline (reuse `Image.MidtonesTransferFunction`, add MTF-only wrappers)

We already have the MTF primitive: `Image.MidtonesTransferFunction(midToneBalance, value)`
at `Image.Stretch.cs:103` -- the standard PixInsight STF, `(m-1)*x / ((2m-1)*x - m)`.
The SAS Pro variant takes a `target_median` (0.25 for AI4) and maps `orig_med → target_median`
rather than `→ 0.5`, but the two are **mathematically equivalent**: pick a midtone balance
`β` such that `MidtonesTransferFunction(β, orig_med) == target_median` and plug it into
the existing function. Closed form:

```
β = orig_med * (t - 1) / (t * (2*orig_med - 1) - orig_med)
```

Bonus: the **inverse** is also the same function with `1 - β` -- the algebraic identity
`MidtonesTransferFunction⁻¹(β, y) == MidtonesTransferFunction(1 - β, y)` holds, so we
don't need a separate `InverseMtf`.

Extend `Image.Stretch.cs` with three public additions (and one internal constant in
`TianWen.AI.Imaging`):

```csharp
// Image.Stretch.cs (TianWen.Lib) -- alongside the existing MidtonesTransferFunction:

public static double MidtonesBalanceFor(double origMedian, double targetMedian)
    => origMedian * (targetMedian - 1.0)
       / (targetMedian * (2.0 * origMedian - 1.0) - origMedian);

// On Image itself -- the round-trippable per-channel wrap/unwrap.
// Internally: per-channel orig_min subtract -> MidtonesTransferFunction(β, v) per pixel.
// `Vector<float>` SIMD inner loop, single pass, no allocation beyond the output buffer.
public Image MtfStretch(double targetMedian, out float[] origMin, out double[] balances);

// Inverse: MidtonesTransferFunction(1 - β, v) per pixel + add orig_min back.
public Image MtfUnstretch(ReadOnlySpan<float> origMin, ReadOnlySpan<double> balances);
```

```csharp
// TianWen.AI.Imaging/AiNafnetInputs.cs -- internal; only the AI enhancers consume it.
internal static class AiNafnetInputs
{
    /// <summary>
    /// Target median for the MTF pre-stretch applied to every AI4 NAFNet ONNX input.
    /// SAS Pro's stretch_image_mono / stretch_image_unlinked_rgb use the same default.
    /// `static readonly` (not `const`) so consumers in other assemblies pick up changes
    /// on rebuild of TianWen.AI.Imaging rather than baking the literal at IL emit time.
    /// </summary>
    public static readonly double TargetMedian = 0.25;
}
```

Enhancer call site:
```csharp
var stretched = image.MtfStretch(AiNafnetInputs.TargetMedian, out var origMin, out var bal);
// ... ORT inference on stretched ...
var result = output.MtfUnstretch(origMin, bal);
```

**Why this layout:** the MTF *math* is generic (PixInsight STF, used by every astro
processing tool) and belongs in `Image.Stretch.cs` next to its peers. The *parameter*
(`target_median = 0.25`) is AI4-training metadata and belongs with the AI enhancers
that depend on it. Different model families can declare their own constants without
touching the math. And we never re-implement MTF -- a single source of truth, matching
CLAUDE.md's "Stretch Pipeline: CPU/GPU Mirror" rule.

**What this does NOT replicate from our viewer stretch pipeline:** background neutralisation,
WB, shadow/rescale, luma blend, curves, HDR boost, normalize, clamp. Those are *display*
stages -- user-tuned look-and-feel adjustments that produce the final on-screen pixels.
SAS Pro skips them too because pre-AI stretch is *input normalisation* (push the
histogram to the training distribution), not display rendering. The two pipelines
share an MTF stage; the rest is intentionally separate.

### PSF measurement

`INonStellarDeconvolver` needs a scalar `psf01` per chunk (single FP32, log2-encoded
in `[0, 1]` from physical PSF radius in `[1, 8]` pixels):

```
psf01 = (log2(psf_radius) - log2(1.0)) / (log2(8.0) - log2(1.0))
```

`IPsfEstimator` abstracts the measurement so the deconvolver doesn't depend on a
specific source:

```csharp
public interface IPsfEstimator
{
    Task<float> EstimateAsync(Image image, CancellationToken ct = default);
    Task<float> EstimateChunkAsync(Image image, int x0, int y0, int w, int h, CancellationToken ct = default);
}
```

v1 impl: `HfdPsfEstimator`; runs `FindStarsAsync` on the whole image once, picks
median HFD across all stars, converts HFD -> Gaussian sigma -> FWHM -> radius. The
chunk variant returns the same whole-image scalar to every chunk (cheap and good
enough for v1; defer per-chunk re-measurement to a follow-up). A future
`SepPerChunkPsfEstimator` (port of SAS Pro's `measure_psf_radius`) can land
without touching the deconvolver.

### Domain semantics: linear-units, but not always linear-semantics

The `MtfStretch → infer → MtfUnstretch` wrapping puts every enhancer's output
back in source units (numerically `[0, MaxValue]`, same scale as the input).
That matches the user-facing contract of linear-domain tools like RC-Astro's
BlurXTerminator / NoiseXTerminator: caller hands in linear, gets back linear.

The subtlety is whether the *function* the AI applied is well-approximated by
a linear-domain transformation. This matters when chaining AI passes with
other linear-domain math (gradient removal, color calibration, classical
fallbacks). Per step:

| Step | Linear-units output? | Linear-semantics? | Reason |
|------|:--:|:--:|--------|
| `IStellarSharpener` | yes | **yes** | Local detail edits in the stars-only plate; no histogram macro-shape change. Chains fine with linear-domain math before or after. |
| `INonStellarDeconvolver` | yes | **yes** | Same -- local-only edits on starless plate. The PSF-conditional scalar input doesn't change global tone. |
| `IStarRemover` (darkstar) | yes | **no** | Globally rewrites the histogram by collapsing every stellar pixel down to the local nebula level. No <c>f</c> in linear units approximates "stretched-trained NAFNet seeing stars at their perceptually-stretched contrast". The output is in source units but is not a linear-domain function of the input. |

**Practical consequence.** Star removal is normally the **last step** in the
linear stage of the pipeline (calibration → stacking → ABE → color calibration →
star removal → stretch → post-processing on the starless + stars-only plates).
The fact that the AI4 darkstar isn't "true linear semantics" doesn't bite
because nothing linear-domain runs after it. If we ever want to chain ABE
*after* star removal -- e.g. re-fit a gradient on the starless plate to clean
up residual sky -- we should be aware that we're feeding a non-linear-semantics
plate to a linear-fitting tool, and either (a) keep the gradient fit very low
degree, or (b) train a linear-domain star remover specifically for that flow.

For now: keep the canonical pipeline order. Documentation here so the
limitation is recoverable months from now without re-deriving it.

### Orchestrator API

```csharp
public sealed class SharpenPipeline(
    IStarRemover?            starRemover     = null,
    IStellarSharpener?       stellarSharpener  = null,
    INonStellarDeconvolver?  deconvolver     = null,
    ILogger<SharpenPipeline>? logger         = null)
{
    public Task<SharpenResult> ProcessAsync(SharpenRequest req, CancellationToken ct = default);
}

public sealed record SharpenRequest(
    Image Source,
    bool RunStarRemoval        = true,
    bool RunStellarSharpen     = true,
    bool RunNonStellarDeconv   = true,
    bool Recombine             = true);

public sealed record SharpenResult(
    Image? Final,                 // present iff Recombine = true
    Image? Starless,              // present iff RunStarRemoval = true
    Image? StarsOnly,             // present iff RunStarRemoval = true
    Image? SharpenedStars,        // present iff RunStellarSharpen = true
    Image? DeconvolvedStarless);  // present iff RunNonStellarDeconv = true
```

Request validation rules (enforced before invoking any enhancer):
- `RunStellarSharpen || RunNonStellarDeconv` requires `RunStarRemoval`.
- `Recombine` requires `RunStarRemoval` and at least one of stellar/deconv.
- Any `Run* = true` requires the corresponding interface to be non-null.

The split (`StarsOnly = Source - Starless`) and the recombine
(`Final = (DeconvolvedStarless ?? Starless) + (SharpenedStars ?? StarsOnly)`) are
per-pixel ops in the orchestrator; no enhancer needed. Pass-through is implicit
when a step is disabled.

### DI registration

```csharp
// TianWen.AI.Imaging
public static IServiceCollection AddTianWenAi(this IServiceCollection services)
{
    services.AddSingleton<IModelResolver, ModelResolver>();
    services.AddSingleton<IStarRemover>(sp => new OnnxStarRemover(
        sp.GetRequiredService<IModelResolver>(),
        sp.GetRequiredService<ILogger<OnnxStarRemover>>()));
    services.AddSingleton<IStellarSharpener>(sp => new OnnxStellarSharpener(...));
    services.AddSingleton<INonStellarDeconvolver>(sp => new OnnxNonStellarDeconvolver(
        sp.GetRequiredService<IPsfEstimator>(), ...));
    services.AddSingleton<IPsfEstimator, HfdPsfEstimator>();
    services.AddSingleton<SharpenPipeline>();
    return services;
}
```

Factory lambdas (not the short generic form) because the ONNX impls take a
non-generic `ILogger` for shared logging; same gotcha as `CatalogPlateSolver`
(see CLAUDE.md "Plate Solving" / factory lambda note).

Consumers (CLI command, future GUI menu, hosting API) call `AddTianWenAi()`
themselves. `TianWen.Lib` does not auto-wire any ONNX impl; keeps the core
library ORT-free.

### Model resolution: a vendor's weights are read WHERE THE VENDOR PUT THEM

`ModelResolver` probes its three directories with the bare model name, then falls back to
GraXpert's own cache (`%LOCALAPPDATA%/GraXpert/GraXpert/bge-ai-models/<semver>/model.onnx`, newest
version first) for `graxpert_bge.onnx`. **All three roots are auto-detected and none takes an
override** -- the vendor cache is written by GraXpert itself through the platform's per-user data
directory, which is the same API `LocalAppDataDir` reads, so this is not a best guess.

A search *directory* structurally cannot reach it: the version subdir is not known ahead of time and
the file is always named `model.onnx`, because for GraXpert the **bucket** (the versioned parent
directory) carries the identity. So an installed GraXpert used to buy nothing -- the only bridge was
`tools/tianwen-ai-models-fetch.ps1` hardlinking the weights across, and **a packaged install cannot
run a repo-relative dev script**. That shipped: the Store build of Astro Photo Viewer failed Enhance
with "model not found" on a machine whose 207 MB of GraXpert weights were sitting on disk, unreachable
by the app. The fetch script still runs and its copy still wins (it outlives GraXpert being
uninstalled), but it is now an optimisation, not the only way in. The same rule already applied to
SAS Pro, probed in its own install directory -- the RC-Astro CLI's own env-var/install-dir/PATH probe
in [rc-astro-enhancers.md](rc-astro-enhancers.md) is the sibling pattern.

**Never make a shipped capability depend on a script only a checkout can run.**

### Future: classical fallbacks

Out of v1 scope, but the design accommodates them cleanly:

```csharp
// TianWen.Lib.Imaging.Enhancement.Classical
public static IServiceCollection AddTianWenClassicalEnhancers(this IServiceCollection services)
{
    // services.TryAddSingleton<IStellarSharpener,      UnsharpMaskStellarSharpener>();
    // services.TryAddSingleton<INonStellarDeconvolver, LucyRichardsonDeconvolver>();
    // (no IStarRemover -- no respectable classical analogue)
    return services;
}
```

Use `TryAddSingleton` so `AddTianWenAi()` (called after) wins when models are
available. If `IModelResolver` later probes for missing model files at startup,
`AddTianWenAi()` can register on a per-interface basis only when the corresponding
model is on disk, leaving the classical fallback for absent ones.

## Phasing

| Phase | Scope | Status |
|-------|-------|--------|
| 0 | PLAN doc + dev-only model fetch script | DONE (`tools/tianwen-ai-models-fetch.ps1` with `.pth`/`.pt` filter; 17 `.onnx` files + manifest.json in `%LOCALAPPDATA%\TianWen\models`, 1.4 GB) |
| 1 | `Image.MtfStretch` / `Image.MtfUnstretch` / `Image.MidtonesBalanceFor` extensions to `Image.Stretch.cs` + `AiNafnetInputs.TargetMedian` constant + `ChunkedInference` + `IPsfEstimator` + `HfdPsfEstimator` | DONE |
| 2 | `IStarRemover` + `OnnxStarRemover` (darkstar_color/mono_AI4); also produces a useful standalone "starless export" feature | DONE |
| 3 | `IStellarSharpener` + `OnnxStellarSharpener` (deep_sharp_stellar_AI4) | DONE |
| 4 | `INonStellarDeconvolver` + `OnnxNonStellarDeconvolver` (deep_nonstellar_sharp_conditional_psf_AI4) | DONE |
| 5 | `SharpenPipeline` orchestrator + request validation + tests | DONE (step-based `SharpenStep` discriminated record + ordered list; `SharpenIntermediates` retention selector) |
| 6 | `AddTianWenAi` DI extension + `ModelResolver` + integration tests against real model files | DONE (ModelResolver portability fix for non-Windows landed alongside) |
| 7 | `tianwen sharpen` CLI command + GUI menu entry | DONE for CLI (`tianwen image {sharpen,remove-stars,flatten,render,stats}`); GUI menu entry still NOT STARTED |
| 8 | Deployment: runtime self-bootstrap (in-app first-launch download) + `tianwen models fetch` sub-command | NOT STARTED |
| 9 | Classical fallbacks (`UnsharpMaskStellarSharpener`, `LucyRichardsonDeconvolver`, etc.) | DEFERRED |
| 10 | NPU acceleration on win-arm64 (INT8 quant or `.serialized.bin` compile) | DEFERRED |

**Refinements shipped beyond the original phasing** (merged to main with the Phases 0-7 work):

| Topic | Detail |
|---|---|
| `IDenoiseEnhancer` + 3-variant ONNX impl | Standalone interface; default / lite / walking-noise weights selected by `DenoiseVariant`; slots between deconv and recombine on the starless branch |
| `IGradientCorrector` + GraXpert BGE | NHWC ONNX wrapper; `EnhanceAndEstimateBackgroundAsync` default-interface-method for diagnostic background plate; `--save-gradient` flag on `tianwen image flatten` |
| Dual-stretch pipeline | `StretchStarsStep` (Frank fixed-curve MTF) + `StretchStarlessStep` (auto-target MTF) OR `GhsStretchStarlessStep` (Cranfield GHS, opt-in only) + `BackgroundReduceStep` (S-curve) + `CompressHighlightsStep` (Reinhard knee) + Screen recombine; per-plate float TIFF export with sRGB v4 ICC |
| `tianwen image stats` | `ImageStats.ComputeAsync` -> HFD/FWHM/Ellipticity/SNR + per-channel pedestal/median/MAD/noise; `IsLinear` flag via `Image.DetectPreStretched` (after `ScaleFloatValuesToUnit`) with cross-stretch comparison warning; `--format text|json` |
| 16-px chunk padding + `ArrayPool` input tensors | NAFNet broadcast errors fixed on real-image sizes; ~200 MB GC churn cut |
| `tianwen image render` + `--png` flags | PNG goes through the same `MasterPreviewRenderer` as the stack output |
| `Image.SubtractiveChromaticNoise` + `Image.Lerp` | SCNR on stars plate (`ScnrStarsStep`); per-step AI blend amounts (`StellarBlend`, `DeconvBlend`, `DenoiseBlend`) |
| Win-arm64: QNN -> DirectML for FP32 | 2.4x speedup on Skull master (6m42s -> 2m45s); Adreno GPU via DirectML EP |
| Pre-stretched-input auto-detect | `ChunkedNafnetRunner` skips MtfStretch round-trip when input median(ch0 - min) >= 0.125 |
| `Image.Histogram` median bugfix | `histogram.Count / 2.0` -> `hist_total / 2.0`; latent bug masked by tight astro backgrounds dominating low bins; 5 new direct tests pin the fix |
| `Image.BilinearResize` | Clamp `fx`/`fy` BEFORE deriving `dx`/`dy` (else non-monotonic dip at boundaries) |

## Open questions

1. **Chunk size / overlap parameters.** SAS Pro picks these per-model dynamically
   (see `_safe_chunk_size`-like helpers around `sharpen_engine.py:809-840`). Port
   the exact heuristics or hardcode known-good defaults per model? Start with
   hardcoded; revisit if VRAM/quality issues emerge.
2. **Mono vs RGB dispatch.** SAS Pro picks the mono or color variant of darkstar /
   denoise from input channel count. Implement as ChannelCount switch inside the
   ONNX wrapper, or expose as two registered services? Single wrapper that
   internally dispatches.
3. **Where does `tianwen sharpen` live in the CLI?** Top-level `sharpen` verb, or
   nested under a new `tianwen enhance` parent (with future `denoise`, `superres`,
   etc. subcommands)? Vote: nested. Less top-level verb pollution.
4. **Per-OTA enhancement during live session?** Out of scope here, but worth
   thinking about; the enhancers are pure `Image -> Image` and could plug into
   the live preview pipeline. Defer until offline batch path is solid.

## Rules in full (moved from CLAUDE.md, 2026-10-09)

CLAUDE.md keeps one line per rule; this is its AI-enhancement section's full text as it stood, moved verbatim, with its paragraph on vendor weights.

`SharpenPipeline` (`TianWen.Lib/Imaging/Enhancement/`) orchestrates role-typed enhancers
(`IStarRemover` / `IStellarSharpener` / `INonStellarDeconvolver` / `IDenoiseEnhancer` /
`IGradientCorrector` / `IImageDeblurrer`) over an immutable `SharpenStep[]` program. Selection is
**RC-preferred, deferred, and license-gated**: `AddRcAstroAi()` wraps `AddTianWenAi()` and `Replace`s
the RC-servable roles with `DeferredEnhancer` proxies that make the RC-vs-in-house choice AND the
blocking license probe on the FIRST `EnhanceAsync`, never at DI registration, so composing a service
collection spawns no `rc-astro` process. Design and every measurement: `docs/plans/ai-enhancement.md`,
`docs/plans/rc-astro-enhancers.md`, `docs/plans/osc-narrowband-denoiser.md` § 1o and
`docs/plans/denoiser-training.md`.

- **The program is shaped by what SERVES, never by what is registered.**
  `SharpenPipeline.CapabilitiesFor(input, options)` asks every role through `IEnhancerAvailability`
  (a deferred RC role answers by its licence, the N2N denoiser by channel count and weights) and
  `CanonicalProgram` builds from the answer: the split with a star remover (BlurX-first where a
  deblurrer serves), whole-frame (gradient + denoise) without one. The viewer, the CLI, `stack
  --enhance` and the endpoint all run that one program. **A gate on a role asks
  `IEnhancerAvailability.Serves`, never `is null`**: a deferred RC role is registered on every host,
  and where the product is absent its first use throws.
- **A broadband SPCC balance is the program's FIRST step** (`LinearEnhanceProgram.WhiteBalance`,
  `WhiteBalanceStep`, 2026-10-09): PixInsight's order, SPCC on the linear data and then every enhancer. The
  HOST supplies it, since the solve needs the plate solution and the catalogue (`stack --enhance` solves it on
  the linear master, `image sharpen` and the endpoint take the one the file states, the viewer its own fit),
  and every returned plate says the balance is in its pixels (`ColourCalibration.Applied`), RC-Astro's
  included, whose metadata the pipeline restamps. Why and the measurements:
  `docs/architecture/stacking-render-pipeline.md`, "A broadband SPCC fit goes INTO the pixels".
- **The SETI Astro (SAS Pro AI4) tier was REMOVED on 2026-09-26** (the user's call, after its model
  licence of 2026-09-24 allowed use only within SASpro). Nothing loads its weights or searches
  SASpro's folder, and `EnhanceBackend` value 2 stays unassigned (enums are numeric on the wire). Its
  GPL-3.0 Python is still ours to LEARN from (how it feeds its models:
  `docs/plans/model-training-roadmap.md` § 8); a model file is never opened or derived from. Star
  removal, deblur and starless deconvolution are RC-only until TianWen's own models ship into the
  roles, which stay as the extension points.
- **The NAFNet pre-stretch measures COVERED pixels only.** `ChunkedNafnetRunner.ApplyInputStretch`
  leaves the canvas ring (`Image.AbsentPixels`, the crop's own absence scan) out of the linearity
  auto-detect and the stretch's floor and median (`Image.MinAndShiftedMedian`, one measurement for
  both), and the runners put the ring back exactly (`CopyAbsent`). A ring's zero was every channel's
  floor: 9 of 190 masters read as already stretched and 108 were stretched from the wrong floor
  (`docs/known-limitations.md`). An interior zero is a measurement and still counts.
- **In-house N2N denoiser** (`N2nDenoiser`, OSC-only, declines mono): the default local
  `IDenoiseEnhancer` (`AddTianWenAi()`) and the fallback behind NoiseXTerminator. Weights ship
  in-repo (`src/TianWen.AI.Imaging/models/`) as an LFS object; **any new LFS file type the apps ship
  must be added to `APP_LFS_INCLUDE` in `dotnet.yml`**, or the publish matrix ships a pointer stub as
  the model (`publish-apps`'s "Verify LFS objects materialised" step is the backstop). Full history,
  including the two-week bug where the runner fed the graph pixels ~100x below its training band and
  cut every star's peak 30 percent with no metric catching it: `docs/plans/denoiser-training.md`.
- **RC-Astro (BlurX / NoiseX / StarXTerminator)**: `AddRcAstroAi()`. `.onnx` files are encrypted at
  rest (license forbids extracting weights), so driven through the `rc-astro` CLI's `--json` NDJSON
  protocol, never loaded into ORT. **A plate outside `[0, 1]` is mapped in and back by
  `RcAstroEnhancerBase`**: RC-Astro rescales such a plate itself and answers on THAT scale (an aligned
  master's negative border ringing put a starless sky at 3.4 times its level). **BlurX stops at 1**, so a
  deblur hands it the brightest star at a quarter of the ceiling (`NarrowbandCombination.DeblurAsync`;
  the enhance pipeline does not yet, #1270).

**A vendor's weights are read WHERE THE VENDOR PUT THEM, never only where a dev script copied them.**
`ModelResolver` also auto-detects GraXpert's own cache for `graxpert_bge.onnx` (no override: the
version subdir isn't knowable ahead of time). **Never make a shipped capability depend on a script
only a checkout can run**: the Store build once shipped an Enhance failure against 207 MB of GraXpert
weights already on disk because the only bridge was a repo-relative dev script.

## Cross-references

- [tools/tianwen-ai-models-fetch.ps1](../../tools/tianwen-ai-models-fetch.ps1): dev model fetch (hardlink from SAS Pro, else download)
- [stacking.md](stacking.md): satellite trail removal belongs there as a pre-rejection filter
- [CLAUDE.md](../../CLAUDE.md): "Plate Solving" section's factory-lambda note (same DI gotcha for `ILogger` ctor params)
- [CLAUDE.md](../../CLAUDE.md), "Stretch Pipeline: CPU/GPU Mirror", `Image.MtfStretch` is the MTF-only entry into the same math `Image.StretchValue` already uses; the multi-stage viewer chain is for display, not for ONNX input prep
