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
| E3 | **Base decided 2026-09-07 ("The fork, decided"): the unrolled Richardson-Lucy operator with the estimator step's kernel and a learned residual prior, not the U-Net.** E3.0 is the operator alone through the gate (pre-registered there: selects at or under 1.10 with stars at or over 0.60 and an honest observer; killed over 1.20), E3.1 the operator with the network, five seeds (stars at or over 0.85 at width at or under 1.15). The smoke arms then run on that base: kernel from the draw vs from the estimator (H2, now inside the operator), shared vs per-channel (H3), noise vs none (H4), two vs three bands (H8), five seeds each by E2.6's measured sd. E2.10a's real-blur pair is the check on the estimator's kernel before a seed is spent. | E3.0 an hour; E3.1 five seeds a day; 4 pairs x 5 seeds after | H2, H3, H4, H8 on the chosen base |
| E2.5 | **DONE 2026-09-06** (details below the table). `--prepare` reads `degradations.jsonl` into a `psf01.npy` beside the tiles; `--cond-psf01` conditions on that stored label instead of on measured noise; `n2n_deconv_gate.DeconvGate` selects on width, ringing and star count. Validated end to end on a two-session blur export, which is also what caught the gate's own first bug. Originally recorded as: **discovered 2026-09-06, and the reason the row above cannot start.** The trainer cannot train a deconvolver at all yet: `--prepare` reads `tiles-manifest.jsonl` only and never `degradations.jsonl`, so the psf01 the exporter now writes never reaches the cache; the conditioning plane comes from `with_sigma`, which MEASURES the input's noise rather than reading a stored label; and `n2n_gate.py`'s metrics are noise, faint amplitude and spurious sources, every one of them noise-oriented. **The gate is the consequential half: as it stands it would select the checkpoint that removes the most noise while doing nothing about sharpness, which is selecting a deconvolver for BLURRING.** It needs FWHM recovery and ringing, scored against E1's ceiling at 60 iterations. | 1 to 2 days | Whether E3 can run |
| E2.6 | **DONE 2026-09-06, and it answered a different question** (results below): no seed selected a checkpoint, the gate's star criterion had no measured null, and the loss never looked at the star scales. As planned: the power check the denoiser campaign paid for: ONE arm at six seeds, seed spread measured against the effect each of H2/H3/H4/H8 expects, then the rest sized from it. On the denoiser's E2 the seed sd beat the between-regime sd 2 to 3x, so three-seed arms could not read a one-point effect and about 31 seeds would have been needed. H4 and H8 are plausibly large enough to read at three; H2 and H3 are the ones at risk. | 6 x 11 min | E3's seed count |
| E2.7 | **DONE 2026-09-06** (results below). Band placement control, two arms x three seeds on E2.6's own seeds: the loss can see the scale now (every minimum at steps 3100 to 3400, paired deltas -0.038 and -0.043), the arms are inseparable at three seeds, and under the corrected gate not one best-width checkpoint survives, because width is bought by suppressing faint stars (0.54 of the truth's against a 0.62 floor). A high-pass, not a deconvolution. | 6 x 10 min | Whether band placement was the lever (it was not) |
| E1b | **RUN 2026-09-07, conditional pass** (results section below). E1's exact-kernel ceiling re-run with the kernel ESTIMATED from the blurred frame's own stars, two arms: width estimated with the injected shape, then both estimated. Where the estimator answers, the paired rec/truth is within 0.01 to 0.05 of exact except at 1.3-1.6x, where a systematic 1.24x width over-read makes arm i fabricate (stars 1.15x truth, rec/truth 0.95 under noise) and arm ii pass only because an over-read beta cancels it. The estimator refused 75 of 180 rows (half the noisy ones), never fitted the 512 crop, and its refusals sit on the narrowband masters. Kill line not crossed; the pre-stated refusal rule makes the estimator its own step before any unrolled RL. | 22.6 min CPU, no training | H11 |
| E2.8 | **RUN 2026-09-07, gate-level pass, observer-level fabrication** (results section below). The star-term loss: per-star flux, peak and concentration ratios against the clean target's own detections, added to E2.7 arm B's objective, same 78-session cache, five seeds paired against E2.7. Selects on 5 of 5 at out/truth 1.10 to 1.17 holding 0.84 to 0.95 of the truth's stars (control: one seed at 1.365, minima unselectable at 0.54 to 0.63 stars); on the observer session (three times quieter truth) the same checkpoints read 34 to 45x the truth's stars and widths under 1.0. E2.8b (a no-star counterpart, and the weight matched at plateau) is pre-registered; the capacity arm waits on it. | 48 min for five seeds | H10 confirmed at the gate; the recipe fabricates on transfer |
| E1c | **RUN 2026-09-07** (under E1b's results). Which of `PsfProfileFit`'s five checks refuses, tallied over E1b's rows at one RL iteration: 28 of 30 whole-frame refusals are `PoorFit`, on RICH fields (4,600 to 7,000 detections, the stack at its cap, 15 to 28 of 48 bins above the noise floor), because the percentile brightness band lands on faint stars there. The fix is the band, not the acceptance rule. | 11 min CPU | the estimator step's first fix |
| E1d | **RUN 2026-09-07, pass** (under E1b's results). The difference width by Moffat COMPOSITION (`MoffatComposition.DifferenceFwhm`, arm `est-c`) instead of a quadrature of FWHMs: estW/true 0.99 to 1.02 at every bin from 1.3x up where quadrature read 1.11 to 1.24, paired recovery within 0.02 of exact, arm i's fabrication gone (stars 0.95 against 1.15). The apparent 0.65 under-read at 1.1-1.3x is the PROBE: `PsfKernel.Build` point-samples at pixel centres, so a nominal 1 px kernel blurs like 0.6 to 0.8 px and a 0.5 px one like 0.1; against the applied kernel est-c reads 0.92 to 1.07 there too. Training labels are measured on the degraded cell and unaffected. | 25 min CPU | the width bias was arithmetic; the light end was the kernel |
| E1e | **RUN 2026-09-07** (under E1b's results). `PsfProfileFit.StarSelection.SignalFloor` (opt-in; every star over fifty MADs, brightest first) in place of the percentile band: refusals 30 of 180 from 75 (prediction under 30, kill 60); clean PoorFit 4 to 1, noisy observed PoorFit 20 to 4, TooFewStacked 2 to 7 on sparse and heavily blurred frames; width ratios unchanged within 0.02. The band was the cause. | 23.5 min CPU | the refusals were the band |
| E2.8b | **RUN 2026-09-07, not killed by one seed** (under E2.8's results). Arm N, the star term's counterpart over EMPTY target windows: observer fabrication 34-45x to 2.6-9.7x (four of five under the input's own null) as predicted, but four of five seeds lose the sharpening (selected 1.32-1.34 against an input of 1.38, stars 0.63, trained toward the identity); seed 1, the lowest weight, keeps both (1.183 selected, observer 9.7x). Arm W, the weight re-fixed at step 400: observer 22-42x, three of five under 1.30; not the mechanism. E2.8c (arm N at seed 1's fixed weight) pre-registered to say whether the weight was the cause. | 10 x ~10 min GPU | a per-star term can be honest; whether it can be honest AND sharp rides on the weight |
| E2.8c | **RUN 2026-09-07, prediction failed, not killed** (under E2.8b's results). Arm N with the weight fixed at 1.84e-3: two of five seeds hold both (selected 1.238 and 1.163 at 0.74 to 0.76 stars, observer 11 and 10x), sharpening only after step 3,000; three never leave the input. Every seed honest on the observer (1.5 to 11x against 34 to 45). The weight was part of the cause, the optimisation the rest; the profile of a regulariser, which is how it enters the unrolled operator. **The fork is decided below: the unrolled operator, the estimator step under it, no capacity arm.** | 5 x ~10 min GPU | the honest recipe has an operating point on two seeds in five |
| E2.9 | **RUN 2026-09-07, then WITHDRAWN to inconclusive the same afternoon** (section below). FWHM against air mass over 79 sessions, 8,507 subs re-measured in 62.6 min, read 0 of 67 sessions reaching a 1.3x span explained by air mass; but E2.10a's diagnostic showed the store's per-sub width is the mosaic mono path's, which reads 1.70 on every sub of an OSC night whatever the seeing, so the slopes measure an instrument floor. Standing: computed air mass agrees with N.I.N.A.'s card to 0.015 on 50 of 52 sessions; SharpCap writes no site cards (27 sessions uncomputed, `--site` supplies one now). **Re-read on `SubFwhmGreen` the same evening: the kill line stands in substance.** 76 of 79 fitted; 1 reaches a 1.3x explained span with a slope of +3.15 (five times what seeing can give); slopes in [0.3, 0.9] on 12 of 76, medians near zero on every train but ZS61 and the ASI1600; within-night p90/p10 on green 1.04 to 1.21x. The green fit refused 27 percent of subs and every sub of the three warmest SV605CC sessions, whose lists were half warm pixels (fixed in the detector that evening; re-measure running). | 62.6 min CPU, plus two re-measures of 87 min | Air-mass pairing is dead as a real-blur source; the heavy end stays synthetic |
| E2.10 | **E2.10a RUN 2026-09-07: no pair to score** (section below). The Orion night's sharp and soft thirds, built as pre-registered, measure the same width (2.82 against 2.72 px on the green fit, B/A 0.97 to 1.03), and the oracle correctly did nothing. Diagnosed stage by stage: the store's per-sub width is the mosaic mono path's floor (1.70 on every sub) so the split was ranked on noise; the subs do differ on the green plane by 1.15 to 1.25x; and every stack of the night, drizzle included, sits at 2.7 to 2.8 px from 1.7 to 2.5 px subs. Two stacker fixes came out of it (`--group-temp-tolerance`, the `CANVASX0/Y0` cards). **The evening placed the stack's blur** ("the third finding placed"): the registration refiner HALVED every shift under 5 px because half of each frame's detections were warm pixels pairing with themselves (fixed, validated to 0.03 px), the detector accepted a single warm photosite as a star (fixed; the "1.70 floor" was their width), and what remains is the bilinear warp (a master is the mean of its warped frames to 0.3 percent; frames at fractional phase widen from 2.15 to 2.4 to 2.7 px). Candidates on green: 14 of 75 at p90/p10 1.15, thirds only 1.10 to 1.25x, the sampled trains 1.15 to 1.17x (SH61 Statue 251 subs, Rosette 115, Helix 119). On the guarded store (re-measured the same evening): 19 of 77 candidates, the best now the Orion L-Quad 2025-10-15 night itself at 1.26 (sharp third 1.89 px against soft 2.38 on 55 green fits), the night E2.10a found no pair in on the floor-ranked list. **E2.10b READ the same evening on the Statue 2026-02-14 night (244 subs of 60 s, Lanczos-3): the first real-blur pair.** The thirds' masters differ 1.19 to 1.22 on the green fit (predicted 1.10 to 1.16), and the oracle with the estimated kernel (0.8 to 1.0 px on 1.7 px cores) recovers to rec/A 1.007 and 1.085 on two channels (1.139 on the third), stars 0.84 to 1.07 of A, ring excess 14 to 36 percent, converged by 20 iterations. **E2.10c READ on the Orion night (19-frame thirds, 1.89 against 2.38 px subs):** the masters differ 1.17 to 1.27 on the fit (the ranking was what E2.10a lacked); rec/A 0.951 / 1.119 / 1.123 misses the 1.00 to 1.10 band on every channel without a kill (stars 1.04 / 0.94 / 0.85, ring 7 to 14 percent), the heavy-winged sharp core over-reading its difference kernel. | two days, eleven launches | Two real-blur pairs exist (1.2x and 1.2 to 1.27x); the estimator's kernel recovers the Statue's within 1 to 9 percent on two channels and over-sharpens one Orion channel; the star count is the number to watch |
| R1 | **RUN 2026-09-07 evening: Lanczos-3 removes the kernel's blur, no ringing measurable** (section below). Synthetic half-pixel shift: +1.15 px in quadrature under bilinear (predicted 1.1 to 1.3), +0.00 under Lanczos-3 (under 0.4). Real: the near6 master reads 2.15 px per star against the bilinear twin's 2.39 (predicted under 2.25), the frames rank by their own subs' seeing rather than by phase; the whole night 2.48 against 2.70 per star, fit 2.61 against 2.81 (the "under 2.5" missed because the night's subs average 2.4, not the 2.2 the six near frames suggested), still the mean of its frames. Ringing identical to bilinear on the annulus undershoot, the radial profile and the synthetic dip. `PsfProfileFit` refused the sharper near6 master (rms 0.83): its log-space fit reads far-wing background residue, which a narrower core exposes (E1g). Default stays bilinear; the flip is the user's. | an hour of code, two stacks, four probes | A master can keep its subs' width at 2 px seeing; every retained master carries about a pixel in quadrature it need not |
| E1g, E1g-2 | **E1g KILLED, E1g-2 RUN, both 2026-09-07 evening** (sections under R1). The profile fit refused every sharp input (the R1 Lanczos master, a two-frame stack, a 1.8 px sub). E1g, a floor relative to the profile's outer level, changed nothing (30 refusals of 180, the same 30 as E1e) and broke a detached-halo refusal's reason: the fit's annulus already leaves the outer bins near zero, and the fit's own profile, now exposed in `Diagnostics`, shows a Gaussian core with a faint wing that no fixed-width Moffat follows. E1g-2 fits the core (bins above 2 percent of peak) and reports the wing at 2 and 3 FWHM: every refused profile returns (2.32, 2.20, 1.73 to 1.90 px), accepted widths unchanged to 0.01, exponents up by 1 to 3 everywhere, exponents above about 6 no longer told apart. The store's `MoffatBeta` is the old quantity until a re-measure. Oracle rows over E1e's 180: 25 refusals against 30, the prediction of under 10 MISSED; the fit's own refusals fell from nine entries to one, and 24 of the 25 rows left are `TooFewStacked` on the Eta Car 24 mm frame, whose brightness band holds 38 / 10 / 2 / 0 stars at 1 / 2 / 3 / 4 px of blur (a star budget, not a fit); fitted widths identical on every row, recovery unchanged at p50, composed kernel estimates 0.01 to 0.06 wider through the higher clean exponent. | two builds, two oracle runs | The estimator step measures sharp inputs; the store's beta semantics changed |
| D1 | **BUILT 2026-09-07, off by default; both halves measured** (under "The next four"). Per-tile psf01 at inference: `ChunkedNafnetRunner` extras per chunk, `OnnxNonStellarDeconvolver` estimating per chunk region with a frame-level fallback, so inference matches the per-cell training label. CPU half: on the seven Rim masters the per-tile radius spans 8 to 61 percent p10 to p90 and 10 to 16 percent of tiles starve. GPU half: a NULL on the shipped SAS graph, per-tile and whole-image outputs within 0.01 px and one percent in count on every master, including four where 250 of 289 tiles carried a different label; the switch stays off for that graph and ships for E7's own. Side finding: on stars the shipped graph widens soft cores and drops most of the outer third's stars. The fixed-psf01 check (0 / 0.5 / 1 over the whole shipped range) moves the output by 0.03 to 0.06 px and three percent in count: the shipped graph's conditioning input is inert on a real master's stars, so the null is the graph's property. | half a day | The field-varying half of the optics blur |
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

> **Corrected the same afternoon (E2.10a's diagnostic, below): the per-sub width this run fitted
> is NOT a star width on OSC frames.** `SessionPsf.SubFwhm` comes from the one detect site, which
> measures on the pre-debayer mosaic through the mono path, and on the Orion 2025-10-15 night every
> sub reads exactly 1.70 px there while the debayered green plane's bright-star fit puts the same subs
> at 1.7 to 2.5 px and shows one "1.71" frame to be the night's softest. The store's 1.71 to 2.60
> spread is a floor plus the 2000-star retry, not seeing, so the slopes below are slopes of an
> instrument reading against air mass, and the "kill line reached" verdict is withdrawn: E2.9 is
> INCONCLUSIVE until the store carries a colour-plane sub width (owed below) and the readout is
> re-run on it. The cross-check of the computed air mass against the card, the SharpCap site gap and
> the within-night spread being large stand; what is attributed to them does not. The mono session
> (ASI1600, 328 subs, slope +0.17) is the one row the correction does not touch.

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

**Re-read on the green width, 2026-09-07 evening (`SubFwhmGreen`, `--site -37.877,145.1775`, 79 sub
sets re-measured in 87 min; `C:/temp/e2/e29-airmass-report-green.txt`).** The kill line is reached
in substance and the verdict above stands, now on a width that reads seeing. 76 of 79 sessions fitted;
one reaches a 1.3x span attributable to air mass (SMC, ZS61, 40 subs: slope +3.15 over a 1.12x span,
1.43x explained) and its slope is five times what seeing can give, so it is focus or wind that
happened to run with altitude, not the atmosphere. Slopes in [0.3, 0.9]: 12 of 76. Medians by train:
SH61 -0.13 (2 of 11), Samyang 135 at 130 mm +0.12 (5 of 34) and +0.15 (3 of 19), ZS61 +0.45 (1 of 2),
ASI1600 at 180 mm +0.42 (1 of 1), RC51 -0.24, QHY SII -0.16. The prediction failed as before on SH61
and held on ZS61 and the ASI1600 alone. The within-night spread on green, p90/p10, is 1.04 to 1.21x
across the archive, smaller than the mosaic read's 1.01 to 1.47x: what the mosaic width called a
spread was the retry, and the real one is at the light end everywhere.

Two facts about the width itself, both owed to E2.10. The green profile fit was refused on 27 percent
of subs (5,876 of 8,034 fitted), unevenly: 100 percent on the mono trains, 92 percent on the ASI533
at 130 mm, 63 percent on the SV605CC and 0 of 60, 78 and 84 on its three warmest sessions (Orion
L-Quad 2025-10-15, Orion L-Ultimate 2025-10-14, Tarantula L-Ultimate 2025-10-14), which are the
sessions whose star lists are half residual warm pixels (the registration finding above); the fit runs
at the detector's positions, so the same defect refuses it. And the site fallback did its job: all 27
SharpCap sessions carry a computed air mass now, and the header cross-check stands unchanged.

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

**The candidate list on the green width, 2026-09-07 evening (`C:/temp/e2/e210-seeing-split-green.txt`;
the table above was ranked on the mosaic width and is superseded).** 14 of 75 sessions reach p90/p10
of 1.15 on `SubFwhmGreen`, but the thirds now differ by 1.10 to 1.25x rather than 1.13 to 1.43x, and
the largest ratios are on the undersampled trains (Rim SII at 6 arcsec a pixel: 0.90 against 1.12 px,
a width below the sampling that no kernel can be drawn for). On the trains that sample their stars:

| session | train | subs (green fitted) | p90/p10 | sharp third px | soft third px | soft/sharp |
|---|---|---:|---:|---:|---:|---:|
| Statue of Liberty, L-Quad | SH61 at 270 mm | 251 | 1.21 | 1.45 | 1.69 | 1.16 |
| Rosette, L-Quad | SH61 at 270 mm | 115 | 1.20 | 1.38 | 1.61 | 1.17 |
| Helix, L-Ultimate | SH61 at 270 mm | 119 | 1.20 | 1.50 | 1.74 | 1.16 |
| Pleiades, L-Quad | SH61 at 270 mm | 49 | 1.18 | 1.63 | 1.89 | 1.16 |
| Tarantula, L-Ultimate, 2025-11-01 | SH61 at 270 mm | 63 | 1.19 | 1.24 | 1.42 | 1.15 |
| SMC, 2024-10-02 | ZS61 at 289 mm | 40 | 1.18 | 2.18 | 2.51 | 1.15 |
| Horsehead, L-Quad | SH61 at 270 mm | 51 | 1.16 | 1.72 | 1.92 | 1.12 |

The Orion 2025-10-15 night that E2.10a paired is absent: none of its 60 subs fitted on green (the
warm-pixel star lists, above), so its split is known only from the diagnostic's six frames (1.15 to
1.25x). No session reaches the 1.3x where E1 says the range begins to matter; the real-blur pairs this
archive can supply are 1.15 to 1.17x on the SH61, and a 1.16x pair at 1.45 against 1.69 px is exactly
the light end where E1d read the kernel at 0.65 to 1.07 of its label. E2.10's second attempt therefore
waits on two things this day supplied one of: a stacking path whose output width follows its input
(the registration fix below, being validated) and a detector that does not hand the fit warm pixels,
so the SH61 sessions' fits stop refusing a third of their subs.

**E2.10b, pre-registered 2026-09-07 late evening: the pair again, on a stack that keeps its inputs'
width.** Both blockers moved during the evening (the registration fixes, R1's Lanczos-3, E1g-2's fit
that reads a sharp core), so the recipe runs once more on the best sampled candidate.

*Setup.* Statue of Liberty Nebula 2026-02-14 (SV605CC, L-Quad, 256 subs of 60 s at -5 C, 251 green
fits at 1.40 / 1.60 / 1.70 px for p10 / p50 / p90, the sharpest and softest thirds' medians 1.45 and
1.69 on the store; this paragraph first said 120 s, and the first launch at 20:12 stacked the night's
thirteen 120 s frames in four minutes before the split refused a session key that matched two store
records, since 45 of the night's 303 frames carry the OBJECT card "Skull and Crossbones Nebula";
group and key corrected, relaunched 20:25). The whole session stacked once (`--group-filter
StatueOfLibertyNebula_light_60s --group-temp-tolerance 2 --strategy Float16Staged
--warp-interpolation Lanczos3`, reference the run's pick), its manifest split by `psf-seeing-split.py --width green` into the sharpest and softest thirds
(about 85 frames each plus the shared reference), each third stacked from its manifest with the same
options into its own directory, then `SeeingSplitPairProbe` exactly as E2.10a: the estimator's width on
A and B, the profile fit on each (E1g-2's), `est-c` and `est-cb` difference kernels by composition,
Richardson-Lucy on B at 20, 40 and 60, read against A. Launcher `run-e210b-statue.ps1`, output
`C:/temp/e2/e210b-statue/`.

*Prediction.* The thirds' masters differ on the green fit by 1.10 to 1.16 (their subs' medians differ
1.16, a Lanczos stack is the mean of its frames to two percent, and a third's mean sits a little inside
its median); both fits succeed on all three channels now that the core is what is fitted. At 60
iterations `est-c` reads rec/A at 1.00 to 1.10 on at least two of three channels with stars over A at
0.7 to 0.95 and ring excess under 40 percent, E1d's 1.1 to 1.3x row on real blur; `est-cb` within 0.05
of `est-c`. Confidence moderate on the widths, low on the recovery: a 1.1x difference at 1.5 px is a
kernel of about 0.7 px effective width, the light end where E1d read the composition at 0.92 to 1.07
and where the oracle removes little because there is little to remove.

*Kill.* The thirds' masters differ by under 1.05 on the fit: then a 1.16x split in the subs does not
survive even a width-preserving stack, and the seeing split is not a usable real-blur source on this
archive at all. Or rec/A above 1.20 on every channel (the kernel does not transfer to real blur), or
stars over A above 1.10 with rec/A under 1.00 (fabrication on real data), as in E2.10a.

*Cost.* About 20 minutes of Release CLI for the three stacks, a minute of probe.

**E2.10b READ 20:50 the same evening: the first real-blur pair, and the oracle recovers it on two
channels of three (`C:/temp/e2/e210b-pair-probe.txt`).** The stacks: 244 frames in the full master
(the 60 s group, temperatures -4.8 to -5.1 C under the tolerance), 79 in the sharp third (subs' green
median 1.45 px, range 1.31 to 1.53) and 80 in the soft (1.69, 1.66 to 1.73; the reference added), the
split dropping five matched frames without a stored width, `STACK_N` confirming 79 and 80 (the
manifest a `--manifest` stack writes re-lists the whole group's 244 frames, a cosmetic defect noted
in `known-limitations.md`). On the 1024 px crop both cover, chosen for the most sharp-master stars
(1,926 to 2,295 at snr 20):

| ch | fit A (beta) | fit B (beta) | fitted B/A | est-c kernel | rec/A at 60 | recov | stars over A | ring excess |
|---|---|---|---|---|---|---|---|---|
| 0 | 1.70 (4.0) | 2.02 (railed) | 1.185 | 0.77 px | 1.007 | 88 % | 1.07 | 14 % |
| 1 | 1.73 (railed) | 2.06 (railed) | 1.190 | 0.91 px | 1.085 | 59 % | 0.97 to 0.99 | 36 % |
| 2 | 1.73 (railed) | 2.10 (5.2) | 1.215 | 0.98 px | 1.139 | 58 % | 0.84 | 35 % |

The prediction on the pair holds and is exceeded: the masters differ 1.19 to 1.22 on the fit against
the predicted 1.10 to 1.16, both fits succeeding on every channel (the sharp master's cores rail the
exponent grid at 24.95, which E1g-2 said a Gaussian-cored profile would). The prediction on the
recovery holds on the pre-registered two channels of three (rec/A 1.007 and 1.085; the third 1.139,
where the estimator's own median widths put B/A at 1.33 against the fit's 1.215) with ring excess
under 40 percent everywhere (14 / 36 / 35) and `est-cb` within 0.005 of `est-c` on every row; the
star prediction (0.7 to 0.95 of A) is missed upward on two channels (1.07 and 0.98), the one number
to watch, since channel 0's 1.07 with rec/A at 1.007 is two points inside the fabrication kill
(stars over 1.10 with rec/A under 1.00). Twenty iterations already read what sixty do: the kernels
are 0.8 to 1.0 px on 1.7 px cores and Richardson-Lucy converges at once there. Two things the run
adds. The thirds' masters sit 1.17 to 1.24x above their own subs' green medians (1.70 against 1.45,
2.02 to 2.10 against 1.69) under Lanczos-3, where R1's per-star reading on the Orion night had the
master at its frames' mean; the two widths here are different statistics (the store's is the fit on
a VNG green plane at the detector's positions on a sub, the probe's on the stacked master's crop), so
this is an observation to measure per star before it is a finding. And the estimator's median FWHM
and the profile fit disagree on B/A by up to 0.12 per channel (1.06 / 1.21 / 1.33 against 1.185 /
1.19 / 1.215), the HFD statistic being the noisier of the two on a soft master. Verdict: PASS on the
pair and on the recovery, the star count noted; E2.10c on the Orion night reads whether a 1.26x
split behaves the same.

**E2.10c, pre-registered 2026-09-07 late evening: the Orion night again, ranked on the guarded store.**
The guarded re-measure made the Orion L-Quad 2025-10-15 night the archive's best pair (55 green fits
of 68 subs at 1.82 / 2.17 / 2.46 px; thirds 1.89 against 2.38, 1.26x), the night E2.10a built on the
floor-ranked list and found no pair in (2.82 against 2.72). Same recipe as E2.10b on that night
(`--group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy Float16Staged
--warp-interpolation Lanczos3`, the split on `--width green` from the guarded store, thirds of about
18 frames each plus the reference, then `SeeingSplitPairProbe`). Launcher `run-e210c-orion.ps1`,
output `C:/temp/e2/e210c-orion/`, queued behind E2.10b on the Release CLI. *Prediction:* the thirds'
masters differ 1.15 to 1.26 on the green fit and both fits succeed; at 60 iterations `est-c` reads
rec/A 1.00 to 1.10 on two of three channels with stars over A at 0.7 to 0.95 and ring excess under 40
percent. *Kill:* the masters differ under 1.10 (then the ranking was not what E2.10a lacked, and the
remaining suspect is the stack's own reference-and-mean); rec/A over 1.20 everywhere; or stars over A
above 1.10 with rec/A under 1.00. *Cost:* about 15 minutes of Release CLI, a minute of probe.

**E2.10c READ 21:00 (`C:/temp/e2/e210c-pair-probe.txt`): the pair holds, the recovery lands just
outside the band on every channel, nothing killed.** The thirds are 19 frames each (18 by rank plus
the reference; the split dropped 16 matched frames without a stored green width), subs' medians 1.89
against 2.38 px. The masters' fits: A 2.28 / 2.26 / 2.22 px, B 2.90 / 2.65 / 2.64, fitted B/A 1.270 /
1.172 / 1.191, inside the predicted 1.15 to 1.26 and well over the 1.10 kill: the ranking WAS what
E2.10a lacked. At 60 iterations `est-c` (kernels 1.03 to 1.21 px) reads rec/A 0.951 / 1.119 / 1.123
against the predicted 1.00 to 1.10 on two channels: channel 0 over-sharpens (154 percent recovered,
stars 1.04 of A, under the 1.10 fabrication bar; its sharp fit has the heaviest wing of the six,
beta 2.3, so the composed difference kernel is the widest) and channels 1 and 2 stop two points over
the band with stars 0.85 to 0.94 and ring excess 14 percent; `est-cb` within 0.01 of `est-c`. On
19-frame thirds with 190 to 220 stars in the crop the reading is noisier than the Statue's (79 to 80
frames, 1,900 to 2,300 stars), which is the cheaper of the two lessons; the other is that the
estimator's difference kernel on a heavy-winged sharp core over-reads, the E1d 1.3 to 1.6x row's
mechanism on real blur. Verdict: pass on the pair, miss on the recovery band, not killed; two real
pairs now exist (1.2x and 1.2 to 1.27x) for E3.0 to be read against.

**E2.10a, pre-registered 2026-09-07: the estimator step and the oracle on the Orion pair.**

*Setup.* The whole session stacked once against the archive root (`tianwen stack D:/Astro-Organized
--group-filter Orion --group-exclude Ultimate --strategy Float16Staged`), its manifest split by
`psf-seeing-split.py` into the sharpest and softest thirds by the store's `SubFwhm` (twenty frames
each plus the shared reference), each third stacked from its manifest into its own output. Then
`SeeingSplitPairProbe` (`TIANWEN_E210_PAIR_DIR`): on a centred square of up to 1024 px of the region
both cover, per channel, the deployed estimator's width and star count on A and B, `PsfProfileFit`
with the signal floor on each, the difference kernel by composition in two shape arms (`est-c`, beta
4 as the synthetic arms injected; `est-cb`, the soft frame's own fitted beta, since the shape of real
seeing is not known), Richardson-Lucy on B at 20, 40 and 60 iterations, read against A: recovered
width over A's, stars over A's truth-anchored count, ring excess over B's own null. Float16Staged on
all three stacks so A and B are integrated alike; the deployed master was BayerDrizzle, a 4 to 9
percent PSF difference the plan already records.

*Prediction.* Both fits succeed on all three channels (twenty-frame masters of a rich field, under the
floor). B/A as the estimator reads it on the masters lands at 1.3 to 1.45 (the thirds' sub medians
were 1.73 and 2.47 px, 1.43; a stack of subs sits near their mean and registration adds a little to
both). At 60 iterations `est-c` reads rec/A at 1.00 to 1.10 on at least two of three channels with
stars over A at 0.55 to 0.85 and ring excess under 40 percent: E1d's noisy 1.3-1.6x row (rec/truth
1.03, stars 0.63, ring 23 percent) with a shape error on top, since a beta-4 kernel is a guess for
seeing. `est-cb` is within 0.05 of `est-c` on rec/A, because composition takes the shape into account
either way and the two betas differ by less than the fit's own scatter. Confidence moderate on the
width, low on the stars column: A is a twenty-frame master and carries noise of its own, so the count
ratio is against a noisy truth, which the synthetic rows never were.

*Kill.* rec/A above 1.20 on every channel at 60 iterations with both fits present. Then the difference
kernel estimated from stars does not transfer from drawn Moffats to real blur (the wings of seeing
are not a Moffat's, or the two masters' registration residuals dominate the difference), and the
estimator step needs a real-blur calibration before the unrolled operator can lean on it. A second
kill: stars over A above 1.10 with rec/A under 1.00, fabrication on real data, which the synthetic
rows never showed for `est-c`.

*Then.* The same probe's GPU arm (`TIANWEN_E210_SAS=1`): the shipped SAS AI4 graph on B, read the
same way, which is the first time the shipped deconvolver is scored against a real truth. Not
pre-registered as a pass or fail: it is the baseline any trained arm must beat on this pair, and its
number is the finding.

**E2.10a ran 2026-09-07 (three launches; `C:/temp/e2/e210a-pair-cpu.txt`, `-sas.txt`,
`e210-orion/` for the masters) and found no pair to score: the two thirds are the same width.** On
the crop with the most stars (1024 px, 202 to 227 sharp-master stars at snr 20), the estimator reads
A at 2.84 / 2.69 / 2.68 px and B at 2.76 / 2.63 / 2.62 across the channels, B/A 0.97 to 0.98; the
bright-star fits read A 2.66 / 2.57 and B 2.70 / 2.65 (B/A 1.02 to 1.03; the third channel's A fit
refused). The composed difference kernel is therefore 0.28 to 0.43 px, and the oracle, correctly,
does nothing (rec/A 0.971 to 0.976 at every checkpoint, stars 1.12 to 1.13 of A's because B has more
detections, ring excess 0). The shipped graph's baseline on the same crop: out/A 0.98 / 1.00 / 0.99,
stars 1.01 to 1.08, ring excess 2 to 3 percent, five seconds. Neither pre-registered kill applies (the
kills presuppose a B wider than A) and the prediction's premise (B/A 1.3 to 1.45) failed at the
input. Three probe faults were found and fixed on the way and are recorded on the probe: the two
masters are on different canvases (the origin cards, above), the geometric centre of the field is
M42's core (the crop is chosen by star count now), and the detector's two scale conventions (a
crop is normalised to a peak of 1 before detection).

*Why the thirds are the same, measured stage by stage (`SeeingSplitDiagnosticProbe`, one
estimator, whole frame).* The six subs at the ends of the store's ranking, the two thirds, the whole
night and the retained drizzle master:

| stage | store width | mosaic, mono path | AHD green (estimator / fit) | VNG green (estimator / fit) |
|---|---:|---:|---:|---:|
| sub 01-30-20, "sharpest" | 1.71 | 1.70 | 2.14 / 2.09 | 2.01 / 1.90 |
| sub 02-57-56 | 1.71 | 1.70 | 2.00 / 1.92 | 1.90 / 1.73 |
| sub 03-45-13 | 1.71 | 1.70 | 2.89 / refused | 2.78 / refused |
| sub 04-25-22 | 2.57 | 1.70 | 2.52 / 2.45 | 2.38 / 2.19 |
| sub 04-49-28 | 2.57 | 1.70 | 2.51 / 2.48 | 2.41 / 2.13 |
| sub 04-43-26, "softest" | 2.60 | 1.70 | 2.50 / 2.41 | 2.40 / 2.17 |
| sharp third, Float16Staged, 20 | | | 2.77 / 2.82 | |
| soft third, Float16Staged, 21 | | | 2.64 / 2.72 | |
| whole night, Float16Staged, 71 | | | 2.73 / 2.81 | |
| retained master, BayerDrizzle, 60 | | | 2.65 / 2.74 | |

Three findings, in order of reach. **First, the store's per-sub width is not a star width on an OSC
frame.** `FrameRegistration.DetectAsync` measures on the pre-debayer mosaic through the mono path,
for registration reasons the code documents, and that measure reads 1.70 on every sub here, the
sharp ones and the frame that both debayers put at 2.8 to 2.9; the store's spread from 1.71 to 2.60 is
that floor plus what the 2000-star retry does to a median, not seeing (corrected the same evening: the
1.70 is the warm pixels' own width dominating the median, not a floor of the mono path; the photosite
measurement below has the numbers). E2.9's slopes and E2.10's
candidate list were both computed on it, so E2.9 is withdrawn to inconclusive (its section says so)
and the candidate list is unreliable; the ranking here put the night's softest frame in the sharp
third. **Second, the subs DO differ on the green plane**, by 1.15 to 1.25x on the bright-star fit
(1.7 to 2.1 against 2.1 to 2.5 px), so a seeing split of this night exists, at the light end and
smaller than the store said. Red and blue are not usable for it: AHD and VNG disagree by a factor of
two on red (1.5 against 3.1 px), so a per-channel width on a debayered OSC sub is a property of the
interpolation. **Third, every stack of this night sits at 2.7 to 2.8 px on green, the drizzle
master included, from subs that measure 1.7 to 2.5**: the integration path adds about two pixels in
quadrature and erases a 1.2x split. Which stage does it (the warp's interpolation, the debayer, the
averaging of frames whose PSF varies 1.5x) is not separated here and is the first thing to measure
before E2.10 is attempted again; the plan already records that a drizzled and a staged master differ
4 to 9 percent, and this says both sit far above their subs.

**The third finding placed, 2026-09-07 evening: the registration halved every shift under 5 px, and
the stack's blur is misregistration, not resampling.** Measured in three steps on the six frames
nearest the reference (`exp-near6-norm`, stacked in RAM with `--save-normalized` so each warped frame
is on disk on the shared canvas):

1. *Where each warped frame sits* (`ReportTheRegistrationScatterOfTheWarpedFrames`, its stars matched
   to the reference frame's): medians of (0.81, 0.30), (-0.75, 0.64), (1.80, -0.60) and (0.76, -0.82)
   px for the four frames within 2 px of the reference, (0.00, 0.02) for the one 6 px away, each with an
   rms about its median of 0.14 to 0.50 px. Whole frames sit off, stars are not scattered; and each
   offset is minus the manifest's own translation for that frame.
2. *The true drift*, from a phase correlation of the raw subs on one green photosite plane: about twice
   the manifest's translation for the four (3.7 px against 1.72 for frame 0036) and equal to it for the
   one at 6 px. The transform was applied faithfully (the residual fixed pattern moves by exactly it);
   the transform itself was half.
3. *Where the half comes from* (`ReportWhereTheRegistrationLosesHalfTheShift`, the pipeline's own
   calibration and detection re-run on the subs, `C:/temp/e2/e210-registration-half.txt`):

| frame | manifest t | detections | unmoved of the raw pairs | true drift (mode) | bulk quad t | refined t (rms px) | refined, unmoved removed (rms px) |
|---|---|---:|---:|---|---|---|---|
| 0032 | (-0.87, -0.50) | 1259 | 647 of 1254 | (-1.57, -0.59) | (-1.71, -0.81) | (-0.87, -0.50) (0.87) | (-1.67, -0.76) (0.47) |
| 0030 | (0.86, -0.76) | 817 | 377 of 808 | (1.61, -1.36) | (1.58, -1.35) | (0.86, -0.76) (1.07) | (1.60, -1.43) (0.27) |
| 0036 | (-1.72, 0.58) | 1210 | 610 of 1193 | (-3.57, 1.18) | (-3.60, 1.19) | (-1.72, 0.58) (1.89) | (-3.54, 1.26) (0.49) |
| 0037 | (-0.82, 0.87) | 1099 | 521 of 1089 | (-1.60, 1.70) | (-1.64, 1.69) | (-0.82, 0.87) (1.20) | (-1.57, 1.71) (0.51) |
| 0040 | (-5.04, 3.22) | 1447 | 744 of 761 | 6 px, outside the pairing | (-5.00, 3.24) | (-5.04, 3.22) (0.45) | (-5.04, 3.22) (0.33) |

The bulk quad solution is right on every frame. The rigid refiner then pairs each detection with its
nearest reference detection within 5 px and fits a Procrustes over all pairs; on this night about half
of every frame's detections are residual warm pixels (the group's dark is a -5 C one under 12 C lights,
and the 2000-star retry lowers the threshold until it reaches them), which sit at the same sensor
position in both frames and pair with themselves at a residual of minus the drift. The fit averages
the two populations: half, reproduced to the hundredth of a pixel on all five frames. Frame 0040
escapes because its 6 px drift puts its own copies outside the 5 px tolerance. Every stack of the
night is therefore a sum of frames each misplaced by half its own drift, up to 2.5 px, which is the
two pixels in quadrature the table above could not place; the frame-count dependence (two or three
frames at the subs' width, six or more at 2.6 to 2.8) is the drift distribution filling in.

*Fix (`RegistrationRefiner.UnmovedTolerancePx`, 0.35 px).* A pair whose raw positions coincide within
the tolerance while the bulk affine moved the detection by more than twice it is a sensor-fixed
detection and is dropped from the refinement, in both refiner variants; the count is logged per frame
("N unmoved dropped"). A frame that drifted under 0.7 px cannot be separated this way and keeps them,
with a bias bounded by half its own drift. Pinned by `RegistrationRefinerTests`. The detector accepting
a single warm photosite as a star (the mono fold turns it into a 2 by 2 blob that passes the size
floor) is the cause one level down and is recorded as owed: it also refuses the green profile fit on
the three warmest SV605CC sessions (0 of 60, 78 and 84 subs fitted, against 42 to 100 percent
elsewhere) and feeds the quality gate's medians and the reference pick. *Its measurement,
pre-registered* (`ReportTheSinglePhotositeSignatureOfTheUnmovedDetections`): on the same five frames,
each detection classed by the reference pairing (unmoved, or moved and paired within 1 px after the
bulk affine) and measured on the calibrated mosaic as the fraction of its background-subtracted 3 by 3
flux that the peak photosite carries, beside the detector's own HFD, FWHM and SNR. *Prediction:* the
unmoved population reads above 0.85 on nine in ten and the moved below 0.6 on nine in ten, so one
threshold on the mosaic separates them; the detector's HFD does not (both populations straddle 1.0 to
1.5 px), which is why the size floor let them through. *Kill:* more than one in ten of either
population on the wrong side of every threshold between 0.6 and 0.85. Then a per-frame photosite test
cannot carry the guard and it needs persistence across frames instead.

**Measured the same evening (`C:/temp/e2/e210-photosite-signature.txt`): the prediction held on the
mosaic and failed, in the useful direction, on the detector's own width.** On all five frames the
unmoved population reads a peak fraction of 0.92 / 0.97 / 0.99 at p10 / p50 / p90 and the moved 0.15 to
0.18 / 0.28 to 0.31 / 0.37 to 0.41; 98 to 99 percent of the unmoved are over 0.85 and none of the moved
is over 0.60, so any threshold from 0.45 to 0.85 separates them completely (377 to 742 unmoved against
430 to 650 moved a frame). The detector's HFD and FWHM separate them too, which the prediction denied:
every unmoved detection reads FWHM 1.69 to 1.71 and HFD 1.70 to 1.71 on the mono plane, the stars 2.55
to 2.86 and 3.38 to 3.75. That 1.70 is the number the store, the estimator and the quality gate
reported for every sub of this night: not the mono path's floor, as the first finding above and
`docs/known-limitations.md` said until this evening, but the warm pixels' own width (a single photosite
folded into a 2 by 2 blob) dominating a median over a list half made of them; the mono path reads a
star's width, widened by the fold. A size floor at 2 px would clear this night and fail a finer-sampled
one, so the guard is the photosite fraction, with two caveats the measurement leaves: a faint spike near
the detection threshold has noise in its eight neighbours that lowers its fraction (to about 0.6 at an
SNR of 5; this population's median SNR is 59 to 92, the stars' 110 to 184), and an undersampled star on
the 6 arcsec trains can reach 0.65 to 0.75 when centred on a photosite. So the threshold sits at 0.85,
and the faint tail is the 2000-star retry's to stop pulling in. The guard itself (task 17) is the next
detector change, pre-registered against the star-detection fixtures and the three sessions' fit fractions.

*The guard's store validation, read at 20:10 the same evening (`--remeasure-subs` over 79 sessions and
8,507 subs, 96 minutes, `C:/temp/e2/store-green-counts-guard.txt` and the two `*-guard.txt`
readouts).* Two of the three warm sessions have their green fits: Orion L-Quad 2025-10-15 fits 55 of
68 (1.82 / 2.17 / 2.46 px at p10 / p50 / p90, p90 over p10 1.36, the widest in the archive) and Orion
L-Ultimate 2025-10-14 64 of 77 (1.33 / 1.48 / 1.62); Tarantula L-Ultimate 2025-10-14 is still 0 of 84.
The archive's fit fraction moved 73 to 74 percent (5,987 of 8,037); E2.9 is unchanged (1 of 78 sessions
reaches a 1.3x span attributable to air mass; the Orion night's slope is -0.49); E2.10's candidate list
grew from 14 to 19 sessions at p90 over p10 of 1.15, and its best pair is now the Orion L-Quad night
itself at 1.26 (sharp third 1.89 px against soft 2.38), which E2.10a built on the floor-ranked list and
found no pair in. The Tarantula session's refusal was then measured on its own subs
(`ReportWhyTheGreenFitRefusesASessionsSubs`, raw and calibrated, six subs spread through the night,
`C:/temp/e2/green-fit-refusals-tarantula-*.txt`): the fit refuses `PoorFit` at a log residual of 0.5 to
1.0 on the warm early subs (12.7 to 10.1 C) and passes at 0.26 to 0.31 once cooled to 9 C; the refused
stacked profile is a spike (0.04 of peak at 0.9 px against 0.21 on a passing sub, a bump at 1.5 px),
which is a hot photosite as VNG renders it on the green plane. The detections the guard kept split by
peak-photosite share into a class under 0.5 that holds at 1,000 to 1,200 a frame through the night (the
stars) and a class between 0.5 and 0.85 that falls 2,260 to 1,076 as the sensor cools (warm pixels
whose neighbours' noise pulls the share under 0.85, and pairs). The cooled sister night (-10 C) carries
1,200 to 2,100 of that class and fits at 0.14 to 0.36 because there they are faint; at 12.7 C they are
an order of magnitude brighter and the fit's stack is the 400 brightest by PEAK over the signal floor,
so they fill it. That is the caveat the signature measurement stated (a faint spike's share drops
toward 0.6) turning out to be a population, not a tail, on the warmest night; the unmoved-versus-moved
pairing that chose 0.85 admits only detections bright enough to pair, so it never saw them. Owed as
task 21, pre-registered in `known-limitations.md`: a noise-aware guard or a flux-ranked stack, read on
the share classes of this night, its cooled sister and the two Orion warm nights (where the class is
15 to 53 a frame on L-Quad and 103 to 142 on L-Ultimate).

*Validation, pre-registered before the run.* The whole night stacked again with the fix
(Float16Staged, reference pinned to frame 0033), its manifest split to the same six frames and those
stacked in RAM with the warped frames saved (`exp-near6-fixed`). *Prediction:* the register log shows
300 to 700 unmoved dropped per frame and a refine rms under 0.6 px; the six warped frames' median
offsets against the reference all fall under 0.15 px; the near6 master's green width falls from 2.65
px to under 2.3, the whole night's from 2.73 to under 2.4 (the subs' 1.9 to 2.5 plus the bilinear
warp). *Kill:* any of the six frames still off by more than 0.5 px, or the whole-night master not
under 2.6 px. Then the halving was not the only mechanism and the resampling stage is measured next.

**Validated the same evening (`exp-full-fixed`, `exp-near6-fixed`, `exp-full-norm`;
`C:/temp/e2/e210-scatter-fixed.txt`, `e210-scatter-full.txt`, `e210-perstar-*.txt`,
`e210-near6-subs.txt`).** The registration half held. The new manifest's translations are 1.9 to 2.2
times the old on the four frames within 2 px and unchanged on the one at 6 px; all six warped frames
sit within 0.03 px of the reference (0.7 to 1.9 before), and over the whole night's 71 frames the
median offset is 0.02 px (p90 0.05, max 0.16) with a per-star scatter about it of 0.21 px median (p90
0.28; five frames at 0.4 to 0.6), worth 0.6 px in quadrature to the whole-night master. The register
log shows 377, 606 and 749 unmoved dropped on the frames within 5 px of the reference and none beyond
(their copies fall outside the tolerance), refine rms 0.27 to 0.58 near against 0.5 to 0.83 far, and
the registration bad-pixel map, refused that morning at a 2.28 px spread, now builds (5,538 persistent
outliers, 5,530 of them also in the dark's mask).

The width half was killed. The near6 master's green fit went 2.65 to 2.47 px (predicted under 2.3), the
whole night's 2.81 to 2.81 (predicted under 2.4, kill at 2.6). The six subs' own green fits are 1.99 to
2.18 px (`ReportTheNear6SubsOwnWidths`), so a six-frame stack placed to 0.03 px still sits 1.3 px above
its inputs in quadrature. Star by star (`ReportPerStarWidthsOfTheWarpedFramesAgainstTheirMaster`: each
warped frame's stars at SNR 30 to 300 matched to the same stars in the master, the detector's width on
both) the stack adds nothing of its own: the master reads 2.39 px on those stars against the six warped
frames' 2.69, 2.64, 2.17, 2.38, 2.42 and 2.14, whose mean is 2.41; over the whole night the master
reads 2.70 against the 71 warped frames' median 2.69 (ratio p50 1.003, p10 0.90, p90 1.15). What widens
the frames is the warp. The two whose shift is an integer or nearly one (the reference at (0, 0), frame
0040 at (-5.04, 3.22)) read 2.14 and 2.17, their subs' own width; the four at fractional shifts read 2.38
to 2.69, the widest at a phase of (0.6, 0.57). A bilinear kernel (a triangle of unit base) adds a
variance of phase times one minus phase per axis: 1.18 px of FWHM in quadrature at half phase, 0.96
averaged over phases, so 2.17 becomes 2.47 at worst by the arithmetic and the widest frame reads a
little past that (1.6 in quadrature; the detector's width on a resampled plane may overstate it). The
ranking is unambiguous and the mechanism is the only one left: the combine, the rejection and the
normalisation add nothing measurable. The two pixels in quadrature the stage table could not place were
two things, the halved shifts, fixed, and the bilinear warp of `WarpToReferenceGridAsync`, a design
choice every master of every session carries: at this archive's 2 px seeing it is 45 to 60 percent of a
star's width added in quadrature.

*Then.* The warp kernel is the next experiment (R1, below). Until it lands, every retained master in the
store is its subs plus about one pixel in quadrature, the deconvolver's training targets included, and
E2.10's pairs cannot be built from masters whose width does not follow their inputs.

#### R1: the warp kernel (pre-registered 2026-09-07 evening)

*Change.* `StackingOptions.WarpInterpolation` (`Bilinear`, the default and byte-identical to today;
`Lanczos3`), on the CLI as `--warp-interpolation`, threaded through `FrameRegistration.WarpToCanvasAsync`
to `Image.WarpToReferenceGridAsync` and its region variant; Lanczos-3 is a = 3, six taps an axis,
weights normalised per sample, a tap on a NaN or outside the source drops out with its weight.

*Measurement.* First on a synthetic Gaussian field: the detector's width after a (0.5, 0.5) shift under
each kernel against the unshifted field, which calibrates the arithmetic above. Then near6 and the
whole night stacked from the fixed manifests under Lanczos-3, the per-star probe on their warped frames
and masters, the masters' green fit, and the ring excess (`Ringing`) on each master against its bilinear
twin.

*Prediction.* Synthetic: bilinear widens a 2.1 px star by 1.1 to 1.3 px in quadrature at half phase,
Lanczos-3 by under 0.4. Real: the fractional-phase frames read within 5 percent of the integer-phase
ones (2.14 to 2.28 rather than 2.38 to 2.69); the near6 master's per-star width falls from 2.39 to under
2.25 and its fit from 2.47 to under 2.30; the whole night's fit from 2.81 to under 2.5; ring excess
under 10 percent of a star's peak, since a 2 px star is at the edge of what a sinc kernel rings on.

*Kill.* The fractional-phase frames stay above 2.4 px under Lanczos-3, or the ring excess passes 20
percent. Then the widening is not the interpolation kernel (or the kernel is unusable at this sampling)
and the warp is measured another way before any default changes. The default stays bilinear until the
user decides; a flip changes every master. (Decided 2026-09-12: CLAMPED Lanczos-3 is the default,
see "The re-bake" below for why the clamp and for the threshold sweep.)

**R1 read, 2026-09-07 evening (`WarpInterpolationTests`; `exp-near6-lanczos` against `exp-near6-fixed`;
`C:/temp/e2/e210-perstar-exp-near6-lanczos.txt`, `e210-ringing-near6.txt`, `e210-radial-near6.txt`,
`e210-stages-r1.txt`).** The synthetic half held exactly: on a 2.15 px Gaussian star shifted by half a
pixel on both axes the model-free second moment reads 2.437 px under bilinear (1.15 px in quadrature,
predicted 1.1 to 1.3) and 2.142 under Lanczos-3 (0.00, predicted under 0.4); the detector's own median
reads 2.14, 2.52 and 2.19 on the same three planes, so its width on a resampled plane overstates
bilinear's cost by about a third, which is the same excess the real frames showed (1.6 against 1.18).
The deepest dip below background within 6 px of the star is at the noise floor under both kernels.

The near6 half held on every pre-registered number but one measure that could not be read. Per star,
the Lanczos master reads 2.15 to 2.16 px on the same stars where the bilinear twin read 2.39 (predicted
under 2.25); the six warped frames read 2.35, 2.28, 2.17, 2.07, 2.05 and 2.02 against 2.69, 2.64, 2.17,
2.38, 2.42 and 2.14, and they now rank as their own subs do (frame 0030's sub is the night's widest of
the six at 2.18 on the green fit, frame 0037's the narrowest at 1.99) rather than by their shift's
phase; the master is still the mean of its frames (ratio 0.91 to 1.09 by frame). The estimator's median
on the master went 2.44 to 2.23. Ringing: the annulus-minimum measure reads the same on both twins
(undershoot -1.68 against -2.08 MADs, no dip, under one percent of a star's peak; 12 percent of stars
past one MAD on both, noise), and the stacked radial profile of the bright stars has no bin below zero
out to 8 px, a narrower core (0.199 against 0.250 of peak at 1.75 px, 0.095 against 0.134 at 2.25)
over the same wings. The 20 percent kill is nowhere near; the 10 percent prediction holds with room.

*What could not be read, and what it says about the estimator step.* `PsfProfileFit` REFUSES the
Lanczos master (`PoorFit`, log residual 0.83 against the 0.5 allowed; 400 stars stacked, 24 bins), so
the "fit under 2.30" prediction has no reading; the bilinear twin fits at 2.47 with beta 3.9. The
profile shows why: the fit works in log space over every quarter-pixel bin above a floor of 0.2 percent
of the peak, out to 12 px, and both masters' far wings sit at 0.7 to 1.5 percent of the peak from 4 px
outward, which is background residue the per-star annulus did not remove, not the star. A Moffat
through the bilinear master's blurrier core passes near that residue; through the Lanczos master's
narrower core it predicts 0.5 percent at 4 px where the profile holds 1.5, a factor of three in every
outer bin, and the residual crosses 0.5. So the refusal is the fit reading the sky, and it will refuse
any sufficiently sharp master the same way, which matters because this fit is the estimator step E3
leans on. Owed (E1g, below): a floor relative to the profile's own outer level, or the fit stopped at a
radius the star still owns, measured against E1e's 180 rows so the refusals and the widths are compared
before and after.

*The whole night* (`exp-full-lanczos` against `exp-full-norm`, 71 frames; `e210-perstar-exp-full-lanczos.txt`,
`e210-ring-radial-full.txt`). Per star the Lanczos master reads 2.47 to 2.49 px where the bilinear
twin read 2.70, and it is still the mean of its warped frames (ratio p50 1.014; the frames' median 2.46
against 2.69); the green fit reads 2.61 with beta 4.6 against 2.81 (this master the fit accepts: the
whole night's mean core is wide enough to sit over the residue), the estimator's median 2.52 against
2.72. The prediction said "fit under 2.5" and missed by 0.11, for a reason the per-star numbers make
plain: the night's 71 subs run 1.9 to 2.6 px on the green fit with a mean near 2.4, not the 2.2 the
prediction took from the six near frames, and a mean-combine cannot read below the mean of what it is
given. Ringing is identical on the twins (undershoot -2.58 against -2.48 MADs, 2.3 percent of stars past
one MAD on both, -0.7 percent of the peak) and no radial bin sits below zero (0.264 against 0.327 of
peak at 1.75 px, 0.137 against 0.178 at 2.25, the same wings from 4 px). So the whole-night master lost
exactly the kernel's contribution, sqrt(2.70 squared minus 2.48 squared) = 1.07 px in quadrature, the
0.96 the arithmetic gives for a random phase plus what the detector's measure adds, and what remains
over the sharpest subs is the night's own seeing spread averaged in.

*Standing.* Lanczos-3 keeps a 2 px star's width to within the subs' own scatter and rings below what
any of the four measures can see on this night; the default stayed bilinear until the user decided
(2026-09-12, Lanczos-3 clamped at a measured 0.7, see "The re-bake"), and every master built before
this carries about a pixel in quadrature. Then E2.10's second attempt has a
stack whose width follows its input, once the estimator's fit is made to read a sharp master.

#### E1g: the profile fit's floor against the far-wing residue (pre-registered 2026-09-07 evening)

*Change.* `PsfProfileFit` fits bins above a floor of 0.2 percent of the peak out to 12 px; real
masters carry 0.7 to 1.5 percent of background residue there after the per-star annulus, so the
log-space residual measures the sky and refuses sharp masters. Two candidate rules, chosen by
measurement: the floor at three times the median of the outermost quarter of the bins, or the fit
stopped at 3 FWHM. *Measurement.* E1e's 180 oracle rows again (refusals by check and widths against
truth), plus the four R1 masters. *Prediction.* Refusals fall from 30 of 180 to under 15 with widths
unchanged within 0.02, and the Lanczos masters fit at 2.15 to 2.25 px. *Kill.* Widths move by more than
0.05 on the clean rows, or refusals do not fall. Then the wing residue is not what refuses, and the
Moffat itself is the wrong wing model for a resampled star.

**E1g read the same evening: KILLED, and the hypothesis with it. The fit exposes its stacked profile
now, and the profile says what refuses is shape.** The relative floor was built (three times the
median of the outermost quarter of bins) and run against the four R1 masters before the oracle rows
came back: on the near6 Lanczos master it changed nothing, the same 24 bins and the same 0.83, because
the fit's per-star annulus already leaves the outer bins at 0.03 to 0.07 percent of the peak (the 0.7 to
1.5 percent plateau the probe had measured was against a GLOBAL crop background; the fit subtracts a
local one), so no bin sat between the two floors. On the detached-halo synthetic the halo sits in the
outer quarter, the relative floor rose into the core, and a `PoorFit` refusal became `TooFewFitBins`,
which is the wrong reason for the right answer. Withdrawn; the diagnostics keep the profile, the floor
and the fitted bins (`Diagnostics.Profile`, `Floor`, `FittedBins`) and the stage probe prints a refused
profile bin by bin.

What the near6 Lanczos master's profile shows, beside the best Moffat the search found (fwhm 2.32,
beta 2.65, 24 bins to 5.6 px): 1.000, 1.000, 0.919, 0.704, 0.521, 0.373, 0.243, 0.166, 0.106 at 0.12
to 2.12 px, a Gaussian of that width to within 0.02 at every bin from 1.1 px out; then 0.023 at 3.12 px
against the Gaussian's 0.007, 0.007 at 4.12 against 0.0002, 0.003 at 5.12: a faint wing at half a
percent to two percent that a Gaussian has not got and a Moffat with the half-maximum width fixed
cannot reach without overshooting the core (the beta-2.65 compromise reads 0.159 where the profile
reads 0.106 at 2.12 px and 0.016 where it reads 0.007 at 4.12). An equal-weight log fit over three
decades has no way to prefer the core, and refuses. The bilinear twin's blurrier core (2.47 fwhm) sits
closer to the Moffat family and passes at beta 3.9; so does every blurrier master, and so did the whole
night under Lanczos (2.61, beta 4.6), while the two-frame stage stack (`exp-ref-plus-1`, rms 0.76) and
the sharpest subs on VNG (fwhm 1.78, rms 0.77) refuse the same way. **The refusal is a systematic
against sharp profiles**, which is the estimator step refusing the inputs the deconvolver most wants
to measure, and the accepted betas on blurrier masters lean the same way. The oracle rows read the
withdrawn rule while it was still in the binary (`C:/temp/e2/oracle-e1g.txt`): 30 refusals of 180,
the same 30 as E1e, every row's width identical; the kill clause "refusals do not fall" is met on the
number as well as on the mechanism.

*E1g-2, pre-registered.* A fit that weights the core: the Moffat's residual in LINEAR space, or the log
fit over bins down to 2 percent of the peak (about 2 FWHM) with beta read there and the far wing
reported separately as a fraction of the peak at 2 and 3 FWHM. Measured on E1e's 180 rows and the four
R1 masters plus the two-frame stage stack and the VNG subs. Prediction: refusals under 10 of 180, the
near6 Lanczos master fits at 2.2 to 2.35 px, the sharp VNG subs at 1.8 to 2.0, widths on the rows both
fit unchanged within 0.02; the reported betas rise on the sharper masters (the wing they carry is
fainter than any beta under 4 says). Kill: widths move over 0.05, or the sharp masters still refuse.

**E1g-2 built and read on the masters and subs the same evening (`PsfProfileFit.CoreFitFloor` 0.02,
`Result.WingAt2Fwhm` and `WingAt3Fwhm`; `C:/temp/e2/e210-stages-e1g2.txt`).** Every profile the old
fit refused now returns, and every width the old fit accepted is unchanged to the hundredth (the width
is the half maximum and never depended on the fit range):

| profile | before: fit (beta) | after: fit (beta) |
|---|---|---|
| near6 Lanczos master (R1) | refused, rms 0.83 | 2.32 px (6.9), predicted 2.2 to 2.35 |
| two-frame stage stack `exp-ref-plus-1` | refused, rms 0.76 | 2.20 (4.5) |
| sub 03-05-58 VNG green | refused, rms 0.77 | 1.78 (2.8) |
| sub 02-57-56 VNG green | refused | 1.73 (2.6) |
| sub 03-45-13 AHD green | refused, rms 0.77 | 2.84 (7.6) |
| near6 bilinear master | 2.47 (3.9) | 2.47 (5.9) |
| whole night, bilinear | 2.81 (5.1) | 2.81 (8.1) |
| whole night, Lanczos | 2.61 (4.6) | 2.61 (7.8) |
| thirds, sharp and soft | 2.82 (4.7), 2.72 (4.7) | 2.82 (7.0), 2.72 (7.3) |
| retained drizzle master | 2.74 (4.3) | 2.74 (6.2) |
| subs AHD green, five | 2.00 (2.9), 1.92 (2.7), 2.09 (3.4), 2.69 (3.5), 2.84 (3.9) | 2.00 (4.4), 1.92 (3.6), 2.09 (5.3), 2.69 (4.8), 2.84 (6.7) |

The exponents rose on everything by one to three, as predicted: the far-wing bins had been pulling
every accepted beta toward heavy wings. Two consequences the measurement adds. Above an exponent of
about six the core carries no wing to tell exponents apart, so a beta-7 synthetic field reads 10.7 and
`PsfProfileFitTests` now asserts "Gaussian-cored" there rather than a value; the wing is reported
beside the fit as the profile's own value at two and three FWHM (pinned against the Moffat's closed
form at beta 2.5 and 4), which is the number a kernel's far wing should be read from. And the store's
`MoffatBeta` changes meaning with this: every beta in `psf-sessions.jsonl` and E0's "beta correlates
with FWHM" statistics were measured with the wing in the fit, so they are not comparable to a value
measured after E1g-2 until the store is re-measured (`--force-psf` on the masters; the running
`--remeasure-subs` pass predates E1g-2 too). The oracle rows over E1e's 180 are the last
pre-registered number, and follow.

**E1g-2 over E1e's 180 oracle rows (`run-e1g2.ps1`, `C:/temp/e2/oracle-e1g2.txt`, 23 minutes): 25
refusals of 180 against 30. The prediction of under 10 is missed, and the reason is the row behind the
row.** E1e's 30 rows were ten on the Eta Car 24 mm L-eNhance frame's channel 0 (`TooFewStacked` on the
clean whole frame: 292 detections, 51 in the band, 33 stacked of the 40 the fit needs), ten on its
channel 1 (the clean whole frame refused `PoorFit` at rms 0.67, one refusal that takes every row of the
channel with it), six on its channel 2 (`TooFewStacked` on the blurred frame) and four `PoorFit` on the
L-Ultimate master's channel 2. The core fit accepts the channel 1 clean frame (1.53 px), so the
estimator goes on to the OBSERVED frame row by row, and there the injected blur empties the brightness
band: 38 stars in the band at 1 px of blur, 10 at 2 px, 2 at 3 px, 0 at 4 px (stacked 25 / 9 / 2 / 0).
The two 0.5 px rows fit; the other eight refuse for want of stars. Three of the L-Ultimate's four rows
fit. What remains is 10 + 8 + 6 + 1: twenty-four star-budget refusals on one wide-field frame of about
1400 detections, which is the right answer (an estimate from two stars is not one), and a single
`PoorFit`. The fit's own refusals therefore fell from nine entries to one, and the count stayed above
10 because the field behind the shape refusal has too few stars once blurred, which the prediction did
not anticipate.

The kill line holds: the fitted widths (the `truth` and `blur` columns) are identical to the hundredth
on every row both runs fitted, and the whole-frame fallbacks fell from 99 to 71 arm rows (the `frame`
column) because the 512 px crop now fits more often. Recovery is unchanged at the median (d(r/t) p50
within 0.01 of the previous run in every band) and tighter at p90 in the bands that matter (1.1 to
1.3x noisy +0.24 to +0.13; 1.3 to 1.6x +0.12 / +0.15 to +0.09 / +0.09). What moved is the composed
kernel estimate: `estW/t` is 0.01 to 0.06 higher per band (1.3 to 1.6x noisy 0.99 to 1.05, 3x and up
1.02 to 1.05), because the composition takes the clean side's exponent and that exponent is now
higher (a more Gaussian clean core composes to a wider difference), and `estB/t` at p50 rose from 1.32
to 1.55 (p90 4.0 to 6.2), the same shift measured on the masters. Verdict: E1g-2 stands as the
estimator's fit (it measures the sharp inputs and the oracle's recovery is unchanged), the refusal
prediction is missed on the number, and the refusals left are a star budget no fit cures.

*What E2.10 needs before a second attempt.* A per-sub width that reads seeing: the bright-star
profile fit on the debayered GREEN plane, beside the mosaic width in the store (owed: a
`SubFwhmGreen` column, filled by `--remeasure-subs`, then E2.9's readout and the candidate list
re-run on it). And a stacking path whose output width follows its input's, or the pair is built
some other way (a per-frame comparison, or a stack that skips the resampling). Until both exist the
real-blur validation set does not, and E3's kernel check rests on the synthetic rows plus this one
observation that the estimator read a 0.3 px difference as 0.3 px.

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

**The GPU half ran 2026-09-07 (8 min, `C:/temp/e2/d1-perchunk-output-rim.txt`, after a first launch
that failed on the probe's own rescale, see the launcher notes) and read as a NULL: per-tile and
whole-image outputs agree to 0.01 px in every bin and to one percent in star count on all seven
masters.** That includes the masters where the per-tile labels DID differ: on the four 2025-26 L-Ultimate
sessions 243 to 269 of 289 tiles carry a psf01 other than the whole-image value, spanning 0.05 to
0.36 under the shipped encoding (a radius of 1.1 to 2.1 px), and the output does not move. The two
2024 sessions whose radii sit under the shipped floor clamp to psf01 0 on all but 1 and 17 tiles, as
the CPU half said they would, and are degenerate by construction. Neither the prediction (centre-to-
corner ratio moving toward 1) nor the kill (a bin narrower than the input's sharpest, a count moving
over 20 percent) fired: the input's c/o of 1.26 became 1.37 whole-image and 1.38 per-tile, 1.18
became 1.24 and 1.24.

Two things the same table says about the shipped graph itself, off-label as the note above allows.
It makes stars WIDER where they are already soft (the 2025-05-02 master's inner third 3.55 to 3.86 px,
outer 2.83 to 2.81), so on stars it worsens the field's centre-to-corner ratio rather than flattening
it; and it removes most of the outer third's detectable stars on five of seven masters (464 to 127,
463 to 157, 282 to 82, 392 to 113, 404 to 105) while the inner third's count holds within three
percent. A non-stellar deconvolver applied to a starless plate is what it is for, so neither is a
defect of the pipeline as shipped, but both are what a user gets if the plate is not starless, and
E2.10a's baseline on the Orion pair will read the same graph against a real truth.

*Reading.* The per-tile switch stays OFF for the shipped graph, as the pre-registration said it
would absent a measured gain, and the machinery ships for `OnnxTianWenDeconvolver` (E7), whose label
is per cell by construction. Whether the null is a graph that ignores its conditioning input at this
scale, or a label spread too small to see, is one more run: the same graph at fixed psf01 0, 0.5 and
1 on the 2025-05-02 master (`TIANWEN_PSF_PROBE_SENSITIVITY=1`); three identical rows say the input is
inert on this graph for a real master's stars.

**The sensitivity check ran the same day (1.7 min, `C:/temp/e2/d1-psf01-sensitivity.txt`), and the
rows are identical to within the measurement:** across the WHOLE shipped range, psf01 0 / 0.5 / 1
(a radius of 1 / 2.8 / 8 px), the inner third reads 3.86 / 3.85 / 3.88 px, the middle 3.41 / 3.39 /
3.37, the outer 2.82 / 2.79 / 2.76, with star counts within three percent (158 / 157 / 160, 424 / 435
/ 429, 79 / 82 / 81), against an input of 3.55 / 3.19 / 2.83. So the shipped SAS AI4 graph's
conditioning input does close to nothing on a real master's stars, and D1's null is the graph's
property, not the labels' spread. Per-tile conditioning is therefore moot for the shipped graph and
stays off; it is built for the graph E7 exports, whose label is per cell and whose response to it
will be measured before the switch is turned on. The one-word lesson for E7: a conditioning scalar a
graph learns to ignore costs nothing to carry and buys nothing, so E3's arms must show the operator's
output MOVES with its kernel (which for an unrolled Richardson-Lucy is true by construction) before
any per-tile machinery is credited.

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
solving for the kernel that realises it (the last shipped the same evening; next paragraph but one).

*Verdict.* Pass. The estimator step's width arithmetic is Moffat composition from here on, in the
probe and in the exporter's kernel label; quadrature stays only as E1b's recorded control.

*The exporter draws the ratio (ground-work, shipped 2026-09-07 evening).* `DatasetDegradationExporter`
draws each cell's blur as a ratio of its own measured width, log-uniform in
[`MinBlurRatio` 1.05, `MaxBlurRatio` 2.0], and solves the nominal Moffat width whose kernel AS SAMPLED
composes with the core to that ratio (`SolveNominalFwhmForRatio`: bisection in log width against
`MoffatComposition.ComposedFwhm` with the sampled kernel, fourteen halvings, 1 to 4 ms a draw at the
exporter's pixel bounds, so under a quarter of an hour on a full export's two hundred thousand). The
row records `BlurRatioDrawn` and `ComposedFwhmPx`, the realised width, so the realised ratio is on
the row rather than inferred; the pixel bounds still clamp the solved width and a cell with no
measured width keeps the pixel draw. Measured on the test fixture (3.7 px cores, 16 rows): every
realised ratio equals its draw to a thousandth, with nominal widths from 0.74 to 3.47 px, and the
solver's own theory on the archive's cores says what the draw now delivers against what a quadrature
draw did:

| core (beta of the kernel) | ratio | nominal solved | quadrature would draw |
|---|---|---|---|
| 2.15 px (3.0) | 1.10 | 0.88 px | 0.99 px |
| 2.15 px (2.5) | 1.50 | 1.74 px | 2.40 px |
| 1.53 px (4.0) | 1.30 | 1.22 px | 1.27 px |
| 2.81 px (6.0) | 1.05 | 0.91 px | 0.90 px |

Two corrections pull opposite ways and the solve carries both: a heavy-winged Moffat widens the half
maximum by MORE than quadrature (the 1.74 against 2.40 at beta 2.5), and the pixel sampling delivers
LESS than nominal below about 1.5 px (the 1.22 against 1.27 at beta 4 on the sharp core, where a
continuous kernel would need less). A degradation cache exported before this carries the light end
E1d found; the one E2's arms trained on is unaffected in its labels (measured on the degraded cell)
and lighter than its rows say in its light-end draws.

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

*Trajectories (added at 13:10 from the same logs, `training/denoise/n2n_gatetrace.py`, the time axis
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

**E2.8c ran 2026-09-07, 12:48 to 13:36, `C:/temp/e2/p2-star-c.log`. Prediction failed, kill line not
reached: two of five seeds, not three, hold both.**

| seed | selected step | selected out/truth | stars there | width minimum | observer out/truth | observer stars, x truth | gate out/truth at 1000 / 2000 / 3000 / 4000 |
|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 3700 | **1.238** | 0.74 | 1.238 | 1.127 | **11.2** | 1.42 / 1.45 / 1.27 / 1.24 |
| 1 | 3700 | **1.163** | 0.76 | 1.163 | 0.949 | **9.6** | 1.40 / 1.40 / 1.17 / 1.17 |
| 2 | 800 | 1.335 | 0.62 | 1.216 (0.56) | 1.160 | 3.1 | 1.46 / 1.41 / 1.39 / 1.37 |
| 3 | 400 | 1.377 | 0.64 | 1.291 (0.59) | 1.169 | 7.8 | 1.32 / 1.44 / 1.42 / 1.40 |
| 4 | 1300 | 1.310 | 0.63 | 1.181 (0.55) | 1.103 | 1.5 | 1.31 / 1.42 / 1.42 / 1.42 |

Seed 1 reproduces itself at its own weight (1.163 against E2.8b's 1.183, observer 9.6 against 9.7),
and seed 0 joins it (1.238 holding 0.74, observer 11.2), so the weight was PART of the cause: at the
first-batch weights zero of the other four seeds had sharpened, at the fixed weight one of them does.
Both passing seeds sharpen late, as the added readout asked to see (1.42 to 1.45 at step 2,000, 1.17
to 1.27 at 3,000, still descending at 4,000), while the other three sit at the input to the end. Every
seed is honest on the observer (1.5 to 11.2x against the control's 34 to 45, five of five under 14),
and seed 1's observer width of 0.949 is the one number under 1.0. Against E2.7 arm B's minima (1.20 to
1.33 holding 0.54 to 0.63, never selectable) the two passing seeds are better on both axes, so the
honest recipe is E2.7 plus a regulariser that makes its minimum selectable, on two seeds in five,
after three thousand steps: worth having, and not a star term doing the sharpening.

*Verdict.* Not killed (two seeds, not one or none, under 1.30 with an honest observer), prediction
failed (two, not three, at or under 1.25). The per-star term with its counterpart is honest by
construction and sharp when the optimisation gets there, which at this budget is two seeds in five,
late. That is the profile of a REGULARISER, and it goes into the unrolled operator as one, at this
weight; it is not a recipe to build a capacity arm on (five times the GPU on a two-in-five seed
success), which closes the fork below.

### The fork, decided 2026-09-07: the unrolled operator, with the estimator step under it

*The rule, as pre-registered.* E2.8's kill paragraph: "unrolled RL if E1b passed, capacity (NAFNet-32)
if it did not. Both carry the star term regardless." E1b's conditional pass became a pass in the
course of the day: E1c named the refusals, E1d put the estimated difference width within 0.02 of truth
at every band from 1.3x up, E1e cut the refusals from 75 to 30 of 180 with the remainder on frames
too sparse for any estimator. The estimator step E1b demanded exists and reads. The pre-registered
rule picks the unrolled operator.

*What the star-term arms add to that choice.* A pixel-domain U-Net with a star term can sharpen and
fabricate (E2.8), be honest and not sharpen (E2.8b arm N), or be honest and sharp on two seeds in
five, late, at a fixed low weight (E2.8c). The trajectories say why: the star term is what sharpens
this net, its counterpart cancels the sharpening at an equal weight, and a low weight hands the run
back to the pixel objective, which is E2.7's behaviour with a regulariser on top. Capacity is not the
axis any of this turned on, so the NAFNet-32 arm is not run: five times the GPU on the same objective
ceiling.

*The operator.* E3 is built as an unrolled Richardson-Lucy: K iterations of the multiplicative update
with the kernel from the estimator step (`PsfProfileFit` with the signal floor, the width by
composition, the shape from the fit; per tile on D1's grid, the whole-image kernel where a tile
starves), with a small learned residual network between iterations as the prior. The physics carries
the width, which is where the oracle is already at the ceiling (rec/truth 1.00 to 1.03 through 1.6x
with the estimated kernel); the network carries what the oracle loses, the stars and the ringing under
noise (0.63 of the truth's stars and 23 percent ring excess at 1.3-1.6x noisy), which is exactly the
job the pixel-domain net did well on the gate session. The objective is E2.7 arm B's pixel and band
terms plus the star term WITH its empty counterpart at E2.8c's weight (1.84e-3), as a regulariser:
inside the operator a term that prefers leaving the frame alone costs no sharpening, because the
sharpening is not the network's to do.

*The kernel at training time is the estimator's, not the draw's.* Inference has no drawn kernel, so a
training that hands the operator the exact injected kernel and an inference that hands it the
estimated one is the H0 domain skew in a new place. The operator trains on the kernel `PsfProfileFit`
reads off the degraded tile's own stars, with the drawn kernel kept as an arm (H2's question moves
inside the operator), and a tile the estimator refuses trains on the whole-frame kernel, as
inference will. E2.10a's Orion pair is the real-blur check on that kernel before a seed is spent.

*What is pre-registered before a seed is trained.* E3.0, the operator ALONE: K = 20 with the
estimated per-tile kernel, no network, run through the gate on E2.6's cache. Prediction: selects at
out/truth at or under 1.10 (the oracle at a 1.38x input reads about 1.03) with stars at the minimum at
or over 0.60 and the observer under 2x its null; a kill at out/truth over 1.20 or the observer over
2x, which would say the tile-wise kernel or the unrolling is wrong before any learning has been asked
for. E3.1, the operator with the network, five seeds: prediction, stars at or over 0.85 with width at
or under 1.15 and the observer under 2x its null, the combination neither the oracle nor the U-Net
reached alone; kill, no seed improves on E3.0's stars column at equal width, which would say the
learned prior is not earning its place and the operator ships as a physics-only deconvolver with
the estimator step. Cost: K RL steps are 2K convolutions per forward pass, so about three to five
times E2.7's step time at K = 20; five seeds a day on the 1070.

*Ground-work pre-registered before E3.0 (2026-09-07, late evening): the estimator's kernel on the
row.* The operator trains on the kernel the estimator reads, and the stored tiles are stretched, so
only the exporter can take that reading (a tone curve moves the half-maximum crossing). Opt-in
(`EstimateKernels`, `--estimate-kernels`), per channel of each draw: `PsfProfileFit` with the signal
floor on the LINEAR clean cell (once per cell) and on the linear degraded cell, the kernel width by
composition (`MoffatComposition.DifferenceFwhm` of the two fits) and the shape from the degraded
fit, written as `EstimatedKernelFwhmPx` / `EstimatedKernelBeta` beside the clean and observed fits
and the drawn kernel's own effective width (`EffectiveKernelFwhmPx`, E1f's number, the truth the
estimate is read against). Where either fit refuses, the row says which check
(`KernelEstimateRefusal`) and carries the drawn kernel's effective width with `KernelSource` "drawn",
the exporter's stand-in for the whole-frame kernel the pre-registration names (blurring the whole
master once per draw is what a whole-frame observed fit would cost). Prediction, on the fixture and
on one real session: where both fits return and the realised ratio is 1.3x or more, the estimated
width is within 5 percent of the effective width (E1d read 0.98 to 1.02 there); under 1.3x it reads
0.9 to 1.1 of it (the sampling floor); the crop refusal fraction on 512 px cells is 20 to 50 percent
(E1g-2's oracle fell back to the whole frame on 71 of 180 arm rows); the cost is under one second a
draw. Kill: the estimate is off by more than 20 percent at 1.3x and up (which would contradict E1d
on the exporter's own cells), or refusals above 70 percent (the operator would then train mostly on
the drawn kernel, and H2 could not move inside it).

*Built and read the same evening (`EstimateKernels`, `--estimate-kernels`; the Rosette 2025-12-17
session, 12 cells by 4 draws, `C:/temp/e2/degrade-kernels-rosette*-read.txt`).* Per TILE the kill
line is met outright: on the 256 px cells every one of 48 rows fell back to the drawn kernel, 32
`TooFewStars` and 16 `TooFewStacked`, because a 256 px cell of this master holds about 17 stars
(`Psf01Stars`) where the fit needs 40; the fixture's synthetic cells refuse the same way. The reading
is therefore taken over a WINDOW centred on the cell (`EstimateWindowPx`, default 1024,
`--estimate-window`), the clean side once per cell and the observed side as the window convolved
with the draw's kernel and noised at the draw's level from its own random stream, which is how
inference takes it (the whole image, never a tile; D1 found per-tile conditioning inert). With the
window, 47 of 48 rows are estimated (one `PoorFit` at 373 stars), against the predicted 20 to 50
percent refusals, and the estimated width reads the drawn kernel's effective width thus (median and
p10 / p90 of estimated over effective, by realised ratio):

| realised blur | rows | est / effective p10 | p50 | p90 |
|---|---|---|---|---|
| 1.0 to 1.1x | 3 | 0.80 | 0.93 | 1.16 |
| 1.1 to 1.3x | 11 | 0.79 | 0.89 | 0.98 |
| 1.3 to 1.6x | 16 | 0.99 | 1.03 | 1.19 |
| 1.6 to 2.0x | 17 | 0.99 | 1.08 | 1.16 |

The prediction (within 5 percent at 1.3x and up) holds at the median in the 1.3 to 1.6x band and
misses by three points in the 1.6 to 2.0x band; the p90 sits within a point of the 20 percent kill
and does not cross it; under 1.3x the estimate reads 0.89 to 0.93 of the effective width, as E1d
did. The cost prediction is missed: 2.5 s a draw (the export alone is under 0.1), two detections at
the signal floor's retry on a 1024 px window plus the window's convolution; a re-export of E2.6's
pool for E3.0 is about a day of CPU at that rate, and detecting once per cell and fitting the
observed side at the clean side's positions (the store's own trick) is the obvious saving, owed.
Two readings the rows add: the profile fit's clean width is 0.65 to 0.74 of the HFD estimator's
(the two widths are different statistics and the operator must use one consistently), and the
observed fit's exponent ranges 1.9 to 9.9 against drawn 1.5 to 8, so the "shape from the fit" is
noisy at the wing-less end, as E1g-2 said it would be. Verdict: the ground-work stands with the
window; per-tile estimation is closed as a design; E3.0 needs the re-export.

*What this does not decide.* E4 (position-varying kernels) and E7 (the export and the runtime) stand
as written; the shipped SAS graph remains the deployed deconvolver until E7, and E2.10a's baseline on
the Orion pair is the number it has to beat. The decision is the plan's; the PR is where it is
reviewed.

### The re-bake, in four steps (2026-09-07, 21:20)

Every retained master in `2026-09-full` predates the two registration fixes and R1, and E3 trains on
tiles cut from them, so the store has to be rebuilt before E3.0's re-export. The facts that size it:
79 retained masters, 52 `BayerDrizzle` and 27 `Float16Staged`; the original bake ran 13.3 h (drizzle
sessions 10.5, staged 2.8; by stage measure 2.4, calibrate 0.5, warp 1.3, integrate 2.5, halves 1.7,
tile export 4.4, psf 0.1). The sub-level store columns were re-measured with the guarded detector on
2026-09-07 (96 minutes); every master, every tile cut from them and the masters' profile fits were
not. The registration halving touches every night whose star list carried residual warm pixels
(three SV605CC nights known, the rest unknown until re-registered: the "N unmoved dropped" count is
the only tell and the old bake never logged it); the guard moves every OSC night's reference pick and
quality gate a little; the bilinear pixel touches the 27 staged masters only, and the drizzle drop
kernel's own cost is unmeasured (the one number, the Orion drizzle master at 2.74 px against the
staged 2.81, predates the registration fix); the beta semantics need no re-stack. So:

1. **`--force-psf` on the masters** (measure stage only, minutes): the store's `MoffatBeta` becomes
   the post-E1g-2 quantity and E0's per-channel beta statistics are re-read on it.
2. **Drizzle's kernel cost per star on one night, before choosing the re-bake's strategy**: R1's
   method (the near6 frames stacked `BayerDrizzle`, the master's per-star width against its warped
   frames' mean, beside the bilinear and Lanczos twins). Pre-registered: drizzle sits with bilinear
   (about a pixel in quadrature at 2 px seeing) or with Lanczos (none measurable); the answer decides
   whether the 52 drizzle masters are part of the problem.
3. **The Lanczos-3 default (the user's), then a bake option for the warp kernel**: `SessionRegistrar`
   hard-codes `WarpInterpolation.Bilinear`, so a Lanczos bake needs the option either way.
   **DONE 2026-09-12, as CLAMPED Lanczos-3.** `WarpInterpolation.Lanczos3Clamped` is the default
   everywhere a kernel is chosen (`StackingOptions`, `IntegrationJob`, the `Image` overloads that name
   none, `tianwen stack`), the registrar takes `warpInterpolation` from
   `DatasetBuildOptions.WarpInterpolation`, and `tianwen dataset build` has `--warp-interpolation`; the
   launcher's `bake-provenance.json` records the argument, so a store's kernel is readable after the
   fact. `Lanczos3` stays as the plain kernel R1 measured.

   **Why clamped, found the same day by flipping the plain kernel on the synthetic RGGB fixture.**
   Seven of eight warped subs carried a ring of 400 to 2000 ADU below a 1000 ADU sky two pixels from
   the brightest star's 15000 ADU peak (13 percent of it; bilinear: no pixel below the sky anywhere).
   Not a property of a 2 px star, which rings 0.04 percent under the plain kernel in mono
   (`WarpInterpolationTests`, the source's own noise), but of a DEBAYERED plane: it samples a 0.85
   sigma star on a 2 px pitch, so per colour the star is a spike with 6 percent at its neighbours, and
   a windowed sinc rings on a spike by construction. Two consequences were measured before the fix:
   the ring set the frame minimum, so the SAS auto-detect's median-minus-minimum statistic read 0.128
   against its 0.125 threshold on sub 0 and the tile exporter wrote that sub UNSTRETCHED (a linear
   tile beside stretched ones, H0's domain skew in a new place, caught only because a negative value
   failed the exporter test's range check); and every sub's profile within 3 px of a bright star was
   wrong-signed, which is what the deconvolver's estimator step reads. PixInsight's default is
   clamped Lanczos-3 for this reason (PCL: "strong undershoot (aka ringing) artifacts when the
   negative lobes of the interpolation function fall over bright isolated pixels or edges").

   **The clamp is PCL `LanczosInterpolation`'s rule verbatim** (weighted samples summed by sign, r =
   negative over positive; r at or above 1 keeps the positive lobes alone, r above the threshold t
   scales the negative part by 1 - ((r - t) / (1 - t))^2), **at a threshold of 0.7, measured, not
   PixInsight's 0.3.** The sweep (a 2.12 px mono Gaussian field, 12 stars, at half phase; a per-plane
   spike of 15000 over 1000 with 6 percent neighbours):

   | t | second-moment FWHM added, px in quadrature | half-max (detector) added | spike ring, percent of peak |
   |---|---|---|---|
   | 0.3 (PixInsight) | 0.73 | 0.49 | 0.7 |
   | 0.4 | 0.55 | 0.47 | 0.7 |
   | 0.5 | 0.30 | 0.45 | 0.8 |
   | 0.6 | 0.00 | 0.45 | 0.8 |
   | **0.7** | **0.00** | **0.46** | **0.8** |
   | 0.8 | 0.00 | 0.46 | 1.3 |
   | 0.9 | 0.00 | 0.46 | 3.9 |
   | 1.0 (plain) | 0.00 | 0.46 | 5.9 |

   The half-max column is flat, including at 1.0, so it is the half-phase shift's own reading and not
   the clamp's; the second-moment column is the clamp lifting the skirt of every smooth star (on a
   skirt the core's negative-lobe contribution exceeds a low threshold of the faint local wing), gone
   from 0.6 up; the ring is bounded through 0.7 and climbs after. 0.7 is the last value inert on a
   smooth profile. On the fixture it takes every sub's gate statistic to 0.013 to 0.026 (from 0.128)
   and the ring to about 2.5 percent. The peak is kept identically at every threshold (36.5 percent of
   a half-phase spike, bilinear 25). Pinned by `WarpInterpolationTests`; the constant's doc carries
   the numbers. Weighed and not added: Lanczos-4 buys nothing R1 could measure; the cubic family
   (B-spline, Catmull-Rom, Mitchell) trades width for a ringing the clamp already bounds; area
   resampling is for downsampling, which the unit-scale registration never does.

   **Owed to the re-bake's reading:** every retained master's ring and skirt are now a property of
   this kernel; the near6 / whole-night per-star probe (R1's method) should be re-read once under
   `Lanczos3Clamped` beside its `Lanczos3` figures before the overnight re-stack, so the default's
   width on REAL OSC subs is a number and not the synthetic one.
4. **The full re-stack as one detached overnight job**, about 11 h with the measure stage done, and
   E3.0's re-export (`--estimate-kernels`, the ratio draw) after it, never before, since the
   degradation cache is cut from the masters.

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
