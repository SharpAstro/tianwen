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
  encoding the model has no lever on the very frames it will be run on.
- Backend order today: RC-Astro `bxt` when installed and licensed, else SAS
  `deep_nonstellar_sharp_conditional_psf_AI4.onnx`. Neither may appear in this training, validation
  or metric loop (EULA section 10; SAS licence unverified, excluded by default).

### Measurements (the sweep is calibrated, with three caveats)

| Fact | Where | Consequence for the sweep |
|---|---|---|
| Per-sub FWHM p05 1.96 / p50 2.10 / p95 2.55 px; intra-session p90/p10 median 1.04 | 51-session PSF store (`stats/psf-sessions.jsonl`, `SubFwhm`) | The archive has no blurry arm; truth is synthetic (2.1b) |
| Master profile is Moffat, beta PER TRAIN: Samyang 7.95, SH61 6.85, ZS61 2.05 to 2.70; Moffat beats Gaussian 48/50; wing flux 98x a Gaussian's at 2 FWHM | `SessionPsf.MasterProfiles[]` (`PsfProfileFit`, log-space fit) | Sweep beta 2 to 12 weighted to 6 to 9, or sample the measured per-train distribution; never Gaussian |
| beta correlates with FWHM (r 0.66, 0.635 within the Samyang train) | same | Sample (FWHM, beta) jointly, never independently |
| PSF is per CHANNEL and the direction is train-dependent (green/red 0.64 Samyang, blue/red 1.28 ZS61) | `MasterProfiles`, one per channel, blue first | Degrade each channel with its own kernel |
| The G/R ratio is mostly an AHD demosaic artifact (0.767 AHD to 0.947 drizzle on one session; AHD red log-rms 0.957 vs 0.130) | `stats/psf-channel-survey`, `drizzle-vs-ahd/` | Calibrate the per-channel ratios on DRIZZLE masters only; split every statistic by `MasterStrategy` |
| Red's centre-to-corner FWHM FALL is chromatic defocus from autofocus optimising at 500 to 550 nm, not a bug (red sharpest in 6 of 65 sessions; red/green 1.38 quad-band, 1.64 at 3 nm) | run log 1c, `docs/todo` task #19 closed | The P2 hold on channel 0's radial profile is LIFTED; model red as it is, per (train, filter) |
| Field-radius bins sample ONE common star set banded on green (`RadiusSampling = "common-stars"`) | `SessionPsf.BinsByChannel`, commit `069e5b14` | The radial profiles are now comparable across channels |
| Faint stars read 25 to 30 percent WIDER (half-max relative to own peak); saturation ruled out (0.1 to 0.2 percent of stars) | 2.2 | Any PSF fitted from detections must band on brightness; `PsfProfileFit` does (55th to 75th percentile) |
| The store predates the 2026-08-27/28 deblender; the current detector finds ~4 percent more matched stars and measures ~2 percent (+0.039 px) wider, non-uniformly | `PsfStoreVsCurrentDetectorProbe`, 12 sessions | **Run `dataset build --force-psf` before fitting anything to the store** |
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

Hardware-validation item to add: **run three nights with `SaveIntermediates` on**, on the two rigs
the archive is dominated by (ASI533 + Samyang, SV605CC + SH61), so H6 has data. It costs nothing but
disk; the frames are taken either way.

## 5. Experiments, in order

| Step | What | Cost | Decides |
|---|---|---|---|
| E0 | `dataset build --force-psf` on the organized bake so the store reflects the current detector; re-render the report; re-fit the per-(train, filter, channel) (FWHM, beta) distributions. | ~10 min (reads retained masters) | Calibration of everything below |
| E1 | H5 encoding spread; H1 oracle ceiling table with Richardson-Lucy and the exact kernel over the sweep. | a day, CPU | The ceiling and the contract floor |
| E2 | The degradation exporter (linear blur, noise after, target-parameter stretch, estimator labels, radius tags); parity of the stretch against `Image.MtfStretch`. | 1 to 2 days | Whether pairs are honest |
| E3 | Smoke arms, three seeds each, on the U-Net: kernel vs estimator label (H2), shared vs per-channel (H3), noise vs none (H4), two vs three bands (H8). Post a labelled comparison at 1:1 around bright and faint stars. | 4 pairs x 3 seeds x 11 min | H2, H3, H4, H8 |
| E4 | Stationary vs position-varying (H7) on the refractor trains. | 2 x 3 x 11 min | H7 |
| E5 | On-the-fly torch degradation with the MTF pin, if E3 is sample-hungry. | a day | Sample efficiency |
| E6 | Ladder capture on three nights (hardware queue); H6 scoring. | nights | The advertised range |
| E7 | Export with the own contract (`[0.5, 8]` px), parity to torch, contract JSON, `OnnxTianWenDeconvolver : INonStellarDeconvolver` through `ChunkedNafnetRunner`, an `IPsfEstimator` variant with the lower floor, backend routing. | 2 days | Ships |
| E8 | Space-truth tier (H9), only after E7 has a baseline to beat. | rented GPU | Optional |

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
| P2.0 | Store re-measured; oracle ceiling and encoding spread tabled | E0, E1 |
| P2.1 | Degradation exporter with stretch parity; first pairs | E2 |
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
