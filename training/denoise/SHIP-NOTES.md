# ship/ -- taking n2n_v19d out of the experiment and into TianWen

2026-08-17, task #43 / plan row N4. Everything here is about deploying ONE checkpoint. The
research question that produced it is closed (see `../v24/README.md`): three disjoint
8-session draws gave 0.825 / 0.726 / 0.739 on the same observer, so there is no recipe, and
v19d ships as **the best checkpoint measured, not as the output of a method.**

## The checkpoint: `n2n_v19d_s2.pt` (gate-selected, step 1500)

**Changed 2026-09-04.** `n2n_v19d_s2_final.pt` (step 4000) shipped from 2026-08-17 to 2026-09-04. The
seed was picked on the four-observer frontier table below, which is sound, but that table compared the
three seeds' FINAL checkpoints and the gate-selected one was never entered into it -- the audit the
trainer's own comment asks for ("if the last step also passes, selection helped has to be demonstrated
against it, not assumed") was never run in that direction. Run 2026-09-04 at matched noise, the
gate-selected checkpoint wins on every measurement taken: Gaia-confirmed faint-star amplitude spent
10.3 against 15.1 and unmatched-detail 7.2 against 9.2 (at 10 percent noise removed, two clean evals),
this table's own criterion 0.79 against 0.68 at SNR 8-15, and fabricated point sources 19.0 per tile
against 34.5 on a raw-sub floor of 13.2. Two columns favour the old one, residual correlation and
fine-structure ratio; they did not outweigh four that do not.

The section below is the ORIGINAL 2026-08-17 record of the SEED choice, unchanged.

## The seed: three of three, on the four-observer frontier

Seed 2 of three, picked on the four-observer frontier table rather than the gate (which is
single-observer). Over the three UNCONTAMINATED observers, faint amplitude kept at matched
noise 0.90:

| seed | Rim | Horsehead | ASI585 | mean | fine structure mean |
|---|---|---|---|---|---|
| s0 | 0.820 | 0.911 | 0.875 | 0.869 | 1.006 |
| s1 | 0.803 | 0.891 | 0.838 | 0.844 | 0.983 |
| **s2** | **0.852** | 0.901 | **0.895** | **0.883** | 0.997 |

s2 leads on amplitude and is a near-tie on structure. State the caveat with the number: the
seed spread is 0.039, so this is the best MEASURED seed, and the same re-measure-don't-assume
rule that applies to the checkpoint applies to the seed.

(Skull and Crossbones is excluded from the pick: armD trained on a same-folder, same-night
mosaic sibling of it. It is still reported everywhere, just never selected on.)

## The export: `n2n_export.py`

Exports two graphs and measures BOTH against torch on real master tiles from the eval cache.
Both reproduce torch to **max |diff| 1.49e-7, five parts per million of the tile noise** --
float32 round-off, nothing more.

- **`n2n_v19d_s2_final.onnx` -- the shipping graph.** Inputs `image` [N,3,256,256] and a scalar
  `strength`; output [N,3,256,256]. The conditioning plane is computed IN THE GRAPH.
- `n2n_v19d_s2_final_plain.onnx` -- the bare net, [N,4,H,W], sigma supplied by the caller.
  Kept as the research escape hatch and as a cross-check on the baked one.

Three things this settled that were open going in:

**`aten::quantile` has no opset-17 lowering**, so the estimator could not be exported as
written. It is a sort plus a lerp, and the rewrite was verified bit-identical (max abs diff
0.0, on real tiles) BEFORE being adopted. A "should be equivalent" reimplementation of the
conditioning is exactly the kind of thing that shifts sigma a few percent and is invisible in
the output.

**Bake the conditioning in, do not let the host compute it.** The plane is computed from the
tile at train time, so at inference it must be computed PER CHUNK. With a caller-supplied
sigma the natural mistake is to compute it once for the whole image, which still runs, still
looks like a denoiser, and feeds the model a number it never saw. In the graph, the host
cannot get it wrong because the host does not do it.

**The baked graph is spatially FIXED at 256, and that is a correctness constraint.** The
estimator reads the darkest half of the tile it is given, so its support region IS the tile:
over 512 px it is a different statistic from the one the model was trained against. A dynamic
spatial axis would let a caller change the conditioning silently just by chunking differently.

`n2n_v19d_s2_final_export.json` carries the parity numbers plus a per-tile sigma fixture, so
a C# test can pin the estimator without a torch dependency.

## The dial: `n2n_dial.py` -- ship the BLEND, not the conditioning lie

Two knobs existed and the task left the choice open. Measured over all four observers,
comparing at matched residual noise:

| Observer | strength noise span | blend noise span | fabrications @s=0.15 | @s=1.0 |
|---|---|---|---|---|
| Rim | 0.745-0.935 (0.190) | 0.795-0.974 (0.179) | 4.50 | 0.71 |
| Horsehead | 0.815-0.887 (0.072) | 0.858-0.979 (0.121) | 3.81 | 2.60 |
| Skull | 0.607-0.880 (0.273) | 0.832-0.979 (0.148) | 2.75 | 1.44 |
| ASI585 | 0.498-0.602 (0.103) | 0.533-0.945 (0.412) | 3.96 | 1.46 |

**`strength` loses on three independent grounds, any one of them sufficient:**

1. **It cannot reach gentle.** At strength 0.15 -- a 6.7x understatement of sigma -- three of
   four observers still sit below the noise the blend reaches at a=0.1. Scaling the
   conditioning down does not scale the residual correction to zero, so the dial saturates
   long before "barely touch it".
2. **Its span is observer-dependent by 4x** (0.072 on Horsehead against 0.273 on Skull), so
   one knob position means different things on different data. That alone rules it out as a
   user-facing control.
3. **Fabrication RISES toward the gentle end, by 2.6x to 6.3x** (Rim 0.71 -> 4.50 per tile
   above the input's own bar). Told its input is clean, the model reads noise as signal and
   sharpens it into point sources. A control labelled "less" that invents more is not
   shippable, and no amount of documentation fixes that.

Where both are measurable at matched noise, strength is behind anyway: -0.066 and -0.017 on
Rim, +0.008 on Skull. So it buys nothing it would need to justify the above.

The blend is a convex combination of two images that already exist, so it is exactly monotone,
spans the full range to "untouched input" BY CONSTRUCTION, and cannot invent. `strength` stays
as a graph input pinned to 1.0 -- free to keep, useful for research, not exposed in the app.

## The level prior: the model would have shipped a colour cast

Found while building the parity fixture, because a synthetic plate at background 0.15 came back at
0.185 and a denoiser has no business moving a mean by 20%. It is not the plate. Over 49 held-out
tiles:

- **corr(median shift, input level) = -0.988**, corr with the tile's noise sigma only **-0.278**.
  The net drags an input toward the sky level of its eight training sessions, in proportion to how
  far below it the input sits. It is a LEVEL prior, not a noise behaviour.
- **It is per channel, so it is a cast and not an offset.** Median shift over the tiles: R +0.006,
  G +0.000, B +0.011 on average, and the worst single tile moved R +0.017, G +0.002, **B +0.048**.
  Any master whose sky sits below the training set's would come out blue.
- **The fix costs nothing that was measured.** Adding back the per-channel constant that restores
  the source median drives the channel spread to exactly 0.000000, and a per-channel constant moves
  the per-channel std by at most 3.7e-9 and the background sigma by 1.7e-7. Every frontier number
  this checkpoint was chosen on is DC-invariant, so they all stand.
- **Applied per CHUNK, not per image**, because the prior acts on each tile's own local level. One
  global offset would correct the average and leave the between-tile variation as a low-frequency
  stain; correcting locally removes it where it is made, and the chunk overlap is averaged by the
  stitch so neighbouring corrections blend instead of stepping at a seam.

Worth stating as a property of the model rather than only as a fix: **eight sessions is a narrow
range of sky levels, and the net learned one.** If the training set is ever widened this is a thing
to re-measure -- it should weaken.

## Wiring notes for the C# side

> **Correction, 2026-09-02.** The first bullet below was wrong, and it shipped. This net did NOT
> train on linear tiles: `DatasetTileExporter` stores every tile AFTER `ChunkedNafnetRunner.ApplyInputStretch`
> (per-channel medians 0.249 to 0.250 on the `n2n-eval4` cache; a sub's darkest-half sigma 0.0082,
> a master's 0.00126), and `n2n_smoke.py` reads the bytes as stored. The C# runner fed a linear
> master verbatim, about 100x below that band. Measured on the 163-sub Bubble master in one process:
> verbatim removed 10/9/17 percent of the noise (R/G/B) and cut EVERY star's peak by ~30 percent
> at every SNR (amp kept 0.70 flat); through the exporter's stretch the same weights remove
> 13/23/36 percent, keep 0.73 of a faint star rising to 0.93 at the bright end, and the per-chunk
> level drag falls from 0.074 on a 0.0019 sky to 0.0029 on a 0.25 level. `N2nLinearRunner` now
> applies `ApplyInputStretch` to the whole frame, runs, and inverts with `MtfUnstretch`, exactly as
> `ChunkedNafnetRunner` does. The two remaining mismatches (per-tile conditioning, the /4 pad) still
> keep it a separate runner. Write-up: `docs/plans/denoiser-training.md`, fact 0 and H0.

**`ChunkedNafnetRunner` cannot host this model**, which is a design constraint rather than a
parameter change. Three independent mismatches:

- ~~it applies `ApplyInputStretch` (MTF) before inference; this net trained on LINEAR [0,1]
  tiles, so that is straight train/inference skew~~ (wrong, see the correction above: the tiles
  ARE stretched, and the runner now applies the same call),
- its `extraInputs` are documented as reused across every chunk; our conditioning is per-tile,
- it pads chunks to NAFNet's /16; this UNet has two pools and needs /4.

So the enhancer needs a sibling runner. What it must get right:

- feed linear `[0,1]` via `Image.UnitScaleDivisor`, the scale the exporter normalised to before
  stretching, then `ApplyInputStretch` on the whole frame and `MtfUnstretch` on the answer,
- chunk to exactly 256x256, one sigma per chunk (automatic: the graph does it),
- honour the 16 px `StitchBorderPx` rim -- training masked it out of the loss, so no output
  pixel may come from a chunk edge (`BORDER = 16` in `n2n_smoke.py`, matching
  `AiNafnetInputs.StitchBorderPx`),
- blend `master + a * (den - master)` as the user-facing strength, a in (0, 1].

## What shipped, and what is left

Shipped in the repo (`src/TianWen.AI.Imaging/Onnx/`): `N2nDenoiser` (the `IDenoiseEnhancer`),
`N2nLinearRunner` (the linear-domain chunked runner), `OnnxIoNames.ImageInputTileSize`, and the
opt-in `AddTianWenN2nDenoiser`. Pinned by `TianWen.Lib.Tests/N2nDenoiserTests.cs` -- 11 tests,
including the cross-language parity one, which was seen to fail by 80x its tolerance with the level
restore removed.

**Distribution (decided + shipped 2026-08-17): in-repo under Git LFS.** The weights live at
`src/TianWen.AI.Imaging/models/tianwen_denoise_osc_v19d.onnx` behind a `*.onnx` LFS rule. The test
project copies them beside its binaries so the parity test runs off the checkout's own bytes, CI's
test-unit job narrow-pulls `*.onnx` so that test runs on every push instead of skipping, and
`tools/tianwen-ai-models-fetch.ps1` grew a phase 4 that hardlinks the checkout copy into
`%LOCALAPPDATA%\TianWen\models` (falling back to the `media.githubusercontent.com` LFS object URL
for a pointer-stub checkout). `ModelResolver` refuses a pointer stub outright, so a clone without
git-lfs degrades to a logged skip rather than an ONNX Runtime protobuf error. Cost accepted: ~3 MiB
of LFS per shipped retrain, fine at this size, wrong for anything AI4-sized.

## Files

- `n2n_export.py` -- export + torch parity + sigma fixture
- `n2n_dial.py` -- the blend-vs-strength measurement, `dial-results.json`
- `n2n_fixture.py` -- the cross-language parity fixture, `n2n-parity-fixture.json`
  (copied into `src/TianWen.Lib.Tests/Data/`)
- `n2n_v19d_s2_final.onnx` (3.12 MiB, shipping), `..._plain.onnx`, `..._export.json`
