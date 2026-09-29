# Theme A: Fourier-domain lucky imaging, frame fusion and speckle methods

> One of three reviews behind [the planetary literature synthesis](../planetary-literature.md), made for `docs/plans/planetary-restoration.md` on 2026-09-30 and kept as written: its "our" and "we" are TianWen, its numbers are R4's and R5's as they stood that day, and each entry says how far its source was read. What it predicted and what was then measured is in the plan.

Literature review for the TianWen planetary restoration (phases R4 frame selection, R5 alignment, R7 blur
measurement, R8 wavelet or Wiener gains). Compiled 2026-09-30. I fetched every item in sections 3 to 6. Section 8
lists works whose metadata I confirmed (Crossref, ADS scan) but whose text I did not read. Section 9 lists leads I
could not verify at all. Nothing is cited from memory alone.

Wherever a statement is my own derivation rather than something the literature says, it is marked **(derived)**.

---

## 0. Summary

- **Two families do per-frequency frame fusion.**
  - The astronomical one is "lucky Fourier" selection: Garrel, Guyon and Baudoz 2012 (ISFAS), Mackay 2013, and the
    Kunming hybrids of 2021 and 2022. For each spatial frequency independently, it averages the complex values of the
    frames with the largest Fourier amplitude.
  - The computer-vision one is Fourier Burst Accumulation (FBA; Delbracio and Sapiro 2015; Anger and Meinhardt-Llopis
    2017; Gilles and Osher 2016 in wavelets). It is a soft version: each frame is weighted per frequency by
    `|smoothed amplitude|^p`, where p = 0 is a plain average and p going to infinity is max selection.
  - Both rest on one assumption: that the frames are registered well enough that averaging complex values does not mix
    phases.
- **Speckle methods take a different route.** They average the power spectrum, which is invariant to shifts. They
  recover the amplitude through a modelled speckle transfer function (Labeyrie 1970, Korff 1973), with r0 taken from
  the spectral ratio (von der Luehe 1984). They recover the phase from the cross-spectrum or the bispectrum (Knox and
  Thompson 1974; Lohmann, Weigelt and Wirnitzer 1983). They run in production on the Sun, an extended object like
  Jupiter (KISIP, DKIST VBI).
- **Our regime is not the one the headline results came from.** The literature's gains (Garrel: about 8x fewer
  frames for the same Strehl; Mackay: the resolution of a 1 % selection from 10 %) come from strongly variable PSFs,
  such as AO residuals or D/r0 of 7 to 30. The calibrated twin is milder:
  - its free air is r0 8.5 cm at 500 nm, so D/r0 is about 3 at 500 nm and about 2.2 in red;
  - it has a still layer at the telescope;
  - its frames vary little (reference-gain spread 0.91 to 1.06 of the median);
  - it carries a residual warp of 0.6 px over 10 px that no coherent method removes.
- **The most useful finding is a reframing (derived).** R4's per-band optimum keeps (5 to 10 % in band 1, fewest
  frames in bands 3 and 4) minimise the error of a raw, unrestored stack. Section 1.4 decomposes R4's own band-1
  numbers:
  - that raw error is mostly transfer deficit, which R8 exists to remove;
  - next comes a structured residual of about 0.24 that does not average down;
  - frame noise at a 5 % keep is only about 0.12.
  After an inverse filter, the optimum keep can move a long way. The matched-filter criterion (weights proportional to
  each frame's transfer) says band 1 wants most frames once it is restored per frequency. FBA's own simulations say the
  same: with short exposures a plain average (p = 0) had the lowest MSE. This is also why Mackay and Garrel (judging
  raw images by Strehl) favour aggressive selection while The Thresher and FBA's MSE analysis favour inclusion.
- **The three measurements most worth making next** (section 5):
  1. Re-score R4's selections, per-band matched weights and an FBA p-sweep **after** an oracle per-frequency Wiener
     and after R8's per-band Wiener.
  2. Compute the **oracle ceiling** of per-frequency selection (FAS and FBA) from the twin's noise-free frames, with
     the warp on and off, as a go or no-go before building it.
  3. The **spectral ratio**, a truth-free statistic. It gives r0 of the varying turbulence, the transfer coherence that
     decides whether speckle reconstruction can ever win, and a warp-immune amplitude probe for R7.

---

## 1. Our regime in numbers

### 1.1 Sampling and the diffraction cutoff

The setup is D = 254 mm, f/4.7, 2.9 um pixels, 0.50"/px (0.497 on 2022-09-03). Cutoff fc = D/lambda, converted to
cycles per pixel:

| Wavelength | fc (cy/px) | Nyquist 0.5 as a fraction of fc |
|---|---|---|
| 450 nm | 1.37 | 0.36 |
| 550 nm | 1.12 | 0.45 |
| 650 nm | 0.95 | 0.53 |
| 850 nm | 0.73 | 0.69 |

**(derived)** Three consequences follow.

- **The data are undersampled about 2x.** Power between 0.5 cy/px and fc aliases into the sampled band.
- **Nothing in the sampled band is free of signal.** The corners of the frequency plane (0.71 cy/px) lie inside the
  cutoff for every filter up to about 900 nm. Reading the noise floor "above the cutoff" is the usual trick in speckle
  interferometry and in R4's debiasing plan, and here it picks up a small signal term. For methods that divide by a
  small transfer (speckle calibration at a 1 to 10 % STF), that term matters. Take the noise from frame differences
  (R2's per-band noise) or from the camera model instead.
- **Aliasing is phase-dependent.** An aliased component's phase depends on the frame's sub-pixel position, so
  per-frequency amplitude selection can select constructive aliasing (prediction A-G4 below).

### 1.2 The twin's seeing (from `docs/plans/planetary-restoration.md`, R2)

- **The varying layer.** Free air has r0 8.5 cm at 500 nm, outer scale 4 m, wind 22 m/s. A still layer sits at the
  telescope (r0 2.7 cm, outer scale 0.25 m), with 5 % scatter and 4 ms exposures. The varying layer's D/r0 is about
  3.0 at 500 nm and about 2.2 at 650 nm. That is below the range of Fried's 1978 formula (valid for D/r0 of 3.5 or
  more), so most frames are "lucky" with respect to the free air. What limits the stack is the static layer, the
  noise and the warp.
- **The pupil slides during an exposure.** At 22 m/s over 4 ms it moves 9 cm, more than the free-air r0 at 500 nm.
  Frames are therefore partly exposure-averaged, not frozen speckle.
- **The frames differ little.** Band 2's reference gain spans 0.91 to 1.06 of its median (p5 to p95) on the twin and
  0.87 to 1.10 on the real capture. Band 1's top 5 % carry about 1.4x the mean transfer (R4: +0.12 on about 0.30).
  **(derived)** For a lognormal spread, that is a log-sd of about 0.17.

### 1.3 Numbers that bound the methods (derived, standard formulas)

Fried's probability of a lucky frame, `P = 5.6 exp(-0.1557 (D/r0)^2)`, as checked in Smith et al. 2009:

| D/r0 | P | One in |
|---|---|---|
| 4 | 0.46 | 2 |
| 5 | 0.11 | 9 |
| 6 | 0.021 | 49 |
| 7 | 0.0027 | 367 |
| 8 | 0.00026 | 3,800 |

- **How far a turbulent transfer stays correlated across the Fourier plane** is about r0/lambda. That is 0.13 cy/px
  for r0 = 3 cm at 550 nm and 0.41 cy/px for the twin's free air. On a 64 px tile that is 8 bins or more, which is why
  Garrel's 5-bin amplitude smoothing is compatible with our tiles.
- **A residual shift jitter of sigma px attenuates a coherent average by `exp(-2 pi^2 sigma^2 f^2)`.** For the residual
  warp of 0.6 px (R5 part 2):

  | f (cy/px) | Factor |
  |---|---|
  | 0.125 | 0.93 |
  | 0.25 | 0.64 |
  | 0.40 | 0.32 |
  | 0.50 | 0.17 |

  Power spectra are invariant to a shift. So this loss hits every coherent method (shift-and-add, FAS, FBA, HDR+
  merging) and not the speckle power spectrum.

### 1.4 What R4's band-1 numbers say about noise versus blur (derived)

R3 and R4 define fidelity per band as `error^2 = (1 - H)^2 + |r|^2 / |truth|^2`, where H is the least-squares gain and
r is the residual orthogonal to the truth (exact for an LS gain). With R4's numbers:

| Stack | H | error | residual^2 |
|---|---|---|---|
| All 3,000 frames | about 0.295 | 0.746 | about 0.060 |
| Reference gain, 5 % (150 frames) | about 0.415 | 0.645 | about 0.074 |

Split the residual into a part that does not average (r_det) and frame noise that averages as 1/k:
`residual^2 = r_det^2 + c/k`. That gives:

- r_det about 0.24;
- random noise about 0.12 at 150 frames and about 0.03 at 3,000.

This assumes r_det does not depend on the selection. It can be checked directly with split halves, which is the T2
idea.

So at the 5 % keep, band 1's raw error^2 (0.42) is 0.34 transfer deficit, about 0.06 structured residual, and only
about 0.015 frame noise. **The raw fidelity is dominated by the blur that R8 is there to undo.** A keep chosen on raw
fidelity is therefore not the keep that minimises error after restoration (sections 4 and 5).

---

## 2. The quantities that decide which method can win

Every one of these can be computed on the twin from the truth, and each has a truth-free twin for the real capture.

| Symbol | Meaning | Truth-free twin |
|---|---|---|
| g_i(b) | frame i's true transfer in band b (R4 already computes it) | R4's reference gain |
| s(b) | spread of g_i(b) over frames (log-sd, or top-5 % over mean) | spread of the reference gain |
| rho(b) | per-frame band SNR^2: `(mean g)^2 x truth band power / noise band power` | noise from frame differences (R2) |
| R(b) | transfer coherence: `abs(mean over frames of the complex transfer)^2 / mean abs(transfer)^2`, band-averaged, after registration | von der Luehe's spectral ratio (3.18) |
| W(f) | coherent attenuation from the residual warp, `exp(-2 pi^2 sigma^2 f^2)` | R5's plain warp statistic |
| A(b) | anisotropy: how much g_i(b) varies with direction within a band (astigmatism) | directional sectors of the reference gain |

---

## 3. The methods

Each entry gives: the citation, the page I fetched to verify it, code and licence, what it does, how it behaves at
low SNR and what it assumes, the phases it touches, and a testable prediction on the twin. "Falsified if" is the
prediction's kill line. Every difference is to be read against the spread over seeds (the project's practice is
dozens of seeds, not three).

TianWen is AGPL-3.0 with an additional permission. BSD-2, MIT, GPL-3.0 and AGPL-3.0 code can be combined with it. Code
under GPL-2.0-only cannot. The project's rule that load-bearing logic is our own code makes these licences a guide to
reading rather than copying.

### 3.1 Fried 1978, the probability of a lucky exposure

- **Citation:** D. L. Fried, "Probability of getting a lucky short-exposure image through turbulence", JOSA 68,
  1651-1658 (1978). DOI 10.1364/JOSA.68.001651.
- **Verified:** https://opg.optica.org/josa/abstract.cfm?uri=josa-68-12-1651 (abstract).
- **Code:** none (analytic).
- **What it does:** A Karhunen-Loeve decomposition of Kolmogorov phase over the aperture, with Monte Carlo
  integration. It gives the probability that the wavefront's mean-square distortion is at most 1 rad^2:
  `P = 5.6 exp(-0.1557 (D/r0)^2)` for D/r0 of 3.5 or more.
- **Low SNR and assumptions:** noise-free, and "lucky" is whole-aperture and isoplanatic. It says nothing about a
  noise-limited choice.
- **Phases:** R4 (which regime the keep fraction lives in).
- **Prediction (D1, a regime diagnostic):**
  - On the calibrated twin (varying D/r0 about 2 to 3), band 1's raw-optimal keep is set by noise against transfer
    bias, not by luck. Changing the free-air r0 by 30 % either way should move it by less than 2x. Doubling the noise
    variance should roughly double the frame count kept, since R4 found that a keep is a count.
  - With the free air made strong (r0 4, 5 and 6 cm at 500 nm, which gives P of about 1 %, 10 % and 34 %), the
    optimal keep should start to track P, falling several-fold from 6 cm to 4 cm.
  - Falsified if the calibrated twin's keep follows P, or if the strong-air twin's keep ignores it.

### 3.2 Law, Mackay and Baldwin 2006

- **Citation:** N. M. Law, C. D. Mackay, J. E. Baldwin, "Lucky imaging: high angular resolution imaging in the visible
  from the ground", A&A 446, 739-745 (2006). DOI 10.1051/0004-6361:20053695.
- **Verified:** https://arxiv.org/abs/astro-ph/0507299 (abstract, journal reference).
- **Code:** none public.
- **What it does:** A 10-night campaign on the 2.56 m NOT in I band. Near-diffraction images in good seeing,
  resolution improved 2.5 to 4 times in poorer seeing, guide stars to I = 16, useful field over 40" across.
- **Low SNR and assumptions:** frames are ranked by the brightest speckle of a reference star. They need a point
  source, which we replace with the planet.
- **Phases:** R4 (per-frame versus per-point selection).
- **Prediction (truth-free, on the real capture):**
  - Law's large lucky field predicts that ranking frames separately on the disk's four quadrants (reference gain,
    band 2) gives rankings that agree between quadrants (Spearman 0.8 or more). If so, per-point selection (#1071)
    has little to offer on this capture.
  - Falsified if the quadrants' rankings correlate below about 0.5. That would be the evidence that per-point
    selection is needed, before any per-point twin exists.

### 3.3 Smith, Bailey, Hough and Lee 2009

- **Citation:** A. Smith, J. Bailey, J. H. Hough, S. Lee, "An investigation of lucky imaging techniques", MNRAS 398,
  2069-2073 (2009). DOI 10.1111/j.1365-2966.2009.15249.x.
- **Verified:** https://arxiv.org/abs/0906.3041 (full text read).
- **Code:** none.
- **What it does:** Empirical selection on bright stars over D/r0 = 3 to 12, with apertures 0.4 to 2.5 m (masks on a
  1 m) and exposures 1 to 640 ms. Selection lifts the Strehl ratio 4 to 6 times, with a slight peak near D/r0 about 7.
  The best results come at exposures of 10 ms or less.
- **Low SNR and assumptions:** bright stars, brightest-pixel ranking.
- **Phases:** R4.
- **Prediction (truth-free, on 2022-09-03 LRGB):**
  - Since r0 grows as lambda^1.2 and every channel lies below D/r0 = 7, the frame-to-frame spread of band 2's
    reference gain should rank B > G > R, and so should the gain from selection in the raw stack.
  - Falsified if R or L shows the widest spread. The spread would then be set by something achromatic (the tube layer,
    the warp or the camera) rather than by the free air.

### 3.4 Roggemann, Stoudt and Welsh 1994, the image-spectrum SNR under frame selection

- **Citation:** M. C. Roggemann, C. A. Stoudt, B. M. Welsh, "Image-spectrum signal-to-noise-ratio improvements by
  statistical frame selection for adaptive-optics imaging through atmospheric turbulence", Optical Engineering 33,
  3254-3264 (1994). DOI 10.1117/12.181250.
- **Verified:** Crossref record with abstract, https://api.crossref.org/works/10.1117/12.181250.
- **Code:** none.
- **What it does:** With AO-compensated short exposures, it compares the per-frequency SNR of the image spectrum when
  processing all frames against a subset chosen by image sharpness. Over "a broad range of practical cases", the
  subset gives the higher image-spectrum SNR.
- **Low SNR and assumptions:** a per-frequency SNR criterion is exactly the right currency for a noise-limited band.
  It assumes selection by a sharpness measure that is itself reliable.
- **Phases:** R4 (a truth-free keep criterion), R8 (noise per band).
- **Prediction (P-R, a truth-free keep chooser):**
  - Per band, compute the image-spectrum SNR of the selected subset's mean spectrum against keep, from the spread over
    frames or split halves. Its maximum should rank candidate keeps as the post-restoration truth score does (5.1),
    with Spearman of 0.8 or more. That is the project's own bar for a truth-free metric.
  - For band 1 on the calibrated twin (log-sd about 0.17), it should rise almost monotonically up to 50 % or more.
  - Falsified if it peaks at 5 to 10 % in band 1. The transfer distribution would then be heavier-tailed than the
    lognormal the numbers suggest.

### 3.5 Popowicz, Radlak, Bernacki and Orlov 2017, image-quality measures for solar frames

- **Citation:** A. Popowicz et al., "Review of image quality measures for solar imaging", Solar Physics 292, 187
  (2017).
- **Verified:** https://arxiv.org/abs/1709.09458 (full text read).
- **Code:** none.
- **What it does:** Ranks 36 frame-quality measures on simulated solar frames degraded with Random Wave Vector PSFs,
  over D/r0 = 1 to 10. It recommends three:
  - Helmli and Scherer's mean, in any conditions;
  - Median Filter Gradient Similarity (MFGS), best at D/r0 below 4;
  - the DCT energy ratio, for strongly blurred frames.
  The rms-contrast measure did poorly. The Laplacian family did well on average.
- **Low SNR and assumptions:** the simulation added scintillation but no detector noise. That is why the Laplacian
  family did well there and near chance in R4's 8-bit frames.
- **Phases:** R4.
- **Prediction:**
  - On 8-bit twin frames, MFGS (median-filtered gradients, mid-band by construction) should rank the true band-2
    transfer at +0.8 or more, like the Sobel gradient.
  - Helmli and Scherer's mean (a 3x3 local ratio, fine-scale) should rank near the Laplacian, +0.1 to +0.3.
  - Falsified if Helmli and Scherer's mean reaches +0.8 at 8 bits.

### 3.6 Garrel, Guyon and Baudoz 2012, ISFAS (Fourier amplitude selection)

- **Citation:** V. Garrel, O. Guyon, P. Baudoz, "A highly efficient lucky imaging algorithm: image synthesis based on
  Fourier amplitude selection", PASP 124, 861-867 (2012). DOI 10.1086/667399.
- **Verified:** https://iopscience.iop.org/article/10.1086/667399 (abstract, method and conclusion read); Crossref.
- **Code:** none found.
- **What it does:**
  - Each short exposure is recentred on its brightest speckle, with 4x Fourier zoom by zero-padding.
  - Each frame is Fourier transformed. For every spatial frequency independently, the frames with the highest Fourier
    amplitude are selected (1 %, 2 %, 5 %, 10 % and more), and their complex values, phase included, are averaged.
  - The inverse transform gives the image.
  - Simulated on an AO-corrected Subaru 8 m at 650 nm: 9,000 frames, 0.6" seeing, 250 nm RMS residual.
  - At equal Strehl it needs far fewer frames discarded: a 15.7 % Strehl from 8.7 % of the frames against 1 % for
    classic selection, and "up to eight times" less exposure for a given SNR at a 15 % Strehl.
- **Low SNR and assumptions:**
  - To keep photon noise from driving the selection, the amplitude is convolved with a small Gaussian before selecting
    (5 bins, FWHM 2 bins). The reason given: "the observed attenuation of MTF is very spatially correlated in the
    Fourier plane ... while the photon noise is almost uncorrelated". The smoothing affects only the selection, not the
    synthesis.
  - The authors say the relative importance of the two noises per frame is "difficult to determine" and give no
    residual-bias analysis.
  - It needs a PSF core to recentre on (registration), and it assumes isoplanatism over the transformed area.
- **Phases:** R4, and R8 (the stack's transfer becomes a per-frequency mixture).
- **Predictions:**
  - **A-G1 (the oracle ceiling first).** Render the twin's frames without noise. Per band, compute H for (a) the top-q
    frames by true transfer and (b) per-frequency top-q by true amplitude, then the complex average. Only (b) minus
    (a) is available to any per-frequency selector.
    - The free air's transfer stays correlated over about 0.4 cy/px, wider than band 1 (0.25 to 0.5). So within a band
      the per-frequency transfers move together, and per-frequency selection can only exploit direction (astigmatism).
    - I expect the ceiling gain to be small on the calibrated twin: under about 15 % in H in band 1 and under 5 % in
      band 2.
    - If it is under 5 % in every band, do not build FAS for this capture.
  - **A-G2 (Garrel's regime is the coarse bands, not the finest).** With noise, using Garrel's smoothing, FAS should
    recover most of its ceiling in bands 2 to 4, where per-frame SNR is high, and little in band 1, where per-frame
    SNR is below 1. Falsified if FAS gains most in band 1.
  - **A-G3 (selection bias without smoothing).** Unsmoothed FAS in band 1 should raise the LS gain on the truth above
    frame-level selection at the same q, while the residual rises more, so the fidelity error does not fall. This is
    the winner's curse on `abs(S + N)`: conditioning on a large amplitude aligns the noise with the signal. Smoothing
    should shrink the gap. Falsified if unsmoothed FAS lowers band-1 fidelity error below R4's whole-frame oracle
    (0.640).
  - **A-G4 (aliasing).** Render the twin at the real pixel scale and 2x oversampled, with the same turbulence. If
    FAS's advantage over frame selection in the upper half of band 1 (0.35 to 0.5 cy/px) differs between the two
    renders by more than the seed spread, FAS is partly selecting aliasing. If they agree, aliasing bias is negligible.

### 3.7 Mackay 2013, "Lucky Fourier" in practice

- **Citation:** C. Mackay, "High-efficiency lucky imaging", MNRAS 432, 702-710 (2013). DOI 10.1093/mnras/stt507.
- **Verified:** https://arxiv.org/abs/1303.5108 (full text read).
- **Code:** none public (Matlab, per the paper).
- **What it does:** Implements Garrel's method on photon-counting EMCCD data from the NOT (21 Hz) and Palomar with
  PALMAO. The pipeline:
  - 4x drizzle interpolation;
  - recentring on the reference star;
  - a 32 px linear edge taper;
  - double-precision FFT.
  A hybrid is its key design choice. The per-frequency selection is made only inside a central low-frequency patch,
  1/8 to 1/16 of the Fourier plane, blended over 32 px. Outside it, the order is the classic image-space selection.
  The reason given is that the image geometry and isoplanatic patch are set by the whole frame, and the worst frames'
  high frequencies would compromise it. Results:
  - Lucky Fourier at 10 % matches classic selection at 1 %.
  - At better than 50 %, it matches classic at 10 %.
  - Lucky Fourier images show higher background noise, attributed to the simple apodisation.
  - Averaging the recentring positions over 3 frames helps faint reference stars, since tip-tilt stays correlated for
    about 7 to 8 frames at 21 Hz.
  - Averaging the sharpness score over frames would hurt, since its autocorrelation drops fast.
- **Low SNR and assumptions:** a bright reference star. Fourier-plane selection costs background noise. The
  isoplanatic geometry is protected only by restricting the selection to low frequencies.
- **Phases:** R4, R5 (the pooled positions are Mackay's registration averaging).
- **Predictions:**
  - **A-M1.** Mackay's split applied here means FAS in bands 3 and 4 only. R4 shows those bands move by less than
    0.01 anywhere below a 20 % keep. So the predicted change is under 0.01: nothing to gain. The split is the wrong way
    round for a noise-limited finest band. Falsified if bands 3 and 4 improve by more than 0.01.
  - **A-M2.** On a twin whose quality coherence matches the real capture's (lag 1 about 0.94, #1071), smoothing the
    reference gain over ±2 frames before ranking should raise its Spearman against the true band-1 transfer, because it
    averages the score's noise. On the current twin (lag 1 0.31) it should lower it. Mackay's warning holds only where
    the quality decorrelates within a frame or two. Falsified if smoothing lowers band-1 Spearman on the real-coherence
    twin.

### 3.8 Wang, Li and Zhang 2021 (a hybrid of the two rates) and Huang et al. 2021 (real-time)

- **Citations:**
  - J.-L. Wang, B.-H. Li, X.-L. Zhang, "A novel hybrid algorithm for lucky imaging", RAA 21, 118 (2021). DOI
    10.1088/1674-4527/21/5/118.
  - Huang et al., "A real-time lucky imaging algorithm based on Fourier transform and its implementation techniques",
    PASJ 73, 1240-1254 (2021). DOI 10.1093/pasj/psab070.
- **Verified:** https://arxiv.org/abs/2012.05480 (full text read); Crossref record with abstract for the PASJ paper.
- **Code:** none public.
- **What they do:**
  - Wang et al. sweep a grid of space-domain (whole-frame) and frequency-domain selection rates, then fuse the grid's
    results again by maximum amplitude per frequency. Each classic algorithm is the limit case of the hybrid. They also
    give a memory-light running top-N store.
  - Huang et al. run frequency-domain selection on an FPGA. They select on `re^2 + im^2` instead of the amplitude
    (the same ranking) and show it is superior in quality to real-time spatial selection.
  - Both are demonstrated on binary stars.
- **Low SNR and assumptions:** as Garrel. The final max-amplitude fusion across the grid is another max selection,
  exposed to the same winner's curse.
- **Phases:** R4.
- **Prediction:** On the calibrated twin (a narrow spread), the hybrid grid should equal the better of its parents
  within the seed spread. The second max-fusion step should add band-1 residual without adding H (as A-G3). Low
  priority. Falsified if the hybrid beats both parents in any band by more than the seed spread.

### 3.9 Fan, Li, Li and Zhang 2022, lucky imaging in wavelets

- **Citation:** W. Fan, B. Li, J. Li, X. Zhang, "Lucky imaging method based on wavelet analysis", MNRAS 516,
  2196-2203 (2022). DOI 10.1093/mnras/stac2303.
- **Verified:** https://academic.oup.com/mnras/article/516/2/2196/6673431, and the Crossref abstract.
- **Code:** none found.
- **What it does:** A bior3.7 decomposition into four subbands.
  - Low-frequency coefficients with larger values are selected and fused at a given ratio.
  - For the three high-frequency subbands, whole subbands with the smaller coefficient sums are selected, the stated
    aim being to keep signal and avoid noise.
  - Tested on 10,000 frames of two binary stars from a 2.4 m telescope, with a Gaussian filter against photon noise.
- **Low SNR and assumptions:** the rule for the high subbands is an admission that the largest fine-scale energy is
  noise. That is the same finding as R4's Laplacian result.
- **Phases:** R4, R8.
- **Prediction:** On 8-bit twin frames, ranking by the smallest band-1 energy should also sit near chance
  (|Spearman| under 0.15 against the true band-1 transfer). At 8 bits neither the largest nor the smallest fine-band
  energy identifies good frames. Falsified if it ranks above 0.5, which would point to a transparency or level effect
  in the twin.

### 3.10 Delbracio and Sapiro 2015, Fourier Burst Accumulation (FBA)

- **Citations:**
  - M. Delbracio, G. Sapiro, "Burst deblurring: removing camera shake through Fourier burst accumulation", CVPR 2015,
    2385-2393. DOI 10.1109/CVPR.2015.7298852.
  - The journal version: "Removing camera shake via weighted Fourier burst accumulation", IEEE TIP 24, 3293-3307
    (2015). DOI 10.1109/TIP.2015.2442914.
- **Verified:** CVPR at https://scholars.duke.edu/publication/1123186 (abstract); TIP at
  https://arxiv.org/abs/1505.02731 (full text read, https://arxiv.org/pdf/1505.02731).
- **Code:** the reviewed reimplementation is 3.11's (BSD-2-Clause). The original authors' results page cited by IPOL
  was not fetched.
- **What it does:**
  - `u_p = F^-1( sum_i w_i(zeta) v_i(zeta) )` with `w_i = abs(G_sigma v_i)^p / sum_j abs(G_sigma v_j)^p`.
  - The Gaussian smoothing G_sigma acts on the magnitudes only, with sigma = min(size)/50.
  - p = 0 is the arithmetic mean; p going to infinity takes the per-frequency maximum.
  - The result is `u * k_FBA + noise`, with an equivalent kernel `k_FBA = F^-1( sum_i w_i k_hat_i )`. That kernel is
    closer to a Dirac only if the least-attenuated frequencies carry little phase error. The authors note this holds
    for camera shake, whose kernel spectra are mostly positive and real.
  - Colour uses one set of weights from the channel-averaged magnitude.
  - Registration is SIFT plus ORSA, fitting a homography.
  - A final step denoises with NL-Bayes, sharpens with a Gaussian, and adds back part of the removed detail.
- **Low SNR and assumptions:** This is the only work here with a bias and variance analysis over noise, burst size
  and misregistration.
  - The larger p is, the smaller the bias and the larger the variance, and noise raises both.
  - The MSE minimum lies at p of 7 to 30 for their motion blur.
  - With short exposures (images not blurred), the arithmetic average (p = 0) gave the best MSE.
  - Misregistration of 2 px or more (their epsilon) makes low p best. Around 1 px, the best p is 7 to 20.
  - The weights depend only on magnitude and are insensitive to misalignment, but the average is not: phases must be
    aligned.
- **Phases:** R4 (soft per-frequency selection), R5 (the alignment it requires), R8 (the stack's k_FBA is what R8
  must invert).
- **Predictions:**
  - **A-F1 (a consistency check).** On raw fidelity, the best p per band should fall from coarse to fine. At that p,
    the effective frame count `N_eff = 1 / sum(w^2)` per band should lie within 2x of R4's raw-optimal counts: 150 to
    300 in band 1, 10 to 60 in band 2, and the fewest in bands 3 and 4. Falsified if the best-scoring p gives an
    N_eff far outside that and still wins. That would mean the per-frequency weighting found something the per-band
    ranking cannot see.
  - **A-F2 (after restoration).** Scored after an oracle per-frequency Wiener (5.1), the best p in band 1 should drop
    to 0 to 2, near a plain or matched average, as in Delbracio's own short-exposure case. Falsified if a large p (7 or
    more) stays best after restoration.
  - **A-F3 (the weights are blind to phase).** With the calibrated warp on, FBA's band-1 advantage over frame selection
    should shrink, compared with the warp off, roughly by `W(f)^2` in the band's upper half (0.1 at 0.4 cy/px). FBA's
    weights cannot reject a frame whose content is displaced. Falsified if FBA's band-1 gain is the same with and
    without the warp.
  - **A-F4 (a coherent variant, derived, not from the literature).** Take weights from each frame's smoothed
    per-frequency coherent gain on the all-frame stack, `max(0, Re(Y_i conj(Ybar)))^p`: the per-frequency analogue of
    R4's reference gain, the best frame ranker at +0.99. This should beat the magnitude-only FBA at the same p in band 1
    wherever registration is imperfect. Falsified if it does not, on the calibrated-warp twin.

### 3.11 Delbracio and Sapiro 2015 (TCI) and Anger and Meinhardt-Llopis 2017 (IPOL), local FBA

- **Citations:**
  - M. Delbracio, G. Sapiro, "Hand-held video deblurring via efficient Fourier aggregation", IEEE TCI 1, 270-283
    (2015). DOI 10.1109/TCI.2015.2501245.
  - J. Anger, E. Meinhardt-Llopis, "Implementation of Local Fourier Burst Accumulation for Video Deblurring", IPOL 7,
    56-64 (2017). DOI 10.5201/ipol.2017.197.
- **Verified:** https://arxiv.org/abs/1509.05251 (abstract);
  https://www.ipol.im/pub/art/2017/197/article_lr.pdf (full text read); https://github.com/kidanger/fba-ipol.
- **Code:** fba-ipol, in C, **BSD-2-Clause**. The IPOL article is CC-BY-NC-SA.
- **What it does:**
  - FBA runs on overlapping W x W tiles: W = 128, overlap 0.5, symmetric padding, and a mean over the overlapping
    tiles.
  - Frames come from a sliding window of 2M + 1 (M = 3).
  - Each frame is registered to the centre frame by TV-L1 optical flow computed on frames **downsampled by 1/3**. A
    forward and backward consistency map falls back to the reference where the flow disagrees.
  - Defaults are p = 11 and sigma = 50/W, with 1 to 4 iterations.
  - Two findings matter for us:
    - Flow computed at full resolution between randomly blurred frames "would likely result in the compensation of the
      blur", so registration transfers the blur.
    - On a stationary synthetic sequence, "the registration degrades the output".
  - p = 11 is recommended "if the noise level is low".
- **Low SNR and assumptions:** the tiles must be large enough for the frequency plane and small enough to be
  isoplanatic. The flow must not fit the blur, or here the noise.
- **Phases:** R5 (registration that must not fit the blur), R4 and R8 (local weights).
- **Predictions:**
  - **A-L1 (a harness check).** On the current single-PSF twin, tiled FBA (32 or 64 px tiles, half overlap) must equal
    global FBA within the seed spread in every band. Any difference is a windowing or overlap artefact to fix before
    #1071's per-point twin can be read.
  - **A-L2 (the flow must not fit noise).** On the warp-free twin, any dewarp or flow applied before a fusion must not
    lower band-1 transfer against the globally registered stack. R5 part 2's small cost from pooling with no warp is the
    same effect. A future finer dewarp should be estimated on frames limited to band 2 and coarser. Falsified if a
    full-band fine dewarp raises band-1 transfer on the warp-free twin.

### 3.12 Gilles and Osher 2016, Wavelet Burst Accumulation

- **Citation:** J. Gilles, S. Osher, "Wavelet burst accumulation for turbulence mitigation", J. Electronic Imaging 25
  (3), 033003 (2016). DOI 10.1117/1.JEI.25.3.033003.
- **Verified:** https://arxiv.org/abs/2410.22802 (full text read; the arXiv posting of 2024 carries the 2016 journal
  reference).
- **Code:** none found.
- **What it does:** It extends FBA to turbulence.
  - Rigid registration is replaced by non-rigid registration: multiscale Lucas-Kanade flow of every frame to the
    temporal mean, inverted with bilinear warps.
  - It generalises FBA to wavelets in two ways:
    - **WWBA** weights every wavelet coefficient in every subband by its own smoothed magnitude^p;
    - **WWFBA** runs FBA on each subband's sequence.
  - A sparsity-constrained variant replaces the weights.
  - Their Section 5 shows WWFBA has an equivalent kernel (Proposition 2) but WWBA does not, because pointwise
    multiplication and convolution do not commute.
- **Low SNR and assumptions:** the weights on fine-scale coefficients are noise-driven without smoothing. It assumes
  the flow to the mean removes the geometric distortion.
- **Phases:** R4, R5, R7 and R8 (whether the stack has a kernel).
- **Predictions:**
  - **A-W1.** On the single-PSF twin, WWBA with magnitude smoothing at or above the band's scale should equal per-band
    FBA within the seed spread. There is no spatial variation to exploit. Unsmoothed WWBA should be worse than
    whole-frame selection in band 1. Falsified if unsmoothed WWBA beats frame selection in band 1.
  - **A-W2 (a consequence for R7).** At equal H, a WWBA stack should leave a larger residual after R7's kernel fit
    than a WWFBA or FBA stack, since WWBA's output is not a convolution of the truth. Falsified if the residuals match.

### 3.13 Hasinoff et al. 2016 (HDR+) and Monod, Delon and Veit 2021, a per-frequency merge anchored to a reference

- **Citations:**
  - S. W. Hasinoff et al., "Burst photography for high dynamic range and low-light imaging on mobile cameras", ACM
    TOG 35 (2016). DOI 10.1145/2980179.2980254.
  - A. Monod, J. Delon, T. Veit, "An Analysis and Implementation of the HDR+ Burst Denoising Method", IPOL 11, 142-169
    (2021). DOI 10.5201/ipol.2021.336.
- **Verified:** the Crossref abstract (Hasinoff); https://www.ipol.im/pub/art/2021/336/article_lr.pdf (full text
  read); https://github.com/amonod/hdrplus-python.
- **Code:** hdrplus-python, **AGPL-3.0**.
- **What it does:**
  - Tile-based alignment on a pyramid (8 x 8 tiles at the coarsest level, 16 x 16 at the others).
  - Each alternate tile's DFT is then merged with the reference's per frequency:
    `T0~(w) = (1/N) sum_z [ (1 - A_z) T_z(w) + A_z T_0(w) ]`, with `A_z = abs(D_z)^2 / (abs(D_z)^2 + c sigma^2)` and
    `D_z = T0 - Tz`.
  - It is built for low-light raw bursts, so low SNR by design.
  - The IPOL analysis states that the merge "does not introduce additional blur from other images, but it does not
    remove blur either".
  - As tau goes to 0 it returns the reference; as tau goes to infinity, a plain average.
- **Low SNR and assumptions:** it needs a good noise model per tile (sigma^2 from the tile's RMS), and it trusts the
  reference to be sharp.
- **Phases:** R4 and R8 (an SNR-switched frame count per frequency).
- **Predictions (A-H1, derived):**
  - Take the reference to be R4's best-5 % stack. In band 1 the per-frame differences are noise-dominated, so A_z is
    about 0 and nearly every frame merges. H falls toward the all-frame 0.295 while the noise falls.
  - Judged raw, band-1 error lands between R4's 5 % and all-frame values.
  - Judged after an oracle per-frequency Wiener, it should approach 5.1's all-frame result.
  - In bands 3 and 4 A_z is about 1, so it falls back to the reference and changes nothing.
  - Raw per-band errors should therefore land within 0.01 of R4's per-band optimum in bands 2 to 4. In effect it
    reproduces R4's per-band keep automatically.
  - Falsified if it beats R4's per-band optimum in bands 2 to 4 by more than 0.01.

### 3.14 Vorontsov and Carhart 2001; Aubailly, Vorontsov, Carhart and Valley 2009 (lucky-region fusion)

- **Citations:**
  - M. A. Vorontsov, G. W. Carhart, "Anisoplanatic imaging through turbulent media: image recovery by local
    information fusion from a set of short-exposure images", JOSA A 18, 1312-1324 (2001). DOI 10.1364/JOSAA.18.001312.
  - M. Aubailly, M. A. Vorontsov, G. W. Carhart, M. T. Valley, "Automated video enhancement from a stream of
    atmospherically-distorted images: the lucky-region fusion approach", Proc. SPIE 7463, 74630C (2009). DOI
    10.1117/12.828332.
  - A companion, "Video Enhancement through Automated Lucky Region Fusion ...", COSI 2009, CThC3. DOI
    10.1364/COSI.2009.CThC3.
  - The patent: US 8,611,691 B2, "Automated video data fusion method".
- **Verified:**
  - https://opg.optica.org/josaa/abstract.cfm?uri=josaa-18-6-1312 (abstract);
  - the SPIE abstract via the Semantic Scholar API (DOI record);
  - https://opg.optica.org/abstract.cfm?uri=COSI-2009-CThC3 (abstract);
  - https://patents.google.com/patent/US8611691 (claims and method read).
- **Code:** none public. Google Patents lists the patent as expired for non-payment of fees.
- **What it does:**
  - An image-quality map per frame, `M(r) = integral J(r') G(r - r', a) dr'`, with `G = exp(-r^2/a^2)`. J can be an edge
    detector, contrast, Tenengrad or intensity squared.
  - Each frame's map is compared with the running fused image's. A gain field Delta blends the regions where the new
    frame is better: `I_F(n+1) = (1 - Delta) I_F(n) + Delta I(n)`.
  - A global test keeps an update only if the global quality improved.
  - The 2009 work selects the kernel width a (the critical fusion parameter) automatically from the source images.
- **Low SNR and assumptions:** a recursive keep-the-better rule is a max selection. At 8 bits the kernel width a
  controls how much noise enters the score. It assumes the frames are pre-registered, or a still scene.
- **Phases:** R4 (per-point selection), R5.
- **Predictions:**
  - **A-R1.** TianWen's stack already weights pixels by `FrameSharpnessMap`, a smoothed Sobel energy, which is lucky
    regions without the recursion. On the single-PSF twin, lucky-region fusion should equal whole-frame selection
    (the plan's own point).
  - **A-R2 (the winner's curse).** Feed lucky-region fusion a twin whose turbulence does not vary (identical PSFs,
    noise only). The fused image's global quality should stay flat. If it rises steadily with frames, the max rule is
    accumulating noise peaks, and it should not be used at 8 bits without a noise-aware threshold. Falsified, meaning
    safe, if it stays flat within the seed spread.

### 3.15 Surveys and related fusion work (read in part)

- **S. H. Chan, N. Chimitt, "Computational Imaging Through Atmospheric Turbulence"**, Foundations and Trends in
  Computer Graphics and Vision 15, 253-508 (2023). DOI 10.1561/0600000103.
  - **Verified:** https://arxiv.org/abs/2411.00338 (sections 5.2.3 and 5.2.4 read).
  - It covers sharpness metrics (intensity variance, gradients, Laplacian), hard against soft fusion,
    `w = exp(alpha ||I||_TV)`, and the geometric-consistency weight of Mao, Chimitt and Chan 2020.
  - Mao et al. 2020 (IEEE TCI 6, 1415-1428, DOI 10.1109/TCI.2020.3029401; metadata verified) down-weights a lucky
    patch that deviates from the temporal-mean reference. Our analogue is R4's reference gain.
- **N. Anantrasirichai, A. Achim, N. G. Kingsbury, D. R. Bull, "Atmospheric turbulence mitigation using complex
  wavelet-based fusion"**, IEEE TIP 22, 2398-2408 (2013). DOI 10.1109/TIP.2013.2249078.
  - The metadata are verified on Crossref. The abstract comes from a search summary: region-level fusion in the
    dual-tree complex wavelet domain, with ROI registration.
  - A repository by the first author, https://github.com/pui-nantheera/atmospheric-turbulence-removal, is
    **GPL-3.0**, in MATLAB.
  - Relevance to R4 and R8 is moderate: selection per wavelet band and per direction is the same idea as A-G1's
    directional ceiling.

### 3.16 Planetary lucky imaging in practice

- **PlanetCam.** J. Mendikoa, A. Sanchez-Lavega, S. Perez-Hoyos, R. Hueso et al., "PlanetCam UPV/EHU: A Two-channel
  Lucky Imaging Camera for Solar System Studies in the Spectral Range 0.38-1.7 um", PASP 128, 035002 (2016). DOI
  10.1088/1538-3873/128/961/035002.
  - **Verified:** https://iopscience.iop.org/article/10.1088/1538-3873/128/961/035002.
  - Its pipeline, PLAYLIST (IDL), ranks whole frames by **Sobel** sharpness, keeps 1, 2, 5, 10, 30 or 100 %, and
    aligns by correlation.
  - It uses 0.05 to 0.1 s exposures and 500 to 1,500 frames on Jupiter, reaching features of 0.25 to 0.30".
  - Professional planetary practice is therefore whole-frame selection with a gradient, which is R4's result.
  - No Fourier fusion.
- **Planetary System Stacker** (R. Hempel), https://github.com/Rolf-Hempel/PlanetarySystemStacker.
  - Python. The GPL-3.0 text is in its License folder, beside a separate licence-agreement document.
  - An automatic alignment-point mesh ranks frames per point by local contrast and blends the stacked patches.
  - No Fourier selection. It is the open analogue of R5 plus per-point R4.
- **S. Zhang, J. Zhao, J. Wang, "An Efficient Lucky Imaging System for Astronomical Image Restoration"**, AMOS 2011
  poster, https://amostech.com/TechnicalPapers/2011/Poster/ZHANG.pdf (abstract read). A 1.23 m telescope, lucky
  imaging of the Moon, Jupiter, Saturn and the ISS. Low relevance.
- **Prediction:** none beyond R4's. These confirm that no planetary pipeline found here does per-frequency fusion, so
  any gain from it on Jupiter would be new.

### 3.17 Labeyrie 1970 and Korff 1973, speckle interferometry and the speckle transfer function

- **Citations:**
  - A. Labeyrie, "Attainment of diffraction limited resolution in large telescopes by Fourier analysing speckle
    patterns in star images", A&A 6, 85 (1970), ADS 1970A&A.....6...85L.
  - D. Korff, "Analysis of a method for obtaining near-diffraction-limited information in the presence of atmospheric
    turbulence", JOSA 63, 971 (1973). DOI 10.1364/JOSA.63.000971.
- **Verified:**
  - Labeyrie's ADS scan, https://articles.adsabs.harvard.edu/pdf/1970A%26A.....6...85L (3 pages, bibcode confirmed).
    The scan's text is not machine-readable; the abstract comes from ADS via search.
  - Korff at https://opg.optica.org/josa/abstract.cfm?uri=josa-63-8-971 (abstract).
- **Code:** see 3.20.
- **What they do:**
  - Labeyrie averages the power spectrum of many short exposures. It keeps object information out to the diffraction
    limit, because the speckle transfer `<abs(T)^2>` stays non-zero where the long-exposure `abs(<T>)^2` has vanished.
    It gives the object's power spectrum (its autocorrelation), not its phase.
  - Korff derives that "Wiener spectrum" of a point source for Kolmogorov turbulence (Rytov), with asymptotes below and
    above the seeing limit. Above it, the spectrum is proportional to the telescope's OTF.
- **Low SNR and assumptions:**
  - The estimator averages every frame and discards none.
  - Its SNR per frequency is limited by the random transfer itself and by additive noise, whose bias must be
    subtracted.
  - It assumes isoplanatism over the transformed area, a stationary object, frozen exposures, and a known STF (a
    reference star, or a model plus r0).
- **Phases:** R7 (a blur probe immune to the warp), R8 (the object's per-band power, the S in N/S).
- **Predictions:**
  - **A-S1 (derived, the crossover).**
    - Per band, a speckle amplitude estimate (the mean power spectrum minus the noise bias, divided by the STF) should
      beat the coherent stack's amplitude, after the Wiener, only where `rho(b) > R(b) W^2`, for rho below 1.
    - Where the inequality fails, the coherent stack wins.
    - On the calibrated twin (a mild varying layer, R near 1 below band 1), the stack should win in bands 2 to 4.
    - The only candidate for speckle is the upper half of band 1, where `W^2 <= 0.14`.
    - Falsified if speckle wins where `rho < R W^2`, or loses where `rho >> R W^2`.
  - **A-S2 (the STF bias).** The twin's pupil slides 9 cm within an exposure (22 m/s x 4 ms), more than r0, so a
    frozen-exposure (Korff) STF overestimates the true high-frequency speckle transfer. An amplitude calibrated with it
    comes out **low** in band 1, so the restoration under-corrects. Measure the ratio of the twin's empirical
    `<abs(T)^2>` (from its PSFs) to Korff's at the same r0: it should be below 1 and falling with f in band 1.
    Falsified if it is at or above 1.
  - **A-S3 (derived, the noise bias).** No signal-free annulus exists above the cutoff (1.1). If the noise variance
    sigma^2 is estimated with a relative error e, the speckle estimate of `abs(S)^2` carries a relative error of about
    e/rho, and the amplitude half of that. With rho(b1) = 0.2 and e = 5 %, band-1 amplitude is wrong by about 12 %.
    The twin can confirm the formula by perturbing sigma^2.

### 3.18 von der Luehe 1984, the spectral ratio

- **Citation:** O. von der Luehe, "Estimating Fried's parameter from a time series of an arbitrary resolved object
  imaged through atmospheric turbulence", JOSA A 1, 510-519 (1984). DOI 10.1364/JOSAA.1.000510. The companion is
  IAU Colloquium 79, 203-220 (1984).
- **Verified:** https://opg.optica.org/josaa/abstract.cfm?uri=josaa-1-5-510 (abstract); the Crossref record of the
  IAU version, with abstract.
- **Code:** inside KISIP-family codes (3.20).
- **What it does:** The ratio `eps(f) = abs(<Y(f)>)^2 / <abs(Y(f))^2>` of the squared mean transform to the mean power
  spectrum is, to first order, independent of the object. Through Fried-Korff theory it estimates r0 from any
  resolved scene (applied to solar granulation). Its behaviour beyond the seeing limit gives the speckle SNR. The
  paper analyses the effect of noise on the ratio.
- **Low SNR and assumptions:** the denominator carries the noise bias (subtract it; see 1.1). The model assumes frozen
  Kolmogorov turbulence.
- **Phases:** R7 (a truth-free r0 per capture and per filter, hence a model long-exposure transfer), R4 and R8
  (R(b) is this ratio), and every go or no-go for the speckle methods.
- **Predictions:**
  - **A-V1 (derived: static blur cancels).** A static aberration T_s multiplies every frame's transform, so it cancels
    between numerator and denominator. On the twin, the r0 fitted from eps should match the **free-air** r0 (8.5 cm at
    500 nm, scaled to the filter), not the combined r0 with the still layer (about 2.5 cm). It should lie within about
    20 %, biased high by the exposure averaging of A-S2.
    - A consequence for R7: the spectral ratio cannot see tube seeing, collimation, or the coma of R7a's hypothesis.
      That part must come from the limb's edge spread function.
    - Falsified if it returns the combined r0.
  - **A-V2 (truth-free on the real 2022-09-03 set).** The fitted r0 should scale close to lambda^1.2 across B, G and R.
    A departure means the ratio is reading something other than Kolmogorov air, such as the warp or the noise bias.

### 3.19 Phase recovery: Knox and Thompson 1974; Lohmann, Weigelt and Wirnitzer 1983; Ayers, Northcott and Dainty 1988

- **Citations:**
  - K. T. Knox, B. J. Thompson, "Recovery of images from atmospherically degraded short-exposure photographs", ApJ 193,
    L45 (1974). DOI 10.1086/181627.
  - A. W. Lohmann, G. Weigelt, B. Wirnitzer, "Speckle masking in astronomy: triple correlation theory and
    applications", Applied Optics 22, 4028-4037 (1983). DOI 10.1364/AO.22.004028.
  - G. R. Ayers, M. J. Northcott, J. C. Dainty, "Knox-Thompson and triple-correlation imaging through atmospheric
    turbulence", JOSA A 5, 963 (1988). DOI 10.1364/JOSAA.5.000963.
- **Verified:**
  - Crossref records for all three.
  - The Knox-Thompson and Lohmann abstracts come from ADS and Optica via search.
  - For Ayers et al., the abstract of the 1987 OSA meeting version (DOI 10.1364/oam.1987.thz3) was read. It says the
    bispectrum's SNR decides which parts of the 4-D bispectrum to compute.
- **Code:** see 3.20.
- **What they do:**
  - **Knox and Thompson** recover the object's Fourier phase from the cross-spectrum `<Y(f) Y*(f + df)>` at small
    offsets df, below r0/lambda.
  - **Speckle masking** averages the bispectrum `<Y(u) Y(v) Y*(u + v)>`. Its phase is that of the object, because
    turbulent and shift phases cancel. It is **exactly invariant to a translation**, so no per-frame registration is
    needed within a subfield.
- **Low SNR and assumptions:** the bispectrum is noisier and needs noise-bias terms. It assumes the subfield is
    isoplanatic, meaning one shift and one PSF per frame over it.
- **Phases:** R5 (it avoids registration), R8 (phases in bands where the stack's are unreliable).
- **Prediction (A-P1, derived):** A bispectrum is invariant to a shift of the whole subfield, not to a warp inside it.
  With the warp correlated over 10 px, a subfield large enough to resolve band 1 (32 px or more) holds about 3 x 3
  independent warp cells. The mean bispectrum then loses the phase relations between cells, as the coherent stack
  does. So on the calibrated-warp twin, bispectrum phases in band 1 should be no better than the stack's. The
  advantage should appear only on a twin whose warp correlation exceeds the subfield (a larger isoplanatic angle, or
  the infrared). Falsified if bispectrum phases from 32 px subfields beat the stack's phase in band 1 on the
  calibrated-warp twin.

### 3.20 Solar speckle reconstruction in production (the extended-object template)

- **The literature, with what I read of each:**
  - O. von der Luehe, "Speckle imaging of solar small scale structure. I. Methods", A&A 268, 374 (1993). The ADS scan
    was downloaded (17 pages, bibcode confirmed), but its text is not machine-readable. Citing literature describes it
    as the Knox-Thompson implementation for photometrically accurate solar maps.
  - P. Mikurda, O. von der Luehe, Solar Physics 235, 31-53 (2006), on the extended Knox-Thompson method. Metadata only.
  - F. Woeger, O. von der Luehe, K. Reardon, "Speckle interferometry with adaptive optics corrected solar data", A&A
    488, 375-381 (2008). DOI 10.1051/0004-6361:200809894. Crossref, plus the abstract via search.
  - F. Woeger, O. von der Luehe, "KISIP: a software package for speckle interferometry of adaptive optics corrected
    solar data", Proc. SPIE 7019, 70191E (2008). DOI 10.1117/12.788062. Metadata only.
  - F. Woeger et al., "The Daniel K. Inouye Solar Telescope (DKIST)/Visible Broadband Imager (VBI)", Solar Physics 296
    (2021). DOI 10.1007/s11207-021-01881-7. The Crossref abstract was read.
- **Code:**
  - I found no public KISIP release.
  - https://github.com/blakeMilner/deblurTC is **GPL-2.0**, in C with MPI: bispectrum reconstruction, "created by
    Friedrich Woeger and Oskar von der Luehe II", with the astrophysics-specific parts removed. GPL-2.0-only cannot
    be combined with AGPL-3.0, so read it and do not copy it.
  - specklepy (https://github.com/felixbosco/specklepy, MIT) implements only shift-and-add and speckle holography,
    which need point sources, so it does not apply.
- **What it does:**
  - The Sun is an extended object with no reference star, like Jupiter. KISIP (C, parallel) reconstructs Fourier
    phases with an extended Knox-Thompson or a triple-correlation scheme.
  - Amplitudes are calibrated with an STF model that includes the partial AO correction, and are field-dependent
    (Woeger and von der Luehe 2007, Applied Optics 46, 8015; metadata verified).
  - Photometric accuracy was checked against simultaneous Hinode/SOT data, and near real-time reconstruction is
    possible (Woeger et al. 2008).
  - DKIST's VBI reaches the diffraction limit "by using adaptive optics in conjunction with post-facto
    image-reconstruction techniques" (Woeger et al. 2021).
- **Low SNR and assumptions:** the Sun is photon-rich. Planetary 8-bit frames are not. Subfields must be isoplanatic,
  and the STF depends on an r0 estimate.
- **Phases:** R7 and R8 (the amplitude calibration route), R5 (subfields).
- **Prediction:** as A-S1 and A-P1. The solar route is worth building only if A-S1 finds bands where
  `rho > R W^2` and A-P1 finds phases the stack does not have.

### 3.21 Use-all-frames alternatives (forward models)

- **The Thresher.** J. A. Hitchcock, D. M. Bramich, D. Foreman-Mackey, D. W. Hogg, M. Hundertmark, "The Thresher:
  Lucky imaging without the waste", MNRAS 511, 5372-5384 (2022). DOI 10.1093/mnras/stac427.
  - **Verified:** https://arxiv.org/abs/2202.04686 (full text scanned); the code at
    https://github.com/jah1994/TheThresher is **MIT**, Python and PyTorch.
  - It is online multi-frame blind deconvolution fitted by SGD against a likelihood, with EMCCD (Poisson-Gamma-Normal)
    and CCD noise models and L1 on the kernel.
  - "Because it uses the full set of images in the stack, The Thresher outperforms TLI in signal-to-noise; as it
    accounts for the individual-frame PSFs, it does this without loss of angular resolution."
  - The authors found non-linearities in flux.
  - It is tested on star fields.
  - **Relevance:** the principled form of 5.1's matched weights. It is heavy, and an extended 8-bit planet with
    anisoplanatic PSFs is untested ground.
- **M. Hirsch, S. Harmeling, S. Sra, B. Schoelkopf, "Online multi-frame blind deconvolution with super-resolution and
  saturation correction"**, A&A 531, A9 (2011). DOI 10.1051/0004-6361/200913955. Crossref plus a search abstract. The
  R7 section of the plan already cites it.
- **K. G. Puschmann, C. Beck, "Application of speckle and (multi-object) multi-frame blind deconvolution techniques
  on imaging and imaging spectropolarimetric data"**, A&A 533, A21 (2011).
  - **Verified:** https://arxiv.org/abs/1107.0703 (abstract).
  - On solar data, speckle gave higher intensity contrast and MOMFBD less noise amplification, with comparable
    resolution. MFBD was more homogeneous at the shortest wavelength.
  - **Prediction (A-T1):** on the twin, restoring band 1 with a speckle-calibrated amplitude should give higher band-1
    transfer but more residual noise than a Wiener with R7's fitted kernel, as Puschmann and Beck found. Falsified if
    it gives both less noise and higher transfer.

### 3.22 Dainty and Greenaway 1979 (photon counting)

- **Citation:** J. C. Dainty, A. H. Greenaway, "Estimation of spatial power spectra in speckle interferometry", JOSA
  69, 786-790 (1979). DOI 10.1364/JOSA.69.000786.
- **Verified:** https://opg.optica.org/josa/abstract.cfm?uri=josa-69-5-786 (abstract).
- **What it says:** In photon-limited speckle (mean photons per frame far below 1), leaving out realisations with fewer
  than two photons raises the SNR by up to about `Nbar^(-1/2)`.
- **Relevance:** low. Our CMOS frames are not photon-counting. It is listed because it is the classic statement that
  discarding frames can raise the SNR, but only in a regime we are not in.

---

## 4. Per-frequency against per-frame selection at low SNR

### 4.1 What the literature says

- **Garrel 2012.** Selection by per-frequency amplitude works when smoothing makes the selection statistic follow the
  MTF rather than the noise. The quantitative results are for bright AO targets. The residual noise bias is not
  analysed.
- **Mackay 2013.** In practice, the Fourier-selected images carried more background noise, and he limited the
  per-frequency selection to low frequencies to protect the geometry.
- **Delbracio and Sapiro 2015 (TIP).**
  - The exponent p trades bias against variance, and noise worsens both.
  - With short exposures (little blur variation), p = 0, the plain average, minimised MSE.
  - With misregistration of about 2 px or more, low p wins.
- **Anger and Meinhardt-Llopis 2017.** p = 11 only "if the noise level is low".
- **Roggemann, Stoudt and Welsh 1994.** A selected subset can have a higher per-frequency image-spectrum SNR than all
  frames, when the transfer varies strongly (AO).
- **Hitchcock et al. 2022.** Using all frames with per-frame PSFs beats selection in SNR at equal resolution.
- **Fan et al. 2022.** At the finest scale, the largest coefficients are noise. They select the smallest.
- **Popowicz et al. 2017.** Their noise-free ranking of metrics does not carry over to 8 bits (R4).
- **Dainty and Greenaway 1979.** Discarding frames helps only when frames are photon-starved.

### 4.2 What follows for our data (derived)

1. **FBA drifts toward averaging where noise dominates.** With smoothed magnitudes, a frame's weight is about
   `(abs(T S)^2 + sigma^2)^(p/2)`. Where sigma^2 dominates, the weights spread little between frames, so FBA tends
   toward a plain average in noise-limited frequencies and stays selective where SNR is high. That is qualitatively
   R4's per-band pattern, with no per-band keep to tune.
2. **Max selection without smoothing is biased.** The winner's curse on `abs(S + N)` inflates the measured transfer
   and adds residual (A-G3, A-R2).
3. **The criterion decides the answer.**
   - Judged raw, as by Strehl, FWHM or R3's raw fidelity, selection pays, because the raw error is dominated by the
     transfer deficit (1.4).
   - Judged after an inverse filter with known H, the error depends only on each frequency's SNR^2 = `k H_sel^2 / n^2`.
     The SNR-optimal linear combination is the matched filter, with weights proportional to each frame's coherent
     transfer. Hard selection is never better.
   - Toy numbers for lognormal transfers (200,000 draws), comparing matched weights with the best hard keep:

     | Log-sd | Top 5 % over mean | Best hard keep | Matched over best hard (SNR^2) | 5 % keep's SNR^2 over the best |
     |---|---|---|---|---|
     | 0.2 | 1.49 | 100 % | 1.04x | 0.11 |
     | 0.3 | 1.79 | 98 % | 1.09x | 0.16 |
     | 0.5 | 2.52 | 81 % | 1.24x | 0.31 |
     | 1.0 | | 20 % | 1.70x | |

   - With R4's band-1 ratio of 1.4, the 5 % keep holds about a tenth of the achievable band-1 SNR^2 after restoration.
   - This holds only for the random part: per-frequency restoration, white noise independent between frames, and H
     known. A per-**band** scalar Wiener leaves the within-band structured residual (r_det about 0.24, 1.4). That
     favours selective keeps again, because r_det is amplified with the gain and matters more at a lower H.
4. **Phase-blind weights cannot fix a warp.** FAS and FBA weight by magnitude, and a warp is phase. The warp of 0.6 px
   over 10 px costs any coherent stack a factor `W(f)` of 0.17 to 0.64 in band 1, whatever the weights. A coherent
   per-frequency weight (A-F4) or a power-spectrum method is the only route around it.
5. **Undersampling makes per-frequency selection partly select aliasing** near Nyquist (A-G4).
6. **The 8-bit sky is not additive Gaussian noise.**
   - 2022-09-03's far sky reads one or two values, with a read noise of 0.21 ADU, below the rounding step. Rounding
     there is not dithered, does not average down, and is correlated with the signal.
   - Any tile containing the limb and sky breaks the additive-noise model that FBA smoothing, HDR+ shrinkage and
     speckle noise-bias subtraction all assume.
   - Mask the sky, or model the rounding (R2's `RoundedGaussianFit`) before per-frequency statistics.

---

## 5. The three methods most worth measuring next (ranked)

### 5.1 First: score the selections after restoration, with per-band matched weights and an FBA p-sweep

- **Why first:** It is the cheapest. It needs only what exists: the reference gain, the stacks, the truth, and an
  oracle Wiener built from the stack's measured transfer against the truth in thin annuli. It decides the R4 to R8
  hand-off (#1072, #1055) and whether per-frequency selection is even the right lever. The literature backs the
  question directly: FBA's own p = 0 result for short exposures, Roggemann's image-spectrum SNR, and The Thresher's
  use of every frame.
- **Measure:** band-1 to band-4 fidelity for four scorings of five selections.
  - Scorings: (i) raw, (ii) after an oracle **per-frequency** Wiener, (iii) after R8's per-band Wiener, (iv) the
    truth-free image-spectrum SNR (3.4).
  - Selections: (a) R4's hard keeps from 10 frames to all 3,000; (b) all frames; (c) per-band matched weights
    proportional to the reference gain; (d) FBA with p in {0, 1, 2, 4, 7, 11, 20}; (e) the HDR+ merge onto the 5 %
    reference.
- **Predictions:**
  - After the oracle per-frequency Wiener, band 1's best keep moves from 5 to 10 % to **50 % or more**.
    - Its random residual should fall about 3x in amplitude (SNR^2 about 10x, from the transfer ratio of 1.4 at 5 %).
    - Matched weights should add only 1 to 4 % in SNR^2 over plain all-frame averaging (a narrow spread).
    - FBA's best p is 0 to 2.
  - After R8's per-band Wiener, 5 to 10 % stays best, because the structured residual of about 0.24 is amplified.
  - Split halves should show a random part of about 0.12 at 150 frames and about 0.03 at 3,000, against a common
    residual of about 0.24.
  - The truth-free image-spectrum SNR should rank keeps as (ii) does, with Spearman 0.8 or more.
- **Falsified if:**
  - band 1's post-restoration optimum stays at 10 % or less under the per-frequency oracle (the residual then is not
    noise-like: a large, selection-dependent deterministic part, itself a finding for R7);
  - or the split-half random part at 5 % is 0.2 or more (band 1 is then truly noise-limited, and the decomposition in
    1.4 is wrong).
- **Consequence either way:** if the per-frequency oracle favours all frames and the per-band one does not, then R8
  needs finer radial resolution in band 1 before any smarter frame fusion is worth building.

### 5.2 Second: the oracle ceiling of per-frequency selection, then the smoothed implementation, warp on and off

- **Why:** This is the literature's headline idea (Garrel, Mackay, FBA). The ceiling can be computed exactly from the
  twin's noise-free frames before any noisy code is written. fba-ipol (BSD-2) is a reference design for the tiling and
  weights. TianWen already has the FFT machinery (phase correlation, band-power scoring).
- **Measure:**
  - A-G1, the per-frequency top-q ceiling against frame top-q, per band, including directional sectors (A(b)).
  - Then FAS with Garrel's smoothing, FBA (3.10), and the coherent variant A-F4. Score each raw and after the oracle
    Wiener, with the calibrated warp on and off (A-F3), at 1x and 2x render scale (A-G4), with A-L1 as the harness
    check.
- **Predictions:**
  - The ceiling is small on the calibrated twin (under about 15 % in H in band 1, under 5 % in band 2), because the
    free air's transfer stays correlated wider than a band, so only direction helps.
  - The noisy implementations recover the ceiling in bands 2 to 4 and little in band 1.
  - The warp removes most of band 1's remaining gain, apart from A-F4's.
- **Falsified if:**
  - the ceiling exceeds 20 % in band 1 (per-frequency transfers then decorrelate faster than r0/lambda suggests, and
    the method deserves a full build);
  - or it is under 5 % in every band (stop; do not build it for this capture, and revisit on a strong-seeing or blue
    twin, where Fried's P and Smith's D/r0 of about 7 predict large frame-to-frame variation).

### 5.3 Third: the spectral ratio, the coherence R(b) and the per-frame SNR rho(b), as R7's warp-immune probe and the gate on speckle

- **Why:** It is cheap: two running sums of FFTs per tile. It is truth-free, so it runs on every real capture. It is
  established on extended objects (von der Luehe 1984, used on granulation). It does three things at once:
  - it measures r0 of the **varying** turbulence per capture and per filter (the Fried-Korff inputs for a model
    transfer);
  - it measures R(b), which with rho(b) and W decides by A-S1 whether any speckle method can beat coherent stacking;
  - it gives R7 an amplitude route that the warp cannot corrupt, since power spectra are shift-invariant.
- **Measure:** eps(f) on registered tiles with the noise bias removed (from frame differences, not corners), fitted
  with Fried-Korff theory. On the twin, against its free-air r0, its combined r0 and its empirical STF (A-V1, A-S2).
  On the real LRGB set, against `lambda^1.2` (A-V2).
- **Predictions:**
  - The ratio's r0 matches the free-air r0 within about 20 %, biased high, and not the combined r0: the static layer
    cancels.
  - The empirical STF falls below Korff's frozen STF in band 1.
  - `rho > R W^2` holds, if anywhere, only in the upper half of band 1.
- **Falsified if:** the ratio returns the combined r0 (then R7 cannot use it to split varying from static blur), or
  `rho > R W^2` holds in bands 2 to 4 (then speckle amplitude calibration is worth building for R8 now, with deblurTC
  as a GPL-2.0 reference to read, not copy).

Kept back for later:
- the KT and bispectrum phases (A-P1 predicts no gain on this capture);
- lucky-region fusion's recursion (A-R2 is a safety check, not a gain);
- The Thresher (the principled end-point of 5.1, too heavy until 5.1 says all frames are wanted);
- Mackay's hybrid (A-M1 predicts nothing measurable).

---

## 6. Table

| Work | Verified link | Relevance to us | Do we use it |
|---|---|---|---|
| Fried 1978, JOSA 68, 1651 | https://opg.optica.org/josa/abstract.cfm?uri=josa-68-12-1651 | Medium: the diagnostic for luck against noise (D1) | No (theory only) |
| Law, Mackay, Baldwin 2006, A&A 446, 739 | https://arxiv.org/abs/astro-ph/0507299 | Medium: a large lucky field, so whole-frame selection is sound | Yes, in spirit (whole-frame R4) |
| Smith et al. 2009, MNRAS 398, 2069 | https://arxiv.org/abs/0906.3041 | Medium: expected gains against D/r0 and exposure | No |
| Roggemann, Stoudt, Welsh 1994, Opt. Eng. 33, 3254 | https://api.crossref.org/works/10.1117/12.181250 | High: the per-frequency SNR criterion for a keep (5.1) | No (band power scoring only) |
| Popowicz et al. 2017, SoPh 292, 187 | https://arxiv.org/abs/1709.09458 | Low to medium: metrics; its sims were noise-free | Partly (Sobel, Laplacian tested) |
| Garrel, Guyon, Baudoz 2012, PASP 124, 861 | https://iopscience.iop.org/article/10.1086/667399 | High: per-frequency amplitude selection with smoothing | No |
| Mackay 2013, MNRAS 432, 702 | https://arxiv.org/abs/1303.5108 | High: Lucky Fourier in practice, the low-frequency hybrid, registration pooling | No (pooling yes, in R5) |
| Wang, Li, Zhang 2021, RAA 21, 118 | https://arxiv.org/abs/2012.05480 | Low: a hybrid of rates | No |
| Huang et al. 2021, PASJ 73, 1240 | https://doi.org/10.1093/pasj/psab070 (Crossref abstract) | Low: real-time FPGA | No |
| Fan et al. 2022, MNRAS 516, 2196 | https://academic.oup.com/mnras/article/516/2/2196/6673431 | Medium: wavelet-band selection; fine scale is noise | No |
| Delbracio, Sapiro 2015, CVPR | https://scholars.duke.edu/publication/1123186 | High: FBA | No |
| Delbracio, Sapiro 2015, IEEE TIP 24, 3293 | https://arxiv.org/abs/1505.02731 | High: FBA's bias, variance and misregistration analysis | No |
| Delbracio, Sapiro 2015, IEEE TCI 1, 270 | https://arxiv.org/abs/1509.05251 | High: local FBA with consistent registration | No |
| Anger, Meinhardt-Llopis 2017, IPOL 7, 56 | https://www.ipol.im/pub/art/2017/197/ ; code https://github.com/kidanger/fba-ipol (BSD-2) | High: a reference implementation, the blur-transfer warning | No |
| Gilles, Osher 2016, JEI 25, 033003 | https://arxiv.org/abs/2410.22802 | Medium to high: FBA in wavelet bands, turbulence | No |
| Hasinoff et al. 2016, ACM TOG 35 | https://doi.org/10.1145/2980179.2980254 (Crossref abstract) | Medium: a low-light per-frequency merge | No (FFT alignment, yes) |
| Monod, Delon, Veit 2021, IPOL 11, 142 | https://www.ipol.im/pub/art/2021/336/article_lr.pdf ; code https://github.com/amonod/hdrplus-python (AGPL-3.0) | Medium: the merge formula and its analysis | No |
| Vorontsov, Carhart 2001, JOSA A 18, 1312 | https://opg.optica.org/josaa/abstract.cfm?uri=josaa-18-6-1312 | Medium: local fusion under anisoplanatism | No |
| Aubailly et al. 2009, SPIE 7463 / COSI | https://opg.optica.org/abstract.cfm?uri=COSI-2009-CThC3 ; https://patents.google.com/patent/US8611691 | Medium: lucky-region fusion, automatic kernel width | Partly (smoothed Sobel per pixel) |
| Chan, Chimitt 2023, FnT CGV 15 | https://arxiv.org/abs/2411.00338 | Medium: a survey of metrics and fusion | No |
| Mendikoa et al. 2016, PASP 128, 035002 (PlanetCam) | https://iopscience.iop.org/article/10.1088/1538-3873/128/961/035002 | Medium: professional planetary practice (Sobel, whole frame) | Yes, equivalent (R4's gradient) |
| Planetary System Stacker | https://github.com/Rolf-Hempel/PlanetarySystemStacker (GPL-3.0) | Medium: open alignment points with per-point ranking | No (own R5) |
| Labeyrie 1970, A&A 6, 85 | https://articles.adsabs.harvard.edu/pdf/1970A%26A.....6...85L | Medium: power-spectrum averaging over every frame | No |
| Korff 1973, JOSA 63, 971 | https://opg.optica.org/josa/abstract.cfm?uri=josa-63-8-971 | Medium: the STF model | No |
| von der Luehe 1984, JOSA A 1, 510 | https://opg.optica.org/josaa/abstract.cfm?uri=josaa-1-5-510 | High: the spectral ratio, r0 and coherence (5.3) | No (we compute spectra, not the ratio) |
| Knox, Thompson 1974, ApJ 193, L45 | https://doi.org/10.1086/181627 (Crossref) | Low to medium: phase from the cross-spectrum | No |
| Lohmann, Weigelt, Wirnitzer 1983, Appl. Opt. 22, 4028 | https://doi.org/10.1364/AO.22.004028 (Crossref) | Low to medium: bispectrum phase, shift-invariant | No |
| Woeger, von der Luehe, Reardon 2008, A&A 488, 375 (KISIP) | https://doi.org/10.1051/0004-6361:200809894 (Crossref); derived code https://github.com/blakeMilner/deblurTC (GPL-2.0) | Medium: extended-object speckle with photometric checks | No |
| Woeger et al. 2021, SoPh 296 (DKIST VBI) | https://doi.org/10.1007/s11207-021-01881-7 (Crossref abstract) | Low to medium: speckle in production | No |
| Hitchcock et al. 2022, MNRAS 511, 5372 (The Thresher) | https://arxiv.org/abs/2202.04686 ; code https://github.com/jah1994/TheThresher (MIT) | Medium: every frame, per-frame PSF; the end-point of 5.1 | No |
| Puschmann, Beck 2011, A&A 533, A21 | https://arxiv.org/abs/1107.0703 | Low to medium: speckle against MOMFBD, contrast against noise | No |
| Dainty, Greenaway 1979, JOSA 69, 786 | https://opg.optica.org/josa/abstract.cfm?uri=josa-69-5-786 | Low: the photon-counting regime | No |

---

## 7. Traps specific to this data (derived, collected from above)

1. **No signal-free annulus.** fc is 0.73 to 1.37 cy/px, above Nyquist at 0.5. Corner noise estimates include signal.
   Use frame differences or the camera model.
2. **Undersampling about 2x.** Per-frequency amplitude selection can select aliasing near Nyquist. Test with a 2x
   render (A-G4).
3. **The 8-bit sky below the rounding step** (read noise 0.21 ADU). The noise is not additive or white there. Mask it,
   or model the rounding, before any per-frequency statistic.
4. **The warp is phase.** No magnitude-weighted method (FAS, FBA, WWBA, HDR+) recovers what the 0.6 px, 10 px warp
   costs a coherent stack in band 1 (a factor 0.17 to 0.64).
5. **Static blur is invisible to the spectral ratio, and to any selection.** Tube seeing, collimation and the coma
   hypothesis of R7a belong to R7's edge-spread and kernel fits.
6. **The frozen-exposure STF is wrong at 4 ms and 22 m/s.** The pupil slides 9 cm, more than r0. Amplitudes
   calibrated with Korff's frozen model come out low in band 1.
7. **Raw fidelity rewards selection that restoration may not.** Choose keeps at the operating point where they will
   be used: after R8, or after R8's per-frequency form.
8. **Colour.** FBA shares its weights across channels (camera shake is achromatic). Turbulence is not: r0 scales as
   lambda^1.2. On RGGB captures (IMX585, ASI462MC), compute weights per CFA plane. The blue plane's band 1 is where
   shared weights should cost most.

---

## 8. Metadata verified but text not read (existence confirmed; no claims rest on them)

- G. R. Ayers, M. J. Northcott, J. C. Dainty 1988, JOSA A 5, 963 (the conference abstract was read).
- P. Nisenson, C. Papaliolios 1983, "Effects of photon noise on speckle image reconstruction with the Knox-Thompson
  algorithm", Optics Communications 47, 91-96. DOI 10.1016/0030-4018(83)90093-7.
- F. Woeger, O. von der Luehe 2007, "Field dependent amplitude calibration of adaptive optics supported solar speckle
  imaging", Applied Optics 46, 8015. DOI 10.1364/AO.46.008015.
- F. Woeger, O. von der Luehe 2008, KISIP, Proc. SPIE 7019, 70191E. DOI 10.1117/12.788062.
- P. Mikurda, O. von der Luehe 2006, Solar Physics 235, 31-53. DOI 10.1007/s11207-006-0069-6.
- O. von der Luehe 1993, A&A 268, 374 (the ADS scan was downloaded; its text could not be extracted).
- A. G. Beard, F. Woeger, A. Ferayorni 2020, "Real-time speckle image processing with the DKIST", SPIE. DOI
  10.1117/12.2563233.
- S. G. Gibbard et al. 1999, "Titan: High-Resolution Speckle Images from the Keck Telescope", Icarus 139, 189-201.
  DOI 10.1006/icar.1999.6095. Also B. Macintosh et al. 2003, "Speckle imaging of volcanic hotspots on Io with the
  Keck telescope", Icarus 165, 137-143. DOI 10.1016/s0019-1035(03)00168-4. These show speckle masking has been applied
  to planetary bodies, small ones in the near infrared.
- M. van Noort, L. Rouppe van der Voort, M. G. Loefdahl 2005, MOMFBD, Solar Physics 228, 191-215. DOI
  10.1007/s11207-005-5782-z.
- D. G. Ford, M. C. Roggemann, B. M. Welsh 1996, "Frame selection performance limits ...", Optical Engineering 35,
  1025. DOI 10.1117/1.600719.
- J. C. Christou 1991, "Image quality, tip-tilt correction, and shift-and-add infrared imaging", PASP 103, 1040. DOI
  10.1086/132922. Also Freeman, Ribak, Christou, Hege 1985, "Statistical analysis of the weighted shift and add image
  reconstruction technique", SPIE 556. DOI 10.1117/12.949552. Both are probable sources for optimal frame weights in
  shift-and-add, worth reading beside 5.1.
- J. Baldwin, P. J. Warner, C. D. Mackay 2008, "The point spread function in Lucky Imaging and variations in seeing
  on short timescales", A&A 480, 589-597. DOI 10.1051/0004-6361:20079214.
- N. Joshi, M. F. Cohen 2010, "Seeing Mt. Rainier: lucky imaging for multi-image denoising, sharpening, and haze
  removal", ICCP. DOI 10.1109/ICCPHOT.2010.5585096.
- O. Mousis et al. 2014, "Instrumental methods for professional and amateur collaborations in planetary astronomy",
  Experimental Astronomy 38, 91-191. DOI 10.1007/s10686-014-9379-0.
- S. Gladysz et al. 2008, "Lucky imaging and speckle discrimination for the detection of faint companions with
  adaptive optics", Proc. SPIE 7015 (a search abstract only).
- C. Schirninger et al., "Neural blind deconvolution to reconstruct high-resolution ground-based solar observations",
  arXiv 2603.05033 (2026; the abstract was read, no code linked; peripheral, R7 and R9).

## 9. Unverified leads (not cited above)

- R. Tubbs 2003, "Lucky exposures" PhD thesis (Cambridge), cited by Wang et al. 2021.
- X. Hu 2019, a Kunming thesis on frequency-domain lucky imaging, cited by Wang et al. 2021.
- R. Staley 2012, the Cambridge lucky-imaging pipeline thesis, cited by Mackay 2013.
- The numerical constant of Korff's high-frequency STF asymptote, the "0.435 (r0/D)^2" form. It is not in the abstract
  I read. Section 3.17 relies only on the abstract's proportionality to the OTF.
- Wieschollek et al. 2017, the neural FBA, cited by the IPOL article.
- Delbracio's original FBA results page, http://dev.ipol.im/~mdelbra/videoFA/, cited by IPOL (not fetched).
- Deng et al. 2015, MFGS (Solar Physics). Seen only through Popowicz et al. and a search listing.
- Hofmann and Weigelt 1993, "Iterative image reconstruction from the bispectrum", A&A 278, 328 (from Popowicz's
  references).
- de Boer 1993, the Goettingen speckle code (a search snippet only).
- Roggemann and Welsh 1996, "Imaging Through Turbulence" (a book; only a review of it was found). Roddier's 1981
  review.
- Whether KISIP itself is publicly downloadable, and the number of frames per DKIST VBI reconstruction (neither was
  found).
- AutoStakkert! (E. Kraaikamp): no paper, closed source, not verified.
