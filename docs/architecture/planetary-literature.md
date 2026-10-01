# Planetary restoration: what the literature says, and what it predicts here

The literature behind `docs/plans/planetary-restoration.md`, read against what R4 and R5 measured. Three reviews were made on
2026-09-30, one a theme, each entry verified against its DOI, arXiv, ADS, publisher or code page (the reviews say for each how
far they read it, full text, method, abstract or metadata only, and list what could not be verified apart). Five of the
citations the conclusions below lean on were checked again against their sources (Mackay 2013, Delbracio and Sapiro 2015,
von der Luehe 1984, Lao et al. 2024, Loefdahl 2010). A fourth was made on 2026-10-01, after R8, on the two problems R8 left. The
reviews in full:

- [Fourier-domain lucky imaging, frame fusion and speckle](planetary-literature/fourier-lucky-imaging.md) (theme A)
- [Sub-pixel registration of noisy frames, and dewarping](planetary-literature/registration-and-dewarp.md) (theme B)
- [The residual blur, measured and inverted; learned restoration](planetary-literature/blur-and-restoration.md) (theme C)
- [The finest band's blur without a point source, and sharpening without a ring at the limb](planetary-literature/fine-band-and-ringing.md) (theme D)

A number marked **derived** is arithmetic from a cited result applied to our capture, not a published figure; the reviews give
the assumptions behind each.

## Where we use the Fourier transform, and where the literature goes further

The question that started this (the user, 2026-09-30), after ASTRA-SR (Ge, Cui and Liu, arXiv 2609.26731) leant on Fourier
analysis throughout.

| We use | Where |
|---|---|
| Correlation for registration: phase correlation (whitened) and a plain cross-correlation, the peak climbed on the Fourier-interpolated surface | `PhaseCorrelation`, `GlobalAligner`, `AlignmentPointMatcher`, `CorrelationRegistrar` |
| A shift exact to the sub-pixel, by the Fourier shift theorem | `PlanetaryMetrics.Shift` |
| Band power: frame scoring and the power past a cutoff | `FftHighBandEstimator`, `PlanetaryMetrics.PowerAbove` |
| The telescope's cutoff read off a power spectrum | R1, `tianwen planetary-aperture` |
| The twin's optics: phase screens, short-exposure PSFs, the scatter kernel | `PlanetaryDegrade`, `Imaging/Optics` |

| We do not (yet) | What it is | Where it would go |
|---|---|---|
| Per-frequency frame selection or weighting | Lucky Fourier (Garrel, Guyon and Baudoz 2012; Mackay 2013); Fourier burst accumulation (Delbracio and Sapiro 2015) | R4, R8 |
| Speckle interferometry and phase recovery | Labeyrie 1970, Korff 1973, Knox and Thompson 1974, the bispectrum (Lohmann, Weigelt and Wirnitzer 1983); in production on the Sun (KISIP, Woeger et al. 2008) | R7, R8 |
| The spectral ratio | `abs(mean F)^2 / mean abs(F)^2` over the frames, which gives r0 of the varying air from any resolved object (von der Luehe 1984) | R7 |
| Deconvolution by a measured transfer | Wiener with an object power spectrum (Conan et al. 1998), Richardson 1972 and Lucy 1974, edge-preserving priors (MISTRAL, Mugnier et al. 2004) | R7, R8 |
| Multi-frame blind deconvolution | MOMFBD (van Noort et al. 2005), torchmfbd (Asensio Ramos et al. 2025, MIT) | R7, only if the oracle gap is large |

ASTRA-SR's Fourier work is all inside its network. Its one physical finding, that restoring the Fourier MAGNITUDE gains more
than the phase under a PSF's blur (3.7 dB against 0.4), is the speckle literature's old point: a long exposure's transfer is real
and positive, so its correction is a magnitude correction. Our twin's still layer is one static screen, though, whose transfer
has a phase; whether R8's real, isotropic per-band gains can be enough is a measurement (the oracle swap below).

## What the literature says about what we measured

### Plain correlation beats phase correlation on an 8-bit frame

R5 part 1 placed a 16 px patch to 1.11 px RMS whitened and 0.35 px plain. That is the textbook result. Knapp and Carter (1976)
derived the maximum-likelihood weighting of a cross-spectrum for delay estimation: it favours the frequencies with the best
signal to noise, and at low SNR it is plain correlation with an extra low-pass (Eckart prefiltering). Phase correlation weights
every frequency alike, the noise-dominated finest ones included. PlanetarySystemStacker's own notes call phase correlation "not
recommended" and blur every frame before measuring a shift; scikit-image's documentation says the unnormalised correlation
"may perform better in high-noise scenarios".

### The peak is placed between the pixels by climbing it, never by a parabola

A disk's autocorrelation peaks in a rounded cone, and a parabola through three samples of a cone locks toward the whole pixel.
Along Jupiter's belts, where only the limb places a frame, the global aligner's plain correlation misplaced a noise-free banded
disk by 0.198 px RMS through that bias alone (R5 part 3). R4's registrar had met it in miniature (0.033 px) and climbed the
peak by Newton's method on the Fourier-interpolated surface; that climb is now `PhaseCorrelation.ClimbPeak`, shared, and the
same disk reads 0.070 px noise-free. Guizar-Sicairos, Thurman and Fienup (2008) is the standard efficient form of the same idea
(an upsampled DFT near the peak, in scikit-image as `phase_cross_correlation`). Loefdahl (2010), comparing shift algorithms for
solar wavefront sensors on known shifts, found square-difference matching with a 2-D quadratic fit best and apodised Fourier
covariance shrinking the shifts: our matcher is of that last class, so the slope of measured against true shift is worth a look.

### A reference is a temporal average, in every working method

Nearly every classical method registers each frame to a temporal average: Fraser, Thorpe and Lambert 1999; Zhu and Milanfar
2013; Mao and Gilles 2012; Hardie et al. 2017 and 2021; MOMFBD, which forces each subfield's mean tilt to zero; the Dunn Solar
Telescope's destretch, a running mean of 30 frames. Among amateur tools PlanetarySystemStacker measures its points against the
mean of the best 5 % and AutoStakkert against a stack of the best frames (its session file for 2022-09-03's Red names 8,572 of
12,990), optionally stacked twice. Lao, Wang, Wong and Soatto (CVPR 2024) is the user's centroid idea exactly: a frame's
deformation modelled as the mean of the flows from it to all the others, by the central limit theorem. Part 2 took the median
geometry out afterwards; re-measuring every frame against a stack on that geometry is the half not yet done.

### The mesh, not the physics, set part 2's kill line

Hardie et al. (2021) model block registration as a filter on the tilt field. Applied to our Gaussian blend (a 12 to 48 px reach
over points 8 to 12 px apart, on a warp whose correlation is gone by 9 px), the same algebra caps the mesh's recovery at 3 to
8 % (**derived**), which bounds part 2's measured 1 and 3 % from above, as it should. Points 4 px apart blended over a 4 px reach
would recover about 11 to 19 %, and a kriging interpolation with the warp's own covariance 15 to 22 %, up to 31 % against a
stacked reference (**derived**). Part 2 varied spacing, reach and reference one at a time; the prediction is that only all three
together pay.

### Resampling is as large as the dewarp

A bilinear sample at an evenly spread sub-pixel phase adds p(1 - p) px^2 of blur variance, 1/6 on average an axis, 46 % of the
warp's own 0.36 (**derived**); whole-pixel shifting adds 1/12. The same fact in the frequency domain is that the stack's average
kernel is the bilinear triangle, sinc^2 an axis in transfer: 0.81 at 0.25 cycles a pixel. R5 part 3 measured it: Lanczos-3 lifts a
global stack's band 1 transfer by 10 to 12 % on the twin and cuts its error by 0.014 to 0.024, more than every dewarp setting of
part 2. SSTRED (Loefdahl et al. 2021) folds every geometric step into one resample for this reason.

### Frame selection was judged at the wrong operating point

R4's keeps (5 to 10 % in band 1, the fewest frames in the coarse bands) minimise the error of a raw, unrestored stack. Theme A
decomposes R4's own band 1 numbers (**derived**): at a 5 % keep the raw error^2 of about 0.42 is about 0.34 transfer deficit,
which R8 exists to remove, about 0.06 a residual that does not average down, and only about 0.015 frame noise. Once a stack is
restored per frequency the best combination is the matched filter, each frame weighted by its transfer, and with band 1's top 5 %
carrying only 1.4 times the mean transfer that predicts band 1 wants half the frames or more. Fourier burst accumulation's own
simulations agree: with short exposures a plain average had the lowest error. It is also why Garrel and Mackay, judging raw
images by their Strehl ratio, favour hard selection while FBA's error analysis and The Thresher (Hitchcock et al. 2022, MIT) use
every frame.

Our regime is not the one the literature's large gains came from. Garrel's factor of about 8 fewer frames and Mackay's 1 %
resolution from 10 % come from strongly varying PSFs, AO residuals or D/r0 of 7 to 30; the calibrated twin's free air is D/r0
about 3 at 500 nm and its frames differ little (their reference gains spread 0.91 to 1.06 of the median).

## What it changes ahead

- **R5a, drizzle.** A Gaussian misregistration of sigma passes exp(-2 pi^2 sigma^2 f^2) of the signal. With 0.6 px of warp left
  in every stack that is 0.64 at 0.25 cycles a pixel, 0.17 at 0.5 and 0.018 at 0.75, the band a 1.5x drizzle would add
  (**derived**). Drizzle past the sensor grid cannot pay on this capture until the warp is followed; Bayer drizzle to the sensor
  grid, recovering 0.25 to 0.5 cycles a pixel on a colour plane, still has 0.17 to 0.64 to work with.
- **R6, de-rotation.** Jupiter's texture moves about 0.53 px a minute at the centre of a 100 px disk (**derived**, System II),
  while its limb stays: any temporal-average reference built over more than about a minute is smeared by more than the warp
  unless it is derotated or built from a shorter window, and a correlation's track and a limb fit's drift apart by as much.
- **R7, the blur.** A static blur cancels out of every ratio of frames. If a frame is S T_i O (a static S, a varying T_i), a lucky
  frame against the stack is T_lucky / mean T and the spectral ratio is abs(mean T)^2 / mean abs(T)^2: S is gone from both. So
  R7's probe (a), the lucky frames against the stack, sees only the varying blur and cannot agree with probe (b), the limb, which
  sees the total, the still layer and the scatter the twin says dominate. The real capture's lucky tenth is only 1 % narrower at
  the limb than every frame (7.28 px against 7.37). Probe (a) is the stack's EXTRA blur over its lucky frames, (b) the total,
  and the spectral ratio is a third, independent probe of the varying part. A limb-only fit trades the core against the wing
  (Wedemeyer-Boehm 2008, Hinode's PSF from a Mercury transit), so (b) fits the limb and the halo together. The twin's scatter
  kernel, (1 + (r/a)^2)^(-3/2) with a = 10 px, has the transfer exp(-2 pi a f) exactly: inverting it lifts bands 1 to 3 by only
  1.053 and puts the halo's light back on the disk, while a compact kernel stretched to absorb the halo would over-boost the
  fine bands.
- **R8, the gains.** Conan et al. (1998) give the per-frequency Wiener gain with a power-law object spectrum, which is R8's formula
  with a prior the capture can fit. ASTRA-SR's oracle swap, the stack's Fourier magnitude replaced by the truth's with its phase
  kept and the other way round, per band on the twin, says how much a real gain can reach. Nobody publishes a quantitative
  ringing metric for planetary sharpening that theme C could find (Lewis 2020 measures the Mars edge-rind's width, not its
  depth), so R3's limb undershoot stays R8's penalty.
- **R9, a learned stage.** Every learned planetary restorer found (PlaNet, AstroDiff, DIPLI, ASTRA-SR, FluxFlow) trains on
  turbulence alone, with no static layer or scatter, on 12 frames or fewer against our thousands, or scores real data by a
  Laplacian energy that rewards over-sharpening; none released code or weights. The gate stays as written.

## Candidate measurements, ranked by what they decide

Each carries its prediction and what would falsify it; the issues are in the planetary-restoration milestone.

1. **Re-register against the mean-geometry stack, then iterate once** (R5, #1081). Every frame's points measured against a stack
   on the median geometry, restacked, measured again. Predicted: the 16 px point error falls from 0.35 toward 0.25 px, the
   points' recovery on the true geometry rises above 19 %, the second pass adds under a fifth of the first's gain. Falsified if
   the error stays at 0.35: then it is bias, not the reference's noise.
2. **Dense points with a matched interpolator** (R5, #1081). Points 4 px apart, the mesh on 4 px nodes, a 4 px reach and a kriging
   with the twin's warp covariance. Predicted 11 to 19 % and 15 to 22 % recovered in the applied field against 3 % today.
   Falsified if kriging at 4 px stays at 3 %: then the loss is in how the field is applied.
3. **An efficient estimator, judged against its bound** (R5, #1082). The Cramer-Rao bound per point on the twin (Robinson and
   Milanfar 2004, Pham et al. 2005); then the maximum-likelihood weight S / (N (N + 2S)) on the cross-spectrum, a mean- and
   plane-subtracted square-difference match with a 2-D quadratic fit, and correlation surfaces averaged over frames rather than
   their peaks (Meinhart et al. 2000). Predicted 10 to 25 % off the 16 px error, more at 8 to 12 px. Falsified if plain
   correlation is already within about 1.2 times the bound.
4. **Frame selection scored after restoration** (R4 and R8, #1083). R4's selections, per-band matched weights and an FBA exponent
   sweep, each scored after an oracle per-frequency Wiener; and the oracle ceiling of per-frequency selection from the twin's
   noise-free frames, warp on and off, before building it. Predicted: band 1's best keep moves from 5 to 10 % to half the frames
   or more, and the ceiling of per-frequency selection is under 15 % in band 1's transfer on this twin.
5. **The blur's static and varying parts, three ways** (R7, #1084). The spectral ratio, the lucky frames against the stack, and a
   joint limb-and-halo fit. Predicted: the spectral ratio's r0 matches the twin's free air within 15 % and ignores the still
   layer; the lucky-against-stack width is under 0.3 of the limb's; the limb's kernel composed from the varying part, the still
   layer and the scatter matches the twin's true kernel within 10 %.
6. **How far a real per-band gain can reach** (R8, #1085). ASTRA-SR's magnitude and phase oracle swap per band, and a
   multi-frame Wiener with the twin's true per-frame PSFs, which bounds anything MFBD or Fourier-domain lucky imaging could add:
   predicted under 10 % on the calibrated twin and over 20 % with the still layer off. Then three regularised inverses at a
   matched band 3 transfer, scored on the limb: Wiener with Conan's object spectrum, Richardson-Lucy on offset-subtracted
   electrons, and an L1-L2 edge-preserving prior.

## Traps this data sets every Fourier method

- **No signal-free annulus.** The pupil's cutoff (0.73 to 1.37 cycles a pixel over the corpus) lies past Nyquist, so a power
  spectrum's corners hold signal as well as noise, and near-Nyquist content aliases. Estimate noise from frame differences or
  the camera model.
- **The 8-bit sky sits below the rounding step** (read noise 0.21 ADU): its noise is neither additive nor white there.
- **The warp is a phase error.** No magnitude-weighted method (FAS, FBA, HDR+) can undo what the 0.6 px warp costs a coherent
  stack.
- **Static blur is invisible to the spectral ratio and to any selection, but only a static CONVOLUTION is** (the scatter, the pixel). A still phase in the pupil adds to the air's before the PSF forms and does not cancel: measured in R7 part 1, the calibrated twin's ratio read as free air alone gave 5.71 cm for 8.5, and 9.06 with its still layer in the theory.
- **A 4 ms exposure is not frozen at 22 m/s**: the pupil slides 9 cm, more than r0, so Korff's frozen-screen transfer reads band
  1 low.
- **Colour.** Turbulence is chromatic (r0 goes as the wavelength to the 1.2), so per-frequency weights on an RGGB capture are per
  CFA plane, never shared as FBA shares them for camera shake.

## Code and licences

TianWen is AGPL-3.0. Reading any of these for ideas is fine; copying code in brings its licence.

| Code | Licence | Into TianWen |
|---|---|---|
| fba-ipol (Anger and Meinhardt-Llopis 2017) | BSD-2 | yes |
| The Thresher (Hitchcock et al. 2022) | MIT | yes |
| torchmfbd (Asensio Ramos et al. 2025) | MIT | yes |
| HCIPy (Por et al. 2018) | MIT | yes, as a reference for the twin |
| scikit-image registration (Guizar-Sicairos 2008) | BSD-3 | yes |
| hdrplus-python (Monod, Delon and Veit 2021) | AGPL-3.0 | yes |
| PlanetarySystemStacker (Hempel) | GPL-3.0-or-later | yes, as GPL-3.0 code |
| deblurTC (derived from KISIP) | GPL-2.0 | read only, if GPL-2.0-only |
| TMT, DATUM (Chan's group) | no licence file | no |
| AutoStakkert!, RegiStax | closed | output and session files only |
