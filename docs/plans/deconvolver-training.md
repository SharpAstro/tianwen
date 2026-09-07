# Deconvolver training (P2): the psf01-conditioned non-stellar sharpener

**Status: NOT STARTED as training; the measurements it needs are DONE and the blockers are known
(written 2026-09-02).** No pair has been generated, no net trained. This is the P2 row of
[ai-denoise-deconv.md](ai-denoise-deconv.md) section 5 at run-level detail; the PSF measurements it
rests on are in that document's section 2.2 ("The measured PSF profile", "The PSF is per CHANNEL")
and in [osc-narrowband-denoiser.md](osc-narrowband-denoiser.md) 1c and 1e. It shares the
synthetic-degradation exporter with [denoiser-training.md](denoiser-training.md) E1 and the
discipline in [model-training-roadmap.md](model-training-roadmap.md).

## 0. What exists, with pointers

### Runtime (the contract the model must fit)

- `OnnxNonStellarDeconvolver` (`src/TianWen.AI.Imaging/Onnx/`): two inputs, the image tensor and a
  scalar `psf01` in `[0,1]` the graph broadcasts as a fourth channel; classified by
  `OnnxIoNames.ImagePlusScalar`. Runs through `ChunkedNafnetRunner`: `ApplyInputStretch` (MTF to
  median 0.25, per channel, whole frame), 256 px chunks padded to a multiple of 16, 64 px overlap,
  16 px rim dropped by `ChunkedInference.Stitch`, inverse stretch. Applied to the **starless plate**
  by `SharpenPipeline`'s `DeconvolveStarlessStep`.
- `HfdPsfEstimator` (`src/TianWen.Lib/Imaging/Enhancement/`): measures the image's own star HFD and
  encodes the RADIUS as `psf01 = log2(r / 1 px) / log2(8)` over `[1, 8]` px, the SAS AI4 convention.
  Two consequences measured 2026-08-11: a FWHM sweep to 8 px reaches only `psf01 = 0.667`, and this
  archive's masters (FWHM 1.8 to 2.4 px, radius 0.9 to 1.2) **clamp at or near 0**, so under the SAS
  encoding the model has no lever on the very frames it will be run on. **REVISED by E1 (H5 below):
  that was the 51-session organized bake. Over the full 79-session bake the estimator reads 0.88 to
  3.03 px radius and the shipped encoding spreads them 0.000 to 0.293 with 6 of 79 clamped, so there
  IS a lever and it is the CEILING, not the floor, that is throwing resolution away.** The estimator
  now exposes its unclamped measurement (`MeasureRadiusPxAsync`) and a range-parameterised
  `EncodeRadiusToPsf01`, because a clamped value cannot be re-encoded under a candidate range.
- Backend order today: RC-Astro `bxt` when installed and licensed, else SAS
  `deep_nonstellar_sharp_conditional_psf_AI4.onnx`. Neither may appear in this training, validation
  or metric loop (EULA section 10; SAS licence unverified, excluded by default).

### Measurements (the sweep is calibrated, with three caveats)

| Fact | Where | Consequence for the sweep |
|---|---|---|
| Per-sub FWHM p05 1.96 / p50 2.10 / p95 2.55 px; intra-session p90/p10 median 1.04 | 51-session PSF store (`stats/psf-sessions.jsonl`, `SubFwhm`) | The archive has no blurry arm; truth is synthetic (2.1b) |
| Master profile is Moffat, beta PER TRAIN: Samyang 7.95, SH61 6.85, ZS61 2.05 to 2.70; Moffat beats Gaussian 154/163 on the current store (48/50 as first measured); wing flux 98x a Gaussian's at 2 FWHM | `SessionPsf.MasterProfiles[]` (`PsfProfileFit`, log-space fit) | Sweep beta 2 to 12 weighted to 6 to 9, or sample the measured per-(train, channel) distribution; never Gaussian, EXCEPT that all nine Gaussian wins are ch0 (E0) |
| ~~beta correlates with FWHM (r 0.66, 0.635 within the Samyang train)~~ **SUPERSEDED by E0: that is an all-channels POOLED statistic and it does not survive the split** | E0 below | Sample (FWHM, beta) jointly PER CHANNEL. The joint draw is supported on ch0 and ch2 and is not supported on ch1 |
| PSF is per CHANNEL and the direction is train-dependent (green/red 0.64 Samyang, blue/red 1.28 ZS61) | `MasterProfiles`, one per channel, blue first | Degrade each channel with its own kernel |
| The G/R ratio is mostly an AHD demosaic artifact (0.767 AHD to 0.947 drizzle on one session; AHD red log-rms 0.957 vs 0.130) | `stats/psf-channel-survey`, `drizzle-vs-ahd/` | Calibrate the per-channel ratios on DRIZZLE masters only; split every statistic by `MasterStrategy` |
| Red's centre-to-corner FWHM FALL is chromatic defocus from autofocus optimising at 500 to 550 nm, not a bug (red sharpest in 6 of 65 sessions; red/green 1.38 quad-band, 1.64 at 3 nm) | run log 1c, `docs/todo` task #19 closed | The P2 hold on channel 0's radial profile is LIFTED; model red as it is, per (train, filter) |
| Field-radius bins sample ONE common star set banded on green (`RadiusSampling = "common-stars"`) | `SessionPsf.BinsByChannel`, #172 "the field-radius profile samples one star set, matched across channels" | The radial profiles are now comparable across channels |
| Faint stars read 25 to 30 percent WIDER (half-max relative to own peak); saturation ruled out (0.1 to 0.2 percent of stars) | 2.2 | Any PSF fitted from detections must band on brightness; `PsfProfileFit` does (55th to 75th percentile) |
| The `2025-2026-organized` store predates the 2026-08-27/28 deblender; the current detector finds ~4 percent more matched stars and measures ~2 percent (+0.039 px) wider, non-uniformly. **RESOLVED by E0 without a re-measure: `2026-09-full` is already current** (count ratio 1.000, 0.000 px) | `PsfStoreVsCurrentDetectorProbe` | **Calibrate on `2026-09-full`, never on `2025-2026-organized`** |
| Report keyed by (train, filter) with `FilterFromSessionId`; the ASI533 split is 36 sessions 3 nm against ONE quad-band | run log 1f, N5 | A one-session (train, filter) cell needs a stated fallback (train profile) or is withheld |

### Data

Retained linear session masters: `D:\Astro-Dataset\2025-2026-organized\session-masters\` (51,
filters known, 33 BayerDrizzle / 7 Float16Staged inside the 3 nm group) and
`2025-2026-darkscaled\session-masters\` (67, no filter, 47 / 20). The P0 tiles themselves are
MTF-stretched (denoiser plan, fact 0) and cannot be blurred as stored: convolution is linear in flux
and the stretch is not. Every degradation happens on the linear master, before the stretch.

### Capture (validation data, none captured yet)

`SessionConfiguration.SaveIntermediates` (default off, shipped 2026-08-29) keeps every auto-focus
V-curve rung plus the verification exposure under `<output>/Intermediates/<date>/<filter>/Focus/ota<n>_<runStart>/`
as `FrameType.Focus`, 9 rungs over +/-100 steps (13 to 16 CFZ) plus the anchor, `FOCUSPOS` /
`FOCTEMP` / `AIRMASS` on every frame. **As of 2026-09-02 no `Intermediates` directory exists on D: or
under `C:\temp`**: the switch has never been on for a real night. A defocus PSF is a disk, not a
Moffat, so these frames are validation and near-focus training data, never a replacement for the
synthetic Moffat sweep.

## 1. Hypotheses

**H1. The achievable sharpening is bounded by the truth, and the bound should be measured first.**
The truth is a seeing-limited master (FWHM ~2.1 px, on rigs sampled at the ~2 px floor), so the net
learns to remove EXCESS blur back to that ceiling and nothing sharper. Before any net: run a
known-kernel classical deconvolution (Richardson-Lucy with the exact synthetic Moffat) on synthetic
pairs and record, per (train, channel, psf01 bin), how much of the injected blur an oracle recovers
without ringing above 1 MAD.
*Prediction:* the oracle recovers within 10 percent of the truth FWHM for injected blur up to about
2x, degrading beyond; nothing recovers below the truth. This table is the ceiling every arm is scored
against, and a net that "beats" it is fabricating.
*Kill:* none; this is a measurement. If the oracle itself rings at every psf01, the noise-after-blur
level is wrong (H4) before anything is trained.

> **MEASURED 2026-09-06 (E1).** `DeconvolutionOracleCeilingProbe`: 6 masters spread across the
> archive, 512 px centre crops, 3 channels, 5 injected FWHM (0.5 to 4.0 px, Moffat beta 4 fixed so
> the sweep has one axis), both noise-free and blur-then-noise at the frame's own background MAD,
> Richardson-Lucy 30 iterations with the EXACT kernel. 180 rows.
>
> **The kill does not fire.** The oracle does not ring at every level: excess is 0 and 2 percent in
> the gentlest band, so the noise-after-blur level is sane and H4 is not implicated.
>
> **The axis is the blur RATIO, not the psf01 bin the hypothesis asked for.** psf01 encodes absolute
> width, so binning by it puts "a frame that was already 7 px wide" in the same bucket as "a 1.5 px
> frame blurred to 5 px", and the top bin turned out to be one short-focal-length master rather than a
> degree of blur. Deconvolution removes EXCESS width, and the conditioning scalar cannot express how
> much of a width is excess. That is a fact about the contract, not about this probe.
>
> **The table is at 60 iterations, not 30, and that correction is the second half of E1.** The first
> version stood at 30, which the iteration sweep below then showed to be under-converged by roughly a
> factor of three in residual. A too-low ceiling flatters every arm scored against it, so the table was
> re-measured rather than annotated.
>
> | blurred/truth | noise | n | rec/truth | residual px | ring excess | stars vs truth |
> |---|---|---:|---:|---:|---:|---:|
> | 1.0-1.1x | no | 24 | 1.00 | 0.00 | 0 % | 1.00 |
> | 1.0-1.1x | yes | 18 | 1.00 | 0.00 | 1 % | 0.94 |
> | 1.1-1.3x | no | 17 | 1.00 | 0.00 | 15 % | 1.00 |
> | 1.1-1.3x | yes | 15 | 0.99 | -0.02 | 8 % | 0.86 |
> | 1.3-1.6x | no | 9 | 1.02 | 0.06 | 48 % | 0.96 |
> | 1.3-1.6x | yes | 11 | 1.01 | 0.04 | 24 % | 0.64 |
> | 1.6-2.0x | no | 14 | 1.14 | 0.26 | 82 % | 0.99 |
> | 1.6-2.0x | yes | 14 | 1.09 | 0.19 | 41 % | 0.71 |
> | 2.0-3.0x | no | 20 | 1.58 | 1.12 | 95 % | 0.77 |
> | 2.0-3.0x | yes | 18 | 1.59 | 1.05 | 77 % | 0.52 |
> | 3.0x and up | no | 5 | 1.59 | 1.17 | 29 % | 0.83 |
> | 3.0x and up | yes | 5 | 1.57 | 1.13 | 38 % | 0.91 |
>
> **"Within 10 percent up to about 2x" HOLDS, and the earlier refutation of it was an under-converged
> measurement rather than a property of the problem.** At 60 iterations the recovered star is 1.01 to
> 1.02 times the truth through 1.3-1.6x and 1.09 to 1.14 at 1.6-2.0x, so the prediction's boundary is
> right where it said. At 30 iterations the same rows read 1.06 to 1.08 and 1.15 to 1.20, which is what
> produced the "the boundary is 1.6x" claim this paragraph replaces. Past 2x the star is about 1.6x the
> truth, which is most of the blur still present, at either count.
>
> **Read rec/truth off the NOISE-FREE arm.** The noisy arm scores BETTER on it (1.09 against 1.14 at
> 1.6-2.0x) while keeping 0.71 of the truth's stars against 0.99, which is the fabrication signature
> again: noise sharpened into point sources reads as narrow stars and pulls the median down. Where the
> two arms disagree on width, the star count says which one to believe.
>
> **"Nothing recovers below the truth" holds.** rec/truth is at or above 0.99 in every band, so at the
> median the oracle never produced a star narrower than the one that was there. That is the line a
> trained arm may not cross.
>
> **Ringing arrives BEFORE recovery degrades, and it is what bounds the usable range.** At 1.1-1.3x
> the residual is still 0.00 px while 15 percent of stars already show excess undershoot. So the
> advertised range is set by the ringing column, not by the FWHM column, and H6's ladder question
> inherits that.
>
> **The recovery FRACTION is not comparable across blur ratios and must not be quoted alone.** Its
> denominator grows with the injected blur, so the worst rows in the table score best on it: one reads
> 85 percent recovery while leaving the star at 1.48x its original width (truth 1.85 px, blurred 8.15,
> recovered 2.74). The absolute residual is monotone in blur and is the honest column; that is why the
> table above leads with it and the earlier by-psf01 summary, which showed recovery rising again in
> its top bin, was an artefact of the same thing.
>
> **Two arms are not comparable to each other on the ring column, only within one.** The excess is
> measured against the same statistic on each arm's OWN input, and a noisy input already has deep
> annulus minima, so the noisy arm's excess is structurally smaller. The cost of noise shows in the
> star count instead: 0.45 of the truth's stars survive at 2-3x against 0.70 noise-free.
>
> **The iteration sweep, which is why the table above stands at 60.** `ReportWhereTheIterationCountStopsHelping`
> walks 5 to 120 on three masters at the 2 and 3 px injections, reading every count off ONE run via
> `RichardsonLucy`'s checkpoint handler, since the iteration is a trajectory and re-running it per
> candidate would repeat every earlier iteration.
>
> | iterations | noise-free residual / ring / stars | noisy residual / ring / stars |
> |---|---|---|
> | 5 | 0.78 px / 28 % / 0.88 | 0.70 px / 25 % / 0.69 |
> | 10 | 0.63 / 34 % / 0.92 | 0.51 / 33 % / 0.73 |
> | 20 | 0.42 / 40 % / 0.96 | 0.32 / 33 % / 0.73 |
> | 30 | 0.37 / 40 % / 0.94 | 0.32 / 38 % / 0.74 |
> | 60 | 0.22 / 42 % / 1.04 | 0.14 / 41 % / 0.71 |
> | 120 | 0.10 / 43 % / 1.02 | 0.09 / 43 % / 0.63 |
>
> **The residual has still not plateaued at 120, and the cost does not land where the metric was
> pointed.** Ringing moves 40 to 43 percent across the whole sweep, so it is nearly blind to this axis;
> what more iterations actually spend is STARS, and only where there is noise (0.74 to 0.63 from 30 to
> 120) while the noise-free arm rises to 1.02 as deconvolution makes faint stars more detectable. That
> is Richardson-Lucy fitting the noise, and it destroys stars rather than announcing itself as ringing
> or as a worse width. **60 is the knee**: it takes the noisy residual from 0.32 to 0.14 px for three
> points of star loss, where 120 buys 0.05 px more for eight.
>
> **Stated limitations.** The sweep used only the 2 and 3 px injections on three masters, so it speaks
> to the heavy-blur regime; at light blur far fewer iterations already suffice, which the ceiling table
> shows as 0.00 px residual in its top two bands at both counts. Beta fixed at 4. Six masters and
> centre crops only, so nothing here speaks to field position (H7).

**H2. Condition on what inference can measure: psf01 from `HfdPsfEstimator` run on the degraded
frame, never from the kernel parameters.** Inference has no kernel; it has the estimator's number,
which carries its own biases (faint-star widening, the undersampling clamp, the azimuthal average of
an elongated star reading near the geometric mean).
*Arms:* label-from-kernel against label-from-estimator, same pairs.
*Prediction:* on held-out masters the estimator-labelled arm reduces FWHM within 0.05 px of the
kernel-labelled arm on synthetic pairs and beats it on REAL frames (AF ladder rungs, H6), where the
kernel is unknown and the estimator's reading is all there is.
*Kill:* estimator-labelled is worse on real rungs too. Then the estimator is the weak link and needs
work before the model does. This is the tile-border asymmetry lesson in another costume: never train
on a quantity the deployed path cannot obtain.

**H3. Per-channel, per-train, correlated (FWHM, beta) kernels matter.** A single kernel for all three
channels, or independent FWHM and beta draws, produces channel structure and wing shapes this
archive never shows.
*Arms:* shared kernel against per-channel kernels drawn from the (train, filter, channel) measured
distribution with the beta-FWHM correlation preserved.
*Prediction:* per-channel reduces red FWHM on held-out drizzle masters by at least 0.1 px more than
shared, at equal ringing, and green is NOT over-sharpened (green FWHM reduction no larger than red's
in absolute px).
*Kill:* the arms overlap across three seeds. Then one kernel is enough and the per-channel store is
diagnostic only.

**H4. Noise after blur is not optional.** Deconvolution amplifies noise under inversion; a net trained
on noise-free pairs learns a brittle sharpener.
*Arms:* noise-free against electron-domain noise at master depth added after the blur.
*Prediction:* the noise-free arm posts the better synthetic FWHM number and the worse absolute-bar
fabrication count and ringing at 1:1 on real masters. Standard, cheap, run once and record.

**H5. TianWen's own contract needs a lower encoding floor than SAS's.** Under `[1, 8]` px radius,
FWHM 2.0 px is `psf01 = 0` and the whole archive sits within 0.1 of the floor.
*Test:* encode the archive's master FWHM distribution under `[1, 8]` and under `[0.5, 8]` px radius
and report the spread of psf01 values a real master would present.
*Prediction:* `[1, 8]` gives a spread under 0.1 (no lever); `[0.5, 8]` spreads the archive over about
0.2 to 0.4. Train and ship the own contract with the lower floor; only a SAS drop-in export is pinned
to `[1, 8]`, and it is not built unless measured equal.

> **ANSWERED 2026-09-06 (E1), and BOTH halves are wrong: the diagnosis and the fix.**
> `PsfEncodingSpreadProbe` runs the DEPLOYED `HfdPsfEstimator` over all 79 retained masters of
> `2026-09-full` and encodes the radius it reads under four candidate ranges.
>
> | range | p05 | p50 | p95 | spread | on floor | at ceiling |
> |---|---:|---:|---:|---:|---:|---:|
> | `[1.0, 8.0]` shipped | 0.000 | 0.131 | 0.293 | **0.293** | 6/79 | 0/79 |
> | `[0.5, 8.0]` H5's proposal | 0.222 | 0.348 | 0.470 | **0.247** | 0/79 | 0/79 |
> | `[0.5, 4.0]` | 0.296 | 0.464 | 0.626 | **0.330** | 0/79 | 0/79 |
> | `[0.75, 3.0]` | 0.152 | 0.404 | 0.647 | **0.495** | 0/79 | 1/79 |
>
> **The shipped range does not collapse the archive.** Spread 0.293 with 6 of 79 clamped, against a
> predicted "under 0.1". The old claim was true of the 51-session organized bake, whose masters run
> FWHM 1.8 to 2.4 px; the full bake runs **0.88 to 3.03 px radius (FWHM 1.75 to 6.06)**, a 3.44x
> span, because it includes short-focal-length sessions the organized root did not.
>
> **Lowering the floor makes the spread SMALLER, and that is arithmetic rather than an accident.**
> psf01 is a ratio of logs, so for any unclamped pair of radii the spread is
> `log2(r_hi / r_lo) / log2(max / min)`: widening the range's span divides every difference by it.
> Going from `[1, 8]` to `[0.5, 8]` takes the denominator from 3 to 4 and costs a quarter of the
> spread everywhere, to unclamp 6 masters. The measured spreads match that formula to three decimals
> (0.246 predicted against 0.247 measured, 0.328 against 0.330, 0.492 against 0.495), which is what
> says the probe is measuring the encoding rather than the archive.
>
> **The SPAN sets the resolution and the window's POSITION decides what clamps. Corrected 2026-09-06,
> a few hours after this section was first written, by the unit test that pins the arithmetic.** The
> first version of this paragraph said "the ceiling is the lever", which is wrong: `[0.5, 4.0]` and
> SAS's `[1, 8]` are BOTH 8:1, so they resolve identically, and the entire measured gain of 0.330 over
> 0.293 is unclamping the six masters pinned to SAS's floor. Buying real resolution means NARROWING
> the span, which costs clamping at one end: `[0.75, 3.0]` is 4:1 and spreads 1.5x further for exactly
> that reason.
>
> **Take `[0.5, 4.0]` anyway**, on the position rather than the span: the floor sits under the
> sharpest master (0.88 px) so nothing clamps low, and the ceiling sits above the widest input the
> exporter can produce, since a +4 px FWHM draw in quadrature on the widest master (6.06 px) reaches
> 7.26 px FWHM = 3.63 px radius. `[0.75, 3.0]` is rejected despite its better spread: it clamps a real
> master today and would clamp most degraded ones, and a conditioning input that saturates is worth
> less than one that resolves slightly less finely.
>
> **What survives of H5:** ship an own contract rather than SAS's, and do not build a SAS drop-in
> unless measured equal. What does not: the claim that the shipped encoding leaves no lever, and the
> instruction to fix it by lowering the floor. The honest size of the win is 0.330 against 0.293,
> about 13 percent more spread, not a transformation.

**H6. A Moffat-trained model generalises to real near-focus defocus, and the point where it stops
defines the advertised range.** Real defocus is a disk; the rungs nearest the anchor are the
Moffat-like regime.
*Test (needs data):* capture ladders on at least three nights with `SaveIntermediates` on (a
hardware-validation item, see section 4), apply the model to each rung with the estimator's own
psf01, compare recovered FWHM to the anchor's, and measure ringing.
*Prediction:* rungs within 1.5x the anchor FWHM recover to within 0.15 px of the anchor without
ringing; rungs beyond 2.5x ring visibly. The crossing point is the range `OnnxTianWenDeconvolver`
advertises and the strength dial clamps to.
*Kill:* ringing already at 1.2x. Then the synthetic family is wrong for real blur and the disk kernel
joins the sweep.

**H7. Position-varying kernels matter for the SH61 and ZS61, not the Samyang.** Corner degradation
is 1.3x on the refractors' green and blue and flat on the Samyang lens.
*Arms:* stationary kernel per frame against a kernel drawn per tile from the (train, filter, channel,
radius bin) profile, cells tagged with their field radius by the exporter.
*Prediction:* on held-out SH61 corners the varying arm reduces FWHM by at least 0.1 px more than
stationary; on Samyang frames the arms tie.
*Kill:* a tie everywhere. Then stationary ships and the radial bins stay a report.

**H8. Band loss on the 1-2 px band HELPS here** (the opposite of the denoiser finding), because the
target is clean: the reason to drop that band in N2N was a noisy target whose 1-2 px band was
5.18x noise.
*Arms:* band loss on 1-2 / 2-4 / 4-8 px against 2-4 / 4-8 only.
*Prediction:* the three-band arm reduces FWHM further at equal fabrication. Cheap; one smoke pair.

**H9 (experiment, above baseline). Space truth.** Public HST/JWST frames downsampled to the rigs'
scale as effectively noiseless linear truth, degraded with OUR measured PSF family. Adopted only if
it beats the own-masters baseline on the pinned split; the licence argument is in the programme doc.
Not before H1 to H5 have run.

**H10. A loss that counts stars rather than pixels lets a small net narrow width without spending
faint stars.** Pixel-wise L2, banded or not, weights error by amplitude squared times pixel count. A
faint star is ten pixels at a few sigma and contributes about 1e-4 of a 65k-pixel tile's loss, so
suppressing it is free, and E2.7 shows the net taking that offer. Per-star RATIO terms (flux, peak,
width, each against the clean target's own detections) weigh a faint star like a bright one, and they
are the gate's own criteria made differentiable, so loss and gate agree on what good means. Tested by
E2.8.

**H11. A kernel estimated from a frame's own stars keeps most of the exact-kernel ceiling.** Stars
are point sources, so a star's profile IS the frame's PSF, which is how BlurXTerminator gets by on one
estimated diameter (`--ansp`; no kernel or shape input, the network learns shape). For the
unrolled-RL route this is the whole question: if a Moffat fitted to the blurred crop's stars,
differenced against the target width, recovers within a few points of E1's table, the
physics-in-the-network design is viable at deployment; if ringing takes over, the estimator is the
blocker and not the network. Tested by E1b, before any torch is written.

## 2. Data and the degradation exporter

**One exporter, two modes, shared with the denoiser (E1 there).** Source: retained linear masters,
drizzle preferred (sharper, Moffat-fittable), session gate on `SubFwhm` below the train median
(sharpest sessions as truth). Per session, read `MasterProfiles[]` for the per-channel (FWHM, beta)
and `BinsByChannel` for ellipticity by radius.

**Degrade in linear, then export through the P0 path so the model meets the exact stretch it will
see:** for each master and each of K draws, sample per channel a Moffat with the extra FWHM and beta
drawn jointly from the (train, filter, channel) distribution (jitter around measured pairs, never
independent marginals), elongation and PA from the ellipticity bins, an optional linear smear from
the session's `GUIDERMS` cards where present; convolve the linear master per channel; add
electron-domain noise after the blur at a level drawn from the session's `NoiseMad` range (master
depth to a few times it); then `ToUnitRange` and `ApplyInputStretch` **using the TARGET frame's MTF
parameters for both sides** so input and target share one domain (a blurred frame's own median moves
a little; if each side took its own parameters the pair would encode the stretch difference as
signal). Cut the same structure-biased cells the P0 manifest chose for that session, so a P2 tile and
its P0 counterpart cover the same pixels. Label each pair with `psf01` from `HfdPsfEstimator` run on
the degraded STRETCHED frame (H2), under both encodings (H5), plus the drawn kernel parameters as
diagnostics never fed to the net. Tag cells with normalised field radius (H7).

**On-the-fly degradation in the trainer is the sample-efficient alternative** and probably the right
one after the exporter has proven the maths: export a LINEAR master tile with a 32 px margin as an
extra slot, blur and add noise in torch, apply the MTF with the frame's stored `(min, median)`
parameters (a closed form), crop the margin. It needs a parity pin that the torch MTF equals
`Image.MtfStretch` on a fixture, the same kind of pin `n2n-parity-fixture.json` is. Decide after E2
below; the exporter mode is the reference either way.

**Size:** 51 masters x 300 cells x 8 draws is about 122k pairs, 50 GB fp16 if exported; free if
on-the-fly.

**Sweep range:** extra FWHM such that the degraded total reaches 16 px (radius 8, `psf01 = 1.0` at
the SAS encoding); own encoding over `[0.5, 8]` px radius so the archive's own masters sit interior.

## 3. Model and recipe

Start where the denoiser is: the 0.81 M residual U-Net (`build_model` in `n2n_smoke.py`) with the
scalar `psf01` broadcast to a fourth plane inside the graph (a scalar INPUT, host-supplied by the
estimator, unlike the denoiser's in-graph sigma; that is the SAS signature and what
`OnnxIoNames.ImagePlusScalar` already resolves). L2 on the tile minus the 16 px rim; DoG band loss
per H8; flux-preservation regulariser (aperture sums over detected-star apertures plus per-tile mean);
Adam 2e-4 cosine, 4,000 steps, batch 8, seeds fixed, cuDNN deterministic; about 11 minutes a seed
locally. The gate: FWHM reduction on the gate slice subject to absolute-bar fabrication at the raw
floor and flux bias under 0.5 percent, then minimise ringing. NAFNet-32 only after the U-Net shows
the effect and looks capacity-bound (the denoiser found base 48 and 32 on one frontier; assume the
same until measured).

Train with stars in (the tiles have them); the pipeline applies the model to the starless plate,
which is a sparser subset of that distribution. If star artefacts show on eval, add star-masked
variants using our own detector, never a third-party star removal in the data path.

## 4. Metrics and gates

- **FWHM reduction per channel** on held-out masters, measured by `PsfProfileFit` on output and input
  (the same log-space, brightness-banded fit the archive numbers use, or the numbers are not
  comparable: a moment FWHM over a fixed aperture measures the aperture).
- **Ringing:** the minimum in an annulus at 1.5 to 3 FWHM around isolated stars, in MAD units below
  the local background; gate at 1 MAD.
- **Worms / fabrication** on the starless plate: absolute-bar spurious count (input's MAD) at or below
  the input's own; structure correlation at 1-2 px against the truth on synthetic pairs.
- **Photometric integrity** (programme section 7): signed flux bias under 0.5 percent per SNR band
  above 20, centroid shift p50 under 0.1 px, excluding saturated cores, via
  `PhotometricRepeatability.Compare` on undithered pairs.
- **Nebulosity at 4-16 px** held at parity, as for the denoiser.
- **Real validation:** AF ladder rungs against their anchor (H6). Until ladders exist, the model is
  validated on synthetic pairs only and must say so in its contract JSON.
- **Never** PSNR for selection; never an RC or SAS output anywhere in the loop.

**Every sky-time blocker in this plan lives in
[`docs/todo/hardware-validation.md`](../todo/hardware-validation.md), one home per item, and this
plan keeps only the why.** Three of them are P2's: the `SaveIntermediates` ladders H6 needs (three
nights on the two rigs the archive is dominated by, ASI533 + Samyang and SV605CC + SH61), nights on
the under-represented (train, filter) pairs so E0's per-cell draw rests on more than one session, and
a mono session, since this model is OSC by data rather than by design. The ladder entry there also
carries what E1 says a night must SPAN to be worth taking: the rungs that decide the advertised range
sit between about 1.1x and 2x the anchor's own FWHM, because below that the oracle recovers
everything and above it recovers little.

## 5. Experiments, in order

| Step | What | Cost | Decides |
|---|---|---|---|
| E0 | **DONE 2026-09-06, and no re-measure was needed** (results below): `2026-09-full` is already current-detector, its report is already rendered, and what remained was the fit. | 0 GPU, ~5 min of probes | Calibration of everything below |
| E1 | **DONE 2026-09-06, both halves.** H5 (`PsfEncodingSpreadProbe`, all 79 masters through the deployed estimator): the shipped range is not the collapse it was recorded as, lowering the floor makes the spread SMALLER, `[0.5, 4.0]` is the pick. H1 (`DeconvolutionOracleCeilingProbe`, `RichardsonLucy` with the exact kernel, 180 rows, 60 iterations): full recovery to 1.3x blur, inside 10 percent to 2.0x (1.01 to 1.02 through 1.6x, 1.09 to 1.14 at 1.6 to 2.0x), about 1.6x the truth width beyond 2x; the first table at 30 iterations read 1.6x as the boundary and was under-converged by roughly three in residual; nothing recovers below the truth; ringing bounds the range before recovery does. | a day, CPU | The ceiling and the contract floor |
| E2 | **SHIPPED 2026-09-03 as the shared exporter** (`tianwen dataset degrade --mode blur`, `DatasetDegradationExporter`): linear Moffat blur with a drawn (FWHM, beta, elongation, PA), noise after, both sides stretched with the TARGET's parameters, field-radius tag per cell, and the drawn kernel parameters in `degradations.jsonl`. Parity is stronger than planned: the clean tile derived from the RETAINED master is byte-identical to the P0 tile of the same cell (0.0 on every session measured), which pins the whole path rather than just the stretch. Still owed: psf01 labels from `HfdPsfEstimator` on the degraded stretched frame under both encodings (H2, H5), and the per-(train, filter, channel) draw distribution, which needs E0's re-measured store | 1 to 2 days | Whether pairs are honest |
| E3 | Smoke arms on the U-Net: kernel vs estimator label (H2), shared vs per-channel (H3), noise vs none (H4), two vs three bands (H8). Post a labelled comparison at 1:1 around bright and faint stars. **BLOCKED on E2.8: no arm runs on a recipe that cannot pass its own gate, and the seed count is set by E2.6's measured sd (0.027 at the null, so five seeds).** | 4 pairs x 5 seeds x 11 min | H2, H3, H4, H8 |
| E2.5 | **DONE 2026-09-06** (details below the table). `--prepare` reads `degradations.jsonl` into a `psf01.npy` beside the tiles; `--cond-psf01` conditions on that stored label instead of on measured noise; `n2n_deconv_gate.DeconvGate` selects on width, ringing and star count. Validated end to end on a two-session blur export, which is also what caught the gate's own first bug. Originally recorded as: **discovered 2026-09-06, and the reason the row above cannot start.** The trainer cannot train a deconvolver at all yet: `--prepare` reads `tiles-manifest.jsonl` only and never `degradations.jsonl`, so the psf01 the exporter now writes never reaches the cache; the conditioning plane comes from `with_sigma`, which MEASURES the input's noise rather than reading a stored label; and `n2n_gate.py`'s metrics are noise, faint amplitude and spurious sources, every one of them noise-oriented. **The gate is the consequential half: as it stands it would select the checkpoint that removes the most noise while doing nothing about sharpness, which is selecting a deconvolver for BLURRING.** It needs FWHM recovery and ringing, scored against E1's ceiling at 60 iterations. | 1 to 2 days | Whether E3 can run |
| E2.6 | **DONE 2026-09-06, and it answered a different question** (results below): no seed selected a checkpoint, the gate's star criterion had no measured null, and the loss never looked at the star scales. As planned: the power check the denoiser campaign paid for: ONE arm at six seeds, seed spread measured against the effect each of H2/H3/H4/H8 expects, then the rest sized from it. On the denoiser's E2 the seed sd beat the between-regime sd 2 to 3x, so three-seed arms could not read a one-point effect and about 31 seeds would have been needed. H4 and H8 are plausibly large enough to read at three; H2 and H3 are the ones at risk. | 6 x 11 min | E3's seed count |
| E2.7 | **DONE 2026-09-06** (results below). Band placement control, two arms x three seeds on E2.6's own seeds: the loss can see the scale now (every minimum at steps 3100 to 3400, paired deltas -0.038 and -0.043), the arms are inseparable at three seeds, and under the corrected gate not one best-width checkpoint survives, because width is bought by suppressing faint stars (0.54 of the truth's against a 0.62 floor). A high-pass, not a deconvolution. | 6 x 10 min | Whether band placement was the lever (it was not) |
| E1b | **RUN 2026-09-07, conditional pass** (results section below). E1's exact-kernel ceiling re-run with the kernel ESTIMATED from the blurred frame's own stars, two arms: width estimated with the injected shape, then both estimated. Where the estimator answers, the paired rec/truth is within 0.01 to 0.05 of exact except at 1.3-1.6x, where a systematic 1.24x width over-read makes arm i fabricate (stars 1.15x truth, rec/truth 0.95 under noise) and arm ii pass only because an over-read beta cancels it. The estimator refused 75 of 180 rows (half the noisy ones), never fitted the 512 crop, and its refusals sit on the narrowband masters. Kill line not crossed; the pre-stated refusal rule makes the estimator its own step before any unrolled RL. | 22.6 min CPU, no training | H11 |
| E2.8 | **RUN 2026-09-07, gate-level pass, observer-level fabrication** (results section below). The star-term loss: per-star flux, peak and concentration ratios against the clean target's own detections, added to E2.7 arm B's objective, same 78-session cache, five seeds paired against E2.7. Selects on 5 of 5 at out/truth 1.10 to 1.17 holding 0.84 to 0.95 of the truth's stars (control: one seed at 1.365, minima unselectable at 0.54 to 0.63 stars); on the observer session (three times quieter truth) the same checkpoints read 34 to 45x the truth's stars and widths under 1.0. E2.8b (a no-star counterpart, and the weight matched at plateau) is pre-registered; the capacity arm waits on it. | 48 min for five seeds | H10 confirmed at the gate; the recipe fabricates on transfer |
| E1c | **RUN 2026-09-07** (under E1b's results). Which of `PsfProfileFit`'s five checks refuses, tallied over E1b's rows at one RL iteration: 28 of 30 whole-frame refusals are `PoorFit`, on RICH fields (4,600 to 7,000 detections, the stack at its cap, 15 to 28 of 48 bins above the noise floor), because the percentile brightness band lands on faint stars there. The fix is the band, not the acceptance rule. | 11 min CPU | the estimator step's first fix |
| E1d | **RUN 2026-09-07, pass** (under E1b's results). The difference width by Moffat COMPOSITION (`MoffatComposition.DifferenceFwhm`, arm `est-c`) instead of a quadrature of FWHMs: estW/true 0.99 to 1.02 at every bin from 1.3x up where quadrature read 1.11 to 1.24, paired recovery within 0.02 of exact, arm i's fabrication gone (stars 0.95 against 1.15). The apparent 0.65 under-read at 1.1-1.3x is the PROBE: `PsfKernel.Build` point-samples at pixel centres, so a nominal 1 px kernel blurs like 0.6 to 0.8 px and a 0.5 px one like 0.1; against the applied kernel est-c reads 0.92 to 1.07 there too. Training labels are measured on the degraded cell and unaffected. | 25 min CPU | the width bias was arithmetic; the light end was the kernel |
| E1e | **RUN 2026-09-07** (under E1b's results). `PsfProfileFit.StarSelection.SignalFloor` (opt-in; every star over fifty MADs, brightest first) in place of the percentile band: refusals 30 of 180 from 75 (prediction under 30, kill 60); clean PoorFit 4 to 1, noisy observed PoorFit 20 to 4, TooFewStacked 2 to 7 on sparse and heavily blurred frames; width ratios unchanged within 0.02. The band was the cause. | 23.5 min CPU | the refusals were the band |
| E2.8b | **RUN 2026-09-07, not killed by one seed** (under E2.8's results). Arm N, the star term's counterpart over EMPTY target windows: observer fabrication 34-45x to 2.6-9.7x (four of five under the input's own null) as predicted, but four of five seeds lose the sharpening (selected 1.32-1.34 against an input of 1.38, stars 0.63, trained toward the identity); seed 1, the lowest weight, keeps both (1.183 selected, observer 9.7x). Arm W, the weight re-fixed at step 400: observer 22-42x, three of five under 1.30; not the mechanism. E2.8c (arm N at seed 1's fixed weight) pre-registered to say whether the weight was the cause. | 10 x ~10 min GPU | a per-star term can be honest; whether it can be honest AND sharp rides on the weight |
| E2.8c | **Pre-registered 2026-09-07** (under E2.8b's results), runs when D1's probe releases the GPU. Arm N with the star term's weight fixed at 1.8e-3 on every seed. Predicts three of five seeds at or under 1.25 selected with the observer under 14x; killed at one or none under 1.30, which sends the counterpart into the unrolled operator as a regulariser only. | 5 x ~10 min GPU | whether the honest recipe has an operating point |
| E2.9 | **RUN 2026-09-07, kill line reached for 2.1c** (section below). FWHM against air mass over 79 sessions, 8,507 subs re-measured in 62.6 min: 0 of 67 fitted sessions reach a 1.3x FWHM span explained by air mass; slopes centre on zero (SH61 -0.15 over 14 sessions, 135 mm -0.04 over 34), only the two ZS61 sessions read where Kolmogorov predicts (+0.29, +0.46) and their spans are too short. The within-night spread (p90/p10 to 1.47x) is real and not altitude. Computed air mass agrees with N.I.N.A.'s card to 0.015 on 50 of 52 sessions; SharpCap writes no site cards, so 27 sessions have none computed (a header fallback covers 15). | 62.6 min CPU | Air-mass pairs cannot reach the range; real blur is E2.10's seeing split |
| E2.10 | **Candidates listed 2026-09-07** (section below): 7 of 79 sessions reach p90/p10 of 1.15 on the gate-surviving subs, two of them 1.3x sharp-to-soft (a 60-sub Orion at 1.43, a 35-sub HD 71272 at 1.28 that is held back over a pointing question), four at 1.11 to 1.18. Seeing-split pairs: per session, the sharpest and softest thirds of the subs stacked into two masters of one night, the first real-blur validation set. Next: `stack --manifest` A and B for the Orion session. | minutes a session | The light end of the range, on real seeing |
| D1 | **BUILT 2026-09-07, off by default; CPU half measured** (under "The next four"). Per-tile psf01 at inference: `ChunkedNafnetRunner` extras per chunk, `OnnxNonStellarDeconvolver` estimating per chunk region with a frame-level fallback, so inference matches the per-cell training label. On the seven Rim masters the per-tile radius spans 8 to 61 percent p10 to p90 and 10 to 16 percent of tiles starve; the output comparison waits for the GPU. Ships with E7, once E2.8 says the route is alive. | half a day | The field-varying half of the optics blur |
| E4 | Stationary vs position-varying (H7) on the refractor trains. | 2 x 3 x 11 min | H7 |
| E5 | On-the-fly torch degradation with the MTF pin, if E3 is sample-hungry. | a day | Sample efficiency |
| E6 | Ladder capture on three nights (hardware queue); H6 scoring. | nights | The advertised range |
| E7 | Export with the own contract (`[0.5, 8]` px), parity to torch, contract JSON, `OnnxTianWenDeconvolver : INonStellarDeconvolver` through `ChunkedNafnetRunner`, an `IPsfEstimator` variant with the lower floor, backend routing. | 2 days | Ships |
| E8 | Space-truth tier (H9), only after E7 has a baseline to beat. | rented GPU | Optional |

### E2.5's results, 2026-09-06: the gate a deconvolver is selected on

**What it selects on, and why only one criterion is a threshold.** `fwhm_ratio >= 1.0` is not a
tuning knob: E1 measured that an oracle handed the EXACT kernel never produces a star narrower than
the one that was there, so crossing it is fabrication rather than success. Star count is bounded on
both sides. Ring excess is REPORTED and not thresholded, for the same reason `resid_corr` is
report-only in the denoiser's gate, and for the sharper reason that a threshold nobody measured is
exactly how the noise gate came to reject the arm that scored best (section 8).

**The star bound is bounded ABOVE, and that half is the load-bearing one.** The first smoke run
produced **ten times the truth's detections** at 200 steps and sailed straight through a lower bound
alone. A deconvolution cannot legitimately create a star the clean master does not have, and
sharpened noise reads to a detector as narrow stars, which is the failure that flatters every other
number in the table. The 1.10 tolerance is jitter allowance and is not itself measured; what IS
measured is that an unbounded version passes a model inventing an order of magnitude of stars.

**The ring statistic needs its null and the self-test says how badly.** On an untouched noisy plate
the raw annulus statistic reads **100 percent of stars** ringing. Used raw, every arm would score
100 percent forever. `n2n_deconv_gate.py --self-test` prints that alongside the width estimator's
check against known answers (2.00 / 3.06 / 4.55 px measured for 2.0 / 3.0 / 4.5), because the width
estimator is what every metric rests on and it is not obvious by inspection.

**A label is never invented.** `load_psf01` drops rows whose `Psf01Estimated` is null, which the
exporter writes exactly when its estimator found no stars and fell back to a constant radius;
training on that constant would condition the model on a number nothing measured. Where a batch
sample's slot has no label the trainer falls back to the batch median rather than to zero, which
would tell the model "no blur" about a blurred tile.

**Measured on the two-session smoke export**, the estimated and kernel labels sit 0.03 to 0.04 apart
in psf01 units with no visible dependence on star count over 1 to 73 stars per cell (median 16). An
alarm raised on four low-star rows did not survive being measured, so no minimum-star threshold was
added; the count is recorded and a consumer can filter on it.

### E2.6's results, 2026-09-06: the power check answered a different question

Six seeds of one arm, identical in every respect but the seed, run to measure the seed spread of the
selected checkpoint's `fwhm_ratio` before E3 spends a day on four arms of three. The pre-registered
prediction was a spread under 0.05. **No seed selected a checkpoint.** All six ended "NO probe passed
every gate", 240 probes without a single pass, so the pre-registered readout has no sample at all.

**The gate could not be passed by construction, and the reason is a null nobody measured.**
`stars_kept` is `detections(output) / detections(truth)`, with a pass band of `[0.90, 1.10]`. Measured
on this cache's own gate cells, the model's INPUT scores **0.763** on that statistic: the blur is what
erases the faint stars, so a model reproducing its input exactly would fail. Every seed's best probe
landed at 0.77 to 0.82, which IS the input's value, so six runs were failed for staying close to their
input by a criterion that placed "unchanged" outside its own pass band. This is the same defect as the
ring column fixed the same day, sitting one field away in the same constructor: the ring null is
measured on the input (`self.ring_null`) and the star null is not.

**A second defect inflates the same column by a tenth.** The star set is edge-trimmed on the truth
(`ok = (ys > 3) & ...`) while `out_det.sum()` counts detections anywhere including that rim, so
numerator and denominator cover different areas. The truth scores **1.096** against itself where a
self-consistency null has to be 1.000.

**Every number in this section is in the PRE-FIX convention, including the 0.763.** The two defects
are not independent: the null was measured with the same untrimmed numerator, so it is inflated by
the same tenth as everything it is being compared against. With `detect()` shared by all three
counts the truth self-scores exactly **1.000** and the input null is **0.653**. The section's
numbers stay internally consistent because the inflation is common to the null and to the model
scores it is set beside, and the replay below is unaffected for the same reason, but do not quote
0.763 against a post-fix measurement.

**Re-anchoring the band on both measured nulls, `[0.763, 1.096]`, makes every seed select**, and the
selected `fwhm_ratio` then has a seed sd of **0.027** over six seeds, which taken at face value says
three seeds is ample and two would do. It must not be taken at face value. The selected ratios run
1.387 to 1.456 against an input of **1.382**: the gate picks the narrowest passer, and the narrowest
passer is still wider than doing nothing. The spread is tight because all six models converged on the
same near-identity, and a power calculation across arms that all sit at the null will cheerfully
resolve differences between four ways of not working.

**The recipe does not deconvolve, and it degrades with training.** Across 240 probes the narrowest
output any seed reached was 1.241 against the 1.382 input, a 10 percent narrowing, and the mean FINAL
ratio is 1.444, wider than the input it was given. Width degrades monotonically with steps while the
L2 loss falls to 8e-5 and plateaus. That is the signature of MMSE regression rather than a broken
trainer: the L2-optimal estimate of a sharp master from a blurred one averages over the sharp images
consistent with that input, and averaging blurs. E1's oracle recovered most of a known blur from
these same frames given the exact kernel, so the gap is the objective and not the data.

**Amended after reading the loss: nothing in the objective looks at the scale being deblurred.** The
band term supervised DoG bands at sigma 2 to 4 and 4 to 8 px (`--band-scales "2,4 4,8"`, and
`_gauss_kernel` takes a SIGMA, not an FWHM). This project's stars are FWHM 1.80 to 2.47 px, which is
sigma 0.76 to 1.05; the probed input sits at 1.43x that, sigma 1.09 to 1.50; the injected kernel
itself is about sigma 0.78 to 1.07. Every one of those numbers falls below the FINEST supervised
band. What is left covering the stars is plain MSE, which the background dominates, so a model can
buy a lower loss by trading star sharpness for background fidelity. Width degrading with training is
then not a subtle MMSE effect, it is the objective working exactly as written. `COND_BAND_SIGMAS`
already carries the (0,1) and (1,2) pairs and the run used neither; the exclusion rationale in the
source is that a SINGLE SUB's 1-2 px band carries 5.18x the master's RMS, measured for the
denoiser's noisy target, and it does not transfer to this regime's clean-master target.

**What this changes.** E3 does not run on this recipe, and its seed count is not the open question.
The blockers in order: the two gate nulls (cheap, measured above, no retraining needed); then a
one-flag control that supervises the bands the blur actually occupies, which is nearly free and
would moot a redesign if it works; and only if that fails, a network with the blur physics built into
it: a fixed handful of Richardson-Lucy iterations with the known kernel written out as layers, and a
small learned network doing only what RL is bad at between them (noise, ringing). A loss can only
REWARD deblurring and the net may find a cheaper answer; the unrolled operator forces it, since the
output is RL applied to the input plus a learned correction, and a high-pass is not expressible.

### E2.7's results, 2026-09-06: the loss can see the scale now, and buys width with faint stars

Two arms x three seeds against E2.6's own seeds 0-2, same cache, same everything but `--band-scales`.
Arm A added the fine band (`"1,2 2,4 4,8"`, which dilutes it to weight 1.0 because the loss divides
by band count); arm B isolated it (`"1,2"`, full weight 3.0).

| seed | E2.6 min | arm A | arm B | E2.6 final | arm A | arm B |
|---|---|---|---|---|---|---|
| 0 | 1.364 | 1.290 | 1.313 | 1.389 | 1.327 | 1.341 |
| 1 | 1.241 | 1.215 | 1.199 | 1.265 | 1.240 | 1.214 |
| 2 | 1.364 | 1.349 | 1.328 | 1.459 | 1.365 | 1.339 |

The input is 1.382. Paired against E2.6 the deltas are arm A -0.038 (sd 0.031) and arm B -0.043
(sd 0.008), consistent in sign 3 of 3 in both arms, and **the two arms are not separable at three
seeds** against a seed sd near 0.046: the band SET is not the thing to tune next.

**The one robust difference is WHERE the minimum sits.** E2.6's minima came at steps 400, 2800 and
100 of 4000, two of three effectively at the untrained model. Every one of E2.7's six lands at 3100
to 3400 and is still falling at the budget. So the objective is no longer fighting the goal.

**But the fixed gate refuses almost all of it, and that is the finding.** Arm B seed 2 is the first
run in this campaign to select anything, and it selected **step 100, score 1.365**, barely under the
1.382 input, rather than its own 1.328 at step 3300. The reason is in the star column:

```
step   100  out/truth 1.365  stars 0.64  pass      step  1300  out/truth 1.639  stars 0.48  FAIL
step   500  out/truth 1.476  stars 0.62  pass      step  3300  out/truth 1.328  stars 0.54  FAIL
```

The floor is 0.620, being 0.95 of the measured input null of 0.653. Every checkpoint that reaches a
deep minimum is holding 0.54 of the truth's stars, which is well under what its own INPUT still had:
the width is being bought by suppressing faint stars. Converting the other five seeds out of pre-fix
units (divide by the 1.096 self-score) puts their minimum-width checkpoints at 0.57 to 0.60, all
below the same floor. **Not one arm's best-width checkpoint would survive the corrected gate.**

That is the opposite of a deconvolution. Concentrating a star's flux RAISES its peak against the
background, so a real inversion should make faint stars easier to detect, not harder. A high-pass
that sharpens bright structure and attenuates everything near the noise floor reproduces the band
statistic the loss is asking for while doing none of the work, and that is what the band term
appears to have taught.

**Two corrections to E2.6 above.** "Width degrades monotonically with steps" is wrong: both
campaigns run a hump, worst around step 1300-1600 (E2.6 peaks at 1.53 to 1.66) and then partially
recover. What actually separates them is whether the model ever beats its own step-100 value, which
E2.6 largely did not and E2.7 does. And the reading "the binding constraint is now the step budget,
so train longer" does not survive this table: more steps drive the star count further down, so a
longer run buys width at exactly the price the gate exists to refuse.

**Unit warning.** Arm B seed 2 ran after the gate fix landed and its star column is in POST-fix
units; the other five are pre-fix and read about a tenth high. The width column is unaffected (the
fix touched star detection only), which is why the table above is comparable throughout. Each seed
is a fresh process, so an edit mid-campaign reaches the seeds not yet started.

### The next four, pre-registered 2026-09-06: E1b, E2.8, E2.9, E2.10 (and D1)

**What they rest on.** Four facts, from E2.6, E2.7 and a look at BlurXTerminator's public surface.

1. **The loss counts pixels.** Any pixel-wise L2, banded or not, weights error by amplitude squared
   times pixel count; a faint star is ten pixels at a few sigma and is about 1e-4 of a tile's loss.
   E2.7's high-pass is that arithmetic working as written, not a subtle MMSE effect.
2. **Session breadth is not the gap.** The cache E2.6 and E2.7 trained on is 78 sessions and 28,080
   degraded rows (360 a session, `D:\Astro-Dataset\degraded\p2-blur`, 2026-09-06). "More data" is
   therefore not the first lever. Capacity might be (0.81 M parameters against the 20 to 30 M Croman
   calls saturated), but it comes after the loss, since a bigger net under the same objective has
   the same incentive to drop faint stars.
3. **Scalar conditioning is not what failed.** BXT takes ONE estimated PSF diameter (`--ansp`, with
   `--nsd` as the manual value in [0, 8] px) and no kernel or shape input; the network learns shape.
   That is our psf01 design, and BXT is existence proof that it can deconvolve real frames.
4. **The deployed blur is seeing plus optics, and it has no sharper truth.** Stars are points, so the
   "true" image has them as deltas and the ratio is unbounded. The product is a REDUCTION by a dialled
   ratio, which makes the injected DIFFERENCE kernel, drawn from the archive's own measured PSF
   family, the right training object. The right validation is the same field under different
   atmosphere, not defocus: a defocus PSF is a disk, and BXT is not for it either. Focus ladders drop
   to an out-of-family check.

**Order and cost.** E1b and E2.9 are CPU and run together; E2.8 is about an hour of GPU on the 1070
(seeds cost 10 to 15 minutes each; E2.7's six ran in an hour). E2.10 follows E2.9. D1 waits for
E2.8's verdict. Nothing here needs sky time.

#### E1b: the estimated-kernel ceiling (H11)

*Method.* E1's 180 rows, the same crops and noise arms, 60 iterations. Per row, `PsfProfileFit` on
the observed (blurred) crop's stars gives FWHM_obs and beta_obs; on the clean crop, FWHM_clean. The
difference width is `sqrt(FWHM_obs^2 - FWHM_clean^2)`, exact for Gaussians and approximate for a
Moffat, and that approximation is part of what is measured. Under N stars in the crop, the fit falls
back to the whole degraded frame and the row records that it did. Two arms, compared row by row
against E1's exact-kernel result: **i** width estimated, shape (beta) exact; **ii** width and shape
both estimated. The readout adds the estimate's own error (estimated over true width, estimated
over true beta) so a ceiling loss can be attributed to width or to shape.

*Prediction.* Arm i within 0.03 of the exact rec/truth through 1.6x and within 0.05 at 1.6 to
2.0x, ring excess up by at most ten points; arm ii loses more, and through ringing where beta is
misread, because the wings are where a Moffat fit is weakest (11 of 163 railed in E0). The noisy
arm's stars column is the fabrication tell: below E1's 0.71 at 1.6 to 2.0x while width improves means
the estimate is sharpening noise. Confidence moderate: the fit is taken on the very stars RL acts on.

*Kill.* Arm ii rec/truth above 1.15 at 1.3 to 1.6x, or ring excess above 50 percent at 1.1 to
1.3x. Then the unrolled-RL route is blocked on the ESTIMATOR, which becomes its own step (a stacked
per-channel star profile fitted in log space, as `PsfProfileFit` already does per master) and not a
torch job.

*Cost and code.* About 45 minutes CPU (E1 at 60 iterations took 45). A screen of code in
`DeconvolutionOracleCeilingProbe` behind `TIANWEN_ORACLE_KERNEL=estimated|estimated-shape`,
recorded under "Reproducing" once it exists. No training, no GPU.

#### E2.8: the star-term loss (H10)

*Change.* `--prepare` writes `stars.npy` beside the tiles: each tile's CLEAN target run through
`n2n_deconv_gate.detect()`, the gate's own detector, so loss and gate see the same stars; up to 32 a
tile with a validity mask, positions and target peak. The trainer gains `--star-loss W`. For every
valid star a 7x7 window is taken on output and target and three ratio terms are formed: log
aperture-flux ratio (r <= 3), log peak ratio, and log concentration ratio (energy within r <= 1 over
energy within r <= 3, which rises when a star tightens), each as an absolute value, averaged over
stars with EQUAL weight. `W` is set once so the term equals L2's magnitude on the first batch and is
then FIXED and logged, never tuned on the gate. Everything else is E2.7 arm B: `--band-scales "1,2"`,
base 32, 4000 steps, gate every 100, the same 78-session cache.

*Arms.* One, at five seeds (0 to 4). E2.7 arm B seeds 0 to 2 are the paired control on the identical
cache; nothing is re-exported.

*Prediction.* The gate selects on at least 4 of 5 seeds; the selected fwhm_ratio is at or under 1.30
(E2.7's best MINIMUM was 1.328, and it held only 0.54 of the stars); at the width minimum the stars
column reads at or above 0.60 against E2.7's 0.54 to 0.60, meaning the minimum itself becomes
gate-eligible. Confidence moderate. The term rewards exactly what the gate demands, but a 7x7 window
and a concentration proxy are blunt, and a net could sharpen the windowed stars while suppressing
the ones the detector missed below `STAR_SIGMA`; the observer therefore also reports `stars_kept` at a
LOWER sigma, which is the check for that.

*Kill.* Width minima still hold under 0.60 of the truth's stars in 3 or more of 5 seeds, or the
selected width is at or above 1.36 (no gain on arm B seed 2's 1.365). Then pixel-domain losses are
exhausted for this net, and the fork is: unrolled RL if E1b passed, capacity (NAFNet-32) if it did
not. Both carry the star term regardless, since a learned correction inside RL has the same
incentive under plain L2.

*Power.* E2.7's paired sd was 0.008 (arm B) to 0.031 (arm A). Five seeds resolve a paired difference
of about 0.04; the predicted width effect (1.328 to at most 1.30) sits at that edge, which is why the
PRIMARY readout is the stars column at the minimum, a 0.06 effect (0.54 to 0.60) against a smaller
spread, and the width is secondary.

*Cost.* 5 x 10 to 15 minutes on the 1070; the detector pass at `--prepare` is minutes. Launch script
`run-p2-starloss.ps1` with this prediction and kill line in its header, as the others.

#### E2.9: FWHM against airmass over the archive (2.1c's first step)

*Change.* `SessionPsf` gains `SubFile[]`, `SubEpochUtc[]` and `SubAirmass[]`, aligned index for
index with the existing `SubFwhm[]`, which today carries 49 widths for the first session and no way
to say which sub each one is. Airmass is COMPUTED from `DATE-OBS`, `OBJCTRA`/`OBJCTDEC` and
`SITELAT`/`SITELONG` the way `DatasetGradientReport` already does per master; a header `AIRMASS` card,
where a sub has one, is recorded beside it as a cross-check and never relied on. A measure-only
switch runs the `measure` stage alone (112 s a session, 147 minutes over 79 sessions) instead of the
full re-registration `ForcePsfRemeasure` implies (13.3 hours for the bake); the store appends
last-wins as it already does, so the earlier records stay readable.

*Readout.* Per session: the log-log slope of FWHM against airmass (Kolmogorov seeing predicts 0.6),
the airmass span, and the FWHM span the slope explains. Pooled, by optical train: how many sessions
reach a 1.3x FWHM span attributable to airmass.

*Prediction.* Slopes of 0.3 to 0.6 on the longer-focal trains (SH61, ZS61); the 135 mm sessions
flatter, because 2 px sampling floors the FWHM; few sessions spanning enough airmass for 1.3x, since
most targets were imaged near culmination. Confidence low, which is the point: this is the
measurement 2.1c asked for before any pairing is built.

*Kill (for 2.1c pairing).* No session reaches a 1.3x span attributable to airmass. Then airmass
pairs cannot reach the range that matters (E1 puts it at 1.1x to 2x), real-blur validation stays at
the light end with E2.10, and the heavy end stays synthetic, a KNOWN limit of the advertised range
rather than an assumption.

*Cost.* One field, one switch, about 2.5 hours of unattended CPU, one python plot.

**E2.9 ran 2026-09-07: 62.6 min for 8,507 subs over 79 sessions (428 ms a sub, measure stage only),
`C:/temp/e2/e29-airmass-report.txt`, `-either.txt`, plot `e29-fwhm-airmass.png`. Kill line reached
for 2.1c: 0 of 67 fitted sessions reach a 1.3x FWHM span attributable to air mass.**

| train | sessions | slope p50 | in [0.3, 0.9] | air-mass span p50 | explained span, best |
|---|---:|---:|---:|---:|---:|
| SH61 EDPH at 270 mm (SV605CC) | 14 | -0.15 | 1 of 14 | 1.35x | 1.14x |
| Samyang 135 at 130 mm (ASI533, 2025-26) | 34 | -0.04 | 1 of 34 | 1.13x | 1.04x |
| Samyang 135 at 130 mm (ASI533, 2024, header air mass) | 9 | -0.08 | 0 of 9 | 1.27x | 1.02x |
| SAMYANG 135mm at 130 mm (ASI533, 2025) | 3 | +0.03 | 0 of 3 | 1.78x | 1.04x |
| ZS61 at 289 mm (ASI585, header air mass) | 2 | +0.29 and +0.46 | 1 of 2 | 1.16x | 1.09x |
| RC51 at 250 mm (ASI1600) | 1 | -0.38 | 0 of 1 | 1.16x | 0.95x |
| ASI1600 at 180 mm (header air mass) | 1 | +0.17 | 0 of 1 | 1.31x | 1.05x |
| ASI585 at 369 / 36 / 24 mm (header air mass) | 3 | -0.88, +0.22, -0.03 | 0 of 3 | 1.12 to 1.22x | 1.03x |

The prediction held for ZS61 alone (the two sessions read where Kolmogorov puts them, over spans of
1.16x and 1.32x that let them explain 1.07x and 1.09x) and failed for SH61, whose fourteen sessions
centre on a slope of -0.15; the 135 mm sessions are flat as predicted; and the "few sessions span
enough air mass" clause was the binding one, since 1.3x of FWHM at slope 0.6 needs a 1.55x span and the
median session has 1.13x to 1.35x. The observed within-night spread is real and is not air mass:
p90/p10 runs 1.01x to 1.47x, and the session with the largest (1.47x, SH61, 60 subs) has slope -0.59,
sharper as it set. Focus, wind and the night's own seeing move a sub more than its altitude does at
these focal lengths.

*Cross-check.* Computed against the capture software's `AIRMASS` card where both exist: median
absolute difference 0.000 to 0.015 on 50 of the 52 sessions carrying both, 0.034 on one, 0.311 on
one 35-sub Samyang session (which is also an E2.10 candidate, and gets checked before it is paired).
`SiteContext.Airmass` is therefore validated against N.I.N.A.'s, which is what makes the report's
`--airmass either` an evidence-based extension rather than the substitution the pre-registration
forbade.

*A gap the run found.* 27 of 79 sessions have no computed air mass at all, and one sub of each says
why: every SharpCap capture in the archive writes `OBJCTRA`/`OBJCTDEC`, `RA`/`DEC` and `DATE-OBS`
and **no `SITELAT`/`SITELONG`** (27 of 27); N.I.N.A. sessions carry all of them. SharpCap 4.1 writes
`AIRMASS` itself and 4.0 did not, which is why the header fallback fits 15 of the 27 and the twelve
SharpCap 4.0 sessions (nine Vela SNR panels, Omega Cen, the 2022 Eta Car, the SII Eta Car) stay
unfitted. Owed: a site fallback for the dataset build (the profile's site, or a `--site lat,lon`
switch) so `SubAirmass` is computed for SharpCap sessions too. Sixty minutes of measure stage, when
it is built.

*Consequence.* Air-mass pairing is dead as a real-blur source in this archive, as the kill clause
says: real-blur validation is E2.10's within-night seeing split, cause-agnostic, and the heavy end
stays synthetic as a known limit of the advertised range.

#### E2.10: seeing-split pairs (real-blur validation at the light end)

Given E2.9's per-sub identity, for each session whose `SubFwhm` p90/p10 is at least 1.15: the
sharpest third of the subs to a manifest and `stack --manifest` for master A, the softest third for
master B, both registered to the session's reference, both retained beside the session master. A is
the truth at the light end, B the input. Score any deconvolver on B against A at matched output
width (E1's readout), and run the gate's observers on the pair. *Prediction:* 2 to 6 sessions qualify
(the earlier store found two of 51 at 1.2), and a deconvolver passing E2.8's gate moves B toward A
on width without dropping below A's star count. *Cost:* minutes a session; no new code beyond the
manifest writer. *Data:* none from the sky.

**E2.9's store lists the candidates (2026-09-07, `tools/psf-seeing-split.py`,
`C:/temp/e2/e210-seeing-split.txt`): 7 of 79 sessions reach p90/p10 of 1.15, one over the predicted
2 to 6.** Sharpest third against softest third of the gate-surviving subs, median FWHM in px:

| session | train | subs | p90/p10 | sharp third | soft third | soft/sharp |
|---|---|---:|---:|---:|---:|---:|
| Great Orion Nebula, 2025-10-15, L-Quad | SH61 at 270 mm | 60 | 1.47 | 1.73 | 2.47 | 1.43 |
| HD 71272 (folder HD-258924), 2026-01-20, L-Ultimate | Samyang 135 at 130 mm | 35 | 1.35 | 2.17 | 2.78 | 1.28 |
| Eta Car SII, 2024-03-02 | QHY at 360 mm | 36 | 1.25 | 2.83 | 3.14 | 1.11 |
| Great Orion Nebula, 2025-12-17, L-Ultimate | Samyang 135 at 130 mm | 126 | 1.24 | 2.07 | 2.41 | 1.17 |
| Tarantula Nebula, 2025-10-18, L-Quad | SH61 at 270 mm | 171 | 1.20 | 2.20 | 2.59 | 1.18 |
| SMC, 2024-09-27, L-eNhance | ASI585 at 369 mm | 120 | 1.19 | 2.72 | 3.13 | 1.15 |
| Lagoon HaOIII, 2024-07-03 | ASI533 at 130 mm | 38 | 1.15 | 2.14 | 2.41 | 1.13 |

Two reach the 1.3x where E1 says the range begins to matter and four sit at 1.11 to 1.18, the
lightest end. Thirds of a 35-sub session are twelve-sub masters, noisier than anything the deconvolver
is deployed on, so the first pair is the 60-sub Orion session at 1.43 (twenty-sub halves), then the
171-sub Tarantula at 1.18 for depth. The HD 71272 session is held back: its folder names one star and
its `OBJECT` card another, and it is the one session whose computed and header air mass disagree
(0.31), so its pointing is checked before a pair is built from it. E2.9 also says what the split
means: these spreads are within-night seeing, focus and wind, not altitude, so a pair is a real blur
of unknown shape, which is exactly what the synthetic arms cannot supply.

#### D1: per-tile psf01 at inference

Training labels psf01 per 256 px cell; inference hands `OnnxNonStellarDeconvolver` ONE radius per
frame, so a frame whose PSF falls 4.03 to 3.12 px centre to corner (Rim) is told one number
everywhere. `ChunkedNafnetRunner`'s extras become a per-chunk callback and the deconvolver estimates
per chunk region with `HfdPsfEstimator.MeasureRadiusPxAsync`, falling back to the frame value under
N stars. Measure on Rim. Ships with E7, once E2.8 says the route is alive; not needed for E1b or
E2.8.

**D1 built 2026-09-07, off by default, and the CPU half of its measurement taken.**
`OnnxNonStellarDeconvolver(perChunkPsf: true)` conditions each tile on its own region's estimate:
`ChunkedInference.Layout` is the tile grid without the pixels (`Split` is built on it), the runner
takes per-chunk extra inputs by index, `IPsfEstimator.EstimateChunkAsync` gains an overload carrying
the whole-image value as the fallback, and `HfdPsfEstimator` measures the region's stars, answering
the whole-image value under eight of them. The switch stays off until the OUTPUT is measured per-tile
against whole-image, because the shipped SAS AI4 graph was trained on whole-image labels. What the
per-tile estimate itself reads on the seven Rim masters (`PerChunkPsfProbe`, radius in px decoded over
`[0.5, 4.0]`, 289 to 324 tiles a master, `C:/temp/e2/d1-perchunk-rim.txt`):

| master | starved tiles | whole-image | tile p10 | tile p50 | tile p90 | p90/p10 | centre / corners |
|---|---:|---:|---:|---:|---:|---:|---:|
| Rim HaOIII 135m, 2024-07-03 | 30 | 0.90 | 0.85 | 0.91 | 0.99 | 1.16 | 0.96 |
| Rim LPS RGB, 2024-06-06 | 31 | 0.88 | 0.84 | 0.88 | 0.91 | 1.08 | 1.01 |
| Rim SII, 2024-07-06 | 31 | 1.11 | 1.08 | 1.19 | 1.32 | 1.22 | 1.05 |
| Rim L-Ultimate, 2025-05-02 | 37 | 1.63 | 1.27 | 1.72 | 1.91 | 1.50 | 1.23 |
| Rim L-Ultimate, 2026-02-16 | 34 | 1.33 | 1.29 | 1.54 | 1.87 | 1.45 | 1.14 |
| Rim L-Ultimate, 2026-02-18 | 33 | 1.12 | 0.93 | 1.20 | 1.48 | 1.59 | 1.12 |
| Rim L-Ultimate, 2026-02-20 | 46 | 1.15 | 0.96 | 1.25 | 1.55 | 1.61 | 1.23 |

Three things it says. The 2025-26 sessions vary 45 to 61 percent from p10 to p90 across one frame,
and their CENTRE is the soft end (12 to 23 percent wider than the corner tiles), so one psf01 per
frame tells most of their tiles the wrong width by more than the E1 encoding resolves; the 2024
sessions on the same camera are flatter (8 to 22 percent). The whole-image value sits at or below
the tile median on every master (a median over all detections weights the dense regions, which here
are the sharp ones). And 10 to 16 percent of tiles starve at eight stars and take the whole-image
value, so the fallback is a real fraction of a frame, not a corner case. The GPU half, whether the
graph's output is better when told the local width, waits for E2.8b to release the card.

*The GPU half, pre-registered.* `PerChunkPsfOutputProbe`: the shipped SAS AI4 graph run twice on each
of the seven Rim masters (rescaled to unit range), whole-image and per-tile, and the stars of the
input and both outputs measured in three field-radius bins (inner, middle, outer third of the
half-diagonal). Off-label for a non-stellar deconvolver, but stars are the only PSF probe a frame
offers, and the comparison is relative. *Prediction:* per-tile moves the output's centre-to-corner
FWHM ratio toward 1 on the four 2025-26 sessions (the soft centre is told it is soft and is
deconvolved more), by at least a third of the whole-image output's departure from 1; star counts
per bin within 10 percent of the whole-image run's; the three flatter 2024 sessions change by less
than the encoding resolves. *Kill:* per-tile makes any bin's stars narrower than the INPUT's sharpest
bin (a deconvolver told the truth about local width should not cross the frame's own best), or a
bin's star count departs from the whole-image run's by over 20 percent. Then per-tile conditioning
is not safe on a graph trained with whole-image labels and stays off.

### E1b's results, 2026-09-07: the estimate is good where it exists, and it exists for half the rows

Run as pre-registered (`TIANWEN_ORACLE_KERNEL=exact,estimated,estimated-shape`, 60 iterations, the
same six masters and noise seeds, all three arms on one observed frame per row so the comparison is
paired), 22.6 minutes with the arms in parallel. One thing was added to the launch header before the
run, from a one-master smoke: the 512 px crop never supported a `PsfProfileFit`, and several noisy
whole-frame fits were refused too, so the summary counts refusals per bin and a high refusal rate on
the noisy arm was declared a kill-class finding in advance. Output: `C:/temp/e2/oracle-e1b.txt`.

| blurred/truth | noise | arm | n | rec/truth | ring excess | stars vs truth | d(rec/truth) p50 / p90 | estW/true | estB/true | no fit |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1.1-1.3x | no | exact | 17 | 1.00 | 15 % | 1.00 | | | | |
| 1.1-1.3x | no | est-w | 17 | 1.03 | 11 % | 1.02 | +0.03 / +0.07 | 0.92 | 1.10 | 6 |
| 1.1-1.3x | no | est-wb | 17 | 1.03 | 13 % | 1.01 | +0.03 / +0.06 | 0.92 | 1.10 | 6 |
| 1.1-1.3x | yes | exact | 15 | 0.98 | 9 % | 0.91 | | | | |
| 1.1-1.3x | yes | est-w | 15 | 1.02 | 7 % | 0.97 | +0.04 / +0.08 | 0.94 | 1.26 | 9 |
| 1.1-1.3x | yes | est-wb | 15 | 1.03 | 7 % | 0.97 | +0.05 / +0.10 | 0.94 | 1.26 | 9 |
| 1.3-1.6x | no | exact | 9 | 1.02 | 48 % | 0.96 | | | | |
| 1.3-1.6x | no | est-w | 9 | 1.00 | 69 % | **1.15** | -0.05 / -0.04 | **1.24** | 1.57 | 3 |
| 1.3-1.6x | no | est-wb | 9 | 1.02 | 65 % | 1.09 | -0.04 / +0.01 | 1.24 | 1.57 | 3 |
| 1.3-1.6x | yes | exact | 9 | 1.01 | 23 % | 0.65 | | | | |
| 1.3-1.6x | yes | est-w | 9 | **0.95** | 25 % | 0.70 | -0.02 / -0.01 | 1.23 | 1.50 | 5 |
| 1.3-1.6x | yes | est-wb | 9 | 1.00 | 23 % | 0.68 | +0.00 / +0.00 | 1.23 | 1.50 | 5 |
| 1.6-2.0x | no | exact | 14 | 1.15 | 82 % | 1.04 | | | | |
| 1.6-2.0x | no | est-w | 14 | 1.10 | 83 % | 0.94 | -0.03 / +0.03 | 1.16 | 1.35 | 5 |
| 1.6-2.0x | no | est-wb | 14 | 1.12 | 83 % | 0.95 | -0.03 / +0.02 | 1.16 | 1.35 | 5 |
| 1.6-2.0x | yes | exact | 17 | 1.09 | 42 % | 0.73 | | | | |
| 1.6-2.0x | yes | est-w | 17 | 1.07 | 48 % | 0.76 | -0.01 / +0.02 | 1.16 | 1.42 | 8 |
| 1.6-2.0x | yes | est-wb | 17 | 1.07 | 50 % | 0.73 | -0.01 / +0.02 | 1.16 | 1.42 | 8 |
| 2.0-3.0x | no | exact | 20 | 1.59 | 95 % | 0.80 | | | | |
| 2.0-3.0x | no | est-w | 20 | 1.54 | 96 % | 0.59 | -0.01 / +0.06 | 1.11 | 1.10 | 5 |
| 2.0-3.0x | yes | exact | 18 | 1.64 | 81 % | 0.51 | | | | |
| 2.0-3.0x | yes | est-w | 18 | 1.68 | 74 % | 0.43 | +0.02 / +0.09 | 1.11 | 1.14 | 5 |

The `<1.1x` and `3.0x+` bands are in the file and add nothing: the first is at d = +0.01 with the
width estimate meaningless by construction (a 0.5 px injection on a 3 px star is a quadrature
difference of two nearly equal numbers, estW/true 0.32 to 0.47), the second has four or five rows.
`n` counts every row in the band; `no fit` is how many of those the estimator refused, and the
other columns are medians over the rest, so a bin with a high refusal count is conditional on the
rows the estimator found easy.

**The kill line is not crossed.** Arm ii's rec/truth at 1.3-1.6x is 1.00 to 1.02 against the 1.15
limit, and the 1.1-1.3x ring excess is 7 to 13 percent against 50. **The prediction largely
holds**: arm i is within 0.03 of exact through 1.3x noise-free (+0.03, at the bound; +0.04 noisy,
just over), within 0.05 at 1.6-2.0x (-0.01 to -0.03) with ring excess up 1 to 8 points, and the
noisy stars column never falls below exact's.

**The one miss is the 1.3-1.6x band, and it is a systematic width error, not noise.** The
estimated difference width reads 1.23 to 1.25 times the injected one on three different masters
there (and 1.16 at 1.6-2.0x, 1.11 at 2-3x, 0.92 to 0.94 at 1.1-1.3x, so the bias runs with the
ratio and is not a constant). Handed a kernel a quarter too wide, arm i over-deconvolves:
noise-free it lands AT the truth width (1.00 against exact's 1.02) while detecting 15 percent more
stars than the truth has, which is the fabrication signature E1 defined, and ringing rises 21
points; under noise it recovers to 0.95 of the truth, below the line an oracle never crosses (one
row reads 0.88 with 17 percent more stars). **Arm ii is better in exactly that band, the opposite
of the prediction, and for the wrong reason**: the observed profile's beta over-reads the
difference kernel's by 1.45 to 1.62x there, a lighter-winged kernel of the same core width
deconvolves less, and the two errors cancel to within 0.00 to 0.04 of exact. A cancellation is not
an estimator that works; it says the shape estimate is as wrong as the width estimate and happens
to be wrong the helpful way.

**The finding that matters is availability.** The estimator refused 75 of 180 rows: 30 of 90
noise-free and 45 of 90 noisy, so at the frame depth a trained net is actually given it answered
half the time. It never once fitted the 512 px crop (every fit in the table is the whole-frame
fallback), and the refusals are not spread evenly: the ASI533 L-Ultimate master accounts for 33,
the SV605 L-Ultimate for 15 and the 7 px Eta Car channel for 14, so 62 of 75 are narrowband
frames or a very wide PSF. Narrowband is 5,385 of the archive's 8,573 lights. `PsfProfileFit`
returns null without saying which of its five refusals fired (too few stars in the brightness
band, too few stacked after isolation, no half-maximum crossing, fewer than eight fit bins above
the noise floor, or a log-space residual over 0.5), so the reason is not yet known; instrumenting
that is the first move of whatever comes next.

#### E1c, pre-registered 2026-09-07: which check refuses

*Change.* `PsfProfileFit.Measure` gains an overload with a `Diagnostics` out-parameter naming the check
that refused (`TooFewStars`, `TooFewStacked`, `NoHalfMaximum`, `TooFewFitBins`, `PoorFit`) and the
counts it tested; the probe prints it per refused row and tallies whole-frame refusals by check, by
noise arm and by master. Re-run at ONE RL iteration (the recovery columns are not read; the tally
is), `TIANWEN_ORACLE_KERNEL=exact,estimated`, same masters and seeds, minutes rather than an hour.

*Prediction.* The noisy refusals are `PoorFit`: the fit is in log space over the wings, the noisy
arm adds a sigma equal to the frame's own MAD, and the outer bins the fit weights most are the ones
noise moves most (E0 saw the same failure signature, a residual of 0.77 to 0.98 with beta collapsing
toward the grid floor). The noise-free refusals on the narrowband masters are `TooFewStacked` or
`TooFewStars`: a 3 nm filter leaves few stars, the brightness band keeps a fifth of them, and the 16 px
isolation radius removes more. The 7 px channel refuses as `PoorFit` or `NoHalfMaximum`: its wings run
past the 12 px background annulus, so the subtracted profile is wrong before it is fitted. Confidence
moderate on the first, low on the split between the other two.

*Kill.* None, it is a diagnostic. What it decides is the estimator step's first fix: a `PoorFit`
majority points at the acceptance rule and the noise weighting of the log-space fit; a
`TooFewStacked` majority at the band and the isolation radius; `NoHalfMaximum` or bins at the sampled
radius.

**E1c's results, 2026-09-07 (11 minutes at one iteration, `C:/temp/e2/oracle-e1c.txt`).** Thirty
whole-frame refusal events reproduce E1b's 75 refused rows exactly (five CLEAN channels refuse and
take all ten of their rows; 25 observed frames refuse, 20 of them noisy). **28 of the 30 are
`PoorFit`**, two are `TooFewStacked` (the 7 px Eta Car channel, 38 stacked against a floor of 40).
The first half of the prediction holds: every noisy refusal is `PoorFit`. The second half is wrong in
an instructive way: the narrowband masters are not star-poor, they refuse on SHAPE, with 4,600 to
7,000 detections, the stack at its 400 cap, and residuals of 0.76 to 1.29 on the clean master (0.57 to
1.08 on the observed), against the 0.07 to 0.22 a healthy fit reads. The tell is the bin count: 15 to
28 of 48 radial bins sit above the noise floor where a fitted frame has 35 to 42. The brightness band
is a PERCENTILE of the frame's detections (55th to 75th), so on a rich field it lands on faint stars,
their wings reach the floor within three or four pixels, and the log-space fit that weights every
decade equally is fitting the noise in the outer bins. The crop never fits for the plain reason the
counts give: 7 to 19 stacked from a band of 12 to 41, against a floor of 40.

So the estimator step's first fix is the band, not the acceptance rule: select the stack by an
absolute signal floor (a peak in MADs, below saturation) rather than by the frame's own percentiles,
so a rich field stacks its bright isolated stars instead of its 60th-percentile ones. To be
pre-registered as E1e on the same 180 rows once E1d has run, since the two fixes are separable and
E1d's width composition uses whatever fit the band produces.

#### E1d, pre-registered 2026-09-07: the difference width by Moffat composition

*Why.* The 1.24 over-read at 1.3-1.6x was guessed above to be what quadrature does to a Moffat, and a
numeric check (two profiles composed on a 0.05 px grid, no detection, no noise) says so: a 2 px beta-4
kernel on a 2.15 px core reads 1.21x through `sqrt(obs^2 - clean^2)` at beta 5 and 1.28x at beta 3;
at 1.6-2.0x the same arithmetic gives 1.14 to 1.19 (E1b: 1.16) and at 2-3x 1.10 to 1.14 (E1b: 1.11).
Only the light end disagrees (predicted 1.30 to 1.45, E1b 0.92 to 0.94), where a 0.1 px error in the
observed width moves the quadrature by 15 percent and the measurement, not the arithmetic, is in
charge.

*Change.* `MoffatComposition` (`TianWen.Lib/Imaging/Degradation/`): the composed FWHM of two Moffats
by direct radial integration, and its inverse by bisection, pinned by tests on the Gaussian limit, the
round trip and the size of the quadrature error. A fourth kernel source in the probe,
`estimated-composed` (`est-c`): the same two `PsfProfileFit` fits as arm i, the width by
`DifferenceFwhm(clean, observed, beta 4)` instead of quadrature, shape exact. Same rows, same seeds,
60 iterations, alongside `exact` and `estimated` so the paired difference reads against both.

*Prediction.* `est-c`'s estW/true at 1.3-1.6x lands within 0.95 to 1.05 (from 1.24), at 1.6-2.0x
and 2-3x within 0.95 to 1.05 (from 1.16 and 1.11); its paired d(rec/truth) at 1.3-1.6x within 0.03
of exact with the stars column back at or under 1.02 noise-free, so the fabrication of arm i is gone;
at 1.1-1.3x no change (the estimate there is noise-dominated). The refusal count is unchanged, since
the fits are the same fits. Confidence high on the width ratios, moderate on the recovery columns.

*Kill.* `est-c`'s estW/true at 1.3-1.6x still above 1.10. Then the bias is not the composition
arithmetic and sits in the MEASUREMENT of the blurred frame (the brightness band selecting different
stars once the peaks drop, and the faint-star widening `PsfProfileFit` documents), which is a different
fix.

**E1d ran 2026-09-07, 25.1 min, 60 iterations, `C:/temp/e2/oracle-e1d.txt`. Pass at every bin from
1.3x up, and the light end is the probe's kernel, not the estimator.** Refusals 75 an arm, the same
30 whole-frame refusals as E1b and E1c, as predicted (the fits are the same fits). The width ratio,
the paired recovery and the two fabrication observers, `est-w` beside `est-c` on the same rows:

| blur | noise | estW/true, est-w | estW/true, est-c | est-c d(rec/true) p50 / p90 | stars vs truth, exact / est-w / est-c | ring excess, exact / est-w / est-c |
|---|---|---:|---:|---:|---:|---:|
| 1.3-1.6x | no | 1.24 | 0.99 | +0.01 / +0.02 | 0.96 / 1.15 / 0.95 | 48 / 69 / 44 % |
| 1.3-1.6x | yes | 1.23 | 1.00 | +0.02 / +0.03 | 0.65 / 0.70 / 0.63 | 23 / 25 / 23 % |
| 1.6-2.0x | no | 1.16 | 0.99 | +0.00 / +0.02 | 1.04 / 0.94 / 0.94 | 82 / 83 / 76 % |
| 1.6-2.0x | yes | 1.16 | 0.99 | +0.00 / +0.03 | 0.73 / 0.76 / 0.73 | 42 / 48 / 47 % |
| 2.0-3.0x | no | 1.11 | 1.00 | +0.00 / +0.01 | 0.80 / 0.59 / 0.76 | 95 / 96 / 95 % |
| 2.0-3.0x | yes | 1.11 | 1.00 | +0.00 / +0.01 | 0.51 / 0.43 / 0.42 | 81 / 74 / 74 % |
| 3.0x+ | no | 1.13 | 1.02 | +0.00 / +0.03 | 0.83 / 0.91 / 0.83 | 29 / 33 / 30 % |
| 3.0x+ | yes | 1.11 | 1.02 | +0.01 / +0.01 | 0.88 / 0.96 / 0.83 | 39 / 39 / 39 % |
| 1.1-1.3x | no | 0.92 | 0.65 | +0.11 / +0.12 | 1.00 / 1.02 / 1.03 | 15 / 11 / 3 % |
| 1.1-1.3x | yes | 0.94 | 0.67 | +0.12 / +0.13 | 0.91 / 0.97 / 0.97 | 9 / 7 / 2 % |
| under 1.1x | no | 0.32 | 0.20 | +0.01 / +0.05 | 1.00 / 1.00 / 1.00 | 0 / 0 / 0 % |
| under 1.1x | yes | 0.47 | 0.29 | +0.01 / +0.12 | 0.89 / 0.89 / 0.89 | 0 / 0 / 0 % |

Every prediction from 1.3x up held with room: the width within 0.02 of truth at four bins where
quadrature read 1.11 to 1.24 over, the paired recovery within 0.02 of the exact kernel at p50, and
arm i's fabrication gone (stars 0.95 against 1.15, ring 44 against 69 percent, both at or under
exact). The estimate never exceeds 1.02 of truth in any bin, so an unrolled RL seeded from it would
never start over-wide, which is the failure that rings.

*The light end, and what it was.* At 1.1-1.3x `est-c` read 0.65 to 0.67 of the injected width where
quadrature read 0.92 to 0.94, and its recovery lagged exact by 0.11; the pre-registration's "no
change there" was wrong, and so was its reasoning. It is not conditioning: composed on a fine grid,
composition's forward curve is STEEPER than quadrature's at every kernel width (slope of the
observed/clean ratio against kernel/core 0.53 to 0.59 against 0.29 at 0.3 of the core, beta 2.5 to
5), so its inverse is the better conditioned of the two. Working back from both arms' readings on the
same two fits, the fitted observed/clean ratio has to sit 9 to 16 percent under what a continuous 1 px
beta-4 Moffat on these cores would give, and the probe's own blur column agrees: it reads 1.12 where
composition says 1.32 on the 1.53 px core, and matches composition to 0.01 at 2, 3 and 4 px. **The
kernel is the cause. `PsfKernel.Build` samples the profile at pixel CENTRES**, so a 1 px FWHM
Moffat lands 65 percent of its mass in one pixel and a 0.5 px one is a delta with four percent in its
neighbours. Composing the DISCRETE kernel with a Moffat core numerically and inverting through the
composition model (`moffat_discrete_kernel.py`, scratch): a nominal 1.0 px kernel is worth 0.59 px on
a 1.53 px core, 0.73 on 2.15, 0.79 on 2.81; a nominal 0.5 px kernel 0.09 to 0.14 px; 2 px and above
within 1 to 3 percent of nominal. Re-scoring E1d's rows against that effective width
(`e1d_effective_width.py`, core beta 3, which moves the answer by under 0.03): `est-c` at the 1 px
kernel reads 0.92 of the applied blur noise-free (p10 0.80, p90 1.10) and 0.94 noisy, at the 0.5 px
kernel 1.00 and 1.07 (the 0.1 px floor it reports IS the kernel), while `est-w` reads 1.32 and 1.34,
the same over-read it shows at 1.3-1.6x. So composition is right at every width, E1b's "noise-
dominated" caveat and the kill paragraph's "measurement is in charge" were both the kernel, and the
bins labelled 1.1-1.3x and under 1.1x in E1b and E1d were run at effective blur ratios nearer 1.12
and 1.005 than their labels say. The same trap as the area-sampled Gaussian in
`denoiser-training.md` H8, in a second place: a profile narrower than about 1.5 px cannot be
represented by point samples on the pixel grid.

*What it touches.* Training pairs are safe: the trainer conditions on `Psf01Estimated`, measured on
the degraded cell, never on the drawn width. But the exporter's realised blur at a draw under about
1.5 px is lighter than `degradations.jsonl`'s drawn width says, so the light end of the training
distribution is lighter than intended, and `Psf01FromKernel` inherits this on top of its quadrature.
Owed, as ground-work (E1f below): the effective width as a first-class number on the kernel and in
the probe's columns, composition in the kernel label, and the exporter drawing the blur RATIO and
solving for the kernel that realises it.

*Verdict.* Pass. The estimator step's width arithmetic is Moffat composition from here on, in the
probe and in the exporter's kernel label; quadrature stays only as E1b's recorded control.

#### E1e, pre-registered 2026-09-07: the stack by an absolute signal floor

*Change.* `PsfProfileFit.StarSelection.SignalFloor`, opt-in beside the default percentile band so
E0's archive survey is untouched: every star whose peak stands at least 50 background MADs over the
frame median (ten times the detector's floor; chosen, not tuned), excluding the brightest percent as a
clipping guard, brightest first up to the 400 cap. The probe selects it with `TIANWEN_ORACLE_BAND=signal`.
Same 180 rows, `exact,estimated-composed`, 60 iterations.

*Prediction.* The refused rows fall from 75 to under 30: the four clean channels that refuse on
`PoorFit` at 4,600 to 7,000 detections fit (their bins-above-floor count rising from 15 to 24 into the
thirties), and most of the 20 noisy observed refusals fit with them; the two `TooFewStacked` refusals
(the 7 px channel, the sparse crops) stay. Where both selections fit, `est-c`'s width ratio is
unchanged within 0.05, since the composition does not care which stars made the profile as long as the
profile is right. Confidence moderate: the floor may still admit too faint a star on the noisiest
observed frames, and the residual bound of 0.5 may then bind there.

*Kill.* Refusals stay above 60 of 180. Then the band is not the cause and the acceptance rule or the
log-space weighting is, which is a different fix again.

**E1e ran 2026-09-07, 23.5 min, `C:/temp/e2/oracle-e1e.txt`. Refusals 30 of 180, from 75, against
a prediction of under 30 and a kill at 60: one row short of the number, the mechanism confirmed.**
Whole-frame refusals by check: clean `PoorFit` 4 to 1, noisy observed `PoorFit` 20 to 4,
`TooFewStacked` 2 to 7 (clean 1, observed noise-free 3, noisy 3). By master: the 5,290-detection
L-Quad channel and the L-Ultimate channel that refused every clean row now fit all of them; the 24 mm
Eta Car frame's 7 px channel refuses all six of its noisy rows on `TooFewStacked` where it had refused
one clean row, and the L-Ultimate master keeps four `PoorFit` across two channels. So the floor does
on the rich fields what the band could not, and where it gives ground it is the sparse or heavily
blurred frames whose fifty-MAD stars number under the stack minimum: a different refusal, honestly
named, and one a lower or per-frame floor could revisit. Width ratios where both selections fit,
`est-c`: 0.98 / 0.99 at 1.3-1.6x (E1d 0.99 / 1.00), 0.99 / 0.99 at 1.6-2.0x, 1.00 / 1.01 at 2-3x,
0.69 / 0.68 at 1.1-1.3x (0.65 / 0.67): unchanged within 0.05 as predicted, and the light-end reading
is the kernel finding above, not the band. Paired recovery at p50 within 0.01 of exact from 1.3x up;
the 1.3-1.6x p90 widened from +0.02 to +0.12 noise-free and +0.15 noisy with 0 no-fit rows in the bin
where E1d had 3 and 5, so the rows the floor newly fits are the harder ones and the tail grew with
the coverage. *Verdict.* The band was the cause. `SignalFloor` is the estimator step's selection for
the difference-kernel path; the archive survey (E0) keeps the percentile band until its own
comparison is run, as pre-registered.

**Verdict on H11.** Conditional pass. Where the estimator answers and the shape is co-estimated,
the ceiling survives estimation to within a few hundredths of the exact kernel through 2x; the
width estimate carries a ratio-dependent bias of up to a quarter that an unrolled-RL layer would
inherit, and the estimator declines half the noisy frames outright, most of them the archive's
majority filter class. By the header's pre-stated rule that refusal rate is kill-class: **the
kernel estimator becomes its own step before any unrolled RL is built**, whatever E2.8 says. Its
brief, from these numbers: instrument the refusal reasons; estimate the DIFFERENCE kernel by
composing Moffats rather than by a quadrature of FWHMs (the 1.24 is what quadrature does to a
Moffat); and measure the width bias against the injected truth on the same 180 rows, which this
probe already prints per row.

### E2.8's results, 2026-09-07: the star term passes its gate on every seed, and fabricates on the session it was not selected on

Run as pre-registered (`run-p2-starloss.ps1`: E2.7 arm B plus `--star-loss auto`, five seeds, the
same cache with `stars.npy` added, 48 minutes for five seeds on the 1070). Read with
`n2n_gatelog.py` on `C:/temp/e2/p2-star.log`; the control is the same tool on `p2-band.log --arm b`.
One correction to the control first: the corrected gate's star floor appears ONCE in the E2.7 log,
at arm B seed 2, so seeds 0 and 1 of the control were scored on the pre-fix detector and their
stars-at-minimum (0.63, 0.62) are the inflated count; seed 2 (0.54) is the paired control for the
star column, the widths of all three are comparable.

| arm | seed | selected step | selected out/truth | stars at the minimum | stars@6 there | final out/truth | obs0 out/truth at the minimum | obs0 stars there |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| E2.7 B (control) | 0 | none | (min 1.313 @3100) | 0.63 (pre-fix) | | 1.341 | 1.058 | 0.95 |
| E2.7 B (control) | 1 | none | (min 1.199 @3200) | 0.62 (pre-fix) | | 1.214 | 1.006 | 1.42 |
| E2.7 B (control) | 2 | 100 | 1.365 (min 1.328 @3300) | 0.54 | | 1.339 | 1.083 | 0.88 |
| star term | 0 | 3200 | 1.148 | 0.93 | 0.93 | 1.162 | **0.922** | **39.1** |
| star term | 1 | 3700 | 1.099 | 0.95 | 0.92 | 1.105 | **0.874** | **34.2** |
| star term | 2 | 2400 | 1.126 | 0.84 | 0.80 | 1.137 | **0.937** | **34.2** |
| star term | 3 | 2900 | 1.143 | 0.94 | 0.96 | 1.165 | **0.906** | **40.4** |
| star term | 4 | 3800 | 1.173 | 0.88 | 0.94 | 1.174 | 1.045 | **45.0** |

The input sits at 1.382x the truth on the gate session and 1.152x on the observer.

**On the selecting session the prediction holds on every count, with margin.** The gate selects
on 5 of 5 seeds (predicted at least 4); the selected out/truth is 1.10 to 1.17 (bar 1.30; the
control's MINIMA were 1.20 to 1.33 and none was selectable); the stars column at the width minimum
reads 0.84 to 0.95 (bar 0.60; control 0.54 to 0.63), so the minimum IS the selected step on every
seed, which is what the term was for; and the low-bar count `stars@6` tracks the high-bar one (0.80
to 0.96), so the net is not sharpening the twelve-MAD stars while erasing the six-MAD ones. Every one
of the 200 probes passed from step 100 on, and the trajectory is still near its minimum at step
4000 (final 1.10 to 1.17), not degrading. Paired against the control's best minimum (1.199) the
selected widths are 0.03 to 0.10 narrower while keeping half again as many stars.

**The observer column says the recipe fabricates, and the mechanism is measurable.** On the val
session the gate never selects on (Pleiades, SV605CC), the same checkpoints read 34 to 45 TIMES the
truth's star count and a width UNDER the truth on four of five seeds (0.87 to 0.94), where the
control read 0.9 to 1.4 and 1.01 to 1.08. Three facts place it. The observer's truth is three times
quieter than the gate session's (background MAD 0.00057 against 0.00167) with a tenth of the stars
(22 against 199 a tile). Its INPUT already reads 6.9 times the truth's detections at the gate's
truth-anchored threshold (the injected sub-level noise on a quiet master clears twelve of its MADs),
and the control took that to 0.9 by denoising, which is what an L2-dominated objective does. The
star arm takes it to 47 by step 100 and never below 28: the term rewards a peak, a flux and a
concentration matched at detected stars, the cheapest way to raise a blurred peak is to sharpen
every peak, and on a quiet frame the sharpened noise peaks clear the threshold. On the gate session
the same residual sits under twelve of a three-times larger MAD and is invisible. Not a labelling
artefact: 44 of the observer's 45 cells carry a psf01 label, as do 45 of 45 gate cells.

**Two properties of the recipe to carry forward.** The weight is matched on the FIRST batch, so
it is a random variable of the seed: 1.29e-3 to 8.62e-3 across the five, a 6.7x range, with no
clean relation to the outcome (the lowest weight gave the narrowest selection, the highest the
widest and the worst observer count). And as the launch header said, at E2.6's plateau the star
term is tens of times the pixel term, so the pixel and band terms only regularise; the observer is
what that costs.

**Verdict on H10.** The gate-level hypothesis is confirmed: an objective that counts stars makes
the width minimum gate-eligible, on every seed, and E2.7's "the loss buys width with faint stars"
is closed as a loss problem. The pre-registered kill line is not touched. But the pre-registration
named the selecting session and said nothing about the observer, and the observer is a
fabrication failure of the kind the gate exists to catch: a user with a deep, sparse master would
see it grow thirty stars for every real one. **The recipe cannot ship and cannot be the base of a
capacity arm as it stands.** What comes next is pre-registered below as E2.8b; the fork's
architecture question is deferred until it answers, and the estimator step (E1c, E1d) runs on the
CPU meanwhile.

#### E2.8b, pre-registered 2026-09-07: hold the star term to the truth's empty sky

*Change.* Two arms, each E2.8's recipe plus one thing. Arm **N** adds the counterpart the term
lacks: the same three ratios over windows placed where the CLEAN target has NO detection (a matched
count of empty 7x7 windows a tile, drawn once at `--prepare-stars` beside the stars), so a peak the
output raises over an empty patch of target is penalised the way a lowered star peak is. Arm **W**
keeps the term as it is and matches its weight at the PLATEAU instead of the first batch: `W` is
set on step 1 as now, then re-fixed once at step 400 to the pixel term's value there, and logged
both times; the pixel term at step 1 is the injected noise, not the task. Seeds 0 to 4 on both,
same cache, gate and observer as E2.8; E2.8's five runs are the paired control.

*Prediction.* Arm N holds the selecting session's result (selected out/truth at or under 1.20,
stars at the minimum at or over 0.80) and brings the observer's star count under 2x its input
null of 6.9 (so under 14) with the observer width back over 1.0; arm W narrows less on the gate
session (selected 1.20 to 1.30) and improves the observer only partly (10 to 25x). Confidence
moderate on N, low on W.

*Kill.* Neither arm brings the observer under 2x its input null while keeping the gate session's
selection under 1.30. Then a per-star term cannot be made honest by construction and the fork
reopens on the objective, not the capacity.

*Readout.* `n2n_gatelog.py` on both logs; the observer columns are the primary readout this time.

**E2.8b ran 2026-09-07, 11:04 to 12:39, ten runs of 9 to 10 min, `C:/temp/e2/p2-star-b.log`, readout
`p2-star-b.gatelog.txt`. Not killed, by one seed. Arm N's prediction held on the observer and failed
on the gate session in four of five seeds; arm W's held as written and fixes nothing.** The gate
session's input sits at 1.382x the truth, the observer's null (its INPUT's star count over the truth's
at the gate's threshold) at 6.9; E2.8's five runs are the control:

| arm | seed | selected step | selected out/truth | stars there | width minimum | stars there | observer out/truth | observer stars, x truth | observer stars@6 | weight at step 1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| control | 0 | 3200 | 1.148 | 0.93 | 1.148 | 0.93 | 0.922 | 39.1 | 5.5 | 2.29e-3 |
| control | 1 | 3700 | 1.099 | 0.95 | 1.099 | 0.95 | 0.874 | 34.2 | 4.4 | 1.29e-3 |
| control | 2 | 2400 | 1.126 | 0.84 | 1.126 | 0.84 | 0.937 | 34.2 | 5.7 | 2.33e-3 |
| control | 3 | 2900 | 1.143 | 0.94 | 1.143 | 0.94 | 0.906 | 40.4 | 5.3 | 5.62e-3 |
| control | 4 | 3800 | 1.173 | 0.88 | 1.173 | 0.88 | 1.045 | 45.0 | 5.3 | 8.62e-3 |
| N, empty windows | 0 | 1200 | 1.339 | 0.63 | 1.267 | 0.59 | 1.159 | 6.3 | 5.7 | 3.56e-3 |
| N | 1 | 3700 | **1.183** | 0.75 | 1.183 | 0.75 | 0.975 | **9.7** | 3.8 | 1.84e-3 |
| N | 2 | 400 | 1.331 | 0.63 | 1.218 | 0.56 | 1.162 | 4.6 | 6.1 | 3.36e-3 |
| N | 3 | 900 | 1.327 | 0.63 | 1.296 | 0.59 | 1.161 | 4.6 | 6.4 | 6.95e-3 |
| N | 4 | 900 | 1.323 | 0.63 | 1.185 | 0.55 | 1.119 | 2.6 | 4.2 | 5.88e-3 |
| W, re-fixed at 400 | 0 | 3700 | 1.227 | 0.79 | 1.227 | 0.79 | 1.025 | 22.0 | 4.6 | 2.29e-3 |
| W | 1 | 2900 | 1.094 | 0.92 | 1.094 | 0.92 | 0.886 | 27.7 | 3.7 | 1.29e-3 |
| W | 2 | 2500 | 1.143 | 0.85 | 1.143 | 0.85 | 0.921 | 22.5 | 4.6 | 2.33e-3 |
| W | 3 | 400 | 1.321 | 0.63 | 1.321 | 0.63 | 1.207 | 25.4 | 6.7 | 5.62e-3 |
| W | 4 | 2900 | 1.364 | 0.67 | 1.364 | 0.67 | 1.244 | 41.9 | 6.6 | 8.62e-3 |

*Arm N.* The counterpart does exactly what it was built to do to the observer: star counts of 2.6
to 9.7 times the truth's against the control's 34 to 45, four of five UNDER the input's own null of
6.9 (the output has fewer false peaks than the frame it was given), and the observer width back over
1.0 on four of five. The cost is the sharpening. Four seeds select at 1.32 to 1.34 against an input of
1.38 and hold 0.63 of the truth's stars there, their width minima (1.19 to 1.30) hold 0.55 to 0.59
and are not gate-eligible, they select EARLY (steps 400 to 1200, 21 to 35 of 40 probes passing) and
their final widths (1.35 to 1.43) sit at or past the input: under the empty-window term at a
first-batch weight the net trains TOWARD leaving the frame alone, since a penalty on every raised peak
over empty sky is a smoothing prior that grows as the pixel term shrinks. Seed 1 is the exception and
meets the kill clause's both halves alone: 1.183 selected at step 3700 holding 0.75, observer 9.7x
with width 0.975. It has the arm's lowest weight (1.84e-3; the other four 3.4e-3 to 7.0e-3), and E2.8
had already noted that the control's lowest weight gave its narrowest selection. One seed is a
lead, not a result.

*Arm W.* As predicted and no better: the observer improves only partly (22 to 42x, median 25 against
39) and the gate session keeps three of five under 1.30 with two at 1.32 and 1.36. The re-fix took the
weight DOWN, to 4.1e-4 to 9.8e-4 (a third to a fourteenth of the step-1 value, since the pixel term
had fallen 20 to 50x by step 400), and the fabrication barely moved: a lower weight on the term as it
is does not make it honest. Matching the weight at the plateau is not the mechanism; the term's
incentive is, which is what the counterpart changes.

*Trajectories (added at 13:20 from the same logs, `training/denoise/n2n_gatetrace.py`, the time axis
`n2n_gatelog.py` collapses).* Gate out/truth at probe steps 200 / 1000 / 2000 / 4000, then the
observer's stars at the same steps:

| run | 200 | 1000 | 2000 | 4000 | obs 200 | obs 1000 | obs 2000 | obs 4000 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| control, seeds 0-4 | 1.33 to 1.42 | 1.29 to 1.36 | 1.14 to 1.21 | 1.11 to 1.17 | 21 to 65 | 19 to 64 | 30 to 41 | 35 to 46 |
| N seeds 0, 2, 3, 4 | 1.39 to 1.42 | 1.32 to 1.45 | 1.39 to 1.42 | 1.35 to 1.43 | 18 to 44 | 3 to 33 | 2 to 14 | 6 to 16 |
| N seed 1 | 1.41 | 1.47 | 1.40 | 1.18 | 27 | 25 | 3.6 | 9.6 |
| W seeds 1, 2 | 1.38 to 1.40 | 1.37 to 1.39 | 1.14 to 1.19 | 1.11 to 1.16 | 34 to 44 | 49 | 24 to 36 | 30 to 31 |
| W seeds 0, 3, 4 | 1.33 to 1.42 | 1.39 to 1.41 | 1.39 to 1.41 | 1.23 to 1.40 | 21 to 65 | 49 to 59 | 39 to 49 | 22 to 50 |

Three things the table says that the summary could not. The control SHARPENS STEADILY (1.4 to 1.15
over 4,000 steps) and fabricates from its first probes on. Arm N's four losing seeds never leave the
input's ratio at any probe, so "trained toward the identity" understates it: they never left it, and
their observer count OSCILLATES between honest (2 to 7x) and fabricating (20 to 33x at step 1,000)
as the two halves of the term trade blows, which is why they select early on a probe that happened to
land honest. Seed 1 sits at the input until step 2,000 and then sharpens (1.40 to 1.19 by 3,000) with
the observer honest: the pixel and band terms took over late, at the arm's lowest weight, which is
E2.7 arm B's dynamics with a weak regulariser on top rather than a star term doing the sharpening.
And arm W's re-fix at step 400 LOWERED the weight, after which three of five seeds stalled at the
input for the rest of the run (the identical numbers up to step 400 are the shared seeds): a weaker
star term means less sharpening, not less fabrication. So the star term is what sharpens this net,
its counterpart cancels the sharpening at an equal weight, and a low weight hands the run back to
the pixel objective. **How to read E2.8c, added after its launch and changing neither its prediction
nor its kill:** a passing seed should be seen to sharpen LATE (after step 2,000) as seed 1 did, and
its result compared against E2.7 arm B's minima (1.20 to 1.33 holding 0.54 to 0.63, never
selectable): if E2.8c's seeds pass at 1.18 to 1.25 holding 0.75, the honest recipe is E2.7 plus a
regulariser that makes its minimum selectable, worth having and not the star term the H10 hypothesis
described.

*Verdict.* The kill line ("neither arm ... while keeping selection under 1.30") is not crossed, on
one seed of ten. The pre-registered N prediction (selected at or under 1.20, stars at or over 0.80,
observer under 14x) is met on its observer half by every seed and on its gate half by one, so a
per-star term with its counterpart CAN be honest and CAN be sharp, and whether it can be both at a
weight one can set is exactly the axis this arm left random. That is E2.8c below, fifty minutes of
GPU, before the fork is written down rather than after.

#### E2.8c, pre-registered 2026-09-07: arm N at a fixed weight

*Change.* Arm N's recipe (`--star-loss-empty`) with the star term's weight FIXED at 1.84e-3 on every
seed instead of matched on the first batch, the value seed 1 drew (1.8386e-3). Seeds 0 to 4, same cache, gate
and observer; arm N's five runs are the paired control, and seed 1 of arm N is the run this arm
should reproduce five times if the weight is the cause.

*Prediction.* At least three of five seeds select at or under 1.25 on the gate session while the
observer stays under 2x its null (14) and over 1.0 in width; the width minima hold at least 0.65 of
the stars. Then the weight was the cause, the honest recipe has an operating point, and the unrolled
operator carries the term at that weight. Confidence moderate: a fixed weight removes one random
variable and leaves the seed's own, which E2.7 measured at 0.008 to 0.031 on width.

*Kill.* At most one of five seeds selects under 1.30 with the observer under 14. Then seed 1 was the
seed and not the weight, the smoothing reading above stands, and the counterpart goes into the
unrolled operator as a REGULARISER only (where the physics does the sharpening and a term that
prefers leaving the frame alone costs nothing), never as the sharpening objective of a pixel-domain
net.

*Cost.* 5 x 10 min on the 1070, once D1's output probe has released it. Launch script
`run-p2-starloss-c.ps1` with this header, as the others.

### Reproducing E0 and E1

Every number in the two sections below comes from a probe in `TianWen.Lib.Tests`, gated on an
environment variable and skipped without it, so none of them run in the ordinary suite. They read a
real archive off a spinning disk. **Recorded here because the invocations are the experiment**: the
plan quotes the results, and without the command lines a re-read cannot re-derive them.

```bash
# from src/. TIANWEN_PSF_STORE_DIR points at a dataset out-dir (the one holding stats/ and
# session-masters/); everything below is against the 79-session 2026-09-full bake.
export TIANWEN_PSF_STORE_DIR="D:/Astro-Dataset/2026-09-full"

# E0, is the store current? ~35 s for 8 masters. Run it against 2025-2026-organized too: that bake is
# the CONTROL, and its documented staleness (count 1.038, +0.033 px) is what makes the newer bake's
# exact zeros readable rather than a self-comparison.
TIANWEN_PSF_PROBE_MAX=8 dotnet test TianWen.Lib.Tests \
  --filter "FullyQualifiedName~PsfStoreVsCurrentDetectorProbe" --logger "console;verbosity=detailed"

# E1 / H5, the conditioning contract. ~1.7 min for all 79 masters (MAX=0 means all).
TIANWEN_PSF_PROBE_MAX=0 dotnet test TianWen.Lib.Tests \
  --filter "FullyQualifiedName~PsfEncodingSpreadProbe" --logger "console;verbosity=detailed"

# E1 / H1, the oracle ceiling. ~26 min at 30 iterations, ~45 at 60. The tabled numbers are 60.
TIANWEN_ORACLE_MASTERS=6 TIANWEN_ORACLE_ITERS=60 dotnet test TianWen.Lib.Tests \
  --filter "FullyQualifiedName~ReportHowMuchOfAKnownBlurAnOracleRecovers" --logger "console;verbosity=detailed"

# E1 / H1, the iteration sweep that picked 60. ~22 min; reads 5..120 off ONE run per combination.
TIANWEN_ORACLE_MASTERS=3 dotnet test TianWen.Lib.Tests \
  --filter "FullyQualifiedName~ReportWhereTheIterationCountStopsHelping" --logger "console;verbosity=detailed"

# E1b / H11, the ESTIMATED-kernel ceiling: every listed arm runs on the same observed frame, in parallel,
# and the summary pairs each estimated arm against exact row by row. ~1 h at 60 iterations including
# the whole-frame fallback fits. Release, because the direct convolution is the cost.
TIANWEN_ORACLE_MASTERS=6 TIANWEN_ORACLE_ITERS=60 TIANWEN_ORACLE_KERNEL=exact,estimated,estimated-shape \
  dotnet test TianWen.Lib.Tests -c Release \
  --filter "FullyQualifiedName~ReportHowMuchOfAKnownBlurAnOracleRecovers" --logger "console;verbosity=detailed"

# E2.9, the per-sub identity and air mass: the measure stage alone over every recorded session of the
# bake (~2 min a session, ~2.5 h for 79), then the readout. From the repo root, in pwsh; the launcher
# builds Release, refuses a binary that is not HEAD, runs detached and writes bake-provenance.json.
./tools/run-dataset-bake.ps1 -Out D:/Astro-Dataset/2026-09-full `
  -ArchiveRoot D:/Astro-Organized/lights,D:/Astro-Organized/flats,D:/Astro-Organized/calibration,D:/Astro-Unsorted `
  -ScratchRoot C:/temp/tianwen-bake-scratch--2026-09-full -ExtraArgs '--resume','--remeasure-subs'
python tools/psf-airmass-report.py D:/Astro-Dataset/2026-09-full --png C:/temp/e2/e29-fwhm-airmass.png
```

**One thing the raw output does not give you.** The per-cell fits behind E0 come from parsing
`stats/psf-sessions.jsonl` directly (`MasterProfiles[]` per channel, filter = the 4th `|`-delimited
field of `SessionId`, rows with `MoffatBeta > 20` dropped as railed); that is a small script, not
committed, and a re-run should redo it from the stored output rather than trusting a remembered
number. (The ceiling probe used to bin by psf01, which E1 found to be the wrong axis, and the tables
below were re-aggregated by BLUR RATIO with a second uncommitted script; since E1b the probe prints
that aggregation itself, in the plan's own bands.)

**Captured output from the 2026-09-06 runs**, kept for comparison rather than as an input:
`C:/temp/e2/psf-encoding-spread.txt`, `oracle-ceiling.txt` (30 iterations), `oracle-ceiling-60.txt`,
`oracle-iterations.txt`; from 2026-09-07, `oracle-e1b.txt` (E1b, all three arms). Scratch, not
backed up.

### E0's results, 2026-09-06

**No `--force-psf` run was needed, because the bake that arrived for the denoiser is already
current.** The plan named `2025-2026-organized`, whose store was written 2026-08-15 and predates the
deblender. `2026-09-full` (79 sessions against 51, filters carried, written 2026-09-04) postdates it,
and a file date is not evidence, so `PsfStoreVsCurrentDetectorProbe` was run on both. The old bake
reproduces its documented staleness (matched-star count ratio p50 **1.038**, banded FWHM **+0.033 px**)
and the new one is identical to the current detector on every metric (**1.000**, **0.000 px**, on all
8 probed masters). The control is what makes the zero readable: without it, a probe that compared a
store with itself would print the same thing. **Calibrate on `2026-09-full`.**

**The pool is thinner than a per-(train, filter, channel) draw needs.** 17 (train, filter) cells over
79 sessions, and **10 of the 17 hold one session each**; two cells carry 43 of the 79 (Samyang 135 at
130 mm under L-Ultimate 3 nm, 33; ASI533 at 130 mm with no filter recorded, 14). 163 of a possible 237
channel profiles fitted. So a cell-level draw distribution is supportable for two or three cells and
every other cell must fall back, per train and then globally.

**The joint (FWHM, beta) draw survives the split on blue and red, and not on green.** The plan's
"r 0.66, sample jointly, never independently" reproduces exactly on its own store as an
**all-channels pooled** statistic (+0.654 there, +0.423 on the current one). Split it and the pooling
is doing most of the work:

| population | ch0 | ch1 | ch2 |
|---|---|---|---|
| `2025-2026-organized`, Samyang, within channel | +0.736 | +0.193 | +0.620 |
| `2026-09-full`, Samyang, within channel | +0.554 | -0.095 | +0.685 |
| `2026-09-full`, all trains, within channel | +0.508 | -0.238 | -0.104 |

Pooling across TRAINS dilutes it (trains sit at different beta levels) and pooling across CHANNELS
within a train inflates it (+0.809 on Samyang), because ch0 is both wider and heavier-winged than
ch1 and ch2, so a channel difference reads as a within-channel slope. **Draw beta from the
conditional below, per channel; on green the slope is zero or negative and the draw is correctly
independent, which needs no special case because the fitted slope says so.**

`log(beta) = a + b * FWHM_px`, residual sd in log units, railed rows excluded:

| population | ch | n | a | b | sd | r2 | beta at FWHM 2.0 / 3.0 |
|---|---:|---:|---:|---:|---:|---:|---|
| all trains (global fallback) | 0 | 40 | 0.138 | 0.657 | 0.487 | 0.238 | 4.27 / 8.24 |
| all trains (global fallback) | 1 | 59 | 2.245 | -0.355 | 0.295 | 0.040 | 4.64 / 3.25 |
| all trains (global fallback) | 2 | 53 | 1.592 | -0.121 | 0.344 | -0.009 | 3.86 / 3.42 |
| Samyang 135 @130 [3 nm] | 0 | 18 | 0.997 | 0.465 | 0.202 | 0.307 | 6.86 / 10.93 |
| Samyang 135 @130 [3 nm] | 1 | 25 | 1.927 | -0.195 | 0.144 | -0.027 | 4.65 / 3.83 |
| Samyang 135 @130 [3 nm] | 2 | 22 | 0.411 | 0.617 | 0.181 | 0.378 | 5.17 / 9.59 |
| SH61 @270 [L-Quad] | 0 | 6 | -0.266 | 0.723 | 0.383 | 0.597 | 3.25 / 6.71 |
| ASI533 @130 [(none)] | 1 | 14 | 0.689 | 0.595 | 0.279 | 0.032 | 6.54 / 11.86 |

**Read the r2 column before using a slope.** Only four of these explain anything; the rest are a line
through a cloud, and the residual sd is then the whole distribution. That is a finding rather than a
defect in the fit: outside blue, beta is roughly independent of width, so the exporter's existing
independent draw was closer to right than "sample jointly, never independently" implied.

**A railed beta means Gaussian, and must be EXCLUDED rather than clipped.** `PsfProfileFit` searches
`beta` on a grid to 25.0, and **11 of 163 fits land on the last grid point (24.95)**. In 9 of those the
Gaussian fit is the better one, and all 9 are ch0. So the rail is the fitter reporting "this profile is
Gaussian" in the only vocabulary it has, and a distribution fitted over the raw column drags blue's
draw toward beta 25, which is the one shape the plan says never to sample. Excluding them is what the
table above does, and it is why the Moffat-versus-Gaussian row now reads 154 of 163 with its exceptions
named rather than 48 of 50 with none.

**What E2 owed on the back of this, now SHIPPED 2026-09-06.** Three pieces:

- **The per-channel correlated draw**, `--per-channel-kernels`, off by default so the shared draw
  stays H3's control. Width scales by the measured channel ratio and beta comes from that channel's
  own fitted relation, log-normal about the fit and clamped to the measured family; elongation and
  angle stay shared, being properties of tracking and optics rather than wavelength. Justification
  measured rather than asserted: a shared kernel drives the blue/green ratio from 1.372 to 1.072 by a
  4 px blur where the per-channel draw holds 1.329.
- **psf01 labelling (H2's other half).** Every blur row now carries `Psf01Estimated`, from
  `HfdPsfEstimator` on the DEGRADED cell, in the LINEAR domain and under the `[0.5, 4.0]` contract,
  beside `Psf01FromKernel`, the training-only twin composed from the clean cell's own measured width
  and the drawn one. Two rules the code enforces: the label is measured on the linear cell because
  `OnnxNonStellarDeconvolver` measures it there too and a tone curve moves a star's half-maximum
  crossing; and **a psf01 is never written without stars behind it** (`Psf01Stars`), because the
  estimator falls back to a constant default radius and storing that would put a number nothing
  measured into the column a model conditions on. Null instead, so a consumer drops the row.
  **Measured on the fixture, the two labels agree closely at light blur (0.639 against 0.642) and
  DIVERGE at heavy blur (0.837 against 0.810)**: quadrature composition understates a Moffat's
  widening, so the kernel label is systematically low exactly where it matters, which is evidence for
  H2's position before the arm is run.
- **The sweep's top is a RATIO now, not a pixel count** (`--max-blur-ratio`, default 2.0, applied on
  top of the pixel cap). E1's ceiling is the reason: past 2x blur the oracle itself leaves the star
  about 1.6x too wide, so a fixed 4 px cap, which is roughly 2x on a 2.3 px master and far past it on
  a 1.5 px one, spends a slice of the training set on a problem nothing can solve.

## 6. Integration

`OnnxTianWenDeconvolver : INonStellarDeconvolver` in `src/TianWen.AI.Imaging/Onnx/`, thin over
`ChunkedNafnetRunner` (the stretched domain is the RIGHT one here, unlike the denoiser's runner),
model file `tianwen_deconv_nonstellar_psf_v1.onnx` plus contract JSON asserted at load. The psf01
comes from an `IPsfEstimator` carrying the own encoding; the SAS estimator stays for the SAS model.
Backend selection: the run log's `--ai-backend n2n` is defined as "the in-house model where this role
has one", so when the deconvolver lands the flag's NAME is wrong (it names the denoiser's method);
rename to `tianwen` (the programme doc's original `ForceTianWen`) with `n2n` kept as an alias for one
release, and Auto stays RC then SAS then in-house rescue until a human side-by-side says otherwise.

## 7. Phasing

| Phase | Deliverable | Exit |
|---|---|---|
| P2.0 | **DONE 2026-09-06.** Store re-measured (E0: no re-measure needed, `2026-09-full` is already current, and the joint draw is fitted per channel); oracle ceiling and encoding spread tabled (E1) | E0, E1 |
| P2.1 | Degradation exporter with stretch parity; first pairs (**exporter + parity done 2026-09-03**; psf01 labelling and the measured draw distribution owed) | E2 |
| P2.2 | H2/H3/H4/H8 answered on the smoke U-Net with posted comparisons | E3 |
| P2.3 | H7 answered; recipe fixed | E4, E5 |
| P2.4 | Ladders captured; H6 range measured | E6 |
| P2.5 | v1 exported, wired, contract-asserted, photometric gate green | E7 |

## 8. Open questions

- **Which stretch parameters at inference?** `ChunkedNafnetRunner` stretches the whole frame per
  channel; training used the target frame's parameters. On a real frame there is no target; the input
  IS the frame, and its own parameters are what the exporter's input side would have had. Verify on
  synthetic pairs that using the input's parameters at inference costs nothing measurable (the
  medians differ by the blur's effect on the median, which is small); if it does, the exporter must
  use the INPUT frame's parameters for both sides instead.
- **Narrowband red.** Under a 3 nm filter red is defocused by 1.64x relative to green by
  acquisition (1c). The model will be asked to sharpen red hardest, correctly; whether that is
  what a user wants on an Ha image is a product question, answered by the per-role strength dial.
- **Per-chunk PSF re-measurement** (`docs/todo/imaging.md`, the `SepPerChunkPsfEstimator` item)
  would let psf01 vary across the field at inference, the deployment-side twin of H7. Not needed for
  v1; note that a per-chunk estimator changes the label distribution the model was trained under.
- **Mono** waits on mono data, as everywhere.
