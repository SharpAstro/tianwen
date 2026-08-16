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
| **Decode** lzip decompress + flatten to star buffer | **measured 1802 ms + 93 ms** = 27% + 1.4% of a cold load. The flatten is noise (a fixed 17-byte stride is a pointer walk); the decompress is real, and shrinks with the bytes fetched rather than needing threads | P1 serial → P2+P4 region-aligned members |
| **Payload** ~30 MB download | **confirmed the dominant blocker at 45%**, and the codec cannot fix it (gzip costs +6.9 MB, brotli is unserveable on Pages) | P1 lazy-fetch → P3 IndexedDB cache ✅ → P2+P4 region ranges |

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
   Measured by slicing the file **in its existing byte order**, which is what makes it the right
   number for the phase below and the wrong number for the one first proposed -- see the correction.
   It answers a question this plan had left as an assumption ("slightly worsens the compression ratio
   ... measure both").

## The on-disk layout, which decides everything below

Read before proposing any change to the asset. Measured from the shipped `tyc2.bin.lz`:

```
[0 .. 4)             int32  streamCount = 9537            (GSC regions)
[4 .. 38152)         int32  startOffset per region        (37.3 KB offset table, ALREADY THERE)
[38152 .. 43.5 MB)   per-region packed 17-byte records:
                     tyc2 u16 | tyc3 u8 | RA f32 | Dec f32 | VT u8 | BT u8 | pmRA i16 | pmDec i16
```

Region sizes: min 391 B / 23 stars, median 3723 B / 219 stars, max 26962 B / 1586 stars;
2,557,501 stars total.

Three consequences, each of which a proposal has to respect:

- **Fixed 17-byte stride, no per-record branching.** Parsing is a pointer walk, which is why the
  flatten measures 93 ms for 2.56M stars. A reorder would not change parse cost -- but see below for
  what it *would* change.
- **The file is ALREADY spatially segmented, with an offset table in its header.** The GSC regions
  are tiles. `Tycho2RaDecIndex` is built on exactly this: it maps a sky cell to region ids
  (`tyc1 = gscIdx + 1`) via the separate 16-byte-per-region bounds table, then reads those regions.
  So region-contiguity is **load-bearing for desktop click-to-identify and the coordinate grid**, not
  an incidental ordering.
- **Compression locality follows the ordering.** Neighbouring stars within a region share RA/Dec high
  bytes; any global re-sort scatters them, and the 0.49% figure above -- taken in the existing order
  -- would not describe the result.

### Correction (2026-08-16): magnitude banding was the wrong axis, and this plan said otherwise

An earlier revision of this section proposed **magnitude bands**, on the claim that "bands are
already contiguous prefixes of the array already shipped ... not a new layout and not a re-sort".
**Both halves were false.** `StarMagnitudeIndex.SortBrightestFirst` runs at RUNTIME, inside
`StarChunkIndex.Build`, and it sorts **per chunk** of a 12x12 grid -- there is no global
brightest-first order on disk or in memory. Magnitude banding therefore needs a global re-sort at
bake time, which destroys the region contiguity `Tycho2RaDecIndex` depends on, i.e. it breaks the
desktop while claiming to serve both hosts. Its supporting measurement did not cover it either.

The conceptual error is worth naming, because it is easy to repeat: **a render-time cull was mistaken
for a transfer-time filter.** `StarMagnitudeIndex` culls by magnitude beautifully, and it can only do
so with the stars already in memory. That makes it useless as a download filter, whatever its
prefix table says about 3.07%.

## P2+P4: region-aligned multi-member bake (the one remaining phase)

**The tiling already exists in the format; it is simply not addressable while the file is one lzip
member.** So the change is to the bake, not to the layout.

- **Bake:** set `LzipOptions.MemberSize` so member boundaries fall on GSC-region-group boundaries.
  Cost 0.49%, and this is the case the measurement actually covers, because slicing in the existing
  byte order IS slicing on region boundaries. **The record order does not change**, so nothing
  downstream of the decode can tell the difference.
- **Desktop reads every member** -- unchanged by construction, not by careful preservation -- and
  *gains* parallel decode for free, since `LzipDecoder.Parallel.For` only engages on a multi-member
  file.
- **Web fetches the 37.3 KB header + the bounds table, then coalesced ranges for the regions the view
  covers.** GitHub Pages honours byte-range GETs (verified: `206 Partial Content` with an exact
  `Content-Range` at offset 0 and mid-file). One asset, no CI splitting step, and **no new tile
  index -- the offset table and the bounds table are both already shipped**.
- **Seam:** `ICelestialObjectDB.TryLoadTycho2BulkFromDecoded` gains an append/incremental form so
  regions can be submitted as they land. `SubmitTycho2Stars` already does a render-thread buffer
  swap, so progressive submission reuses the mechanism that is there rather than adding one.

**The honest caveat on request count.** 9537 regions over 41253 square degrees is ~4.3 sq deg each,
so a 60-degree FOV covers roughly 650 of them. That is far too many individual requests; it is only
viable because regions are ordered by `tyc1`, which runs in declination bands, so the visible set
should form a modest number of CONTIGUOUS runs that coalesce into a few dozen ranges. **Measure the
run count before building this** -- it is the difference between a few dozen requests and hundreds,
and it is cheap to compute offline from the bounds table alone.

**If the instant-sky property is still wanted**, the non-destructive form is a **separate** small
bright-prefix asset (V<=8.5 is ~78k stars, ~1.3 MB at 17 B/record) baked alongside the untouched main
file. It costs ~1.3 MB of duplication, needs no re-sort, breaks no index, and can be dropped later
without touching anything. That is what the magnitude idea should have been from the start: an
addition, not a reordering.

**What is NOT needed any more.** The wasm-threads infrastructure the old P2 was built on --
`WasmEnableThreads`, the `coi-serviceworker` COOP/COEP shim, the subresource audit, the Blazor
dispatcher marshalling -- was there to parallelize a decode worth 27% of the load and a flatten worth
1.4%. The multi-member bake alone gives the desktop `Parallel.For` decode, and the web's problem is
payload rather than decode. It stays available for its own reasons (see web-multithreading.md), but
this plan no longer asks for it.

## Phasing

| Phase | Scope | Risk | Ships |
|-------|-------|------|-------|
| **P1 ✅ DONE** | **Lazy-fetch + serial decode.** tyc2 stays un-embedded for web (`Lightweight`); shipped as a same-origin static asset (CI-staged into wwwroot); fetched on **first atlas-open**; serial decode + flatten off the first-paint path; swapped over the HR seed. | Med | Full-density atlas, no first-load bloat |
| **P3 ✅ DONE** | **IndexedDB cache** of the raw decompressed catalog (`Tyc2CacheVersion = "tyc2-v2-raw"`; v1 cached the flattened buffer, raw enables clickable stars). Measured 6.67 s cold -> 0.997 s warm. | Med | Instant repeat visits |
| **P2+P4** | **Region-aligned multi-member bake** (below). The record order does NOT change: the file is already segmented by GSC region with an offset table in its header, so the bake only makes those segments independently decodable. Desktop gets parallel decode; web range-fetches the regions it can see. Supersedes the separate "parallel decode" and "spatial tiling" phases. | Med | Progressive first load + faster decode everywhere |

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

Both are folded into the region-aligned multi-member bake described above; the sections are kept here
in outline because each contains a decision that is still live, and because what killed them is
worth reading beside what replaced them.

**The old P2 (parallel decode over wasm-threads)** proposed `WasmEnableThreads`, a
`coi-serviceworker.js` COOP/COEP shim, a COEP subresource audit, Blazor dispatcher marshalling, and a
parallel flatten -- all to speed up a decode measured at 27% of the load, and a flatten measured at
1.4%. Its one surviving element is the **multi-member bake**, which is now wanted for region
addressability rather than for threads, and whose ratio trade-off it correctly flagged as needing
measurement (answered: 0.49% for 8 members). The threads work keeps its own justification in
[web-multithreading.md](web-multithreading.md); this plan no longer needs it.

**The old P4 (spatial tiling)** had the right axis and the wrong amount of work. It proposed
*pre-tiling* by HEALPix or a new RA/Dec grid, with a tile index and fetch-on-pan -- none of which
needs building, because the file is already segmented by GSC region and already ships both an offset
table and a bounds table. The surviving idea is "fetch the sky you are looking at"; what is struck is
the invention of a tiling scheme to do it with. Its real open question is the one the region layout
inherits: how many requests a view costs, which is now a measurable run-count rather than a design
choice.

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
- **NEVER reorder the records.** The file is grouped by GSC region and `Tycho2RaDecIndex` resolves a
  sky cell to region ids and then reads those regions, so the ordering carries desktop
  click-to-identify and the coordinate grid. A bake may change where members BEGIN; it may not change
  which record follows which. (This is the invariant an earlier revision of this plan proposed to
  break, in the name of magnitude banding -- see the correction above.)
- **`StarMagnitudeIndex` is a render-time cull and cannot be a transfer-time filter.** It sorts at
  runtime, inside `StarChunkIndex.Build`, per chunk of a 12x12 grid, and it needs the stars in memory
  to do it. Its 3.07%-at-V<=8.5 figure describes what the GPU draws, never what the client has to
  download.
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
  while the browser reads the subset it needs -- the host decides how much to read, and neither knows
  what the other did.
- **Byte ranges are the addressing mechanism, and GitHub Pages supports them** (`206 Partial
  Content` with an exact `Content-Range`, verified at offset 0 and mid-file). So the regions are
  offsets into ONE asset -- do not split the catalog into N files, which would add a CI step, N
  cache entries and a manifest to keep in sync alongside the offset table the file already has.
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
  members cost 0.49% (+0.15 MB)**, measured in the file's existing byte order. The ratio is not the
  constraint, so `MemberSize` should be chosen by where the GSC-region groups fall.

Still open:

- **How many ranges a view actually costs.** 9537 regions over 41253 sq deg is ~4.3 sq deg each, so a
  60-degree FOV touches ~650 of them. The whole design rests on those coalescing into a few dozen
  CONTIGUOUS runs, because `tyc1` runs in declination bands. **Compute the run-count distribution
  offline from the bounds table** before writing any fetch code -- it is cheap, it needs no browser,
  and it is the number that says whether this is viable at all.
- **What the user sees while regions land.** A star field that fills in by sky patch is either
  delightful or distracting, and it is a different experience from one that thickens uniformly.
  Worth deciding with a real build in front of you, not on paper.
- **Range-request behaviour under a CDN cache.** Pages returned `206` cleanly on a cold and a mid-file
  range, but N range GETs per visit interact with Fastly caching differently from one whole-file GET;
  confirm the regions are actually cached rather than re-fetched.
- **Whether the bright-prefix side asset is worth ~1.3 MB.** It buys an instant full-sky impression
  that region fetching alone does not, at the cost of duplicating 78k records. Only worth it if the
  region path measures badly on the first paint.
- Published `_framework` + tyc2 asset size budget (web-showcase.md already flags a payload-budget item).
- HR-replace vs HR+tyc2-mag-split: a visual-quality call on bright-star color.
