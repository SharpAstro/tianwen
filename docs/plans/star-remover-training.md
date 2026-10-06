# Star-remover training (P4): the inject-and-remove bootstrap

**Status: R0 BUILT (2026-10-03), the first of the model plans in priority (the owner, 2026-10-02: #902 is
`priority:high`, above the gradient plan's de-vignetting model). The classical plate builder and its report
run on the ten held-out masters with holes below their null on every one ([R0 results](#r0-results-2026-10-03));
R1, the injector, is next.** Design captured in [ai-denoise-deconv.md](ai-denoise-deconv.md) section 2.5, restated here at run
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

**The goal (the owner, 2026-10-02): stellar extraction into two plates, a stars-only plate and a non-stellar
plate, with no star holes.** The two are the additive split the pipeline already uses (`RemoveStarsStep`,
stars = input minus starless), so they add back to the input exactly; what decides quality is the starless
plate, and its one forbidden artefact is a hole: a star's core left darker than the sky around it, by
over-subtraction or by a fill that misses its surroundings. Every stage measures it (R0's "Holes", the fill
probe, the net's spot checks).

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
| Classical starless plate | built (R0) | `ClassicalStarRemover` (`TianWen.Lib.Imaging.StarRemoval`) with its finder (`PointSourceFinder`), fill (`HoleFill` over `PushPullFill`) and fill probe (`StarlessFillProbe`); `tianwen dataset starless-plates` writes the starless and stars-only plates and the report. Not yet an `IStarRemover` (the owner's call, section 6) |
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
- **Draw the profile the master's own amplitudes were fitted with, measured on the master, never the subs', and
  integrate it over the pixel.** The master's profile already carries the warp kernel and the demosaic or drizzle;
  a sub-2 px profile sampled at pixel centres does not blur by its label. That profile is R0's field profile (its
  Moffat and residual table, `StarlessFieldProfile` beside each plate), not the PSF store's single Moffat
  (`MasterProfiles`): an amplitude means something only with the profile it was fitted with, and the store's Moffat
  drew saturated cores 1.2 to 1.6 times too bright and left out the wing light real stars carry ("The soft top,
  resolved").
- **Edges are masked, never cropped** (the gradient plan's "Edges"): no star lands on an absent pixel of the
  canvas ring, and a cell that straddles it carries the presence the stretch already respects (the NAFNet
  pre-stretch measures covered pixels only).
- **A saturated star is injected as a STACK saturates, measured off the masters' own saturated stars**, never as a
  hard clip of plate plus star. A stack of clipped, dithered, normalised subs gives a plateau with soft edges, often
  one channel clipping before the others, sometimes a bloom: its level per channel, its radius against the
  amplitude the wings extrapolate to (`ClassicalStarRemover` reads both for every saturated star it fits) and its
  edge profile are what the injector reproduces. A net taught on hard clips learns to fill holes it will never
  see, and leaves the real ones' rims behind.
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

### The speckle teacher

**A learned remover is penalised for dark speckles, not only scored on them** (the owner, 2026-10-03, after R0's
plates showed them). A speckle is a core pixel left more than 4 sigma below the plate's own sky where a star was: a
star subtracted a fraction of a pixel off leaves a dark pixel beside a bright one, and a mean over the core, which is
what the hole test reads, stays at the sky. R0's plates had them at 3 to 13 percent of a band's stars against a sky
null under 1.5 percent ("What the ten masters taught the builder", the speckle entry). The loss gains a one-sided term
at every removed star's site: the shortfall of each output pixel within 3 px of the site below the local sky (the
median of the OUTPUT from 6 to 12 px, sigma its MAD) less 4 sigma, squared and summed, zero where nothing falls short.
It needs no truth, so it applies at real stars too: at a held-out master's detections for the eval, and on real
masters in self-refinement (H4), where an injected star's target is not available. **One definition**:
`StarlessSpeckles` (C#) is the measure and the gate; the trainer's term is a port of it pinned by a parity fixture (a
plane and its sites, the C# rate and the per-site flags written out, the torch term asserted to flag the same sites),
as the denoiser's runner path was, so the teacher and the gate cannot drift.

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
- **Speckles** (`StarlessSpeckles`): the share of removed stars with a core pixel 4 sigma under the output's own sky,
  per significance band, at injected sites and at a held-out master's detections, against the same test at the
  plate's star-free sky. Gate: no band above twice its null, read per background (`FittedStar.Texture`): a bright
  nebula's texture alone fires the test at about 10 percent of star-free places (the M42 core), the frame's dark sky
  under 1.5, so a rate pooled over both judges a remover by how much nebula the field holds.
- **Real-star spot checks at 1:1** on held-out masters: bright saturated stars, close pairs (the
  deblender's domain), stars on nebulosity, and spikes on the SWQ8 masters. Human adjudication with a
  labelled comparison image; RC outputs never in the frame as a reference. The gradient plan's golden-set
  page is the shape for it (a private review page, each master's panels side by side, the owner's verdict
  stored with the page and read back): the owner's verdicts become the set every later generation is
  re-checked against, which is what H4's "preservation does not degrade" needs to be measurable.
- **Nebulosity at 4-16 px** held at parity (the remover must not eat knots).
- **No RC output** anywhere in the loop.

### What the eval could not see, and what it reads since (2026-10-06)

A review of R2's scoring against what the N2N denoiser took to become usable (the owner asked for both on 2026-10-06)
found `StarRemovalEval`'s arithmetic right and four things it could not see. The measures R2a and R2b were registered
on are kept to the bit: re-scored, every registered number comes back unchanged (`C:\temp\e2\eval_v2_parity.py` over
each `starless-eval-v2.json`, written beside the registered report with `--report-name`). The new ones sit beside them:
- **Removal was one-sided.** A star counts as removed when its 3x3 core is left under a sigma above the plate, so a
  core dug far below the sky passes. Each band now also reads **Clean** (the core's mean within a sigma of the plate
  either way, and no pixel within the speckle core 4 sigma off it, both judged against the truth) and **Dug** (the
  core's mean under minus a sigma, or a pixel 4 sigma under the plate), with the core's signed mean and RMS.
- **The sky's change was one unsigned number on the luminance.** A level offset, a colour cast and a smoothed noise
  read alike. It is now taken apart: each channel's signed mean, the draws' level shifts against the texture changed
  about them (the two add in quadrature to the old number), and the RMS of neighbour differences against the plate's,
  which reads under 1 where the sky's noise was smoothed.
- **The footprint residue was pooled.** The draws' percentiles and the share of the pooled squared residue in the worst
  one percent of draws now say whether a large number is a few tiles or all of them.
- **Arms were compared at whatever strength each trained to.** Every arm is now also read per session, and the output
  is blended back toward the input at 0.75, 0.5 and 0.25. The blend turned out to be no matched-strength dial for
  completeness: a quarter of every star stays, so at 0.75 the field model's 5-20 sigma removal fell from 54 to 1
  percent. It is one for the sky alone.

**What it showed, over R2a's and R2b's field and Gaussian models** (11 of 12 so far, `r2b_gaussian_s2` still training;
on the random arm's draws, arm means with the seed range; `C:\temp\e2\eval_v2_table.py`, `eval_v2_core.py`):

| at 100-1000 sigma | removed (registered) | clean | dug | core residual, signed mean / RMS |
|---|---|---|---|---|
| R2b field | 64.3 (61.1-67.8) | 16.0 (13.3-18.6) | 51.3 (50.0-52.4) | +0.7 / 5.9 |
| R2b Gaussian | 9.6 (8.4-10.9) | 6.3 (5.4-7.2) | 1.8 (1.7-1.9) | +14.7 / 44.5 |
| R2a field | 72.6 (58.5-85.4) | 9.0 (5.1-13.4) | 64.5 (50.9-80.3) | +0.1 / 4.1 |
| R2a Gaussian | 25.0 (17.3-38.8) | 15.2 (11.6-22.2) | 5.1 (3.3-7.8) | +13.6 / 49.9 |

- **At the bright end the registered "removed" reads the SIGN of the error, not the removal.** The field arm's core
  residual is unbiased (+0.1 to +0.7 sigma) with an RMS of 4 to 6 sigma, so about half its bright stars fall under the
  plate and the one-sided test counts them as removed. The Gaussian arm leaves its bright stars, a mean of +14 sigma.
  H2 holds on the residual's size (an RMS 7 to 12 times the field arm's) and on its sign, not on the registered rate,
  which overstated the gap: clean removal is 16.0 against 6.3 in R2b and 9.0 against 15.2, reversed, in R2a.
- **"Dug" at the bright end is that same unbiased scatter's negative half, not a systematic hole.** A pixel 4 sigma
  under the plate is still a visible dark spot, and the speckle rate (18 to 21 percent of bright sites) agrees.
- **At 5-20 sigma the arms tie on clean removal** (R2b field 53.5, 48.7-57.1; Gaussian 55.2, 50.5-59.9).
- **The pooled footprint RMS is a few tiles in every arm.** The median draw reads 0.73 to 1.07 sigma, under or at the
  gate of 1, and the worst one percent of draws hold 86 to 99.6 percent of the pooled squared residue. `r2b_random_s1`'s
  16.1 is 13 tiles (its median draw 0.79). Read the footprint by its median and 90th percentile from now on: R2b's
  field arm 0.75 and 1.37, the Gaussian arm 1.07 and 7.6.
- **The sky's movement is texture, not smoothing and not level.** The field arm's 0.54 is 0.525 about each tile's level
  and 0.107 of level, with a noise ratio of 0.97; the Gaussian arm's 0.185 is the same shape. What texture it changes is
  open. One candidate is the faint stars R0 left in the plates, which a remover taught every star would take as stars;
  if so, the kill's sky bar partly penalises removing real stars the truth kept.
- **The clean bar may be strict at the bright end:** the noise plane is low-passed, so at a bright core it can read
  under the core's own shot noise. An ideal-remover reference (the draw less the injected star's noise-free render,
  which the export does not write today) would calibrate it.

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
- **Holes** (the goal's one forbidden artefact, added 2026-10-02): a subtracted star whose core in the plate, filled
  or not and over a saturated star's whole plateau, sits more than 3 sigma over root n below its own fitted sky.
  Noise alone puts about 0.1 percent of cores there (the synthetic field: 3 of 826). The verb writes the stars-only
  plate beside the starless one (input minus plate), so the pair is the deliverable, not only the plate.
- **Leftover point sources**: the finder at 4 sigma on the finished plate, as a fraction of the first
  pass's detections, per band. **This is the measure that decides whether a plate can be a target at all.**
  A star the plate keeps is a lesson in keeping stars: an injected star and a leftover one look the same to
  the net, so wherever leftovers rival the injected stars in number the net learns to leave stars. R1 sets
  its injection density per band from this.
- **Residual by field radius** (inner third against the corners): the price of an isotropic PSF where the
  optics elongate stars, which decides whether elongation must be fitted.
- **Fill error on known pixels** (the owner, 2026-10-02: what goes into a saturated core is the real problem).
  Holes of the sizes the master's saturated cores were given are cut at random star-free places in the same
  master, nebulae included, filled, and compared with the pixels that were there: the error in local sigma by
  hole size and by the structure under it. Exact truth on real data, and the measure a better fill (patch-based,
  or one that carries a filament's edges across) has to win on before it replaces the push-pull. On the
  synthetic field's smooth sky the push-pull already reads 1.4 to 1.6 sigma in the core, which is two
  independent noise fields' 1.41: on a smooth sky the fill itself adds almost nothing, and what is not known is
  how it does on structure.

**Where the hole question lands.** For the plate, a real saturated core is background in both the net's
input and its target, so the net learns to keep it, never to make it; what the NET learns to put in a hole
comes from the injected saturated stars, whose cores land on the plate's own pixels, so the truth under them is
exact (section 3 says how they must saturate). The classical fill matters in two places only: where the
builder becomes the product's AI-free remover, where the fill is what a user sees, and where a plate's filled
cores are so many that they shape what the net calls background.

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

#### R0 results, 2026-10-03

`tianwen dataset starless-plates` over the ten masters, every plate pair also inspected by eye at its brightest
star, at its worst negative patch of the stars plate, at its worst dip of the starless one and at a fixed field
spot. Holes are cores more than 3 sigma over root n below the plate's own annulus sky, as a percentage of each
band, beside the same test at random star-free places of the plate (the null, which correlated noise and crowding
raise); residual is in local sigma (noise reads 1).

| Master | found | inpaint | holes 5-20 / 20-100 / 100+ (null) | residual 5-10 / 10-20 / 100+ | bias 5-20 | leftover 10-20 / 20+ |
|---|---|---|---|---|---|---|
| Centaurus A (QHY294C, SWQ8) | 44,163 | 6.1 % | 0.30 / 0.29 / 1.07 (6.10) | 0.81 / 0.93 / 2.01 | 0.09 | 0.01 / 0.01 |
| eta Carinae (ASI1600MM, L) | 160,162 | 37.7 % | 0.79 / 1.43 / 2.54 (11.25) | 1.59 / 2.00 / - | 0.21 | 0.02 / 0.02 |
| Leo Triplet (ASI294MM, L) | 16,027 | 1.2 % | 0.25 / 0.00 / 0.32 (0.35) | 0.77 / 0.90 / 1.99 | 0.10 | 0.03 / 0.10 |
| Horsehead (SV605CC, L-Quad) | 14,170 | 2.8 % | 0.74 / 1.12 / 1.92 (15.00) | 0.76 / 0.92 / - | 0.37 | 0.02 / 0.00 |
| SMC (SV605CC, L-Ultimate) | 25,311 | 1.5 % | 0.17 / 0.40 / 2.36 (7.00) | 0.80 / 0.89 / 1.55 | 0.35 | 0.04 / 0.03 |
| Orion (ASI533, UV/IR) | 34,156 | 5.9 % | 0.04 / 0.18 / 0.31 (4.35) | 0.88 / 1.14 / 2.39 | 0.24 | 0.01 / 0.00 |
| Antares (ASI533, LPS-D3) | 60,294 | 6.6 % | 0.03 / 0.47 / 0.85 (4.85) | 0.85 / 1.07 / 2.11 | 0.47 | 0.01 / 0.01 |
| Rim Nebula (ASI533, SII) | 58,702 | 6.3 % | 0.10 / 0.91 / 2.93 (12.90) | 0.97 / 1.42 / - | 0.88 | 0.05 / 0.03 |
| Carina, 24 mm (ASI585) | 56,518 | 14.0 % | 0.87 / 2.81 / 8.62 (25.80) | 1.08 / 1.57 / - | 1.53 | 0.01 / 0.01 |
| M8 + M20 (Uranus-C, FMA135) | 23,677 | 12.8 % | 0.39 / 1.84 / 3.70 (11.10) | 0.92 / 1.21 / - | 1.05 | 0.01 / 0.01 |

Against what was predicted before the run:

- **Holes: below the null on every master and every band**, which is the goal's one forbidden artefact. The
  stars-only plate (input minus starless) holds no negative patch summing past 220 sigma on any master but
  Antares, whose 517 is M4's unresolved core.
- **Residual at faint sites under 2 sigma: held** (eta Carinae's 10-20 band is the edge, at 2.00). **Above SNR
  100 it was predicted over 3 sigma and is 1.5 to 2.4**: the bright stars fit better than expected.
- **Bias within 0.5 sigma over root n below SNR 100: held on seven, missed on three**, all positive (light left,
  never taken): the Rim Nebula 0.88, M8 + M20 1.05, the 24 mm Carina field 1.53, the three crowded or nebulous
  fields.
- **Inpaint under 5 percent on nine of ten: wrong, three of ten.** The fill is the price of the hole rule: a core
  whose misfit is not noise is filled rather than left, and crowded fields have many. eta Carinae's 37.7 percent
  is a third of a confusion-limited Milky Way field; R1 has to weigh what a third of a target made by the fill
  teaches before that master enters the plates.
- **Leftovers under 5 percent at SNR 10 and up: held** everywhere but the Leo Triplet's 20+ band (0.10, the
  galaxies' cores and HII regions the fit calls knots, as it should).
- **Field radius: half right.** The refractors' corners sit within 10 percent of their centres (FMA180 7, SH61 6
  to 8, FMA135 within noise). The camera lenses were predicted 30 percent and more and read 12 to 25 (the 135 mm)
  and 1 (the 24 mm, whose stars are wide everywhere).
- **Spikes: left whole**, as decided; a giant's subtraction never digs between them (below).

#### What the ten masters taught the builder

Each was found on one master by eye and measured before it was fixed; none is specific to that master.

- **A saturated star needs a halo test, and the test reads the ring past its fit window**, never the background
  map at the star, which a bright star raises under itself. A wing fit whose sky is far from that ring holds light
  the field's Moffat cannot (the Horsehead's 4,933-sigma star: a sky 1,100 below, 2e6 for a 65,000 plateau, a blob
  and four over-subtracted lobes); it takes its own symmetric profile instead.
- **Saturation is the full scale, not only a flat top**: a clipped star stacked over sub-pixel shifts has a round
  top (9 Sgr, 0.98 of 0.99, fitted unsaturated at 1.61 times the field's width and left as a knot).
- **A giant's profile is the star's, not its surroundings'.** It is monotone, it ends where it flattens and is
  measured against that level, never a sky 100 px out; it falls no slower than the inverse square of its radius
  (the aureole's far wing); and it never takes a pixel more than 2 sigma below its level. Without the first three,
  nebula round eta Carinae (at 600 and at 24 mm) and 9 Sgr went into the stars plate and left dark discs; without
  the last, the Antares-field giant's 18 spikes had notches 4 to 20 sigma deep between them. Smoothing a halo
  before the minimum is wrong: a mean over a convex halo sits above it and dug a 0.7 sigma disc.
- **A giant's core is filled to its plateau's farthest pixel**, and its residual is flagged only where it dominates,
  against the plate around it (flagged to its full reach against the far sky, M42's nebula was filled as a disc
  110 px across).
- **A wide source is tried as a blend before it is called a knot**: two field-width stars along its long axis (the
  brightest Centaurus A "knots", 672 and 402 sigma, are doubles 3 px apart).
- **In a crowded field the filtered plane's spread is the stars, not the noise.** The finder's noise is capped by
  what the plane's lag-1 and lag-2 differences predict for a correlated noise (within 8 percent of the measured on
  every sparse field; eta Carinae's 107 to 149 against a prediction of 47), and the find on the residual repeats
  until a pass adds under a tenth of the one before: eta Carinae went from 73,605 sources to 160,162.
- **A fill may not rise above the data**: a star only adds light, so a fill more than 3 local sigma above the
  pixel it replaces takes the data's value. The local sigma there, and the fill's grain, come from the local
  differences too, because the rms map counts a nebula's structure and a crowd's faint stars as noise (the
  Trapezium's fill sat 0.26 above data whose noise is a hundredth of that; its negative patch went from 1,487 sigma
  to 34).
- **A core misfit alternates sign from pixel to pixel**, which a three-pixel mean averages away, so a pixel beyond
  5 sigma inside a footprint is flagged on its own (eta Carinae's +-3,000 ADU cores).
- **Edges and refits**: a NaN of the absent canvas no longer makes a plateau NaN (a 700-sigma star at Orion's
  edge was left whole), a centre fitted past the edge is no drift, and a refinement that fails puts back the fit
  it was meant to improve.
- **Speckles, the hole test's blind spot** (the owner's question, 2026-10-03): a core pixel more than 4 sigma under
  the plate's own sky (`StarlessSpeckles`, now in every plate's statistics and the report), which a core's mean hides
  beside a bright pixel. Read on the ten plates as dark against bright excursions, they are two things. At stars of
  significance under 100 the bright outnumber the dark 2 to 12 times: leftover light, the faint-star limit below,
  with dark pixels as its smaller half. At bright stars the dark dominate (Leo Triplet 474 against 84), and those
  were the master's own: an undershoot at the edge of a bright core (single pixels 8 to 11 sigma under the sky beside
  an 800-sigma core in the Leo master), and the fill copied them, since its ceiling held a fill at the data. **The
  camera starts it and the stack deepens it.** In the Leo master 219 of 423 bright stars (significance 300 to 5000)
  carry a pixel under -4 sigma within 3 px of the peak, the darkest two rows below it in 128 of them. Leo's raw subs
  carry the same one-sided deficit: averaged over about 330 bright stars a sub, the pixel two rows below the peak holds
  2.4 to 3.6 percent of the peak where the other three 2 px neighbours hold 4.2 to 5.3 (standard errors 0.2 to 0.4), so
  it is the ASI294MM's own, not the stacker's. In the master the pixel below falls to 0.7 percent and the other three to
  2.4 to 3.8, while the 1 px neighbours rise from about 34 to 39 to 44: a symmetric sharpening at 2 px, the look of a
  warp kernel's negative lobe, which on top of the camera's deficit takes single pixels under the sky. The kernel was
  `Lanczos3Clamped` (the bake's provenance; the session fingerprint includes it, so no session in the store was made
  with another), which documents its ring as under 0.8 percent of a spike's peak; whether it holds on this undersampled
  mono star, and the camera's deficit itself, are #1210, as is the master header that does not name its kernel. **The ceiling
  now never falls below the local sky** (its background map at the fill's scale): a fill can always reach the sky,
  and still never rises above data brighter than that. Leo's bright-star speckles went from 43 and 27 percent to 1
  and 4, Horsehead's from 3.6 and 2.0 to 0.0 and 0.5, Carina's from 1.2 to 0.0, with holes, bias and the inpaint
  fraction unchanged and the Trapezium the same to the eye. A frame-wide version (no ceiling under the frame's
  darkest sky) took a third of Leo's: its sky varies by 25 sigma, so its defects sat 8 sigma over that floor.
  **Refitting each star's centre per channel did not help, and was withdrawn**: with amplitude and level fitted
  beside the shift it cut speckles only by leaving faint cores brighter (the faint band's median core bias from 0.4
  to 1.5 sigma over root n), and the shift alone left them as they were or worse. The fitted position was never
  the fault.

#### What R0 leaves

Tracked by #902 with the rest of P4.0; none of it is a hole.

- **Diffraction spikes** stay, section 8's open question.
- **Stars on a steep, bright nebula** that fit wider than 1.6 times the field (a curved sky under a planar one) are
  left as knots: Orion's 50-sigma star beside the Trapezium, Herschel 36 in the Hourglass.
- **Clipped nebula round a saturated star** (eta Carinae's Keyhole, the Trapezium) is filled from its boundary,
  which is darker than the clip it replaces; what was there is not in the data.
- **An elongated saturated star** (the Horsehead's 4,933-sigma one, its plateau 3 by 5 px) keeps a brighter fill
  where its core was: its residual past a symmetric profile is flagged and filled, and the fill takes the bright
  ends of its elongation as its boundary. An elliptical profile would follow it.
- **M4's core** keeps its unresolved glow and the stars on its bar, the one negative patch over 220 sigma.
- **eta Carinae's inpaint fraction** (above), and the faint stars of its Milky Way field still under the confusion
  the finder's deeper noise reaches.
- **Faint stars on a bright nebula leave small dark dots** (the Orion plate round the M42 core, visible stretched).
  The speckle measure and the hole tests now read their sigma capped by the pixels' own differences
  (`PointSourceFinder.CapByDifferenceNoise`), which the rms map, counting the nebula's texture as noise, put 1.6
  times too high there. **Read against the texture's own null**: in a 600 px box on the M42 core the test fires at
  10.5 to 11 percent of star-free places, in the plate and in the master alike, so the dots R0 adds are the excess
  over that, 13 points (23.5 percent of the stars) before the hole tests took the capped noise and 7 after (18.3).
  The per-pixel flags against the smooth sky keep the map's spread, which shields the texture from being taken for
  residual. A refill driven by the speckle test itself was tried and withdrawn: it made the measure circular and
  raised the holes. Each star's catalogue row now says what it sits on (`FittedStar.SkyAbove`, its sky above the
  frame's darkest in the pixels' noise; `FittedStar.Texture`, the map's spread over that noise, 1 on a smooth sky
  or glow), so a report, a gate or a loss can read a star on nebula apart from one on dark sky.
- **Split by background, the dots at moderately bright stars are R0's own, not the texture's** (all 190 plates of the
  2026-10-03 rebuild, `SpeckleMeasureProbe` with the masters' fields). The speckle measure now classes every site
  and null position by the MASTER's texture (`TextureField`: smooth under 1.2, textured to 2, strongly textured
  above) and draws each class its own null. Median rate against the class's own null, per significance band:

  | Band | Smooth | Textured | Strongly textured |
  |---|---|---|---|
  | 0-20 | 1.1 against 0.2 (89 of 190 over twice) | 5.2 against 1.4 (65 of 117) | 16.7 against 14.9 (11 of 59) |
  | 20-100 | **3.7 against 0.2 (176 of 190)** | 12.3 against 1.7 (61 of 74) | 22.5 against 16.8 (4 of 23) |
  | 100-1000 | 0.5 against 0.2 (42 of 190) | 1.2 against 2.6 (1 of 24) | 22.0 against 16.1 (0 of 3) |
  | 1000+ | 0.0 against 0.2 (2 of 175) | | |

  The frame-wide null had put 168 masters over the gate in the 20-100 band; texture accounts only for the strongly
  textured class. On smooth sky R0 leaves a dark pixel at about one star in 27 of significance 20 to 100, the
  dipole of a fit a fraction of a pixel off (the per-channel recentring that was tried left it as it was). For R1
  that matters where a star is injected AT a subtracted site: the target keeps the site's dot under the injected
  star, which teaches a net to leave one. Either such sites are kept out of at-site placement or the speckle teacher
  outweighs them.

### R1: the injector

Designed 2026-10-03, before any code, from R0's plates and the exporter as it stands
(`DatasetDegradationExporter`, `TianWen.AI.Imaging`: P0's 256 px cells, the clean tile in slot 0 and `deg###`
draws beside it, a `degradations.jsonl` row per draw, noise through `LinearDegradation`).

**A third mode, `DegradationMode.Stars`, of the same exporter**, so it inherits the cells, the bright-cell list,
the per-channel noise anchor (`master-calibration`), the noise field's shape, the resume and the P0-shaped
manifest. What differs:

1. **The clean tile is the STARLESS PLATE**, read from a `starless-plates` store (`--plates <dir>`, the plate of
   `session-masters/<id>.fits` at `plates/<id>_plate.fits`, its catalogue beside it). The plates of the whole
   pool are built once into the bake's own `starless/` folder; a session with no plate is skipped and counted.
2. **Each draw is the plate plus injected stars plus their own noise, and nothing else**: no extra noise level,
   no blur. Both are stretched with the TARGET's parameters (the plate's, measured once per session, H6) over the
   MASTER's unit divisor, so an injected saturated star cannot leave [0, 1].
3. **How many**: a cell gets as many stars as R0 subtracted in it, so the synthetic field is exactly as crowded
   as the real one there (and a crowded master's cells are crowded), plus, in a quarter of draws, one saturated
   star (the bright tail on purpose: 0.1 to 0.2 percent of detections would show the net almost none). Stars are
   placed over the cell and an 8 px margin, so a tile's edge cuts stars as a real tile's does.
4. **Where** (`--placement`): `random` (default) puts each at a uniformly random present pixel at least 3 FWHM
   from every site R0 subtracted, drawn again up to 200 times and otherwise dropped and counted; `at-site` puts
   them on R0's subtracted sites, jittered under half a pixel: H3's control, run once.
5. **How bright**: a real star of the same master is drawn at random from R0's unsaturated subtracted stars and
   its per-channel amplitudes taken whole, so the flux distribution and the colours are the field's own. A
   saturated star takes its clip levels and its wing amplitude over its clip from one of the master's own
   saturated stars (per channel: the core's level in the master; R0's wing amplitude over it); a master with none
   clips at 95 percent of each channel's brightest pixel, and says so.
6. **What shape** (`--profile`): `moffat` (default) at each channel's FWHM and beta from the master's own
   `MasterProfiles` (the PSF store), or `gaussian` at the same FWHM, H2's arm. Elongation and position angle are
   measured where the star lands: windowed second moments of the up to eight brightest isolated unsaturated stars
   of R0's stars plate within 384 px. Every profile is integrated over the pixel (Gauss-Legendre, as R0's).
7. **How a stack saturates**: as the masters showed (plateaus of 1 to 4 px at the median, edges 0.5 to 1 px from
   90 to 50 percent, a clip level that varies from star to star and, on 18 to 24 stars of some masters, one
   channel clipping first), a saturated star is rendered as a stack of eight virtual subs, each with its own
   amplitude (5 percent scatter), width (5 percent) and sub-pixel offset (0.25 px), each clipped where the plate
   plus the star passes its channel's level, and averaged. Its noise is scaled by the fraction of subs that did
   not clip, so a plateau is quiet as a real one is.
8. **Its noise**: the extra shot noise of the star at the master's depth, sqrt(sigma(plate + star)^2 -
   sigma(plate)^2) through each channel's anchor, on a field of the master's shape (the session's warped noise
   field); the plate already carries the sky's. The sigma plane beside each draw is the noise-free plate plus
   stars at that depth.
9. **The record**: every injected star goes into `injections.jsonl`, one row per draw (tile, cell, draw,
   placement, profile, the shortfall, and per star: x and y in the cell, the amplitude, FWHM and beta per channel,
   axis ratio, position angle, whether it is saturated and its clip levels), because eval is against exactly that
   list. `degradations.jsonl` gains the mode, the counts and the placement.

The load-bearing parts live in `TianWen.Lib.Imaging.StarRemoval` (the population drawn from a plate's catalogue,
the elliptical pixel-integrated profiles, the stacked saturation, the renderer) with tests there; the exporter only
cuts, calls and writes. R0's catalogue gains what the injector needs: each star's per-channel amplitudes and the
model it was subtracted with (Moffat, profile or pair), since only a Moffat-fitted saturated star has a wing
amplitude.

**Predicted before the run**, over the ten R0 masters' cells (the checks a test or a report makes):

- **Shape**: injected unsaturated stars of SNR 30 to 1000, fitted back on the draw minus the plate, give each
  channel's drawn FWHM within 5 percent and beta within 15 percent at the median (Gaussian arm: a Moffat fit's beta
  over 6).
- **Noise**: the injected noise's band-1 over band-0 ratio (as `--measure-shape` reads it) within 0.03 of the
  master's own half-pair ratio.
- **Saturation**: injected saturated stars' plateau sizes and 90-to-50 percent edges read like the master's real
  ones (the medians within a factor of 1.5), and no pixel passes its clip level.
- **Placement**: the random arm places at least 95 percent of what it is asked to on every master but eta Carinae
  and the 24 mm Carina field, whose crowding leaves too little room 3 FWHM from every subtracted site; no random
  star is nearer than 3 FWHM to one.
- **Determinism**: the same seed gives the same bytes, and the Noise and Blur modes' exports are unchanged.

#### R1 as built (2026-10-03)

`tianwen dataset degrade --mode stars --plates <store> [--placement random|at-site] [--profile moffat|gaussian]
[--saturated-fraction 0.25] [--psf-store <jsonl>]`, the noise anchor defaulting to `master-calibration`, the only
one it takes. The pieces are `StarProfile`, `StarInjection` and `InjectionPopulation` in
`TianWen.Lib.Imaging.StarRemoval`, and `StarlessCatalogue`, which now carries each star's model and per-channel
amplitudes (a catalogue from before reads back with neither, and the injector refuses it rather than guessing),
and what each star sits on (`SkyAbove`, `Texture`; NaN from an older catalogue).
Two details differ from the design above. A cell's count is R0's count in the cell scaled to the cell plus its
margin, so the margin is as crowded as the cell. And an injected star's elongation comes from the NEAREST eight
measured stars within 384 px (unsaturated, isolated by 3 FWHM, significance 30 to 1000), not the brightest eight,
because elongation varies over a field (tilt, coma) and the nearest are the sample that shares it.

Three things the tests found while it was built, each fixed and pinned:

- **A windowed moment reads every star rounder than it is.** Through a circular window of two FWHM a star of axis
  ratio 0.6 read 0.677 (the angle exactly), because the window cuts the wings harder along the major axis. The
  population now reads each measurement back through the same window applied to the master's own model profile
  (a table over axis ratios, four sub-pixel centres): 0.596.
- **The render's reach was the box around a star, not its radius.** The corners reach 1.4 radii, past where the
  light is under the floor, and the sliver of noise added there flipped two f16 values 41 px from a saturated star
  whose reach is 29. The reach is now a circle; far from every injected star a draw is the clean tile to the bit.
- **The pixel-mean rule (three-point Gauss-Legendre, R0's) errs in the core pixel of a sharp star**: 1.3e-4 of
  the peak at 1.8 px FWHM and 4e-4 at 1.5 px, against 1e-5 at 3 px. `MoffatPsf`'s remark claimed 1e-5 at 1.5 px
  too and now says what it is. It is harmless to the injector (a net learns to remove whatever is rendered) and
  0.3 sigma on an 800-sigma 1.5 px star for R0.

**The first checks' reading (ten R0 masters, 20 cells and two draws a session, warped noise as E16b):**
placement 100 percent everywhere, eta Carinae and the 24 mm Carina included (the prediction was too pessimistic: even
there a fifth of the frame lies 3 FWHM from every site); shape within its bounds on every channel but two, Carina
24 mm's red (FWHM 1.08, its store beta 1.05 at the grid's floor, where width and wings cannot be told apart) and a
channel whose beta is the store's 24.95 ceiling (a Gaussian, beta unidentifiable); the same seed the same bytes, and
the Noise and Blur exports byte-identical to a build from before R1 but for five null fields on the row; nothing past a
clip. **Saturation misses**: with a saturated star in every draw (24 to 40 a session), the injected plateaus are 2 to 3
times the masters' own on six of nine (SMC 3.0 px against 1.0, Antares 3.0 against 1.0). A master's saturated core is
mostly soft, compressed under a one-pixel top (Leo Triplet's: 0.84 to 0.93 of full scale, no flat), where each virtual
sub here is clipped hard at a level; a soft knee is the likely model, and it is open. `SaturatedEdgeProbe` cuts both in
one linear unit range (16 of each on Leo, the SMC, Antares and eta Carinae, measured with `InjectionMeasure.SaturatedShape`):
the miss is at the very top, not in the wings. Where a master's saturated star is small (Leo, the SMC, Antares, median
plateau 1 px) its top is round and the injected one holds its maximum about 0.75 px farther before falling along the
same wing; on eta Carinae (ASI1600MM) the plateaus agree (4 px) but the real stars are wider below 0.9 of their maximum
and softer (edge 1.0 px against 0.5) and elongated. And two of the SMC's sixteen are no star at all: an extended core
and a star beside a brighter one, both flagged saturated, which the saturated pool draws from as from any other.

The pool's plates found a fourth, in R0: **a giant whose plateau passes 40 px failed its master.** Every sky
annulus was a clamp of the star's reach between what it must clear and 40 px, and eta Carinae at 288 mm (QHY183M,
SII) has a 67 px plateau, so the clamp's floor passed its ceiling and threw. The annulus now clears the core first
and caps only the reach (`SkyAnnulus`, the clamp itself below the cap, so every plate already built is unchanged);
a synthetic giant of 45 px leaves its core 0.15 sigma under the truth.

**The second checks' reading (the 190 rebuilt plates, the same ten masters, 2026-10-03):** placement 100 percent, nothing
past a clip, the same seed the same bytes, shape within its bounds but where the store's beta sits at a grid edge (the Rim
Nebula's blue, Carina 24 mm's red). The saturated plateaus still ran 2 to 3 times the masters' own (SMC 3 against 1,
Antares 3 against 1). And the noise arm missed on the one mono master: the ASI1600MM's injected band-1 over band-0 ratio is
0.458 against its half-master's 0.279, because `--warp-sigma 0.5` was calibrated on a demosaiced master's noise and a mono
master's is near white; Carina 24 mm is 0.04 off, and three sessions have no half-master pairs to read against. The mono
noise shape is open.

#### The soft top, resolved (2026-10-04)

**The injector drew each star with a profile its amplitude was not fitted with.** A saturated star's catalogued amplitude
is R0's wing fit with R0's field profile (its Moffat plus the radial residual table); the injector drew it with the PSF
store's single Moffat, which holds 0 to 2 percent of the light past 4.5 px where real stars hold 4 to 28. Drawn with R0's
own profile the first ring is within 0.01 to 0.04 of the real one on six of ten masters (0.09 to 0.11 on the SMC and the
Lagoon) and the plateau 1 px as real; the 8 hard-clipped virtual subs were right all along. R0 now writes its field profile
beside each plate (`<stem>_plate.profile.json`, `StarlessFieldProfile`), a store built before is given one by `tianwen
dataset starless-plates --profiles-only` (the same calibration, checked against the beta the store recorded: all 190
match), and `--profile field` is the default (feat(imaging): the injector draws each star with the profile its amplitude
was fitted with, the plate builder's own).

What was left, a halo excess around saturated stars that shrank with saturation depth, was taken apart with the literature
in [star-removal-literature.md](../architecture/star-removal-literature.md) and four experiments. Seeing scatter (0.3 to
7.5 percent from sub to sub), a soft sensor (the ASI1600MM's single subs are linear to a hard clip) and the pixel phase of
each sub's own clip (2 to 15 percent of the knee) are refuted. Tycho-2 photometry through SPCC's matcher confirmed the
rest: the catalogue's saturated amplitude is right at the median on seven of nine masters but rises with saturation depth
(up to a quarter too bright on the most saturated, Spearman -0.24 to -0.44), as R0's wing-fit window grows with the
plateau; and against each star's photometric amplitude the near-wing excess runs on continuously across the saturation
threshold, so it is the residual table under-reading bright stars' near wings (up to about 30 percent), not saturation.

**The third checks' reading (the field profile the default, 2026-10-04):** placement 100 percent, nothing past a clip,
every injected FWHM within 5 percent of its reference (0.987 to 1.043), beta within 15 percent but on the Lagoon (1.24),
the Orion master (1.18) and one Carina channel. The saturated arm's plateaus (injected against real) moved toward the
masters on five sessions (Antares 3 to 2 against 1, the SMC 3 to 2 against 1, the Rim Nebula 2 to 1 against 1, the
Horsehead 1 against 1, Centaurus A 1 against 2) and stand at 2 against 1 on four; on eta Carinae they doubled (5.5 to 10
against 3, and its two flip halves 7 and 6 against 4 and 3) and on Carina 24 mm they grew (4 to 5.5 against 2). Those two
are the masters the at-site probe already flagged: the ASI1600MM's real top is a few percent below flat over 3 px where a
hard clip is flat (theme B reads it as the per-sub clip spread a 12-bit unity-gain camera has), and the 24 mm field has the
widest plateaus. A 2 against 1 is a top still a little flatter than real (the probe's first ring 0.03 to 0.09 short on the
SMC and the Lagoon), which theme B's clip spread would also round. The noise arm is unchanged, as it must be. Leo has been
refused by every R1 run, the first included: its plate fails the exporter's linearity test (median over minimum above
0.125 of full scale, the bright sky at about a third of it), so no R1 measure has covered it.

**The training exports (started 2026-10-04).** The pool is the store's train split less the three plates the exporter's
linearity test refuses (ZWO ASI294MM luminance, a bright sky: Leo Triplet 2022-03-26 and the Rosette 2021-12-31 and
2022-01-09, at 0.196, 0.176 and 0.138 against the auto-detect's 0.125; `PlateLinearityProbe`, 3 of 190) and less one whole
held-out train, the SVBONY SV605CC on the SH61 at 270 mm (19 sessions, a camera, sensor family and scope apart from the
dominant ASI533 and Samyang 135): 138 sessions, 40 cells and 4 draws each, the field profile, saturated fraction 0.25,
warped noise as the checks. Three arms from one binary: random (the training arm), Gaussian (H2) and at-site (H3's
control). Each arm runs in chunks of ten sessions into one folder, the stop file read between chunks; the draws are seeded
per session, cell and draw, so a chunked arm is the bytes one run would write.

**R2a, the first training (launched 2026-10-04).** `training/denoise/run-r2.ps1`: the three arms, three seeds each,
interleaved, on the shipped denoiser's training line (L2 on the rim-masked tile, the 2-4 and 4-8 px DoG bands at 3, the
stored noise plane as conditioning, plateau schedule), regressing an injected draw onto its starless plate. The export
writes 4 draws where the cache has 8 sub slots, and the trainer drew from all 8, so half its inputs would have been zero
tiles, silently: `prepare` now records the slots every cell fills (`meta.json` `draws`) and the trainer samples, holds out
and checks noise planes within them (a cache from before samples exactly as it did). The split is by night
(`arms/r2-train.txt`, 113 sessions; `arms/r2-val.txt`, HIP 80609 and the first non-comet night of the ASI585, ASI294MC
and QHY294PROC); the 15 mono sessions wait on H7, the trainer being 3-channel. `--gate-every 0`, since the denoiser's
selection keeps only a checkpoint that cuts the noise. **What R2a is not:** the speckle teacher (a port of
`StarlessSpeckles` with its parity fixture, which needs the noise cap `PointSourceFinder.CapByDifferenceNoise` ported too)
and the flux penalty (H5) are not written, so these models are taught L2 and bands and scored for speckles; and no R2a
model is scored before its predictions are registered here.

**R2's eval (built 2026-10-04).** `training/denoise/r2_infer.py` runs a checkpoint over an export's val draws (the net
only), and `tianwen dataset starless-eval` (`StarRemovalEval`) scores them in C#, on the luminance inside the stitched
rim, in each draw's own noise plane (its `.sigma.f16` over `StretchedNoise.PlaneScale`). A draw is its plate to the bit
wherever no star light was rendered, so the pixels where it differs ARE the injected footprints, far wings included: the
output minus the plate is read on them (what was left) and off them (the model's change to sky it was handed unchanged).
Completeness is a star's 3x3 core left under 1 sigma, by the injected core's significance (1-5, 5-20, 20-100, 100-1000,
1000+, the saturated as their own band; under 1 sigma counted, not rated); speckles are `StarlessSpeckles` at the injected
sites against the output's own null. Two references come from the same draws, the input (removes nothing) and the plate
(removes all and nothing else), and read as they must on the random arm's 1,280 val draws (350,886 stars): the input 0
percent removed, sky 0; the plate 100 percent, footprints and sky 0, speckled 0.2 percent at the sites against a null of
0.5. A 60-step smoke model read 20 percent at 1-5 sigma, none past 20, and moved the untouched sky by 2.7 sigma. The 1:1
spot checks on real masters remain the owner's, by eye.

**R2a, pre-registered 2026-10-04, before any R2a model was scored** (the first, `r2_random_s0`, still training).
*Protocol.* Every model is run (`r2_infer.py`) on all three arms' val draws (`arms/r2-val.txt`) and scored with
`starless-eval`; the random arm's draws (field profile, random positions, the realistic case) are the common yardstick. Seeds
are reported as mean and range, and a difference between arms counts only where it exceeds the wider of the two arms' seed
ranges. *Predictions, on the random arm's draws, at the final weights:*
(1) every arm removes at least 80 percent of the 5-20 sigma stars, 60 of the 20-100, 40 of the 100-1000, and under 30 of the
saturated (the bright tail stays the hard case); low to moderate confidence.
(2) the untouched sky moves by at most 0.3 sigma RMS (outside the stars the target is the input, which L2 learns early);
moderate.
(3) the footprint RMS is over the plan's gate of 1, carried by the saturated and 100+ sigma stars; moderate.
(4) speckles at the sites stay within twice the output's own null in the 0-20 and 20-100 bands and exceed it at 100+, the
learned remover having no speckle teacher yet; low.
(5) H2: the Gaussian arm removes at least 5 points less than the random (field) arm at 20-1000 sigma and leaves a higher
footprint RMS, its stars having no Moffat wings; moderate.
(6) H3: on its OWN draws the at-site arm removes at least as much as the random arm on its own; on the random arm's draws it
is within 5 points of the random arm (positions alone teach little the plate does not), and its worse real-star behaviour,
if any, shows only in the owner's spot checks; low.
*Kill for R2a as a recipe:* (1) fails at 5-20 sigma for every arm (the net does not learn removal at all), or (2) fails (it
damages sky it was handed unchanged).

*Interim, one model of nine (2026-10-05; nothing is judged until the arms and seeds are in).* `r2_random_s0` on the random
arm's draws: removed 59.0 percent at 5-20 sigma, 63.8 at 20-100, 73.9 at 100-1000 and 43.8 of the saturated (1-5 sigma 30.3);
footprint RMS 2.76; the untouched sky moved 0.52 sigma; speckled 0.1 percent at 0-20, 1.8 at 20-100 and 31.8 at 100-1000
against a null of 0.6. So far (1) misses at 5-20, the saturated band beats its bound, (2) misses (the kill's second
condition, if it holds over the seeds), (3) holds and (4) misses at 20-100. On the gaussian and at-site arms' draws it reads
62-65 percent at 5-20 and moves their sky 0.54 and 0.71. A plate's own speckle rate at R0's subtracted sites (the at-site
draws) is 1.6 to 1.8 percent against a null of 0.3, R0's dark speckles, which an at-site arm's target carries.
`r2_gaussian_s0` (two of nine) on the random arm's draws: 66.2 at 5-20, 60.7 at 20-100, 38.8 at 100-1000 and 28.3 of the
saturated, footprint RMS 18.6 against the random model's 2.76, the sky moved 0.20; speckles at or under the null. On its own
Gaussian draws it removes 77 to 89 percent with a footprint RMS of 1.07. So far H2 reads in its predicted direction, and
larger: a model taught Gaussian stars leaves the field profile's wings (35 points fewer at 100-1000 sigma, seven times the
footprint residual), while it moves the untouched sky less than the field model does.
`r2_atsite_s0` (three of nine) on the random arm's draws: 44.2 at 5-20, 41.2 at 20-100, 58.3 at 100-1000 and 50.7 of the
saturated, footprint RMS 2.38, the sky moved 0.41; that is 15 to 23 points under the random model, past (6)'s 5. On its OWN
draws it removes 56.7 at 5-20 and at 20-100, under the random model on its own (59.0, 63.8), and moves that sky 0.74. So
far both halves of (6) miss the same way: injecting at R0's sites teaches less removal, not more, wherever it is read.
`r2_random_s1` (four of nine) withdraws most of that: the field arm's own seeds differ by 15 to 18 points (s1 on the random
arm's draws: 40.8 at 5-20, 53.5 at 20-100, 58.5 at 100-1000, 59.4 of the saturated, footprint RMS 7.09, the sky moved 0.55),
so the at-site model's 44.2 at 5-20 sits inside the field arm's range (40.8 to 59.0) and its 41.2 at 20-100 just under it
(53.5 to 63.8); one seed was read as a difference it is not, which is what the protocol's seed rule is for. What stands past
the field arm's range so far: the Gaussian model's 38.8 at 100-1000 (field 58.5 to 73.9) and its footprint RMS of 18.6
(field 2.76 to 7.09), H2's direction; and the field arm moving the untouched sky 0.52 and 0.55, both over (2)'s 0.3.
`r2_gaussian_s1` (five of nine) holds the Gaussian arm apart on its second seed. On the random arm's draws it removes 63.2
at 5-20, 55.5 at 20-100, 18.9 at 100-1000 and 16.9 of the saturated. Its footprint RMS is 17.3, and the sky moved 0.23. So
the arm reads 18.9 to 38.8 at 100-1000 against the field arm's 58.5 to 73.9, and 17.3 to 18.6 of footprint RMS against 2.76
to 7.09: two seeds each, the ranges far apart, H2's direction. It moves the untouched sky 0.20 to 0.23 against the field
arm's 0.52 to 0.55, also apart. On its own Gaussian draws it removes 71 to 77 percent from 5 to 1000 sigma and 72.6 of the
saturated, with a footprint RMS of 1.01. It learned the stars it was shown and not the field's wings.
`r2_atsite_s1` (six of nine) puts a second seed on the at-site arm. On the random arm's draws it removes 40.6 at 5-20, 35.5
at 20-100, 39.7 at 100-1000 and 32.0 of the saturated, with a footprint RMS of 7.79, and the sky moved 0.37. At 100-1000
sigma it leaves speckles at 13.4 percent of sites against a null of 0.5. The arm's two seeds now read:
- 5-20 sigma: 40.6 to 44.2, inside the field arm's 40.8 to 59.0;
- 20-100 sigma: 35.5 to 41.2, wholly under the field arm's 53.5 to 63.8, past (6)'s 5 points;
- 100-1000 sigma: 39.7 to 58.3, against 58.5 to 73.9.
On its own draws it removes 55.9 at 5-20 and 54.0 at 20-100, within the field arm's own range, and moves that sky 0.65.
So far the second half of (6) misses at 20-100 sigma on both seeds, and the first half holds.
`r2_random_s2` (seven of nine) is the field arm's third seed and its best. On its own draws it removes 67.9 at 5-20, 76.5 at
20-100, 85.4 at 100-1000 and 77.2 of the saturated, with a footprint RMS of 3.79 and the sky moved 0.60. It leaves speckles
at 0.2, 4.0 and 48.5 percent of sites by band against a null of 0.6. Its plateau stopped it early. The field arm over three
seeds now reads:
- 5-20 sigma: 40.8 to 67.9;
- 20-100 sigma: 53.5 to 76.5;
- 100-1000 sigma: 58.5 to 85.4;
- footprint RMS: 2.76 to 7.09;
- sky moved: 0.52 to 0.60, every seed over (2)'s 0.3.
The seed spread is the widest thing measured so far, 27 points at 5-20 sigma. Against it, two readings still stand apart.
The Gaussian arm's 18.9 to 38.8 at 100-1000 sigma, with a footprint RMS of 17.3 to 18.6, is H2's direction. The at-site
arm's 35.5 to 41.2 at 20-100 sigma is (6)'s second-half miss.
`r2_gaussian_s2` (eight of nine) completes the Gaussian arm. On the random arm's draws it removes 61.7 at 5-20, 52.4 at
20-100, 17.3 at 100-1000 and 18.7 of the saturated, with a footprint RMS of 18.5, and the sky moved 0.19. On its own
draws it removes 74 to 80 percent from 5 to 1000 sigma and 63.0 of the saturated, with a footprint RMS of 1.00. Over its
three seeds the arm reads:
- 100-1000 sigma: 17.3 to 38.8, against the field arm's 58.5 to 85.4;
- footprint RMS: 17.3 to 18.6, against 2.76 to 7.09;
- sky moved: 0.19 to 0.23, against 0.52 to 0.60.
All three are apart, H2's direction on every seed. The arm also moves the untouched sky inside (2)'s 0.3, which no field
seed does.

#### R2a's result: the recipe is KILLED; H2 holds where the wings are, H3 misses (2026-10-05)

Nine models, three arms by three seeds, each scored on every arm's val draws (`C:\temp\e2\r2a_read.py` over
`C:\temp\e2\r2-eval`). Read as registered: on the random arm's draws, arm means, with a difference counted only past the
wider of the two arms' seed ranges.

| arm | 5-20 sigma | 20-100 | 100-1000 | saturated | footprint RMS | sky moved |
|---|---|---|---|---|---|---|
| random (field) | 55.9 (40.8-67.9) | 64.6 (53.5-76.5) | 72.6 (58.5-85.4) | 60.1 (43.8-77.2) | 4.54 (2.76-7.09) | 0.56 (0.52-0.60) |
| Gaussian | 63.7 (61.7-66.2) | 56.2 (52.4-60.7) | 25.0 (17.3-38.8) | 21.3 (16.9-28.3) | 18.1 (17.3-18.6) | 0.21 (0.19-0.23) |
| at-site | 37.7 (28.3-44.2) | 36.2 (31.7-41.2) | 51.1 (39.7-58.3) | 38.4 (32.0-50.7) | 5.82 (2.38-7.79) | 0.42 (0.37-0.47) |

- **The kill fires on both of its conditions.** No arm reaches (1)'s 80 percent at 5-20 sigma, and the field and at-site
  arms move the untouched sky past (2)'s 0.3 on every seed. Its parenthetical, "the net does not learn removal at all",
  is not what happened: every arm removes some, the field arm three quarters at 100-1000 sigma, the Gaussian arm 74 to 80
  percent on its own draws. But the bar was the registered one, so the recipe as run is killed.
- **(1)** also holds at 20-100 and 100-1000 sigma for the field arm only. The saturated band beats its bound wherever the
  wings were taught: the bright tail is not the hardest case here.
- **(3) holds** on every arm.
- **(4) misses at 20-100 sigma** on the field and at-site arms (2.5 and 1.3 percent of sites against twice a 0.6 null).
  The Gaussian arm leaves no speckles at 100+ because it leaves those stars in place.
- **(5) H2 holds where the wings are.** At 100-1000 sigma the Gaussian arm removes 47.6 points less, past the 26.9-point
  seed range, with four times the footprint residual. At 20-100 sigma its 8.4 points sit inside the 23.1-point seed range,
  so H2 is not shown there.
- **(6) H3 misses both halves.** On its own draws the at-site arm removes less than the field arm on its own (50.3 against
  55.9 at 5-20, 52.8 against 64.6 at 20-100, 61.3 against 72.6 at 100-1000). On the field arm's draws it is 18 to 28 points
  under, past (6)'s 5 everywhere and past the seed range at 20-100. Injecting at R0's sites teaches less, not more.
- **The seeds spread 27 points at 5-20 sigma, and the plateau schedule is part of it.** It stopped the nine runs at 21,000
  to 51,500 of 60,000 steps, and the earliest stop of an arm is that arm's worst seed on its own draws. The at-site arm's s2
  stopped at 21,000 and reads 38.3 at 5-20 (s1 at 36,000: 55.9; s0 at 44,000: 56.7). The field arm's s1 stopped at 27,500
  and reads 40.8. A recipe whose length the held-out loss decides this early cannot be judged at 3 seeds.

What it leaves for R2b, the owner's choice (#32): a fixed length, or a plateau patience long enough that no seed stops
early (the owner chose the longer patience, 2026-10-05); the sky term, which the field arm moves twice as far as the
Gaussian one (an L2 that leaves the sky to learn early, per (2)'s reasoning, did not); and whether at-site placement goes
on at all, now that it reads worse on every axis.

#### R2b, pre-registered (2026-10-05)

Written before any R2b model existed.

**One change: the plateau's patience, 4 evaluations to 12.** The schedule halves the rate after `--patience` evaluations
(every 500 steps) without a 0.1 percent gain and stops at the next stall after four halvings, so R2a allowed at most
10,000 stalled steps and its runs stopped at 21,000 to 51,500 of 60,000. At 12 the allowance is 30,000. Everything else is
R2a's: the same exports, split, seeds 0 to 2, 60,000 steps, `--max-decays 4`, loss and scoring (`run-r2.ps1 -Tag r2b
-Patience 12`, `score-r2.ps1 -Tag r2b`).

**Arms: the field (random) and the Gaussian, three seeds each.** The at-site arm is left out: it read worse than the field
arm on every axis in R2a, and its export carries R0's dotted sites (#86). Six runs, about 15 hours, started once the E16c
S4 bake ends (the owner's choice).

**Predictions**, read as R2a's were (the random arm's draws, arm means, a difference counted only past the wider seed
range):
1. Every run trains past step 45,000. Moderate confidence.
2. The field arm's seed range at 5-20 sigma narrows to at most 15 points, from R2a's 27. Moderate. If it does not, the
   spread is the seeds, not the schedule.
3. The field arm's mean at 5-20 sigma rises at least 5 points over R2a's 55.9. Low to moderate.
4. R2a's kill bar is still missed at 5-20 sigma (80 percent) by the field arm. Moderate: longer L2 training without a
   speckle teacher should not close a 24-point gap.
5. The field arm still moves untouched sky past 0.3. Moderate: what moves the sky is the loss, not the length.
6. H2 holds again at 100-1000 sigma and on footprint RMS.

**Kill:** R2a's, unchanged. What it decides: if (2) and (3) hold, R2a's seed spread was the schedule, and the next lever
is the loss (a sky term, the speckle teacher); if (2) fails, three seeds were never enough to read a difference at 5-20
sigma, and the next R2 must run more.

**As the runs land** (`C:\temp\e2\r2b_read.py` over `C:\temp\e2\r2-eval`). `r2b_random_s0` (one of six) ran to the
60,000-step cap with its held-out loss still improving (best at 58,500, three halvings): the cap ended it, not the
plateau. On the random arm's draws it removes 64.4 percent at 5-20 sigma, 62.9 at 20-100, 67.8 at 100-1000 and 60.3 of
the saturated, with a footprint RMS of 3.15, and the sky moved 0.52. It leaves speckles at 0.1, 1.3 and 18.2 percent of
sites by band against a null of 0.6. At 64.4 it sits inside R2a's field range at 5-20 (40.8 to 67.9) and above its mean
(55.9). One seed reads no range: (1) holds for it, and (3), (4) and (5) read its way so far.
`r2b_gaussian_s0` (two of six) also ran to the cap, after only one halving (best at 55,500). On the random arm's draws it
removes 53.3 at 5-20 sigma, 37.8 at 20-100, 8.4 at 100-1000 and 11.9 of the saturated, with a footprint RMS of 18.1, and
the sky moved 0.19. All four removals sit at or under the bottom of R2a's Gaussian range (61.7, 52.4, 17.3, 16.9). On its
own draws it removes 65.6, 69.7 and 60.8 percent from 5 to 1000 sigma and 53.0 of the saturated, with a footprint RMS of
1.04. Against the field seed so far, H2 reads its way again: 59.4 points apart at 100-1000 sigma, and 15 sigma more
footprint residual.
`r2b_random_s1` (three of six) ran to the cap too (best at 53,500, three halvings). On the random arm's draws it removes
54.4 at 5-20 sigma, 59.0 at 20-100, 61.1 at 100-1000 and 50.7 of the saturated, and the sky moved 0.49. It also removes
only 24.2 at 1-5 sigma, against s0's 41.4. Its footprint RMS is 16.1 there and 13.8 on the at-site draws: five times
s0's 3.15, past R2a's whole field range (2.76 to 7.09) and near the Gaussian arm's 18.1. On the Gaussian draws it reads
1.83, as s0 does. Over two seeds the field arm reads 54.4 to 64.4 at 5-20 sigma (a mean of 59.4), so (2) holds so far at
10 points and (3) misses by 1.5. H2 holds at 100-1000 sigma (56.0 points against a 6.7-point range), but not yet on the
footprint, whose field range s1 has widened to 13.0.
`r2b_gaussian_s1` (four of six) ran to the cap with its loss still falling (best at 59,500, two halvings). On the random
arm's draws it removes 63.2 at 5-20 sigma, 49.3 at 20-100, 10.9 at 100-1000 and 20.1 of the saturated, with a footprint
RMS of 17.7, and the sky moved 0.19. On its own draws it removes 76.8 at 5-20, 79.5 at 20-100 and 67.9 at 100-1000, and
74.0 of the saturated, with a footprint RMS of 0.96: the nearest any model has come to (1)'s 80, and on the stars it was
taught. The Gaussian arm over two seeds reads 53.3 to 63.2 at 5-20 sigma and 8.4 to 10.9 at 100-1000, against R2a's 61.7
to 66.2 and 17.3 to 38.8: the longer schedule moved it further from the field at the bright end. H2 holds at 100-1000
sigma (54.8 points against a 6.7-point range).
`r2b_random_s2` (five of six) ran to the cap with its loss still falling (best at 59,000, two halvings). On the random
arm's draws it removes 61.7 at 5-20 sigma, 69.8 at 20-100, 63.9 at 100-1000 and 48.4 of the saturated, with a footprint
RMS of 9.02, and the sky moved 0.60. The field arm over its three seeds reads 54.4 to 64.4 at 5-20 sigma, a mean of
60.2: (2) holds at 10 points against R2a's 27, (3) misses its 60.9 by 0.7, (4) and (5) hold, and H2 holds at 100-1000
sigma (54.6 points against a 6.7-point range) but not on the footprint, whose field range is 13.0.
`r2b_gaussian_s2` (six of six) ran to the cap with its loss still at its best (best at 60,000, two halvings). On the
random arm's draws it removes 63.8 at 5-20 sigma, 49.0 at 20-100 and 12.5 at 100-1000.

#### R2b's result: the seeds' spread was the schedule; the kill still fires; read with the fixed eval, H2 holds smaller (2026-10-06)

Six models, two arms by three seeds, each scored on every arm's val draws (`C:\temp\e2\r2b_read.py`), then re-scored on
the random arm's draws with the fixed eval ("What the eval could not see", section 5; every registered number came back
to the bit over all 15 of R2a's and R2b's models).

**As registered** (the random arm's draws, arm means and seed ranges):

| arm | 5-20 sigma | 20-100 | 100-1000 | saturated | footprint RMS | sky moved |
|---|---|---|---|---|---|---|
| field | 60.2 (54.4-64.4) | 63.9 (59.0-69.8) | 64.3 (61.1-67.8) | 53.1 (48.4-60.3) | 9.44 (3.15-16.15) | 0.54 (0.49-0.60) |
| Gaussian | 60.1 (53.3-63.8) | 45.4 (37.8-49.3) | 10.6 (8.4-12.5) | 16.4 (11.9-20.1) | 18.18 (17.73-18.71) | 0.18 (0.17-0.19) |

- **(1) holds:** every run trained to the 60,000-step cap, and every one stopped there with its held-out loss still
  improving, so the cap, not convergence, ended them all.
- **(2) holds:** the field arm's seeds span 10 points at 5-20 sigma, against R2a's 27. R2a's spread was the schedule.
- **(3) misses:** the field arm's mean rose 4.3 points (55.9 to 60.2), under the 5 registered.
- **(4) and (5) hold:** 60.2 is far under 80, and every field seed moves the sky past 0.3.
- **(6) holds at 100-1000 sigma** (53.7 points against a 6.7-point range), **not on the footprint** (8.7 against the
  field arm's 13.0 range, which `r2b_random_s1`'s few bad tiles set).
- **The kill fires on both conditions**, as predicted.

**Read with the fixed eval** (same draws):

| arm | clean 5-20 | clean 100-1000 | dug 100-1000 | core residual 100-1000, mean / RMS | footprint p50 / p90 | sky: level / about level / noise ratio |
|---|---|---|---|---|---|---|
| field | 53.5 (48.7-57.1) | 16.0 (13.3-18.6) | 51.3 (50.0-52.4) | +0.7 / 5.9 | 0.75 / 1.37 | 0.107 / 0.525 / 0.970 |
| Gaussian | 57.0 (50.5-60.6) | 7.1 (5.4-8.7) | 1.9 (1.7-2.2) | +17.1 / 52.7 | 1.06 / 8.78 | 0.018 / 0.181 / 0.990 |

- **H2 holds, smaller than registered.** At 100-1000 sigma the field profile teaches an unbiased removal with an RMS
  of 6 sigma where the Gaussian one leaves +17; clean removal is 16.0 against 7.1, apart by more than either range,
  where the registered rate read 64.3 against 10.6. At 5-20 sigma the arms tie on clean removal.
- **Neither arm removes a bright star cleanly:** 16 percent at best. Its unbiased scatter, not a bias, is what the
  field arm has left to lose.
- **The footprint gate of 1 is met by both arms' median tile** (0.75 and 1.06); the pooled RMS is the worst tiles'.
- **The field arm's sky movement is texture** (0.525 of its 0.536), not level and not smoothing.

**What it decides.** Length and seeds are settled: three seeds now read a 10-point range at 5-20 sigma, and longer
training did not lift removal past the bar. What is left to move the recipe is not more of the same:
- **the truth:** whether the sky "movement" is the faint stars R0 left in its plates, which would make the kill's sky bar
  penalise correct removal (a check on the existing outputs, no GPU);
- **the loss:** nothing in an L2 against the plate asks for a bright star's scatter to shrink; the speckle teacher and a
  per-star term are the candidates the plan already names;
- **convergence:** every run was still improving at the cap, so a single seed run to convergence would say what more
  steps buy;
- **the physics:** the injector's stars are the master's symmetric profile, never the train's own field aberrations
  ([physical-psf-model.md](physical-psf-model.md), #1298).
The owner chooses which of these R2c is. **The owner's answer (2026-10-06): check all four, the truth first.**

#### R2c: convergence, pre-registered (2026-10-06)

Written before the run started. One run: the field arm, seed 0, R2b's recipe exactly (`run-r2.ps1 -Tag r2cconv -Arms random
-SeedCount 1 -Patience 12`), with the step cap raised from 60,000 to 240,000 (`-Steps 240000`), so the plateau
schedule's fourth halving, not the cap, ends it. The schedule does not depend on the cap, so the run follows
`r2b_random_s0` for its first 60,000 steps up to the GPU's nondeterminism. It is scored as R2b was (`score-r2.ps1`) and
read with the fixed eval, against `r2b_random_s0` on the random arm's draws, whose numbers it is set against here:
clean 57.1 at 5-20 sigma and 18.6 at 100-1000, the core residual's RMS 4.26 at 100-1000, the footprint's median tile
0.73, and the sky's change about its level 0.51.

**The question.** Every R2b run stopped at the cap still improving. Do the steps past it buy removal?

**Predictions.**
1. It ends on its own before 240,000 steps. Moderate confidence.
2. Clean removal at 5-20 sigma rises by at most 5 points over 57.1. Moderate: an L2 that has not taught clean removal in
   60,000 steps should not learn it from more of the same.
3. The core residual's RMS at 100-1000 sigma falls by at least 20 percent from 4.26. Low to moderate: the bright stars'
   scatter is the slowest thing an L2 fits.
4. The sky's change about its level stays above 0.4. Moderate.

**What it decides.** If (2) holds, length is not R2c's lever and the cap was not what held R2b back. If (2) fails,
every R2 so far was read early, and the next arms run to convergence.

#### R2c's four checks (2026-10-06)

The owner asked for all four levers to be checked, the truth first. Each read is on R2b's models and the random arm's
draws, with the fixed eval.

**1. The truth: the plates keep faint stars, and most of the field arm's "sky moved" is on them.** `PlateSources` finds
the plate's own point sources as the builder does, at 4 sigma (its leftover threshold; its detection threshold is 5).
- The plates hold **36,765 of them on their sky over the 1,280 draws, about 29 a tile**: the stars under the builder's
  detection threshold. Their surroundings (two PSF widths) are 4.0 percent of the sky.
- **The field arm moves that 4 percent at an RMS of 2.10 sigma**, lowering it on average (-0.26), and takes 14 percent of
  the plate's sources outright. That is about 62 percent of its sky's whole squared change. The Gaussian arm reads 0.81
  there and takes 10 percent.
- **Away from them the field arm moves the sky 0.34 sigma** (the Gaussian arm 0.08), just over the kill's 0.3. Stars
  fainter than 4 sigma, which the finder cannot see, are the likely rest.
- So the kill's sky bar mostly penalised the field arm for removing real stars the truth kept. The truth also teaches
  against itself: an injected faint star must go and a kept one of the same kind must stay.

**2. The loss: it rewards the bright cores first, then stops seeing them.** `LossShare` reads a candidate's squared
error against the plate in the tile's own units, the pixel term's view.
- For the input (an untrained identity), the bright stars' 3x3 cores (100 sigma and over, saturated ones included) hold
  **42.7 percent** of the squared error: the L2 is not blind to bright stars at the start.
- For `r2b_random_s0` they hold **2.8 percent** of what is left. Its unbiased scatter of about 6 sigma there costs the
  loss almost nothing, so nothing pushes it down. The rest is 82.5 percent on the other footprint pixels (fainter stars
  and wings), 10.2 percent on the sky near the plate's own sources (the loss penalising it for taking the kept stars),
  and 4.5 percent on the far sky.
- `r2b_gaussian_s0` keeps 22.7 percent of its error in the bright cores: it leaves them.
- So a per-star term (the speckle teacher, or a core term scaled per star) is what would keep pushing on bright stars;
  an L2 has finished with them.

**3. Convergence:** running ("R2c: convergence, pre-registered"), read when it ends.

**4. The physics: real stars change size from the centre to the corner, per channel, and the injector draws one size.**
`InjectionPopulation.ProfilesAt` gives every injected star its channel's whole-master profile; only the elongation is
local. The bake's own field-radius profile (`C:\temp\e2\field_profile_summary.py` over the 2026-10-05 store's report, 25
colour trains, flip halves left out) says real stars change by 10 to 58 percent from the centre to the corner, and the
three channels disagree about the corner by a median of 0.144 (at most 0.93) in their corner-over-centre ratio. The
Samyang 135 with the L-Ultimate (3,191 corner stars): red 0.905, green 1.352, blue 1.358. The SH61 with the L-Quad: red
0.845, green 1.172, blue 1.097. That is a focal surface curved and chromatic (the archive's red inversion), and the
physical model's P1 to P3 (#1296 to #1298) are where it would enter.

**Which R2c.** The truth is the first lever: it moves the kill's sky bar and it teaches against the injected faint
stars. Either plates that keep no faint stars (the builder subtracting deeper), or a loss and an eval that do not count
the sky near a source the plate kept. The loss is the second: a per-star term for the bright end. Both need no new
export of the injector; the physics does.

### R2d: a truly starless background, synthetic fine scales over the plate's coarse ones (the owner's direction, 2026-10-06)

Tracked by #1311. Section 0 argued that the plate need not be starless, because its imperfections would be
removal artefacts the net learns to leave. R2c's first check found something else: the plates keep about 29 REAL faint
stars a tile, the same kind as the injected ones, so the target asks the net to remove a faint star and to keep one
exactly like it. The owner asked whether there is data enough to make the starless background synthetic, add the stars
and remove them.

**Stars: yes, already.** The injector draws each master's measured profile per channel, its stack's saturation and its
own shot noise in the master's noise shape; the physical PSF ([physical-psf-model.md](physical-psf-model.md)) adds the
field and colour terms it lacks.

**Nebulosity: by how it is made.**

| way | data it needs | risk |
|---|---|---|
| a learned generator over our plates | far more than 4,520 cells, and it would learn the plates' faint stars with them | not viable |
| wholly procedural (turbulence, filaments, dust lanes) | none | the domain gap: the net learns the generator, not the sky |
| **hybrid: the plate's coarse scales, synthetic fine ones** | the 190 plates there are | fine-scale realism, which is measured first |

The hybrid, physics first:
- **D1, measure the real sky** (`tianwen dataset sky-texture`). On each plate, away from every source it kept and its
  canvas ring: the starlet energy per scale, its kurtosis per scale, and the power-law slope the scales above the noise
  give (turbulent emission and dust have power-law spectra; the slope is measured, never assumed), by texture class.
- **D2, the background.** The plate's starlet scales from about 4 PSF widths up, where no star survives, kept as they
  are; the scales under them drawn anew, a field with the measured slope and per-scale kurtosis for that plate's
  texture class; the master's own noise shape on top (E16b). Starless by construction at every scale a star lives at.
- **D3, the cue that tells a star from a knot.** Every star in a frame shares its PSF; a nebula's knot does not. The
  generator adds compact structure that is not the PSF (knots wider or narrower than it, elongated, a filament's
  crossing), labelled keep: a label no real plate can give.
- **D4, the interim fix and the baseline.** A loss and an eval that do not count the sky within two PSF widths of a
  source the plate kept (`PlateSources`). It needs no new export, and is R2d's control arm.
- **D5, validation on the real thing.** Never on synthetic draws alone ("the network will faithfully learn all of your
  mistakes" holds for a generator as much as for a plate): held-out synthetic draws, today's real-plate draws, and real
  held-out masters read with R0's hole, leftover and speckle tools and the owner's 1:1 spot checks.

#### R2d, pre-registered (2026-10-06)

Written before any synthetic background or model existed; D1's measurements set the generator, and nothing in it is
fitted to an R2d outcome. Two arms, three seeds each, R2b's recipe (patience 12, the cap the convergence run argues
for): **A**, today's plates with D4's mask in the loss; **B**, the D2 and D3 background with the same stars. Read with
the fixed eval and D4's mask.
1. On A's draws, B's clean removal at 5-20 sigma is at least 5 points over A's. Low to moderate: the domain gap works
   against it.
2. On A's draws, B moves the sky away from the plate's sources less than A. Moderate.
3. On its own draws, B's clean removal at 5-20 sigma is at least 70 percent. Moderate.
4. On real held-out masters, B keeps the starlet energy at 4 to 16 px in star-free regions within 5 percent of the
   input, which is the plan's "nebulosity held at parity". Moderate; read once the runner path scores a real master.

**Kill:** B under A on A's draws at 5-20 sigma, by more than either arm's seed range. Then the generator's gap
dominates, and the truth is fixed by D4 and a deeper plate builder instead.

Open from it, under #902: R0's saturated fit on a fixed annulus in FWHM units with a 0.1 to 0.3 core cut (#1240); a
residual table that holds bright stars' near wings (#1241); the Lagoon's and eta Carinae's photometric offsets (1.4 to 1.7,
a V against luminance colour term is the likely reading, #1242); the mono noise shape (#1243, calibrated 2026-10-04:
`--warp-sigma-mono`, 0 by default, gives 0.313 band1/band0 against seven mono half-master pairs' 0.290, where 0.5 gave
0.435; bilinear is the floor, the bake's Lanczos-3 sharper still, and the R1 training exports, made before it, carry 0.5 on
their 15 mono sessions, which R2a's 3-channel trainer leaves out); and theme C's validations,
R0 run on R1's draws, a noise-free twin draw and a zero-flux draw (#1244).

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
  A third way since 2026-10-06: a physical PSF draws spikes from the pupil's vanes with no spike model of its
  own, so they generalise to any vane count and width a profile gives ([physical-psf-model.md](physical-psf-model.md),
  P3, #1298).
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
