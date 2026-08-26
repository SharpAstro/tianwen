---
name: stack
description: Run `tianwen stack` against a folder of FITS lights + calibration. Use when the user asks to stack frames, build a master, integrate a session, or wants a tianwen stack run kicked off (e.g. "stack C:/temp/stack", "integrate the SoL frames", "build a master from the latest session").
---

Usage: `/stack <data-root> [options]`

Examples:

```
/stack C:/temp/stack
/stack C:/session1 -o C:/masters --strategy TilePipelined
/stack C:/skull --group-filter skull --output-format none
/stack C:/temp/stack --no-plate-solve --output-format none   # fastest -- FITS only
```

The skill runs from `src/`:

```
cd src && dotnet run --project TianWen.Cli -c Release -- stack <args>
```

Use `-c Release` (Release build) -- the integration loop is CPU-bound and
the Debug build is ~3x slower per frame.

## What gets written

Under `<data-root>/output/` (or `--output` if set):

- `masters/master_<group>.fits`: cached bias/dark/flat masters (reused across runs)
- `master_<group>.fits`: integrated light master with WCS embedded
- `master_<group>_autocrop.fits`: same, cropped to the no-NaN intersection
- `master_<group>.png`: display-encoded preview with SPCC + bg-neut (unless `--output-format none`)
- `master_<group>.manifest.json`: the stack manifest -- the frame list with each frame's fate, the
  reference frame, and each matched frame's STAR transform, keyed by a digest of the FITS data
  section. Feed it to a later run with `--manifest` so both layers are built from identical inputs.
- `master_<group>.rejection.fits`: per-pixel rejection count map (when rejections > 0)

**Drizzle exception**: when `--strategy BayerDrizzle` is in use, the master + sidecars carry a `_drizzle` infix so a side-by-side run against the default strategy can coexist in the same output dir:

- `master_<group>_drizzle.fits`
- `master_<group>_drizzle_autocrop.fits`
- `master_<group>_drizzle.png`
- `master_<group>_drizzle.rejection.fits`  (here this is the per-channel **coverage map**, not a rejection-fraction map)

**Comet runs** (`--comet`) write three masters from the one run, all on the same reference frame:

- `master_<group>.fits`: the COMET layer, registered on the body (stars trail). With `--remove-stars`
  it is built from per-frame star-removed plates and holds the comet alone.
- `master_<group>_stars.fits`: the STAR layer, registered on the stars with the body SUBTRACTED from
  every frame (or EXCLUDED, when no star remover is registered; `--no-star-layer` skips it).
- `master_<group>_composite.fits`: the finished image, the star layer with the body added back once at
  its ephemeris position for the reference epoch. Plate-solved and SPCC'd like any master, since it has
  both stars and comet. Written only when the body was subtracted (`--no-composite` skips it).
- `starless/<group>/*_starless.fits`: the per-frame star-removed plates, a CACHE beside `masters/`.
  A re-run into the same `-o` reuses a plate whose `SRCDGST` + `STARMODE` match instead of paying
  ~7 s of `sxt` per frame again, which turns a 13-minute iteration into a 3-minute one.

Every master also carries `SWCREATE = TianWen.Imaging.Stacking.Integrator` + `STRATEGY = <kind>` in its FITS header so provenance stays queryable even if the file gets renamed.

## Knobs worth knowing

| Flag | When to use |
|---|---|
| `--strategy <kind>` | Force a specific integrator. Default lets the selector pick. `TilePipelined` for tight RAM, `InRamAllFrames` for max fidelity when N x canvas fits, `ChunkedTwoPass` for huge N. `BayerDrizzle` for RGGB inputs where you want zero-interpolation colour reconstruction (skips AHD entirely, fills "missing" R/G/B at each pixel from real Bayer samples in other dithered frames; needs >= 60 matched frames by default). |
| `--drizzle-pixfrac <float>` | BayerDrizzle: linear drop size in `(0, 1]`. Default `1.0` (full unit-square drop -- forward-bilinear coverage). Lower = sharper output, needs more frames to fill cells. Ignored unless `--strategy BayerDrizzle`. |
| `--drizzle-min-frames <int>` | BayerDrizzle: matched-frame floor before the strategy runs. Default `60`. Drop only if you accept NaN holes in R/B channels (each Bayer colour is only ~25% of input pixels). Ignored unless `--strategy BayerDrizzle`. |
| `--group-filter <pat>` | Substring on the group slug; only matching groups run. Useful when one session has multiple targets and you only want one. |
| `--group-exclude <pat>` | Inverse of `--group-filter`. |
| `--output-format <fmt>` | Companion written beside the FITS: `png` (default), `uhdr` (Ultra HDR gain-map JPEG), `exr` (float-true linear HDR), or `none` to skip it. **There is no `--no-png`** -- use `--output-format none`. |
| `--comet [designation]` | Register on the BODY, so the comet integrates sharp and the stars trail. Omit the value to read the designation from the frames' own `OBJECT` card. Derives the rate by plate-solving the reference frame and asking JPL Horizons for a TOPOCENTRIC track from the site in `SITELAT`/`SITELONG`/`SITEELEV`. Needs network. |
| `--comet-rate dx,dy` | The offline counterpart, in CANVAS px/hr of the reference frame's grid. Wins over `--comet` when both are given. Carries no position, so it cannot drive the star layer or the composite. |
| `--remove-stars` | Per-frame star removal after registration, before integration: the comet LAYER. Needs a registered `IStarRemover` (RC-Astro CLI or the SAS models). Pair with `--no-bayer-drizzle` so both layers take the same normalising strategy. |
| `--no-star-layer` / `--no-composite` | Skip the companion star layer / the finished composite of a `--comet` run. |
| `--inherit-wb <master.fits>` | Take the white balance from a donor master's `WBRED`/`WBGREEN`/`WBBLUE`. Required for a comet stack: its stars are trailed, so it cannot plate-solve and SPCC has nothing to work with. |
| `--reference-frame <substr>` | Pin the reference to the first candidate whose path contains this substring. Prefer `--manifest`, which pins the reference along with the frame list and the transforms. |
| `--no-plate-solve` | Skip plate-solving. Use for synthetic / non-celestial data, or when the catalog DB initialisation is the bottleneck. |
| `--stack-debayer AHD` (default) | Best colour fidelity, slower per-frame. Swap to `VNG` for ~2-3x speedup at small fidelity cost. Unused under `--strategy BayerDrizzle` (no debayer step happens). |

## Long-running runs

Real-dataset runs can take 5-30 minutes depending on N, canvas size, and
chosen strategy. The CLI streams `[stack] <group>: <status>` lines after each
group completes; the underlying StackingPipeline emits per-stage
`[Information]` chatter via `ILogger`. **Do not background** -- the user wants
to see progress. Stream stdout to the conversation.

## Failure modes

- **Exit code 127 / 13x**: .NET process crashed. Look for a stack trace
  before the crash line; the AOT-published binary would land it in stderr.
- **"Data root does not exist"**: typo or wrong drive letter; the CLI checks
  before doing anything.
- **"fewer than 2 matched frames" for a group**: registration failed for most
  frames. Usually means `--min-stars` is too high for the field's star density
  (try `--min-stars 500` for sparse fields) or the reference frame is bad
  (rare; the pipeline picks the highest-star-count frame as reference).
- **"strategy <X> not implemented" / NotImplementedException**: someone forced
  a strategy that isn't shipped yet (e.g. `LiveAccumulator`). Drop the
  `--strategy` flag to let the selector pick a working one.

## Verifying the output

After the run, check `<output-dir>/master_<group>.png` for a quick visual.
For deeper inspection, open `master_<group>_autocrop.fits` in PixInsight /
ASTAP / FITS Viewer (`/run-fits`).
