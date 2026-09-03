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
SUPERVISED pair**, which is the point of the arm; the trainer's existing regimes give the first for
free and the second needs the `--synthetic` flag that does not exist yet. And **`--warp-sigma` is a
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
  read a file a running job is appending to; read its status file.

The C# parity fixture is what `n2n_fixture.py` writes; regenerate it whenever the checkpoint or the
plate changes, then run `N2nDenoiserTests`.
