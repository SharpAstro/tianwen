# Literature review C: synthetic stars to train and test a star remover

> One of the reviews behind [docs/architecture/star-removal-literature.md](../star-removal-literature.md), made for [docs/plans/star-remover-training.md](../../plans/star-remover-training.md) on 2026-10-03. Its "we" and "our" are TianWen; R0, R1, H1 to H6 and the measurements quoted are the plan's as they stood that day.

The question this review answers: when stars are injected into a starless plate to make a training pair with an exact
truth, what decides whether a network trained on them removes REAL stars, and what do the people who inject sources for
a living check that we do not. Three misses measured on 2026-10-03 are held against the literature throughout:

- **Saturated tops.** Injected saturated stars hold a flat top 2 to 3 px wide; the masters' own are round, with about a
  1 px plateau (SMC 3.0 px against 1.0, Antares 3.0 against 1.0).
- **Halos.** Real bright stars hold more light than the fitted Moffat past about 4 px, and less at 1 to 3 px.
- **R0's dots.** R0 leaves a dark pixel beside about 3.7 percent of stars of significance 20 to 100 on smooth sky,
  against a 0.2 percent null; an at-site injection over those sites carries the dot under the new star into the target.

## 0. How to read this

**Verified** means I fetched the primary page on 2026-10-03: the arXiv abstract, HTML or PDF, the publisher or Crossref
record of the DOI, the ADS scan, the code repository through the GitHub API, or the software's own documentation. ADS
abstract pages refused automated fetches (HTTP 405); for the two old PASP papers I read the ADS full-text scans instead.
Each entry says how far I read:

- **full text read**: the whole paper;
- **method section read**: the sections that say how the data were made and validated (named in the entry);
- **abstract read**: the abstract and the bibliographic record only;
- **README/source read**: the repository's README, licence and the named source files;
- **metadata only**: title, authors, venue confirmed, nothing of the content.

Nothing is cited that I could not reach. Leads I could not verify, or read only in part, are in the final section and
are not used in the body. Numbers marked "(my arithmetic)" are mine, not the source's.

How far each item was read:

- 2.1 Balrog (Suchyta 2016; Everett 2022): Suchyta abstract read; Everett method sections 2, 3.2, 3.4, 4.2 and 6 read.
- 2.2 Balrog Y6 (Anbajagane 2025): method section read (section 2 pipeline steps, 3.4, the start of 4).
- 2.3 SynPipe (Huang 2018): method section read (3.2, 3.3, 4.2).
- 2.4 Rubin source_injection: README/source read (`inject_engine.py`), documentation overview page read.
- 2.5 DAOPHOT (Stetson 1987): manual section read (DAOPHOT II manual, ADDSTAR); the 1987 paper metadata only.
- 2.6 DOLPHOT (Dolphin 2000; Weisz 2024): DOLPHOT manual (March 2026) sections on fake stars read; Dolphin 2000
  abstract read; Weisz 2024 artificial-star section read.
- 2.7 ACS globular clusters (Anderson 2008): method section read (4.4, 4.5, 5.2, 6).
- 3.1 King 1971: abstract and first page read.
- 3.2 Racine 1996: full text read.
- 3.3 Trujillo 2001: abstract read.
- 3.4 Infante-Sainz 2020: abstract read.
- 3.5 Liu 2022 (elderflower): abstract read; repository metadata read.
- 3.6 Alarcon 2023: method section read (3.3, linearity).
- 3.7 Zackay 2016 (ZOGY): abstract and method section 3.3 to 3.4 read.
- 4.1 StarNet (Misiura): README/source read (README, LICENSE.md, `starnet_v1_TF2.py`, issue 36; StarNetPro README;
  starnetastro.com release notes).
- 4.2 StarXTerminator (RC-Astro): product page and its version history read.
- 4.3 Madarasz 2025: method section read (arXiv HTML).
- 4.4 PSFGAN (Stark 2018): method section read (2.1 to 2.3) and abstract.
- 4.5 Liu 2025 (galaxy model subtraction): method section read (arXiv HTML).
- 5.1 Brooks 2019: abstract, section 3.1 and the ablation study read.
- 5.2 Tobin 2017: abstract read.
- 5.3 Noise2Noise (Lehtinen 2018): method section read (sections 2 and 3.1).
- 5.4 AstroSURE (Vaheb 2026): abstract read.
- 5.5 Arazo 2020: abstract read.
- 5.6 Noisy Student (Xie 2020): abstract read.
- 5.7 Shumailov 2024: abstract read (arXiv); the Nature record metadata only.
- 6.1 FASTLens (Pires 2009): abstract and method section 4 read.
- 6.2 deepCR (Zhang 2020): method section read (section 2, the losses and the inpainting masks).
- 6.3 Partial convolutions (Liu 2018): abstract read.
- 6.4 maskfill (van Dokkum 2024): abstract read; repository metadata read.

## 1. The short version

1. **Every injection pipeline renders with the image's own measured PSF and adds the source's own shot noise, as R1
   does, and every one of them fails in the same place: bright, PSF-like objects, their wings, and saturation.** DES
   found its largest Balrog-against-data discrepancies "for bright, PSF-like objects" and a PSF model "slightly too
   large for bright stars" (2.1); the ACS globular cluster survey calls the bright-star wings "very hard to model
   accurately" and declares its saturated artificial stars qualitative because it "made no attempt to model how the
   added charge would bleed" (2.7). Nobody I found injects saturated stars quantitatively. DES Y6 answered the bright
   end by injecting REAL stars instead of model stars (2.2). Our two shape misses are exactly the regime the literature
   flags as unsolved.

2. **Our halo miss is the textbook shape of a real PSF, and so is half of it we did not expect.** A real star is a core
   plus a broad wing that ends in an inverse-square aureole (King 1971, Racine 1996). Racine fits the atmospheric PSF
   with one Moffat over about 7 magnitudes and needs two (80 percent of the flux at beta 7, 20 percent at beta 2) over
   15. At the same half width that two-component profile holds 0.91, 0.79 and 0.88 of a single beta 4 Moffat's light
   at 1, 1.5 and 2 FWHM, and 1.3, 2.2 and 5.6 times it at 2.5, 3 and 4 FWHM; it crosses over at 2.2 FWHM (my
   arithmetic). For a star of FWHM near 2 px that is "less light at 1 to 3 px, more past about 4 px", which is what we
   measured. Prediction: a two-component profile (or the master's own empirical profile) closes both halves at once.

3. **Injecting and measuring with the same model is circular, and R1's shape check is that circle.** SynPipe states it
   "cannot be used to test how the uncertainty of the PSF modeling affects the photometry" (2.3); DES Y6 says a PSF
   error "will not be observed in Balrog, which uses the same PSF for convolving a source during injection and for
   deconvolving" (2.2). R1 fits back with a Moffat what it drew as a Moffat, so it passes while the halo and the round
   top are wrong. The external check the surveys use is distributional: measure injected and real objects with one tool
   and compare. SaturatedEdgeProbe did this once for saturated stars; it should be a standing R1 check for bright
   unsaturated stars and halos too.

4. **What the misses will teach a network (predictions).** A network learns to remove what was injected. Missing halo:
   on real bright stars it leaves a faint glow past about 2 FWHM, which neither the hole test nor the speckle test reads
   (both look for DARK pixels). Excess light at 1 to 2 FWHM in the Moffat: it may subtract too much there on real bright
   stars, a dark annulus, which is the plan's one forbidden artefact. Hard flat tops: it learns to fill 2 to 3 px
   plateaus and leaves the rim of a real round top (the plan's own section 3 warning). PSFGAN is robust to a 50 percent
   PSF broadening only when trained on several PSFs (4.4); domain randomization makes the real case "just another
   variation" (5.2). So jitter beta, width, halo strength and the saturation knee around each master's measured values.

5. **R0's dots under at-site stars matter less than feared, and its leftover light matters more.** An L2 loss converges
   to the conditional mean of the target and an L1 loss to its median (Noise2Noise, 5.3). A dot at 3.7 percent of sites,
   4 to 5 sigma deep on one of eight neighbours, is learned under L2 as a mean dip of about 0.02 sigma a neighbour (0.17
   sigma if it always sits on the same side), and under L1 not at all (my arithmetic). R0's leftover light is more
   common (bright excursions outnumber dark 2 to 12 times below significance 100), so H3's at-site control will show
   its harm as incomplete removal, not as dots. deepCR's practice answers the rest: it multiplies the loss by (1 - M)
   over pixels whose truth is known to be wrong (6.2); do that over R0's flagged pixels if at-site draws are ever used
   beyond the control.

6. **Three validations the literature uses that we lack.** (a) Run the classical pipeline on the injected images: DES
   reruns its whole measurement on Balrog images (2.1), DAOPHOT reduces artificial stars "by procedures identical to
   those used for the program stars" (2.5). R0 run on R1's draws gives R0's own completeness, holes and dot rate at
   KNOWN positions, which separates R0's misfit from the master's own defects and is the baseline the network has to
   beat. (b) A twin draw without the star's shot noise (DOLPHOT's "once with and once without", Balrog's noiseless grid)
   splits a network's error into shape and noise. (c) A zero-flux injection (DES's "dark" injections): the plate alone
   through the network must come back as the plate.

7. **Self-refinement erodes the tails first.** Naive pseudo-labelling overfits its own wrong labels (confirmation bias,
   5.5); training on a model's own output makes "tails of the original content distribution disappear" (5.7); a student
   beats its teacher only when trained with more noise than it (5.6). For H4: read every generation per tail bin (the
   saturated band, stars on strongly textured nebula, the knot set), keep a fixed share of generation-0 draws in every
   batch as an anchor whose errors are not the network's, and augment the student harder than the teacher.

## 2. Source injection in survey pipelines and crowded-field photometry

### 2.1 Suchyta 2016 and Everett 2022, Balrog in DES SV, Y1 and Y3

- **Citation:** E. Suchyta et al., "No galaxy left behind: accurate measurements with the faintest objects in the Dark
  Energy Survey", MNRAS 457, 786-808 (2016), doi:10.1093/mnras/stv2953, arXiv:1507.08336. S. Everett et al., "Dark
  Energy Survey Year 3 Results: Measuring the Survey Transfer Function with Balrog", ApJS 258, 15 (2022),
  doi:10.3847/1538-4365/ac26c1, arXiv:2012.12825.
- **Verified:** Suchyta abstract read (https://arxiv.org/abs/1507.08336); Everett method sections 2, 3.2, 3.4, 4.2 and 6
  read (https://arxiv.org/pdf/2012.12825); both DOIs through Crossref.
- **Code / licence:** Balrog-GalSim, https://github.com/sweverett/Balrog-GalSim, MIT (repository metadata only).
- **What:** Balrog embeds synthetic objects in real images and reruns the pipeline to measure its transfer function.
  The Y3 run injected about 30 million objects into the single-epoch images of a random 20 percent of the footprint.
  Each object is convolved with "the local single-epoch PSFEx solution at the injection position" and rendered
  "including Poisson noise from the new source". 10 percent of injections are simulated stars, "pure delta functions
  convolved with the local PSFEx solution". Objects sit on a hexagonal lattice of 20 arcsec spacing (7.8 per arcmin2,
  about 40 percent of the real density) to cut injection-on-injection blends; blends with REAL objects are kept,
  because they are part of what is measured. A "noiseless-grid" run and zero-flux "dark" injections at random positions
  served as validation. What they learned: injecting into the coadd (Y1) "skip[s] many important aspects of the
  measurement pipeline whose effects we want to capture"; PSFEx was "slightly too large for bright stars" (the
  brighter-fatter effect) but used anyway; the remaining discrepancies are largest "for bright, PSF-like objects"; and
  the sky is overestimated near "bright stars with extended wings".
- **Relevance to us:** R1 is Balrog for a remover, with the same two halves: a measured PSF per position and the new
  source's own shot noise. Like Balrog Y1 it injects into the final product (the master), and our saturated-star miss is
  a STACKING effect, the class of effect Y3 moved to single-epoch injection to capture.
- **Prediction:** rendering a saturated star as virtual subs that are each clipped on their own pixel grid and then
  resampled to the master with the bake's warp kernel (instead of clipped on the master's grid, as the R1 design reads)
  will round the top and shrink the plateau toward the masters' 1 px. The noiseless twin and the zero-flux run are cheap
  to add to the R2 evaluation.

### 2.2 Anbajagane 2025, Balrog across the full DES Y6 footprint

- **Citation:** D. Anbajagane et al., "Dark Energy Survey Year 6 Results: Synthetic-source Injection Across the Full
  Survey Using Balrog", The Open Journal of Astrophysics 8 (2025), doi:10.33232/001c.138627, arXiv:2501.05683.
- **Verified:** method section read (section 2 pipeline steps, section 3.4, the start of section 4),
  https://arxiv.org/pdf/2501.05683; DOI through Crossref.
- **Code / licence:** not checked.
- **What:** 146 million injections over the 5000 deg2 footprint, made into the CCD images before coaddition, "light
  profiles account for the pixel scale and include shot noise", convolved with the Piff PSF at the position. The star
  sample changed from simulated stars (Y3) to real deep-field objects: "At brighter magnitudes this enhances the realism
  of the recovered star sample as we are directly injecting stars from the deep field." The paper states the
  circularity outright: a PSF-model error "will not be observed in Balrog, which uses the same PSF for convolving a
  source during injection and for deconvolving observations of the source during model fitting." Validation is the
  consistency of the injected and real catalogues, including their correlations with seeing, depth and airmass.
- **Relevance to us:** both lessons land on R1. The circularity is R1's shape check; the bright-star fix is empirical
  stars. We have the empirical stars already: R0's stars-only plate holds every bright isolated star of the master, and
  R0 builds an azimuthal profile for its giants.
- **Prediction:** an "empirical" profile arm (the master's own stacked bright-star profile, or isolated stars cut from
  the stars plate) beats the Moffat arm on real bright-star spot checks, and the two arms tie below significance 100.

### 2.3 Huang 2018, SynPipe for the HSC pipeline

- **Citation:** S. Huang et al., "Characterization and photometric performance of the Hyper Suprime-Cam Software
  Pipeline", PASJ 70, S6 (2018), doi:10.1093/pasj/psx126, arXiv:1705.01599.
- **Verified:** method section read (3.2 injection, 3.3 limitations, 4.2 input models), https://arxiv.org/pdf/1705.01599;
  DOI through Crossref.
- **Code / licence:** not checked.
- **What:** injects into single-visit images "to follow the data reduction process as realistically as possible";
  point sources are the pipeline's own PSF model; added noise "only accounts for the additional flux in the synthetic
  object", through a per-amplifier gain map; a FAKE mask bit marks the injections. The test tracts were chosen to hold no
  "extremely bright (i < 12 mag) saturated stars". Star density was set "not [to] create unrealistic crowded images".
  The stated limitations: it takes the pipeline's background subtraction for granted, so the known over-subtraction
  around bright objects cannot be tested; and it "simply adopts the PSF measured by hscPipe ... Hence, it cannot be used
  to test how the uncertainty of the PSF modeling affects the photometry and shape measurements."
- **Relevance to us:** the field excludes the bright saturated tail on purpose; our plan targets it on purpose, so we
  are in territory the survey tools do not cover. Their limitation 2 is our shape check's limitation.
- **Prediction:** R1's "FWHM within 5 percent, beta within 15 percent" result, which held on most channels, says nothing
  about realism; only an external comparison against real stars does.

### 2.4 Rubin Observatory, the source_injection package of the LSST Science Pipelines

- **Citation:** Rubin Observatory, `source_injection`, "Synthetic Source Injection (SSI) tasks for the LSST Science
  Pipelines", https://github.com/lsst/source_injection (created 2022-03-24); documentation
  https://pipelines.lsst.io/v/daily/modules/lsst.source.injection.
- **Verified:** README/source read (`python/lsst/source/injection/inject_engine.py`, the injection and noise functions),
  documentation overview page read.
- **Code / licence:** GPL-3.0 (repository metadata).
- **What:** the PSF is the exposure's own model, evaluated as a kernel image at the position and aperture-corrected to a
  12 px calibration radius; it is drawn with `method="no_pixel"` because "pixel is already part of the PSF". The gain is
  inferred per region by fitting image flux against variance. Noise: Poisson shot noise on a visit; on a coadd, Gaussian
  noise with the injected variance (`VariableGaussianNoise`), which is white. Injected pixels get INJECTED and
  INJECTED_CORE mask planes; a draw can be clipped to a maximum size.
- **Relevance to us:** two points. Our noise rule (the master's own correlated shape) is ahead of Rubin's white noise on
  coadds. And the `no_pixel` remark is a check we owe: an empirical PSF measured on the image already contains the
  pixel. If the PSF store's Moffat FWHM and beta were fitted to pixel-integrated models, integrating the injected star
  again is right; if they were fitted to point-sampled values, the pixel is applied twice.
- **Prediction:** a doubled pixel integration widens a 1.5 px FWHM star by about 10 percent and a 2.5 px star by about
  4 percent (my arithmetic, Gaussian approximation, a 1 px box adds 1/12 px2 of variance). R1's fitted-back shape check
  cannot see it, because it fits with the same convention it drew with.

### 2.5 Stetson 1987, DAOPHOT and its ADDSTAR

- **Citation:** P. B. Stetson, "DAOPHOT: A computer program for crowded-field stellar photometry", PASP 99, 191 (1987),
  doi:10.1086/131977; "User's Manual for DAOPHOT II", section XX (ADDSTAR).
- **Verified:** manual section read (https://srmastro.uvacreate.virginia.edu/astr511/daophotii.pdf, ADDSTAR and the
  introduction); the 1987 paper metadata only (Crossref).
- **Code / licence:** not applicable.
- **What:** ADDSTAR scales the PSF to the requested magnitudes and adds the stars at random or given positions; it asks
  for photons per ADU and uses it "to add the appropriate Poisson noise to the star images (the correct amount of
  readout noise already exists in the frame)"; a seed makes the frames reproducible on any computer. The manual's two
  rules: test reduction "by procedures identical to those used for the program stars", and "create a number of
  different frames, each containing only a few extra stars. If you try to add too many stars at once your synthetic
  frames will be significantly more crowded than your original frame".
- **Relevance to us:** R1 meets both. It adds only the extra shot noise of the star (the plate holds the sky's), it is
  seeded and byte-deterministic, and its count per cell is R0's count, so plate plus injected stars is exactly as crowded
  as the master. The "identical procedures" rule is the argument for running R0 itself on R1's draws.
- **Prediction:** none new; this is the practice R1 already follows.

### 2.6 Dolphin 2000 and Weisz 2024, DOLPHOT artificial-star tests

- **Citation:** A. E. Dolphin, "WFPC2 Stellar Photometry with HSTphot", PASP 112, 1383-1396 (2000),
  doi:10.1086/316630; A. Dolphin, "DOLPHOT User's Guide version 2.1/3.1" (March 2026),
  http://americano.dolphinsim.com/dolphot/dolphot.pdf; D. R. Weisz et al., "The JWST Resolved Stellar Populations Early
  Release Science Program. V. DOLPHOT Stellar Photometry for NIRCam and NIRISS", ApJS 271, 47 (2024),
  doi:10.3847/1538-4365/ad2600, arXiv:2402.03504.
- **Verified:** DOLPHOT manual read (the FakeStars parameters and section 7); Dolphin 2000 abstract read; Weisz 2024
  artificial-star section read (https://arxiv.org/html/2402.03504v1); DOIs through Crossref.
- **Code / licence:** not checked.
- **What:** artificial stars reuse "the photometry list, PSFs, etc." of the real run. `RandomFake = 1` applies Poisson
  noise and "should always be used, unless running fake star tests twice (once with and once without) to quantify
  photometric errors from crowding and background independently of the errors due to photon noise". The PSF residual
  correction stays off by default for fake stars "unless the PSF residuals are small and well-measured". Weisz: the
  stars are injected "using the best PSF model and realistic noise obtained from the original reduction run" and "one
  at the time, to avoid altering the crowding properties of the original images"; artificial stars give "a more
  realistic accounting of photometric uncertainties than the Poisson noise".
- **Relevance to us:** the paired run with and without the source's noise is a diagnostic R2 can use for free: R1 can
  emit a twin of each draw without the stars' shot noise. The caution on empirical PSF corrections applies to an
  empirical halo: measured from too few stars it adds its own noise to every injected star.
- **Prediction:** on noiseless-star twins, a network's residual at injected stars falls to the plate's own noise in every
  band but the saturated one if its errors are noise-driven; where it does not fall, the miss is shape.

### 2.7 Anderson 2008, ACS globular cluster artificial-star tests

- **Citation:** J. Anderson et al., "The ACS Survey of Globular Clusters. V. Generating a Comprehensive Star Catalog for
  each Cluster", AJ 135, 2055-2073 (2008), doi:10.1088/0004-6256/135/6/2055, arXiv:0804.2025.
- **Verified:** method section read (4.4 patches, 4.5 artifacts, 5.2 saturated stars and halos, 6 artificial-star
  tests), https://arxiv.org/pdf/0804.2025; DOI through Crossref.
- **Code / licence:** not applicable.
- **What:** about 1e5 artificial stars a cluster, added "one artificial star at a time" into temporary copies of small
  patches, so they "can never interfere with each other"; done in parallel instead, they could add "at most one test
  star every 20x20 pixels". Bright stars affect a large region "because of the mottled wings of the PSF, which are very
  hard to model accurately"; the authors drew by eye an upper-envelope halo profile from bright isolated saturated stars
  and required every detection to stand above it. Saturated stars were found by adding an artificial peak in the plateau
  and fitting "the wings of the PSF to the unsaturated pixels". For artificial stars, a pixel pushed past saturation was
  treated as saturated, but "we made no attempt to model how the added charge would bleed up and down the columns.
  Thus, brighter than the SGB, the artificial-star tests should be treated more qualitatively than quantitatively."
- **Relevance to us:** the most careful crowded-field practice I found calls its saturated injections qualitative; we
  train on ours. Their halo envelope is an empirical radial profile off the bright stars themselves, the object R0
  already builds for its giants. R0's wing fit of saturated stars is their method.
- **Prediction:** until saturated stars are injected the way a stack makes them, R2's saturated-tail verdict should come
  from the real-star spot checks only; injected-star completeness in that bin measures the injector, not the remover.

## 3. What a real star looks like: core, wing, aureole, top

### 3.1 King 1971, the profile of a star image

- **Citation:** I. R. King, "The Profile of a Star Image", PASP 83, 199 (1971), doi:10.1086/129100.
- **Verified:** abstract and first page read (ADS scan, https://articles.adsabs.harvard.edu/pdf/1971PASP...83..199K);
  DOI through Crossref.
- **Code / licence:** not applicable.
- **What:** a composite profile from the central peak out to six degrees: "The profile has a central core, an
  exponential drop, and an extended inverse-square aureole. The origin of its shape is not well understood."
- **Relevance to us:** the aureole is why real bright stars hold more light far out than any single Moffat of their core
  width. R0's giants already take "no slower than the inverse square of its radius" from this shape.
- **Prediction:** at significance 20 to 100 the aureole is under the noise and the Moffat is enough; the injected halo
  matters only for the stars whose wings R0 already fits separately (saturated and giant), which is where the 2026-10-03
  measurement saw it.

### 3.2 Racine 1996, the telescope point-spread function

- **Citation:** R. Racine, "The Telescope Point Spread Function", PASP 108, 699-705 (1996), doi:10.1086/133788.
- **Verified:** full text read (ADS scan, https://articles.adsabs.harvard.edu/pdf/1996PASP..108..699R); DOI through
  Crossref.
- **Code / licence:** not applicable.
- **What:** CCD profiles from several telescopes agree with a Kolmogorov PSF "except for the presence of an extended
  aureole of light which appears to result from a combination of instrumental and atmospheric light scattering"; the
  aureole falls as the inverse square and dominates beyond about 10 half widths. "The traditional Gaussian ... has too
  little power at large radii." A single Moffat of beta 4 fits the Kolmogorov profile over about 7 magnitudes; the sum
  of two Moffats of the same half width, one with 80 percent of the flux at beta 7 and one at beta 2, fits over more
  than 15. Part of the aureole may be light returned to the detector by a nearby glass surface.
- **Relevance to us:** the pattern we measured on bright stars (less light at 1 to 3 px, more past 4 px) is the
  difference between a two-component profile and a single Moffat of the same width. At equal half width, Racine's pair
  holds 0.91, 0.79 and 0.88 of the beta 4 Moffat's surface brightness at 1, 1.5 and 2 FWHM, and 1.31, 2.15 and 5.6 times
  it at 2.5, 3 and 4 FWHM; the crossover is at 2.2 FWHM, 4.4 px for a 2 px star (my arithmetic).
- **Prediction:** a two-component profile fitted per channel to each master's bright unsaturated stars removes both
  halves of the 2026-10-03 profile miss together; a single Moffat with a lower beta cannot, because it raises the
  shoulder and the wing at once.

### 3.3 Trujillo 2001, the Moffat PSF

- **Citation:** I. Trujillo et al., "The effects of seeing on Sersic profiles. II. The Moffat PSF", MNRAS 328, 977-985
  (2001), doi:10.1046/j.1365-8711.2001.04937.x, arXiv:astro-ph/0109067.
- **Verified:** abstract read; DOI through Crossref.
- **Code / licence:** not applicable.
- **What:** the Moffat "is shown to provide the best fit to the PSF predicted from atmospheric turbulence theory when
  beta = 4.765", and contains the Gaussian as the limit of large beta.
- **Relevance to us:** a master's beta well below that says its wing is not atmospheric alone (optics, guiding, the
  stack's warp); a beta at the store's 24.95 ceiling is a Gaussian, and R1 already found it unidentifiable there.
- **Prediction:** channels whose store beta sits at the floor or the ceiling are where a single Moffat's wing is least
  trustworthy, so they are the first place an empirical or two-component profile pays.

### 3.4 Infante-Sainz 2020, the SDSS extended PSFs

- **Citation:** R. Infante-Sainz, I. Trujillo, J. Roman, "The Sloan Digital Sky Survey extended point spread
  functions", MNRAS 491, 5317-5329 (2020), doi:10.1093/mnras/stz3111, arXiv:1911.01430.
- **Verified:** abstract read; DOI through Crossref.
- **Code / licence:** the paper states the PSF models and scripts are public; not checked.
- **What:** "By stacking ~1000 images of individual stars with different brightness, we obtain the bidimensional SDSS
  PSFs extending over 8 arcmin in radius"; the PSFs are asymmetric, following the drift-scan direction; the models are
  used to remove the scattered light of the brightest stars round the Coma core.
- **Relevance to us:** the way to measure a halo is to stack many stars of different brightness, the bright ones
  carrying the wing; one master holds tens to hundreds of usable bright stars. Asymmetry follows the observing mode, so
  a round halo is an assumption to check per train.
- **Prediction:** a halo stacked from one master's bright isolated stars is measurable well past R0's fit windows, and
  its shape differs between trains more than between sessions of one train.

### 3.5 Liu 2022, the wide-angle PSF from many bright stars (elderflower)

- **Citation:** Q. Liu et al., "A Method to Characterize the Wide-angle Point-Spread Function of Astronomical Images",
  ApJ 925, 219 (2022), doi:10.3847/1538-4357/ac32c6, arXiv:2110.11598.
- **Verified:** abstract read; DOI through Crossref and Semantic Scholar.
- **Code / licence:** elderflower, https://github.com/qliu4676/elderflower, MIT (repository metadata).
- **What:** scattered light from several bright stars is fitted together with a background model, pixel by pixel in a
  Bayesian framework, to model the Dragonfly PSF out to 20 to 25 arcmin; its wing energy is low enough "that optical
  cleanliness plays an important role in defining the PSF".
- **Relevance to us:** two lessons R0 learned the hard way on giants. A halo must be fitted jointly with the background,
  or it swallows the nebula; and the wing belongs to the optic and its cleanliness, so it is per train, not universal.
- **Prediction:** a halo constant shared across the archive will be wrong on the camera lenses and right on nobody; each
  master's halo has to come from its own bright stars.

### 3.6 Alarcon 2023, CMOS linearity up to saturation

- **Citation:** M. R. Alarcon et al., "Scientific CMOS Sensors in Astronomy: IMX455 and IMX411", PASP 135, 055001
  (2023), doi:10.1088/1538-3873/acd04a, arXiv:2302.03700.
- **Verified:** method section read (3.3, photon transfer curve and linearity), https://arxiv.org/pdf/2302.03700; DOI
  through Crossref.
- **Code / licence:** not applicable.
- **What:** for the QHY600 (IMX455) and QHY411 (IMX411), "the deviation from linearity is less than 2% up to the
  saturation point".
- **Relevance to us:** if our cameras' sensors behave the same (this paper measured two other Sony sensors, not ours),
  the round top of a master's saturated star is not a soft sensor knee. It is made by the stack: each sub clips at a
  level that differs in normalised units, at its own sub-pixel phase, and is then resampled by the warp kernel. That
  says where R1 should model the knee.
- **Prediction:** virtual subs clipped on their own grid, resampled with the bake's kernel, with clip levels spread as
  the session's normalisation scales spread and as many subs as the session had, give round tops with a 1 px plateau
  where a hard clip on the master's grid gives 2 to 3 px.

### 3.7 Zackay 2016, the dipole from a misplaced PSF (ZOGY)

- **Citation:** B. Zackay, E. O. Ofek, A. Gal-Yam, "Proper image subtraction: optimal transient detection, photometry,
  and hypothesis testing", ApJ 830, 27 (2016), doi:10.3847/0004-637X/830/1/27, arXiv:1601.02655.
- **Verified:** abstract and method sections 3.3 to 3.4 read, https://arxiv.org/pdf/1601.02655.
- **Code / licence:** the paper provides MATLAB and Python implementations; not checked.
- **What:** registration error adds a variance proportional to the squared gradient of the source model: "astrometric
  noise causes shifts in individual PSFs. The noise induced by these shifts is proportional to the difference between
  neighboring pixels (i.e., the gradient)." For a source of 1e4 electrons and FWHM 2 px, the Poisson astrometric error is
  "about a few tens of milli-pixels".
- **Relevance to us:** R0's dot is a residual of that form. The Cramer-Rao centroid error of a Gaussian star is 0.80/A px
  for a peak of A sigma, and the residual it leaves at the steepest pixel is 0.48/s sigma for a PSF sigma of s px: 0.57,
  0.46, 0.38 and 0.29 sigma at FWHM 2, 2.5, 3 and 4 px, whatever the star's brightness. Over two pixels that adds about
  0.03 percentage points to a 4 sigma test (my arithmetic). The 3.7 against 0.2 percent excess is a hundred times
  larger, so it is a systematic misfit whose residual grows with the peak, not a noise-limited centre.
- **Prediction (hypothesis, untested):** stack the residuals of significance 20 to 100 stars on smooth sky about their
  fitted centres and split them into a ring (azimuthal order 0, a profile error) and a dipole (order 1, a position
  error). The plan found that refitting centres per channel did not help, which points at the ring; a Moffat that holds
  too much light at 1 to 2 FWHM (3.2) makes exactly a shallow dark ring there. If so, the ring passes R0's 3 sigma
  inpaint flag above significance about 100 and is filled, which would explain why the dot rate falls from 3.7 to 0.5
  percent above 100, and a better profile in R0 removes the dots that at-site injection would carry.

## 4. Star-removal networks and point-source separation

### 4.1 Misiura, StarNet and StarNet2

- **Citation:** N. Misiura, StarNet, https://github.com/nekitmm/starnet (created 2018-03-25, last push 2022-09-12);
  StarNet2 at https://www.starnetastro.com/; the StarNetPro macOS front end maintained by the same author,
  https://github.com/nekitmm/StarNetPro.
- **Verified:** README/source read (StarNet README, LICENSE.md, `starnet_v1_TF2.py`, issue 36 "Full training data";
  StarNetPro README; the starnetastro.com documentation, FAQ and release-notes pages, which carry no training details).
- **Code / licence:** StarNet v1 code MIT ("Code is Copyright (c) 2018-2019 Nikita Misiura"); weights distributed
  separately; StarNet2 is a closed binary under its own licence; StarNetPro carries no licence file.
- **What:** v1 is "a convolutional residual net with encoder-decoder architecture and with L1, Adversarial and
  Perceptual losses", built on pix2pix. In the TF2 code the generator loss is 100 x L1 plus 0.1 x the adversarial term
  plus feature-matching terms on eight discriminator layers; it trains on 512 px windows of 8-bit stretched TIFFs with
  flips, 90-degree rotations, brightness offsets and channel shuffles. It "was trained using data from refractor
  telescope (FSQ106 + QSI 683 wsg-8)"; the pairs are hand-made starless versions and the dataset is private. The author's
  rule: "the only difference between two images (original and starless) should be stars", and "quality of star removal
  by the net will never be better than that in your training set". Known limits: it leaves "really huge" stars and
  handles long spikes badly; the author suggests fine-tuning for about 20 epochs on one or two of the user's own starless
  images. For StarNet2 nothing about architecture or data is public; its CLI takes linear data and "automatically
  stretches for neural processing and then returns the result to linear data" (StarNetPro README), and the StarNet2
  2.6.0 PixInsight module "adds Restore clipped pixels under Linear Data Options".
- **Relevance to us:** the reference open remover is trained on hand-made real pairs, and its author states the ceiling
  that is the plan's section 0 argument: hand-made truth caps the remover. Its stretched 8-bit domain and StarNet2's
  stretch-and-return support H6. Its failures (huge stars, spikes) are our bright tail and section 8's spike question.
- **Prediction:** none we can measure; like RC output, StarNet output must never become a target or a reference in the
  loop.

### 4.2 RC-Astro, StarXTerminator (public notes only)

- **Citation:** RC-Astro, StarXTerminator product page and version history, https://www.rc-astro.com/software/sxt/.
- **Verified:** product page read, including the module and "AI Definition" version histories.
- **Code / licence:** commercial, closed.
- **What:** "extensively trained on photographs from a wide range of instruments, from camera lenses to the James Webb
  space telescope"; "Small stars, big stars, huge stars, and even diffraction spikes are recognized and removed". AI6 (8
  Nov 2021) "Implements matching of noise statistics for replaced pixels, resulting in a more natural appearance and
  elimination of the 'smooth' areas around stars seen in earlier AI versions." AI7 (16 Dec 2021) "retains much more
  detail around stars than AI5, largely correcting the overly-smooth areas of that version." AI8 (18 Mar 2022): "Training
  data set further expanded to include tough cases such as bright stars, crowded star fields, and especially galaxies
  and galaxy clusters", with "an improved statistical noise matching engine that matches the noise statistics of
  surrounding areas when removing large stars". AI10 fixed noise matching that "could cause black or white squares ...
  in clipped regions". AI11 (15 Sep 2022): a new architecture "trained from scratch on a much wider data set", "Greatly
  improved retention of non-stellar detail, especially from the glare of very bright stars". Version 2.0.2 added
  "Automatic handling of linear and nonlinear images"; 2.0.3 adjusted Unscreen Stars "to avoid saturated pixels when one
  or more channels in the original image are clipped". Whether synthetic stars are used is not stated.
- **Relevance to us:** the commercial reference's own history names our risks in order: a smooth fill where stars were
  (fixed by a separate noise-statistics stage, not by the network), bright stars and crowded fields added to training on
  purpose (our bright-tail rule), the glare of very bright stars (our halo), and clipped channels.
- **Prediction:** our first network's output will be smoother than the plate inside removed footprints, more so under
  brighter stars (5.3 says why). The background-preservation gate (RMS in MAD units, at 1) cannot tell a smooth patch
  from a right one, so add a grain check: the output's local variance and lag-1 correlation inside footprints against
  the plate's around them.

### 4.3 Madarasz 2025, compact source removal by inpainting (Herschel)

- **Citation:** M. Madarasz et al., "A deep neural network approach to compact source removal", A&A 696, A37 (2025),
  doi:10.1051/0004-6361/202453262, arXiv:2502.18345.
- **Verified:** method section read (https://arxiv.org/html/2502.18345); DOI through Crossref.
- **Code / licence:** not checked.
- **What:** training pairs are Herschel maps plus injected point sources; the PSFs are "official Vesta PSFs, which are
  specific to the Observing Mode, band, and scan speed", scaled from 10 to 40,000 mJy with denser steps at low flux, 25
  to 210 per map, injected into fields "selected with relatively few real sources present". Real sources are not
  removed: they stay in input and target alike. The PSF arrays are scaled and added with no mention of added noise. The
  network is a U-Net of partial convolutions (11 down and 11 up blocks) fed a mask whose size is set from each source's
  signal-to-noise (six sizes); the loss is 2 L1(valid) + 6 L1(hole) + 120 style + 0.4 SSIM + a source term. Validation:
  extracted against injected fluxes in apertures of 4 to 18 arcsec, real standard stars against their models, and power
  spectra that differ by "10^-4 to 10^-10". The deviation grows "starting at around 13,000 mJy" in "particularly complex
  backgrounds". At inference the masks come from user coordinates or a point-source catalogue.
- **Relevance to us:** the closest academic analogue to R1 and R2: a real background, an injected measured PSF, real
  sources left in the target, and the mask-input architecture the plan holds for H4's kill. Their field selection is the
  uncorrelation rule enforced by choosing sparse fields. Their noiseless injections are less careful than ours. Their
  bright-end failure on complex backgrounds is our stars on steep bright nebula.
- **Prediction:** a mask-input remover needs a detection list at inference; R0's finder supplies one, so the two-branch
  alternative is buildable without a new detector. Its weak point will be what the mask misses (faint stars below the
  finder's threshold), which the plain U-Net does not depend on.

### 4.4 Stark 2018, PSFGAN

- **Citation:** D. Stark et al., "PSFGAN: a generative adversarial network system for separating quasar point sources
  and host galaxy light", MNRAS 477, 2513 (2018), doi:10.1093/mnras/sty764, arXiv:1803.08925.
- **Verified:** method section read (2.1 to 2.3), https://arxiv.org/pdf/1803.08925; abstract read.
- **Code / licence:** a SpaceML/PSFGAN repository exists without a licence field (search result only, not read).
- **What:** SDSS galaxies with an artificial point source added at the centre; the network removes it. The training PSF
  is the SDSS PSF tool's, fitted with three 2-D Gaussians (noise-free, because noise "would be amplified when the PSF is
  scaled to high contrast ratios"); the test PSF is a median stack of 40 to 60 nearby stars, on purpose: "The mismatch
  between training and testing PSF is necessary to take into account the lack of information about the exact PSF we
  would have in a real situation." An asinh stretch (A = 50) scored best of the stretches tried. The network "is robust
  against a broadening in the PSF width of +-50% if it is trained on multiple PSF's", and still works trained on
  non-astronomical images.
- **Relevance to us:** H2 and H6 in one paper: diversity of PSFs in training buys robustness to a PSF the network never
  saw, and the stretched domain helps. Testing on a differently built PSF is what our real-star spot checks do.
- **Prediction:** an R2 arm that jitters each injected star's profile around the master's values (width by tens of
  percent, beta, a halo fraction) holds up better on the held-out train than an arm locked to each master's exact
  profile, and costs little on the master's own stars.

### 4.5 Liu 2025, galaxy model subtraction with a denoising autoencoder

- **Citation:** R. Liu et al., "Galaxy Model Subtraction with a Convolutional Denoising Autoencoder", ApJ 994, 235
  (2025), doi:10.3847/1538-4357/ae0d83, arXiv:2510.04957.
- **Verified:** method section read (https://arxiv.org/html/2510.04957v1); DOI through Crossref.
- **Code / licence:** stated in the paper as public on GitHub and Zenodo under MIT; not checked.
- **What:** GALFIT model galaxies are injected into real NGVS sky cutouts (cutouts holding large galaxies or bright stars
  removed by hand); the target is the clean model, so the real stars and galaxies of the cutout stay in the input only
  and the network learns to suppress them. MAE loss. Validation is an injection-recovery test of mock globular clusters
  made from flux-scaled local DAOPHOT PSFs. Partial convolutions with SExtractor masks were tried and dropped for
  convergence problems. It fails on bulges, because training held no multi-component models.
- **Relevance to us:** the same lesson from the other side: what the training family lacks (bulges there; halos, round
  tops and spikes for us) is what fails on real data. And a mask-input design that did not converge for them is a
  caution for the two-branch arm.
- **Prediction:** our first network's failures on the real spot checks will sit on the star shapes the injector does not
  make: halos, round saturated tops, elongated saturated stars, spikes.

## 5. Synthetic-to-real gap and self-training

### 5.1 Brooks 2019, unprocessing images for learned raw denoising

- **Citation:** T. Brooks et al., "Unprocessing Images for Learned Raw Denoising", CVPR 2019, 11028-11037,
  doi:10.1109/cvpr.2019.01129, arXiv:1811.11127.
- **Verified:** abstract, section 3.1 (shot and read noise) and the ablation study read,
  https://arxiv.org/pdf/1811.11127; DOI through Crossref.
- **Code / licence:** not checked.
- **What:** realistic raw training data are made by inverting each step of a camera pipeline (colour correction, white
  balance, gain, tone mapping, gamma) and adding shot and read noise drawn from a fitted distribution; the loss is taken
  after re-applying the pipeline. "Performance is most sensitive to our modeling of noise, as using Gaussian noise
  significantly decreases performance."
- **Relevance to us:** "invert the pipeline, then corrupt" is H6's design (inject in linear on the linear plate, stretch
  input and target with the target's parameters), and the noise model being the most sensitive part is what our
  denoiser programme paid for.
- **Prediction:** with R1's noise-shape check inside its bound (band 1 over band 0 within 0.03 of the master's), the
  synthetic-to-real gap will not be in the noise; it will be in the bright-star shapes.

### 5.2 Tobin 2017, domain randomization

- **Citation:** J. Tobin et al., "Domain randomization for transferring deep neural networks from simulation to the real
  world", IROS 2017, 23-30, doi:10.1109/iros.2017.8202133, arXiv:1703.06907.
- **Verified:** abstract read; DOI through Crossref.
- **Code / licence:** not applicable.
- **What:** randomizing the simulator's rendering so that "the real world may appear to the model as just another
  variation"; a detector trained only on simulated images "with non-realistic random textures" was accurate to 1.5 cm on
  real images.
- **Relevance to us:** where we cannot render a star exactly (halo strength, the saturation knee, the elongation of a
  saturated star), randomizing it over a range that brackets the real masters is the standard answer.
- **Prediction:** randomizing the saturated plateau across the 1 to 4 px range the masters show, rather than fixing one
  rendering, narrows the saturated-tail gap on real spot checks even before the knee model is right.

### 5.3 Lehtinen 2018, Noise2Noise: what an L2 or L1 loss converges to

- **Citation:** J. Lehtinen et al., "Noise2Noise: Learning Image Restoration without Clean Data", ICML 2018,
  arXiv:1803.04189.
- **Verified:** method section read (sections 2 and 3.1), https://arxiv.org/pdf/1803.04189.
- **Code / licence:** not checked.
- **What:** L2 training leaves the estimate "unchanged if we replace the targets with random numbers whose expectations
  match the targets"; the optimum is the conditional expectation of the target given the input. "The L1 loss ... has its
  optimum at the median of the observations"; random text overlays are removed by the median-seeking L1. And
  "saturation (gamut clipping) renders the expectation incorrect due to removing part of the distribution."
- **Relevance to us:** three consequences for the plan's L2 loss. (1) A target defect that the input does not reveal is
  learned at its mean. For R0's dot under an at-site star: 0.037 x 4.5 sigma / 8 neighbours = about 0.02 sigma a
  neighbour, or 0.037 x 4.5 = 0.17 sigma if it always sits on one side; under L1, zero (my arithmetic). (2) R0's
  leftover light at its sites is the more common defect and is learned the same way, as a faint residual star. (3) Under
  a bright injected star the plate's own noise is buried in the star's shot noise and cannot be predicted from the
  input, so an L2 network outputs its conditional mean there: a smooth patch, StarXTerminator's AI5 problem (4.2).
- **Prediction:** H3's at-site arm shows its harm on real stars as incomplete removal (positive residual), not as dark
  dots; and the output's local variance under bright injected stars falls below the plate's, increasingly with the
  star's flux.

### 5.4 Vaheb 2026, AstroSURE

- **Citation:** O. Vaheb, S. Fabbro, S. Draper, "AstroSURE: Learning to Remove Noise from Astronomical Images Without
  Ground Truth Data", arXiv:2604.16793 (2026).
- **Verified:** abstract read.
- **Code / licence:** not checked.
- **What:** Noise2Noise, SURE and blind-spot denoisers trained without clean truth, on synthetic, HST and CFHT data;
  "encouraging gains on HST data after domain-consistent initialization, while transfer to CFHT data is more limited,
  highlighting the importance of instrument/domain similarity".
- **Relevance to us:** independent support for the plan's held-out TRAIN: transfer across instruments is where learned
  restorers lose.
- **Prediction:** the held-out train shows a larger gap than the held-out sessions on every gate, largest in the bright
  tail, whose profiles are the most instrument-specific (3.5).

### 5.5 Arazo 2020, pseudo-labelling and confirmation bias

- **Citation:** E. Arazo et al., "Pseudo-Labeling and Confirmation Bias in Deep Semi-Supervised Learning", IJCNN 2020,
  doi:10.1109/ijcnn48605.2020.9207304, arXiv:1908.02983.
- **Verified:** abstract read; DOI through Crossref.
- **Code / licence:** the abstract points to public code; not checked.
- **What:** "a naive pseudo-labeling overfits to incorrect pseudo-labels due to the so-called confirmation bias", and
  "mixup augmentation and setting a minimum number of labeled samples per mini-batch are effective regularization
  techniques for reducing it".
- **Relevance to us:** in H4 the removal truth stays exact (injected stars), but the BACKGROUND of generation n is the
  network's own output, a pseudo-label. The mitigation translates directly: keep a fixed share of generation-0 (R0)
  draws in every batch, whose errors are R0's and not the network's.
- **Prediction:** a generation trained on its predecessor's plates alone degrades in background preservation on the
  golden set before its completeness moves.

### 5.6 Xie 2020, Noisy Student self-training

- **Citation:** Q. Xie et al., "Self-Training With Noisy Student Improves ImageNet Classification", CVPR 2020,
  10684-10695, doi:10.1109/cvpr42600.2020.01070, arXiv:1911.04252.
- **Verified:** abstract read; DOI through Crossref.
- **Code / licence:** public per the abstract; not checked.
- **What:** a teacher labels unlabelled data, an equal-or-larger student is trained on it "with noise such as dropout,
  stochastic depth, and data augmentation", and the student becomes the next teacher; noising the student is what lets
  it "generalize better than the teacher".
- **Relevance to us:** H4's loop only improves if each student sees more variation than its teacher did; with the same
  augmentation it can at best copy.
- **Prediction:** generation 2 beats generation 1 on the real spot checks only if it is trained with stronger
  augmentation (profile jitter, 4.4 and 5.2) than generation 1.

### 5.7 Shumailov 2024, model collapse on recursively generated data

- **Citation:** I. Shumailov et al., "AI models collapse when trained on recursively generated data", Nature 631,
  755-759 (2024), doi:10.1038/s41586-024-07566-y; preprint "The Curse of Recursion: Training on Generated Data Makes
  Models Forget", arXiv:2305.17493.
- **Verified:** abstract read (arXiv); the Nature record metadata only (Crossref).
- **Code / licence:** not applicable.
- **What:** "use of model-generated content in training causes irreversible defects in the resulting models, where
  tails of the original content distribution disappear", shown for variational autoencoders, Gaussian mixtures and
  language models.
- **Relevance to us:** H4 trains each generation on backgrounds the previous one made. Our tails are the bright
  saturated stars, stars on strongly textured nebula and the knots the remover must keep.
- **Prediction:** per-generation regressions appear first in the tail bins (the saturated band, the strongly textured
  class of `FittedStar.Texture`, the knot set), so H4 must be read per bin against the golden set; a pooled metric will
  hide it.

## 6. Inpainting masked cores and artefacts

### 6.1 Pires 2009, FASTLens: sparse inpainting of bright-star masks

- **Citation:** S. Pires et al., "FASTLens (FAst STatistics for weak Lensing): fast method for weak lensing statistics
  and map making", MNRAS 395, 1265 (2009), doi:10.1111/j.1365-2966.2009.14625.x, arXiv:0804.4068.
- **Verified:** abstract and method section 4 (inpainting by sparse decomposition) read, https://arxiv.org/pdf/0804.4068.
- **Code / licence:** not checked.
- **What:** masked bright-star regions of weak-lensing maps are filled by sparse representation: iterative thresholding
  in a DCT or wavelet dictionary with a threshold that decreases each iteration, N log N. On masks with about 20 and 10
  percent missing (CFHTLS and Subaru patterns) the power spectrum is recovered to about 1 and 0.3 percent and the
  equilateral bispectrum to about 3 and 1 percent.
- **Relevance to us:** they judge a fill by its STATISTICS (power spectrum, bispectrum), not pixel by pixel. R0's fill
  probe measures error in sigma; a power-spectrum comparison of filled against untouched patches would catch a fill
  that is too smooth or too grainy, which a pixel error at 1.4 sigma cannot tell apart.
- **Prediction:** the push-pull fill with correlated noise matches the plate's power spectrum on smooth sky and falls
  short across filaments; a wavelet-sparse fill is the classical alternative to put through the fill probe on structure.

### 6.2 Zhang 2020, deepCR: mask, then inpaint, with the loss kept off unknown pixels

- **Citation:** K. Zhang, J. S. Bloom, "deepCR: Cosmic Ray Rejection with Deep Learning", ApJ 889, 24 (2020),
  doi:10.3847/1538-4357/ab3fa6, arXiv:1907.09500.
- **Verified:** method section read (section 2, the losses and the construction of inpainting masks),
  https://arxiv.org/pdf/1907.09500.
- **Code / licence:** https://github.com/profjsb/deepCR, BSD-3-Clause (repository metadata).
- **What:** two U-Nets, one predicting the cosmic-ray mask and one inpainting under it, on HST ACS/WFC images whose true
  masks come from comparing each exposure with the median. The inpainting net is trained on REAL pixels: cosmic-ray
  masks from other stamps are laid on clean regions as the inpainting mask, the loss is taken only inside that mask, and
  it is multiplied by (1 - M) so that "the loss is not computed for parts of the inpainting mask that overlap with CR
  artifacts". Inpainting errors were 20, 5 and 2.5 times lower than the best non-neural method in globular-cluster,
  resolved-galaxy and extragalactic fields.
- **Relevance to us:** three things. Laying hole shapes on known pixels is exactly R0's fill probe, so our fill test is
  the field's standard. The (1 - M) loss mask is the clean answer to R0's dots in at-site draws: exclude the pixels R0's
  speckle and leftover tests flag from the loss. And mask-then-inpaint as two networks is the plan's held two-branch
  structure, shown to work on astronomical data.
- **Prediction:** an at-site arm with R0's flagged pixels masked out of the loss scores like the random arm on real spot
  checks. Only H3's one-off control needs the unmasked version.

### 6.3 Liu 2018, partial convolutions

- **Citation:** G. Liu et al., "Image Inpainting for Irregular Holes Using Partial Convolutions", ECCV 2018,
  arXiv:1804.07723.
- **Verified:** abstract read.
- **Code / licence:** not checked.
- **What:** a convolution "masked and renormalized to be conditioned on only valid pixels", with the mask updated for the
  next layer in the forward pass; it beats standard convolutions on irregular holes, which otherwise condition on the
  fill value and give colour discrepancy and blur.
- **Relevance to us:** the building block of 4.3, and one way to build the two-branch arm's inpainting branch with the
  injected footprints (training) or the detections (inference) as its mask. 4.5 could not make it converge.
- **Prediction:** none; it is an architecture option, to be judged by the same gates.

### 6.4 van Dokkum 2024, maskfill

- **Citation:** P. van Dokkum, I. Pasha, "A Robust and Simple Method for Filling in Masked Data in Astronomical Images",
  PASP 136, 034503 (2024), doi:10.1088/1538-3873/ad2866, arXiv:2312.03064.
- **Verified:** abstract read; DOI through Crossref.
- **Code / licence:** https://github.com/dokkum/maskfill, MIT (repository metadata).
- **What:** edge pixels are extrapolated inward by iterative median filtering, giving "a smoothly varying spatial
  resolution within the filled-in regions" and "seamless transitions"; "Gaps in continuous, narrow features can be
  reconstructed with high fidelity, even if they are large." Compared against several interpolation schemes.
- **Relevance to us:** a simple classical fill that carries a filament across a hole, which the plan names as the open
  question of R0's fill (what goes into a saturated core on structure).
- **Prediction:** on the fill probe's holes over nebula, median extrapolation has lower error than push-pull where the
  structure is elongated and ties on smooth sky; it adds no grain, so the correlated noise still goes on after it.

## 7. Mapping onto the plan's hypotheses and R1's checks

**H1 (a classical plate is good enough as a bootstrap target).** The field accepts real content in the target as long as
it does not move with the injections: Balrog keeps every real object (2.1), Madarasz leaves real sources in input and
target (4.3), the galaxy autoencoder trains on real sky (4.5). So H1's bar is uncorrelation, not purity. Where a
target's truth is known to be wrong, deepCR keeps the loss off it (6.2). With random placement at least 3 FWHM from every
subtracted site, injected stars do not land on R0's fills, so even eta Carinae's 37.7 percent inpaint teaches identity
over fills, not what goes under a star.

**H2 (Moffat from the measured distribution beats Gaussian).** Supported in direction: the Gaussian "has too little power
at large radii" (3.2). But the literature says the larger remaining gap is in the bright tail, where neither arm is the
real profile (2.1, 2.2, 2.7, 3.1, 3.2). Recommendation: add a third arm, an empirical or two-component profile from each
master's own bright stars, and a jittered variant (4.4, 5.2). Prediction: Gaussian < Moffat < empirical on real bright
stars, all three tied below significance 100.

**H3 (position independence is load-bearing).** The crowding rule of artificial-star practice is met (2.5, 2.6, 2.7).
The loss statistics (5.3) predict the at-site arm's harm will be leftover light, not dots; the dots are learned at about
0.02 to 0.17 sigma under L2 (my arithmetic). Read H3's control on injected-star completeness, on real-star leftover
light, and on the speckle measure, and expect the last to show little. deepCR's loss mask (6.2) makes at-site draws safe
if they are ever wanted beyond the control.

**H4 (self-refinement converges).** The self-training literature predicts erosion of the tails first (5.5, 5.7) and gain
only with a noised student (5.6). Read each generation per tail bin against the golden set, keep a generation-0 anchor
share in every batch, and augment each student harder than its teacher.

**H5 (the additive split holds photometrically).** A remover trained on Moffat stars leaves the aureole (3.1, 3.2) in the
starless plate, so the stars plate's flux of a bright star reads low, more so in a larger aperture. Prediction: if H5's
bias appears, it is negative, confined to the brightest band, and grows with the aperture radius. Madarasz's validation
(extracted against injected flux over a range of apertures, 4.3) is the shape of the check.

**H6 (the stretched domain).** Consistent with every remover that says anything: StarNet trains on stretched 8-bit data
and StarNet2's CLI stretches linear input itself and returns linear (4.1); StarXTerminator handles linear and nonlinear
input automatically (4.2); PSFGAN's best preprocessing was an asinh stretch (4.4).

**R1's checks, with what the literature adds:**

- **Shape** (FWHM within 5 percent, beta within 15 percent, fitted back): circular (2.2, 2.3). Add an external check:
  the radial profile of injected stars against real isolated stars of matched amplitude in the same master, to 6 FWHM,
  per amplitude band, with the light beyond 2.2 FWHM as its own number (3.2). Confirm whether the PSF store's Moffat fit
  was pixel-integrated before integrating again (2.4).
- **Noise** (band 1 over band 0 within 0.03): the most sensitive part of a synthetic set (5.1) and already gated; ahead
  of Rubin's white coadd noise (2.4). Add a twin draw without the stars' shot noise for R2's diagnosis (2.1, 2.6).
- **Saturation** (plateau and edge medians within a factor 1.5, nothing past a clip): the miss is real and the
  literature has no ready model; its answer is to inject before the stack (2.1, 2.2, 2.3). Clip each virtual sub on its
  own grid, resample it with the bake's kernel, and spread the clip levels and widths as the session's own subs spread
  (3.6). Until it matches, treat saturated-tail results as qualitative (2.7) and randomize the plateau (5.2).
- **Placement** (at least 95 percent placed, none nearer than 3 FWHM to a site): R1's count matching makes
  injection-on-injection blends occur at the real rate, which a remover needs and a photometric transfer function avoids
  (2.1).
- **Determinism**: matches DAOPHOT's seeded practice (2.5).
- **Missing, and cheap:** R0 run on R1's draws, giving R0's completeness, holes and dot rate at known positions (2.1,
  2.5); a zero-flux draw through the network, plate in and plate out (2.1); a grain check inside removed footprints (4.2,
  5.3); and the ring-or-dipole stack of R0's residuals at significance 20 to 100 (3.7).

## Leads I could not verify, or verified only in part

- **StarNet2's architecture and training data.** Not public: the starnetastro.com documentation, FAQ and release notes
  carry no training details, and the v1 README keeps its dataset private ("This is one part I'd like to keep for myself
  for now").
- **StarXTerminator's training data.** Whether synthetic stars, real pairs or both are used is not stated anywhere I
  could reach; only the version notes quoted in 4.2.
- **Stetson 1987 full text.** Only the Crossref record; ADDSTAR is described from the DAOPHOT II manual.
- **Weisz 2024's artificial-star counts** (over a million for M92, as a summarising fetch reported from its Table 5):
  not checked against the table, so not cited.
- **The DES Y3 brighter-fatter cause** (Antilogus et al. 2014, cited by Everett) and the **Y6 PSF model paper** (Schutt
  et al. 2025): not read.
- **Rubin's source_injection documentation subpages** (catalogue generation, injection, matching): not read beyond the
  overview.
- **Winecki and Kochanek 2024**, "Photometry of Saturated Stars with Neural Networks", arXiv:2404.15405: abstract read; a
  network trained on real saturated ASAS-SN stars, about photometry rather than image profiles, so left out. A possible
  lead for learning saturated shapes from real stars.
- **Frei 1996**, "Semi-automatic Removal of Foreground Stars from Images of Galaxies", PASP, doi:10.1086/133775: abstract
  read; an empirical-PSF subtract-and-patch remover, a classical predecessor of R0, left out for space.
- **Puglisi and Bai 2020**, PICASSO, ApJ, doi:10.3847/1538-4357/abc47c: abstract read; generative inpainting of
  point-source holes in diffuse emission maps, left out for space.
- **Schawinski 2017** (GAN recovery of degraded SDSS galaxies, doi:10.1093/mnrasl/slx008), **Vojtekova 2021** (Astro
  U-net, doi:10.1093/mnras/staa3567) and **Sureau 2020** (deconvolution trained on GREAT3 simulations, A&A 641, A67):
  abstracts read, not used.
- **A Medium post on star reduction trained from one background image and one star mask**: a blog, not a primary
  technical source; not cited.
- **The Croman quote** in the plan's section 0 ("the network will faithfully learn all of your mistakes"): not checked by
  this review.
- **Any paper training a star remover on stars injected into real astrophotographs and measuring the synthetic-to-real
  gap on real stars**: searched for and not found; 4.3 (Herschel) and 4.4 (SDSS point sources) are the nearest.
