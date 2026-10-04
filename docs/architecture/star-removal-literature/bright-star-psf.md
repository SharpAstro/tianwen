# Literature review A: the bright star's point spread function, and a saturated star's brightness from its wings

> One of the reviews behind [the star-removal literature synthesis](../star-removal-literature.md), made for
> `docs/plans/star-remover-training.md` on 2026-10-03.

## 0. How to read this

**Verified** means I fetched a primary page for the item on 2026-10-03 or 2026-10-04: the DOI record at
Crossref, the arXiv abstract or full text, the publisher's PDF, the ADS article scan, the agency's own
documentation page, or the code repository. ADS's search interface refused automated access (a CAPTCHA),
so no item rests on an ADS abstract page; where ADS appears it is the ADS article-scan server.

Each item says how far I read it:

- **full text read**: every page of the paper.
- **method section read**: the abstract plus the sections that describe the method and the numbers I quote.
- **abstract read**: the abstract only (from arXiv, the publisher, or the OpenAlex mirror of the publisher's
  abstract, which I name when it is the source).
- **metadata only**: title, authors, journal, volume and page from the DOI record. Nothing from such an item
  is used as evidence; it is in section 7.

A number marked **(my arithmetic)** is mine, not the paper's. The assumptions and the scripts' results are
in the appendix. A number with no mark is quoted from the item it sits under. Where I could not read a
number I say so rather than guess.

Our measurement, restated so the review can be read alone (brief of 2026-10-03, updated with the ten-master
measurements of 2026-10-03):

- R0 fits a saturated star's amplitude with its own **field profile**: a Moffat at the field's FWHM and beta
  (beta 2.5 to 8.6 on these masters) plus a radial residual table, core excluded above 0.7 of the plateau,
  window 4 FWHM plus the plateau radius, sky a tilted plane.
- The injector rendered that amplitude with the **PSF store's** single Moffat (beta 4 to 25). The core came
  out too bright (real/re-render 0.6 to 0.9 at 1 to 3 px) and the wings far too faint (1.5 to 20 at 5 to 13
  px). Rendered with R0's own profile, the injected tops match the real ones (first ring within 0.01 to 0.04
  of real on six masters, plateau 1 px).
- Wing light past 4.5 px as a fraction of the light within 20 px (annulus medians): real saturated stars
  0.04 to 0.28; R0's field profile 0.015 to 0.18; the PSF store Moffat 0.000 to 0.02, except two low-beta
  masters. Bright unsaturated stars (300 to 5000 sigma) match the field profile within about 10 to 25 percent
  out to 8 px.
- Saturated stars still carry more halo than the field profile: real over field 1.1 to 1.6 at 2.5 to 4 px,
  rising further out. The excess shrinks as the star is driven further past its clip (Spearman -0.4 to -0.6
  on five masters, +0.5 on one).

## 1. The short version

1. **The real/re-render pattern is the textbook core-plus-aureole signature.** Every measured stellar
   profile has a seeing core that a Moffat describes, then a much shallower power-law "aureole" that carries
   a few percent of the light. King (1971) found an inverse-square aureole holding about 5 percent of a
   star's light; Racine (1996) found the core follows Kolmogorov turbulence out to about 10 half-widths
   (5 FWHM) and the aureole takes over beyond; a single Moffat of beta 4 matches that core over only about
   7 magnitudes. The closest optical analogue to our refractors, the Dragonfly array of 400 mm telephoto
   lenses at 2.5 to 2.85 arcsec/px (Liu et al. 2022), is a Moffat core of beta 6.7 plus a power-law
   component holding 30 percent of the flux, falling as r^-3.6 to 54 arcsec, r^-2.9 to 126 arcsec and
   r^-1.9 beyond. A Moffat of beta 4 or more puts under 0.04 percent of its flux beyond 4 FWHM (my
   arithmetic, A.1); the published PSFs put 1 to 5 percent there (King, Racine, Karabal et al. 2017,
   Garate-Nunez et al. 2024, and about 3 percent for the Dragonfly model by my integration, A.4). The PSF
   store's 0.000 to 0.02 against the real 0.04 to 0.28 past 4.5 px is that gap.
2. **The too-bright core was a consistency error, and the literature's rule is to render with the profile
   that measured the normalisation.** Liu et al. convert the intensity measured at a scale radius into total
   flux with the same model they render (their Eq. 10); Infante-Sainz et al. (2020), Roman et al. (2020) and
   the Rubin pipeline scale and subtract the very PSF they fitted. My arithmetic (A.2) gives the size of the
   error we hit: at FWHM 2.5 px the same flux rendered with beta 25 instead of beta 2.5 has a 1.41 times
   higher peak (1.30 times against beta 3), which is the 0.6 to 0.9 we measured at 1 to 3 px. The measured
   fix (render with R0's own profile) agrees.
3. **The rule for a saturated star's normalisation:** measure it where the star is unsaturated, above the
   noise and away from reflections, at a radius fixed for the whole sample, with a profile that already
   contains the aureole, as a robust ratio of star to profile, with the sky fitted alongside. Infante-Sainz et
   al. take the 3-sigma-clipped median of the star/PSF ratio over the radii between 0.1 of the saturation
   level and 3 times the background; Liu et al. use a thin annulus at a fixed 30 arcsec where the star is at
   least 5 sigma; the Rubin code fits one amplitude plus a planar background by least squares over every
   unsaturated pixel. Su et al. (2022) add the check that matters most: when the profile is right, the
   star/profile ratio is flat with radius (they reach better than 1 percent this way). Our 0.6 to 20 ratio
   failed that check by an order of magnitude.
4. **How far a pure Moffat wing fit is off.** WISE found that profile-fitting the unsaturated wings of
   saturated sources overestimates their W2 flux from about 0.5 mag past saturation, and traced it to a PSF
   whose shape depends on flux in the saturated regime; Pan-STARRS found its standard PSF fit "fails to fit
   the wings of highly saturated stars" and switched to an empirical per-star radial profile. In my
   simulation of R0's fit (A.3), a Moffat-only fit with a free sky gets the total flux within -9 to +30
   percent for aureoles of 5 to 30 percent up to a peak 20 times the clip, and up to 2.6 times too high at
   100 times the clip (worse the deeper the saturation), while the predicted wing
   beyond 2 to 4 FWHM is low by 1.5 to 4 times for a beta 3 core and 10 to over 100 times for a beta 4.8 core.
   Our measured 1.5 to 20 at 5 to 13 px sits inside that band, so it is what a few-percent aureole on beta
   3 to 5 cores gives.
5. **The excess that shrinks with saturation depth is not what a physical aureole predicts.** A fixed-fraction
   aureole gives a constant real/field ratio at a fixed radius: de Jong (2008) found the SDSS envelope's
   brightness "depends only on the total brightness of the star", and halo photometry (White et al. 2017)
   works because a halo pixel tracks the star in proportion. A ratio that falls as the star saturates more
   says the normalisation depends on saturation depth. In order of likelihood: (a) R0's window grows with the
   plateau, so a more saturated star is normalised further out, where its halo pulls the amplitude up; in my
   simulation (A.3) this raises the amplitude by about 5 to 20 percent between peak/clip 2 and 20 and
   lowers the excess at 4 to 5 px by about 5 to 17 percent, the sign we see (Bazkiaei et al. 2024 report
   that the choice of annulus is the critical setting for exactly this reason); (b) R0 keeps pixels up to
   0.7 of the plateau, where the literature stops at 0.1 of saturation (Infante-Sainz et al.) or outside twice
   the saturated radius (WISE); any depression of near-clip pixels (partial clipping across subs, sensor
   non-linearity near full well; neither verified here) biases the amplitude low most for the barely saturated
   star; (c) a PSF that genuinely changes with flux in the saturated regime, which WISE documented and could
   not explain. The test: refit with a fixed annulus in FWHM units and a core cut of 0.1 to 0.3 of the
   plateau. If the Spearman collapses, it was the fit.
6. **Expect the aureole to differ per colour channel, but not wildly.** Scatter from surface roughness falls
   with wavelength (Bennett and Porteus 1961), diffraction by cirrus ice widens in proportion to wavelength
   and dims as its square (DeVore et al. 2013), and thinned silicon adds a "red halo" from about 750 nm,
   reaching 20 percent of the energy at 1 micron in one HST camera (Sirianni et al. 2005). Yet de Jong (2008),
   Sandin (2014) and Racine (1996) all found the halo's shape the same across the visible bands except where
   that red halo or a detector reflection (Karabal et al. 2017) added a component. So fit the aureole
   fraction and slope per channel and per master, and expect the red channel to stand out.

## 2. The classical profile of a star image

### 2.1 Moffat 1969, the Moffat profile

- **Citation:** Moffat, A. F. J. 1969, "A Theoretical Investigation of Focal Stellar Images in the
  Photographic Emulsion and Application to Photographic Photometry", Astronomy and Astrophysics 3, 455-461.
- **Verified:** method section read (pages 455 to 458 of 7) from the ADS article scan,
  https://articles.adsabs.harvard.edu/pdf/1969A%26A.....3..455M . No DOI exists for this volume.
- **What:** convolving a Gaussian seeing disk with diffraction and emulsion scattering "predict[s] too low an
  intensity for large radial distances from the centre of the image, contradicting the assumption that seeing
  is gaussian". He proposes I(r) = I0 / {1 + (r/R)^2}^beta (his Eq. 7), which fits measured profiles "at
  least to the point where diffraction begins to dominate (i.e. at I/I0 approx. 10^-3)". Typical Hamburg
  Schmidt plates give beta approx. 3 to 5; the Lick 120-inch profile of King and Hinrichs (1967) fits with
  beta = 2.72. "For poor seeing R and beta are large and the function approaches a gaussian profile."
- **Relevance to us:** the Moffat was introduced to fit the core and inner wing down to 10^-3 of the peak,
  not the aureole. A saturated star in a deep stack is measured far below 10^-3 of its true peak, which is
  outside the range Moffat validated. A beta of 25 is, by his own description, a Gaussian.

### 2.2 King 1971, The profile of a star image

- **Citation:** King, I. R. 1971, "The Profile of a Star Image", Publications of the Astronomical Society of
  the Pacific 83, 199-201. DOI 10.1086/129100.
- **Verified:** full text read (3 pages), publisher PDF https://iopscience.iop.org/article/10.1086/129100/pdf ;
  Crossref record checked.
- **What:** a composite profile from the centre out to six degrees, built from four unadjusted sources
  (60-inch Cassegrain plates, image diameters on the POSS, de Vaucouleurs' photoelectric data on a star and
  Jupiter, sky brightness near the Sun). Three parts: a nearly uniform disk, an exponential drop that is "not
  as steep as the fall-off of a Gaussian", then "the slope moderates abruptly into an inverse-square law that
  is closely followed over a factor of 1000 in angular distance. Integration shows that the inverse-square
  halo, which has sometimes been called the aureole, contains about five percent of the star's light." The
  origin of the r^-2 law is "a particular puzzle".
- **Relevance to us:** the canonical statement that a star image is core plus aureole, and the first number
  for the aureole's share: about 5 percent.
- **Prediction:** if 5 percent of the light sits in an r^-2 aureole, a profile with none (our PSF store) is
  missing that 5 percent wherever it lands, mostly at large radii; the amplitude fitted in a window that
  holds some aureole light is then too high for a core-only render (see 6.1).

### 2.3 Racine 1996, The telescope point spread function

- **Citation:** Racine, R. 1996, "The Telescopic Point-Spread Function", Publications of the Astronomical
  Society of the Pacific 108, 699-705. DOI 10.1086/133788.
- **Verified:** full text read (7 pages), publisher PDF https://iopscience.iop.org/article/10.1086/133788/pdf ;
  Crossref record checked.
- **What:** CCD (B band) and IR (H band) profiles from three telescopes, extended to 200 arcsec with a 500 s
  exposure. The inner profile follows the Kolmogorov prediction "out to a radius of approx. 10 HWs, which
  covers a factor of approx. 4000 in surface brightness" (HW = half the FWHM). "Beyond 10 HWs, the Kolmogorov
  profile lies increasingly below the PSF aureole." King's profile is described as "an exponential core
  superposed on an theta^-2 aureole ... which dominates beyond approx. 10 HWs". A single Moffat of beta 4 fits
  the Kolmogorov profile "over a range of approx. 7 mag in surface brightness"; the sum of two Moffats of the
  same HW, one with beta 7 holding 80 percent of the flux (86 percent of the central intensity) and one with
  beta 2, fits over more than 15 mag. The aureole "cannot result from turbulence"; candidates are aerosols,
  micro-ripples and dust on the optics, and light returned to the detector by reflection. A diffuse-reflection
  model, I(theta) = f F / (8 pi d0^2) cos^3(atan(theta / 2 d0)), fits with f = 0.03 +/- 0.01 (d0 = 100 HW,
  King's data) and f = 0.020 +/- 0.005 (d0 = 10 HW, CCD data). Its intensity "can be strongly enhanced by the
  presence of hardly detectable cirrus clouds". The B and H inner profiles agree in shape, so the core's shape
  is independent of wavelength away from the diffraction limit.
- **Relevance to us:** gives the radius where the aureole takes over (about 5 FWHM), the share (2 to 3
  percent in the reflection fits, 5 percent for King), and the two-Moffat form: a high-beta core plus a
  beta-2 component, which is close to what R0's field profile with its residual table amounts to.
- **Prediction:** our 4 FWHM window ends just before the aureole dominates, so a wing fit in it sees a mix of
  core and aureole and no single Moffat matches both (A.3).

### 2.4 Trujillo et al. 2001, The Moffat PSF

- **Citation:** Trujillo, I., Aguerri, J. A. L., Cepa, J., Gutierrez, C. M. 2001, "The effects of seeing on
  Sersic profiles. II. The Moffat PSF", Monthly Notices of the Royal Astronomical Society 328, 977-985.
  DOI 10.1046/j.1365-8711.2001.04937.x.
- **Verified:** method section read (sections 1 and 2), arXiv https://arxiv.org/abs/astro-ph/0109067 ;
  Crossref record checked.
- **What:** PSF(r) = (beta-1)/(pi alpha^2) [1 + (r/alpha)^2]^-beta, normalised to unit flux, with FWHM =
  2 alpha sqrt(2^(1/beta) - 1). The beta that best matches the atmospheric-turbulence PSF is 4.765; a Moffat
  tends to a Gaussian as beta goes to infinity. "The PSFs usually measured in real images have bigger 'wings',
  or equivalently smaller values of beta, than those expected from the turbulence theory ... because the real
  seeing not only depends on atmospheric conditions but is also caused by imperfections in telescope optics."
  IRAF's default is beta 2.5; they bracket real data with beta 5, 2.5 and 1.5.
- **Relevance to us:** a fitted beta above about 5 is narrower in the wings than pure turbulence allows. Our
  PSF store's beta 4 to 25 therefore describes the core only; R0's field beta of 2.5 to 8.6 is in the range
  real images give.

## 3. Extended PSFs built from stars of different brightness

### 3.1 Slater, Harding and Mihos 2009, Removing internal reflections

- **Citation:** Slater, C. T., Harding, P., Mihos, J. C. 2009, "Removing Internal Reflections from Deep
  Imaging Data Sets", Publications of the Astronomical Society of the Pacific 121, 1267-1278.
  DOI 10.1086/648457.
- **Verified:** method section read (abstract, PSF section, summary, Table 1), arXiv
  https://arxiv.org/abs/0909.3320 ; Crossref record checked.
- **What:** the Burrell Schmidt PSF to 1 degree from long exposures of Arcturus plus reflections modelled as
  out-of-focus pupil images whose offset from the star grows linearly with field position. Inside 5 arcmin
  the profile's local power-law slopes run from -3 to -2.4; beyond 5 arcmin about -1.6. The region from 2.4 to
  60 arcmin holds 1.2 percent of the star's light. The reflections (CCD to dewar window, CCD to filter) have
  radii of 2.7 to 19.5 arcmin and reflectivities of 0.2 to 1.6 percent.
- **Code / licence:** none found.
- **Relevance to us:** reflections between the sensor and a filter or window are not part of a radial
  profile; they are offset rings that move across the field. Our cameras have a window and filters a few mm
  to cm from the sensor, so the same ghosts exist; a radial aureole model will not remove them.

### 3.2 Sandin 2014, The PSF and its role in observations of NGC 5907

- **Citation:** Sandin, C. 2014, "The influence of diffuse scattered light. I. The PSF and its role in
  observations of the edge-on galaxy NGC 5907", Astronomy and Astrophysics 567, A97.
  DOI 10.1051/0004-6361/201423429.
- **Verified:** method section read (sections 1 and 2), arXiv https://arxiv.org/abs/1406.5508 ; Crossref
  record checked.
- **What:** a compilation of every radially extended PSF published to 2014 (Table 1), normalised to a 0 mag
  star. The core is "described by a Moffat profile, using Kolmogorov statistics"; the aureole is attributed to
  aerosols, dust, micro-ripples on optical surfaces and reflections in the instrument. "The comparison of the
  PSFs demonstrates a lower-limit r^-2 power-law decline at larger radii." Energy in the aureole plus the
  "blue sky" component: about 28 percent (CV83's PSF), 18 percent (its other core), 6.5 percent (King's B-band
  PSF) and 8 percent (Michard's V-band PSF), with "no simple correlation" with atmospheric extinction. The
  Dragonfly refractor PSF "lies markedly lower than all other PSFs where r >~ 30 arcsec". PSFs vary in time
  (Michard's two V-band PSFs three months apart differ by up to 1 mag/arcsec^2; an aureole at one observatory
  grew tenfold in a few months, correlated with mirror cleaning) and show weak wavelength dependence except
  the CCD red halo in the i band. Masking spikes underestimates the PSF.
- **Relevance to us:** the aureole fraction is anywhere from about 5 to 30 percent of the light depending on
  telescope, epoch and how far out one integrates, and it changes with cleanliness. It must be measured per
  master, never assumed from a published value.

### 3.3 Sandin 2015, Galaxy haloes and thick discs

- **Citation:** Sandin, C. 2015, "The influence of diffuse scattered light. II. Observations of galaxy haloes
  and thick discs and hosts of blue compact galaxies", Astronomy and Astrophysics 577, A106.
  DOI 10.1051/0004-6361/201425168.
- **Verified:** abstract read, plus introduction and conclusions, arXiv https://arxiv.org/abs/1502.07244 ;
  Crossref record checked.
- **What:** convolving galaxy models with the PSFs of paper I produces "bright scattered-light haloes and high
  amounts of red excess at large radii". "Any radial differences between PSFs of different wavelengths or
  bandpasses will induce artificial colour profiles that could be either red or blue."
- **Relevance to us:** a star remover that leaves an aureole behind leaves a coloured halo when the channels'
  aureoles differ. The leftover is not only an amplitude error but a colour error.

### 3.4 Abraham and van Dokkum 2014, The Dragonfly Telephoto Array

- **Citation:** Abraham, R. G., van Dokkum, P. G. 2014, "Ultra-Low Surface Brightness Imaging with the
  Dragonfly Telephoto Array", Publications of the Astronomical Society of the Pacific 126, 55-69.
  DOI 10.1086/674875.
- **Verified:** method section read (abstract and section 4.2), arXiv https://arxiv.org/abs/1401.5473 ;
  Crossref record checked.
- **What:** eight Canon 400 mm f/2.8 telephoto lenses on commercial CCD cameras. Vega imaged at a range of
  exposure times, the sub-profiles "stitched together" out to about one degree. "The minimum radius plotted is
  0.1 arcmin, and >~ 90% of the total stellar flux is interior to this." The lenses show "a factor of 5-10
  less scattered light at radii > 5 arcmin than the Burrell Schmidt", credited mainly to nanostructured
  anti-reflection coatings. A footnote argues that a dirty refractive objective mostly scatters light
  backwards, out of the beam, while dust on a mirror scatters into it.
- **Relevance to us:** the nearest published analogue to an amateur refractor with a cooled camera.
  Refractive optics have a fainter far aureole than reflectors, but not a missing one; the "over 90 percent
  within 6 arcsec" leaves up to 10 percent outside.

### 3.5 Trujillo and Fliri 2016, Beyond 31 mag/arcsec^2

- **Citation:** Trujillo, I., Fliri, J. 2016, "Beyond 31 mag arcsec^-2: The Frontier of Low Surface
  Brightness Imaging with the Largest Optical Telescopes", The Astrophysical Journal 823, 123.
  DOI 10.3847/0004-637X/823/2/123.
- **Verified:** method section read (section 4 on scattered light and the GTC PSF), arXiv
  https://arxiv.org/abs/1510.04696 ; Crossref record checked.
- **What:** the GTC PSF to 5 arcmin from gamma Dra (V = 2.36) in 0.5 s exposures; the star "appears saturated
  in its innermost region (< 10 arcsec)", so the core is replaced by a stack of unsaturated field stars and
  the two matched. The scattered-light field of all stars with R < 17 is built by placing the PSF at each star,
  "normalized to the flux provided by the USNO catalogue".
- **Relevance to us:** where an external catalogue flux is available, a saturated star can be normalised
  without its core at all. We do not have a calibrated catalogue flux in our bands, so this is a cross-check
  at best (a plate-solved master could compare R0's flux with Gaia photometry).

### 3.6 Karabal et al. 2017, Deconvolution of instrumental scattered light

- **Citation:** Karabal, E., Duc, P.-A., Kuntschner, H., Chanial, P., Cuillandre, J.-C., Gwyn, S. 2017,
  "A deconvolution technique to correct deep images of galaxies from instrumental scattered light",
  Astronomy and Astrophysics 601, A86. DOI 10.1051/0004-6361/201629974.
- **Verified:** method section read (abstract and section 2.3), arXiv https://arxiv.org/abs/1612.05122 ;
  Crossref record checked.
- **What:** MegaCam PSFs with an inner part from PSFEx stacks of faint stars (to about 6.5 arcsec) and an
  outer part modelled by hand from the ghosts of a bright saturated neighbour, joined by "the median ratio of
  the pixel values in a common region" of 1.1 arcsec. "The flux integrated between approx. 50-200 arcsec
  accounts for only 1.6%; 98% of the flux is concentrated within the inner 10 arcsec." Beyond 75 arcsec the r
  band carries more reflected light than g, "mainly due to the CCDs being more reflective in the r-band".
  Worse seeing gives more prominent halos, "about 45% at 100 arcsec", against a scatter of about 30 percent
  per seeing bin; normalising at 10 arcsec instead of 3 arcsec "does not change the results".
- **Relevance to us:** a 2 percent outer halo, a chromatic reflection term, and a halo that changes with
  seeing. A stack mixes nights and seeing, so its aureole is an average that a per-master measurement
  captures and a published PSF does not.

### 3.7 Infante-Sainz, Trujillo and Roman 2020, The SDSS extended PSFs

- **Citation:** Infante-Sainz, R., Trujillo, I., Roman, J. 2020, "The Sloan Digital Sky Survey extended point
  spread functions", Monthly Notices of the Royal Astronomical Society 491, 5317-5329.
  DOI 10.1093/mnras/stz3111.
- **Verified:** full text read (14 pages), arXiv https://arxiv.org/abs/1911.01430 ; Crossref record checked.
- **Code / licence:** the paper names a reproducible project at
  https://gitlab.com/infantesainz/sdss-extended-psfs-paper/ (Git commit v0.5-0-g62d83df) built on GNU
  Astronomy Utilities. I did not open the repository or check its licence.
- **What:** PSFs to 8 arcmin in u, g, r, i, z from about 1000 stars per part. Three parts from three
  brightness ranges: saturated stars brighter than 7 mag for the outer part, 9 mag stars for the
  intermediate part, unsaturated stars fainter than 14 mag for the core. Each star is normalised on a 1-pixel
  ring at a fixed radius (60 arcsec outer, chosen "close enough to the centre, therefore a good S/N is
  obtained, but ... distant enough to avoid the region where most of the internal reflections ... are found";
  7 arcsec intermediate; 2 arcsec core) and stacked with a 3-sigma-clipped median. Each star contributes only
  out to the radius where it falls to the survey's surface-brightness limit. Parts are joined by solving for a
  multiplicative factor and an additive sky at one pixel either side of a junction radius chosen where the
  two parts have equal S/N. Profiles "decline as power laws (r^-alpha) with an exponent close to alpha = 2.49"
  over 20 magnitudes of dynamic range. To subtract a real saturated star they compute "the ratio between the
  radial profile of the star and the radial profile of the PSF" for every radius "between 0.1 times the
  saturation level and 3 times the value of the background", take the 3-sigma-clipped median as the scale
  factor, and work hierarchically, brightest star first, in two iterations with remasking. The i-band PSF has
  an extra "extended halo of light between 7 and 70 arcsec".
- **Relevance to us:** this is the reference recipe for our problem: a PSF that already contains the aureole,
  a normalisation that starts at a tenth of saturation (not 0.7 of the plateau), a robust ratio, and a fixed
  ring per class of star. The junction recipe (scale plus sky offset at a radius of equal S/N) is how R0's
  field profile and a bright-star aureole could be joined.
- **Prediction:** with the core cut lowered from 0.7 to 0.1 of the plateau, R0's normalisation of the barely
  saturated star loses the pixels most at risk from near-clip effects; see 6.2.

### 3.8 Roman, Trujillo and Montes 2020, Galactic cirri in deep optical imaging

- **Citation:** Roman, J., Trujillo, I., Montes, M. 2020, "Galactic cirri in deep optical imaging",
  Astronomy and Astrophysics 644, A42. DOI 10.1051/0004-6361/201936111.
- **Verified:** method section read (section 2, star modelling), arXiv https://arxiv.org/abs/1907.00978 ;
  Crossref record checked.
- **What:** Stripe 82 PSFs to 12 arcmin. For each star the saturated pixels are masked, the centre is taken
  from a Moffat fit to the masked star, then "we scaled the radial profile of the PSF to match in flux the
  profile of the star" over a radius range that "depends on the tentative magnitude": "the higher the
  luminosity of the star, the larger the range in radius", truncated where a neighbour makes the profile
  rise. The flux is the average of the star/PSF ratios over that range "discarding outliers beyond 2 sigma".
- **Relevance to us:** same family as 3.7, but with a range that grows with brightness, as R0's does. That is
  safe only because their PSF already holds the aureole, so a ratio taken further out is still unbiased. With
  a profile lacking the aureole, a range that grows with saturation biases the amplitude upwards with depth
  (A.3).

### 3.9 Liu et al. 2022, elderflower: a method to characterise the wide-angle PSF

- **Citation:** Liu, Q., Abraham, R., Gilhuly, C., van Dokkum, P., Martin, P. G., Li, J., et al. 2022, "A
  Method to Characterize the Wide-angle Point-spread Function of Astronomical Images", The Astrophysical
  Journal 925, 219. DOI 10.3847/1538-4357/ac32c6.
- **Verified:** method section read (sections 1 to 8: model, normalisation, the M44 PSF, telescope comparison,
  dust, caveats), arXiv https://arxiv.org/abs/2110.11598 ; Crossref record checked.
- **Code / licence:** https://github.com/qliu4676/elderflower , MIT licence (checked through the GitHub API).
- **What:** a Bayesian forward model of all bright stars plus a sky (first-order Legendre plane), pixel by
  pixel. PSF = (1 - fp) Moffat(gamma, alpha) + fp x multi-power-law, "flattened within the inner theta0 = 5
  arcsec for numerical stability". The core Moffat is fixed from a stack of bright unsaturated stars; the
  power-law indices, transition radii and fp are fitted (fp with a log-uniform prior from 1 to 40 percent).
  Saturated stars are normalised by "the 3-sigma-clipped azimuthally averaged intensity I0 at a scale radius,
  r0", with the light of other stars and the sky subtracted iteratively; r0 is "chosen to keep away from the
  saturated core of the brightest star ... while keeping I0 at least 5 sigma above an (estimated) background",
  a thin annulus at about 30 arcsec for Dragonfly. The intensity at r0 is converted to total flux "by
  integrating IPSF to infinity" with the same model (their Eq. 10). For M44 (2.85 arcsec/px) the fitted model
  is fp = 0.3, alpha = 6.7, gamma = 6.1 arcsec, power-law indices 3.62, 2.90 and 1.89, transitions at
  10^0.7, 10^1.73 and 10^2.1 arcsec (5, 54 and 126 arcsec). Before lens cleaning the far wing is flatter; both
  nights follow "r^-3.5 out to around 1 arcmin and then flatten out to a power law of r^-2.8". The
  measured power indices in the literature range "from 1.6 to 3". A footnote says the spikes in Dragonfly data
  "are very low-level artifacts from the micro lens array on the sensor".
- **Relevance to us:** the closest model to our case: refractive optics, a pixel scale like ours, a high-beta
  Moffat core (6.7, inside our PSF store's range) and a separate power-law component carrying 30 percent of
  the flux. It says our PSF store's high beta is a core fit, and that the missing light belongs in a second
  component, not in a lower beta. It also states the consistency rule of finding 2 in a formula.
- **Prediction:** about 3 percent of the total light lies beyond 4 FWHM and 1.5 percent beyond 30 arcsec in
  this model (my arithmetic, A.4). At 1 to 4 arcsec/px, 30 arcsec is 8 to 30 px, so much of the aureole is
  outside R0's window.

### 3.10 Garate-Nunez et al. 2024, The Hyper Suprime-Cam extended PSFs

- **Citation:** Garate-Nunez, L. P., Robotham, A. S. G., Bellstedt, S., Davies, L. J. M.,
  Martinez-Lombilla, C. 2024, "The Hyper Suprime-Cam extended point spread functions and applications",
  Monthly Notices of the Royal Astronomical Society 531, 2517-2530. DOI 10.1093/mnras/stae1292.
- **Verified:** method section read (sections 3 and 4.1), arXiv https://arxiv.org/abs/2309.16244 ; Crossref
  record checked.
- **What:** HSC PSFs in g, r, i, Z, Y to about 5.6 arcmin by median stacking stars of four brightness classes,
  each normalised by the median flux in an annulus (400 to 410 px for the outer stack, chosen "as close to the
  centre as possible but avoiding the central saturated region"). "Our PSF accounting for 97.2% of the total
  light within R < 0.1 arcmin. This means that less than approx. 3% of the total flux is contained in the
  extended wings." To fit a real bright star, "the central part of each image is masked up to a radius where
  the pixels are no longer saturated", because saturated pixels "introduce fake flux measurements", and the
  ProFit point-source template rescales the PSF to the star.
- **Relevance to us:** another measurement of a 3 percent outer share, and the same fitting rule. (This is the
  HSC paper; I found no LSST or HSC bright-star paper by a "Garcia".)

### 3.11 Bazkiaei et al. 2024, The Rubin bright star subtraction pipeline

- **Citation:** Bazkiaei, A. E., Kelvin, L. S., Brough, S., O'Toole, S. J., Watkins, A., Schmitz, M. A. 2024,
  "Bright star subtraction pipeline for LSST: phase one report", Proceedings of SPIE, Software and
  Cyberinfrastructure for Astronomy VIII, paper 137. DOI 10.1117/12.3019590.
- **Verified:** full text read (12 pages), arXiv https://arxiv.org/abs/2408.04387 ; Crossref record checked.
- **Code / licence:** https://github.com/lsst/pipe_tasks , GPL-3.0. On 2026-10-03 the code lives in
  `python/lsst/pipe/tasks/extended_psf/` (the paper's module names have since changed). I read
  `extended_psf_subtract.py`: the per-star amplitude is a linear least-squares fit of the warped extended PSF
  plus a background polynomial (`bg_order`, default 1, "a planar background"), over pixels not flagged
  SATURATED (or BAD, CROSSTALK and others) with model value above `min_model_value`. `extended_psf_stack.py`
  stacks with a clipped mean and fits a Moffat2D to the stack for diagnostics only.
- **What:** stamps are normalised by the flux in an annulus (default 70 to 80 px). "We use an annulus for
  normalising stamps because the central regions of stars are saturated and useless." On HSC data: "smaller
  annuli can exclude stars due to overlap with saturated regions, while larger annuli may compromise data
  quality because of lower signal-to-noise ratios", so the annulus is "crucial". Two scaling modes exist,
  "annularFlux" and "leastSquare" (the default).
- **Relevance to us:** a production pipeline doing what R0 does (amplitude plus a plane, saturated pixels
  out), but with an empirical extended PSF instead of a Moffat, and with the warning that where the
  normalisation is measured relative to the saturated region changes the answer.

### 3.12 Cuillandre et al. 2025, Euclid Early Release Observations

- **Citation:** Cuillandre, J.-C., Bertin, E., Bolzonella, M., Bouy, H., Gwyn, S., et al. 2025, "Euclid:
  Early Release Observations. Programme overview and pipeline for compact- and diffuse-emission photometry",
  Astronomy and Astrophysics 697, A6 (journal reference as given on arXiv).
- **Verified:** method section read (section 9.2), arXiv https://arxiv.org/abs/2405.13496 . I did not find the
  Crossref record by search, so the journal reference is the arXiv one.
- **What:** the VIS and NISP extended PSFs to 5 arcmin from HD 1973 plus intermediate and unsaturated stars;
  "the detector saturates in the brightest central regions ... This information is needed to anchor the
  energy found in the extended halo to the energy found in the core of the PSF". Each star's profile is used
  only where it is robust (no flat saturated top, above the noise) and all are rescaled jointly. About 90
  percent of the energy lies within 1 arcsec, 99 percent within 10 arcsec; beyond 10 arcsec the halo holds
  about 1 percent and the spikes 0.5 percent. The far slope follows the r^-3 of pure diffraction and "never
  reaches an r^-2.5 slope", which would indicate "defects, dust, and various aberrations" on the mirror. The
  extended PSF "allows for accurate magnitude estimation of any saturated star by aligning its profile outside
  the saturated core".
- **Relevance to us:** even a space telescope with no atmosphere keeps about 1.5 percent of the light beyond
  10 arcsec. An r^-3 slope is the clean-optics floor; r^-2 to r^-2.5 means scatter.

## 4. Brightness of saturated stars from their wings

### 4.1 Gilliland 2004, CCD linearity beyond saturation

- **Citation:** Gilliland, R. L. 2004, "ACS CCD Gains, Full Well Depths, and Linearity up to and Beyond
  Saturation", Instrument Science Report ACS 2004-01, Space Telescope Science Institute.
- **Verified:** abstract and introduction read, STScI PDF
  https://www.stsci.edu/files/live/sites/www/files/home/hst/instrumentation/acs/documentation/instrument-science-reports-isrs/_documents/isr0401.pdf
- **What:** with a gain that samples the full well, the ACS CCDs "remain perfectly (< 0.1% relative count
  levels) linear to well beyond saturation with simple summation over pixels that have been bled into";
  "electrons are clearly conserved after saturation occurs", as found earlier for STIS (Gilliland,
  Goudfrooij and Kimble 1999).
- **Relevance to us:** the best way to measure a saturated star, summing its bled charge, is not open to us.
  Our CMOS sensors clip at full well and do not bleed, so the clipped charge is gone and only the wings carry
  the star's brightness. That puts the whole burden on the wing profile.

### 4.2 The WISE and NEOWISE explanatory supplements, saturated-source photometry

- **Citation:** WISE All-Sky Release Explanatory Supplement, section VI.3.c.i.4, "Measurement Biases: High S/N
  Bright Stars and Saturated Sources" (IRSA web document, last updated 2019 July 15); NEOWISE Explanatory
  Supplement, section II.1.c.iv.a, "Saturation Photometric Bias Corrections". I did not record an author list
  from the pages and give none.
- **Verified:** the relevant sections read in full,
  https://irsa.ipac.caltech.edu/data/WISE/docs/release/All-Sky/expsup/sec6_3c.html and
  https://irsa.ipac.caltech.edu/data/WISE/docs/release/NEOWISE/expsup/sec2_1civa.html
- **What:** the PSF template fit normally covers 1.25 FWHM; "for very bright sources that saturate the
  detectors, the fitting radius is enlarged to twice the radius of the saturated region, and the template fit
  is performed on the non-saturated pixels in the wings". Against 2MASS Ks, W1 shows "relatively little bias
  for about 4 magnitudes beyond saturation, after which the flux becomes systematically underestimated"; W2
  shows "a pronounced flux-overestimation bias [that] starts approx. 0.5 mag after saturation (6.5 mag)". They
  "ruled out ... nonlinearity and errors in sky background estimation" and "found that the PSF shape is
  flux-dependent in the saturated regime, and attribute the bright star flux biases to that effect"; the cause
  is "not known". 2MASS stars with Ks < 3 to 4 saturated the 51 ms Read_1 exposures and were measured by
  "fitting a 1-dimensional template to the non-saturated parts of the azimuthally averaged source profiles".
- **Relevance to us:** a large survey doing R0's fit, with a measured answer: the wing fit is biased, the bias
  changes with saturation depth, and its sign can differ by band. It is also the only primary source I found
  that reports a saturated-star PSF changing with flux, which is one reading of our trend (finding 5).

### 4.3 Magnier et al. 2020, Pan-STARRS pixel analysis

- **Citation:** Magnier, E. A., Sweeney, W. E., Chambers, K. C., Flewelling, H. A., Huber, M. E., Price, P. A.,
  et al. 2020, "Pan-STARRS Pixel Analysis: Source Detection and Characterization", The Astrophysical Journal
  Supplement Series 251, 5. DOI 10.3847/1538-4365/abb82c.
- **Verified:** method section read (section 4.6.1 and the flag tables), arXiv
  https://arxiv.org/abs/1612.05244 ; Crossref record checked.
- **What:** "The standard psphot PSF modeling code fails to fit the wings of highly saturated stars,
  especially if the core of the star is too contaminated by saturated pixels. For stars with more than a
  single saturated pixel, we model the radial profile of the logarithmic instrumental flux in logarithmically
  spaced radial bins", median per bin, interpolated, and subtract that.
- **Relevance to us:** a PSF model built on unsaturated stars did not describe a saturated star's wings well
  enough to subtract them, so the survey subtracts each saturated star's own measured radial profile. For a
  remover that is an option where a fit fails; for an injector it is the reason to take the profile from real
  saturated stars, not from unsaturated ones.

### 4.4 White et al. 2017, Halo photometry

- **Citation:** White, T. R., Pope, B. J. S., Antoci, V., Papics, P. I., Aerts, C., Gies, D. R., et al. 2017,
  "Beyond the Kepler/K2 bright limit: variability in the seven brightest members of the Pleiades", Monthly
  Notices of the Royal Astronomical Society 471, 2882-2901. DOI 10.1093/mnras/stx1050.
- **Verified:** method section read (abstract and section 2), arXiv https://arxiv.org/abs/1708.07462 ;
  Crossref record checked.
- **What:** a light curve built "from a weighted sum of the non-saturated pixels" of the scattered-light halo,
  weights positive and summing to one, chosen to minimise the total variation of the light curve.
- **Relevance to us:** relative, not absolute, photometry, but it works only because a halo pixel's flux is
  proportional to the star's. That is the empirical basis for treating the aureole as a fixed fraction of the
  flux (finding 5).

### 4.5 Su et al. 2022, Photometry of saturated stars by the PSF wing technique

- **Citation:** Su, K. Y. L., Rieke, G. H., Marengo, M., Schlawin, E. 2022, "Accurate Photometry of Saturated
  Stars Using the Point-spread-function Wing Technique with Spitzer", The Astronomical Journal 163, 46.
  DOI 10.3847/1538-3881/ac3b5e.
- **Verified:** method section read (abstract, sections 1 to 3.3), arXiv https://arxiv.org/abs/2111.10054 ;
  Crossref record checked.
- **What:** Spitzer IRAC photometry of 11 stars relative to Sirius from the radial profiles outside the
  saturated core (r >~ 10 arcsec masked), "better than 1% relative photometry ... in the radial range of 20-100
  arcsec". The ratio of two stars' profiles "is expected to be constant in an ideal situation (e.g., PSF is
  stable ...)". Method 1 keeps points within 1 sigma of the cumulative average ratio, weighting small radii;
  method 2 adjusts "a small background offset" until the ratio is flat with radius, then takes the median.
- **Relevance to us:** the check to adopt: a profile is right for a star when the star/profile ratio is flat
  with radius. Su et al. reach 1 percent with a space PSF that does not change; a ratio that drifts, as ours
  did from 0.6 to 20, means the profile is wrong before any amplitude question arises.

## 5. How the aureole scales with the star, and its colour

### 5.1 de Jong 2008, PSF tails and edge-on galaxy haloes

- **Citation:** de Jong, R. S. 2008, "Point spread function tails and the measurements of diffuse stellar halo
  light around edge-on disc galaxies", Monthly Notices of the Royal Astronomical Society. DOI
  10.1111/j.1365-2966.2008.13505.x. (The Crossref record I saw lists no volume or pages, so I give none.)
- **Verified:** method section read (abstract and section 3.1), arXiv https://arxiv.org/abs/0807.0229 ;
  Crossref record checked.
- **What:** SDSS profiles from a bright unsaturated star and the brightest (core-saturated) star of each frame,
  "matching these profiles in the high signal-to-noise overlap region", to 200 px and 16 mag below peak.
  Normalised within 10 px, they show "a central, Gaussian-like core surrounded by a faint envelope. The width
  of the core depends on camera column and time ... The surrounding envelope is very similar in shape across
  all frames, with a brightness that depends only on the total brightness of the star. This halo is most
  likely caused by scattering off of dust particles on the telescope optics. The shape and brightness of the
  halo is the same in all filters with a near power law profile of slope about -2.6. The only exception is the
  i-band filter, which shows an additional exponential component dominating between 35 and 180 pixels", typical
  of thinned back-illuminated CCDs.
- **Relevance to us:** the clearest primary statement that the aureole is a fixed fraction of the flux,
  independent of the seeing core, and nearly achromatic in the visible. It is why the core (from the PSF store
  or the field) and the aureole should be separate components in the injector: the core width varies, the
  envelope does not.
- **Prediction:** at a fixed radius, real/field should not depend on how saturated the star is. Our trend
  (finding 5) is therefore a measurement effect until shown otherwise.

### 5.2 Sirianni et al. 2005, The red halo of thinned CCDs

- **Citation:** Sirianni, M., Jee, M. J., Benitez, N., Blakeslee, J. P., Martel, A. R., Meurer, G., et al.
  2005, "The Photometric Performance and Calibration of the Hubble Space Telescope Advanced Camera for
  Surveys", Publications of the Astronomical Society of the Pacific 117, 1049-1112. DOI 10.1086/444553.
- **Verified:** method section read (section 4.3, "Long Wavelength Scattered Light"), arXiv
  https://arxiv.org/abs/astro-ph/0507614 ; Crossref record checked.
- **What:** thinned back-illuminated CCDs are partly transparent in the near infrared ("A 16 um-thinned CCD
  transmits approx. 5% of 8000 A photons and approx. 85% of 1 um photons"); the light scatters in the substrate
  and returns as a halo. "The onset of the halos occurs at a wavelength of approx. 7500 A"; in the HRC "the
  intensity of the halo increases with the wavelength and reaches about 20% of the total energy at 1 um". The
  halo depends on the source's colour within one filter: "the PSF for the red star will therefore be broader
  than the one of the blue star."
- **Relevance to us:** a red halo needs light beyond about 750 nm. A colour camera with an IR-cut filter, or a
  mono camera behind an L or RGB filter ending near 700 nm, should see little of it; a camera without an
  IR cut, or a red filter that leaks, can. Because the halo depends on the star's colour, red giants (common
  among the brightest stars in a field) may carry more red halo than blue stars in the same channel, which
  would scatter the per-star aureole fraction.

### 5.3 DeVore, Kristl and Rappaport 2013, Stellar aureoles from cirrus

- **Citation:** DeVore, J. G., Kristl, J. A., Rappaport, S. A. 2013, "Retrieving cirrus microphysical
  properties from stellar aureoles", Journal of Geophysical Research: Atmospheres 118, 5679-5697.
  DOI 10.1002/jgrd.50440.
- **Verified:** abstract read, plus sections 2 and 3.1.2, arXiv https://arxiv.org/abs/1307.2946 ; Crossref
  record checked.
- **What:** thin cirrus makes aureoles measurable to about 0.2 degree from stars. Their profiles are
  "approximately flat out to a critical angle followed by gradually steepening power-law falloff with slope less
  steep than -3". For diffraction by ice crystals, "the scattering angle theta scales with lambda" and the
  amplitude "scales inversely with lambda^2". Water clouds of small droplets give no visible aureole at those
  angles.
- **Relevance to us:** amateur subs are often taken through thin cirrus; Racine (1996) already suspected
  cirrus of brightening his aureole. Such an aureole varies from sub to sub and is wider in red than in blue.
  The master holds the average, which is another reason to measure the aureole per master and per channel.

### 5.4 Bennett and Porteus 1961, Surface roughness and specular reflectance

- **Citation:** Bennett, H. E., Porteus, J. O. 1961, "Relation Between Surface Roughness and Specular
  Reflectance at Normal Incidence", Journal of the Optical Society of America 51, 123. DOI
  10.1364/JOSA.51.000123.
- **Verified:** abstract read (the publisher's abstract as mirrored by OpenAlex) and the Crossref record. I did
  not read the paper's equations.
- **What:** for roughness small against the wavelength, the loss of specular reflectance "is a function only
  of the root mean square height of the surface irregularities", and long-wavelength measurements give the
  roughness. The standard form of their result is a scattered fraction of about (4 pi sigma / lambda)^2 for rms
  roughness sigma; I quote that form from general knowledge, not from the abstract.
- **Relevance to us:** roughness scatter falls as lambda^-2, so blue scatters about 1.7 times as much as red
  between 450 and 600 nm (my arithmetic: (600/450)^2 = 1.78). Dust scatter (larger particles) is far less
  chromatic. Combined with 5.1 to 5.3, expect channel differences of tens of percent in the aureole, not
  factors of ten.

## 6. What it predicts for our injector and for R0

### 6.1 The injector

- **Render with the profile that measured the amplitude.** This is now done and the tops match. The general
  rule (Liu et al.'s Eq. 10): an amplitude is a number about one profile; carrying it to another profile needs
  the conversion between them, and the conversion is the ratio of their light in the fit window, not their
  total flux.
- **Model the bright star as two components, not one Moffat.** The literature form is a Moffat core plus an
  aureole: Racine's two Moffats (beta 7 and 2), Liu et al.'s Moffat plus a multi-power-law flattened inside a
  few arcsec, or the Racine diffuse term. The PSF store's Moffat (beta 4 to 25) can stay as the core; the
  aureole should be a separate term with its own flux fraction f and slope n. Starting values from the
  literature: f of 2 to 5 percent of the light beyond about 5 FWHM (King, Racine, Karabal, Garate-Nunez, and
  about 3 percent beyond 4 FWHM in the Dragonfly model, A.4), slope n between 1.6 and 3.6 (Liu et al.'s range
  and their own 3.6 inside 1 arcmin). Our own numbers (real saturated stars 0.04 to 0.28 past 4.5 px of the
  light within 20 px) should set f per master, because the published values span a factor of five and change
  with cleaning, seeing and cirrus (Sandin, Karabal, Liu, DeVore).
- **Scale the aureole with the star's total flux, never with its peak.** de Jong (2008) and halo photometry
  (White et al. 2017) say the envelope tracks the total brightness while the core width varies. The injector
  should therefore keep the aureole's share fixed when it varies the core between virtual subs.
- **Fit f and n per channel.** Expect the red channel to differ most when there is light past 750 nm (Sirianni,
  de Jong), blue to scatter somewhat more from roughness (Bennett and Porteus), and cirrus to widen the red
  aureole (DeVore). Do not share an aureole between channels or masters.
- **Extend the render beyond R0's window.** With 1 to 4 arcsec/px, the Dragonfly model's power law runs from
  about 2 px to 20 px and beyond; 1.5 percent of the light lies beyond 30 arcsec (8 to 30 px). An injected star
  truncated at 13 to 16 px leaves a visible step in a stretched starless plate.
- **Ghosts and spikes are separate.** Reflections between sensor and window or filter are offset rings that
  move with field position (Slater et al.), the 250 mm Newtonian has diffraction spikes, and Dragonfly's sensor
  shows microlens artefacts (Liu et al.). None is in a radial profile, and none should be added to the aureole.

### 6.2 R0

- **What a Moffat-only subtraction leaves.** With the field Moffat alone, a saturated star leaves a positive
  ring beyond about 2 to 4 FWHM: by my simulation (A.3), 1.5 to 4 times the model for beta 3 and up to about
  140 times for beta 4.8 at 10 to 14 px. R0's residual table removes much of this (field profile 0.015 to 0.18 past
  4.5 px against real 0.04 to 0.28), but saturated stars still carry 1.1 to 1.6 times the field's light at 2.5
  to 4 px. In a starless master that leftover is a soft halo, coloured when the channels differ (Sandin 2015).
- **Adopt the literature's normalisation rule.**
  - Fixed annulus for every star, in FWHM units, rather than a window that grows with the plateau (Infante-Sainz
    fixed rings, Liu fixed r0, the Rubin annulus).
  - Core cut at 0.1 of the plateau, as Infante-Sainz et al. do, or outside twice the saturated radius, as WISE
    does, instead of 0.7.
  - Robust ratio: the clipped median of star/profile per radius (Infante-Sainz 3 sigma, Roman 2 sigma), with
    the sky solved alongside (Liu, the Rubin plane).
  - The flatness check of Su et al.: report the slope of star/profile against radius per star; a profile is
    right only when it is flat.
- **Predicted outcome of the finding-5 test.** If the excess's dependence on saturation depth comes from the
  moving window or from near-clip pixels, a fixed annulus and a 0.1 to 0.3 core cut make the Spearman collapse
  towards zero and leave a constant excess, which is then the aureole fraction R0's table lacks at saturated
  brightness. If the Spearman survives the refit, the saturated PSF itself changes with flux, as WISE found;
  then the profile must be taken from saturated stars of similar depth (the Pan-STARRS per-star profile is the
  extreme of that).
- **Subtract brightest first and iterate.** In a crowded field the aureole of one star sits under the
  normalisation annulus of the next (Infante-Sainz hierarchical subtraction, Liu mutual subtraction).
- **Keep R0's tilted plane, but fit it with the star.** A plane fitted alone over the window absorbs aureole
  light and lowers the amplitude; in my simulation the free sky took up as much as about 1 percent of the
  plateau level (3 percent at a peak 100 times the clip).
  A plane fitted outside the aureole, or jointly with a profile that contains it, does not.

## 7. Leads I could not verify, or verified only in part

- **Capaccioli and de Vaucouleurs 1983**, ApJS 52, 465, DOI 10.1086/190879 ("Luminosity distribution in
  galaxies. II"): abstract only (OpenAlex mirror), which says it presents "a new determination of the PSF from
  0 to 90 degrees". The 28 and 18 percent aureole shares I quote are Sandin's (3.2), not read in the paper.
- **Kormendy 1973**, AJ 78, 255, DOI 10.1086/111412: metadata only. Its r^-1.7 slope between 3 and 30 arcmin
  is quoted by Sandin (3.2).
- **Michard 2002, Bernstein 2007 (ApJ 666, 663, metadata checked), Shectman 1974, Watkins et al. 2016,
  Gonzalez et al. 2005**: read only through Sandin (3.2) and Liu et al. (3.9). Watkins et al.'s Burrell Schmidt
  wing of r^-2.4 is Liu et al.'s statement.
- **GNU Astronomy Utilities PSF scripts** (`astscript-psf-stamp`, `-unite`, `-scale-factor`, `-subtract`),
  which implement the Infante-Sainz method: the manual pages at gnu.org refused my requests (HTTP 429, then
  403). I did not read how the scale factor's radii are chosen there.
- **Pope et al. 2019**, "The K2 Bright Star Survey. I. Methodology and Data Release", ApJS 245, 8, DOI
  10.3847/1538-4365/ab3d29: metadata only. Also Rudrasingam et al. 2026 (arXiv 2602.22472), halo photometry of
  98 bright TESS stars: title only.
- **Gilliland, Goudfrooij and Kimble 1999**, PASP 111, 1009, DOI 10.1086/316407: metadata only; its result is
  quoted through Gilliland 2004 (4.1).
- **Baena-Galle et al. 2020** (RNAAS 4, 124) and **Infante-Sainz, Trujillo and Roman 2020** (RNAAS 4, 130), two
  research notes on extended PSFs: metadata only.
- **Marois et al. 2006**, ApJ 647, 612, "Accurate Astrometry and Photometry of Saturated and Coronagraphic
  Point Spread Functions": abstract read; it normalises saturated PSFs with satellite spots from a pupil mask,
  which needs hardware we lack, so I left it out of the review.
- **Sensor non-linearity near full well, and the brighter-fatter effect**, as causes of a PSF that changes
  with flux: not checked for our Sony and Panasonic CMOS sensors. This is the open question behind finding
  5(b) and 5(c).
- **Refractor secondary spectrum** (a coloured halo from residual chromatic aberration in achromats and, less,
  in apochromats) and **microlens diffraction rings around bright stars on front-illuminated CMOS** (often
  reported by amateurs for the ASI1600MM's Panasonic sensor): no primary source found. Both would make the
  aureole chromatic or structured in ways no published survey PSF shows, and the ASI1600MM master being wider
  everywhere past 1 px (ratio 1.2 to 2.9) would fit either. Only Liu et al.'s footnote on microlens artefacts
  is verified.
- **Antilogus et al. 2014** (brighter-fatter in CCDs) and **Zackrisson et al. 2006** (the red halo phenomenon):
  titles only.

## Appendix A. My arithmetic

All scripts were run in Python 3.13 with NumPy and SciPy on 2026-10-03; they are not kept in the repository.

### A.1 Moffat light beyond a radius

For a unit-flux Moffat, the fraction beyond r is (1 + (r/alpha)^2)^(1 - beta) with alpha = FWHM / (2
sqrt(2^(1/beta) - 1)). In FWHM units:

```
beta    >1 FWHM  >2 FWHM  >3 FWHM  >4 FWHM  >6 FWHM  >10 FWHM
1.5     0.546    0.310    0.213    0.161    0.108    0.065
2.0     0.376    0.131    0.063    0.036    0.017    0.006
2.5     0.291    0.066    0.023    0.010    0.003    0.0007
3.0     0.240    0.038    0.009    0.0032   0.0007   0.0001
4.0     0.184    0.015    0.002    0.0004   <0.0001
4.765   0.160    0.009    0.0008   0.0001
6.7     0.127    0.003    0.0001   <0.0001
10      0.103    0.001    <0.0001
25      0.078    0.0001
```

At FWHM 2.5 px, the share of the light within 20 px that lies past 4.5 px is 0.085 (beta 2.5), 0.052 (beta 3),
0.024 (beta 4), 0.0037 (beta 8.6) and 0.0006 (beta 25). The PSF store's measured 0.000 to 0.02 is what beta 4 to
25 gives; R0's 0.015 to 0.18 needs beta 2 to 4 or an added aureole; the real 0.04 to 0.28 needs beta 2.5 or less,
or a few percent of aureole on top of the core.

### A.2 Peak per unit flux, the size of the profile-mismatch error

Peak of a unit-flux Moffat = (beta - 1) / (pi alpha^2). At FWHM 2.5 px:

```
beta    alpha(px)  peak per unit flux  relative to beta 3
2.5     2.211      0.0976              0.922
3.0     2.452      0.1059              1.000
4.0     2.874      0.1156              1.092
4.765   3.159      0.1201              1.134
6.7     3.786      0.1266              1.195
8.6     4.315      0.1300              1.227
25      7.455      0.1375              1.298
```

The same flux rendered with beta 25 instead of beta 2.5 has a peak 1.41 times higher (0.1375 / 0.0976), and with
beta 25 instead of beta 3, 1.30 times. Assumes equal FWHM; a profile with a residual table adds wing light and
raises the factor further.

### A.3 Simulation of R0's wing fit on a core-plus-aureole star

Set-up: pixel-integrated images (5 x 5 subsampling), star centred on a pixel, FWHM 2.5 px. True profile T =
(1 - f) Moffat(FWHM 2.5, beta_core) + f x power law r^-n, flat inside r0 = 2 to 2.5 px, normalised within a 40 px
half-stamp. The "real" star is min(F T, S) with S = 1 and F set so that the unclipped peak is k times S. R0 is
imitated: pixels below 0.7 S, within 4 FWHM plus the plateau radius, fitted by least squares with one amplitude A
and a constant sky (a tilted plane reduces to a constant for a symmetric star), unweighted and, as a check,
inverse-variance weighted (the two agree within 3 percent in A). The re-render is min(A M, S). No noise, no
stacking.

Results (A/F is the fitted flux over the true flux; ratios are real/re-render in rings):

```
case (fit profile = true core Moffat)        k     A/F    ratio at 4 px   5 px    10 px   14 px
beta 3,     f 0.05, n 3                      2     1.00   1.05            1.10    1.62    2.67
beta 3,     f 0.05, n 3                      20    1.05   0.99            1.04    1.53    2.53
beta 3,     f 0.10, n 3                      2     0.99   1.10            1.20    2.25    4.36
beta 3,     f 0.10, n 3                      20    1.11   0.99            1.08    2.02    3.91
beta 4.765, f 0.05, n 3                      2     0.99   1.12            1.33    9.9     69
beta 4.765, f 0.05, n 3                      20    1.09   1.02            1.22    9.0     63
beta 4.765, f 0.10, n 3                      2     0.99   1.24            1.67    18.9    138
beta 4.765, f 0.10, n 3                      20    1.18   1.04            1.40    15.8    115
beta 4.765, f 0.30, n 3.62                   2     1.08   1.26            1.86    19.7    118
beta 4.765, f 0.30, n 3.62                   20    1.30   1.05            1.55    16.4    98
```

When the fit profile is instead a Moffat fitted to an unsaturated copy of the composite star within 2 FWHM (the
analogue of a field Moffat), A/F runs from 0.91 to 1.03 over the same cases and the far ratios fall to 1.9 to 290
depending on the fitted beta. Read from these tables:

- The flux bias of a Moffat-only wing fit is modest, -9 to +30 percent, and grows with saturation depth k when
  the fit profile lacks the aureole (the window moves out into it). In a first run with the star centred on a
  pixel corner and k = 100, A/F reached 1.14 to 1.39 for beta 3 and 1.25 to 2.57 for beta 4.765.
- The far-wing deficit is large and set mainly by the core's beta: 1.5 to 4 times at 10 to 14 px for beta 3,
  10 to 140 times for beta 4.8. Our measured 1.5 to 20 at 5 to 13 px lies in this band.
- The excess at a fixed 4 to 5 px falls by about 5 to 17 percent from k = 2 to k = 20, because the amplitude
  rises by about 5 to 20 percent. That is the sign of our finding-5 trend.
- The free sky absorbs up to about 1.3 percent of S of aureole light inside the window at k up to 20, and
  about 3 percent at k = 100.

A second simulation stacked 40 clipped subs whose FWHM varied by 15 percent (lognormal) and transparency by 8
percent, to test whether partial clipping near the plateau biases the fit. With beta 3.5 cores and f of 0 or
0.05, the field Moffat fitted to the stacked unsaturated star gave A/F of 0.93 to 1.00 at a 0.7 cut and 0.87 to
1.00 at a 0.1 cut, and the ratio at 2 to 4 px stayed within 0.96 to 1.09 for k from 1.2 to 30. Partial
clipping alone, at that seeing scatter, is too small to explain an excess of 1.1 to 1.6 at 2.5 to 4 px; a larger
effect would need real non-linearity near full well, which I have not checked.

### A.4 Light beyond a radius in the Dragonfly model of Liu et al. 2022

The fitted M44 model: 0.7 x Moffat(gamma 6.1 arcsec, alpha 6.7) + 0.3 x power law, constant inside 5 arcsec,
r^-3.62 to 53.7 arcsec, r^-2.90 to 125.9 arcsec, r^-1.89 beyond, continuous at the joins, integrated to 1200
arcsec (their 20 arcmin modelling range). The core FWHM is 2 x 6.1 x sqrt(2^(1/6.7) - 1) = 4.03 arcsec.

```
radius (arcsec)   power-law component beyond   whole PSF beyond
16.1 (4 FWHM)     0.103                        0.031
30                0.051                        0.015
60                0.031                        0.009
126               0.022                        0.007
300               0.014                        0.004
```

So about 3 percent of the light lies beyond 4 FWHM and 1.5 percent beyond 30 arcsec. Assumes the power law's
share fp = 0.3 is defined over the same 20 arcmin range; Liu et al. integrate to the model's maximum range, which
I take to be that.

### A.5 Two small conversions

- Roughness scatter ratio between 450 and 600 nm, assuming the (4 pi sigma / lambda)^2 form: (600 / 450)^2 = 1.78.
- Moffat profile at the first pixel ring, used to read the plateau results: for FWHM 2.5 px and beta 3,
  M(1 px) / M(0) = (1 + (1 / 2.452)^2)^-3 = 0.63, so a real first ring at 0.79 to 0.90 of the maximum means the
  unclipped peak exceeded the clip by only about 1.25 to 1.45 times at that pixel scale (a single-pixel
  estimate that ignores pixel integration and stacking).
