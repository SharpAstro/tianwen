# RC-Astro CLI-wrapper enhancers

> **2026-09-26: the SETI Astro fallback below is gone.** The SAS tier was removed from the product
> (its model licence of 2026-09-24 allows use only within SASpro). A `DeferredEnhancer` now picks
> RC-Astro, else TianWen's own model where the role has one (the N2N denoiser today), else nothing,
> and answers `IEnhancerAvailability.CanServe` so the canonical program leaves out a role nothing
> serves: without StarXTerminator the program is whole-frame. `--ai-backend sas` fails with a message
> saying why, and `EnhanceBackend` value 2 stays unassigned. The text below describes the RC-vs-SAS
> design as it shipped and is kept as history.

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

### WHICH GPU is RC-Astro's setting, and it is worth checking once per machine

TianWen passes no `--device`: the compute device is RC-Astro's own, saved in its
config and shared with every other integration on the box, and choosing for it
would mean ranking the machine's adapters ourselves. **On a machine with more
than one GPU its `auto` can land on the weakest one.** Measured on the x64
desktop (2026-09-18): `auto` resolved to `gpu0`, the Intel UHD Graphics 630, and
one 30 MP master took **5m53s**; pinned to `gpu1`, the GTX 1070, the same master
took **44.7s**, an 8x difference with byte-identical output (noise sigma per
channel agreed to every digit). Nothing in the run said which device it used
except the debug log line `RC-Astro nxt completed on gpu in 121658ms`, and
`nvidia-smi` showed the discrete card idle at 139 MHz throughout while the
Windows GPU-engine counter had `rc-astro` at 189% on the other adapter.

So, once per machine:

```
rc-astro --device                 # lists auto / gpu0 / gpu1 / cpu and what auto resolves to
rc-astro --device-default gpu1    # pin the discrete card; --device-default auto puts it back
```

A TianWen-side option would only be worth adding alongside an evaluator that can
rank the adapters, since otherwise it moves the same guess into our code.

## Architecture

Lives in `TianWen.AI.Imaging` (`RcAstro/` folder) so the present+licensed
selector can reference both the RC wrappers and the `Onnx*` fallbacks.

| Type | Role |
|------|------|
| `IRcAstroCli` / `RcAstroCli` | Locate `rc-astro` (`RC_ASTRO_CLI` env -> platform install dir -> PATH), probe per-product `--license` (cached, sync), run a product with `--json` NDJSON parsing. Modeled on `ExternalProcessPlateSolverBase`. |
| `RcAstroEvent` (internal) | `JsonDocument`-based NDJSON parser (AOT/trim clean). `RcAstroProgress` / `RcAstroRunResult` are the public result types. |
| `RcAstroEnhancerBase` | FITS round-trip: `Image.WriteToFitsFile` -> `cli.RunAsync` -> `Image.TryReadFitsFile`; temp-file lifecycle; progress -> logger. |
| `RcAstroStarRemover : IStarRemover` (sxt) | The product's default `-o` output IS the starless plate; the pipeline derives stars-only itself. sxt's `--stars` output is defined (per `sxt --help`) as "original minus starless" = the pipeline's additive split (both clamp negatives), and `--stars --unscreen` = the pipeline's Screen split -- so neither flag adds information, just a duplicate plate + file round-trip. sxt has NO star-mask output; derive one from the stars plate (`Binarize` + `GaussianBlur`). |
| `RcAstroDenoiser : IDenoiseEnhancer` (nxt) | Noise-adaptive `--dn` (see below). |
| `RcAstroNonStellarDeconvolver : INonStellarDeconvolver` (bxt) | Runs on the starless plate and passes `--sn` only. `--ansp` (auto nonstellar PSF) defaults true and estimates the PSF "from stars in the image", which a starless plate does not have: see [the starless BlurX pass](#the-starless-blurx-pass-has-no-stars-to-measure). `--nsd` is the manual value, one diameter in pixels (0 to 8); there is no kernel or shape input. `--ss` and `--ash` are no-ops on a starless plate. |
| `DeferredEnhancer` (+ `DeferredStarRemover`/`DeferredDenoiser`/`DeferredNonStellarDeconvolver`) | Proxy that makes the RC-vs-SAS choice (and its blocking license probe) on the FIRST `EnhanceAsync`, not at DI registration/resolution. So composing/building a service collection -- even resolving `SharpenPipeline` -- spawns no `rc-astro` process; only the first actual enhancement does (cached). |
| `AddRcAstroAi()` | Calls `AddTianWenAi()` for the SAS baseline, then `Replace`s each RC-servable role with its deferred proxy (RC when present+licensed -> else the concrete `Onnx*` singleton). |

FITS round-trip is in [0, 1]: pipeline plates are Float32 (`WriteToFitsFile` emits BITPIX=-32) and a plate
already inside the range is written as it is. **A plate outside it is mapped in and back by the wrapper**
(`RcAstroEnhancerBase.RangeMap`, 2026-10-05): RC-Astro rescales such a plate onto [0, 1] itself and returns its
output on THAT scale, so an aligned master that rings below zero at its border came back as a starless red with
its sky at 0.031 where the plate's was 0.009. Pinned by `RcAstroRangeMapTests`.

## BlurX clips a sharpened star at the ceiling

BlurX's output stops at 1, and sharpening lifts a star's peak above its input: on LDN 1622's green master the
brightest star rose to 3.4 times its input peak, so at the peak's own scale (what `image deblur` handed over until then) 197
pixels came back clipped at 1 and the frame lost about 1 percent of its flux, all in bright stars. With the
brightest star at a quarter of the ceiling none clip, and the result agrees with an eighth's to 0.7 percent on star
pixels. `image combine --deblur` and `image deblur` take that headroom (`NarrowbandCombination.DeblurAsync`, each
result on its input's scale); the enhance pipeline's deblur step does not yet, which is
[#1270](https://github.com/SharpAstro/tianwen/issues/1270), since a headroom there hands the later steps a plate whose
peak is above 1.

## The starless BlurX pass has no stars to measure

`RcAstroNonStellarDeconvolver` hands BlurX the STARLESS plate with the automatic PSF, which BlurX estimates "from
stars in the image". It runs where it is the only deconvolution (`--ai-backend tianwen`, whose whole-frame deblur
waits on a stated kernel) and where `--deconv-blend` asks for it after a whole-frame BlurX. BlurX reports no PSF
(nothing in its `--json` events or the FITS header) and is deterministic, so what it used was read by comparing its
automatic run with fixed diameters (`--no-ansp --nsd`, 0.5 to 6 px), per 300 px tile, as the share of its own change
the closest fixed diameter still misses (Tarantula, SV605CC, 2026-10-17; CLI 2.6.9, `--sn 0.90`):

| Plate | Diameters the tiles pick | Median miss, each tile its own diameter | Tiles within 0.10 |
|---|---|---|---|
| The master, with its stars | 2.0 to 3.5 px, median 2.5 | 0.17 | 12 % |
| The starless plate | 0.5 to 6.0 px | 0.57 | none |

- **With stars the automatic PSF is the image's**: about 2.5 px against a median FWHM of 2.36 px over 2,435 stars by
  Tianwen's measurer. It is not one diameter, though, not even per tile, so it conditions on more than `--nsd` states.
- **Without stars it is no PSF at all**: some regions act like no sharpening, others like 6 px or more, and nothing
  fixed comes within 0.27 of it anywhere. It changed the plate 1.68 times as much as `--nsd 2.4` as the only
  deconvolution, and 1.48 times as much as a second pass after a whole-frame BlurX, where it turned 30 Doradus's dust
  lanes dark brown and crunchy.
- **A second pass after a whole-frame BlurX deconvolves the nebula twice**, so even the right diameter double-counts;
  the canonical program leaves it out when a deblur ran (`DeconvolveStarless` needs `!Deblur`).

Open: measure the stars' median FWHM before star removal and pass it as `--nsd`, and decide what the step does when
there are no stars: [#1704](https://github.com/SharpAstro/tianwen/issues/1704).

## Parameters

CLI defaults move between versions. When this was written (`rc-astro <product> --json`) **every
amount defaulted to 0**, so bxt/nxt were no-ops on raw CLI defaults (unlike PixInsight's non-zero
GUI defaults); CLI 2.6.5 (2026-08-23) defaults `--sn` and `--ss` to 0.50. The wrapper supplies its
own either way, and passes only what it sets:

| Product | Wrapper default |
|---------|-----------------|
| sxt | CLI defaults (star removal is unconditional). |
| bxt | `--sn 0.90` only; `--ansp` (auto nonstellar PSF) defaults true, which on a starless plate reads no PSF ([#1704](https://github.com/SharpAstro/tianwen/issues/1704)), and `--ss` / `--ash` are no-ops on the starless plate so are not passed. |
| nxt | `--it 1`, `--dn` auto from noise (0.7-0.95), heavier for noisier input. The passes are the enhance's own, gated (below). |

### Denoise passes

**Denoise passes are run by the enhance, never by `nxt --it`** (#1805). On the enhanced Sculptor master `--it 1`, `2` and
`3` left the starless plate's residual the same at every scale, for 12.9, 22.1 and 31.3 s. Feeding the output back in, as
people do by hand, does what `--it` does not: each pass took about 80 % off the 1 px noise, but the coarse (16 and 32 px)
sky noise fell 47 % on pass 2 and only 22 % on pass 3, which by eye only blotched the sky. So
`SharpenPipeline.DenoisePassesAsync` runs pass 1, keeps each further pass while it takes at least 35 % off the coarse sky
noise (`MultiScaleNoise`, a trous bands on the same sky pixels before and after), discards the first that does not, and
stops at 3; a stated `DenoiseIterations` is that many passes, ungated. The 35 % is provisional, from one master.

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
| 3a | **Options surface + backend control (CLI).** Immutable `EnhanceOptions`/`EnhanceTuning` threaded through `SharpenPipeline.ProcessAsync` -> a non-breaking `IImageEnhancer.EnhanceAsync(input, options, progress, ct)` default-interface-method overload. `DeferredEnhancer` reads `Backend` (Auto/ForceRcAstro/ForceSas) per call; RC `BuildArgs` reads `Tuning` (null = today's auto behaviour). CLI flags `--ai-backend`, `--bxt-sharpen`, `--nxt-denoise`, `--nxt-iterations` on `image sharpen` + `stack` (the latter via `StackingOptions.EnhanceOptions` -> `MasterPostProcessor`). *2026-08-17: the three tuning flags were renamed backend-neutral (`--deblur-sharpen` / `--denoise-strength` / `--denoise-iterations`, hard rename, no aliases) when the in-house N2N denoiser became a fourth `--ai-backend` value (`n2n`) and started reading `DenoiseStrength` as its blend dial -- see `osc-narrowband-denoiser.md` section 1o.* | DONE |
| 3b | **Progress -> CLI.** `EnhanceProgress(StepName, StepIndex, StepCount, StepPercent, Eta)`; `SharpenPipeline.ProcessAsync` stamps a per-step boundary tick + forwards each enhancer-backed step's sub-step ticks (RC-Astro NDJSON `IProgress<float>`, relayed via `StepProgressRelay`); CLI `EnhanceProgressConsole` printer (step transitions + ~10% sub-step ticks) wired on both `image sharpen` (direct) and `stack --enhance` (via `StackingPipeline` -> `MasterPostProcessor`). Boundary-tick + sub-step-forwarding pinned by `SharpenPipelineTests`. | DONE |
| 3c | **Viewer enhance action (tianwen-fits).** Landed in the standalone FITS viewer, NOT the GUI: `tianwen-gui` has no document-viewer tab (its `VkImageRenderer` surfaces are the live preview / guider / planetary view), so the interactive "open an image -> enhance it" action belongs in `tianwen-fits` (the real viewer host with `ViewerController` + file-open + `AdoptImageAsync`). Shared viewer layer: `ToolbarAction.Enhance` + a presence-gated toolbar button (renderer `EnhanceAvailable`, hidden where no pipeline) + `ViewerState.{IsEnhancing,EnhanceProgressPct,PreferredEnhanceBackend}`; `EnhanceActions.EnhanceAsync` (route-only) runs `SharpenPipeline.ProcessAsync` off the render thread (`SharpenRequest.DeblurFirst`/`Canonical` per `SupportsDeblur`), per-step progress -> status line, result adopted via `AstroImageDocument.AdoptImageAsync` and swapped in on the render thread (`ViewerController._enhanceTask` + `TryApplyPendingEnhance`, the SkyMap async-result hand-off; no spin-render, so the AI work doesn't contend the GPU). Left-click runs; right-click cycles backend (shown on the button label), snapshotted into `EnhanceOptions` per click -- no global. `EnhanceImageSignal` + 'E' shortcut. `tianwen-fits` registers `AddRcAstroAi()`. Pinned by `EnhanceActionsTests`. A dedicated GUI viewer tab is deferred. | DONE |
| 3d | **Server enhance endpoint.** `AddRcAstroAi()` wired into `TianWen.Server` (registers `SharpenPipeline`; the RC-vs-SAS probe stays deferred so startup spawns no `rc-astro`). `HostedImageEnhancer` (single-flight `Interlocked` gate) runs `SharpenPipeline.ProcessAsync` on a background task tied to `ApplicationStopping` (not the request), with a synchronous `IProgress` relay that swaps an immutable `EnhanceStatusDto` snapshot atomically (lock-free read). `POST /api/v1/image/enhance` (path-in/path-out, `EnhanceRequestDto` body) returns `Enhance started` / `409` / `404` / parse-error; `GET /api/v1/image/enhance/status` returns the concrete `EnhanceStatusDto`; `ENHANCE-PROGRESS` + `ENHANCE-COMPLETED` push through `EventBroadcaster` -> `EventHub` (same `WebSocketEventDto` + `Dictionary<string,object?>` path as the session events). Backend/tuning parse goes through the new shared `EnhanceOptions.TryParse` -- the single source of truth now also used by `image sharpen` + `stack --enhance` (their duplicated inline blocks were collapsed into it). AOT-safe: `EnhanceRequestDto` / `EnhanceStatusDto` / `ResponseEnvelope<EnhanceStatusDto>` registered in `HostingJsonContext`; **published `win-arm64` (only the 2 documented LibUsbDotNet rollups, no new IL warnings) and smoke-tested the binary** -- body binding, the shared parser over the wire, single-flight 409, the concrete status DTO, a live `ENHANCE-PROGRESS` WS frame, and a full SAS run to `succeeded:true` all verified. Pinned by `EnhanceOptionsTests`. | DONE |

`IStellarSharpener` and `IGradientCorrector` stay SAS (no CLI equivalent).

**Deferred / nice-to-have:** the Phase 3d server endpoint is **single-flight** (one enhance at a
time, no job id; the client polls the status singleton or subscribes to the WebSocket). A job-id +
small-queue model (concurrency / history / targeted cancel) is captured separately in
[server-enhance-job-model.md](server-enhance-job-model.md) -- build only when a client needs it.

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
    int?   DenoiseIterations = null);  // the enhance's denoise PASSES, null = gated (never nxt --it, which stays at 1)

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

---

## Stellar-sharpen is opt-in, and hard-skipped when a deblurrer is live

`image sharpen --stellar-sharpen`, default **OFF**. The SAS stellar sharpener (NAFNet) over-sharpens
already-tight star cores into square white clipped blocks; and when a deblurrer (BlurX) is live the
stars are already tightened, so the option is **hard-skipped even if requested**, with a warning.
This is why the BlurX-first step program carries no stellar-sharpen stage at all
([../architecture/stacking-render-pipeline.md](../architecture/stacking-render-pipeline.md) section
3).

Role split, for the same reason: RC-Astro serves `IStarRemover` / `INonStellarDeconvolver` /
`IDenoiseEnhancer` (sxt / bxt / nxt) and is preferred + license-gated; SAS ONNX is the free fallback
tier; `IStellarSharpener` and `IGradientCorrector` stay SAS, because RC has no equivalent.
