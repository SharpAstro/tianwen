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
