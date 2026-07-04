# Lzip.Lib — managed lzip codec sibling repo (plan)

**Status:** P1 **DONE** (repo `SharpAstro/Lzip.Lib`, decoder-only, published `Lzip.Lib 1.0.21`).
P1.5 **DONE** (TianWen re-pointed at the package; in-tree `LzipDecoder` deleted; branch
`feat/lzip-lib-repoint`). P2 (encoder) + P3 (de-externalize build/regeneration) remain.

Extract the managed lzip codec into its own SharpAstro sibling repo (like `SharpAstro.Jpeg` /
`SER.Lib` / `DIR.Lib`), published to NuGet, and have TianWen depend on it -- so we drop the external
`lzip` CLI dependency entirely (build, runtime, and offline regeneration). Today TianWen has a managed
**decoder** (`LzipDecoder`) but **no encoder**; all compression to `.lz` shells out to the `lzip -9`
binary. This repo adds the missing encoder and gives the codec a reusable home.

## Motivation / current state (from the sim-CI branch investigation)

| lzip use | direction | today | after |
|---|---|---|---|
| build (`PreprocessCatalogs` / `tools/preprocess-catalog.ps1`) | decode `.json.lz` / `.csv.lz` | external `lzip -dc` | `SharpAstro.Lzip` decoder |
| runtime (`CelestialObjectDB`, `SkyMapTab`) | decode `tyc2.bin.lz` + bounds + sidecar + others | managed `LzipDecoder` (already) | `SharpAstro.Lzip` decoder (moved) |
| regeneration (`Get-Tycho2Catalogs.ps1` `Compress-WithLzip`, `tools/generate_milkyway.cs`) | **encode** `.lz` (`lzip -9 -b <n>MiB`) | external `lzip` | `SharpAstro.Lzip` **encoder** (new) |

Runtime already decodes with our managed code; the only external-`lzip` touchpoints are the build
decode and the regeneration encode. The decoder is self-contained (only `System.*`), so extraction is
mechanical; the encoder is the real new work.

## Naming / conventions (DECIDED)

- **Repo/package name:** `Lzip.Lib` (matches `SER.Lib` / `DIR.Lib` / `FITS.Lib`), with
  **`RootNamespace = SharpAstro.Lzip`** (the SharpAstro-org namespace convention -- same split as
  `SER.Lib` -> `SharpAstro.Ser`). PackageId `Lzip.Lib`, namespace `SharpAstro.Lzip`.
- **Target framework:** `net10.0` to match the SharpAstro line (external netstandard reuse not needed).
- Repo lives at `../Lzip.Lib` (sibling), CI `.github/workflows/dotnet.yml` with the standard
  `VERSION_PREFIX: 1.0.${{ github.run_number }}` publish-to-NuGet flow, centralized
  `Directory.Packages.props`. Mirrored SER.Lib (the closest recent template).

## Repo layout

```
Lzip.Lib/                            # PackageId Lzip.Lib, RootNamespace SharpAstro.Lzip
├── src/Lzip.Lib/Lzip.Lib.csproj
│   ├── LzipDecoder.cs            # DONE: moved verbatim from TianWen.Lib/Astrometry/Catalogs
│   ├── LzipEncoder.cs            # P2: NEW public API
│   ├── LzipOptions.cs            # P2: NEW (level, dict size, member/chunk size)
│   ├── LzipHeader.cs / Crc32.cs  # P2: shared framing helpers (factor out of the decoder)
│   └── Lzma/                     # P2: LZMA1 core (vendored -- see "Encoder")
├── src/Lzip.Lib.Tests/          # DONE: golden-fixture decoder tests (single.lz / multi.lz)
├── .github/workflows/dotnet.yml # DONE
├── Directory.Build.props / Directory.Packages.props / Lzip.Lib.slnx
├── README.md                    # API + (P2) LZMA-SDK attribution
└── LICENSE                       # MIT
```

Namespace: `SharpAstro.Lzip` (decoder moved from `TianWen.Lib.Astrometry.Catalogs`).

## Public API

Decoder (unchanged behaviour, just relocated):
- `byte[] LzipDecoder.Decompress(Stream | byte[] | ReadOnlySpan<byte>)`
- `Stream LzipDecoder.DecompressToStream(Stream)` (lazy)

Encoder (new):
- `byte[] LzipEncoder.Compress(ReadOnlySpan<byte> data, LzipOptions? options = null)`
- `void LzipEncoder.Compress(Stream input, Stream output, LzipOptions? options = null)`
- `LzipOptions { int Level = 9; int? DictSize = null; long MemberSize = 0 /* 0 = single member */; }`
  - `Level` maps to lzip's `-0..-9` (dict size + match-len limits); `DictSize` overrides; `MemberSize`
    is lzip's `-b` (split the input into independent members of ~this many *uncompressed* bytes, which
    is what enables `LzipDecoder`'s parallel multi-member decode -- keep the two in lockstep).

## Encoder = the real work (LZMA1 + lzip framing)

The lzip *container* is easy (the decoder already defines it, so the encoder must produce exactly what
`LzipDecoder` reads):
- Member = 6-byte header (`"LZIP"` + version `1` + coded dict-size byte) + LZMA1 stream + 20-byte
  trailer (CRC32 of the uncompressed data + uncompressed size `u64` + member size `u64`).
- Fixed LZMA properties `lc=3, lp=0, pb=2` (implied by lzip, not stored). Standard CRC-32 (reflected,
  poly `0xEDB88320`) over uncompressed bytes. Multi-member = concatenated independent members.
- The dict-size byte coding and member parsing are already implemented in `LzipDecoder` -- factor the
  shared bits (`Crc32`, header read/write, dict-size (de)coding) into `LzipHeader`/`Crc32` used by both.

The hard part is the **LZMA1 compressor** (range coder + match finder + length/distance coders +
lazy/optimal parse). Do NOT hand-roll from scratch. Approach:
- **Vendor the public-domain 7-Zip LZMA SDK C# encoder** (Igor Pavlov, public domain) into `Lzma/`.
  It becomes our in-repo source (no external/binary dep -- consistent with "no external deps"; a
  vendored PD source file is not a dependency to install). Attribute in `NOTICE`/`README`.
- Adapt: the SDK `Encoder` emits raw LZMA1 + a 5-byte props header + 8-byte size; we want the raw
  LZMA1 stream only (props are implied by lzip) wrapped in our lzip framing. Drive the SDK core, skip
  its property/size header, and let it emit the end-of-stream marker (lzip streams use it).
- Map `Level`/`DictSize` to the SDK's `DictionarySize` / `NumFastBytes` / match-finder settings to
  approximate `lzip -9`.

### Byte-parity expectation (manage this)

Do NOT aim for byte-identity with the `lzip` binary. lzip and the 7-Zip SDK use different match
finders, so they produce *different but equally valid* lzip files for the same input. The contract is
**cross-decode compatibility**, not byte-equality:
1. our-encode -> our-decode round-trips to the original (primary, CI-friendly, no external lzip).
2. our-decode reads committed golden `.lz` files produced by the real `lzip` binary (proves we read
   real lzip output -- the existing TianWen `.lz` fixtures already exercise this in the decoder).
3. our-encode -> real `lzip -d` decodes cleanly (proves real lzip reads us). Needs the lzip binary, so
   run it in a CI job that installs lzip, or as a gated/manual check -- NOT in the default unit run.

## Tests

- Decoder tests: move the existing decoder coverage; add the golden-`.lz`-fixture reads (#2 above).
- Encoder tests: round-trip over sizes/patterns incl. empty, single-byte, > member-size (multi-member),
  and random data; assert `MemberSize` actually produces N members `LzipDecoder` can decode in parallel;
  cross-decode (#3) in a lzip-installed CI job.

## Integration back into TianWen (after each publish; respect the release dance)

Never push TianWen referencing an unpublished package version -- publish to NuGet first, then re-point.

1. **DONE (P1.5).** Added `Lzip.Lib` `1.0.*` to `src/Directory.Packages.props` + the `UseLocalSiblings`
   set in `Directory.Build.props` (+ conditional `ProjectReference` in `TianWen.Lib.csproj`), like the
   other siblings.
2. **DONE (P1.5).** Deleted `src/TianWen.Lib/Astrometry/Catalogs/LzipDecoder.cs`; the only two callers
   (`CelestialObjectDB`, `SkyMapTab`) now `using SharpAstro.Lzip;` (SkyMapTab gets the package
   transitively through TianWen.Lib). 349 catalog tests green against the extracted decoder.
3. Replace external `lzip -dc` in `tools/preprocess-catalog.ps1` with a tiny `dotnet` decode step (or
   port the whole preprocess to a small `tools/` .NET tool) using `SharpAstro.Lzip`.
4. Replace `Compress-WithLzip` (`Get-Tycho2Catalogs.ps1`) and `generate_milkyway.cs`'s `lzip -9` with
   the `SharpAstro.Lzip` encoder.
5. Drop the `lzip` install from `dotnet.yml` + `simulators.yml` (`catalogs` job) once nothing shells
   out to it. Update the CLAUDE.md / preprocess-catalog.ps1 docstrings that mention "lzip on PATH".

## Phasing

| Phase | Scope | Ships | Status |
|---|---|---|---|
| P1 | New repo skeleton + conventions; move `LzipDecoder` (+ tests) verbatim; publish decoder-only | `Lzip.Lib` 1.0 | **DONE** (1.0.21) |
| P1.5 | Re-point TianWen to the package; delete the local decoder (integration steps 1-2) | TianWen consuming it | **DONE** (`feat/lzip-lib-repoint`) |
| P2 | Add `LzipEncoder` (vendor LZMA SDK + framing + chunking) + tests | `Lzip.Lib` 1.1 | TODO |
| P3 | TianWen: swap build decode + regeneration encode to managed; remove all external `lzip` (steps 3-5) | zero external lzip | TODO |

P1/P1.5 are low-risk and immediately give the sibling structure + a NuGet package; P2 is the big lift
(the encoder); P3 finishes the de-externalization.

## Decisions (resolved)

1. **Name:** `Lzip.Lib` (PackageId), `SharpAstro.Lzip` namespace -- like `SER.Lib` -> `SharpAstro.Ser`.
2. **Target:** `net10.0` (match the SharpAstro line; external netstandard reuse not needed).
3. **Encoder source (P2):** vendor the public-domain 7-Zip LZMA SDK (no re-introduced package dep).
4. **Scope of first cut:** P1+P1.5 (decoder package + re-point); encoder deferred to P2.
