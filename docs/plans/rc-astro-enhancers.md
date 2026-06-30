# RC-Astro CLI-wrapper enhancers

Drive RC-Astro's neural tools (BlurXTerminator / NoiseXTerminator / StarXTerminator)
from TianWen, **preferring them over the SETI Astro ONNX enhancers when the
RC-Astro CLI is present and the product is licensed**, falling back to SAS
otherwise.

## Why a CLI wrapper, not direct ONNX (like SETI Astro)

The RC-Astro `.onnx` files under `%AppData%\RC-Astro` are **encrypted at rest**
(entropy ~7.997 bits/byte across the whole file, no protobuf, no strings). Only
the official `rc-astro` binary can decrypt them, in-memory, at load time. So
unlike SAS Pro's plain ONNX, they cannot be loaded into ONNX Runtime directly.
The license (`LICENSE.txt` sections 3/7/9/10) forbids extracting/decrypting the
weights, and circumventing the encryption is an anti-circumvention issue
independent of the EULA. The supported integration path is the documented
`--json` machine protocol (`README-DEVS.txt`), which is what we drive.

### Viability (measured, win-arm64)

The x64 binary runs under Windows-on-ARM x64 emulation and DirectML reaches the
**native** Adreno X1-85 GPU via D3D12 passthrough (NOT emulated CPU). On a real
9.05 MP frame: sxt 18.7s, bxt 35.5s (GPU), vs sxt 111s on emulated CPU (~6x).
Outputs are valid 32F FITS in [0, 1]. Good for a post-capture/batch step.

## Architecture

Lives in `TianWen.AI.Imaging` (`RcAstro/` folder) so the present+licensed
selector can reference both the RC wrappers and the `Onnx*` fallbacks.

| Type | Role |
|------|------|
| `IRcAstroCli` / `RcAstroCli` | Locate `rc-astro` (`RC_ASTRO_CLI` env -> platform install dir -> PATH), probe per-product `--license` (cached, sync), run a product with `--json` NDJSON parsing. Modeled on `ExternalProcessPlateSolverBase`. |
| `RcAstroEvent` (internal) | `JsonDocument`-based NDJSON parser (AOT/trim clean). `RcAstroProgress` / `RcAstroRunResult` are the public result types. |
| `RcAstroEnhancerBase` | FITS round-trip: `Image.WriteToFitsFile` -> `cli.RunAsync` -> `Image.TryReadFitsFile`; temp-file lifecycle; progress -> logger. |
| `RcAstroStarRemover : IStarRemover` (sxt) | The product's default `-o` output IS the starless plate; the pipeline derives stars-only itself. |
| `RcAstroDenoiser : IDenoiseEnhancer` (nxt) | Noise-adaptive `--dn` (see below). |
| `RcAstroNonStellarDeconvolver : INonStellarDeconvolver` (bxt) | Runs on the starless plate: `--sn` + `--ansr` (auto PSF), `--ss 0`. |
| `DeferredEnhancer` (+ `DeferredStarRemover`/`DeferredDenoiser`/`DeferredNonStellarDeconvolver`) | Proxy that makes the RC-vs-SAS choice (and its blocking license probe) on the FIRST `EnhanceAsync`, not at DI registration/resolution. So composing/building a service collection -- even resolving `SharpenPipeline` -- spawns no `rc-astro` process; only the first actual enhancement does (cached). |
| `AddRcAstroAi()` | Calls `AddTianWenAi()` for the SAS baseline, then `Replace`s each RC-servable role with its deferred proxy (RC when present+licensed -> else the concrete `Onnx*` singleton). |

FITS round-trip is in [0, 1] with no rescaling: pipeline plates are Float32
(`WriteToFitsFile` emits BITPIX=-32); RC normalises internally and returns
[0, 1] 32F (verified empirically).

## Parameters

Documented CLI defaults (from `rc-astro <product> --json`): **every amount
defaults to 0** -- i.e. bxt/nxt are no-ops on raw CLI defaults (different from
PixInsight's non-zero GUI defaults). So the wrapper supplies its own:

| Product | Wrapper default |
|---------|-----------------|
| sxt | CLI defaults (star removal is unconditional). |
| bxt | `--ansr` (auto PSF), `--sn 0.90`, `--ss 0` (starless plate has no stars). |
| nxt | `--it 2`, `--dn` auto from noise (0.7-0.95), heavier for noisier input. |

**Noise-adaptive denoise** (matches the "heavier denoise on short integration"
workflow): the denoiser measures the input it is handed
(`Image.EstimateNoiseProfile()` MAD sigma; short integration -> high sigma) and
maps sigma -> `--dn` in a clamped band. Self-contained in the enhancer (no
interface change), default on, with a fixed-`dn` override. For RC we steer via
native params (the model is nonlinear in strength), not the pipeline's post-hoc
`Blend` lerp.

## Phasing

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | Foundation (`RcAstroCli` + NDJSON + FITS round-trip base) + `RcAstroStarRemover` (sxt) + `AddRcAstroAi` selector for `IStarRemover` + tests | DONE |
| 2 | `RcAstroDenoiser` (nxt, noise-adaptive `--dn`) + `RcAstroNonStellarDeconvolver` (bxt); selector extended to all three roles via a DRY `PreferRcAstro<TRole, TFallback>` helper; tests | DONE |
| 3 | Wire `AddRcAstroAi()` into composition roots (CLI) + docs (CLAUDE.md arch note, TODO) | DONE |
| 3a | **Options surface + backend control (CLI).** Immutable `EnhanceOptions`/`EnhanceTuning` threaded through `SharpenPipeline.ProcessAsync` -> a non-breaking `IImageEnhancer.EnhanceAsync(input, options, progress, ct)` default-interface-method overload. `DeferredEnhancer` reads `Backend` (Auto/ForceRcAstro/ForceSas) per call; RC `BuildArgs` reads `Tuning` (null = today's auto behaviour). CLI flags `--ai-backend`, `--bxt-sharpen`, `--nxt-denoise`, `--nxt-iterations` on `image sharpen` + `stack` (the latter via `StackingOptions.EnhanceOptions` -> `MasterPostProcessor`). | DONE |
| 3b | **Progress -> CLI.** `EnhanceProgress(StepName, StepIndex, StepCount, StepPercent, Eta)`; `SharpenPipeline.ProcessAsync` stamps a per-step boundary tick + forwards each enhancer-backed step's sub-step ticks (RC-Astro NDJSON `IProgress<float>`, relayed via `StepProgressRelay`); CLI `EnhanceProgressConsole` printer (step transitions + ~10% sub-step ticks) wired on both `image sharpen` (direct) and `stack --enhance` (via `StackingPipeline` -> `MasterPostProcessor`). Boundary-tick + sub-step-forwarding pinned by `SharpenPipelineTests`. | DONE |
| 3c | **Viewer enhance action (tianwen-fits).** Landed in the standalone FITS viewer, NOT the GUI: `tianwen-gui` has no document-viewer tab (its `VkImageRenderer` surfaces are the live preview / guider / planetary view), so the interactive "open an image -> enhance it" action belongs in `tianwen-fits` (the real viewer host with `ViewerController` + file-open + `AdoptImageAsync`). Shared viewer layer: `ToolbarAction.Enhance` + a presence-gated toolbar button (renderer `EnhanceAvailable`, hidden where no pipeline) + `ViewerState.{IsEnhancing,EnhanceProgressPct,PreferredEnhanceBackend}`; `EnhanceActions.EnhanceAsync` (route-only) runs `SharpenPipeline.ProcessAsync` off the render thread (`SharpenRequest.DeblurFirst`/`Canonical` per `SupportsDeblur`), per-step progress -> status line, result adopted via `AstroImageDocument.AdoptImageAsync` and swapped in on the render thread (`ViewerController._enhanceTask` + `TryApplyPendingEnhance`, the SkyMap async-result hand-off; no spin-render, so the AI work doesn't contend the GPU). Left-click runs; right-click cycles backend (shown on the button label), snapshotted into `EnhanceOptions` per click -- no global. `EnhanceImageSignal` + 'E' shortcut. `tianwen-fits` registers `AddRcAstroAi()`. Pinned by `EnhanceActionsTests`. A dedicated GUI viewer tab is deferred. | DONE |
| 3d | **Server enhance endpoint.** `AddRcAstroAi()` wired into `TianWen.Server` (registers `SharpenPipeline`; the RC-vs-SAS probe stays deferred so startup spawns no `rc-astro`). `HostedImageEnhancer` (single-flight `Interlocked` gate) runs `SharpenPipeline.ProcessAsync` on a background task tied to `ApplicationStopping` (not the request), with a synchronous `IProgress` relay that swaps an immutable `EnhanceStatusDto` snapshot atomically (lock-free read). `POST /api/v1/image/enhance` (path-in/path-out, `EnhanceRequestDto` body) returns `Enhance started` / `409` / `404` / parse-error; `GET /api/v1/image/enhance/status` returns the concrete `EnhanceStatusDto`; `ENHANCE-PROGRESS` + `ENHANCE-COMPLETED` push through `EventBroadcaster` -> `EventHub` (same `WebSocketEventDto` + `Dictionary<string,object?>` path as the session events). Backend/tuning parse goes through the new shared `EnhanceOptions.TryParse` -- the single source of truth now also used by `image sharpen` + `stack --enhance` (their duplicated inline blocks were collapsed into it). AOT-safe: `EnhanceRequestDto` / `EnhanceStatusDto` / `ResponseEnvelope<EnhanceStatusDto>` registered in `HostingJsonContext`; **published `win-arm64` (only the 2 documented LibUsbDotNet rollups, no new IL warnings) and smoke-tested the binary** -- body binding, the shared parser over the wire, single-flight 409, the concrete status DTO, a live `ENHANCE-PROGRESS` WS frame, and a full SAS run to `succeeded:true` all verified. Pinned by `EnhanceOptionsTests`. | DONE |

`IStellarSharpener` and `IGradientCorrector` stay SAS (no CLI equivalent).

## Phase 3 design -- threaded options + progress (no mutable singleton)

The enhancer interface is param-less (`EnhanceAsync(Image, ct)`) and the enhancers are
DI singletons, so backend preference + RC-native strength have no natural pass-through.
The chosen design threads an **immutable options record per call** rather than a
process-global mutable holder (which would be the wrong abstraction and would tear the
moment two enhances diverge -- e.g. concurrent server requests). The progress sink rides
the same mechanism, so it is one plumbing change, not two.

Backend-agnostic types in `TianWen.Lib/Imaging/Enhancement/` (RC maps them to native CLI
args; SAS ignores them):

```csharp
public enum EnhanceBackend { Auto, ForceRcAstro, ForceSas }   // Auto = "RC when present + licensed" (today's behaviour)

public sealed record EnhanceTuning(
    float? DeblurSharpen     = null,   // RC bxt --sn ; SAS has no analogue -> ignored
    float? DenoiseStrength   = null,   // RC nxt --dn ; null = noise-adaptive auto (today)
    int?   DenoiseIterations = null);  // RC nxt --it

public sealed record EnhanceOptions(EnhanceBackend Backend = EnhanceBackend.Auto, EnhanceTuning? Tuning = null)
{
    public static readonly EnhanceOptions Default = new();
}

public sealed record EnhanceProgress(string StepName, int StepIndex, int StepCount, float StepPercent, double EtaSeconds);
```

Non-breaking interface extension (the ~5 SAS impls + all existing call sites compile
untouched -- the default body ignores both new args):

```csharp
Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken ct = default)
    => EnhanceAsync(input, ct);
```

Threading (no shared mutable state anywhere):

- **`SharpenPipeline.ProcessAsync(request, EnhanceOptions options, IProgress<EnhanceProgress>? progress, ct)`**
  owns step identity. Per step *i* of *n* it builds a per-step `IProgress<float>` that stamps
  the step name/index and forwards into the caller's `IProgress<EnhanceProgress>`, then calls
  `enhancer.EnhanceAsync(img, options, stepProgress, ct)`. Caller sees `(StepName, i/n, %)`;
  the enhancer only deals with its own 0..1.
- **`DeferredEnhancer`** overrides the options overload: `options.Backend` decides RC-vs-SAS
  per call (`Auto` -> cached `cli.IsLicensed` probe; `ForceSas`/`ForceRcAstro` short-circuit).
  It still lazily memoizes the two stateless instances, so "no `rc-astro` subprocess at DI
  build, license probe only on first Auto/RC use" is intact -- what is gone is the *cached
  decision*, now per-call.
- **`RcAstroEnhancerBase`** overrides it: `BuildArgs` reads `options.Tuning` (null ->
  bit-identical to today's auto behaviour), and reports the NDJSON `RcAstroProgress` ticks
  into the passed `progress`.
- **SAS ONNX impls** do not override -> default ignores both; their progress is the coarse
  step-boundary tick the pipeline already emits.

Where the preference *lives* (the part that replaces the rejected singleton): nowhere new and
mutable -- each entry point snapshots an immutable `EnhanceOptions` at the call site. CLI from
flags (once per invocation); GUI from existing viewer/UI state into a fresh record per Enhance
click; Server deserialized from the request DTO per request (naturally per-call, supports
concurrent divergent enhances).
