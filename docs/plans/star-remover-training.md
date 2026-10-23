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

Tracked by #1362.

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

### Colour, which the luminance cancels (2026-10-15)

Every test above reads the channels' mean, so a residue in one colour that another offsets cancels there. The owner saw
it by eye on R2i's draws ("a lot of dark blotches where the stars were") while every number read R2i as the best remover
(R2i's by-eye read, under R2 below). `StarRemovalEval` now reads each test per channel too (`ColourDetail` in the report).
Every measure above is unchanged: the R2f to R2i re-scores (`starless-eval-v5.json`) differ from their `v4` reports only
in the dug-in-sky test and the plate sources' near zone, both added by #1400's review after the v4 scorer was built.
- **Each channel in its own noise.** The noise plane is the noise of the channels' mean. A channel's sigma is that times
  its share, measured on the plate's own sky as the spread of neighbour differences in the channel over the same in the
  luminance. On A's draws, R, G and B read 1.34, 1.41 and 2.08 times the luminance's.
- **Clean and dug in colour.** A star is clean in colour when it is clean on the luminance and in every channel by the
  same two tests. It is dug in colour when it is dug on the luminance or in any channel. Each band also reads each
  channel's digs and its core's signed mean.
- **The footprint per channel.** Each channel's signed mean over the footprints, and its share of pixels more than
  2 sigma under the plate and over it, beside the luminance's share under it.

## 6. Experiments, in order

| Step | What | Cost | Decides |
|---|---|---|---|
| R0 | Classical starless plate builder (PSF-fit subtract at detections + multi-scale inpaint); residual report on ten masters | 2 to 3 days | H1 |
| R1 | Injector (third exporter mode) with the manifest of injected stars; the at-site control arm data | 1 to 2 days | H3 data |
| R2 | Gaussian vs Moffat injection, three seeds each; random vs at-site control; posted 1:1 comparison | 4 x 3 x ~11 min | H2, H3 |
| R3 | Self-refinement, two further generations | 2 x 3 x ~11 min plus plate rebuilds | H4 |
| R4 | Photometric gate on the stars plate | hours | H5 |
| R5 | Export, contract JSON, `OnnxTianWenStarRemover : IStarRemover` through `ChunkedNafnetRunner`, registered in `AddTianWenAi()` and answering `IEnhancerAvailability.Serves`, so `CanonicalProgram` can take the split without RC. Opt-in first (`--ai-backend tianwen`, `EnhanceBackend.TianWen`); once the bright tail passes the spot checks, Auto still prefers RC and the deferred `sxt` dispatcher falls back to the in-house remover where RC is not licensed | 2 days | Ships opt-in |

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
SMC and the Lagoon), which theme B's clip spread would also round (measuring that spread before the render changes is
#1482). The noise arm is unchanged, as it must be. Leo has been
refused by every R1 run, the first included: its plate fails the exporter's linearity test (median over minimum above
0.125 of full scale, the bright sky at about a third of it), so no R1 measure has covered it.

**The training exports (started 2026-10-04).** The pool is the store's train split less the three plates the exporter's
linearity test refuses (ZWO ASI294MM luminance, a bright sky: Leo Triplet 2022-03-26 and the Rosette 2021-12-31 and
2022-01-09, at 0.196, 0.176 and 0.138 against the auto-detect's 0.125; `PlateLinearityProbe`, 3 of 190; left out for
good, the owner's call 2026-10-07) and less one whole
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

What it leaves for R2b, the owner's choice: a fixed length, or a plateau patience long enough that no seed stops
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
arm on every axis in R2a, and its export carries R0's dotted sites ("What R0 leaves", #902). Six runs, about 15 hours, started once the E16c
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
- So a per-star term (the speckle teacher, or a core term scaled per star) is what would push harder on bright stars.
  (The convergence run below shows the L2 had not finished with them, only slowed.)

**3. Convergence: the cap was what held R2b back.** `r2cconv_random_s0` ended on its own at step 110,000, its
held-out best at 104,000. On the random arm's draws, against `r2b_random_s0` (the same seed at the 60,000 cap):

| | 60,000 steps | converged | registered |
|---|---|---|---|
| removed at 5-20 sigma (registered) | 64.4 | 73.4 | |
| clean at 5-20 sigma | 57.1 | **65.9** | (2) at most +5: **misses**, +8.8 |
| clean at 20-100 / 100-1000 sigma | 44.6 / 18.6 | 56.2 / 28.9 | |
| core residual at 100-1000 sigma, RMS | 4.26 | **2.24** | (3) down a fifth: **holds**, down 47 percent |
| footprint, median and 90th percentile tile | 0.73 / 1.29 | 0.64 / 1.09 | |
| sky moved (registered) / about its level | 0.52 / 0.51 | 0.40 / **0.38** | (4) above 0.4: **misses** |
| sky away from the plate's own sources | 0.32 | **0.22** | under the kill's 0.3 |

- **(1) holds:** it ended on its own, well under the 240,000 cap.
- **By the registration's own rule, (2) failing means every R2 so far was read early, and the next arms run to
  convergence.** R2d's arms take this run's schedule.
- **It corrects check 2.** The bright cores' share of the remaining loss is small at the cap (2.8 percent) and at
  convergence (2.3), yet their residual halved between the two: the L2 had not finished with them, it was slow.
- **R2a's kill still fires as registered** (73.4 under 80 at 5-20 sigma; the sky at 0.40 over 0.3). Read away from the
  plate's own sources, the sky passes.

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

#### D1's read: the real sky is noise where it is smooth, and turbulent where it is not (2026-10-06)

`tianwen dataset sky-texture` over all 190 plates of the 2026-09-29 store (169 colour, 21 mono, none failed;
`C:\temp\e2\sky-texture\sky-texture.jsonl`, summarised by `C:\temp\e2\sky_texture_summary.py`). Medians across the plates,
with the interquartile range:
- **What is read:** 84 percent of a frame (80 to 88), off the canvas ring, the frame's edge and the sources the plate kept,
  of which there are 1,430 a megapixel at 4 sigma (950 to 2,390).
- **The smooth class is 99.6 percent of the read sky.** Its scales' robust RMS over the 1 px scale's: 0.387 at 2 px, 0.222
  at 4, 0.144 at 8, 0.115 at 16, 0.113 at 32. The fine end is the master's correlated noise (a starlet's second scale
  reads 0.225 of its first on white noise, more on a demosaiced or drizzled master), and the flattening from 16 px up is
  the large-scale sky. Its index is 1.29 (0.99 to 1.59), between noise and turbulence because both are in it, and its
  tail past 5 sigma is 0.05 to 0.6 percent from 1 to 8 px.
- **The textured class** (106 plates hold 20,000 pixels or more of it) **reads an index of 3.04** (2.62 to 3.33), its
  energy rising with scale, with tails of 0.4 to 1.4 percent.
- **The strongly textured class** (57 plates) **reads 3.34** (3.17 to 3.72), with tails of 2.6 to 3.4 percent at every
  scale: a nebula's knots and edges.
- Both textured indices sit where turbulent emission and dust are found (Kolmogorov's 11/3 is 3.67), as the physics says.

**What it sets for D2.**
- **Smooth sky, almost all of it:** below about 4 PSF widths its scales are the master's noise, so the synthetic fine
  scales there are the master's noise shape alone, with no texture at all. That is the simplest generator and truly
  starless.
- **Textured sky:** fine scales drawn as a field of index 3.0 to 3.3 for its class, with tails matched per scale (sparse
  knots, D3).
- **The tails are an upper bound for the nebula alone.** The finder masks sources from 4 sigma, so the fainter stars are
  still in the 1 to 4 px tails.

#### D2 to D4 as built, and the arms started (2026-10-07)

- **D4, the control arm's mask** (`tianwen dataset plate-masks`, `StarRemovalMasks`): beside every draw, 0 on the sky within
  two PSF widths of a source the plate kept and 1 elsewhere, by the eval's own rule (`StarRemovalEval.PlateSourceZone`), an
  injected star's footprint still counted. Over the field export's 21,760 draws: 885,645 sources the plates kept, 2.57
  percent of the pixels inside the rim left out. The trainer packs them beside the existing cache
  (`n2n_smoke.py --prepare-keep`, `keep.u8`, so the 26 GB cache is not prepared again) and `--loss-mask` takes each draw's
  own into the pixel and band terms and the plateau's objective (`run-r2.ps1 -LossMask`).
- **D2 and D3, the background** (`SyntheticBackground`, `tianwen dataset degrade --background synthetic`): per session the
  plate's starlet scales from 4 PSF widths up are kept; per cell the finer ones are drawn as one log-normal field of index
  3.1 shared by the channels, each scale held to the plate's own local signal there, and the knots are added; the exporter
  puts the master's noise on it in its own shape and injects the stars into that. **The amplitude maps' first form left
  texture where faint stars had been**; read with the sources masked from 3 sigma, over at least 16 px and with a 20
  percent noise margin, the sources the eval finds the truth keeping fall, on one session's six cells, from the plate's
  12.4 a draw to 3.1 on a smooth field (the knots are about 2.5) and from 34.6 to 8.3 on eta Carinae's dense nebula, where
  the rest are the texture's own peaks.
- **Arm A** (today's plates, the mask, three seeds to convergence: `run-r2.ps1 -Tag r2da -Arms random -SeedCount 3
  -Patience 12 -Steps 240000 -LossMask`) started 2026-10-07 07:13. **Arm B's export** (`C:\temp\e2\run-r2d-export.ps1`,
  R1's pool, field arm, seed and noise shape, `--background synthetic`, into `D:\tianwen-scratch\r1-train-synth`) started
  08:41; its cache and training follow arm A on the GPU.
- *Interim, one model of six (2026-10-07; nothing is judged until both arms' seeds are in).* `r2da_random_s0` stopped
  on its own at 121,000 steps (its best held-out at 115,000; the unmasked convergence run stopped at 110,000). Against
  that run (`r2cconv_random_s0`; one seed each, so well inside R2b's seed spread of 15 to 18 points) on the random arm's
  draws with the fixed eval: clean removal 64.9 percent at 5-20 sigma (65.9), 53.3 at 20-100 (56.2) and 21.5 at
  100-1000 (28.9), with less digging there (33.8 percent dug against 47.2; the core's mean +0.93 sigma against -0.07,
  so it leaves bright cores rather than digging them). It takes more of the plate's own sources (16.3 percent against
  13.8) and moves the sky far from them more, not less (0.390 sigma RMS against 0.221; the registered sky RMS 0.620
  against 0.399). If the seeds hold it, the mask frees the model to take faint source-like structure, and it takes it
  from the star-free sky too.
- *Arm A's three seeds, and arm B's training (2026-10-08; still no B-against-A read).* The runs stopped on their own:
  A at 121,000, 112,500 and 85,000 steps (best held-out at 115,000, 106,500 and 79,000), B at 75,500, 67,500 and 80,000
  (best at 69,500, 43,500 and 68,000). B's seed 1 stalled: its best, 1.83e-5, came 24,000 steps before it stopped,
  against 1.16e-5 and 1.14e-5 for the other two (B's held-out loss is on its own draws, not comparable with A's). On A's
  draws, against the unmasked convergence run in brackets: clean removal 64.4 to 67.5 percent at 5-20 sigma (65.9), 53.3
  to 56.8 at 20-100 (56.2) and 21.5 to 26.1 at 100-1000 (28.9); dug at 100-1000 33.8 to 58.3 (47.2); the plate's own
  sources taken 16.3 to 23.1 (13.8); the sky far from them moved 0.115 to 0.390 sigma RMS (0.221). **The seeds do not
  hold seed 0's sky reading**: the unmasked run sits inside the masked arm's range, so the far sky is a seed's, and the
  mask's one effect all three seeds share is taking more of the plate's own sources. On B's synthetic draws the
  plate-trained models move the far sky 1.14 to 1.28 sigma and take 34 to 37 percent of the sources the eval finds the
  synthetic truth keeping (its knots and texture peaks): taught on plates, they remove the synthetic sky's own structure.
- **The generator against the Pleiades, by eye (2026-10-07).** `tianwen dataset synthetic-background` draws it over any
  starless plate beside the plate's own cutout (`SyntheticBackgroundPreview`). Over StarXTerminator's starless of the
  iTelescope M45 stack (RASA 11, FWHM 7.22 px, so 1 to 16 px drawn anew): on smooth sky it is the plate plus its knots,
  but on the reflection nebula it is wrong in kind. The real nebula's fine scales are long, smooth striations aligned
  over hundreds of pixels and a bright arc about 15 px wide; the synthetic texture there is isotropic and clumpy, a
  log-normal of the right energy and the wrong shape. D1 read only the spectrum's index and tails, which cannot see
  orientation. A star remover taught on it never sees a filament cross a star, which is the Pleiades' whole difficulty;
  the fix is an anisotropic texture whose orientation follows the plate's own coarse structure, measured first on the
  plates (the structure tensor's coherence per scale and class, beside D1's index).

#### R2d's read: every prediction misses, the kill does not fire, and B loses the bright stars on its own draws too (2026-10-08)

All six models through the fixed eval (`C:\temp\e2\score-r2d.ps1`, read by `C:\temp\e2\compare_r2d.py`), each arm's three
seeds as a range, the unmasked convergence run for reference:

| model, on whose draws | clean 5-20 sigma | 20-100 | 100-1000 | saturated | core RMS 100-1000 | far sky | plate sources taken |
|---|---|---|---|---|---|---|---|
| unmasked, A's | 65.9 | 56.2 | 28.9 | 21.5 | 2.2 | 0.221 | 13.8 |
| A, A's | 64.4 to 67.5 | 53.3 to 56.8 | 21.5 to 26.1 | 21.9 to 26.0 | 2.7 to 5.8 | 0.115 to 0.390 | 16.3 to 23.1 |
| B, A's | 50.5 to 67.5 | 32.8 to 44.2 | 5.9 to 8.4 | 8.7 to 11.0 | 6.7 to 19.1 | 0.246 to 0.269 | 20.4 to 34.6 |
| A, B's | 66.9 to 69.8 | 56.0 to 58.8 | 22.3 to 28.0 | 21.0 to 28.3 | 3.2 to 6.2 | 1.14 to 1.28 | 34.1 to 37.3 |
| B, B's | 51.3 to 70.1 | 33.6 to 45.7 | 5.5 to 8.5 | 7.3 to 8.2 | 7.0 to 18.7 | 0.169 to 0.271 | 14.9 to 17.1 |

Against the pre-registration:
1. **Misses.** On A's draws B's clean removal at 5-20 sigma is 60.0 against A's 65.6, 5.5 points under, where at least 5
   over was registered.
2. **Misses.** B moves the far sky 0.256 (0.246 to 0.269) against A's 0.246 (0.115 to 0.390): tighter, not lower.
3. **Misses.** On its own draws B cleans 61.9 (51.3 to 70.1), under the registered 70.
4. Unread: it waits on the runner path scoring a real master.

**The kill does not fire**: A's lead of 5.5 points is inside B's seed range, 17.0, which the stalled seed 1 widens.
Without that seed nothing changes (B 64.8, 62.1 to 67.5, against A's 65.6; A's lead 0.8 against a range of 5.4, (3) at
67.3).

What it says:
- **B learned the generator's sky, not the real one.** On its own draws it holds the far sky at 0.17 to 0.27 sigma,
  where the plate-trained models move it 1.1 to 1.3. On real plates it holds the sky no better than A.
- **B removes bright stars worse on its own draws as much as on A's**: 5.5 to 8.5 percent clean at 100-1000 sigma
  against A's 22 to 28 on B's draws, its core RMS two to four times A's. On B's own draws A beats B in every band but the
  sky, so the deficit is not a domain gap. Seed 0's error is unbiased scatter (core mean +0.1 sigma, 71 percent dug);
  seeds 1 and 2 leave the cores in (+3.8 and +9.1 sigma).
- **The cause is not measured.** Candidates:
  - D3's knots, labelled keep, teaching the net to leave compact bright things;
  - the texture taking the share of the loss the bright cores had;
  - B's earlier stops (67,500 to 80,000 steps against A's 85,000 to 121,000, each at its own plateau).

  R2c's split of where the squared error sits, run on B's models, tells the second from the other two, on the CPU.
- **That split rules the texture out** (read the same evening from the reports' own `Loss`, in the units the loss sees; an
  identity puts 43 percent in the bright cores). The trained A models leave 2.4 to 3.0 percent of their error in the
  bright cores, as R2c found. B's leave 8 to 28 percent (seeds 0 to 2) on its own draws as on A's, while the sky near
  and far from sources holds only 3 to 12 percent of it. In absolute terms, on its own draws B's bright-core error is 3.6
  to 26 times the mean of what A's models leave on those same draws. So the texture never crowded the cores out of B's loss. B
  stopped on its plateau with the cores still carrying the error, which leaves the knots and the stops.
- **The knots overlap the bright stars.** They peak 5 to 200 sigma, log-uniform, so 62 percent of them sit above 20
  sigma and 19 percent above 100. The round ones are 1.6 to 4 times the PSF's width, and a knot at 1.6 widths and 150
  sigma is close to a bright star, whose core a saturating stack widens anyway. B keeps the knots (it takes 15 to 17
  percent of what the synthetic truth keeps, the plate-trained models 34 to 37), and seeds 1 and 2 leave bright cores
  in. Telling the knots from the stops needs the GPU: B's seed 0 resumed with a longer patience (the stops), or an export
  whose knots stay out of the PSF's neighbourhood, at least 2.5 widths or under 20 sigma (the knots).

#### The stops or the knots, pre-registered (2026-10-08)

The owner chose both tests, the stops first. **The stops**: B's seed 0 trained again from scratch with a patience of 24
evaluations instead of 12 (`run-r2.ps1 -Tag r2dblong -Arms synth -SeedCount 1 -Patience 24 -Steps 240000`); the cache,
the seed, the cap, the four halvings and the trainer are B's (no change to `training/denoise` since B ran). A GPU run is
not bit-reproducible, so it is read against B's three seeds, not against seed 0 alone. Written before it runs, on A's
draws with the fixed eval:
- **The stops are the cause** if its clean removal at 100-1000 sigma reaches A's range (at least 21.5 percent) and the
  bright cores hold under 5 percent of its error.
- **They are not** if that removal stays under 10 percent (B's seeds read 5.9 to 8.4).
- **They are part of it** in between.

The knots test runs whatever this reads, unless the stops alone reach A's range. **The knots**: B's export again, with
every knot either at least 2.5 PSF widths across or peaking under 20 sigma (`tianwen dataset degrade --bright-knots-wide`,
`C:\temp\e2\run-r2dk-export.ps1`: B's sessions, cells, draws, noise shape and seed, into `D:\tianwen-scratch\r1-train-synthk`),
then one seed at B's patience of 12 (`run-r2.ps1 -Tag r2dk -Arms synthk -SeedCount 1 -Patience 12`), queued behind the stops
on the GPU. The option changes no knot's place, width or angle, only the brightness of the compact ones: 62 percent of
B's knots peaked above 20 sigma, under 20 percent do now. Written before it trains, on A's draws with the fixed eval and
against B's three seeds:
- **The knots are the cause** if its clean removal at 100-1000 sigma reaches A's range (at least 21.5 percent) and the
  bright cores hold under 5 percent of its error.
- **They are not** if that removal stays under 10 percent.
- **They are part of it** in between.
- Either way it keeps what B learned of the sky: on B's draws it moves the far sky within B's 0.17 to 0.27 sigma.

#### The stops and the knots read: each is part of it, and each repairs a different part (2026-10-09)

Both trained overnight, the stops to 180,000 steps (best at 168,000), the knots at B's patience. They were scored
through the fixed eval on A's draws and B's (`C:\temp\e2\score-r2dtests.ps1`, read by `C:\temp\e2\read_r2dtests.py`):

| on A's draws | the stops | the knots | A's seeds | B's seeds |
|---|---|---|---|---|
| clean 100-1000 sigma | 24.3 | 17.2 | 21.5 to 26.1 | 5.9 to 8.4 |
| bright cores' share of the error | 7.0 % | 3.0 % | 2.4 to 3.0 % | 7.5 to 27.9 % |
| core mean / RMS at 100-1000 | +1.07 / 6.4 | -0.54 / 3.7 | -0.20 to +0.93 / 2.7 to 5.8 | +0.11 to +9.07 / 6.7 to 19.1 |
| clean saturated | 17.4 | 22.4 | 21.9 to 26.0 | 8.7 to 11.0 |
| clean 5-20 sigma | 73.4 | 65.9 | 64.4 to 67.5 | 50.5 to 67.5 |
| far sky | 0.23 | 0.21 | 0.12 to 0.39 | 0.25 to 0.27 |

- **The stops are part of it.** Trained longer, B's model removes bright stars as A's do (24.3, inside A's range) but
  scatters them more (7.0 percent of its error, against the 5 registered). Its faint-star removal, 73.4 at 5-20 sigma, is
  the best of any model so far.
- **The knots are part of it.** With the compact knots faint the bright cores carry A's share of the error (3.0
  percent) and the saturated stars are removed as A removes them. Bright stars are removed short of A (17.2), dug rather
  than left (66 percent dug, core mean -0.54).
- **The knots test's sky misses.** On B's draws it moves the far sky 0.34 sigma, outside B's 0.17 to 0.27: with fewer
  bright knots in its targets it takes more of the sky's own faint structure.
- **What the 73.4 is not:** evidence that the synthetic background beats A. A never trained at a patience of 24, so the
  schedule and the background are confounded in it.

#### The two fixes together, against A at the same schedule, pre-registered (2026-10-09)

Written before either trains. **Combined**: the knots' export at a patience of 24 (`run-r2.ps1 -Tag r2dkl -Arms synthk
-SeedCount 1 -Patience 24`). **A's control**: today's plates with D4's mask at a patience of 24 (`run-r2.ps1 -Tag r2dal
-Arms random -SeedCount 1 -Patience 24 -LossMask`). One seed each, so a difference under A's three-seed range at 12 (3.1
points at 5-20 sigma) is not read. Both are scored on A's draws with the fixed eval.
1. A's control at a patience of 24 lifts A's own clean removal at 5-20 sigma to at least 70. If it does, the stops'
   73.4 is the schedule's. Moderate.
2. Combined reaches A's control at 100-1000 sigma (within 3 points) with the bright cores under 5 percent of its error,
   so both of B's losses are repaired at once. Moderate.
3. Combined against A's control at 5-20 sigma is R2d's prediction (1) at a matched schedule: at least 5 points over. Low
   to moderate.
4. **Kill**: Combined under A's control at 5-20 sigma by more than 5 points. Then the synthetic background costs faint
   stars at a matched schedule, and the truth is fixed by D4 and a deeper plate builder.

*Superseded (dated 2026-10-10, #1400): the next paragraph was written on 2026-10-09 from R2d's own read, before this
pair trained, and the read below contradicts it. It is kept as what was believed when the pair was registered.*

So the synthetic background as built does not help, and what it cost is the bright stars. That cost is B's training,
not the background's realism. R2e's structure is still worth having for the fill and for a later arm, but it would not
recover what B lost.

#### The two fixes together, read: a tie on faint stars, a gain on bright ones (2026-10-10)

Both models trained to their plateau at a patience of 24 under a 240,000-step cap (`r2dkl` 07:23 to 17:55 on 2026-10-09,
`r2dal` 17:58 to 02:14 the next morning) and were scored on A's draws with the fixed eval (`score-r2dmatched.ps1`):

| measure | A at 12, 3 seeds | A's control at 24 (`r2dal`) | combined at 24 (`r2dkl`) |
|---|---|---|---|
| clean 5-20 sigma | 64.4 to 67.5 | 73.0 | 71.4 |
| clean 20-100 sigma | 53.3 to 56.8 | 58.6 | 63.4 |
| clean 100-1000 sigma | 21.5 to 26.1 | 24.4 | 31.9 |
| dug 100-1000 sigma | 33.8 to 58.3 | 66.1 | 46.2 |
| core mean 100-1000 sigma | -0.20 to 0.93 | -1.00 | -0.07 |
| clean saturated | 21.9 to 26.0 | 29.2 | 26.9 |
| bright-core share % | 2.4 to 3.0 | 2.8 | 3.0 |
| far sky | 0.12 to 0.39 | 0.28 | 0.24 |

1. **Holds.** A's control reaches 73.0 at 5-20 sigma, so the stops' 73.4 was the schedule's, not the background's.
2. **Misses as registered, by over-reaching it.** The registration says "reaches A's control (within 3 points)", and
   read as written, two-sided, the scorer's MISSES is the verdict: combined is 7.5 points OVER A's control at 100-1000
   sigma, beyond A's three-seed range there (4.6), with the bright cores at 3.0 % of its error. This read first called
   it a hold by taking "reaches" as one-sided, a reading chosen after the numbers were in (#1400); the gain stands as a
   gain, but it is not the registered prediction. It also digs bright stars less (46 % of their error against 66 %) with
   a core mean near zero.
3. **Misses, and is not read.** Combined sits 1.6 points UNDER A's control at 5-20 sigma, against a registered 5 over, and
   inside A's three-seed range of 3.1.
4. **The kill does not fire.**

**One seed each, and the ranges are a patience of 12's** (#1400): A's three-seed spread (3.1 points at 5-20 sigma, 4.6 at
100-1000) was measured at a patience of 12, and both models here trained at 24. Nothing measures the seed spread at 24,
so every "within the range" above uses the 12's spread as a stand-in, and a difference near it is not a result.

So at a matched schedule the synthetic background, with its knots capped, costs no faint stars and removes bright ones
better than the plates do, and the rule under "R2e's background exported for the next arm" sends that export to training
next (the owner chose it over second seeds of this pair, 2026-10-10).

#### What the loss weighs, and the bar StarXTerminator sets (2026-10-07 and 08)

The owner asked why the training does not stop a model changing star-free sky, and whether synthetics can teach it to
leave no black dots. Both answers are in where the squared error sits.
- **The sky is in the loss, but it hardly drives the gradient.** The loss is L2 over the tile (less the 16 px rim) plus
  two band terms, so wherever no star was injected any change costs. Split by region, the stars' footprints carry 80
  percent of the squared error, the sky near the plate's own sources 14 and the sky far from every source 6. At low SNR a
  faint star, a noise peak and a small knot look alike, and the lowest-error answer partly removes anything star-shaped,
  which shaves the sky's own structure. Three ways to make it hold the sky: a leave-alone term (the star-free pixels
  weighted up, or a change in the fine bands penalised only where no star was injected, which a synthetic draw knows
  exactly); targets that show what to keep (D3's knots, R2e's filaments crossing stars); and a gate on the fixed eval's
  far-sky RMS.
- **The black dots are the bright cores' error, and the loss has finished with them.** Bright cores carry about 2.5
  percent of the trained models' squared error, with a core error of 2.2 to 2.7 sigma RMS, so a third to a half of them
  land under the sky: 47.2 percent of 100-1000 sigma stars are dug in the convergence run. Those stars are 3.5 percent of
  the eval's, and a quarter of the draws carry a saturated one. R0's own dots are not the cause: an injected star is placed
  at least 3 FWHM from every site R0 subtracted, so no target shows a star turning into a dot. What would teach it, in
  order: the speckle teacher (section 4; not built, #1362); a truth term on the injected footprints that only a
  synthetic draw allows (undershoot under the truth weighted above overshoot, or H5's flux term star by star); more bright
  stars a draw.
- **StarXTerminator, measured where it can be** (`tianwen image remove-stars` over the iTelescope stacks, crops and
  profiles in `C:\temp\rc-sxt-test`). On M45 (RASA 11, 46 frames), at the six largest saturated stars (4,000 to 4,700
  sigma): what it removed, scaled to its value 10 to 20 px out, reads 0.010 to 0.012 at 45 to 60 px around most stars
  (one 0.007), the clean-sky star 0.0102 inside that range and the two stars in thick nebula 10 to 30 percent higher at
  45 to 80 px, within the stars' own scatter: one halo shape, not nebula taken with it. No holes: round the two nebula
  stars its starless stays 10 to 14 sigma over the sky and rises toward the star, as reflection nebula does, and nowhere
  falls 5 sigma under the sky; the RASA's spikes go too. That shows it consistent, not right, since the true halo is
  unknown. On omega Centauri (ASI533MC) its starless is flat sky: it erases the cluster, unresolved glow included.

#### Two scenes no real master can give (the owner's direction, 2026-10-07)

Tracked by #1363.

A globular cluster or the Pleiades has no starless truth, so these are the cases synthetics are for. Both are next arms,
on whichever background R2d picks, beside the speckle teacher: the scenes put the training signal into the bright cores,
the teacher makes digging cost.
- **A globular cluster** (M13, omega Centauri, 47 Tuc): stars placed on a King profile with a realistic luminosity mix,
  overlapping, each drawn with the master's own PSF per channel, the core blended into a glow. What the truth keeps of the
  unresolved light is the owner's decision (section 8). The test fields with no truth: the denoiser's crowded set (omega
  Centauri, NGC 362, M22, M28) and M4, whose core glow and bar stars R0 already leaves.
- **A bright star in nebulosity** (the Pleiades): a star thousands of sigma bright, saturated over tens of pixels, with
  its far halo, spikes and ghosts where the optics make them, injected onto a plate with nebula. The truth is exact: the
  halo is the star's and goes, the nebula is the sky's and stays. Today's injector draws a Moffat at the master's width
  saturated through 8 simulated subs with no far halo, so this scene waits on R0's near wings (#1241) and the physical
  PSF's halo and spikes (#1296 to #1298), and on R2e's filaments to put under it.

Open from it, under #902: R0's saturated fit on a fixed annulus in FWHM units with a 0.1 to 0.3 core cut (#1240); a
residual table that holds bright stars' near wings (#1241); the Lagoon's and eta Carinae's photometric offsets (1.4 to 1.7,
a V against luminance colour term is the likely reading, #1242); the mono noise shape (#1243, calibrated 2026-10-04:
`--warp-sigma-mono`, 0 by default, gives 0.313 band1/band0 against seven mono half-master pairs' 0.290, where 0.5 gave
0.435; bilinear is the floor, the bake's Lanczos-3 sharper still, and the R1 training exports, made before it, carry 0.5 on
their 15 mono sessions, which R2a's 3-channel trainer leaves out); and theme C's validations,
R0 run on R1's draws, a noise-free twin draw and a zero-flux draw (#1244).

### R2e: the nebula's structure, for the background and the fill (the owner's direction, 2026-10-08)

The Pleiades preview (above) showed D2's texture right in energy and wrong in shape: the real reflection nebula's fine
scales are striations aligned over hundreds of pixels, ours are isotropic clumps. The same model serves twice, so it is one
model: the synthetic background a remover is taught on (a filament under a bright star is a target that keeps it), and
R0's fill (`HoleFill`, a push-pull pyramid plus grain), which today ends every filament at a hole's edge and leaves the
inside smooth and a little dark ("What R0 leaves": eta Carinae's Keyhole, the Trapezium). Physics before shape: dust
filaments run along the magnetic field, which is why a reflection nebula is striated; it brightens toward the star that
lights it; a dark lane absorbs, so it multiplies what lies behind it and ends sharply on one side; an ionisation front is
one-sided too. Turbulence (D2's log-normal) is right for the emission between them.

#### S1: measure the structure on the plates

D1's measure (`SkyTexture`, every plate of the store, per starlet scale and background class) gains the structure tensor
of each scale's coefficients over a window twice the scale: its **coherence** (`(l1 - l2) / (l1 + l2)`, energy-weighted
over the class's pixels, against the same read of white noise, which is not zero over a finite window), and its
**alignment** with the coarse structure the generator keeps (from `SyntheticBackground.FirstKeptScale` up, and never
closer than two octaves above the scale read, `SkyTexture.CoarseStart`: adjacent starlet scales share frequencies, and on
white noise 8 px against 16 px up read 0.23 before that rule): the mean of `cos 2(theta_fine - theta_coarse)` weighted by
both coherences, +1 where the fine structure runs along the coarse contours, -1 across them, 0 where they are unrelated,
read against white noise's (`Measurement.NoiseAlignment`). Pre-registered, before any plate is read:
1. On the textured and strongly textured classes, the coherence at 4 to 16 px is above white noise's by at least 0.1.
   Moderate: the Pleiades shows it, an emission nebula may not.
2. Where it is, the alignment is positive (fine structure along the coarse contours, so the plate's kept scales can steer
   the texture). Low to moderate.
3. On the smooth class neither departs from noise's by more than 0.05. High.

If (1) misses, the striations are a few fields' and the generator draws them per class without a coarse guide; if (2)
misses, the orientation is drawn rather than read.

#### S1's read: the nebula's fine structure runs along its coarse contours (2026-10-08)

All 190 plates (`C:\temp\e2\sky-texture-s1\sky-texture.jsonl`, from the commit that added the measure; summarised by
`C:\temp\e2\sky_structure_summary.py`). Medians across plates, with the interquartile range:

| class, plates | | 2 px | 4 px | 8 px | 16 px |
|---|---|---|---|---|---|
| textured, 106 | coherence over noise | +0.07 | +0.11 (+0.05 to +0.20) | +0.15 (+0.07 to +0.21) | +0.14 (+0.08 to +0.22) |
| | alignment | +0.13 | +0.30 (+0.19 to +0.39) | +0.38 (+0.14 to +0.49) | +0.43 (+0.26 to +0.55) |
| strongly textured, 57 | coherence over noise | +0.13 | +0.15 (+0.09 to +0.21) | +0.19 (+0.11 to +0.24) | +0.15 (+0.11 to +0.25) |
| | alignment | +0.27 | +0.38 (+0.26 to +0.51) | +0.43 (+0.14 to +0.56) | +0.32 (+0.14 to +0.52) |
| smooth, 190 | coherence over noise | +0.02 | +0.01 | +0.01 | +0.16 |
| | alignment | +0.02 | +0.04 | +0.10 | +0.26 |

White noise's alignment is -0.003 to -0.054 at these scales.
1. **Holds, narrowly.** The textured classes' coherence at 4 to 16 px is over noise's by +0.11 to +0.19 at the median,
   0.1 or more on 58 to 79 percent of the plates. The anisotropy is real but modest: no plate's sky is all striations.
2. **Holds, strongly.** Where it is, the fine structure runs along the coarse contours. The alignment is +0.30 to +0.43
   at the median, positive on 88 to 98 percent of the plates. The plate's kept scales can steer the texture.
3. **Misses.** The smooth class's coherence is noise's to 8 px but +0.16 over it at 16 px. Its alignment departs from
   noise's by more than 0.05 from 4 px, on 177 to 180 plates of 190.
   - Read afterwards, not registered: white noise's slightly negative alignment comes from its fine and coarse scales
     sharing one field. On a plate the coarse scales are the sky's own and the noise is independent of them, so 0 is
     the null that applies there.
   - Against 0, the smooth class is noise to 4 px and departs from 8 px (+0.10, then +0.26). That is the large-scale sky
     D1 found from 16 px up, faint enough to read as smooth, and it runs along the coarse shape as the nebulae do.

The null's alignment takes the coarse scales from two octaves up, while a plate starts no finer than the first scale the
generator keeps. The two agree from 4 px up, which is every scale the reads above rest on.

**What it sets for S2.** The generator draws the scales under its first kept one: 1 to 4 px on 147 plates, 1 to 8 px on
43. There the textured sky reads coherence +0.07 to +0.19 over noise and alignment +0.13 to +0.43, so the texture is
drawn steered along the plate's coarse orientation, with its anisotropy set per class from these numbers. The smooth
sky's alignment says the steering should follow the local coherence everywhere, not switch on with a class.

#### S2 and S3 as built: the steer, calibrated on emission nebulae, short of M45 (2026-10-08)

- **The steer** (`SyntheticBackground.Steering`, opt-in, so arm B's isotropic texture is unchanged). PowerLawField's
  spectrum, the same draws, goes through the isotropic window and eight angular ones (`cos^2m` about each direction). Per
  pixel the two directions either side of the plate's coarse orientation are blended. That is mixed with the isotropic
  field by a strength times the coarse coherence, and the mix's variance, read off the fields' covariances, divided out.
  The coarse orientation is S1's structure tensor of the kept scales' luminance. A wander turns the orientation drawn
  along by a smooth random angle. Pinned by `SyntheticBackgroundTests` on a plate whose coarse structure runs one way.
- **S3's harness** (`tianwen dataset synthetic-background --steer s,m[,w] --textured N --measure`): the most textured
  384 px cells of a plate, each read beside its synthetic by S1's measure. Read class by class by
  `C:\temp\e2\s3_summary.py`. The score is the mean distance between synthetic and plate, coherence over noise and
  alignment, at 2 to 8 px.
- **Calibrated on five eta Carinae plates** (8 cells each, 28 settings in three sweeps). The rule, fixed before the
  held-out plates were read, is the lowest mean of the textured and strongly textured classes' scores. It picked strength
  0.5, exponent 1, wander 0.8 rad, at 0.063 against the isotropic texture's 0.160.
  - The isotropic texture reads no alignment, where the plates read +0.15 to +0.42, and little coherence.
  - A steer with no wander overshoots the alignment (+0.6 to +0.9) before it reaches the plates' coherence, so the real
    fine structure is about as one-way as a strong steer makes it but follows the coarse contours less tightly.
  - Held out (Lagoon, Orion; Vela's cells hold too little textured sky to score): 0.061 against the isotropic 0.175.
  - Two faults remain. At 2 px the pick is too one-way (+0.11 coherence against the plates' +0.03): their anisotropy
    grows with scale, and one steer is the same at every scale. Over whole cells, mostly smooth sky, it is no closer
    than the isotropic texture (0.080 against 0.075).
- **M45 by eye misses** (StarXTerminator's starless of the iTelescope stack, the six centres of the first preview,
  `C:\temp\rc-sxt-test\m45-steered.png`). The steer gives a hint of streaking along the arc where the isotropic texture
  gives none. It is still clumpy where the reflection nebula is long smooth striations. In the two striated cells the
  plate's coherence at 4 to 16 px reads +0.41 to +0.58, the steered texture's +0.13 to +0.29. The calibration came from
  emission nebulae, whose fine structure is far less one-way, and one global setting cannot serve both. **Next (S2b):**
  steer by the plate's own fine-scale structure tensor where it holds signal, read as the amplitude maps are (sources
  masked, energy smoothed over the clean pixels), so its local coherence sets how one-way the texture is, with the coarse
  orientation only where the fine scales are noise. Then the same harness, and M45 again.
- **S2b as built** (`Steering.Fine`, the same evening). The luminance's replaced scales from 2 px up are read, each scale's
  gradient tensor over the clean pixels in units of its own noise. The noise is a low quantile of the smoothed trace; a
  median read the stripes of a half-striped test frame as noise. Where their signal matches the noise of the scales read,
  they set the orientation and a share of `Fine` times their coherence, and the coarse steer holds elsewhere. Pinned by a
  test whose fine stripes run across the coarse wave: the coarse steer turns the texture along the wave, the fine one
  across it, as the plate's stripes run.
  - **On M45 it gets the orientation and the one-wayness right at 4 to 16 px.** In the striated cell at (2663, 1359),
    exponent 2 and gain 1 read coherence +0.54, +0.56, +0.46 against the plate's +0.54, +0.58, +0.53. By eye the texture
    streaks along the arc.
  - **Two faults remain, both measurable.** At 1 and 2 px it is one-way where the plate is noise: +0.15 to +0.45 against
    +0.04 to +0.05. One steer serves every scale; a per-scale steer would follow the plate's own per-scale structure.
  - **The tails are wrong, and steering makes them worse.** On the smooth arc the plate's kurtosis is 1.0 to 1.75 at 2 to
    16 px, with 0.05 to 0.2 percent past 5 sigma. The isotropic texture reads 5 to 13 there and every steer 5 to 24, with
    2 to 7 percent past 5 sigma. The log-normal's clumps (`LogNormalSigma` 0.8 everywhere) are the dark mottling seen in
    the bright nebula, and a streak is sparser than a blob of the same variance.
  - It is not shot noise counted as signal: the plate's local pixel noise there, 1.6 to 2.9e-5, is its global 2.7e-5,
    since M45's nebula is about a fifth over its sky.
  - **Next:** the S3 score takes the tails (kurtosis and the share past 5 sigma per scale) beside coherence and alignment;
    the log-normal's width is set from the plate's own local tails rather than one constant; then the per-scale steer.
- **Tails from the plate, built and read (2026-10-09; `--tails-from-plate`, opt-in).**
  - **How it works:** per replaced scale from 2 px up, the luminance's local second and fourth moments over the clean
    pixels give the signal's kurtosis, with the noise's share taken out (Gaussian noise adds 6SN + 3N^2 to the fourth
    moment). A table of the texture's own kurtosis per width turns it into a width. Each drawn scale is held to unit local
    RMS, so the amplitude maps still set its strength.
  - **A bug found on the way:** the first table was read over the whole field, where the log-normal's envelope swings
    through its range (17 at width 0.8, 72 at 1.2). A plate is read over a window, so every plate mapped near zero. The
    table is read locally now. Pinned by two tests: the read recovers a texture's local kurtosis above the noise, and a
    smooth plate draws a smoother texture than the one width.
  - **M45:** the dark mottling is gone. Where the nebula is rough, the tails match the plate's (kurtosis 6.4/7.7/5.8/5.6
    against 6.5/5.7/4.0/4.1). Where it is a smooth arc, it is still too clumpy.
  - **Over the eight plates it does not beat the one width.** Read clear of the amplitude maps' 3 sigma sources, it drew
    the texture too smooth: eta Carinae's textured sky read 1.5 to 2.5 against the plate's 3.1 to 3.8. Read clear of the
    measure's 4 sigma instead, it is close there (3.0 to 4.4). Elsewhere it swings: the held-out textured sky reads 11.7
    to 16.3 against 3.5 to 4.5 with the fine steer, while the held-out strongly textured sky is closer (5.3 to 7.2 against
    about 10, where the one width reads 5).
  - **A confound:** the synthetic knots (D3) add tails of their own to every read. On M45 one elongated knot took a
    cell's kurtosis to 87. The texture's tails cannot be tuned against the plate's while the knots ride in both.
  - **Next:** S3 read with the knots left out, so the texture alone is compared, before the tails read is kept, damped,
    or replaced by a width per texture class.
- **The texture alone (`--no-knots`, the same evening): the knots hid the read's bias, and they overshoot.**
  - **The tails read draws the texture too smooth.** Without knots it reads kurtosis 1.0 to 1.8 at every scale and class,
    where the plates read 3 to 4 (about 10 on the held-out strongly textured sky). Its apparent match at 4 sigma was the
    knots' tails added to it. The read is parked; a round trip (a texture of known width, read back) would find its bias.
  - **The one width, texture alone, matches the emission nebulae's textured sky at 2 and 4 px:** 3.4 to 3.6 against
    eta Carinae's 3.1 to 3.3. It is too smooth at 8 px (2.0 against 3.8) and on the strongly textured sky (1.4 to 1.6
    against 2.5 to 4.5, held out 6.4 against 10.5).
  - **The knots overshoot.** With them the one width's textured sky reads 5.6 to 7.1 against the plate's 3.1 to 3.8:
    D3's knots, three a cell at 5 to 200 sigma wherever the cell is, add more tail than the real sky has. This is R2d's
    knots test seen from the sky's side (bright compact knots cost the bright stars).
  - **Kept:** the one width (`LogNormalSigma`) as the texture's tails.
- **Next: knots drawn as a nebula's, not as a cell's.** Each one should be:
  - placed where the plate's amplitude maps say there is nebula, not uniformly;
  - as bright as a few times the local texture, not 5 to 200 sigma of noise;
  - coloured as the plate is there, not equal in noise sigma per channel (which made them pink on M45, like satellite
    trails);
  - when elongated, laid along the local orientation (S2b's), not at a random angle.

  Judged by S3 with the knots in (the tails per class) and by R2d's knots test's reading of the bright stars.
- **The nebula knots as built** (`--nebula-knots` on `synthetic-background` and `degrade`, opt-in; D3's knots draw exactly
  as before).
  - **Placement:** a Poisson count of `KnotsPerCell` times the share of the cell that is nebula, each knot placed by
    rejection on the plate's fine-scale signal over the noise.
  - **Brightness and colour:** peaks of 2 to 8 times that signal's RMS, channel by channel.
  - **Shape:** elongated knots lie along the steer's contour.
  - **Tests:** none on a noise-only sky, the peaks a few times the local signal and in its colour, and the elongated
    ones along the contour.
  - **On M45 by eye:** no knots on empty sky (D3's put pink blobs there) and no pink trails.
  - **Over the eight plates** the tails score's mean over the eight views falls from 0.282 with D3's knots to 0.230. It
    is closer in five of eight views, worse in two, and most of all where D3's overshot (eta Carinae's textured sky,
    unsteered: kurtosis 2.8 to 4.3 against the plate's 3.1 to 3.8, where D3's read 5.6 to 7.1).
- **Every S3 preview so far drew its own texture.** A cell's seed was `HashCode.Combine`, which .NET seeds afresh in every
  process, so the S2b build no longer reproduced its own cell. Every setting was compared on different random draws:
  - the sweeps' medians over 40 to 64 cells are fair but noisier than they looked;
  - a close call is within that noise (the steer's pick, 0.063, against 0.071);
  - the M45 panels set two different textures side by side.

  The exporters were never affected: they derive their seeds by fixed arithmetic, which the preview now does too
  (`SyntheticBackgroundPreview.CellSeed`, pinned by a test that fails in any process if `HashCode` comes back). Every
  setting's texture is now the same draw, so a knots comparison differs in the knots alone.
- **The knots compared on paired draws (the same texture in every setting; `C:\temp\e2\s3_paired.py`).**

  | tails score, lower is closer | D3's knots | no knots | nebula knots |
  |---|---|---|---|
  | mean of the eight views | 0.266 | 0.253 | 0.231 |

  - **The nebula knots are the closest in five views of eight**, by most on eta Carinae's textured sky, unsteered: 0.096
    against D3's 0.310, kurtosis 3.4/4.3/3.2 against the plate's 3.4/3.1/3.9 and D3's 5.3/5.7/6.5.
  - **They are never far behind:** D3's are closer on eta Carinae's textured sky under the fine steer (0.145 against
    0.174), and all three are level on the held-out strongly textured sky. The structure scores are level in every view.
  - **They are the knots of the next synthetic export.**
  - **How noisy the unpaired runs were:** the same texture alone read 3.4 to 3.6 at 2 and 4 px there, and reads 4.2 to
    6.0 on these draws.
- **What the knots cannot fix: the strongly textured sky's tails are short whatever the knots.** Kurtosis 1.6 to 3.0
  against eta Carinae's 3.4 to 4.9, and about 5.5 against the held-out 10.5. That is the texture's one width (0.8),
  light for that class. **Next:** a heavier width for the strongly textured class, read off the plate's own class map
  (SkyTexture's), calibrated on the harness the same way. Deferred: the class is a small share of the sky (57 plates of
  190 hold 20,000 px of it), and S4 is the owner's reason for R2e.

#### S4, pre-registered: the fill as a conditional draw (2026-10-09)

R0's fill (`HoleFill`) is a push-pull level plus the plate's grain, so a hole on a nebula comes out smooth: every
filament ends at its edge.

**The method.** S4 adds the steered texture as a conditional draw:
- draw the texture T over the hole and its surroundings (the generator's own amplitude maps and orientation, read with
  the holes masked, so both come from the sky around the hole);
- fill the hole with push-pull(data) + (T - push-pull(T)).

T minus its own push-pull is zero at the edge and the full texture deep inside, so the drawn structure fades in from
the real sky, scales under the hole's size only, along the orientation round it. It is opt-in: R0's plates are unchanged
until it is chosen.

**The measure.** Judged on R0's own probe (`StarlessFillProbe`: discs of 3, 6, 12 and 24 px cut where the plate has no
star, filled, compared with what was there), extended by two reads:
- the band energy inside the hole at each scale under its size, against the original's;
- the alignment between the fill's fine structure and the ring round the hole (S1's measure).

Written before it is built, on the plates' structured holes:
1. **Energy:** S4's fill holds 75 to 125 percent of the original's band energy at 2 to 8 px, where today's holds under
   half (its grain ratio sits under one). Moderate.
2. **Level:** its mean residual stays within today's plus 0.1 sigma; the draw adds no bias. High.
3. **Price:** its structure error (filled against original, noise taken out) rises no more than the square root of two
   times today's on structured holes. A random texture of the right energy costs that much and no more; it buys
   realism, not prediction. On smooth holes it stays within 0.1 sigma of today's. Moderate.
4. **Continuity:** the fill's fine structure aligns with the ring round it at +0.2 or more, where today's reads no
   structure to align. Low to moderate.

**Kill:** (2) or (3) misses. Then the fill stays the push-pull and the texture serves the synthetic background only.

#### S4's read: right in level and direction, over the price, and the kill fires (2026-10-09)

**As built.** `TexturedHoleFill`: R0's fill, then each hole's draw from a generator read off the sky round the holes.
Pinned by a test on broadband filaments: in the holes' interiors the push-pull keeps 0.04 of their energy, the draw
1.09, running their way.

**The probe.** `tianwen dataset fill-probe` runs R0's probe twice on the same holes, today's fill and the draw: the
fine steer, 100 holes a radius on the eight S3 plates, `C:\temp\e2\s4\fill-probe.jsonl`, read by
`C:\temp\e2\s4_summary.py`. It gained a read of a hole's interior, where the hole's own edge is out of the scale's
kernel; read at every hole pixel, a smooth fill's edge against the structure was 3.5 times that structure's energy.
Medians over the plates:

| | 3 px | 6 px | 12 px (read at 2 px) | 24 px (read at 4 px) |
|---|---|---|---|---|
| structure error, structured holes | 0.28 -> 0.53 | 0 -> 0 | 0 -> 0 | 0.25 -> 0.65 |
| structure error, smooth holes | 0 -> 0 | 0 -> 0.12 | 0.29 -> 0.35 | 0.15 -> 0.15 |
| mean residual, structured holes | -0.03 -> -0.12 | +0.08 -> +0.03 | -0.08 -> -0.06 | +0.08 -> +0.11 |
| energy in the interior, of the original's | | | 0.30 -> 1.59 | 0.14 -> 1.16 |
| alignment with the ring (the truth's own) | | | +0.37 -> +0.58 (+0.54) | +0.60 -> +0.87 (+0.89) |

1. **Energy: holds at 24 px, misses at 12 px.** It overshoots there (1.59), where today's fill holds a third.
2. **Level: holds at every radius.** The draw adds no bias.
3. **Price: misses.** The structure error roughly doubles on the structured holes of 3 and 24 px, against the square
   root of two allowed, and the smooth holes of 6 px rise 0.12 against 0.1.
4. **Continuity: holds.** The fill carries the sky's direction into the hole as far as the sky's own carries.

**The kill fires.** R0's fill stays the push-pull, and the texture serves the synthetic background. Two things to read
beside it:
- **The square root of two was the floor:** a random texture of exactly the right energy, uncorrelated with what was
  there, costs that and no less, so any overshoot misses. Passing would take a fill that predicts the hidden structure
  (patch-based, or one that continues a filament's own pixels), not one that only has its statistics.
- **The 24 px read rests on few holes:** most plates hold 0 to 2 structured holes of that size.
- **Two things this read did not say (#1400).** Energy was read at 2 and 4 px only (the 12 px holes at 2, the 24 px at
  4), not at the 2 to 8 px registered. And (3)'s bar, the square root of two, is the EXPECTED error of a perfect draw of
  the right energy, uncorrelated with what was there, so even an exact texture misses it about half the time: the bar
  was a coin toss. Its re-registration, and the read again with the generator fixed, are under "#1400's review" below.

What the draw buys, the right energy and direction with no bias, is realism rather than accuracy. Whether a user-facing
AI-free remover wants that is a product question, the owner's.

#### R2e's background exported for the next arm (2026-10-09)

`degrade --steer` takes the steer into an export (one parser for the three verbs that take one). The export is arm B's
sessions, cells, draws, noise shape and seed again, with:
- the fine steer (0.5,2,0.8,1);
- the nebula knots;
- the knots test's cap on compact knots.

It is written to `E:\tianwen-scratch\r1-train-synth2` by `C:\temp\e2\run-r2ds-export.ps1`, from the build of the commit
that added the option. Whether an arm trains on it waits on R2d's matched-schedule read: if A beats the two fixes at
the same schedule, the synthetic background is set aside and this export with it.

#### R2e's arm, pre-registered (2026-10-10)

Written before it trains. **R2e**: the export above at a patience of 24, one seed, as the knots arm trained
(`run-r2.ps1 -Tag r2ds -Arms synth2 -Export E:/tianwen-scratch/r1-train-synth2 -ExportLog C:/temp/e2/r2ds-export.log
-SeedCount 1 -Patience 24 -Steps 240000 -Scratch E:/tianwen-scratch`, from a snapshot of `training/denoise` at main, the
pair's schedule and cap), scored on A's
draws with the fixed eval beside `r2dkl` and `r2dal`. A difference under A's three-seed range (3.1 points at 5-20 sigma,
4.6 at 100-1000) is not read.
1. **The bright stars stay repaired**: R2e at 100-1000 sigma at least A's control (24.4), the bright cores under 5 % of
   its error. The compact-knot cap is in this export too. Moderate.
2. **The steer costs no faint stars**: R2e at 5-20 sigma within 3.1 of A's control (73.0). Moderate.
3. **The steer moves the sky no more than the knots arm**: far sky at most `r2dkl`'s 0.24 plus A's spread about its
   median (0.13). Low to moderate.
4. **Kill**: R2e under A's control by more than 5 points at 5-20 sigma, or by more than 4.6 at 100-1000. Then the steered
   texture costs what the knots repaired, and the synthetic background stays the knots export.

#### R2e's read: the steer gains at every brightness, and (2) misses by over-reaching (2026-10-10)

`r2ds_synth2_s0` trained to the 240,000-step cap with its held-out objective still falling (best at 235,500, four
halvings), as the knots arm did (best at 239,000); A's control converged on its own at 178,500. Scored on A's draws by
the binary that scored the matched pair (`C:\temp\e2\score-r2ds.ps1`, `read_r2ds.py`, `C:\temp\e2\r2ds-read.txt`):

| measure | A, 3 seeds at 12 | A's control `r2dal` | the knots `r2dkl` | R2e `r2ds` |
|---|---|---|---|---|
| clean 5-20 sigma | 64.4 to 67.5 | 73.0 | 71.4 | **79.6** |
| clean 20-100 sigma | 53.3 to 56.8 | 58.6 | 63.4 | **70.0** |
| clean 100-1000 sigma | 21.5 to 26.1 | 24.4 | 31.9 | **46.3** |
| dug 100-1000 sigma | 33.8 to 58.3 | 66.1 | 46.2 | 42.2 |
| core RMS 100-1000 | 2.7 to 5.8 | 3.0 | 5.9 | **1.8** |
| clean saturated | 21.9 to 26.0 | 29.2 | 26.9 | **37.9** |
| bright-core share % | 2.4 to 3.0 | 2.83 | 2.96 | **1.59** |
| far sky | 0.12 to 0.39 | 0.28 | 0.24 | 0.29 |

1. **The bright stars: holds.** 46.3 at 100-1000 sigma against A's control's 24.4, the bright cores 1.6 % of its
   error.
2. **The faint stars: misses as registered, by over-reaching.** It was registered as "within 3.1 of A's control (73.0)",
   and R2e reads 79.6, 6.6 above. The scorer read it one-sided (no more than 3.1 below), the same slip the #1400 review
   corrected on the matched read's point (2), and it is recorded here as a miss. What the prediction was written to
   guard, that the steer costs no faint stars, holds.
3. **The sky: holds.** Far sky 0.29 against the knots arm's 0.24 plus A's spread (0.37).
4. **The kill does not fire.**

**Against the knots arm, on the same schedule** (both at the cap): +8.2 points at 5-20 sigma, +6.7 at 20-100, +14.3 at
100-1000, +11.0 on saturated stars, the bright cores' share of the error halved and their RMS a third. Each gain is past
A's three-seed range at its band (3.1 points at 5-20 sigma, 4.6 at 100-1000), the only seed spread measured. It beats
A's control at every brightness, where the knots arm tied it on the faint stars.

Read with it:
- **One seed**, and the spread at a patience of 24 is unmeasured: patience 12's stands in.
- **Its training data predates #1400's fixes**: its texture held 1.2 to 1.9 times the plates' energy at 1 to 8 px, and
  F7's seam. Whether the fixed generator trains as well is untested; the gain may rest partly on texture that is too
  strong.
- **The synthetic arms stopped at the cap, A's control at its own convergence**, so against A the comparison is each
  arm's best within the cap, not the same number of steps.
- **The eval and the export predate F6 and F8**: no count of digs in the sky's noise, and the near zone that leaves half
  a kept star in the loss.

What would turn this into a decision, the owner's to choose among: R2e's seeds 1 and 2 (its spread at a patience of 24,
which every read since the matched pair has lacked); an export from the fixed generator, trained the same way; and the
bar StarXTerminator sets ("What the loss weighs, and the bar StarXTerminator sets").

#### R2e's second seed and R2f, pre-registered (2026-10-10)

Written before either trains (the owner's goal for the night: meaningful training). Both on the 1070, one after the other,
by R2e's own runner and snapshot of the trainer (`C:\temp\e2\r2ds-run`), patience 24, cap 240,000; both scored on A's
draws by the binary that scored the matched pair and R2e, so every number below compares with every number above.

**R2e's spread: `r2ds_synth2_s1`.** R2e's export, recipe and schedule again, seed 1 (`run-r2.ps1 -Tag r2ds -Arms synth2
-SeedCount 2`, which skips seed 0's checkpoint). It is the first second seed at a patience of 24, so it measures the spread
every read since the matched pair has borrowed from patience 12.
1. **The gain is not the seed's**: seed 1 beats A's control at 5-20 sigma (73.0) and at 100-1000 (24.4). Moderate.
2. **Patience 12's spread holds at 24**: seed 1 within 3.1 points of seed 0 at 5-20 (79.6) and within 4.6 at 100-1000
   (46.3), either side. Low to moderate.
3. **Kill**: seed 1 under A's control at either band. Then R2e's read was its seed, and nothing past it is read until a
   third seed is in.

**R2f: R2e's recipe on the fixed generator.** An export from main as it stands after #1400's fixes (`synth3`): R2e's
command again, the same sessions, cells, draws, noise shape and seed, the fine steer (0.5,2,0.8,1), the nebula knots and the
compact-knot cap, so the one change is the generator's texture (its width on a fixed grid, its energy at the plate's, the
steer's seam; "#1400's review" below). `run-r2.ps1 -Tag r2f -Arms synth3 -SeedCount 1`, after R2e's seed 1. The band below
is the larger of A's three-seed range at 12 and R2e's own two-seed spread, once seed 1 is read.
1. **The bright stars stay repaired**: R2f at 100-1000 sigma at least A's control (24.4), the bright cores under 5 % of its
   error. Moderate.
2. **The fix costs R2e nothing**: R2f no more than the band below R2e's seed 0 at 5-20 (79.6) and at 100-1000 (46.3).
   One-sided by design, written so now: R2f above R2e is a pass, not a miss. Low to moderate: R2e's texture held 1.2 to 1.9
   times the plates' energy, and some of its gain may have been that.
3. **The sky**: far sky at most 0.37, R2e's bar. Moderate.
4. **Kill**: R2f under A's control by more than 5 points at 5-20 sigma or 4.6 at 100-1000. Then the gain rested on the
   too-strong texture, and the fixed generator does not train as well as the one it replaced.

#### R2e's second seed read: the faint gain holds, and two seeds at 24 lie 12.9 points apart on bright stars (2026-10-11)

`r2ds_synth2_s1` ran to the cap, as seed 0 did (best at 233,000 against 235,500), and was scored at 09:21 by the same
binary on A's draws (`C:\temp\e2\score-r2e-s1-r2f.ps1`, `read_r2e_s1_r2f.py`, `C:\temp\e2\r2e-s1-r2f-read.txt`):

| measure | A's control | the knots | R2e seed 0 | R2e seed 1 |
|---|---|---|---|---|
| clean 5-20 sigma | 73.0 | 71.4 | 79.6 | **77.7** |
| clean 20-100 sigma | 58.6 | 63.4 | 70.0 | **71.6** |
| clean 100-1000 sigma | 24.4 | 31.9 | 46.3 | **33.4** |
| dug 100-1000 sigma | 66.1 | 46.2 | 42.2 | 58.1 |
| core RMS 100-1000 | 3.0 | 5.9 | 1.8 | 5.4 |
| clean saturated | 29.2 | 26.9 | 37.9 | 35.2 |
| bright-core share % | 2.83 | 2.96 | 1.59 | 2.29 |
| far sky | 0.28 | 0.24 | 0.29 | 0.22 |

1. **The gain is not the seed's: holds.** Seed 1 beats A's control at 5-20 sigma (77.7 against 73.0) and at 100-1000
   (33.4 against 24.4).
2. **Patience 12's spread holds at 24: misses.** On faint stars the two seeds lie 1.9 points apart, inside 3.1; on bright
   stars 12.9 apart, nearly three times the 4.6 registered, and every bright measure moves with it (dug 42 to 58, core RMS
   1.8 to 5.4).
3. **The kill does not fire.**

What it changes:
- **R2e's faint-star gain is reproduced**: +4.7 and +6.6 points over A's control, +6.3 and +8.2 over the knots arm.
- **Its bright-star gain over A's control holds in both seeds, at a size the seeds do not pin**: +9.0 and +21.9.
- **Against the knots arm on bright stars it is no longer read**: seed 0's +14.3 was the lucky side, seed 1 is +1.5,
  inside the spread now measured. "R2e's read" above said each gain was past A's three-seed range; on bright stars that
  range was patience 12's and too narrow for 24.
- **Every single-seed bright-star difference since the matched pair is within this spread**, A's control's own included
  (one seed at 24): the matched pair's +7.5 and the stops' and knots' bright reads are not read as differences until
  their arms have a second seed.
- **R2f's band**, as registered, is now 3.1 points at 5-20 sigma (A's range, the larger) and 12.9 at 100-1000 (R2e's two
  seeds).

#### R2f's read: the fixed generator costs R2e nothing, and every run of it ends at the cap (2026-10-11)

`r2f_synth3_s0` ran to the cap: the trainer's own last line is "reached the 240000-step cap BEFORE the held-out objective
converged (best 2.683049e-06 at 239000, 4 halvings)". It was scored at 20:45 by the same binary on A's draws, after a first
attempt failed: the C: clean-up that evening had deleted `C:\temp\tianwen-scratch\r1-train`, and with it `random`, the
fixed eval export every score since R2a reads. Its eight val sessions were re-exported with `run-r1-train.ps1`'s own
binary (591a2c39) and arguments (`C:\temp\e2\restore-r1-train-random-val.ps1`; the plates were last written before the
original export ran), and re-scoring R2e seed 1's stored outputs against the restored export reproduced its stored report
with 0 differences, so the numbers below compare with every number above.

| measure | A's control | R2e seed 0 | R2e seed 1 | R2f |
|---|---|---|---|---|
| clean 5-20 sigma | 73.0 | 79.6 | 77.7 | **76.8** |
| clean 20-100 sigma | 58.6 | 70.0 | 71.6 | **68.9** |
| clean 100-1000 sigma | 24.4 | 46.3 | 33.4 | **43.3** |
| dug 100-1000 sigma | 66.1 | 42.2 | 58.1 | 46.1 |
| core RMS 100-1000 | 3.0 | 1.8 | 5.4 | 1.9 |
| clean saturated | 29.2 | 37.9 | 35.2 | 35.6 |
| bright-core share % | 2.83 | 1.59 | 2.29 | 1.63 |
| far sky | 0.28 | 0.29 | 0.22 | 0.25 |

1. **The bright stars stay repaired: holds.** 43.3 at 100-1000 sigma against A's control's 24.4, the bright cores 1.6 %
   of the error.
2. **The fix costs R2e nothing: holds.** 2.9 points under R2e's seed 0 at 5-20 sigma (band 3.1) and 3.0 under it at
   100-1000 (band 12.9).
3. **The sky: holds.** Far sky 0.25, under R2e's 0.37.
4. **The kill does not fire.**

What it changes:
- **The fixed generator stays.** R2f lies inside R2e's two-seed spread on every measure in the table, so #1400's fixes
  (the texture's width on a fixed grid, its energy at the plate's, the steer's seam) cost nothing measurable, and R2e's
  gain did not rest on the too-strong texture.
- **It is one seed.** On bright stars R2f sits between R2e's two seeds (46.3 and 33.4), and that spread is the size of
  the effect being read, so no bright-star difference between R2f and R2e is read here.
- **The cap now binds every run of this recipe.** R2e's seed 0 found its best at 235,500, its seed 1 at 233,000 and R2f at
  239,000, all within the last 7,000 of 240,000 steps, and R2f was still improving at the end. What the recipe reaches is
  therefore not yet measured, only what it reaches in 240,000 steps.

#### R2g: R2f run to convergence, pre-registered (2026-10-12)

Written before it trains (the owner's goal for the day: get training going again). Tracked by #1338 with the rest of
R2e's line. One run: R2f again, seed 0, from the same runner and trainer (`C:\temp\e2\r2ds-run`, identical to R2f's
snapshot `scripts-r2f`) on the same `synth3` cache (moved byte for byte from E: to `C:\temp\tianwen-scratch\r2-synth3`
on 2026-10-11), with the cap raised from 240,000 to 480,000: `run-r2.ps1 -Tag r2g -Arms synth3 -SeedCount 1 -Patience 24
-Steps 480000`. Under the plateau schedule the cap only bounds the loop, so the run retraces R2f to step 240,000 and goes
on at R2f's last rate (1.25e-5, its fourth halving at step 213,000) until 24 scores pass without a 0.1 % gain. It is scored
by the same binary on A's draws and set against R2f's column above. **One seed against itself**, so the 12.9-point spread
between seeds on bright stars does not enter: every difference read here is the steps past the cap and nothing else.

**The question.** What R2f's recipe reaches, which its read left open.

**Predictions.**
0. **It is R2f to step 240,000**: every held-out line through 240,000 equals R2f's log to the printed digits, as R2c's
   convergence run equalled R2b's at 10,000, 30,000 and 60,000. High. If not, it is read as a second seed against R2e's
   band, and nothing below is read as a gain from length.
1. **It ends on its own before 480,000 steps.** Moderate to high: the best held-out window fell 3.9 % in the 38,000
   steps from 201,000 to 239,000 (2.791e-6 to 2.683e-6), a rate that cannot hold a 0.1 % gain per 12,000 steps for long.
2. **The held-out best falls 0.5 to 5 % below R2f's 2.683e-6.** Moderate.
3. **Length buys little removal**: clean removal within 2 points of R2f at 5-20 sigma (76.8) and at 20-100 (68.9).
   Low to moderate. The precedent cuts the other way: R2c's convergence run gained 8.8 points at 5-20 sigma past R2b's
   cap. But R2b stopped at 60,000 steps on its third rate, and R2f has already spent 27,000 steps on its last.
4. **The bright stars do not get worse**: clean 100-1000 sigma at least 41.3 (R2f 43.3), the bright cores under 2 % of
   the error (R2f 1.63 %). Moderate.
5. **The sky**: far sky at most 0.37, R2e's bar. Moderate.

**Kill:** the held-out objective improves while clean removal falls by more than 3.1 points at 5-20 sigma or 4.6 at
100-1000. Then the plateau's objective and the removal it is meant to teach part past 240,000 steps, its convergence is
not the stopping rule for this task, and the next arm keeps R2f's length.

**What it decides.** If (3) holds, 240,000 steps is the recipe's length within what the eval reads and the cap stops being
a question; R2f stands as the recipe's number. If (3) fails upward, every arm since R2e was read short, as R2c found of
R2b, and the next arms run to convergence at a raised cap.

#### R2g's read: the length past the cap buys no faint or mid-bright removal, and every prediction holds (2026-10-12)

`r2g_synth3_s0` ended on its own: "stopped at step 273000: the held-out objective's best was 2.612369e-06 at step 261000",
still at R2f's last rate (1.25e-5), with no halving past R2f's fourth. It was scored at 21:16 by the same binary on A's
draws (`C:\temp\e2\r2g-read.txt`, `score-r2g.ps1`).

| measure | A's control | R2e seed 0 | R2e seed 1 | R2f | R2g |
|---|---|---|---|---|---|
| clean 5-20 sigma | 73.0 | 79.6 | 77.7 | 76.8 | **75.4** |
| clean 20-100 sigma | 58.6 | 70.0 | 71.6 | 68.9 | **70.1** |
| clean 100-1000 sigma | 24.4 | 46.3 | 33.4 | 43.3 | **48.8** |
| dug 100-1000 sigma | 66.1 | 42.2 | 58.1 | 46.1 | 31.5 |
| core mean 100-1000 | -1.00 | -0.42 | -0.93 | -0.48 | 0.06 |
| core RMS 100-1000 | 3.0 | 1.8 | 5.4 | 1.9 | 1.8 |
| clean saturated | 29.2 | 37.9 | 35.2 | 35.6 | 37.0 |
| bright-core share % | 2.83 | 1.59 | 2.29 | 1.63 | 1.50 |
| far sky | 0.28 | 0.29 | 0.22 | 0.25 | 0.26 |

0. **It is R2f to step 240,000: holds.** All 480 held-out lines through 240,000 equal R2f's log to the printed digit, so
   every difference below is the 33,000 steps past the cap and nothing else.
1. **It ends on its own before 480,000: holds.** At step 273,000, 12,000 steps after its best at 261,000.
2. **The held-out best falls 0.5 to 5 % below R2f's: holds.** 2.612e-6 against 2.683e-6, 2.63 % below.
3. **Length buys little removal: holds.** 75.4 against 76.8 at 5-20 sigma (-1.4) and 70.1 against 68.9 at 20-100 (+1.2),
   both inside the registered 2 points.
4. **The bright stars do not get worse: holds.** 48.8 at 100-1000 sigma (bar 41.3), the bright cores 1.50 % of the error.
5. **The sky: holds.** Far sky 0.26, under 0.37.

**The kill does not fire**: the held-out objective improved and neither clean measure fell past its band.

What it decides, by the registered rule: **(3) holds, so 240,000 steps is the recipe's length for faint and mid-bright
stars, and R2f stands as the recipe's number.** The cap stops being a question for them.

What it shows that was not registered, and so is read as a hypothesis, not a result: **the steps past the cap went to the
bright stars.** Clean at 100-1000 sigma rose 5.5 points, the dug share there fell from 46.1 to 31.5 (the lowest of any
column here), and the core's mean error went from -0.48 to 0.06, the nearest to zero of any column here. One seed, and
R2e's two seeds lie 12.9 points apart on this measure, so a seed's luck can be this size; but here the seed is the SAME,
so it is not a seed's luck, only a length whose effect on another seed is unmeasured. An arm that wants the bright stars
registers it: two seeds to convergence against R2f's two at the cap.

**The data under the next arm changes.** The star detector now triggers against the local sky (#1545), which every
session's measurements, registration and so masters move with (nine training sessions keep 427 subs against 416). A
store re-baked with it and an export made from that store are new inputs, so the next arm's control is re-run on them
rather than read against the columns above.

#### R2h: does the length past the cap buy the bright stars on a second seed? Pre-registered (2026-10-12)

Written before it trains, on the owner's word to train something that does not wait for the store's re-bake: this run
reads only the `synth3` cache, which no re-bake touches, and runs on the GPU while the re-bake runs on the CPU. Tracked by
#1338. One run: R2f's recipe on **seed 1**, the cap at 480,000, from the same trainer and cache as R2f and R2g:
`run-r2.ps1 -Tag r2h -Arms synth3 -FirstSeed 1 -SeedCount 1 -Patience 24 -Steps 480000 -SnapshotAt 240000`. The one
change to the trainer only writes a file: `--snapshot-at 240000` saves the weights as they stand at step 240,000 beside
the run's own output. With no gate the output is the last step's weights, so the snapshot is what a run capped at
240,000 saves, i.e. **R2f's recipe on seed 1**; checked before launch on 10 against 20 steps of seed 1, the snapshot at
10 equal to a run capped at 10 in every tensor (the weights at 20 differ). Both models are scored by the same binary on
A's draws; the run's own pair (its snapshot and its end) is the comparison, so every difference read is the length past
the cap on a seed that is not R2g's.

**The question.** R2g's unregistered finding: past the cap the bright stars gained (clean at 100-1000 sigma +5.5, dug
46.1 to 31.5) while faint and mid-bright removal stayed put. One seed; is it the recipe or seed 0's trajectory?

**Predictions** (each set against the run's own snapshot at 240,000):
1. **It ends on its own before 480,000.** Moderate to high: R2g ended at 273,000.
2. **The held-out best falls 0.5 to 5 % below the best window at or before 240,000.** Moderate: R2g 2.63 %.
3. **The bright stars gain**: clean at 100-1000 sigma at least 2 points above the snapshot, AND the dug share there at
   least 5 points below it (R2g +5.5 and -14.6). Low to moderate: it is the hypothesis, read on one seed.
4. **Faint and mid-bright removal stays put**: clean at 5-20 and 20-100 sigma within 2 points of the snapshot (R2g -1.4
   and +1.2). Moderate.
5. **The sky**: far sky at most 0.37 for both models, R2e's bar. Moderate.

**Kill** (as R2g's): the held-out objective improves while clean removal falls by more than 3.1 points at 5-20 sigma or
4.6 at 100-1000. Then convergence is not the stopping rule for this task, and the next arm keeps R2f's length.

**Read, not predicted:** the snapshot against R2f, two seeds of the fixed generator at the cap. R2e's two seeds lay 12.9
points apart on bright stars, so any difference there is read as the seed spread on this recipe, nothing more.

**What it decides.** If (3) holds here as on seed 0, the recipe's length is its convergence, not the cap: the next arms
run to convergence at a raised cap, and R2g is the recipe's number for bright stars. If (3) fails, R2f's length stands
at every brightness and R2g's bright-star gain is read as seed 0's.

#### R2h's read: the bright stars gain past the cap on the second seed too, so the recipe's length is convergence (2026-10-13)

`r2h_synth3_s1` ended on its own: "stopped at step 276000: the held-out objective's best was 2.545240e-06 at step
264000". Both models were saved (`r2h_synth3_s1_at240000.pt`, the snapshot, and `r2h_synth3_s1.pt`) and scored at 11:48
by the same binary on A's draws (`C:\temp\e2\r2h-read.txt`, `score-r2h.ps1`). It trained beside the store's re-bake,
which shares no input with it.

| measure | R2f (seed 0, cap) | R2g (seed 0, end) | R2h seed 1, cap | R2h seed 1, end |
|---|---|---|---|---|
| clean 5-20 sigma | 76.8 | 75.4 | 78.6 | 78.1 |
| clean 20-100 sigma | 68.9 | 70.1 | 70.6 | **73.2** |
| clean 100-1000 sigma | 43.3 | 48.8 | 41.9 | **51.9** |
| dug 100-1000 sigma | 46.1 | 31.5 | 50.4 | 37.9 |
| core mean 100-1000 | -0.48 | 0.06 | -0.78 | -0.45 |
| core RMS 100-1000 | 1.85 | 1.78 | 1.77 | 1.68 |
| clean saturated | 35.6 | 37.0 | 34.7 | 41.1 |
| bright-core share % | 1.63 | 1.50 | 1.60 | 1.47 |
| far sky | 0.25 | 0.26 | 0.27 | 0.28 |

1. **It ends on its own before 480,000: holds.** At step 276,000, 12,000 steps after its best, as R2g did (273,000).
2. **The held-out best falls 0.5 to 5 % below the best by 240,000: holds.** 2.545e-6 against 2.612e-6, 2.56 % below
   (R2g 2.63 %).
3. **The bright stars gain: holds, by more than on seed 0.** Clean at 100-1000 sigma +10.0 (41.9 to 51.9) and the dug
   share -12.6 (50.4 to 37.9), against the registered +2 and -5 (R2g +5.5 and -14.6).
4. **Faint and mid-bright removal stays put: misses, upward.** 5-20 sigma -0.4, inside the band; 20-100 sigma +2.6,
   0.6 past it.
5. **The sky: holds.** Far sky 0.27 at the snapshot and 0.28 at the end.

**The kill does not fire.**

**Read, not predicted: the two seeds at the cap.** Seed 1's snapshot against R2f: clean +1.8, +1.7 and -1.4 at 5-20,
20-100 and 100-1000 sigma, dug +4.3. On this generator the two seeds at the cap lie within 2 points on clean removal at
every brightness, against R2e's 12.9 on the old one.

What it decides, by the registered rule: **(3) holds on both seeds, so the recipe's length is its convergence, not the
cap.** The next arms run to convergence at a raised cap. The length goes to the bright stars on both seeds: clean
+5.5 and +10.0, dug -14.6 and -12.6. Faint removal stays put on both (-1.4 and -0.4). Mid-bright removal gains on
both (+1.2 and +2.6), which seed 1 takes past the 2 points (4) allowed, so (4) is read as a gain, not a cost. Unregistered
and on one seed each: saturated stars gain 1.4 and 6.4 points past the cap.

**The next arm's control** is this recipe run to convergence on the store re-baked with the local-sky star detector
(#1545) and an export made from it, since every number above was read on the old store's export.

#### R2i: R2f's recipe at convergence on the re-baked store, pre-registered (2026-10-14)

Written before it trains, on the owner's word to start GPU work once the re-bake ends. Tracked by #1338. The store
`D:/Astro-Dataset/2026-10-12-full` is the 09-29 store's sessions baked again from main with the local-sky star trigger
(#1545), the count-plane crop rule (#1536), the CALSTAT guard (#1539) and the `BAD_` marks of 2026-10-12 (Leo, M42
RGB, HD 71272), the 09-29 split kept (`--split-from`). One run, built by a chain that starts when the bake ends:

- **Plates:** `dataset starless-plates` over the new store's session masters, from a binary at main (`b80bff16`).
- **Export:** R2f's export command exactly (`run-r2f-export.ps1`'s arguments, the same 138 sessions in chunks of 10,
  `--seed 1`), on the new store and its plates, into `synth4`. A listed session the new store no longer holds is
  dropped and named in the chain's log.
- **Training:** `run-r2.ps1 -Tag r2i -Arms synth4 -SeedCount 1 -Patience 24 -Steps 480000`, seed 0, R2f's trainer and
  train/val split, run to convergence as R2h's read decided.
- **Scored** on A's draws by the same binary as every column above. The EVAL does not change, so R2i reads against R2g
  directly.

**The question.** Does the re-baked store change what the recipe learns? Against R2g (seed 0, the old store, at
convergence) every difference is the store's, nothing else.

**The bar is the seed spread at convergence**, R2g against R2h's end (seed 0 against seed 1): 2.7 points clean at 5-20
sigma, 3.1 at 20-100, 3.1 at 100-1000, and 6.4 dug at 100-1000. A store change smaller than that cannot be read on one
seed.

**Predictions** (against R2g):
1. **It ends on its own before 480,000.** Moderate to high: R2g ended at 273,000 and R2h at 276,000.
2. **The store changes nothing readable**: clean removal within 2.7 points at 5-20 sigma, 3.1 at 20-100 and 3.1 at
   100-1000, the dug share within 6.4 points. Moderate. The masters the detector changed most register more subs (nine
   training sessions 427 against 416 in the A/B), which is a few percent of their depth.
3. **The sky**: far sky at most 0.37. Moderate.

**Kill:** clean removal falls by more than the spread at any brightness (2.7, 3.1, 3.1). Then the new store's data
costs the recipe something, and it is not adopted as the line's store until that is found.

**What it decides.** If (2) holds, the re-baked store is a drop-in: the next arms train on it with R2i as their control.
If clean removal RISES past the spread, the detector fix improved the training data, read on one seed until a second
confirms it.

#### R2i's read: the re-baked store gains at every brightness on seed 0, and past both old seeds on bright stars (2026-10-15)

`r2i_synth4_s0` ended on its own: "stopped at step 216500: the held-out objective's best was 2.262201e-06 at step
204500". It was scored at 15:51 by the same binary on A's draws (`C:\temp\e2\r2i-read.txt`). It trained once,
uninterrupted, for 12.5 hours (2026-10-15, 03:17 to 15:50), on 113 training and 8 validation sessions, 4,520 and 320
cells, with the 15 mono sessions skipped: R2f's split and counts.

**How it got there.** Two things differ from the chain the pre-registration describes, neither in what trains:
- **The run was relaunched before it trained.** The first chain took its membership from the store's session ledger
  (`stats/sessions.jsonl`), which keeps one row per night and no pier-side ids, so it exported 99 of the 138 listed
  sessions: all 38 pier halves and one renamed night were missing. That partial export was set aside untrained
  (`synth4.partial99-2026-10-14`), and the chain was relaunched with its membership from the PSF store
  (`stats/psf-sessions.jsonl`), which names the halves. All 138 were exported, in R2f's order and chunks of 10. The
  relaunched chain was killed twice mid-export and resumed chunk by chunk under a scheduled driver; one chunk finished
  after its chain died and was recorded from its own log, whose summary reports no failure.
- **One night was renamed when its filter was identified.** The Uranus-C (IMX585) Lagoon and Trifid night of 2023-08-09
  was filed under `Unidentified-HaOIII` when R2f exported it and is now under `Optolong-L-Ultimate-3nm`. It was
  exported under its new name and the training list (`arms/r2-train.txt`) changed to match, so it trains on the same
  side of the split as in R2f.

**What "the store" covers.** The export and plate code is the same source in R2f's binary and R2i's. The local-sky
trigger reaches the training data twice: through the masters it registers, and through the plates, whose classical
remover measures each master's PSF with the same detector. So every difference below is the re-baked store and the
plates made from it, as the chain built them; nothing in the recipe moved.

| measure | R2g (seed 0, old store) | R2h (seed 1, old store) | R2i (seed 0, re-baked store) |
|---|---|---|---|
| clean 5-20 sigma | 75.4 | 78.1 | **79.2** |
| clean 20-100 sigma | 70.1 | 73.2 | **74.5** |
| clean 100-1000 sigma | 48.8 | 51.9 | **60.2** |
| dug 100-1000 sigma | 31.5 | 37.9 | 28.7 |
| core mean 100-1000 | 0.06 | -0.45 | -0.20 |
| core RMS 100-1000 | 1.78 | 1.68 | 1.78 |
| clean saturated | 37.0 | 41.1 | 46.6 |
| bright-core share % | 1.50 | 1.47 | 1.27 |
| far sky | 0.26 | 0.28 | 0.22 |

1. **It ends on its own before 480,000: holds.** At step 216,500, 12,000 steps after its best, earlier than R2g
   (273,000) and R2h (276,000).
2. **The store changes nothing readable: misses, upward, at every brightness.** Clean +3.9 at 5-20 sigma (bar 2.7),
   +4.4 at 20-100 (bar 3.1) and +11.5 at 100-1000 (bar 3.1); the dug share -2.8, inside its 6.4.
3. **The sky: holds.** Far sky 0.22.

**The kill does not fire**: clean removal rises at every brightness.

**Read, not predicted.** R2h's end is the better old-store seed on clean removal at every brightness. Against it R2i
is +1.1 and +1.3 at 5-20 and 20-100 sigma, inside the spread, and +8.4 at 100-1000 with the dug share 9.1 lower, past
it. So on bright stars R2i is past both old-store seeds, while on faint and mid-bright stars its gain over seed 0 is no
larger than the two old seeds' own difference. Saturated stars clean 46.6 % against 37.0 and 41.1; the core stays level
(mean -0.20, RMS 1.78) and the bright-core share falls to 1.27 %. The held-out objective is not compared with R2g's,
since its validation cells are cut from the re-baked masters.

**What it decides**, by the registered rule: clean removal rose past the spread, so the detector fix improved the
training data, read on one seed until a second confirms it. The firm part is the bright stars, which clear the spread
against both old-store seeds; the faint and mid-bright gains clear it only against the same seed. Nothing holds the
re-baked store back as the line's store, since the kill guards only a fall: the next arms train on it with R2i as
their control, and R2g and R2h stay the old store's numbers. The confirmation is R2i's recipe on seed 1, from the same
`synth4` cache, not yet pre-registered.

#### R2i by eye: the blotches are one colour, and every model has them (2026-10-15)

The owner looked at R2i beside R2g and R2h on the eval's own draws: crops of stars drawn at random and a blink comparator
over three draws, also drawn at random (`C:\temp\e2\r2i-look.py`). Their read: "R2i seems to have more leftovers than
R2g", "a lot of dark blotches where the stars were", and on the third tile R2h beat R2i. Measured over every draw
(`C:\temp\e2\r2i-blotch.py`, then the eval's colour measures, section 5):
- **On the luminance R2i digs no more than the others.** The share of footprint pixels 2 sigma under the plate is
  0.99 % for R2i, 0.99 % for R2g, 1.05 % for R2h and 1.15 % for R2f. The third tile does read R2h ahead (2.66 % of its
  footprint 2 sigma over the plate for R2i, 1.43 % for R2h, RMS 1.40 against 1.20). That is one draw of 1,280; R2i is
  the darkest of the three on 243 draws, R2h on 700.
- **In colour every model digs a channel on most bright stars:**

| clean in colour / dug in colour | R2f | R2g | R2h | R2i |
|---|---|---|---|---|
| 20-100 sigma | 33.9 / 52.3 | 40.5 / 38.8 | 40.1 / 43.2 | 43.7 / 38.7 |
| 100-1000 sigma | 7.9 / 85.7 | 12.6 / 73.1 | 13.2 / 76.9 | 21.2 / 63.3 |
| saturated | 10.0 / 84.0 | 15.1 / 74.0 | 15.5 / 80.4 | 19.6 / 63.9 |

- **R2i is the best of the four in colour too, and its dig is green.** Its bright cores sit at -0.28, -0.56 and
  +0.14 sigma in R, G and B, where R2g's sit at -0.22, +0.12 and -0.01 and R2h's at -0.84, -0.13 and -0.13. Low in
  green and high in blue reads as a dark magenta on screen. Over the footprints R2i's share 2 sigma under the plate is
  the lowest of the four in red and blue (1.68 % and 0.25 %) and the second highest in green (1.24 %, R2g 0.99 %).
- **The re-baked store does not move the training data's colour.** Over the 121 colour sessions both exports hold,
  `synth4` over `synth3`: the linear background's G/R and B/R 1.000 (within 0.3 %), the stretched background level
  1.000 in every channel (within 1 to 2 %), each channel's noise share 1.005, 0.992 and 1.000, and the injected stars'
  G/R and B/R 0.996 and 1.003. The stars' colours were reshuffled session by session (32 to 37 sessions moved more than
  5 %, against 2 to 11 between two halves of one export), but both ways (`C:\temp\e2\r2i-store-colour.py`).

What it changes: R2i's read stands on the measures it was registered on, and R2i also leads in colour. The line has a
defect every model shares: a bright or saturated star leaves a channel under the plate in 63 to 86 percent of cases, and
the luminance hid it. The next arms register their colour measures beside the luminance's. Whether R2i's green dig is
its seed's or the store's is what R2i's second seed reads.

#### R2j: each channel's error weighed by its own noise, pre-registered (2026-10-15)

Written before it trains, on the owner's word to train a model that respects the colour defect. Tracked by #1338.

**Why the loss, not the data.** On their own export's validation draws (`synth4` for R2i, `synth3` for R2g, scored by
the same colour eval), both models dig exactly as on A's draws. R2i's bright cores sit at -0.31, -0.56 and +0.10 sigma
in R, G and B there, against -0.28, -0.56 and +0.14 on A's draws, so the dig is the model's, not a gap between its
training and the eval. The digs fall in the channels the eval reads as quietest: at 100-1000 sigma R2i digs red on
34 % of stars and green on 48 %, blue (the noisiest, 2.08 times the luminance's sigma against 1.34 and 1.41) on 13 %.
The trainer's L2 weighs an error alike in every channel, in the tile's units; the eval and the eye weigh it against
its channel's noise.

**The change, one variable.** `n2n_smoke.py --channel-noise` (`channel_weights`; `run-r2.ps1 -ChannelNoise`) divides
each channel's error in the pixel and band terms, and the plateau's held-out objective, by that channel's share of the
noise. The share is read on the truth tile as ColourDetail reads it (neighbour-difference spread in the channel over
the luminance's), and the shares are divided by their RMS over the channels. A tile of equally noisy channels keeps
R2i's objective exactly, and only the channels' weights against each other move. A per-pixel sigma was tried first and
set aside: in each pixel's own sigma, the worst 1 % of a converged model's pixels hold 66 % of the objective, so it
would also have re-weighted bright cores against the sky. Checked before launch: on a tile of known noise 0.02, 0.01
and 0.03 the weights come back within 0.5 %; 1,000 steps of the recipe run at R2i's speed with the held-out objective
falling (5.3e-4 to 2.8e-4).

**The run.** R2i exactly but for the switch: the same `r2-synth4` cache, seed 0, R2f's trainer and split, patience 24,
cap 480,000: `run-r2.ps1 -Tag r2j -Arms synth4 -SeedCount 1 -Patience 24 -Steps 480000 -ChannelNoise`. Scored on A's
draws by the colour eval (`starless-eval-v5.json`), against R2i.

**The bars.** Luminance: the seed spread at convergence as R2i registered it (2.7, 3.1 and 3.1 points clean at 5-20,
20-100 and 100-1000 sigma). Colour, the same pair of seeds (R2g against R2h) read by the colour eval: 1.9, 0.4 and 0.6
points clean in colour, and 1.9, 4.4 and 3.8 dug in colour, at 5-20, 20-100 and 100-1000 sigma. One pair of seeds is a
thin estimate, as it was for the luminance.

**Predictions** (against R2i):
1. **It ends on its own before 480,000.** Moderate to high.
2. **The colour digs fall**: dug in colour at most 34.3 % at 20-100 sigma and at most 59.5 % at 100-1000 sigma (R2i's
   38.7 and 63.3, less the spread). Moderate: it is the hypothesis.
3. **The digs move toward blue**: at 100-1000 sigma, red's and green's digs each under R2i's (34.3 % and 47.7 %), blue's
   over R2i's 12.6 %. Low to moderate: it checks the mechanism.
4. **Luminance removal holds**: clean within the spread of R2i's at every brightness. Moderate.
5. **The sky**: far sky at most 0.37.

**Kill:** clean removal on the luminance falls by more than its spread at any brightness. Then weighing the channels
costs what R2i gained, and it is not adopted.

**What it decides.** If (2) and (4) hold, the channel weighting joins the recipe and R2j is the next arms' control. If
(2) fails, the dig is not the loss's channel weighting. Then the next candidate is the net's input: it is told one
noise plane for three channels, and a plane per channel needs an export that writes them.

#### R2j's read: the weighting moves the dig into blue and costs the bright stars; the kill fires (2026-10-16)

`r2j_synth4_s0` ended on its own: "stopped at step 204500: the held-out objective's best was 3.750105e-06 at step
180500". It was scored at 16:57 on A's draws by the colour eval (`C:\temp\e2\r2j-read.txt`, `read_r2j.py`).

**The run was started twice.** The first, launched 2026-10-15 at 21:29, reached step 64,600 when the desktop rebooted
at 00:35 on 2026-10-16, and its scheduled task started it again from step 0 after the next logon. That copy was
stopped a minute in, and the run was launched again at 08:00 with `--checkpoint-every 2000 --resume` (#1602, #1603),
which changes nothing trained, at normal priority. It then ran unbroken to its end, so the weights are one continuous
run from step 0.

| measure | R2g | R2h | R2i | R2j |
|---|---|---|---|---|
| clean 5-20 / 20-100 / 100-1000 sigma | 75.4 / 70.1 / 48.8 | 78.1 / 73.2 / 51.9 | 79.2 / 74.5 / 60.2 | 79.5 / **66.2** / **27.4** |
| dug 100-1000 sigma | 31.5 | 37.9 | 28.7 | **66.5** |
| clean in colour 5-20 / 20-100 / 100-1000 sigma | 56.8 / 40.5 / 12.6 | 58.7 / 40.1 / 13.2 | 57.3 / 43.7 / 21.2 | 61.4 / 42.4 / 9.5 |
| dug in colour 20-100 / 100-1000 sigma | 38.8 / 73.1 | 43.2 / 76.9 | 38.7 / 63.3 | 47.7 / 84.5 |
| dug R / G / B at 100-1000 sigma | 42.6 / 47.6 / 22.3 | 49.1 / 46.8 / 26.3 | 34.3 / 47.7 / 12.6 | 44.1 / 52.3 / **56.1** |
| core R / G / B at 100-1000 sigma | -0.22 / +0.12 / -0.01 | -0.84 / -0.13 / -0.13 | -0.28 / -0.56 / +0.14 | -0.52 / -0.54 / -1.04 |
| clean saturated | 37.0 | 41.1 | 46.6 | 25.6 |
| far sky | 0.20 | 0.19 | 0.19 | 0.20 |

1. **It ends on its own before 480,000: holds.** At step 204,500, 24,000 steps after its best.
2. **The colour digs fall: misses, the wrong way.** Dug in colour 47.7 % at 20-100 sigma and 84.5 % at 100-1000, against
   the registered 34.3 and 59.5 at most (R2i 38.7 and 63.3).
3. **The digs move toward blue: half holds.** Blue's dug share at 100-1000 sigma rose from 12.6 % to 56.1 %, but red's
   and green's rose too (34.3 to 44.1, 47.7 to 52.3) where the registration asked them to fall.
4. **Luminance removal holds: misses.** Clean -8.3 points at 20-100 sigma and -32.8 at 100-1000, against bars of 3.1;
   +0.3 at 5-20.
5. **The sky: holds.** Far sky 0.20.

**The kill fires**: clean removal on the luminance falls past the spread at 20-100 and 100-1000 sigma, so the channel
weighting is not adopted.

**Read, not predicted.** Clean in colour at 5-20 sigma rose 4.1 points (61.4 against 57.3, past the 1.9 spread), the one
gain. The weights did what they were built to do to the objective: blue, the noisiest channel at 2.08 times the
luminance's sigma against 1.34 and 1.41, weighed about 0.4 of red's per unit error. The model learned to leave blue
under the plate at the bright cores (-1.04 sigma), and that dig is large enough to take the luminance under too, which
is where the bright stars' clean removal went.

What it decides, by the registered rule: (2) fails, so the dig is not the loss's channel weighting. Turning one
channel's weight down hands that channel the dig instead of curing it. The next candidate is the net's input: it is
told one noise plane, the channels' mean, for three channels, so it cannot see which channel is noisier at a pixel. A
plane per channel needs an export that writes them. R2i stays the line's control.

#### R2k: a real star injected whole, its lobes beside it, pre-registered (2026-10-16)

Written before it trains, on the owner's agreement of 2026-10-16 to this arm ahead of the per-channel planes, after the
real masters were measured. Tracked by #1338.

**What the real masters show** (the 201 catalogues of the 2026-10-12 store, `C:\temp\e2\real-master-crowding.py`,
`bright-satellites.py`, `bright-satellites-2.py`, `lobe-directions.py`, `lobe-claims.py`):
- **The injector's count is the master's.** A master holds a median 316 Subtracted stars over 5 sigma per 256 px cell
  (p10 69, p90 710), 18 over 100 sigma and 1.8 saturated; a test tile asks for the cell's own sites times 1.1, the
  margin.
- **One real bright star is not one Moffat.** 52 % of the 100 to 1000 sigma stars have a Subtracted neighbour within 4
  px, and 94 % of those neighbours are second-pass rows (found on the first pass's residual) at a median 8 % of the
  star's significance. They point one way in 123 of 200 sessions (Rayleigh z over 20; the mean resultant length's median
  0.15, up to 0.82), radially on some trains (a cosine of +0.6 to +0.7 against the direction from the frame centre on the
  SV605CC L-Quad nights, -0.5 to -0.67 on the ASI533 L-Ultimate ones), often in another colour (red's share +0.47 to
  +0.58 on the SV605CC nights), and fade against their star (7.3 % of it at 100 to 200 sigma, 2.3 % past 1000; a log-log
  slope of 0.46, part of it the 5 sigma floor). So they are the star's shape, the optics', with some real close
  companions among them.
- **The injector draws them as stars.** They are 32.4 % of its pool (3.65 M of 11.25 M rows; 40.5 % of the 5 to 20 sigma
  ones), placed at random as stars of their own, while every injected bright star is one round profile.
- **Crowding cannot be judged from the catalogue.** Without the second pass, a real bright star has a neighbour under 4 px
  for 1.5 % of them against 22.3 % for the same stars at random positions: the builder cannot see a pair that close.
- **The quiet validation master is inside the training range**, at its bottom: 1.8 % of the training cells are quieter
  than its draws (0.00093).

**The change, one variable.** `dataset degrade --star-lobes` (`InjectionPopulation.Build(lobes: true)`): a second-pass
row whose nearest brighter first-pass row lies within `LobeReachFwhm` (2) field FWHM is that star's. It leaves the
amplitude pool and the cell's count, and is injected beside its star at the offset and with the amplitudes it was fitted
with, flagged `Lobe`. The reach is where the excess ends: the median lobe is 1.71 FWHM out, and past 2 FWHM a second-pass
row's brightness against its neighbour's stops changing. The eval scores a lobe as part of its star: never a site of its
own, its light on the footprints, and its star not clean while it is left. On lobe-free draws the eval is unchanged: R2i's
outputs on A's draws re-score byte for byte as the v5 report.

**The pre-check, run before this was written.** Two twins of A's draws, from the same 09-29 store, plates, seed and
arguments by the lobe branch's binary: PLAIN, without the switch, gives A's draws back byte for byte (tiles, plates and
noise planes; the rows add a `Lobe` field), and LOBED, with it. They hold 7 of A's 8 sessions: the Orion night's master
in that store was baked again after A's draws, and its old plate no longer fits it (4151 against 4155 px). In the lobed
twin 35.2 % of the 100 to 1000 sigma stars carry a lobe, and a draw a median 46 lobes. R2i on both, by the same eval:

| R2i | plain twin | lobed twin |
|---|---|---|
| clean 5-20 / 20-100 / 100-1000 sigma | 79.6 / 75.1 / 61.2 | 79.8 / 75.1 / 61.0 |
| dug 100-1000 sigma | 29.6 | 30.0 |
| clean in colour 100-1000 sigma | 22.5 | 19.7 |
| dug in colour 100-1000 sigma | 63.6 | 66.6 |
| clean / dug saturated | 52.1 / 23.7 | 49.7 / 32.4 |

So the lobes cost a model that never saw one almost nothing on the luminance (0.3 points clean on bright stars) and 2.8
points clean in colour on bright stars, where the lobe's other colour shows. That bounds what this arm can win on
injected stars, and it is small: the arm is run because its data are the real stars' and the owner chose it, not on the
promise of a large number.

**The run.** Export: R2i's export command exactly (the 2026-10-12 store and its plates, R2f's 138 sessions with the PSF
store's ids, chunks of 10, `--seed 1`, the synthetic background, its steer and knots) plus `--star-lobes`, into `synth5`,
by the lobe branch's binary (`tianwen 9.0.0+ebcab93a`, main plus this change), started the night of 2026-10-16 before
the PR merged. Training: R2i's recipe on it, `run-r2.ps1 -Tag r2k -Arms synth5 -SeedCount 1 -Patience 24 -Steps 480000
-CheckpointEvery 2000`, once a GPU slot is free (R2l and R2i's seed 1 hold it first). Scored on A's draws and both twins.

**The bars** are R2l's, the larger of the old-store and the same-store seed spreads.

**Predictions** (against R2i):
1. **It ends on its own before 480,000.** Moderate to high.
2. **On the lobed twin, colour gains at the bright stars**: clean in colour at 100-1000 sigma at least 20.3 % (R2i's 19.7
   plus the colour spread 0.6). Low to moderate: the pre-check leaves 2.8 points to win.
3. **On A's draws, the luminance holds**: clean within the spread of R2i's at every brightness. Moderate: the faint pool
   loses its lobes (40 % of its 5 to 20 sigma rows), so the faint stars it trains on are fewer and the real ones.
4. **The sky**: far sky at most 0.37.

**Kill:** clean removal on the luminance falls by more than the spread at any brightness on A's draws. Then the lobes cost
the recipe more than they bring, and the switch stays off.

**What it decides.** If (2) and (3) hold, `--star-lobes` joins the export recipe, the injected stars being the real ones.
If (3) holds and (2) misses, it is a draw: the switch costs nothing and buys nothing measurable, and is adopted only if a
real master's removal by eye favours it. The per-channel planes stay the next candidate for the colour dig either way.

#### R2l: the bright cores weighed up in the objective, pre-registered (2026-10-16)

Written before it trains, on the owner's goal of 2026-10-16: an overnight run that measurably improves the removal.
Tracked by #1338. It is lettered after R2k (the lobes, above), whose export it trains beside, and runs first because
it needs no new export.

**Why the objective.** R2i's weakest band is the bright one: 60.2 % clean at 100 to 1000 sigma against 79.2 % at 5 to
20, with 28.7 % dug. The trainer's L2 counts an error in the tile's units alike on every pixel, while the eval asks a
bright core to within one sigma of its own noise, and bright pixels are few. On A's draws, the pixels where the star
layer (draw less plate, the channels' mean) stands 100 sigma or more of the draw's own noise, grown by one pixel, are
0.61 % of the pixels inside the rim and hold 7.2 % of R2i's squared error (`C:\temp\e2\bright-share.py`). The eval's
own readout, over the 3x3 cores alone, puts them at 1.27 %.

**The change, one variable.** `n2n_smoke.py --bright-weight 5 --bright-sigma 100` (`bright_weights`; `run-r2.ps1
-BrightWeight 5 -BrightSigma 100`) weighs those pixels 5 times in the pixel and band terms, and in the plateau's
held-out objective. It is a weighted mean, so a tile with no bright star keeps R2i's objective exactly. The trainer
finds the region itself, from the draw, its truth and the draw's stored noise plane: no star list is read. With R2i's
errors, 5 would give the region about 28 % of the objective. The value is set once and is not tuned: 2.3 would give
15 %, and 40 at 300 sigma would also give 15 % over 0.05 % of the pixels. Checked before launch: 1,000 steps of the
recipe ran at R2i's speed (42 tiles a second) with the held-out objective falling (1.57e-4 to 1.05e-4).

**The run.** R2i exactly but for the switch: the same `r2-synth4` cache, seed 0, R2f's trainer and split, patience 24,
cap 480,000, a resume checkpoint every 2,000 steps: `run-r2.ps1 -Tag r2l -Arms synth4 -SeedCount 1 -Patience 24 -Steps
480000 -CheckpointEvery 2000 -BrightWeight 5 -BrightSigma 100`. Scored on A's draws by the colour eval
(`starless-eval-v5.json`), against R2i, and on R2k's two twins of them (read, not predicted).

**The bars** are R2j's: on the luminance the seed spread at convergence (2.7, 3.1 and 3.1 points clean at 5-20, 20-100
and 100-1000 sigma, 6.4 dug at 100-1000), in colour 1.9, 0.4 and 0.6 clean and 1.9, 4.4 and 3.8 dug.

**Predictions** (against R2i):
1. **It ends on its own before 480,000.** Moderate to high.
2. **The bright stars gain**: clean at 100-1000 sigma at least 63.3 % (R2i's 60.2 plus the spread). Moderate: it is the
   hypothesis.
3. **The bright digs fall**: dug at 100-1000 sigma at most 22.3 % (28.7 less 6.4). Low to moderate: the weight
   penalises a dig and a leftover alike.
4. **The faint stars hold**: clean within the spread of R2i's at 5-20 and 20-100 sigma (at least 76.5 % and 71.4 %).
   Moderate: 99.4 % of the pixels keep their weight, but their share of the objective falls from about 93 % to 72 %.
5. **The sky**: far sky at most 0.37.

**Kill:** clean removal on the luminance falls by more than its spread at 5-20 or 20-100 sigma. Then the faint stars
pay for the bright ones, and the weight is not adopted.

**What it decides.** If (2) and (4) hold, the weight joins the recipe and R2l is the next arms' control. If (2) fails
while (4) holds, the bright cores' error is not the objective's weighting, and the next place to look is the data (R2k).
If (2) holds and the kill fires, a smaller weight is the next arm, not this one again.

**Amended 2026-10-16 at 22:17, ten minutes into the run and before any of its numbers exist.** The bars above are an
old-store pair of seeds (R2g, R2h), and on this line a seed has moved the bright stars by 3.1 points at convergence and
by 12.9 at patience 24 (R2e). So R2i runs again on seed 1 beside R2l (below), and R2l's gains are also read against
that pair: an improvement is claimed only where it clears the larger of the two spreads.

#### R2i on seed 1, the line's own seed spread, pre-registered (2026-10-16)

Written before it trains. R2i's read named it as its confirmation; it runs now because an arm read on one seed against
a control on one seed is read against a spread measured on another store (`feedback`: decompose the variance before
the next arm). The same `r2-synth4` cache and recipe, seed 1, a resume checkpoint every 2,000 steps: `run-r2.ps1 -Tag
r2i -Arms synth4 -FirstSeed 1 -SeedCount 1 -Patience 24 -Steps 480000 -CheckpointEvery 2000`, beside R2l on the same
GPU. Scored as R2l is.

**Predictions** (against R2i on seed 0):
1. **It ends on its own before 480,000.** Moderate to high.
2. **Within the old-store spread**: clean within 2.7, 3.1 and 3.1 points of seed 0's at 5-20, 20-100 and 100-1000
   sigma. Moderate: R2i's gain over R2g past that spread was 3.9, 4.4 and 11.5 points, so a seed 1 that falls back to
   R2g says the store's gain was partly a seed.

No kill: it is a measurement. **What it decides.** Its difference from seed 0 is the same-store seed spread, the bar
R2l's and R2k's reads take beside the old one; and whether R2i's gain over the old store holds on a second seed.

**Moved to Azure, not run on the desktop (2026-10-16, 22:23).** Beside R2l on the GTX 1070 the two runs shared it: 46 tiles
a second together against about 45 for one alone, so each would have taken a day. Seed 1 was stopped at step 600 and
moves to a spot T4, whose cache is already in the blob container. On that GPU its difference from seed 0 also holds the two
GPUs' arithmetic, which the read must say. East US had no T4 spot capacity in any zone that night or the next morning.

#### Averaging at inference: two models, and the tile's eight flips and turns, pre-registered (2026-10-17)

Written before any of it is scored, on the morning R2l's read missed (below). Tracked by #1338. A model's error at a bright
core is part bias and part variance from the draw, and averaging attacks the variance: two models trained apart, or one
model shown its tile flipped and turned and its outputs turned back. Nothing is trained. `r2_infer.py --ckpt a.pt,b.pt`
averages models' outputs, and `--tta 8` each model's over the eight views, each turned back with its noise plane turned
alike; one checkpoint at `--tta 1` is the script as before. Scored on A's draws by the colour eval, against R2i.

- **E1**: R2i and R2l averaged.
- **E2**: R2i over its eight views.
- **E3**: R2i and R2l, each over its eight views (read, not predicted).

**Checked first:** R2i through the changed script at `--tta 1` gives its stored outputs back bit for bit.

**The bars** are the seed spread R2i registered (2.7, 3.1 and 3.1 points clean at 5-20, 20-100 and 100-1000 sigma).

**Predictions** (against R2i):
1. **E1, the bright stars gain**: clean at 100-1000 sigma at least 63.3 %. Moderate: the two models differ in what they
   were taught, so their errors are partly their own.
2. **E1, the faint stars hold**: clean within the spread at 5-20 and 20-100 sigma.
3. **E2, the bright stars gain**: at least 63.3 %. Low to moderate: the net never saw a turned tile, and a demosaic's
   texture and a field's elongation are not the same turned.
4. **E2, the faint stars hold.**

**Kill**, for each: clean on the luminance falls past the spread at any brightness. Then that average is not adopted.

**What it decides.** E1 holding makes an ensemble of separately trained models a product option at twice the inference,
and makes every further seed worth twice (a spread and a member). E2 holding makes the views free in training and eight
times the inference. Neither is a better model, and the read says so.

#### R2l's read: the weight moves the bright stars' dig between channels and not their clean rate (2026-10-17)

`r2l_synth4_s0` ended on its own: "stopped at step 113000: the held-out objective's best was 4.111201e-06 at step 77000",
at 03:27, on the GTX 1070 from 22:07. It ran alone but for two spells: R2i's seed 1 beside it for six minutes (22:17 to
22:23), and R2k's training from 02:58, whose export pass had loaded its driver before the gate that was to hold it back
(the pass read the script at 22:07, the gate went in at 22:17). Neither changes what trains, only how fast. Its first
scoring failed on a driver bug (a one-pair list flattened, so the set's name read `o`) and was run again at 05:28.
`C:\temp\e2\r2l-read.txt`.

| on A's draws | R2i | R2l |
|---|---|---|
| clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 79.1 / 74.7 / 59.8 |
| dug 100-1000 sigma | 28.7 | 27.6 |
| dug R / G / B at 100-1000 sigma | 34.3 / 47.7 / 12.6 | 25.9 / 46.1 / 20.1 |
| clean in colour 5-20 / 20-100 / 100-1000 sigma | 57.3 / 43.7 / 21.3 | 57.4 / 45.7 / 21.3 |
| clean / dug saturated | 46.6 / 28.3 | 52.5 / 23.7 |
| far sky | 0.19 | 0.17 |

1. **It ends on its own before 480,000: holds**, at 113,000, its best at 77,000: earlier than R2i (216,500), on an
   objective that is not R2i's, so the two lengths do not compare.
2. **The bright stars gain: misses.** Clean at 100-1000 sigma 59.8 % against at least 63.3.
3. **The bright digs fall: misses.** 27.6 % against at most 22.3.
4. **The faint stars hold: holds**, -0.1 and +0.2 points.
5. **The sky: holds**, 0.17.

**The kill does not fire.** On the twins it reads the same: on the plain twin clean 61.4 against R2i's 61.2 at
100-1000 sigma, on the lobed one 59.9 against 61.0.

**Read, not predicted.** The weight moved the bright stars' dig from red to blue (red 34.3 to 25.9 %, blue 12.6 to 20.1)
and left its total where it was. Saturated stars clean 52.5 % against 46.6, and clean in colour at 20-100 sigma rose 2.0
points (spread 0.4). By the registered rule (2) failing with (4) holding means the bright cores' error is not the
objective's weighting: the net already spends what it can there. What R2l is worth came from averaging it (below).

#### Averaging at inference read: every average gains past the spread, and the most on the brightest stars (2026-10-17)

Scored at 05:36 to 05:50 on A's draws (`C:\temp\e2\r2-averaging.ps1`, `read_avg.py`, `avg-read.txt`). **The check
holds**: R2i through the changed script at `--tta 1` gave its 1,280 stored outputs back bit for bit.

| on A's draws | R2i | E1: R2i + R2l | E2: R2i, 8 views | E3: both, 8 views each |
|---|---|---|---|---|
| clean 5-20 sigma | 79.2 | 80.7 | 80.1 | **81.1** |
| clean 20-100 sigma | 74.5 | 76.9 | 76.4 | **78.1** |
| clean 100-1000 sigma | 60.2 | 65.0 | 66.2 | **68.9** |
| dug 100-1000 sigma | 28.7 | 24.8 | 24.8 | **22.3** |
| clean in colour 5-20 / 20-100 / 100-1000 | 57.3 / 43.7 / 21.3 | 60.6 / 50.1 / 28.6 | 59.6 / 48.1 / 31.0 | **61.9 / 53.4 / 35.1** |
| dug in colour 100-1000 sigma | 63.3 | 57.4 | 54.3 | **50.9** |
| clean / dug saturated | 46.6 / 28.3 | 54.3 / 18.3 | 52.1 / 19.2 | **62.1 / 14.2** |
| core RMS 100-1000 sigma | 1.78 | 1.71 | 1.87 | 2.06 |
| far sky | 0.19 | 0.17 | 0.19 | 0.17 |

1. **E1, the bright stars gain: holds**, 65.0 % against at least 63.3 (+4.7).
2. **E1, the faint stars hold: holds**, +1.4 and +2.5, both up.
3. **E2, the bright stars gain: holds**, 66.2 % (+5.9).
4. **E2, the faint stars hold: holds**, +0.9 and +1.9.

**No kill fires**: no clean rate falls anywhere. E3, read and not predicted, gains at every brightness and in every
colour test, +8.7 points clean on the bright stars and +13.8 in colour there, with saturated stars clean 62.1 % against
46.6. Its 20-100 sigma gain (+3.7) is past the spread upward.

**How firm.** E2 compares one model with itself, so no seed spread enters it: on 12,393 bright stars the counting noise
of a clean rate is about half a point, and the views add six. E1 and E3 average R2l, which alone is no better than R2i,
so their gains are the averaging's, not R2l's. The core's RMS rises under the views while every rate improves: the
average pulls most cores inside a sigma and leaves a few further out. And A's draws are injected stars: whether a real
master's stars come off as much better is for the lightbox.

**What it decides.** Averaging is the first change since R2i that improves the removal past the spread at every
brightness, and it trains nothing. The product decision (a second model to ship, and eight times the inference per model)
is the owner's. On the line, every further seed is now both a spread and an ensemble member, which R2i's seed 1 (on the
T4) is the first of; and a single model that keeps E3's gain is the next training target (distillation, or the views as
training augmentation).

#### The real stars: the same order on the validation masters' own stars (2026-10-17)

Read, not predicted, the same morning, because A's draws are injected stars. The 8 validation sessions' REAL master tiles,
their own stars in, came from the 2026-10-12 store by a noise-mode export (40 cells a session, each tile with its noise
plane; `C:\temp\e2\real-val-export.ps1`), and every model and average ran over them (`real-star-infer.ps1`). No truth
exists there, so `real-star-check.py` reads what is LEFT at each star the plate builder subtracted (first pass,
unsaturated, inside the rim): the output's 3x3 core less the median of a ring 6 to 10 px out, in the tile's noise sigma.
53,108 stars: 33,934 at 5-20 sigma, 14,959 at 20-100 and 4,215 at 100-1000 by the builder's significance.

| real stars | input | R2i | R2l | E1 | E2 | E3 |
|---|---|---|---|---|---|---|
| left within 1 sigma, 5-20 sigma | 0.0 | 94.8 | 95.7 | 95.8 | 95.3 | **96.1** |
| left within 1 sigma, 20-100 sigma | 0.0 | 97.1 | **98.9** | 98.6 | 97.5 | 98.8 |
| left within 1 sigma, 100-1000 sigma | 0.0 | 96.4 | 97.6 | 97.9 | 97.1 | **98.2** |
| light left (over +1), 100-1000 sigma | 100 | 2.11 | 1.28 | 1.07 | 1.73 | **0.83** |
| dug (under -1), 100-1000 sigma | 0 | 1.47 | 1.12 | 1.04 | 1.21 | **0.93** |

The averages hold their order on real stars, E3 first at the faint and the bright end, and the counting noise is about 0.3
points on the bright stars. What the injected draws did not show, the real ones do: R2l alone leaves less than R2i in
every band (light left on bright stars 1.28 % against 2.11). The test is gentler than the eval, a core against its own
model's surroundings, so every model reads 95 to 98 %. It says nothing of a halo or a ring left beyond 6 px, which the
lightbox still has to show.

**Is it the injection or the measure?** (the owner's question: R2l does not beat R2i on injected stars but does on real
ones.) The same core-against-ring measure on the INJECTED stars (`injected-ring-check.py`, the eval's significance bands,
lobes read with their star) gives R2l the same lead it has on real ones: 96.7 against 94.5 % within a sigma at 20-100
sigma and 93.1 against 91.2 at 100-1000 on A's draws, 97.2 against 95.2 at 20-100 on the lobed twin; E3 leads both. So
the two reads disagree because they measure differently, not because the injected stars are wrong: the eval holds a
core to the truth, this measure to the model's own surroundings, and R2l more often leaves a star level with a
surrounding it moved too. The ranking (E3, then R2l, then R2i) is the same on injected and real stars. The levels do not
compare across the two sets, which band by different significances (the builder's peak, the eval's 3x3 core).

#### Real stars are noisier at their cores than injected ones (2026-10-17)

The owner's idea, the same morning: "maybe the injected stars lose the underlying noise that was present?" The injector
gives a star its shot noise by the master's calibration (`NoiseCalibration.SigmaAt`: one sub's sigma times the square root
of the level over the background, above the pedestal, clamped to 0.25 to 1000, at the master's depth), and the noise plane
the net is conditioned on is the same formula. Measured two ways:

- **On the plates** (`C:\temp\e2\noise-at-stars.py`, 95 sessions of the 2026-10-12 store): the plate under a subtracted,
  uninpainted star keeps that star's noise, and its adjacent-difference scatter at the core is a median 2.04 times
  `SigmaAt` there (p25 1.42, p75 2.97, 100,000 channel-stars), normalised on each session's sky. The cap never binds. But
  the plate is the master less a FITTED model, and at a 1.7 px FWHM a 2 % misfit of a 100 sigma star is 2 sigma a pixel,
  so this read cannot tell noise from fit.
- **On half pairs, with no model** (`half-noise-at-stars.py`, three caches of half-master tiles, 15 sessions): (A - B) / 2
  has the master's variance and no star in it, so its scatter at a real star's core, over the noise plane, over the same
  on the sky, is what a real star carries against what an injected one gets:

| a real star's peak | stars | (A - B) / 2 at the core against the plane, sky-normalised (median over the three caches) |
|---|---|---|
| 10-30 sigma | 59,000 | 1.15 to 1.36 |
| 30-100 sigma | 27,000 | 1.87 to 2.53 |
| 100-300 sigma | 5,400 | 2.69 to 4.69 |
| 300 sigma and over | 150 | 2.94 to 3.91 |

So a real bright star's core is three to five times noisier than the injector makes it, and the excess grows with the
star's amplitude rather than its square root: it is the sub-to-sub seeing, guiding and registration the halves differ by,
about 2 to 4 % of the star's peak a pixel beyond its shot noise. Injected stars are too clean exactly where the eval
fails most, and the plane under-states a real core's noise by the same factor. **The next arm (R2m, not yet registered):
that profile jitter injected with each star, in proportion to its level and fitted to the half pairs, the plane left as
it is, since a real tile's plane is computed the same way; a better eval set and better training data in one.**

#### R2k's read: the lobes buy colour on the bright stars and cost them a dig (2026-10-17)

`r2k_synth5_s0` ended on its own: "stopped at step 230000: the held-out objective's best was 2.171420e-06 at step
206000", at 13:40 on the GTX 1070. It trained from 02:58, sharing the GPU with R2l for half an hour (its export pass had
loaded the driver before the gate that was to hold it), and was scored by its driver on A's draws and both twins
(`C:\temp\e2\r2k-read.txt`).

| | R2i | R2k |
|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 80.3 / 75.7 / 58.0 |
| A's draws: dug 100-1000 sigma | 28.7 | **35.6** |
| A's draws: clean in colour 100-1000 sigma | 21.3 | 23.8 |
| lobed twin: clean / dug 100-1000 sigma | 61.0 / 30.0 | 58.1 / 37.7 |
| lobed twin: clean in colour 100-1000 sigma | 19.7 | **23.9** |
| A's draws: clean / dug saturated | 46.6 / 28.3 | 48.4 / 37.4 |

1. **It ends on its own before 480,000: holds**, at 230,000.
2. **On the lobed twin, colour gains at the bright stars: holds**, 23.9 % against at least 20.3 (+4.2), and on A's draws too
   (+2.5).
3. **On A's draws the luminance holds: holds**, +1.0, +1.2 and -2.2 against bars of 2.7, 3.1 and 3.1.
4. **The sky: holds**, 0.20.

**The kill does not fire.** **Read, not predicted:** the dug share at 100-1000 sigma rose 6.9 points on A's draws (35.6
against 28.7, the seed spread there 6.4) and 7.7 on the lobed twin, and saturated stars dug 37.4 % against 28.3. So R2k
digs its bright stars more while their colour comes out cleaner: taught that a bright star may carry a lobe, it takes more
light round a bright core, which the injected stars without one pay for. By the registered rule (2) and (3) holding make
`--star-lobes` the export's switch; the dig says to read it once more beside the core jitter (R2m), which changes the same
cores, before it becomes the default. The averages take it as a member (below).

#### Averaging with R2k, pre-registered (2026-10-17)

Written before it is scored. **E4**: R2i, R2l and R2k, each over its eight views (`r2_infer.py --ckpt` with the three,
`--tta 8`), on A's draws and on the validation masters' real stars, against R2i and against E3. **Predictions**: (1) E4's
clean at 100-1000 sigma is at least E3's 68.9 % less 1 point (a third member, the one that digs more, need not add, but
should not take much away); (2) no clean rate falls past the spread against R2i. **Kill**: none; it is the average's next
member, read. **What it decides**: whether the averages keep gaining with members that differ in their data.

**E4 read** (14:00, `C:\temp\e2\r2-averaging-e4.ps1`, `real-star-read-e4.txt`): both predictions hold, and the third member
adds nothing a reader could call a gain.

| | R2i | E3 | E4 |
|---|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 81.1 / 78.1 / **68.9** | **81.7 / 78.4** / 68.1 |
| A's draws: dug 100-1000 sigma | 28.7 | **22.3** | 25.0 |
| A's draws: clean in colour 100-1000 / clean saturated | 21.2 / 46.6 | 35.1 / 62.1 | **36.4 / 63.5** |
| real stars, 100-1000 sigma: within a sigma / dug | 96.4 / 1.47 | **98.2 / 0.93** | 98.1 / 1.21 |

(1) holds, 68.1 % against at least 67.9; (2) holds. R2k carries its dig into the average (dug 25.0 against E3's 22.3, and
on real stars 1.21 against 0.93), so E3 stays the best combination; R2k alone on the real stars leaves 95.4 % of the bright
ones within a sigma against R2i's 96.4, the dig again (2.92 % against 1.47).

#### Averaging with R2i's seed 1, pre-registered (2026-10-17)

Written while seed 1 trains, before any of its numbers exist. **E5**: R2i's seeds 0 and 1, eight views each: the cleanest
average there is, one recipe twice, so whatever it gains is the averaging's alone. **E6**: R2i's two seeds and R2l, eight
views each. On A's draws and the validation masters' real stars, against R2i and E3. **Predictions**: (1) E5's clean at
100-1000 sigma at least 63.3 % (R2i plus the spread); (2) E6's at least E3's 68.9 less 1 point; (3) neither lets a clean
rate fall past the spread against R2i. **Kill**: none, read. **What it decides**: whether two seeds of one recipe average
as well as two recipes do, which says whether a product average needs different recipes or only more seeds.

#### R2i's seed 1 read: the faint stars repeat, and the bright stars lie 4.3 points apart (2026-10-17)

`r2i_synth4_s1` ended on its own: "stopped at step 166000: the held-out objective's best was 2.441324e-06 at step
142000", at 14:27 on the Azure spot T4. It was evicted once (about 07:30) and resumed from its step-44,000 checkpoint
with its RNG states. The resume also stopped its sync to the container, because the line that installs the sync's cron
entry empties the crontab when it runs a second time. Training went on, so the weights were synced by hand at 14:32 and
scored by the driver at 14:33 to 14:36, by the same binary as seed 0, on A's draws and both of R2k's twins
(`C:\temp\e2\read_s1.py`).

| | seed 0 | seed 1 |
|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 80.6 / 75.3 / 55.9 |
| A's draws: dug 100-1000 sigma | 28.7 | 32.3 |
| A's draws: clean in colour 100-1000 sigma | 21.3 | 19.0 |
| A's draws: clean / dug saturated | 46.6 / 28.3 | 46.6 / 35.6 |
| plain twin: clean / dug 100-1000 sigma | 61.2 / 29.6 | 57.5 / 32.4 |
| lobed twin: clean / dug 100-1000 sigma | 61.0 / 30.0 | 56.6 / 33.8 |
| real stars, 100-1000 sigma: within a sigma / dug | 96.4 / 1.47 | 95.4 / 2.78 |

1. **It ends on its own before 480,000: holds**, at 166,000 (seed 0 at 216,500).
2. **Within the old-store spread: holds on the faint stars, misses on the bright ones.** Clean +1.3 and +0.8 at 5-20 and
   20-100 sigma (bars 2.7 and 3.1), and -4.3 at 100-1000 (bar 3.1), with -3.7 and -4.4 on the two twins. On the real
   stars it is the same: better than seed 0 on faint stars (96.4 % within a sigma against 94.8), worse on bright ones.

**What it decides.** The same-store seed spread is 1.3, 0.8 and 4.3 points clean, and 3.5 dug at 100-1000 sigma. It is
one difference, as the old store's was, and it holds the T4's arithmetic and one resume as well as the seed. From here
the line reads its bright stars against **4.3 points** (the larger of the two spreads) and its faint ones against the
old store's 2.7 and 3.1. Nothing read so far changes: R2l's -0.4 and R2k's -2.2 at 100-1000 sigma were inside both
bars, and R2k's +6.9 dig is past the same-store 3.5 as it was past the old 6.4. **The re-baked store's gain holds on
a second seed, smaller**: seed for seed against the old store's seed 1 (R2h, 78.1 / 73.2 / 51.9), seed 1 gains +2.5,
+2.1 and +4.0, against seed 0's +3.9, +4.4 and +11.5 over R2g. The bright stars clear the spread on both seeds; the
faint gain is at the spread.

#### E5 and E6 read: the averaging's gain does not need different recipes, and three members lead on real stars (2026-10-17)

Scored at 14:38 to 14:46 (`C:\temp\e2\r2-averaging-e56.ps1`, `read_s1.py`, `real-star-read-e56.txt`).

| | R2i | E3: R2i + R2l | E5: R2i's two seeds | E6: two seeds + R2l |
|---|---|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 81.1 / 78.1 / **68.9** | 81.4 / 78.0 / 66.7 | **81.5 / 78.5** / 68.8 |
| A's draws: dug 100-1000 sigma | 28.7 | **22.3** | 24.7 | 22.8 |
| A's draws: clean in colour 100-1000 / clean saturated | 21.3 / 46.6 | 35.1 / **62.1** | 33.1 / 54.3 | **35.3** / 60.3 |
| the members' mean, clean 100-1000 sigma | | 60.0 | 58.1 | 58.6 |
| the average over its members' mean | | +8.9 | +8.7 | **+10.2** |
| real stars, 5-20 / 20-100 / 100-1000 sigma within a sigma | 94.8 / 97.1 / 96.4 | 96.1 / 98.8 / 98.2 | 96.3 / 98.4 / 97.8 | **96.6 / 98.9 / 98.4** |
| real stars, 100-1000 sigma: light left / dug | 2.11 / 1.47 | 0.83 / 0.93 | 1.07 / 1.09 | **0.78 / 0.81** |

1. **E5's bright stars gain: holds**, 66.7 % against at least 63.3.
2. **E6 against E3: holds**, 68.8 % against at least 67.9.
3. **No clean rate falls past the spread against R2i: holds** for both; every rate rises.

**What it decides.** Two seeds of one recipe gain over their members' mean as much as two recipes do (+8.7 against
+8.9), so the averaging needs no second recipe: E5 lands below E3 only because seed 1 is the weaker member, and the
seed lottery is itself 4.3 points on bright stars. A third member adds 1.3 points more over the mean. On A's draws E6 and
E3 are one result, every difference inside its counting noise (half a point on 12,393 bright stars, about 3 on the 219
saturated ones, where E6 is 1.8 lower); on the real stars E6 is first in every band,
with the least light left and the least dug at 100-1000 sigma, though its lead over E3 there (0.2 points) is inside the
0.3 counting noise. So a product average is any few members of the line's recipe, and its quality follows the members;
which ones ship, and at what inference cost, is the owner's. On the line, the target stays a single model that keeps an
average's gain, and R2m (the core jitter) is the next training arm.

#### R2n: four levels deep instead of two, so the net reaches past a bright star, pre-registered (2026-10-18)

Written before it trains, on the owner's word of 2026-10-18 to apply what other star removers do. Tracked by #1338. The
idea comes clean-room from StarNet2, which the owner keeps as a judge only: its weights never ship and its outputs never
train anything. Its graph halves a 512 px tile eight times, so every output pixel can draw on the whole window; R2i halves
a 256 px tile twice. Read off the two ONNX graphs (`C:/temp/e2/net-stages.py`), R2i has 0.81 M weights and a reach of 66 px,
StarNet2 32.87 M and 1,037 px. On our scorer StarNet2 itself lost to R2i by far (bright injected stars 15 % clean against
60 %, real bright stars 87 % flat against 96 %), so size alone does not win; this arm tests reach alone.

**The arm.** R2i's recipe exactly (seed 0, the re-baked store's synth4 cache, patience 24, cap 480,000, a resume
checkpoint every 2,000 steps), with `--levels 4 --max-ch 256`: the tile halves four times (256 to 16 px), the channels
double up to 256 and stop, 6.86 M weights, and a reach of about 294 px, wider than the tile. Two levels stays the default,
and every earlier checkpoint loads as it was (R2i's does, checked). On the 1070 a forward and backward pass runs 52 tiles/s
against R2i's 68.

**The ring.** Measured the same day on the owner's eye (a violet ring where the brightest star was): on A's 10,517 bright
unsaturated injected stars, the 3 to 6 px annulus sits at -0.40 / -0.56 / -0.33 sigma (R / G / B) under R2i, with a
channel dug past -1 sigma on 43.1 % of them (E6: 37.1 %). It is a bias, so averaging barely moves it. Neither eval reads
that annulus (the scorer checks the 3 px core, the real-star check compares the core with 6 to 10 px), so
`C:/temp/e2/ring-annulus.py` measures it until the scorer carries it.

**Predictions.** The bars are the same-store seed spread: 1.3 / 0.8 / 4.3 points clean at 5-20 / 20-100 / 100-1000 sigma.
1. Bright stars (100 to 1000 sigma) clean at least 64.5 %, R2i's 60.2 plus the spread.
2. Saturated stars clean at least 50.0 %, R2i's 46.6 plus their counting noise (about 3.4 points on 219 stars).
3. The faint bands hold: 5-20 sigma at least 77.9 % and 20-100 at least 73.7 %.
4. The ring shallows: a channel dug past -1 sigma in the annulus on at most 38 % of bright stars (R2i's 43.1 less 5).
5. Real bright stars (`r2-real-val`, 100-1000 sigma) read flat on at least R2i's 96.4 %.
6. M31's nucleus stays: R2n alone over 8 views takes at most 5 % of the central 6 px in every channel (R2i 3.1 / 3.1 /
   0.3 %, its seed 1 -0.9 / 4.8 / 1.8 %). Registered after R2n started training and before anything of it was scored
   ("Bright compact structure taken as stars", below, #1658).

**Kill:** bright stars clean below R2i's 60.2 % means reach is not what they lack; the deeper net is dropped and R2m (the
core jitter) runs next. **What it decides:** whether the line's base net grows. If (1) and (2) hold, four levels become
the recipe every later arm and average starts from.

#### R2o: one model that keeps the views' average, pre-registered (2026-10-18)

Written before it trains, on the owner's question "possible to do averaging as part of a single model?" and the word "use
azure". Tracked by #1338. Most of what averaging bought came from a model's own views, not from more models: E2 (R2i
over its 8 flips and turns) cleaned 66.2 % of bright stars against R2i's 60.2, and E5 (R2i's two seeds, 8 views each)
added only half a point more (66.7). The trainer has never shown the net a flipped or turned tile. So the net is not
symmetric under them, its eight views disagree, and averaging them cancels the disagreement.

**The arm.** R2i's recipe exactly (two levels, seed 0, the synth4 cache, patience 24, cap 480,000, a resume checkpoint
every 2,000 steps), with two switches:
- `--dihedral`: each training sample in a random one of its 8 views, its noise plane turned with it.
- `--ema 0.999`: a running average of the weights (about 1,000 steps), saved as the output `r2o_synth4_s0.pt`.

The live weights are saved beside it as `r2o_synth4_s0_raw.pt`. The schedule reads the live weights, so the raw model is
R2i plus the views and nothing else, and one run reads both effects. It trains on an Azure spot T4 under the owner's
guards, while R2n has the 1070.

**Predictions.** The bars are the same-store spread (1.3 / 0.8 / 4.3 points clean at 5-20 / 20-100 / 100-1000 sigma);
E2 is 80.1 / 76.4 / 66.2 clean, dug 24.8 at 100-1000, saturated 52.1 clean.
1. One model, one view: R2o cleans bright stars at least 64.5 % (R2i's 60.2 plus the spread), most of the way to E2.
2. Its views agree: R2o over 8 views gains at most 3.0 points on bright stars over R2o at one view (R2i's views gained 6.0).
3. The views carry it: R2o's raw model alone also cleans bright stars at least 64.5 %.
4. The faint bands hold: 5-20 sigma at least 77.9 % and 20-100 at least 73.7 %.
5. M31's nucleus stays: at most 5 % of the central 6 px in every channel (#1658).

**Kill:** bright stars below R2i's 60.2 % at one view means training on the views does not move the average's gain into
the weights. Distillation from E5 (not E6, which carries R2l's appetite for nuclei) is then next. **What it decides:**
whether a shipped single model can carry most of the average's gain at an eighth of its inference, or a 24th of E6's.

#### A whole-frame runner: an average over a master, in overlapping windows (2026-10-18)

Asked for by the owner to see E6 on whole frames, and tracked by #1338. Until then the line had no path that put a
checkpoint over a whole master; every number came from 256 px tiles scored one by one. `OnnxTianWenStarRemover`
(`TianWen.AI.Imaging`) is that path, reached as `tianwen image remove-stars <master> --model a.onnx b.onnx ... --views 8`.
It goes through the same split as RC-Astro's remover (`NarrowbandCombination.SplitStarsAsync`: one scale, the canvas ring
filled for the remover and absent again after, the stars as the input less the starless plate). No weights ship. Each
named ONNX file loads only against the contract beside it, fed as the shipped denoiser is, since it is the same graph
shape. `n2n_export.py` writes a model and its contract.

- **Not the denoiser's runner.** `N2nLinearRunner` puts each chunk's median back after the net (`RestoreLevel`), which
  corrects a denoiser's level prior but would return a star remover's removed light as a haze.
- **The windows, clean-room from StarNet2.** That tiling was reproduced exactly against StarNet2's exe, which the owner
  keeps as a judge only. The tile's 16 px rim is never used. Its interior is weighted by a triangle, zero at the rim and
  one at the centre, and the windows step by half the interior (112 px), so two triangles sum to one everywhere. Each
  pixel is therefore decided mostly by the window that sees it centrally, where a star is not cut. The frame is mirrored
  out by the rim plus a step, so its own edges get the same overlap.
- **The average is E6's.** Every named model sees each window in its 1, 2, 4 or 8 views (`r2_infer.py`'s order). Each
  output is turned back and all are averaged in the stretched domain, as the scored averages were. Then comes one
  unstretch.
- **Mono.** A mono master is put in all three channels only when asked (`--mono-as-rgb`), and the mean of the three
  outputs comes back. No model here was trained or measured on mono.

The first frames were the owner's, in `C:/temp/startnet2`. Pinned by `StarRemoverRunnerTests`: the views are
`torch.rot90`'s and undo themselves, the triangles sum to one, the mirror is reflect-101, and a frame with a canvas ring
goes through end to end, using the denoiser's weights as a stand-in graph.

#### Bright compact structure taken as stars: a galaxy nucleus, a nebula knot (2026-10-18)

Tracked by #1658. The owner saw it three times on the first whole frames: a green-and-red blob in M31's core in E6's stars layer, and the
Lagoon's and the Trifid's bright knots with their dust lanes. Measured on M31 (`C:/temp/e2/m31-nucleus.py`, the share of
the input's light a starless frame no longer holds), E6 takes 16.5 / 7.9 / 6.2 % (R / G / B) of the central 6 px and
2.8 / 0.6 / 0.6 % from 6 to 12 px. StarNet2's exe and StarXTerminator take about none of either (within 1 %). The
owner's word: "M31 needs some fixing". The green in an unlinked stretch is a smaller excess further out (25 to 100 px,
about 0.1 % of the light), largest in green relative to that channel's own noise.

**It is R2l's objective, not reach.** Each member alone over 8 views, same frame:

| | R2i | R2i, seed 1 | R2l | E6 (all three) |
|---|---|---|---|---|
| central 6 px taken (R / G / B) | 3.1 / 3.1 / 0.3 % | -0.9 / 4.8 / 1.8 % | **35.9 / 14.9 / 14.9 %** | 16.5 / 7.9 / 6.2 % |

R2l weighs the pixels where the star layer stands 100 sigma or more five times. It learned to take any bright compact
core, a galaxy's nucleus with it, and E3 and E6 inherit that through it. E5 (R2i's two seeds, no R2l) leaves the nucleus
nearly whole for 2.1 points of bright-star clean rate on A's draws (66.7 % against E6's 68.8 %), which bears on which
members ship, the owner's call. R2n is R2i's recipe without the weight, so it carries the nucleus as a sixth prediction
(above): no worse than R2i's own seeds. A nebula's bright knots, the Lagoon's and the Trifid's, are still to be read per
member the same way.

#### Core protection: the brightest part of an extended source kept from any remover (2026-10-19)

Tracked by #1658, on the owner's idea of 2026-10-19: identify the large, extended sources, mask the absolute brightest
part of each, and keep it out of the stars layer, "something you can do with SXT in a very manual, tedious way". The
owner's other reason: SXT can take the brightest part of M42 as one large star. It is the inference half of #1658; the
training half (bright compact cores the target keeps, in the synthetic scenes) is "Kept cores" below.

**`ExtendedCoreMask` (TianWen.Lib), applied in the split** (`NarrowbandCombination.SplitStarsAsync` with options;
`image remove-stars --protect-cores`), so it holds for any remover, RC-Astro's as well as ours:
1. The large-scale map: the luminance's 8 px block median, median-filtered over 7 blocks (56 px). A star covers too
   little of any window to survive it, and an extended source survives.
2. An extended source is a connected region of that map 3 per-pixel sky sigmas over the sky. The sigma comes from
   second differences, which a galaxy's gradient does not reach; first differences read 15 % high on a steep one.
3. A source whose peak stands 50 sigmas or more gets a core: where the map is at least half that peak, at most 1 % of
   the source's area, its brightest blocks first. A frame-filling nebula therefore gives up only its brightest part.
4. The core is grown by the feather's reach and then feathered, so it takes the input back whole and the ramp lies outside.

Measured first as a prototype on the owner's frames (`C:/temp/e2/extended-mask.py`, E6's starless plates):

| frame | core | E6's take there |
|---|---|---|
| M31 | one, 2,944 px about the nucleus (0.1 % of the galaxy) | 16.5 / 7.9 / 6.2 % of the central 6 px (R / G / B), to 0 |
| Lagoon (OIII) | one, 2,048 px about its brightest knot | the knot in E6's stars layer, gone from it |
| Trifid (8-bit) | one, 14,976 px | little either way |

Through the product (`image remove-stars --protect-cores` with E6's three models at 8 views), the same cores came back:
2,048 px on the Lagoon, 2,944 px on M31. M31's central 6 px are taken 0 % in every channel. Grown and feathered, the
protected region changes 9,318 and 12,238 px, and keeps 3 of the Lagoon's 13,718 compact sources over 20 sigma and 2 of
M31's 23,728 out of the stars layer. The owner, on the Lagoon's crops: "it does make the stars a lot cleaner". The price
is those few stars, which stay in the starless plate. Off by default until the owner says otherwise. The stacking workbench draws the cores and lets
the user drop or add one (`stacking-workbench.md`, Deep sky, step 6). Pinned by `ExtendedCoreMaskTests`.

#### Kept cores: bright galaxy-like cores the synthetic target keeps (2026-10-23)

Tracked by #1658, its training half: `degrade --kept-cores` (with `--background synthetic`) draws a bright extended core
into a share of the synthetic background's cells (`KeptCores`, TianWen.Lib). Because the core sits in the background, it
is in the input and in the starless target alike, and a remover is taught to keep it. Nothing is trained here; the option
is off by default.

**Sized from M31's nucleus** on the owner's Andromeda master. Its peak stands at 6,127 / 4,760 / 3,951 sigma (R / G / B).
It falls to half that peak about 3.5 px out, 2.5 to 3 star widths across, and holds 0.14 of the peak at 12 to 20 px, 0.06
at 20 to 40 px and 0.02 at 40 to 80 px. A core is drawn as:

| part | drawn | why |
|---|---|---|
| share of cells | 15 % | a modest share: a core is rare in a real field |
| core | a Gaussian, FWHM 2.5 to 6 PSF widths across its minor axis (log-uniform), axis ratio 0.6 to 1 | **never under 2.5 widths**: R2d's knots test kept compact knots at 1.6 widths and left bright stars' cores in |
| bulge | a Moffat of index 0.75 on the same ellipse, 0.35 to 0.6 of the peak, scale 1 to 2 core widths | M31's wing falls as r^-1.5. A bulge of one core width (7 px) at 0.55 of the peak reads 0.14 / 0.06 / 0.02 of the whole at 16 / 30 / 60 px, as M31 does |
| reach | tapered to zero from 8 to 16 bulge scales | so a core ends inside a known radius |
| peak | log-uniform from 200 to 6,000 noise sigma, in the channel it stands brightest in against its noise | M31's red stands 6,127 |
| colour | one of the master's redder stars (its red over blue at or above the pool's median) | a bulge is an old, red population |
| ceiling | its top held under 0.8 of the master's fallback clip (0.95 of each channel's brightest pixel), colour kept | a core never clips, which would make a flat top no star has |

The width floor applies to the Gaussian, so the whole profile is wider than the core alone. With the bulge at M31's 0.55
and one core width, it halves at 1.46 core widths. Even at the bottom of its range, a drawn core is therefore wider at
half its peak than M31's nucleus.

**From a stream of its own** (the cell's seed with a fixed mask, as `--channel-wing-cut` does), so every other draw is the
one it was. Pinned in `SyntheticBackgroundTests`:
- the drawn widths, and the half-peak area on the drawn plane;
- the peak in sigma and its colour;
- the ceiling;
- M31's wing readings;
- the redder-colour pool;
- isolation: beyond a core's reach every pixel is the cell's own, byte for byte, and inside it the core alone is added.

**The sample export** (synth4's recipe, the 8 R2 validation sessions, 40 cells, one draw each, once with `--kept-cores` and
once without; scratch `C:/temp/tianwen-scratch/TIA1658`, `measure_cores.py`):

| | without | with |
|---|---|---|
| cells holding a core | 0 of 320 | **50 of 320 (15.6 %)**, 4 to 9 per session |
| tiles of the 270 cells without a core (clean, draw, sigma) | 1,080 | the same 1,080, **byte for byte** |
| injection and degradation manifests | | identical, but for the 50 cells' `Core` field and clean-tile MAD |
| cores' drawn peak | | median 1,088 sigma, 212 to 5,404 |
| cores held by the ceiling | | 10 of 50, 8 of them on HIP 80609's three halves, the brightest stretched pixel 0.98 |
| core height, input over target (stretched, 5 x 5 at the centre, less the cell without the core) | | **median 1.0000, 0.982 to 1.000** over every core and channel |

The core is the same in input and target. The few points under 1 are injected stars landing on a core's centre, which
the input holds and the target does not. The cores' minor-axis FWHM ran 4.6 to 12.1 px over these PSFs, and their colour
ran from Small-Magellanic-Cloud's red (0.39 / 0.17 / 0.09 stretched) to HIP 80609's near-white. Training on them waits
for the GPU and for the owner's call on which arm carries them.

#### Halos left around bright stars: an option to remove them, deferred (2026-10-18)

The owner's first look at E6 on whole frames: "we mostly leave some halos around". A bright star leaves a soft disc in
the starless frame, on Andromeda and the Sagittarius Triplet alike, wider than the 66 px the net can see. Overlap and
averaging cannot reach it. R2n tests whether more reach removes it. If it does not, or not fully, the owner wants an
option that just removes the halos. Deferred by the owner, and tracked by #1659.

The comparison on the owner's frames that day (E6, StarNet2's exe and StarXTerminator, whole frames, no truth) found
that each remover gives something up. SXT removed the most stars but took the most nebula with them (on M8, 9.7 % of the
nebula's light away from stars, against E6's 6.0 % and StarNet2's 7.6 %). In the owner's experience it often takes a good
part of the Milky Way into its stars layer too. E6 kept the nebula and the sky best, but left halos and a few faint stars,
and took part of M31's nucleus (16.5 % of its red, where SXT and StarNet2 took about none).

#### R2n's read: the deeper net blew up at step 12,200 and never recovered, so reach is untested (2026-10-19)

Trained on the 1070 from 19:40 to 23:51 on 2026-10-18 and scored by the same binary as R2i (`C:/temp/e2/read_r2no.py`).
At step 12,000 its held-out objective was 8.5e-6 and still falling. Over the next 400 steps the training loss went from
1e-5 to 3.7 and then 27.7, and the held-out objective rose 150-fold to 1.3e-3. It came down again only slowly, never
regained its step-12,000 best (9.3e-6) through four halvings, and stopped at 72,000 for want of a gain. The trainer
saves the last weights, which held out at 1.2e-5, against 2.4e-6 for R2i's seed 1 and 2.5e-6 for R2o. 39 logged steps
of R2n's loss sit above 1e-3; R2o, on two levels, logged none.

| | R2i | R2n |
|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 29.8 / 24.3 / 0.8 |
| A's draws: dug 100-1000 sigma / clean saturated | 28.7 / 46.6 | 57.1 / 3.7 |
| real stars, 5-20 / 20-100 / 100-1000 sigma within a sigma | 94.8 / 97.1 / 96.4 | 53.5 / 81.4 / 66.4 |
| ring, 3-6 px (R / G / B): mean; a channel dug past -1 / left past +1 | -0.40 / -0.56 / -0.33; 43.1 / 12.4 % | +1.28 / +1.62 / +0.59; 19.4 / 71.0 % |
| M31, central 6 px taken (R / G / B) | 3.1 / 3.1 / 0.3 % | 24.5 / 36.3 / 26.5 % |

Predictions 1, 2, 3, 5 and 6 miss by far. Prediction 4 holds on its letter only: the annulus is dug on fewer stars
because it is left bright on 71 % of them.

**What it decides.** The kill's condition is met (0.8 % against 60.2), but its reading, that reach is not what the bright
stars lack, needs a net that trained, and this one blew up. So the arm is **void, not killed**: reach is still untested.
The blow-up belongs to the deeper net at R2i's recipe (learning rate 2e-4 from the first step, no warm-up, no gradient
clipping, no normalisation layers); no two-level run of the line has shown one. A rerun needs one stabiliser, pre-registered
as its own arm, and gradient-norm clipping is the smallest change that leaves the recipe otherwise R2i's. Whether it runs,
and before what, is the owner's call.

#### R2o's read: training on the views moves none of their average into the weights, and the nucleus goes (2026-10-19)

Trained on the Azure spot T4 from 21:14 on 2026-10-18. It was evicted once (about 03:10) and resumed from its
step-144,000 checkpoint, halved four times, and stopped on its own at 150,500 (best 2.55e-6 at 102,500; R2i's seed 1,
2.44e-6 at 142,000). The driver fetched both models at 03:45, deallocated the VM and scored them by the same binary as R2i.
The VM and its network were deleted at 07:52, and the run's SAS file with them. October's spend in both groups, R2i's seed
1 and R2o together, was about A$5.50 as Azure reported it the next morning (its cost data lags by a day), two fifths
of it the storage account's egress to the VM's region.

| | R2i | E2: R2i, 8 views | R2o | R2o raw | R2o, 8 views |
|---|---|---|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 80.1 / 76.4 / **66.2** | 80.3 / 74.7 / 56.8 | 80.3 / 74.7 / 56.5 | **81.3 / 76.8** / 63.5 |
| A's draws: dug 100-1000 sigma | 28.7 | **24.8** | 33.2 | 33.2 | 28.9 |
| A's draws: clean in colour 100-1000 / clean saturated | 21.3 / 46.6 | **31.0** / 52.1 | 18.1 / 47.0 | 15.5 / 45.2 | 26.3 / **58.0** |
| plain / lobed twin: clean 100-1000 sigma | 61.2 / 61.0 | | 58.1 / 56.8 | | |
| real stars, 5-20 / 20-100 / 100-1000 sigma within a sigma | 94.8 / 97.1 / 96.4 | | 96.2 / 98.4 / 95.9 | 96.5 / 98.4 / 95.0 | |
| real stars, 100-1000 sigma dug | 1.47 | | 2.89 | 4.08 | |
| ring, 3-6 px: a channel dug past -1 | 43.1 % | 42.6 % | 44.3 % | 43.0 % | 44.2 % |
| M31, central 6 px taken (R / G / B) | 3.1 / 3.1 / 0.3 % | | 40.4 / 32.2 / 29.3 % | | |

1. **One model, one view, bright stars at least 64.5 %: misses**, 56.8 (3.4 below R2i, inside the 4.3 spread).
2. **Its views agree, 8 views gaining at most 3.0 points: misses**, +6.7 (R2i's views gained 6.0).
3. **The raw model alone at least 64.5 %: misses**, 56.5. The weight average adds 0.3.
4. **The faint bands hold: holds**, 80.3 and 74.7.
5. **M31's nucleus stays under 5 %: misses**, 40.4 / 32.2 / 29.3 %, more than R2l took (35.9 / 14.9 / 14.9).

**The kill fires** (56.8 below R2i's 60.2 at one view), inside the seed spread but with no gain to show.

**What it decides.** The arm assumed the views disagree because the net had never seen them, so that training on them
would make them agree and leave nothing to average. Trained on all eight, its views still gain as much as R2i's (6.7
against 6.0). So the gain is the average's own: eight views make eight partly independent errors, and averaging cancels
them however the net was trained. A single model does not get it this way. The weight average buys nothing at the end of
a schedule halved four times, where the live weights barely move. The pre-registered next is distillation from E5 (one
model trained toward E5's output, never E6's, which carries R2l's appetite for nuclei).

**The nucleus is not R2l's alone.** R2o carries no bright-core weight and takes more of M31's red nucleus than R2l did. It
also digs bright stars more than R2i (33.2 % against 28.7 on A's draws, 2.89 % against 1.47 on real stars), which goes
with taking a bright compact core, though nothing here shows which causes which. It is not the T4's arithmetic: R2i's
seed 1 trained there and took -0.9 / 4.8 / 1.8 %. So #1658 (M31's nucleus taken as a star) is not fixed by leaving R2l
out of an average. A member's nucleus has to be measured before it ships, as E5's two members were, and the line needs
an objective or a training scene that holds a bright core that is not a star.

#### R2nc: R2n with its gradient clipped, pre-registered (2026-10-19)

Written before it trains, on the owner's word of 2026-10-19 ("R2n and R2m both on Azure"). Tracked by #1338. R2n blew up
at step 12,200 and so never tested reach. R2nc is R2n exactly (R2i's recipe with `--levels 4 --max-ch 256`, seed 0, the
synth4 cache, patience 24, cap 480,000, a resume checkpoint every 2,000 steps), with one stabiliser:

- `--clip-grad 8`: each step's gradient norm held to 8 times the median of the last 200 steps' norms (none before step 50).

Adam already bounds a step to about three learning rates per weight. But a step whose gradient is a hundred times the
recent ones moves every weight that far at once, along that one batch's direction. Held to 8 times, the same step moves
about half as far. An ordinary batch with bright stars passes untouched. On the first 300 steps of a test it bound 2 of
100 steps, and a run killed after step 100 resumed to the same held-out score at step 200, to every digit. It trains on
an Azure spot T4 under the owner's guards, as R2i's seed 1 and R2o did. Its driver deletes the VM itself once the weights
are fetched (the owner's correction of R2o's, which left that to the session).

**Predictions.** R2n's six with R2n's bars: (1) bright stars clean at least 64.5 %, (2) saturated at least 50.0 %, (3) the
faint bands at least 77.9 and 73.7 %, (4) the ring dug past -1 sigma on at most 38 % of bright stars, (5) real bright
stars flat on at least 96.4 %, and (6) M31's central 6 px taken at most 5 % in every channel. One more:

7. No blow-up: no logged training loss above 1e-3 after step 2,000.

**Read, not predicted:** how often the clip binds after step 2,000.

**Kill:** if (7) holds and bright stars clean below R2i's 60.2 %, reach is not what they lack, and the deeper net is
dropped. If (7) fails, the arm is void again and the stabiliser was not enough. **What it decides:** as R2n's.

#### R2p: one model distilled from E5's average, pre-registered (2026-10-19)

Written before it trains, on the owner's word of 2026-10-19 ("distillation on this box"), and the next step R2o's kill
named. Tracked by #1338. R2o showed that the averaging's gain is the average's own: eight views make partly independent
errors that cancel, whatever the net was trained on. So one model must be TOLD the average's answer. E5 is the teacher
(R2i's two seeds, eight views each), never E6, which carries R2l's appetite for nuclei.

**The teacher.** `n2n_teacher.py` runs E5 over every injected draw of the synth4 cache (4,840 cells x 4 draws), each
member through its eight views and turned back, as `r2_infer.py` averaged it for scoring. It writes `teacher.f16` in the
cache's own order, with `teacher.json` naming the members, their hashes and the cache. It took 30 minutes on the 1070. Over 60 draws its
output sits 400 times closer to the clean tile than the input does (squared error 1.1e-6 against 4.6e-4), with no
non-finite pixel.

**The arm.** R2i's recipe exactly (two levels, seed 0, patience 24, cap 480,000, a resume checkpoint every 2,000 steps),
with one switch:

- `--teacher`: each draw regresses onto the teacher's output on that same draw instead of the clean tile.

The held-out objective still reads the clean tile, so the schedule stops on the truth, not on the teacher. The draws and
the generator's stream are R2i's. One caveat: the teacher's members trained on these draws, so the teacher is better on
them than on draws it never saw. The student therefore learns a target between the truth and E5's answer on new data.
It trains on the 1070, while R2nc and R2m have the T4s.

**Predictions.** The bars are the same-store spread (1.3 / 0.8 / 4.3 points clean at 5-20 / 20-100 / 100-1000 sigma). E5
cleans 81.4 / 78.0 / 66.7 %, digs 24.7 % at 100-1000 sigma, and reads 97.8 % of real bright stars flat.
1. One model, one view: bright stars clean at least 64.5 % (R2i's 60.2 plus the spread), most of the way to E5's 66.7.
2. The faint bands hold: 5-20 sigma at least 77.9 % and 20-100 at least 73.7 %.
3. Real bright stars (`r2-real-val`, 100-1000 sigma) flat on at least R2i's 96.4 %.
4. M31's nucleus stays: at most 5 % of the central 6 px in every channel. E5's members took at most 4.8 % each.

**Read, not predicted:** R2p's own eight views' gain, and R2p against E5 band by band.

**Kill:** bright stars below R2i's 60.2 % at one view means distillation does not carry the average into one model
either. The single-model target then stops, and a shipped model is an average (which one is the owner's call). **What it
decides:** whether one model at an eighth of E5's inference carries most of its gain.

#### R2m: every injected star a stack of virtual subs, pre-registered (2026-10-19)

Written before its export runs, on the owner's word of 2026-10-19 ("R2n and R2m both on Azure"), and on the owner's own
idea of 2026-10-17 ("Real stars are noisier at their cores than injected ones", above). Tracked by #1338. A real master's
star is the average of its subs, each seen, guided and registered a little differently, so the star departs from a
smooth profile at its core by 2 to 5 % of its peak a pixel beyond its shot noise. An injected unsaturated star has been
its exact profile. A saturated one already is a stack: 8 virtual subs, each with its own amplitude and width (5 %
scatter) and offset (0.25 px a side). That was made to match the masters' own plateaus.

**The switch.** `dataset degrade --star-subs` renders EVERY injected star as that stack, an unsaturated one with no clip
(`StarInjection.Render(stackAll)`). No new constant. At the saturated path's own scatter, an unsaturated star departs at
its 3x3 core by a median 4.0 % of its peak as (A - B) / 2 (a simulation over Moffats of FWHM 1.8 to 3.0 px: 3.3 to 4.2 %).
That is the statistic the half pairs were read with. Against its exact profile it departs by 5.3 %
(`StarInjectionTests`), and it keeps its light. The half pairs read 3.3 to 5.3 % at 10 to 30 sigma and 2.9 to 4.2 % at 30
to 100 sigma, as `sqrt(r^2 - 1)` over the band's middle peak. The noise plane is left as it is, since a real tile's
plane is computed the same way.

**The arm.** The export is R2i's command exactly (the same store, plates, sessions, seed 1, synthetic background, steer
and knots) plus `--star-subs`, into `r1-train-synth6`. Its cache `r2-synth6` has 4 draws and the same night split. On it
runs R2i's recipe exactly (two levels, seed 0, patience 24, cap 480,000, a resume checkpoint every 2,000 steps), on the
second Azure spot T4. It is scored on the same eval as every arm (A's draws and both twins, all exact-profile stars) and
on the validation masters' real tiles, whose stars carry the jitter.

**Predictions.** The bars are the same-store spread (1.3 / 0.8 / 4.3 points clean at 5-20 / 20-100 / 100-1000 sigma) and
the real-star check's counting noise (about 0.3 points on its 4,215 bright stars).
1. Real bright stars (100-1000 sigma) flat on at least 97.0 % (R2i's 96.4 plus twice the counting noise).
2. Real bright stars left bright (past +1 sigma) on at most 1.5 % (R2i's 2.11).
3. A's draws hold within the spread: clean at least 77.9 / 73.7 / 55.9 % at 5-20 / 20-100 / 100-1000 sigma.
4. M31's nucleus stays: at most 5 % of the central 6 px in every channel.

**Kill:** real bright stars flat below R2i's 96.4 % means the injected jitter does not carry to real stars, and the switch
is dropped. **What it decides:** whether `--star-subs` becomes the line's export. With (1) and (3) holding, it does, and
every later arm and average starts from it.

#### R2nc's read: the clipped deeper net trains cleanly and gains on faint stars, not on bright ones (2026-10-20)

Trained on the Azure spot T4 on 2026-10-19. It was evicted once (found deallocated at 15:20, started again and resumed
from its checkpoint), halved four times and stopped on its own at step 184,500, its held-out best 2.10e-6 at 172,500
(R2i's seed 1, 2.44e-6; R2o, 2.55e-6). The driver fetched the weights at 18:50 and deleted the VM, its disk and its
network itself at 18:51, then scored them by the same binary as R2i (`C:/temp/e2/read_r2nc_r2p.py`).

| | R2i | R2nc |
|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | **82.1 / 77.3** / 61.8 |
| A's draws: clean in colour 5-20 / 20-100 sigma | 57.3 / 43.7 | **64.0 / 48.9** |
| A's draws: dug 100-1000 sigma / clean saturated / dug saturated | 28.7 / 46.6 / 28.3 | 31.2 / **56.2** / 35.2 |
| A's draws: footprint RMS | 2.50 | **1.05** |
| plain / lobed twin: clean 100-1000 sigma | 61.2 / 61.0 | 62.0 / 60.4 |
| real stars, 5-20 / 20-100 / 100-1000 sigma within a sigma | 94.8 / 97.1 / 96.4 | 96.5 / 98.4 / 95.0 |
| real stars, 100-1000 sigma dug / left bright | 1.47 / 2.11 | 4.06 / 0.90 |
| ring, 3-6 px (R / G / B): mean; a channel dug past -1 / left past +1 | -0.40 / -0.56 / -0.33; 43.1 / 12.4 % | -0.68 / -0.56 / -0.54; 53.1 / 7.9 % |
| M31, central 6 px taken (R / G / B) | 3.1 / 3.1 / 0.3 % | 3.9 / -0.3 / 1.2 % |

1. **Bright stars clean at least 64.5 %: misses**, 61.8 (1.6 over R2i, inside the 4.3 spread).
2. **Saturated stars clean at least 50.0 %: holds**, 56.2. They are dug more too (35.2 against 28.3).
3. **The faint bands at least 77.9 and 73.7 %: holds**, 82.1 and 77.3, both past the spread over R2i.
4. **The ring dug on at most 38 % of bright stars: misses**, 53.1 %, deeper than R2i's 43.1 in red and blue.
5. **Real bright stars flat on at least 96.4 %: misses**, 95.0, and dug on 4.06 % of them against R2i's 1.47.
6. **M31's nucleus at most 5 % in every channel: holds**, 3.9 / -0.3 / 1.2 %.
7. **No blow-up: holds.** No logged training loss above 1e-3 after step 2,000; the held-out objective fell to the end.

**Read, not predicted:** after step 2,000 the clip bound 66 of 183,500 steps (0.04 %), at most 6 in a window of 500
(at step 7,500), so the stabiliser acted only on the odd batch and changed nothing else.

**The kill does not fire** (61.8 is above R2i's 60.2), but the gain it would need is not there either: inside the seed
spread at one view, and on real stars the deeper net digs bright stars three times as often as R2i does.

**What it decides.** Clipping was enough: four levels train cleanly at R2i's recipe, so R2n's blow-up was the
instability and not the depth. With that settled, reach buys the FAINT stars (about 3 points clean, 5 to 7 in colour,
and the tightest footprint of the line) and not the bright ones, which it digs harder on A's draws, on the ring and on
real stars. So prediction (1) fails and four levels do not become the recipe: the base net stays at two levels, and
what the bright stars lack is not reach. A deeper member could still serve an average for its faint-star gain, which
is not measured here.

#### R2p's read: one model told E5's answer carries most of E5's gain, and its own views pass E5 (2026-10-20)

Trained on the 1070 from 08:36 to 18:55 on 2026-10-19 toward E5's output (`teacher.f16`), halved four times and stopped
on its own at step 224,500, its held-out best 2.30e-6 at 200,500 (the held-out objective reads the clean tile, not the
teacher). Scored by the same binary as R2i, at one view and over its own eight views (`C:/temp/e2/read_r2nc_r2p.py`).

| | R2i | E5 | R2p | R2p, 8 views |
|---|---|---|---|---|
| forward passes per tile | 1 | 16 | 1 | 8 |
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | **81.4** / 78.0 / 66.7 | 80.6 / 77.2 / 65.8 | 81.0 / **78.3 / 69.5** |
| A's draws: dug 100-1000 sigma | 28.7 | 24.7 | 25.9 | **23.3** |
| A's draws: clean in colour 100-1000 / clean saturated | 21.3 / 46.6 | **33.1** / 54.3 | 25.3 / 54.8 | 29.7 / **58.9** |
| plain / lobed twin: clean 100-1000 sigma | 61.2 / 61.0 | | 66.6 / 65.9 | |
| real stars, 5-20 / 20-100 / 100-1000 sigma within a sigma | 94.8 / 97.1 / 96.4 | 96.3 / 98.4 / 97.8 | 96.1 / 98.7 / 97.8 | |
| real stars, 100-1000 sigma dug / left bright | 1.47 / 2.11 | 1.09 / 1.07 | 1.00 / 1.19 | |
| ring, 3-6 px: a channel dug past -1 / left past +1 | 43.1 / 12.4 % | 38.9 / 10.3 % | 37.2 / 11.2 % | 36.5 / 11.1 % |
| M31, central 6 px taken (R / G / B) | 3.1 / 3.1 / 0.3 % | 1.1 / 3.9 / 1.1 % | 1.2 / 4.5 / 1.8 % | |

1. **One model, one view, bright stars at least 64.5 %: holds**, 65.8 against E5's 66.7.
2. **The faint bands hold: holds**, 80.6 and 77.2.
3. **Real bright stars flat on at least 96.4 %: holds**, 97.8, as E5, and dug on 1.00 % of them.
4. **M31's nucleus at most 5 % in every channel: holds**, 1.2 / 4.5 / 1.8 %, green within half a point of the bar
   and above E5's 3.9.

**Read, not predicted:** R2p's own eight views gain 3.7 points on bright stars (R2i's gained 6.0), so the distilled
model's views still disagree and averaging them still pays. Against E5 band by band, R2p at one view sits within a
point everywhere but colour on bright stars (25.3 against 33.1); over its eight views it passes E5 on bright and
saturated stars and digs less, at half E5's passes, and still trails it in colour on bright stars.

**The kill does not fire.**

**What it decides.** Distillation carries the average into one model where training on the views did not (R2o): one
pass gets 5.6 of E5's 6.5 points over R2i on bright stars, and matches it on real stars, at a sixteenth of its passes.
The colour of bright stars is the part that does not carry. A shipped single model is now possible; which model ships
(R2p alone, R2p over its views, or an average) is the owner's call. The teacher caveat stands: E5's members trained on
these draws, so R2p learned a target a little better than E5 would be on new data, and the real-star check, where E5
never trained, is the one that says it held (97.8 against 97.8).

#### R2m's read: jittered cores do not carry to real stars, and the nucleus goes (2026-10-20)

Trained on the second Azure spot T4 overnight on 2026-10-19, evicted once (found deallocated at 03:41, resumed from its
checkpoint) and stopped on its own at step 107,500, its held-out best 2.73e-6 at 59,500 (its own synth6 held-out set,
not comparable to the synth4 runs'). The driver deleted the VM itself at 05:32 and scored the weights; the run's SAS and
tasks were removed the same morning, leaving the group empty.

| | R2i | R2m |
|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 | 80.1 / 74.4 / 57.7 |
| A's draws: clean in colour 100-1000 / clean saturated | 21.3 / 46.6 | 14.6 / 44.3 |
| real stars, 100-1000 sigma within a sigma / left bright / dug | 96.4 / 2.11 / 1.47 | 96.1 / 2.30 / 1.57 |
| ring, 3-6 px: a channel dug past -1 / left past +1 | 43.1 / 12.4 % | 35.7 / 16.6 % |
| M31, central 6 px taken (R / G / B) | 3.1 / 3.1 / 0.3 % | 37.3 / 43.5 / 40.9 % |

1. **Real bright stars flat on at least 97.0 %: misses**, 96.1.
2. **Real bright stars left bright on at most 1.5 %: misses**, 2.30.
3. **A's draws hold within the spread: holds**, 80.1 / 74.4 / 57.7.
4. **M31's nucleus at most 5 %: misses by far**, 37 to 44 % in every channel.

**The kill fires** (96.1 below R2i's 96.4), so `--star-subs` is dropped and the line's export stays synth4. **What it
decides.** Rendering every injected star as a stack of jittered subs does not make the net clean real stars better; it
teaches it that a bright core with structure is a star, and it takes M31's nucleus as R2o and R2l did, more than either.

#### Shipping R2p: Tianwen's own star remover (2026-10-20)

Tracked by #1702. The owner's decision of 2026-10-20 ("R2p it is"), on the reads above: R2p is the line's first
shipped star remover. It is one model at R2i's size (3.3 MB of ONNX, its contract beside it), so a tile costs one pass
where E5 costs sixteen, and it matches E5 on real stars and keeps M31's nucleus (4.5 % at most). What shipping takes:
the weights and their contract into `src/TianWen.AI.Imaging/models/` beside the denoiser and the deconvolver; the
remover registered as Tianwen's own `IStarRemover`, served by `--ai-backend tianwen` and wherever RC-Astro is absent, so
the canonical program splits the stars instead of running whole-frame; its views a setting (one pass by default, eight as
the measured quality step); and CLAUDE.md's "none ship and none are registered" rewritten with it. Core protection
(`--protect-cores`, #1658) applies to it as to any remover.

#### R2q: R2p's recipe with synthetic crowded clusters beside synth4, pre-registered (2026-10-21)

Written before it trains, on the owner's word of 2026-10-21 ("start the training both local and Azure"). Tracked by #1338.
R2p was trained on synth4 alone: single injected stars on real plates, with no crowded cluster and no unresolved glow in
any tile. On the held-out crowded field, the SV605CC Tarantula of 2026-10-17 (held out on 2026-10-20 for exactly this
test), it takes the cores and leaves the halos: measured before this run by `remover-check2.py` and
`gaia-remover-check.py`, the checks of 2026-10-20, at one view:

| R2p on the held-out Tarantula 2026-10-17 | 20-100 sigma | 100-1000 sigma | over 1000 sigma |
|---|---|---|---|
| dark sky: flat / light left | 58.3 / 40.6 % | 29.6 / 68.3 % | 7.9 / 90.4 % |
| crowded (3 or more within 15 px): flat | 70.2 % | | |
| Gaia: removed over half, BP 13-15 / 15-17 / 17-19 | 97.3 / 96.4 / 79.5 % | | |
| Gaia purity: stars-layer light away from every Gaia star | 0.7 % | | |

**The data.** The synthetic crowded clusters (the section on them carries the generator, its gates and the owner's calls
of 2026-10-20 and 21): 90 skies drawn from the training-side ZWO ASI585MC Tarantula of 2024-10-02, never the held-out
masters, in measured and lateral optics, a nebula in 61 of them, 16 cells a scene. That is 2,880 input tiles, 14.9 % of
synth4's 19,360: 2,592 for training and 288 held out, every tenth seed held out whole. The window is x 2553-3577, y
678-1702 of the ASI585 frame, on the crowded LMC field east of 30 Doradus (7,590 catalogued stars), its nebula tile 378 px
clear of R136 at every rotation. Two windows west of it were rejected: the first, beside R136, printed black holes into 61
skies' nebula (the texture placed at the 64 px structure's full strength where the plate's fine structure is weakest), and
every tile west of 30 Doradus that keeps R136 out holds two bright compact HII knots that print the same holes. So the
nebula here is faint (about 2 noise over the sky against 11 in the west), and the "nebula" skies carry little of it. The
truths' gates: none star-like above 0.25 noise (largest 0.063), the light to 1.0001; the cell gate (the source plate's
own ratio plus 0.05) fails 6 of 12 truths by 0.01 to 0.31 at its floor, where the cell bands are 0.04 to 0.24 noise and
the excess is 0.02 to 0.03. Three of the six are no-nebula truths, which hold no plate content and so no fill, and the
excess borders the cluster cores, so it is read as the gate's floor and the cluster glow's own grain, not fills.

**The arm.** R2p's recipe exactly (R2i's, two levels, patience 24, cap 480,000, `--teacher` E5 on every synth4 draw, a
resume checkpoint every 2,000 steps), with one switch:

- `--extra-cache r2-clusters-cache`: the cluster tiles join the training draw by their share of the input tiles (12.5 %
  beside synth4's 18,080 training tiles). A cluster tile regresses onto its own truth, the starless scene with the SAME noise
  draw as its input (input less target is the stars' light and their own shot noise only, synth4's convention); its loss
  mask is all ones, since its truth holds no star a plate kept and its knots must be kept. synth4 draws keep E5's answer.
- The held-out objective reads the truth on both caches and is logged in three parts: synth4, clusters, and the two
  combined by share (0.875 / 0.125). The schedule stops on the combined one.

Seed 0 trains on the 1070; seed 1 on an Azure spot T4 under the owner's guards (its driver deletes the VM, disk and network
once it has fetched and loaded the weights). Without `--extra-cache` the trainer is bit-identical to R2p's (40 steps, 30
of 30 tensors).

**The bars** are the line's same-store spread (1.3 / 0.8 / 4.3 points clean at 5-20 / 20-100 / 100-1000 sigma), with
4.3 points for any brightness class of a real master, whose own seed spread is not measured.

**Predictions**, seed 0 at one view:

1. The crowded field improves: on the held-out Tarantula, crowded stars flat on at least 74.5 % (R2p's 70.2 plus 4.3), and
   dark-sky stars at 100-1000 sigma flat on at least 33.9 % (29.6 plus 4.3).
2. Nothing else is taken for it: Gaia purity at most 1.0 % of the stars layer, and BP 17-19 removed on at least 77.5 %.
3. A's draws hold within the spread of R2p: at least 79.3 / 76.4 / 61.5 % clean.
4. Real bright stars (`r2-real-val`, 100-1000 sigma) flat on at least R2i's 96.4 %.
5. M31's nucleus stays: at most 5 % of the central 6 px in every channel.
6. The owner's three regression masters (`C:/temp/startnet2`: Andromeda, the Sagittarius Triplet in OIII, and the third)
   do not regress, read by `owner-regression-read.py` against R2p's baseline of 2026-10-21: dark-sky stars at 100-1000
   sigma flat on at least 35.0 % on Andromeda (R2p 39.3) and 94.5 % on the Sagittarius Triplet (R2p 98.8), and the crops
   no worse by the owner's eye.

**Read, not predicted:** the blue dig at bright stars on colour masters (on Andromeda R2p's dug stars sit at -0.75 sigma
in blue and above zero in red and green; on the mono Triplet it digs none), R2q's own eight views, the clusters' share of
the held-out objective, and seed 1 against seed 0, which is this arm's own spread.

**Kill:** prediction 1 missing means the cluster tiles do not carry to a real crowded field, and the line keeps R2p. Any of
3 to 6 missing means R2q is not shippable whatever 1 says; which costs are acceptable is then the owner's call.
**What it decides:** whether synthetic crowded fields with an exact truth teach a remover what real crowded fields need.

#### Synthetic crowded clusters: scenes drawn from one master (2026-10-20)

`tianwen dataset synthetic-cluster` (`SyntheticClusterScene`, `SyntheticClusterRun`) composes a crowded-cluster scene from
one real master's own measurements: a King-profile cluster and a field of resolved stars whose brightness and colour are
drawn from its starless plate's catalogue, the LMC's own compact clusters as groups (their sizes, star counts and light
measured on the master), the stars too faint to find, a nebula, and the master's noise in its shape, in three optics and
any sky rotation of one sky. It writes the composite, the stars truth, the starless truth, the starless truth with the
composite's own noise draw, the faint stars the starless light stands for, and the resolved stars' list.

**The starless truth holds nothing the eye reads as a star** (the owner, 2026-10-20): the faint population's smooth
expected light with its own grain from 16 px up, never its points; a compact cluster (half-light radius under 16 px) is
all stars; the nebula is the background generator driven by the plate's scales from 64 px up, its texture's strength per
scale read off the plate at the same place, with no floor. Both the grain and the nebula's drawn texture stop at 16 px:
round texture finer than 16 px makes quasi-stars, and knots carry the fine detail (the owner, 2026-10-20). With
`--nebula-knots` the nebula carries the background generator's nebula knots (a compact one held faint), as many per 256 px
square of the tile as the exporter's cells hold (three, times the share of the cell that is nebula), each listed per scene
in `_knots.csv`. The knots are mostly elongated, 2.5 to 5 to one (`SyntheticBackground.ElongatedKnots`, the owner,
2026-10-21): a fifth are round, and a round one is at least four PSF widths across and peaks under 20 noise sigma.
Every render is gated before any picture: no local maximum of 12 px or less, with a second-moment axis ratio under 2,
over 0.25 noise above its 16 to 32 px surround on the knot-free pixels (the knots read on their own: how many, the
brightest's peak against its own width, and how many the same test would call a star); at cell scale (a 16 px mean less a
64 px median) the ratio of the plate's filled cells to its least-filled half at most the nebula plate's own ratio at the
same place and turn plus 0.05; and the starless light equal to the faint stars it stands for.

**The noise keeps synth4's convention.** The noisy starless truth is the truth plus one draw of the master's noise at its
own level (`SyntheticClusterSource.NoiseCalibration`, the exporter's own `NoiseCalibration` rule), and the composite is
that noisy truth plus the stars plus the stars' own shot noise, a second, independent draw at
sqrt(sigma(composite)^2 - sigma(starless)^2) (`SyntheticClusterScene.AddNoise`): an injected draw over its plate. So the
composite less the noisy truth is the stars and their own noise only, and a net trained toward the noisy truth keeps the
frame's noise, as on synth4.

**The source is the ASI585MC Tarantula of 2024-10-02** (broadband, ZS61, 2.07 arcsec a pixel), the owner's choice of
2026-10-20. The first scenes were drawn from the SV605CC Tarantula of 2026-10-17, which `dataset-parameters.json` holds out
as the crowded-field TEST session (and the same field, camera and filter's night of 2025-10-18 is a test session by
bucket): a test session's stars, noise and nebula must never reach training, and releasing it would cost the LMC as a test
field, the nearest crowded broadband one left being the SMC. So those scenes are development only and are never exported.
The ASI585 night is on the training side and frames the same LMC field. Its scenes are drawn at the window x 2553 to 3577,
y 678 to 1702, the nebula from the same place on its R0 plate as it is (the owner, 2026-10-21: under the saturated stars
the plate's large-scale level against a 60 to 120 px ring has a median of +0.06 noise and 4 % pits, as at random places).
**The nebula tile (1,453 px, every rotation's source) must hold no bright compact nebula** (2026-10-21). The texture's
strength follows the window's own fine-scale RMS, placed by the plate's 64 px power, and a Gaussian texture at a bright
knot's strength dips 10 to 26 noise below its surroundings: black holes the gates do not see (their 4-16 px band 2.15 times
the plate's beside R136 at the first window, x 790 to 1814, whose tile held R136). Left of 30 Doradus every window whose
tile keeps R136 out holds the bright HII knots near frame x 810 to 1070, y 986 to 1066, which drew holes too, so the window
is right of it: tile x 2339 to 3792, y 464 to 1917, R136 378 px outside it, 7,590 catalogued stars, and a faint nebula
(about 2 noise over the sky against 11 on the left). There the drawn 4-16 px band is at most 0.84 times the plate's in
any 256 px column and nothing dips more than 3.1 noise below its 64 px surround. The cell gate reads at its floor: the plate's
own ratio is 0.94 to 1.03 and the bands 0.04 to 0.24 noise, and six of twelve truths pass it narrowly or not (1.00 to 1.30
against limits of 0.99 to 1.08), from the compact cluster's own glow in the filled cells (a no-nebula truth, 1.19) and the
drawn texture's placement (the nebula alone 1.11 to 1.14 against the plate's 0.94 and 0.95, +0.02 to +0.03 noise).
Its own numbers at 2.07 arcsec a pixel: field density 0.0060 a square pixel and faint slope 1.12, lateral colour R +97.9
and B +115.0 ppm, the LMC clusters' half-light radii 5.8 to 21.0 px, resolved stars 2 to 15 and unresolved light 2.5 to 18.

#### The synthetic clusters as training tiles (R2q, 2026-10-21)

`tianwen dataset synthetic-cluster --export-skies N` (`SyntheticClusterExport`) draws N skies of the ASI585 master, each
its own rotation, nebula (two in three) and cluster (1,500 to 3,000 resolved stars, a core of 20 to 90 px, a tidal radius
of 250 to 500 px and at least three cores, up to eight groups), renders each in measured and lateral optics, and cuts
every 1024 px scene into sixteen 256 px cells laid out as a Stars-mode export's, so `n2n_smoke.py --prepare` packs them
unchanged (`C:\temp\e2\clusters-cache.sh`; `--compact` keeps only the truth and its one draw, two slots a cell where the
usual eleven would leave nine empty): per cell the target is the noisy starless truth (`master`), the one draw the
composite (`deg000`), each with its noise plane, and the draw an all-ones loss mask (the truth holds no star a plate kept,
and its knots must count). Both are stretched with the plate's own parameters. Every tenth seed is held out whole, in both
optics and every cell, never a tile of a training sky (`clusters-train.txt`, `clusters-val.txt`). The export at the
window right of 30 Doradus, 90 skies (61 with a nebula), is 2,592 training and 288 held-out input tiles, 14.9 % of synth4's 19,360 and 12.5 % of the
training draw beside synth4's 18,080 training tiles (`C:\temp\tianwen-scratch\r2-clusters`, its cache `r2-clusters-cache`,
3.2 and 3.0 GB).

The trainer reads them beside synth4 with `n2n_smoke.py --extra-cache` (`run-r2.ps1 -ExtraCache`): the extra cache's
training cells join the draw by their share of the input tiles, each regressing onto its own truth, while synth4's draws
keep R2p's teacher; the plateau stops on both caches' held-out objectives, each against its truth, weighed by that share,
and logs the three. Without the option a run is bit-identical to the trainer before it (40 steps of R2p's recipe, every
tensor). Tested in `training/denoise/test_n2n_extra_cache.py` and `SyntheticClusterExportTests`.

#### R2q's read: the clusters carry to the real crowded field, and the costs come on a schedule half R2p's (2026-10-21)

Seed 0 trained on the 1070 from 03:02 to 08:44 on 2026-10-21 and stopped on its own at step 125,000, its combined
held-out best 2.51e-6 at 101,000. Seed 1 trained on an Azure spot T4 from 03:20, was evicted twice and resumed both
times, and stopped at step 121,500 (best 2.44e-6 at 97,500); its driver deleted the VM at 08:57 and listed the group
empty, and the run's SAS and tasks were removed the same morning. Both were scored by the binaries that scored R2p. The
local scoring stopped for an hour on a driver bug of its own (a helper named `Cli` is PowerShell's alias for
`Clear-Item`, which wins over a function), and the first regression read compared R2q's mono Sagittarius Triplet plates
with R2p's copies (the three-plane copy was named after the file alone); both were fixed and the reads below are re-run.

| | R2p | R2q | R2q, 8 views | R2q seed 1 |
|---|---|---|---|---|
| A's draws: clean 5-20 / 20-100 / 100-1000 sigma | 80.6 / 77.2 / 65.8 | 78.7 / 74.8 / 54.5 | 79.6 / 77.2 / 63.8 | 79.8 / 75.3 / 58.2 |
| A's draws: dug 100-1000 sigma / clean in colour 100-1000 | 25.9 / 25.3 | 31.4 / 15.7 | 25.2 / 23.5 | 29.8 / 21.3 |
| plain / lobed twin: clean 100-1000 sigma | 66.6 / 65.9 | 56.8 / 55.2 | | 60.1 / 58.7 |
| real stars, 5-20 / 20-100 / 100-1000 sigma within a sigma | 96.1 / 98.7 / 97.8 | 96.5 / 98.7 / 95.7 | | 97.1 / 98.8 / 95.5 |
| real stars, 100-1000 sigma dug / left bright | 1.00 / 1.19 | 1.40 / 2.94 | | 1.78 / 2.73 |
| ring, 3-6 px: a channel dug past -1 / left past +1 | 37.2 / 11.2 % | 51.2 / 13.3 % | 50.7 / 12.9 % | 47.0 / 13.3 % |
| held-out Tarantula: crowded stars flat | 70.2 % | 79.9 % | 80.2 % | |
| held-out Tarantula: dark sky flat at 20-100 / 100-1000 / over 1000 sigma | 58.3 / 29.6 / 7.9 % | 79.3 / 47.8 / 18.4 % | 79.4 / 48.5 / 18.4 % | |
| Gaia removed, BP 13-15 / 15-17 / 17-19 | 97.3 / 96.4 / 79.5 % | 99.4 / 97.9 / 88.6 % | 99.3 / 97.9 / 88.2 % | |
| Gaia purity (stars-layer light away from every Gaia star) | 0.7 % | 0.4 % | 0.4 % | |
| owner's Andromeda: dark sky 100-1000 sigma flat / dug | 39.3 / 45.9 % | 44.1 / 37.3 % | 43.3 / 39.5 % | |
| owner's Sagittarius Triplet (OIII): dark sky 100-1000 sigma flat | 98.8 % | 98.8 % | 98.7 % | |
| M31 on the owner's Andromeda, central 6 px taken (R / G / B) | 1.2 / 4.4 / 1.7 % | 13.0 / 13.7 / 8.8 % | 12.6 / 13.7 / 8.8 % | 4.6 / 4.7 / 4.6 % |

1. **The crowded field improves: holds**, crowded stars flat on 79.9 % (bar 74.5) and dark-sky stars at 100-1000 sigma
   on 47.8 % (bar 33.9). Every brightness class on the dark sky gains, the brightest from 7.9 to 18.4 %, and the stars
   left bright halve at 20-100 sigma (40.6 to 19.7 %).
2. **Nothing else is taken for it: holds**, purity 0.4 % (bar 1.0) and BP 17-19 removed on 88.6 % (bar 77.5).
3. **A's draws hold within R2p's spread: misses in all three bands**, 78.7 / 74.8 / 54.5 against 79.3 / 76.4 / 61.5; the
   bright stars by 7.0 points, dug on 31.4 % against 25.9, and their colour falls from 25.3 to 15.7.
4. **Real bright stars flat on at least 96.4 %: misses**, 95.7, with the stars left bright up from 1.19 to 2.94 %.
5. **M31's nucleus at most 5 %: misses**, 13.0 / 13.7 / 8.8 %. Seed 1 keeps it on the same file (4.6 / 4.7 / 4.6 %).
6. **The owner's masters do not regress: holds on the numbers**, Andromeda 44.1 % (bar 35.0, and dug on 37.3 % against
   R2p's 45.9) and the Sagittarius Triplet 98.8 % (bar 94.5); the crops (`C:/temp/e2/owner-regression/read-r2q/`) are
   the owner's to judge. By eye they sit with R2p's, and new-image's bright star leaves a fainter ghost.

**Read, not predicted.** The blue dig at Andromeda's bright stars is still there and a little smaller (the dug ones at
+0.22 / +0.33 / -0.68 sigma against R2p's +0.16 / +0.30 / -0.75), on fewer stars. R2q's own eight views recover A's
draws (79.6 / 77.2 / 63.8, inside all three bars) but move nothing on the crowded field, the real stars or the nucleus.
The clusters' part of the held-out objective sat near 2.15e-6 and flat from step 100,000 on. Seed 1 against seed 0 is
3.7 points on A's bright stars, inside the line's 4.3, and 8 to 9 points on the nucleus, the noisiest read here.

**The schedule is not R2p's, and it confounds 3 to 5.** The plateau rule halved the learning rate on the combined
objective far sooner than it did on synth4 alone:

| halved at step | to 1e-4 | to 5e-5 | to 2.5e-5 | to 1.25e-5 | stopped |
|---|---|---|---|---|---|
| R2p | 50,000 | 81,500 | 161,000 | 213,000 | 224,500 |
| R2q seed 0 | 33,000 | 63,500 | 79,000 | 113,500 | 125,000 |
| R2q seed 1 | 48,000 | 72,500 | 91,000 | 110,000 | 121,500 |

At step 100,000 synth4's part of the objective reads 2.52e-6 on R2q (seed 0) and 2.56e-6 on R2p at the same step; R2p's
best, 2.30e-6, came in the 124,500 steps after it, which R2q did not train. So this run cannot say whether the clusters
or the shorter schedule cost A's bright stars, the real bright stars and the nucleus.

**The kill does not fire on 1** (the cluster tiles carry to a real crowded field), **and 3 to 5 miss, so R2q is not
shippable and the line keeps R2p.** **What it decides.** Synthetic crowded fields with an exact truth teach a remover
what the real crowded field needed: on the held-out Tarantula R2q leaves half as many stars bright at 20-100 sigma,
takes nine more points of the faint Gaia stars, and puts less light where no star is. What it costs is not yet separated
from the schedule. The arm that would separate them is R2q's data on R2p's schedule, the plateau read off synth4's part
alone or the steps matched to R2p's; whether to run it is the owner's call.

#### R2r: R2q's data on R2p's schedule, pre-registered (2026-10-21)

Written before it trains, on the owner's word of 2026-10-21 ("yes", to R2r beside the halo arm). Tracked by #1338.

**The arm.** R2q exactly (R2p's recipe, `--teacher` E5, `--extra-cache` the synthetic clusters at 12.5 % of the draw),
with one switch: `--schedule-on main`, so the plateau halves the rate and stops on synth4's held-out part alone, as
R2p's schedule read it; the clusters' part and the combined one are still logged. Seed 0 on the 1070, R2p's seed and
hardware, so the read against R2p differs only in the clusters. Frozen at "feat(training): --schedule-on main, the
plateau read off the main cache's held-out part alone" on feat/synthetic-cluster-scenes; its resume fingerprint is R2q's
plus the switch. **Seed 1**, added the same afternoon before it trains (the owner: "why we not using azure as well?"), on
an Azure spot T4 under the owner's guards, its data read from the storage account in the VMs' own region: read, not
predicted, as R2q's seed 1 was, since R2q's two seeds lay 8 to 9 points apart on M31's nucleus. **Seed 1 was stopped at
step 45,800** the same day, when the owner stopped Azure ("lets stop azure for now and only use this box"; see R2s); its
resume checkpoint at step 44,000 is kept, so R2r reads on seed 0 alone unless seed 1 is resumed on the 1070.

**Predictions**, seed 0 at one view, every bar R2q's:

1. The crowded field keeps R2q's gain: on the held-out Tarantula, crowded stars flat on at least 74.5 % and dark-sky
   stars at 100-1000 sigma on at least 33.9 %.
2. Nothing else is taken for it: Gaia purity at most 1.0 %, BP 17-19 removed on at least 77.5 %.
3. A's draws hold within R2p's spread: at least 79.3 / 76.4 / 61.5 % clean.
4. Real bright stars flat on at least 96.4 %.
5. M31's nucleus on the owner's Andromeda at most 5 % in every channel.
6. The owner's masters do not regress: dark-sky stars at 100-1000 sigma flat on at least 35.0 % on Andromeda and 94.5 %
   on the Sagittarius Triplet, and the crops by the owner's eye.

**Read, not predicted:** the step it stops at and its rate halvings against R2p's (50,000 / 81,500 / 161,000 / 213,000,
stopped at 224,500); synth4's part of the held-out objective at its best against R2p's 2.30e-6; the clusters' part;
seed 1 against seed 0, every prediction's measure on both.

**Kill:** prediction 1 missing means R2q's crowded gain does not survive R2p's length, and the line keeps R2p. With 1
holding, 3 to 5 holding means R2q's costs were its schedule's and R2r is a candidate to ship (the owner's call); 3 to 5
missing again means the clusters themselves cost synth4's bright stars and the nucleus, and the next question is their
share. **What it decides:** whether the synthetic clusters can join the training draw at no cost elsewhere.

#### R2s: each channel's halo and width drawn on its own, pre-registered (2026-10-21)

Written before it trains, on the owner's word of 2026-10-21 ("yes" to the halo arm, after "we defo have to consider the
halos"). Tracked by #1338.

**Why: two measurements on the owner's Andromeda** (an Astro Pixel Processor master of an unknown rig) **and the
held-out Tarantula.**

- **The model takes blue's shape from red and green.** R2p digs 46 % of Andromeda's dark-sky stars at 100-1000 sigma,
  the dug ones in blue. Each channel put through alone, as a mono frame, digs 1.5 %: blue's mean residual over 2,160
  bright isolated stars goes from -1.40 / -1.04 sigma at 1.5-3 / 3-4.5 px to -0.28 / +0.01. Andromeda's blue wing is
  weaker than its others (3.1 sigma at 3-4.5 px, against 5.0 and 5.6). On the Tarantula, whose blue near halo is a
  little stronger for its core, R2p leaves the most light in blue instead (about 2 sigma at 0-8 px around the 40
  brightest stars). Every training star's channels keep their master's measured relation, so nothing taught the model to
  read each channel on its own. Blue's halo is not the stronger one on these rigs (on five masters it is weaker than or
  level with red's and green's, but for the Tarantula's near halo): blue fails because the model infers it.
- **The halos stay, in every channel.** Around Andromeda's 40 brightest stars R2p and R2i leave up to 1.1 sigma of halo
  at 4-20 px in each channel, where StarX leaves at most 0.33. The field profile's halo tables reach about 10 FWHM
  (median 17.5 px over the store's 543 channel tables), so that light is inside what the injector draws; but each
  master's table is one median halo, and it under-reads bright stars' near wings by up to 30 percent (#1241).

**The arm.** R2i's recipe exactly (two levels, patience 24, cap 480,000, a resume checkpoint every 2,000 steps) with no
teacher, since E5's answer carries the coupled channels and the weak halos this arm is meant to undo, on synth7:
synth4's export command with `--channel-halos` ("feat(star-removal): --channel-halos, each channel of an injected star
drawn at its own halo strength and width (R2s)"). Each channel of every injected star is drawn at its own wing gain (0.5
to 2 times the measured profile past 1.5 FWHM, the core and peak untouched, the halo table scaled with it) and width
(0.85 to 1.18), log-uniform, once per draw of a cell and scattered up to 10 percent per star, from a stream of its own:
every draw holds synth4's stars at synth4's places, brightness and colour, and only their shapes differ. Seed 0 on the
1070, beside R2i's own seed 0, also trained on the 1070, once R2r's seed 0 has trained and been scored.

**Moved to the 1070 before it trained, and its baseline with it (2026-10-21, 16:45).** R2s was first registered as seed
1 on an Azure spot T4 against R2i's seed 1. Its 26 GB cache never reached the storage account: on a slow uplink azcopy's
default 160 connections each timed out and were sent again (1,130 retries; 12.7 % of the first 6.5 GB file landed in 90
minutes), and the owner stopped Azure ("lets stop azure for now and only use this box"). Seed 0 on the 1070 is read
against R2i's seed 0, so the read still differs from its baseline in the training stars' shapes alone, on one seed and
one GPU. Every bar below is re-derived by the rule the first one used, from R2i seed 0's numbers, measured that evening
before R2s trained. The first bars, against R2i seed 1, were: dug at most 17.0 %; A's draws at least 79.3 / 74.5 / 51.6;
real bright stars at least 94.4 %; the Sagittarius Triplet at least 94.2 %; the rest as below.

**Predictions**, seed 0 at one view, against R2i's seed 0:

1. The coupling goes: Andromeda's dark-sky stars at 100-1000 sigma dug on at most 18.3 % (half of R2i seed 0's 36.6;
   seed 1: 33.9; R2p 45.9; each channel alone 1.5), and blue's mean residual over the 2,160 bright isolated stars no
   deeper than -0.5 sigma at 1.5-3 and 3-4.5 px (R2i seed 0: -1.10 / -0.90; seed 1: -1.14 / -0.88).
2. The halos go: around Andromeda's 40 brightest stars, every channel's mean residual at 4-8 and 8-12 px at most 0.5
   sigma (R2i seed 0: up to 1.08 and 1.23, in green; seed 1: up to 0.72 and 0.93; StarX at most 0.33).
3. A's draws hold within R2i's spread: at least 77.8 / 73.7 / 55.9 % clean (R2i seed 0: 79.2 / 74.5 / 60.2, less the
   1.4 / 0.8 / 4.3 points its two seeds lie apart).
4. Real bright stars (`r2-real-val`, 100-1000 sigma) flat on at least 95.4 % (R2i seed 0: 96.4, less the 1.0 points its
   two seeds lie apart).
5. M31's nucleus on the owner's Andromeda at most 5 % in every channel (R2i seed 0: 3.2 / 3.1 / 0.5).
6. The Sagittarius Triplet (mono): dark-sky stars at 100-1000 sigma flat on at least 94.0 % (R2i seed 0: 98.3, less
   4.3), and the owner's three masters' crops no worse by the owner's eye.

**Read, not predicted:** the held-out Tarantula (R2i seed 0: dark-sky stars at 100-1000 sigma flat on 35.8 %, crowded
69.8 %; seed 1: 41.4 and 71.9; blue around the brightest stars); Andromeda's flat rate; R2s's eight views; bright stars'
colour on A's draws.

**Kill:** prediction 1 missing means drawing each channel on its own does not decouple them, and `--channel-halos` is
dropped. With 1 holding, any of 3 to 6 missing means R2s is not adopted as it stands; which costs are acceptable is the
owner's call. **What it decides:** whether the colour dig and the halos left come from the training stars' shapes. If
they do, the next arm puts `--channel-halos` together with R2r's clusters and E5's teacher retrained on synth7.

#### R2r seed 0 is R2q seed 0, and what that corrects (2026-10-21)

`--schedule-on main` made the same rate halvings at the same evaluations as R2q's combined read (33,000 / 63,500 /
79,000 / 113,500, stopped at 125,000): every held-out value in the two logs is identical and all 30 weight tensors are
equal, so R2r seed 0 scores what R2q seed 0 scored (predictions 1, 2 and 6 hold, 3 to 5 miss). **R2q's read was wrong
about its confound:** synth4's own held-out objective plateaus that early once the clusters share the draw. Seed 1
matched R2q's seed 1 to step 20,000 and then halved sooner (29,000 against 48,000), so reading synth4 alone never
lengthens the run. R2r cannot separate the clusters' cost from the shorter run, because the clusters cause the shorter
run.

#### Round 1: iterating for a better model (2026-10-21)

The owner, after R2r's read: "What i want is a better model, so we need to iterate on how we can get it better." From
here each arm is judged on one real-data scorecard against R2p and StarX (Andromeda's bright stars dug and blue's
residual, the halo left around the brightest stars, the held-out Tarantula's crowded and faint Gaia stars, M31's
nucleus, the Sagittarius Triplet, the real tiles' bright stars), arms run side by side on the 1070 and Azure T4s, every
arm at a fixed length so the schedule never confounds a read again, and a pre-registration is a few lines. Round 1:

- **A, R2t** (below): R2q's data on the four-level model, to R2p's length.
- **B, R2s** (above): each channel's halo and width on its own, on the 1070.
- **C**, after both: the bigger model on synth7 with the clusters and a teacher retrained on synth7. synth7's cache
  uploads to the VMs' region for it, held to 8 connections so a slow uplink cannot thrash.

#### R2t: R2q's data on the four-level model, run to R2p's length, pre-registered (2026-10-21)

Written before it trains, on the owner's word ("yes arm A"). **Why:** the clusters gain on real crowded fields (crowded
stars +10 points, faint Gaia stars +9 on the held-out Tarantula) but cost bright stars and M31's nucleus, and synth4's
own objective plateaus sooner with them in the draw: what a model too small for its data does. Ours has 0.81 M weights;
StarX's file holds about 16 M.

**The arm.** R2q exactly (R2p's recipe, `--teacher` E5, `--extra-cache` the clusters at 12.5 % of the draw) on R2nc's
four-level U-Net (`--levels 4 --max-ch 256`, 6.86 M weights, `--clip-grad 8`, which trained R2nc cleanly), with a cosine
schedule fixed at R2p's length (`--schedule cosine --steps 224500`), seed 1 on an Azure spot T4. Three changes at once
by design: the question is whether this model is better, not which change made it so.

**Predictions**, at one view, every bar R2r's: (1) the held-out Tarantula's crowded stars flat on at least 74.5 % and
dark-sky stars at 100-1000 sigma on at least 33.9 %; (2) Gaia purity at most 1.0 %, BP 17-19 removed on at least 77.5 %;
(3) A's draws at least 79.3 / 76.4 / 61.5 % clean; (4) real bright stars flat on at least 96.4 %; (5) M31's nucleus at
most 5 % in every channel; (6) Andromeda's dark-sky stars at 100-1000 sigma flat on at least 35.0 % and the Sagittarius
Triplet's on 94.5 %. **Read, not predicted:** the rest of the scorecard (blue's residual and the halo left, as R2s reads
them) against R2p.

**Kill:** 1 or 2 missing means the bigger model loses what the clusters gave. With both holding, 3 to 5 holding makes
R2t the better model and round 1's base; 3 to 5 missing means capacity at this size does not buy the clusters' cost, and
the next lever is a smaller cluster share.

#### Round 1's reads: R2t is the base (2026-10-22)

| arm | A's draws clean, 5-20 / 20-100 / 100-1000 sigma | real bright stars flat | held-out Tarantula crowded flat | M31's nucleus taken, R / G / B |
|---|---|---|---|---|
| R2i seed 0 (baseline) | 79.2 / 74.5 / 60.2 | 96.4 | 69.8 | 3.1 / 3.1 / 0.3 % |
| R2p (ships) | | | 70.2 | 1.2 / 4.5 / 1.8 % |
| R2r seed 0 (= R2q seed 0) | 78.7 / 74.8 / 54.5 | | | |
| R2r seed 1 | 79.6 / 75.1 / 58.3 | 95.2 | | -0.1 / 3.7 / 1.6 % |
| R2s (synth7, channel halos) | 81.0 / 76.0 / 58.2 | 97.3 | 76.0 | **45.5** / 4.5 / 8.4 % |
| **R2t** (four levels, cosine) | **80.4 / 77.8 / 66.2** | **97.4** | **81.3** | 1.3 / **6.6** / 3.1 % |

A's draws at one view; the nucleus at 8 views (R2s's at one); the E6 average reads 98.4 on the real bright stars and takes
the nucleus 16.5 % in red. **R2t holds five of its six predictions**: (1) the Tarantula's crowded stars 81.3 % and
dark-sky bright stars 57.2 % (bars 74.5 and 33.9; R2p 70.2 and 29.6); (2) Gaia purity 0.5 %, BP 17-19 removed 85.4 %
(bars 1.0 and 77.5; R2p 79.8); (3) A's draws above; (4) the real bright stars 97.4 % (bar 96.4); (6) Andromeda's dark-sky
bright stars 40.6 % and the Sagittarius Triplet's crowded stars 95.2 % (bars 35.0 and 94.5). (5) misses by 1.1 to 1.6
points: the nucleus 6.1 % in green at one view, 6.6 % at eight. Read, not predicted: the ring's residual R/G/B -0.24 /
-0.55 / -0.45 sigma against R2i's -0.40 / -0.56 / -0.33. By its kill rule R2t is the better model and round 1's base: the
bigger model buys the clusters' cost and more, the largest single step on bright stars since R2i. It trained through four
spot evictions, each resumed from its checkpoint. **R2s** is killed by the nucleus (its own read above), though it is
flatter on the crowded field; its halos are being fixed against measured real stars before they are tried again. **R2r**
bought nothing: its two seeds sit 4 points apart on bright stars, which is the seed spread R2u is read against.

#### R2u: round 1's arm C, R2t's recipe with E6 as the teacher, pre-registered (2026-10-22)

Written before it trains, on the owner's word ("I'd train it as R2t's recipe plus whatever won elsewhere"). R2s's halos
lost (M31's nucleus taken 45.5 % in red), so synth7 stays out until they are fixed. R2t's first numbers lead round 1: A's
draws 80.4 / 77.8 / 66.2 % clean against R2i seed 0's 79.2 / 74.5 / 60.2, real bright stars flat on 97.4 %, the
nucleus 6.6 % in green. The one other measured winner is the E6 average (R2i seeds 0 and 1 and R2l over 8 views), which
beat E5, R2t's teacher, on every band of the real-star check (bright stars flat on 98.4 % against 97.8).

**The arm.** R2t exactly, the teacher E6 instead of E5, built on the T4 before training (`launch-arm.ps1
-TeacherMembers`), seed 0. **Predictions**, at one view: R2t's six (above), and the real bright stars flat on at least
97.4 %, R2t's own. **Read, not predicted:** A's draws and the nucleus against R2t, knowing R2r's two seeds differed by 4
points on bright stars, so a difference under that is a seed's. **Kill:** below R2t on both the real bright stars and A's
bright draws means the better teacher buys nothing at this size, and R2t stays round 1's base.

**Amended before its first training step (2026-10-22 14:05): the teacher is E5, so R2u is R2t's recipe on a second
seed.** R2t's own read, finished while E6 was being built, put E6 itself at 16.5 % of M31's nucleus taken in red (8
views), against E5's and R2p's 1.2 to 4.5 %. R2p showed a student keeps its teacher's nucleus, and a nucleus taken is what
killed R2m, R2o and R2s, so E6 did not win elsewhere after all. The half-built E6 was stopped and dropped on the VM, E5
fetched from the container, and training started on it (the trainer's own line: `--teacher` E5's two members over 8
views, `--seed 0`, R2t's flags). The predictions stand as written; the read now says whether R2t's gain over R2r is its
recipe's or its seed's.

#### Round 2: the hole under bright stars, read on whole masters (2026-10-22)

Tracked by #1767. Round 1 judged our models on 256 px tiles. Put through the owner's whole masters beside the two judges (the "best three
against StarNet2 and SXT" page), all three of ours fall far behind on bright stars: SXT leaves 98.6 % of Andromeda's
bright dark-sky stars flat where ours leave 39 to 43 %, because ours dig a hole under 37 to 46 % of them, and on the
held-out Tarantula's crowded stars StarNet2 and SXT leave about 96 % flat against R2t's 81 %. Ours win only purity, the
light taken where no star is (0.3 to 0.7 % against 1.7 and 3.1 %). The owner: "Okay then let's do that": round 2 aims at
the bright stars, and every arm is read on whole masters.

**Where the hole is.** Per channel, over Andromeda's 2,160 bright isolated dark-sky stars, in sigma, at 0-1.5 / 1.5-3 /
3-4.5 / 4.5-6 px:

| | red | green | blue |
|---|---|---|---|
| input (FWHM) | 122.7 / 21.1 / 5.6 / 2.1 (2.76 px) | 110.9 / 18.9 / 5.0 / 1.6 (2.26 px) | 118.4 / 19.1 / **3.1 / 0.9** (2.76 px) |
| R2t's residual | -0.03 / -0.64 / +0.36 / +0.21 | +0.29 / +0.07 / +0.51 / +0.22 | -0.55 / **-1.23 / -1.00** / -0.36 |
| SXT's residual | +0.04 / -0.55 / +0.19 / -0.00 | -0.00 / -0.34 / +0.03 / -0.10 | +0.19 / -0.40 / -0.27 / -0.17 |

Two failures. (1) **Andromeda: blue's shoulder is dug.** Blue's core is as wide as red's, but its shoulder (3-6 px) falls
about twice as fast; ours take the same shape out of every channel, so blue goes below the sky there while red and green
keep a little light. That blue hole is what remover_check2 counts as dug. (2) **The Tarantula: light left in sharp
cores** (FWHM under 2 px in green and blue): R2p leaves 1.5 to 2.2 sigma at the core, R2t 0.4 to 0.9, SXT nothing.

**What the injected stars get wrong, measured (the halo fix, 2026-10-22).** A first reading here said the injected red
was narrower than green, against the 50-session survey's red at 1.16 to 1.90 times green's FWHM. That compared the drawn
Moffat FWHM of A's draws, whose validation masters happen to have narrow red (ASI533 L-Quad HIP-80609: real 2.01 / 2.19 /
2.08 px), with a survey of mostly L-Ultimate nights; measured on the drawn stars as on real ones, the injected FWHM
matches each master's own stars to 0.99 to 1.00 per channel (R/G 1.21 injected, 1.22 real). What IS short is the light
outside the core: per fitted amplitude, read on bright stars, the drawn star holds 0.91 / 0.88 / 0.86 of the real 3-8 px
light (R/G/B) on broadband nights and 0.79 / 1.03 / 1.12 on dual-band ones, and the drawn core is 1.2 to 1.9 times
brighter per amplitude than a real one (wrong: a ratio of two other measures; read directly the drawn core is the real one to
2 to 6 %, "The cores measured", below). The owner's OIII halo around the brightest stars of dual-band nights came from
giants, whose catalogued amplitude is their first ring and which the injector never draws. And R2s's halos broke the
real relation between channels: over the 123 colour training masters a wider red core comes with a weaker red wing
(correlation -0.59), R2s's draws had -0.23, and 45 % of its stars lay outside every real master's relation, which is how
it learned a red-only star 8 px wide and took 77 % of M31's red nucleus above its ring.

**The scorecard** is one command (moving into the repo with the per-arm scorer and the driver: #1774),
`C:/temp/e2/score-whole.py <tag> --model <onnx>` (one view, how a model ships): Andromeda's
bright dark-sky stars flat and dug and blue's residual at 1.5-3 and 3-4.5 px, the Tarantula's bright dark-sky stars flat
and left and its core residual per channel, its crowded stars, Gaia BP 17-19 and purity, the Sagittarius Triplet's
crowded stars, and M31's nucleus. Its guards are round 1's: the nucleus at most 5 % in every channel, purity at most 1.0 %.
The judges' rows are the ceiling, R2i seed 0's the small-model baseline and R2t's the round 1 base.

**The arms.** A1, **R2s2**: each training master's stars drawn at that master's own measured wing gains (`degrade
--star-shapes`, from `tianwen dataset star-shapes`, which solves per master and channel the gain that matches the drawn
star's 3-8 px light per amplitude to the real stars'; branch fix/channel-halos-measured), exported as synth9, on R2i's
recipe on the 1070, against R2i seed 0. Measured gains: broadband 1.11 / 1.13 / 1.18 (17 of 49 masters), dual-band 1.26 /
0.97 / 0.90 (61 of 70); masters without ten bright isolated dark-sky stars keep 1. (synth8, the first try, drew each
cell's channel relation from another master and crossed filter families; it is kept as an ablation, not trained.) A2:
R2t's recipe on whichever data wins, on a T4, its teacher rebuilt on that data. The cores, drawn 1.2 to 1.9 times too
bright per amplitude, are the next lever if the wings alone do not move the Tarantula (they are not, measured: "The cores
measured", below; the next lever became R2v).

#### R2s2: the wings drawn at each master's measured gains, pre-registered (2026-10-22)

Written before it trains. **The arm.** R2i's recipe (run-r2.ps1, no teacher, no extra cache) on synth9, seed 0, on the
1070. Against R2i seed 0's whole-master row (`score-whole.py`): Andromeda's bright dark-sky stars 32.8 % flat and 36.6 %
dug, blue's residual -1.10 / -0.90 sigma at 1.5-3 / 3-4.5 px; the Tarantula's 35.8 % flat and 60.6 % left, its core
+0.89 / +1.06 / +0.61; crowded 69.8 %; nucleus 3.2 / 3.1 / 0.5 %. **Predictions:** (1) Andromeda's blue residual above
-0.6 / -0.5 and its bright stars dug at most 20 %; (2) the Tarantula's bright stars left with light at most 45 %; (3) no
guard lost: the nucleus at most 5 % in every channel, purity at most 1.0 %, A's draws within 2 points of R2i seed 0's 79.2 /
74.5 / 60.2 % clean, the real tiles' bright stars at least 95.4 %. **Kill:** (3) failing. **Read:** (1) holding means the
dig came from the training stars' wings; (1) missing with (2) holding means the wings were short but the hole is the cores;
both missing sends the next arm to the cores. The judges' rows stay the ceiling.

#### Round 2's first reads: R2s2 killed, and R2u gives the whole masters a seed spread (2026-10-23)

Every row is `score-whole.py` at one view:

| model | Andromeda bright flat / dug | blue at 1.5-3 / 3-4.5 px | Tarantula bright flat / left | Tarantula core R / G / B | Tarantula crowded | BP 17-19 / purity | Sag crowded | M31's nucleus R / G / B |
|---|---|---|---|---|---|---|---|---|
| R2i seed 0 | 32.8 / 36.6 | -1.10 / -0.90 | 35.8 / 60.6 | +0.89 / +1.06 / +0.61 | 69.8 | 90.3 / 0.6 | 95.0 | 3.2 / 3.1 / 0.5 % |
| **R2s2** | 43.3 / 42.1 | -1.05 / -1.01 | 48.0 / 49.8 | +0.95 / +1.15 / +0.92 | 81.5 | 90.8 / 0.5 | 95.3 | **78.6 / 74.4 / 70.7 %** |
| R2t seed 1 | 40.6 / 41.7 | -1.23 / -1.00 | 57.2 / 40.3 | +0.84 / +0.38 / +0.90 | 81.3 | 85.4 / 0.5 | 95.2 | 1.1 / 6.1 / 3.1 % |
| R2u (R2t, seed 0) | 39.5 / 43.4 | -1.31 / -1.03 | 44.0 / 54.2 | +0.62 / +1.06 / +1.06 | 74.8 | 86.9 / 0.4 | 95.1 | -0.1 / 3.5 / 2.1 % |
| SXT | 98.6 / 1.4 | -0.40 / -0.27 | 96.6 / 2.7 | -0.04 / +0.06 / +0.04 | 96.1 | 98.3 / 3.1 | 99.6 | 0.1 / 0.1 / 0.1 % |
| StarNet2 | 48.1 / 0.4 | -0.22 / -0.12 | 92.3 / 7.4 | -0.33 / +0.01 / +0.05 | 96.5 | 98.9 / 1.7 | 99.8 | -0.8 / -0.9 / 0.1 % |

**R2s2 is killed by (3).** It takes M31's nucleus at 78.6 / 74.4 / 70.7 %, the most any arm has, and A's bright draws fall to
51.4 % clean (79.7 / 73.8 / 51.4 against R2i seed 0's 79.2 / 74.5 / 60.2), with the real tiles' bright stars at 95.33 %
(bar 95.4). (1) misses outright: blue's residual is where it was (-1.05 / -1.01 against -1.10 / -0.90) and Andromeda's
bright stars are dug more, 42.1 % against 36.6. (2) misses at 49.8 % left (bar 45). Purity held at 0.5 %. By the read
written before it trained, both missing sends the next arm to the cores. Wings drawn up to 1.26 times brighter taught the
small model to take a bright, extended core whole, and did not touch the blue hole.

**R2u holds every prediction** (R2t's six, from the round 1 reads, and the real bright stars at 97.4 or more): A's draws
80.1 / 77.2 / 66.3 % clean, the real bright stars 97.58 %, purity 0.4 % and BP 17-19 86.9 %, Andromeda's dark-sky bright
stars 39.5 % and the Sagittarius Triplet's crowded stars 95.1 %, the Tarantula's crowded stars 74.8 % (bar 74.5) and its
dark-sky bright stars 44.0 % (bar 33.9), and M31's nucleus at most 3.5 % in every channel, the one prediction R2t itself
missed. Its kill (below R2t on both the real bright stars and A's bright draws) does not fire. R2t's recipe holds on a
second seed, and its nucleus miss was its seed's.

**What the second seed says about reading a whole master.** On Andromeda the two seeds agree to 1 to 2 points (flat 40.6
and 39.5 %, dug 41.7 and 43.4 %) and blue's hole is the same in both (-1.23 and -1.31 sigma): the hole belongs to the
recipe and its data, not to a seed. The held-out Tarantula moves far more: crowded stars 81.3 and 74.8 %, bright stars flat
57.2 and 44.0 %, left 40.3 and 54.2 %, the core 0.4 sigma apart in green. So a single seed's Tarantula difference under
about 13 points is not evidence, which reads R2s2's Tarantula gains over R2i seed 0 (flat 48.0 against 35.8, crowded 81.5
against 69.8) as within a seed's reach, while its Andromeda dig and its nucleus are not.

#### The cores measured: no gap there, and Andromeda's blue outside every training master (2026-10-23)

**The cores are not 1.2 to 1.9 times too bright.** That figure (above, and the round's next lever) was a ratio of two other
measures, the light 3 to 8 px out and the shoulder over the core, never read itself. `tianwen dataset star-shapes` now reads
each channel's light per catalogued amplitude in four rings (0-1.5, 1.5-3, 3-4.5 and 4.5-8 px) on the real bright stars and
on the drawn star at its wing gain (`StarShape.RingEdgesPx`). Over the 138 training masters, the 82 colour ones with ten
readable bright stars, drawn over real, the median and quartiles:

| | 0-1.5 px | 1.5-3 px | 3-4.5 px | 4.5-8 px |
|---|---|---|---|---|
| broadband (21), R / G / B | 0.97 / 0.97 / 0.98 | 0.95 / 0.97 / **0.86** (0.80-0.95) | 0.94 / 0.95 / 0.93 | **1.10 / 1.08 / 1.18** |
| dual-band (61), R / G / B | 1.04 / 1.06 / 1.06 | **0.82** (0.74-0.90) / 1.06 / 1.04 | 0.91 / 0.98 / 0.95 | **1.20** / 1.03 / 1.05 |

The drawn core is the real one to 2 to 6 %. What a one-number wing gain cannot draw is the shape between 1.5 and 8 px: it
matches the light 3 to 8 px out by drawing the far wing 10 to 20 % too bright and the shoulder short (broadband blue, a
dual-band night's red). R2s2's extra dig on Andromeda (42.1 % against 36.6) is what a too-bright far wing would teach.

**Andromeda's blue falls off faster than any training master's.** Read the same way on 232 bright isolated dark-sky stars
(a Python cross-check that gives the product's 0.72 on the Running Chicken master's own catalogue), each channel's light at
3 to 4.5 px over its core, blue's over green's:

| | blue over green, 1.5-3 px | blue over green, 3-4.5 px |
|---|---|---|
| Andromeda | 0.99 | **0.56** |
| broadband training masters (21) | median 1.12, least 0.75 | median 0.98, tenth percentile 0.79, least **0.72** |
| dual-band training masters (61) | median 1.00, least 0.81 | median 1.02, least 0.87 |

Each of Andromeda's channels alone is inside the training range (its red and green shoulders near the top, its blue in the
middle); the two together are not, and no training star ever had a blue shoulder half its green one. A model that has only
seen a channel's shoulder follow the others' takes green's shape out of blue, which is the hole: blue alone is dug, at
1.5-4.5 px, in every model of ours and both seeds of R2t. The held-out Tarantula is no such gap: its field FWHM (1.93 / 1.57
/ 1.46 px) sits at the training masters' median (1.99 / 1.46 / 1.54).

#### R2v: one channel's wing weakened past every training master's, pre-registered (2026-10-23)

Written before it trains, on the owner's word ("just keep everything in this branch for the doco, and the training"). **The
arm.** R2i's recipe (run-r2.ps1, no teacher, no extra cache) on synth10, seed 0, on the 1070: synth4's command with
`degrade --channel-wing-cut 0.45` (`ChannelWingCut`), which in half the draws of a cell weakens one channel's wing, the
channel at random and the factor log-uniform from 0.45 to 1, from a stream of its own. A wing is only ever weakened, never
brightened: R2s's and R2s2's brighter wings are what took the nucleus. Checked on a smoke export before it runs: the uncut
draws and every clean target are bit-identical to a plain export, and a cut draw differs in its one channel alone. Against R2i
seed 0's whole-master row (above).

**Predictions:** (1) Andromeda's blue residual above -0.5 sigma at 3-4.5 px and above -0.8 at 1.5-3 px (R2i seed 0 -0.90 /
-1.10), and its bright stars dug at most 30 % (36.6; two seeds of R2t sit 1.7 apart); (2) no guard lost: M31's nucleus at most
5 % in every channel, purity at most 1.0 %, A's draws within 2 points of 79.2 / 74.5 / 60.2 % clean, the real tiles' bright
stars at least 95.4 %. **Read, not predicted:** the Tarantula (a seed moves it up to 13 points) and the crowded fields.
**Kill:** (2) failing. **Read:** (1) holding means blue's hole was a channel relation the training never held, and the cut
goes onto R2t's recipe; (1) missing with (2) holding means the relation is not the cause, and the remover takes one shape out
of every channel for another reason.

#### The synthetics review: what tells the training data from real, measured (2026-10-21)

The owner, 2026-10-21: "review our synthetics, they do still do not look natural enough ... we should be able to measure
our existing corpus for certain values, including doing some domain analysis". Measured over the 8 validation sessions,
40 cells each, through the exporter's own stretch in all three sources: the real masters (`r2-real-val`), synth4's own
training inputs (stars on the synthetic background) and A's draws (stars on the real starless plates). Two instruments,
kept in `C:/temp/e2/domain`: statistics in each tile's own noise sigma (`domain_stats.py`), and a small CNN told to tell
real from synthetic and tested on a camera it never saw, leaving one of the QHY294PROC, ASI294MC, ASI533MC Pro and
ASI585MC Pro out at a time (`domain_classifier.py`; an AUC of 0.5 is indistinguishable).

| what the classifier reads | synth4 | A's draws |
|---|---|---|
| the whole 64 px crop | 0.81 | 0.64 |
| high-pass: noise and star cores | 0.84 | 0.75 |
| star-centred 24 px crops | 0.79 | 0.73 |
| noise alone: 16 px crops with nothing over 4 sigma | 0.70 | 0.61 |
| low-pass: the background's structure | 0.60 | 0.52 |

| statistic | real | synth4 | A's draws |
|---|---|---|---|
| bright stars (100-1000 sigma): light at 3-5 px over the peak, R / G / B | 0.088 / 0.095 / 0.055 | 0.041 / 0.050 / 0.038 | 0.042 / 0.046 / 0.041 |
| real wing over synth4's, at 20-50 / 50-100 / 100-200 / 200-500 sigma | 0.82 / 1.24 / 1.81 / 1.47 | | |
| share of stars at 5-20 / 100-1000 sigma | 60 / 2.1 % | 53 / 6.0 % | 53 / 6.0 % |
| stars over 5 sigma a tile | 146 | 119 | 122 |
| star colour B/G at 20-100 sigma | 0.63 | 0.81 | 0.83 |
| noise correlation between channels, R-G / G-B / R-B | 0.12 / 0.05 / 0.04 | 0.05 / 0.02 / 0.02 | 0.08 / 0.04 / 0.02 |
| sky texture at 8 / 16 px, rms over sigma | 0.48 / 0.40 | 0.54 / 0.44 | 0.52 / 0.44 |

The stars give the synthetics away most, then the noise; the synthetic background is close, and on the real plates the
background's structure reads at chance. The statistics name what: bright stars carry about half the real near-wing
light, the shortfall rising with brightness and differing by rig (none on the ASI585, 1.4 to 1.7 times on the ASI294MC);
the population is three times too top-heavy with too few faint stars, since the injector draws brightness from the
catalogue's fitted stars, which hold none of the faint and unresolved ones; mid-brightness stars are too blue; and the
noise is drawn too independently across channels, where a stacked colour master's channels share theirs through the
demosaic or the drizzle. Two caveats: every number is in the stretched domain the model sees, which compresses bright
peaks (it would raise the synthetic wing share, so the shortfall is if anything larger), and a real "star" in a crowded
field can be a blend.

**The owner's call (2026-10-21): fix them by hand first, and keep a learned generator in sight** ("lets fix by hand
first, but not lose the genrator out of sight"). Each fix is read by the two instruments on its own export before it
trains, aiming at every component near 0.6 or under; the four below and R2s's halos are the list.

#### Synthetics S1: bright stars' halos, per channel and by brightness

Tracked by #1241 (and R2s, above). R2s draws each channel's wing at 0.5 to 2 times the measured one, around 1. The
brightness trend is the step after it: a wing gain that rises with the star's brightness as the review's ratio does (0.8
at 20-50 sigma to 1.8 at 100-200), which is #1241's own suggestion (a table with a brightness term), drawn per rig
rather than fixed.

#### Synthetics S2: the star population below the catalogue

Tracked by #1737. The injected brightness follows a luminosity function fitted to the real tiles' star counts and
extended below the catalogue's detection limit, so the input holds the faint stars a real master does (60 % of the real
stars over 5 sigma are under 20 sigma, against 53 % injected) instead of three times the real share of bright ones.

#### Synthetics S3: the stars' colours

Tracked by #1738. The injected colours follow the real stars' colour distribution at each brightness. The pool's
mid-brightness stars are bluer than the fields' (B/G 0.81 against 0.63 at 20-100 sigma); the bright stars agree.

#### Synthetics S4: noise shared across channels

Tracked by #1739, and its next step by #1781. The noise the export draws (the stars' own, and the synthetic background's) carries the master's
measured covariance between channels (R-G 0.12 on the real tiles against 0.05 drawn), with each channel's spatial
correlation as now.

##### S4's read (2026-10-23): the correlation matches, and the classifier finds the noise more easily

**What was built.** `dataset degrade --channel-noise-correlation` (stars mode only, off by default):

- Each session's correlation is measured once, on its starless plate (`ChannelNoiseCorrelation.Measure`). Each channel's residual from its 5x5 median is taken on a strided grid of covered pixels and read by a robust correlation, from the MADs of the standardised sum and difference.
- The per-channel unit noise fields are drawn as before, then mixed by the correlation's Cholesky factor. Each field keeps its variance and its spatial shape, and the mix draws nothing, so every other draw is unchanged.

**Why the correlation is read robustly.** The review's Pearson over a tile's sky also counts light the channels share, such as the faint stars S2 is about. That is why synth4, whose noise is drawn independent, still reads 0.05.

- A star at 2 % of the field moves a true 0.12 to 0.42 by Pearson, but only to 0.14 robustly (`StarInjectionTests`).
- On a plate drawn at 0.12 / 0.05 / 0.04, the measure reads back 0.113 / 0.043 / 0.036. The 5x5 residual costs it about 5 %.

**Proof the option leaves other draws alone.** On the 8 validation sessions, the export with the option off is byte-identical to synth4's own tiles: 320 degraded, 320 clean, and both sets of sigma tiles. With the option on:

- the injections (320 of 320) and the clean tiles' sigma are unchanged;
- every noisy tile moves: the degraded tile, the synthetic-background clean tile, and the degraded sigma tile, which is read off the noisy level.

A test pins that a correlation read as none leaves every file as it was.

**The numbers.** The correlation is the median over tiles, R-G / G-B / R-B. The noise-only AUC is synth4's classifier on its `noise` crops, over three seeds, on the tiles as exported.

| export | Pearson | robust | noise AUC, seeds 0 / 1 / 2 |
|---|---|---|---|
| real | 0.119 / 0.048 / 0.038 | 0.062 / 0.027 / 0.016 | |
| synth4 (the option off) | 0.048 / 0.017 / 0.019 | 0.011 / 0.007 / 0.003 | 0.696 / 0.687 / 0.715 |
| the option on | 0.101 / 0.034 / 0.027 | 0.063 / 0.021 / 0.009 | 0.745 / 0.714 / 0.720 |

A second read, re-stretched from the plate's MTF to the master's own (#1776), was dropped. The copy of the re-stretch tool it used skipped every exact zero or NaN in each channel, where the exporter skips only the canvas ring, so its figures are not comparable. The verdict rests on the tiles as exported.

**The correlation matches.** Every pair is within 0.02 of real by Pearson (0.018, 0.014 and 0.011 short), and within 0.007 robustly.

**But the noise is easier to find, not harder.** The AUC rose with the option on at every seed, from a mean of 0.699 to 0.726 (+0.049, +0.027, +0.005 per seed). Seed 2's +0.005 is inside synth4's own spread over seeds (0.687 to 0.715), so the size of the rise is uncertain; its direction is not. Synth4's 0.70 from the review is reproduced (0.687 to 0.715).

The likely reason is where in space the channels share their noise. Below is each source's cross-correlation, on the classifier's own residuals, at a few pixel offsets (dy, dx), beside each channel's own lag-1. The values are medians over tiles.

| source | R-G at 0,0 / 0,1 / 1,1 | R-B at 0,0 / 1,1 | each channel's own lag-1, R / G / B |
|---|---|---|---|
| real | 0.099 / 0.052 / 0.025 | 0.029 / 0.018 | 0.205 / 0.181 / 0.162 |
| synth4 | 0.021 / 0.009 / 0.006 | 0.010 / 0.003 | 0.173 / 0.167 / 0.155 |
| the option on | 0.079 / 0.017 / 0.003 | 0.019 / 0.003 | 0.173 / 0.167 / 0.155 |

- In real tiles, R-G one pixel away is half its zero-lag value. That is far more than each channel's own lag-1 would carry, and it is what a demosaic's interpolation makes.
- The mix carries the shared part with each channel's own shape, so it falls away at once.
- Per rig, the ASI533's real R-B sits on the diagonal (+0.050 at 1,1, +0.010 at 0,0). Read at zero lag on the plate, it is drawn at -0.018.
- Before, the classifier saw almost no shared noise. Now it sees a zero-lag correlation without its neighbourhood, a shape no real master has.

**Verdict: not adopted.** The zero-lag mix alone hands the classifier a correlation with no neighbourhood.

- The option stays off by default, and nothing trains on it. It lands anyway, because the next step builds on it.
- The next step is tracked by #1781: draw the shared part with real's own spatial cross-structure. Each rig's cross-correlation is measured off its plate at lags 0 to 2, and a shared component of that structure is drawn with each channel's own lag-1 held. It relaxes this item's "each channel's spatial correlation as now".

#### A learned synthetics generator, kept in sight

Tracked by #1740. If the hand fixes stall with a component above about 0.6, the next step is a generator learned from
the corpus itself (a diffusion model or a GAN over real tiles, conditioned so that the truth stays exact), and the
review's classifier is its acceptance test from its first day.

#### #1400's review: the texture's width and energy, re-read on the export's own grid (2026-10-10)

An agent's review of R2c to R2e (#1400) found the generator's texture meant different things at different sizes, and its
energy scaled in a unit the plate's is not in. Each finding was checked in the code first; what changed, and what the
reads it moved say now.

**F1, the width depends on the grid.** `PowerLawField` gives a mode `k^(-beta/2)` in grid-index units, and `TextureScales`
took the log-normal's width against the WHOLE grid's spread, which a red spectrum's coarsest modes set. The grid is the
next power of two at twice the cut, so the same 0.8 was a smoother texture in S3's 384 px cells (a 1,024 px grid: a fine
band 0.65 as strong, over sixteen draws) and a far rougher one in S4's small fills (64 px) than in the export's 256 px
cells. **Fix:** every texture draws on at least 512 px (`SyntheticBackground.TextureGridPx`), and a larger grid's modes
coarser than 512 px are taken out before the field is normalised (`PowerLawField`'s cutoff, the same in the steered
field). The export's cells always drew on 512 px, so they and `TailTable` are what they were; a fine band on twice the
grid now reads 1.07 of the export's (the lattice at the cutoff samples the 512 px grid's coarsest modes more sparsely),
pinned by `AWidthDrawsTheSameFineScalesOnTheExportsGridAndOnALargerOne`. On the filament test F1 alone doubles the fill's
alignment with the filaments (0.32 to 0.68 against the truth's 0.96): a small grid sampled the steered spectrum too
coarsely to hold a direction.

**F2, the energy's unit.** Each drawn band was scaled by its robust spread (1.4826 MAD) and multiplied by an amplitude
that is a plain RMS, so a heavy-tailed band drew more energy than the plate's: on S3's plates at the export's cells, 1.2
to 1.9 times the plate's at 1 to 8 px (`C:\temp\e2\s3-1400\summary.txt`, the R2e export's own build against this one).
Scaled by its plain RMS instead, the overshoot went and an undershoot it had hidden showed: 0.65 to 0.8 at 2 and 4 px,
less at 8, because a starlet band re-read spreads into its neighbours and bands each at their amplitude, summed, re-read
under it (a Python check: a sum of unit bands of a 0.8 log-normal re-reads at 0.59 to 0.73 per scale). **Fix, beyond the
review's:** `DrawnTexture` matches each replaced scale's RE-READ energy to the plate's amplitude, three passes of a gain
per scale, read only over the cut's interior that scale's kernel reaches no edge from (a margin of `4 << j` px; a scale
with less than `MinEnergyReadPx`, 16 px, inside keeps its gain). Read over the whole cut first, the mirrored edge read
weak and a small cut is mostly edge, so a hole's fill had its gains pumped up (S4's 24 px holes at 1.62 of the
original's energy, below). The export's setting (the fine steer) now reads, synthetic over plate, at 1, 2, 4 and 8 px:

| cells | before (R2e's export) | plain RMS | matched over the cut | matched inside each scale's reach |
|---|---|---|---|---|
| whole cell | 1.75 / 1.70 / 1.34 / 1.45 | 1.15 / 0.79 / 0.67 / 0.68 | 1.28 / 1.17 / 1.11 / 1.01 | 1.28 / 1.13 / 1.03 / 1.00 |
| textured | 1.65 / 1.53 / 1.08 / 0.77 | 1.14 / 0.83 / 0.65 / 0.51 | 1.17 / 1.07 / 0.96 / 0.80 | 1.20 / 1.08 / 1.01 / 0.88 |
| strongly textured | 1.58 / 1.01 / 0.83 / 1.18 | 1.03 / 0.60 / 0.60 / 0.70 | 1.24 / 1.07 / 0.96 / 1.23 | 1.23 / 1.03 / 0.99 / 1.69 |

(noise in both; the 1 px row is the noise's, and the 8 px row rests on 3 to 15 cells). The last column is what ships.
The structure moves within its noise: S3's score at 2 to 4 px, the fine steer, 0.119 / 0.114 / 0.070 before, 0.114 /
0.121 / 0.068 as shipped (whole, textured, strongly textured; the 8 px row's handful of cells, whose class membership
moves with the draw, swings the 2 to 8 px score more than any arm does). On the filament test the fill's energy is 0.86
of the truth's (1.09 before, all of it the overshoot, 0.47 on plain RMS alone).

**R2e drew the BEFORE column** ("R2e's read", above): its export was made from the generator before these fixes, so its
texture holds 1.2 to 1.9 times the plate's energy at 1 to 8 px and F7's seam. Its read is read with that beside it.

**S4 again, the same eight plates and holes** (`C:\temp\e2\s4-1400` with F1 and F2, `s4-1400m` matched over the cut,
`s4-1400c` as shipped). The textured fill, medians over the plates, today's fill and the bar in the first column:

| | before (S4's read) | F1 and F2 | matched over the cut | as shipped |
|---|---|---|---|---|
| energy, 12 px holes at 2 px (bar 0.75 to 1.25) | 1.59 | 0.75 | 0.86 | 0.86 |
| energy, 24 px holes at 4 px | 1.16 | 0.91 | 1.62 | 1.41 |
| structure error, structured 3 px (today 0.28, bar 0.39) | 0.53 | 0.43 | 0.44 | 0.43 |
| structure error, structured 12 px (today 0) | 0 | 0.18 | 0.35 | 0.33 |
| structure error, structured 24 px (today 0.25, bar 0.35) | 0.65 | 0.47 | 0.71 | 0.73 |
| structure error, smooth 6 px (today 0, bar 0.1) | 0.12 | 0 | 0 | 0 |
| structure error, smooth 24 px (today 0.15, bar 0.25) | 0.15 | 0.15 | 0.30 | 0.30 |
| alignment with the ring, 12 / 24 px (the truth +0.54 / +0.89) | +0.58 / +0.87 | +0.58 / +0.88 | +0.49 / +0.88 | +0.48 / +0.89 |

The level holds on every build, within today's plus 0.1 sigma at every radius.

1. **Energy: no build holds at both radii.** F1 and F2 alone hold at 24 px and sit at the bar's edge at 12 (0.75, a
   miss by the third figure); as shipped it holds at 12 and overshoots at 24. The 24 px read rests on four plates of
   eight (the others hold no structured hole that size), 85 holes in all: the plate holding 55 of them reads 0.97, and
   the overshoot is two dual-band colour plates (1.69 on 24 holes, 2.78 on 4), where the fill's grain also rises to 1.5
   of the plate's against 1.0 to 1.1 elsewhere. Pooled over the holes it reads 1.26. Its cause is open.
2. **Price: misses on every build**, F1 and F2 alone included, at 3, 12 and 24 px. Matching the energy raises it at 12
   and 24 px (0.18 to 0.33, 0.47 to 0.73) and doubles the smooth 24 px holes' (0.15 to 0.30). The re-registered bar
   below was not read: its five-seed spread was not measured.
3. **Continuity holds on every build.**

**The kill stands**: R0's fill stays the push-pull. The matching is shipped for the synthetic background, which it brings
to the plate's energy at 2 to 8 px (the table above), and the fill, opt-in and unused, draws through the same routine.
As in S4's read, the energy was read at 2 and 4 px only.

**S4's bar, re-registered (F3).** The price's square root of two is a perfect draw's expected cost, so it is not a
ceiling with room for error. Registered for any later fill: the energy first, 75 to 125 percent at the read scales, then
the price no more than the square root of two times today's error PLUS the spread of that ratio over five seeds of the
draw on the same holes, read before the fill is judged.

**The tails decision, on paired draws (`s3-tails-1400m`).** It was taken on draws that differed between settings (before
`CellSeed`) and in 384 px cells, where F1 left the table's widths wrong.

| tails score, lower is closer (structure beside it) | one width | tails from the plate |
|---|---|---|
| calibration, whole cell | **0.156** (0.106) | 0.464 (0.111) |
| calibration, textured | **0.188** (0.130) | 0.301 (0.144) |
| calibration, strongly textured | **0.257** (0.206) | 0.301 (0.228) |
| held out, whole cell | **0.134** (0.103) | 0.204 (0.110) |
| held out, textured | **0.118** (0.073) | 0.139 (0.071) |
| held out, strongly textured | 0.235 (0.067) | **0.210** (0.035) |

The one width is closer in five views of six, and the plate's tails still draw the texture too smooth (kurtosis 1.0 to
1.7 at 2 to 8 px on the calibration plates' whole cells, where the plates read 3.3 to 4.8, and the one width 2.5 to 3.2),
so that bias was not F1's: its cause is still open. **Kept:** the one width.

**F4, S1's null.** White noise is the wrong null for a red sky: an isotropic `PowerLawField` at the plates' index (3.1)
read through the same windows reads coherence 0.215 to 0.219 at 2 to 8 px against white noise's 0.175 to 0.179, and
alignment +0.011 to +0.040 against +0.005 to +0.016 (`AnIsotropicRedFieldReadsMoreCoherenceThanWhiteNoise`). So of S1's
coherence over noise (+0.11 to +0.19) about 0.04 is the spectrum and the rest the sky's own; of its alignment (+0.10 to
+0.26) at most 0.03. S1's reading stands, smaller. Every measure now carries both nulls (`SkyTexture.Measurement`'s
`RedCoherence` and `RedAlignment`, at the plate's own index).

**F5, the split.** The validation draws hold SMC on the ASI585 on 2024-09-27, five days before a training night of the
same field and camera (M42 and the Lobster are in both too, on other cameras). Scored again without that night, by the
registered eval's own build:

| measure, with the night -> without it | A, 3 seeds at 12 | B, 3 seeds at 12 | B minus A | `r2dkl` minus `r2dal` |
|---|---|---|---|---|
| clean 5-20 sigma | 65.6 -> 69.9 | 60.0 -> 63.9 | -5.5 -> -6.0 | -1.6 -> -0.5 |
| clean 20-100 sigma | 54.8 -> 56.2 | 38.7 -> 39.7 | -16.1 -> -16.5 | +4.8 -> +4.7 |
| clean 100-1000 sigma | 23.4 -> 24.0 | 6.8 -> 7.0 | -16.6 -> -16.9 | +7.5 -> +7.7 |
| dug 100-1000 sigma | 42.1 -> 42.7 | 47.4 -> 48.3 | +5.4 -> +5.7 | -19.9 -> -20.8 |
| bright-core share % | 2.65 -> 2.64 | 16.9 -> 17.0 | +14.3 -> +14.4 | +0.13 -> +0.15 |

Every model removes faint stars better without it (the SMC night is a hard one), and no comparison between arms moves by
more than 1.1 points: the shared field did not tilt R2d's reads (`C:\temp\e2\f5\read.txt`, `f5_read.py`).

Later arms split by FIELD, not by night.

**F6, a dig in the sky's noise.** `Dug` judges a dig in the noise plane's sigma, which at a bright core carries the
injected star's own shot noise: at 100 to 1,000 sigma a hole of several sky sigma passed under it. `DugInSky` counts the
same two tests in the draw's sky noise (its median sigma off every injected footprint), beside `Dug`
(`AHoleUnderABrightStarsShotNoiseIsCountedAsDugInTheSkysNoise`).

**F7, the steer's seam.** Where the fine structure runs across the coarse, their doubled-angle vectors are opposite and
at half and half the blend is nothing while the angle jumps 90 degrees. The strength now follows the blend's length
(`SyntheticBackground.SteerAt`), so the steer fades there.

**F8, the mask.** A plate source whose centre an injected star covers is not scored, and its wings off that footprint
were left out of the near zone too, so the loss asked the net to keep half a star. The near zone is now every plate
source's (`AKeptStarUnderAnInjectedOneStillHasItsWingsLeftOutOfTheLoss`); only the scored list keeps the footprint rule.

#### S2: generate it, S3: check it, S4: fill with it

- **S2.** The texture drawn along an orientation field, isotropic where the coarse plate shows none (a steerable sum of
  oriented power-law fields, each one's weight the local orientation's), its anisotropy set by S1's coherence per class;
  absorption as a multiplicative layer with one-sided edges; brightening toward a bright star where it sits in nebula.
- **S3.** The synthetic read by the same measure, class by class: index, tails, coherence and alignment within S1's
  ranges, then the M45 three-way again by eye.
- **S4.** R0's fill takes the coarse level it interpolates today and S2's texture for the scales under the hole's size,
  steered by the orientation round it. Judged where the answer is known: holes cut where no star is (sized as R0's are),
  filled, and compared with what was there, the orientation's continuity across the edge and each band's energy and the
  level inside, against today's fill.
- Tracked by #1338.

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
- **A globular cluster's unresolved glow (the owner's decision, open; #1363).** The cluster scene's truth either keeps it as a
  smooth glow, the stars under the eval's own detection rule left in (consistent with the plates, which keep faint stars,
  and so training and scoring agree), or removes every star, which is what StarXTerminator does on omega Centauri. The
  recommendation is the glow; choosing it means ours knowingly differs from RC on a cluster.
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
