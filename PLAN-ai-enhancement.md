# PLAN: AI image enhancement — star removal, deconvolution, sharpening, denoise

## Goal

Wire SetiAstroSuite Pro's AI4 NAFNet models (and a Walking Noise denoise variant) into
TianWen as a set of composable image enhancers. The headline pipeline is **sharpen**:
remove stars → sharpen the stars-only plate → PSF-deconvolve the starless plate →
optionally recombine. Each step must be usable standalone (starless export, deconv on
a pre-starless image, etc.), so the orchestrator composes atomic `IImageEnhancer`s
rather than baking the chain into one method.

Reference implementation: SetiAstroSuite Pro (Python — see `../../other/setiastrosuitepro/`).
Models distributed under their GitHub release `setiastro/setiastrosuitepro` tag
`benchmarkFIT` (2 zip assets, ~2.5 GB extracted). Same architecture (NAFNet),
same training distribution, same input conventions — we just port the orchestration
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
  pre-rejection filter, not here. Cross-reference [PLAN-stacking.md](PLAN-stacking.md)
  when the time comes.
- **Classical (non-ONNX) fallbacks** for every enhancer except `IStarRemover`.
  Lucy-Richardson, unsharp-mask, bilateral, etc. exist and are worth having later
  but are not the v1 priority. Star removal has no respectable classical analogue —
  no fallback there.
- **Hexagon NPU acceleration** on win-arm64. AI4 ships pure FP32; QNN HTP wants
  INT8/INT16 or a pre-compiled `.serialized.bin`. Real NPU accel requires either
  upstream re-export at INT8 or our own ORT QNN compile pass — separate workstream.
- **Runtime self-bootstrap of model files**. v1 uses the dev-only fetch script
  `tools/tianwen-ai-models-fetch.ps1`. Deploy story (in-app first-launch download
  with progress UI + CLI sub-command) is a follow-up plan.

## Architecture

### Project layout

```
TianWen.Lib/Imaging/Enhancement/                  -- zero AI dep
├── IImageEnhancer.cs                              (already exists; Image -> Image base)
├── IStarRemover.cs                                (-> starless plate)
├── IStellarSharpener.cs                           (sharpens a stars-only plate)
├── INonStellarDeconvolver.cs                      (deconvolves a starless plate)
├── IPsfEstimator.cs                               (whole-image or per-chunk PSF scalar)
├── HfdPsfEstimator.cs                             (TianWen-native; uses FindStarsAsync)
└── SharpenPipeline.cs                             (orchestrator; pure delegation + per-pixel math)

TianWen.AI.Imaging/                               -- ORT-backed concrete impls
├── SasProMtf.cs                                   (pre-stretch + inverse-stretch helpers)
├── ChunkedInference.cs                            (tile + overlap + 16-px border ignore stitch)
├── Onnx/
│   ├── OnnxStarRemover.cs                         (darkstar_color_AI4 / darkstar_mono_AI4)
│   ├── OnnxStellarSharpener.cs                    (deep_sharp_stellar_AI4)
│   └── OnnxNonStellarDeconvolver.cs               (deep_nonstellar_sharp_conditional_psf_AI4)
├── ModelResolver.cs                               (resolves %LOCALAPPDATA%/TianWen/models paths)
└── AiServiceCollectionExtensions.cs               (AddTianWenAi)
```

`TianWen.Lib` has no `ProjectReference` to `TianWen.AI*`. `SharpenPipeline` constructs
purely from `IStarRemover?` + `IStellarSharpener?` + `INonStellarDeconvolver?` —
all nullable so the orchestrator is constructible even when no concrete impl is
registered. Throws `InvalidOperationException` at `ProcessAsync` time if a requested
step's enhancer is missing.

### Interface contract: atomic + black box

Each `IImageEnhancer` impl owns its own:
- **Stretch / unstretch** via `SasProMtf` (caller passes any `Image`, linear or
  stretched — the enhancer auto-detects via the SAS Pro `median(image-min) < 0.125`
  rule and reverses on output).
- **Tiling** via `ChunkedInference` (`chunk_size` + `overlap` + 16-pixel border
  ignore on stitch — same parameters as SAS Pro's `split_image_into_chunks_with_overlap`
  / `stitch_chunks_ignore_border`).
- **ONNX session** (lazy, cached, EP selection via `ExecutionProviderResolver`).

Output is in the same color space as the input. Caller does not need to know
anything about stretching, tiling, or PSF scalars.

### Stretch pipeline (SAS Pro MTF, frozen)

```csharp
public static class SasProMtf
{
    public const float TargetMedian = 0.25f;

    // Detect-and-apply. Returns the transformed image + the per-channel
    // (origMin, origMed) tuple needed for inversion. If no stretch was needed,
    // origMed = null and Inverse is a no-op.
    public static (Image stretched, float[] origMin, float[]? origMed) Apply(Image src);

    public static Image Inverse(Image stretched, float[] origMin, float[]? origMed);
}
```

Formula (per channel, target `t = 0.25f`):
```
x' = ((m - 1) * t * x) / (m * (t + x - 1) - t * x)    // m = orig_med
```
Inverse is algebraic. Implemented inline with `Vector<float>` SIMD — single pass,
no allocation beyond the output buffer.

**Why frozen, not reusing `Image.StretchValue()`:** AI4 is trained against this
specific transform with no pedestal / WB / shadow-rescale / luma-blend stages. Our
viewer stretch pipeline has all of those and is intentionally evolving (see CLAUDE.md
"Stretch Pipeline: CPU/GPU Mirror"). Coupling them invites silent quality drift.

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

v1 impl: `HfdPsfEstimator` — runs `FindStarsAsync` on the whole image once, picks
median HFD across all stars, converts HFD -> Gaussian sigma -> FWHM -> radius. The
chunk variant returns the same whole-image scalar to every chunk (cheap and good
enough for v1; defer per-chunk re-measurement to a follow-up). A future
`SepPerChunkPsfEstimator` (port of SAS Pro's `measure_psf_radius`) can land
without touching the deconvolver.

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
per-pixel ops in the orchestrator — no enhancer needed. Pass-through is implicit
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
non-generic `ILogger` for shared logging — same gotcha as `CatalogPlateSolver`
(see CLAUDE.md "Plate Solving" / factory lambda note).

Consumers (CLI command, future GUI menu, hosting API) call `AddTianWenAi()`
themselves. `TianWen.Lib` does not auto-wire any ONNX impl — keeps the core
library ORT-free.

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
| 1 | `SasProMtf` + `ChunkedInference` + `IPsfEstimator` + `HfdPsfEstimator` | NOT STARTED |
| 2 | `IStarRemover` + `OnnxStarRemover` (darkstar_color/mono_AI4) — also produces a useful standalone "starless export" feature | NOT STARTED |
| 3 | `IStellarSharpener` + `OnnxStellarSharpener` (deep_sharp_stellar_AI4) | NOT STARTED |
| 4 | `INonStellarDeconvolver` + `OnnxNonStellarDeconvolver` (deep_nonstellar_sharp_conditional_psf_AI4) | NOT STARTED |
| 5 | `SharpenPipeline` orchestrator + request validation + tests | NOT STARTED |
| 6 | `AddTianWenAi` DI extension + `ModelResolver` + integration tests against real model files | NOT STARTED |
| 7 | `tianwen sharpen` CLI command + GUI menu entry | NOT STARTED |
| 8 | Deployment: runtime self-bootstrap (in-app first-launch download) + `tianwen models fetch` sub-command | NOT STARTED |
| 9 | Classical fallbacks (`UnsharpMaskStellarSharpener`, `LucyRichardsonDeconvolver`, etc.) | DEFERRED |
| 10 | NPU acceleration on win-arm64 (INT8 quant or `.serialized.bin` compile) | DEFERRED |

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
   thinking about — the enhancers are pure `Image -> Image` and could plug into
   the live preview pipeline. Defer until offline batch path is solid.

## Cross-references

- [tools/tianwen-ai-models-fetch.ps1](tools/tianwen-ai-models-fetch.ps1) — dev model fetch (hardlink from SAS Pro, else download)
- [PLAN-stacking.md](PLAN-stacking.md) — satellite trail removal belongs there as a pre-rejection filter
- [CLAUDE.md](CLAUDE.md) — "Plate Solving" section's factory-lambda note (same DI gotcha for `ILogger` ctor params)
- [CLAUDE.md](CLAUDE.md) — "Stretch Pipeline: CPU/GPU Mirror" — why we use a frozen `SasProMtf`, not `Image.StretchValue`
