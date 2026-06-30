# Deep-Sky Stacking, Enhance & Display-Render Pipeline

Architecture reference for the deep-sky integration pipeline (`tianwen stack`) and,
specifically, the **unified display-render** layer that turns a linear integrated
master into the colour-calibrated PNG quick-look and the `--split-plates` edit
TIFFs. Sibling of, but completely separate from, the planetary lucky-imaging
stacker (`docs/plans/planetary-stacking.md`).

**Scope of this doc:** the flow from `StackingPipeline.RunAsync` down to the
per-pixel stretch, with the colour / white-balance / pedestal decisions that keep
the PNG and the split plates colour-matched and neutral. The buffer-lifecycle /
live-capture path is a different doc (`image-pipeline.md`).

## Code map

| Concern | Type | Project |
|---------|------|---------|
| Orchestrator (scan -> cal -> register -> integrate) | `StackingPipeline` | `TianWen.Lib/Imaging/Stacking/` |
| Post-integration disk side-effects + enhance + render | `MasterPostProcessor` | `TianWen.Lib/Imaging/Stacking/` |
| SPCC + sky-bg WB, bg-neut, MTF stretch, PNG/TIFF render | `MasterPreviewRenderer` | `TianWen.Lib/Imaging/Stacking/` |
| Pure stretch-uniform math (CPU/GPU single source) | `StretchSolver` | `TianWen.Lib/Imaging/` |
| AI enhance step program (BlurX-first / SAS-shaped) | `SharpenPipeline` | `TianWen.Lib/Imaging/Enhancement/` |
| Per-pixel CPU stretch | `Image.RenderStretchedRgba16` / `StretchChannelCpu` | `TianWen.Lib/Imaging/` |
| CLI verb | `StackSubCommand` | `TianWen.Cli/` |

`MasterPreviewRenderer` + `StretchSolver` are **CPU-only** (no GPU, no UI), so they
live in `TianWen.Lib` and `MasterPostProcessor` drives them in-pipeline. The
viewer's `AstroImageDocument.ComputeStretchUniforms` / `ComputeSkyBackgroundWB`
forward to `StretchSolver`, so the stretch math has one source the GLSL + CPU
paths agree on.

---

## 1. Top-level pipeline: `StackingPipeline.RunAsync`

```mermaid
flowchart TD
    Start([RunAsync]) --> Scan[Scan DataRoot for FITS]
    Scan --> Prov{TianWen product?<br/>STACK_N gt 0 OR<br/>TianWen SWCREATE}
    Prov -->|yes, not --include-integrations| Drop[Drop from scan<br/>report in ScanSummary]
    Prov -->|no| Keep[Keep as input sub]
    Keep --> Cal[Build bias / dark / flat masters<br/>per cal group]
    Cal --> Group[Group lights by<br/>target / exp / gain / temp / pattern]
    Group --> Reg[Register each light<br/>star-quad match vs reference]
    Reg --> Strat{Strategy auto-pick}
    Strat -->|RGGB and frames gte MinFrameCount| Driz[Bayer drizzle]
    Strat -->|else| Ahd[AHD demosaic + sigma-clip reject]
    Driz --> Post[MasterPostProcessor.WriteMasterAsync]
    Ahd --> Post
    Post --> Yield[yield GroupResult<br/>master path, SPCC, elapsed]
    Yield -->|next group| Group
```

**Provenance skip (never re-ingest our own outputs).** The scan drops any
TianWen-produced FITS so a processed image parked alongside the lights is never
re-stacked. Two markers (`IntegrationFitsWriter`): `STACK_N > 0` (a master) OR a
TianWen `SWCREATE` prefix (`IsTianWenProduct` -- catches sharpen / enhance outputs
which inherit the master's `SWCREATE` but carry no `STACK_N` and an
`IMAGETYP=Light`). The `ScanSummary` is reported on the progress channel so a
silent skip is visible.

---

## 2. Post-processing: `MasterPostProcessor.WriteMasterAsync`

```mermaid
flowchart TD
    In([WriteMasterAsync]) --> Fix[Fix MaxValue tag to 1.0<br/>no pixel rescale]
    Fix --> Crop[Pre-compute autocrop<br/>footprint-intersection AABB]
    Crop --> Solve[Plate-solve<br/>prefer autocrop input, NaN-ring-free]
    Solve --> FL[Backfill focal length<br/>from solved pixel scale]
    FL --> WriteFull[Write master_slug.fits<br/>WCS embedded]
    WriteFull --> Enh{--enhance?}

    Enh -->|yes| EnhPath[EnhanceAndWriteAsync<br/>see section 3]
    Enh -->|no| CropFits[Write master_slug_autocrop.fits]

    EnhPath --> CropFits
    CropFits --> Prev{--enhance?}
    Prev -->|no| RawPng[RenderPreviewAsync<br/>RAW master -> _autocrop.png]
    Prev -->|yes| Done([return MasterWriteResult<br/>Result, SolvedWcs, Spcc])
    RawPng --> Done
```

**Output contract by data type (do not regress):**

| Tier | Files | Cropped? | Notes |
|------|-------|:--------:|-------|
| Linear (canonical) | `master_<slug>.fits` + `master_<slug>_autocrop.fits` | both | only place uncropped raster exists; `--output-format exr` mirrors both |
| Enhanced linear | `master_<slug>_sharpened.fits` + `_sharpened_autocrop.fits` | both | `--enhance`; raw masters never overwritten |
| Display PNG | `master_<slug>_autocrop.png` | **always** | bare `_<slug>.png` only when coverage is full (no autocrop) |
| Split plates | `master_<slug>_stars.tif` + `_starless.tif` | always | `--split-plates`; sRGB-ICC float, Screen-blend stars over starless |

The PNG is a display artifact, so the pipeline (NOT the CLI) renders **only the
autocrop** -- the autocrop region is NaN-ring-free, so WB / bg-neut can never be
poisoned by partial-coverage edges. The CLI renders nothing; it sets
`StackingOptions.RenderPreviewPng`, writes EXR from the emitted FITS, and prints
the SPCC summary from `GroupResult.Spcc`.

---

## 3. Enhance + render: `EnhanceAndWriteAsync`

This is where the **PixInsight OSC order** is enforced: gradient correction, then
**one** SPCC white balance with the stars in, then star removal, then a **per-plate
stretch**.

```mermaid
flowchart TD
    In([EnhanceAndWriteAsync]) --> GC[GC.Collect compacting<br/>reclaim integration heap<br/>avoid GPU TDR on iGPU]
    GC --> Steps{SharpenPipeline.SupportsDeblur?}

    Steps -->|RC-Astro present| Blur["BlurX-first program:<br/>Deblur (whole frame, auto-PSF)<br/>-> GradientCorrection<br/>-> RemoveStars (the split)<br/>-> DenoiseStarless<br/>-> ScnrStars<br/>-> Recombine"]
    Steps -->|no RC deblurrer| Sas["SAS-shaped program:<br/>GradientCorrection<br/>-> RemoveStars (the split)<br/>-> SharpenStars<br/>-> DeconvolveStarless<br/>-> DenoiseStarless<br/>-> Recombine"]

    Blur --> Final[Final = recombined enhanced master<br/>+ kept stars / starless lineage]
    Sas --> Final
    Final --> WriteSharp[Write _sharpened.fits<br/>+ _sharpened_autocrop.fits]
    WriteSharp --> OneSolve["ONE RenderAsync on the ENHANCED master:<br/>solves SPCC WB (stars in, gradient-corrected)<br/>+ renders the preview PNG"]
    OneSolve --> Plates{--split-plates?}
    Plates -->|yes| Tiff["Per-plate TIFF:<br/>stars  = self-stretch + shared WB<br/>starless = self-stretch + shared WB"]
    Plates -->|no| Ret([return SpccDiagnostics])
    Tiff --> Ret
```

`--split-plates` is a **single AI pass**: `KeepIntermediates =
StarsAndStarlessLineage` keeps the stars-only + denoised-starless plates from the
SAME `ProcessAsync`. No second enhance runs.

---

## 4. The render model: WB once, per-plate self-stretch

This is the load-bearing colour decision. It mirrors the PixInsight OSC workflow:

```
gradient correction  ->  SPCC / WB  ONCE (stars in)  ->  star removal  ->  per-plate STRETCH
```

```mermaid
flowchart LR
    Enh[Enhanced master<br/>gradient-corrected, stars in] --> SPCC[SPCC solve ONCE<br/>-> shared WB triple R,G,B]
    SPCC --> P[Preview PNG]
    SPCC --> S[Stars plate]
    SPCC --> SL[Starless plate]
    P -. self-stretch:<br/>own bg-neut + own MTF .-> Pout[(neutral preview)]
    S -. self-stretch:<br/>own bg-neut + own MTF .-> Sout[(stars, calibrated colour)]
    SL -. self-stretch:<br/>own bg-neut + own MTF .-> SLout[(neutral starless)]
```

**Only the white balance is shared.** Each output (preview, stars, starless)
computes its **own** background-neutralisation + shadow/MTF from its own pixels.
Sharing the master's *full* stretch uniforms instead would graft the master's
bg-neut onto a plate whose background differs -> double-correction -> a colour cast
(this was the original `--split-plates` regression). Sharing only the WB keeps the
star colours on the SPCC calibration while every plate's background lands neutral.

| Quantity | Source | Shared? |
|----------|--------|:-------:|
| White balance (SPCC) | enhanced master, stars in | **yes** (one solve) |
| Background neutralisation | each plate's own pixels | no (per-plate) |
| Shadow / midtones / rescale (MTF) | each plate's own pixels | no (per-plate) |

---

## 5. The unified solve: `ComputeStretchUniformsAsync`

Single source of the bg-neut + stretch math, used by both the PNG render
(`RenderAsync`) and the split-plate TIFF (`RenderStretchedPlateTiffAsync`).

```mermaid
flowchart TD
    In([ComputeStretchUniformsAsync<br/>renderImage, statsImage, wbOverride]) --> Zero[WithZeroPedestal statsImage<br/>MinValue -> 0, share arrays]
    Zero --> Bg[ScanBackgroundRegion<br/>per-channel background median]
    Bg --> Wb{wbOverride supplied?}
    Wb -->|yes shared WB| UseShared[use shared SPCC triple<br/>skip solve]
    Wb -->|no| SolveWb[FindStars -> SPCC<br/>-> sky-bg fallback -> identity]
    UseShared --> Bn[MinPivot bg-neut, post-WB:<br/>bn_X = K/wb_X - 1 / bg_X - 1]
    SolveWb --> Bn
    Bn --> Uni[Per-channel stretch uniforms<br/>StretchSolver.ComputeStretchUniforms<br/>+ BackgroundNeutralization]
    Uni --> Out([Uniforms, Spcc, Wb])
```

### Why `WithZeroPedestal` (the parity-restoring fix)

The stretch derives per-channel shadows from the **pedestal-subtracted** median
(`GetPedestralMedianAndMADScaledToUnit` subtracts `MinValue/MaxValue`). Raw stacked
masters happen to have `MinValue ~ 0`, so the subtraction is a no-op -- which is the
*only* reason the historical raw-master render path was colour-neutral.

An **enhanced** master is different: GraXpert background-extraction flattens the
floor to roughly half-scale (`MinValue ~ 0.16-0.41`). Subtracting that floor leaves
the faint per-channel medians as tiny near-zero residues, where small absolute
differences explode:

- 120s: `R - ped = 0.012` vs `G - ped = 0.002` -> 6x -> **green crushed** (magenta cast)
- drizzle: `median 0.012 - pedestal 0.164` -> **negative** -> the whole frame renders **black**

The auto-stretch's own shadow clipping (`median - k * MAD`) already finds the black
point, so the floor is just a uniform DC offset best left in place. `WithZeroPedestal`
rewraps the stats image with `MinValue = 0` (a cheap by-reference array share, no
pixel copy), so the enhanced master behaves exactly like the proven raw path. The
render images are stretched with the resulting `Pedestal = 0` uniform, so render +
stats stay in one coordinate space.

---

## 6. Per-pixel CPU stretch order (`StretchChannelCpu`)

The CPU loop mirrors the GLSL shader (`feedback_cpu_gpu_stretch_mirror`). For each
channel, with `Pedestal = 0` from the zero-pedestal stats:

```mermaid
flowchart LR
    raw[raw pixel] --> n1["norm = raw * NormFactor - Pedestal"]
    n1 --> n2["bg-neut: norm = norm*bn + (1-bn)"]
    n2 --> n3["WB: norm = max(norm * wb, 0)"]
    n3 --> n4["shadow+rescale: (norm - sh) * re"]
    n4 --> n5["MTF(midtones, .)"]
    n5 --> n6["* NormalizeScale"]
    n6 --> n7["gamut clamp: /max if max gt 1"]
    n7 --> out[16-bit channel]
```

Order: normalize -> **bg-neut -> WB** -> shadow/rescale -> MTF -> normalize-scale ->
gamut clamp. bg-neut runs *before* WB; the MinPivot bn gains are solved in post-WB
space so the post-shader background is neutral across channels by construction.

---

## 7. Parity verification (`temp/stack/output` reference)

Measured per-channel percentiles, background ratio at the 5th percentile of
luminance (`1.000` = neutral):

| Output | New (`output_split`) | Old reference (`output`) |
|--------|----------------------|--------------------------|
| 120s preview PNG | R/G=1.00, B/G=1.00 @ median 0.098 | R/G=1.00, B/G=1.00 @ median 0.098 |
| 120s starless plate bg | R/G=1.025, B/G=0.999 | R/G=1.20, B/G=0.94 (old `DualStretchPlates` cast) |
| 120s stars plate bg | R/G=1.00, B/G=1.00 | n/a (old path) |

The PNG is at parity; the split plates are *more* neutral than the old
`DualStretchPlates` output (which carried the R/G=1.20 background cast the unified
render was built to remove).

---

## 8. Dedup notes

The unification collapsed several near-duplicate code paths into single sources:

- `DualStretchPlates` (deleted) -- the self-contained plate stretch that lacked the
  PNG's WB + bg-neut. Replaced by the shared `MasterPreviewRenderer` path.
- The CLI-side PNG render + autocrop-fallback logic (deleted) -- moved into
  `MasterPostProcessor` so PNG + plates share one solve.
- `ComputeStretchUniformsAsync` is the single producer of bg-neut + stretch
  uniforms for both the PNG and the plates.
- `StretchSolver` is the single producer of the stretch-uniform math the viewer
  (`AstroImageDocument`), the CPU stretch, and the GLSL shader all agree on.
