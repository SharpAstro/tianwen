# Astro Image Archive Survey (D: drive)

**Status: reference snapshot, not a phased plan.** Backs
[ai-denoise-deconv.md](ai-denoise-deconv.md) §2.3 (the P0 dataset-builder input). Read-only survey
of `D:\` performed 2026-07-11. Re-run before P0 starts if the archive has changed materially —
this is a point-in-time snapshot, not a live index.

**Method note:** counts below are folder-listing + extension/size aggregation (`Get-ChildItem
-Recurse`) cross-checked against **real FITS headers read directly** (ASCII text in the first
2880-byte block) for one representative file per era/camera — every sample's `FRAMETYP`/`IMAGETYP`
matched its folder's implied frame type. A full-corpus per-file `IMAGETYP` census (~83,500 files)
was attempted but aborted as too slow/disk-bound for a one-off survey; treat the light-vs-calibration
splits as good estimates, not a byte-exact count. `tianwen dataset build` (P0) must derive the real
split from `IMAGETYP` + `EXPTIME` per file, not from folder names.

## 1. Roots surveyed

| Root | Files | Size | Role |
|---|---|---|---|
| `D:\Astro-Pics` | 60,616 | 1,329.8 GB | Primary, organized archive |
| `D:\BobbyBox-Temp` | 22,930 | 632.4 GB | Secondary working tree — partially duplicates Astro-Pics 2024/early-2025, plus unique Aug–Nov 2025 sessions not yet filed into Astro-Pics. Largely a **live PixInsight workspace** (3,337 XISF files, 165.5 GB of calibrated/debayered/registered/master intermediates sitting next to the raw subs) |
| `D:\stack` | small | — | Transient single-session TianWen `stack` working folder (BIAS/DARK/FLAT/LIGHT/output), not part of the archive |
| `D:\Pictures` | — | — | Phone/DSLR family photos, non-astro, out of scope |

Everything else at the D: root (`azdi-corpus`, `prod-train-corrected*`, `temp`, `BitLocker...txt`,
`Temp.7z`) is non-astro and was not touched. Combined astro total: **~83,500 files, ~1.96 TB**.

## 2. Layout conventions

### Astro-Pics (evolves over time)

- **2021–2022**: SharpCap only. `Year\Target Name\{Light,Dark,Bias,proc}\...`; mono filter-wheel
  sessions further split per-filter (`M83 LUM\M83 R\M83 G\M83 B\M83 Ha\...`).
- **2023–mid-2024**: SharpCap, N.I.N.A.-style nesting appears:
  `Target\Date\{Light,Dark,Flat,Bias}\HH_MM_SSZ\{rawframes,AutoSave,proc}`.
  **`AutoSave\Stack_16bits/32bits_Nframes_*.fits` are SharpCap's own live-stack previews, not raw
  subs** — must be excluded from any raw-light training pool.
- **mid-2024 onward**: capture software switches SharpCap → **N.I.N.A. 3.1–3.2** (confirmed via the
  `SWCREATE` header field).
- **2025 (Jan–May, in Astro-Pics)**: flatter `YYYY-MM-DD - Target\{BIAS,DARK,DARKFLAT,FLAT,LIGHT,PROC,MASTER}`.
- **2026**: consistently flat `YYYY-MM-DD[ Target]\{BIAS,DARK,DARKFLAT,FLAT,LIGHT,PROC}`. Includes
  `2026-02-20 BAD LIGHT EXAMPLES` — 33 frames the user hand-flagged as bad (prefixed `BAD_`, all
  ASI533MC Pro / Rim Nebula / 60 s subs) — a ready-made small negative-label set for a focus/quality
  gate.
- **`Vela SNR Moasic Project`** (own top-level root, Dec 2025–Apr 2026): 13+ mosaic panels, each
  with its own BIAS/DARK/DARKFLAT/FLAT/LIGHT + `_Proc`, plus combined `Panel 1-20`/`Panel 14-20`
  PixInsight (XISF) integrations.
- **Duplication found *within* Astro-Pics itself**, independent of the BobbyBox-Temp overlap: e.g.
  `2024\June 2024\Oph Mol Cloud 120s F2.8 RGB\...` and the top-level
  `2024\Oph Mol Cloud 120s F2.8 RGB\...` show identical timestamps/frame counts — the same session
  filed twice.

### BobbyBox-Temp

Flat target/date dirs at top level, no year folders. Also contains `PI_SWAP`, `pixinsight`,
`reproc`, `tests` — processing-workspace clutter, not raw data. Holds genuinely unique, newer
content not present in Astro-Pics at all: `2025-08-09`/`2025-08-20 - Helix Nebula`, `2025-03-20`,
`2025-03-22 Leo Triplet`, and `2025-10` (biggest/newest — dated subfolders `2025-10-28` &
`2025-11-03`, targets comet **C/2025 R2 (SWAN)**, Horsehead Nebula, M45, Orion, Tarantula Nebula,
Triangulum Galaxy, shot on the new SVBONY SV605CC).

## 3. Camera / equipment timeline (read from real FITS headers)

| Era | Camera(s) | Type | Frame size | Scope(s) | Capture SW |
|---|---|---|---|---|---|
| 2021 | ZWO ASI294MC / ASI294MM | OSC / mono+FILTER | 4144×2822 | — | SharpCap 4.0 |
| 2022 | ASI294MM + QHYCCD QHY178m | mono | 3056×2048 | — | SharpCap 4.0 |
| 2023 | ZWO ASI533MC Pro; Player One Uranus-C (IMX585) | OSC | 3008×3008 / 3856×2180 | — | SharpCap 4.0 |
| 2024 | ASI533MC Pro, ASI585MC Pro, Canon 6D (CR2, 1 session only) | OSC | 3008×3008 | Samyang 135/2.8, WO ZS61 | SharpCap → N.I.N.A. 3.1 |
| 2025 | ASI533MC Pro, ASI585MC Pro, ASI1600MM (mono, 1 filtered session) | OSC/mono | mixed | Samyang 135, ZS61, FMA180 | N.I.N.A. 3.1–3.2 |
| 2026 (+ BobbyBox 2025-10/11) | ASI533MC Pro + **new SVBONY SV605CC** | OSC (RGGB / **GRBG**) | 3008×3008 | Samyang 135, SH61 EDPH | N.I.N.A. 3.2 |

Site constant across all headers: Glen Waverley, lat −37.877, lon 145.178. Focusers seen in
headers: EAF, **Gemini Focuser Pro**, QFocuser — the same devices TianWen's own drivers target.
**SV605CC uses GRBG, opposite the RGGB majority (ASI533/585/294MC)** — the dataset builder's
per-camera debayer must not assume a single Bayer pattern.

**v1 scope for ai-denoise-deconv**: ASI533MC Pro (2023–2026 mainstay) + ASI585MC Pro (2024–25) +
the one ASI1600MM session (2025) + SVBONY SV605CC (Aug 2025+) — the "recent/good" band. 2021–2022
(ASI294/QHY178m, ~668 GB combined) is lower value/excluded; Uranus-C/IMX585, Canon 6D CR2, and all
SER/planetary video are excluded from v1.

## 4. Extension / size breakdown (combined)

FITS 67,975 files / 1,322 GB + FIT 5,065 / 23 GB dominate. Also of note: SER 97 files / 225 GB
(planetary/lucky-imaging video, separate domain, excluded from v1), XISF 4,022 / 213 GB (PixInsight
intermediates, not raw — see §2), 7z 43 archives / 100 GB (**not extracted — see hazard #5 below**),
CR2 467 / 11 GB (Canon 6D), afphoto 50 / 19 GB (finished Affinity projects).

## 5. Per-year breakdown, Astro-Pics (all files, LastWriteTime-based)

| Year | Files | Size |
|---|---|---|
| 2021 | 23,306 | 446.7 GB |
| 2022 | 9,860 | 221.9 GB |
| 2023 | 5,292 | 120.5 GB |
| 2024 | 6,969 | 163.6 GB |
| 2025 | 5,197 | 152.3 GB |
| 2026 | 9,790 | 222.6 GB |

Plus non-year buckets: `Capture` 106/2.3 GB, `Focusing` 588/2.4 GB, `Unsorted` 2,242/42.5 GB,
`Vela SNR Moasic Project` 4,316/95.6 GB. BobbyBox-Temp by year: 2024 10,433/267.9 GB, 2025
12,133/355.3 GB (dominant, and additional to — with partial overlap with — Astro-Pics 2024/2025).

## 6. Masters & calibration presence

Finished masters/processed outputs found throughout: `Unsorted\` has several finished
masters/stretches (`masterLight_*_DBE.tif`, `*_GraXpert_stretched*.{tiff,jpg,png,afphoto}`); every
2022+ session carries per-session `PROC`/`Proc` dirs; PixInsight XISF calibrated/debayered/
registered/master intermediates exist for the 2026 Statue-of-Liberty session, the Vela mosaic, and
pervasively in BobbyBox-Temp. Calibration frames (Bias/Dark/DarkFlat/Flat) are present and
consistently organized for essentially every 2022+ session; 2021 calibration is looser (shared
top-level `2021\Bias`, `2021\Darks`).

## 7. Estimated usable lights for training

Folder-heuristic count for explicit "Light"-named/rawframes dirs, 2024+, excluding AutoSave
stacks/PROC/XISF: roughly **13,000–15,000** single-sub lights across Astro-Pics 2024–2026 + Vela
Mosaic, plus **~9,000–10,000** in BobbyBox-Temp's unique (non-duplicate) 2025-08→11 sessions —
**~20,000–24,000 candidate raw lights** in the recent/good-gear band before any quality filtering.
Re-derive the true count in P0 from `IMAGETYP='Light'` + `EXPTIME` in the sensible sub range
(~10–300 s); SharpCap AutoSave stacks (`EXPTIME` up to 8280 s) and calibration frames sit in the
same numeric extension bucket as raw lights and must not be miscounted as such.

## 8. Data-quality caveats / hazards for the dataset builder

1. **Astro-Pics ↔ BobbyBox-Temp overlap** for 2024–mid-2025 sessions (identical folder names,
   timestamps, frame counts) — needs a content-hash dedup pass before training (not performed here,
   read-only survey).
2. **Duplication within Astro-Pics itself** (see §2 example) — same hash-based dedup catches this.
3. **SharpCap `AutoSave\Stack_*` and all `PROC`/`Proc*`/`reproc`/`PIX`/`pixinsight` content are
   calibrated or pre-stacked, not raw subs** — exclude via `StackingPipeline`'s existing provenance
   skip (`IntegrationFitsWriter.IsTianWenProduct`, `StackedFrameCount > 0`), not folder names alone
   (BobbyBox-Temp especially: XISF intermediates sit right next to raw subs in the same folders).
4. **Mixed Bayer/mono conventions**: RGGB (ASI533/585/294MC) vs **GRBG (SVBONY SV605CC)** vs
   mono+FILTER (ASI294MM, QHY178m, ASI1600MM) — the debayer step must be per-camera, not assumed.
5. **Mixed frame sizes** (4144×2822, 3056×2048, 3856×2180, 3008×3008) — irrelevant once tiled, but
   matters for any full-frame step before tiling.
6. **39+4 `.7z` archives (~100 GB) hold additional raw subs invisible to a folder/file scan** —
   mostly 2021 rawframe archives plus a couple of 2022/2025 planetary sets, not extracted here. None
   of the currently-known ones are in scope for v1 (pre-2023 / planetary, both excluded already),
   but a directory-walking builder will silently skip any *new* `.7z` under a future 2024+ session
   unless it's extracted first or read via an in-process 7z reader.
7. `2026-02-20 BAD LIGHT EXAMPLES` is the only explicitly hand-labeled negative set found; there's
   no equivalent hand-labeled "GOOD" folder — good frames are implicitly "everything else."
8. Older (2021) data includes DSLR/phone/JPEG-only captures not representative of the current
   cooled-camera pipeline — already excluded from v1 scope.

## 9. Local NPU/GPU validation (aside, feeds §3 "Infra" of the main plan)

`sebgod/testwinai` (sibling repo, `source/repos/sebgod/testwinai`) confirms the Snapdragon Hexagon
NPU on this box works for **inference only** — proven with a 7B LLM via ONNX Runtime GenAI + QNN EP
(~13 tok/s) — and its own `LIMITATIONS.md` states the HTP is a "fixed-function tensor accelerator"
with **no QNN-compiled vision models available** and no training path. The realistic "validate
locally before paying for cloud GPU" lane on this box is what TianWen's own AI stack already proves
out: ONNX inference via **DirectML → native Adreno GPU** (RC-Astro/SAS enhancers) — good for
sanity-checking a trained model's inference and small-scale smoke runs, not for the actual training
job (which the main plan already scopes to rented cloud GPU — RunPod RTX 4090).
