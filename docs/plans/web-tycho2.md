# Tycho-2 in the Browser Atlas (plan)

**Status: COMPLETE. P1 + P3 shipped earlier; P2+P4 shipped 2026-08-16 as one region-aligned bake
plus a per-member client.** A first open fetches 8.96 MiB of the sky it is looking at instead of an
unconditional 28.71 MiB, and picks up more as you pan. Bring the full ~2.5M-star Tycho-2
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

- **Bake:** cut lzip members on GSC-region boundaries -- never on a fixed stride, because a member
  that splits a region puts half a region behind a boundary no client can address. So `MemberSize`
  is a *target* the packer rounds up to the next region edge, not a stride. **The record order does
  not change**, so nothing downstream of the decode can tell the difference.
  - **The 0.49%-for-8-members figure does NOT apply here and must not be quoted for it.** The run
    table below puts a run at ~143 KB in a wide field and ~40 KB when zoomed, so members have to be
    in that size class to be addressable at all -- hundreds or thousands of them. Every member resets
    the LZMA dictionary (`LzipEncoder` caps the dictionary to the member's own length), so the
    penalty grows as members shrink by an amount eight members cannot predict. Measured separately;
    see the member-size table.
- **Desktop reads every member** -- unchanged by construction, not by careful preservation. It does
  **not** gain parallel decode from this: an earlier draft claimed it would, but the shipped asset is
  *already* multi-member, because `Get-Tycho2Catalogs.ps1` bakes it with `lzip -9 -b 4MiB` (~11
  members). `LzipDecoder`'s `Parallel.For` has been engaging all along. Finer members make its units
  smaller, nothing more.
- **Web fetches one file per member.** Not byte ranges -- see below, they do not work on either host.
- **Seam:** `ICelestialObjectDB.TryLoadTycho2BulkFromDecoded` gains an append/incremental form so
  regions can be submitted as they land. `SubmitTycho2Stars` already does a render-thread buffer
  swap, so progressive submission reuses the mechanism that is there rather than adding one.

### Byte ranges do not work, on EITHER host (measured 2026-08-16)

An earlier revision of this plan specified coalesced byte ranges over one asset and explicitly
forbade splitting into files. **Both halves were wrong**, and the way they were wrong is the reason
to record it: the range design passes every local test, works perfectly under `curl`, and returns
garbage in a browser.

- **GitHub Pages returns `206` -- over the GZIP representation.** Fastly compresses *every* content
  type there (checked `application/octet-stream`, `application/json`, `text/html`), so a request
  carrying Chrome's real `Accept-Encoding: gzip, deflate, br, zstd` for `bytes=20000000-20000063`
  comes back with `Content-Range: bytes 20000000-20000063/30116376` -- the **gzip** length, not the
  file's 30,107,173 -- and a 64-byte body that is not valid gzip standalone. **`Accept-Encoding` is a
  forbidden header name**, so no browser can ask for identity and no amount of client code recovers
  it. The earlier "verified 206 at offset 0 and mid-file" was true and measured with `curl`'s default
  headers, which is precisely the trap.
- **Cloudflare Pages ignores `Range` outright**: `200 OK` with the full body, no `Accept-Ranges`,
  cold or warm. Worse for ranges, though it does not gzip binaries.

**So: one file per member.** The objection the old plan raised against files ("a CI step, N cache
entries and a manifest to keep in sync") does not survive contact: the bake is a CI step either way,
the manifest is needed either way because lzip member enumeration walks BACKWARDS from the file end
(`LzipDecoder.FindMembers`) and a client holding only the head cannot find member boundaries, and N
cache entries is a *benefit* -- the browser's own HTTP cache handles them per member.

**It is still one artifact.** The member files concatenated in order ARE the multi-member lzip file,
byte for byte -- the desktop embeds the concatenation, the browser fetches slices of it, and neither
knows what the other did. That is the "one artifact serves both hosts" invariant intact, not
abandoned; only the delivery is sliced.

### Host choice: build for GitHub Pages, treat Cloudflare as a pure upgrade

Ranges are dead on both, so the design is host-agnostic and nothing about the code changes with the
host. Cloudflare Pages is nonetheless the better home for it, on two axes that are **deploy config,
not code** (both verified live against a throwaway `pages.dev` project):

- **`Cache-Control` on immutable members.** GitHub Pages pins `max-age=600` with no override, so a
  visitor returning after ten minutes revalidates every member. Cloudflare `_headers` accepts
  `public, max-age=31536000, immutable` -- verified served back. On a design that lands 166 immutable
  files that is most of what P3's IndexedDB cache does, bought with a header.
- **COOP/COEP, i.e. wasm threads.** [web-multithreading.md](web-multithreading.md) names these two
  headers as *the* gated layer and records "GitHub Pages gives no way to set response headers" as the
  blocker. `_headers` sends arbitrary headers (verified), so Cloudflare removes that blocker and the
  `coi-serviceworker` shim with it. **Worth noting threads are worth LESS after this phase, not
  more** -- a wide view decodes ~17 MB instead of 43.5 MB -- and that COEP `require-corp` forces every
  cross-origin subresource to opt in, which is survivable here only because the JPL comet assets are
  now baked same-origin.

### The run-count gate: MEASURED 2026-08-16, and it passes

This was the plan's one blocking unknown -- 9537 regions over 41253 square degrees is ~4.3 sq deg
each, so a wide view touches many hundreds of them, and the whole design rested on those coalescing
into a modest number of CONTIGUOUS runs. Computed offline from the shipped bounds table by
`Tycho2RegionSelectorTests` (no browser, no network), with the view radius set to the FULL field of
view, matching what both pipelines already cull with (`StarChunkIndex.IsVisible`).

| view | FOV | regions | exact runs | reqs @256K gap | MB | reqs @1M gap | MB |
|---|---:|---:|---:|---:|---:|---:|---:|
| galactic centre | 60 | 2,915 | 86 | **15** | 13.17 | 13 | 14.43 |
| galactic centre | 10 | 110 | 18 | 4 | 1.01 | 4 | 1.01 |
| galactic centre | 2 | 9 | 5 | 2 | 0.17 | 2 | 0.17 |
| north galactic pole | 60 | 1,931 | **99** | 17 | 6.48 | 13 | 9.05 |
| Orion | 60 | 2,543 | 64 | 18 | 11.77 | 16 | 13.03 |
| RA 0h seam | 60 | 2,167 | 60 | 18 | 9.24 | 18 | 9.24 |
| north celestial pole | 60 | 2,481 | **23** | 2 | 12.40 | 1 | 13.29 |
| south celestial pole | 60 | 2,614 | 24 | 2 | 14.05 | 2 | 14.05 |

**Dozens, not hundreds: the worst case across every view and zoom is 99 exact runs, and a 256 KB gap
allowance turns that into 17 requests.** Zoomed in it collapses -- a 10-degree field is 4 requests
and ~1 MB of raw records, a 2-degree field 2 requests and 0.17 MB, against today's unconditional
30.1 MB.

Three things the measurement said that guesswork did not:

- **The poles are the BEST case, not the worst.** 23-24 runs against 60-99 elsewhere, collapsing to
  one or two requests, because polar regions are few and adjacent in `tyc1`. The intuition that
  converging RA bands would fragment the selection is backwards: convergence means *fewer, wider*
  regions, and they are neighbours in the index.
- **The two culling axes are anti-correlated, and that is the real cost structure.** A wide field
  needs few stars but many regions (2,915 regions, ~13 MB) while a deep zoom needs many stars but
  almost no regions (9 regions, 0.17 MB). So region fetching is weakest exactly at the default view
  and strongest exactly where the magnitude prefix gives up. They compose; neither alone would do.
- **The gap allowance is worth more than it costs.** Going from exact runs to a 256 KB allowance cuts
  the galactic-centre wide view from 86 requests to 15 for +0.86 MB. On any link where latency
  dominates that is the right side of the trade, and the numbers say where the knee is.

### Member size: MEASURED 2026-08-16, and 64 KB is the knee

The other half of the price, from `Tycho2RegionBakeProbe` (env-gated; it recompresses the catalog six
times over). Members are region-aligned by the greedy packer described above, the 37 KB header gets a
member to itself because every client must decode it before it can ask for anything, and consecutive
members are one Range GET because they are contiguous in the compressed file.

Baseline: raw 43.52 MB, single member **28.88 MiB** with our own encoder (the shipped asset is
28.71 MiB -- `LzipEncoder` is ~0.6% behind whatever baked it, which is why the deltas below are
against our single-member number and not against the file on disk).

Requests below are **files**, since ranges are unusable. Files cannot be coalesced, which moves the
knee upward from where a range-based delivery would have put it.

| member target | members | total | vs 1 member | gal. centre 60 deg | gal. centre 10 deg | gal. centre 2 deg |
|---|---:|---:|---:|---|---|---|
| 2 MB | 22 | 28.90 MiB | +0.0% | 17 files, 21.95 MiB | 6 files, 6.93 MiB | 3 files, 2.78 MiB |
| 1 MB | 43 | 28.97 MiB | +0.3% | 29 files, 19.14 MiB | 6 files, 3.49 MiB | 3 files, 1.41 MiB |
| 512 KB | 84 | 29.12 MiB | +0.8% | 43 files, 14.61 MiB | 7 files, 2.09 MiB | 4 files, 1.06 MiB |
| **256 KB** | **166** | **29.28 MiB** | **+1.4%** | **69 files, 11.87 MiB** | **9 files, 1.40 MiB** | **3 files, 0.36 MiB** |
| 128 KB | 326 | 29.36 MiB | +1.6% | 120 files, 10.54 MiB | 14 files, 1.16 MiB | 4 files, 0.28 MiB |
| 64 KB | 636 | 29.45 MiB | +1.9% | 213 files, 9.68 MiB | 18 files, 0.79 MiB | 5 files, 0.20 MiB |
| 32 KB | 1,219 | 29.61 MiB | +2.5% | 397 files, 9.44 MiB | 30 files, 0.72 MiB | 6 files, 0.15 MiB |

**Take 256 KB.** It costs +1.4% (~0.40 MiB) on the asset and turns the worst view from an
unconditional 28.88 MiB into 11.87 MiB (2.4x), a 10-degree view into 1.40 MiB (21x) and a 2-degree
view into 0.36 MiB (80x), at 69 / 9 / 3 requests.

**Why not smaller, given 64 KB downloads less?** Because the 2.2 MiB it saves on the worst view costs
213 requests against 69, and that view is the one where the user is waiting. Under HTTP/2 the extra
requests are cheap but not free, and a year-long immutable cache (Cloudflare) makes the byte
difference matter even less on any visit after the first. If first-open latency measures badly and
bytes turn out to be the binding constraint rather than round trips, 128 KB is the next stop -- it is
a one-line change to the bake and nothing downstream knows the difference.

- **The dictionary-reset penalty is far smaller than the member count suggests, and that is a
  property of this data.** 636 independent members cost 1.9%, not the tens of percent a
  from-eight-members extrapolation would have predicted. The records are a fixed 17-byte stride whose
  compressible structure is *local* -- neighbouring stars in a region share RA/Dec high bytes -- so an
  LZMA dictionary capped at 64 KB still sees everything worth matching against. Do not carry this
  conclusion to a differently-shaped asset.
- **Coarse members are the trap, and they look fine on the request column.** 4 MB members give the
  prettiest request count in the table (2) and are useless: 23.31 MiB for a wide view is 81% of
  fetching the whole thing. Requests are not the metric; bytes are.
- **Past 64 KB the curve is flat and the costs are not.** 16 KB buys 0.49 MiB on the wide view for
  +1.6% ratio and 53% more requests. The knee is unambiguous.
- **A wide view is the worst case at every member size** -- see the anti-correlation note above. If
  9.68 MiB on first open is judged too slow, the fix is the bright-prefix side asset below, not a
  smaller member.

### Driven in a browser (2026-08-16): it works, and it found two flaws nothing offline could

`TianWen.UI.Web.E2E/AtlasMemberFetchProbe` against a Lightweight dev server with the members staged.
**Counts and bytes only** -- a dev server is interpreted, so its durations mean nothing on the
deployed build, but request counts and payload sizes are properties of the design and transfer
exactly.

| | files | bytes |
|---|---:|---:|
| first open (default 60-degree view) | 52 | **8.96 MiB** |
| after panning to new sky | 71 | 12.36 MiB |
| *what it replaced* | 1 | *28.71 MiB, whatever you were looking at* |

- **Members were fetched one at a time**, so a wide view was 50 sequential round trips -- which
  spends the entire point of small independent files. Issuing every request before awaiting any took
  a 4-member batch from 32.5 s to 2.5 s on that server. The residual is serial LZMA decode on the one
  WASM thread, not the network.
- **A single pan cost SIX full rebuilds**, one per quantized view cell it crossed, each re-walking
  all 2.5M record offsets, regrouping the buffer and re-uploading the whole instance buffer.
  **Making the fetch single-flight did not help, and that is the useful part**: the fetches then
  finished *between* crossings, which is the tell that the REBUILD was the cost all along. Debouncing
  the rebuild on the same generation-guard as the overlay labels took it to three, with batches
  visibly coalescing. The probe asserts the rebuild COUNT for the same reason the numbers above are
  counts.

- **The rebuild's cost was then attributed to the wrong half of itself, and only a deployed-build
  trace could tell them apart.** "The flatten" was named as the expense in the commit, the probe's
  comment and the bullet above; the flatten was never the expense. A DevTools trace of the deployed
  site showed three 2.4 s main-thread blocks in thirteen seconds -- 580 of 611 frames dropped, each
  block one `setTimeout(120)`, i.e. exactly the one-rebuild-per-settle the debounce promises -- and
  the app's own log placed the split: `tyc2 flatten (66 members): 1011547 stars in 74 ms`. The
  remaining 2.4 s was `StarChunkIndex.Build`, and inside it `SortBrightestFirst`.
  **`Span<T>.Sort<T, TComparer>` over an app-defined record struct and a struct comparer is a generic
  instantiation the Mono AOT compiler does not emit, so that one call ran interpreted while
  everything around it was compiled**: 272 ms on desktop against ~2.4 s in the browser. No desktop
  measurement can see that factor, and it is invisible in source -- the call looks like the cheapest
  possible sort, and on every other target it is.
  The sort was also unnecessary: its only reader is `VisibleCount`, which answers with a bin's prefix
  count, so ordering within a 0.5-magnitude bucket is unobservable. The region grouping and the
  magnitude ordering now fold into ONE counting scatter keyed (chunk major, bucket minor). Desktop
  `Build` 334 ms -> 57 ms at 2.56M stars; on the deployed build a settle's rebuild went from ~2.4 s
  to **81-113 ms measured at 0.76M-1.46M stars held**.
  Two lessons worth more than the fix: **name the phase you measured, not the phase you assumed**
  (one `Stopwatch` around the flatten would have exonerated it immediately, and the log that finally
  did was already there), and **a WASM perf claim needs the AOT target** -- the interpreted dev
  server this plan's probe runs against is uniformly ~25x slower, which is the same order as the
  cliff, so it hides exactly the bug it looks like.

- **Fixing the sort exposed the next two layers, and the profile named each one in turn.** Measured
  the same way each time -- `PerformanceObserver('longtask')` on the deployed site, three pans, with
  the app's console lines on the same `performance.now()` clock so a task can be placed between two
  of them.
  1. With the sort gone, **1183 ms of the remaining 1443 ms was the member fetch loop**, in tasks of
     132-318 ms, every one ending at a `tyc2 members: +N` line. Cause: issuing every request before
     awaiting any -- correct for the network -- means `await body` completes SYNCHRONOUSLY for each
     member already downloaded, so a batch decoded end to end inside one JS task. A `Task.Delay(1)`
     between members puts each ~15 ms decode in its own task, under the long-task threshold.
  2. What was left were two O(sky HELD) terms paid per settle, when what changes is O(sky ARRIVED):
     the flatten walks the whole offset table regardless of which members are present, and the chunk
     build re-keyed every star already in the buffer. `StarChunkAccumulator` keys a member once as it
     lands and a settle concatenates -- **the rebuild is now 5-35 ms**, and it gets FASTER as more sky
     is held (35 ms at 762k stars, 10 ms at 1.50M), because a later settle dirties fewer chunks and
     the clean ones carry their cone over.
  3. **The member path never reached the IndexedDB cache** -- `TryStartIncrementalAtlasAsync` returns
     before it, so `tyc2-v2-raw` only ever served the whole-catalog fallback the deployed site no
     longer takes. P3's warm-start win was lost the day members shipped, silently, because the HTTP
     cache still saved the download and only the decode was being re-paid. Members now cache their
     DECOMPRESSED bytes per member.

  **Deployed result, three pans:**

  | | long tasks | blocked | longest | rebuild |
  |---|---|---|---|---|
  | before | 37 | 12 078 ms | 2 483 ms | ~2 400 ms |
  | after, cold | 7 | 684 ms | 235 ms | 10-35 ms |
  | after, warm (IndexedDB) | 7 | 654 ms | 215 ms | 5-24 ms |

  A warm member batch reads `+3 of 3 (3 cached) in 27 ms` against a cold `+9 of 9 (0 cached) in
  652 ms`, and the first batch of 49 goes 1944 ms -> 797 ms.

  **Still O(total) per settle, and the next thing to look at if it matters:** the GPU upload
  re-uploads the whole instance buffer (`CreateBuffer` over every held star) and the previous one
  becomes garbage. That is the likeliest remaining occupant of the ~215 ms outlier. The fix would be
  a fixed-capacity buffer sized from a baked per-chunk census plus `bufferSubData` into each chunk's
  own sub-range -- worth it only if a measurement says that outlier is actually the upload, which
  this one does not.

### The bright-prefix side asset is NOT needed

The plan reserved it for "only if the region path measures badly on the first paint". It does not,
and the reason is that the instant-sky job is already done by something that ships today: **the HR
bright-star seed (8,641 stars) is on screen from the first paint** and stays there until Tycho-2
replaces it. The side asset would buy a denser instant sky for ~1.3 MB of duplicated records, on top
of a first open that already fell from 28.71 MiB to 8.96 MiB.

Kept on the shelf rather than deleted, because the one measurement that would revive it has not been
taken: **this was measured over localhost, so it says nothing about how the first open FEELS on a
slow link.** If that ever reads badly, the non-destructive form is a **separate** small bright-prefix
asset (V<=8.5 is ~78k stars, ~1.3 MB at 17 B/record) baked alongside the untouched main file -- no
re-sort, no index broken, droppable later without touching anything. That is what the magnitude idea
should have been from the start: an addition, not a reordering.

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
| **P2+P4 ✅ DONE** | **Region-aligned multi-member bake + per-member client.** The record order does NOT change: the file is already segmented by GSC region with an offset table in its header, so the bake only makes those segments independently decodable, and `tools/bake-tycho2` derives them from the committed `.lz` (verifying the concatenation decodes back to identical bytes). Published as ONE FILE PER MEMBER, not byte ranges -- ranges are unusable on both candidate hosts. Supersedes the separate "parallel decode" and "spatial tiling" phases. | Med | 8.96 MiB first open instead of 28.71 MiB, more as you pan |

Incremental value: **P1 ships the feature**, P3 solves the repeat visit, and P2+P4 made the first
open proportional to what you are looking at -- each justified by measurement rather than expectation.

**What is deliberately NOT here.** The bright-prefix side asset (measured unnecessary, see above);
wasm threads (`web-multithreading.md` keeps its own case, and this phase made them worth *less*, not
more, since a wide view now decodes ~13 MB rather than 43.5 MB); and searching individual TYC stars,
still deferred. The IndexedDB cache (P3) now only serves the whole-catalog fallback -- on the member
path the browser's own HTTP cache holds the members, which is what it is for, and a partial buffer
written to IndexedDB would be indistinguishable from a complete one on the next visit.

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
- **One file per member is the addressing mechanism; byte ranges are NOT usable on either host.**
  GitHub Pages answers `206` over the *gzip* representation and Cloudflare Pages ignores `Range`
  entirely -- see "Byte ranges do not work" above for the measurements and why `curl` says otherwise.
  **Never re-derive this from a `curl` probe**: curl does not send a browser's `Accept-Encoding`, and
  that single difference is what makes the range design look correct.
- **The manifest is not optional and is not a tile index.** `LzipDecoder.FindMembers` enumerates
  members by walking BACKWARDS from the end of the file, reading each trailer's `member_size`, so a
  client holding the head of the file cannot find a single member boundary. The manifest carries only
  the framing (member count, raw length, each member's first region); the *spatial* index is still
  the bounds table, which is already embedded and needs no fetch.
- **Do not switch away from lzip to get free native decompression.** Pages serves gzip only and
  browsers' `DecompressionStream` has no brotli, so the only reachable alternative costs +6.9 MB to
  save 1802 ms -- break-even at ~31 Mbps, a regression below it. Measured; see above.

## Open questions / gates

**Answered 2026-08-16** (kept, because a plan that silently deletes its gates loses the record of
what it was allowed to assume):

- ~~Measured AOT serial decode wall-time (gates P2).~~ **1802 ms decompress + 93 ms flatten of a
  6.67 s cold load.** Re-measure with `AtlasLoadCostProbe` against a DEPLOYED build after any change
  to the bake; a dev server answers a different question.
- ~~Multi-member `MemberSize` sweet spot: decode-parallelism vs compression-ratio.~~ **64 KB
  region-aligned members: 636 of them, +1.9% on the asset, and a 3.0x-to-144x cut in what a view
  downloads.** The first pass at this answered "8 members cost 0.49%", which was true and irrelevant:
  8 members are not addressable. See the member-size table.

- ~~How many ranges a view actually costs.~~ **Dozens. Worst case 99 exact runs, 17 requests at a
  256 KB gap allowance; a 10-degree field costs 4 requests and ~1 MB.** See the run-count table.
  Computed by `Tycho2RegionSelectorTests` from the bounds table alone -- re-run it after any change
  to the bake or to the record order.

Still open:
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
