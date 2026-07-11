# TianWen-Trained Denoise + Deconvolution Models ("own AI")

**Status: NOT STARTED (plan approved-pending-review, 2026-07-11).**
Goal: train our own CNN denoiser (`IDenoiseEnhancer`) and non-stellar deconvolver
(`INonStellarDeconvolver`) on the user's own image archive, shipped as versioned ONNX models through
the existing `TianWen.AI` / `TianWen.AI.Imaging` stack — a third backend tier alongside RC-Astro
(paid, licensed) and SAS AI4 (free fallback).

Scope boundaries settled up front:

- **Training is offline, on our side** (Python/PyTorch on rented GPU). Customer machines run ONNX
  Runtime inference only — **no Python, no on-device training** for the imaging models. (On-device
  online learning remains a NeuralGuider-only feature; its PPEC-style per-rig adaptation is out of
  scope here.)
- **Star removal (SXT-analogue) is in scope as a LATER phase** (P4, after denoise/deconv prove the
  pipeline) via the inject-and-remove bootstrap (§2.5). Croman's "hand-edit is the only way" holds
  only when you need ground truth for *existing* stars; synthetic injection gives exact truth by
  construction. Until its eval gates pass, `IStarRemover` stays on RC/SAS — but a TianWen remover
  is required for the tier to run the full canonical program (the starless plate is the workhorse
  intermediate: `RemoveStarsStep`, `--split-plates`, star/starless dual stretch).

## 1. Licensing constraint (load-bearing — read first)

The RC-Astro EULA (`C:\Program Files\RC-Astro\CLI\LICENSE.txt`, §10) **explicitly prohibits** using
the Software *or its outputs*, "directly or indirectly, to create, train, fine-tune, test, benchmark
for replication purposes, distill, validate, improve, or otherwise develop any machine learning
model … intended to replicate, emulate, compete with, or perform functions substantially similar",
naming *"the creation of training datasets or paired input/output datasets derived from the
Software's operation"* verbatim. The originally-floated "rc-astro as batch oracle for golden images"
is therefore off the table — as is using RC outputs for **validation or benchmarking** of our
models.

The same section carves out *"lawful independent development of competing technologies … developed
without use of the Software, its proprietary components, or outputs"*. This plan is built entirely
on that carve-out:

- **RC-Astro outputs appear nowhere in the training, validation, or metric loop.** RC remains
  what it is today: the preferred runtime backend for *processing images*.
- **SAS AI4 model outputs are also excluded from the ML loop** until their license terms are
  verified (open question #1) — assume the same restriction by default.
- All ground truth is derived from the user's own raw data (stacks, sub-pairs) or from synthetic
  degradation with published math. This is not a workaround; it is the scientifically stronger
  approach (real sensor noise, real optics) and it enables a claim RC cannot make (§7 photometric
  integrity).

## 2. Training-data strategy (no oracle needed)

### 2.1 Denoiser ground truth — the archive already contains it

- **Noise2Noise (primary):** two registered, calibrated subs of the same target are two independent
  noise realisations of the same signal. Training input = sub A tile, target = sub B tile
  (same footprint, same session). Expectation over pairs equals the clean signal — **no clean target
  needed at all**, no input↔target noise correlation, and the pair count is combinatorial in subs.
- **Stack-as-truth (evaluation + optional supervised mix):** the session's integrated master is the
  low-noise reference for held-out metrics (PSNR/SSIM vs master). As a *training* target it slightly
  correlates with each contributing sub (1/N of its noise); with N ≥ 20 subs this is acceptable for
  a supervised mix-in, but N2N stays primary.
- **Synthetic noise augmentation (secondary):** degrade master tiles with the electron-domain noise
  model already calibrated in `SyntheticPlanetRenderer` (shot noise Poisson in e⁻, read noise in
  quadrature, `aduPerElectron = maxAdu / fullWell`) using per-camera gain/full-well from FITS
  headers. Widens the noise-level distribution beyond what the archive naturally has.

### 2.2 Deconvolver ground truth — synthetic PSF degradation

- Input = sharp master tile convolved with a synthetic PSF; target = the undegraded tile;
  **electron-domain noise is added AFTER the blur on every pair** (never optional — deconvolution
  is ill-posed and noise amplifies under inversion, so noise-free pairs train a brittle sharpener).
  PSF family: Moffat (β 2.5–4.5) with FWHM swept over [1, 8] px, elongation/PA, coma term, optional
  linear guiding-smear kernel — and **position-varying**: P0 measures the archive's FWHM/
  ellipticity/PA distribution **binned by field radius** (`FindStarsAsync` centroids give star
  positions; fast-lens corners genuinely differ from center), and per-tile degradation samples
  aberrations from the measured field-position distribution instead of one stationary kernel.
- **Space-truth tier (experiment, above the own-masters baseline):** own masters are seeing-limited
  (FWHM ~2–3 px), so they teach only relative sharpening toward their own ceiling. Public HST/JWST
  FITS from MAST (public domain / CC-BY — degrading *public archive data with our own measured PSF
  family* is fully independent development; HST/JWST truth is *reported* as BXT's approach in
  RC-Astro's FAQ and secondary coverage, NOT stated in the 2022 AIC talk, which predates BXT — and
  our justification is independent of what BXT did) become sharper truth: downsampling to the rigs' 1–3"/px scales crushes HST noise, yielding effectively
  noiseless linear truth at our sampling. Domain gaps (filter sets ≠ OSC RGB) are handled by
  luminance/per-channel training — PSF inversion is near-achromatic — and the tier is adopted only
  if it beats the baseline on the pinned split.
- **PSF conditioning mirrors the SAS conditional model exactly:** a second scalar ONNX input
  `psf01 ∈ [0,1]`, log2-encoded over [1, 8] px — the *same* encoding `HfdPsfEstimator` already
  produces and `OnnxIoNames.ImagePlusScalar` already classifies. Our model becomes a drop-in for
  `OnnxNonStellarDeconvolver` (different model file, same two-input signature).
- Masters are themselves seeing-blurred, so the net learns *relative* sharpening (standard for
  synthetically-bootstrapped deconv nets). Two mitigations: prefer the sharpest sessions as truth
  (median FWHM gate), and optionally use 2× Bayer-drizzle masters as a sharper truth tier.
- The pipeline applies deconv to the **starless plate** (`DeconvolveStarlessStep`); training tiles
  keep their stars (a starless plate is a sparser subset of that distribution). If star artefacts
  show up in eval, add star-masked tile variants (own star detection + mask, no third-party star
  removal in the data path).

### 2.3 The archive

Full survey (roots, per-era layout conventions, camera-by-era table from real FITS headers,
extension/size/per-year breakdowns, and the complete hazard list) lives in the dedicated
[astro-archive-survey.md](astro-archive-survey.md) — read it before starting P0. Summary: ~83,500
files / ~1.96 TB across `D:\Astro-Pics` (primary) + `D:\BobbyBox-Temp` (working tree, partially
duplicating 2024–mid-2025, plus unique Aug–Nov 2025 sessions). The **recent/good band (2024–2026)**
— ASI533MC Pro (RGGB), ASI585MC Pro, SVBONY SV605CC (GRBG), one ASI1600MM mono session, consistent
N.I.N.A. headers, per-session BIAS/DARK/DARKFLAT/FLAT — holds an estimated **~20,000–24,000
candidate raw lights** before quality filtering. Older eras (2021–2022, ASI294/QHY178m, ~668 GB)
are lower value; SER/planetary and CR2/DSLR are excluded in v1.

**Step 0 (archive organization, before any builder code):** `tools/astro-archive-dedup.py` — a
READ-ONLY scanner producing a resumable per-file header index (`fits-index.jsonl`) plus three
reports: `dup-files.csv` (exact-dup groups, identity = camera + `DATE-OBS` + exposure + dims,
hash-confirmed; cross-root flagged), `nights-rollup.csv` (per camera/night light counts split
dup/unique — the "what in BobbyBox is actually new" answer), and `calibration-coverage.csv` (per
light group: matching darks/bias found anywhere in the archive — because **calibration masters are
shared between sessions**, per-session folders cannot be assumed). Any physical
extraction/filing of BobbyBox uniques into Astro-Pics happens as a user-reviewed step from these
reports; the script itself never moves or deletes anything.

Load-bearing hazards for the dataset builder (all detailed with examples in the survey doc): (1)
dedup across Astro-Pics ↔ BobbyBox-Temp *and* within Astro-Pics itself — content-hash + `DATE-OBS`
pass (Step 0's `dup-files.csv`); (2) never ingest `AutoSave`/`PROC`/`pixinsight`/XISF processed intermediates — gate on
`IMAGETYP='Light'` + `EXPTIME` in [10, 300] s from headers, not folder names (BobbyBox-Temp
especially: raw subs and XISF intermediates share the same session folders), **and exclude
simulator cameras by `INSTRUME`** (Step 0 found 139 "Camera V3 simulator" lights from a
2024-03-15 N.I.N.A. test session — synthetic frames would poison the noise model); (3) mixed Bayer
patterns (RGGB vs GRBG) and mono+FILTER sessions need per-camera debayer; (4) `2026-02-20 BAD LIGHT
EXAMPLES` (33 hand-flagged bad frames) is a free validation set for the quality gate; (5) 39+4
`.7z` archives (~100 GB, mostly pre-2023/planetary, already out of v1 scope) are invisible to a
folder scan unless extracted — watch for new ones under future 2024+ sessions.

### 2.4 Dataset builder (`tianwen dataset build`, new CLI subcommand)

C# tooling in-repo, reusing existing Lib machinery end-to-end (scan `FitsFolderFrameSource` →
dedup → quality gate → calibrate via `MasterFrameBuilder` → debayer → register subs to the session
master → tile export). **Calibration is resolved by header match across the whole archive, never by
session folder** (confirmed 2026-07-11: dark/bias libraries are shared between sessions) — the same
`MasterGroupKey`-style identity (camera, exposure, gain, binning, ±temp) the stacker already uses;
Step 0's `calibration-coverage.csv` is the coverage map. **Masters are built once per
`MasterGroupKey` group and cached on disk** (the `StackingPipeline.BuildMastersAsync` mechanism —
a shared dark library serving five sessions is one group, one master), with two builder-specific
requirements: the cache dir is **one shared archive-wide location** (not per-run `outputDir`, so
build-once holds across all ~180 sessions and re-runs), and the cached master carries an
**input-set fingerprint** (frame count + content hashes stamped into its header) so a grown dark
library invalidates its stale master instead of silently cache-hitting on the slug. Foreign
pre-existing masters (PixInsight XISF / DBE'd TIFs in `PROC` dirs) are deliberately **not**
reused — unknown rejection/scaling provenance breaks the "pure function of inputs" cache trust;
raw calibration frames exist for essentially every 2022+ session, so rebuilding is cheap and
reproducible. Per session:

- **Quality gate** per sub: `FindStarsAsync` star count + median HFD + ellipticity, thresholded
  *relative to the session median* (absolute thresholds don't transfer across focal lengths); drops
  cloud/trailing/defocus frames. Validated against the BAD LIGHT set.
- **Fixed tile grid per session** (256 px cells on the master's frame, sampled ~200–400 cells biased
  toward structure by local signal): every exported sub tile and the master tile share exact
  footprints, so any two subs' cell (i,j) is an N2N pair and the master's cell (i,j) is eval truth.
  Export the master tile + a random 4–8 subs per cell (not all subs — bounds dataset size).
- **Output**: fp16 tiles (npy-compatible raw blobs) + a JSONL manifest per tile: source file,
  session id, camera, gain, exposure, tile coords, per-tile noise σ (MAD), session median FWHM.
  Manifest rows written via a **canonical sort before any sampling** (parallel writers break every
  downstream seeded operation otherwise).
- Budget: ~60 sessions × ~300 cells × ~9 tiles ≈ 160k tiles ≈ **50–80 GB** — one upload to a cloud
  volume, regenerable from scratch by re-running the command.

**Zero train/inference skew (non-negotiable):** the tile exporter calls the *same* code the
inference path uses — `AiNafnetInputs` MTF pre-stretch (target median 0.25, auto-skip threshold
0.125), `[0,1]` linear convention, `ChunkedInference`-compatible geometry. Python never
re-implements preprocessing; it consumes tiles as-stored. A `parity-check` diff (export N tiles,
run the C# stretch and the stored bytes side by side) pins this in CI-able form.

### 2.5 Star-removal ground truth — inject-and-remove bootstrap (P4)

Ground truth for *existing* stars would need hand editing; ground truth for *injected* stars is
exact by construction. Four stages, fully license-clean (own data + own synthetics + own model):

1. **Classical bootstrap starless plates**: PSF-fit subtraction at `FindStarsAsync` detections +
   multi-scale inpaint. Imperfections are acceptable — residual artifacts become background the
   net must *preserve*, never content it must invent.
2. **Synthetic star injection** onto those plates, drawn from the archive's measured PSF
   distribution (same P0 stats that calibrate the deconv sweep): Moffat cores, lens halos,
   saturation/bloom for the bright tail. Input = plate + injected stars, target = plate.
   **Injection positions must be uncorrelated with the classical-removal residual sites**
   (Croman's "the network will faithfully learn all of your mistakes"): if injected stars
   preferentially land on inpaint artifacts, the net learns that removing a star reveals
   artifacts — random placement keeps the truth under injected stars overwhelmingly clean
   background.
   Advantage of this archive: all optics are refractive (Samyang 135, ZS61, FMA180, SH61) — no
   spider vanes, no diffraction spikes — so the morphology distribution is far narrower than a
   general-purpose remover must handle.
3. **Self-refinement loop**: run the trained net on real images → better starless plates →
   re-inject → retrain. Distills only our own model.
4. **Output contract** matches `RemoveStarsStep`'s additive split (stars = input − starless).

Eval is objective because injected truth is exact: removal completeness on injected stars,
pixel-level background preservation under them, flux conservation of the stars plate; plus
existing-star spot checks at 1:1 (the bright-saturated tail is the known hard case — keep RC/SAS
preferred until it passes).

## 3. Model + training

- **Architecture: NAFNet, width 32, standard block config ≈ 29 M params** (the SXT 21M / NXT 24M /
  StarNet-V2 30M league; ~115 MB fp32 ONNX, same class as the SAS AI4 files the runtime already
  handles). Capacity tuning goes DOWN via middle-block count on pinned-split ablations — Croman
  ("capacity saturated"), Topaz competing at 14M, and our narrower single-user domain all say >30M
  needs evidence, and width-64 (~116M) is off the table (4× the customer download for nothing).
  Same family as the SAS AI4 models, so the stride-16 / tile-256 / overlap-64 constraints of
  `ChunkedNafnetRunner` hold by construction. Denoiser: 3-channel in/out (mono handled by the
  runner's channel-tiling, as `OnnxStellarSharpener` does). Deconvolver: image + `psf01` scalar.
- **Strength control comes free:** train full-strength models; `SharpenPipeline` already applies
  per-step `Blend` as a post-hoc `Image.Lerp` toward the source — that *is* the user-facing strength
  slider. NXT-style per-frequency knobs are explicitly deferred.
- **Losses:** L1 (MAE) primary + MS-SSIM auxiliary; plus a **flux-preservation regulariser**
  (per-tile mean/aperture-sum penalty) — see §7. **Adversarial/GAN losses are deliberately
  excluded**: they optimise for plausibility, which is hallucination pressure — directly opposed
  to the photometric-integrity gates. If perceptual quality ever needs a boost, prefer feature
  losses with the flux regulariser as a hard constraint.
- **Optimisation:** AdamW, cosine schedule, grad-norm clip 1.0, early stop on held-out val, seeded
  end-to-end. Mirrors the Croman talk's recipe and prior in-house ML-pipeline experience.
- **Discipline (proven in-house ML-pipeline patterns, adopted wholesale):**
  - `training/EXPERIMENTS.md` — every run logged, ablations base-vs-+change on the **pinned split**,
    negative verdicts recorded to stop re-litigation.
  - **Pinned held-out split by SESSION** (never by tile/frame — adjacent tiles leak noise/PSF stats
    exactly like words leak page layout), committed as a flat `test-sessions.txt`.
  - **ONNX-vs-torch parity check** in the export step (run both on N val tiles, assert max prob…
    pixel delta ≤ tolerance) before any artifact is promoted.
  - **`<model>.contract.json` provenance stamped into the artifact set**: tensor conventions
    (layout, stretch constants, psf01 encoding), dataset manifest SHA-256, git commit, package pins,
    ONNX SHA-256, timestamp — asserted at load time in C# (NeuralGuider's gate-and-refuse pattern,
    minus the delete: refuse + log + fall back to SAS).

### Infra

- Repo layout: `training/` at repo root (Python: `dataset.py`, `train_denoise.py`,
  `train_deconv.py`, `export_onnx.py`, `parity_check.py`, `requirements.txt`, `EXPERIMENTS.md`,
  `test-sessions.txt`). Dev smoke runs: torch-CPU under WSL (no win-arm64 torch wheels; the venv
  bootstrap gets documented in `requirements.txt` comments).
- **Full runs: an internal AKS GPU dev pool as a k8s Job** (Tesla T4 16 GB, 4 vCPU / 28 GB,
  `nvidia.com/gpu: 1`; the workload-identity Job + blob-storage pattern is already proven
  in-house). 16 GB VRAM fits NAFNet-32/64 @ 256 px with AMP; T4 ≈ 3–5× slower than a 4090, so a full
  run is ~4–10 days — fine unattended with checkpoint-every-N-steps (restarts free). The 4-vCPU
  loader is not a bottleneck (tiles are pre-baked fp16).
- **Ablation sweeps (optional fast lane): RunPod Secure Cloud RTX 4090** (~$0.35–0.69/hr,
  per-second billing, network volume ~$0.07/GB/mo) — ≈ 24–72 GPU-h ≈ **$15–50/run** when iteration
  speed matters; Vast.ai interruptible (~$0.13–0.37/hr) once checkpoint-resume is proven. Pull back
  only checkpoint + ONNX.
- Local Adreno/DirectML is for **inference smoke only** (the existing TianWen.AI path); Hexagon NPU
  is inference-only with no vision-model path (verified) — neither trains anything.

## 4. Runtime integration (C#)

- New `OnnxTianWenDenoiser : IDenoiseEnhancer` and `OnnxTianWenDeconvolver : INonStellarDeconvolver`
  in `TianWen.AI.Imaging/Onnx/` — thin: model file names (`tianwen_denoise_color_v1.onnx`,
  `tianwen_deconv_nonstellar_psf_v1.onnx`) + contract assertion; the heavy lifting is the existing
  `ChunkedNafnetRunner` / `TensorImageConverter` / `OnnxIoNames`, reused verbatim.
- **Backend selection** extends the existing single source of truth: `EnhanceBackend` gains
  `ForceTianWen`; `EnhanceOptions.TryParse` accepts `--ai-backend tianwen`; the `Deferred*` proxies'
  Auto order becomes **RC (installed + licensed) → TianWen (model + contract present) → SAS**. RC
  stays first in Auto until our eval says otherwise; users opt in via the flag / viewer
  backend-cycle (right-click on the Enhance button already cycles backends).
- **Model distribution:** published as GitHub Release assets on the TianWen repo;
  `tools/tianwen-ai-models-fetch.ps1` gains a TianWen-models section **with SHA-256 verification**
  (the SAS fetch currently has none — ours sets the standard; contract JSON ships beside the model).
- **Safety gates (NeuralGuider learning, scaled to fit):** default position is "present but not
  Auto-preferred"; contract mismatch → refuse + fall back; NaN/Inf/out-of-range output check on the
  stitched result → discard + fall back to input (passthrough), log a warning. No perf monitor
  needed — this is a user-invoked batch step, not a closed loop.

## 5. Phasing

| Phase | Deliverable | Exit criterion |
|---|---|---|
| **Step 0 — Archive organization** | `tools/astro-archive-dedup.py` READ-ONLY scan (header index + dup-files / nights-rollup / calibration-coverage reports); user-reviewed filing of BobbyBox uniques into Astro-Pics from the reports | Dup report reviewed; unique-to-BobbyBox sessions identified/filed; calibration coverage map exists (feeds P0's header-matched calibration) |
| **P0 — Dataset + stats** | `tianwen dataset build` (scan/dedup/gate/calibrate/register/tile+manifest, zero-skew export; calibration header-matched archive-wide, never per-folder); archive PSF/noise distribution report; pinned `test-sessions.txt` | Tile set regenerable one-command; BAD LIGHT set 100% rejected by gate; parity check green |
| **P1 — Denoiser v1** | `training/` N2N pipeline; NAFNet-32 color run on RunPod; ONNX + contract; `OnnxTianWenDenoiser` + `--ai-backend tianwen`; eval report | Beats classical baseline + no photometric regression (§7) on held-out sessions; visually clean on 3 reference masters |
| **P2 — Deconvolver v1** | Synthetic-PSF pipeline (measured-distribution sweep); psf01-conditioned NAFNet; `OnnxTianWenDeconvolver`; eval incl. FWHM-reduction + artefact checks | Measured FWHM reduction on held-out masters without ringing/worms; photometric gates hold |
| **P3 — Ship** | Auto-order wiring, fetch-script + release assets, CLI/GUI surfacing, `docs` + CLAUDE.md section | `stack --enhance --ai-backend tianwen` end-to-end on a fresh machine (models auto-fetched) |
| **P4 — Star remover** | Inject-and-remove bootstrap (§2.5): classical starless plates + measured-PSF star injector + self-refinement; `OnnxTianWenStarRemover : IStarRemover` (additive split) — completes the tier so the full canonical program runs TianWen-only | Injected-star removal completeness + background preservation + stars-plate flux conservation on held-out sessions; bright-saturated tail passes 1:1 spot checks (RC/SAS stay preferred until then) |
| **P5 — Deferred** | Strength/frequency conditioning beyond Blend-lerp; mono-native models; drizzle-truth sharper tier; frame-quality classifier from BAD-examples; dataset-contribution flow for other users; **comet-registered stacking** (P4 unlock: star-remove subs → integrate on the `CometEphemeris`-computed per-frame comet position via WCS → recombine star-registered stars plate — the AIC comet workflow, automated by ephemeris instead of manual alignment) | — |

## 6. Evaluation (all internal, license-clean)

- **Held-out pinned sessions only** (never trained, never tuned against).
- Denoise: PSNR/SSIM vs session master; residual-noise σ (MAD) reduction; N2N val loss.
- Deconv: star FWHM before/after on held-out masters; structure metrics on nebulosity (local
  contrast without ringing); "worm"/hallucination spot-checks at 1:1.
- **No RC-Astro or SAS outputs in any metric.** Qualitative side-by-sides for a blog post are the
  user's call as an ordinary product comparison — never part of the automated loop.
- Human adjudication: a tiny local compare page (an in-house-learned lesson: blind A/B,
  don't score against "what the user kept").

## 7. The differentiator: photometric integrity

Croman's own stated con: AI-processed images "destroy the scientific value" — flux and centroids
are not conserved — *"unless they were specifically trained to conserve star flux or conserve the
positions of star centroids"* (AIC talk, verbatim). That carve-out is this section: we train and
gate on exactly that:

- **Training:** flux-preservation regulariser (aperture-sum penalty over detected-star apertures +
  per-tile mean preservation).
- **Eval gates:** aperture-photometry delta < X% and centroid shift < Y px (thresholds set in P1
  from the classical pipeline's own repeatability) on held-out subs — a release-blocking gate, not
  a dashboard number.

This gives TianWen a claim neither RC nor SAS makes: an enhancer that is *measured* science-safe on
every release.

## 8. Risks / open questions

1. **SAS AI4 model license** — verify SETI Astro's terms before *any* SAS output touches the ML loop
   (default: excluded, same as RC).
2. **Master quality variance** — weak masters (few subs, poor night) as N2N eval truth understate
   quality; session gate (min sub count, min FWHM percentile) mitigates.
3. **GRBG (SV605CC) vs RGGB** — handled by the existing debayer path; verify no channel-swap in the
   tile exporter with a colour-target session.
4. **Deconv hallucination risk** — the classic failure mode; mitigations: conservative default
   Blend, psf01 conditioning (no blind deconv), artefact spot-check protocol in eval.
5. **~1/N noise correlation** in stack-as-truth supervised mix — N2N-primary makes this a non-issue;
   documented so nobody "optimises" it back in.
6. **Cloud spend discipline** — pinned-split ablation protocol keeps the sweep small; budget
   ceiling per phase agreed before P1 kicks off (~$100–300 total expected).
