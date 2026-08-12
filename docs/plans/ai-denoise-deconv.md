# TianWen-Trained Denoise + Deconvolution Models ("own AI")

**Status: P0 SHIPPED (2026-07-12); P1 DE-RISKED, full training run not yet made; P2+ NOT STARTED.**
"P1+ NOT STARTED" is what this line used to say, and it stayed there through the entire smoke
campaign: § 3a documents **eleven** completed N2N variants on the GTX 1070 and names **v8** as the
config, § 7 carries measured photometric gates, and the training environment is settled. What has
NOT happened is the full-scale run on the real tile set. Read "not started" as "no shipped model",
never as "no P1 work exists", or the eleven variants get repeated.
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

**Superseded in turn on 2026-08-12/13 by the drizzled rebake**, `D:\Astro-Dataset\2025-2026-drizzle`,
which is now **the training set**: **64/68 sessions, 197,400 tiles (70.7 GB), 64 retained session
masters (6.7 GB), parity OK**, in 4h34m. The 3 skips are sessions with no resolvable dark. It is the
full rebake the paragraph above deferred, so the registration skew it accepted is gone. Two properties
matter downstream. **The master integrator is gated per session and split 45 BayerDrizzle / 19
Float16Staged**, so any per-channel statistic read across the whole set averages two populations that
are not comparable; split on `MasterStrategy` in `stats/psf-sessions.jsonl` first. And **the held-out
split is pinned by a stable hash bucket of the session id** (`test-sessions.txt`, 6 sessions), so
adding sessions later never reshuffles it.

One caveat on its statistics, because it is invisible from the numbers: the bake launched at 20:17,
minutes before the per-channel field-radius bins and the retained-master read landed, so all 64
records in `psf-sessions.jsonl` carry the old single-channel `Bins` key and the field-radius profile
reads as covering 0 of 64 sessions until a `--force-psf` pass re-measures them. That pass reads the
retained masters rather than re-registering from the archive, so it costs minutes, not the ~7h the
original registration took.

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
  PSF family: Moffat with elongation/PA, coma term, optional linear guiding-smear kernel, and
  **position-varying**: P0 measures the archive's FWHM/ellipticity/PA distribution **binned by
  field radius** (`FindStarsAsync` centroids give star positions; fast-lens corners genuinely
  differ from center), and per-tile degradation samples aberrations from the measured
  field-position distribution instead of one stationary kernel.
  **The beta and FWHM ranges are now MEASURED, not assumed** -- see "The measured PSF profile"
  below, which supersedes the beta 2.5-4.5 / FWHM [1, 8] px this line used to specify. Three
  results change the sweep: beta runs ~2-12 and sits per train (7.95 Samyang, 2.05-2.70 ZS61);
  beta and FWHM are correlated (r = 0.66) so they must not be sampled independently; and each
  COLOUR CHANNEL needs its own PSF, since green is ~35% narrower than red archive-wide.
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
- **Measured FWHM depends strongly on how BRIGHT the star is, and that is the biggest single
  contaminant of the PSF numbers.** On real masters, pooling all radii, the median FWHM across
  peak-ADU deciles runs 2.613 -> 1.914 px (Lobster) and 2.909 -> 2.324 px (HIP 85088): faint stars
  measure roughly 25-30% WIDER than bright ones of the same field. Direction reproduced
  synthetically (3.841 -> 3.933 px as SNR falls 595 -> 29), and the mechanism is that the half-max
  level is set relative to the star's own peak, so any residual background left after subtraction is
  a larger FRACTION of a faint star's peak and pushes the crossing outward. **Anything fitting a PSF
  from detected stars must control for brightness**, or it fits a magnitude distribution.
- **Saturation was investigated and RULED OUT on this archive** (an earlier note here claimed it
  dominated; that was wrong). The synthetic effect is real and large -- on a 4.00 px star, clipping
  21 px reads 6.154 and heavy clipping 10.837 -- but the population is not there: only **0.1-0.2%**
  of detected stars are core-clipped, and excluding them moves the per-bin medians by ~0.001 px.
  The lesson is the one worth keeping: a large effect measured synthetically says nothing until the
  affected FRACTION of the real population is counted.
- **The centre-to-corner FWHM fall survives brightness control, and is session-dependent.** Holding
  peak ADU inside the 40th-60th percentile band: Rim Nebula still falls 4.032 -> 3.115 px, HIP 85088
  3.020 -> 2.731, but Lobster is flat (2.625 -> 2.514). Elongation explains part of it where
  ellipticity rises (the crossing is taken on an AZIMUTHALLY AVERAGED profile, so an elongated star
  reads near its geometric mean, worth ~0.089 px over the archive's 0.465 -> 0.536 range), but not
  HIP 85088, whose ellipticity is flat (~0.52) while its FWHM still falls. So the aggregate profile
  is mixing genuinely different per-session field behaviour, and a single archive-wide curve should
  not be treated as one optical signature.

#### The measured PSF profile (what an own-BlurX has to model)

**Surveyed across all 50 session masters** (2026-08-12). Per master: stack the azimuthally averaged,
background-subtracted, peak-normalised profiles of up to 400 isolated brightness-controlled stars,
then fit a Moffat `(1 + (r/alpha)^2)^-beta` in LOG space with alpha tied to the measured FWHM so the
fit is about SHAPE. Shipped as `PsfProfileFit`; every future bake records it per session.

| optical train | n | beta p5 | beta p50 | beta p95 | FWHM p50 | Moffat / Gaussian log-rms |
|---|---|---|---|---|---|---|
| ASI533MC Pro / Samyang 135 f/2 ED @ 130mm | 38 | 4.70 | **7.95** | 23.65 | 2.940 | 0.131 / 1.197 |
| SV605CC / SH61 EDPH @ 270mm | 9 | 3.00 | **6.85** | 24.95 | 3.226 | 0.266 / 1.614 |
| ASI585MC Pro / WO ZS61 @ 288mm (0.8x reducer) | 2 | 2.70 | **2.70** | 2.70 | 3.128 | 0.058 / 6.327 |
| ASI585MC Pro / WO ZS61 @ 360mm (flattener) | 1 | 2.05 | **2.05** | 2.05 | 2.950 | 0.070 / 10.712 |

All 50: beta p5 2.70, p25 5.80, **p50 7.85**, p75 9.65, p95 23.65. Moffat beats Gaussian in **48 of
50**. Wing flux at r = 2*FWHM relative to a same-FWHM Gaussian: p5 30x, **p50 98x**, p95 535x.

- **Fit the PSF in LOG space.** An unweighted least-squares fit on a peak-normalised profile is
  dominated by the core and effectively ignores the wings -- exactly what governs ringing and halo
  in a deconvolution. On the first three masters, plain RMS reported "Gaussian fits better" for two
  of them; in log space Moffat wins all three, by 4x to 15x. Same data, opposite conclusion, purely
  from the error metric. This is the most transferable lesson in this section.
- **A Gaussian PSF is the wrong function, not a rough one.** The median master carries ~98x the wing
  flux a same-FWHM Gaussian predicts at twice the FWHM.
- **beta is a PER-TRAIN property, and the spread is the finding.** The ZS61 sits at beta 2.05-2.70
  with wings 535-994x Gaussian, while the Samyang sits at 7.95 -- genuinely different optics, not
  scatter. Note the ZS61 also has by far the BEST Moffat fits in the survey (log-rms 0.058-0.070)
  and by far the worst Gaussian ones (6.3-10.7): it is very nearly a textbook heavy-winged Moffat.
  This independently vindicates keeping the 288mm and 360mm ZS61 configurations as separate trains.
- **The plan's assumed beta 2.5-4.5 is calibrated to the three rarest sessions.** It is about right
  for the ZS61 (3 of 50) and badly wrong for the Samyang and SH61, which are 47 of 50. Sweeping it
  would synthesise halos far heavier than 94% of the archive shows and train the network to remove
  a wing that is not there. **Sweep roughly beta 2-12, weighted toward 6-9**, or better, sample the
  measured per-train distribution directly.
- **beta correlates with FWHM: blurrier masters are MORE Gaussian** (Pearson r = 0.66 over all 50,
  and 0.635 within the Samyang train alone, so it is not a between-train artifact; Samyang FWHM<2.9
  gives beta median 6.45, FWHM>=2.9 gives 9.05). Physically sensible -- more seeing and tracking
  blur, and more frames averaged, convolve toward a Gaussian and wash out the sharp-core/heavy-wing
  structure. **So do NOT sample FWHM and beta independently in the sweep**: that generates
  combinations (a 4 px FWHM with beta 2.7) this archive never produces.
- Caveats: alpha is tied to the measured FWHM rather than fitted freely, so beta answers "how heavy
  are the wings at this width"; the upper tail is grid-limited (2 sessions pinned at the beta=25
  ceiling, and p95 23.65 should be read as "effectively Gaussian" rather than a number); the stack
  averages over field position and position angle, smearing elongation into the radial average; and
  this is measured on registered+integrated MASTERS -- the right target for master enhancement, but
  it includes resampling and seeing-variation blur rather than the per-sub PSF.
- **The table above is CHANNEL 0 ONLY, which is red, and red is the widest channel in 48 of 49
  masters.** See below; the sweep has to be per channel.

#### The PSF is per CHANNEL, and the spread is larger than the spread between trains

The table above was measured on channel 0 because the report sampled only channel 0. Re-measuring
all three channels on all 50 masters (2026-08-12, raw results in
`stats/psf-channel-survey-2026-08-12.csv`, 49 of 50 measurable -- Helix is too star-poor) shows the
channel choice moves the answer more than the optical train does:

| | FWHM p50 (px) | ratio to red | Moffat beta p50 |
|---|---|---|---|
| channel 0 (red) | 2.900 | 1.000 | 5.00 |
| channel 1 (green) | 1.875 | **0.648** | 7.00 |
| channel 2 (blue) | 2.314 | 0.799 | 4.50 |

Green is narrower than red in **48 of 49** masters, blue in 44 of 49. So the single-channel table
above was reporting the archive's WORST channel as if it were the frame's PSF.

- **Not a registration or population artifact, and both were checked.** The median centroid shift
  between channels is 0.064 px (max 0.339), far too small to widen a ~2.9 px profile, so it is not
  lateral chromatic aberration or a misregistration. And re-running with a COMMON set of the same
  physical stars in every channel (matched within 3 px) reproduces the ratios almost exactly
  (green/red 0.641 own-stars vs 0.648 common), so it is not driven by each channel detecting a
  different star population -- which was a real worry, since star counts differ by up to 3x per
  channel on an emission target where nebulosity raises the background in red only.
- **The size and even the DIRECTION are train-dependent**, which is why this is stored per channel
  per session rather than reduced to one archive-wide correction:

  | train | n | green/red | blue/red |
  |---|---|---|---|
  | ASI533MC Pro / Samyang 135 f/2 ED @ 130mm | 38 | 0.637 | 0.808 |
  | SV605CC / SH61 EDPH @ 270mm | 9 | 0.668 | 0.738 |
  | ASI585MC Pro / WO ZS61 @ 288mm | 2 | 0.904 | **1.279** |

  Blue is the SH61's best channel and the ZS61's worst by a wide margin -- textbook for a short
  refractor, where blue is the hardest end to correct.
- **Cause is mixed, and it does not need settling to act on.** Green has 2x the CFA sampling of red
  and blue, so its demosaiced plane is reconstructed from twice as many real samples and is expected
  to be sharper for reasons that are not optical. But red and blue have IDENTICAL sampling, and they
  differ by 20-28% in both directions depending on the train, so there is a genuine chromatic term
  on top. The training tiles are 3-channel demosaiced data, so whichever mechanism dominates, the
  per-channel PSF difference is real degradation the model sees.
- **Consequences for the sweep (P2):** degrade each channel with its OWN PSF. Blurring all three
  identically generates training data whose channel structure never occurs in this archive, and a
  net trained on it would learn to sharpen green as hard as red. Sample the per-channel, per-train
  distribution -- and keep the FWHM/beta correlation above, which holds within a channel too.
- **Shipped:** `SessionPsf.MasterProfiles` is an array with one entry per channel (null where a
  channel is unmeasurable, which happens on blue first), and the report renders a per-channel table
  per train. Nothing had to be migrated: no store record had ever carried a profile, because the
  measurement shipped after the last bake.
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
- **Frame vocabulary** (`DatasetTileExporter.Frame*`, the manifest's `Frame` column and the tile
  filename suffix): `master`, `halfmaster_a`, `halfmaster_b`, `sub`. Named constants rather than
  literals because four places must agree (filename, row, canonical sort, and the parity check's
  source resolution), and the sort is by an explicit rank, not by the string; `halfmaster_a` sorts
  *before* `master` lexicographically, so an ordinal compare would silently reorder the manifest.
- Budget: ~60 sessions × ~300 cells × ~9 tiles (~11 where a half-master pair exists) ≈ 160k tiles
  ≈ **50–80 GB**; one upload to a cloud volume, regenerable from scratch by re-running the command.

**Master integration strategy is gated per session, and the dataset legitimately holds both kinds.**
Bayer drizzle when it can run (`SessionRegistrar.TryDrizzle`), AHD + sigma-clip otherwise. Two
independent conditions: the stacker's own `DrizzleStrategy.Evaluate` (RGGB, enough matched frames for
per-Bayer-position R/B coverage, flux+weight planes inside the RAM budget) **and a matched dark
master**, which is not the stacker's business. Drizzle has no per-cell rejection while the AHD path's
sigma-clip washes hot pixels out across the session, and dark subtraction removes a hot pixel's
offset; so an uncalibrated session would get uncorrected hot pixels deposited straight into the
master, which is a worse master than the interpolated one. Falling back beats building a bad-pixel
mask, which would only reconstruct what the dark already carries.

- **Which one produced a master is recorded per SESSION** (`SessionPsf.MasterStrategy` in
  `stats/psf-sessions.jsonl`), deliberately not per tile: same rule as the FWHM column above, one
  fact one writer. **Any per-channel PSF statistic must group by it** or it is meaningless.
- **Measured, same session both ways** (2025-05-20 Lobster Nebula, ASI533MC Pro, 236 subs, both
  measured by `PsfProfileFit`): FWHM R 2.79 → 2.19 px, G 2.14 → 2.07, B 2.65 → 2.07; G/R 0.767 →
  0.947; Moffat β 2.85 / 24.95 / 6.00 → 4.85 / 4.50 / 4.15. The strongest single number is the fit
  residual: AHD **red** log-RMS 0.957 versus drizzle's 0.130, i.e. the Moffat model never described
  the AHD red profile at all, so that channel's FWHM was never trustworthy. Drizzle does not merely
  improve the measurement, it makes it possible. Consequence: the per-channel spread recorded in
  §2.2 above is substantially a demosaic artifact, not optics.
- **Eligibility is a minority of the archive**, which matters for how the trained model is scoped:
  25 of 61 session directories reach the 60-frame drizzle floor, so the population stays mixed
  whatever else changes.

**Half-master pairs** (`RegisteredSession.HalfMasterA/B`, exported as two more tiles per cell): two
integrations over **disjoint interleaved** halves of the session, sharing the scene and nothing else.
This closes the gap the smoke runs exposed (§3a): a single sub is **5.42x** the master's background
noise, the deepest pair 8-subs-per-cell allows (4v4) is still **2.96x**, and a half-master pair lands
at ~**1.41x** (√2), which is the regime a denoiser is actually deployed in. The split is interleaved
rather than first-half/second-half because seeing, transparency and focus drift monotonically through
a session: contiguous halves differ systematically in PSF and sky level, and an N2N pair whose two
sides disagree about the *signal* teaches the model to average that disagreement away. Depth is not
on the row; a consumer conditions on `NoiseMad`, which measures the level per tile and cannot drift.
**Open:** the floor is `2 × DrizzleStrategy.AutoSelectMinFrameCount` = 120 subs, a drizzle-derived
number that only ~10 sessions reach. An AHD half needs no CFA coverage and the halves exist to carry
a noise *level* rather than colour fidelity, so a lower floor on the non-drizzled path is defensible;
`RegisterAsync(minSubsForHalfMasters:)` makes it a call-site decision rather than a code change.

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
- **Losses: L2 (MSE) primary, NOT L1.** The plan said L1 primary until 2026-08-12, when the smoke
  run measured it as the single worst choice for faint stars (§3a). L1 converges to the conditional
  MEDIAN, and for a star near the noise floor that median sits at the background, so an L1 denoiser
  erases faint stars while scoring beautifully on PSNR (which the background it cleans perfectly
  dominates). L2 converges to the conditional MEAN, which is unbiased and preserves faint flux in
  expectation. Keep MS-SSIM auxiliary + the **flux-preservation regulariser** (per-tile
  mean/aperture-sum penalty); see §7. **Adversarial/GAN losses are deliberately
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

### 3a. P1 smoke-run findings (2026-08-12, GTX 1070, eleven variants)

Noise2Noise on the calgated P0 tiles: 8 sessions / 960 cells train, 2 held-out sessions / 240 cells
eval, a 0.81 M-param U-Net, ~10 min per variant. Deliberately small, because the question was
whether the DATA supports N2N, not whether a big net wins. Figures + checkpoints + scripts live in
`D:\Astro-Dataset\n2n-smoke`. **The conclusions below are about the training setup and the metrics,
and they carry over to the real NAFNet run unchanged.**

**The winning configuration is `v8`: L2 + nearest-upsample decoder + noise conditioning over mixed
1v1/2v2/4v4 pairing + a difference-of-Gaussians band loss restricted to 2-4 and 4-8 px (w=3).**
Applied to a master it leaves 0.70x the master's background noise, keeps 99 % of the master's faint
(SNR 8-15) stars visible with 0.62 of their amplitude, holds 0.840 structure correlation at 1-2 px,
and fabricates the fewest spurious point sources of any variant tested.

Ranked by what actually mattered:

1. **Noise conditioning is the single biggest factor, and it is not optional.** Feed the tile's
   measured background sigma as a 4th input plane and train across pairing depths so the model sees
   several noise levels. Without it a model trained on single subs (5.42x the master's noise) applies
   that same fixed strength to a master and the only thing left to remove is signal: faint-star
   amplitude 0.07-0.10 and 30 % visibility, versus 0.75 and 100 % once conditioned. **The root cause
   is the training noise distribution being a single POINT, not the level being wrong**: an ablation
   that only switched 1v1 to 4v4 pairing (still one level, still 2x off the master) recovered most of
   the gain on its own. Denoising strength must be an input, never a constant baked in at training time.
2. **`ConvTranspose2d(k=2, s=2)` mottles; use nearest-upsample + 3x3 conv.** The textbook
   checkerboard generator, and visible as ringing around stars. Worth 13 points of faint-star
   visibility on its own, but that is not why it is here: it removed an artifact no metric was
   tracking. Two independent wins that are easy to mistake for one.
3. **A structure loss must skip the noise-dominated scales.** A DoG band loss over 1-2 / 2-4 / 4-8 px
   made structure at 1-2 px WORSE, not better, and roughly halved faint-star retention. On this data
   the 1-2 px band of a single sub carries 5.18x the master's RMS, so it is very nearly pure noise:
   the term is unbiased in expectation (a squared penalty converges to the conditional mean even
   against a noisy target) but its gradient is dominated by the target frame's own noise realisation.
   Dropping just the 1-2 px band turns it from harmful into useful. Confirmed by isolating the band
   rather than the weight: three bands at w=1 fabricates MORE than two bands at w=3.
4. **The band loss only behaves once conditioning is present.** Added to an unconditioned model it
   trades the faint end away to buy the bright end (replicated in two backgrounds). Added on top of
   conditioning it improves noise removal AND fabrication together. Sequence the two accordingly.

**Metric lessons, which cost more time than the training did:**

- **PSNR actively misleads here.** Every failure in this run scored well on it; the best-PSNR variant
  (38.03 dB) was among the worst at keeping stars. Never select or early-stop on it.
- **"Star amplitude kept" and "faint stars visible" are different questions and can move in OPPOSITE
  directions.** Visibility is amplitude relative to the residual noise, so a model that halves a star
  while quartering the noise makes it easier to see while keeping less of its flux. Report both,
  bucketed by the master's own SNR; a single overall figure hides the faint end where variants differ.
- **Add a FABRICATION gate to §7, counting point sources that do NOT coincide with a master star.**
  It is the only measure here that asks whether the model INVENTED signal rather than whether real
  signal survived, and it reversed a verdict: the variant with the best bright-star fidelity turned
  out to be inventing ~170 fake stars per tile against a 22 floor. Two traps: the threshold must use
  a whole-tile MAD (a darkest-half MAD is a better noise estimator but too low a detection bar, and
  compresses every model into 18-25 % real, destroying discrimination), and **the metric's direction
  FLIPS with the input.** On a noisy sub, high means invention. On a master, where the input IS the
  reference, a model that changes nothing scores the input's own floor and a LOW score means erasure.
- **A residual-correlation check needs no clean reference:** correlate (input - output) against the
  output over star-free pixels. Noise correlates with nothing, so 0 is a clean removal and positive
  means structure went out with it. The only diagnostic here that would also work on a real image,
  hence a candidate runtime quality gate.

**Two known limits on these numbers.** The 1-2 px structure correlations are scored against an
AHD-demosaiced master, so part of what they reward is reproducing the interpolator; they are
provisional until the drizzle re-bake (§2.3, the masters are `Float16StagedStrategy`, NOT drizzled).
And the deployment gap is not closed: the model can only be trained down to 4v4 (0.5x of a sub) while
a master sits at 0.18x, because the tiles carry 8 subs per cell. Overstating sigma at inference is a
free strength dial but saturates at 0.66x while costing a third of the faint signal, so **the root
fix is HALF-MASTER PAIRS**: integrate subs 1..60 and 61..120 of a session separately for two
independent half-masters at sigma ~0.24x, an N2N pair at the noise level the model actually meets.
That belongs in the same registrar pass as the drizzle change so the archive is walked once.

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
- **Sizing the local box, measured 2026-08-12 on the real tile shape.** Re-verified from a clean
  install that `torch==2.13.0+cu126` still carries `sm_61` (arch list `sm_50 sm_60 sm_61 sm_70
  sm_75 sm_80 sm_86 sm_90`), so the de-risk path above is live rather than assumed. **Install from
  the cu126 index explicitly**: cu130 wheels exist for the same torch version and cannot target
  Pascal at all, so a bare `pip install torch` is a real trap here. Measured: fp32 matmul **4.91
  TFLOPS** (the plan's 4.85 reproduces), and a 4.33 M-param U-Net (base 48) on 256x256x3 tiles runs
  **~40 tiles/s at every batch size tried** (4/8/16, so it is compute-bound, not loader-bound),
  peaking at 5.25-6.69 GiB of the 8. That is **~0.95 h per epoch over the full 135,000 tiles**, which
  is what makes a full local run a multi-week proposition and a subset smoke (2,000 tiles ~ 50 s per
  epoch) entirely comfortable. Size local convergence proofs by tile COUNT, not epoch count.
- **fp16 measured SLOWER than fp32 on this card: 4.35 against 4.91 TFLOPS.** So AMP is not merely
  unavailable, it is a regression, and a training script that switches it on by default makes the
  local de-risk run slower while helping the rented one. Gate AMP on the device, and note the
  mechanism differs from the 1/64-rate claim above (torch does not appear to take GP104's native
  fp16 path at all); the conclusion is the same either way, which is why the conclusion is what to
  rely on.
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
| **P0: Dataset + stats** ✅ SHIPPED 2026-07-12 | `tianwen dataset build` (scan/dedup/gate/calibrate/register/tile+manifest, zero-skew export; calibration header-matched archive-wide, never per-folder); archive PSF/noise distribution report; pinned `test-sessions.txt` | Tile set regenerable one-command ✅; gate rejects transparency/focus-bad frames (star-count-led; §2.4) ✅; parity check green (maxDiff 0, in-run gate) ✅. **Real-archive run DONE 2026-07-15**, regenerated post-pedestal-fix 2026-08-10/11 as `D:\Astro-Dataset\2025-2026-calgated` (50 sessions / 135,000 tiles + `psf-sessions.jsonl` covering all 50); see § 2.3b for root order and the two session groups that must stay excluded. **Rebaked again 2026-08-12/13 as `D:\Astro-Dataset\2025-2026-drizzle` (64 sessions / 197,400 tiles / 64 retained masters), and THAT is what P1 trains on**, not the calgated set. |
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
- **Eval gates:** aperture-photometry delta and centroid shift below the thresholds measured below,
  on held-out subs; a release-blocking gate, not a dashboard number.

#### Measured classical repeatability (2026-08-12)

`PhotometricRepeatability.Compare` (pure, in `TianWen.Lib/Imaging/Dataset/`) matches stars between two
frames of one field, removes the global dither, and reports flux and centroid scatter **banded by
SNR**. Run it with the env-gated `PhotometricRepeatabilityProbe`
(`TIANWEN_REPEATABILITY_SUBS`, semicolon-separated FITS).

First measurement, 4 consecutive raw subs of `2025-03-20/24mm ASI585 30s 13o 252g` S1 (~4000 stars
each, ~3650 matched per pair), from the two pairs with no dither:

| SNR band | flux abs-delta p50 | flux abs-delta p95 | flux BIAS p50 | centroid p50 | centroid p95 |
|---|---|---|---|---|---|
| 100+ | 2.1-2.4% | 8.4-8.9% | 0.04 / -0.19% | 0.10 px | 0.53-0.59 px |
| 50-100 | 4.6% | 14.7-16.2% | 0.04 / -0.06% | 0.13 px | 0.47-0.50 px |
| 20-50 | 7.6-7.7% | 26-27% | 0.10 / -0.04% | 0.15 px | 0.42-0.43 px |

**Read this as a CEILING, not as the target.** Sub-to-sub scatter contains two independent
photon-noise realisations; the gate compares one sub against its own denoised output, where no new
realisation exists, so a correct model must preserve flux far better than this. The table's use is
that a model moving flux by more than the pipeline's own frame-to-frame scatter is unacceptable
without further argument.

**The sharp gate is BIAS, not scatter.** The pipeline's own signed median flux change is 0.03 to
-0.04% overall and never exceeds 0.2% above SNR 20, i.e. indistinguishable from zero. A denoiser that
introduces a systematic flux bias above a few tenths of a percent is therefore doing something the
classical path demonstrably does not, and that is measurable on far fewer stars than a scatter
comparison needs. Proposed gate: **|bias| < 0.5% per SNR band above 20, and abs-delta p95 no worse
than the table above**.

Three caveats, all of which make the numbers conservative rather than optimistic:

- **Raw Bayer mosaics**, undebayered, so a star's centroid is pulled by CFA structure. Re-measure on
  debayered or drizzled subs before treating the centroid column as final.
- **The bright-end centroid p95 rises while its p50 falls** (0.097 p50 against 0.587 p95 at SNR 100+).
  That shape is a minority of clipped cores, not a broken measurement: a saturated star's flat top
  gives an unstable centroid, matching the "bright end scrambled by saturation" already recorded for
  the Vela field. Exclude saturated stars from the gate, or the gate inherits a tail it cannot fix.
- **The harness fits TRANSLATION only.** The one pair carrying a real dither (-0.04, -4.84 px)
  reports ~0.9 px centroid shift roughly constant across every SNR band, which is the signature of
  field rotation that a translation cannot absorb, not of noise. So either compare undithered pairs
  or fit a full transform first; the two clean pairs above are the valid measurement.

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
