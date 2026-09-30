# Theme C: the blur a planetary lucky-imaging stack is left with, measured and inverted

> One of three reviews behind [the planetary literature synthesis](../planetary-literature.md), made for `docs/plans/planetary-restoration.md` on 2026-09-30 and kept as written: its "our" and "we" are TianWen, its numbers are R4's and R5's as they stood that day, and each entry says how far its source was read. What it predicted and what was then measured is in the plan.

Literature review for TianWen's planetary restoration plan (R5a, R7, R8, R9), 2026-09-30.

## How this was verified

Every item below was checked against a primary page: the arXiv abstract or HTML, the publisher's abstract
page (Optica, IOP, CVF, AAAI, PMC), or the DOI's Crossref record, and the code repository through the
GitHub API where one exists. For each item the entry says what was actually read:

- **read**: abstract and method (for PlaNet, Burns 2000, the PSS source and ASTRA-SR the full text);
- **metadata**: title, authors, venue, pages confirmed through Crossref or the publisher, with the content
  taken from the abstract page or the search index only.

A&A, ADS, Science and iso.org refused automated fetches (403/405); for those the DOI was resolved through
Crossref and the abstract read from another copy (arXiv, Semantic Scholar) where one exists. Items I could
not verify are listed at the end and are not cited in the body.

## Numbers of our system the predictions use

From the plan's R2 part 2 (the calibrated twin of 2022-09-03 Red):

| Quantity | Value | Note |
|---|---|---|
| Aperture, scale | 254 mm f/4.7, 0.497"/px | ASI290MM, 8 bit, 4 ms, disk radius 49 px |
| lambda/D at 550 nm | 0.447" = 0.90 px | scales with wavelength |
| Pupil cutoff D/lambda at 550 nm | 1.11 cycles/px | Nyquist is 0.5; a Bayer plane's 0.25 per sensor px |
| Airy first-zero diameter | 1.09" = 2.2 px | |
| Free air (twin) | r0 8.5 cm at 500 nm, L0 4 m, 22 m/s | D/r0 3.0 at 500 nm, about 2.2 in Red |
| Still layer at the telescope (twin) | r0 2.7 cm at 500 nm, L0 0.25 m, held still | static in every frame |
| Wide scatter (twin) | 5 % of the light, kernel (1 + (r/a)^2)^(-3/2), a = 5" = 10 px | its OTF is exactly exp(-2 pi a f) |
| Exposure travel | 22 m/s x 4 ms = 8.8 cm = 0.35 D | not frozen, see ET-MTF |
| Registration error | phase correlation 0.71 px RMS; limb fit 0.04 px | R2 part 2 finding 6 |
| Residual warp | about 0.6 px RMS over 10 px | not followed by any alignment (R5 part 2) |

Two derived facts carry through the whole review:

1. **The scatter's transfer is exp(-2 pi 10 f)**: 0.53 at 0.01 cycles/px, 0.28 at 0.02, 0.04 at 0.05. Above
   about 0.05 cycles/px (a-trous bands 1 to 3, roughly) the stack's transfer is 0.95 times the PSF's, so an
   exact inverse multiplies those bands by 1/0.95 = 1.053 and returns the halo's light to the disk at the
   lowest frequencies. That is photometry, not invented detail; the danger is a COMPACT kernel stretched to
   absorb the halo, which then over-boosts the fine bands.
2. **A static blur cancels out of every ratio of frames.** If frame i is `S T_i O` (static S, varying T_i),
   then a lucky frame against the stack is `T_lucky / <T>` and the spectral ratio is
   `|<T_i>|^2 / <|T_i|^2>`: S is gone from both. Only a probe with an absolute reference (the limb against
   its rendered profile, a moon's known disk, a space image) sees the still layer and the scatter, which
   the twin says dominate.

## Headline findings

1. **R7's two probes measure different things, so its pre-registered agreement fails by construction.**
   Probe (a), the lucky-against-stack kernel, is a ratio of frames and cannot see the still layer or the
   scatter (fact 2); probe (b), the limb, sees the total. The real capture's lucky tenth is 7.28 px wide at
   the limb against 7.37 for every frame, so (a) will return a kernel close to a delta while (b) returns
   the full blur. Re-register (a) as the stack's EXTRA blur over its lucky frames and (b) as the total, and
   compare (b) with (a) composed with a static term. The spectral ratio of von der Lühe (1984) is a third,
   independent measure of the dynamic part alone.
2. **The classical answer to "restore the magnitude" is 50 years old.** ASTRA-SR's finding (restoring the
   Fourier magnitude gains 3.7 dB, the phase 0.4 dB) is what speckle interferometry and KISIP build on: a
   long exposure's OTF is real and positive, so its correction is a magnitude correction. But our twin's
   still layer is ONE static phase screen, whose PSF is asymmetric and whose OTF has a phase. Whether R8's
   real, isotropic per-band gains suffice is therefore a measurement, and the ASTRA-SR oracle swap run on
   our twin is the cheapest one (ranked second below).
3. **No published quantitative ringing metric for planetary sharpening exists that I could find.** The
   closest are a morphological ringing metric for Richardson-Lucy iterations (Balasubramanian et al. 2005),
   a perceptual one (Zuo et al. 2009), and Lewis (2020, JBAA) on the Mars "edge-rind", which quantifies
   the dark arc's width (0.6 of the Airy diameter, 0.65" here) but not its depth. R3's limb undershoot is,
   as far as this search reaches, new.
4. **Every learned planetary restorer found (PlaNet, AstroDiff, DIPLI, ASTRA-SR, FluxFlow) is trained or
   judged in a way our twin would fail:** turbulence-only degradations with no static layer or scatter,
   12 or fewer input frames against our thousands, targets that are amateur-processed images (AstroDiff) or
   resampled spacecraft frames above the telescope's cutoff (ASTRA-SR), or real-data scores by Laplacian
   energy (DIPLI), which rewards exactly the over-sharpening we measured. None released code or weights.
   R9's gate stays as written.
5. **Drizzle past the sensor grid cannot pay while a 0.6 px residual warp remains.** A Gaussian
   misregistration of sigma passes exp(-2 pi^2 sigma^2 f^2): at 0.6 px that is 0.64 at 0.25 cycles/px,
   0.17 at 0.5 and 0.018 at 0.75 (the band 1.5x drizzle would add). Bayer drizzle to the sensor grid
   (recovering 0.25 to 0.5 on a colour plane) still has 0.17 to 0.64 to work with. With phase-correlation
   registration alone (0.71 px) it is worse again; the limb fit's 0.04 px passes 0.98 at 0.75.

---

## A. Physics of the residual blur (short and long exposure, simulation)

### A1. Fried 1966, short- versus long-exposure resolution
- **Citation:** D. L. Fried, "Optical resolution through a randomly inhomogeneous medium for very long and
  very short exposures", JOSA 56(10), 1372-1379 (1966). doi:10.1364/JOSA.56.001372
- **URL:** https://opg.optica.org/abstract.cfm?URI=josa-56-10-1372 (read, abstract)
- **Code, licence:** none (theory).
- **What it does:** derives the long-exposure MTF `exp(-3.44 (lambda f / r0)^(5/3))` and the short-exposure
  (tilt-removed) MTF, which carries the factor `[1 - alpha (lambda f / D)^(1/3)]` in the exponent; notes that
  short exposures beat long ones "only if the wave distortion does not include substantial intensity
  variation", i.e. scintillation.
- **Needs:** r0, D, wavelength, and the near- or far-field alpha (1 or 1/2).
- **Phases:** R7 (the dynamic kernel's analytic form), R2 (twin validation).
- **Prediction on the twin:** with the still layer and the scatter switched off, a stack of every frame
  registered by the limb fit (global shift only, no alignment points) has a transfer against T1 that
  follows Fried's short-exposure MTF at the free air's r0 (scaled to the filter) within 10 % in bands 2 to
  4, and the long-exposure MTF describes the same frames summed without registration. With the still
  layer on, the total is the product of that and the still layer's (von Karman, L0 0.25 m) long-exposure
  MTF times the scatter's `0.95 + 0.05 exp(-2 pi 10 f)`; a fit of r0 alone to the total overestimates
  the blur's dynamic share.

### A2. Fried 1978, the probability of a lucky exposure
- **Citation:** D. L. Fried, "Probability of getting a lucky short-exposure image through turbulence",
  JOSA 68(12), 1651-1658 (1978). doi:10.1364/JOSA.68.001651
- **URL:** https://opg.optica.org/abstract.cfm?URI=josa-68-12-1651 (read, abstract)
- **What it does:** probability that the wavefront distortion over the aperture is 1 rad^2 or less,
  `Prob ~ 5.6 exp[-0.1557 (D/r0)^2]` for D/r0 of at least 3.5.
- **Phases:** R4 (selection), R2 (twin validation).
- **Prediction:** at the twin's free-air D/r0 (3.0 at 500 nm, about 2.2 in Red) the formula's lower edge
  already gives more than 0.8, so selection cannot buy much against the free air; what it cannot touch at
  all is the static part. Testable directly on the twin's pupil phase: the share of frames whose free-air
  phase variance over the pupil (the same tilt convention as Fried's) is under 1 rad^2 exceeds 0.8 in Red,
  and falls to near zero once the still screen's phase is added. That is the physical reason the real
  lucky tenth is only 1 % sharper at the limb.

### A3. Korff 1973, the speckle transfer function
- **Citation:** D. Korff, "Analysis of a method for obtaining near-diffraction-limited information in the
  presence of atmospheric turbulence", JOSA 63(8), 971-980 (1973). doi:10.1364/JOSA.63.000971
- **URL:** https://opg.optica.org/josa/abstract.cfm?uri=josa-63-8-971 (metadata; abstract from the index)
- **What it does:** closed forms of the mean power spectrum of short exposures (the speckle transfer
  function) under Kolmogorov turbulence; above the seeing limit it stays finite up to the diffraction
  cutoff.
- **Phases:** R7 (the theory the spectral ratio, A4, is fitted against).
- **Prediction:** see A4; Korff's STF is the model for the denominator.

### A4. von der Lühe 1984, Fried's parameter from a time series of a resolved object (spectral ratio)
- **Citation:** O. von der Lühe, "Estimating Fried's parameter from a time series of an arbitrary resolved
  object imaged through atmospheric turbulence", JOSA A 1(5), 510-519 (1984). doi:10.1364/JOSAA.1.000510
- **URL:** https://opg.optica.org/josaa/abstract.cfm?uri=josaa-1-5-510 (read, abstract)
- **Code, licence:** the method lives on in KISIP (C5), whose code I could not locate.
- **What it does:** estimates r0 from "the ratio of the observed squared modulus of the average Fourier
  transform and the observed average power spectrum" of a burst of an arbitrary extended object, fitted
  against theoretical transfer functions; applied to solar granulation.
- **Needs:** many frames, a noise-bias correction of the power spectrum, the shift convention of the theory.
- **Phases:** R7, as a third, independent probe of the dynamic blur; R2, as a check of the twin's
  calibration that uses none of its five statistics.
- **Prediction on the twin:** computed on limb-registered frames over the band where signal power exceeds
  the (subtracted) noise power by 10x, the fitted r0 recovers the free-air r0 within 15 %, and does not move
  by more than 5 % when the still layer and the scatter are toggled on and off (they cancel, fact 2). On
  the real capture it then measures the free air alone, which the twin's calibration (8.5 cm at 500 nm)
  predicts; a disagreement beyond the seed spread is a missing term in the twin's free air.

### A5. Zeng et al. 2026, continuous exposure-time turbulence synthesis (ET-MTF, ET-Turb)
- **Citation:** J. Zeng, D. Liang, S.-J. Huang, K. Zhan, S. Chen, "Continuous exposure-time modeling for
  realistic atmospheric turbulence synthesis", CVPR 2026, 26678-26687. arXiv:2603.01398
- **URL:** https://arxiv.org/abs/2603.01398 (read); CVF:
  https://openaccess.thecvf.com/content/CVPR2026/html/Zeng_Continuous_Exposure-Time_Modeling_for_Realistic_Atmospheric_Turbulence_Synthesis_CVPR_2026_paper.html
- **Code, licence:** https://github.com/Jun-Wei-Zeng/ET-Turb holds the dataset (Baidu download), no
  simulator code seen, no licence file.
- **What it does:** an exposure-time-dependent MTF that interpolates between Fried's short- and long-exposure
  forms through an effective aperture `D + v_w tau` (wind times exposure), turned into a tilt-invariant PSF
  and a spatially varying blur-width field; 5,083 synthetic videos. Validated on real data only by
  no-reference scores (NIQE, BRISQUE).
- **Needs:** r0, D, wind, exposure time, wavelength.
- **Phases:** R2 (the twin's exposure integration), R7 (a kernel model that carries across captures shot at
  2 ms and 4 ms).
- **Prediction on the twin:** with 22 m/s the twin's 4 ms frames have `v tau = 0.35 D`, not a frozen
  exposure. Rendering the twin at 1, 2, 4 and 8 ms, the tilt-removed mean single-frame MTF should fall
  monotonically with exposure, and the ET-MTF form should track it within 10 % in bands 2 to 4 if its
  effective-aperture argument holds. Caveat: the arXiv HTML rendering lost the equations' fractions, so the
  exact form must be re-derived from the PDF before use.

### A6. HCIPy (Por et al. 2018) and Assémat et al. 2006 infinite phase screens
- **Citation:** E. H. Por, S. Y. Haffert, V. M. Radhakrishnan, D. S. Doelman, M. van Kooten, S. P. Bos,
  "High Contrast Imaging for Python (HCIPy): an open-source adaptive optics and coronagraph simulator",
  Proc. SPIE 10703, 1070342 (2018). doi:10.1117/12.2314407. F. Assémat, R. W. Wilson, E. Gendron,
  "Method for simulating infinitely long and non stationary phase screens with optimized memory storage",
  Opt. Express 14, 988 (2006). doi:10.1364/OE.14.000988
- **URL:** https://doi.org/10.1117/12.2314407 (metadata); code https://github.com/ehpor/hcipy (read)
- **Code, licence:** MIT. `InfiniteAtmosphericLayer` implements Assémat 2006 (an autoregressive extension
  of the screen by rows and columns) and warns in its docstring that it "does not work well with large
  outer scales"; `FiniteAtmosphericLayer`; standard Cn2 profiles (Mauna Kea after Guyon 2005, Las
  Campanas, Keck); Fresnel propagation between layers for scintillation.
- **What it does:** the propagation library ASTRA-SR used for its training PSFs.
- **Phases:** R2 (an independent reference for `EvolvingPhaseScreen`).
- **Prediction:** for the same Cn2, L0 and wind, the twin's screens and HCIPy's `FiniteAtmosphericLayer`
  agree on the phase structure function within 5 % out to D and on the tilt variance per frame within
  10 %; HCIPy's infinite layer disagrees at L0 = 4 m by more (its own warning), so use the finite layer as
  the reference. Scintillation needs no term: Jupiter's 45" extent averages it far below the real
  capture's 0.12 % frame-to-frame flux, which the twin already reproduces without it.

### A7. Mao, Chimitt and Chan 2021, the phase-to-space transform
- **Citation:** Z. Mao, N. Chimitt, S. H. Chan, "Accelerating atmospheric turbulence simulation via learned
  phase-to-space transform", ICCV 2021, 14759-14768. arXiv:2107.11627
- **URL:** https://arxiv.org/abs/2107.11627 (read, abstract)
- **Code, licence:** none linked on the paper page.
- **What it does:** writes a spatially varying turbulent blur as invariant convolutions with learned basis
  functions driven from Zernike phase statistics; 300x to 1000x faster than split-step simulation. Built
  for horizontal (terrestrial) paths; PlaNet (F2) stacks three of them for a vertical path.
- **Phases:** R2 only as a form for the missing term "blur varying over the disk" (ASTRA-SR's mean plus 12
  PCA kernels is the same idea and simpler).
- **Prediction:** not needed for R7/R8 on the current twin, which has one PSF per frame. When R4's
  per-point quality needs spatially varying blur, a mean-plus-PCA kernel field driven by the twin's own
  screens reproduces the per-patch PSFs within the seed spread with 12 components on a 100 px disk.

---

## B. Measuring the blur from the data

### B1. Slanted-edge MTF: ISO 12233 and Burns 2000
- **Citation:** P. D. Burns, "Slanted-edge MTF for digital camera and scanner analysis", Proc. IS&T PICS
  2000, 135-138. ISO 12233:2023 (4th ed., since replaced by ISO 12233:2024), "Photography, electronic
  still picture imaging, resolution and spatial frequency responses".
- **URL:** https://www.imaging.org/common/uploaded%20files/pdfs/Papers/2000/PICS-0-81/1621.pdf (read, full
  text); standard: https://www.iso.org/standard/79169.html (not fetchable, 403; the algorithm changes read
  at https://www.imatest.com/imaging/iso-12233/).
- **Code, licence:** Burns's sfrmat (Matlab); the standard is paywalled.
- **What it does:** locates the edge per row by centroid, fits the edge line, projects every pixel onto the
  edge normal into a 4x oversampled edge spread function, differentiates (FIR), windows (Hamming; Tukey and a
  5th-order polynomial edge in the 2023/2024 revision) and Fourier transforms. Burns quantifies the biases:
  a slope error acts as a sinc "skew MTF" that grows with the number of rows, the discrete derivative
  cascades a sinc, noise biases the high frequencies, and a target whose edge is not a step must be divided
  out ("divided by the input target modulation").
- **Phases:** R7(b), the planetary slanted edge.
- **Prediction on the twin:** a limb is a curved edge, so a radial projection gives every sub-pixel phase for
  free. Two biases must be handled, both measurable on the twin. (1) Jupiter's flattening (0.065): the
  polar radius is about 3.2 px short of the equatorial 49, so projecting onto a CIRCLE spreads the edge by
  up to 1.6 px of blur that is not there; projecting onto R1's spheroid limb brings the edge-location
  scatter under 0.1 px, and the ESF-derived transfer then agrees with the forward fit within 5 % in bands
  2 to 4. (2) The limb is not a step (limb darkening, haze), so the observed ESF spectrum is divided by the
  rendered truth limb's, exactly Burns's "input modulation"; skipping that division reads the planet's
  limb darkening as blur, mostly in bands 3 to 5.

### B2. Wedemeyer-Böhm 2008, Hinode SOT PSFs from a Mercury transit and eclipse limbs
- **Citation:** S. Wedemeyer-Böhm, "Point spread functions for the Solar Optical Telescope onboard Hinode",
  A&A 487, 399-412 (2008). doi:10.1051/0004-6361:200809819; arXiv:0804.4536
- **URL:** https://arxiv.org/abs/0804.4536 (read, abstract; the non-uniqueness finding from the index)
- **What it does:** fits PSFs (the diffraction PSF convolved with a narrow Voigt for stray light) to
  intensity profiles across Mercury's disk and the lunar limb; finds the comparison "does not produce a
  unique solution for the best-fitting PSF", and recommends a range of PSFs rather than one.
- **Phases:** R7(b).
- **Prediction on the twin:** a limb-only fit (inside 1.15 radii) of a core-plus-scatter kernel trades the
  core's width against the scatter's share, with a correlation between the two above 0.9 across seeds;
  adding the halo annuli out to 2.5 radii (which R2 already measures) pins the scatter share within 1 %
  (absolute) and the core's FWHM within 10 % of the true kernel. So R7(b) must fit the limb and the halo
  jointly, never the limb alone.

### B3. Yeo et al. 2014, the SDO/HMI PSF from a Venus transit
- **Citation:** K. L. Yeo, A. Feller, S. K. Solanki, S. Couvidat, S. Danilovic, N. A. Krivova, "Point
  spread function of SDO/HMI and the effects of stray light correction on the apparent properties of solar
  surface phenomena", A&A 561, A22 (2014). doi:10.1051/0004-6361/201322502; arXiv:1310.4972
- **URL:** https://arxiv.org/abs/1310.4972 (read, abstract)
- **What it does:** fits the PSF by matching a model of the Venusian disk on the solar background,
  convolved with a trial PSF, to the observation; the PSF is a sum of five Gaussians whose amplitudes vary
  sinusoidally with azimuth. Deconvolving the stray light raised small-scale intensity and field contrasts
  1.3 to 1.7 times.
- **Phases:** R7 (the "known disk convolved with a trial PSF" is the verified precedent for the plan's
  Galilean-moon probe), R7a (azimuthally modulated kernels for the lopsided shell).
- **Prediction:** (1) On the twin, inverting the 5 % scatter alone raises bands 1 to 3 by 1.053 and band 4
  by 1.03 to 1.05, and brings the halo at 1.15 to 1.6 radii (0.43 and 0.15 ADU in the real stack) to within
  its noise, while the disk's total flux rises by the same 5 %. (2) On 2022-09-03, a Yeo-form kernel fitted
  beyond the limb separates R7a's lopsided shell (the azimuthal m = 1 terms) from the round glow (m = 0); an
  m = 1 term that also fits at the limb itself names coma rather than a reflection, the test R7a asks for.

### B4. Waldmann and von der Lühe 2010, PSF estimation from speckle reconstructions
- **Citation:** T. A. Waldmann, O. von der Lühe, "Point spread function estimation using speckle
  reconstructions of solar surface images", Solar Physics 267, 217-231 (2010).
  doi:10.1007/s11207-010-9637-x
- **URL:** https://doi.org/10.1007/s11207-010-9637-x (metadata; abstract from the index)
- **What it does:** takes a speckle reconstruction as the object and fits a parameterised PSF to each short
  exposure (simulated annealing); reliable instantaneous PSF estimates, robust against noise.
- **Phases:** R7(a) is this with the stack as the object; R4 (frame grading).
- **Prediction on the twin:** with the stack as the object, a per-frame fit of a few low-order Zernikes plus
  a free-air r0 ranks the frames against the twin's recorded per-frame Strehl with a Spearman correlation of
  at least 0.8, where R3 found the Laplacian score to be noise (its lag-1 0.08). If it does, it is R4's
  grader; like R7(a), it cannot see the static part (fact 2).

### B5. Lewis 2020, the Mars edge-rind artefact (the one planetary limb-artefact study found)
- **Citation:** M. Lewis, "Investigating the Mars edge-rind artefact", J. Br. Astron. Assoc. 130(5),
  273-284 (2020); ADS bibcode 2020JBAA..130..273L.
- **URL:** https://britastro.org/journal_contents_ite/investigating-the-mars-edge-rind-artefact (read,
  abstract); the author's extended version https://skyinspector.co.uk/mars-edge-artefact/ (read)
- **What it does:** documents a dark arc just inside the sharp limb, often with a brightening between it
  and the edge; its angular width is constant with planet size, grows with wavelength, falls with aperture,
  and is about 0.6 of the Airy diameter; wavelet sharpening amplifies it. Lewis argues it is primarily
  diffraction, accentuated by processing.
- **Phases:** R3/R8 (the ringing gate), R7.
- **Prediction on the twin:** a non-negative PSF cannot make an edge profile overshoot, so the twin's
  UNSHARPENED stack and the diffraction-limited truth should have a monotone radial profile at the lit limb
  (no rind). Every preset-sharpened stack should show the arc at about 0.6 x 2.44 lambda/D = 0.65" = 1.3 px
  inside the limb at 550 nm (1.5 px in Red) when the sharpening lifts the transfer near the cutoff, and at
  the wavelet layer's own scale when it does not. The twin can thus decide between Lewis's diffraction
  reading and a processing one, and the arc's depth is a planetary ringing metric beside R3's
  undershoot below the sky.

---

## C. Inverting a known or measured kernel

### C1. Wiener filtering with a measured OTF; the review
- **Citation:** J.-L. Starck, E. Pantin, F. Murtagh, "Deconvolution in astronomy: a review", PASP 114,
  1051-1069 (2002). doi:10.1086/342606
- **URL:** https://iopscience.iop.org/article/10.1086/342606 (metadata)
- **What it does:** the standard review: Wiener, Richardson-Lucy, maximum entropy, wavelet-regularised
  methods, and why noise makes all of them hard.
- **Phases:** R7, R8 background.
- **Prediction:** see C3, which is the Wiener form R8 needs.

### C2. Richardson 1972 and Lucy 1974
- **Citation:** W. H. Richardson, "Bayesian-based iterative method of image restoration", JOSA 62(1), 55-59
  (1972), doi:10.1364/JOSA.62.000055. L. B. Lucy, "An iterative technique for the rectification of
  observed distributions", AJ 79, 745-754 (1974), doi:10.1086/111605
- **URL:** https://opg.optica.org/abstract.cfm?URI=josa-62-1-55 (read, abstract); Lucy via
  https://doi.org/10.1086/111605 (metadata)
- **What it does:** the multiplicative iteration that converges to the Poisson maximum-likelihood object;
  keeps positivity and flux; amplifies noise and rings at sharp edges as the iterations grow.
- **Needs:** the PSF, data in counts (Poisson), a non-negative background.
- **Phases:** R7 (the inverse and the oracle), R9 (the unrolled RL).
- **Prediction on the twin:** positivity can only bound the undershoot if the sky is at ZERO, so RL must run
  on offset-subtracted data (the 16.8 ADU level removed) and, to match its noise model, in electrons (x32
  e-/ADU). Run that way, RL at matched band-3 transfer (1.0 of the truth) has a limb undershoot below the
  sky of at most a third of the per-band Wiener's; run on the raw ADU with the offset in, it has no such
  advantage. Its band-1 error has a minimum over iterations, and that iteration count with the TRUE kernel
  is the oracle's.

### C3. Conan et al. 1998, myopic deconvolution with object and PSF power spectra
- **Citation:** J.-M. Conan, L. M. Mugnier, T. Fusco, V. Michau, G. Rousset, "Myopic deconvolution of
  adaptive optics images by use of object and point-spread function power spectra", Appl. Opt. 37(21),
  4614-4622 (1998). doi:10.1364/AO.37.004614
- **URL:** https://opg.optica.org/ao/abstract.cfm?uri=ao-37-21-4614 (read, abstract)
- **What it does:** a regularised (Wiener-type) deconvolution with positivity, the object's local mean and a
  MODEL of its power spectral density, extended to an imperfectly known PSF through the PSF's own mean and
  variance.
- **Needs:** the mean OTF, its variance, the noise level, an object PSD model (typically a power law).
- **Phases:** R8 (this IS the per-band Wiener gain `H / (H^2 + N/S)` with S modelled rather than guessed),
  R7.
- **Prediction on the twin:** a power law fitted to the stack's PSD after dividing by R7's measured |H|^2
  (over bands 3 to 5, where the signal dominates) and extrapolated to bands 1 and 2 gives per-band gains
  within 10 % of the oracle gains computed with the true PSD from T1, and at most 1.0 in any band whose
  split-half S/N is below 1. The presets' 1.5x in band 3 should then come out as roughly 1.0 to 1.2.

### C4. Mugnier, Fusco and Conan 2004, MISTRAL (myopic, edge-preserving)
- **Citation:** L. M. Mugnier, T. Fusco, J.-M. Conan, "MISTRAL: a myopic edge-preserving image restoration
  method, with application to astronomical adaptive-optics-corrected long-exposure images", JOSA A 21(10),
  1841-1854 (2004). doi:10.1364/JOSAA.21.001841. Applied to Io: F. Marchis et al., "High-resolution Keck
  adaptive optics imaging of violent volcanic activity on Io", Icarus 160, 124-131 (2002),
  doi:10.1006/icar.2002.6955 (metadata).
- **URL:** https://opg.optica.org/josaa/abstract.cfm?uri=josaa-21-10-1841 (read, abstract)
- **Code, licence:** not public as far as found.
- **What it does:** Bayesian deconvolution with a mixed photon plus detector noise model and an L1-L2
  (quadratic for small gradients, linear for large) prior suited to "sharp edges and smooth areas", with the
  PSF estimated jointly under soft constraints rather than blindly.
- **Phases:** R7 (an inverse whose prior targets exactly the limb), R8 (the comparison that tells whether a
  linear per-band gain can ever pass the ringing gate).
- **Prediction on the twin:** at matched band-3 transfer, an L1-L2 gradient prior leaves at most half the
  per-band Wiener's limb undershoot, because the limb's large gradient is penalised linearly rather than
  quadratically; in the disk interior (belts, small gradients) both behave alike within 5 % per band.

### C5. Neelamani, Choi and Baraniuk 2004, ForWaRD (Fourier then wavelet shrinkage)
- **Citation:** R. Neelamani, H. Choi, R. G. Baraniuk, "ForWaRD: Fourier-wavelet regularized
  deconvolution for ill-conditioned systems", IEEE Trans. Signal Process. 52(2), 418-433 (2004).
  doi:10.1109/TSP.2003.821103
- **URL:** https://www.ece.rice.edu/dsp/software/ward.shtml (read)
- **Code, licence:** Matlab v1 (1D) and v2 (images), "Copyright 2001 Rice University DSP Group", no open
  licence stated.
- **What it does:** a light Fourier (Wiener-like) shrinkage to handle the deconvolution's coloured noise,
  then wavelet shrinkage for piecewise-smooth structure; "minimal ringing, unlike purely Fourier-based
  Wiener deconvolution".
- **Phases:** R8 (the two-stage alternative to one gain per band).
- **Prediction on the twin:** at matched noise, ForWaRD's two stages beat R8's single per-band gains on band
  1 and 2 error by 10 % or more only if the per-band gains leave visible noise there; ringing at the limb
  is unchanged within 10 % (both are linear at the edge). If neither holds, R8's simpler form is enough.

### C6. The a-trous (undecimated, isotropic) wavelet: Starck, Fadili and Murtagh 2007
- **Citation:** J.-L. Starck, J. Fadili, F. Murtagh, "The undecimated wavelet decomposition and its
  reconstruction", IEEE Trans. Image Process. 16(2), 297-309 (2007). doi:10.1109/TIP.2006.887733
- **URL:** https://doi.org/10.1109/TIP.2006.887733 (metadata)
- **What it does:** the starlet (B3-spline a-trous) transform and its reconstructions; the bands R3 scores.
- **Phases:** R3, R8.
- **Prediction:** per-band gains g_j compose to ONE linear filter, `sum_j g_j (h_(j-1) - h_j)` plus the
  residual. Its step response, convolved with R7's kernel, predicts the limb undershoot before any image
  is touched. For the presets this analytic pre-check should reproduce the measured undershoot (a fifth of
  the disk below the sky) within 20 %; if it does, it is R8's ringing penalty, computed on the gain vector.

### C7. Wavelet sharpening in practice: PlanetarySystemStacker and RegiStax
- **PSS:** R. Hempel, PlanetarySystemStacker, https://github.com/Rolf-Hempel/PlanetarySystemStacker (read,
  source `postproc_editor.py` and `stack_frames.py`). Licence GPL-3.0 (`License/`), plus a Software License
  Agreement document.
  - **What it does:** each sharpening layer Gaussian-blurs the previous layer's output (sigma = radius/3,
    optionally mixed with a bilateral filter), the layer component is the difference, optionally
    Gaussian-denoised, and the output is the most-blurred layer plus the sum of components times each
    layer's "amount" (1 is identity). It is therefore exactly a per-band gain on a Gaussian cascade, the
    same object R8 designs. PSS also drizzles (a `drizzle_factor` in the stacking buffers).
- **RegiStax 6:** C. Berrevoets et al., https://www.astronomie.be/registax/linkedwavelets.html (read).
  Closed-source freeware. The page describes "Gaussian" layers and "linked wavelet layers" (after
  J.-J. Poupeau and E. Rousset), with a sharpen and a denoise slider per layer; it does not state the
  layer scales or step, and says nothing of ringing.
- **Phases:** R8 (presets to beat; a form users can reproduce).
- **Prediction:** R8's derived gains, re-expressed as PSS amounts at radii 1, 2, 4 and 8 px, fall inside
  PSS's slider range, with the finest layer's amount under 1 on the 2022-09-03 capture (band-1 S/N below
  one after the stack), so the result is reproducible in a free GPL tool. RegiStax can only be compared by
  output (the plan's split-half test).

---

## D. Multi-frame, blind and Fourier-domain methods

### D1. MOMFBD: van Noort, Rouppe van der Voort and Löfdahl 2005
- **Citation:** M. van Noort, L. Rouppe van der Voort, M. G. Löfdahl, "Solar image restoration by use of
  multi-frame blind deconvolution with multiple objects and phase diversity", Solar Physics 228, 191-215
  (2005). doi:10.1007/s11207-005-5782-z
- **URL:** https://doi.org/10.1007/s11207-005-5782-z (metadata); https://dubshen.astro.su.se/wiki/index.php?title=MOMFBD (read)
- **Code, licence:** C++; the SST wiki now marks it deprecated in favour of Redux.
- **What it does:** jointly estimates the object(s) and each frame's wavefront (modal) from bursts, with
  phase-diversity channels and several objects sharing wavefronts.
- **Phases:** R7 (the general case of which (a) is the single-kernel one), R9.
- **Prediction:** see D2 (the modern, testable implementation).

### D2. torchmfbd (Asensio Ramos et al. 2025) and the marginal estimator (Asensio Ramos 2026)
- **Citation:** A. Asensio Ramos, C. Díaz Baso, C. Kuckein, S. Esteban Pozuelo, M. G. Löfdahl, "torchmfbd:
  a flexible multi-object multi-frame blind deconvolution code", A&A 703, A269 (2025),
  doi:10.1051/0004-6361/202555530, arXiv:2505.10639. A. Asensio Ramos, "Marginal multi-object multi-frame
  blind deconvolution", arXiv:2605.11980 (May 2026).
- **URL:** https://arxiv.org/abs/2505.10639 and https://arxiv.org/abs/2605.11980 (read, abstract and
  method); code https://github.com/aasensio/torchmfbd (read)
- **Code, licence:** MIT, PyTorch, GPU.
- **What it does:** MAP MFBD with Gaussian weighted likelihood; PSFs from Zernike or KL wavefront modes or a
  learned non-negative (NMF) PSF dictionary; spatial variance by apodised overlapping patches; smoothness or
  IUWT-L1 object priors; given the PSFs, the object has the closed multi-frame Wiener form
  `O = sum_j gamma_j I_j S_j* / (sum_j gamma_j |S_j|^2 + S_n/S_o)`. Demonstrated on 12 to 15 frames. The
  2026 marginal estimator adds a log-determinant term that stops noise being assigned to high-order modes
  and makes the object-PSD hyperparameters tunable automatically.
- **Needs:** per-frame SNR high enough to constrain the modes; many modes for D/r0 of a few.
- **Phases:** R7, R9 (and, via the closed form, R4/R8).
- **Prediction on the twin:** first the ORACLE: the closed-form multi-frame Wiener with the twin's true
  per-frame PSFs over the best tenth beats "stack, then one Wiener" by less than 10 % in bands 1 and 2 on the
  calibrated twin (its dominant blur is common to every frame and cannot be weighted away), and by more
  than 20 % with the still layer switched off. Only if the oracle gap is large is blind torchmfbd worth
  running; at 8-bit, 4 ms per-frame SNR it should need the marginal estimator to keep the high-order modes
  near the twin's true pupil phase.

### D3. Phase diversity: Gonsalves 1982 and Paxman, Schulz and Fienup 1992
- **Citation:** R. A. Gonsalves, "Phase retrieval and diversity in adaptive optics", Opt. Eng. 21(5),
  215829 (1982), doi:10.1117/12.7972989. R. G. Paxman, T. J. Schulz, J. R. Fienup, "Joint estimation of
  object and aberrations by using phase diversity", JOSA A 9(7), 1072 (1992), doi:10.1364/JOSAA.9.001072
- **URL:** https://doi.org/10.1117/12.7972989 (resolves to SPIE; abstract from the index);
  https://doi.org/10.1364/JOSAA.9.001072 (metadata)
- **What it does:** a second image with a known added aberration (usually defocus) makes the joint
  object-and-wavefront estimate well posed.
- **Phases:** none without hardware (a beam splitter and a defocused second camera).
- **Prediction:** not testable on our single-channel captures. Noted only because it is the one way to
  measure a static aberration (the still layer, coma) from the frames themselves without an absolute
  reference.

### D4. Hirsch et al. 2011, online multi-frame blind deconvolution
- **Citation:** M. Hirsch, S. Harmeling, S. Sra, B. Schölkopf, "Online multi-frame blind deconvolution with
  super-resolution and saturation correction", A&A 531, A9 (2011). doi:10.1051/0004-6361/200913955
- **URL:** https://www.aanda.org/articles/aa/full_html/2011/07/aa13955-09/aa13955-09.html (metadata;
  abstract from the index; already a plan source)
- **What it does:** processes frames one at a time, updating the object and each frame's PSF; handles
  super-resolution and saturated pixels.
- **Phases:** R7, R5a.
- **Prediction:** its saturation handling matters only where the 8-bit disk clips; on 2022-09-03 Red the
  disk sits far below 255, so it should change nothing there.

### D5. Fourier-domain lucky imaging: Garrel et al. 2012, Mackay 2013; and Law et al. 2006
- **Citation:** V. Garrel, O. Guyon, P. Baudoz, "A highly efficient lucky imaging algorithm: image synthesis
  based on Fourier amplitude selection", PASP 124, 861-867 (2012), doi:10.1086/667399. C. Mackay,
  "High-efficiency lucky imaging", MNRAS 432, 702-710 (2013), doi:10.1093/mnras/stt507, arXiv:1303.5108.
  N. M. Law, C. D. Mackay, J. E. Baldwin, "Lucky imaging: high angular resolution imaging in the visible
  from the ground", A&A 446, 739-745 (2006), doi:10.1051/0004-6361:20053695
- **URL:** https://iopscience.iop.org/article/10.1086/667399 (metadata; abstract from the index);
  https://arxiv.org/abs/1303.5108 (read)
- **What it does:** select or weight frames per spatial FREQUENCY by the strength of their Fourier
  amplitude instead of per frame; up to 4x Strehl gain on an AO long exposure (Garrel), much higher
  selection percentages (Mackay).
- **Phases:** R4, R7 (the stack as a magnitude-weighted combination, the natural companion of ASTRA-SR's
  magnitude finding).
- **Prediction on the twin:** gains track D2's oracle: under 10 % in bands 1 and 2 on the calibrated twin,
  over 20 % with the still layer off. The ablation doubles as a test of the finding that the residual blur
  is mostly static.

### D6. KISIP: Wöger, von der Lühe and Reardon 2008
- **Citation:** F. Wöger, O. von der Lühe, K. Reardon, "Speckle interferometry with adaptive optics
  corrected solar data", A&A 488, 375-381 (2008). doi:10.1051/0004-6361:200809894
- **URL:** https://www.aanda.org/articles/aa/abs/2008/34/aa09894-08/aa09894-08.html (metadata; A&A blocked,
  abstract from the index)
- **What it does:** speckle reconstruction (amplitude calibrated through the spectral ratio and a
  speckle transfer model, phase by speckle masking) with photometry checked against Hinode.
- **Phases:** R7 (the magnitude half of a classical restoration).
- **Prediction:** a KISIP-style amplitude calibration on our stack restores only the dynamic part (fact 2);
  it must be composed with R7(b)'s static term. On the twin: calibration alone recovers under half of the
  oracle's band-2 and band-3 gain; composed with the limb-and-halo static kernel, at least 80 %.

### D7. Learned MFBD: Asensio Ramos et al. 2018, 2021, 2023
- **Citation:** A. Asensio Ramos, J. de la Cruz Rodríguez, A. Pastor Yabar, "Real-time, multiframe, blind
  deconvolution of solar images", A&A 620, A73 (2018), doi:10.1051/0004-6361/201833648, arXiv:1806.07150.
  A. Asensio Ramos, N. Olspert, "Learning to do multiframe wavefront sensing unsupervised: applications to
  blind deconvolution", A&A 646, A100 (2021), doi:10.1051/0004-6361/202038552, arXiv:2006.01438.
  A. Asensio Ramos, S. Esteban Pozuelo, C. Kuckein, "Accelerating multiframe blind deconvolution via deep
  learning", Solar Physics 298, 91 (2023), doi:10.1007/s11207-023-02185-8, arXiv:2306.12078
- **URL:** the three arXiv pages (read, abstracts); code https://github.com/aasensio/learned_mfbd (MIT),
  https://github.com/aasensio/unsupervisedMFBD (MIT), https://github.com/aasensio/neural-MFBD (Apache-2.0)
- **What it does:** 2018: CNNs trained on MOMFBD outputs correct 7-frame bursts at about 100 images a
  second, keeping photometry. 2021: trained UNSUPERVISED from observations alone through the linear image
  formation model, returning images and instantaneous wavefronts, applied to FastCam stellar data and SST
  solar data. 2023: algorithm unrolling of MFBD, trained unsupervised.
- **Phases:** R9.
- **Prediction:** as with D2, a learned MFBD can at best reach the multi-frame oracle, which the calibrated
  twin predicts is close to single-kernel Wiener; the 2021 unsupervised scheme is the relevant one if R9 is
  ever opened, since it needs no MOMFBD teacher and trains on our own captures.

### D8. TMFS: Xie et al. 2026, frame-shared degradation parameters
- **Citation:** D. Xie, Y. Huang, Y. Xu, H. Ji, "Physically-grounded turbulence mitigation with frame-shared
  degradation parameters", CVPR 2026, 29919-29928.
- **URL:** https://cvpr.thecvf.com/virtual/2026/poster/37244 (read, abstract); paper
  https://openaccess.thecvf.com/content/CVPR2026/html/Xie_Physically-Grounded_Turbulence_Mitigation_with_Frame-Shared_Degradation_Parameters_CVPR_2026_paper.html
- **Code, licence:** none found.
- **What it does:** unsupervised, optimisation-based: the degradation is a frame-SHARED correlation function
  plus per-frame noise maps, a physical inductive bias against overfitting. Terrestrial scenes.
- **Phases:** R7, R9 (conceptually our static-plus-dynamic split).
- **Prediction:** none testable without code; its frame-shared term is the one our fact 2 says a ratio of
  frames cannot estimate, so it must be anchored by an absolute reference to recover the still layer.

---

## E. Drizzle (R5a)

### E1. Fruchter and Hook 2002, Drizzle
- **Citation:** A. S. Fruchter, R. N. Hook, "Drizzle: a method for the linear reconstruction of
  undersampled images", PASP 114, 144 (2002). doi:10.1086/338393; arXiv:astro-ph/9808087
- **URL:** https://iopscience.iop.org/article/10.1086/338393 (read, abstract)
- **Code, licence:** the reference implementation is STScI's `drizzle` (not reviewed here).
- **What it does:** variable-pixel linear reconstruction from dithered undersampled frames; preserves
  photometry and resolution, weights by statistical significance, corrects geometric distortion; output
  noise is correlated between pixels.
- **Needs:** dithers covering sub-pixel phases, registration accurate against the OUTPUT pixel.
- **Phases:** R5a.
- **Prediction on the twin:** the residual warp's transfer (0.64, 0.17 and 0.018 at 0.25, 0.5 and 0.75
  cycles per input px, headline 5) caps what drizzle can win. So on 2022-09-03-like captures, 1.5x drizzle
  gains less than 5 % transfer against T1 at 0.5 to 0.75 cycles/px over a Lanczos-resampled plain stack,
  at matched noise, while the warp remains; with the twin's warp switched off and limb-fit registration
  (0.04 px) it gains wherever the frames' own transfer there exceeds the noise. That is R5a's pre-registered
  rule made quantitative.

### E2. Fruchter 2011, iDrizzle (band-limited reconstruction)
- **Citation:** A. S. Fruchter, "A new method for band-limited imaging with undersampled detectors", PASP
  123, 497-502 (2011). doi:10.1086/659313; arXiv:1102.0292
- **URL:** https://arxiv.org/abs/1102.0292 (read via index and Crossref)
- **What it does:** iterates from a drizzled image towards a band-limited one, removing drizzle's
  high-frequency artefacts and its interpolant-kernel convolution.
- **Phases:** R5a (our truth IS band-limited at D/lambda, so this is the principled target).
- **Prediction:** at 1.5x with limb-fit registration and no warp, iDrizzle's transfer at 0.5 to 1.1 cycles
  per input px exceeds plain drizzle's by the drizzle kernel's own MTF (a few to 20 % across that range)
  at matched noise; with the warp on, neither gains (E1).

### E3. Bayer drizzle (Dave Coffin's suggestion, implemented in DeepSkyStacker)
- **Source:** W.-H. Wang (ASIAA), "Bayer Drizzle in DeepSkyStacker" note, 2011,
  https://group.asiaa.sinica.edu.tw/whwang/old/gallery/random_notes/Bayer_drizzle/index.html (read).
  No peer-reviewed paper found.
- **What it does:** skips the demosaic and drizzles each photosite's colour into the output, so dithered
  frames fill the missing colours with measured values; the note measured stars at 2.4 px against 2.95 px
  with bilinear demosaicing, recommends at least 10 to 20 frames, and warns the result looks noisier.
- **Phases:** R5a.
- **Prediction on the twin (colour captures):** a Bayer plane samples to 0.25 cycles per sensor px, and the
  warp still passes 0.17 to 0.64 over 0.25 to 0.5, so Bayer drizzle to the sensor grid beats AHD-then-stack
  in R and B above 0.25 cycles/px by at least 10 % transfer at matched noise even with the warp on, the
  one drizzle that should pay on this data.

### E4. Swanson et al. 2025, super-resolved imaging with adaptive optics
- **Citation:** R. Swanson, E. Y. H. Lin, M. Lamb, S. Sivanandam, K. N. Kutulakos, "Super resolved imaging
  with adaptive optics", ICCV 2025, 29142-29152. arXiv:2508.04648
- **URL:** https://arxiv.org/abs/2508.04648 (read); project https://www.cs.toronto.edu/~robin/aosr/ (read)
- **Code, licence:** https://github.com/swansonr/AOSR is a placeholder ("code soon", 4 KB, no licence).
- **What it does:** uses the deformable mirror to inject learned sub-pixel diversity into sub-exposures and
  jointly optimises that diversity with the reconstruction; up to 12 dB SNR gain over non-AO
  super-resolution.
- **Phases:** R5a only conceptually: our diversity is the atmosphere's uncontrolled tilt, and our limit is
  registration, not diversity.
- **Prediction:** none testable; it confirms that multi-frame super-resolution needs KNOWN sub-pixel shifts,
  which our 0.6 px warp denies.

---

## F. Learned restoration of planetary images, and the simulations they train on

### F1. ASTRA-SR (Ge, Cui and Liu 2026)
- **Citation:** X. Ge, Z. Cui, S. Liu, "ASTRA-SR: atmospheric seeing and turbulence restoration for
  astronomical image super-resolution", arXiv:2609.26731 (22 Sep 2026; submitted to ICASSP 2027).
- **URL:** https://arxiv.org/abs/2609.26731 and https://arxiv.org/html/2609.26731v1 (read in full)
- **Code, licence:** none released.
- **What it does:** Cassini ISS RAW frames (about 20,000 kept of 400,000) degraded by exposure-averaged PSFs
  from six frozen-flow layers (MASS strengths at 0.5 to 16 km from ESO Paranal, HCIPy split-step Fresnel),
  spatially varying as a mean kernel plus 12 PCA kernels, plus Gaussian noise; a three-stage network
  (noise-suppressed LR, blur-aware LR restoration with a patch-Fourier refiner and residual gain bounded to
  (0.5, 1.5), then spatial and amplitude HR refinement that changes windowed Fourier magnitudes and keeps
  phase). The diagnostic: under full degradation, restoring the Fourier magnitude "toward its clean
  counterpart" lifts PSNR from 31.164 to 34.903 dB, the phase to 31.558; under PSF-only degradation 35.324
  against 31.518; under noise only the two help alike. 0.55 dB PSNR over the best baseline (StarIR); real
  Jupiter and Moon judged by eye, with no telescope stated.
- **Phases:** R8 (the case for magnitude gains), R9 (prior art), R2 (same physics, different calibration).
- **Prediction on the twin (ranked second below):** swap, per a-trous band, the stack's Fourier magnitude
  for T1's (keeping the stack's phase), and separately the phase. If the phase swap improves band 2 or 3
  error by less than 10 %, R8's real per-band gains are sufficient (ASTRA-SR's regime); if by more than
  20 %, the twin's static still screen has put a phase into the transfer that no real gain can remove, and
  R7 must hand R8 a 2D complex kernel measured all around the limb.

### F2. PlaNet (Xia et al., AAAI 2025)
- **Citation:** Y. Xia, C. Zhou, C. Zhu, C. Xu, B. Shi, "PlaNet: learning to mitigate atmospheric
  turbulence in planetary images", AAAI 39(8), 8584-8592 (2025). doi:10.1609/aaai.v39i8.32927
- **URL:** https://ojs.aaai.org/index.php/AAAI/article/view/32927 (read in full)
- **Code, licence:** none found.
- **What it does:** clean images are orthographic snapshots of 27 NASA 3D solar-system models; the
  degradation is three cascaded layers each simulated with Mao et al.'s P2S at its own D/r0 ("vertical
  distance-aware"), plus noise; a shared U-Net backbone per frame with permutation-invariant feature
  aggregation for any number of frames, and a Laplacian edge map of the AVERAGE frame supervising the
  decoder at three scales. Fixed at 12 input frames for comparison; AutoStakkert with its own
  post-processing scores 24.41 dB against PlaNet's 27.78 on their synthetic set. Real tests on a C11 with an
  ASI290.
- **Phases:** R9 (prior art only).
- **Prediction:** none testable (no code). Its comparison gives AutoStakkert 12 frames where it is built for
  thousands, and its training set has no static layer or scatter, the blur that dominates ours; a
  lucky-stack plus measured-kernel Wiener baseline on thousands of frames is the comparison it lacks.

### F3. AstroDiff (Kim et al. 2025)
- **Citation:** J. Kim, Y. Yuan, X. Zhang, X. Wang, S. Chan, "Astrophotography turbulence mitigation via
  generative models", arXiv:2506.02981 (3 Jun 2025).
- **URL:** https://arxiv.org/abs/2506.02981 and https://arxiv.org/html/2506.02981 (read, method)
- **Code, licence:** none released.
- **What it does:** a diffusion prior branch and a restoration branch fused by SGLD; PlanetSYN, 22,512
  images, whose planet part is 2,609 images from astrosurf.com (amateur, already processed) plus Kaggle
  textures, degraded with Chan's group simulator at nine Cn2 values; real Jupiter from a 127 mm Maksutov
  with an ASI462MC; BRISQUE, PSNR, LPIPS.
- **Phases:** R9 (a caution).
- **Prediction:** its targets are amateur-processed images, so a model trained on them learns to
  reproduce preset sharpening. On our twin the same design can be tested without their code: train R9's
  stage once on preset-sharpened targets and once on T1; the first returns band 3 at 1.3x the truth or more
  (the presets' own over-sharpening), the second near 1.0.

### F4. DIPLI (Singh et al., Scientific Reports 2026)
- **Citation:** S. Singh, A. Batsheva, O. Y. Rogov, A. Bouridane, "DIPLI: deep image prior lucky imaging for
  blind astronomical image restoration", Sci. Rep. 16, 17204 (2026). doi:10.1038/s41598-026-47300-4;
  arXiv:2503.15984
- **URL:** https://pmc.ncbi.nlm.nih.gov/articles/PMC13234401/ (read, method); arXiv v3 (read)
- **Code, licence:** no code in the article or on arXiv (data "on request"). The arXiv PAPER is CC BY-NC-SA
  4.0; the plan's note "code CC BY-NC-SA" should read "paper CC BY-NC-SA, no code released".
- **What it does:** deep image prior extended to 7 to 13 frames by a back-projection loss through a
  forward model (downsampling, PSF, TVNet optical flow), with SGLD averaging instead of early stopping; best
  LPIPS and DISTS on synthetic scenes; real videos (640 x 480) judged by Laplacian energy.
- **Phases:** R9 (prior art only).
- **Prediction:** Laplacian energy rewards ringing; on our twin a preset-sharpened stack beats T1 itself on
  Laplacian energy, which disqualifies the metric (R3 already found the Laplacian score is noise per frame).

### F5. FluxFlow (Liu et al. 2026)
- **Citation:** S. Liu, X. Ge, Z. Cui, L. Li, G. Chang, J. Liu, Z. Gu, D. Li, X. Chu, L. Gu, T. Harada,
  "FluxFlow: conservative flow-matching for astronomical image super-resolution", arXiv:2605.03749 (May
  2026).
- **URL:** https://arxiv.org/abs/2605.03749 and https://arxiv.org/html/2605.03749 (read, method)
- **Code, licence:** "will be publicly available"; paper CC BY-NC-SA 4.0.
- **What it does:** pixel-space flow matching on 19,500 real DESI Legacy (ground) and HST ACS (space)
  co-registered pairs; a training-free test-time correction back-projects the residual through a Wiener
  kernel `H* / (|H|^2 + 1/lambda_SNR)` (lambda_SNR = 50) instead of the adjoint, to stop "cumulative PSF
  widening" and hallucinated sources. The PSF is one Gaussian, sigma = 2 px, calibrated by minimising the
  MSE between forward-projected HST and DESI.
- **Phases:** R7 (a third probe on real captures where a near-simultaneous space image exists), R9 (the
  Wiener-regularised data consistency).
- **Prediction on the twin:** a single Gaussian fitted by MSE between (T1 convolved with it) and the stack
  absorbs the halo and comes out wider than the true core; its Wiener then over-boosts bands 1 and 2 and the
  limb undershoot is at least 1.5x that of the core-plus-scatter kernel at matched band-3 transfer.

### F6. Liu et al. 2025, TDR self-supervised denoising (Nature Astronomy)
- **Citation:** T. Liu, Y. Quan, Y. Su, Y. Guo, S. Liu, H. Ji, Q. Hao, Y. Gao, Y. Liu, Y. Wang, W. Sun,
  M. Ding, "Astronomical image denoising by self-supervised deep learning and restoration processes", Nat.
  Astron. 9, 608-615 (2025). doi:10.1038/s41550-025-02484-z; arXiv:2502.16807
- **URL:** https://arxiv.org/abs/2502.16807 (read); code https://github.com/zimugh/Denoising-TDR (read;
  licence file present, GitHub reports NOASSERTION)
- **What it does:** Self2Self trained on one image and applied to others of its kind, then pixels whose
  deviation from the input exceeds a threshold are RESTORED to their input values, a bounded-deviation
  guard; HMI magnetogram noise from about 8 G to 2 G.
- **Phases:** R9 (a safety rule for any learned stage).
- **Prediction on the twin:** clamping a learned stage's output to within k sigma of its input per pixel
  (sigma from T2's split-half noise, k = 3) cuts R3's fabrication metric in band 1 by at least half and
  costs under 5 % of band 2 and 3 transfer.

### F7. Other items in ASTRA-SR's references, followed only far enough to place them
- S. Liu et al., "Denoising the deep sky: physics-based CCD noise formation for astronomical imaging",
  arXiv:2601.23276 (ECCV 2026; CC BY 4.0; read, abstract): shot, PRNU, dark current, readout, cosmic rays
  and hot pixels. Relevant to the twin's unmodelled fixed pattern (R2); no code link.
- Y. Guo et al., "Deeper detection limits in astronomical imaging using self-supervised spatiotemporal
  denoising" (ASTERIS), Science 392, eady9404 (2026), doi:10.1126/science.ady9404, arXiv:2602.17205 (read,
  abstract; CC BY 4.0): transformer denoising across exposures, about 1 mag deeper on JWST/Subaru. A
  frame-level denoiser before stacking is outside R7 to R9 as planned.

---

## G. Ringing metrics

- **Balasubramanian, Iyengar, Reynaud, Beuerman**, "A ringing metric to evaluate the quality of images
  restored using iterative deconvolution algorithms", 18th Int. Conf. Systems Engineering, 483-488 (2005),
  doi:10.1109/ICSENG.2005.12 (metadata; the method, binary morphology around detected edges applied to
  Lucy-Richardson iterations, from the index).
- **Zuo, Ming, Tian**, "Perceptual ringing metric to evaluate the quality of images restored using blind
  deconvolution algorithms", Opt. Eng. 48 (2009), doi:10.1117/1.3095766 (metadata).
- **Lewis 2020** (B5): the only planetary study found; it measures the rind's width, not its depth.
- **Verdict:** no published quantitative ringing metric for planetary sharpening was found. R3's limb
  undershoot below the sky, and B5's arc depth inside the limb, are the planetary pair; C6's analytic
  step-response pre-check predicts both from the gains.

---

## The three methods most worth measuring next

1. **The static/dynamic split of the blur, measured three ways (R7, one day's work on the twin).**
   von der Lühe's spectral ratio (A4) and R7(a)'s lucky-against-stack kernel measure only the DYNAMIC part;
   R7(b)'s joint limb-and-halo forward fit (B1, B2, B3) measures the total. Predictions: the spectral-ratio
   r0 matches the free air within 15 % and ignores the still layer; FWHM(a)/FWHM(b) is under 0.3 on the
   real and the synthetic captures; (b) composed from (a), the still layer and the scatter matches the true
   kernel within 10 %. This rescues R7's pre-registration, which as written fails by construction, and
   cross-checks the twin's free air with a statistic it was not calibrated on.
2. **ASTRA-SR's magnitude/phase oracle swap on the twin's stack, per band (R8, an hour).** It decides whether
   R8's real, isotropic per-band gains can reach the oracle or whether a 2D complex kernel is required
   (F1). Its companion is the multi-frame Wiener oracle with the twin's true per-frame PSFs (D2, D5), which
   bounds everything MFBD or Fourier-domain lucky imaging could add (predicted under 10 % on the calibrated
   twin, over 20 % with the still layer off).
3. **Three regularised inverses at matched band-3 transfer, scored on the limb (R7, R8).** Per-band Wiener
   with Conan's power-law object PSD (C3), Richardson-Lucy on offset-subtracted electrons (C2) and an L1-L2
   edge-preserving MAP (C4), each with the measured core-plus-scatter kernel and against the single-Gaussian
   kernel (F5). Predictions: RL and L1-L2 leave at most a third and a half of Wiener's undershoot; the
   single Gaussian rings 1.5x more; C6's analytic step response predicts every undershoot within 20 %,
   which makes it R8's ringing penalty.

## Summary table

| Work | Verified link | Relevance | Do we use it |
|---|---|---|---|
| Fried 1966, short/long exposure MTF | https://opg.optica.org/abstract.cfm?URI=josa-56-10-1372 | High: R7 dynamic kernel form, twin check | Yes, as the analytic model |
| Fried 1978, lucky probability | https://opg.optica.org/abstract.cfm?URI=josa-68-12-1651 | Medium: R4, twin check | Yes, as a twin check |
| Korff 1973, speckle transfer | https://opg.optica.org/josa/abstract.cfm?uri=josa-63-8-971 | Medium: model for A4 | Yes, with A4 |
| von der Lühe 1984, spectral ratio | https://opg.optica.org/josaa/abstract.cfm?uri=josaa-1-5-510 | High: third R7 probe, dynamic only | Yes, ranked 1 |
| Zeng et al. 2026, ET-MTF / ET-Turb | https://arxiv.org/abs/2603.01398 | Medium: exposure-time kernel | Test on twin; re-derive equation |
| HCIPy (Por 2018), Assémat 2006 | https://github.com/ehpor/hcipy | Medium: twin reference | Yes, validation only (MIT) |
| Mao, Chimitt, Chan 2021, P2S | https://arxiv.org/abs/2107.11627 | Low: spatially varying blur form | No (mean+PCA instead) |
| Burns 2000 / ISO 12233 e-SFR | https://www.imaging.org/common/uploaded%20files/pdfs/Papers/2000/PICS-0-81/1621.pdf | High: R7(b) method and biases | Yes |
| Wedemeyer-Böhm 2008, limb PSFs | https://arxiv.org/abs/0804.4536 | High: edge-fit degeneracy | Yes, fit limb plus halo |
| Yeo et al. 2014, Venus-transit PSF | https://arxiv.org/abs/1310.4972 | High: known-disk probe, stray light | Yes (moons; R7a kernel form) |
| Waldmann & von der Lühe 2010 | https://doi.org/10.1007/s11207-010-9637-x | Medium: per-frame PSF, R4 grader | Test on twin |
| Lewis 2020, Mars edge-rind | https://britastro.org/journal_contents_ite/investigating-the-mars-edge-rind-artefact | Medium: planetary ringing geometry | Yes, arc depth as a metric |
| Starck, Pantin, Murtagh 2002 review | https://iopscience.iop.org/article/10.1086/342606 | Background | Reference |
| Richardson 1972 / Lucy 1974 | https://opg.optica.org/abstract.cfm?URI=josa-62-1-55 | High: R7 inverse and oracle | Yes (offset-subtracted, electrons) |
| Conan et al. 1998, myopic PSD Wiener | https://opg.optica.org/ao/abstract.cfm?uri=ao-37-21-4614 | High: R8's gain formula with object PSD | Yes, ranked 3 |
| Mugnier et al. 2004, MISTRAL | https://opg.optica.org/josaa/abstract.cfm?uri=josaa-21-10-1841 | Medium: edge-preserving prior | Test the L1-L2 prior |
| Neelamani et al. 2004, ForWaRD | https://www.ece.rice.edu/dsp/software/ward.shtml | Medium: two-stage alternative | Test only if R8 leaves noise |
| Starck, Fadili, Murtagh 2007, IUWT | https://doi.org/10.1109/TIP.2006.887733 | High: R3/R8 bands, ringing pre-check | Yes |
| PSS (Hempel), wavelets and drizzle | https://github.com/Rolf-Hempel/PlanetarySystemStacker | Medium: preset form, user settings | Yes, as a comparison (GPL-3, learn only) |
| RegiStax 6 wavelets | https://www.astronomie.be/registax/linkedwavelets.html | Low: closed, undocumented scales | Output comparison only |
| MOMFBD, van Noort et al. 2005 | https://doi.org/10.1007/s11207-005-5782-z | Medium: general R7 case | Via torchmfbd |
| torchmfbd 2025; marginal MFBD 2026 | https://github.com/aasensio/torchmfbd | Medium: oracle closed form, blind MFBD | Oracle yes; blind only if the oracle gap is large (MIT) |
| Phase diversity, Gonsalves 1982 / Paxman 1992 | https://doi.org/10.1364/JOSAA.9.001072 | Low: needs a second channel | No |
| Hirsch et al. 2011, OBD | https://www.aanda.org/articles/aa/full_html/2011/07/aa13955-09/aa13955-09.html | Low-medium | Already a source; no new test |
| Garrel 2012 / Mackay 2013, Fourier lucky imaging | https://arxiv.org/abs/1303.5108 | Medium: tests "static dominates" | Yes, as an ablation |
| Law et al. 2006, lucky imaging | https://doi.org/10.1051/0004-6361:20053695 | Background | Reference |
| KISIP, Wöger et al. 2008 | https://doi.org/10.1051/0004-6361:200809894 | Medium: magnitude calibration | Idea only (no code found) |
| Asensio Ramos 2018/2021/2023, learned MFBD | https://arxiv.org/abs/2006.01438 | Low-medium: R9 if opened | Only if R9 opens (MIT/Apache) |
| TMFS, Xie et al. 2026 | https://cvpr.thecvf.com/virtual/2026/poster/37244 | Low-medium: shared degradation | No (no code) |
| Drizzle, Fruchter & Hook 2002 | https://iopscience.iop.org/article/10.1086/338393 | High: R5a | Yes |
| iDrizzle, Fruchter 2011 | https://arxiv.org/abs/1102.0292 | Medium: band-limited R5a | Test after the warp is solved |
| Bayer drizzle (DSS note) | https://group.asiaa.sinica.edu.tw/whwang/old/gallery/random_notes/Bayer_drizzle/index.html | High: R5a on colour captures | Yes |
| Swanson et al. 2025, AO super-resolution | https://arxiv.org/abs/2508.04648 | Low: needs a DM | No |
| ASTRA-SR 2026 | https://arxiv.org/abs/2609.26731 | High: magnitude finding, R9 prior art | Its diagnostic, ranked 2 |
| PlaNet 2025 | https://ojs.aaai.org/index.php/AAAI/article/view/32927 | Low: 12 frames, no static blur | No |
| AstroDiff 2025 | https://arxiv.org/abs/2506.02981 | Low: processed targets | No; its design flaw tested on our twin |
| DIPLI 2026 | https://pmc.ncbi.nlm.nih.gov/articles/PMC13234401/ | Low | No |
| FluxFlow 2026 | https://arxiv.org/abs/2605.03749 | Medium: Wiener data consistency, reference PSF fit | Its PSF fit as a baseline to beat |
| Liu et al. 2025, TDR | https://arxiv.org/abs/2502.16807 | Medium: bounded-deviation guard | Yes, if R9 opens |
| Balasubramanian 2005; Zuo 2009 ringing metrics | https://doi.org/10.1109/ICSENG.2005.12 | Low: non-planetary | No; R3's undershoot stays |

## Leads I could not verify (not cited above)

- A published use of a Galilean moon's known disk as the PSF probe for Jupiter; none found (Yeo 2014's Venus
  transit is the verified analogue; Marchis 2002 restores Io itself with MISTRAL).
- A quantitative ringing metric for planetary sharpening; none found (section G).
- RegiStax 6's wavelet scales (initial layer, step, Gaussian against dyadic): not stated on the official page.
- The ISO 12233:2023/2024 text itself (paywalled; iso.org refused the fetch). Algorithm changes taken from
  Imatest's page.
- ET-MTF's exact equations (the arXiv HTML lost the fractions); read the PDF before implementing.
- KISIP's code and licence; OBD's (Hirsch 2011) code; MISTRAL's code.
- Whether an OPAL (or other HST) Jupiter epoch lies close enough to 2022-09-03 for a FluxFlow-style reference
  PSF fit on the real capture.
- The Scientific Reports DIPLI article's licence (likely CC BY; not checked).
- ASTERIS code on Zenodo (record 17115038 seen in search results only).
- PlaNet's supplementary material (its simulator validation), not read.
- Abstracts of Balasubramanian 2005, Zuo 2009, Lucy 1974, Law 2006, Paxman 1992, Starck 2002 and 2007:
  metadata verified, content from the index or from standard knowledge of the method only.
- Items seen only in reference lists and not followed: Chimitt and Chan 2020 (Zernike anisoplanatic
  simulation, Opt. Eng. 59, 083101), Chimitt et al. 2022 (dense-field P2S, IEEE TCI 8), Zhang et al. CVPR
  2025 (MambaTM), Mendikoa et al. 2016 PlanetCam (PASP 128, 035002; metadata verified, processing not read).
