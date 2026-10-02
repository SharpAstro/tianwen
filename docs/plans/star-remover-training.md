# Star-remover training (P4): the inject-and-remove bootstrap

**Status: NOT STARTED, and now the first of the model plans in priority (the owner, 2026-10-02: #902 is
`priority:high`, above the gradient plan's de-vignetting model). Nothing built; R0 is next and needs no
GPU.** Design captured in [ai-denoise-deconv.md](ai-denoise-deconv.md) section 2.5, restated here at run
level 2026-09-02 and reviewed against the code and the store on 2026-10-02 (section 9). It was written as
the LAST of the four imaging models because it waited on three things, and all three are in place: the
deconvolver programme's PSF distribution (P2.0 done 2026-09-06; the re-baked store measures it per channel),
the classical flattener (shipped 2026-09-02), and the owner's call on its order, raised when the SAS tier
went. The starless plate is the pipeline's workhorse intermediate (`RemoveStarsStep`, `--split-plates`, the
star/starless dual stretch, the comet layer), so a weak in-house remover would degrade every downstream
step. Until its gates pass, `IStarRemover` is RC-Astro (`sxt`) when licensed, else NOTHING: the SAS
`darkstar_*_AI4` fallback went with that tier on 2026-09-26, so without StarXTerminator the canonical
program runs whole-frame, `--split-plates` writes no plates and `--remove-stars` refuses, and with it the
comet layer.

Companions: [deconvolver-training.md](deconvolver-training.md) (the PSF family the injector uses),
[gradient-remover-training.md](gradient-remover-training.md) (the flatten step),
[model-training-roadmap.md](model-training-roadmap.md).

## 0. Why synthetic truth is the only honest truth here

Ground truth for EXISTING stars would need hand-editing (the reference remover's author is on record
that hand-editing was the only way). Ground truth for INJECTED stars is exact by construction: input =
plate + injected stars, target = plate. The plate does not need to be perfectly starless; residual
removal artefacts become background the net must preserve, never content it must invent. What that
requires is that the injected stars' positions be UNCORRELATED with the residual sites, or the net
learns "removing a star reveals an artefact" (Croman's "the network will faithfully learn all of your
mistakes").

Two archive properties make this narrower than a general remover's problem: almost every optic in it is
refractive (Samyang 135, ZS61, FMA180, SH61, SV545, RedCat 51), so almost no spider vanes or diffraction
spikes, and the PSF family is measured per (train, filter, channel) already. **Almost, not every** (found in
the 2026-10-02 review): the QHY294C's SWQ8 at 600 mm is a Newtonian, and every one of the eight brightest
stars in its 2026-02-20 Centaurus A master carries a four-point spike. Three masters in the store are on it
(Centaurus A, omega Centauri, the Running Chicken, all 2026-02-20). Spikes are fixed to the optic: their
orientation follows the sensor's rotation against the vanes and their length grows with the star's flux.
A user's Newtonian is common, so what v1 does with spikes is section 8's first question.

## 1. What exists, with pointers

| Piece | State | Where |
|---|---|---|
| The role and contract | shipped | `IStarRemover : IImageEnhancer`; `RemoveStarsStep`'s additive split `stars = input - starless`; `SharpenPipeline` retention via `SharpenIntermediates.StarsAndStarlessLineage` |
| Today's implementation | shipped | `RcAstroStarRemover` (the `sxt` CLI, licensed) behind the deferred `sxt` dispatcher (`DeferredEnhancer`, "RC-Astro only until TianWen's own"), and nothing else: `OnnxStarRemover` and the SAS `darkstar_*_AI4` weights went with that tier on 2026-09-26. The split has two modes, `RecombineMode.Additive` (the default, `stars = input - starless`) and `Screen` (`Unscreen`) |
| Star detection + measurement | shipped | `FindStarsAsync`, `ImagedStar` (HFD, FWHM via `HalfMaxDiameter`, eccentricity), `PsfProfileFit`, and the deblender: "the star mask splits into a footprint and a claim, so a close companion is measured", "two tight stars are two stars, by fitting the aperture instead of sizing it", then `BackgroundMap` / `SourceSegmentation` (the Background2D and detect_sources shapes) and the crowded-field rule (a large segment with many maxima is a field, not a source) |
| PSF distribution | measured, current | The re-baked store's `stats/psf-sessions.jsonl` (`D:/Astro-Dataset/2026-09-29-full`, recipe 3): `MasterProfiles` per channel (Moffat FWHM and beta, the wing at 2 and 3 FWHM, the Moffat and Gaussian fits' log RMS; three profiles on 338 records, one on the 42 mono ones), `BinsByChannel` (ellipticity by radius), the train on every record. Measured ON the master, so the warp and demosaic (or drizzle) widening is already in it, which is what an injector into a master needs. Saturation fraction 0.1 to 0.2 percent of detections |
| Degradation exporter | shipped, two of three modes | `DatasetDegradationExporter` (`tianwen dataset degrade`): `DegradationMode.Noise` (denoiser) and `Blur` (deconvolver), the noise anchored per channel on each master's recorded calibration and shaped by its integration (`NoiseShape.Warped`, E16b). Star injection is the third mode, R1 |
| Classical starless plate | NOT built | PSF-fit subtraction at detections plus multi-scale inpaint. Masking exists (`StarMask`, `ScanBackgroundRegion`, the segmentation's footprints), and so does ONE inpainter, low-pass only: the classical background extractor's masked inpainted surface (`RobustBackgroundFit`, `SurfaceRefinement`). It fills a hole on smooth sky; a hole on nebula structure needs the multi-scale fill R0 builds |
| Bright-tail morphology | measured elsewhere | the "bright end scrambled by saturation" note on the Vela field; flat-topped cores give unstable centroids |

## 2. Hypotheses

**H1. A classical PSF-subtract-and-inpaint plate is good enough as a bootstrap target.** Its
imperfections are tolerable if they are CONSISTENT (the net learns to leave them) and if injected
stars land independently of them.
*Test:* build the plate for ten held-out masters; measure the residual at subtracted-star sites (in
background MAD) and the fraction of the frame the inpaint touched.
*Prediction:* residuals under 2 MAD at faint sites, visible rings at the bright saturated tail (which
is why the bright tail is the known hard case and RC stays preferred there), inpaint area under 5
percent of the frame. The ten masters span what the store holds: AHD and drizzled, OSC and mono,
broadband and line filters, a crowded field (omega Centauri), nebula-rich fields, and the SWQ8's
Centaurus A for its spikes, which no PSF fit takes and the report states rather than hides.
*Kill:* residuals dominate the plate. Then a plain masked-inpaint (no PSF fit) is the plate, and the
first-generation net's job shrinks to faint and medium stars only.

**H2. Injecting from the measured PSF distribution, per channel and per train, is what makes the
truth transferable.** Stars drawn from a single Gaussian would teach a Gaussian remover; the archive
carries Moffat wings 98x a Gaussian's at 2 FWHM.
*Arms:* Gaussian injection against Moffat injection from the (train, filter, channel) distribution
with elongation from the radius bins and a saturation/bloom model for the bright tail.
*Prediction:* on REAL existing stars (spot-checked at 1:1 on held-out masters, the only measurement
that is not synthetic), the Moffat arm leaves smaller halos.
*Kill:* no visible difference. Then a simpler injector ships.

**H3. Position independence is load-bearing and measurable.** Inject at uniformly random positions
(with a minimum distance from any subtracted site) and compare against injection AT the subtracted
sites.
*Prediction:* the at-site arm scores better on injected-star completeness and worse on real-star
spot checks (it has learned the artefact). This is the control that proves the bootstrap is not
learning its own mistakes; run it once, record it, and never inject at sites again.

**H4. Self-refinement converges rather than drifting.** Run the trained net on real masters, use its
output as better plates, re-inject, retrain. Each generation distils only our own model.
*Test:* three generations; measure injected-star completeness, background preservation under injected
stars, and the real-star spot checks per generation.
*Prediction:* completeness rises and then plateaus by generation 2; background preservation does not
degrade.
*Kill:* preservation degrades (the net starts inventing background where it removed a star). Then the
loop stops at generation 1 and the plate quality is the limit.

**H5. The additive split holds photometrically.** `stars = input - starless` must conserve star flux:
the stars plate's aperture sums equal the input's minus the local background.
*Gate:* signed flux bias under 0.5 percent per SNR band above 20 (programme section 7), measured with
`PhotometricRepeatability.Compare` on the stars plate against the input.

**H6. The stretched domain is right for this role.** `OnnxStarRemover` runs through
`ChunkedNafnetRunner` (MTF to 0.25). A star remover benefits from the stretch (faint stars are
lifted into the range the net sees), and injecting stars in LINEAR then stretching keeps the physics
honest.
*Design, not an arm:* inject in linear on the linear plate, stretch input and target with the TARGET
frame's MTF parameters (the deconvolver plan's rule), export through the P0 path.

## 3. Data and the exporter

Third mode of the shared degradation exporter (denoiser E1, deconvolver E2): source the retained
linear masters; build the classical plate per master (H1); inject N stars per 256 px cell (N drawn
from the master's own detection density so the synthetic field is as crowded as the real one), each
with a Moffat core from the per-channel distribution, elongation and PA from the radius bin the cell
sits in, a flux drawn from the master's own detected-flux distribution extended into the saturated
tail with a clipped flat top plus a bloom model; place at uniform random positions at least 3 FWHM
from any subtracted site; add the injected stars' own shot noise; `ToUnitRange`, `ApplyInputStretch` with
the target's parameters; cut cells. Record every injected star (position, flux, FWHM, beta, saturated
flag) in the manifest row, because eval is against exactly that list.

Four rules the denoiser and gradient programmes paid for since this was written (2026-10-02 review):

- **An injected star's noise must have its master's SHAPE**, through the exporter's existing noise path
  (the per-channel anchor on the master's recorded calibration, `NoiseShape.Warped` for its integration).
  A master's noise is correlated (band 1 over band 0 of a half pair 0.45 on a demosaiced master, 0.31 to
  0.33 on a drizzled one, against white noise's 0.22; `tianwen dataset degrade --measure-shape`), so a star
  injected with white noise is recognisable by its texture alone, and a net that learns "a star with white noise is the one to
  remove" leaves every real star in place.
- **Draw the profile from `MasterProfiles`, never the subs', and integrate it over the pixel.** The master's
  profile already carries the warp kernel and the demosaic or drizzle; a sub-2 px profile sampled at pixel
  centres does not blur by its label.
- **Edges are masked, never cropped** (the gradient plan's "Edges"): no star lands on an absent pixel of the
  canvas ring, and a cell that straddles it carries the presence the stretch already respects (the NAFNet
  pre-stretch measures covered pixels only).
- **The bright tail is sampled on purpose**: the denoiser cleaned a bright level only once bright cells were
  in its pool and in its eval (E16b), and the saturated stars this remover is weakest on are 0.1 to 0.2
  percent of detections, so a cell draw proportional to the field never shows the net enough of them.

Held-out split by session, as everywhere, and one whole TRAIN held out besides: the store's PSFs and star
populations are train-specific (96 of the 172 flat-fielded masters are the ASI533 at 130 mm, most likely one
Samyang 135), users bring optics the archive never saw, and only a held-out train says whether the remover
learned stars or this archive's stars.

## 4. Model and recipe

The same 0.81 M residual U-Net first, predicting the STARLESS plate (the residual formulation makes
an untrained net the identity, which for a remover means "removes nothing", the safe direction). L2
on the tile minus the 16 px rim; a DoG band loss on the 2-8 px bands where star cores live; a flux
penalty on the implied stars plate (H5). Stretched domain through `ChunkedNafnetRunner` at
inference, so the tile-256 / stride-16 / overlap-64 constraints hold by construction. NAFNet-class
capacity only if the U-Net plateaus on completeness while the plate quality is not the limit.

**Start from the recipe that shipped, not the one this was written against** (2026-10-02 review): the
denoiser's `convmapb_s2` (D6 in [denoiser-training.md](denoiser-training.md)) is the same U-Net family
trained to convergence with seeds interleaved, the bright cells in pool and eval, and a conditioning plane
the runner computes as the eval did (`N2nLinearRunner`, `OnnxIoNames.IsImagePlusPlane`). Its export,
parity fixture and runner path are the template R5 follows.

**One alternative structure, held for H4's kill:** the third-party architectures read in the SAS study
([model-training-roadmap.md](model-training-roadmap.md) section 8, item 7) separate a positive subtractive
star layer from a gated inpaint branch that takes the hole mask as an input. That is R0's own structure
(subtract, then fill) learned end to end, and the hole mask is something we have exactly (the injected
footprints in training, the detections at inference). It is an arm only if the plain U-Net starts inventing
background where it removed a star.

## 5. Metrics and gates

- **Injected-star removal completeness** by flux bin (residual at the injected position under 1 MAD),
  including the saturated tail reported separately.
- **Background preservation under injected stars:** target vs output inside the injected footprints,
  RMS in MAD units, gate at 1.
- **Stars-plate flux conservation** (H5).
- **Real-star spot checks at 1:1** on held-out masters: bright saturated stars, close pairs (the
  deblender's domain), stars on nebulosity, and spikes on the SWQ8 masters. Human adjudication with a
  labelled comparison image; RC outputs never in the frame as a reference. The gradient plan's golden-set
  page is the shape for it (a private review page, each master's panels side by side, the owner's verdict
  stored with the page and read back): the owner's verdicts become the set every later generation is
  re-checked against, which is what H4's "preservation does not degrade" needs to be measurable.
- **Nebulosity at 4-16 px** held at parity (the remover must not eat knots).
- **No RC output** anywhere in the loop.

## 6. Experiments, in order

| Step | What | Cost | Decides |
|---|---|---|---|
| R0 | Classical starless plate builder (PSF-fit subtract at detections + multi-scale inpaint); residual report on ten masters | 2 to 3 days | H1 |
| R1 | Injector (third exporter mode) with the manifest of injected stars; the at-site control arm data | 1 to 2 days | H3 data |
| R2 | Gaussian vs Moffat injection, three seeds each; random vs at-site control; posted 1:1 comparison | 4 x 3 x ~11 min | H2, H3 |
| R3 | Self-refinement, two further generations | 2 x 3 x ~11 min plus plate rebuilds | H4 |
| R4 | Photometric gate on the stars plate | hours | H5 |
| R5 | Export, contract JSON, `OnnxTianWenStarRemover : IStarRemover` through `ChunkedNafnetRunner`, registered in `AddTianWenAi()` and answering `IEnhancerAvailability.Serves`, so `CanonicalProgram` can take the split without RC. Opt-in first (`--ai-backend n2n`, `EnhanceBackend.N2n`); once the bright tail passes the spot checks, Auto still prefers RC and the deferred `sxt` dispatcher falls back to the in-house remover where RC is not licensed | 2 days | Ships opt-in |

### R0: the classical plate builder

Designed 2026-10-02, before any code. **One routine, `ClassicalStarRemover.Build`**
(`TianWen.Lib.Imaging.StarRemoval`), returns a `StarlessPlate`: the plate, a mask of what it touched
(subtracted, inpainted), the fitted stars and the statistics below. It makes the training plates, and if its
report earns it, it is also the product's AI-free `IStarRemover`, as `ClassicalBackgroundExtractor` is for
gradients; that is the owner's call once the report is in.

1. **The PSF per channel is measured on the image itself** (`PsfProfileFit` on its own stars): FWHM and
   Moffat beta per channel. The PSF store holds the same measurement; the builder never needs the store, so
   it runs on any image.
2. **Finding: a matched-filter peak finder of its own** (`PointSourceFinder`), because neither detector
   does this job. `FindStarsAsync` selects stars to MEASURE: it returns nothing over a background at or below
   zero, skips 15 px at every edge and refuses an HFD over 28, which is every big saturated star.
   `SourceSegmentation` segments SOURCES: 64 peaks to a segment and a crowded field re-thresholded higher
   lose the faint stars on a nebula. The finder works on the luminance, with a local sky from `BackgroundMap`
   at a block of about 8 FWHM (at least 16 px) so it follows nebulosity, a Gaussian matched filter at the
   PSF's width, and local maxima at 5 sigma of the filtered plane's own robust noise, merged within one
   FWHM. Edges included: a fit uses whatever pixels are present.
3. **Fitting, brightest first on a working copy.** A Moffat with the channel's beta, integrated over the
   pixel (supersampled in the core, since a 1.5 px star does not blur by its label). On the luminance the
   centre and a width scale are fitted by Levenberg-Marquardt with the amplitude, sky level and sky plane
   solved linearly inside it; each channel then takes its own amplitude and sky linearly at its own width.
   Below 20 sigma a candidate keeps its found centre and the field's width and is fitted linearly only.
   **The fit is the star test**: a candidate that wants a width over 1.6 times the field's, or whose model
   explains under half its peak, is a knot, not a star, and is left in the plate. A flat core (the top
   pixels within 2 percent of the peak) is a saturated star and is fitted on its wings. One refinement pass
   re-fits every star with its neighbours subtracted, then the finder runs again on the residual for stars
   a brighter one hid.
4. **What is inpainted**: per star, the residual pixels beyond 3 sigma where its model exceeds 1 sigma,
   grown by a pixel; for a saturated star, the core where its model exceeds the plateau, grown by two.
5. **The fill**: a push-pull pyramid per channel (normalised averaging down, interpolation back up) over
   the mask, absent pixels weightless and never filled, then noise at the local rms (`BackgroundMap`) with
   the plate's measured lag-1 correlation (a three-tap kernel solved for it), so a filled hole has the
   plate's grain.

**The report** (`tianwen dataset starless-plates`, a JSONL store and a markdown table, the shape of
`gradient-report`), per master: stars found, subtracted, left as knots, inpainted; and four measures.

- **Residual at a subtracted site**: the RMS of plate minus local sky over the pixels within one FWHM of a
  subtracted, not inpainted star, in local sigma (pure noise reads 1), median per SNR band (5-10, 10-20,
  20-50, 50-100, 100 and up). Its **signed bias**, the mean over the same pixels in sigma over root n.
- **Inpaint fraction**: inpainted over present pixels.
- **Leftover point sources**: the finder at 4 sigma on the finished plate, as a fraction of the first
  pass's detections, per band. **This is the measure that decides whether a plate can be a target at all.**
  A star the plate keeps is a lesson in keeping stars: an injected star and a leftover one look the same to
  the net, so wherever leftovers rival the injected stars in number the net learns to leave stars. R1 sets
  its injection density per band from this.
- **Residual by field radius** (inner third against the corners): the price of an isotropic PSF where the
  optics elongate stars, which decides whether elongation must be fitted.

The leftover measure uses the tool that did the finding, so it cannot see what the finder cannot; the
synthetic tests are its external truth. They build a plate (a smooth nebula, noise of a master's shape),
inject Moffats of known parameters (saturated ones, close pairs, a Gaussian knot three times the PSF's
width, stars on the absent ring's edge) and assert completeness, the residual bound, the knot kept, the ring
untouched, and the same output twice.

**The ten masters**, all from the store's held-out test split (`D:/Astro-Dataset/2026-09-29-full`): the
QHY294C on the SWQ8 (Centaurus A, drizzled, spikes); the ASI1600MM on the FMA180 (eta Carinae, luminance,
mono); the ASI294MM at 250 mm (Leo Triplet, luminance, mono); the SV605CC on the SH61 (the Horsehead through
the L-Quad, demosaiced; the SMC through the L-Ultimate, drizzled and crowded); the ASI533 at 130 mm (Orion
through the UV/IR cut and Antares through the LPS-D3, both drizzled with very bright stars; the Rim Nebula
in SII, demosaiced); the ASI585 at 24 mm (a Milky Way field at 24 arcsec a pixel, demosaiced, undersampled);
the Uranus-C on the FMA135 (M8 and M20, drizzled, no flat).

**Predicted before the run**, over those ten, beside H1's own:

- Residual at faint sites (SNR 5-20): median under 2 sigma on every master (H1). Above SNR 100: over 3 sigma
  wherever a star was subtracted but not inpainted.
- Signed bias: within 0.5 sigma over root n in every band below 100.
- Inpaint fraction: under 5 percent on nine of ten; the ASI585 at 24 mm (Milky Way, 24 arcsec a pixel,
  undersampled) is the one that may not be.
- Leftovers: under 5 percent at SNR 10 and above on every master but the crowded fields (the SMC, the
  24 mm Milky Way).
- Field radius: the corners' faint residual at least 30 percent above the centre's on the camera lenses
  (the ASI533's 135 mm, the ASI585's 24 mm), within 10 percent on the refractors (FMA180, SH61, FMA135).
- The SWQ8's spikes are left whole outside the inpainted cores; the report states it and does not count
  them as leftovers.

## 7. Phasing

Tracked by #902.

| Phase | Deliverable | Exit |
|---|---|---|
| P4.0 | Classical plate builder with its residual report | R0 |
| P4.1 | Injector + control data | R1 |
| P4.2 | H2/H3 answered with posted comparisons | R2 |
| P4.3 | Self-refinement measured | R3, R4 |
| P4.4 | v1 opt-in | R5 |

## 8. Open questions

- **Diffraction spikes (the owner's decision, open).** Three masters (the SWQ8 Newtonian) carry them and
  a PSF fit takes none of them. Either v1 leaves them: the SWQ8 masters stay out of the plates and the
  training, one stays in the spot checks so what v1 does to a spike is seen, and the limit is documented;
  or the injector models them (orientation measured off those masters against the sensor, length grown
  with flux) and R0 subtracts them. The first is the recommendation: three masters cannot teach a spike
  model that generalises to other vanes, and a spike left whole is honest where a half-removed one is not.
- **The bright saturated tail** is the acknowledged hard case; whether v1 should refuse it (leave stars
  above a flux threshold in place, documented) or attempt it is a product decision for R2's spot
  checks.
- **Comet-registered stacking shipped** (`tianwen stack --comet`; `--remove-stars` builds the comet layer
  from per-frame star-removed plates), on RC alone. So this remover is what gives a user without
  StarXTerminator a comet layer at all, and per-frame use is why the additive split has to be photometric,
  not only cosmetic (H5): every sub's stars plate is recombined on a star-registered grid.
- **Met since 2026-09-02, kept for the record:** the dependency on P2 (P2.0 done 2026-09-06; R1 draws from
  the re-baked store's per-channel profiles) and on the flattener (shipped 2026-09-02).

## 9. Review, 2026-10-02

The plan read against the code and the store when the owner moved it up the order. What changed:

- **Order**: no longer last; the three things it waited on are in place (status line).
- **Wrong**: "every optic is refractive, no spikes" (the SWQ8 is a Newtonian, section 0); "no inpainter
  exists" (a low-pass one does, section 1); `OnnxStarRemover` and the SAS fallback (gone 2026-09-26). The
  deblender was cited by commit hash, which the docs never do; it is cited by subject now.
- **Added from the denoiser and gradient programmes**: the injected noise in its master's shape, the
  profile from the master's own measurement integrated over the pixel, edges masked, the bright tail
  sampled on purpose, a held-out train (section 3); the shipped denoiser as the recipe and R5's template,
  the two-branch structure held for H4's kill (section 4); the golden-set page for the spot checks
  (section 5); R5's wiring through `IEnhancerAvailability` and the `sxt` dispatcher (section 6).
- **Unchanged**: the inject-and-remove bootstrap, H1 to H6, the phasing. R0 is next, CPU only.
