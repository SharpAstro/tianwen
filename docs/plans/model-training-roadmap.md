# Model-training roadmap: the five trainings, in order

**Written 2026-09-02.** TianWen trains, or plans to train, five models. Four are imaging enhancers
trained offline in PyTorch and shipped as ONNX (denoiser, deconvolver, star remover, gradient
remover); one is the neural guider, trained in process in C#. Each has its own plan with hypotheses,
arms and kill criteria; this document holds what they share, the order they should run in, and the
facts that gate all of them. It supersedes nothing: [ai-denoise-deconv.md](ai-denoise-deconv.md)
stays the programme's history and measurement record, [osc-narrowband-denoiser.md](osc-narrowband-denoiser.md)
the run log of the first campaign.

| Training | Plan | State 2026-09-02 | Next concrete step |
|---|---|---|---|
| OSC denoiser (P1) | [denoiser-training.md](denoiser-training.md) | v19d shipped opt-in; pool 100 percent narrowband; master-depth gap open; **E0.5 + E0 done 2026-09-02** (runner domain fixed, trainer in the repo, v19d reproduced bit for bit); **E1 done 2026-09-03**: `tianwen dataset degrade` exports injected pairs, and the warped arm is calibrated to the deployment regime by measurement (band1/band0 0.460 against a real half-master's 0.463; white reads 0.216) | E2: arms S-white against S-warped at --warp-sigma 0.5, three seeds each |
| Non-stellar deconvolver (P2) | [deconvolver-training.md](deconvolver-training.md) | Not started as training; PSF family measured per (train, filter, channel); store predates the deblender; no ladder captured; **E2's exporter shipped 2026-09-03** (`--mode blur`, shared with the denoiser) with byte-parity against the bake's own tiles | E0: `dataset build --force-psf`; E1: oracle ceiling + encoding spread |
| Gradient remover (P5) | [gradient-remover-training.md](gradient-remover-training.md) | Not started as training; **G0 done 2026-09-02** (`ClassicalBackgroundExtractor`, the `AddTianWenAi()` fallback when GraXpert is absent); **G1 done 2026-09-03**: `tianwen dataset gradient-report` over both bakes, 118 masters. H1 answered: p-p p50 2.32 sigma / p95 13.66, amplitude driven by FIELD OF VIEW, a dome is the plurality shape (so the injection needs the quadratic), direction tracks the horizon (58 to 61 percent within 45 degrees), **the Moon is refuted** (7 percent, both bakes). Both of G0's reasoned thresholds now measured | G2: the whole-frame linear exporter, injection family drawn from G1's distribution |
| Star remover (P4) | [star-remover-training.md](star-remover-training.md) | Not started; last by design (depends on P2's PSF family and P5's flattener) | R0 after P2.0 |
| Neural guider | [neural-guider-training.md](neural-guider-training.md) | Shipped, opt-in, cannot beat its own teacher by construction | N1: measure the imitation ceiling on the coupling harness |

## 1. The order, and why

1. **Denoiser E0.5 first, because it is a shipped defect and costs hours** (done 2026-09-02). The
   training tiles are MTF-stretched to median 0.25; the runner fed linear pixels near 0.005. Every
   real-frame verdict in the programme was measured through that skew. Fixing it moved the
   real-frame result onto the best rescale arm and removed a 30 percent flat star suppression no
   metric had seen; every later arm is judged through the fixed runner, in linear units.
2. **Background-extraction Phases 1 and 2 (G0) next** (done 2026-09-02), because it is three things at once: the
   classical `IGradientCorrector` fallback the product needs anyway, the baseline P5 must beat, and
   the flatten step P4 and P5 both consume. It is pure math in `TianWen.Lib`, fully synthetic-testable,
   and its design is complete after the reference review.
3. **The shared degradation exporter (denoiser E1 = deconvolver E2 = star remover R1)** over the
   retained LINEAR masters, with three modes: noise injection, PSF blur plus noise, star injection.
   One exporter, because all three need the same thing the P0 tiles cannot give (a linear frame to
   degrade before the exporter's own stretch) and the same parity pin against `Image.MtfStretch`.
   **Done 2026-09-03 for the first two modes (section 2).**
4. **Denoiser E1 to E5** (synthetic injection, shape, broadband) and **deconvolver E0 to E4** in
   parallel; both are 11-minute local runs on the 0.81 M U-Net and share the eval tooling.
5. **Neural guider N1 to N5** whenever the imaging GPU is busy: it needs no GPU at all.
6. **Gradient remover G1 onward** once G0 exists; **star remover** once P2.0 has re-measured the PSF
   store.
7. **Rented GPU only for a capacity question the local smoke has already made worth asking**
   (denoiser H5), never for an ablation sweep.

## 2. Shared infrastructure (what exists now, and what does not)

**The trainer IS in the repo since 2026-09-02 (`training/denoise/`, E0 passed; see `training/README.md`
for what came across and what stayed on D:). The rest of this section is the state it was written
against, kept because the layout below is still the target: only `denoise/` exists, and `common/` is
deliberately deferred until a second trainer needs it.** Before that day the trainer was not in the
repo: every Python file that trained, gated, scored, exported or fixtured a model lived under
`D:\Astro-Dataset\n2n-smoke\` (newest generation `v24/scripts/`, deployment set
`ship/`), snapshotted per run and referenced from the plan docs by absolute path. Losing that disk
loses the ability to retrain or re-export. There is no `requirements.txt`, `pyproject.toml` or
environment file anywhere; the versions are recoverable only from `ship/n2n_v19d_s2_final_export.json`
(`opset 17`) and the ONNX producer string (`pytorch 2.13.0`). Three hardcoded paths still point at
the dead `C:\tianwen-scratch` (`--cache` default, `EVAL` in five scripts, `CACHES` in one).

Bring it in as `training/` at the repo root, the layout the programme doc planned:

```
training/
  requirements.txt          torch==2.13.0+cu126 (cu126 index, NOT cu130), onnx, onnxruntime, numpy, scipy, matplotlib
  common/                   n2n_gate.py, n2n_metrics.py, n2n_bars.py, n2n_rotate.py, n2n_structfrontier.py, n2n_frontier.py
  denoise/                  n2n_smoke.py (renamed train_denoise.py once stable), export, dial, fixture
  deconv/                   the degradation exporter driver + train_deconv.py
  gradient/                 whole-frame exporter driver + train_gradient.py
  starless/                 plate builder driver + train_starless.py
  EXPERIMENTS.md            one line per run: arm, seeds, cache, commit, verdict (negatives included)
```

Rules for the port: no behaviour change (acceptance is a bit-identical reproduction of
`n2n_v19d_s2_final.pt` from `C:\temp\tianwen-scratch\n2n-d8`, met 2026-09-02 and judged tensor for
tensor by `n2n_ckpt_equal.py`, never by file hash, since `torch.save` writes the output name into
the archive and the metadata dict grows with the trainer's options); every machine path becomes a
required argument; a run still snapshots its scripts and pre-registers its predictions in its run script header;
`.py` files under `training/` are not built or tested by `dotnet`, and `dotnet.yml` ignores them.

**Torch environment (verified 2026-09-02):** `torch 2.13.0+cu126`, CUDA available, arch list
`sm_50 sm_60 sm_61 sm_70 sm_75 sm_80 sm_86 sm_90`, device `NVIDIA GeForce GTX 1070`; numpy 2.5.2,
scipy 1.18.0; the Python `onnxruntime` is 1.28.0 CPU-only (Azure + CPU providers), which is fine for
parity checks and unrelated to the .NET DirectML path. Re-run the one-liner before any campaign; a
`pip install torch` without the cu126 index installs cu130, which cannot target Pascal.

**Provenance contract.** Each exported model ships a `<model>.contract.json` (dataset manifest
SHA-256, git commit, ONNX SHA-256, tensor conventions incl. domain and stretch constants, psf01
encoding where relevant, timestamp) asserted at load by the C# enhancer: mismatch means refuse, log,
fall back to the next backend. `N2nDenoiser` ships without one today.

**One shared degradation exporter: SHIPPED 2026-09-03** as `DatasetDegradationExporter`
(`tianwen dataset degrade`), in C# beside `DatasetTileExporter` and reusing its `ToUnitRange`,
`ApplyInputStretch` and tile writer. Two of the three modes are in (noise injection for the denoiser's
E1, blur-then-noise for the deconvolver's E2); star injection waits on the star remover's R0, which
does not exist yet. Four decisions it settled, each of which is a way the naive version is wrong:

- **Both sides of a pair take the TARGET's unit divisor and MTF parameters** (`Image.MtfStretchWith`).
  Each side taking its own would encode the stretch difference as signal, and injected noise moves a
  frame's maximum, so the unit divisor alone would rescale the input side. It also makes the transform
  pointwise, which is what lets a draw be applied to one CELL instead of the whole canvas per draw.
- **Cells and the level anchor come from the P0 manifest**, so a degraded tile covers exactly the
  pixels its P0 counterpart does and the injected level is anchored on a REAL sub's measured noise.
- **The manifest is P0-shaped**, so the trainer's `--prepare` reads a degraded cache with no Python
  change: `Frame` = `master` for the clean target lands in slot 0 and `deg000..` in the sub slots.
  Verified by running the trainer's own `load_cells` over an exported cache rather than by reading its
  source (36 cells, master resolved, four draws in the sub slots). Degradation parameters live in their
  own `degradations.jsonl` rather than as extra columns, so one fact keeps one authority.
- **The parity gate is against the bake itself**: the clean tile derived from the retained master is
  byte-identical to the P0 tile of the same cell (0.0 measured on every session). A self-parity check
  compares a path against itself and cannot see a drift between the two paths; this one can.

## 3. Compute, measured

- **GTX 1070, 8 GB, Pascal `sm_61`.** fp32 4.91 TFLOPS; fp16 SLOWER (4.35), so AMP is gated OFF on
  this device and ON on a rented card. The 0.81 M U-Net at 256 px runs ~48 tiles/s at batch 8: 4,000
  steps is 11 minutes, three seeds 35 minutes. A 4.33 M base-48 net runs ~40 tiles/s. A NAFNet-32
  (29 M) at 256 px does not fit a useful batch in 8 GB and is a rented-GPU job (RunPod 4090 at
  $0.35 to 0.69 per hour, or the internal T4 pool; state batch and accumulation, since results are not
  comparable across cards otherwise).
- **RAM bounds the cache, not disk:** a prepared cache is resident (4.33 MiB per cell); 2,940 cells
  is 11.8 GiB of 31.8. Trade cells per session for session count.
- **Disk:** `C:\temp\tianwen-scratch` is 76 GB with 132 GB free on C:; `bpm-probe` (19 GB of FITS)
  and six 11 to 13 GB caches are the bulk. **`drizzle-root` inside it is a live junction into
  `D:\Astro-Pics\2025\2025-05-20 - Lobster Nebula`; a recursive delete of the scratch reaches the
  archive.** D: has 1.5 TB free; `D:\Astro-Dataset` measures **419 GB over 944,722 files** (about 371 GB of it
  the 393,216-byte tiles), enumerated in a second with `robocopy /L /S /E /BYTES`; msys `du` is
  unusable there (35 minutes, no output, one stat per file on a spindle).
- **Wall-clock jobs run detached** (`Start-Process`; the bake launcher is `tools/run-dataset-bake.ps1`
  with its staleness gate), never through the session's background shell, and nothing reads a file a
  running job appends to.

## 4. Data, one table

| Set | Sessions / tiles | Filter known | Retained linear masters | Trained on it | Use |
|---|---|---|---|---|---|
| `D:\Astro-Dataset\2025-2026-organized` | 51 / 159,300 | yes (40 L-Ultimate 3 nm, 11 L-Quad) | 51 | nothing yet | the pool for every new arm |
| `2025-2026-darkscaled` | 67 / 207,900 | no | 67 | v15 to v24 (armD = v19d) | controls only; not interchangeable with organized |
| `2025-2026-drizzle` | 64 / 197,400 | no | 64 | nothing | drizzle-only PSF calibration |
| `2025-2026-calgated` | 50 / 135,000 | no | 50 | v2 to v14 | history |
| `C:\temp\astro\2026-08 SV545` | 2 non-comet nights, 594 lights | yes (`IDAS LPS-D3`, broadband LP) | none (unbaked) | nothing | the only broadband-class data; denoiser H4 |
| `C:\temp\tianwen-scratch\n2n-eval4` | 193 cells, 4 observers | mixed | n/a | eval only | the four-observer eval cache |
| Older archive years (2021 to 2024) | unbaked | no | none | nothing | more observers (denoiser H6), behind `CALSTAT` read guards |

Stores: `<bake>/stats/psf-sessions.jsonl` (per-session PSF, per-channel profiles, `MasterStrategy`;
the drizzle and darkscaled stores hold two records per session, last wins), `skipped-sessions.jsonl`,
`session-timings.jsonl`, `tiles-manifest.jsonl` (`NoiseMad` per tile; no FWHM column by design).
Every store predates the 2026-08-27/28 deblender; `--force-psf` before fitting anything.

**Held-out split is by session, pinned by a stable hash bucket** (`test-sessions.txt`), and val is
pinned BY NAME inside each cache's `meta.json`. Adding sessions never reshuffles it.

## 5. Discipline every plan inherits

Written down once here because each item cost a wrong conclusion in the first campaign.

- **Pre-register.** Predictions in the run script header before the prepare; a result that needs a new
  explanation afterwards is recorded as a finding, not drawn as a conclusion.
- **Seed everything and prove it.** Init and draw seeded, cuDNN deterministic; two runs of one seed
  must produce identical tensors (compare checkpoints, not logs).
- **Several seeds per arm, and rotate the axis the conclusion rides on.** One training set, one
  observer or one seed cannot support a claim whatever it returns. Measure the noise floor of the
  comparison (a second draw of the SAME condition) before the first mechanism experiment.
- **Compare at matched operating point** (matched residual noise for a denoiser, matched FWHM
  reduction for a deconvolver), never at matched step count.
- **Select on transfer-tested metrics; audit each threshold against its distribution** (a threshold
  nothing reaches vetoes; a ratio maximised by the identity selects nothing). PSNR never.
- **A detection count whose threshold derives from the array being counted measures the threshold.**
  Use an absolute bar for safety and a relative one for ranking, and say which.
- **Truth masks are depth-specific**; re-derive before measuring at a new depth.
- **Check level bias per channel before shipping any learned operator**; the selection metrics are
  DC-invariant and cannot see it.
- **Zero train/inference skew is a measurement, not a statement**: verify the domain the runner hands
  the graph against the domain the tiles are in (the denoiser's fact 0 is what happens otherwise).
- **Post a labelled comparison image** when an experiment concludes.
- **Third-party model outputs never enter the loop**: RC-Astro by EULA section 10, SAS AI4 by
  default until its licence is verified, GraXpert by licence (CC-BY-NC-SA weights). Comparisons
  against them are human side-by-sides outside the automated loop, at the user's discretion.
- **Never write to `D:\Astro-Pics`.** `C:\temp\astro` is a working copy.

## 6. What is NOT planned, and why

- **Mining the archive for deblur pairs.** Measured: intra-session FWHM p90/p10 median 1.04, zero
  auto-focus frames in 245,213 files. Ladders are captured going forward instead.
- **Half-master N2N pairs.** Final negative, three seeds, two conditioning shapes.
- **The conditioning-plane strength dial.** Measured and rejected; the blend ships.
- **Pair-time selection.** Closed by v23.
- **A size recipe for the training set.** Three disjoint eights gave 0.825 / 0.726 / 0.739; ship
  measured checkpoints, re-measure retrains.
- **Mono models.** Until mono data worth training on exists.

## 7. Bookkeeping this roadmap owes

- `docs/plans/summary.md` rows for the five plans (added 2026-09-02).
- `ai-denoise-deconv.md` phasing rows P1, P2, P4, P5 point here.
- `.gitattributes` line 67: the temporary LFS exemption for the in-repo N2N weights, revert due when
  the replacement model lands (denoiser E7).
- `N2nLinearRunner`, `N2nDenoiser`, `ship/README.md`, run-log 1o: the four "trained on linear" claims
  to correct once E0.5 confirms the fix.
- `docs/todo/hardware-validation.md`: three nights with `SaveIntermediates` on, on both main rigs.
