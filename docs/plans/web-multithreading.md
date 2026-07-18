# Web Multithreading — getting off the single WASM thread (research)

**Status: NOT STARTED (research captured, 2026-07-18).** Forward-looking, *not* a fix for a current
problem: the catalog-load freeze that motivated this is already solved by AOT (see below). This plan
records what real parallelism in the browser would take, the GitHub-Pages constraint that gates it,
and the recommendation. Companion to [web-webgpu.md](web-webgpu.md) (the GPU-compute alternative) and
[web-showcase.md](web-showcase.md) (the app itself).

## The problem it would address (and why it's already mostly solved)

`TianWen.UI.Web` runs single-threaded. The two heavy blocks — catalog init (`Db.InitDBAsync`) and the
tonight's-best sweep (`PlannerActions.ComputeTonightsBestAsync` → `ObservationScheduler.TonightsBest`,
a nested RA/Dec grid loop) — are long *synchronous* stretches with no yields, so they freeze the UI
thread while they run.

**AOT already fixes the freeze.** Measured (recorded in web-showcase.md, 2026-07-17): catalog init
23 s → 0.55 s, sweep 26 s → 0.59 s (24–42×). The freeze the user saw was the *interpreted dev server*;
the deployed AOT build is native-desktop-class. So this plan is about wanting real parallelism for its
own sake (buttery first paint, future in-browser image processing), not about unfreezing the planner.

## Why async/await doesn't help — the concurrency model

Blazor WASM's C# `async`/`await` compiles onto the browser's single-threaded event loop. `await`
*yields* to the loop (frees it to paint/handle input) but does **not** parallelize: synchronous code
between awaits runs to completion and blocks everything. Awaiting a CPU-bound call runs it
synchronously first; only the continuation is deferred. `Task.Run` in a no-threads WASM build has no
thread pool to dispatch to, so the delegate just queues as another continuation on the same loop —
concurrency, never parallelism. (This is why `SetStatusAsync` does `StateHasChanged(); await
Task.Delay(30)` — the `Task.Delay` yields a *macrotask* so the browser can paint before the next
blocking block. Awaiting a resolved promise is a microtask and still starves the paint.)

Real parallelism therefore needs a genuine second thread (a Web Worker) or the work to leave the CPU
(GPU compute — see web-webgpu.md). There is no third option.

## The constraint stack (why "just use threads" hits a wall on free Pages)

```
real .NET threads (WasmEnableThreads, Task.Run parallelizes)
        │ built on
SharedArrayBuffer (one WebAssembly.Memory many workers map)   ← the gated layer
        │ requires
crossOriginIsolated === true
        │ requires two response headers
COOP: same-origin  +  COEP: require-corp   ← GitHub Pages cannot send these
        │ underneath
Web Workers (browser background threads)   ← always available, no headers
```

Web workers themselves are free on Pages. What's gated is **shared memory**: managed .NET threads all
live in one `WebAssembly.Memory`, which multiple workers can only see if it's a `SharedArrayBuffer`,
which needs cross-origin isolation, which needs COOP/COEP headers. **GitHub Pages gives no way to set
response headers** (no `_headers` file, no server config — verified live 2026-07-18: the site sends only
`Content-Type` + `Cache-Control`). So `WasmEnableThreads` is blocked on Pages without a workaround.

## Option A — wasm-threads (shared heap)

`<WasmEnableThreads>true</WasmEnableThreads>`. Worker threads share the same heap, so the big
`ICelestialObjectDB` graph stays put and `BackgroundTaskTracker.Run`/`Task.Run` become genuinely
parallel **with almost no app-code change** (the tracker already emits `Task.Run`). This is the elegant
one — flip a flag, existing background calls start threading.

Costs / caveats:
- **Needs cross-origin isolation on Pages** → add a `coi-serviceworker.js` shim (a service worker that
  re-injects COOP/COEP client-side and reloads once; known to work on Pages), or move hosting off Pages.
  We have no service worker today.
- COEP side-effects: once on, every cross-origin subresource must be CORP/CORS-clean. Our assets are
  same-origin/baked (comet JSON, fonts) so mostly fine — audit the geolocation/font paths.
- Bigger download (separate multithreaded runtime build) + measure first-load.
- **Blazor's renderer + component lifecycle + `IJSRuntime` are main-thread-affine** — background
  threads may do pure compute but must marshal any component/JS touch back via the dispatcher. The
  current `ComputeAsync` interleaves `StateHasChanged`/`IJSRuntime` with the compute, so you'd split the
  DB init/sweep into pure-compute-on-worker + marshal-results-back.
- Verify AOT + threads publish interaction.

## Option B — message-passing worker (no sharing)

A plain Web Worker running a **second, independent .NET runtime** (its own `WebAssembly.Memory`/heap).
No SharedArrayBuffer, no COOP/COEP — **works on free Pages today**. Genuine parallelism (real OS thread).

Costs / caveats:
- **No shared heap**: the worker can't see the main runtime's `ICelestialObjectDB`. You marshal flat
  inputs/outputs across the boundary — cheaply for numeric arrays via **Transferable** `ArrayBuffer`s.
  The sweep fits: flatten candidate objects (RA/Dec/mag/type — the same flattening the sky map already
  does) into a Transferable, score in the worker, transfer back the top-100. Anything needing the live
  DB *graph* forces the worker to rebuild its own catalog (expensive).
- **~2× runtime RAM** + instantiation latency (the `.wasm`/assemblies are fetched once and browser-
  cached, so not 2× *download*; each runtime instance allocates its own memory).
- No first-class Blazor "run on a worker" API — hand-roll against the `dotnet.js` runtime API or pull a
  community lib (e.g. SpawnDev.BlazorJS.WebWorkers). Sits outside Blazor's main-thread model.
- Running the sweep in the worker needs *C#*, hence the second runtime; rewriting scoring in JS would
  fork it out of C# (rejected — breaks single-source-of-truth).

## Option C — GPU compute (header-free, but not CPU threads)

Dispatch the data-parallel sweep to the GPU and read the result back async. No workers, no
SharedArrayBuffer, no headers — dodges this whole stack. **Requires WebGPU** (WebGL2 has no compute) and
only fits the data-parallel half (not the serial catalog init). Full treatment in
[web-webgpu.md](web-webgpu.md).

## The Blazor question

The web app's Blazor surface is thin (grepped: 7 `@inject`, 5 `StateHasChanged`, 4 `@onclick`, 3
`PageTitle`, 2 `NavigationManager`, 2 `@bind`, 1 `IJSRuntime`, 1 `HeadOutlet`) and the perf-critical GL
path already runs on bare `[JSImport]`/`[JSExport]` in WebGl.Renderer. So dropping Blazor is *feasible*
(we're AOT/reflection-clean, no reflection wall) — reimplement the chrome as raw DOM, routing as a
`popstate` listener, DI by building a `ServiceProvider` in `Main`, `IJSRuntime`→`[JSImport]`. **But:**
- AOT is orthogonal to Blazor — you AOT-compile Blazor too (we already do). "We output AOT" doesn't
  imply "we don't need Blazor," only "dropping it wouldn't cost reflection niceties."
- The real anchor is the two shared components — `WebGlCanvas` + `CanvasTextOverlay` — which are Blazor
  `.razor` components in WebGl.Renderer, **shared with chess**. De-Blazoring tianwen forks or diverges
  those.
- **Blazor is not required for any threading path.** GPU compute doesn't care about it; wasm-threads
  works with it (with marshaling discipline); bare would only give a cleaner threading substrate.

**Verdict: keep Blazor** unless payload/threading-substrate becomes a goal in itself.

## Recommendation + decision triggers

**Now: build none of it.** AOT solved the freeze; the shipped site is fast.

| Option | Verdict | Trigger that flips it on |
|--------|---------|--------------------------|
| wasm-threads (A) | park | Real CPU-parallel work lands in the browser (in-browser stacking/image processing). Only then is the coi-serviceworker + bigger-download + COEP tax worth it. Not for the planner. |
| message-passing worker (B) | park | You want the *existing C#* sweep off-thread on free Pages and accept 2× runtime RAM + marshaling plumbing. Middle option; rarely the best. |
| GPU compute (C) | spike-worthy | You want the sweep re-scored *interactively* (time-scrub). Header-free + a real feature — see web-webgpu.md. |
| de-Blazor | keep | Payload becomes a measured problem, or the leanest threading substrate is a goal in itself. |

## Relevance to Tycho-2 (the deferred web catalog)

The Tycho-2 bulk decode (lzip + parse ~2.5M records) is the archetypal threading candidate — serial,
GPU-hostile (so GPU compute can't help it), and it's literally the workload that *wedged the
interpreted page* and motivated `Lightweight=true` stripping `tyc2.bin.lz`. Option A (wasm-threads,
shared heap) would decode it off the UI thread straight into the shared DB; Option B could decode in
a worker and transfer a flat star buffer back. **But two caveats blunt the case:** (a) cooperative
**chunking** (decode N stars → yield → repeat) avoids the wedge with no threads / no COOP/COEP, just
slower wall-time; (b) AOT makes the decode far faster, likely sub-second, so the wedge may be moot.
And the dominant Tycho-2 cost — the **~30 MB download** — is orthogonal to threading entirely; that's
a data-delivery problem (lazy fetch / decoded IndexedDB snapshot / spatial tiling), not a parallelism
one. Net: threading is a *possible but not the cleanest* lever for Tycho-2, and it doesn't touch the
real blocker. See web-webgpu.md and web-showcase.md's deferred item.

## Facts / invariants for a future implementer

- Live Pages site sends **no COOP/COEP** and Pages can't add them — any shared-memory path needs a
  coi-serviceworker shim or off-Pages hosting.
- `BackgroundTaskTracker.Run` already calls `Task.Run` (DIR.Lib) — thread-ready; only the runtime
  capability is missing. It's shared with the GUI/TUI hosts, so don't fork it.
- The catalog DB is a large read-mostly managed dictionary graph (`_objectsByIndex` ~32k, cross-index
  ~39k, etc.), built once by the idempotent `InitDBAsync`. It has **no serialization/blittable
  boundary**, so it can't be transferred/shared across a worker copy boundary as-is — only the sweep's
  flat candidate arrays can.
- AOT is command-line only (`-p:RunAOTCompilation=true` in pages.yml); local dev is interpreted (hence
  the dev freeze). `-p:Lightweight=true` strips the 30 MB Tycho-2 catalog on the web build.
