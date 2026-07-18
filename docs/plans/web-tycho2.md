# Tycho-2 in the Browser Atlas (plan)

**Status: P1 SHIPPED (2026-07-18); P2-P4 measurement-gated.** Bring the full ~2.5M-star Tycho-2
catalog to the web sky atlas, which used to show only the ~8.6k HR bright stars (`Lightweight=true`
strips `tyc2.bin.lz` from the WASM bundle). Grew out of the threading/WebGPU investigation
([web-multithreading.md](web-multithreading.md), [web-webgpu.md](web-webgpu.md)), which established
that **this is a data-delivery problem, not a compute/GPU one**. Companion to
[web-showcase.md](web-showcase.md).

## What P1 shipped (2026-07-18)

- **Injection seam (Lib):** `ICelestialObjectDB.TryLoadTycho2BulkFromCompressed(byte[])` — default
  no-op (embedded/desktop hosts, test stubs), overridden by `CelestialObjectDB` to `LzipDecoder`-
  decompress the fetched bytes and publish `_tycho2Data`/`_tycho2StreamCount` (idempotent; publishes
  `_tycho2Data` last so `Tycho2StarCount`/`CopyTycho2Stars` never see a torn state). **Display-only:**
  it wires ONLY the flat star records, not the GSC-bounds spatial index (`_tycho2RaDecIndex`) or the
  HD/HIP cross-maps or the high-pm sidecar (~11 stars, rail-clamped pm — invisible at plot scale).
  Pinned by `Tycho2BulkInjectionTests` (fresh-DB inject → count > 2M + decoded records sane;
  idempotent re-inject; empty-input false).
- **Lazy fetch (web host, `Planner.razor`):** `EnsureTycho2AtlasAsync` fires once, on the **first
  Sky-Atlas paint** (guard set in `RenderFrame`'s SkyMap branch — not `ApplyViewFromLocation`, so the
  pipeline is guaranteed to exist even for a deep-link; covers chip + back/forward too). Yields, then
  `Http.GetByteArrayAsync("tyc2.bin.lz")`, `Db.TryLoadTycho2BulkFromCompressed`, flatten via the
  shared `SkyMapState.FillTycho2StarVertices` (dt=0), `_skyTab.SubmitTycho2Stars`, `RenderFrame`.
  Best-effort: a 404 (dev server without the CI-baked asset) or any failure leaves the HR field.
- **Swap-in (web pipeline, `WebGlSkyMapPipeline`):** `SubmitTycho2Stars` stashes the built buffer;
  `ApplyPendingTycho2` (called each frame from `Draw`) does the render-thread `CreateBuffer` + flips
  the star draw over to it — a **switch, not an overlay** (additive blend would double every shared
  star), the browser analogue of the desktop `VkSkyMapPipeline` HIP-seed → Tycho-2 swap. HR stays
  allocated (~180 KB) as the bootstrap/fallback.
- **Delivery (`pages.yml` + `.gitignore`):** a CI step copies the LFS `tyc2.bin.lz` into `wwwroot/`
  before publish (mirrors the comet-JSON bake; guards against a stale LFS pointer); the staged asset
  is gitignored so it never lands in the source tree.
- **Reuse, not new code:** the flatten is the desktop's `SkyMapState.FillTycho2StarVertices` (NOT a
  new `BuildTycho2StarInstances` — one path); the zoom-aware mag limit is the shared `SkyMapUbo`
  (already wired for the HR field), so nothing new was needed there.

**Not yet measured:** the AOT decode+flatten wall-time (gates whether P2 is worth it) needs a
*published* Lightweight+AOT build — dev is interpreted (slow) and 404s the asset (HR-only). See the
open questions.

## Where we are

The web atlas deliberately built the *real* instanced GPU pipeline (`WebGlSkyMapPipeline` +
`DrawInstanced`) so that adding Tycho-2 is "a data + payload change, not a code change"
(web-showcase.md P3 decision). The current HR-only field is `Lightweight=true` removing the embedded
`tyc2.bin.lz` (`TianWen.Lib.csproj:54`); `ReadTycho2Bulk` already no-ops when the manifest entry is
absent, so nothing crashes — the data is simply not there.

## The three costs (and which this plan actually fights)

| Cost | Status | Lever |
|------|--------|-------|
| **Render** 2.5M instanced stars | **already solved** — `DrawInstanced` renders it; desktop Vulkan proves the scale (~50 MB VRAM instance buffer) | — |
| **Decode** lzip decompress + flatten to star buffer | serial *today* on WASM, but **parallel across lzip members** via `LzipDecoder.Parallel.For` (unlocked by wasm-threads + a multi-member bake) | P1 serial → P2 parallel |
| **Payload** ~30 MB download | **the dominant blocker** — untouched by threads or GPU | P1 lazy-fetch → P3 IndexedDB cache → P4 tiling |

## Disciplined framing (value gate)

The atlas is **already a fine showcase** with the ~8.6k naked-eye HR stars. Full Tycho-2 (down to
~mag 11.5) is a density/"real sky" *wow* upgrade, not essential. So every expensive phase below is
**measurement-gated** — ship the cheapest working version first, measure, and only take on the
infrastructure (wasm-threads, tiling) if the numbers justify it.

**Scope decision — display-only, not searchable (v1).** The web atlas needs tyc2 for *rendering*, not
for F3 search / cross-identity. So the decode path builds **only the flat star-instance buffer** (via
the `CopyTycho2Stars` shape) and **skips** the desktop's DB dictionary integration
(`hip_to_tyc`/`hd_to_tyc` cross-maps, per-star `TryGetTycho2Star` lookup). That keeps the parse
per-record-parallel and avoids the serial dictionary-build. Searching individual TYC stars is deferred.

## Phasing

| Phase | Scope | Risk | Ships |
|-------|-------|------|-------|
| **P1 ✅ DONE** | **Lazy-fetch + serial decode.** tyc2 stays un-embedded for web (`Lightweight`); shipped as a same-origin static asset (CI-staged into wwwroot); fetched on **first atlas-open**; serial decode + flatten off the first-paint path; swapped over the HR seed. AOT decode wall-time still to measure. | Med | Full-density atlas, no first-load bloat |
| **P2** | **Parallel decode** (gated on P1 decode being too slow). Bake tyc2 **multi-member**; `WasmEnableThreads`; `coi-serviceworker` shim; COEP subresource audit; marshal decode off the Blazor main thread. `LzipDecoder.Parallel.For` then parallelizes for free. | High | Faster decode |
| **P3** | **IndexedDB decoded-snapshot cache** (gated on repeat-visit UX). Cache the decoded flat buffer; later visits skip download + decode. | Med | Instant repeat visits |
| **P4** | **Spatial tiling** (deferred). Pre-tile tyc2 by sky region, fetch tiles on pan/zoom. Progressive/instant load, smallest incremental download. | High | Progressive load |

Incremental value: **P1 ships the feature.** P2/P3/P4 are each independently justified only by a
measured problem P1 surfaces.

## P1 — lazy-fetch + serial decode (the shippable core)

1. **Un-embed for web, ship as a static asset.** Keep `Lightweight=true` stripping the *embedded*
   resource (so it never bloats the WASM bundle), but publish `tyc2.bin.lz` into `wwwroot/` — exactly
   the model the baked `comets-sbdb.json` already uses (`pages.yml` writes it into wwwroot; the app
   fetches it same-origin via `HttpClient` — see `Program.cs` `AddAstrometry(cometQueryUri:)`). Add a
   pages.yml step that copies/bakes `tyc2.bin.lz` into wwwroot on deploy.
2. **Fetch on first atlas-open, not startup.** The planner (default view) must stay fast — it's
   DSO-only and doesn't need tyc2. Trigger the fetch when the user first switches to the Sky Atlas (or
   first zooms past HR density), with a progress indicator (the `.status`/`.catalog-loading` chrome
   pattern). The planner never pays the 30 MB.
3. **Decode without wedging.** Feed the fetched `byte[]` to `LzipDecoder.Decompress` (needs a
   from-bytes entry point alongside the current embedded-manifest path in `ReadTycho2Bulk`). On
   single-thread WASM this runs serial; keep the UI responsive via cooperative **chunking** (decode /
   flatten in slices with `await Task.Delay`-style yields, like `SetStatusAsync`). Measure the AOT
   wall-time — this is the number that gates P2.
4. **Flatten to star instances.** New `SkyMapGpuGeometry.BuildTycho2StarInstances(...)` mirroring
   `BuildHrStarInstances`: each `Tycho2StarLite` (RA hours, Dec deg, V mag, B−V) → the 5-float instance
   (unit vector x/y/z + mag + bv, `SkyMapState.FloatsPerStar`). Consume via the `CopyTycho2Stars`
   span-paging shape (decode-into-buffer, no per-star dictionary).
5. **Merge with HR without double-drawing.** Additive blend double-counts a star drawn twice
   (`BuildHrStarInstances` already avoids combining with the figure seed for this reason). Tycho-2
   subsumes the bright stars, so either (a) **replace** the HR field with tyc2 outright, or (b)
   **split by magnitude** — HR for the brightest (better color/photometry) + tyc2 for mag > HR-limit.
   Pick (b) if HR's colors look better on the bright end; else (a) is simpler. Upload as a second
   persistent instance buffer + a second `DrawInstanced`, or rebuild the one buffer.
6. **Zoom-aware mag limit.** 2.5M stars at full-sky zoom is visual mush + overdraw; gate the drawn
   magnitude limit on zoom (the sky map already has a zoom-aware mag limit for markers) so only the
   appropriate density draws per view.

## P2 — parallel decode (measurement-gated)

Only if P1's AOT serial decode is a real UX problem (rule of thumb: a multi-second stall even with a
progress bar). Then:
- **Bake multi-member.** Set `LzipOptions.MemberSize` in the tyc2 preprocess/bake so `tyc2.bin.lz`
  is many independent members; the default is single-member → `LzipDecoder` takes the serial
  `DecompressSingleMember` path and `Parallel.For` never engages. **Trade-off:** multi-member slightly
  worsens the compression ratio (each member resets the LZMA dictionary) and payload is the dominant
  cost — pick `MemberSize` large enough that the ratio hit is negligible (measure both).
- **Enable threads.** `<WasmEnableThreads>true</WasmEnableThreads>`. `LzipDecoder.Parallel.For` then
  dispatches across the mono thread pool → real multi-core decompress, **zero new decode code** (the
  parallel loop already exists). This is the "ready-made wasm-threads consumer."
- **Cross-origin isolation on Pages.** `SharedArrayBuffer` needs COOP/COEP, which Pages can't set → add
  a `coi-serviceworker.js` shim (re-headers responses client-side + one reload) or host off Pages.
  Audit COEP subresource fallout (comet JSON + fonts are same-origin so fine; check geolocation).
- **Blazor marshaling.** The decode must run off the main thread but marshal the finished buffer + any
  `StateHasChanged`/JS touch back via the dispatcher (Blazor renderer is main-thread-affine).
- Also parallelize the **flatten** (`Tycho2StarLite[]` → instance floats) — per-record independent, a
  natural `Parallel.For`.
- Measure: multi-core decode speedup **vs** the MT-runtime download-size + first-load cost + the
  multi-member ratio hit. Full analysis in web-multithreading.md.

## P3 — IndexedDB decoded-snapshot cache (measurement-gated)

Pay the download + decode **once**; cache the decoded flat star buffer (a `Float32Array`/blob) in
IndexedDB keyed by a catalog version. Later visits load the decoded buffer directly — no 30 MB
re-download, no re-decode. Orthogonal to P1/P2 (a caching layer on top). Generalizes the deferred
"decoded-DB IndexedDB snapshot" idea from web-showcase.md, scoped to just the tyc2 star buffer.

## P4 — spatial tiling (deferred)

Pre-tile tyc2 by sky region (HEALPix or a simple RA/Dec grid), fetch only the visible tiles on
pan/zoom, cache per-tile. Smallest incremental download + progressive load, but requires a tiling
scheme + tile index + fetch-on-pan logic. Only if payload/instant-load becomes critical (e.g. mobile).

## Integration points / invariants for the implementer

- **Delivery precedent:** `comets-sbdb.json` — a same-origin static asset baked into wwwroot by
  pages.yml, fetched at runtime via `HttpClient` (`Program.cs` `AddAstrometry(cometQueryUri:)`). tyc2
  follows the same shape (binary + lzip decode instead of JSON).
- **Decode API:** `LzipDecoder.Decompress` (multi-member `Parallel.For`, single-member serial) — needs
  a from-`byte[]` entry point next to `ReadTycho2Bulk`'s embedded-manifest path. `LzipEncoder` +
  `LzipOptions.MemberSize` bakes multi-member.
- **Star API:** `Tycho2StarLite(RaHours, DecDeg, VMag, BMinusV, PmRa…, PmDec…)`; `CopyTycho2Stars(
  Span<Tycho2StarLite>, startIndex)` is the paged flatten source. `SkyMapState.FloatsPerStar` = 5.
- **Render is ready:** `DrawInstanced` + `WebGlSkyMapPipeline` star pipeline; `SkyMapGpuGeometry`
  (Abstractions) is the shared geometry builder — add `BuildTycho2StarInstances` beside
  `BuildHrStarInstances`. Do NOT double-draw HR + tyc2 under additive blend (replace or mag-split).
- **Keep the bundle lean:** never re-embed tyc2 (`Lightweight=true` stays); it's a fetched asset.
- **Display-only v1:** tyc2 stars are NOT added to the searchable `ICelestialObjectDB` — flat instance
  buffer only. F3 search of individual TYC stars is deferred.
- **GPU compute is the wrong tool** for the decode (LZMA range-decode is sequential-within-member);
  parallelism is across-member CPU threads (P2), not GPU. WebGPU is irrelevant to this feature.

## Open questions / gates

- Measured AOT serial decode wall-time (gates P2). Unknown until P1 ships.
- Multi-member `MemberSize` sweet spot: decode-parallelism vs compression-ratio (payload) — measure both.
- Published `_framework` + tyc2 asset size budget (web-showcase.md already flags a payload-budget item).
- HR-replace vs HR+tyc2-mag-split — a visual-quality call on bright-star color.
