# Star removal: what the literature says, and what it predicted here

The literature behind `docs/plans/star-remover-training.md` (#902), read against what R1's injector measured on 2026-10-03
and 2026-10-04. Three reviews were made on 2026-10-03, one a theme, each entry verified against its DOI, arXiv, ADS,
publisher, datasheet, documentation or code page; each review says for every entry how far it was read, and lists apart
what it could not verify. The reviews in full:

- [The bright star's point spread function, and a saturated star's brightness from its wings](star-removal-literature/bright-star-psf.md) (theme A)
- [How a star saturates, in the sensor and in a stack](star-removal-literature/saturation-in-stacks.md) (theme B)
- [Synthetic stars to train and test a star remover](star-removal-literature/synthetic-stars-for-training.md) (theme C)

A number a review marks **(my arithmetic)** is its own derivation from a cited result, not a published figure. The
measurements below are ours, made with `SaturatedEdgeProbe`, `RawSubLinearityProbe` and `SaturatedPhotometryProbe`
(env-gated, `TianWen.Lib.Tests`) on the ten R1 masters.

## The question that started it

R1 drew saturated stars whose tops were flat over 2 to 3 px where the masters' own are round with a 1 px plateau (first
ring 0.79 to 0.90 of the maximum against 0.92 to 1.00), and real saturated stars were 0.6 to 0.9 of the re-render at 1 to
3 px and 1.5 to 20 times it past 5 px. One hypothesis, that each sub clips on its own pixel grid before the stack's warp,
had already failed (first ring 0.995).

## What the literature said, and what was then measured

### The too-bright core was drawing an amplitude with a profile it was not fitted with

Theme A: every pipeline that normalises a saturated star renders it with the profile that measured the normalisation
(Liu et al. 2022, their Eq. 10; Infante-Sainz et al. 2020; the Rubin bright-star code), and at FWHM 2.5 px the same flux
drawn at beta 25 instead of 2.5 peaks 1.41 times higher (its arithmetic), the size of the miss. Measured: the catalogue's
saturated amplitude is fitted with the plate builder's field profile (its Moffat plus a radial residual table) and was
drawn with the PSF store's single Moffat. Drawn with the builder's own profile the first ring is within 0.01 to 0.04 of the
real one on six of ten masters (0.09 to 0.11 on the SMC and the Lagoon) and the plateau 1 px, as real. This is what R1 now
does (`StarProfileFamily.Field`, "feat(imaging): the injector draws each star with the profile its amplitude was fitted
with, the plate builder's own").

### The wings: the core-plus-aureole star

Theme A: a star is a seeing core a Moffat describes plus a shallow aureole holding a few percent of the light (King 1971,
Racine 1996); a beta 4 Moffat puts under 0.04 percent of its flux past 4 FWHM, published PSFs 1 to 5 percent. Theme C: DES
found its PSF model "slightly too large for bright stars" and the ACS survey calls bright-star wings "very hard to model
accurately". Measured (annulus medians, light past 4.5 px over light within 20 px): real saturated stars 0.04 to 0.28, the
field profile 0.015 to 0.18, the PSF store's Moffat 0.000 to 0.02 on eight of ten masters. Bright unsaturated stars (300 to
5,000 sigma) match the field profile within about 10 percent out to 8 px on four masters and run 20 to 40 percent above it
at 2.5 to 4 px on two (Orion, Antares).

### The halo excess that shrinks with saturation depth: four hypotheses, three refuted

Drawn with the field profile at the catalogue's amplitude, saturated stars still hold 1.1 to 1.6 times the field profile's
light at 2.5 to 4 px, and the excess shrinks as a star is driven further past its clip (Spearman -0.4 to -0.6 on five
masters). The reviews offered four causes; each was tested.

| Hypothesis | From | Test | Result |
|---|---|---|---|
| Seeing varies from sub to sub by tens of percent, so a barely saturated star clips in its sharp subs only and its stacked core is depleted | Theme B (Racine 1996: 0.175 dex) | The stacker's own per-sub FWHM (`SubFwhm` in the PSF store) | **Refuted**: 0.3 to 7.5 percent log scatter per session, least on the masters with the clearest trend (Antares 0.8, Carina 0.5, Leo 1.4); theme B's arithmetic needs 10 to 30 |
| The sensor's response softens near full well | Theme B (Wang and Theuwissen 2017; Glover 2022 for the IMX294 in HCG) | Theme B's breakpoint method on single raw subs (`RawSubLinearityProbe`): peak against the 2.5 to 5 px annulus | **Refuted** on the ASI1600MM at unity gain: linear to the clip, then the hard clip bin for bin (0.83, 0.71, 0.56, 0.41, 0.27, 0.14 against 0.83, 0.74, 0.58, 0.41, 0.26, 0.14). Theme B's gain charts say the ADC clips first above gain 0 on every ZWO body here |
| The stack depletes the core: a star's peak pixel moves with its sub-pixel phase on each sub's own grid | Measured, then theme B's soft-knee arithmetic | A star's raw peak across nine eta Carinae subs, and a render clipping each virtual sub on its own grid at a random phase, then warped back with the bake's clamped Lanczos-3 | A star's peak scatters 14 percent from sub to sub (noise 2, transparency 1.1), but clipping on each sub's own grid moves the near-clip profile only 2 to 15 percent: **not the cause** |
| The catalogue amplitude is biased: R0's wing-fit window grows with the plateau and takes in more halo | Theme A (its simulation of R0's fit; Bazkiaei et al. 2024 on the annulus) | An external truth: each star's Tycho-2 V and B-V, amplitude calibrated on the unsaturated stars of the same master through SPCC's own matcher (`Tycho2ColorCalibration.MatchStars`) | **Confirmed**: the catalogue amplitude is within 17 percent of the photometric one at the median on seven of nine masters, about right for barely saturated stars (0.95 to 1.07) and up to a quarter too bright for the most saturated (0.72 to 0.82 on Leo, Rim, Horsehead; Spearman -0.24 to -0.44) |

What is left after the photometry is a shortfall of the field profile itself, not a saturation effect: against each star's
photometric amplitude the light at 2.5 to 4 px runs on continuously across the saturation threshold (Leo 0.91 to 1.18
unsaturated, 1.03 to 1.17 saturated; Orion 1.20 to 1.38 against 1.11 to 1.32; Horsehead 1.02 to 1.10 against 0.93 to
1.21), so the residual table, measured mostly on fainter stars, under-reads a bright star's near wings by up to about 30
percent. Normalising on the far wing instead (the "depleted core" reading) is refuted by the same photometry: it puts the
amplitude 1.2 to 2 times too high on five masters and widens the tops back to 2 to 7 px.

Two masters fall outside it: on the Lagoon and eta Carinae the photometric amplitude is 1.4 to 1.7 times the catalogue's,
and on eta Carinae (ASI1600MM, luminance) the whole star, wings included, reads 0.73 to 0.81 of the field profile at the
photometric amplitude. A colour term between V and an unfiltered luminance that the calibration's B-V range does not reach
is the likely reading there; not yet tested.

### What the reviews predict for training (theme C, untested)

- **The shape check is circular.** Fitting back a Moffat that was drawn as a Moffat passes while the halo and the tops are
  wrong (SynPipe and DES Y6 both say so of their own checks). The external check is distributional: injected and real
  stars measured with one tool. `SaturatedEdgeProbe` is that for saturated stars; it should become a standing R1 check,
  bright unsaturated stars and halos included.
- **Run R0 on R1's draws** (DES reruns its measurement on Balrog images; DAOPHOT reduces artificial stars like the program
  stars): R0's completeness, holes and dot rate at known positions, the baseline the network must beat.
- **R0's dots under at-site stars matter less than its leftover light**: under an L2 loss a 4 to 5 sigma dot at 3.7
  percent of sites is learned as a mean dip of about 0.02 sigma (its arithmetic); its leftover light is more common.
- **Self-refinement erodes the tails first** (Shumailov 2024; Arazo 2020): read every generation per tail bin and keep a
  fixed share of generation-0 draws as an anchor.

## What is open, and where it is tracked

- R0's saturated fit on a fixed annulus in FWHM units with a 0.1 to 0.3 core cut (theme A's rule), which the photometric
  bias says would take the quarter off the most saturated stars' amplitudes; it changes the plates, so it waits for the
  next R0 pass.
- A residual table that holds bright stars' near wings (fitted with a brightness term, or on the brightest unsaturated
  stars).
- The Lagoon and eta Carinae photometric offsets.
- Theme C's three validations (R0 on R1's draws, a noise-free twin draw, a zero-flux draw).

These are in the plan's R1 section, under #902.

## Duplication checked

The per-sub seeing came from the stacker's own measurement, the warp from `Image.Lanczos3Value`, the catalogue match from
SPCC's `MatchStars`, and the residual table is read through one `RadialCorrection.Interpolate`; no seeing model was
written. The planetary twin's turbulence (`Imaging/Optics`: phase screens, short-exposure PSFs) answers a different
question, a millisecond frame; a deep-sky sub averages it out and its profile is read off the data. Its wide-scatter kernel,
(1 + (r/a)^2)^(-3/2) in `PlanetaryDegrade`, is the aureole form theme A recommends, and is the one to reuse if the injector
ever draws a parametric halo. One duplicate was found and removed: the injector's `StarProfile` carried a copy of the
builder's Gauss-Legendre pixel integration; both now use `PixelQuadrature`.
