# R9 (#1056) revisited: learned parameters and a learned stage, stage by stage (rules set before any arm ran)

Written 2026-10-12 for the owner's goal ("finish the planet training leg, including re-considering machine learnt parameters for
sharpening, types of sharpening, alignment, quality estimation, dewarping, deconvolution, denoising"). Amended only before a run, every
amendment dated below with its reason.

## Stage by stage, read off what is already measured (the plan's sections; no new run)

| stage | today | what the truth or the oracle leaves | learned? |
|---|---|---|---|
| frame quality | gradient energy, keep half when sharpened | Spearman +0.87 (gradient) to +0.99 (reference gain) against the true transfer; band 1 0.649 against the oracle's 0.640 (R4) | no: 1.4 % at most |
| per-capture settings (keep, points, strength) | the measured defaults (#1159) | AUTO's largest headroom 2.4 % under the seed spread 2.1 % (#817 A2, A3) | no: inside the noise |
| alignment points, dewarp | plain correlation, 32 px patches, a mesh | band 1 0.776 (dense points) against 0.751 with no warp at all, so a perfect dewarp is worth about 3 % (R5 part 2, #1081) | no: under 2 seed spreads; the readings' shrink is #1082's |
| de-rotation | the spheroid, the relight by the fit's k | geometric; #1542 confirmed k on the night | no |
| blur kernel | the limb's edge to 0.3 cycles a pixel | past 0.3 noise on every real capture (R8 follow-ups 3, 4) | only inside the stage below |
| denoising | none (the gains are a Wiener) | the finest band 4 to 9 times its noise on twins, 6 to 30 on real masters; BayesShrink 0.654 to 0.652 (#1313) | no: little noise to take |
| colour, drizzle | OPAL's colour, the demosaic | pinned to OPAL; drizzle past the grid invents (R5a) | no |
| **sharpening / deconvolution** | derived wavelet gains (R8) | band error 0.664 against 0.586 (per-band gains fitted to the truth) and 0.446 (the exact 2-D kernel) on the calibrated twin; RL 0.861, L1-L2 1.274, the power-law Wiener 1.650 (R8 parts 1 to 3) | **the one arm: a third of the error is left** |

Types of sharpening: wavelet gains, Richardson-Lucy, the power-law Wiener, L1-L2, the presets and the dering variants are measured
(R8, its follow-ups); unsharp masking is one linear filter, which the jointly fitted per-band gains bound by construction.

## The arm: L-REFINE, a learned refiner on today's sharpening

- **What it is:** a small U-Net (about 0.8 M parameters, the N2N trainer's shape) that adds a residual to the derived-gains master D: the
  output is D plus its prediction. Inputs: D and the stack S, each over the disk's level above the sky, and the radius in disk radii. It
  can only correct what the classical sharpening left.
- **Training pairs (synthetic, never a real capture):** OPAL Jupiter maps, epochs 2022b and 2024c, all five filters, rendered at a random
  central meridian, radius 35 to 110 px and north, through the pupil (the truth T, the sharpening's own target); blurred by a stack PSF
  made of a random kept subset (5 to 50 %) of a twin's per-frame PSFs (`<twin>.psf`: r8 calibrated, r8 nostill, r8m calibrated, r8w
  warped), scaled in width by 0.7 to 1.5; a sky; white noise with the finest band's SNR log-uniform over 3 to 40; then D made by
  `PlanetarySharpening` exactly as `planetary sharpen` makes it.
- **Held out:** OPAL 2024d (every filter) and the r8m nostill twin's PSFs.
- **Loss:** L2 against T inside 1.05 radii.

## The gates (all must hold)

1. **G1, held-out pairs:** band error 1 to 4 inside 0.9 radii (R8's measure) at least 10 % below D's, and no band more than 2 % above.
2. **G2, full twins through the stacker** (r8 calibrated and nostill, r8w warped; their masters sharpened as shipped, then refined):
   at least 10 % below D on each, no band more than 2 % above.
3. **G3, nothing invented:** the power past 0.9 of the pupil's cutoff at most 1.05 times D's, on G1 and G2.
4. **G4, the limb:** the ring below the sky (`planetary ringing`'s share) and the limb profile error no worse than D's on G2.
5. **G5, real captures with a _post** (the four of the 10-11 full chain): `planetary judge` bands 2 and 3 against the post not more than
   2 % below D's; a picture for the owner's eye.
6. **G6, the deploy point:** each real master's edge-read core width and finest-band SNR inside the training range.

If G1 fails, the arm stops there: the twins' own error is what it would have to learn.

## Amendments

(none yet)

**2026-10-12, before any pair was made: the PSF's width.** Scaling a twin's PSF in width would scale its diffraction with it, so the
stack PSF is instead the kept subset's mean PSF convolved with a Gaussian of 0 to 1.5 px (only ever broader: worse seeing). The truth is
the sharp render through the twin's own diffraction PSF (`SyntheticPsfHeader.Diffraction`), the stack the same render through the
stack PSF, both on the twin's pixel scale.

**2026-10-12, after a four-pair smoke run, before any set was made: the noise range.** The finest band's signal to noise (band 1 inside
the disk over band 1 of the far sky) reads 44.6 on the calibrated twin's master, 3.2 on the 12-inch SCT Jupiter and 22.9 on the 16-inch
Meade Saturn, so the draw is widened from 3 to 40 to 3 to 60. The sets: 1,500 training pairs (maps 2022b and 2024c, the PSFs of r8
calibrated, r8 nostill, r8m calibrated and r8w warped, seed 1) and 200 held-out pairs (map 2024d, the PSFs of r8m nostill, seed 2).

**2026-10-13, after the first G2 scoring, before G1 was scored: the output outside the limb.** The net was trained inside 1.05 radii
only, so outside the disk its output is unconstrained, and the scorer reads the sky from there: the first G2 scoring read every band's
transfer about 0.87 (band 5's too) and the limb profile error 0.105 against D's 0.0065, a level error, not a sharpening one (calibrated
1.254, nostill 0.542, warped 1.606 against D's 0.623, 0.267, 0.766). The refined image is therefore D outside 1.05 radii and the net's
output inside 0.95, blended by a smoothstep between, the region the loss covered. This is the instrument's fix, not a change of the arm;
G1 and G2 are scored with it, and the unblended G2 reading stays recorded here.

**2026-10-13, the first arm read on G2, before the second ran.** L-REFINE fails G2 with the blend in place: the full twins' band error
1 to 4 refined against D: calibrated 1.074 against 0.623, nostill 0.453 against 0.267, warped 1.117 against 0.766, worse in bands 1 and 2.
Its held-out pairs' curve fell to 0.54 of D's squared error inside 0.9 radii, so the net learned the convolution pairs, and what it
learned does not reach a master made by the stacker (registration error, resampling, noise correlated between pixels, none of which a
convolved render has). G1's full reading follows below when scored.

- **L-REFINE-T, the same refiner trained on masters made by the stacker:** 48 twins of the 2022-09-03 Red capture (`planetary degrade
  --frames 600`, its statistics from `twins/red-600.real.json`), each with a map drawn from 2022b and 2024c (all five filters) and the air
  drawn per twin: r0 6 to 12 cm, wind 10 to 25 m/s, exposure 4 ms, a local layer of r0 2.2 to 3.5 cm or none (a quarter), scatter 0 to
  8 %, warp 0 to 0.8 px, seed the twin's index; each stacked by `planetary stack --truth` and sharpened by `planetary sharpen` (derived,
  newtonian, 650 nm) as the R8 twins are. The net starts from L-REFINE's weights and trains 4,000 steps on crops of these masters only.
- **Held out:** 4 twins made the same way from map 2024d, and the three R8 twins of G2. Every gate as written, G2 on both.

**2026-10-13, while the twins were being made, before any was gathered or trained on: the R8 twins' map is out of training.** Every
twin of the 2022-09-03 capture is rendered at that capture's instant (DATE-OBS 2022-09-03T12:11, north 159 deg, radius 49 px), the
R8 twins of G2 included, so a training twin drawing their map (2022b f631n, 6 of the 48) would be the very view G2 scores, under other
air. Those 6 are dropped from training (42 remain) and kept as further held-out readings. Also stated: every L-REFINE-T twin shares
that one geometry, so the arm tests the domain gap (stacker-made masters against convolved renders), not the spread of radii a real
corpus has; G6 reads the deploy point as written.

**2026-10-13, before any G3 was read: G3's band.** The twins are sampled coarser than their optics resolve: the pupil's cutoff is 0.942
cycles a pixel on the R8 twins (the r8m nostill PSF's scale, 650 nm, 254 mm), past Nyquist, so "past 0.9 of the cutoff" is empty and
read zero for every image (L-REFINE's G2 printed 0 against 0). G3 is read past 0.9 of the lesser of the cutoff and Nyquist, 0.45 cycles
a pixel, with the same 1.05 bound against D; G1's scoring, run before this, has its G3 read again over its rows (`r9_score.py
--g3-from`). G4 is read off `planetary fidelity`'s limb profile error and the limb's trough below the truth (`planetary ringing`
restores captures and cannot read a given image). L-REFINE's G2 under both: past 0.45, refined 5.62e-6 against D's 5.78e-6 (0.972,
truth 6.46e-6), so it holds G3; the limb profile error 0.0093 against D's 0.0063 and the trough 0.0005 against 0, so it fails G4 too.

## The second arm: L-GAINS, the sharpening's own gains predicted by a model (set 2026-10-13, before any reading of it)

The owner's goal names learnt PARAMETERS for sharpening, not only a learned image stage. `planetary sharpen --truth` already prints
the truth's own gains on a master (`PlanetaryMasterScore.TruthGains`: one gain per scored a trous band, fitted jointly to the truth
inside 0.9 radii), and on the twins they sit well below the derived gains' band error 1 to 4 (read off the existing sharpen logs, the
reading that prompted this arm): r8 calibrated 0.477 against 0.623, nostill 0.200 against 0.267, r8w warped 0.636 against 0.766,
t001 to t005 3 to 32 % below. Those are fitted in-sample, without the shipped limb window, so first:

- **Step 0, the ceiling (kills the arm if it fails):** the truth's own gains applied THROUGH the shipped sharpening (`planetary sharpen
  --gains g1,g2,g3,g4`, the same limb window and limb fix) on the three R8 twins and every finished L-REFINE-T twin. If their band
  error 1 to 4 is not at least 10 % below the derived gains' on the mean over the twins AND on at least two of the three R8 twins, no
  predictor can pass G2 and the arm stops.
- **Step 1, the predictor:** a model from what a master gives at deploy time (the derived gains, the edge-read kernel's transfer at
  0.1, 0.2 and 0.3 cycles a pixel as the derivation prints it, the finest band's signal to noise, the disk's radius) to the truth's own
  gains, log scale, trained on the L-REFINE-T training twins (the same 42 plus t000), the R8 twins' map excluded. Ridge regression
  first (the fewest parameters); a small MLP only if ridge leaves more than half the ceiling's gain on the training twins'
  leave-one-out. Applied through `--gains`.
- **Held out:** the 4 twins of 2024d, the 6 twins of 2022b f631n, the 3 R8 twins. Gates G2 (on the R8 twins and on the 10 held twins),
  G3, G4 as amended; G5 and G6 on the real captures only if G2 holds.

**L-GAINS step 0, read 2026-10-13 (30 twins finished by then: the three R8 twins, t000 and t001 to t026): PASSES.** The truth's own
gains through the shipped sharpening (`step0.py`, `step0.csv`) against the derived gains, band error 1 to 4: r8 calibrated 0.457
against 0.623 (-26.6 %), nostill 0.207 against 0.267 (-22.5 %), r8w warped 0.625 against 0.766 (-18.4 %); over all 30 a mean of
-17.0 % (median -18.5 %, from -1.7 % to -32.4 %). The limb profile error 0.0039 against the derived gains' 0.0045, and the gains
through the limb window score as they do in-sample (0.544 against 0.549 on the mean), so the window costs the truth's gains nothing.
Step 1 runs once every twin is made; step 0 is completed over the rest before it (the same script, resuming).

**2026-10-13, before step 1's reading (only a two-twin smoke run of its code, both training twins left out: t000 0.634 against the
derived 0.762, t001 0.307 against 0.301; no held twin read): step 1 is LINEAR, not on a log scale.** The derived gains go to zero or
below on the most blurred twins (band 2: r8w warped -0.42, t024 -0.44, t003 0.17, t004 0.22, the oscillating gains R8 noted), where
a log is undefined and the clamp made the uncorrected error 87 against 2 for the fit. The model predicts the truth's own gains
themselves from the derived gains and the edge's two readings, unlogged, ridge as before.

**G6, the deploy point, read 2026-10-13 off the four real masters with a _post (`planetary sharpen --auto --no-write` on final-1011's
masters: features only, nothing scored): every one lies outside the twins' range, so G6 FAILS for any model trained on the twins of
2022-09-03, L-REFINE-T and L-GAINS alike, whatever G2 reads.**

| master | planet, pupil | pupil cutoff, cycles a pixel | edge at 0.1 / 0.3 | derived gains, bands 1 to 4 (r) |
|---|---|---|---|---|
| 12-inch SCT, ASI224MC | Jupiter, 300 mm | 0.31 to 0.41 | 0.399 / 0.782 | 1.00, 13.76, -1.90, 1.46 |
| EdgeHD 11, ASI678MC | Jupiter, 279 mm | 0.23 to 0.30 | 0.277 / 0.816 | 1.00, 6.78, -0.86, 1.56 |
| EdgeHD 11, ASI183MC | Saturn, 279 mm | 0.39 to 0.52 | 0.407 / 0.439 | 1.00, 5.21, 0.77, 0.98 |
| Meade 16-inch | Saturn, 406 mm | 0.31 to 0.41 | 0.329 / 1.098 | 1.00, 5.93, 1.76, 0.70 |
| the twins (30 read) | Jupiter, 254 mm, mono | 0.94 | 0.402 to 0.909 / 0.044 to 0.722 | band 1 carries the gain (to 13) |

The twins are sampled coarser than their optics resolve and every real capture finer (its cutoff 0.23 to 0.52), so the a trous bands
fall on other frequencies against the cutoff; a colour master's finest band is held as stacked (#1187); two are Saturn. A learned
gain or refiner trained on these twins can only be read on twins (G2); deploying one needs twins made at each deploy capture's own
sampling, from its own statistics.

## Fresh readings on the other stages, off the twins already made (2026-10-13, no new run; glue over step0.csv, plan.json, the logs)

- **Denoising:** on all 30 twins of step 0 the truth's own gain on the finest band is above 1 (1.37 to 6.40, median 3.55): the truth
  asks for the finest band to be lifted, never shrunk, so a denoiser (a shrink there) moves away from it. With #1313's BayesShrink
  (0.654 to 0.652), no learned denoiser.
- **Alignment and dewarp:** over the 26 training twins with their drawn air, a linear fit of the error on the knobs (each knob's
  unique share of R2): at the STACK the warp is small (+0.085 per standard deviation on a mean of 1.283, unique R2 0.03; the still
  layer dominates, 0.21 and 0.07), and after the SHIPPED SHARPENING it is the largest single knob (+0.110 on 0.666, unique R2 0.19).
  The R8 pair agrees: the warp added to the calibrated twin costs 11 % at the stack (1.500 to 1.667) and 23 % sharpened (0.623 to
  0.766). So R5's ceiling of about 3 %, read at the stack, understates what the warp costs a sharpened master. The point estimator is
  4.6 to 11 times its Cramer-Rao bound on the twins (#1082), the gap from a frame's blur unlike its reference's and a warp varying
  inside a patch; so a learned point estimator is the second place a model could pay. It is a build of its own (an estimator on every
  point of every frame, trained on twins' true warps), filed as a follow-up rather than run here.
- **Frame quality:** nothing new to read off these twins (their SERs are deleted after stacking); R4's oracle bound stands, 1.4 %.

## L-GAINS-D: the learned gains at a deploy point (set 2026-10-13, before any of its twins was made; a pilot twin only shows the logs)

G6 put every real master outside the 2022-09-03 twins. The one capture with a _post AND a twin fitted to its air is the EdgeHD 11
Jupiter of 2023-11-11 (ASI678MC; `planetary/twin/edgehd-trial11`: r0 45 cm, wind 22 m/s, still layer 3.6 cm, scatter 3.8 %, core 5",
defocus 99, 78, 83 nm, aperture 280 mm, obstruction 0.375, exposure 0, still layer's outer scale 0.12 m and drift 1 m/s). So:

- **Twins:** 30 colour twins of that capture (`planetary degrade` on its SER, 600 frames, its own statistics measured once and kept),
  the three colours' maps of one OPAL epoch (red F631N, green F502N, blue F467M, OPAL's Minnaert k 0.999, 0.950, 0.950): 24 for
  training from 2022b and 2024c, 6 held out from 2024d. The air drawn about the fit, uniformly: r0 30 to 60 cm, wind 15 to 30 m/s,
  still layer 2.5 to 5.0 cm, scatter 0.02 to 0.06, each colour's defocus 0.7 to 1.3 times the fit's; seed the twin's index. Each
  stacked by `planetary stack --truth` and sharpened by `planetary sharpen` (derived; 610, 535, 460 nm; the capture's pupil).
- **Step 0 at the deploy point:** the truth's own gains (each channel's) through the shipped sharpening at least 10 % below the
  derived gains on the training twins' mean, or the arm stops.
- **Step 1:** step 1's ridge, one per channel, from that channel's derived gains and edge readings to its truth's own gains, its
  strength by leave-one-out over the 24.
- **Gates:** G2 on the 6 held twins (every channel's band error 1 to 4 at least 10 % below the derived gains', no band more than 2 %
  above); G3 and G4 as amended; G6, the real EdgeHD master's features inside the 24 twins' range in every coordinate; then G5 on the
  real master: its predicted gains from its own features, `planetary judge` against its _post, bands 2 and 3 no more than 2 % below
  the derived sharpening's, and a picture for the owner's eye.
- Made after the 2022-09-03 twins, one or two at a time as memory allows (the box is shared).

**2026-10-13, L-GAINS-D amended before any deploy twin was scored (the pilot's log only, disclosed here): a classical comparator.**
The pilot twin (2024c maps, the fitted air) reads band error 1 to 4 per channel, stack / derived / the truth's own gains in sample:
r 2.634 / 2.306 / 1.159, g 2.244 / **2.545** / 1.010, b 2.664 / 2.457 / 1.508. The edge read at 0.3 cycles a pixel is 1.67 to 3.32,
past the pupil's cutoff (0.23 to 0.30): noise, through which the derived gains are fitted; the truth's own gain on the finest band is
-0.12 to -0.27 (it takes that band out). The 2022-09-03 twins never showed this: their cutoff lies past Nyquist. So the learned gains
must beat not only the derived gains but the classical options that act at the cutoff, scored on the SAME held twins: `--finish cutoff`
(a low-pass at the pupil's cutoff), `--edge-reach 0.2` (the physical kernel fitted to the edge up to 0.2 cycles a pixel and carried by
its physics to the cutoff), `--colour-finest derived` (the finest band's gain derived, not held), each alone and the three together.
Adopted only if the learned gains are at least 10 % below the BEST of those on every held twin's mean; if a classical option alone
passes G2 against the derived gains, it is the finding, and it is a rule anyone can read rather than a model.

**2026-10-13, L-REFINE-T's instrument fixed before any of its gates was read: the truth's scale.** A degrade twin's truth is in the
photons it was rendered in (a disk level of 55 against its master's 0.28), where a synthetic pair's truth is in its stack's units, and
the trainer divided the truth by the STACK's level: the fine-tune's loss was the 200-fold level, its held curve read 0.008 of D's at
step 500 and stayed there, so it learned a rescaling and nothing else. The chain was stopped by PID during its first held scoring
(nothing read from it). The trainer now normalises the truth by its own sky and level, as the scorer does (`PlanetaryMetrics.Normalise`);
the fine-tune and every L-REFINE-T reading are made again with that, the rules otherwise unchanged. L-REFINE's own readings stand:
its synthetic truths are in their stacks' units (90.8 against 90.6 inside 0.5 radii), and G2 reads no truth through the net.

**2026-10-13, L-REFINE-T's second instrument fix, before any gate: registration.** A twin's master sits 0.1 to 5.7 px off its truth
(the stack's reference frame; r8 warped 5.7, t030 3.1, a 2024d held twin 3.5), which the CLI's scorer undoes by a limb fit and a pixel
loss does not: a net cannot learn a twin's random shift, only blur. `r9_twin_pairs.py` now moves each truth onto its master (the
cross-correlation's sub-pixel peak against the derived sharpening, a Fourier shift; left after it: at most 0.04 px; t013's squared
error inside 0.9 radii 0.00103 to 0.00002). L-REFINE's synthetic pairs are rendered on one grid, so its readings stand.

**L-GAINS step 1 (the 2022-09-03 twins), the 13 held twins read: FAILS G2 as written (10 % below D on EACH).** Band error 1 to 4,
derived / predicted / the truth's own: r8 calibrated 0.623 / 0.483 / 0.457 (-22.5 %), nostill 0.267 / **0.276** / 0.207 (+3.4 %),
r8w warped 0.766 / 0.652 / 0.625 (-14.9 %); the six f631n twins t006 -15.9 %, t013 **+9.7 %**, t019 -20.9 %, t035 -4.9 %, t041
-17.9 %, t047 -7.3 %; the four 2024d twins t049 -2.2 %, t050 -10.2 %, t051 -24.9 %, t052 -0.9 %. On the mean 0.673 / 0.592 / 0.556:
-12.0 %, about 70 % of the ceiling's gain, worse than D on the two mildest twins (where D is already near the truth's gains). And G6
fails (the deploy point). So: a learned gain correction is worth about 12 % on the mean at the sampling it was trained at, not safe twin
by twin; the deploy arm (L-GAINS-D) decides whether it pays where it would be used.

**L-REFINE-T, read 2026-10-13 with both instrument fixes (the truth's scale, its registration): FAILS G2 and G4.** The fine-tune
(4,000 steps from L-REFINE's weights, 43 training twins) took the four held twins' squared error inside 0.9 radii to 0.52 of D's.
Band error 1 to 4, shipped D against refined:

| set | D | refined | twin by twin |
|---|---|---|---|
| 2024d, 4 held | 0.662 | 0.617 (-6.8 %) | -2.4, -5.3, -10.6, -8.2 % |
| 2022b f631n, 6 | 0.741 | 0.698 (-5.8 %) | -6.1, **+17.2**, -13.9, -8.1, -5.9, -5.5 % |
| R8 (G2's twins) | 0.552 | 0.540 (-2.2 %) | calibrated -19.3, **nostill +50.2**, warped -6.5 % |

Bands 3 and 4 fall by a quarter to a half on every set; bands 1 and 2 hold or rise (R8: +1.9 and +2.4 %). G3 holds (0.96 of D's
power past 0.45 cycles a pixel on all three sets). G4: the limb profile 0.0027 against 0.0054 on 2024d but 0.0088 against 0.0063 on
R8, and the trough doubles on f631n (0.0027 against 0.0015). Like L-GAINS it helps the most blurred twins and hurts the mildest (nostill,
t013), where D is already near the truth: it does not know when to stop.

**2026-10-13, a third instrument fault, found before the corrected R8 readings were made: the R8 twins' disk.** G2 on the R8 twins
gave the refiner the disk from the truth's header, but each R8 master holds its planet 1.5 to 5.7 px from there (calibrated -1.5, +2.8;
nostill -1.4, +4.0; warped -5.7, +0.6), so the net's radius plane and the blend outside the limb were displaced by up to 0.12 radii. It
showed when L-REFINE, unchanged, read the 2024d twins (whose truths are now moved onto their masters) at 0.628 against D's 0.662
(-5.1 %, 4 of 4), where its R8 reading had been 1.074 against 0.623. Both nets' R8 readings (L-REFINE's G2 above, L-REFINE-T's R8
row) are made again with each R8 truth moved onto its master, as the twin sets are; the earlier ones stay recorded as faulted.

**The R8 twins read again with the disk placed right (each truth moved onto its master; D's scores shift by at most 0.002 with it):**
L-REFINE 0.613 against D's 0.551 (+11.3 %; calibrated 0.648 / 0.621, nostill 0.422 / 0.265, warped 0.769 / 0.766; band 2 +43 %):
FAILS G2 still. L-REFINE-T 0.490 against 0.551 (-11.1 %; calibrated 0.507 / 0.621 -18.4 %, nostill 0.289 / 0.265 **+9.1 %**, warped
0.673 / 0.766 -12.1 %; every band lower, bands 3 and 4 by 36 and 44 %): FAILS G2 (10 % on EACH twin) and G4 (limb profile 0.0068
against 0.0062), though its mean clears 10 %. These replace both nets' faulted R8 readings above.

**L-GAINS step 1, complete:** held 13 twins 0.673 / 0.592 / 0.556 (D / predicted / the truth's own; -12.0 %), better than D on 11,
at least 10 % better on 7, worse on 2; the 43 training twins left out one at a time 0.674 / 0.585 / 0.542 (-13.2 %), better on 33, at
least 10 % on 24, worse on 10. G3 holds (power past 0.45 cycles a pixel 0.46 of D's on the held twins), the limb profile 0.0044
against D's 0.0050. Verdict as recorded: FAILS G2 twin by twin; worth about 12 % on the mean where it was trained.

**L-GAINS-D, G6 and G5 on the real EdgeHD master (read 2026-10-13, 17:20 to 17:40):**
- **G6 FAILS: the real master lies outside the 24 deploy twins on every channel.** Its edge at 0.1 cycles a pixel is 0.209 to 0.277
  against the twins' 0.29 to 0.60 (the real master is blurrier than any twin), and its derived band 3 gain is -0.86 to -1.14 against
  the twins' +0.94 to +2.36 (the derived gains oscillate there; on no twin). #1281 found the same of this capture's twin: sharper than
  the real (its single frames' edge 0.87 to 0.93 of the real's), and a twin of 600 frames stands in for a master of 2,621.
- **G5, read for the record although G6 fails (an extrapolation):** `planetary judge` against the capture's _post, derived / learned:
  band 2 correlation 0.868 / 0.760, gain on what both hold 0.377 / 0.574; band 3 0.964 / 0.973, 0.557 / 1.120; band 4 0.975 / 0.987,
  0.739 / 1.029; band 5 0.989 / 0.994. By G5's own rule (bands 2 and 3 not more than 2 % below) it passes, taking bands 3 and 4 to
  the post's own detail with better correlation; band 2's correlation falls. Its gains were extrapolated far (band 3 7.9 to 9.7,
  band 4 down to -2.08). By eye (`deploy/real/edgehd-compare-ccw.png`): the post's belt and festoon detail, a greenish cast (each
  channel's gains its own), some mottling at the limb.
- So the learned gains cannot be adopted for this capture by the rules; what would make them deployable is a twin family whose air
  brackets the real master's measured blur (its edge, its oscillating band 3), at the real capture's length.

**L-GAINS-D, every gate read (2026-10-13, 19:00):** held twins 2.272 / 1.160 (derived / learned, -48.9 %; the truth's own 1.122,
-50.6 %); the best classical option, all three together, 1.801 (-20.7 %), the cutoff alone -13.2 %, the edge reach -8.2 %, the finest
band derived -9.3 %. **G2 HOLDS** (every channel of every held twin 36 to 62 % below D; the worst band of 72, d029 green band 3,
0.153 to 0.156, +1.96 %, inside the 2 % and the only one above D). **G3 HOLDS** (power past 0.9 of each channel's cutoff 0.013 to 0.063
of D's: the derived gains lift noise past the cutoff, the learned take it out). **G4 FAILS on its trough clause:** the limb profile
error halves (0.0021 against 0.0049), but the trough below the truth deepens on 11 of 18 channels (mean 0.0012 against 0.0009; d027
blue 0.0022 against 0.0012). **G6 FAILS** (above). G5 passes as an extrapolation (above). Verdict: NOT adopted. What it leaves is the
measurement that the shipped gains are twice the truth's error at the real captures' sampling, and two follow-ups: twins bracketing a
real master's own blur, and the limb's trough under a stronger band 3.

## L-GAINS-S: the learned gains made shippable (owner's goal, 2026-10-13: "get the trained model good enough that we are confident to ship it")

Set before any of its readings. What blocks shipping L-GAINS-D: G6 (the real EdgeHD master lies outside the 24 twins: blurrier, edge
at 0.1 cycles a pixel 0.21 to 0.28 against 0.29 to 0.60; its derived band 3 gain negative), G4 (the limb's trough slightly deeper),
and inputs that oscillate (the derived gains themselves).

- **Step A, the air that brackets the real master (a design step, not an arm):** probe twins of the same capture at worse air and at
  the capture's full length; read each master's edge and the limb fit's core and halo (`planetary sharpen` prints them). The family
  is extended until the real master's features lie inside the training range on every channel.
- **Step B, the inputs:** two feature sets chosen by leave-one-out over the TRAINING twins only: (1) as L-GAINS-D, the derived gains
  and the edge's two readings; (2) physical inputs only, the edge at 0.1 cycles a pixel and the limb fit's core, halo share and halo
  width (no derived gain, no edge read past the cutoff). The one with the lower leave-one-out error is taken; a tie goes to (2).
- **Step C, the trough (G4):** the held twins' trough read with the learned gains through the shipped limb fix and through
  `--limb-fix bounded`; the arm passes G4 only through a fix that keeps every held twin's trough no deeper than D's on the mean.
- **The gates, all to hold:** G2 and G3 on held twins drawn from the WHOLE extended range (a third of the new twins held out); G4 per
  step C; G6 on the real EdgeHD master, every feature inside the training range on every channel; G5 on the real master against its
  _post: bands 2 and 3's gain on what both hold at least the derived's, band 2's correlation not more than 0.05 below the derived's,
  and a picture for the owner.
- **Shipping:** an opt-in derivation mode whose coefficients ship as a reviewed constant table, applied only where the master's
  features lie inside the training range (else the derived gains, said in words). Unit-tested on the range guard and on a fixture.

**L-GAINS-S step B, read on the 24 training twins only (leave-one-out squared error over the four gains, r / g / b / total):** (1)
the derived gains and both edges 95.5 / 15.4 / 19.9 / 130.8; (2) the edge at 0.1, the limb fit's core, halo share and halo width
40.3 / 10.3 / 28.7 / 79.3. **Amended before any arm reading: the halo width is dropped**, because the real master's reads 370 px
against the twins' 12 to 25 px. That parameter reads the frame's broad glow, not the stack's kernel, so no air can bracket it and G6
could never hold with it. The set taken is (2'), the edge at 0.1, the core and the halo share: 53.6 / 12.7 / 27.9 / 94.2, still
below (1). The real master's (2') features: core 5.79 px (twins 2.61 to 6.00, inside), halo share 0.50 (twins 0.23 to 0.50, at the
fit's bound), edge at 0.1 r 0.277, g 0.263, b 0.209 (twins 0.40 to 0.54, 0.41 to 0.60, 0.29 to 0.49: outside, step A's target).

**The kernel check (#1552 step 1), all six held EdgeHD twins:** the gains derived through the TRUE kernel (`--kernel truth`) against
the shipped edge reading and the truth's own gains; see the next lines for the means. Red and green: through the true kernel the
derivation lands near the truth's own gains in bands 2 to 4 on every twin, so the shipped kernel read off the limb's edge is the main
fault at this sampling. Blue: closer but still lifts band 2 too hard on most twins, a second fault (the noise level the Wiener reads,
suspected). The finest band, held at 1 on a colour master where the truth takes it out, is part of what remains.

**G1, read in full after the reaper (restarted detached on the owner's word, 2026-10-13): L-REFINE on the 200 synthetic held pairs,
band error 1 to 4, stack 1.133 / shipped 0.940 / refined 0.832 (-11.4 %), every band lower (-5.7, -19.2, -20.0, -17.6 %), better on 165
of 200; G3 holds (0.983); the trough below the truth 0.0027 against 0.0009. G1 passes; L-REFINE's verdict (G2, G4) stands.**

**L-GAINS-S step A, first readings (2026-10-13, design probes, not arms).** The real master's edge at 0.1 read through the twins'
own pupil (280 mm, 0.375): r 0.299, g 0.286, b 0.228 (0.277 / 0.263 / 0.209 through the 279 mm, 0.33 pupil `--auto` named).
- *Worse air* (p1, p2: still layer 2.5 and 2.0 cm; p3: r0 25 cm, still layer 3.0 cm, scatter 0.05): the core grows to 7.9, 9.3, 7.8 px,
  the edge stays at 0.34 to 0.38. Worse seeing does not reach the real master.
- *Scattered light* (d016's master convolved with the twins' scatter kernel, 10 % at a 5" core, which commutes with the stack): the edge
  falls only 0.455 to 0.431 (r), 0.410 to 0.391 (g). The degrade probes p5 and p6 (scatter 0.10, 0.15) were stopped on this.
- *The light outside the limb, from the frame's corners* (fraction of the disk at 1.02, 1.1, 1.3 radii, green): real 0.0181, 0.0059,
  0.0015; d007 (the sharpest twin) 0.0170, 0.0054, 0.0012; d016 0.0247, 0.0090, 0.0021. **The real master has no more glow than the
  sharpest twin**, so neither seeing's wings nor scatter separates it. (A first reading said otherwise because it took the sky at 1.6
  to 1.9 radii, which on the real master is still glow; withdrawn.)
- *The limb fit's core and halo are bimodal:* with the radius held at the ephemeris's (`--plate-scale 0.1032`, 239.05 px) the real
  master reads core 4.77 px, halo 0.24 of 19.9 px (free: 5.79 px, 0.50 of 370 px, radius 240.12), and d007 jumps the other way, core
  5.58 px, halo 0.50 of 269 px (free: 2.61 px, 0.50 of 12 px). The edge at 0.1 barely moves (real 0.292 to 0.312, d007 0.524 to 0.559,
  red). So core and halo are not readings a model can lean on; the edge is.
- What is left: the real master loses the mid frequencies (edge at 0.2: real 0.022, d007 0.317, d016 0.190, green, `planetary compare`)
  with no wider wings, which is what a residual misregistration does, and **every deploy twin was made with no local warp** (90 of 90
  logs: "warp 0.00 px"). Second round: p7 and p8 add a warp of 1 and 2 px RMS per axis, p9 doubles the defocus; same air as p4.

**Amendment, before any arm reading: step B's (2') is replaced by (3), the edge at 0.1 cycles a pixel alone**, for the bimodality
above (a feature that flips between two fits of one master cannot bracket anything). Step B's leave-one-out on the training twins is
read for (3) beside (2') and reported; (3) is taken unless its error is more than 1.5 times (2')'s, in which case neither is a model to
ship and the arm ends there.

**Step B under the amendment, the 24 training twins only:** (3), the edge at 0.1 alone, leave-one-out 46.3 / 13.8 / 35.1 / 95.3
against (2')'s 53.6 / 12.7 / 27.9 / 94.2: within the 1.5 times, so **(3) is taken**. (Read beside it: the edges at 0.1 and 0.3
together, 149.6; the reading past the cutoff hurts here too.)

**Step A, a Gaussian on a twin's master (a misregistration's stand-in, no wings):** d007 + 1.0 px: edge 0.477 / 0.487 / 0.336; d007 +
2.0 px: edge 0.375 / 0.376 / 0.293, core 4.80 px, halo 0.24 of 20 px; d016 + 0.7 px: 0.436 / 0.396 / 0.300. The edge falls as about
exp(-0.09 sigma^2) on both twins, and d007 + 2 px reads what the real master reads with its radius held (core 4.77, halo 0.24 of 19.9).
**The real master is a sharp twin plus about 2 to 2.5 px of Gaussian-like blur**, the stack's misregistration, which no deploy twin
carried (no warp). Whether `degrade --warp-rms` makes it: p7 and p8.

**Step A, the second round's readings.** Edge at 0.1, r / g / b: p7 (warp 1 px) 0.423 / 0.473 / 0.361; p8 (warp 2 px) 0.359 / 0.359 /
0.282; p9 (defocus doubled) 0.364 / 0.352 / 0.315 with a core of 9.89 px. The light outside the limb from the frame's corners (green,
1.02 / 1.05 / 1.1 radii): real 0.0181 / 0.0109 / 0.0059, d007 0.0170 / 0.0104 / 0.0054, p7 0.0243 / 0.0145 / 0.0075, p8 0.0252 /
0.0151 / 0.0076, p9 0.0328 / 0.0183 / 0.0089. **Warp lowers the edge and adds no light outside the limb (p7 and p8 alike); a larger
defocus adds it, which the real master has not.** So the real master is mild air, about as d007's, with a misregistration near 3 px,
the p7 to p8 trend carried on. p4 (the full length) was stopped: it cannot tell the two apart.

**The extension, set before any of its twins is made:** 18 twins (e000 to e017) of the same capture, `planetary degrade` as the deploy
family (600 frames, its statistics, OPAL's k), each colour's defocus and the air drawn from the deploy family's own ranges (r0 31 to 60
cm, wind 16.5 to 30 m/s, still layer 2.9 to 5.0 cm, scatter 0.022 to 0.058, defocus 57 to 129 nm), plus a local warp drawn uniformly
from 1 to 4 px RMS per axis (correlation length 20 px, lag-1 0.9, the option's defaults). Every third held out (6), from the 2024d maps
as the deploy family's held twins are; the training twins from 2022b and 2024c. The training set is then the 24 deploy training twins
and the 12 warped ones; the held set, the 6 and the 6.
- **G6 first:** if the real master's edge (through the twins' pupil) lies outside the training twins' range on any channel, 6 more
  twins are made once at a warp of 3.5 to 5 px (4 training, 2 held); still outside, G6 fails and the arm ends there.
- **Step B again, on the extended training set:** (3) the edge alone against (4) the edge and the light just outside the limb (1 to
  1.1 radii, found over what the planet's model through the pupil expects, as `planetary compare` reads it: seeing's wings raise it, a
  misregistration does not). (4) is taken only if its leave-one-out error is at least 10 % below (3)'s.
- **Then the gates as set:** G2 on all 12 held twins (each channel's band error 1 to 4 at least 10 % below the derived gains', no
  band more than 2 % above, and at least 10 % below the best classical comparator on each held twin's mean), G3, G4 (step C), G6, G5.

**2026-10-14, the extension paused after its first round (disclosed: e000 to e002's edges were read, nothing else).** Edge at 0.1,
r / g / b: e000 (warp 2.11 px, still layer 2.94 cm) 0.340 / 0.325 / 0.297; e001 (3.47 px, 2.91 cm) 0.364 / 0.368 / 0.324; e002 (held,
3.63 px, 3.06 cm) 0.337 / 0.349 / 0.316. Past about 2 px the degrade's warp stops lowering the edge: its default lag-1 of 0.9 is a
warp the stack's mesh follows, while the real capture's own warp statistic reads a lag-1 of 0.016 (frame to frame, a warp the mesh
cannot follow). No new twin is started (`extend.stop`; the round in flight finishes, and every twin made stays in the family). Probes
p10 and p11 take a fast warp (2 and 3 px, lag-1 0.1), p12 a layer at altitude instead (`--high-r0 15`, 10 km, 20 m/s), the warp that
varies over the disk and from frame to frame as a real atmosphere's does. The extension's warp is set again from them, before its
twins resume, and that setting is recorded here before any gate is read.

**Step A, the fast warp and the frame's exposure (2026-10-14).** p10 (warp 2 px, lag-1 0.1) reads edge 0.374 / 0.394 / 0.316 and
its stack's transfer is p8's to the third decimal: a warp's lag-1 changes nothing. The warped twins finished so far read 0.34 to
0.36 (r, g) and 0.30 to 0.32 (b) whatever their warp from 2.1 to 3.6 px (e003: 2.22 px, 0.347 / 0.347 / 0.311; e005 held: 2.84 px,
0.347 / 0.342 / 0.304), while the stack's transfer keeps falling with the warp (green, bands 1 to 3: p8 0.167, 0.280, 0.455; p11 at
3 px 0.106, 0.205, 0.383): the mesh holds the limb, the strongest edge in the frame, so the limb's edge stops reading the warp. So a
warp cannot carry the twins to the real master's edge, and the real master's loss is at the limb itself. **Every twin was rendered
with an instant exposure (`--exposure-ms 0`), while the real capture ran at 29.95 frames a second**, so each real frame is exposed
for up to about 33 ms, five times the air's coherence time at 22 m/s (about 6 ms): each frame is a partly long exposure, its speckle
averaged, the mid frequencies lost and no wider wing. Probes p13 and p14 take an exposure of 30 and 15 ms, the fitted air otherwise.

**Step A, the exposure (2026-10-14): ruled out for the master.** p13 (30 ms) reads edge 0.449 / 0.515 / 0.373, p14 (15 ms) 0.447 /
0.511 / 0.361: no lower than the instant twins. The exposure does bring the single frames to the real capture's (each frame's limb
blur sigma p10 / p50 / p90, real 2.25 / 2.56 / 2.85 px; 30 ms 2.35 / 2.81 / 3.29; instant 3.20 / 3.66 / 4.13; the wing's share real
0.25, 30 ms 0.32, instant 0.50), so the real frames look like exposed ones, but the stack of their best half reads the same edge
either way. p11 (fast warp 3 px) 0.325 / 0.334 / 0.285, e004 (2.79 px) 0.310 / 0.338 / 0.311: the warp's floor is about 0.31 / 0.33 /
0.28 against the real master's 0.299 / 0.286 / 0.228. **No degrade physics tried reaches the real master's edge** (seeing, scatter,
defocus, warp slow or fast, exposure); only a static Gaussian of 2 to 2.5 px on a twin's master does, in every reading at once. Next,
on the real capture itself: its first 600 frames (the span the twins were calibrated on) stacked as the twins are, to tell whether
the gap is the full capture's length (180 s, 5,392 frames, against 20 s) or in the real data whatever its length.

**Step A, the real capture's first 600 frames (2026-10-14; `diag/ser_head.py` copies them to scratch, the source only read),
stacked and read as every twin is:** core 4.69 px, halo 0.26 of 19.3 px (the twins' own fit basin), edge 0.325 / 0.335 / 0.265,
where the full 5,392-frame master reads 0.299 / 0.286 / 0.228. So the gap is in two parts: the real frames already carry a loss no
unwarped twin has (the 600-frame real master sits where the 3 px warped twins do: p11 0.325 / 0.334 / 0.285), and the full run's
three minutes add more. One reading points at the first part: **each real frame's fitted radius jitters 0.286 px RMS from frame to
frame, every twin's 0.045 to 0.182** (`planetary degrade`'s own table, half-resolution planes), a warp on the scale of the disk, which
moves the limb itself, where the degrade's 20 px warp averages out along the limb. Probes p15 and p16 take a warp of 150 px
correlation length (1 and 2 px, lag-1 0.1); their radius jitter against the real's 0.286 is the calibration.

**Step A, a disk-wide warp (2026-10-14).** p15 and p16 (warp 1 and 2 px at a 150 px correlation length, lag-1 0.1) bring each
frame's radius jitter to the real capture's (0.237 and 0.483 px against 0.286; every earlier twin 0.045 to 0.182) but their stacks'
edges stay high: 0.428 / 0.480 / 0.357 and 0.389 / 0.434 / 0.339. The mesh takes a disk-wide warp out. **So no modelled air or warp
makes the real stack's loss at its limb**; a static Gaussian does, in every reading at once (d007 + 2 px: edge 0.375 / 0.376 / 0.293,
core 4.80 px, halo 0.24 of 20 px; the real master with its radius held: core 4.77, halo 0.24 of 19.9).

**Amended, before any of its twins is made: the extension takes a static blur instead of a warp.** `planetary degrade
--static-blur <sigma px>` (new, `DegradeOptions.StaticBlurPx`: a Gaussian on every frame's light before the camera's noise, so the
noise stays the camera's; this build, copied to `bin-s` so the deploy family's binary is left as it was). The warped twins e000 to
e005 stay in the family. One probe first, p17 (2 px, the fitted air), to see the degrade's blur reads as the master's did. Then
18 twins (f000 to f017) over the deploy family's air with a static blur drawn uniformly from 1.0 to 3.5 px, no warp, every third held
(2024d), the rest 2022b and 2024c; the training set the 24 deploy training twins, the 4 warped training twins and the 12 blurred ones,
the held set the 6, 2 and 6. G6 as set (the real master's edge inside the training range on every channel, read through the built
CLI's own guard), then step B again and the gates as set.

**p17 read (2026-10-14), and the blur's range set from it before any of its twins is made.** The fitted air with a static blur of 2
px reads edge 0.369 / 0.384 / 0.313 against the same air without it (the pilot twin) 0.458 / 0.482 / 0.336: a factor of 0.81 / 0.80
/ 0.93, so the blur in the frames takes less than the same Gaussian on the master did (0.72 / 0.70 / 0.82 on d007), and blue least.
By p17's factors the real master (0.299 / 0.286 / 0.228) needs about 3.0 to 3.3 px on red and green and about 4.5 on blue from the
fitted air, less from stronger air. **The blurred twins draw their static blur uniformly from 1.0 to 4.5 px** (in place of 1.0 to 3.5);
everything else as set above.

**G6's fallback for the blurred family, set before any twin past 3.4 px is read (2026-10-14; read so far: f000 to f003, 1.3 to 3.4
px, edges 0.33 to 0.40 / 0.33 to 0.42 / 0.29 to 0.34).** If after the 18 the real master's edge lies outside the training range on
any channel, 6 more blurred twins are made once at a blur of 4.5 to 6.5 px (4 training, 2 held, the same air ranges); still outside on
any channel, G6 fails and the arm ends there, a channel the model cannot reach being refused as `PlanetaryGainsModel.Predict` refuses
it, not trained into range by any other means.

**2026-10-14, the finding that ends the edge reading as the model's input.** The blurred twins at 4.3 to 4.5 px read edge 0.366 /
0.379 / 0.334 (f005), 0.386 / 0.377 / 0.340 (f006), 0.370 / 0.355 / 0.315 (f007), no lower than at 1.3 px, while their stacks are far
blurrier (f006 green, bands 1 to 3 against its truth, 0.048 / 0.112 / 0.270; the unblurred pilot 0.362 / 0.426 / 0.546): **the edge at
0.1 cycles a pixel stops tracking blur at about 0.33 / 0.35 / 0.31, and the real master reads below that.** The profile across the
limb says why (each channel's level over its centre's, the sky from the frame's corners; r | g | b at 0.95 / 1.0 / 1.02 / 1.05 radii):
the real master's light outside the limb is the sharpest twin's (real 0.0284 / 0.0177 / 0.0106 red, d007 0.0262 / 0.0175 / 0.0111;
the blurred twins 0.038 to 0.060 at 1.0), so it is about as sharp as d007, but **just inside the limb it is darker than every twin**
(0.95 radii: real 0.143 / 0.148 / 0.148, d007 0.154 / 0.166 / 0.167, the blurred ones 0.159 to 0.187), and its first 600 frames read
the same. Real Jupiter's limb falls faster than the Minnaert law the twins are rendered with and the limb fit models, and the edge
reading (the plane's limb against the fit's Minnaert model) takes that for blur; so does the limb fit (the real master's halo of 370 px
at its bound and its radius 1.2 px past the ephemeris's are the fit making blur out of a darker limb). **The shipped derived gains read
their kernel off the same edge**, so on real Jupiter they are fitted through a kernel blurrier than the stack's (the real master's band
2 at 6.8 to 9.1, band 3 negative); a finding for the derivation as much as for this arm, filed as its own issue.

**Amended before any of its leave-one-out errors is computed: the model's reading becomes the light just outside the limb.** (5) The
glow at 1 to 1.1 radii, found over what the planet's model through the pupil expects there (`planetary compare`'s
`glowNearFound / glowNearExpected`, each channel): seeing's wings and any blur raise it, and a darker limb lowers found and expected
alike. Read so far (green, before this rule, from the three masters `planetary compare` was run on): real 2.51, d007 2.50, d016 3.27.
(6) That glow and the edge together. Step B on the training twins (deploy, warped and blurred alike): (5) taken unless (6)'s
leave-one-out error is at least 10 % lower; either taken only if its error is no more than 1.5 times (3)'s, else the arm ends.
G6 is then read on (5)'s reading; the gates otherwise as set.

**Step B, read on the full training set (2026-10-14; 40 twins: 24 deploy, 4 warped, 12 blurred).** Leave-one-out squared error over
the four gains, r / g / b / total: (3) the edge 344.0 / 758.2 / 903.6 / 2005.8; (5) the glow 291.5 / 545.9 / 606.3 / 1443.7; (6) the
glow and the edge 242.6 / 496.1 / 579.9 / 1318.6. (6) is 8.7 % below (5), short of the 10 % the rule asks, so **(5), the glow alone,
is taken**, within 1.5 times (3)'s. (A first run left the blurred twins out, the script not yet loading their plan, and read 28 twins;
it was discarded on the count before anything was taken from it.) The errors are far above the unwarped family's (95.3 for (3) on 24
twins): the blurred twins' truths ask large gains in bands 2 and 3, and a line through them is coarse; the gates say whether it is too
coarse.

**G6 holds (2026-10-14), read through the built CLI's own guard** (`planetary sharpen --learned-gains`, the trained constant in
`PlanetaryGainsModels`): the real EdgeHD master's glow 1.725 / 2.014 / 2.510 lies inside the 40 training twins' 1.574 to 2.602 /
1.834 to 3.275 / 2.464 to 4.195, and every channel takes the learned gains: r -0.17, 2.46, 2.34, 1.08; g -0.24, 4.13, 1.54, 1.23; b
-0.33, 3.83, 3.26, 0.71 (bands 1 to 4), where the derived gains are r 1.00, 6.78, -0.86, 1.56; g 1.00, 7.83, -0.87, 1.33; b 1.00,
9.13, -1.14, 1.22. Next, G2, G3 and G4 (step C) on the 14 held twins, then G5.

**The check, G3 and G4 (step C), read 2026-10-14 on the 14 held twins through the built CLI.** The guard refused two held channels,
d029 blue (glow 2.459 against the training range's 2.464 to 4.195) and f014 red (2.725 against 1.574 to 2.602); every other channel took
the learned gains, and the CLI's gains there match the training's arithmetic to 0.005 (the log prints two decimals).
- **G3 holds:** the power past 0.9 of each channel's cutoff, learned over derived, 0.001 to 0.008 where the model applies (1.000 on
  the two refused channels); the learned gains take out what lies past the cutoff, where the derived gains lift it.
- **G4 FAILS as written.** Means over the held twins, limb profile error / trough below the truth: derived 0.0059 / 0.0007; learned
  through the shipped fix 0.0037 / 0.0010; through `--limb-fix bounded` 0.0072 / 0.0007. Through the shipped fix the limb's profile is
  37 % closer to the truth but the trough is deeper than the derived's on 11 of the 14 twins (by 0.0001 to 0.0007 of the disk's
  level); through bounded the trough is no deeper on the mean but is on f002, f008 and f017 (up to 0.0042), and the profile is worse.
  Neither fix keeps every held twin's trough no deeper, which step C asked. The same failure as L-GAINS-D's (0.0012 against 0.0009).
- **G5 FAILS as written** (the real master sharpened through the twins' pupil, `planetary judge` against its post; correlation /
  gain on what both hold): band 2 derived 0.868 / 0.355, learned 0.790 / 0.320; band 3 derived 0.959 / 0.540, learned 0.965 / 0.657;
  band 4 0.973 / 0.732 against 0.989 / 0.831; band 5 0.989 / 0.924 against 0.992 / 0.945. Bands 3 to 5 are closer to the post with
  the learned gains; band 2's gain is below the derived's and its correlation 0.078 below (the rule allows 0.05). The post lifts band 2
  hard by its author's taste, and the derived gains lift it hard through the edge's misreading (#1561), so they meet it there.

## L-CHAIN: wavelet gains and deconvolution, learned from the posts (owner's goal, 2026-10-14 16:47 +11: "the learnt model that takes into account both deconvolution and wavelet sharpening and whatnot, must beat both derived and our efforts from pre 16:47")

Set before any of its readings (`deploy/chain/chain_grid.py`; the grid launched, nothing read).
- **The teachers:** the four captures with another program's post and a master of ours (final-1011): the EdgeHD 11 Jupiter
  (2023-11-11), the 12-inch SCT Jupiter (2022-09-10), the EdgeHD 11 Saturn (2022-10-25), the Meade 16-inch Saturn (2023-10-10).
- **The loss against a post:** each band's normalised error e - 2 c sqrt(e) + 1 (c the judge's correlation, e the master's energy over
  the post's, 0 for the same picture at the same strength); L24 the sum over bands 2 to 4 (the one decided on), L14 over 1 to 4
  (reported; band 1 is mostly noise in both).
- **The chain:** the sharpening's gains (derived, at a strength s of 1, 1.25, 1.5 or 2; on the EdgeHD Jupiter also the learned gains,
  #1552, at s 1), then Kolivas's damped Richardson-Lucy at an amount A of 0, 15.6, 31.2, 46.8, 62.4 or 93.6 (`--finish kolivas`).
- **Learned by leave-one-capture-out:** for each capture, (s, A) is the pair with the least mean L24 over the OTHER three captures
  (derived gains on all of them, the base every capture has); that recipe is applied to the held capture, and on the EdgeHD Jupiter
  also on the learned gains (A alone, s = 1).
- **"Beat":** on every held capture the recipe's L24 below the derived gains' (s 1, A 0); and on the EdgeHD Jupiter below every
  effort made before 16:47, read from this same grid with the same options: the derived gains, the learned gains, and each with A
  15.6 and 31.2 (learned + RL 31.2 read 0.292 / 1.062, L24 / L14, from the earlier judge runs).
- **G2's comparator clause** (the learned gains at least 10 % below the best classical option, each held twin's mean): 13 of 14 hold,
  29 to 52 % below (the deploy twins' comparators from deploy-comparators.csv, the warped and blurred twins' from gates-s); **d029
  fails, +15.7 %**, its blue refused by the guard so the derived gains stand there. `--edge-reach` and the three together scored only
  red and green on e002 and f002 (the physical kernel's fit gave blue nothing), so those two twins' best comparator is read over the
  options that scored every channel.

**L-CHAIN read (2026-10-14): FAILS as set.** L24 against each post, derived (s 1, A 0) / the capture's own best in the grid / the
recipe from the other three on it: EdgeHD Jupiter 0.487 / 0.255 (s 2, A 0) / 0.355 at s 1, A 15.6 (0.334 on the learned gains; the
best earlier effort, learned + RL 31.2, 0.292); 12-inch SCT Jupiter 0.236 / 0.168 (s 1.5, A 0) / 0.374 (s 1.25, A 15.6); EdgeHD
Saturn 0.250 / 0.198 (s 1, A 15.6) / 0.309 (s 2, A 0); Meade Saturn 0.423 / 0.113 (s 1.5, A 15.6) / 0.321 (s 1.5, A 0). Every capture
has a recipe that beats the derived gains on its own post, but the four posts ask different things (both Jupiter posts strength and
no deconvolution, both Saturn posts some deconvolution, one near the derived gains as they are), so a recipe fitted to three posters'
taste does not carry to the fourth. (The report's "best this capture could have had" line took its minimum over the recipe and not the
loss on its first run, and was fixed; the verdict did not use it.)

## L-CHAIN-C: the deconvolution learned from the posts' structure (set 2026-10-14 after L-CHAIN was read, disclosed as such)

Why a second arm, and why this loss: a uniform gain on an a trous band scales it and leaves its correlation with the post's band as it
was, so L24's strength term is each poster's taste (the owner: "they convey personal taste"; in TianWen, the strength slider), which four
posters do not agree on. What a step can recover of the post's own structure is its correlation, which only a non-linear step moves:
the deconvolution. So:
- **The score:** C24, the mean of the judge's correlation over bands 2 to 4 against the capture's post (higher is closer); C14 over 1 to
  4 reported.
- **The chain:** the sharpening's gains at strength 1 (derived on every capture; on the EdgeHD Jupiter also the learned gains), then
  Kolivas's Richardson-Lucy at an amount A from the same grid (0 to 93.6). The strength stays the user's.
- **Learned by leave-one-capture-out:** A* is the amount with the highest mean C24 over the other three captures (derived gains),
  applied to the held capture (on the EdgeHD Jupiter on the learned gains too).
- **"Beat":** on every held capture C24 above the derived gains' (A 0); on the EdgeHD Jupiter above every effort before 16:47 (derived
  and learned gains at A 0, 15.6 and 31.2), read from the same grid. Ties count as not beating.

**L-CHAIN-C read (2026-10-14): FAILS as set.** A* is 15.6 on every leave-one-out. C24 against each post, derived gains (A 0) / the
recipe from the other three: EdgeHD Jupiter 0.9333 / 0.9437 (on the learned gains 0.9433), 12-inch SCT Jupiter 0.9677 / 0.9573, EdgeHD
Saturn 0.9560 / 0.9713, Meade Saturn 0.9343 / 0.9760. Three captures beat the derived gains, the 12-inch SCT does not (its post wants
no deconvolution); on the EdgeHD Jupiter the best earlier effort, learned + RL 31.2, reads 0.9483 (learned gains by A: 0.9147, 0.9433,
0.9483, 0.9477, 0.9427, 0.9297 at 0 to 93.6), so the recipe's 0.9437 does not beat it. Neither the master's glow, its edge nor its
cutoff orders the captures by the A their posts want (the SCT's glow 2.69 wants 0, the Meade's 1.58 wants 31.2). What both arms had
for a deconvolution was Kolivas's ONE damped step with a fixed 1.29 px Moffat, the same kernel on every capture whatever its optics or
sampling: not a deconvolution of anything the capture holds.

## L-DECONV: a learned chain with a real deconvolution (set 2026-10-14 19:45 +11, after L-CHAIN-C was read, before any reading of this one)

The owner's goal stands ("the learnt model that takes into account both deconvolution and wavelet sharpening and whatnot, must beat both
derived and our efforts from pre 16:47"), and the owner has said deconvolution many times. Read before setting this: three smoke runs of
the new step on the real EdgeHD master, for their time only (77 s derived, 141 s learned, 20 RL steps about 40 s more); no score of
them against the post or any truth was read.
- **The new step** (`--finish deconvolve`, `PlanetaryFinishing.Deconvolve`): Richardson-Lucy with the pupil's own diffraction (each
  channel's wavelength, the capture's aperture and obstruction, at the master's plate scale as the limb window reads it) for N steps, on
  the sharpened window after the gains, before it is pasted. So the PSF is the capture's own optics at its own sampling, where Kolivas's
  step took one fixed 1.29 px Moffat everywhere. N in {0, 10, 20, 40, 80}.
- **The learned chain:** the learned gains (#1552, the EdgeHD 11 Jupiter model; on the twins given as `--gains` from the model's own
  prediction off glow.csv, which the CLI matched to 0.005, so the glow is not re-read 270 times), then RL at N_T.
- **The teacher (no taste in it):** each twin's truth carried to the aperture target (`--score-against aperture`: the planet as the
  aperture passes it, the telescope's diffraction undone up to its cutoff). Loss: the mean over r, g and b of the error, bands 1 to 4.
  **N_T** is the N with the least mean loss over the 40 training twins.
- **Held twins (14), each scored the same way:** derived (telescope target), derived `--target aperture` (the derived route to this same
  target), learned gains alone, learned + Kolivas 15.6, learned + Kolivas 31.2 (the efforts before 16:47), learned + RL N_T.
- **The posts:** the four captures, derived + RL at each N, and on the EdgeHD Jupiter learned + RL at each N, each through `planetary
  judge` against its post: L24 and C24 as L-CHAIN and L-CHAIN-C define them.
- **"Beat", the goal's verdict (both must hold):**
  1. On the held twins, learned + RL N_T's mean loss below each of: derived, derived `--target aperture`, learned alone, learned + K15.6,
     learned + K31.2.
  2. On the EdgeHD Jupiter against its post, learned + RL N_T's C24 above 0.9483 (learned + K31.2, the best effort) and 0.9333
     (derived), AND its L24 below 0.292 (the best effort) and 0.487 (derived), the efforts read from the L-CHAIN grid.
- **Reported with a verdict of its own (transfer, not the goal's):** derived + RL N_T on the other three captures, C24 and L24 against
  the derived gains alone; and N_P, the N with the highest mean C24 over the other three captures (derived), on each held capture.
- **Operational note (2026-10-14 21:03, before any reading):** the first launch scored each arm in a run of its own, which recomputed the
  stack's scores, the truth's own gains and the truth carried to the aperture (a full-frame diffraction a channel) every time: about
  28 minutes a twin run on the shared box. The CLI now takes `--kolivas-amount` and `--deconvolve-iterations` as comma lists and caches
  the aperture truth a process, so one run a twin covers its arms; the arms, the scores and the rules are as set. The first launch's
  logs (five, none read for a score) are kept in deconv/old and not used.
- **N_T read (2026-10-15 06:16):** the 40 training twins' mean loss against the aperture truth by N: 0 steps 2.3461, 10 1.8922, 20
  1.8811, 40 1.8994, 80 1.9274; **N_T = 20**. Any N beats none on 38 to 40 of the 40 twins. The held twins and the posts are not read
  yet.
- **Verdict 2 read (2026-10-15 06:28): FAILS.** Learned gains + RL 20 on the EdgeHD Jupiter against its post: C24 0.9477 (needed
  above 0.9483), L24 0.294 (needed below 0.292). So **L-DECONV as set FAILS** whatever verdict 1 reads. Read beside it, not a verdict:
  the real deconvolution brings bands 3 and 4 to the post's own amplitude (learned + RL gains 0.99 to 1.00 there, derived 0.54 and 0.73)
  and lifts the EdgeHD Saturn (C24 0.956 to 0.978 at 40 steps); against this post more steps read closer (learned C24 0.942, 0.948,
  0.950, 0.951 at 10, 20, 40, 80; derived 0.952 at 10) while the twins' truth is best at 20, and the 12-inch SCT's post is closest with
  none (0.968). The best step count of a Richardson-Lucy rises as the noise falls; whether the twins are noisier than the real masters
  is not yet read.
- **The noise reading (2026-10-15 06:50), against the explanation above:** the white level past 0.4 cycles a pixel (each channel's,
  the Wiener's own estimator, printed by a build that always says it): the real EdgeHD master 0.182, 0.191, 0.197; six twins across the
  three families 0.140 to 0.202. The real master is NOT quieter than the twins (its 2,696 frames against their 300 notwithstanding), so
  noise does not explain why this post asks more steps than the twins' truth.

## L-DECONV-C: the deconvolution, then the low-pass at the cutoff (set 2026-10-15 06:55, after L-DECONV's verdict 2 was read, disclosed as such)

- **The chain:** the learned gains (#1552), Richardson-Lucy with the pupil's own diffraction for N steps, then the low-pass at each
  channel's pupil cutoff (`--finish deconvolve+cutoff`, `PlanetaryFinishing.LowPassAtCutoff`).
- **Why:** on these oversampled captures the finest a trous band (about 0.25 to 0.5 cycles a pixel) lies past every channel's cutoff
  (0.23, 0.26, 0.30): nothing of the planet is there, and each RL step raises the noise there. L-DECONV's loss summed band 1, so its
  N_T was held down by noise the low-pass removes. #1281 measured the low-pass raising band 1's correlation with every post and waited
  on an oversampled twin to judge it on a truth: these twins are oversampled.
- **N_T':** the N in {10, 20, 40, 80} with the least mean error over bands 2 to 4 (mean of r, g, b) against the aperture truth over
  the 40 training twins, read from L-DECONV's training logs (the low-pass acts from 0.85 of the cutoff, on band 1 almost wholly and the
  top of band 2 a little, so bands 2 to 4 are what the steps decide). Computed after this is written.
- **Disclosed:** L-DECONV's post table for every N WITHOUT the low-pass has been read (learned + RL C24 0.942, 0.948, 0.950, 0.951 and
  L24 0.322, 0.294, 0.283, 0.278 at 10, 20, 40, 80), so verdict 2 below is not blind on the EdgeHD Jupiter. What is blind: every held
  twin, and the Meade Saturn's post.
- **Verdicts (all must hold):**
  1. Held twins (new runs of the chain at N_T', scored against the aperture truth as L-DECONV): the mean error, bands 1 to 4, below
     every L-DECONV comparator on the same twins (derived, derived `--target aperture`, learned, learned + K15.6, learned + K31.2) and
     below learned + RL 20 without the low-pass.
  2. The EdgeHD Jupiter against its post: C24 above 0.9483 and L24 below 0.292 (the best effort before 16:47), and above / below the
     derived gains' 0.9333 / 0.487.
  3. The Meade Saturn (blind): derived + RL N_T' + low-pass, C24 above the derived gains' own on its post.
- **N_T' read (2026-10-15 07:00):** bands 2 to 4 over the 40 training twins: 0 steps 1.4898, 10 1.1346, 20 1.1277, 40 1.1325, 80 1.1372;
  band 1 alone is least at 20 as well (0.7533). **N_T' = 20**, the same as N_T: band 1 was not what held the steps down.
- **L-DECONV-C read (2026-10-15 07:25): FAILS as set (verdict 2).** The EdgeHD Jupiter, learned gains + RL 20 + the low-pass, against
  its post: C24 0.9477, L24 0.294 (needed above 0.9483 and below 0.292); the low-pass moved band 1 alone (its correlation with the
  post 0.528) and left bands 2 to 4 as L-DECONV read them. **Verdict 3 (blind) holds:** the Meade Saturn, derived + RL 20 + low-pass,
  C24 0.9810 and L24 0.123 against the derived gains' 0.9343 and 0.423 (L-CHAIN's grid). Verdict 1 (held twins) not yet run.

## L-DECONV-P: the posts teach the steps (the owner's choice, 2026-10-15 07:00, "Posts teach the steps"; set before the Meade Saturn's step grid was judged)

- **N_P(held):** the N in {0, 10, 20, 40, 80} with the highest mean C24 over the OTHER three captures' posts, each on the derived
  gains + RL N (`--finish deconvolve`, no low-pass), as L-DECONV set it for its report. Ties to the fewer steps.
- **The chain:** each capture's gains (the learned gains on the EdgeHD Jupiter, the derived elsewhere), then RL at N_P(held).
- **Disclosed:** every post reading for N 0 to 80 on the EdgeHD Jupiter, the 12-inch SCT Jupiter and the EdgeHD Saturn has been read;
  the Meade Saturn's step grid has not, and over the two others N_P for the EdgeHD Jupiter is a near tie from 20 to 80 (summed C24
  1.9360, 1.9367, 1.9367), so the Meade decides it.
- **The goal's verdict:** the EdgeHD Jupiter against its post, learned gains + RL N_P: C24 above 0.9483 AND L24 below 0.292 (the best
  effort before 16:47), and past the derived gains' 0.9333 / 0.487.
- **Transfer, its own verdict:** every other capture's derived + RL N_P(held) C24 above its derived gains' own (N 0).
- **Reported, not a verdict:** the held twins' mean error against the aperture truth for learned + RL N_P(EdgeHD Jupiter), against
  learned + RL 20 (the truth's own count): what following the posts costs in truth.
- **L-DECONV-P read (2026-10-15 07:10). The goal's verdict HOLDS.** The Meade Saturn's grid (blind until now), derived + RL N, C24 /
  L24: 0 steps 0.9343 / 0.423, 10 0.9763 / 0.151, 20 0.9810 / 0.123, 40 0.9813 / 0.120, 80 0.9823 / 0.115. N_P for the EdgeHD Jupiter,
  from the other three posts' mean C24 (0.9527, 0.9703, 0.9723, 0.9727, 0.9730 at 0 to 80): **80**. Learned gains + RL 80 against its
  own post: **C24 0.9510, L24 0.278**, past the best effort before 16:47 (0.9483 / 0.292) and the derived gains (0.9333 / 0.487). Its
  own post did not choose the count.
- **The transfer verdict FAILS:** the 12-inch SCT Jupiter's N_P is 20 and its post reads 0.9593 against 0.9677 with no deconvolution;
  the EdgeHD Saturn (N_P 10: 0.9727 against 0.9560), the Meade Saturn (10: 0.9763 against 0.9343) and the EdgeHD Jupiter on the derived
  gains (80: 0.9453 against 0.9333) gain.
- **Read beside it, not a verdict:** the count is poorly set by the posts: the EdgeHD Jupiter's N_P is a near tie (0.9723 to 0.9730
  over 20 to 80), and one count learned from all four posts (the mean C24 on the derived gains: 0.9478, 0.9657, 0.9663, 0.9659, 0.9661)
  is **20**, the truth's own count, which on the learned gains reads 0.9477 against this post and does not beat 0.9483. The held
  twins' cost of 80 steps against the truth's 20 waits on the grid.
