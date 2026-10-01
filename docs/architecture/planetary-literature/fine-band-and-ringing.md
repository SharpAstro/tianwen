# Theme D: the finest band's blur without a point source, and sharpening without a ring at the limb

> One of four reviews behind [the planetary literature synthesis](../planetary-literature.md), made for `docs/plans/planetary-restoration.md` on 2026-10-01, after R8. Its "our" and "we" are TianWen. Three searches were made in parallel: ASTRA-SR re-read with its references and citations, the blur's finest band measured from an extended object, and sharpening a disk without a dark ring. Each entry says how far its source was read: **read** (the method or the code), **abstract**, or **metadata** (the citation checked, the content from an abstract page or a search index). A number marked **derived** is arithmetic, not a published figure.

## The two problems R8 left

- **The finest band's blur is not measured.** The limb fit's kernel holds within about 5 % at 0.06 to 0.25 cycles a pixel, but at 0.3 it passes 0.05 where the twins' true transfer is 0.28: there it is the fitted model's extrapolation. Wiener-derived wavelet gains with the true kernel come within 3 to 11 % of the per-band ceiling; with the limb's they put 11 to 19 on the finest layer. No Galilean moon is in the capture's field (#1120).
- **Sharpening digs a dark ring just outside the limb**, below the sky: a sixth to a third of the disk's brightness for the shipped presets. Richardson-Lucy and L1-L2 with the sky held at zero do not; AutoStakkert's own sharpening of the same capture lifts the fine bands 2 to 3.7 times with no ring at all.

## What the three searches agree on

- **Any ratio of frames cancels a static convolution, and a static phase in the pupil shows only to a method that knows something about the object (the limb, a shadow, its spectrum's statistics) or about the pupil (multi-frame blind deconvolution, phase diversity, a star).** The solar and adaptive-optics groups close it the same way: a physical model of the PSF and one outside constraint. A fit to a limb alone is degenerate.
- **The 0.05 is the limb fit's freedom, not the kernel's form** (**derived**, one scratch check): a transfer built from the obstructed pupil, the pixel, the resampling kernel and the warp, fitted in FOURIER space at 0.06 to 0.25 with a Gaussian core and a fixed halo, lands within about 12 % at 0.3. In image space the core and the wing trade along a valley of equal fits, as Wedemeyer-Boehm 2008 and Yeo et al. 2014 found on the Sun.
- **A ring below the sky is a transfer above one.** A non-negative unit-sum kernel has a transfer of at most 1 in magnitude at every frequency, so a restoration whose end-to-end transfer against the truth exceeds 1 anywhere has a composite kernel with a negative lobe, and over a bright disk on a dark sky that lobe is the ring (**derived**, from the definition). The presets' band 3 at 1.3 to 2.0 times the truth, and the derived gains' band 1 at 2.1 with the limb's kernel, are such cases. Restoring toward the pupil's own diffraction PSF, which is non-negative, never past it, is ring-free by construction: Magain, Courbin and Sohy 1998's deconvolution to a target PSF.

## ASTRA-SR re-read

- **Ge, Cui and Liu**, "ASTRA-SR: Atmospheric Seeing and Turbulence Restoration for Astronomical Image Super-Resolution", arXiv:2609.26731v1 (2026-09-22), https://arxiv.org/abs/2609.26731. **Read** in full from the TeX source, `refs.bib` and the figures' text layers. No code released.
  - The network never estimates a kernel and is never given one ("Inference receives only y"). Its robustness is the training spread and bounds on its output: a global and a local gain each held to (0.5, 1.5), and Fourier magnitudes changed by a bounded factor in 32 px windows with the phase kept.
  - Figure 1's text reads "Fine sampled 0.25, 132x132" and "Downsampled 33x33": if those are its training kernels they reach 16 px, and a (1 + (r/10)^2)^-1.5 wing like our twin's scatter has 53 % of its light beyond that (**derived**), so its model never meets the trade between the wing and band 1 that our limb fit runs into.
  - No limb or ringing metric; the loss has a 0.1-weighted gradient L1 term and no positivity term. Its real Jupiter and Moon results are judged by eye.
  - **Cited by** nothing found: OpenAlex (W7214109006) reports 0 citations, Semantic Scholar's citations list is empty, a search on the identifier found none. Google Scholar was not queried.
  - Its references already in theme C are not repeated. New and relevant: **Meanti, Ryckeboer, Arbel and Mairal**, ICCV 2025, arXiv:2506.14605 (abstract; code https://github.com/inria-thoth/ddm4ip, MIT): the blur learned so that clean samples degraded by it match the degraded data's distribution, a kernel from the object's statistics with no edge. SelfDeblur (Ren et al. 2020) and BlindDPS (Chung et al. 2023), CVPR (metadata; code without a licence), estimate the kernel jointly with the image but are as blind in band 1 as we are on a disk with no point source.

## The finest band from an extended object

**Solar speckle and multi-frame blind deconvolution**
- **Woeger, von der Luehe and Reardon 2008**, A&A 488, 375, doi:10.1051/0004-6361:200809894; **Woeger and von der Luehe 2007**, Appl. Opt. 46, 8015 (abstracts): analytic speckle transfer models for adaptive-optics correction and anisoplanatism (KISIP). How KISIP treats a static telescope phase was not found.
- **Peck, Woeger and Marino 2017**, A&A 607, A83 (abstract): photometric precision when the speckle transfer's inputs are wrong; amplitude rests on the r0 estimate.
- **Scharmer et al. 2010**, A&A 521, A68, arXiv:1007.1236 (abstract and introduction): multi-frame blind deconvolution's contrast falls short; compensating the unfitted high-order modes statistically from r0 explains only part, the rest is put down to straylight. The best solar pipelines MODEL the static high-frequency transfer; they do not measure it.
- **Loefdahl and Hillberg 2022**, A&A 668, A129, doi:10.1051/0004-6361/202244123 (abstract and introduction): "statistical diversity" adds the random high-order tails to MFBD and can include fixed instrumental aberrations.
- **Asensio Ramos et al. 2025**, A&A 703, A269, arXiv:2505.10639 (sections 3 and 4 read; code https://github.com/aasensio/torchmfbd, MIT): its configuration has a central obscuration, a spider and phase-diversity channels; its Fourier filter cuts at 0.7 to 0.9 of the diffraction limit. **Asensio Ramos 2026**, arXiv:2605.11980 (abstract): a marginal MOMFBD in torchmfbd that keeps noise out of the high-order modes.
- **Thelen, Paxman, Carrara and Seldin 1999**, JOSA A 16, 1016, doi:10.1364/JOSAA.16.001016 (abstract): a MAP estimate of the fixed aberrations, the dynamic ones and the object from phase-diverse speckle data. **Mugnier et al. 2008**, Opt. Express 16, 18406, doi:10.1364/OE.16.018406 (abstract): in long-exposure phase diversity the turbulence averages into a convolution, so it estimates the static aberrations alone. **Blanc, Mugnier and Idier 2003**, JOSA A 20, 1035, doi:10.1364/JOSAA.20.001035 (abstract): marginal phase diversity on an extended object.

**The kernel from the power spectrum**
- **Fetick, Mugnier, Fusco and Neichel 2020**, MNRAS 496, 4209, doi:10.1093/mnras/staa1813, arXiv:2006.11160 (sections 2 to 4.7 read): a physical PSF model estimated by marginalising over the object with Conan's spectrum k / (1 + (f/rho0)^p). Left free, the object's slope trades against the transfer's: p came out 3.27 against a true 2.91 and the PSF too sharp (Strehl 17 % against 11.5 %). Supply p from the object's category: an error of 0.1 in p moves r0 by about 1 cm in 15. R8 part 2's free power-law fit is the case shown to fail.
- **Blanco and Mugnier 2011**, Opt. Express 19, 23227 (abstract): a joint estimate fails even with a few PSF parameters; the marginal one is consistent.
- **Goldstein and Fattal 2012**, ECCV, LNCS 7576, 622, analysed in **Anger, Facciolo and Delbracio 2018**, IPOL 8, doi:10.5201/ipol.2018.211 (method skimmed; code MIT per the IPOL entry): whiten, a power law corrected for strong edges, then phase retrieval. The edge correction matters here: a disk's spectrum is dominated by its limb.
- **Choi and Showman 2011**, Icarus 216, 597, doi:10.1016/j.icarus.2011.10.001, arXiv:1301.6132 (abstract): Cassini cloud spectra go as -5/3, then -3 above harmonic degree about 200. **Cosentino, Simon and Morales-Juberias 2019**, JGR Planets 124, 1204, doi:10.1029/2018JE005762 (abstract): OPAL gives k^-5/3 from k about 30 to 1,000. At 0.3 cycles a pixel on a 100 px disk the degree is about 95 at the centre and higher toward the limb (**derived**), so a break in slope is near.
- **Danilovic et al. 2008**, A&A 484, L17, doi:10.1051/0004-6361:200809857, arXiv:0804.4230 (abstract): a simulated scene with trusted statistics, through the computed pupil (obstruction and spider) and a slight defocus, reproduces Hinode's observed contrast: the fine transfer pinned by the object's statistics, not by an edge.

**Edges and limbs**
- **Wedemeyer-Boehm 2008**, A&A 487, 399, arXiv:0804.4536 (abstract and section 4): Mercury-transit and eclipse limbs fitted with diffraction convolved with a Voigt; the minimum-error trench follows lines of constant Strehl, broken only by the pre-flight Strehl.
- **Yeo et al. 2014**, A&A 561, A22, arXiv:1310.4972 (sections 2.3 and 2.4): blurring the assumed Venus edge by 0.2 to 0.4" narrowed the retrieved core; the object's assumed edge trades directly against the core width. Gaussian sums give an unphysical transfer above Nyquist.
- **Choi, Xiong and Wang 2014**, IEEE TGRS 52, 270, doi:10.1109/TGRS.2013.2238545 (abstract): the transfer from the lunar edge, its edge profile oversampled by aligning many scans with sub-pixel fits, within 2 % of the onboard calibrator. **Caron and Rollins 2020**, JARS 14, 032408 (abstract): the Moon's albedo flattened with an outside map before the edge profile is built. **Richard et al. 2012**, Med. Phys. 39, 4115, doi:10.1118/1.4725171 (abstract): the transfer from a circular disk's edge, validated against a wire. **Joshi, Szeliski and Kriegman 2008**, CVPR (abstract): predict the sharp edge, then solve for a sub-pixel PSF. **Gonzalez, Delouille and Jacques 2016**, JSWSC 6, A1, arXiv:1412.6279 (skimmed): non-parametric blind deconvolution of transits.

**Moons**
- **Christian Buil**, Iris documentation, https://buil.astrosurf.com/iris/tutorial12/doc30_fr.htm (read): a Galilean satellite used as Jupiter's PSF at 780 nm. No published use of a moon's SHADOW as a probe was found; Io's and Europa's are dark disks of about 2.5 and 2.2 px here, whose own transforms at 0.3 cycles a pixel are 0.45 and 0.55 (**derived**).

## Sharpening without a ring

**Tools**
- **ImPPG** (Szczerek), https://github.com/GreatAttractor/imppg, GPL-3.0-or-later (read: `lrdeconv.cpp`, `w_lrdeconv.cpp`, `w_unshmask.cpp`). Richardson-Lucy with a Gaussian kernel, clamped to [0, 1]. "Prevent ringing" acts only at SATURATED edges: a band about them is replaced by a blurred copy before the deconvolution. Its adaptive unsharp mask interpolates the amount by the blurred image's brightness, so a dark sky can stay untouched.
- **PixInsight**: Conejero's tutorials, https://pixinsight.com/examples/M81M82/index.html and https://pixinsight.com/tutorials/NGC7023-HDR/index.html (read): global deringing restores "ringing artifacts with original pixels partially like a sort of localized flat fielding process"; local deringing is a per-iteration regularisation from a support image, and "deringing tends to degrade the deconvolution process". The open RestorationFilters module of PCL (https://gitlab.com/pixinsight/PCL, read) has a `Dering()` from a ratio of the restored to the original image. **Licence: PCL License 2.0 forbids machine-learning training and automated code generation, so it is not AGPL-compatible; implement only from the public tutorial descriptions.** Deconvolution's and MultiscaleLinearTransform's deringing are closed.
- **AutoStakkert!** (Kraaikamp, closed): its changelog (http://astrokraai.nl/software/latest.php, read) says sharpening "no longer decreases the brightness" (2.3.0.20) and that a custom sharpening kernel can be used (2.5.1.2); the AS!2 manual (https://www.astrokraai.nl/software/manual/as2_planet.html, read) has "a default sharpening" and a "Blend in Raw" percentage. So `_conv` is, as documented, a fixed brightness-preserving convolution, possibly blended with the raw stack. The kernel and why it leaves no ring are not documented.
- **PlanetarySystemStacker** (Hempel, GPL-3.0; `postproc_editor.py` read): each layer's smoothing can mix in a bilateral filter, keeping a strong edge in the coarser layer; no limb-specific code.
- **PlanetFlow**: H. You, RNAAS 10(5), 137 (2026), doi:10.3847/2515-5172/ae71cd (read); code https://github.com/CODEJIN/planetflow, LICENSE Apache-2.0 (the paper says MIT; either is compatible with attribution). `sharpen_disk_aware` (`pipeline/modules/wavelet.py`, read) multiplies each level's gain by a weight that is 1 inside the limb-fit ellipse and falls linearly to 0 at the limb over 2^L x 2 px, so the sky is untouched. No ringing is measured.
- **Lewis**, https://skyinspector.co.uk/mars-edge-artefact/ (read; theme C has his 2020 paper): paste the planet onto an enlarged copy of itself as background before the wavelets and remove it after; a sharp interior over a soft limb through a feathered selection; a radial blur of the planet as an opacity mask.
- **Siril** (https://siril.readthedocs.io/en/stable/processing/deconvolution.html, read): Richardson-Lucy with total-variation or Hessian regularisation and early stopping; nothing limb-specific. **LuckyStackWorker** has an "edge artifact suppression" whose algorithm is undocumented (binaries only, no licence). Nothing documented was found for RegiStax 6, AstroSurface or WinJUPOS.

**Methods**
- **Magain, Courbin and Sohy 1998**, ApJ 494, 472, doi:10.1086/305187, arXiv:astro-ph/9704059 (abstract): deconvolve to a narrower target PSF the sampling supports, never to a delta; no ringing around point sources on a smooth background.
- **Lucy 1994**, A&A 289, 983 (metadata, through works citing it): two-channel restoration, sharp sources in an analytic channel so the smooth channel does not ring beside them. The limb fit's disk can be that channel.
- **Yuan, Sun, Quan and Shum 2007**, ACM TOG 26(3), doi:10.1145/1276377.1276379 (read through patent US8184926B2): residual deconvolution, the ringing shrinking with the residual's magnitude, and a gain-controlled Richardson-Lucy.
- **Lagendijk, Biemond and Boekee 1988**, IEEE TASSP 36, 1874, doi:10.1109/29.9032 (metadata): projections onto convex sets and space-variant regularisation against ringing near sharp transitions. **Snyder, White and Hammoud 1993**, JOSA A 10, 1014, doi:10.1364/JOSAA.10.001014 (metadata): Richardson-Lucy with background and read-noise terms, R8 part 2's sky floor. **Farbman et al. 2008**, ACM TOG 27(3), doi:10.1145/1360612.1360666 (metadata): an edge-preserving multiscale decomposition removes the halos a linear decomposition's detail layers cause.

## Ideas, ranked, and where they went

The plan's "R8 follow-ups" section takes them as four steps, each pre-registered when it starts:

1. **Two diagnostics on data in hand**: whether every ring measured so far comes with an end-to-end transfer above one, and AutoStakkert's `_conv` fitted as a kernel of its own stack (residual by region: a linear filter, or a mask or clamp).
2. **The ring fixed**: derived gains held so the composite kernel stays non-negative toward the diffraction PSF (Magain), the limb fit's disk as its own channel (Lucy, Yuan), PlanetFlow's feathered weights.
3. **The finest band measured**: a physical kernel from what is known (the pupil, the pixel, the resampling, the warp, the air's r0) with only a few static aberration terms and the halo fitted to the limb, in Fourier space; a direct oversampled edge profile over the limb, its albedo flattened; the stack's spectrum against an OPAL map of another year, the slope supplied (Fetick).
4. **Heavier, if step 3 falls short**: torchmfbd (MIT; PyTorch's support for the GTX 1070's sm_61 first), a moon's shadow, a defocused burst after a capture.

## Not verified

The full texts of Woeger 2008 and Peck 2017; whether torchmfbd has a separate shared static term; Cosentino 2019 beyond its abstract; Lucy 1994's content (known through citing works); Choi and Showman's transition degree (a search summary); whether ASTRA-SR's Figure 1 kernels are its training size; AutoStakkert's algorithm (its forum threads refused the fetch).

## Licences of the code found

| Code | Licence | Into TianWen |
|---|---|---|
| PlanetFlow | Apache-2.0 | yes, with its notice |
| torchmfbd | MIT | yes |
| ddm4ip | MIT | yes |
| Goldstein-Fattal (IPOL) | MIT | yes |
| ImPPG | GPL-3.0-or-later | yes, as GPL-3.0 code |
| PlanetarySystemStacker | GPL-3.0 | yes, as GPL-3.0 code |
| PCL (PixInsight) | PCL License 2.0 | no; from the public descriptions only |
| SelfDeblur, BlindDPS | none | read only |
| AutoStakkert!, LuckyStackWorker, RegiStax | closed | output only |
