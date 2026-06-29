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
| 3 | Wire `AddRcAstroAi()` into composition roots + docs (CLAUDE.md arch note, TODO) DONE; options surface (per-product params + "prefer RC" toggle) in CLI/GUI + NDJSON progress -> UI still TODO | PARTIAL |

`IStellarSharpener` and `IGradientCorrector` stay SAS (no CLI equivalent).
