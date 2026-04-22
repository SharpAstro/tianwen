# Plan: Catalog Data Binary Format (replace JSON + CSV)

Goal: cut `CelestialObjectDB.InitDBAsync` further by swapping the `.json` / `.csv`
payloads inside the `.lz` embedded resources for a format that's cheaper to
decode. Lookup paths don't change — this is purely about the one-shot init cost.
Keep lzip outer compression (it's doing a great job on these payloads and the
wire size is small; the cost is the JSON/CSV parse step on top, not the LZ).

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

### A. MessagePack (leading recommendation)

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

Start with **A (MessagePack everywhere)**. Deliver ~275 ms saving at low risk.
If after shipping A we still want more speed, revisit the NGC-specific custom
binary as a targeted upgrade (the NGC payload is smaller and regular — easy
second pass).

## Build-time Preprocessing Design

Add a small .NET tool `TianWen.CatalogPreprocess` (subfolder in `src/`) that:

1. Takes a catalog source file (`*.json` or `*.csv`).
2. Parses it with the EXISTING reader.
3. Re-serialises to MessagePack.
4. Writes `<name>.msgpack` next to the source.

Wire it as an MSBuild target on `TianWen.Lib.csproj`:

```xml
<Target Name="PreprocessCatalogs" BeforeTargets="EmbedResources">
  <!-- For each catalog source, run the preprocessor if the .msgpack is stale. -->
</Target>
```

`<EmbeddedResource>` items then switch from `*.json.lz` / `*.csv.lz` to
`*.msgpack.lz`. External lzip invocation stays the same (same tool, different
input file).

The JSON/CSV source files stay in the repo for diff-ability and for the
preprocessor's input. LFS already tracks them.

## Runtime Reader Changes

Minimal. The current flow:

```csharp
var records = new List<SimbadCatalogDto>(4096);
await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable(
    memoryStream, SimbadCatalogDtoJsonSerializerContext.Default.SimbadCatalogDto, ct))
{
    if (record is not null) records.Add(record);
}
```

becomes:

```csharp
var records = MessagePackSerializer.Deserialize<List<SimbadCatalogDto>>(
    memoryStream, ContractlessStandardResolver.Options, ct);
```

`SimbadCatalogDto` gets `[MessagePackObject(keyAsPropertyName: true)]` +
`[Key(N)]` per field (or named keys for schema-evolution robustness).

`MergeLzCsvData` switches from `CsvFieldReader(text, ';')` to iterating the
deserialised list — no string parsing.

The depth-1 SIMBAD prefetch pipeline and HR early-start stay unchanged; the
only thing that changes inside `ParseSimbadFileAsync` / `DecompressCsvAsync`
is the inner deserialisation step.

## Incremental Rollout

One catalog at a time to de-risk. Suggested order:

1. **HR SIMBAD** first (heaviest file, biggest payoff). Prove the pipeline
   end-to-end on one file before changing everything.
2. **NGC CSV** (second-heaviest, different code path — exercises the CSV side
   of the preprocessor).
3. Remaining SIMBAD files (small, low-risk, high-volume sanity check).
4. Cross-ref JSONs (`hip_to_tyc_multi`, `hd_to_tyc_multi`) — only 14 ms total
   so this is about consistency, not speed.

Each step: verify with the 1649-test suite + the init time harness in
`CelestialObjectDBBenchmarkTests.GivenNewDBWhenInitializingThenItCompletesInUnder20Seconds`
(prints per-phase breakdown).

## Out-of-Scope (explicitly NOT touched)

- **Tycho2** (`tyc2.bin.lz` + `tyc2_gsc_bounds.bin.lz`): already binary, loaded
  in ~200 ms on a background thread while the main path parses SIMBAD; not on
  the critical path. Leave alone.
- **LZ compression**: stays. Wire/disk size is small, and LZ decompress is
  already overlapped with other work via the prefetch pipeline.
- **Disk cache** of the fully-built dicts: complementary but separate plan. A
  disk cache skips parsing entirely on cache-hit (the common case). This plan
  is about making the cache-miss / first-launch path faster for release bumps.
  Do both; do the cache after the format migration to keep the cache-file
  structure simple.

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
