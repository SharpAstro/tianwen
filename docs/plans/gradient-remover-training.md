# Gradient-remover training (P5): flatten-and-inject

**Status: NOT STARTED; design captured 2026-08-11, sharpened 2026-09-02 after the Siril and SAS Pro
reference review. Nothing measured, nothing exported, nothing trained.** This is the P5 row of
[ai-denoise-deconv.md](ai-denoise-deconv.md) (design in its section 2.6) at run-level detail. Its
classical prerequisite is [background-extraction.md](background-extraction.md) Phases 1 and 2, and
it competes for the same `IGradientCorrector` slot that `OnnxBackgroundExtractor` (GraXpert BGE)
fills today. Shared discipline: [model-training-roadmap.md](model-training-roadmap.md).

## 0. Why this one is different from the other three imaging models

- **The truth is manufactured, not measured.** A gradient is an additive low-frequency field in
  linear space, so input = flattened + synthetic gradient, target = flattened, is exact by
  construction. No pairing premise to violate, no clean-reference problem.
- **Full-resolution pixels never pass through the net.** The model predicts the background at low
  resolution (GraXpert-style, 256 px), which is bicubic-upsampled and subtracted at full resolution.
  The hallucination class that dominates the denoiser and deconvolver programmes is structurally
  absent; the failure class here is **eating real large-scale nebulosity**, and this archive is
  unusually good for that test because 135 mm fields of Carina, Vela and Orion have Ha across most of
  the frame.
- **The P0 tiles are the wrong artifact** (a 256 px crop of a 3008 px sensor is a near-constant
  offset, and the tiles are MTF-stretched while gradients are additive in linear). It needs its own
  whole-frame LINEAR exporter.
- **It has covariates nobody else has.** Every frame's header carries `DATE-OBS`, site and pointing;
  `MeeusMoon` and `VSOP87a` are in-repo; one `CatalogPlateSolver` solve per session gives the WCS
  rotation. So moon separation, moon altitude and illumination, target altitude and azimuth, and the
  in-frame direction of a sky-anchored ramp are all computable offline AND at inference from the
  header alone. GraXpert cannot do this.

## 1. What exists, with pointers

| Piece | State | Where |
|---|---|---|
| The role | shipped | `IGradientCorrector : IImageEnhancer` (`src/TianWen.Lib/Imaging/Enhancement/`), `EnhanceAndEstimateBackgroundAsync` returning the surface; `GradientCorrectionStep` must run first in `SharpenPipeline` (enforced) |
| Today's implementation | shipped | `OnnxBackgroundExtractor` (`src/TianWen.AI.Imaging/Onnx/`): GraXpert BGE, downsample to 240, pad to 256, per-channel median+MAD normalise, single NHWC pass, denormalise, smooth, upsample; output `source - background + mean(background)`. Read from the user's GraXpert install (`%LOCALAPPDATA%\GraXpert\GraXpert\bge-ai-models\1.0.1\model.onnx`, 217.7 MB); GPL-3.0 code, CC-BY-NC-SA-4.0 weights, never redistributed and never a training target |
| CLI surface | shipped | `tianwen image flatten` with `--save-gradient` (`src/TianWen.Cli/ImageSubCommand.cs`) |
| Classical flattener | **SHIPPED 2026-09-02** (`ClassicalBackgroundExtractor`, also the `IGradientCorrector` that `AddTianWenAi()` falls back to when GraXpert is absent; that plan's "Implementation" section has the measurements) | [background-extraction.md](background-extraction.md); after the 2026-09-02 review the recommended Phase 2 is the sample-free robust-rejection + structure-mask + masked-low-pass-inpainting surface (AutoGradientRemoval.py), degree 2 polynomial, correction in LINEAR, level preserved |
| Ephemerides | shipped | `MeeusMoon` (`Astrometry/Lunar/`, internal), `VSOP87a` (`Astrometry/VSOP87/`), `CatalogPlateSolver`, `FitsHeaderEditor` writing `AIRMASS` / `CENTALT` / `CENTAZ` |
| Scenes | on disk | Retained linear session masters: 51 (`2025-2026-organized\session-masters\`, filters known) + 67 (`2025-2026-darkscaled\`). Calibrated subs are NOT retained (scratch is wiped per session), 5,908 of them per bake |
| Real pairs (control) | not measured | 2.1c: same-night, same-field high-vs-low-airmass frames on zenith-crossing targets; the cards are on disk |

## 2. Hypotheses

**H1. A classical flattener is a prerequisite AND the baseline, and it must exist before any net.**
It produces the flattened scenes the pairs are built from; its residual is the number the net must
beat; and its report over every master is the gradient-distribution measurement that calibrates the
injection (amplitude relative to sky, orientation, shape percentiles). Without it the injection
family is a guess and "removed to below the classical residual" has no denominator.
*Test:* build background-extraction Phases 1 and 2; run it over all 67 masters; record per master
the fitted surface's amplitude (peak-to-peak over the frame, in units of the frame's background
sigma and of its median), principal direction, and low-order shape coefficients.
*Prediction:* amplitudes span roughly 1 to 30 background sigma, dominated by a linear term, with
direction correlating with the frame's `CENTAZ`-projected up-direction and with moon azimuth on
moon-up nights. This table IS the injection distribution.

**RESULT 2026-09-03 (G1): amplitude and horizon direction confirmed, "dominated by a linear term"
half right, the Moon refuted.** `tianwen dataset gradient-report` over both bakes, independently:
`2025-2026-darkscaled` (67 masters, 197 planes, 62 solved, 15 moon-up) and `2025-2026-organized`
(51 masters, 153 planes, 51 solved, 14 moon-up). Reports and the per-master JSONL are in each bake's
`stats/`. Read the second as a replication: every figure below lands within a few percent on it.

- **Amplitude is real but the bulk sits an order of magnitude below the top of the predicted range.**
  Peak-to-peak of the fitted model, in the plane's own background sigma: p5 0.83, p25 1.58, **p50
  2.32**, p75 3.53, p95 13.66, max 18.2 (organized: 0.79 / 1.59 / **2.28** / 3.09 / 6.80). As a
  fraction of the fit's median level: p50 8.3 %, p95 31 %. So the injection family must be *dense
  near 1 to 4 sigma* with a tail to about 20, not uniform over 1 to 30: sampling the predicted range
  uniformly would spend most of its capacity on gradients this archive does not contain.
- **The amplitude is a property of the FIELD OF VIEW, not of the night.** Per camera, p-p / sigma
  p50: ASI585MC Pro (the 24 mm wide field, 9 masters) **11.28** (p95 20.81), ASI1600MM Pro 8.56 (2
  masters, so read it as a hint), SV605CC 2.42 (14), ASI533MC Pro 2.24 (42). Every master at or above
  12 sigma is one of the 24 mm wide-field ones. The second bake is the natural control: it holds no
  585 masters at all (SV605CC 2.57, ASI533MC 2.12) and its p95 is 6.80 against darkscaled's 13.66, so
  the entire high tail is the wide field rather than a set of unusually bad nights. Condition the injection on
  plate scale and frame coverage, or the wide-field frames define a tail the narrow ones never see.
- **A dome is the plurality shape, so "dominated by a linear term" cannot be taken as "a ramp".**
  Linear share of the peak-to-peak: p5 0.26, p50 0.64, p95 0.93; shape census over 197 planes is
  Dome 100, Ramp 50, Saddle 40, Bowl 7 (organized: 88 / 33 / 27 / 5 of 153), and 100 of 197 planes
  carry a dome deeper than a quarter of their linear range. Curvature median 1.11 sigma. **The
  injection needs the quadratic term; a linear-only family would be off-distribution for half the
  archive**, which is also why degree 2 is the right default for the classical fit.
- **Direction tracks the horizon, and this is the one prediction that lands cleanly.** |brightening
  minus anti-zenith| over the solved masters: p50 34 degrees, **58 % within 45 degrees** against 25 %
  by chance (organized: p50 33, **61 %**), with 25 of 57 masters inside 30 degrees. It survives the
  epoch caveat below, which can only have diluted it.
- **The Moon is refuted, not merely unconfirmed.** On moon-up masters |brightening minus Moon PA| is
  p50 112 degrees with **7 % within 45** (organized: p50 91, 7 %), i.e. materially WORSE than chance
  in both bakes, over 15 and 14 masters. Whatever moonglow contributes here does not point away from
  the Moon in the frame; do not condition the injection on Moon azimuth on this evidence. It stays
  worth re-testing on a set selected for a bright Moon well clear of the horizon.
- **Epoch caveat, and it bounds every direction figure above.** Covariates are evaluated at the
  master's `DATE-OBS`, which is the reference sub's start rather than an exposure-weighted mid-session
  instant, so the parallactic angle carries tens of degrees of uncertainty over a multi-hour session.
  The horizon correlation is measured THROUGH that smearing; a mid-session epoch can only sharpen it.
- **The fit itself:** kept fraction p50 0.79, iterations p50 11, canvas ring masked as absent 0.3 %
  of a frame at the median. About 30 to 38 s per master in Release for a plate solve plus nine fits.

**H2. Flatten-and-inject on masters alone gives enough scene diversity, given augmentation.** 67
masters is few scenes, but each yields unlimited (gradient, target) pairs, and the covariates rotate
with the scene under flips and rotations if the injector rotates them consistently.
*Arm M:* masters only, K = 64 gradients per master per epoch, flips and 90-degree rotations.
*Prediction:* on held-out masters, injected gradients are removed to below the classical residual
(H1's number) and nebulosity flux at 32-256 px scales is preserved to within 2 percent.
*Kill:* residual above classical, or nebulosity loss above 5 percent on the Ha-rich fields. Then
arm S below is needed.

**H3. Calibrated SUBS as scenes buy real sky-gradient variety.** A calibrated sub's residual
background after flats IS sky gradient (vignetting is gone), and 5,908 subs are 5,908 realisations of
altitude and moon geometry drifting through a night. The dataset builder already calibrates every
sub during its analyze stage (385 ms per frame); a `--export-whole-frame` option can write each
calibrated sub downsampled to model resolution at no extra read cost.
*Arm S:* masters plus downsampled calibrated subs (flattened by the classical fit first).
*Prediction:* arm S beats arm M on the real-frame gate (H6) by more than the seed spread, because
it has seen real gradient shapes rather than only synthetic ones.
*Kill:* a tie. Then masters suffice and the sub exporter is dropped.

**H4. Injecting from the measured distribution, conditioned on covariates, beats isotropic
injection.** Moon up and close should draw a strong directed ramp toward the projected moon azimuth;
a moonless dark frame the weak altitude ramp; the Krisciunas and Schaefer (1991) moonlight model over
the footprint gives a physically-shaped non-linear surface no polynomial produces at small
separations.
*Arms:* isotropic polynomial injection against covariate-conditioned injection (H1's regression
plus the K&S term).
*Prediction:* on real held-out frames (H6) the conditioned arm's removed component agrees with the
site prediction in direction to within 20 degrees on 90 percent of moon-up frames; the isotropic
arm's does not.
*Kill:* no difference on the real-frame gate. Then the covariates stay diagnostics.

**H5. Optional covariate CONDITIONING (scalars the net receives) helps it tell an expected gradient
from large-scale nebulosity.** The core ambiguity of this model class; only testable after H4.
*Arms:* no conditioning against moon separation, moon altitude, illumination, target altitude and
the projected ramp direction as scalar inputs.
*Prediction:* nebulosity preservation on the Ha-rich fields improves (loss halves) at equal residual.
*Caveats:* clouds break the physics, so covariates are inputs the net may weigh, never a subtraction
the pipeline asserts; several scalars need a new `OnnxIoNames` signature (`ImagePlusScalar` carries
exactly one). Deferred if H4 fails.

**H6. A per-site light-pollution prior in horizon coordinates gives real frames the only
physics-anchored gate they can have.** Synthetic injection only ever tests synthetic truth. LP is
quasi-static in alt-az for a fixed site and locally linear over a 5-degree field, so per-sub linear
fits (moonless frames, or K&S moon term removed first) regressed against pointing alt-az and
time-of-night, with a per-train pixel-fixed term alongside, give a per-frame LP prediction from
pointing plus WCS rotation plus clock.
*Test:* fit the prior on the calibrated-sub fits from H3's exporter; hold out sessions; predict each
held-out frame's ramp direction and relative amplitude; compare against what the model removes.
*Prediction:* systematic disagreement (wrong direction, under-removal along the predicted axis) on a
held-out frame fails the model; agreement is a consistency check, never exact subtraction (LP varies
with hour, season and aerosols; the fit is a distribution with error bars).
*Bonus:* the pixel-fixed term measures residual flat error per train for free.
*Licence:* a thin site can bootstrap from VIIRS upward radiance (public domain) through a
Garstang-style propagation; the Falchi 2016 atlas is CC BY-NC and stays out of anything shipped
(TianWen is AGPL-3.0-or-later, which grants downstream commercial use, so NC material inside a
shipped artifact is a grant we do not hold).

**H7. Real airmass pairs (2.1c) are a CONTROL on the injection model, not a training source.** A
high-altitude frame of a field is a flatter version of the low-altitude one on the same night. The
difference is a real gradient with a known independent variable.
*Test (no training):* on zenith-crossing sessions, fit the classical surface to each sub, plot
amplitude against `AIRMASS`, and compare the high-minus-low difference surfaces against the injection
family's shapes.
*Prediction:* the real difference surfaces are dominated by a linear term aligned with the vertical
(CENTAZ-projected up), and their amplitudes fall inside H1's distribution. If they fall outside, the
synthetic family is unfair and H1's distribution is re-fitted to include them.
*Never pair across nights* (transparency, moon and focus all move).

**H8. The trained model beats the classical fit only on frames where the fit's model is wrong.** A
degree-2 polynomial plus inpainting surface handles most gradients. The net's value is on
nebula-everywhere fields where no sample or pixel is background, and on moon-scatter surfaces.
*Test:* stratify held-out masters by nebulosity coverage (fraction of the frame above the classical
fit's structure mask) and report both methods per stratum.
*Prediction:* parity on sparse fields, a clear win for the net above 50 percent coverage. If the net
does not win there either, ship the classical fit as the `IGradientCorrector` fallback and stop.

## 3. Data and the exporter

**Sample = one whole calibrated frame downsampled to model resolution, LINEAR.** GraXpert's recipe
(downsample to 240, pad to 256) is the reference shape; ours: area-average the linear frame to 256
on the long side, per-channel, keep the median and MAD as normalisation constants beside the sample
(the same constants `OnnxBackgroundExtractor` derives at inference so the two paths agree), store
fp16 CHW plus a JSONL row with session id, camera, filter, `DATE-OBS`, site, `CENTALT` / `CENTAZ` /
`AIRMASS`, moon separation / altitude / illumination (computed at export, stored so a consumer never
recomputes ephemerides), the WCS rotation, and the classical flattener's fitted coefficients for that
frame (the "residual truth" of what was removed to make the scene).

**Scenes:** the retained masters (flattened classically) for arm M; plus, for arm S, every calibrated
sub written by a new `DatasetBuildOptions.ExportWholeFrame` option inside `DatasetBuildRunner`'s
analyze stage, before registration, since registration is irrelevant to a whole-frame low-frequency
sample and the warp would only introduce NaN borders. Flatten each classically before storing the
target; store the fitted surface too.

**Injection at training time, in torch, on the 256 px sample** (cheap at this resolution, unlimited
draws): low-order 2D polynomials with coefficients from H1's distribution; edge ramps; corner glows;
the K&S moon-scatter surface evaluated over the footprint from the stored covariates; amplitudes in
units of the sample's own background sigma so the injection is calibrated per frame, never in
absolute ADU. Flips and rotations applied to scene AND covariate directions together.

**Held-out split by session**, the same stable hash bucket the tile bakes use, so a frame's siblings
never leak.

## 4. Model

Small. GraXpert's BGE is a 217 MB graph; a background predictor at 256 px does not need it. Start with
the 0.81 M U-Net from `n2n_smoke.py` at base 32 with a 3-channel output predicting the background
surface (not the corrected image), L2 on the surface plus an L2 on `input - surface` against the
target at the 32-256 px DoG bands (nebulosity preservation, the band loss with the band ORDER
inverted relative to the denoiser: here the coarse bands are the signal). Covariate scalars, if H5
runs, broadcast as extra planes. Adam 2e-4 cosine, seeds fixed, cuDNN deterministic; minutes per
seed at 256 px.

The model predicts LOW-RES background only. Inference: normalise as at export, predict, denormalise,
bicubic-upsample to full resolution, subtract, add back `mean(background)` (level preserved, the
convention every reference and the existing extractor share).

## 5. Metrics and gates

- **Injected-gradient residual** on held-out masters: RMS of `(predicted - injected)` in background
  sigma units, required below the classical fit's residual on the same frames (H1's denominator).
- **Nebulosity preservation:** flux in the 32-256 px DoG bands inside the classical structure mask,
  target vs corrected, within 2 percent (gate) and reported per coverage stratum (H8).
- **Real-frame LP consistency** (H6): direction within 20 degrees and relative amplitude within a
  factor 1.5 of the site prediction on moon-down frames; direction toward the projected moon on
  moon-up frames. A consistency check with error bars, never an exact-subtraction assert.
- **Photometric integrity** (programme section 7): a smooth additive surface cannot change aperture
  flux relative to local background by construction, but measure the signed flux bias anyway; it is
  the check that the upsample and level add-back did what they claim.
- **Level:** output median equals input median per channel (the reviewed references all preserve
  level; a model that re-baselines fails).
- **No GraXpert output anywhere** in the loop: its weights are CC-BY-NC-SA and its outputs are not
  ours to target. The classical fit is the baseline.

## 6. Experiments, in order

| Step | What | Cost | Decides |
|---|---|---|---|
| G0 | **DONE 2026-09-02.** Background-extraction Phases 1 and 2 (the classical flattener, sample-free, linear, level-preserving) with its synthetic tests; its two reasoned thresholds were measured by G1 and both are now settled (`background-extraction.md`) | done, one day | H1 prerequisite, the baseline, the fallback |
| G1 | **DONE 2026-09-03.** Gradient-distribution report over both bakes (118 masters, 350 planes) as a sibling of `psf-noise-report.md`: `tianwen dataset gradient-report`, `stats/gradient-report.md` + `gradient-masters.jsonl` per bake. Answered H1 (see section 2) and measured both of G0's reasoned thresholds | done, a day | H1; the injection family |
| G2 | Whole-frame linear exporter (masters; then the `ExportWholeFrame` sub option) with covariates and fitted coefficients per row | 1 to 2 days | H2, H3 data |
| G3 | Airmass-pair control on the zenith-crossing sessions, no training | half a day | H7 |
| G4 | Arm M, three seeds; nebulosity strata report; labelled comparison at full resolution on three Ha-rich masters | 3 x minutes | H2, H8 |
| G5 | Arm S vs arm M | 3 x minutes plus the sub export bake (~1 h) | H3 |
| G6 | Isotropic vs covariate-conditioned injection | 6 x minutes | H4 |
| G7 | Site LP prior fitted from G2's sub fits; the real-frame gate | a day | H6 |
| G8 | Covariate conditioning, only if H4 passed | 6 x minutes | H5 |
| G9 | Export (fixed 256, opset 17, parity to torch), contract JSON, `OnnxTianWenGradientCorrector : IGradientCorrector`, routing beside `OnnxBackgroundExtractor` | 2 days | Ships |

## 7. Integration

`OnnxTianWenGradientCorrector : IGradientCorrector` in `src/TianWen.AI.Imaging/Onnx/`, sharing
`OnnxBackgroundExtractor`'s downsample / normalise / upsample / add-back skeleton (extract it to a
base so the two cannot drift); `EnhanceAndEstimateBackgroundAsync` returns the surface, which is what
`tianwen image flatten --save-gradient` already asks for. Selection: the classical
`ClassicalBackgroundExtractor` from background-extraction is the AI-free fallback and the default
when no weights are present (shipped 2026-09-02 as `FallbackGradientCorrector`, GraXpert when its
weights resolve, the classical fit otherwise); the in-house net sits behind the same in-house backend flag as the
other roles (the `n2n`-to-`tianwen` rename noted in the deconvolver plan); GraXpert stays where it
is. With this and P4 the in-house tier can run the whole canonical program.

## 8. Phasing

| Phase | Deliverable | Exit |
|---|---|---|
| P5.0 | **DONE 2026-09-02.** Classical flattener shipped (background-extraction Phases 1 and 2) | G0 |
| P5.1 | Gradient-distribution report (**G1 done 2026-09-03**); whole-frame exporter; airmass control | G1, G2, G3 |
| P5.2 | Arm M and arm S answered with posted comparisons | G4, G5 |
| P5.3 | Injection conditioning and the site prior; real-frame gate defined | G6, G7 |
| P5.4 | v1 exported, wired, contract-asserted | G8 (optional), G9 |

## 9. Open questions

- **Which downsample?** Area-average is the honest choice for a background; GraXpert's exact
  resampler is not known to us and does not need to be, since our extractor derives its own
  normalisation constants and the parity that matters is ours against ours.
- **Mono frames** are natural here (a background is a background); the exporter should write mono
  samples as one channel and the model can be 1-channel-per-plane from the start, unlike the OSC
  denoiser. Decide at G2.
- **How the flattener's own residual artefacts enter the training pairs:** they become background the
  net must preserve (the same argument as the star remover's bootstrap plates). Injected gradients
  must be placed independently of where the flattener struggled, or the net learns that removing a
  gradient reveals artefacts.
