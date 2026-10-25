# training/

The Python side of the model-training programme (`docs/plans/model-training-roadmap.md`): the
scripts that prepare caches, train, gate, score, export and fixture the in-house models. Nothing here
is built or tested by `dotnet`. The .NET side consumes only an exported `.onnx` plus a parity fixture,
and the tiles these scripts train on come from the C# exporter (`DatasetTileExporter`), which is why
"trained on the bytes as stored" is the contract on both sides.

## Layout

| path | what |
|---|---|
| `requirements.txt` | the pinned environment; the cu126 index line is load-bearing, see the file |
| `EXPERIMENTS.md` | one line per run or measurement, with its verdict, negatives included |
| `denoise/` | the OSC Noise2Noise denoiser: `n2n_smoke.py` (prepare / train / eval, and the module every scorer imports for cache access), `n2n_gate.py`, `n2n_metrics.py`, the scorers, the ship set (`n2n_export.py`, `n2n_dial.py`, `n2n_fixture.py`, `SHIP-NOTES.md`, the export record and dial results), the arm lists under `arms/`, and `repro-v19d.ps1` |
| `denoise/n2n_paths.py` | the one place machine paths live; `TIANWEN_SCRATCH` and `TIANWEN_DATASETS` override them |
| `denoise/run-e1*.ps1`, `run-d1-*.ps1`, `run-e210*.ps1` | the pre-registered launchers of the deconvolver plan's E1b to E1g-2, D1 and E2.10a to E2.10c (`docs/plans/deconvolver-training.md`): `dotnet test` over an opt-in probe (`DeconvolutionOracleCeilingProbe`, `SeeingSplitPairProbe`, the D1 conditioning probe) or a `tianwen stack` of a seeing split, prediction and kill line in each header |
| `denoise/e1d_effective_width.py`, `moffat_discrete_kernel.py`, `moffat_quadrature.py`, `moffat_small_ratio.py`, `kernel_rows.py` | E1b and E1d's kernel arithmetic: what a point-sampled kernel is worth, how a quadrature of FWHMs reads a Moffat difference, and the estimator's kernel against the drawn one per cache row |
| `denoise/e210_xcorr_raw.py`, `e210_xcorr_warped.py` | E2.10a's phase correlations of the raw and the warped subs, independent of star centroids: how the refiner's halved shifts were found |
| `denoise/build_matrix.py`, `render_matrix_pngs.py` | the whole-master comparison page ("The whole master, looked at") |
| `denoise/run-e7-5.py`, `read-e7-5.py` | E7.5's 98-run crop grid through `n2n_operator_real.py`, and its read |
| `denoise/condprobe.py`, `condpool.py` | where a cache and every master of a bake sit on the conditioning plane (`docs/plans/denoiser-training.md`) |
| `denoise/run-x2r-export.ps1` | arm X2R's `dataset pair` export with the nights swapped |
| `star-removal/` | the star remover's whole-master scorecard (`score_whole.py`, one row per arm) and the checks it parses: `remover_check2.py`, `gaia_remover_check.py`, `profiles.py`, `m31_nucleus.py`; an arm's own scorer `score-arm.ps1` (with `real_star_check.py`, `ring_annulus.py`) and the resumable driver of an arm trained here, `arm-driver.ps1` (a pass over `arm-step.ps1`'s five steps, each also a pilot job; `test-arm-steps.ps1` checks them) and `register-arm-task.ps1`; no machine path in it, they come from a config (`scorecard.example.json`); see "Scoring an arm" below |
| `azure/` | training runs on an Azure spot T4 (`azure/README.md`): `launch-arm.ps1` launches an arm in one command and checks the trainer's own command line, `az-driver.ps1` babysits it and deletes everything it made once the weights are home, `upload-cache.ps1` and `blockupload.py` send a cache up resumably at a capped rate, and `vm/` holds the scripts that run on the VM. The subscription, group and storage account come from a user-only settings file outside this public repository |
| `planetary/r9_refiner.py`, `r9_score.py`, `r9_twin_pairs.py` | R9's learned refiner on the shipped planetary sharpening (`docs/plans/planetary-restoration.md`, "R9 revisited", #1056): train, apply, and refine one master; score a set against its truths through `tianwen planetary fidelity`; gather stacker-made twins into the pair layout. The synthetic pairs come from `tianwen planetary training-pairs` |
| `planetary/r9-rules.md`, `r9_*.sh`, `r9_gains_*.py`, `r9_*plan.json` | R9's run records, machine paths kept as constants: the rules and every dated amendment, set before each arm ran; the twin makers (2022-09-03 Red, the EdgeHD 11 Jupiter at its fitted air) and their drawn plans; the learned gains' steps (the truth's own gains through the sharpening, the ridge, the deploy point's comparators and the real master's G5 and G6) |

The roadmap's `common/`, `deconv/`, `gradient/` and `starless/` do not exist yet. The gate and the
metrics import the trainer module for cache access, so splitting them out is a refactor with its own
acceptance, to be done when a second trainer needs them and not before.

## Provenance

Ported 2026-09-02 from `D:\Astro-Dataset\n2n-smoke\` (E0 of `docs/plans/denoiser-training.md`),
which keeps the per-generation run records (`v2` to `v24` and `ship`) with their logs, checkpoints
and figures. What came across: the newest generation of every script the campaign plan names
(`v24/scripts` for the trainer, gate, metrics and the v22 to v24 scorers; `ship/` for export, dial
and fixture), three scorers that survive only in older generations because a later step needs them
(`n2n_frontier.py` from v19, `n2n_halfscore.py` from v16, `n2n_depth.py` from v9), and the six arm
lists. Left on D: deliberately: the bake audits and figure scripts of v9 and v16, one-off reports on
data that no longer changes.

The deconvolver campaign's probe launchers, kernel arithmetic, E7.5 sweep and conditioning-plane
probes came across on 2026-09-17 from `C:/temp/e2` and two session scratchpads, where the plans had
been citing them. They keep their machine paths as constants (outputs under `C:/temp/e2`, caches under
`C:/temp/tianwen-scratch`), since each is the record of one run; the per-run launch snapshots
(`C:/temp/e2/scripts-*`) were copies of this folder and stay where they are.

The port changed no behaviour: the acceptance test re-ran the shipped recipe from its prepared cache
and both checkpoints came back with every parameter bit-identical to the shipped ones
(`EXPERIMENTS.md`, row E0). **Compare checkpoints with `n2n_ckpt_equal.py`, never by file hash**:
`torch.save` names every archive member after the output file's stem and pickles the trainer's
metadata dict, which grows with its options, so identical weights under another name are a different
file by construction (that false DIFFERENT is how E0 was first read). Three things changed around
the code: every `C:\tianwen-scratch` literal became a cache NAME resolved by `n2n_paths.py`, since the
scratch root moved in August and the defaults still said it; `n2n_smoke.py --cache` is required, and
`--root` is required with `--prepare`, with no default for either, because a default that points at a
sibling dataset is how the v14 scoring once ran cross-bake and reported a plausible table for the
wrong tiles; and the ship scripts import their siblings directly instead of through `../v24/scripts`.
`n2n_fixture.py` now writes the parity fixture straight into `src/TianWen.Lib.Tests/Data/`.

## Running

Install once with `pip install -r requirements.txt`, then run the torch one-liner from that file.
An arch list without `sm_61` means the cu130 wheel got in and training would fall to the CPU.

The prepared caches live under `TIANWEN_SCRATCH` (default `C:\temp\tianwen-scratch`), one directory
per arm; `n2n-d8` is the shipped arm and `n2n-eval4` the four-observer eval cache, the full list is
in `docs/plans/denoiser-training.md`. A cache is `tiles.f16` (a memmap, 4.33 MiB per cell, resident
in RAM while training), `meta.json` (the train and val sessions BY NAME), and the checkpoints the
trainer saves beside them. **`--out` is a file name inside the cache**, so a re-run of a recipe needs
its own name or it overwrites the reference it would be compared against.

The recipe that produced the shipped weights, from `denoise/`:

```pwsh
python n2n_smoke.py --prepare --root D:\Astro-Dataset\2025-2026-darkscaled --cache $env:TIANWEN_SCRATCH\n2n-d8 `
    --train-from-list arms\armD-8x45.txt --val-sessions 2 --cells-per-session 45 `
    --val-cells-per-session 120 --val-from-meta $env:TIANWEN_SCRATCH\n2n-ds\meta.json
python n2n_smoke.py --train --cache $env:TIANWEN_SCRATCH\n2n-d8 --loss l2 --upsample --mix-avg --cond `
    --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps 4000 --gate-every 100 --seed 2 --out n2n_v19d_s2.pt
```

### A machine that can be taken away mid-run (a spot VM)

`--checkpoint-every N` writes everything the loop carries to `<out>.resume.pt` beside `--out` every N steps,
through a rename so an eviction mid-write leaves the previous one whole: the weights, the optimizer's moments, every
rng stream, the plateau's history and counters, the star weight and any `--snapshot-at` weights. `--resume` continues
from it, and refuses one whose recipe or cache differs (`resume_fingerprint`); the step cap may be raised on resume
under `--schedule plateau`, which it only bounds. The file is removed once the run's outputs are written. It continues
the SAME run: on 2026-10-15 a 40-step run of the R2j recipe killed after its step-20 checkpoint and resumed matched an
unbroken one in every one of its 30 tensors, to the bit (on the CPU; a GPU's kernels may not repeat to the bit).

### Training on injected pairs

The C# side exports a degraded cache that `--prepare` reads with **no change**: its manifest is
P0-shaped, so the clean target lands in slot 0 and the draws in the sub slots, exactly where subs
would be. Export it first (from the repo root, Release):

```pwsh
tianwen dataset degrade --bake D:\Astro-Dataset\2025-2026-organized --out $env:TIANWEN_SCRATCH\deg-warped `
    --mode noise --shape warped --warp-sigma 0.5 --draws 8 --cells 300 --measure-shape
python n2n_smoke.py --prepare --root $env:TIANWEN_SCRATCH\deg-warped --cache $env:TIANWEN_SCRATCH\n2n-inj1 ...
```

Two things to know before using one. **A pair of draws is an N2N pair and a draw against slot 0 is a
SUPERVISED pair**, which is the point of the arm: the trainer's existing regimes give the first for
free, and `--synthetic` gives the second (exclusive of the N2N regimes, and refused on a cache of real
subs, where slot 0 is an integration the subs are noisy views OF). And **`--warp-sigma` is a
measurement, not a constant**: run with `--measure-shape` and match the injected band1/band0 to the
bake's own real half-master pairs (0.5 px landed on 0.460 against 0.463 for `2025-2026-organized`).
Injecting at the wrong shape trains for a noise distribution no frame has.

Four rules, each of which cost a wrong conclusion once (the roadmap's section 4 has the list):

- **Pre-register.** Predictions go into the run script's header before the prepare, the way
  `D:\Astro-Dataset\n2n-smoke\v24\scripts\run-v24.ps1` does it; a result that needs a new explanation
  afterwards is a finding to record, not a conclusion to draw.
- **Several seeds per arm, and a second draw of whatever axis the conclusion rides on.** Three
  disjoint eight-session draws scored 0.825 / 0.726 / 0.739 on the same observer.
- **One prepared cache per arm, never edited between runs.** The row in `EXPERIMENTS.md` names the
  cache and the commit the scripts ran from.
- **Launch long jobs detached** (`Start-Process`, as the header of `repro-v19d.ps1` shows) and never
  read a file a running job is appending to; read its status file. `Start-Process pwsh -File` wants a
  Windows path, not a Git Bash one, and a launcher that snapshots the scripts cannot `Copy-Item` the
  snapshot onto itself, so a relaunch runs from a copy of the snapshot directory.
- **The GPU runs one thing; the CPU runs several.** Never share the card between a training run and a
  probe. The Richardson-Lucy oracle and `PsfKernel.Convolve` are single-threaded, so CPU arms run side
  by side on the 16 cores.

The C# parity fixture is what `n2n_fixture.py` writes; regenerate it whenever the checkpoint or the
plate changes, then run `N2nDenoiserTests`.

## Scoring an arm

A star remover is scored on WHOLE real masters, never on tiles (round 2 of `docs/plans/star-remover-training.md`;
round 1's tile score put the models level with the judges while whole masters showed them far behind). One command
makes the starless plates of the three masters and writes one row:

```pwsh
cd training\star-removal
python score_whole.py r2t --config scorecard.local.json --model path\to\arm.onnx [more.onnx ...] [--views 1]
```

`<out root>\<tag>\` then holds `andromeda_starless.fits`, `tarantula_starless.fits`, `sag_starless.fits`, each
check's working folder (`check2-*`, with the background and noise maps `tianwen image sources` wrote), `score.log`
(every command and its output), `score.json` (the parsed numbers) and **`row.txt`**, the one line the plan's tables
quote. A plate already in the folder is reused, so a re-run only re-reads it. A judge's plates (StarNet2, SXT, a
tag made earlier) are scored without a model by linking them in: `--plates andromeda=... tarantula=... sag=...`
(copied into the folder once, never over a plate that is there).

**The paths are the machine's and are not in the repository.** `scorecard.example.json` shows the shape: the
`tianwen` executable that runs `image remove-stars` and `image sources` (`cli`), the folder the tags are made under
(`out_root`) and the three input masters (`masters`: the owner's M31 RGB master, the held-out Tarantula of 2026-10-17
with `PIXORIG = 1`, and the Sagittarius Triplet, mono or RGB). Copy it to `scorecard.local.json` (ignored by git) and
fill it in, or give `--cli`, `--out-root` and `--master NAME=PATH` on the command line, which win over the file.
The Gaia stars for the Tarantula come from Vizier through `denoise/gaia_vizier.py`, cached under `TIANWEN_GAIA_CACHE`
(default `~/.tianwen/gaia-cache`), so the first run needs the network and later ones do not.

What a row says, and the scripts that read it (each also runs alone, `--help` or its docstring first):

| row field | read by | meaning |
|---|---|---|
| `And dark flat / dug` | `remover_check2.py` | Andromeda's bright (SNR 100 to 1000) dark-sky stars: the share whose starless plate is flat to within half a sigma of the INPUT's own background inside 6 px, and the share dug below it |
| `blue 1.5-3 / 3-4.5` | `profiles.py` | blue's residual (starless less local sky) in sigma at those radii, median over the bright isolated stars; negative is a hole |
| `Tar dark flat / left` | `remover_check2.py` | the same on the Tarantula; `left` is light still above the background |
| `core R/G/B` | `profiles.py` | the residual at 0 to 1.5 px, per channel: the light a sharp core leaves |
| `Tar crowded`, `Sag crowded` | `remover_check2.py` | stars with three or more neighbours within 15 px that come out flat (the Triplet is mono, scored as three copies of its plane) |
| `BP17-19`, `purity` | `gaia_remover_check.py` | Gaia stars of that magnitude the starless plate no longer holds, and the share of the removed light that lies away from every Gaia star |
| `nucleus` | `m31_nucleus.py` | the share of M31's nucleus light (0 to 6 px) the remover took as a star, R/G/B; the guard is at most 5 % |

The guards are round 1's: the nucleus at most 5 % in every channel and purity at most 1.0 %. A row is read against
the judges' rows (the ceiling), R2i seed 0's (the small-model baseline) and R2t's (the round 1 base). Two things the
scripts do not hide: **every number is parsed from the checks' own printed output**, so a check that changes its
wording changes `score_whole.py`'s regexes with it (a missing match is a `KeyError`, never a zero); and the NaN
canvas ring of a TianWen master is handled in `remover_check2.py` (its fine-scale filters fill the ring first and
skip every cell it reaches), which the first copy of the Tarantula's check did not do and which moves only its
cell-level lines, none of them in the row. Run any torch the scorer calls for on the CPU with
`CUDA_VISIBLE_DEVICES=-1` while a training arm owns the card; the scorer itself needs no GPU.

### Scoring an arm's weights, and driving a local arm

`star-removal/score-arm.ps1 -Name <arm> -Weights <cache>/<arm>.pt` is the scorer an arm gets once its weights are home (the
Azure driver's `-AfterScript`, below): A's draws and the twins at one view through the arm's own `r2_infer.py`, the
real tiles' check beside earlier arms (`real_star_check.py`), the ring around bright stars (`ring_annulus.py`), and M31's
nucleus over 8 views (`m31_nucleus.py`). Its paths are the `arm` object of `scorecard.local.json`
(`scorecard.example.json` lists the keys). **Resumable: a step whose output exists is skipped**, so a second call over a
scored arm runs no inference and exits 0. `arm-driver.ps1` is the same idea for an arm trained on this machine: one step per
pass, run by a scheduled task (`register-arm-task.ps1`: every 10 minutes, `conhost --headless`, priority 4, `IgnoreNew`),
a stop file, and a failing step stops the chain rather than retrying all night (any exception does, not only a named
failure, #1787).

**The real-star read is per session as well as pooled.** `real_star_check.py` prints its pooled table first, unchanged,
then three small tables for the bright band (100 to 1,000 sigma): `% flat`, `% left` and `% dug`, one row per validation
session as the plan's "R2w's read" names them (Lobster, M42 (ASI294MC), HIP 80609, SMC), then `all`. A session's views (the
night and its `flip=a` / `flip=b` halves) share a row, their stars and percentages joined by ` / `, and every listed model
is a column. `score-arm.ps1` copies the whole log into `real-star-read-<name>.txt`, so the tables land there; the whole
masters' `row.txt` (`score_whole.py`) reads no real tiles and carries none.
`test_real_star_sessions.py` pins the split on a stub export (no GPU, no owner data).

**Scoring on a second set of A's draws and twins** (F2's master-stretched ones) is one more config, never a flag:
`score-arm.ps1 -Name <arm> -Weights <cache>/<arm>.pt -Config scorecard.master-stretch.json`, where the copy names NEW folders
for `arm.random_export`, `arm.twins_root`, `arm.eval_root`, `arm.reports` and `arm.logs_root`. The skip tests key on
`<eval_root>/<name>/on-*/` and `<reports>/*-<name>.txt`, so with those five apart the second run reads and writes nothing of
the first set's (the scores already made stay as they are); with `eval_root` or `reports` shared it would take the first set's
outputs for its own and skip, silently. `real_export`, `val_list`, `plates_root`, `m31_master` and `m31_out` may stay shared:
they do not depend on the set (the real tiles are scored again under the new `eval_root`, a cheap inference).
`arm-step.ps1 -Step score` and `arm-driver.ps1` take the same `-Config`.

The work of a pass is `star-removal/arm-step.ps1 -Step export|train|onnx|score|whole`, one step per call with the driver's
parameters, so each runs alone as a pilot job and the task and a job run the same code. A step whose output exists
(ExportDone, `<Name>.pt`, `<Name>.onnx` with its contract, the three reports, `row.txt`) prints `skipped` and exits 0
without starting a child; a failure exits non-zero and writes `<tag>-chain.stop`, which a step also refuses to run past; exit
3 (train only) is "another trainer holds the GPU", nothing done, nothing failed; exit 4 (train only) is "resumable", below.

**An arm as pilot jobs, one line per step.** `<common>` is the arm's own `-Tag r3a -Name r3a_s0 -TrainDir <the training
cache> -Cache <the scoring cache> -Config <scorecard.local.json> -Runs <the runs folder>`, the same for every step. Every
line carries `--timeout 8h`, because pilot run's default is 2 h and ends a job at it. A job killed at its timeout loses
nothing: every step skips what exists and the export resumes, so **run the same line again**.

```
pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step export <common> -ExportScript <export.ps1> -ExportDone <its done file>
pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step train <common> -TrainScript training/denoise/run-r2.ps1 -TrainArgs <run-r2's switches, comma-separated>
pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step onnx <common>
pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step score <common>
pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step whole <common>
```

**The train step finishes across two jobs.** `-TrainArgs` must make `run-r2.ps1` train exactly `<Name>` in `<TrainDir>`:
its `-Tag`, ONE value in `-Arms`, `-FirstSeed` and `-SeedCount 1` (its defaults train three arms x three seeds, nine
models, and a budget stop would land in the middle of one of them). The step passes `-CheckpointEvery N` (default 500; `run-r2.ps1` forwards it as
`--checkpoint-every` and `--resume`) and waits on the trainer for at most `-Budget` (a timespan, default 7h30m, under
pilot's 8 h cap). At the budget it stops the trainer's whole process tree itself; the trainer replaces
`<TrainDir>/<Name>.resume.pt` by an atomic rename, so the last checkpoint is whole whenever the process dies. It then says
`resumable: run the train step again` and exits 4, without a stop file: **run the same train line again** and the trainer
continues from the checkpoint (the second job's budget is its own, a full 7h30m). A budget stop after the trainer had written
`<Name>.pt` (it removes its resume file right after the final save) is done: exit 0, no stop file. A trainer that ends
without `<Name>.pt`, or a budget stop that left no resume file (`-CheckpointEvery 0`), is a failure and writes the stop file. Under the driver
a pass that hits the budget just ends, and the next pass resumes.

The onnx step runs the frozen run's `n2n_export.py` (the config's `arm.run`) over `<Cache>/<Name>.pt`, writing `<Name>.onnx`
and `<Name>.contract.json` into the score folder (`<arm.logs_root>/score-<Name>`); `score-arm.ps1` and the whole step reuse
that ONNX. `star-removal/test-arm-steps.ps1` checks all of it on a stub arm in a temp folder (no GPU, no build, no data;
its stub trainer writes a resume file and sleeps, to show the budget stop and the resume).
