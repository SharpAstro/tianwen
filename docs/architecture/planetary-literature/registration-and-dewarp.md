# Literature review B: sub-pixel registration of noisy frames, and dewarping anisoplanatic turbulence

> One of three reviews behind [the planetary literature synthesis](../planetary-literature.md), made for `docs/plans/planetary-restoration.md` on 2026-09-30 and kept as written: its "our" and "we" are TianWen, its numbers are R4's and R5's as they stood that day, and each entry says how far its source was read. What it predicted and what was then measured is in the plan.

For TianWen's planetary restoration plan, phases R5 (alignment and dewarp) and R6 (derotation). Compiled 2026-09-30. It read `docs/plans/planetary-restoration.md` (R5 part 2) and a few planetary sources, so the "do we use it" column is right.

## 0. How to read this

- **Verified** means I fetched, on 2026-09-30, the item's DOI record (CrossRef API), its publisher, arXiv, PubMed or CVF page, or its full text. Each item states how far I got: *full text read*, *method section read*, *abstract read*, or *metadata only*. Nothing is cited that I could not reach; the leads I could not verify are in section 9.
- Numbers marked **(my arithmetic)** are mine, not the cited work's. Their assumptions are in Appendix A. They are there to turn each paper's idea into a prediction on our twin, and should be re-derived with the twin's exact covariance before anyone relies on them.
- Our context, as given: 10-inch f/4.7 Newtonian at 0.49 to 0.51 arcsec/px, 8-bit video, 2 to 4 ms, 250 to 445 fps; Jupiter about 100 px across. Twin warp 0.6 to 0.65 px RMS per axis, correlation length about 10 px (real: 9 px or less; the calibrated twin's correlation at 9 px is +0.05), AR(1) in time with about 0.9 per frame. R5 findings: whitened phase correlation 1.11 px RMS on a 16 px patch, plain cross-correlation 0.35 px; points recover 11 to 19 percent, the applied mesh 0 to 3 percent; pooling over 1 to 4 frames adds almost nothing; the median geometry is 8 percent closer to the truth than the best-frame geometry.
- What TianWen does today (read from source, so the table in section 8 is right): `AlignmentPointMatcher` matches Hann-windowed patches from **one reference frame** by FFT correlation (whitened or plain) with a parabolic peak; `DisplacementMesh` blends the point residuals by **Gaussian kernel regression** (`influence` default 48 px, `regularization` 0.25) onto a node grid (default 24 to 32 px) and samples it bilinearly; `AlignmentPointTracks` pools over frames and takes out the per-point **median** warp; bilinear resampling and a Bayer drizzle exist.

## 1. The short version

1. **Finding (1) is the textbook result.** Knapp and Carter (1976) derived the maximum-likelihood weighting for correlation-based delay estimation: it emphasises the frequencies with the best signal-to-noise ratio and, at low SNR, reduces to Eckart prefiltering. Phase correlation ("PHAT") weights every frequency alike, so on an 8-bit frame it gives the noise-dominated high frequencies full weight. PlanetarySystemStacker's own algorithm notes call phase correlation "not recommended" and blur the frames before any shift is measured; scikit-image's documentation says unnormalised correlation "may perform better in high-noise scenarios". Löfdahl (2010), testing solar Shack-Hartmann shift algorithms, found square-difference matching with a 2-D quadratic peak fit best, and the apodised Fourier covariance method worse and systematically shrinking shifts.
2. **Finding (3) is half of what every working method does.** Nearly every classical turbulence method registers each frame to a **temporal average**, not to a single frame: Fraser et al. 1999 (shift maps), Shimizu et al. 2008, Zhu and Milanfar 2013 ("can be obtained by averaging the frame sequence"), Mao and Gilles 2012 (initial latent image = temporal average), Hardie et al. 2017/2021 ("because the warping shifts have a zero-mean, a simple average ... produces a useful prototype"), Mao, Chimitt and Chan 2020, and TMFS 2026 (latent initialised by fitting the mean of the frames). **Lao et al. (CVPR 2024) is exactly the user's idea**: pick one frame, and model its deformation as the *mean of the flows* from it to all others (a Central Limit Theorem argument), then invert. Solar pipelines do the same: MOMFBD runs force the mean tip-tilt per subfield to zero; the DST destretch uses a running mean of 30 frames as reference. Among amateur tools, PSS measures alignment-point shifts against the mean of the best 5 percent of frames, and AutoStakkert against a stack of the best frames, optionally stacked twice ("Double Stack Reference"). We took the mean geometry out afterwards but never **re-measured every frame against the high-SNR mean-geometry stack**, which is the second half.
3. **The 0 to 3 percent mesh is predicted by the mesh's own low-pass, not by the impossibility of dewarping.** Hardie et al. (2021) model block registration as the tilt field filtered by (identity minus a moving average), minus an error term, and define a "tilt correction factor" that can go negative when registration error exceeds the correction. Applied to our mesh (a Gaussian blend of reach 12 to 48 px over points 8 to 12 px apart, on a warp whose covariance is nearly gone by 9 px), the same algebra predicts 3 to 8 percent RMS recovery for the two grids R5 measured **(my arithmetic)**, an upper bound on the measured 1 and 3 percent, as it should be since it leaves out the reference's own noise and warp. The same model predicts 11 to 19 percent for a reach of about 4 px, and 15 to 22 percent (up to 31 with a stacked reference's lower point error) for a statistically optimal (kriging, Gaussian-process) interpolation of points 4 px apart, single frame, against a noise-free reference. So a recovery above 3 percent is within reach, but only when three things change together: dense points (spacing at most the correlation length), a matched interpolator, and a better reference. R5 part 2 varied them one at a time.
4. **Resampling can erase a dewarp of this size.** A bilinear resample at a uniformly distributed sub-pixel offset adds 1/6 px^2 of blur variance per axis (σ ≈ 0.41 px) **(my arithmetic)**, which is 46 percent of the warp's own 0.36 px^2. PSS avoids it by shifting patches by whole pixels (1/12 px^2 of misregistration, 23 percent); SSTRED folds every shift, rotation and distortion into one interpolation for the same reason. Part 3 (bilinear against Lanczos-3) should come before any conclusion about the dewarp's value to the stack.
5. **Grid versus isoplanatic patch, in practice.** MOMFBD subfields are about 5 arcsec (about the isoplanatic patch), with residual stretching "on scales smaller than the subfields" removed afterwards by destretching; the DST destretch goes coarse to fine, 12, 8 then 4 arcsec. At our 0.5 arcsec/px that ladder is 24, 16 and 8 px, ending at the warp's correlation length. PIV reaches the same place by iterating window deformation (Scarano 2002: "order-of-magnitude improvement" in sub-pixel precision) and by averaging correlation planes rather than displacements when the signal is weak (Meinhart et al. 2000).

## 2. Registration theory

### 2.1 Kuglin and Hines 1975, phase correlation
- **Citation:** C. D. Kuglin, D. C. Hines, "The phase correlation image alignment method", Proc. IEEE Int. Conf. on Cybernetics and Society, 1975, pp. 163 to 165.
- **Verified:** *metadata only.* Bibliographic record at Semantic Scholar, https://www.semanticscholar.org/paper/e4482d429ddd155f4c5b1299dec71d4faa10d241 (1,463 citations). The proceedings are not online; I have not read the paper. Kiya and Ito (https://doi.org/10.5772/8520, abstract read) credit it as the origin of phase-only correlation with the DFT and relate it to PHAT in sonar delay estimation.
- **Code / licence:** none of its own.
- **What:** normalise the cross-power spectrum to unit magnitude so the inverse transform is a delta at the shift.
- **Relevance:** the origin of our `whiten: true`. Its implicit assumption, that every frequency is equally trustworthy, is what fails on 8-bit frames (see 2.5).
- **Phases:** R5.

### 2.2 Foroosh, Zerubia and Berthod 2002, sub-pixel phase correlation
- **Citation:** H. Foroosh, J. B. Zerubia, M. Berthod, "Extension of phase correlation to subpixel registration", IEEE Trans. Image Process. 11(3), 188 to 200 (2002).
- **Verified:** *abstract read.* https://pubmed.ncbi.nlm.nih.gov/18244623/ ; DOI https://doi.org/10.1109/83.988953 (resolves to IEEE Xplore).
- **What:** for downsampled images the phase-correlation energy lies in several coherent peaks next to each other (the polyphase transform of a filtered impulse); the ratio of the main peak to its neighbours gives a closed-form sub-pixel shift, with an error analysis.
- **Scale / noise:** the analysis is about the peak model (aliasing), not about low SNR; it keeps the whitening.
- **Reference:** a pair of frames.
- **Phases:** R5.
- **Prediction on the twin:** the closed-form peak on a *whitened* 16 px patch still carries the whitening's noise weighting, so it should not come near plain correlation's 0.35 px. If it reached 0.35 px or better, the loss would be in our peak fit and not in the weighting, and the Knapp and Carter reading in 2.5 would be wrong.

### 2.3 Guizar-Sicairos, Thurman and Fienup 2008, upsampled DFT
- **Citation:** M. Guizar-Sicairos, S. T. Thurman, J. R. Fienup, "Efficient subpixel image registration algorithms", Opt. Lett. 33(2), 156 to 158 (2008).
- **Verified:** *abstract read.* https://opg.optica.org/abstract.cfm?URI=ol-33-2-156
- **Code / licence:** implemented in scikit-image's `phase_cross_correlation` (the docs cite it), BSD-3-Clause. Docs verified: https://scikit-image.org/docs/stable/api/skimage.registration.html
- **What:** locate the correlation peak to a small fraction of a pixel by a matrix-multiply DFT evaluated only near the coarse peak (an upsampled cross-correlation), at a small fraction of the cost and memory of FFT upsampling; judged with a translation-invariant error metric.
- **Scale / noise:** it makes the peak location precise for the correlation surface it is given; it does nothing about the surface's noise.
- **Reference:** a pair.
- **Phases:** R5 (also R6 channel alignment).
- **Prediction on the twin:** replacing our parabolic vertex by an upsampled-DFT peak (factor 20 to 100) on the *plain* correlation should move the 16 px RMS by less than 0.02 px, because Löfdahl measured quadratic-fit undulations of about 0.01 px amplitude on solar granulation. A change larger than 10 percent of 0.35 px would mean our peak fit, not noise, dominates, and would move the priority toward 2.6.

### 2.4 Robinson and Milanfar 2004; Pham et al. 2005, the Cramér-Rao bound
- **Citations:** D. Robinson, P. Milanfar, "Fundamental performance limits in image registration", IEEE Trans. Image Process. 13(9), 1185 to 1199 (2004). T. Q. Pham, M. Bezuijen, L. J. van Vliet, K. Schutte, C. L. Luengo Hendriks, "Performance of optimal registration estimators", Proc. SPIE 5817, 133 (2005).
- **Verified:** Robinson and Milanfar *full text read* (author manuscript, https://users.soe.ucsc.edu/~milanfar/publications/journal/MotionPerformanceFinal.pdf ; https://pubmed.ncbi.nlm.nih.gov/15449581/ ; DOI https://doi.org/10.1109/TIP.2004.832923). Pham et al. *full text read* (https://repository.tudelft.nl/file/File_49e89f9e-61b7-42cb-af2f-376759523029 ; DOI https://doi.org/10.1117/12.603304).
- **What:** the Fisher information for a translation depends only on the image's gradients: F = (1/σ^2) [ΣIx^2, ΣIxIy; ΣIxIy, ΣIy^2] over the registration window (Pham eq. 9), so var(vx) ≥ σ^2 ΣIy^2 / det F, and in the simplified form var(vx) ≥ σ^2 / ΣIx^2 (Pham eq. 11). Robinson and Milanfar show that practical estimators are biased, derive the bias of gradient-based estimators, and explain multiscale methods through it; Pham et al. show an iterative gradient-based estimator removes the bias and converges to the bound.
- **Scale / noise:** the bound falls as 1/sqrt(gradient energy), so for uniform texture as 1/W for a W-px square window. When the reference is as noisy as the frame, the difference image carries twice the noise variance, so expect about sqrt(2) times the bound **(my reasoning, not stated in either paper in this form)**.
- **Reference:** Pham's likelihood has both images noisy around one noiseless scene; the bound is reached against the scene.
- **Phases:** R5 (it decides whether estimator work can pay at all).
- **Prediction on the twin:** compute the bound directly from the twin's noiseless render and noise model for each 16 px point. If 0.35 px is within about 1.2 times sqrt(2) times the bound, plain correlation is already efficient, and only a better reference (4.x, item P1) or pooling can help. If 0.35 px is two or more times that, an iterative gradient estimator (Pham) or a better-weighted correlation (2.5) should close part of the gap. Either outcome is informative; it is the cheapest measurement in this review.

### 2.5 Knapp and Carter 1976, the ML generalised cross-correlation (phase versus plain correlation under noise)
- **Citation:** C. Knapp, G. Carter, "The generalized correlation method for estimation of time delay", IEEE Trans. Acoust., Speech, Signal Process. 24(4), 320 to 327 (1976).
- **Verified:** *abstract read* (OpenAlex; DOI https://doi.org/10.1109/TASSP.1976.1162830).
- **What:** the ML delay estimator is a pair of prefilters followed by a cross-correlator; "the role of the prefilters is to accentuate the signal passed to the correlator at frequencies for which the S/N ratio is highest and, simultaneously, to suppress the noise power ... For low S/N ratio, the ML estimator is shown to be equivalent to Eckart prefiltering."
- **What it means for us (my arithmetic, from the paper's standard ML weight):** with signal power S(f) and white noise N in each image, the ML weight on the cross-spectrum is S / (N (N + 2S)). Where S >> N it is a constant, i.e. **plain cross-correlation**; where S << N it is S / N^2, i.e. plain correlation with an extra low-pass. The phase transform (weight 1 / |cross-spectrum|) is ML at no SNR for white noise. That is our 1.11 against 0.35 px.
- **Reference:** a pair.
- **Code / licence:** none needed; it is a weight on the cross-spectrum TianWen already forms.
- **Phases:** R5.
- **Prediction on the twin:** weighting the cross-spectrum by S / (N (N + 2S)), with S taken from the stack's power spectrum less the flat noise floor and N from that floor (TianWen already measures both for R3), lowers the 16 px RMS below 0.35 px, by about 10 to 25 percent, and by more at 8 to 12 px where more of the band is noise. PSS's practice (a Gaussian blur of width 7 before matching) is a crude version and should land between the two. Falsified if the ML weight does no better than plain; then plain correlation is already near the bound (check 2.4) and the next gain must come from the reference or from pooling.

### 2.6 Löfdahl 2010, shift measurement on solar granulation
- **Citation:** M. G. Löfdahl, "Evaluation of image-shift measurement algorithms for solar Shack-Hartmann wavefront sensors", A&A 524, A90 (2010).
- **Verified:** *full text read.* https://arxiv.org/abs/1009.3401 ; DOI https://doi.org/10.1051/0004-6361/201015331
- **What:** compares square difference (SDF), absolute difference (ADF), ADF squared, image-domain covariance (CFI) and Fourier-domain covariance (CFF, Hamming-apodised), each with 1-D and 2-D quadratic and least-squares peak interpolation, on 16x16 and 24x24 px subfields with known shifts and simulated seeing. Best: SDF and ADF^2 with 2-D quadratic interpolation, error RMS under 0.02 px noise-free and under 0.03 px at 0.5 percent noise for 16x16; going from 16 to 24 px cuts the error about 30 percent. CFF "systematically underestimates" shifts (slope below one; apodisation lowers the minimum for large shifts). A 1 percent intensity bias mismatch raises SDF's error to 0.7 px (16x16) and 0.4 px (24x24); "bias mismatch should be removed in pre-processing by subtraction of the intensity mean", and Smithson and Tarbell (1977) showed a linear intensity trend shifts the covariance peak, so a fitted plane should be removed. The methods measure Z-tilt, not G-tilt. It also notes a real sensor is then "limited by other effects than noise, such as image warping from anisoplanatism".
- **Scale / noise:** 16 to 24 px windows; the solar granulation there has far more gradient energy per pixel than an 8-bit Jupiter.
- **Reference:** a reference subimage (one frame).
- **Phases:** R5.
- **Prediction on the twin:** our matcher is CFF-class (Hann-windowed FFT correlation). Regress each point's measured shift on its true (window-averaged) shift: expect a slope below one from the apodisation, beyond the shrinkage the window averaging alone predicts. Swapping to SDF on an oversized search area, mean-subtracted and plane-subtracted, with a 2-D quadratic fit, should bring the slope to within a few percent of the window-averaging prediction and cut the RMS by 10 to 30 percent. Separately, points near the limb (where limb darkening puts a strong intensity trend in the patch) should show a radial bias that plane subtraction removes. Falsified if the slope already matches the window-averaging prediction.

### 2.7 Gratadour, Mugnier and Rouan 2005, ML registration of low-SNR frame sequences
- **Citation:** D. Gratadour, L. M. Mugnier, D. Rouan, "Sub-pixel image registration with a maximum likelihood estimator", A&A 443, 357 to 365 (2005).
- **Verified:** *abstract read.* DOI https://doi.org/10.1051/0004-6361:20042188 (open PDF listed at https://www.aanda.org/articles/aa/pdf/2005/43/aa2188-04.pdf, not fetched).
- **What:** ML shift between two noisy images, then a *joint* ML estimate of the noiseless reference and all the shifts for a sequence of low-signal frames; "the registration accuracy is increased at low photon levels as the number of frames grows, reaching the sub-pixel domain at very low SNR (about 1), when considering 100 frames", and both ML methods "totally outperform the classical cross-correlation" on faint thermal-IR data.
- **Reference:** the jointly estimated noiseless reference, which is the registered mean.
- **Phases:** R5 (global and per point).
- **Prediction on the twin:** the same as P1 below; this is its estimation-theory justification.

### 2.8 Tong et al. 2019, review of Fourier-based correlation
- **Citation:** X. Tong et al., "Image registration with Fourier-based image correlation: a comprehensive review of developments and applications", IEEE JSTARS 12(10), 4062 to 4081 (2019).
- **Verified:** *abstract read.* https://doi.org/10.1109/JSTARS.2019.2937690
- **Relevance:** a map of sub-pixel variants; nothing in the abstract addresses our noise regime. Low.

## 3. Turbulence dewarping and fusion (classical)

### 3.1 Fraser, Thorpe and Lambert 1999
- **Citation:** D. Fraser, G. Thorpe, A. Lambert, "Atmospheric turbulence visualization with wide-area motion-blur restoration", JOSA A 16(7), 1751 (1999).
- **Verified:** *abstract read.* https://doi.org/10.1364/JOSAA.16.001751
- **What:** short exposures freeze the distortion into randomly warped frames; "point-by-point registration results in x and y shift maps describing the warp for each image", used to dewarp each frame before averaging. The abstract does not say which reference; I could not read the method.
- **Phases:** R5. **Prediction:** covered by P1 and P2.

### 3.2 Shimizu, Yoshimura, Tanaka and Okutomi 2008
- **Citation:** "Super-resolution from image sequence under influence of hot-air optical turbulence", CVPR 2008.
- **Verified:** *abstract read.* https://doi.org/10.1109/CVPR.2008.4587525
- **What:** estimate an undeformed frame from the sequence; register every frame to it with a B-spline non-rigid model with "a stable non-rigid registration technique ... for dealing with a textureless region"; then multi-frame super-resolution. Zhu and Milanfar (3.3) describe its stabilisation as keeping deformation parameters small where the gradient is low.
- **Scale / noise:** that stabilisation is a gradient-weighted shrinkage, which is what an MMSE estimator does where the Fisher information (2.4) is small.
- **Reference:** the estimated undeformed frame (temporal estimate).
- **Phases:** R5.
- **Prediction on the twin:** shrinking each point's shift toward zero by its own information, g / (g + σ^2 / σ_w^2) with g the patch's gradient energy, beats a uniform shrinkage on Jupiter's low-contrast zones (the equatorial zone, polar regions) and ties it on the belts. Falsified if uniform and gradient-weighted shrinkage recover the same.

### 3.3 Zhu and Milanfar 2013, register to the average, fuse, deconvolve
- **Citation:** X. Zhu, P. Milanfar, "Removing atmospheric turbulence via space-invariant deconvolution", IEEE TPAMI 35(1), 157 to 170 (2013).
- **Verified:** *full text read* (author manuscript http://users.soe.ucsc.edu/~milanfar/publications/journal/manuscript_turbulence.pdf ; https://pubmed.ncbi.nlm.nih.gov/23154324/ ; DOI https://doi.org/10.1109/TPAMI.2012.82).
- **Code / licence:** the Milanfar software page links code, but the download link now redirects to the school's home page (checked 2026-09-30). No licence found.
- **What:** (1) register each frame to a reference "without turbulent deformation (which can be obtained by averaging the frame sequence)" with a B-spline displacement field fitted to intensities by Gauss-Newton, with a soft **symmetry (inverse-consistency) constraint** instead of a smoothness or stabilisation prior, because "the deformation caused by atmospheric turbulence is independent of image content"; (2) per 9x9 patch, pick the sharpest and denoise by temporal kernel regression; (3) blind deconvolution of the fused image.
- **Scale / noise:** control points every **16 px**, symmetry weight 5000, on 237x237 hot-air sequences with warps much larger than ours. Every pixel's gradient enters the fit jointly; there is no separate patch-match-then-blend step.
- **Reference:** temporal average.
- **Phases:** R5, and R7 (deconvolution of a now space-invariant blur).
- **Prediction on the twin:** a B-spline field fitted by intensity to the stack with control spacing 16 px cannot represent a 4 to 5 px (Gaussian σ) warp and should recover under about 10 percent in RMS; at a 4 px control spacing, with a smoothness or covariance prior (their symmetry term alone is not a noise regulariser), it should reach the P2 ceiling. Falsified if the 4 px spline does no better than 16 px.

### 3.4 Hirsch, Sra, Schölkopf and Harmeling 2010, efficient filter flow
- **Citation:** "Efficient filter flow for space-variant multiframe blind deconvolution", CVPR 2010, 607 to 614.
- **Verified:** *abstract read.* https://doi.org/10.1109/CVPR.2010.5540158 (the open copy at pure.mpg.de refused automated access).
- **What:** a class of linear operators expressive enough for space-variant filters yet cheap to apply (FFT-based, on overlapping windowed patches, as the literature describes it), used for multiframe blind deconvolution through turbulence. Local shifts are absorbed into each patch's PSF rather than dewarped.
- **Relevance:** R7 rather than R5; it does not produce a displacement field. I could not read its patch sizes.
- **Prediction:** none on the warp metric (it has no warp output); on the stack metric, it competes with dewarp plus deconvolution.

### 3.5 Mao and Gilles 2012; Gilles 2013 (IPOL)
- **Citations:** Y. Mao, J. Gilles, "Non rigid geometric distortions correction: application to atmospheric turbulence stabilization", Inverse Problems and Imaging 6(3), 531 to 546 (2012). J. Gilles, "Mao-Gilles algorithm for turbulence stabilization", Image Processing On Line 3, 198 to 207 (2013).
- **Verified:** Mao and Gilles *method read* (arXiv copy https://arxiv.org/pdf/2411.01788 ; DOI https://doi.org/10.3934/ipi.2012.6.531). IPOL page read: https://www.ipol.im/pub/art/2013/46/ (DOI 10.5201/ipol.2013.46).
- **Code / licence:** IPOL reference code, **BSD-2-Clause** (per the IPOL page), with an online demo.
- **What:** a variational model for the static scene: estimate each frame's flow from the latent image, update the latent image with nonlocal total variation, by Bregman iteration and operator splitting, and iterate. "The initial value of u is chosen as the temporal average of the frames"; the result is "not sensitive to the choice of the optical flow scheme" (they compared their flow with pyramidal Lucas-Kanade).
- **Reference:** temporal average, refined.
- **Phases:** R5.
- **Prediction on the twin:** iterating "register to the latent, rebuild the latent" converges in two or three passes; most of the gain comes in the first re-registration against the stack (P1), and the second pass adds under a fifth of the first's gain. Falsified if the second pass adds as much as the first (then the reference is still far from the mean geometry after one pass).

### 3.6 Lou, Kang, Soatto and Bertozzi 2013
- **Citation:** "Video stabilization of atmospheric turbulence distortion", Inverse Problems and Imaging 7(3), 839 to 861 (2013).
- **Verified:** *abstract read.* https://doi.org/10.3934/ipi.2013.7.839
- **What:** "temporal averaging produces a blurred version of the scene's radiance"; a Sobolev-gradient plus Laplacian flow stabilises the sequence, then lucky regions. Relevance low; noted because it states the averaged reference's blur, which a mean-geometry stack carries.

### 3.7 Anantrasirichai, Achim, Kingsbury and Bull 2013 (CLEAR)
- **Citation:** "Atmospheric turbulence mitigation using complex wavelet-based fusion", IEEE Trans. Image Process. 22(6), 2398 to 2408 (2013).
- **Verified:** *abstract read.* https://doi.org/10.1109/TIP.2013.2249078 (the Bristol open copy refused automated access, so I could not read which reference its ROI registration uses).
- **What:** select informative ROIs from good frames, register them "to further reduce offsets and distortions", fuse at region level in the dual-tree complex wavelet domain, then contrast enhancement; plus a learned quality metric.
- **Relevance:** the fusion step (per-region, per-band selection) is R4/R8 territory; the registration detail is unverified. Medium-low for R5.

### 3.8 Halder, Tahtali and Anavatti 2014 (iFRTAAS)
- **Citation:** "Simple and efficient approach for restoration of non-uniformly warped images", Applied Optics 53(25), 5576 (2014).
- **Verified:** *abstract read.* https://doi.org/10.1364/AO.53.005576
- **What:** register all frames non-rigidly to a reference frame to get motion fields; then an "iterative First Register Then Average And Subtract" step corrects the geometry (the average of the motion fields is the reference's own distortion, which is subtracted); non-local means for noise.
- **Reference:** one frame, corrected by the average of the fields and iterated. This is our median-geometry step with a mean and an iteration.
- **Phases:** R5.
- **Prediction on the twin:** iterating our median-geometry correction (re-read every frame against the corrected reference, re-take the median) converges within two or three passes, and its total gain over one pass is small unless the first pass was limited by the reference frame's own noise; if that noise dominates, iterating against a *single* frame will not remove it, and only P1 (a stacked reference) will.

### 3.9 Hardie et al. 2017 (BMWF) and 2021 (tilt correlation statistics)
- **Citations:** R. C. Hardie, M. A. Rucci, A. J. Dapore, B. K. Karch, "Block matching and Wiener filtering approach to optical turbulence mitigation and its application to simulated and real imagery with quantitative error analysis", Optical Engineering 56(7), 071503 (2017). R. C. Hardie, M. A. Rucci, S. Bose-Pillai, R. Van Hook, "Application of tilt correlation statistics to anisoplanatic optical turbulence modeling and mitigation", Applied Optics 60(25), G181 to G198 (2021).
- **Verified:** 2017 *abstract read* (https://doi.org/10.1117/1.OE.56.7.071503 ; SPIE's copy blocked automated access). 2021 *full text read* (https://arxiv.org/abs/2108.00528 ; DOI https://doi.org/10.1364/AO.418458).
- **Code / licence:** none found.
- **What:** form a prototype by temporal averaging ("because the warping shifts have a zero-mean"); register every frame to it by block matching with (2L+1)^2 blocks; average; Wiener-deconvolve with an OTF that **includes how much tilt the registration removed**. The 2021 paper derives anisoplanatic tilt correlation functions, models patch registration as filtering the tilt field with (identity minus moving average), and defines a tilt correction factor from the residual tilt variance and "the registration error-to-signal ratio"; it "can become negative if the residual tilt variance is larger than the input tilt variance", in which case "it would be advisable ... to forgo such ineffective registration". Tilt correlation is higher for perpendicular than for parallel separation, so the 2-D tilt autocorrelation is anisotropic. Fried's parameter is estimated from the motion vectors.
- **Scale / noise:** the block size trades the fraction of tilt a block can represent against its measurement error; the paper plots both against block size.
- **Reference:** temporal average.
- **Phases:** R5 (the model), R7 (the OTF with registration).
- **Predictions on the twin:** (a) the tilt correction factor, computed from the twin's known warp covariance, our window and mesh, and the measured point error, should reproduce the measured recoveries (points 11 to 19 percent, mesh 1 to 3 percent) to within a few points; Appendix A does this roughly for the mesh and bounds it from above. If it does not, some term (the reference's own warp or noise, or estimator bias) is missing from our accounting. (b) On the real capture, the point displacements' x-component should correlate further along y than along x (perpendicular beats parallel); an isotropic twin would then be slightly wrong. (c) Wiener deconvolution of the stack with a Gaussian of the *residual* warp variance (0.36 px^2 times one minus the correction factor) should recover more of band 1 than any dewarp change R5 measured (0.005 of band 1's error), which makes it the natural R7 complement.

### 3.10 Mao, Chimitt and Chan 2020
- **Citation:** Z. Mao, N. Chimitt, S. H. Chan, "Image reconstruction of static and dynamic scenes through anisoplanatic turbulence", IEEE Trans. Comput. Imaging 6, 1415 to 1428 (2020).
- **Verified:** *method section read.* https://arxiv.org/abs/2009.00071 ; DOI https://doi.org/10.1109/TCI.2020.3029401
- **What:** a reference built by space-time non-local averaging (patches matched across neighbouring frames and averaged with weights that reject badly distorted ones), optical flow of every frame to it, lucky-region fusion by a sharpness metric *plus a geometric-consistency metric*, then blind deconvolution with a physics-constrained PSF prior. "When the scene is static, one can take the temporal average as the reference frame."
- **Reference:** temporal (non-local) average.
- **Phases:** R5, R4 (selection by registration consistency).
- **Prediction on the twin:** weighting each frame's contribution at a point by how well it registered there (the correlation peak's height, or its residual after dewarp) as well as by sharpness gives a lower band-1 error than sharpness alone at the same keep. Falsified if the two weightings tie.

### 3.11 Lao, Wang, Wong and Soatto 2024, diffeomorphic template registration (the median-geometry idea)
- **Citation:** D. Lao, C. Wang, A. Wong, S. Soatto, "Diffeomorphic template registration for atmospheric turbulence mitigation", CVPR 2024 (Highlight).
- **Verified:** *method read* (https://arxiv.org/abs/2405.03662 ; poster page https://cvpr.thecvf.com/virtual/2024/poster/30441).
- **Code / licence:** "demo code in the supplementary material"; arXiv text CC BY 4.0; no repository found.
- **What:** choose one frame (the first); estimate optical flow from it to every other frame with plain Horn-Schunck; by the Central Limit Theorem the mean of the zero-mean warps tends to identity, so the *average* of those flows is the warp from the reference to the latent template; a flow-inversion module (splat each endpoint to its four neighbours, inverse-distance weights, inpaint holes) registers every frame to the template without ever forming the template. State of the art despite the simplest flow.
- **Reference:** a single frame, corrected by the mean of the flows. Mean, not median.
- **Phases:** R5; the flow inversion is also what R6 needs to compose derotation with a dewarp.
- **Prediction on the twin:** replacing our median by a mean (or a trimmed mean) changes the result by less than one percent, because with 3,000 frames either statistic's averaging error is tiny and the remaining error is the reference frame's own noise, common to every frame. That is a test of the common-mode hypothesis behind P1. If mean and median differ by more than a few percent, outliers (whitened matches, points near the limb) dominate instead, and a robust statistic matters.

## 4. Learned and optimisation-based methods (recent)

### 4.1 TMT (Zhang, Mao, Chimitt and Chan), DATUM (CVPR 2024), MambaTM (CVPR 2025)
- **Citations:** X. Zhang, Z. Mao, N. Chimitt, S. H. Chan, "Imaging through the atmosphere using turbulence mitigation transformer", IEEE TCI 10, 115 to 128 (2024), https://arxiv.org/abs/2207.06465 , DOI https://doi.org/10.1109/TCI.2024.3354421. X. Zhang, N. Chimitt, Y. Chi, Z. Mao, S. H. Chan, "Spatio-temporal turbulence mitigation: a translational perspective" (DATUM), CVPR 2024, https://arxiv.org/abs/2401.04244. X. Zhang, N. Chimitt, X. Wang, Y. Yuan, S. H. Chan, "Learning phase distortion with selective state space models for video turbulence mitigation" (MambaTM), CVPR 2025 Highlight, https://arxiv.org/abs/2504.02697.
- **Verified:** all three *abstracts read*; venues from the arXiv comments and CrossRef (TMT).
- **Code / licence:** https://github.com/xg416/TMT and https://github.com/xg416/DATUM carry **no licence file** (checked the repository trees); https://github.com/xg416/MambaTM states **MIT** in its README.
- **What:** TMT decouples tilt and blur with a multi-scale loss and temporal attention, plus a simulator; DATUM brings classical ideas (recurrent long-range temporal aggregation, deformable attention for "pixel registration", temporal-channel attention for "lucky imaging") into one network trained on its ATSyn dataset; MambaTM uses a selective state space model with a learned latent phase distortion map for a global receptive field at linear cost.
- **Scale / noise:** trained on long-range horizontal-path scenes, not on photon-starved 8-bit planetary video; TMFS (4.2) reports that supervised methods show a significant domain gap on real data.
- **Reference:** learned, implicit.
- **Phases:** R5 and R8 as architectures; not usable as they are.
- **Prediction on the twin:** run as released on the twin, they should do worse than our global stack on band 1, because their training distribution lacks 8-bit shot noise at our levels. Retraining on the twin is the only fair test and is out of R5's scope.

### 4.2 Xie, Huang, Xu and Ji 2026, TMFS (frame-shared degradation parameters)
- **Citation:** D. Xie, Y. Huang, Y. Xu, H. Ji, "Physically-grounded turbulence mitigation with frame-shared degradation parameters", CVPR 2026.
- **Verified:** *method section read* (open-access PDF https://openaccess.thecvf.com/content/CVPR2026/papers/Xie_Physically-Grounded_Turbulence_Mitigation_with_Frame-Shared_Degradation_Parameters_CVPR_2026_paper.pdf ; poster https://cvpr.thecvf.com/virtual/2026/poster/37244).
- **Code / licence:** no code link found in the paper.
- **What:** unsupervised, per-sequence optimisation of a tilt-then-blur model. Each frame's tilt field is **coloured noise with a scene-shared autocorrelation**: M_T = F^-1(|F(C_T)| · N_T), with C_T a learned, monotone, isotropic-plus-anisotropic correlation function and N_T per-frame noise maps; blur likewise through phase-to-space PSF bases. A "low amplitude filter" drops the weak frequencies of the tilt PSD so the tilt field cannot fit image content. The latent image is an implicit neural representation "initialized by fitting the mean of the observed frames"; losses are L1 reconstruction, an edge-gradient term and a lucky term.
- **Scale / noise:** the warp's spatial scale is set by the shared PSD, learned from the data; this is a prior matched to the warp's correlation length, the thing our Gaussian blend lacks.
- **Reference:** a latent image started at the temporal mean, fitted jointly.
- **Phases:** R5 (and R7 through the blur model).
- **Prediction on the twin:** parametrising each frame's warp as noise shaped by the twin's own warp PSD (known exactly, or estimated from the points' spatial correlation) and fitting it by intensity against the stack should reach at least the P2 ceiling in the applied field; with a white (unshaped) prior, or one shaped like our 48 px Gaussian, it should recover about nothing. Falsified if the shaped prior is no better than a white one.

### 4.3 DIPLI (Singh, Batsheva, Rogov and Bouridane 2026)
- **Citation:** "DIPLI: deep image prior lucky imaging for blind astronomical image restoration", Scientific Reports 16, 17204 (2026).
- **Verified:** *abstract read;* CrossRef metadata (published 2026-04-15, CC BY 4.0), DOI https://doi.org/10.1038/s41598-026-47300-4 ; arXiv https://arxiv.org/abs/2503.15984 ; repository read, https://github.com/SteelRail/DIPLI (**MIT**).
- **What:** deep image prior with a multi-frame back-projection loss, dense optical flow by TVNet (alternatives: TV-L1, iterative Lucas-Kanade, RAFT and others), and Monte Carlo estimates by stochastic gradient Langevin dynamics; 7 to 13 input frames rather than thousands; best perceptual scores (LPIPS 12/12, DISTS 10/12) on synthetic data while a diffusion model wins PSNR and SSIM.
- **Reference:** a `pivot.png` if given, otherwise **"the best frame"**, the choice our R5 found to be the worse geometry.
- **Phases:** R8 at most. Low for R5.
- **Prediction on the twin:** with a best-frame reference its output inherits that frame's warp (0.6 px RMS on our twin); its geometry against truth should be no better than our best-frame reference, and worse than our median geometry.

### 4.4 Simulators behind the twin (Chimitt and Chan 2020; phase-to-space 2021 and 2022)
- **Citations:** N. Chimitt, S. H. Chan, "Simulating anisoplanatic turbulence by sampling intermodal and spatially correlated Zernike coefficients", Optical Engineering 59(8), 083101 (2020), https://arxiv.org/abs/2004.11210 , DOI https://doi.org/10.1117/1.OE.59.8.083101. Z. Mao, N. Chimitt, S. H. Chan, "Accelerating atmospheric turbulence simulation via learned phase-to-space transform", ICCV 2021, DOI https://doi.org/10.1109/ICCV48922.2021.01449. N. Chimitt, X. Zhang, Z. Mao, S. H. Chan, "Real-time dense field phase-to-space simulation of imaging through atmospheric turbulence", IEEE TCI 8, 1159 to 1169 (2022), DOI https://doi.org/10.1109/TCI.2022.3226293.
- **Verified:** 2020 *abstract read*; the other two *metadata only*.
- **What:** draw spatially correlated Zernike coefficients (tilt included) from a covariance that equates angle-of-arrival correlation (Basu, McCrae and Fiorino) with multi-aperture correlation (Chanan); later work makes it fast and dense.
- **Phases:** R2 (twin realism), R5.
- **Prediction:** an angle-of-arrival tilt field for a 0.254 m aperture decorrelates over roughly D/h radians for a layer at height h; a 5 arcsec (10 px) correlation length then points at a layer near 10 km **(my arithmetic)**. If the real capture's warp correlation length changes between nights with the jet stream's strength, that is consistent; if it is constant, something instrumental (focus, sampling) sets it instead.

### 4.5 AstroDiff (2025) and ASTRA-SR (2026)
- **Citations:** J. Kim, Y. Yuan, X. Zhang, X. Wang, S. Chan, "Astrophotography turbulence mitigation via generative models", https://arxiv.org/abs/2506.02981. X. Ge, Z. Cui, S. Liu, "ASTRA-SR: atmospheric seeing and turbulence restoration for astronomical image super-resolution", https://arxiv.org/abs/2609.26731 (submitted 2026-09-22, CC BY 4.0).
- **Verified:** *abstracts read.*
- **What:** single-frame generative restoration; ASTRA-SR is planetary, trained on spacecraft RAW images degraded through measured turbulence profiles, moving phase screens, exposure-averaged space-variant PSFs and sensor noise.
- **Relevance:** neither registers frames. ASTRA-SR's degradation pipeline is worth a look for R2's twin. Low for R5.

## 5. Solar and professional practice

### 5.1 MOMFBD (van Noort, Rouppe van der Voort and Löfdahl 2005)
- **Citation:** "Solar image restoration by use of multi-frame blind de-convolution with multiple objects and phase diversity", Solar Physics 228, 191 to 215 (2005).
- **Verified:** *metadata only* (DOI https://doi.org/10.1007/s11207-005-5782-z ; the abstract is elided in every index I tried). What follows on its use is from CRISPRED, SSTRED and torchmfbd, which I read.
- **What (as used):** restoration on overlapping subfields of about 5x5 arcsec, where the PSF is taken as isoplanatic, each low-pass filtered by its own SNR, then mosaicked. "Forcing the average of the tip-tilt corrections to be zero yields restored images where the geometric distortions from anisoplanatism are largely removed" (CRISPRED). Residual stretching "on scales smaller than the subfields that MOMFBD cannot compensate for" is removed afterwards by destretching (SSTRED).
- **Grid versus isoplanatic patch:** subfield about the isoplanatic patch for the PSF; the geometry below that scale is left to a destretch.
- **Reference:** the mean tilt (mean geometry), by constraint.
- **Phases:** R5, R7.

### 5.2 CRISPRED (de la Cruz Rodríguez et al. 2015) and SSTRED (Löfdahl et al. 2021)
- **Citations:** J. de la Cruz Rodríguez, M. G. Löfdahl, P. Sütterlin, T. Hillberg, L. Rouppe van der Voort, "CRISPRED: a data pipeline for the CRISP imaging spectropolarimeter", A&A 573, A40 (2015). M. G. Löfdahl et al., "SSTRED: data- and metadata-processing pipeline for CHROMIS and CRISP", A&A 653, A68 (2021).
- **Verified:** both *full text read* (the relevant sections): https://arxiv.org/abs/1406.0202 (DOI 10.1051/0004-6361/201424319) and https://arxiv.org/abs/1804.03030 (DOI 10.1051/0004-6361/202141326).
- **Code / licence:** SSTRED, REDUX (the MOMFBD code) and CRISPEX are "freely available through git repositories"; I did not check their licences.
- **What:** after derotation (from the telescope's pointing model) and cross-correlation co-alignment, "the residual rubber-sheet motions ... are removed by resampling following Shine et al. (1994): divide the images in small subfields and compute the shift that aligns each subfield with a reference". SSTRED adds: "as each interpolation is a blurring operation, we wish to minimize the number of such operations ... SSTRED can combine measured image shifts, rotations, and spatial distortions into a single interpolation operation".
- **Phases:** R5 and **R6**: derotate, align and destretch, and apply them as one resample.
- **Prediction on the twin:** composing the global shift, the dewarp mesh (and, in R6, the derotation) into one Lanczos-3 resample rather than consecutive bilinear ones lowers band 1's error by more than any R5 dewarp setting did. Falsified if a single combined resample and chained resamples tie.

### 5.3 torchmfbd (Asensio Ramos et al. 2025)
- **Citation:** A. Asensio Ramos, C. Díaz Baso, C. Kuckein, S. Esteban Pozuelo, M. G. Löfdahl, "torchmfbd: a flexible multi-object multi-frame blind deconvolution code", A&A 703, A269 (2025).
- **Verified:** *full text read* (destretch and patch sections). https://arxiv.org/abs/2505.10639 ; DOI https://doi.org/10.1051/0004-6361/202555530
- **Code / licence:** https://github.com/aasensio/torchmfbd , **MIT**.
- **What:** before MOMFBD, destretch: pick a reference frame (in their example "the frame with the largest contrast"), estimate the optical flow ("essentially the local tip-tilts") to every other frame by maximising the enhanced correlation coefficient (Evangelidis and Psarakis 2008, https://doi.org/10.1109/TPAMI.2008.113) with Adam, on a coarse N_opt x N_opt grid bilinearly interpolated, "supplemented with a regularization term that penalizes non-smooth estimations of the tip-tilt"; "destretching is not perfect, but any remaining tip-tilt is corrected later during the reconstruction". Patches are merged with cosine apodisation.
- **Reference:** one frame (the sharpest); the later MOMFBD step supplies the mean-tilt constraint.
- **Phases:** R5, R7.
- **Prediction on the twin:** a coarse-grid ECC flow against the *best frame*, as they do it, inherits the best frame's warp (as our R5 found); against the stack, with a smoothness weight set from the twin's warp covariance, it lands at the P2 ceiling. This is P2 with an off-the-shelf optimiser.

### 5.4 November and Simon 1988; Shine et al. 1994 (local correlation tracking and destretch)
- **Citations:** L. J. November, G. W. Simon, "Precise proper-motion measurement of solar granulation", ApJ 333, 427 (1988). R. A. Shine, A. M. Title, T. D. Tarbell, K. Smith, Z. A. Frank, "High-resolution observations of the Evershed effect in sunspots", ApJ 430, 413 (1994).
- **Verified:** November and Simon *abstract read* (https://doi.org/10.1086/166758); Shine et al. *metadata only* (https://doi.org/10.1086/174416), cited by CRISPRED as its destretch method.
- **What:** "the time average of the spatially localized cross correlation is shown to provide a measure of the displacement that is not biased by atmospheric seeing." That is ensemble correlation (5.6), applied to the time-averaged geometry.
- **Phases:** R5 (the mean geometry).
- **Prediction on the twin:** estimating the reference frame's own warp by averaging each point's correlation *surfaces* over all frames, and then finding one peak, allows much smaller patches (8 px) than a median of per-frame peaks, because the per-frame peak at 8 px is outlier-prone and a surface average is not. The median-geometry gain (8 percent) should grow at 8 to 12 px patches with surface averaging; if it does not, the reference frame's own noise (common to every frame) is the limit, which P1 removes.

### 5.5 Schad and Lin 2017 (the DST destretch ladder)
- **Citation:** T. Schad, H. Lin, "Infrared imaging spectroscopy using massively multiplexed slit-based techniques and sub-field motion correction", Solar Physics 292, 158 (2017).
- **Verified:** *method section read.* https://arxiv.org/abs/1809.05132 ; DOI https://doi.org/10.1007/s11207-017-1188-y
- **What:** destretching "refers to a range of methods that enforce smooth variation of the spatial content image relative to a reference image by doing sub-field local correlation and interpolation". Using the Rimmele (1994) algorithms at the Dunn Solar Telescope, the reference is "the running average of 30 consecutive images (3.15 seconds)", and "the destretching procedure uses an iterative approach with progressing smaller sub-field sizes (12, 8, and 4 arcsec)" at 0.128 arcsec/px (about 94, 63 and 31 px).
- **Grid:** coarse to fine, each pass on the frame already corrected by the last; at our scale the ladder is 24, 16 and 8 px.
- **Reference:** a running temporal average.
- **Phases:** R5.
- **Prediction on the twin:** a three-pass destretch (24, 16, 8 px windows, each against the stack, each on the frame warped by the previous passes, one resample at the end) recovers more in the applied field than any single-scale pass. Falsified if the 8 px pass adds nothing; then 8 px windows are noise-limited on our frames and the ladder should stop at 16.

## 6. PIV, the neighbouring field that measures dense displacement fields

- **Meinhart, Wereley and Santiago 2000**, "A PIV algorithm for estimating time-averaged velocity fields", J. Fluids Eng. 122(2), 285 to 289, https://doi.org/10.1115/1.483256 (*abstract read*): "the quality of the velocity measurements can be dramatically increased by averaging a series of instantaneous correlation functions, before determining the location of the signal peak, as opposed to ... estimating instantaneous velocity fields first and then averaging"; it also "allow[s] smaller interrogation spots". **Prediction on the twin (P4):** for the per-frame warp, which the twin keeps coherent for several frames (0.9 per frame), averaging each point's correlation surfaces over the frames either side before the peak should beat averaging the displacements (what `WarpPoolFrames` does, which gained nothing), at 8 to 16 px patches. If it also gains nothing, the per-point error is common to neighbouring frames (the reference's noise), and P1 is the fix.
- **Scarano 2002**, "Iterative image deformation methods in PIV", Meas. Sci. Technol. 13, R1 to R19, https://doi.org/10.1088/0957-0233/13/1/201 (*abstract read*): window offset and continuous window deformation compensate the displacement gradient and peak locking; "order-of-magnitude improvement on the precision of the particle image displacement at a sub-pixel level when the image deformation is applied"; the interpolation method, predictor-corrector and peak fit are crucial choices. **Prediction:** re-measuring each point on the frame already warped by the current mesh (one or two iterations) brings the regression slope of measured on true shift toward one; falsified if the slope does not move.
- **Kähler, Scharnowski and Cierpka 2012**, "On the resolution limit of digital particle image velocimetry", Exp. Fluids 52, 1629 to 1639, https://doi.org/10.1007/s00348-012-1280-x (*abstract read*): even single-pixel ensemble correlation is limited by the size of the particle images, not the pixel. For us the "particle" is the blurred feature; windows smaller than the seeing-blurred feature scale gain nothing.
- **Keane and Adrian 1992** (https://doi.org/10.1007/BF00384623), **Westerweel 1997** (https://doi.org/10.1088/0957-0233/8/12/002) and **Westerweel and Scarano 2005** (https://doi.org/10.1007/s00348-005-0016-6): *metadata only*; the theory of window correlation, the fundamentals of digital PIV, and outlier detection for PIV vectors. Not read, so no claim is made on their content.

## 7. Amateur software

### 7.1 PlanetarySystemStacker (Rolf Hempel)
- **Citation / verified:** https://github.com/Rolf-Hempel/PlanetarySystemStacker (the author is Rolf **Hempel**, not Hempelmann). *Documentation and code read:* `Documentation/algorithm_summary.pdf` (PSS 0.9.8), `configuration.py`, `stack_frames.py`, `alignment_points.py`.
- **Licence:** **GPL-3.0-or-later** (`License/GNU-General-Public_license-3.txt`, and every source header: "either version 3 of the License, or ... any later version"). GitHub's API does not detect it. Reading it for ideas is fine; copying its code into TianWen would bring the GPL.
- **What:**
  - Frames are ranked on a Gaussian-blurred monochrome copy (`frames_gauss_width = 7`), which is also what shifts are measured on, "to avoid spurious local minima caused by pixel noise".
  - Global alignment is to the best frame; four methods, with "Translation (not recommended): Phase-correlation" and the default "MultiLevelCorrelation" (normalised cross-correlation, stride 2 then stride 1 within 4 px).
  - The **mean frame** is the average of the best 5 percent (`align_frames_average_frame_percent = 5`), globally aligned only; each alignment point's local shift is measured **against the mean frame**, with a penalty term pulling the coarse phase toward zero shift (`alignment_points_penalty_factor`, a prior on the displacement).
  - Alignment boxes are 48 px (`half_box_width = 24`), patches 72 px, points about 54 px apart on a staggered grid; the best 10 percent of frames are chosen per point.
  - Local shifts are whole pixels unless drizzling (`local_search_subpixel = False`); patches are shifted rigidly (`remap_rigid`) and blended with tent weights; there is no dense displacement field.
- **Scale versus our warp:** a 48 px box is five times our warp's correlation length; PSS corrects only large-scale distortion and relies on per-point frame selection.
- **Reference:** temporal average of the best frames.
- **Phases:** R5 (baseline), R4 (per-point selection).
- **Prediction on the twin:** PSS's parameters (48 px boxes, whole-pixel shifts) recover under 3 percent of our 10 px warp; PSS can win on the twin only through selection and through avoiding resampling blur, not through dewarping. A useful control for part 3.

### 7.2 AutoStakkert! (Emil Kraaikamp)
- **Verified:** site https://www.autostakkert.com/ (author, guides); the AS!2 planetary manual, E. Kraaikamp, 2012-10-05, https://www.astrokraai.nl/software/manual/as2_planet.html (*read*); AS!3 release notes (3.1.4, 2018-06-26), http://www.astrokraai.nl/software/secret_2018_JLD.php (*read*).
- **Code / licence:** no source published; the pages I read state no licence terms.
- **What:** "The reference frame is created from the best set of frames automatically" (auto size, quality based), and alignment points are aligned "to the reference frame that was previously created". On point size: "Use smaller sizes for fine high-quality details. Use larger boxes for noisy images." AS!3 adds **Double Stack Reference** ("when checked, it will automatically reprocess each recording with the previous stack as reference") and **Multi-Scale** points (extra, always larger, points placed alongside the chosen size).
- **Reference:** a stack of the best frames, optionally rebuilt from the first stack: P1 in practice.
- **Phases:** R5. TianWen already reads AutoStakkert sessions (`AutoStakkertSession.cs`) and part 3 compares with its alignment.
- **Prediction on the twin:** AutoStakkert's point shifts, read from a session on the twin, sit closer to the mean geometry than our best-frame reference (its reference is a stack), roughly where our median geometry sits; Double Stack Reference moves it a little further, not a lot.

### 7.3 RegiStax (Cor Berrevoets)
- **Verified:** https://www.astronomie.be/registax/ (read): freeware; RegiStax 6's final update was 6.1.0.8 (May 2011) and development ended; its wavelet sharpening continues as waveSharp (https://codeberg.org/Corbee/waveSharp3.0). No published description of its alignment internals found. PSS notes its own sharpening "reduces to the one used by Registax 6" for one parameter choice. Low relevance for R5.

### 7.4 Resampling (finding 4)
- **Fruchter and Hook 2002**, "Drizzle: a method for the linear reconstruction of undersampled images", PASP 114, 144 to 152, https://doi.org/10.1086/338393 (*metadata only*).
- **Arithmetic (mine):** bilinear interpolation at fractional offset p is the two-tap kernel (1 - p, p), whose variance is p(1 - p); averaged over a uniform p it is 1/6 px^2 per axis (σ ≈ 0.41 px), against the warp's 0.36 px^2. Whole-pixel shifting (PSS) instead leaves a uniform misregistration of variance 1/12 px^2. A Lanczos-3 kernel adds far less. So a bilinear dewarp must remove more than 46 percent of the warp's variance (a 26 percent RMS reduction) before it beats a whole-pixel stack, which the P2 ceiling (Appendix A) does not reach at today's point error and only just reaches with a stacked reference. **Prediction:** bilinear against Lanczos-3 moves band 1 more than every R5 dewarp setting did; the plan's own note (bilinear "costs about 1 px FWHM in quadrature") is consistent with this.

## 8. Synthesis: what the literature says about our R5 numbers

1. **Point error.** On an 8-bit frame the whitened correlation loses by weighting (2.5); plain correlation is near ML where the SNR is high. Whether 0.35 px is near the bound is one computation away (2.4). Apodisation shrinks shifts (2.6), and a plane in the patch biases them near the limb.
2. **Pooling found nothing** although the twin keeps its warp coherent at 0.9 per frame. If the per-point error were independent between frames, pooling over plus or minus 2 frames would cut it by close to sqrt(5). That it does not says the error is mostly **common to neighbouring frames**: the reference frame's own noise and warp, window-averaging bias, apodisation bias. None of those average away over frames; a stacked reference (P1) removes the first two, deformation iteration (6, Scarano) the third.
3. **The median geometry's 8 percent** is Lao's and Halder's step. Its size is limited by the same common-mode terms; a stacked reference and correlation-surface averaging (5.4) should grow it.
4. **The mesh's 1 to 3 percent** sits under what Hardie's filter model allows at best (3 to 8 percent, Appendix A) for a 12 to 48 px Gaussian blend of points 8 to 12 px apart on a warp that decorrelates by 9 px. The mesh, not the physics, set the kill line. A matched interpolator (kriging with the twin's covariance, or TMFS's PSD-shaped prior) at spacing at most the correlation length is the remedy.
5. **Stack payoff is capped anyway.** Even at the P2 ceiling (about 15 to 30 percent RMS), the warp's blur variance falls from 0.36 to about 0.18 to 0.26 px^2, the same order as bilinear's 0.17 px^2. The literature's answer (Zhu and Milanfar, Hardie) is to register partly and **deconvolve the residual** with a kernel that knows how much registration removed. For R7 that kernel is a Gaussian of the residual warp variance, known on the twin.
6. **R6 coupling (my arithmetic).** Jupiter's System II period is 9 h 55.5 min, so at the centre of a 100 px disk the surface moves 2π·50 / 595.5 ≈ 0.53 px per minute. Any temporal-average reference (median geometry, a stack, PSS's mean frame) over more than about a minute is smeared by more than the warp itself unless it is derotated first or built from a short window. On the real capture, the median-geometry offsets measured from the first and second halves should differ by a rotation-shaped field (x-shift largest at the central meridian, falling as cos(longitude) toward the limb) of about 0.5 px per minute of separation.

## 9. Ranked: the three approaches most worth measuring next

A zero-cost **diagnostic first** (P0), on the calibrated twin, with the existing `planetary-dewarp` readings: split every point's error against truth into (a) its time mean over the capture (common-mode) and (b) the frame-to-frame remainder, and regress measured on window-averaged true shift (slope and scatter). If (a) dominates, rank 2 below becomes rank 1. If the slope is well below the window-averaging prediction, add Scarano-style iteration to rank 3.

1. **Dense points with a statistically matched interpolator (P2).** Literature: Hardie 2021 (the model that predicts the loss), Zhu and Milanfar 2013 and torchmfbd 2025 (dense fields with smoothness), TMFS 2026 (a warp prior shaped by its own PSD), Shimizu 2008 (information-weighted shrinkage), the DST ladder (spacing down to the correlation length).
   - *Test:* 16 px patches 4 px apart, applied field on 4 px nodes; (i) the existing Gaussian blend with `--mesh-influence 4` instead of 12 or 48; (ii) kriging with the twin's known warp covariance and the measured point error variance (a linear solve per frame, or a Wiener filter in the Fourier domain on the node grid).
   - *Prediction (my arithmetic, Appendix A):* the applied field recovers about 11 to 19 percent RMS with the 4 px blend and 15 to 22 percent with kriging, against 3 percent today; with the present reference expect somewhat less, since the model assumes a noise-free one. Halving `influence` from 12 to 4 alone should at least double the mesh's recovery.
   - *Falsified if* the applied field stays at 3 percent or below with kriging at 4 px spacing. Then the loss is in how the field is applied (sampling direction, time index, or the resample), not in the estimate, and that is worth knowing before R7.
2. **Re-register every frame against the mean-geometry stack, and iterate once (P1).** Literature: Zhu and Milanfar, Hardie BMWF, Shimizu, Mao and Gilles, Gratadour (joint ML), Lao (template), Halder (iFRTAAS), PSS (mean frame), AutoStakkert (reference stack, Double Stack Reference), MOMFBD (zero mean tilt), DST destretch (running mean).
   - *Test:* build the stack from the current pass on the median geometry (or a global-only stack, which is already on the mean geometry), use it as every point's reference, re-read the points, rebuild, repeat once. Derotate first, or use windows under about 60 s (R6 coupling).
   - *Prediction:* the per-point error at 16 px falls from 0.35 px toward 0.25 px (one noisy image instead of two, if the error is noise-limited); the common-mode part of P0 falls to near zero; the points' recovery on the true geometry rises above 19 percent; the second iteration adds under a fifth of the first's gain; mean and median geometry then agree to under one percent.
   - *Falsified if* the per-point error stays at 0.35 px against the stack. Then the error is bias (apodisation, window averaging, peak fit), and rank 3 is where the gain is.
3. **Make each point's estimate efficient: ML weighting, an unapodised square-difference match, and correlation-surface pooling (P3 and P4).** Literature: Knapp and Carter 1976, Löfdahl 2010, Robinson and Milanfar 2004 and Pham 2005 (the bound to compare against), Meinhart 2000 and November and Simon 1988 (average surfaces, not peaks), Scarano 2002 (deformation iteration).
   - *Test:* compute the Cramér-Rao bound per point on the twin; then compare, at 8, 12 and 16 px: plain (today), ML-weighted S / (N (N + 2S)), and mean-and-plane-subtracted SDF with a 2-D quadratic fit; then pool correlation surfaces over plus or minus 2 frames.
   - *Prediction:* ML weighting and SDF each cut the 16 px error by 10 to 25 percent and more at 8 to 12 px; surface pooling helps where displacement pooling did not, but only after rank 2 has removed the common-mode error.
   - *Falsified if* plain correlation is already within about 1.2 times the bound (then no estimator change can help), or if every variant ties.

**Control for all three:** part 3's resampling comparison (bilinear, Lanczos-3, whole-pixel, one combined resample), because at this warp size the resample's own blur is as large as anything a dewarp can win.

## 10. Summary table

| Work | Verified link | Relevance (phase) | Do we use it |
|---|---|---|---|
| Kuglin and Hines 1975, phase correlation | https://www.semanticscholar.org/paper/e4482d429ddd155f4c5b1299dec71d4faa10d241 (record only) | Medium, R5: the method our `whiten` implements | Yes (whitened option); R5 found it worse |
| Foroosh et al. 2002, sub-pixel phase correlation | https://pubmed.ncbi.nlm.nih.gov/18244623/ | Low-medium, R5 | No |
| Guizar-Sicairos et al. 2008, upsampled DFT | https://opg.optica.org/abstract.cfm?URI=ol-33-2-156 | Low-medium, R5, R6 channel alignment | No (parabolic vertex instead) |
| Robinson and Milanfar 2004, registration CRB | https://users.soe.ucsc.edu/~milanfar/publications/journal/MotionPerformanceFinal.pdf | High, R5: decides whether estimator work can pay | No; candidate (P3) |
| Pham et al. 2005, optimal registration estimators | https://doi.org/10.1117/12.603304 | High, R5 | No; candidate (P3) |
| Knapp and Carter 1976, ML generalised correlation | https://doi.org/10.1109/TASSP.1976.1162830 | High, R5: explains finding 1 | Partly (plain correlation); ML weight is a candidate |
| Gratadour et al. 2005, joint ML reference and shifts | https://doi.org/10.1051/0004-6361:20042188 | High, R5 | No; candidate (P1) |
| Löfdahl 2010, solar shift algorithms | https://arxiv.org/abs/1009.3401 | High, R5: SDF plus 2-D quadratic, apodisation bias, bias mismatch | No (Hann-windowed FFT correlation) |
| scikit-image registration docs | https://scikit-image.org/docs/stable/api/skimage.registration.html | Medium, R5: states finding 1 | No |
| Tong et al. 2019, Fourier correlation review | https://doi.org/10.1109/JSTARS.2019.2937690 | Low | No |
| Fraser et al. 1999, shift maps | https://doi.org/10.1364/JOSAA.16.001751 | Medium, R5 | Partly (per-point shifts) |
| Shimizu et al. 2008, B-spline to estimated frame | https://doi.org/10.1109/CVPR.2008.4587525 | Medium, R5: information-weighted shrinkage | No |
| Zhu and Milanfar 2013 | http://users.soe.ucsc.edu/~milanfar/publications/journal/manuscript_turbulence.pdf | High, R5, R7 | Partly (median geometry); not the stack reference |
| Hirsch et al. 2010, efficient filter flow | https://doi.org/10.1109/CVPR.2010.5540158 | Low for R5, medium for R7 | No |
| Mao and Gilles 2012; IPOL 2013 (BSD-2 code) | https://www.ipol.im/pub/art/2013/46/ | Medium, R5: iterate to the latent | No |
| Lou et al. 2013 | https://doi.org/10.3934/ipi.2013.7.839 | Low | No |
| Anantrasirichai et al. 2013, CLEAR | https://doi.org/10.1109/TIP.2013.2249078 | Low-medium, R4/R8 fusion | No |
| Halder et al. 2014, iFRTAAS | https://doi.org/10.1364/AO.53.005576 | High, R5: our median geometry, iterated | Partly (one pass, median) |
| Hardie et al. 2017, BMWF | https://doi.org/10.1117/1.OE.56.7.071503 | High, R5, R7 | No; candidate (R7 residual-tilt Wiener) |
| Hardie et al. 2021, tilt correlation statistics | https://arxiv.org/abs/2108.00528 | High, R5 model, R2 twin anisotropy | No; candidate (predicts our numbers) |
| Mao, Chimitt and Chan 2020 | https://arxiv.org/abs/2009.00071 | Medium, R5, R4 | No |
| Lao et al. 2024, diffeomorphic template | https://arxiv.org/abs/2405.03662 | High, R5, R6 (flow inversion) | Yes in spirit (median, not mean) |
| TMT 2024 | https://arxiv.org/abs/2207.06465 | Low, no licence on code | No |
| DATUM, CVPR 2024 | https://arxiv.org/abs/2401.04244 | Low, no licence on code | No |
| MambaTM, CVPR 2025 (MIT) | https://arxiv.org/abs/2504.02697 | Low | No |
| TMFS, CVPR 2026 | https://openaccess.thecvf.com/content/CVPR2026/papers/Xie_Physically-Grounded_Turbulence_Mitigation_with_Frame-Shared_Degradation_Parameters_CVPR_2026_paper.pdf | High as a design, R5, R7 | No; candidate (PSD-shaped warp prior) |
| DIPLI, Sci. Rep. 2026 (MIT) | https://doi.org/10.1038/s41598-026-47300-4 | Low, R8 | No |
| Chimitt and Chan 2020 simulator | https://arxiv.org/abs/2004.11210 | Medium, R2 twin | No |
| AstroDiff 2025; ASTRA-SR 2026 | https://arxiv.org/abs/2506.02981 ; https://arxiv.org/abs/2609.26731 | Low (single frame); ASTRA-SR's degradation for R2 | No |
| MOMFBD 2005 | https://doi.org/10.1007/s11207-005-5782-z | Medium, R5 (zero mean tilt), R7 | No |
| CRISPRED 2015 | https://arxiv.org/abs/1406.0202 | Medium, R5, R6 | No |
| SSTRED 2021 | https://arxiv.org/abs/1804.03030 | High for resampling, R5, R6 | No; candidate (one combined resample) |
| torchmfbd 2025 (MIT) | https://arxiv.org/abs/2505.10639 | Medium, R5, R7 | No |
| November and Simon 1988 | https://doi.org/10.1086/166758 | Medium, R5 (mean geometry by averaged correlation) | No |
| Schad and Lin 2017, DST destretch ladder | https://arxiv.org/abs/1809.05132 | Medium, R5 | No |
| Meinhart et al. 2000, ensemble correlation | https://doi.org/10.1115/1.483256 | High, R5 (pooling surfaces) | No (we pool displacements) |
| Scarano 2002, iterative deformation | https://doi.org/10.1088/0957-0233/13/1/201 | Medium, R5 | No |
| Kähler et al. 2012 | https://doi.org/10.1007/s00348-012-1280-x | Low-medium, R5 (smallest useful window) | No |
| PlanetarySystemStacker (GPL-3.0-or-later) | https://github.com/Rolf-Hempel/PlanetarySystemStacker | High as baseline, R5, R4 | No (ideas only; mean-frame reference not adopted) |
| AutoStakkert! (AS!2 manual, AS!3 notes) | https://www.astrokraai.nl/software/manual/as2_planet.html | High as baseline, R5 | Partly (sessions read; its stacked reference not adopted) |
| RegiStax | https://www.astronomie.be/registax/ | Low | No |
| Fruchter and Hook 2002, drizzle | https://doi.org/10.1086/338393 | Medium, resampling | Yes (Bayer drizzle, with the AP mesh) |

## 11. Leads I could not verify, or verified only in part

- **Not verified at all:** Rimmele (1994), the DST destretch algorithms (only through Schad and Lin); Henriques (2012), CRISPRED's post-MOMFBD dewarp (only through CRISPRED); Yi and Molowny Horas (1992), large-field remapping (only through Löfdahl); Smithson and Tarbell (1977), the intensity-trend bias (only through Löfdahl); Tahtali, Lambert and Fraser's original FRTAAS paper; any DKIST-specific destretch description (searches found none); AutoStakkert!4 documentation beyond a French tutorial PDF I did not open; the internals of RegiStax's alignment.
- **Verified but full text not read:** Kuglin and Hines 1975 (not online); MOMFBD 2005 (abstract elided everywhere, paywalled); Hirsch et al. 2010 (open copy refused automated access); Anantrasirichai et al. 2013 (open copy refused automated access, so its registration reference is unknown to me); Hardie et al. 2017 (SPIE blocked automated access; its content here comes from the 2021 paper's recap); Fraser 1999, Shimizu 2008, Halder 2014, Lou 2013, Gratadour 2005, Tong 2019 (abstracts only); Keane and Adrian 1992, Westerweel 1997, Westerweel and Scarano 2005, Shine et al. 1994, the two phase-to-space simulator papers, Evangelidis and Psarakis 2008 and Fruchter and Hook 2002 (metadata only).
- **Dead code link:** Zhu and Milanfar's code (the Milanfar software page's link redirects to the school's home page as of 2026-09-30).
- **Licence unknown:** TMT and DATUM repositories have no licence file (treat as all rights reserved); AutoStakkert states none on the pages read; SSTRED, REDUX and CRISPEX not checked.

## Appendix A. The back-of-envelope behind sections 1, 8 and 9 (my arithmetic)

- **Warp model:** isotropic Gaussian covariance σ_w^2 exp(-r^2 / (2 ℓ^2)) with σ_w = 0.6 px per axis. The calibrated twin's correlation of +0.05 at 9 px implies ℓ ≈ 9 / sqrt(6) ≈ 3.7 px, so I used ℓ = 4 and 5 px (7 px as an optimistic bound). The twin's exact covariance should replace this.
- **Point model:** each point measures the box average of the warp over a W x W window, with Gaussian error σ_e = 0.35 px x (16 / W) (the Cramér-Rao 1/W scaling for uniform texture from the measured 16 px figure), errors of overlapping windows correlated in proportion to their overlap, and a noise-free reference on the true (mean) geometry. Single frame, no temporal pooling. A second run used 0.25 px at 16 px (the "stacked reference" case).
- **Metric:** RMS reduction over every pixel of a central 12 x 12 px region (the applied field, like `planetary-dewarp`'s mesh sampled every 4 px), 1 - sqrt(residual variance / σ_w^2).
- **Estimators compared:** the present Gaussian blend (Nadaraya-Watson weights exp(-d^2 / (2 reach^2)), normalised with the 0.25 regularisation in the denominator, evaluated at each pixel, no node grid), and the MMSE linear estimate (kriging).

| ℓ | error at 16 px | Grid (window, spacing) | Blend, reach 48 | Blend, reach 12 | Blend, reach 4 | Kriging |
|---|---|---|---|---|---|---|
| 4 | 0.35 | 32 px, 12 px | 3.3 % | 3.7 % | 3.4 % | 4.2 % |
| 4 | 0.35 | 16 px, 8 px | 1.3 % | 4.5 % | 10.7 % | 12.6 % |
| 4 | 0.35 | 16 px, 4 px | 1.7 % | 4.8 % | 11.2 % | 15.3 % |
| 4 | 0.25 | 16 px, 4 px | 2.6 % | 6.2 % | 16.0 % | 22.4 % |
| 5 | 0.35 | 32 px, 12 px | 5.8 % | 6.6 % | 6.4 % | 7.6 % |
| 5 | 0.35 | 16 px, 8 px | 3.1 % | 8.1 % | 18.0 % | 19.6 % |
| 5 | 0.35 | 16 px, 4 px | 3.8 % | 8.6 % | 18.9 % | 22.2 % |
| 5 | 0.25 | 16 px, 4 px | 4.7 % | 10.0 % | 24.1 % | 31.0 % |

- R5 part 2 measured 1 percent for (32 px, 12 px, reach 48, 24 px nodes) and 3 percent for (16 px, 8 px, reach 12, 8 px nodes). The model's 3.3 to 8.1 percent for those two configurations bounds them from above, as it should, since it ignores the reference's own noise and warp, apodisation bias and the node grid's bilinear smoothing.
- **Hardie's correction factor for a Gaussian blend alone** (noise-free points everywhere): for a blend of width s on the covariance above, the recovered variance fraction is 2 ℓ^2 / (ℓ^2 + s^2) - ℓ^2 / (ℓ^2 + 2 s^2). At ℓ = 5 it is 0.016 for s = 48, 0.131 for s = 16, 0.398 for s = 8 and 0.667 for s = 5; at ℓ = 4 it is 0.010, 0.087, 0.289 and 0.538.
- **Resampling:** bilinear adds p(1 - p) px^2 at offset p, 1/6 on average; whole-pixel shifting leaves 1/12; the warp is 0.36 px^2 per axis.
- **Rotation:** 2π x 50 px / 595.5 min ≈ 0.53 px per minute at the centre of a 100 px Jupiter.
- **Layer height:** a 5 arcsec tilt decorrelation for D = 0.254 m gives h ≈ D / θ ≈ 10.5 km.
