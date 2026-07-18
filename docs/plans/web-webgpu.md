# Web WebGPU — GPU compute + a WebGPU render backend (research)

**Status: NOT STARTED (research captured, 2026-07-18).** Records whether/how to add a WebGPU backend to
`WebGl.Renderer`, whether our shaders reuse, and — the strongest motivator — using **GPU compute** to
parallelize the planner sweep without the SharedArrayBuffer/COOP/COEP wall that blocks CPU threads on
GitHub Pages. Companion to [web-multithreading.md](web-multithreading.md) (the CPU-thread alternatives)
and [web-showcase.md](web-showcase.md).

## Current state

- `WebGl.Renderer` is **WebGL2-only** — no fallback path even to WebGL1. It's one implementation of the
  shared `DIR.Lib.Renderer<TSurface>` abstraction (the desktop `VkRenderer` is the other), so the seam
  for a second backend already exists.
- **Shaders are GLSL ES 3.00 source strings authored in C#**, hand-transcribed 1:1 from the desktop
  Vulkan GLSL 450 (`VkSkyMapPipeline.cs` → `WebGlSkyMapPipeline.cs`, `VkPipelineSet.cs` →
  `WebGlPipelines.cs`): shader *bodies byte-identical*, only boilerplate (`#version`, `precision highp`,
  push-constants→uniforms) and a Y-flip differ. The custom-pipeline seam (`RegisterPipeline`) takes raw
  GLSL strings from consumers.
- **No compute shaders exist anywhere** in the three repos — all GPU work is vertex/fragment. The image
  stretch is a fragment shader + full CPU mirror; stacking/wavelet is pure CPU.

## Shader intake: why WebGL was the cheap port and WebGPU won't be

Each API eats a different shader form — this is the crux of "can we reuse our shaders":

| API | Accepts | Toolchain needed |
|-----|---------|------------------|
| Vulkan (SDL desktop) | SPIR-V | GLSL→SPIR-V ahead-of-time (`Vortice.ShaderCompiler`/glslang) |
| **WebGL2 (web today)** | **GLSL ES source** | **none — the browser's GL driver compiles it at runtime** |
| WebGPU (this plan) | **WGSL only** | GLSL→WGSL transpile (naga / Tint); browser WebGPU dropped SPIR-V ingestion |

WebGL2 is the odd one out that swallows GLSL source directly, which is exactly why porting the sky atlas
to the web was "transcribe the boilerplate, ship the strings" with no build-time shader stage. WebGPU is
Vulkan-shaped: it will *not* take our GLSL, so a WebGPU backend reintroduces a compile/transpile step
that WebGL let us skip.

### Shader-reuse options for WebGPU
1. **Transpile GLSL→WGSL** (naga or Tint). Built-in shaders are a fixed set → transpile-and-embed at
   build time is clean, and desktop *already* compiles GLSL→SPIR-V, so **GLSL→SPIR-V→WGSL** is a natural
   extension of an existing step. Friction: consumer-supplied custom-pipeline GLSL strings (how
   `WebGlSkyMapPipeline` plugs in) would need a *runtime* GLSL→WGSL transpiler in the browser
   (naga-in-WASM), or a breaking change to make the custom-pipeline contract accept WGSL/SPIR-V.
2. **Hand-port WGSL twins** — mirrors today's "byte-identical body" discipline but now *three* dialects
   (GLSL 450 / GLSL ES 3.00 / WGSL) to keep in sync. More maintenance.

## The real motivator: GPU compute to sidestep the single thread

The strongest reason to touch WebGPU isn't rendering (WebGL2 covers what we draw) — it's **compute
shaders as a way to parallelize the planner sweep without CPU threads**, and therefore without the
SharedArrayBuffer/COOP/COEP wall that blocks wasm-threads on GitHub Pages. You dispatch the work to the
GPU, the WASM thread stays free to render/handle input, and you read the result back async. **No
workers, no shared memory, no headers.**

Which of our workload fits:
- **Tonight's-best sweep — yes.** `for(ra) for(dec) score-each-object → Take(100)` is embarrassingly
  parallel; the alt/az trig is what the sky-map vertex shader already does. Flatten candidates
  (RA/Dec/mag/type — the sky map already builds such buffers) into a GPU buffer, run a WGSL compute pass,
  read back scores, sort/take-100 on CPU.
- **Catalog init — no.** gzip/lzip decompression + string parsing + dictionary building is serial
  pointer-chasing the GPU is bad at. (Already 0.55 s on AOT anyway.)

Economics: on AOT the sweep is already ~0.59 s, so GPU compute earns its keep specifically when the
sweep becomes **interactive** — live re-scoring as you drag a time slider ("what's up at 2 a.m. / next
Tuesday"), or a much bigger catalog. For a one-shot compute-on-load it's a lot of WGSL machinery to save
half a second. (Distinct from the shipped [skymap-time-scrub.md](skymap-time-scrub.md), which re-draws
the sky *map* on a time offset with no planner recompute; this would re-run the planner *scoring*.)

## Fallback: WebGPU → WebGL2

Mandatory, not optional. As of early 2026 WebGPU is stable in Chrome/Edge and Safari 18, but Firefox
shipped it only on Windows (other platforms rolling out), plus older/locked-down browsers — so
WebGPU-only isn't viable. Feature-detect `navigator.gpu` + `requestAdapter()` at startup and fall back.
Because the renderer is already abstracted (`Renderer<TSurface>`, `WebGlRenderer` is one impl), a
`WebGpuRenderer` sibling slots in and startup picks one. For the compute path the fallback is simply
"run the sweep on the CPU like today" (AOT-fast) — graceful, no correctness risk.

## What a WebGPU backend must reimplement

The C#-side opcode layer (`WebGlContext` command/vertex buffers, `WebGlRenderer` draw methods, the
opcode emission) is largely GL-agnostic — it just appends fixed-format command+vertex data — and could
be reused against a new JS shim. What's WebGL-specific and must be rewritten:
- The JS `flush()`/`syncAtlas()` opcode interpreters against `GPUCommandEncoder`/`GPURenderPassEncoder`
  instead of `WebGL2RenderingContext`.
- Pipelines become `GPURenderPipeline` objects with explicit vertex-buffer-layout + bind-group-layout
  descriptors (vs `gl.vertexAttribPointer` per draw).
- Uniforms become explicit bind groups (vs the ad-hoc `uProj`/`uColor`/`uExtra` name lookups).
- Blend state becomes part of the pipeline descriptor (vs a per-draw `blendFuncSeparate`).
- The shaders (built-in + consumer custom-pipeline) become WGSL (see shader-reuse options above).
- Add a compute path: `GPUComputePipeline` + `dispatchWorkgroups` + a mapped readback buffer, plus new
  opcodes / a bridge call for dispatch + async result pickup.

There is no .NET/Blazor WebGPU binding — it'd be a hand-rolled JS shim exactly like the current WebGL
one (which already uses `[JSImport]`/`[JSExport]`).

## Recommendation + trigger

**Defer** until a workload justifies it. WebGL2 fully covers current rendering; WebGPU's headline win
(compute) has zero consumers today; support isn't universal so you'd maintain a WebGL2 fallback + a
third shader dialect regardless.

**Trigger:** you want the planner sweep re-scored *interactively* (time-scrub / live "what's up now"), or
GPU compute for a heavier browser workload (planetary stacking / image stretch on the web). That's the
one place GPU compute has a real consumer *and* it stays on free Pages with zero header hacks.

### If pursued — the bounded spike
Branch-only, measure before committing:
1. Feature-detect `navigator.gpu`; stand up a minimal `WebGpuRenderer` behind the `Renderer<TSurface>`
   seam with the existing `WebGlRenderer` as fallback.
2. One WGSL **compute** pass that re-scores the sweep from a flat candidate `ArrayBuffer`, running beside
   the current CPU path (which stays the fallback), wired to a time slider.
3. Prove the **GLSL→WGSL** path on *one* render pipeline (transpile via naga/Tint from the existing
   GLSL→SPIR-V) before committing to the whole port.
4. **Measure** whether non-blocking live re-scoring feels worth the WGSL + backend cost. If yes: a real
   feature + a proven WebGPU path. If no: a spent branch, not a rewrite.

### If it graduates to a full backend
- `WebGpuRenderer : Renderer<TSurface>` alongside `WebGlRenderer`, runtime WebGPU→WebGL2 fallback.
- Build-time **GLSL→SPIR-V→WGSL** transpile (reuse the desktop GLSL→SPIR-V step); evolve the
  custom-pipeline contract to accept SPIR-V/WGSL so consumer pipelines (`WebGlSkyMapPipeline`) come along.
- Keep the shared-geometry / shader-duplicated split (`SkyMapGpuGeometry`/`SkyMapUbo` stay the single
  geometry source; each backend interprets the same std140 byte layout).

## Facts / invariants for a future implementer

- Browser WebGPU is **WGSL-only** (no SPIR-V ingestion) — reuse needs transpile or WGSL twins.
- The `Renderer<TSurface>` abstraction + the GL-agnostic C# opcode layer already support a second
  backend; the WebGL-specific parts are the JS `flush()`/`syncAtlas()` shim + the shader sources + the
  `RegisterPipeline` raw-GLSL contract.
- Shader bodies are kept byte-identical across backends by discipline (string sanity tests in
  WebGl.Renderer; ASCII-only per the GLSL rule — Vortice.ShaderCompiler chokes on non-ASCII).
- No compute shaders exist yet; the sweep is the first plausible consumer.
- WebGL2 (GL ES 3.0) has **no** compute shaders — the abandoned "WebGL2-compute" extension never
  shipped; browser GPU compute = WebGPU, full stop.
