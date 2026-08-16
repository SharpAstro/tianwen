# Tycho-2 in the Browser Atlas (plan)

**Status: P1 + P3 SHIPPED; P2 and P4 MEASURED and merged into one bake change (see "The measurement
that settled P2 and P4", 2026-08-16).** Bring the full ~2.5M-star Tycho-2
catalog to the web sky atlas, which used to show only the ~8.6k HR bright stars (`Lightweight=true`
strips `tyc2.bin.lz` from the WASM bundle). Grew out of the threading/WebGPU investigation
([web-multithreading.md](web-multithreading.md), [web-webgpu.md](web-webgpu.md)), which established
that **this is a data-delivery problem, not a compute/GPU one**. Companion to
[web-showcase.md](web-showcase.md).

## What P1 shipped (2026-07-18)

- **Injection seam (Lib):** `ICelestialObjectDB.TryLoadTycho2BulkFromCompressed(byte[])`; default
  no-op (embedded/desktop hosts, test stubs), overridden by `CelestialObjectDB` to `LzipDecoder`-
  decompress the fetched bytes and publish `_tycho2Data`/`_tycho2StreamCount` (idempotent; publishes
  `_tycho2Data` last so `Tycho2StarCount`/`CopyTycho2Stars` never see a torn state). **Display-only:**
  it wires ONLY the flat star records, not the GSC-bounds spatial index (`_tycho2RaDecIndex`) or the
  HD/HIP cross-maps or the high-pm sidecar (~11 stars, rail-clamped pm, invisible at plot scale).
  Pinned by `Tycho2BulkInjectionTests` (fresh-DB inject → count > 2M + decoded records sane;
  idempotent re-inject; empty-input false).
- **Lazy fetch (web host, `Planner.razor`):** `EnsureTycho2AtlasAsync` fires once, on the **first
  Sky-Atlas paint** (guard set in `RenderFrame`'s SkyMap branch, not `ApplyViewFromLocation`, so the
  pipeline is guaranteed to exist even for a deep-link; covers chip + back/forward too). Yields, then
  `Http.GetByteArrayAsync("tyc2.bin.lz")`, `Db.TryLoadTycho2BulkFromCompressed`, flatten via the
  shared `SkyMapState.FillTycho2StarVertices` (dt=0), `_skyTab.SubmitTycho2Stars`, `RenderFrame`.
  Best-effort: a 404 (dev server without the CI-baked asset) or any failure leaves the HR field.
- **Swap-in (web pipeline, `WebGlSkyMapPipeline`):** `SubmitTycho2Stars` stashes the built buffer;
  `ApplyPendingTycho2` (called each frame from `Draw`) does the render-thread `CreateBuffer` + flips
  the star draw over to it; a **switch, not an overlay** (additive blend would double every shared
  star), the browser analogue of the desktop `VkSkyMapPipeline` HIP-seed → Tycho-2 swap. HR stays
  allocated (~180 KB) as the bootstrap/fallback.
- **Delivery (`pages.yml` + `.gitignore`):** a CI step copies the LFS `tyc2.bin.lz` into `wwwroot/`
  before publish (mirrors the comet-JSON bake; guards against a stale LFS pointer); the staged asset
  is gitignored so it never lands in the source tree.
- **Reuse, not new code:** the flatten is the desktop's `SkyMapState.FillTycho2StarVertices` (NOT a
  new `BuildTycho2StarInstances`: one path); the zoom-aware mag limit is the shared `SkyMapUbo`
  (already wired for the HR field), so nothing new was needed there.

**Not yet measured:** the AOT decode+flatten wall-time (gates whether P2 is worth it) needs a
*published* Lightweight+AOT build: dev is interpreted (slow) and 404s the asset (HR-only). See the
open questions.

## Where we are

The web atlas deliberately built the *real* instanced GPU pipeline (`WebGlSkyMapPipeline` +
`DrawInstanced`) so that adding Tycho-2 is "a data + payload change, not a code change"
(web-showcase.md P3 decision). The current HR-only field is `Lightweight=true` removing the embedded
`tyc2.bin.lz` (`TianWen.Lib.csproj:54`); `ReadTycho2Bulk` already no-ops when the manifest entry is
absent, so nothing crashes; the data is simply not there.

## The three costs (and which this plan actually fights)

| Cost | Status | Lever |
|------|--------|-------|
| **Render** 2.5M instanced stars | **already solved**, `DrawInstanced` renders it; desktop Vulkan proves the scale (~50 MB VRAM instance buffer) | - |
| **Decode** lzip decompress + flatten to star buffer | **measured 1802 ms + 93 ms** = 27% + 1.4% of a cold load. The flatten is noise; the decompress is real but is not what the user waits for once band 0 lands | P1 serial → P2+P4 band-aligned members |
| **Payload** ~30 MB download | **confirmed the dominant blocker at 45%**, and the codec cannot fix it (gzip costs +6.9 MB, brotli is unserveable on Pages) | P1 lazy-fetch → P3 IndexedDB cache ✅ → P2+P4 banded prefix |

## Disciplined framing (value gate)

The atlas is **already a fine showcase** with the ~8.6k naked-eye HR stars. Full Tycho-2 (down to
~mag 11.5) is a density/"real sky" *wow* upgrade, not essential. So every expensive phase below is
**measurement-gated**: ship the cheapest working version first, measure, and only take on the
infrastructure (wasm-threads, tiling) if the numbers justify it.

That gate did its job. The measurement below retired the wasm-threads infrastructure and the tiling
scheme -- the two most expensive things this plan had proposed -- and replaced both with a bake
change costing 0.49% of payload. Neither would have been wrong to build; both would have been built
against a guess about where the 6.67 s went.

**Scope decision; display-only, not searchable (v1).** The web atlas needs tyc2 for *rendering*, not
for F3 search / cross-identity. So the decode path builds **only the flat star-instance buffer** (via
the `CopyTycho2Stars` shape) and **skips** the desktop's DB dictionary integration
(`hip_to_tyc`/`hd_to_tyc` cross-maps, per-star `TryGetTycho2Star` lookup). That keeps the parse
per-record-parallel and avoids the serial dictionary-build. Searching individual TYC stars is deferred.

## The measurement that settled P2 and P4 (2026-08-16)

Everything below P1 was gated on a number this plan recorded as unknown: the AOT decode wall-time.
Measured on the **deployed** build (a dev server is interpreted, where compute is 24-42x slower and
the ratio between phases is meaningless, and it 404s the asset anyway) by
`TianWen.UI.Web.E2E/AtlasLoadCostProbe`:

| phase | cold | share |
|---|---:|---:|
| fetch 30.1 MB | 2972 ms | 45% |
| lzip decompress -> 43.5 MB, incl. the DB wire-up | 1802 ms | 27% |
| flatten 2,557,481 stars | **93 ms** | 1.4% |
| unaccounted (phase yields, the 51 MB vertex alloc, GPU upload) | ~1.8 s | 27% |
| **total atlas work after the app is usable** | **6.67 s** | |

Warm (P3 cache hit): **0.997 s**, flatten 63 ms.

Three conclusions, each of which redirects a phase:

1. **The flatten is noise.** 93 ms of 6.67 s. Nothing in P2 should be spent parallelizing it, and the
   original P2 bullet proposing exactly that is struck.
2. **Payload dominates, and the codec cannot fix it.** Measured against the raw 43.5 MB:

   | codec | size | vs lzip | note |
   |---|---:|---:|---|
   | **lzip -9** (shipped) | **30.1 MB** | - | costs the 1802 ms WASM decode |
   | gzip -6 / -9 | 37.0 MB | +6.9 MB (+23%) | what Pages would do transparently |
   | brotli q5 | 36.4 MB | +6.3 MB (+21%) | |
   | brotli q11 | 31.0 MB | +0.9 MB (+3.1%) | 121 s to compress |

   Two hard constraints make brotli unreachable: **GitHub Pages serves gzip only** (`Accept-Encoding:
   br` alone returns identity, verified against the live host), and **`DecompressionStream` supports
   gzip/deflate only in every browser** -- so a pre-compressed brotli asset would need a WASM decoder,
   which is precisely what lzip already is. The only real option is raw + transparent Pages gzip,
   which trades 6.9 MB of payload for the 1802 ms decode: **break-even at 3.8 MB/s (~31 Mbps)**. The
   measured link ran 10.1 MB/s (net -1.1 s), but below ~31 Mbps it is a REGRESSION, and slow/mobile
   links are the case this plan cares about. **Do not swap the codec.** (Pages currently gzips the
   `.lz` for nothing and adds 9 KB doing it -- harmless, but do not read the header as a saving.)
3. **Splitting into independently-decodable members is nearly free: 8 members cost 0.49% (+0.15 MB).**
   That is what makes the phase below possible at all, and it is the number that had been assumed
   ("slightly worsens the compression ratio ... measure both") rather than known.

## P2+P4: band-aligned multi-member bake (the one remaining phase)

**Bands, not sky tiles**, and the same artifact serves the desktop and the browser.

The bands already exist. `StarMagnitudeIndex` sorts the buffer brightest-first and indexes 0.5-mag
prefixes, so a magnitude band is a **contiguous prefix of the array already shipped** -- the file
needs a segment table in its header, not a new layout and not a re-sort. And the prefix table shows
why the axis is right: V<=8.5, the default 60-degree FOV, is **3.07% of the catalog**.

- **Bake:** set `LzipOptions.MemberSize` so member boundaries land on band boundaries, and write the
  per-band byte offsets into the header. Cost measured above: 0.49%.
- **Desktop reads every member.** Concatenated members carry the same records in the same order, so
  the embedded-resource path is behaviourally unchanged -- and it *gains* the parallel decode for
  free, because `LzipDecoder.Parallel.For` only engages on a multi-member file. The desktop gets
  faster; it is not merely left unbroken.
- **Web range-fetches per band.** GitHub Pages honours byte-range GETs (verified: `206 Partial
  Content` with an exact `Content-Range` at both offset 0 and mid-file). Band 0 is ~1 MB, so a
  complete-looking 60-degree sky lands almost immediately and the deep bands stream in behind it.
  ONE asset: no CI splitting step, no tile index, no fetch-on-pan logic, no per-tile cache.
- **Seam:** `ICelestialObjectDB.TryLoadTycho2BulkFromDecoded` gains an append/incremental form so
  bands can be submitted as they land. `SubmitTycho2Stars` already does a render-thread buffer swap,
  so progressive submission reuses the mechanism that is there rather than adding one.

**Why not the spatial tiling this plan used to specify.** At the default 60-degree FOV the view
covers a large slice of the visible sky, so a tiled scheme needs most of its tiles immediately and
buys nothing on first load. Tiles only pay at deep zoom -- which is exactly the case
`StarChunkIndex`'s cone cull already handles on the GPU, without downloading anything. Bands also
need no view-dependent fetch logic at all: a fixed count, in a fixed order, decided before the user
touches anything.

**What is NOT needed any more.** The wasm-threads infrastructure the old P2 was built on --
`WasmEnableThreads`, the `coi-serviceworker` COOP/COEP shim, the subresource audit, the Blazor
dispatcher marshalling -- was there to parallelize a decode that is 27% of the load. Band 0 makes
the sky appear before most of that decode has run, so the infrastructure is no longer on the
critical path for the thing it was meant to fix. It stays available for its own reasons (see
web-multithreading.md), but this plan no longer asks for it.

## Phasing

| Phase | Scope | Risk | Ships |
|-------|-------|------|-------|
| **P1 ✅ DONE** | **Lazy-fetch + serial decode.** tyc2 stays un-embedded for web (`Lightweight`); shipped as a same-origin static asset (CI-staged into wwwroot); fetched on **first atlas-open**; serial decode + flatten off the first-paint path; swapped over the HR seed. | Med | Full-density atlas, no first-load bloat |
| **P3 ✅ DONE** | **IndexedDB cache** of the raw decompressed catalog (`Tyc2CacheVersion = "tyc2-v2-raw"`; v1 cached the flattened buffer, raw enables clickable stars). Measured 6.67 s cold -> 0.997 s warm. | Med | Instant repeat visits |
| **P2+P4** | **Band-aligned multi-member bake** (below). ONE artifact change serves both: the desktop gets parallel decode, the web gets a progressive banded load over HTTP ranges. Supersedes the separate "parallel decode" and "spatial tiling" phases. | Med | Progressive first load + faster decode everywhere |

Incremental value: **P1 ships the feature**, P3 solves the repeat visit, and P2+P4 is the only
remaining phase -- justified by the measurement below rather than by expectation.

## P1: lazy-fetch + serial decode (the shippable core)

1. **Un-embed for web, ship as a static asset.** Keep `Lightweight=true` stripping the *embedded*
   resource (so it never bloats the WASM bundle), but publish `tyc2.bin.lz` into `wwwroot/`, exactly
   the model the baked `comets-sbdb.json` already uses (`pages.yml` writes it into wwwroot; the app
   fetches it same-origin via `HttpClient`, see `Program.cs` `AddAstrometry(cometQueryUri:)`). Add a
   pages.yml step that copies/bakes `tyc2.bin.lz` into wwwroot on deploy.
2. **Fetch on first atlas-open, not startup.** The planner (default view) must stay fast; it's
   DSO-only and doesn't need tyc2. Trigger the fetch when the user first switches to the Sky Atlas (or
   first zooms past HR density), with a progress indicator (the `.status`/`.catalog-loading` chrome
   pattern). The planner never pays the 30 MB.
3. **Decode without wedging.** Feed the fetched `byte[]` to `LzipDecoder.Decompress` (needs a
   from-bytes entry point alongside the current embedded-manifest path in `ReadTycho2Bulk`). On
   single-thread WASM this runs serial; keep the UI responsive via cooperative **chunking** (decode /
   flatten in slices with `await Task.Delay`-style yields, like `SetStatusAsync`). Measure the AOT
   wall-time; this is the number that gates P2.
4. **Flatten to star instances.** New `SkyMapGpuGeometry.BuildTycho2StarInstances(...)` mirroring
   `BuildHrStarInstances`: each `Tycho2StarLite` (RA hours, Dec deg, V mag, B−V) → the 5-float instance
   (unit vector x/y/z + mag + bv, `SkyMapState.FloatsPerStar`). Consume via the `CopyTycho2Stars`
   span-paging shape (decode-into-buffer, no per-star dictionary).
5. **Merge with HR without double-drawing.** Additive blend double-counts a star drawn twice
   (`BuildHrStarInstances` already avoids combining with the figure seed for this reason). Tycho-2
   subsumes the bright stars, so either (a) **replace** the HR field with tyc2 outright, or (b)
   **split by magnitude**: HR for the brightest (better color/photometry) + tyc2 for mag > HR-limit.
   Pick (b) if HR's colors look better on the bright end; else (a) is simpler. Upload as a second
   persistent instance buffer + a second `DrawInstanced`, or rebuild the one buffer.
6. **Zoom-aware mag limit. SHIPPED** (it was missed on the first cut, and the deployed atlas paid for
   it: a trace of a real drag showed the GPU process 59% busy and **944 of 1287 frames dropped**,
   because every frame submitted all ~2.5M instances -- ~15M vertices -- whatever the view showed).
   Both star buffers are sorted brightest-first at build time and indexed into a 0.5-mag prefix
   table, so the draw at `SkyMapState.EffectiveMagnitudeLimit` is a smaller instance count and
   nothing else: no per-frame CPU pass, no re-upload. Measured on the real catalog
   (`StarMagnitudeIndexTests`, 2,557,481 stars):

   | Limit | Instances | Share |
   |---|---:|---:|
   | V<=6 (zoomed out) | 5,043 | 0.20% |
   | V<=8.5 (default 60 deg FOV) | 78,422 | 3.07% |
   | V<=10 | 359,375 | 14.05% |
   | V<=12 (deep zoom) | 2,084,175 | 81.49% |

   The arithmetic is `StarMagnitudeIndex` in Abstractions, shared with `VkSkyMapPipeline`, which has
   culled this way for a while -- there the unbounded form did not merely drop frames, it **TDR'd an
   Adreno X1-85**. That is the whole reason this is shared rather than reimplemented.

7. **Spatial cone cull. SHIPPED**, in the same cut, because the table above shows the magnitude prefix
   alone is not enough: past V~11 it stops bounding anything, so a 1-degree field would still submit
   80% of the catalog for a patch of sky holding almost none of it. `StarChunkIndex` (Abstractions,
   shared with Vulkan) groups the buffer into a 12x12 RA/Dec grid, sorts + indexes within each chunk,
   and gives each a bounding cone; the draw submits only the chunks whose cone can meet the view cone,
   and only their prefix. The two axes are complementary -- magnitude for a wide field, the cone for a
   deep zoom -- and neither substitutes for the other.

   This needed **WebGl.Renderer 1.24**: a per-chunk draw is an instance sub-range, and WebGL2 has no
   base-instance draw argument (desktop GL 4.2 does). `DrawInstanced` now takes a `firstInstance` that
   the JS side turns into a per-instance attribute byte offset -- equivalent for a divisor-1 attribute
   and free, since the draw re-binds those attributes anyway.

## P2 (superseded) and P4 (superseded)

Both are folded into the band-aligned multi-member bake described above; the sections are kept here
in outline because each contains a decision that is still live, and because what killed them is
worth reading beside what replaced them.

**The old P2 (parallel decode over wasm-threads)** proposed `WasmEnableThreads`, a
`coi-serviceworker.js` COOP/COEP shim, a COEP subresource audit, Blazor dispatcher marshalling, and a
parallel flatten -- all to speed up a decode measured at 27% of the load, and a flatten measured at
1.4%. Its one surviving element is the **multi-member bake**, which is now wanted for band
addressability rather than for threads, and whose ratio trade-off it correctly flagged as needing
measurement (answered: 0.49% for 8 members). The threads work keeps its own justification in
[web-multithreading.md](web-multithreading.md); this plan no longer needs it.

**The old P4 (spatial tiling)** proposed pre-tiling by HEALPix or an RA/Dec grid with fetch-on-pan.
It is the wrong axis for this app: at the default 60-degree FOV the view covers a large slice of the
visible sky, so a tiled scheme must fetch most of its tiles on first load. Tiles only pay at deep
zoom, and that case is already handled entirely on the GPU by `StarChunkIndex`'s cone cull, which
downloads nothing. Magnitude bands additionally need no view-dependent fetch logic, no tile index and
no per-tile cache -- and, unlike tiles, they are already the order the file is sorted in.

**P3 ✅ SHIPPED** as the IndexedDB cache of the *raw decompressed* catalog (`Tyc2CacheVersion =
"tyc2-v2-raw"`). v1 cached the flattened float buffer; raw was chosen because it feeds the same DB
path as a cold decode, which is what makes click-to-identify work on a cached load. Measured 6.67 s
cold -> 0.997 s warm. **Its write is fire-and-forget and moves 43.5 MB**, so anything measuring the
warm path has to wait for the app to report `tyc2 cached to IndexedDB` first -- navigating on the
flatten line measures a second cold load and reads as a broken cache, with identical phase timings
and only the `(decode)` source string to give it away.

## Integration points / invariants for the implementer

- **Delivery precedent:** `comets-sbdb.json`; a same-origin static asset baked into wwwroot by
  pages.yml, fetched at runtime via `HttpClient` (`Program.cs` `AddAstrometry(cometQueryUri:)`). tyc2
  follows the same shape (binary + lzip decode instead of JSON).
- **Decode API:** `LzipDecoder.Decompress` (multi-member `Parallel.For`, single-member serial); needs
  a from-`byte[]` entry point next to `ReadTycho2Bulk`'s embedded-manifest path. `LzipEncoder` +
  `LzipOptions.MemberSize` bakes multi-member.
- **Star API:** `Tycho2StarLite(RaHours, DecDeg, VMag, BMinusV, PmRa…, PmDec…)`; `CopyTycho2Stars(
  Span<Tycho2StarLite>, startIndex)` is the paged flatten source. `SkyMapState.FloatsPerStar` = 5.
- **Render is ready:** `DrawInstanced` + `WebGlSkyMapPipeline` star pipeline; `SkyMapGpuGeometry`
  (Abstractions) is the shared geometry builder; add `BuildTycho2StarInstances` beside
  `BuildHrStarInstances`. Do NOT double-draw HR + tyc2 under additive blend (replace or mag-split).
- **Keep the bundle lean:** never re-embed tyc2 (`Lightweight=true` stays); it's a fetched asset.
- **Display-only v1:** tyc2 stars are NOT added to the searchable `ICelestialObjectDB`; flat instance
  buffer only. F3 search of individual TYC stars is deferred.
- **GPU compute is the wrong tool** for the decode (LZMA range-decode is sequential-within-member);
  parallelism is across-member CPU threads, not GPU. WebGPU is irrelevant to this feature.
- **The bake must serve BOTH hosts from one artifact.** A web-only catalog format would be a second
  implementation of the thing `StarMagnitudeIndex` and `StarChunkIndex` are shared to avoid. Members
  concatenate to the same records in the same order, so the desktop's embedded read is unchanged
  while the browser reads a prefix -- the host decides how much to read, and neither knows what the
  other did.
- **Byte ranges are the addressing mechanism, and GitHub Pages supports them** (`206 Partial
  Content` with an exact `Content-Range`, verified at offset 0 and mid-file). So the bands are
  offsets into ONE asset -- do not split the catalog into N files, which would add a CI step, N
  cache entries and a manifest to keep in sync for nothing.
- **Do not switch away from lzip to get free native decompression.** Pages serves gzip only and
  browsers' `DecompressionStream` has no brotli, so the only reachable alternative costs +6.9 MB to
  save 1802 ms -- break-even at ~31 Mbps, a regression below it. Measured; see above.

## Open questions / gates

**Answered 2026-08-16** (kept, because a plan that silently deletes its gates loses the record of
what it was allowed to assume):

- ~~Measured AOT serial decode wall-time (gates P2).~~ **1802 ms decompress + 93 ms flatten of a
  6.67 s cold load.** Re-measure with `AtlasLoadCostProbe` against a DEPLOYED build after any change
  to the bake; a dev server answers a different question.
- ~~Multi-member `MemberSize` sweet spot: decode-parallelism vs compression-ratio.~~ **8 independent
  members cost 0.49% (+0.15 MB).** The ratio is not the constraint, so `MemberSize` should be chosen
  by where the magnitude bands fall, not by compression.

Still open:

- **Band boundaries.** How many, and at which magnitudes. V<=8.5 (3.07%) is the obvious band 0
  because it is the default 60-degree FOV; the rest is a trade between request count and how
  abruptly density arrives. `StarMagnitudeIndex`'s 0.5-mag table is the natural quantisation.
- **What the user sees while bands land.** A star field that visibly thickens is either delightful or
  a flicker, and which one it is depends on band size. Worth deciding with a real build in front of
  you, not on paper.
- **Range-request behaviour under a CDN cache.** Pages returned `206` cleanly on a cold and a mid-file
  range, but N range GETs per visit interact with Fastly caching differently from one whole-file GET;
  confirm the bands are actually cached rather than re-fetched.
- Published `_framework` + tyc2 asset size budget (web-showcase.md already flags a payload-budget item).
- HR-replace vs HR+tyc2-mag-split: a visual-quality call on bright-star color.
