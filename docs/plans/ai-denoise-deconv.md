# TianWen-Trained Denoise + Deconvolution Models ("own AI")

**Status: P0 SHIPPED (2026-07-12); P1+ NOT STARTED.**
P0 (the full `tianwen dataset build` pipeline: scan -> discover sessions + calibration -> gate ->
archive-wide header-matched calibrate -> register + integrate -> zero-skew fp16 tile export + JSONL
manifest -> PSF/noise report -> pinned by-session split -> in-run parity gate) is code-complete and
validated on synthetic data; the classes are `SessionDiscovery`, `MasterCache`, `SessionFrameAnalyzer`,
`SessionRegistrar`, `CalibrationResolver` (all `TianWen.Lib/Imaging/Dataset/`), `DatasetTileExporter` +
`DatasetBuildRunner` (`TianWen.AI.Imaging/`, which own the zero-skew `ChunkedNafnetRunner.ApplyInputStretch`
seam), and `DatasetPsfNoiseReport` + `DatasetSplitWriter` (Lib).

**The real-archive run happened on 2026-07-15** (this line used to say it was still to do). Output is
`D:\Astro-Dataset\2025-2026\`: **45 sessions, 4,958 subs, 121,500 tiles** (every session hits the same
300-cell x 9 cap), plus `test-sessions.txt` pinning 5 held-out sessions, and
`stats/psf-noise-report.md`. Cameras: ASI533MC Pro 30, SV605CC 12, ASI585MC Pro 2, ASI1600MM Pro 1.
See § "Running it again" below before regenerating.

**Superseded 2026-08-10/11 by the calibration-gated regenerate**, `D:\Astro-Dataset\2025-2026-calgated`:
**50/58 sessions, 5,984 registered subs, 135,000 tiles** (post-pedestal-fix binary, dark matching gated
on temperature; the 7 skips awaiting darks are listed in `darks-to-shoot.csv`; the 8th skip is HIP 42861,
genuinely too star-poor to register). `stats/psf-sessions.jsonl` covers all 50, re-measured 2026-08-11
with the duplicate-detection fix. One known skew, accepted: 49 sessions' tile registrations predate that
detection fix (they registered fine; the fix mainly rescued sessions that could not), only Helix was
re-exported post-fix. A future full rebake unifies it; not urgent, since a registration either converged
on dozens of quads or was rejected wholesale.

**That run's report header read 31 sessions / 3,433 subs against the manifest's 45 / 4,958, and this
document used to call that "the documented resume behaviour ... not a discrepancy". It was a defect,
and calling it documented behaviour is what let it survive.** The report was derived state held in
memory for one run and overwritten at the end of it, so a resumed run rewrote the whole file from
only the sessions it happened to register. It is not recoverable after the fact either: the
field-radius profile is measured on the session MASTER, which exists only as the output of register +
integrate and lives in scratch that is wiped per session. So every resume permanently narrowed the
one artifact that tells the deconvolution sweep what PSF range to cover.

Fixed 2026-08-10: `DatasetPsfStore` (`stats/psf-sessions.jsonl`) checkpoints each session's raw
measurement samples as it completes, and the report is re-rendered from that store after every
session, so it accumulates across runs and a killed run costs only its in-flight session. Recovering
the sessions measured before the store existed needs `--regen-psf`, which re-registers an
already-exported session purely to re-measure it and leaves its tiles untouched. Two related
protections landed with it: a resume checkpoint is honoured only if the tiles are still PRESENT on
disk (the manifest is a claim about the past, and a session whose tiles were deleted was being
skipped as "already exported" while the run reported success over missing files), and a fresh
non-resume run rotates an existing manifest to `.bak-N` instead of deleting it.
Goal: train our own CNN denoiser (`IDenoiseEnhancer`) and non-stellar deconvolver
(`INonStellarDeconvolver`) on the user's own image archive, shipped as versioned ONNX models through
the existing `TianWen.AI` / `TianWen.AI.Imaging` stack; a third backend tier alongside RC-Astro
(paid, licensed) and SAS AI4 (free fallback).

Scope boundaries settled up front:

- **Training is offline, on our side** (Python/PyTorch on rented GPU). Customer machines run ONNX
  Runtime inference only; **no Python, no on-device training** for the imaging models. (On-device
  online learning remains a NeuralGuider-only feature; its PPEC-style per-rig adaptation is out of
  scope here.)
- **Star removal (SXT-analogue) is in scope as a LATER phase** (P4, after denoise/deconv prove the
  pipeline) via the inject-and-remove bootstrap (§2.5). Croman's "hand-edit is the only way" holds
  only when you need ground truth for *existing* stars; synthetic injection gives exact truth by
  construction. Until its eval gates pass, `IStarRemover` stays on RC/SAS, but a TianWen remover
  is required for the tier to run the full canonical program (the starless plate is the workhorse
  intermediate: `RemoveStarsStep`, `--split-plates`, star/starless dual stretch).

## 1. Licensing constraint (load-bearing, read first)

The RC-Astro EULA (`C:\Program Files\RC-Astro\CLI\LICENSE.txt`, §10) **explicitly prohibits** using
the Software *or its outputs*, "directly or indirectly, to create, train, fine-tune, test, benchmark
for replication purposes, distill, validate, improve, or otherwise develop any machine learning
model … intended to replicate, emulate, compete with, or perform functions substantially similar",
naming *"the creation of training datasets or paired input/output datasets derived from the
Software's operation"* verbatim. The originally-floated "rc-astro as batch oracle for golden images"
is therefore off the table, as is using RC outputs for **validation or benchmarking** of our
models.

The same section carves out *"lawful independent development of competing technologies … developed
without use of the Software, its proprietary components, or outputs"*. This plan is built entirely
on that carve-out:

- **RC-Astro outputs appear nowhere in the training, validation, or metric loop.** RC remains
  what it is today: the preferred runtime backend for *processing images*.
- **SAS AI4 model outputs are also excluded from the ML loop** until their license terms are
  verified (open question #1); assume the same restriction by default.
- All ground truth is derived from the user's own raw data (stacks, sub-pairs) or from synthetic
  degradation with published math. This is not a workaround; it is the scientifically stronger
  approach (real sensor noise, real optics) and it enables a claim RC cannot make (§7 photometric
  integrity).

## 2. Training-data strategy (no oracle needed)

### 2.1 Denoiser ground truth: the archive already contains it

- **Noise2Noise (primary):** two registered, calibrated subs of the same target are two independent
  noise realisations of the same signal. Training input = sub A tile, target = sub B tile
  (same footprint, same session). Expectation over pairs equals the clean signal; **no clean target
  needed at all**, no input↔target noise correlation, and the pair count is combinatorial in subs.
- **Stack-as-truth (evaluation + optional supervised mix):** the session's integrated master is the
  low-noise reference for held-out metrics (PSNR/SSIM vs master). As a *training* target it slightly
  correlates with each contributing sub (1/N of its noise); with N ≥ 20 subs this is acceptable for
  a supervised mix-in, but N2N stays primary.
- **Synthetic noise augmentation (secondary):** degrade master tiles with the electron-domain noise
  model already calibrated in `SyntheticPlanetRenderer` (shot noise Poisson in e⁻, read noise in
  quadrature, `aduPerElectron = maxAdu / fullWell`) using per-camera gain/full-well from FITS
  headers. Widens the noise-level distribution beyond what the archive naturally has.

### 2.2 Deconvolver ground truth: synthetic PSF degradation

- Input = sharp master tile convolved with a synthetic PSF; target = the undegraded tile;
  **electron-domain noise is added AFTER the blur on every pair** (never optional, deconvolution
  is ill-posed and noise amplifies under inversion, so noise-free pairs train a brittle sharpener).
  PSF family: Moffat (β 2.5–4.5) with FWHM swept over [1, 8] px, elongation/PA, coma term, optional
  linear guiding-smear kernel, and **position-varying**: P0 measures the archive's FWHM/
  ellipticity/PA distribution **binned by field radius** (`FindStarsAsync` centroids give star
  positions; fast-lens corners genuinely differ from center), and per-tile degradation samples
  aberrations from the measured field-position distribution instead of one stationary kernel.
- **Space-truth tier (experiment, above the own-masters baseline):** own masters are seeing-limited
  (FWHM ~2–3 px), so they teach only relative sharpening toward their own ceiling. Public HST/JWST
  FITS from MAST (public domain / CC-BY, degrading *public archive data with our own measured PSF
  family* is fully independent development; HST/JWST truth is *reported* as BXT's approach in
  RC-Astro's FAQ and secondary coverage, NOT stated in the 2022 AIC talk, which predates BXT, and
  our justification is independent of what BXT did) become sharper truth: downsampling to the rigs' 1–3"/px scales crushes HST noise, yielding effectively
  noiseless linear truth at our sampling. Domain gaps (filter sets ≠ OSC RGB) are handled by
  luminance/per-channel training, PSF inversion is near-achromatic, and the tier is adopted only
  if it beats the baseline on the pinned split.
- **PSF conditioning mirrors the SAS conditional model exactly:** a second scalar ONNX input
  `psf01 ∈ [0,1]`, log2-encoded over [1, 8] px; the *same* encoding `HfdPsfEstimator` already
  produces and `OnnxIoNames.ImagePlusScalar` already classifies. Our model becomes a drop-in for
  `OnnxNonStellarDeconvolver` (different model file, same two-input signature).
- **Two range facts about that encoding, both measured 2026-08-11, both easy to get wrong.** The
  `[1, 8]` px is a **radius**, so (a) a FWHM sweep of [1, 8] px is a radius sweep of [0.5, 4] px and
  only reaches `psf01` 0.667, leaving the top third of the conditioning range untrained while
  `HfdPsfEstimator` can legitimately emit 1.0, and (b) the **bottom** of the range is above what a
  wide-field undersampled rig delivers: the archive's own frames measure FWHM ~1.8 to 2.4 px, i.e. a
  radius of 0.92 to 1.22 px, so everything below 2.0 px FWHM **clamps to `psf01` = 0** and becomes
  indistinguishable to the model. So sweep to FWHM 16 px, and give TianWen's own contract a lower
  floor than 1.0 px; only the SAS drop-in signature is pinned to `[1, 8]`.
  (These numbers postdate the FWHM estimator fix below and are not comparable to earlier ones.)
- **The FWHM measurement was quantised until 2026-08-11 and the P0 report is stale because of it.**
  `Image.AnalyseStar` derived FWHM from the area of the above-half-maximum sample COUNT
  (`2*sqrt(n/pi)`), so it could only return lattice values ~0.2 px apart, which put the per-sub median
  at the identical 2.523 px across the 5th to 75th percentile of 5,984 subs and flattened the
  field-radius profile on four of five optical trains. It is now an interpolated radial half-maximum
  crossing (`Image.HalfMaxDiameter`), continuous and ~0.5 px lower on undersampled stars (a
  unit-area-per-sample count over-estimates a half-max disc smaller than a pixel). The tiles are
  unaffected. **Re-measured 2026-08-11** (`--force-psf`, all 50 sessions): `Bins[].Fwhm` is now
  206,432 distinct values of 209,578 with only 1.0% left on the old lattice (was 100% by
  construction), p5 2.146 / p50 2.947 / p95 4.004, and `SubFwhm` 5,961 distinct of 5,984. So the
  store is calibrated for the sweep. `--force-psf` exists because the ordinary regen only FILLS
  GAPS, which cannot correct a record that is present and wrong.
- **One lens under two `TELESCOP` spellings was two optical trains, so it had two weaker profiles.**
  The archive recorded the Samyang 135 as both "SAMYANG 135mm" (3 sessions) and "Samyang 135 f/2 ED"
  (35), and since the field-radius profile is measured PER TRAIN the sweep was being calibrated from
  a 3-session profile that disagreed with the 35-session one most where it had least data (inner bin
  3.411 px on 1,293 stars against 3.052 px on 11,422). `TelescopeAliases` merges them at REPORT
  RENDER time, never on the way into the store, so the store keeps what the headers said and a bad
  alias costs a re-render rather than a re-measure. Merged: 38 sessions / 4,327 subs / 184,694 stars,
  and the report names the spellings it folded together, because a merge that changes a train's
  session count has to be visible in the artifact.
  **The alias touches the NAME only, never the focal length.** The same archive holds
  "WO ZS61 @ 288mm" and "WO ZS61 @ 360mm": one scope behind a 0.8x reducer and behind a
  flattener claiming 1x (360 x 0.8 = 288). A reducer changes exactly the off-axis aberration this
  profile exists to measure, so those stay separate trains and a name-only collapse would merge
  them.
- **`tianwen dataset report --out <dir>` re-renders the report from `stats/psf-sessions.jsonl`**
  with no archive scan, nothing re-measured and no tile touched (~seconds). It exists because the
  report is derived state whose INPUTS change without the measurements changing: an alias, a
  rendering fix, a re-tuned bin count. A normal run filters the report to what the current
  discovery found, which is the only reason it walks ~19k FITS headers first; report-only takes its
  session set from the tile manifest instead, which is the record of what was actually exported. A
  sibling command rather than a `build` flag because `build` requires `--archive-root` and a
  re-render must work with the archive unmounted. To re-MEASURE it is still `build --regen-psf`
  (fills gaps) or `--force-psf` (replaces records), both of which re-register.
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
[astro-archive-survey.md](astro-archive-survey.md): read it before starting P0. Summary: ~83,500
files / ~1.96 TB across `D:\Astro-Pics` (primary) + `D:\BobbyBox-Temp` (working tree, partially
duplicating 2024–mid-2025, plus unique Aug–Nov 2025 sessions). The **recent/good band (2024–2026)**;
ASI533MC Pro (RGGB), ASI585MC Pro, SVBONY SV605CC (GRBG), one ASI1600MM mono session, consistent
N.I.N.A. headers, per-session BIAS/DARK/DARKFLAT/FLAT; holds an estimated **~20,000–24,000
candidate raw lights** before quality filtering. Older eras (2021–2022, ASI294/QHY178m, ~668 GB)
are lower value; SER/planetary and CR2/DSLR are excluded in v1.

**Step 0 (archive organization, before any builder code):** `tools/astro-archive-dedup.py`; a
READ-ONLY scanner producing a resumable per-file header index (`fits-index.jsonl`) plus three
reports: `dup-files.csv` (exact-dup groups, identity = camera + `DATE-OBS` + exposure + dims,
hash-confirmed; cross-root flagged), `nights-rollup.csv` (per camera/night light counts split
dup/unique, the "what in BobbyBox is actually new" answer), and `calibration-coverage.csv` (per
light group: matching darks/bias found anywhere in the archive, because **calibration masters are
shared between sessions**, per-session folders cannot be assumed). Any physical
extraction/filing of BobbyBox uniques into Astro-Pics happens as a user-reviewed step from these
reports; the script itself never moves or deletes anything.

**Declaring a filter the capture software never recorded (`.tianwen-meta.json`).** N.I.N.A. models a
motorised filter wheel and writes its slot name to `FILTER`; it does not model a filter screwed onto
the nosepiece by hand, so those frames carry no `FILTER` card at all. That is the worst case for
grouping, since a manual holder is how a dual-band usually goes onto an OSC: the frames that most
need separating from the broadband ones are the ones with nothing to separate them by. Drop a file
next to them:

```json
{
  // screwed onto the nosepiece, NINA has no wheel configured
  "filter": "Antlia ALP-T"
}
```

- **It cascades like `.gitignore`**: a file applies to its directory and everything beneath it, and
  the nearest file wins wholesale. Put it on the session directory and both `LIGHT/` and `FLAT/`
  inherit it. Resolution never escapes the archive root, so two roots cannot leak into each other.
- **Fill-only, never override.** A frame that recorded its own `FILTER` is left alone, so a
  declaration can never relabel a frame that told the truth about itself. Correcting a header that
  is present but wrong is a different job and wants a deliberate rewrite of the file.
- **Applied at the frame source**, so lights and their calibration frames learn it together. This is
  not a detail: `CalibrationResolver.BestFlat` scores a filter mismatch at +1000, so giving the
  lights a filter while their flats kept none would be worse than leaving both blank.
- **A recognised name canonicalises** exactly as a recorded one does, so a declared `"Ha"` and a
  wheel-written `"Ha"` group and calibrate together. Anything unrecognised stands as its own
  identity, which is all grouping needs.
- **Nothing is silent.** `tianwen dataset build --discover-only` prints a filter census
  (`[dataset] filters: HydrogenAlpha x412, (no FILTER header) x1240, ...`), which is how you find
  out a night needs a declaration, followed by what the declarations did, including a count of files
  that parsed but changed nothing (usually a misplaced file) and of files that failed to parse. A
  malformed sidecar never aborts a sweep and never passes unnoticed.

Comments and trailing commas are accepted, since the file is written by hand. Filter is the only
field so far; the shape allows more.

Load-bearing hazards for the dataset builder (all detailed with examples in the survey doc): (1)
dedup across Astro-Pics ↔ BobbyBox-Temp *and* within Astro-Pics itself, content-hash + `DATE-OBS`
pass (Step 0's `dup-files.csv`); (2) never ingest `AutoSave`/`PROC`/`pixinsight`/XISF processed intermediates, gate on
`IMAGETYP='Light'` + `EXPTIME` in [10, 300] s from headers, not folder names (BobbyBox-Temp
especially: raw subs and XISF intermediates share the same session folders), **and exclude
simulator cameras by `INSTRUME`** (Step 0 found 139 "Camera V3 simulator" lights from a
2024-03-15 N.I.N.A. test session; synthetic frames would poison the noise model); (3) mixed Bayer
patterns (RGGB vs GRBG) and mono+FILTER sessions need per-camera debayer; (4) `2026-02-20 BAD LIGHT
EXAMPLES` (33 hand-flagged bad frames) is a free validation set for the quality gate; (5) 39+4
`.7z` archives (~100 GB, mostly pre-2023/planetary, already out of v1 scope) are invisible to a
folder scan unless extracted; watch for new ones under future 2024+ sessions.

### 2.3b Running it again (facts from the 2026-07-15 run, re-measured 2026-08-03)

Four things that are not inferable from the code and cost real time to rediscover.

**Root order is `2025` and `2026`, never the Vela tree first.** The Vela panel folders are a
user-made reorganisation of nights that also live under `2026\` by date, and step 0 then hard-linked
the copies, so 2,822 frames are shared between the two trees. The copy was **partial per night**, so
neither tree is a superset. Measured both ways over `2026` + Vela:

| first root | sessions | lights kept | too-small |
|---|---|---|---|
| `2026` | 39 | 4,406 | 1 |
| Vela | 43 | 4,376 | 9 |

Vela-first strands each night's un-copied remainder in the date tree as fragments (`2026-01-05/Panel 6`
42 lights beside the Vela copy's 132, `2026-02-10 HD 71526` 13 beside 143), nine of which fall under
the 10-sub floor, losing 30 lights. Dedup drops the same 2,822 either way, so root order only decides
which path names the session. The panel names read better and cost frames.

**The Vela tree adds about 450 lights, not thousands.** Genuinely unique to it: `2025-12-17 Panel 1`
Vela SNR 137 (gain 252, the only one), `2025-12-27 Panel 2` RCW 32 146, and `2026-02-20 P15 HD 70414`
x2 at 56 + 54. Plus 60 `Vela SNR` lights under `2026-01-23` that the 2026-07-15 run's
`--exclude-object *vela*` removed from the date tree. Anything sourced from
`fits-index.jsonl` overstates this: that index predates the hardlink pass and is stale.

**Two things must stay out of a training run**, neither excluded by any header gate:
`2026-02-20 BAD LIGHT EXAMPLES` (33 frames, reserved as the quality-gate validation set) and
`2026-02-20 SW8Q ...` under camera `QHY294PROC` (3 sessions, 193 lights at gain 1600, and `PROC` in
the camera name says what it is).

**Filters do not perturb the existing split.** All 11,554 lights in the built set carry no `FILTER`
card, so the filter component of `ImagingSession.Id` stays empty for every one of the 45 and the
stable hash buckets do not move. `test-sessions.txt` remains valid as written. The archive's 618
FILTER-bearing lights are all in the older mono era, outside this set.

### 2.4 Dataset builder (`tianwen dataset build`, new CLI subcommand)

**CLI contract; no machine specifics in the tool.** The command ships to every user, so nothing
in the repo encodes this machine: archive locations are **required** parameters (`--archive-root`,
repeatable; `--out`), with fail-fast errors instead of defaults pointing anywhere. Behavioural
knobs are parameters with *portable* defaults: exposure gate (`--min/--max-exposure`, default
10/300 s), instrument exclusion (`--exclude-instrume`, default `*simulator*`; generic, not a
camera list), tile size/cells/subs-per-cell, split-file path. Machine-specific invocations live in
the operator's own runner scripts outside the repo (the `run-archive-step0.ps1` pattern) or in
docs as examples only. The step-0 python helpers already conform (paths appear solely in docstring
usage examples); keep that bar.

C# tooling in-repo, reusing existing Lib machinery end-to-end (scan `FitsFolderFrameSource` →
dedup → quality gate → calibrate via `MasterFrameBuilder` → debayer → register subs to the session
master → tile export). **Calibration is resolved by header match across the whole archive, never by
session folder** (confirmed 2026-07-11: dark/bias libraries are shared between sessions); the same
`MasterGroupKey`-style identity (camera, exposure, gain, binning, ±temp) the stacker already uses;
Step 0's `calibration-coverage.csv` is the coverage map. **Masters are built once per
`MasterGroupKey` group and cached on disk** (the `StackingPipeline.BuildMastersAsync` mechanism,
a shared dark library serving five sessions is one group, one master), with two builder-specific
requirements: the cache dir is **one shared archive-wide location** (not per-run `outputDir`, so
build-once holds across all ~180 sessions and re-runs), and the cached master carries an
**input-set fingerprint** (frame count + content hashes stamped into its header) so a grown dark
library invalidates its stale master instead of silently cache-hitting on the slug. Foreign
pre-existing masters (PixInsight XISF / DBE'd TIFs in `PROC` dirs) are deliberately **not**
reused; unknown rejection/scaling provenance breaks the "pure function of inputs" cache trust;
raw calibration frames exist for essentially every 2022+ session, so rebuilding is cheap and
reproducible. Per session:

- **Quality gate** per sub: `FindStarsAsync` star count + median HFD + ellipticity, thresholded
  *relative to the session median* via the shared `FrameQualityFilter` (MAD-based; absolute
  thresholds don't transfer across focal lengths); drops cloud/trailing/defocus frames.
  **Measured finding (2026-07-11, ASI533MC Pro on a Samyang 135 f/2, BAD-LIGHT vs healthy session):
  star count is the load-bearing metric, not HFD/ellipticity.** On a fast refractor ellipticity is a
  rig constant (~0.56 for good AND bad, corner elongation), and HFD *inverts* under transparency
  loss (clouded frames read a LOWER median HFD, 2.0 vs 2.8, because only bright tight cores survive
  detection), so a naive HFD gate would rank clouded frames as *sharper*. Transparency collapse
  shows as a star-count drop (bad median 1261 vs good p10 2671), caught by the left-tail
  `StarCountTooLow` check. HFD/ellipticity are retained for rigs/failures that do move them
  (defocus, tracking) but are not this archive's discriminator. The keep-floor is raised from
  stacking's 0.20 to 0.50 (`QualityMaxRejectFraction`); purity over yield, since there are 20k+
  subs to draw from. **Not a catch-all**: ~3 of the 33 hand-flagged bad frames are metrically
  identical to good ones (normal star count + PSF, bad for reasons no PSF metric sees: satellite,
  gradient, the last clear frame before clouds); those survive by design. The exit criterion is
  "rejects the transparency/focus-bad frames, keeps the good ones", not "100% of hand-flagged".
  **Validated (cached-metrics test):** mixing the 33 bad frames into the full 400-frame healthy
  session at a realistic 8% minority, the gate rejects 30/33 bad (the 3 survivors are the
  metrically-good ones) and trims ~8% of good frames, and that good-frame rejection is *principled*
  (the rejected good are the measurably softer/thinner tail, not random), which for a training set
  is a purity feature, not a loss. Yield/purity note: this trims the softest ~5-10% of otherwise-fine
  frames; acceptable given 20k+ subs, and desirable for deconv (sharper master truth).
- **Fixed tile grid per session** (256 px cells on the master's frame, sampled ~200–400 cells biased
  toward structure by local signal): every exported sub tile and the master tile share exact
  footprints, so any two subs' cell (i,j) is an N2N pair and the master's cell (i,j) is eval truth.
  Export the master tile + a random 4–8 subs per cell (not all subs, bounds dataset size).
- **Output**: fp16 tiles (npy-compatible raw blobs) + a JSONL manifest per tile: source file,
  session id, camera, gain, exposure, tile coords, per-tile noise σ (MAD).
  Manifest rows written via a **canonical sort before any sampling** (parallel writers break every
  downstream seeded operation otherwise).
- **FWHM is deliberately NOT in the manifest** (the `SessionMedianFwhm` column was dropped
  2026-08-12). Condition on `median(SubFwhm)` from `stats/psf-sessions.jsonl`, joined on
  `SessionId`. The manifest row is written once, at export time, and a session that resumes with
  its tiles intact never rewrites its rows, while the PSF store is rewritten independently
  (`--force-psf` rewrites only the store). So the column survived the estimator change carrying
  quantized values for 45 of 50 sessions, looking authoritative while being wrong, and not as a
  uniform offset that could be scaled away. One fact, one writer: the store.
- Budget: ~60 sessions × ~300 cells × ~9 tiles ≈ 160k tiles ≈ **50–80 GB**; one upload to a cloud
  volume, regenerable from scratch by re-running the command.

**Zero train/inference skew (non-negotiable):** the tile exporter calls the *same* code the
inference path uses; `AiNafnetInputs` MTF pre-stretch (target median 0.25, auto-skip threshold
0.125), `[0,1]` linear convention, `ChunkedInference`-compatible geometry. Python never
re-implements preprocessing; it consumes tiles as-stored. A `parity-check` diff (export N tiles,
run the C# stretch and the stored bytes side by side) pins this in CI-able form.

### 2.5 Star-removal ground truth: inject-and-remove bootstrap (P4)

Ground truth for *existing* stars would need hand editing; ground truth for *injected* stars is
exact by construction. Four stages, fully license-clean (own data + own synthetics + own model):

1. **Classical bootstrap starless plates**: PSF-fit subtraction at `FindStarsAsync` detections +
   multi-scale inpaint. Imperfections are acceptable; residual artifacts become background the
   net must *preserve*, never content it must invent.
2. **Synthetic star injection** onto those plates, drawn from the archive's measured PSF
   distribution (same P0 stats that calibrate the deconv sweep): Moffat cores, lens halos,
   saturation/bloom for the bright tail. Input = plate + injected stars, target = plate.
   **Injection positions must be uncorrelated with the classical-removal residual sites**
   (Croman's "the network will faithfully learn all of your mistakes"): if injected stars
   preferentially land on inpaint artifacts, the net learns that removing a star reveals
   artifacts; random placement keeps the truth under injected stars overwhelmingly clean
   background.
   Advantage of this archive: all optics are refractive (Samyang 135, ZS61, FMA180, SH61), no
   spider vanes, no diffraction spikes, so the morphology distribution is far narrower than a
   general-purpose remover must handle.
3. **Self-refinement loop**: run the trained net on real images → better starless plates →
   re-inject → retrain. Distills only our own model.
4. **Output contract** matches `RemoveStarsStep`'s additive split (stars = input − starless).

Eval is objective because injected truth is exact: removal completeness on injected stars,
pixel-level background preservation under them, flux conservation of the stars plate; plus
existing-star spot checks at 1:1 (the bright-saturated tail is the known hard case, keep RC/SAS
preferred until it passes).

### 2.6 Gradient-removal ground truth: flatten-and-inject (P5)

A trained gradient remover (the GraXpert analogue) fills `IGradientCorrector`, the one enhancer
role every other phase leaves on SAS permanently (RC-Astro has no gradient product), so together
with P4 it completes the TianWen-only tier. It is also the easiest truth of the four model types:
a gradient is an additive low-frequency field in linear space, so exact ground truth is
manufactured rather than measured. Captured 2026-08-11; independent of P1/P2/P4 (it needs only
P0's calibrated sessions, registration optional) and far cheaper to train, so it can run whenever
a slot opens.

1. **Flatten classically**: star-masked low-order polynomial / RBF background fit on the session
   master (the `StarMask` + `ScanBackgroundRegion` machinery already exists), subtract. The plate
   does not need to be perfectly gradient-free, only consistent: exactly as with §2.5's starless
   plates, residual flattening artefacts become background the net must preserve, never content it
   must invent.
2. **Inject a synthetic gradient**: input = flattened + gradient, target = flattened (the
   subtraction formulation keeps §7's flux gates directly applicable). Family: low-order 2D
   polynomials, edge light-pollution ramps, corner glows, plus the physically-modelled moon-scatter
   surface below. Amplitudes and orientations sampled from the measured archive distribution, not
   guessed.
3. **Measure first (the P0-equivalent)**: fit low-order star-masked backgrounds to every session
   master and report amplitude relative to sky level, orientation, and shape percentiles; a sibling
   section to the PSF report, same measure-then-sweep pattern as §2.2.

**Physically-derived covariates (moon geometry + frame rotation) earn their keep three ways.**
Every frame's header carries `DATE-OBS` + site + pointing, the ephemerides are in-repo
(`MeeusMoon` position + illumination, `VSOP87a` sun), and one `CatalogPlateSolver` solve on the
session master supplies the WCS rotation that projects sky directions into pixel coordinates. So
per-sub moon-target separation, moon altitude, illumination, target altitude/azimuth, and the
in-frame direction of any sky-anchored ramp are computable offline AND at inference, from the FITS
header alone, with no user input:

- **Conditional sampling**: regress the measured gradient distribution against the covariates so
  injection samples conditionally (moon up and close draws a strong directed ramp toward the
  projected moon azimuth; a moonless dark-site frame draws the weak altitude/airmass ramp) instead
  of isotropically.
- **A physical family member**: the Krisciunas & Schaefer (1991) moonlight sky-brightness model
  evaluated over the frame footprint yields a realistic non-linear moon-scatter surface, better
  than any polynomial at small separations.
- **Optional inference conditioning** (the psf01 pattern): scalars the net receives so it can tell
  an expected gradient direction from genuine large-scale nebulosity, which is the core ambiguity
  of this model class. GraXpert cannot do this (no ephemeris, no header contract); we can. Two
  caveats: clouds break the physics (covariates are inputs the net may weigh, never a subtraction
  the pipeline asserts), and multiple scalars need a new `OnnxIoNames` signature
  (`ImagePlusScalar` carries exactly one).

**A site LP prior: site-specific does not mean unknowable.** The full-sky light-pollution field is
not linear, but its restriction to a <= 5 deg FOV is locally linear-to-quadratic, and for a fixed
site it is quasi-static in HORIZON coordinates. Static field plus varying pointing means the
archive itself can fit it: per-sub linear background fits (moonless frames, or the Krisciunas &
Schaefer moon term subtracted first) regressed against pointing alt-az and time-of-night, with a
per-train PIXEL-fixed term alongside the sky-anchored one. The two components separate naturally
because pointing varies across sessions while the sensor does not, so the pixel-fixed term both
absorbs and incidentally *measures* residual flat error, a free diagnostic. Once fitted, per-frame
LP prediction IS computed (pointing + WCS rotation + clock), which buys the two things §2.6 needs:

- **Simulation**: injected LP ramps take the direction and relative amplitude the site model
  predicts for that frame's pointing, so the synthetic LP population matches the site's actual
  dome geometry instead of an isotropic guess.
- **A real-frame validation gate, the one synthetic injection cannot provide**: on held-out REAL
  frames, the component the model removes must agree in direction and relative amplitude with the
  predicted LP; systematic disagreement (wrong direction, under-removal along the predicted axis)
  fails the gate. Injected-gradient eval only ever tests synthetic truth; real frames otherwise
  have no truth at all, so this is the only physics-anchored check on them.

Error bars are part of the prior: LP varies with hour (curfews), season, and aerosols, so the fit
is a distribution and the gate is a consistency check, never an exact-subtraction assert. Clouded
frames would poison the fit (clouds amplify LP several-fold) but the P0 quality gate already drops
them. A site with too few frames can bootstrap the prior from public VIIRS upward radiance through
a Garstang-style propagation model. VIIRS is public domain; the Falchi 2016 atlas is CC BY-NC and
stays out of anything shipped, **and the reason is not whether SharpAstro is commercial**: TianWen
is AGPL-3.0-or-later, which grants every downstream consumer commercial-use rights (copyleft
constrains secrecy, not commerce), so NC material inside a shipped artifact would be a grant we do
not hold. Internal dev-side use (fitting, eval, cross-checking our own prior against the atlas) is
non-commercial and fine.

**The P0 tiles are the wrong artifact for this model; it needs its own exporter.** A gradient is a
whole-frame low-frequency phenomenon, so a 256 px native-res crop of a 3008 px sensor is a
near-constant offset with no context, and the stored tiles are MTF pre-stretched while gradients
are additive in linear. The training sample is the whole calibrated frame downsampled to model
resolution (GraXpert-style): predict the background at low res, bicubic-upsample, subtract at full
res. Full-res pixels never pass through the net, so the hallucination class of §8.4 is
structurally absent here. Masters and calibrated subs both qualify as scenes; flats have already
removed vignetting, so a calibrated sub's residual background is sky gradient, exactly the target,
and each sub is its own realization as altitude and moon geometry drift through a night (5,984
registered subs vs 50 masters in the current set).

**Why this archive is unusually good for it**: the classic gradient-AI failure mode is eating real
large-scale nebulosity, and models trained on mostly-empty fields are worst at it. This archive is
dominated by 135 mm fields where Ha covers most of the frame (Carina, Vela SNR, Orion, Rim), so
training pairs where the injected gradient is known and the underlying nebulosity must survive are
exactly the discrimination signal.

**Licensing**: GraXpert is GPL-3.0. Since TianWen went **AGPL-3.0-or-later** (2026-08-11), section 13
expressly permits combining with GPL-3.0 material, so vendoring its code would be lawful where it once
was not. The preference still runs the other way, for the reasons in narrowband ADR-2: a vendored part
stays GPL-3.0 and needs that tracked forever, and a Python-to-C# port is most of the work regardless.
**Its weights are a separate matter and stay out**, since a model trained on someone else's data
distribution is not what this plan is for, and its outputs are never training targets (unnecessary
anyway, the synthetic truth is exact). The held-out split stays by session, unchanged.

## 3. Model + training

- **Architecture: NAFNet, width 32, standard block config ≈ 29 M params** (the SXT 21M / NXT 24M /
  StarNet-V2 30M league; ~115 MB fp32 ONNX, same class as the SAS AI4 files the runtime already
  handles). Capacity tuning goes DOWN via middle-block count on pinned-split ablations. Croman
  ("capacity saturated"), Topaz competing at 14M, and our narrower single-user domain all say >30M
  needs evidence, and width-64 (~116M) is off the table (4× the customer download for nothing).
  Same family as the SAS AI4 models, so the stride-16 / tile-256 / overlap-64 constraints of
  `ChunkedNafnetRunner` hold by construction. Denoiser: 3-channel in/out (mono handled by the
  runner's channel-tiling, as `OnnxStellarSharpener` does). Deconvolver: image + `psf01` scalar.
- **Strength control comes free:** train full-strength models; `SharpenPipeline` already applies
  per-step `Blend` as a post-hoc `Image.Lerp` toward the source, that *is* the user-facing strength
  slider. NXT-style per-frequency knobs are explicitly deferred.
- **Losses:** L1 (MAE) primary + MS-SSIM auxiliary; plus a **flux-preservation regulariser**
  (per-tile mean/aperture-sum penalty); see §7. **Adversarial/GAN losses are deliberately
  excluded**: they optimise for plausibility, which is hallucination pressure; directly opposed
  to the photometric-integrity gates. If perceptual quality ever needs a boost, prefer feature
  losses with the flux regulariser as a hard constraint.
- **Mask a `StitchBorderPx` border out of every loss, and read the constant rather than picking a
  number.** This is a train/inference asymmetry the tile format cannot fix, so it has to be built into
  the loss from the first run. At inference the outer **16 px** (`AiNafnetInputs.StitchBorderPx`) of
  every 256 px chunk is discarded and never reaches the output: `ChunkedInference.Stitch` sums only
  each chunk's central 224x224 and averages the overlaps, precisely because that rim is where NAFNet's
  tile-edge artefacts live (SAS Pro's `stitch_chunks_ignore_border(border_size=16)` does the same).
  A loss taken over the full 256x256 training tile therefore spends capacity on a border condition the
  model never meets: at the tile rim its receptive field runs off the crop into whatever the framework
  pads (zeros, typically), whereas at inference that neighbourhood is real pixels from an adjacent
  chunk. Masking the rim costs 25 % of the tile's pixels per sample and removes the mismatch.
  **The tiles themselves need no change** (the exporter never clips or zero-pads: cells are required
  to lie wholly inside `StatsRect`, and a star bisected by a tile edge is bisected identically in the
  master and its subs, so the loss never asks the model to invent the missing half).
- **Optimisation:** AdamW, cosine schedule, grad-norm clip 1.0, early stop on held-out val, seeded
  end-to-end. Mirrors the Croman talk's recipe and prior in-house ML-pipeline experience.
- **Discipline (proven in-house ML-pipeline patterns, adopted wholesale):**
  - `training/EXPERIMENTS.md`: every run logged, ablations base-vs-+change on the **pinned split**,
    negative verdicts recorded to stop re-litigation.
  - **Pinned held-out split by SESSION** (never by tile/frame, adjacent tiles leak noise/PSF stats
    exactly like words leak page layout), committed as a flat `test-sessions.txt`.
  - **ONNX-vs-torch parity check** in the export step (run both on N val tiles, assert max prob…
    pixel delta ≤ tolerance) before any artifact is promoted.
  - **`<model>.contract.json` provenance stamped into the artifact set**: tensor conventions
    (layout, stretch constants, psf01 encoding), dataset manifest SHA-256, git commit, package pins,
    ONNX SHA-256, timestamp; asserted at load time in C# (NeuralGuider's gate-and-refuse pattern,
    minus the delete: refuse + log + fall back to SAS).

### Infra

- Repo layout: `training/` at repo root (Python: `dataset.py`, `train_denoise.py`,
  `train_deconv.py`, `export_onnx.py`, `parity_check.py`, `requirements.txt`, `EXPERIMENTS.md`,
  `test-sessions.txt`). The venv bootstrap gets documented in `requirements.txt` comments.
- **Dev smoke runs: native torch + CUDA, no WSL** (measured 2026-08-10). The earlier note here said
  torch-CPU under WSL "no win-arm64 torch wheels"; that was laptop provenance and is wrong for the
  x64 dev box. Wheel coverage is a property of the host, so check the index for your interpreter
  rather than assuming either way. Measured working setup: GTX 1070 (Pascal, `sm_61`), 8 GB, driver
  582.66; `torch==2.13.0+cu126` lists `sm_61` in `torch.cuda.get_arch_list()`. **No system CUDA
  toolkit is required to train**, because the wheel bundles its own CUDA runtime and cuDNN (9.10.2);
  a toolkit is only needed to compile CUDA C++. If one is installed anyway it must be **12.4 to
  12.9**: CUDA 13.x dropped every architecture below `sm_75`, so it cannot target Pascal at all,
  and it is what a bare `winget install Nvidia.CUDA` gives you. Pin with
  `winget pin add --id Nvidia.CUDA --version "12.*"` so a routine upgrade cannot silently break it.
- **The local GPU cannot do a full NAFNet-32 run, and the reason is AMP.** The T4 sizing above
  ("16 GB VRAM fits NAFNet-32/64 @ 256 px with AMP") names the exact assumption Pascal breaks:
  tensor cores start at compute 7.0, and GP104 runs fp16 at 1/64 rate, so local training is locked
  to fp32 at a measured 4.85 TFLOPS. With 8 GB against 256 px activations on a 29 M-param
  restoration net, batch sizes collapse as well. Rented GPU stays the plan for full runs.
- **What the local GPU is for: de-risking the rented run before paying for it.** Two failures from
  the in-house ML pipeline whose discipline § 3 already adopts wholesale were both cheap to catch
  locally and expensive to catch remotely: one run lost at the export step because the legacy ONNX
  tracer baked a fixed sequence dim into reshapes (only the dynamo exporter keeps it dynamic), and
  another lost because early-stop patience was shorter than the cosine schedule, killing it while
  the LR was still hot. So validate locally first: the data path off `D:\Astro-Dataset\2025-2026`,
  the loop with checkpoint/resume, **the ONNX export plus its parity check** (the actual deliverable,
  since customers run ONNX), and a shrunk width-8/16 variant to prove convergence end-to-end. The
  rented job then becomes the same script with more GPU, not a debugging session at hourly rates.
  Do not extrapolate a local-vs-rented ratio from fp32 measurements: a 1.2 M-param fp32 transformer
  on this box ran 58 s/epoch against 31 s on a T4 (1.87x), but that gap widens sharply for a conv
  net where the rented GPU uses AMP and this one cannot.
- **Full runs: an internal AKS GPU dev pool as a k8s Job** (Tesla T4 16 GB, 4 vCPU / 28 GB,
  `nvidia.com/gpu: 1`; the workload-identity Job + blob-storage pattern is already proven
  in-house). 16 GB VRAM fits NAFNet-32/64 @ 256 px with AMP; T4 ≈ 3–5× slower than a 4090, so a full
  run is ~4–10 days; fine unattended with checkpoint-every-N-steps (restarts free). The 4-vCPU
  loader is not a bottleneck (tiles are pre-baked fp16).
- **Ablation sweeps (optional fast lane): RunPod Secure Cloud RTX 4090** (~$0.35–0.69/hr,
  per-second billing, network volume ~$0.07/GB/mo); ≈ 24–72 GPU-h ≈ **$15–50/run** when iteration
  speed matters; Vast.ai interruptible (~$0.13–0.37/hr) once checkpoint-resume is proven. Pull back
  only checkpoint + ONNX.
- Local Adreno/DirectML is for **inference smoke only** (the existing TianWen.AI path); Hexagon NPU
  is inference-only with no vision-model path (verified), neither trains anything.

## 4. Runtime integration (C#)

- New `OnnxTianWenDenoiser : IDenoiseEnhancer` and `OnnxTianWenDeconvolver : INonStellarDeconvolver`
  in `TianWen.AI.Imaging/Onnx/`: thin: model file names (`tianwen_denoise_color_v1.onnx`,
  `tianwen_deconv_nonstellar_psf_v1.onnx`) + contract assertion; the heavy lifting is the existing
  `ChunkedNafnetRunner` / `TensorImageConverter` / `OnnxIoNames`, reused verbatim.
- **Backend selection** extends the existing single source of truth: `EnhanceBackend` gains
  `ForceTianWen`; `EnhanceOptions.TryParse` accepts `--ai-backend tianwen`; the `Deferred*` proxies'
  Auto order becomes **RC (installed + licensed) → TianWen (model + contract present) → SAS**. RC
  stays first in Auto until our eval says otherwise; users opt in via the flag / viewer
  backend-cycle (right-click on the Enhance button already cycles backends).
- **Model distribution:** published as GitHub Release assets on the TianWen repo;
  `tools/tianwen-ai-models-fetch.ps1` gains a TianWen-models section **with SHA-256 verification**
  (the SAS fetch currently has none, ours sets the standard; contract JSON ships beside the model).
- **Safety gates (NeuralGuider learning, scaled to fit):** default position is "present but not
  Auto-preferred"; contract mismatch → refuse + fall back; NaN/Inf/out-of-range output check on the
  stitched result → discard + fall back to input (passthrough), log a warning. No perf monitor
  needed; this is a user-invoked batch step, not a closed loop.

## 5. Phasing

| Phase | Deliverable | Exit criterion |
|---|---|---|
| **Step 0: Archive organization** | `tools/astro-archive-dedup.py` READ-ONLY scan (header index + dup-files / nights-rollup / calibration-coverage reports); user-reviewed filing of BobbyBox uniques into Astro-Pics from the reports | Dup report reviewed; unique-to-BobbyBox sessions identified/filed; calibration coverage map exists (feeds P0's header-matched calibration) |
| **P0: Dataset + stats** ✅ SHIPPED 2026-07-12 | `tianwen dataset build` (scan/dedup/gate/calibrate/register/tile+manifest, zero-skew export; calibration header-matched archive-wide, never per-folder); archive PSF/noise distribution report; pinned `test-sessions.txt` | Tile set regenerable one-command ✅; gate rejects transparency/focus-bad frames (star-count-led; §2.4) ✅; parity check green (maxDiff 0, in-run gate) ✅. **Real-archive run DONE 2026-07-15**, regenerated post-pedestal-fix 2026-08-10/11 as `D:\Astro-Dataset\2025-2026-calgated` (50 sessions / 135,000 tiles + `psf-sessions.jsonl` covering all 50); see § 2.3b for root order and the two session groups that must stay excluded. P1 trains on the calgated set. |
| **P1: Denoiser v1** | `training/` N2N pipeline (loss masks a `StitchBorderPx` rim, §3); NAFNet-32 color run on RunPod; ONNX + contract; `OnnxTianWenDenoiser` + `--ai-backend tianwen`; eval report | Beats classical baseline + no photometric regression (§7) on held-out sessions; visually clean on 3 reference masters; **no tile-seam artefact** visible on a full-frame master (the border-masked loss is what this checks) |
| **P2: Deconvolver v1** | Synthetic-PSF pipeline (measured-distribution sweep); psf01-conditioned NAFNet; `OnnxTianWenDeconvolver`; eval incl. FWHM-reduction + artefact checks | Measured FWHM reduction on held-out masters without ringing/worms; photometric gates hold |
| **P3: Ship** | Auto-order wiring, fetch-script + release assets, CLI/GUI surfacing, `docs` + CLAUDE.md section | `stack --enhance --ai-backend tianwen` end-to-end on a fresh machine (models auto-fetched) |
| **P4: Star remover** | Inject-and-remove bootstrap (§2.5): classical starless plates + measured-PSF star injector + self-refinement; `OnnxTianWenStarRemover : IStarRemover` (additive split); completes the tier so the full canonical program runs TianWen-only | Injected-star removal completeness + background preservation + stars-plate flux conservation on held-out sessions; bright-saturated tail passes 1:1 spot checks (RC/SAS stay preferred until then) |
| **P5: Gradient remover** | §2.6 flatten-and-inject: archive gradient-distribution report (the measure-then-sweep P0-equivalent); per-site LP prior fitted in horizon coordinates (sky-anchored vs pixel-fixed regression, the latter doubling as a residual-flat-error probe); whole-frame downsampled LINEAR exporter (the P0 tiles are the wrong artifact, §2.6); moon/geometry covariates (`MeeusMoon` + `VSOP87a` + one WCS solve per session); small background-prediction net + ONNX; `OnnxTianWenGradientCorrector : IGradientCorrector` | Injected gradients on held-out masters removed to below the classical-fit residual baseline; on held-out REAL frames the removed component agrees with the site-LP prediction (direction + relative amplitude, consistency not exact-subtraction); nebulosity / large-scale flux preserved (§7 gates). Order-independent of P1-P4 (needs only P0) |
| **P6: Deferred** | Strength/frequency conditioning beyond Blend-lerp; mono-native models; drizzle-truth sharper tier; frame-quality classifier from BAD-examples; dataset-contribution flow for other users; **comet-registered stacking** (P4 unlock: star-remove subs → integrate on the `CometEphemeris`-computed per-frame comet position via WCS → recombine star-registered stars plate, the AIC comet workflow, automated by ephemeris instead of manual alignment) | - |

## 6. Evaluation (all internal, license-clean)

- **Held-out pinned sessions only** (never trained, never tuned against).
- Denoise: PSNR/SSIM vs session master; residual-noise σ (MAD) reduction; N2N val loss.
- Deconv: star FWHM before/after on held-out masters; structure metrics on nebulosity (local
  contrast without ringing); "worm"/hallucination spot-checks at 1:1.
- **No RC-Astro or SAS outputs in any metric.** Qualitative side-by-sides for a blog post are the
  user's call as an ordinary product comparison, never part of the automated loop.
- Human adjudication: a tiny local compare page (an in-house-learned lesson: blind A/B,
  don't score against "what the user kept").

## 7. The differentiator: photometric integrity

Croman's own stated con: AI-processed images "destroy the scientific value", flux and centroids
are not conserved, *"unless they were specifically trained to conserve star flux or conserve the
positions of star centroids"* (AIC talk, verbatim). That carve-out is this section: we train and
gate on exactly that:

- **Training:** flux-preservation regulariser (aperture-sum penalty over detected-star apertures +
  per-tile mean preservation).
- **Eval gates:** aperture-photometry delta < X% and centroid shift < Y px (thresholds set in P1
  from the classical pipeline's own repeatability) on held-out subs; a release-blocking gate, not
  a dashboard number.

This gives TianWen a claim neither RC nor SAS makes: an enhancer that is *measured* science-safe on
every release.

## 8. Risks / open questions

1. **SAS AI4 model license**: verify SETI Astro's terms before *any* SAS output touches the ML loop
   (default: excluded, same as RC).
2. **Master quality variance**: weak masters (few subs, poor night) as N2N eval truth understate
   quality; session gate (min sub count, min FWHM percentile) mitigates.
3. **GRBG (SV605CC) vs RGGB**: handled by the existing debayer path; verify no channel-swap in the
   tile exporter with a colour-target session.
4. **Deconv hallucination risk**: the classic failure mode; mitigations: conservative default
   Blend, psf01 conditioning (no blind deconv), artefact spot-check protocol in eval.
5. **~1/N noise correlation** in stack-as-truth supervised mix; N2N-primary makes this a non-issue;
   documented so nobody "optimises" it back in.
6. **Cloud spend discipline**: pinned-split ablation protocol keeps the sweep small; budget
   ceiling per phase agreed before P1 kicks off (~$100–300 total expected).
