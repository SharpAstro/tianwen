# Plan: Catalog Data Binary Format (replace JSON + CSV)

Goal: cut `CelestialObjectDB.InitDBAsync` further by swapping the `.json` / `.csv`
payloads inside the `.lz` embedded resources for a format that's cheaper to
decode. Lookup paths don't change — this is purely about the one-shot init cost.
Keep lzip outer compression (it's doing a great job on these payloads and the
wire size is small; the cost is the JSON/CSV parse step on top, not the LZ).

> **Status (2026-05-05 update).** Option D **shipped end-to-end** (all 13 SIMBAD
> + 2 NGC catalogs migrated to the `.gs.gz` ASCII-separated format). Outer
> compression for the `.gs` payloads is gzip, not lzip — initial roll-out used
> `.gs.lz` (lzip), then a benchmark of BCL decoders showed `GZipStream` decodes
> small single-stream payloads 4-8× faster than the managed `LzipDecoder`, so
> the final shape is `.gs.gz`. Tycho2 stays on parallel multi-member lzip; see
> [§ Out-of-Scope] and [§ Phase 2] below.
>
> **Phase 2A also SHIPPED** (2026-05-05): `tools/precompute-hd-hip-cross/` bakes the
> output of `BuildHdHipCrossIndicesViaTyc` into an embedded `hd_hip_cross.bin.gz`
> resource (~2.4 MB). Cold-start init applies the snapshot in ~110 ms (parallelised
> read + SHA-256 input-hash check + dict apply) instead of recomputing it in ~460 ms.
> Net saving: **~350 ms** on the hd-hip-cross phase.
>
> Two test guards live in `HdHipCrossSnapshotTests`:
>   * `GivenEmbeddedSnapshot_WhenHashedAgainstCurrentInputs_ThenItIsFresh` —
>     fails CI if a contributor edits a catalog input without running
>     `tools/precompute-hd-hip-cross.ps1`.
>   * `GivenLiveAndSnapshotPaths_WhenComparingState_ThenTheyAgree` — sample
>     comparison between the live capture path and the apply path; catches
>     algorithm-vs-snapshot semantic drift.
>
> Runtime safety net: a stale or malformed snapshot logs a phase entry
> (`hd-hip-cross-snapshot:stale` / `:malformed`) and falls back to live compute,
> so init stays correct even if the embedded snapshot rots between releases.
>
> Real-life cold-start (post-migration, post-gzip):
>
> | Build | First `InitDBAsync` |
> |---|---|
> | Release + warm runtime (test bench) | **716–906 ms** (run-to-run variance) |
> | Debug + cold disk + cold JIT (GUI cold launch, `InitializePlanner` log) | **2,411 ms** |
>
> Phase-by-phase at Release+warm (one representative run, 716 ms total):
>
> ```
>   predefined            23.8 ms  (3%)
>   ngc-csv               79.8 ms  (11%)
>   simbad-total         200.8 ms  (28%)  HR alone = 123 ms
>   shapes                28.5 ms  (4%)
>   tycho2-join           79.1 ms  (11%)  pure idle wait on background Tycho2 LZ
>   cross-ref-json         8.9 ms  (1%)
>   hd-hip-cross         292.0 ms  (41%)  ← biggest single phase
> ```
>
> The hot phases are now **dict-mutation work** (SIMBAD merge + hd-hip-cross),
> not parse work. The format migration delivered the parse win Option D was
> designed for; the next 280–400 ms of cold-start savings live in Phase 2.

## Baseline (Snapdragon X, cold start, post-parallel-scan + HR-prefetch wins)

```
DB initialization:    ~729 ms
  predefined           17 ms
  ngc-csv             165 ms  ← CSV parse loop dominates
  simbad-total        240 ms  ← HR 131 ms + Dobashi 60 ms + HH 30 ms
  shapes               17 ms
  tycho2-join           0 ms  (in-flight with above)
  cross-ref-json       14 ms
  hd-hip-cross        266 ms  (parallel scan on 8 threads)
```

Parse-bound phases (SIMBAD + NGC-CSV) total **~405 ms / 55%** of init.
That's the target of this plan.

Tycho2 (`.bin.lz`), cross-ref (`*_to_tyc*.bin.lz` / `*.json.lz`) and shapes are
either already binary or too small to matter — leave them alone.

## Options Considered

### D. ASCII-separated text via .ps1 preprocessor (LEADING RECOMMENDATION)

Use ASCII control characters as separators (no escaping, ever — these bytes
don't appear in any catalog string). Encoder lives in PowerShell, runs as an
MSBuild step. No new NuGet deps, no new .NET project.

**Format**

```
File:   <record> 0x1D <record> 0x1D ...
Record: <field>  0x1E <field>  0x1E ... <field>
Field:  raw UTF-8; numbers in invariant-culture decimal (G17 for doubles)
        for variable-length sub-arrays (e.g. SIMBAD Ids[]):
        <item> 0x1F <item> 0x1F ...
```

`0x1D` GS = record terminator, `0x1E` RS = field separator, `0x1F` US =
sub-item separator. Output stream is then lzip-compressed as today —
embedded resources renamed to `*.gs.lz`.

**Reader pattern**

```csharp
var bytes = await DecompressLzAsync(stream);
foreach (var rec in bytes.Span.Split((byte)0x1D))
{
    var fields = rec.Split((byte)0x1E).ToArray();
    var ra  = double.Parse(Encoding.UTF8.GetString(fields[0]), NumberStyles.Float, CultureInfo.InvariantCulture);
    var dec = double.Parse(Encoding.UTF8.GetString(fields[1]), NumberStyles.Float, CultureInfo.InvariantCulture);
    // ...
}
```

`Span<byte>.IndexOf` is a `memchr` — same order of magnitude as MsgPack on
these payload sizes. Hand-written reader per catalog shape, but the readers
are ~30-50 lines each.

**Encoder** (`tools/preprocess-catalog.ps1`) — pure pwsh:
`Get-Content -Raw | ConvertFrom-Json`, iterate the records, build a
`StringBuilder` with `[char]0x1D/0x1E/0x1F`, `[Text.Encoding]::UTF8.GetBytes`,
shell to `lzip`. Wire as an MSBuild `<Exec>` target gated on input file
timestamps so it only re-runs when source `*.json` / `*.csv` change.

**Pros**

- **Zero new dependencies.** No `MessagePack` NuGet, no source generator, no
  AOT-compatibility verification needed.
- **No new .NET preprocessor project** — encoder is a single `.ps1` file in
  `tools/`. Less moving parts in the source tree.
- **Diff-able.** A `.gs.lz` file decompressed and piped through
  `tr '\035\036\037' '\n|,'` becomes human-readable — easier to spot encoder
  bugs than hex-dumping MessagePack.
- **AOT-trivial.** `Span<byte>.IndexOf` + `double.Parse(string, Invariant)`
  have no reflection paths.
- **Streaming-friendly.** Reader can iterate records without materialising
  the full list, same as the existing `JsonSerializer.DeserializeAsyncEnumerable`
  pattern.

**Cons**

- **Hand-written reader per catalog shape** (SIMBAD, NGC, cross-ref). Same
  drawback as Option B. Each new catalog adds ~30-50 lines of parsing.
- **Schema changes are coupled** — encoder `.ps1` + reader edit must land
  together. No `[Key(N)]` add-field-safe semantics like MessagePack.
- **Float round-trip needs care.** Encoder writes doubles with `G17`,
  reader parses with `NumberStyles.Float | AllowExponent` invariant. Worth
  a round-trip unit test per catalog.
- **PowerShell is the build dependency.** `pwsh` is already standard on the
  dev machine and CI runners; not a real new requirement, but it's a
  build-time prerequisite vs. self-contained .NET tooling.

**Estimated savings**: SIMBAD 240 → ~50 ms, NGC 165 → ~30 ms.
**Total: ~325 ms saved, init ≈ 405 ms.** Slightly better than Option A
because there's no MsgPack header overhead per record, just `IndexOf`.

### A. MessagePack (alternative)

Keep existing DTO shapes (`SimbadCatalogDto`, NGC row records). Swap
`JsonSerializer` → `MessagePackSerializer` (MessagePack-CSharp, neuecc). Write
a build-time MSBuild target that converts embedded `.json.lz` → `.msgpack.lz`
and `.csv.lz` → `.msgpack.lz`. Runtime reader is a one-line change per call site.

**Pros**

- Schema evolution is as safe as JSON (add-field / reorder-safe with
  `[Key(N)]` or named keys).
- Source-gen AOT support via `MessagePack.Generator` — no reflection at
  runtime, works under `PublishAot`.
- Existing DTOs stay DTOs; no new hand-written readers.
- MsgPack is ~5× faster to parse than JSON on these shapes (documented and
  replicable on our payload sizes).
- Already-compressed LZ of MsgPack is 20-30% smaller than LZ of JSON —
  wire and disk both improve.

**Cons**

- New NuGet dep in `TianWen.Lib` (`MessagePack` + `MessagePack.Annotations`).
  Small, stable, widely used.
- Build-time preprocess step adds complexity to the source tree.

**Estimated savings**: SIMBAD 240 → ~80 ms, NGC 165 → ~50 ms.
**Total: ~275 ms saved, init ≈ 465 ms.**

### B. Custom packed binary

Drop DTOs entirely. Hand-written reader consumes a fixed layout:

```
[total_count u32]
For each record:
  [ra f64] [dec f64] [vmag f16] [bv f16]
  [objType u32]
  [ids_count u16] [ids_string_pool_offsets u32 × ids_count]
  [common_names_count u16] [names_string_pool_offsets u32 × n]
[string_pool_length u32]
[string_pool utf8 bytes]
```

Parse is effectively memcpy + offset arithmetic. String de-dup at build time
means no runtime interning cost either.

**Pros**

- 10-20× faster than JSON — per-record cost drops to ~40 ns.
- Smallest on-disk size (often beats JSON-LZ even without LZ).
- Zero allocation per record at read time.

**Cons**

- Schema evolution is painful — add-a-field requires a version bump + branch in
  the reader. Historical incompat hurts.
- Hand-written reader per catalog shape (SIMBAD, NGC, cross-ref variants).
- More test surface.

**Estimated savings**: SIMBAD 240 → ~20 ms, NGC 165 → ~10 ms.
**Total: ~375 ms saved, init ≈ 350 ms.**

### C. Hybrid

MsgPack for variable-shape records (SIMBAD: `Ids[]` per record), custom packed
binary for fixed-width tables (NGC: one row = fixed column set).

About the same engineering cost as A, ~30% extra payoff on the NGC side. Not a
compelling step up from A given the complexity.

## Recommendation

Start with **D (ASCII-separated text + .ps1 preprocessor)**. Delivers ~325 ms
saving with zero new NuGet deps and no new .NET project. Reader is hand-rolled
per catalog shape, but each reader is small (~30-50 lines) and fully AOT-safe.

Tycho2 stays untouched (already binary, off the critical path) — see
"Out-of-Scope" below.

## Build-time Preprocessing Design

Add a single PowerShell script `tools/preprocess-catalog.ps1` that:

1. Takes a catalog source file (`*.json` or `*.csv`) and an output path.
2. Parses it with `ConvertFrom-Json` (or a CSV split for NGC).
3. Re-emits records using ASCII separators:
   - `0x1D` (GS) between records
   - `0x1E` (RS) between fields within a record
   - `0x1F` (US) between sub-items within a variable-length field
4. UTF-8 encodes the result, pipes through `lzip` to write `<name>.gs.lz`.

Wire it as an MSBuild `Exec` target on `TianWen.Lib.csproj`, gated on input
timestamps:

```xml
<Target Name="PreprocessCatalogs" BeforeTargets="EmbedResources"
        Inputs="@(CatalogSource)" Outputs="@(CatalogSource->'%(RelativeDir)%(Filename).gs.lz')">
  <Exec Command="pwsh -NoProfile -File tools/preprocess-catalog.ps1 -Input %(CatalogSource.FullPath) -Output %(CatalogSource.RelativeDir)%(CatalogSource.Filename).gs.lz" />
</Target>
```

`<EmbeddedResource>` items switch from `*.json.lz` / `*.csv.lz` to `*.gs.lz`.
The JSON/CSV source files stay in the repo for diff-ability and as the
preprocessor's input. LFS already tracks them; add `*.gs.lz` to the same glob.

**Float-encoding rule.** Encoder writes doubles via `[double]::ToString('G17', [Globalization.CultureInfo]::InvariantCulture)`
to guarantee round-trip. Decoder uses `double.Parse(span, NumberStyles.Float, CultureInfo.InvariantCulture)`.
Add a round-trip unit test per catalog as part of HR-SIMBAD rollout.

## Runtime Reader Changes

One hand-written reader per catalog shape. Current flow:

```csharp
var records = new List<SimbadCatalogDto>(4096);
await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable(
    memoryStream, SimbadCatalogDtoJsonSerializerContext.Default.SimbadCatalogDto, ct))
{
    if (record is not null) records.Add(record);
}
```

becomes (sketch):

```csharp
var bytes = await DecompressLzToMemoryAsync(stream, ct);
var span = bytes.Span;
const byte GS = 0x1D, RS = 0x1E, US = 0x1F;

while (span.Length > 0)
{
    var recEnd = span.IndexOf(GS);
    var rec = recEnd < 0 ? span : span[..recEnd];
    span = recEnd < 0 ? default : span[(recEnd + 1)..];

    // Slice fields by RS (0x1E)
    var f0End = rec.IndexOf(RS); var ra = ParseDouble(rec[..f0End]);
    rec = rec[(f0End + 1)..];
    var f1End = rec.IndexOf(RS); var dec = ParseDouble(rec[..f1End]);
    rec = rec[(f1End + 1)..];
    // ... remaining fields ...

    // Sub-array (Ids) split by US (0x1F)
    foreach (var idSpan in rec.Split(US))
        ids.Add(Encoding.UTF8.GetString(idSpan));

    records.Add(new SimbadCatalogDto(ra, dec, ..., ids.ToImmutableArray()));
}

static double ParseDouble(ReadOnlySpan<byte> utf8) =>
    double.Parse(Encoding.UTF8.GetString(utf8), NumberStyles.Float, CultureInfo.InvariantCulture);
```

`MergeLzCsvData` is replaced by the same span-slicing pattern — `CsvFieldReader`
goes away for these payloads (CSV-quote handling is no longer needed because
`0x1E` cannot appear in field content).

The depth-1 SIMBAD prefetch pipeline and HR early-start stay unchanged; the
only thing that changes inside `ParseSimbadFileAsync` / `DecompressCsvAsync`
is the inner deserialisation step.

A small shared helper `AsciiRecordReader` (probably in
`TianWen.Lib/IO/AsciiRecordReader.cs`) wraps the boilerplate so each catalog
shape only declares its column layout, not the slicing logic.

## Incremental Rollout

One catalog at a time to de-risk. Suggested order:

1. **HR SIMBAD** first (heaviest file, biggest payoff). Prove the encoder + reader +
   round-trip test end-to-end on one file before changing everything.
2. **NGC CSV** (second-heaviest, different code path — exercises CSV-style input
   to the encoder; encoder produces the same `.gs.lz` output regardless).
3. Remaining SIMBAD files (small, low-risk, high-volume sanity check that the
   shared `AsciiRecordReader` helper holds up).
4. Cross-ref JSONs (`hip_to_tyc_multi`, `hd_to_tyc_multi`) — only 14 ms total
   so this is about consistency, not speed.

Each step: verify with the 1649-test suite + the init time harness in
`CelestialObjectDBBenchmarkTests.GivenNewDBWhenInitializingThenItCompletesInUnder20Seconds`
(prints per-phase breakdown).

## Phase 2: Pre-bake init state (~280–400 ms savings target)

Format migration is done; remaining cost is **dict mutation**, which can't be
parallelised further without changing semantics. The next step is to skip the
work entirely by shipping the post-init state as an embedded resource and just
applying the delta at runtime.

### 2A. Pre-bake `BuildHdHipCrossIndicesViaTyc` (~280 ms target)

`hd-hip-cross` is currently 292–331 ms / ~40 % of init. Sub-phase breakdown
(timers added in `BuildHdHipCrossIndicesViaTyc` 2026-05-04):

```
hd-hip-cross:tycToHip            33 ms   build TYC→HIP reverse index
hd-hip-cross:scan               152 ms   8-way Parallel.For over hdToTyc
hd-hip-cross:objects+union       78 ms   serial: HD obj insert + EdgeDelta union
hd-hip-cross:bulk-merge          67 ms   serial: tuple rebuild per affected key
                                ────
                                330 ms
```

145 ms (objects+union + bulk-merge) is **serial dict mutation on the main
thread** — not improvable by more cores. The 152 ms parallel scan reads
runtime-mutated state (`_objectsByIndex`, `_crossIndexLookuptable`) so it
can't easily move to the Tycho2 background task either. Pre-bake skips the
whole 330 ms.

**Determinism check.** The output of `BuildHdHipCrossIndicesViaTyc` is fully
determined by build-time-frozen inputs:

- `tyc2.bin.lz`, `tyc2_gsc_bounds.bin.lz` (Tycho2 RA/Dec/V/B-V data)
- `hip_to_tyc.bin.lz`, `hd_to_tyc.bin.lz` (binary HIP↔TYC, HD↔TYC)
- `hip_to_tyc_multi.json.lz`, `hd_to_tyc_multi.json.lz` (cross-ref-json)
- All `.gs.gz` SIMBAD catalogs (their cross-ref additions to
  `_crossIndexLookuptable` are read by the propagation step)
- Compiled-in `ConstellationBoundary` polygon data
- `_predefinedObjects` dictionary (in source)

All immutable at runtime → the result is byte-for-byte reproducible.

### Build-time pipeline

A new `tools/precompute-hd-hip-cross/` console project that:

1. References `TianWen.Lib` via `ProjectReference` (with `InternalsVisibleTo`).
2. Runs `new CelestialObjectDB().InitDBAsync()` against the same embedded
   resources the runtime sees.
3. After init, captures the **delta** that `BuildHdHipCrossIndicesViaTyc`
   applied:
   - **New HD entries**: list of `(hdIndex, ra, dec, vMag, bv, constellation, objType)`
     — every HD CelestialObject created by the phase.
   - **Edge delta**: list of `(key, [target1, target2, …])` — the new edges
     added to `_crossIndexLookuptable` (post-`MergeEdgesBulk` shape, i.e.
     consolidated per key).
4. Emits a single binary file `hd_hip_cross.bin.gz`.
5. MSBuild target conditional on `BuildPrecomputed=true` (or input mtimes) so
   the snapshot regenerates when any of the determinism-input files change.

### Binary format (sketch)

```
[ 8 bytes ] magic           = "TWHDHIP1"
[ 4 bytes ] schema version  = uint32
[32 bytes ] input hash      = SHA-256 of concat(input file bytes + algorithm version)
[ 4 bytes ] hd_count        = uint32
[ N * 28 ] hd_entries       = (hdIndex u64, ra f64, dec f64, vMag f16, bv f16, constellation u8, objType u8) × N
[ 4 bytes ] edge_count      = uint32 (edges = sum of all targets)
[ 4 bytes ] key_count       = uint32 (distinct from-keys)
[ M *  ?? ] edge_delta      = packed (fromKey u64, target_count u16, targets u64[]) × M
```

Estimated size: 13 K HDs × 28 B ≈ 364 KB + 200 K edges × 8 B ≈ 1.6 MB →
~2 MB raw → ~500 KB-1 MB gzipped.

### Runtime path

In `InitDBCoreAsync`, replace `BuildHdHipCrossIndicesViaTyc()` with:

```csharp
phaseSw.Restart();
if (TryLoadPrecomputedHdHipCross(assembly, manifestNames, out var snapshot)
    && snapshot.InputHash == ComputeInputHash(...))
{
    ApplyHdHipCrossSnapshot(snapshot);  // ~30-50 ms estimated
}
else
{
    _logger.LogWarning("hd-hip-cross snapshot stale or missing — recomputing live (slow path)");
    BuildHdHipCrossIndicesViaTyc();
}
_lastInitPhaseTimings.Add(("hd-hip-cross", phaseSw.Elapsed));
```

The hash check is the safety net: if a contributor edits SIMBAD data without
re-running precompute, the runtime falls back to live computation with a
warning, but stays correct. CI guards this with a "precomputed snapshot is
fresh" test that re-runs the live path and diffs against the snapshot.

### Tradeoffs

- **Pro:** ~280 ms saved (most of hd-hip-cross's 330 ms minus 30–50 ms
  deserialise + apply).
- **Pro:** Snapshot stays small (~500 KB compressed), comfortable to ship
  embedded.
- **Pro:** `objType` correctly captured at build time — no runtime patching,
  because SIMBAD merge runs identically at build time.
- **Pro:** Drop the embedded `hip_to_tyc.bin.lz` and `hd_to_tyc.bin.lz`
  resources entirely (they're only used to feed `BuildHdHipCrossIndicesViaTyc`,
  which the snapshot now provides). Saves ~1.2 MB of LFS.
- **Con:** New `tools/precompute-hd-hip-cross/` project + InternalsVisibleTo
  + binary serialiser/deserialiser + CI guard test. Real engineering work,
  estimated 1–2 days.
- **Con:** Snapshot has to be regenerated when any of ~20 input files change.
  Most contributors won't trigger this path (catalog data is stable), but
  the regeneration step needs to be one command.

### 2B. Pre-bake SIMBAD merge state (~150 ms target, FOLLOW-UP)

Same pattern as 2A applied to the SIMBAD merge phase. Today's
`simbad-total = 201 ms` is mostly dict insertions into `_objectsByIndex` +
cross-ref edge writes into `_crossIndexLookuptable`. Both are deterministic
from the embedded `.gs.gz` files + `_predefinedObjects`.

Defer until 2A ships and we've measured the deserialise-and-apply cost
empirically — Phase 2A's binary format will likely be reusable here.

### 2C. Lookup-speed BFS pooling (separate, smaller win)

(Was previously the secondary section below.) Out of scope for Phase 2's main
win but tackle in the same PR series if 2A's `ApplyHdHipCrossSnapshot` happens
to land the same `FrozenDictionary` shape.

## Out-of-Scope (explicitly NOT touched)

- **Tycho2** (`tyc2.bin.lz` + `tyc2_gsc_bounds.bin.lz`): the file is built
  with `lzip -9 -b 4MiB` (multi-member, ~5–6 members on the 21 MB tyc2.bin.lz),
  and `LzipDecoder` decompresses members in parallel via `Parallel.For` over
  threadpool. **Switching to gzip would lose that parallelism** — single-stream
  GZipStream has no built-in multi-member parallel decode in the BCL, and
  hand-rolling one mirrors the existing LzipDecoder for zero net gain. Leave
  on lzip.
- **LZ outer compression on `.gs` files**: replaced with gzip (small
  single-stream payloads, where BCL GZipStream's 4-8× faster decode wins
  over `LzipDecoder`). See top status block.
- **Disk cache** of the fully-built dicts: complementary but separate plan. A
  disk cache skips parsing entirely on cache-hit (the common case). This plan
  is about making the cache-miss / first-launch path faster for release bumps.
  Do both; do the cache after the format migration to keep the cache-file
  structure simple. Phase 2 reduces the cache-miss cost to the point where the
  cache may not be worth doing.

## Open Questions

- **AOT compatibility**: MessagePack has a source-generator path
  (`MessagePack.Generator`). Need to verify it works with our
  `TianWen.Lib.SourceGenerators` (the project already has a source-gen setup,
  so adding another generator shouldn't conflict, but confirm).
- **Fallback format**: do we ship both `.json.lz` AND `.msgpack.lz` during the
  migration window, or hard-cut? Hard-cut is cleaner; `.json.lz` files stay in
  the tree as preprocessor input but don't ship as embedded resources.
- **LFS & build speed**: preprocessor reads `.json.lz` (LFS-tracked) and writes
  `.msgpack.lz` (will be LFS-tracked by the `*.lz` glob). Local dev with LFS
  materialised is fine; CI `lfs: true` already fetches real content.

## Lookup Speed (secondary)

Lookup is already fast (90 ns `TryLookupByIndex`, 722 ns `TryGetCrossIndices`
with 4 KB alloc from per-call BFS) per the `CelestialObjectDBBenchmarks`. The
cross-index BFS allocations are the only obvious remaining win:

- Replace the per-call `new HashSet` + `new List` with `ArrayPool<CatalogIndex>`
  frontier buffers → drops to ~0 B allocated.
- Or pre-compute transitive closures at init into
  `FrozenDictionary<CatalogIndex, ImmutableArray<CatalogIndex>>` → lookup
  becomes a single dict-hit (~50 ns), zero alloc. Shifts cost to init (~50 ms
  extra), but with the format migration above init has the headroom.

This is a small, targeted change to do AFTER the format migration lands —
revisit when the benchmark shows it's worth it.
