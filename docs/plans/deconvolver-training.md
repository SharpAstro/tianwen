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
| Field-radius bins sample ONE common star set banded on green (`RadiusSampling = "common-stars"`) | `SessionPsf.BinsByChannel`, commit `069e5b14` | The radial profiles are now comparable across channels |
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
| E1 | **DONE 2026-09-06, both halves.** H5 (`PsfEncodingSpreadProbe`, all 79 masters through the deployed estimator): the shipped range is not the collapse it was recorded as, lowering the floor makes the spread SMALLER, `[0.5, 4.0]` is the pick. H1 (`DeconvolutionOracleCeilingProbe`, `RichardsonLucy` with the exact kernel, 180 rows): full recovery to 1.3x blur, inside 10 percent to 1.6x, 1.7 to 1.8x the truth width beyond 2x; nothing recovers below the truth; ringing bounds the range before recovery does. | a day, CPU | The ceiling and the contract floor |
| E2 | **SHIPPED 2026-09-03 as the shared exporter** (`tianwen dataset degrade --mode blur`, `DatasetDegradationExporter`): linear Moffat blur with a drawn (FWHM, beta, elongation, PA), noise after, both sides stretched with the TARGET's parameters, field-radius tag per cell, and the drawn kernel parameters in `degradations.jsonl`. Parity is stronger than planned: the clean tile derived from the RETAINED master is byte-identical to the P0 tile of the same cell (0.0 on every session measured), which pins the whole path rather than just the stretch. Still owed: psf01 labels from `HfdPsfEstimator` on the degraded stretched frame under both encodings (H2, H5), and the per-(train, filter, channel) draw distribution, which needs E0's re-measured store | 1 to 2 days | Whether pairs are honest |
| E3 | Smoke arms on the U-Net: kernel vs estimator label (H2), shared vs per-channel (H3), noise vs none (H4), two vs three bands (H8). Post a labelled comparison at 1:1 around bright and faint stars. **BLOCKED on E2.5 below, and the seed count is NOT three until the power check says so.** | 4 pairs x N seeds x 11 min | H2, H3, H4, H8 |
| E2.5 | **DONE 2026-09-06** (details below the table). `--prepare` reads `degradations.jsonl` into a `psf01.npy` beside the tiles; `--cond-psf01` conditions on that stored label instead of on measured noise; `n2n_deconv_gate.DeconvGate` selects on width, ringing and star count. Validated end to end on a two-session blur export, which is also what caught the gate's own first bug. Originally recorded as: **discovered 2026-09-06, and the reason the row above cannot start.** The trainer cannot train a deconvolver at all yet: `--prepare` reads `tiles-manifest.jsonl` only and never `degradations.jsonl`, so the psf01 the exporter now writes never reaches the cache; the conditioning plane comes from `with_sigma`, which MEASURES the input's noise rather than reading a stored label; and `n2n_gate.py`'s metrics are noise, faint amplitude and spurious sources, every one of them noise-oriented. **The gate is the consequential half: as it stands it would select the checkpoint that removes the most noise while doing nothing about sharpness, which is selecting a deconvolver for BLURRING.** It needs FWHM recovery and ringing, scored against E1's ceiling at 60 iterations. | 1 to 2 days | Whether E3 can run |
| E2.6 | The power check the denoiser campaign paid for: ONE arm at six seeds, seed spread measured against the effect each of H2/H3/H4/H8 expects, then the rest sized from it. On the denoiser's E2 the seed sd beat the between-regime sd 2 to 3x, so three-seed arms could not read a one-point effect and about 31 seeds would have been needed. H4 and H8 are plausibly large enough to read at three; H2 and H3 are the ones at risk. | 6 x 11 min | E3's seed count |
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
would moot a redesign if it works; and only if that fails, an architecture that carries the forward
operator rather than a loss that hopes to imply it.

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
```

**Two things the raw output does not give you.** The ceiling probe prints per-row detail and bins by
psf01, which E1 found to be the wrong axis; the tables in this plan are re-aggregated by BLUR RATIO
(`blurred / truth`) from those rows, and the per-cell fits behind E0 come from parsing
`stats/psf-sessions.jsonl` directly (`MasterProfiles[]` per channel, filter = the 4th `|`-delimited
field of `SessionId`, rows with `MoffatBeta > 20` dropped as railed). Both are small scripts, not
committed, and a re-run should redo them from the stored output rather than trusting a remembered
number.

**Captured output from the 2026-09-06 runs**, kept for comparison rather than as an input:
`C:/temp/e2/psf-encoding-spread.txt`, `oracle-ceiling.txt` (30 iterations), `oracle-ceiling-60.txt`,
`oracle-iterations.txt`. Scratch, not backed up.

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
