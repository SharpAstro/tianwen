# Star-remover training (P4): the inject-and-remove bootstrap

**Status, 2026-11-11: rounds 1 to 3 run (R2a to R3s, section 6), and NO model ships.** R2p was chosen on
2026-10-20 ([Shipping R2p](#shipping-r2p-tianwens-own-star-remover-2026-10-20), #1702, still open) and held
before shipping by the dig it leaves under bright stars on a smooth background, which is what round 2 and round 3
are about. `image remove-stars --model` runs weights a caller names, none are in `src/TianWen.AI.Imaging/models/`,
and RC-Astro stays the registered `IStarRemover`. The status below is the one written on 2026-10-03, kept as the
record.

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

**The port and its fixture (#1362).** `training/denoise/n2n_speckle.py` is the torch port (`noise_cap`, `read_sites`,
`speckle_penalty`, batched over tiles and sites) and `test_n2n_speckle.py` pins it per site to
`training/denoise/fixtures/speckle/`, which `SpecklePortFixtureProbe` writes from `StarlessSpeckles.ReadSites` (flags exact,
the ring median exact, sigma to 1e-6, the noise cap to 1e-4; regeneration is in the test's docstring). Two choices the C#
does not make: the ring's median and sigma are computed from the output but **detached** by default (with their gradient the
net could shrink the term by making the ring noisier), and the noise cap is read from the INPUT once per tile
(`noise_cap` is a loop over cells, a data-pipeline step, not a per-step one). Torch has no win-arm64 wheel for Python 3.13+
(3.12 has a CPU one, from the `https://download.pytorch.org/whl/cpu` index; PyPI has none for win-arm64), so on the arm64
laptop the test runs only under a 3.12 venv; elsewhere without torch it skips.

**The trainer option (#1362).** `n2n_smoke.py --train --speckle-weight W [--speckle-sigma S]` adds `W` times the penalty to the
objective, in the training step and in the plateau's held-out objective alike (one routine, `add_speckle`, so the schedule
stops on what the net trains on). Off (the default, 0) is the run it always was: the function returns the loss object itself
before touching anything, the fingerprint gains nothing (`speckle_weight` and `speckle_sigma` are recorded only when set, so a
checkpoint written before still resumes), and a fixed-seed CPU step gives the same loss and weights to the bit
(`test_n2n_speckle_option.py`). Four decisions the option makes, each stated in the code beside it:

- **Where a tile's sites come from: the star layer the draw removed.** The layer is the draw less its truth (the channels'
  mean) in the draw's own noise sigma, read from the stored per-pixel noise plane exactly as `--bright-weight` reads it
  (`star_layer_sigmas`), so the option **needs `--cond-map`'s planes and refuses with a `SystemExit` without them**. A site is a
  local maximum of that layer (5 x 5 neighbourhood) at or above `--speckle-sigma` (default 100, the band where R2's cores
  were dug), at least `BORDER + 3` px inside the tile so its core lies in what the pixel term averages, at most 32 per tile (a
  saturated core peaks on many equal pixels), brightest first, ties by index. No truth is read beyond that: the penalty
  itself needs none, which is what lets real masters use it later (R3b2).
- **The noise cap is the INPUT's**, computed once per tile and step (`noise_cap` is a loop over cells: 0.39 s for 16 tiles
  of 256 x 256 on the win-arm64 laptop's CPU, an extra per step to be measured on the training box before an arm leans on it) and never from the output, whose
  differences a remover can bend. The ring's median and sigma carry no gradient (the module's `detach_stats`).
- **The term is in the pixel term's units**: the penalty summed over every site, divided by the pixels the pixel term averages
  (tile less the 16 px rim, over the batch), so `W = 1` is of the order of the plain objective, and a batch with no site adds an
  exact zero that stays in the graph. It is the output's LUMINANCE (the channels' mean) that is tested, as `StarlessSpeckles`
  tests a luminance.
- **Logged, never gated**: the step line prints `speckle <mean term>` beside the loss while the option is on, and the plateau's
  `val` line is the objective with the term in it.

To turn it on: `run-r2.ps1 -SpeckleWeight W [-SpeckleSigma S]` (the arm-driver takes the same two parameters and hands them to its
train script as `-SpeckleWeight` / `-SpeckleSigma`; the Azure VM's `TRAIN_FLAGS` carry `--speckle-weight` as any other flag). `W`
is not chosen here: R3b is the arm that picks it, against R3a, and the pre-registration names the value before the run.

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

Tracked by #1658; what the mask protects that is not a core, and the default, by #2199 (the survey of 2026-11-17, under R3z's read). The owner saw it three times on the first whole frames: a green-and-red blob in M31's core in E6's stars layer, and the
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
`image remove-stars`, on by default; `--no-protect-cores` turns it off, `--protect-cores` is still accepted), so it holds for any remover, RC-Astro's as well as ours:
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

Through the product (`image remove-stars`, protection on with E6's three models at 8 views), the same cores came back:
2,048 px on the Lagoon, 2,944 px on M31. M31's central 6 px are taken 0 % in every channel. Grown and feathered, the
protected region changes 9,318 and 12,238 px, and keeps 3 of the Lagoon's 13,718 compact sources over 20 sigma and 2 of
M31's 23,728 out of the stars layer. The owner, on the Lagoon's crops: "it does make the stars a lot cleaner". The price
is those few stars, which stay in the starless plate. On by default since #2199; `--no-protect-cores` reads the remover alone. The stacking workbench draws the cores and lets
the user drop or add one (`stacking-workbench.md`, Deep sky, step 6). Pinned by `ExtendedCoreMaskTests`.

##### The survey repeated with the two rules (2026-11-17, #2199)

In every earlier round of this plan, "read without `--protect-cores`" means `--no-protect-cores` today, since protection is now the default and `--protect-cores` is a no-op.

The first survey of this mask over the dataset store's 201 session masters found 148 cores, and a read of their contact
sheets sorted them: a frame-edge rim, a single bright star, dense stars (a cluster), and about 78 extended cores. #2199
turned that read into two rules in `ExtendedCoreMask.Find`, each reading a measure now carried on `Core`:

- **Edge rule.** A source whose map peak lies closer than `Options.EdgeReachPx` (56 px, `FilterBlocks * BlockPx`) to the
  frame's border or to an absent pixel gets no core, since the median window is cut there (`Core.EdgeDistancePx`).
- **Star rule.** A source gets no core when the 90th percentile of the full-resolution light about its peak, over the map's
  peak above the sky, exceeds `Options.MaxPeakRatio` (`Core.PeakRatio`). A star reads as a faint extended source with a tall
  compact peak; an extended source's core is the map itself.

The bar is 6.0. The old survey's gap held stars at 7.2 and over and extended sources at 5.5 and under, so 6.0 sits inside
it and no bar had to move.

| | cores | masters with a core |
|---|---|---|
| Before (no rules) | 148 | not recounted |
| After (both rules) | 77 | 69 of 201 |
| Dropped | 71 | |

The 77 kept are the issue's 78 extended cores to within one. The highest kept peak ratio is 5.49 (an iTelescope Pleiades
master; the next is 4.89) and no kept core is within the edge reach. The three scorecard masters keep their cores to the
pixel: Andromeda (2068, 1484), peak ratio 2.55; the Tarantula (1460, 1516), 3.04; the Sagittarius Triplet (1924, 1340),
1.90. Omega Centauri's centre is still left for #1363: its masters keep a core of 26,368 px and 1,216 px (peak ratio 1.55
and 2.04), neither of which is the cluster's centre as such.

**A person's read of the 77 kept cores (#2201, 2026-11-21): one is a single bright star.** The owner was sent the
contact sheets of the 77 (`sheet.py` over the survey above, two sheets of 48 and 29 tiles numbered in the order of the
masters' names) with the question whether any is a single bright star, and named number 77, the Pleiades master of
2022-01-05. His answer was "77 pleiades", and asked whether that covered all 77 he said "i did look at both sheets".
Number 77 is the kept core with the highest peak ratio,
the 5.49 of the paragraph above, so "extended sources at 5.5 and under" does not hold for it. The core lies at
(3260, 2164) and is 1,024 px; Alcyone is at (3262, 2160) on that master. Its source is 10,067,136 px, since the cluster's
nebulosity joins most of the frame into one source, which is how a star's peak comes to read 5.49 over the map's. The
master of the same field of 2022-01-04 keeps no core. What it costs is in the bright-star reads of #2292 (R4d, ring
medians at 0 to 8 px less the sky, in the master's pixel sigma, R / G / B): on 2022-01-05 the master holds 22,412 /
17,517 / 22,648, R4d as trained leaves 7,823 / 7,794 / 6,375, and through the product's chain the plate reads 22,412 /
17,518 / 22,649, the star handed back whole; on 2022-01-04 the chain reads as the model does, 1,148 / 2,625 / 964 both
ways. The bar of 6.0 is not moved here: the next highest kept ratio is 4.89 (a Large Magellanic Cloud master), and a bar
set between the two would rest on one master and a gap of 0.6. What tells this star from a core is #2360's to measure.
The owner's read names number 77 and no other. The session's own look at the second sheet had also flagged number 68
(the Horsehead master of 2022-12-24, peak ratio 2.73, a pair of stars on the tile; its survey record lists 47 catalogued
stars inside the core, 3 of them at 100 sigma and over or saturated), which his read of both sheets does not name. The
read is his; the session did not look through the first sheet.

To run it (a heavy run, so through the node's queue; about 5 minutes on the 201 masters):

```
dotnet build training/star-removal/coremask-probe/CoreMaskProbe.csproj -c Release -o <build dir>
dotnet <build dir>/CoreMaskProbe.dll <out dir> <session-masters folder>
python training/star-removal/coremask-probe/classify.py <out dir>/survey.jsonl [<earlier survey.jsonl>]
python training/star-removal/coremask-probe/sheet.py <out dir>/survey.jsonl <session-masters folder> <out dir>
```

`classify.py` reports the kept cores against the bounds the probe recorded and, given an earlier survey, lists what was
dropped. The classification itself is the library's: the scripts only read the probe's JSON. Since the clipped-star rule
of the next section (#2360) the probe at its defaults keeps 76 cores, not the 77 of this section and its sheets: with
`--max-core-over-flat-top 0` before `<out dir>` it runs under the two rules above alone and lists the 77 again.

#### A clipped star reads as a core: the star rule has a ceiling (2026-11-21)

The single bright star of the owner's read above is not a matter of where the star rule's bar sits (#2360). He added to
that read: "note that some of the tiles did have a bright star in there as well, but it wasn't a sole bright star without
any actual core of a nebula/galaxy". So a bright star on a real core is what the protection is for, and the defect is a
star with no core under it.

**Why the ratio cannot see it.** The star rule divides the 90th percentile of the light about the peak by the 56 px
median map. A star bright enough to clip has a flat top: the percentile is the clip level however bright the star is, and
the wider the top, the more of the window it fills. Alcyone's top on the master of 2022-01-05 is 773 px (a disc of
15.7 px radius), the 56 px median under it reads 0.162 of the top, and the ratio reads 5.49. The same star on the master
of 2022-01-04 reads 7.18 and was turned away by a hair: the old survey's gap, stars at 7.2 and over and extended sources
at 5.5 and under, was this one star on both sides of the bar. The catalogue step does not catch it either:
`tianwen image protect-cores` on the master of 2022-01-05 (the installed ASTAP database, 15,435 stars) prints "1 of 35
compact peaks inside the cores are catalogued stars and stay removed". A flat top has no compact peak at its centre for
the catalogued position to fall on; where the 35 lie and which one was matched is not read (it is since: the section
after this one).

**The measures.** `ExtendedCoreMask` reads the flat top at a source's peak and carries it on `Core`: `FlatTopPx` (the
connected pixels at 0.9 of the brightest light near the peak), `FlatTopRoundness` (the axis ratio times how well the top
fills the ellipse of its second moments, 1 for a filled disc) and `FlatTopFall` (the light two top radii out, as a share
of the top's). The probe with both star rules off lists every source of the 201 masters with them, 135 in all:

| | sources | with a top of 50 px or over | roundness | fall |
|---|---|---|---|---|
| Stars (peak ratio over 6) | 58 | 37 | 0.71 to 0.98, median 0.95 | 0.010 to 0.144, median 0.020 |
| Alcyone on 2022-01-05 (peak ratio 5.49) | 1 | 1 | 0.93 | 0.043 |
| Kept cores of clipped nebula light: Orion (6), the Helix (3), the Tarantula, a Large Magellanic Cloud knot | 11 | 11 | 0.29 to 0.87 | 0.221 to 0.752 |
| Kept cores with eta Carinae on the Keyhole | 2 | 2 | 0.94 and 0.97 | 0.083 and 0.036 |
| Kept cores, the others | 63 | 0 | | |

The fall tells a star's top from clipped nebula light, with a gap from 0.144 to 0.221. Roundness alone does not: two of
Orion's tops read 0.83 and 0.87, and one star reads 0.71 (eta Carinae on a master where the star rule turns it away).
What tells Alcyone from eta Carinae, a clipped star on a real core, is the core against the top: Alcyone's core is 1.3
times its top, eta Carinae's two are 71 and 128 times theirs.

**The rule.** A source whose map peaks on a flat top of 50 px or over that is round (0.75 or over) and falls off as a
star does (0.18 or under, the geometric middle of the gap), and whose core is no more than `Options.MaxCoreOverFlatTop`
(9, the disc of three top radii) times that top, gets no core. Run over the 201 masters at the new defaults, the 76 other
kept cores are each as they were (place, area and ratio) and Alcyone's is gone.

**What it rests on, and what it does not do.** One star was kept by the old rules, so the rule is derived from the
ratio's ceiling and not fitted to a class: the shape bars are read off 37 stars the star rule already turns away, the
size bar off three cases (1.3 against 71 and 128). A small, round, clipped object with a sharp edge and no wider light
around it (a compact planetary nebula, a star-like nucleus) would read as a clipped star; none is among the 201 masters.
On a plate-solved master the catalogue could arbitrate, which is not built. Eta Carinae stayed with the Keyhole's core
under this rule: taking a clipped star out of a real core is the catalogue step's work (it has to read a flat top as one
peak), which the section after this one builds (#2373).

**A model's second look at the 77 (2026-11-21).** At the owner's suggestion the tiles were judged a second time, as a
check beside his read and not in its place: one image of four views per core (`tiles.py`), no names, by four Sonnet
subagents of the session. The suggestion was a batch through the API; the token at hand had no batch right, which was
not worked around. The second look names number 77 as the one sole star it is sure of. It also called numbers 4 and 10
sole stars at medium confidence and nine tiles stars only. The session looked at all twelve at full size: ten have nebula
under the contour (eta Carinae and the Keyhole on six, the Rim Nebula's edge on three, a small nebula on number 10), and
number 68 is two stars on a faint haze, which the owner's sentence above covers. The tile's third view misleads where a
star is in the crop: linear to the brightest pixel shows the star and nothing else.

To run the survey of the measures:

```
dotnet <build dir>/CoreMaskProbe.dll --max-peak-ratio 1e9 --max-core-over-flat-top 0 --no-weights <measures dir> <session-masters folder>
python training/star-removal/coremask-probe/flat_tops.py <measures dir>/survey.jsonl
```

`flat_tops.py` prints the table's rows by class from the probe's JSON and what the rule at given bars would turn away;
the measures are the library's.

The tiles take a probe run of their own, since `tiles.py` reads each core's weight plane and numbers the tiles as the
contact sheets do. That run writes the weights (no `--no-weights`), keeps the star rule's bar at its default and turns
the clipped-star rule off, which lists the 77 cores of the sheets, Alcyone's last:

```
dotnet <build dir>/CoreMaskProbe.dll --max-core-over-flat-top 0 <sheets dir> <session-masters folder>
python training/star-removal/coremask-probe/tiles.py <sheets dir>/survey.jsonl <session-masters folder> <sheets dir> <tiles dir>
```

#### A clipped star on a real core: the catalogue leads on its flat top (2026-11-21)

The case the section above left (#2373): a clipped star on a core that is a core. Of the 76 kept cores on the 201
masters two have a star's flat top, both eta Carinae on the Keyhole: the SII master of 2024-03-07, which is plate-solved,
and the colour master of 2023-02-24, which is not and was read here on a scratch copy solved with `tianwen solve`.

**Where the catalogue step's peaks lie.** `CoreStars.Find` records every compact peak it judged; the probe's
`--core-stars` mode prints them with the catalogued stars on the cores, from the installed ASTAP database as
`image protect-cores` reads it.

| | eta Carinae, SII 2024-03-07 | Alcyone, Pleiades 2022-01-05 (clipped-star rule off) |
|---|---|---|
| Flat top | 87 px, radius 5.3 px | 773 px, radius 15.7 px |
| The catalogued star | magnitude 4.6, 0.6 px from the top's centre | magnitude 3.0 |
| Peaks on the rim | 3, at 0.9 radii, 5,600 to 6,000 sigma | 11, at 1.0 to 1.1 radii, 4,700 to 9,900 sigma |
| Peaks inside the top | none | 6, of 12 to 29 sigma, the nearest 1.6 px from the star |
| A peak within the 1.5 px match radius | none (the nearest 4.5 px) | none |
| Before the fix | 43 of 88 peaks are stars, eta Carinae not one | 1 of 35, Alcyone not one |

The compact light is the smoothed light less a 15 px median. Across a clipped top the smoothed light is level and the
median falls toward the rim, so the difference rises to the rim's inner edge: that is why the peaks stand there (a
reading; where they stand is measured). On a wide top the miss is chance, a ripple of the plateau a tenth of a pixel
outside the match radius.

**What it does to the picture.** On the SII master StarXTerminator and R4d as trained both take eta Carinae out to
nothing (ring medians at 0 to 48 px of 0, and of 0 to 1, in units of the crop's far scatter, against 1,265 at the centre
of the master), and the protection puts the star back whole. On the colour copy the chance goes the other way and is
worse: one of the four peaks about the top is matched (the fix makes those four one, and the count of stars is 111 with
it and without), the star gets the line's reach of 28 px, and 193 pixels 6 to 20 px from the top's centre keep the
input, a wedge of the star's own rim at 869 sigma by the median and 2,813 at most, which is the clip level. It stands in
the starless plate of either remover as a white blob beside a star that is gone. Which of the peaks was matched and
which held the wedge against the star's disc is not printed.

**The rule.** Where the mask read a star's flat top on a core (`Core.HasStarTop`: the clipped-star rule's own three
bars, and `Core.FlatTopX`/`FlatTopY` for its centre), the top is one peak at its centre. It is a star when a catalogued
star lies on the top (within its radius and the match radius) that can carry what the top's highest peak shows, by the
same slack as for any peak: the top shows the least the star can be. Its peaks, within the top's radius and a local-peak
window (5 px), are then its own and are not judged again. That takes in whatever else peaks there, a faint catalogued
star or a knot 5 px past the rim: it is not judged by itself and goes with the star. So the star's disc is never
smaller than that window and a pixel, whatever reach the frame gives it, and a peak set aside always lies inside the
disc, where the remover's plate stands (the review of #2393 asked; the reach below was already far past the window on
both masters, 40 and 45 px against 10 and 13, but nothing had made it so). With no such star on the top its peaks are
judged one by one as before, so a round clipped knot with a faint star on it stays with its core.

**Its reach.** The frame's line is drawn through stars that do not clip and falls short of one that does: 25 px for eta
Carinae on the SII master (12.4 px at magnitude 10 and 2.3 px more per magnitude), where the star's light stands 26
sigma over its far sky at 27 px, 17 at 30 px, 12 at 36 px and 4 at 42 px. A disc of that reach would leave a ring of the
star's halo around a hole. So a clipped star's reach is the farther of the line's and its own rings', read as the line's
stars were (ring medians down to one sigma over the middle of its three outermost rings): 40 px on the SII master and 45
px on the colour copy, where the master's rings stand 40 sigma at 30 px, 24 at 36 px, 15 at 42 px and nothing at 48 px.

**What changes.** Nothing where no core has a star's top: with R4d's plates `image protect-cores` built from this
change writes the same bytes as main's on the Andromeda, Tarantula and Sagittarius masters. On the SII master 44 of 86
peaks are stars (43 of 88) and the star's place is the remover's plate, with either remover. On the colour copy 111 of
253 (111 of 256): no pixel within 31 px of the top's centre keeps the input, the wedge is gone, and the nebula beside
the star is as the removers left it, which on that frame is as the master has it.

**What it does not do.** (1) A clipped star away from where its core's large-scale light peaks: the mask reads one top
per core, at the map's peak. (2) A top under 50 px, the bar of the rule above. (3) Tell a star's far halo from a bright
nebula's own fall-off. Ring medians cancel a slope and not a curvature, so on the peak of a bright smooth nebula the
rings do not come down to one sigma and the reach is the cap (48 px, as for any star bright enough by the line). That is
how the test's scene reads (a star on the peak of a nebula of 3,000 sigma); no master of the archive shows it, and on
the colour copy, the brightest nebula at hand, the rings end before the cap. Inside that disc the remover's plate
stands, which is right where the remover took the star and wrong where it dug a knot beside it. (4) The colour master
itself: unsolved, the catalogue step does not run on it, as before.

To print the peaks and draw them:

```
dotnet <build dir>/CoreMaskProbe.dll --core-stars --no-weights <out dir> <master.fits>
python training/star-removal/coremask-probe/core_stars_where.py <out dir>/<stem>.corestars.json <master.fits> <out.png>
```

The probe is given the library's internals for this (the peaks are an internal overload a test reads too).

#### Core protection without the stars: the catalogued stars inside a core stay removed (2026-11-20)

**Issue:** #2242 (core protection puts back the stars inside a core: 30 Doradus's cluster, M31, the Lagoon).

Protection puts the INPUT back in a core, so every star that sits there comes back with the nucleus or knot. The
owner saw it on the first renders he asked for (2026-11-19): 30 Doradus's cluster whole, a handful of stars beside
M31's nucleus, the Lagoon's two bright ones, on every model alike, since it is the input that returns.

- **Neither the peak's width nor the model's own take tells a nucleus from a star.** Putting back only the smooth
  part of what protection restores (a median of 9, 15 or 25 px) loses 10 to 23 % of M31's nucleus, which is as
  narrow as a star (`pc_smooth_proto.py`). The share of a compact peak the model took (`pc_ratio.py`) reads 111 %
  for M31's nucleus on R4c and 99 % on R3z, exactly as for the 16 stars in the same core (100 to 174 %); only R4d
  takes it differently (22 %). The models that need protection most take the nucleus as they take a star.
- **A star catalogue does** (`pc_gaia.py`, the installed ASTAP database through `tianwen solve --export-gaia`). M31:
  17 compact peaks over 30 sigma inside the core, 16 with a catalogued star within 0.7 px, and the one with none is
  the nucleus. Lagoon: the two bright stars are catalogued (BP 5.9 and 7.0); the Hourglass knot has only a BP 15.6
  star on it against a peak reading 8.8. 30 Doradus: 40 of 110 peaks over 10 sigma are catalogued; the database is
  at its density cap there and R136's centre is unresolved at 2.87 arcsec/px.
- **The step** (`CoreStars` in TianWen.Lib, beside `ExtendedCoreMask`; prototype `pc_catalog.py`). The compact light
  is the luminance smoothed by 1 px less its 15 px median. A local peak of it inside a core, over 10 sky sigmas, is
  a star when a catalogued star lies within 1.5 px AND carries at least a tenth of the peak's light: the peak may
  read at most 2.5 magnitudes brighter than that star on the frame's own zero point (the median of magnitude plus
  2.5 log of the compact peak over the catalogued stars the frame shows away from the cores; scatter 0.14 to 0.22
  mag). The star's footprint is a disc of the frame's own reach for its magnitude (the radius where ring medians
  of isolated catalogued stars sink to one sigma, a straight line by magnitude), grown by the feather and
  feathered 1.5 px; where it meets a peak the catalogue has no star for that stands at least as high, each pixel
  goes to the nearer of the two in units of their reach. Inside the footprint the mask's weight is taken out, so
  the remover's plate stands; everywhere else protection is as before. With too few stars to read a zero point (20)
  the mask is left as it was.
- **Two footprint rules that failed first.** A fixed small radius left the bright stars' halos to come back. The
  model's take about each peak ran every radius to its cap, since inside a core the model takes broad light too,
  and a neighbour's disc then covered M31's nucleus (5.8 to 9.3 % of it lost).
- **Where the stars come from** (`CatalogFrameStars`): a plate-solved frame, and the star database ASTAP installs
  where there is one (Gaia, to about magnitude 17 at its cap), else the embedded Tycho-2, which holds the Lagoon's
  two stars (a test reads them at 5.97 and 7.09). With no solution or no catalogue, protection is as before and
  says so. `image remove-stars` and `image combine` ask on their own (`--core-catalogue none` turns it off, `embedded` asks Tycho-2 alone), and
  `image protect-cores --input <master> --starless <plate>` is the step alone, with no remover run; without a
  catalogue it writes the plate the old step wrote, bit for bit, on the three masters.
- **Read on three models' plates, the product's own step** (`tianwen image protect-cores` on solved copies of the three
  masters, 2 s a master; the footprint depends on the input and the catalogue alone, so it is the same for every
  model: 54 of 76 compact peaks in M31's core, 39 of 114 in 30 Doradus's, 23 of 94 in the Lagoon's). Protection
  as it was against protection with the catalogued stars left removed:

  | | R4c seed 0 | R4d seed 0 | R3z seed 0 | StarXTerminator |
  |---|---|---|---|---|
  | M31 nucleus, light taken in the central 15 px box, R / G / B, % | 0.0 / 0.0 / 0.0, as before | 0.0 / 0.0 / 0.0, as before | 0.0 / 0.0 / 0.0, as before | 0.1 / 0.2 / 0.1 |
  | scorecard's nucleus row, % | 0.0 / 0.0 / 0.0, as before | as before | as before | |
  | Lagoon, brightest tenth of the peaks standing (18 peaks) | 3 to 1 | 3 to 1 | 3 to 1 | 1 |
  | Tarantula, brightest tenth of the peaks standing (77 peaks) | 21 to 12 | 21 to 17 | 21 to 17 | 17 |
  | Andromeda, all peaks standing, % | 1.0 to 0.3 | 1.2 to 0.3 | 1.1 to 0.3 | 0.1 |
  | Lagoon, light taken in the central box, % | 0.0 to 3.1 | 0.0 to 5.8 | 0.0 to 1.7 | 5.6 |
  | broad light taken, Tarantula / Sagittarius | 4.35 to 5.06 / 1.64 to 1.92 | 4.87 to 5.54 / 1.73 to 2.06 | 3.87 to 4.21 / 2.43 to 2.57 | 5.99 / 0.42 |

  Every other scorecard row is the as-trained row before and after. The cost is in the last two rows: inside a
  star's footprint the model's plate stands, so the broad light it took there is no longer given back.
- **With the embedded catalogue alone** (`--core-catalogue embedded`, R4c's plates, as a machine without ASTAP runs
  it; zero points 7.66, 8.57 and 5.73 over 369 to 3,000 Tycho-2 stars, scatter 0.23 to 0.29 mag): 3 of 76, 3 of 114
  and 2 of 94 peaks are taken as stars. The Lagoon's two bright stars stay removed and the Hourglass comes back
  whole, so its brightest tenth standing falls from 3 to 1 of 18 as with the database; the Tarantula's from 21 to
  16 of 77; M31's nucleus is whole and the fainter stars beside it come back as before, being past Tycho-2's reach.
- **The product's chain, protection and then the dig fill** (R4c's plates, `whole_floor.py floor4` on the protected
  plate, the order `SplitStarsAsync` runs them in). Every scorecard row equals the old chain's (protection as it
  was, then floor3): Andromeda dark flat 98.2 and dug 0.9, Tarantula dark flat 95.5 and crowded 90.4, the nucleus
  0.0. The nebula rows: light added 0.5 / 2.6 / 3.2 % with the old chain and 0.1 / 0.8 / 0.3 % with the new one;
  brightest peaks standing 21 of 77 and 3 of 18 against 12 and 1. Inside a core the fill now meets the model's plate
  in the star footprints: in the 100 px crops it changes 39 pixels on M31 (0.1 sigma at most), 94 on the Lagoon
  (one inside a footprint) and 914 on 30 Doradus (716 inside footprints, up to 9.9 sigma, 87 pixels left over the
  input by a sigma).
- **Looked at** (`D:/temp/tianwen-scratch/r4-look/pc-zoom-<model>-pcs.png`, the three cores at 6x, on three models; R4c's
  sheet carries the chain's plate as a sixth column, which shows no patch on any of the three). M31: the nucleus
  whole, the stars beside it gone, as on StarXTerminator's plate. Lagoon: the two bright stars gone with no ring
  where they were, the Hourglass kept. 30 Doradus: the stars around R136 gone; the unresolved centre stands as the
  model leaves it (StarXTerminator leaves a clump there too), and cluster members the catalogue does not list come
  back as before.
- **Limits.** It needs a solved frame; the two held-out masters without a solution were solved as copies. The star
  test rests on three objects (magnitudes as the prototype read them): R136's centre reads 1.9 magnitudes brighter
  than its catalogued star and goes with the stars, the Hourglass 6.8 and stays, M31's nucleus has no star within 1.5 px (the nearest is 2.7 px off, so a
  coarser frame than 6.9 arcsec/px would put it inside, where only the magnitude test stands). A catalogued star as
  bright as a nucleus, on it, takes it; no such case is in hand. Adoption is the owner's.
- **What the review left, measured after the merge (#2257).**
  - **How far a kept peak pushes into the stars beside it** (the peaks the step judged, printed by a one-off build;
    `pcs_rivals.py` in `C:/temp/e2/explore`). M31's nucleus stands 2,898 sigma in compact light and reads magnitude
    9.89 on the frame's zero point; it holds 14 px: the 11 px the frame's reach line gives that magnitude (25.5 px
    less 1.50 px a magnitude), 1 more, and 2 of feather. Over the three cores the stars hold 5 to 13 px and the
    kept peaks 5 to 14. A kept peak reaches at most 2.0, 1.4 and 1.5 times as far as a star it meets (M31, 30
    Doradus, the Lagoon); of the stars that meet one (15 of 54, 21 of 39, 12 of 23) none keeps under 55 % of its
    disc, and the medians are 91, 85 and 88 %. The review's case (a star of 8 px, 10 px from a kept peak of 51 px,
    keeps 9 of its 197 px) needs a peak at the 48 px cap, 15 magnitudes brighter than these lines put it. It is
    written down as the rule's limit in a test: beside a nucleus that reaches three times as far (22 px against 7,
    11.7 px apart) a star keeps its centre (footprint 0.93 there) and 48 of the 149 px of its disc; the rest comes
    back with the core, as all of it did before there was a catalogue.
  - **One defect, found by the tests of the wiring.** A FITS header gives a WCS without a plate solution too: its
    pointing alone, or a matrix made up from a pixel scale and an angle. The verbs asked the catalogue with either.
    An unsolved master was told "the plate solution cannot place the frame on the sky" where it should be pointed
    at `tianwen solve`, and a made-up matrix would have put the stars pixels off their peaks. The read and the
    verbs now ask `CatalogFrameStars.IsSolved` first. The three solved masters give the plates they gave, no value
    different.
  - **The wiring is tested**: the star split with the catalogue handed over, the recipe (a star left in the
    starless plate with its core is denoised and loses 35 % of its peak to a 3 px mean; one in the stars loses
    nothing), and `image protect-cores` and `image remove-stars` on a frame of real sky drawn from the embedded
    Tycho-2 (290 stars about the Lagoon, a galaxy drawn beside one of them).


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

**The sample export** (synth4's recipe, the 8 R2 validation sessions, 40 cells each, one draw a cell, once with `--kept-cores` and
once without; scratch `C:/temp/tianwen-scratch/TIA1658`, `measure_cores.py`):

| | without | with |
|---|---|---|
| cells holding a core | 0 of 320 | **50 of 320 (15.6 %)**, 4 to 9 per session |
| tiles of the 270 cells without a core (clean, draw, sigma) | 1,080 | the same 1,080, **byte for byte** |
| injection and degradation manifests | | identical, but for the 50 cells' `Core` field and clean-tile MAD |
| cores' drawn peak | | median 1,088 sigma, 212 to 5,404 |
| cores held by the ceiling | | 10 of 50, 8 of them on HIP 80609's three views, the brightest stretched pixel 0.98 |
| core height, input over target (stretched, 5 x 5 at the centre, less the cell without the core) | | **median 1.0000, 0.982 to 1.000** over every core and channel |

The core is the same in input and target. The few points under 1 are injected stars landing on a core's centre, which
the input holds and the target does not. The cores' minor-axis FWHM ran 4.6 to 12.1 px over these PSFs, and their colour
ran from Small-Magellanic-Cloud's red (0.39 / 0.17 / 0.09 stretched) to HIP 80609's near-white. Training on them waits
for the GPU and for the owner's call on which arm carries them. That arm is read against the nucleus guard (at most 5 %
of M31's central 6 px taken in every channel), where the before numbers are R2i's 3.1 / 3.1 / 0.3 %, E6's 16.5 / 7.9 /
6.2 % and R2l's 35.9 / 14.9 / 14.9 %.

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
(`--protect-cores`, #1658, now the default) applies to it as to any remover.

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
and its knots must count). Both were stretched with the plate's own parameters (H6) for R2q; since round 3 they take the
master's by default, as synth11's draws do (`--stretch-from master|plate`, #1776), and an output holds one stretch
(`stretch.txt`). On this window the master's slope at its sky is 1.32 / 1.53 / 1.48 times the plate's, so R2q's tiles
were stretched more gently than inference stretches. Every tenth seed is held out whole, in both
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

**The scorecard** is one command, in the repo since #1774 (`training/star-removal/score_whole.py`, its paths from a
config; `training/README.md`, "Scoring an arm"), `python score_whole.py <tag> --config scorecard.local.json --model <onnx>`
(one view, how a model ships): Andromeda's
bright dark-sky stars flat and dug and blue's residual at 1.5-3 and 3-4.5 px, the Tarantula's bright dark-sky stars flat
and left and its core residual per channel, its crowded stars, Gaia BP 17-19 and purity, the Sagittarius Triplet's
crowded stars, and M31's nucleus. Its guards are round 1's: the nucleus at most 5 % in every channel, purity at most 1.0 %.
The judges' rows are the ceiling, R2i seed 0's the small-model baseline and R2t's the round 1 base.
The repo copy, run with tag r2t over R2t's own three plates and no model, wrote `row.txt` byte for byte as the scratch
copy had (checked 2026-10-23, no GPU).

**An arm's scorer and driver are in the repo too** (#1774, `training/star-removal/score-arm.ps1`, `arm-driver.ps1`,
`register-arm-task.ps1`; paths from the `arm` object of the same config). `score-arm.ps1` is what scored R2nc to R2u after
their weights came home: A's draws and R2k's twins at one view, the real tiles' check, the ring and M31's nucleus over 8
views, with `real_star_check.py` and `ring_annulus.py` (moved beside it, their paths now arguments) and `m31_nucleus.py`.
**Every step is skipped when its output exists**, the three reads included (they used to run again on every call), so a
second call over a scored arm does no inference and uses no GPU. `arm-driver.ps1` is R2v's driver made generic: a scheduled
task every 10 minutes (`conhost --headless`, priority 4, `IgnoreNew`), one step per pass, a stop file, and a failing step
writes it so it is not retried all night. **Checked 2026-10-23** against R2u's existing outputs: `score-arm.ps1` skipped
every step and exited 0 (only `score.log` grew); the three reads re-run into scratch matched the scratch copies' files line
for line (ring: R2u -0.20 / -0.52 / -0.38 sigma, 41.1 % dug, 13.8 % left; nucleus 0-6 px +0.0 / +3.5 / +2.1 %; real-star
5-20 sigma left |<1| 96.11 %), and the driver, pointed at that arm with a scratch runs folder, scored and wrote its done
file with no GPU.

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

#### The glow row's history and its bar (#1871, 2026-10-28)

The row (`training/star-removal/glow_ring.py`, field `And glow` / `Tar glow` of `training/README.md`'s "Scoring an arm" table) reads the
light a remover leaves in a wide halo around a bright clipped star, which the core rows do not see. Every number is a sum over the
10 to 30 px annulus of the starless plate's excess over its own 45 to 60 px ring, in the export's stretch and the master's own noise
sigma (sigma-pixels). **Thresholds fixed once from the INPUT masters** (`--derive-threshold`, a tenth of the input's own median
halo, per channel R / G / B; kept in the local config, never re-derived from an arm's plate): Andromeda 273.9 / 187.1 / 148.6 (71
stars, input median 2739 / 1871 / 1486), Tarantula 252.6 / 225.7 / 153.0 (13 stars), Sagittarius 123.3 on its one plane (7 stars).

Median of the glow, R / G / B, in sigma-pixels (p90 in `score.json`; `score_whole.py --plates` on each arm's linked whole-master
plates, no inference). The input's own halo is the ceiling the removal starts from, so a lower number is cleaner and a negative one
is a hole:

| arm | Andromeda median | Andromeda p90 | Tarantula median | Tarantula p90 | Sag median |
|---|---|---|---|---|---|
| R2i seed 0 | 420 / 488 / 207 | 1912 / 1838 / 1197 | 1501 / 1269 / 721 | 4224 / 3327 / 2455 | 1590 |
| R2i seed 1 | 436 / 460 / 324 (M31 8-view plate only) | 2060 / 1783 / 1528 | missing | missing | missing |
| R2s2 | 497 / 472 / 292 | 1997 / 2072 / 1227 | 1441 / 1161 / 653 | 3724 / 3398 / 2587 | 1687 |
| R2t | 499 / 547 / 305 | 1960 / 1552 / 1491 | 1748 / 1395 / 897 | 3191 / 3004 / 2482 | 1972 |
| R2u | 574 / 494 / 379 | 2147 / 1691 / 1407 | 1741 / 1526 / 912 | 4606 / 3788 / 2840 | 1996 |
| R2v | 415 / 501 / 307 | 1911 / 1890 / 1236 | 1692 / 1491 / 871 | 4286 / 3401 / 2422 | 1817 |
| R2w | 254 / 447 / 444 | 1323 / 1685 / 1487 | 1960 / 1748 / 1020 | 4386 / 3869 / 2579 | 1957 |
| R2x | 556 / 638 / 325 | 2120 / 2233 / 1424 | 1734 / 1520 / 911 | 4698 / 3749 / 2357 | 1918 |
| R3a seed 0 | 566 / 680 / 366 | 1840 / 1938 / 1869 | 2089 / 1490 / 1025 | 4633 / 3548 / 2571 | 2278 |
| R3a seed 1 | 701 / 700 / 334 | 2266 / 1721 / 1610 | 2047 / 1572 / 955 | 4538 / 3259 / 2392 | 2233 |
| R3b seed 0 | 577 / 512 / 320 | 2067 / 2272 / 1592 | 1903 / 1559 / 920 | 4392 / 3615 / 2571 | 1995 |
| R3b seed 1 | 723 / 481 / 440 | 2426 / 1933 / 1721 | 2039 / 1593 / 952 | 4516 / 4057 / 2653 | 2147 |
| StarNet2 (judge) | 562 / 401 / 459 | 1241 / 837 / 1042 | 746 / 885 / 213 | 1118 / 1277 / 593 | 798 |
| SXT (judge) | 157 / 243 / 391 | 710 / 575 / 898 | -39 / -31 / -55 | 169 / 203 / 16 | 332 |

**Missing, named.** R2i seed 1 has no whole-master Tarantula or Sagittarius plates on this machine (only M31's 8-view plate,
`m31_r2i_synth4_s1_v8.fits`, was kept), so its row is Andromeda alone and its seed 0 comparison uses the same 8-view plate (401 /
534 / 211; the single-view row above is 420 / 488 / 207). R3a and R3b's plates are those of the F2 whole-master scorecard
(`f2-whole-master/r3a_s0` and the rest; PR #1868). R2u and R2x are listed for history only.

**What the row says.** (1) Every arm of ours keeps a glow of 200 to 700 sigma-pixels in Andromeda and 650 to 2100 in the Tarantula,
between a seventh and four fifths of the input's own halo (medians 2739 / 1871 / 1486 in Andromeda, 2526 / 2257 / 1530 in the Tarantula): the visible glow is
real and no arm has cleared it. (2) StarNet2 leaves a half to a third of that in the Tarantula (746 / 885 / 213 against our arms' 1441 to 2089 / 1161 to 1748 /
653 to 1025: about 0.36 to 0.52 in red, 0.51 to 0.76 in green, 0.21 to 0.33 in blue) and SXT removes it with a slight
over-removal (negative medians: it digs the sky below the ring), which is the gap a later arm is measured against. In Andromeda
StarNet2 (562 / 401 / 459) is not below ours: R2w and R2i seed 0 beat it in red and blue, so the gap is a Tarantula one. (3) The
**share above threshold** is not discriminating: a tenth of the input's halo sits far below what any of these plates leaves, so
44 to 90 % of Andromeda's stars and 92.3 % (12 of 13) of the Tarantula's are above it for every arm of ours, in steps of 7.7 points
on the Tarantula. Read the median and p90. The share does not even separate the judges: StarNet2's Tarantula medians (746 / 885 / 213) sit above the
thresholds (252.6 / 225.7 / 153.0), and with 13 stars the median is the 7th value, so at least 7 of 13 stars (53.8 %) are above in
every channel. Only SXT (p90 169 / 203 / 16, all under the thresholds, so at most one star above) is in the 0 to 8 % range. On
these numbers the share separates SXT from everything else, StarNet2 included, so it is no gate for a judge-level arm short of SXT.

**The seed spread, the bar.** Two seeds of one recipe, per channel R / G / B, in median sigma-pixels:

| pair | Andromeda | Tarantula |
|---|---|---|
| R2i seeds 0 and 1 (M31 8-view plate) | 35 / 74 / 113 | not scored |
| R3a seeds 0 and 1 | 135 / 20 / 32 | 42 / 82 / 70 |
| R3b seeds 0 and 1 | 145 / 31 / 119 | 137 / 34 / 32 |

**The bar a later arm must clear: a glow median lower than the best arm of ours by more than the widest spread, 145 / 74 / 119 in
Andromeda and 137 / 82 / 70 in the Tarantula** (R, G, B), on at least two of three channels. R2i's seed mean is 418 / 497 / 268 in Andromeda. A single-seed gap inside those numbers is noise. R2w's red (254, 166 under R2i seed 0) is
the one arm-level difference that clears the Andromeda red bar, and it is paid for in blue (444, 237 over R2i seed 0).

**R3b's dig penalty in the glow row.** R3b is R3a plus the dig penalty, whose A's-draws read showed the penalty did not cost the
bright-star dig. The glow row separates them only in Andromeda green: the two-seed mean falls from 690 (R3a) to 497 (R3b), 193
under it against a 20 to 31 seed spread, while red (633 to 650) and blue (350 to 380) stay inside the spread, and the Tarantula
moves by 97 or less in every channel (R3a 2068 / 1531 / 990, R3b 1971 / 1576 / 936). So the dig penalty did not raise the glow
(it did not buy a bright-star hole at the glow's expense), and it lowered Andromeda's green glow by more than a seed spread. R3b
does not reach the bar against R2i: its seed mean is 650 / 497 / 380 against R2i's 418 / 497 / 268 in Andromeda.

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

#### A `--session` too short to name a session is refused (#1778, 2026-10-23)

A sample export took the whole bake because a PowerShell splat turned `--kept-cores` into twelve one-letter values of
degrade's `--session`: the option takes several values per flag, each a case-insensitive substring of a session id, and
"e" or "t" matches 201 of 201. The read: this was a guard the verbs never had, not a bug in the matching, so the rule is
one shared helper (`SessionFilter`, `TianWen.AI.Imaging`) and not a per-verb patch. A value under 3 characters is refused
and named, by the parser for the CLI (one option factory used by every declaration) and again by the exporters for a
library caller; and each verb says what the filter selected, the count of the total and every id, before it reads or
writes anything: through the console, since a Release CLI's console logger shows warnings only and a logger line would reach
the file log alone (measured: `bright-cells --session zzzzqq` printed no selection line before the fix), and after
`--max-sessions` has cut it, so the count is what runs. It covers `dataset degrade`, `bright-cells`, `noise-check` and `noise-planes`, which are the verbs whose
`--session` takes substrings. **Left alone on purpose:** `dataset build --session` and `--rebuild-session` are wildcards
(`*M42*`), a different shape that a short value does not blow up; `gradient-export --only` matches master file names; and
the cross-night `--exclude` and `--object` are not session filters. Pinned by `SessionFilterTests`, with no data.

#### The export trains in a gentler stretch than inference runs (#1776, 2026-10-23)

Found by the S2 worker (#1737) while reading the synthetics review's numbers. Inference stretches the whole master with its
own parameters (`OnnxTianWenStarRemover`, `ChunkedNafnetRunner.ApplyInputStretch`). The stars export stretched both
halves of every pair with the starless plate's (H6, "stretch input and target with the TARGET frame's MTF parameters", a rule
written for the deconvolver, whose target and input share a floor). `MtfStretch` anchors each channel on its minimum, and a
plate's minimum sits in its few deepest subtraction holes. So the plate's sky stands further above its floor, and its stretch
is gentler there. Every model so far has trained in a gentler stretch than it runs in.

The survey (`degrade --mode stars`, one cell of each of the 138 training masters; every row now records
`StretchSkySlopeRatio`, the master's MTF slope at its own sky over the plate's at the same value):

| | red | green | blue |
|---|---|---|---|
| broadband (54), median (IQR) | 1.13 (1.00-2.05) | 1.00 (0.98-1.88) | 1.00 (0.99-1.48) |
| dual-band (69), median (IQR) | 1.31 (1.07-1.63) | 1.10 (1.00-1.45) | 1.00 (1.00-1.24) |
| mono (15) | 0.97 (0.81-1.00) | | |

73 of the 138 masters have a channel stretched at least 1.25 times more gently in training than at inference, 33 at least
twice, and a few 8 to 30 times (the two Omega Centauri nights, V1045 Ori's blue, eta Carinae). It runs the other way too:
18 masters have a channel the plate's stretch takes MORE steeply, by 0.8 or below (the Running Chicken night's green and blue
at 0.67 and 0.69), so 91 of the 138 are off by 1.25 times or more one way or the other. Red is the gentle channel
most often, which bears on the synthetics review's colour gap (S3, #1738: stretched stars bluer than real) and on its
brightness gap (S2, #1737: stars read twice their real sigma).

**The fix**, `degrade --stretch-from master`, stretches both halves with the master's own parameters through the runner's
one helper (`ChunkedNafnetRunner.InputStretchParameters`), so the pair lands where inference puts the master. The default
stays `plate`, which leaves every export and every arm so far as it was.

#### R2w: the pairs stretched as inference stretches the master, pre-registered (2026-10-23)

Written before it trains. **The arm.** R2i's recipe (run-r2.ps1, no teacher, no extra cache) on synth11, seed 0, on the
1070 after R2v: synth4's command with `--stretch-from master`, every star, colour and background draw synth4's. One change
against R2i seed 0. **Where it is read.** The whole-master scorecard and the real tiles stretch each master with its own
parameters, R2w's training domain. A's draws and R2k's twins were exported with the plate's stretch, so R2w meets them out
of its domain: they are read, not predicted.

**Predictions** against R2i seed 0's whole-master row:
1. The held-out Tarantula's core residual under +0.5 sigma in every channel (+0.89 / +1.06 / +0.61). A star met in a steeper
   stretch than it was taught in reads brighter at its core than any it learned, which is the light left in sharp cores.
2. The real tiles' bright stars flat on at least 97.0 % (R2i seed 0 96.4).
3. No guard lost: M31's nucleus at most 5 % in every channel, purity at most 1.0 %.

**Read, not predicted:** Andromeda's dig and blue residual, the crowded fields, A's draws and the twins.
**Kill:** (3) failing.
**Read:**
- (1) and (2) holding make the master's stretch every later export's default.
- Either missing with (3) holding means the skew was real but not what kept the cores.

#### R2v's read: killed by the nucleus's red, and Andromeda's blue moves toward its bars without reaching one (2026-10-23)

R2v stopped on its own at step 217,500 (best at 193,500), twice R2s2's 110,500 on the same recipe: 9.8 hours on the 1070.
Against its pre-registration, with R2i seed 0 as the baseline:

| | R2i seed 0 | R2v | bar | |
|---|---|---|---|---|
| Andromeda blue, 1.5-3 px | -1.10 | -0.91 | above -0.8 | misses |
| Andromeda blue, 3-4.5 px | -0.90 | -0.84 | above -0.5 | misses |
| Andromeda bright stars dug | 36.6 % | 32.7 % | at most 30 % | misses |
| M31's nucleus taken, R / G / B | 3.2 / 3.1 / 0.5 % | **13.9** / 3.4 / 2.9 % | at most 5 % | **fails** |
| purity | 0.6 % | 0.3 % | at most 1.0 % | holds |
| A's draws clean, by band | 79.2 / 74.5 / 60.2 % | 80.8 / 76.0 / 59.7 % | within 2 points | holds |
| real tiles' bright stars flat | 96.4 % | 96.7 % | at least 95.4 % | holds |

**The kill fires** on the nucleus's red, so the cut does not go onto R2t's recipe. Prediction (1) moved toward its bar on
every count and reached none: blue at 1.5-3 px came 0.19 of the 0.30 sigma it needed, at 3-4.5 px 0.06 of 0.40, the dig 3.9
of 6.6 points. The channel relation outside every training master explains part of Andromeda's blue hole, not most of it.
One reading of the nucleus, not measured: taught that one channel may fall off faster than the others, the remover now
accepts a source whose channels do not share one profile, and M31's red-heavy core is one.

**Read, not predicted.** The held-out Tarantula: dark flat 23.7 % (35.8), left 74.5 % (60.6), core +1.67 / +0.96 / +0.74
sigma (+0.89 / +1.06 / +0.61). The drop is near the 13 points a seed moves it, so it says little on one seed. The crowded
Tarantula falls to 62.0 % (69.8), and Sagittarius stays at 94.7 % (95.0). On A's draws, colour-clean rises 4.9 points on
faint stars and 5.7 on mid-bright, the same on both twins (4.8 / 5.7 plain, 4.9 / 6.1 lobed), and the dug bright stars move
from red to green (red 5.0 points fewer, green 9.1 more). That is one seed, kept as a lead only. R2w (#1776) trains next on
the 1070, as pre-registered.

#### R2x: kept cores in the training data, pre-registered (2026-10-24)

Written before it exports. The owner (2026-10-24) asked why the guards are not part of the loss. They already are, for
whatever the training data holds: the loss compares the output with the starless target, which keeps a galaxy's core, so
any core the remover takes costs it. But the synthetic backgrounds hold no bright compact core that is not a star, so the
loss never meets one. Every arm that widened what a star may look like took M31's nucleus with it: R2l, R2m, R2s, R2s2, R2v.
R2l is also the warning against weighting one kind of pixel instead, since its weight on bright stars took 35.9 % of the
nucleus's red. So the case goes into the data (#1658's training half, "Kept cores" above), and the guard stays a guard: it
reads a held-out master, where a loss on it would spend the test.

**The arm.** R2i's recipe (run-r2.ps1, no teacher, no extra cache) on synth12, seed 0, on the 1070 after R2w: synth4's
command with `--kept-cores`, the plate's stretch. One change against R2i seed 0. **Not R2t's recipe**, though R2t is the
base: with `--teacher` every synthetic draw regresses onto the teacher's output instead of its clean target
(`n2n_smoke.py`), so a kept core would be taught as E5 treats it, not as kept. Checked on a smoke export before it runs (one
session, 40 cells, 4 draws): 7 cells hold a core, every injection row equals the plain export's but for `Core`, every
differing tile lies in a core cell, and the plain export's 400 tiles are synth4's byte for byte.

**Predictions** against R2i seed 0's whole-master row:
1. M31's nucleus taken at most 1.0 % in every channel. R2i seed 0 takes 3.2 / 3.1 / 0.5 %; on this scorecard R2i seed 0,
   R2t and R2u put their worst channel at 3.2, 6.1 and 3.5 %, so a run under 1.0 in all three is outside the seeds' spread.
2. No guard lost: purity at most 1.0 %, A's draws within 2 points of 79.2 / 74.5 / 60.2 % clean, the real tiles' bright
   stars at least 95.4 %.

**Read, not predicted:** Andromeda's dig and blue, the Tarantula (a seed moves it 13 points) and its core residual, the
crowded fields. The cores were sized from M31's nucleus (its width and fall-off, never its pixels), so the nucleus is a
friendlier test than a galaxy the generator was not shaped on.
**Kill:** (2) failing.
**Read:**
- (1) holding means the training data was what the nucleus lacked. The cores then go onto R2t's recipe, with a core cell
  regressing onto its clean target as the clusters' rows already do, and onto the master's stretch if R2w holds.
- (1) missing with (2) holding means the cores in the data do not carry to a real nucleus. The next step is then weighting
  the kept cores' pixels in the loss.

#### R2w's read: the master's stretch empties the Tarantula's cores, and the real tiles' bright stars miss (2026-10-24)

R2w stopped on its own at step 154,500 (best at 130,500), 6.9 hours on the 1070. Against its pre-registration, with R2i
seed 0 as the baseline:

| | R2i seed 0 | R2w | bar | |
|---|---|---|---|---|
| Tarantula core residual, R / G / B | +0.89 / +1.06 / +0.61 | **-0.11 / -0.17 / +0.42** | under +0.5 in every channel | holds |
| real tiles' bright stars flat | 96.4 % | 95.6 % | at least 97.0 % | **misses** |
| M31's nucleus taken, R / G / B | 3.2 / 3.1 / 0.5 % | 1.6 / 2.6 / 4.4 % | at most 5 % | holds |
| purity | 0.6 % | 0.8 % | at most 1.0 % | holds |

**No kill.** Prediction (1) holds by a wide margin: trained in the stretch it runs in, the remover leaves no light in the
held-out Tarantula's sharp cores, so #1776's skew is what kept it there. Prediction (2) misses: on the real tiles the bright
stars fall to 95.6 %, 0.8 below R2i seed 0 and the lowest of the comparable runs (R2t 97.4, R2u 97.6, R2v 96.7). The
pre-registered rule makes the master's stretch every export's default only when both hold, so it does not become the default
on this read. That stays the owner's call.

**Read, not predicted**, and the reason the call is worth making:

| | R2i seed 0 | R2w | R2t |
|---|---|---|---|
| Andromeda dark flat / dug | 32.8 / 36.6 % | **48.7 / 28.8 %** | 40.6 / 41.7 % |
| Andromeda blue, 1.5-3 / 3-4.5 px | -1.10 / -0.90 | **-0.89 / -0.74** | -1.23 / -1.00 |
| Tarantula dark flat / left | 35.8 / 60.6 % | **56.5 / 31.5 %** | 57.2 / 40.3 % |
| crowded Tarantula / its BP 17-19 stars | 69.8 / 90.3 % | 77.8 / **92.6 %** | 81.3 / 85.4 % |
| Sagittarius crowded | 95.0 % | 94.2 % | 95.2 % |

Against R2i seed 0 every whole-master figure but Sagittarius moves the right way, past what a seed moves it (the Tarantula up
to 13 points, here 29 on what is left; Andromeda 1 to 2, here 7.8 on the dig). Andromeda's blue and dig come closer than any
model of ours so far, and the dig meets the 30 % bar R2v set and missed. The price in the guards is the nucleus's blue, 0.5 to
4.4 %, still inside 5. A's draws and R2k's twins were exported with the plate's stretch, outside
R2w's domain, and fall there as expected: 76.9 / 71.5 / 51.7 % clean on A's draws (79.2 / 74.5 / 60.2). One seed. R2x
(kept cores, above) keeps the plate's stretch as pre-registered, so it still isolates the cores.

**The owner's call (2026-10-24): the master's stretch is the default, "if it truly helps".** What argued against it, the real
tiles' 0.8-point dip, was read by session first (the real-star check split per validation session, bright stars 100 to
1,000 sigma, % flat):

| session | stars | R2i seed 0 | R2t | R2u | R2v | R2w |
|---|---|---|---|---|---|---|
| Lobster (three views) | 933 / 622 / 791 | 98.9 / 99.5 / 99.9 | 98.7 / 99.8 / 99.9 | 98.8 / 99.8 / 99.9 | 98.6 / 99.7 / 99.5 | 98.0 / 99.4 / 99.7 |
| M42 (ASI294MC) | 248 | 80.6 | 83.5 | 86.3 | 86.3 | **71.4** |
| HIP 80609 (three views) | 639 / 441 / 444 | 94.8 / 96.4 / 98.2 | 95.6 / 96.8 / 98.6 | 95.9 / 96.8 / 98.0 | 95.1 / 95.2 / 95.7 | 94.5 / 96.1 / 96.2 |
| SMC | 97 | 67.0 | 93.8 | 93.8 | 85.6 | **79.4** |
| all | 4,215 | 96.4 | 97.4 | 97.6 | 96.7 | 95.6 |

M42 alone is two thirds of the dip: on its bright stars over bright nebula R2w leaves light at 27.0 % of them (R2i seed 0
16.5 %). Without M42, R2w stands at 97.1 % against R2i seed 0's 97.4 %, and the SMC gains 12 points. Against the whole
masters' gains that is a net help, so `--stretch-from` now defaults to `master` (`DatasetDegradationExporter.Options
.StretchFromMaster` true); `--stretch-from plate` reproduces an export before synth11. **M42's bright stars over nebula are
the thing to watch** in the next arms on the master's stretch.

#### R2x's read: M31's nucleus kept whole, and killed by the bright stars it digs on A's draws (2026-10-24)

R2x stopped on its own at step 215,000 (best at 191,000), 9.4 hours on the 1070. Against its pre-registration, with R2i
seed 0 as the baseline:

| | R2i seed 0 | R2x | bar | |
|---|---|---|---|---|
| M31's nucleus taken, R / G / B | 3.2 / 3.1 / 0.5 % | **-0.1 / -0.1 / -0.1 %** | at most 1.0 % in every channel | holds |
| purity | 0.6 % | 0.6 % | at most 1.0 % | holds |
| A's draws clean, 5-20 / 20-100 / 100-1000 sigma | 79.2 / 74.5 / 60.2 % | 80.5 / 74.7 / **53.8 %** | within 2 points | **fails** |
| real tiles' bright stars flat | 96.4 % | 96.9 % | at least 95.4 % | holds |

**The kill fires** on A's bright stars. Prediction (1) holds as far as it can: R2x takes nothing from the nucleus, the first
model of ours to do so (R2i seed 0, R2t and R2u put their worst channel at 3.2, 6.1 and 3.5 %). The 2-point bar was tighter
than the seeds' own spread on the bright band (R2i seed 1 stands at 55.9 %), but what moved is the dig, and that is outside
the spread: 39.0 % of A's bright stars dug, against 28.7 and 32.3 % for R2i's two seeds, and the same on both twins (40.3 %
plain, 41.5 % lobed). The holes are in the sky, not over structure (31.1 % of the bright stars dug in sky, against 21.7 and
24.3 %), deeper at the core (core mean -0.58 sigma, against -0.20 and -0.28), and worst on the Lobster's three views (dug
34.9 / 22.6 / 38.4 %, where neither seed passes 22.9 / 20.7 / 24.0). HIP 80609 sits where R2i seed 1 does. The real tiles
show a trace of the same: 2.1 % of their bright stars dug, against 1.5 % for R2i seed 0 and 1.2 % for R2t. Why kept cores
teach a deeper cut at a bright star was not measured.

**Read, not predicted**, one seed:

| | R2i seed 0 | R2x | R2w | R2t |
|---|---|---|---|---|
| Andromeda dark flat / dug | 32.8 / 36.6 % | 43.7 / 33.1 % | 48.7 / 28.8 % | 40.6 / 41.7 % |
| Andromeda blue, 1.5-3 / 3-4.5 px | -1.10 / -0.90 | **-0.75** / -0.86 | -0.89 / -0.74 | -1.23 / -1.00 |
| Tarantula dark flat / left | 35.8 / 60.6 % | 46.9 / 51.9 % | 56.5 / 31.5 % | 57.2 / 40.3 % |
| Tarantula core residual, R / G / B | +0.89 / +1.06 / +0.61 | +0.76 / +0.78 / +0.45 | -0.11 / -0.17 / +0.42 | +0.84 / +0.38 / +0.90 |
| crowded Tarantula / its BP 17-19 stars | 69.8 / 90.3 % | 69.6 / **78.7 %** | 77.8 / 92.6 % | 81.3 / 85.4 % |
| Sagittarius crowded | 95.0 % | 94.7 % | 94.2 % | 95.2 % |
| A's draws clean in colour, 5-20 / 20-100 / 100-1000 sigma | 57.3 / 43.7 / 21.2 % | 64.3 / 52.2 / 23.4 % | 56.6 / 41.7 / 15.5 % | 60.4 / 50.4 / 33.0 % |

Andromeda's blue at 1.5-3 px is the first of ours above the -0.8 that R2v set and missed, and colour-clean on A's draws rises
7.0 points on faint stars and 8.5 on mid-bright, the most of any arm. The crowded Tarantula's faint Gaia stars fall 11.6
points. **By the pre-registration the cores do not go onto R2t's recipe.** In use, M31's nucleus does not depend on them:
inference keeps a core whatever the remover does (`--protect-cores`, #1658's inference half). The next lever is the owner's.

#### Round 3: closing on the judges, flown as pilot flights (draft, 2026-10-25)

The owner, 2026-10-25: "lets first create a plan on how we improve the stellar removal training, including connecting
flights". Rounds 1 and 2 were run by one long session with scheduled-task drivers; from round 3 each piece of work is a
pilot flight (one issue, one plane cut from main, legs, a reviewed PR), and the owner creates, connects and merges them.

##### Where round 2 left us

On whole masters, at one view (rows from "Round 2" and "R2w's read" above):

| | ours, best | which | SXT | StarNet2 |
|---|---|---|---|---|
| Andromeda bright dark-sky stars flat / dug | 48.7 / 28.8 % | R2w | 98.6 / 1.4 % | 48.1 / 0.4 % |
| Andromeda blue at 1.5-3 px | -0.75 sigma | R2x (killed) | -0.40 | -0.22 |
| Tarantula bright dark-sky stars flat / left | 57.2 / 40.3 % | R2t | 96.6 / 2.7 % | 92.3 / 7.4 % |
| held-out Tarantula crowded stars flat | 81.3 % | R2t | 96.1 % | 96.5 % |
| purity (light taken where no star is) | 0.5 % | R2t | 3.1 % | 1.7 % |

StarNet2 is no closer than ours on Andromeda's flat count, but it leaves light where ours dig: 0.4 % dug against 28.8. A
star left faint is a cosmetic miss; a hole is the one forbidden artefact (the goal, top of this plan).

Three things are settled and carried: R2t's recipe is the base (four levels, 6.86 M weights, cosine, `--clip-grad 8`, the
clusters at 12.5 %); every export takes the master's stretch (#1776); M31's nucleus is kept at inference
(`--protect-cores`), not by the training data (R2x). The gap left is the hole under bright stars: Andromeda's dig stands
27.4 points above SXT's, three and a half times the most any round-2 arm moved it (7.8, R2w).

##### What limits us, ranked

1. **Nothing in the objective punishes a dug core more than a left one.** Bright cores carry about 2.5 % of the squared
   error, and the hole is a one-sided failure. The speckle teacher (#1362) is the lever built for it and was never tried.
2. **The teacher is out of domain.** E5's members trained on the plate's stretch; on master-stretched inputs R2t's
   recipe would regress onto answers to a different stretch. Retrain or drop it, never carry it silently.
3. **The evaluation sets were out of domain too.** A's draws and R2k's twins were exported with the plate's stretch, so
   R2w and every later arm fell there by construction ("R2w's read"). F2 re-exported them (#1827, "F2's read" below),
   and they are fair reads on those paths.
4. **The training stars are still the easiest thing to tell from real** (the synthetics review: star crops AUC 0.76 to
   0.79): bright stars carry about half the real 3-5 px light, rising with brightness and differing by rig (S1, #1241),
   and the noise's shared part has no neighbourhood (S4b, #1781) and runs 1.11 to 1.17 times real on the QHY294PROC
   (#1786).
5. **One seed is not a read on bright stars.** R2i's two seeds stand 4 points apart on A's bright band and R2u moves the
   Tarantula up to 13 points; R2x's kill bar was tighter than the seeds' own spread. Bright-band bars come from two seeds.
6. **Capacity** last: R2t gained by growing to 6.86 M weights and SXT's file holds about 16 M, but a bigger net on
   unrealistic stars learns the unrealistic stars better.

##### The flights

Infrastructure and data changes are code flights; each training arm is a flight of its own, three legs, written below.
Every flight is filed as an issue in the `star-remover-training` milestone and links its heading here.

| flight | issue | what lands | done when | connects to |
|---|---|---|---|---|
| F1 run tooling | #1774, #1787 folded in | the arm chain runnable as pilot jobs: `training/star-removal/arm-step.ps1 -Step export`, `train` (`-Budget` 7h30m, `-CheckpointEvery`, exit 4 "resumable: run the train step again", run it twice when needed), `onnx`, `score`, `whole`; the driver's stop file on any exception | a dry arm over R2u's outputs runs every step through `pilot run` and exits 0 with no GPU; that dry run on DE1 is the bench check, `test-arm-steps.ps1` the stub one | none |
| F2 fair reads | #1827 | A's draws and both twins re-exported on the master's stretch; R2i seed 0 and 1, R2t, R2w re-scored on them; bright-band bars stated from two seeds; M42 read per session in the scorecard | the four baselines' rows on the new sets in the plan | F1 |
| F3 the dig penalty | #1362 | the speckle teacher as a trainer option, pinned to `StarlessSpeckles` by a parity fixture. **Landed:** the port and fixture, then `--speckle-weight` / `--speckle-sigma` in `n2n_smoke.py` (off by default, in the fingerprint only when set, in the training and held-out objectives, logged), `-SpeckleWeight` / `-SpeckleSigma` in `run-r2.ps1` and `arm-driver.ps1`; see "The speckle teacher" | the fixture passes on CPU; the option off is byte-identical training | none |
| F4 the wings by brightness | #1241 | S1b: each rig's wing gain with a brightness term, drawn per channel inside the real channel relation (R2s's lesson); the classifier's spread over seeds read first | star-crop AUC moves past the classifier's own spread, and no drawn star outside every real master's relation | none |
| F5 the noise | #1781, #1786 folded in | S4b's shared component with real's cross-structure at lags 0 to 2, and the QHY294PROC's noise anchor recalibrated | noise-crop AUC below synth4's 0.70 and Lobster's sigma ratio within 1.05 (**not met**, 2026-10-26: Lobster 1.16, and the AUC's fall is one rig's; "S4b: the read") | none |
| F6 the clusters' stretch | #1823 | the synthetic cluster tiles stretched with the master's parameters, as synth11's are (they took the plate's, 1.32 to 1.53 times gentler at the sky), and re-exported | the 90 skies re-exported and packed into a cache beside synth11's | none |
| R3a | #1828 | R2t's recipe on a master-stretched export, no teacher, seeds 0 and 1, both on the 1070 | both rows on whole masters, real tiles and F2's sets. **Read 2026-10-27: killed as R3b's base** (A's draws clean 51.4 / 56.1 against R2t's 59.2, the nucleus guard failed by both seeds, the seeds 4.7 apart); see "R3a's read" | F2, F6 |
| R3b | #1829 | R2w's recipe on the master's stretch plus the dig penalty on the synthetic tiles (R3a is not its base, "R3a's read"), `-SpeckleWeight 10000`, seed 0 first; **pre-registered 2026-10-27**, "R3b's pre-registration" | its read: A's draws dug at 100-1000 sigma at most 29.2, R2w's whole-master rows kept. **Read 2026-10-28: gate missed and killed as R3b2's base by both seeds** (A's draws dug 32.7 / 35.3, clean 48.5 / 50.1, the lobed twin under 49.1 for both; seed 0 also fails the nucleus and M42 guards, seed 1 the nucleus's blue); see "R3b's read" | R3a, F3 |
| R3b2 | filed after R3b's read | the dig penalty on real master tiles too, beside the synthetic loss, which must stay or the net learns to remove nothing | its read, timidity checked: real stars within a sigma at 100 to 1000 sigma no worse than R3b's | R3b |
| R3c | filed after R3b's read | the winner plus a teacher rebuilt from R3a's two seeds (E7, 8 views) | its read | R3b |
| R3d | filed after F4 and F5's gates | the winner on the export with whatever F4 and F5 adopted | its read | R3c |
| R3e | only if R3d plateaus | a larger net on a T4 | its read | R3d |
| R3f | #1867 | R2w's recipe on synth13: synth11's export from a binary with #1867's injector guard, so no diverged catalogued star is drawn (the plates unchanged); **pre-registered 2026-10-30**, "R3f"; **KILLED 2026-10-31** (flat 92.9, dug 3.0, M42 45.2) | its read on F2's scorecard beside both R2w seeds | R2w seed 1 |

**Why R3a has no teacher.** It isolates what R2t's recipe does on the master's stretch against R2w (R2i's recipe on the
same stretch), and its two seeds are then both the bright-band spread and E7's members, so the teacher question costs no
extra training.

**Which connections are made ahead.** F2 to F1, R3a to F2 and F6, and R3b to R3a and F3 run whatever the earlier read says, so
they are created and connected at once and each lands into the next. R3c onwards depend on a verdict, so the read that
precedes each names its candidates as issue drafts and the owner files the one picked. F3, F4 and F5 have no
connection: they fly beside the arms, and their checks stay off the GPU (jobs queue one at a time on the node).

##### An arm as a flight

Each leg's step is `training/star-removal/arm-step.ps1 -Step <s> <common>`, `<common>` being the arm's `-Tag -Name -TrainDir
-Cache -Config -Runs` (`training/README.md`, "Scoring an arm's weights, and driving a local arm", has the five lines in full).
Every line carries `--timeout 8h` (pilot run's default is 2 h); a job killed at its timeout is resumed by running the same line again.

- **Leg 1, export.** `dataset degrade` from the frozen recipe, on C: or D: (never E:), as
  `pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step export <common> -ExportScript <export.ps1> -ExportDone <done file>`.
  Check: the export's manifest exists with the expected tile counts, and `FROZEN-AT.txt` names the commit.
- **Leg 2, train.** The worker starts
  `pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step train <common> -TrainScript training/denoise/run-r2.ps1 -TrainArgs <switches>`
  and ends its turn; `<switches>` make run-r2.ps1 train exactly `<Name>` in `<TrainDir>` (`-Tag`, ONE `-Arms`, `-FirstSeed`,
  `-SeedCount 1`), its defaults being nine models. The step stops the trainer itself at its `-Budget` (7h30m) with the resume checkpoint whole, says
  `resumable: run the train step again` and exits 4; the worker then runs **the same line again** as a second job, which
  resumes. A modest token budget, since the worker sleeps while the job runs. Then
  `pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 -Step onnx <common>` writes `<Name>.onnx` and its
  contract from the weights. Check: the best checkpoint and the ONNX with its contract exist. A seed on an Azure T4 is a job
  that drives `launch-arm.ps1` and polls, under the same cap, with eviction answered by the trainer's resume; it needs a
  new SAS first.
- **Leg 3, read.** `arm-step.ps1 -Step score <common>` (`score-arm.ps1`, reusing the ONNX) and `-Step whole <common>`
  (`score_whole.py`), each its own `pilot run --timeout 8h -- pwsh -NoProfile -File training/star-removal/arm-step.ps1 ...`, and F2's sets; the pre-registration's predictions checked and the
  read written into this plan under the arm's own heading, with the candidate next arms as issue drafts. Check: the row
  file exists and parses. A bad score never fails the leg: a negative result lands like any other.
- **The PR carries the read.** The reviewer reads it; the owner reads it on the merge ring, merges, and picks the next
  arm. Every arm flies on DE1, the desktop with the 1070.

##### Rules carried into round 3

- Pre-register before the first training step, a few lines, its kill rule included.
- Bright-band bars from both of R2i's seeds, or the arm's own two; a difference under the seeds' spread is a seed's.
- The whole masters and the real tiles decide. A's draws and the lobed twin are fair reads too, **on F2's master-stretched
  sets** (`C:/temp/tianwen-scratch/f2-random-master` and `f2-twin-lobed-master`, 7 sessions, recorded in
  `training/star-removal/f2-sets.json`), against the four baselines' rows in "F2's read", never against a row on the
  plate-stretched sets. The plain twin is A's draws again (same recipe, seed and shape), so it is no second read.
- Bright-band bars on F2's sets come from R2i's two seeds there: 2.6 points clean at 100-1000 sigma on A's draws, 3.1 on the
  lobed twin, and 3.2 / 3.9 dug; M42's real stars 6.4 points flat, the SMC's 24.8 (97 stars, no bar at all).
- Guards: purity at most 1.0 %; M31's nucleus read without `--protect-cores` at most 5 % in every channel, since a
  remover that takes a core takes other bright compact structure too.
- M42's bright stars over nebula read per session on every master-stretched arm (two thirds of R2w's real-tile dip).

##### Housekeeping before R3a

- Azure: `r2-synth4`, `r2-synth6` and `r2-synth7` serve no round-3 arm, since every new export takes the master's
  stretch; the owner deletes them (134 GB over two accounts).
- R3a trains both seeds on the 1070 (the owner, 2026-10-25), so nothing is uploaded. Its inputs are local: synth11's
  export (`C:/temp/tianwen-scratch/r2-train-synth11`, kept; its cache, deleted after R2w, is rebuilt from it by the prepare
  step) and F6's clusters (`C:/temp/tianwen-scratch/r3-clusters-master`, re-exported 2026-10-25).
- The four-level net has never trained on the 1070 (R2nc, R2t and R2u ran on T4s), so its time there is measured on R3a's
  first job, not assumed.
- The 1070 and the Azure group are idle; no scheduled task is left (all retired 2026-10-24).

##### R3a's pre-registration (2026-10-26)

**When this was written.** After seed 0 trained and before any R3a score was read: no scorer, no whole-master run and no GPU
test has touched either R3a checkpoint. Seed 1 is still training on the 1070 (step 24,400 of 224,500 at 11:55 on 2026-10-26,
due about 23:15); this section is fixed before it finishes, so its read cannot move the bars.

**Inputs, as checked (read-only, 2026-10-26).**

| input | found |
|---|---|
| synth11's export | `C:/temp/tianwen-scratch/r2-train-synth11/export.done` exists |
| synth11's cache | `r2-synth11/meta.json` and `prepare.done`: 4 injected draws, 4,520 train and 320 held-out cells, 11 slots, 3 channels |
| F6's clusters (#1823) | `r3-clusters-master-cache`: 2,592 train and 288 held-out cells, 1 injected draw, master-stretched |
| the trainer | `C:/temp/e2/r3a-run/FROZEN-AT.txt` names the commit "build: the camera SDKs on TianWen.DAL 4.0, whose offset control is BlackLevel" |
| seed 0's weights | `r2-synth11/r3a_synth11_s0.pt` exists |

**The recipe, from the driver's command line and the head of seed 0's log.** R2p's base line (`--synthetic --loss l2 --upsample
--cond-map --band-loss 3 --band-scales "2,4 4,8" --base 32 --val-every 500 --patience 24 --max-decays 4 --min-improve 0.001
--gate-every 0`), `--levels 4 --max-ch 256 --clip-grad 8 --schedule cosine --steps 224500 --seed 0`, `--extra-cache` the
clusters (the log: "extra cache ON ... 12.5% of the training draw"), `--checkpoint-every 2000 --resume`, no teacher. The log
prints "levels=4 (channels capped at 256), 6.86 M params, 4520 train cells" and "gradient clipping ON". It matches the brief.

**Seed 0 on the 1070, measured:** 224,500 steps in 698.1 min (11.6 h) at 42.9 tiles/s (41.7 at the first 200 steps), loss
0.00000 at the end, no error output. That is the four-level net's time on this card; R2nc, R2t and R2u ran on T4s.

**Seed 1 on the 1070, measured (a fact added once it finished, 2026-10-26; the predictions and bars above are unchanged):**
the same recipe with `--seed 1` (the head of its log matches seed 0's line for line), 224,500 steps in 725.3 min (12.1 h) at
41.3 tiles/s, finished and saved at 23:43 on 2026-10-26, loss 0.00000 at the end, an empty error log. The cosine schedule runs to
the last step, so the checkpoint is the final one and no best step is recorded. Both seeds are exported to ONNX with contracts
under `f2-logs-master/score-r3a_synth11_s{0,1}/`.

**What is compared.** R3a seed 0 and seed 1 against R2t and R2w (the two arms on the same recipe or the same stretch), on:
the three whole masters (Andromeda, the Tarantula and the held-out crowded field), the real tiles with M42 per session, and
F2's sets (A's draws and the lobed twin, `f2-sets.json`, 7 sessions) against the four baselines' rows in "F2's read". R2t is
the one to beat there: A's draws 59.2 clean and 32.4 dug at 100-1000 sigma (56.7 / 24.2 saturated), the lobed twin 58.1 / 34.0,
M42 83.5 % flat.

**Predictions.** R3a is R2t's recipe on the stretch inference runs in, so it is R2t's net with the domain skew removed.

1. **A's draws, clean at 100-1000 sigma:** each seed at least 59.2 minus 2.6, and the two seeds' mean at or above R2t's 59.2.
   R2t barely moved with the stretch in colour (+0.8), so no large jump is expected; a gain would come from the dug side.
2. **A's draws, dug at 100-1000 sigma:** the mean no worse than 32.4, with a hope of 29 or less. R2w's master-stretched data
   cut its dig against the plate-stretched R2i, and R3a has R2t's capacity on top of it.
3. **Lobed twin:** the same signs, clean at least 55.0 and dug at most 37.9 per seed (R2t's rows less and plus the 3.1 and
   3.9 bars).
4. **Whole masters:** Andromeda's dug count (28.8 % for R2w, the best round-2 row) moves toward R2w's and no worse than
   R2t's; the Tarantula's left count stays at R2t's 40.3 % or below. Neither is expected to close on SXT, whose gap is the dig
   penalty's (R3b).
5. **M42:** flat within 6.4 points of R2t's 83.5 per seed, so at least 77.1 %.
6. **Seeds:** a bright-band difference between the two seeds under 2.6 clean and 3.2 dug on A's draws is a seed's, as is
   every other gap under F2's bars.

**Guards, either seed failing one is a fail.** Purity (light taken where no star is) at most 1.0 %. M31's nucleus read
without `--protect-cores` at most 5 % in every channel. M42 read per session, no session more than 6.4 points under R2t's.

**Kill rule.** R3a is killed as a base for R3b if, on A's draws at 100-1000 sigma, EITHER seed is under 56.6 clean (R2t less
the bar of 2.6) AND the lobed twin agrees (under 55.0); or if either seed fails a guard; or if both seeds' dug at 100-1000
sigma on A's draws is above R2t's 32.4 by more than 3.2. A kill on one set alone, or a spread between the seeds past the bars,
is reported and the arm kept: R3b would then start from R2t's recipe on the master's stretch, which R2w and F2 already say is
the right stretch. A pass needs no win over R2t, only no loss past the bars and clean guards, since R3a's job is the
two-seed baseline (and E7's members) for R3b and R3c. Seeds spread over the bars are read as the arm's own bars from then on.

##### Deferred, and why

The learned generator (#1740) waits on F4 and F5: if a component stays above about 0.6 AUC after them, it comes next. The
halo option (#1659), the scenes (#1363, the bright star in nebula first if M42 keeps failing), the background structure
(#1338, #1311) and the spikes (section 8) stay where they are.

#### F2's read: the fair sets move every plate-stretched arm down, and R2w's fall was the stretch (#1827, 2026-10-26)

F2 (#1827) re-exported A's draws and both of R2k's twins with the master's stretch (`--stretch-from master`, #1776) and scored
R2i seeds 0 and 1, R2t and R2w on them again, the same binary and scorecard for all four
(`training/star-removal/f2-scorecard.master-stretch.json`, `score-arm.ps1 -Skip ring,nucleus`; `f2_read.py` tabulates the two
stretches side by side). The sets are recorded in `training/star-removal/f2-sets.json`. Two things about them first:

- **They hold 7 of the 8 sessions.** The Orion night (`Great-Orion-Nebula`, 2021-11-28) cannot be exported again: its master
  was baked again after its plate was made. Every row below, old and new, is therefore pooled over the same 7 sessions, so the
  old figures differ from the 8-session ones above (R2i seed 0's 100-1000 sigma is 61.2 here, 60.2 there). The loss share and
  far sky are the report's own pool, which has no per-session breakdown, so their old figures are over 8.
- **The plain twin is A's draws.** Their `dataset degrade` arguments are identical (stars, warped, seed 1, 4 draws, 40 cells),
  and the new reads agree on every figure of every model. By the old numbers R2k's plain twin was the same draw over the same
  7 sessions (R2i seed 0 at 100-1000 sigma: 61.2 on the plain twin, 61.2 on A's 7-session pool). So F2's sets give two reads,
  A's draws and the lobed twin, not three.

The real tiles are unchanged by F2: re-reading them reproduced R2i seed 0's 96.4 % within a sigma and 1.47 % dug to the digit.

On A's draws, plate-stretched / master-stretched (7 sessions):

| | R2i seed 0 | R2i seed 1 | R2t | R2w | seeds' spread (new) |
|---|---|---|---|---|---|
| clean 5-20 sigma | 79.6 / 77.4 | 80.7 / 78.4 | 80.5 / 78.8 | 77.1 / 76.1 | 1.0 |
| clean 20-100 sigma | 75.1 / 70.6 | 75.3 / 71.5 | 78.1 / 73.5 | 71.5 / 69.9 | 0.9 |
| clean 100-1000 sigma | 61.2 / 53.5 | 57.5 / 50.9 | **67.5 / 59.2** | 52.7 / 53.4 | **2.6** |
| dug 5-20 sigma | 7.7 / 10.6 | 8.2 / 9.4 | 6.6 / 9.0 | 8.2 / 9.5 | 1.3 |
| dug 20-100 sigma | 15.4 / 21.0 | 16.6 / 18.9 | 13.7 / 18.1 | 17.8 / 17.4 | 2.1 |
| dug 100-1000 sigma | 29.6 / 36.7 | 32.4 / 40.0 | 25.1 / 32.4 | 30.0 / 32.4 | **3.2** |
| clean saturated | 52.1 / 49.5 | 52.6 / 52.1 | 62.9 / 56.7 | 45.4 / 54.1 | 2.6 |
| dug saturated | 23.7 / 26.3 | 29.9 / 29.4 | 19.1 / 24.2 | 25.3 / 17.5 | 3.1 |
| clean in colour 5-20 sigma | 57.5 / 67.7 | 56.9 / 66.2 | 60.6 / 67.7 | 56.8 / 68.2 | 1.5 |
| clean in colour 20-100 sigma | 44.1 / 55.2 | 43.4 / 53.9 | 50.7 / 56.6 | 41.7 / 55.1 | 1.4 |
| clean in colour 100-1000 sigma | 22.5 / 30.2 | 20.0 / 27.4 | 35.3 / 36.1 | 16.1 / 30.4 | 2.8 |
| clean in colour saturated | 22.2 / 30.9 | 23.2 / 32.5 | 34.5 / 34.0 | 20.6 / 34.5 | 1.6 |
| dug in colour 100-1000 sigma | 63.6 / 54.3 | 66.2 / 58.4 | 52.5 / 48.7 | 71.2 / 48.5 | 4.1 |
| core mean at 100-1000 sigma | -0.26 / -0.34 | -0.35 / -0.49 | -0.28 / -0.32 | +0.13 / -0.02 | 0.14 |
| core rms at 100-1000 sigma | 1.62 / 1.41 | 1.64 / 1.31 | 1.54 / 1.26 | 1.93 / 1.75 | 0.10 |
| bright cores' share of the loss, % | 1.27 / 0.69 | 1.37 / 0.72 | 1.15 / 0.66 | 1.35 / 0.70 | 0.03 |
| far sky | 0.19 / 0.25 | 0.20 / 0.26 | 0.19 / 0.23 | 0.22 / 0.26 | 0.01 |

On the lobed twin the pattern is the same (clean at 100-1000 sigma, plate / master: R2i seed 0 61.0 / 52.3, seed 1
56.6 / 49.3, R2t 67.1 / 58.1, R2w 51.6 / 52.2; dug 30.0 / 37.9, 33.8 / 41.8, 25.9 / 34.0, 31.4 / 33.2). Its seeds' spread is
3.1 clean and 3.9 dug at 100-1000 sigma, 1.1 and 1.6 clean at 5-20 and 20-100 sigma, and 0.6 on clean saturated.

**The bright-band bars.** R2i's two seeds stand 2.6 points apart on A's draws at 100-1000 sigma clean (3.1 on the lobed
twin), 3.2 apart dug (3.9), and 1.0 and 0.9 clean on the faint bands (1.1 and 1.6). The plate-stretched pair stood 4.3 apart
on the bright band over 8 sessions (3.7 over these 7), so the master-stretched set is a quieter yardstick. From here a
bright-band clean difference is a model's only past **2.6 on A's draws and 3.1 on the lobed twin**, a dug difference past
**3.2 and 3.9**, a faint one past 1 to 1.6. Each is one pair of seeds, as the earlier bars were.

**R2w's fall on A's draws was the stretch, wholly.** On the plate's stretch R2w stood 8.5 points under R2i seed 0 at
100-1000 sigma (52.7 against 61.2). On the master's it is 53.4 against 53.5: level with seed 0, 2.5 above seed 1, inside
the bar. What it did not do is win: its clean rates sit level or just under R2i's on every band (5-20 sigma 76.1 against
77.4 and 78.4, past the bar of 1.0; 20-100 sigma 69.9 against 70.6 and 71.5, inside it), so training in the stretch it runs
in bought it parity on A's draws, not a lead. Where it does lead is the dig: 32.4 % at 100-1000 sigma against 36.7 and 40.0
(past the bar of 3.2), saturated stars dug 17.5 % against 26.3 and 29.4 (past 3.1), and in colour 48.5 % against 54.3 and
58.4. The master's stretch gave R2w less dug and not more clean, which is the whole-master pattern of "R2w's read".

**Everything else falls on the new sets, and R2t still leads.** The three plate-stretched arms lose 6.6 to 8.2 points clean
at 100-1000 sigma and 1.7 to 4.5 on the faint bands when read in the stretch they now run in at inference, and their dug at
100-1000 sigma rises 7.1 to 7.5 points: the plate-stretched A flattered them, and the master-stretched set is the harder
one for bright stars. The order at 100-1000 sigma is R2t 59.2, then R2i seed 0 53.5 and R2w 53.4, then seed 1 50.9. R2t's
lead over R2w is 5.8 points clean (bar 2.6) with the dig equal (32.4 against 32.4), and over R2i seed 0 it is 5.7 clean and
4.4 less dug, both past their bars. The recipe's gain (four levels, the clusters) is therefore real on a fair set: 6.2
points over seed 0 on the plate-stretched A, 5.7 now.

**Read, not predicted.** (1) R2t's colour lead mostly disappears: clean in colour at 100-1000 sigma it was 12.8 points over
R2i seed 0 (35.3 against 22.5) and is 5.9 now (36.1 against 30.2), because R2t barely moves with the stretch (+0.8) and the
three others rise 7 to 14 (R2w 16.1 to 30.4). (2) The cores' error shrinks for all four (rms 1.3 to 1.8 against 1.5 to 1.9),
and R2w's core mean goes from +0.13 to -0.02, the closest to zero of the four. (3) The bright cores' share of the squared
error falls from 1.1 to 1.4 % to 0.7 % for every model, so it belongs to the set's stretch and not to a model. (4) The far sky
reads 0.23 to 0.26 against 0.19 to 0.22, for all of them alike.

**M42 per session on the real tiles** (the tiles F2 leaves alone; the scorecard's per-session real-star read for all four,
bright stars 100-1000 sigma, `real-star-read-*.txt` in `f2-reports-master`):

| session | stars | R2i seed 0 | R2i seed 1 | R2t | R2w | seeds' spread |
|---|---|---|---|---|---|---|
| Lobster (three views), flat | 933 / 622 / 791 | 98.9 / 99.5 / 99.9 | 98.2 / 99.7 / 100.0 | 98.7 / 99.8 / 99.9 | 98.0 / 99.4 / 99.7 | 0.7 / 0.2 / 0.1 |
| **M42 (ASI294MC), flat** | 248 | 80.6 | 74.2 | **83.5** | **71.4** | **6.4** |
| M42, light left >+1 / dug <-1 | | 16.5 / 2.8 | 23.4 / 2.4 | 14.5 / 2.0 | 27.0 / 1.6 | 6.9 / 0.4 |
| HIP 80609 (three views), flat | 639 / 441 / 444 | 94.8 / 96.4 / 98.2 | 88.6 / 95.7 / 97.3 | 95.6 / 96.8 / 98.6 | 94.5 / 96.1 / 96.2 | 6.2 / 0.7 / 0.9 |
| SMC, flat | 97 | 67.0 | 91.8 | 93.8 | 79.4 | 24.8 |
| all: flat / light left / dug | 4,215 | 96.4 / 2.1 / 1.5 | 95.4 / 1.9 / 2.8 | 97.4 / 1.4 / 1.2 | 95.6 / 2.6 / 1.8 | 1.0 / 0.3 / 1.3 |

The faint bands, pooled, within a sigma (R2i seed 0, seed 1, R2t, R2w): 5-20 sigma 94.8 / 96.4 / 95.4 / 94.3 %, 20-100 sigma
97.1 / 98.3 / 98.7 / 97.5 %.

**M42 stays the watch, and the SMC's gain was a seed's.** R2w's 71.4 % is the lowest of the four: 9.2 under seed 0, 2.8
under seed 1, and 6.0 under the two seeds' mean, against a spread of 6.4, so on M42 R2w sits at the edge of the bar and
under both seeds; it leaves light on 27.0 % of M42's bright stars against 16.5 and 23.4. R2t is the best (83.5, 14.5 % left).
Without M42 R2w stands at 97.1 % flat against 97.4 for seed 0, as written before. **The SMC row argues neither way**: the
seeds stand 24.8 points apart on its 97 stars (67.0 against 91.8), so the "SMC gains 12 points" that supported the owner's
call of 2026-10-24 for the master's stretch is inside what a seed does, and R2w is below seed 1 there. F2 removes that half
of the call's support from the real tiles. The other half, the whole masters (Andromeda's dig, the Tarantula's left light), it does not touch, and
the call stays the owner's. HIP 80609's first view moves 6.2 points between seeds as well (88.6 against 94.8).

**What surprised.** (1) That the plain twin is A's draws, so one read had been two in the plan. (2) How far every
plate-stretched arm falls on the fair set: a 7 to 8 point bright-band drop that no training run caused. (3) That R2w, trained
for this stretch, only reaches parity with R2i seed 0 on A's clean rates and wins on the dig alone. (4) That R2t's colour
reads barely change with the stretch.

**What it decides.** Any arm trained on the master's stretch is now read on these sets against the four baselines above:
the bright band against 2.6 (A's draws) and 3.1 (lobed twin) clean and 3.2 and 3.9 dug, M42 against 6.4. R3a's
pre-registration takes its A's-draws bars from here, not from the plate-stretched rows of "R2i's seed 1 read". R2t is the one
to beat on A's draws (59.2 clean at 100-1000 sigma, 32.4 dug, 56.7 / 24.2 saturated), on the lobed twin (58.1 / 34.0) and on
M42 (83.5 % flat).

#### R3a's read (#1828, 2026-10-27)

R3a (#1828) is R2t's recipe trained on the master's stretch on the 1070, two seeds, no teacher. Both seeds were scored by the
same binary and scorecard as F2's four baselines (`f2-scorecard.master-stretch.json`; `score-arm.ps1` with the nucleus, the
ring read as well; `score_whole.py` for the whole masters), on F2's sets (#1827), the real tiles and the three whole masters.
**Both seeds fail the pre-registered kill rule, and the two seeds stand further apart than any pair so far.** R3a is not a base
for R3b (the dig penalty, #1362 for the trainer option) as it stands.

**A's draws, master-stretched (7 sessions), at 100-1000 sigma unless stated.** The tool is `f2_read.py`; the baselines' columns
are the ones in "F2's read".

| | R2i seed 0 | R2i seed 1 | R2t | R2w | R3a seed 0 | R3a seed 1 | R3a spread |
|---|---|---|---|---|---|---|---|
| clean 5-20 sigma | 77.4 | 78.4 | 78.8 | 76.1 | 77.4 | 78.4 | 1.0 |
| clean 20-100 sigma | 70.6 | 71.5 | 73.5 | 69.9 | 71.2 | 72.7 | 1.5 |
| **clean 100-1000 sigma** | 53.5 | 50.9 | **59.2** | 53.4 | **51.4** | **56.1** | **4.7** |
| **dug 100-1000 sigma** | 36.7 | 40.0 | 32.4 | 32.4 | **40.8** | **34.0** | **6.8** |
| dug 20-100 sigma | 21.0 | 18.9 | 18.1 | 17.4 | 19.8 | 18.9 | 0.8 |
| clean saturated | 49.5 | 52.1 | 56.7 | 54.1 | 52.6 | 61.3 | 8.8 |
| dug saturated | 26.3 | 29.4 | 24.2 | 17.5 | 30.9 | 27.3 | 3.6 |
| clean in colour 100-1000 sigma | 30.2 | 27.4 | 36.1 | 30.4 | 29.6 | 34.6 | 5.0 |
| dug in colour 100-1000 sigma | 54.3 | 58.4 | 48.7 | 48.5 | 57.3 | 49.6 | 7.6 |
| core mean / rms at 100-1000 sigma | -0.34 / 1.41 | -0.49 / 1.31 | -0.32 / 1.26 | -0.02 / 1.75 | -0.48 / 1.51 | -0.26 / 1.37 | 0.23 / 0.14 |
| far sky | 0.25 | 0.26 | 0.23 | 0.26 | 0.26 | 0.29 | 0.03 |

The lobed twin, clean / dug at 100-1000 sigma: R2i seed 0 52.3 / 37.9, seed 1 49.3 / 41.8, R2t 58.1 / 34.0, R2w 52.2 / 33.2,
**R3a seed 0 50.7 / 41.2, seed 1 55.1 / 35.5** (spread 4.4 and 5.7). The plain twin is A's draws again, as F2 found.

**The real tiles** (320 master tiles, 53,108 catalogued stars; flat |<1| sigma / light left / dug, bright stars 100-1000 sigma,
`real-star-read-r3a_synth11_s{0,1}.txt`):

| | R2i seed 0 | R2i seed 1 | R2t | R2w | R3a seed 0 | R3a seed 1 |
|---|---|---|---|---|---|---|
| all 4,215 stars, flat | 96.4 | 95.4 | 97.4 | 95.6 | 92.4 | 96.9 |
| all, left / dug | 2.1 / 1.5 | 1.9 / 2.8 | 1.4 / 1.2 | 2.6 / 1.8 | 3.4 / **4.2** | 1.5 / 1.6 |
| **M42 (ASI294MC), 248 stars, flat** | 80.6 | 74.2 | 83.5 | 71.4 | **60.1** | 81.5 |
| M42, left / dug | 16.5 / 2.8 | 23.4 / 2.4 | 14.5 / 2.0 | 27.0 / 1.6 | 37.5 / 2.4 | 13.7 / 4.8 |
| Lobster (three views), flat | 98.9 / 99.5 / 99.9 | 98.2 / 99.7 / 100.0 | 98.7 / 99.8 / 99.9 | 98.0 / 99.4 / 99.7 | 97.0 / 98.7 / 98.1 | 98.0 / 99.4 / 99.5 |
| HIP 80609 (three views), flat | 94.8 / 96.4 / 98.2 | 88.6 / 95.7 / 97.3 | 95.6 / 96.8 / 98.6 | 94.5 / 96.1 / 96.2 | 89.5 / 93.2 / 91.4 | 96.2 / 97.1 / 97.3 |
| SMC (97 stars), flat | 67.0 | 91.8 | 93.8 | 79.4 | 62.9 | 91.8 |
| faint, 5-20 / 20-100 sigma flat | 94.8 / 97.1 | 96.4 / 98.3 | 95.4 / 98.7 | 94.3 / 97.5 | 92.9 / 96.4 | 94.5 / 98.2 |

HIP 80609's dug share at 100-1000 sigma is 10.5 / 6.6 / 8.3 % over its three views for seed 0 (R2t 4.2 / 2.9 / 0.7) and
2.8 / 2.5 / 2.0 for seed 1.

**The whole masters** (`score_whole.py`, the master-stretch config; the baselines' columns are their rows in "R2w's read" and
"Round 3: closing on the judges", whose masters and scorer are the same):

| | R2i seed 0 | R2t | R2w | R3a seed 0 | R3a seed 1 | R3a spread |
|---|---|---|---|---|---|---|
| Andromeda bright dark-sky stars flat / dug | 32.8 / 36.6 | 40.6 / 41.7 | 48.7 / 28.8 | 44.0 / 29.0 | 47.6 / 39.0 | 3.6 / 10.0 |
| Andromeda blue, 1.5-3 / 3-4.5 px | -1.10 / -0.90 | -1.23 / -1.00 | -0.89 / -0.74 | -0.99 / -0.79 | -1.38 / -1.02 | 0.39 / 0.23 |
| Tarantula bright dark-sky stars flat / left | 35.8 / 60.6 | 57.2 / 40.3 | 56.5 / 31.5 | 43.5 / 32.8 | 51.0 / 24.7 | 7.5 / 8.1 |
| Tarantula core R / G / B | +0.89 / +1.06 / +0.61 | +0.84 / +0.38 / +0.90 | -0.11 / -0.17 / +0.42 | -0.05 / -0.17 / +0.42 | +0.10 / -0.08 / +0.07 | |
| held-out crowded Tarantula / BP 17-19 | 69.8 / 90.3 | 81.3 / 85.4 | 77.8 / 92.6 | 75.7 / 92.4 | 79.1 / 93.8 | 3.4 / 1.4 |
| purity (light taken where no star is) | 0.6 | 0.5 | not in this table | 0.6 | 0.7 | 0.1 |
| Sagittarius crowded | 95.0 | 95.2 | 94.2 | 94.2 | 93.5 | 0.7 |
| **M31's nucleus R / G / B, no `--protect-cores`, %** | 3.2 / 3.1 / 0.5 | 1.1 / 6.1 / 3.1 | 0.5 to 4.4 | **3.0 / 6.4 / 4.7** | **20.3 / 19.0 / 16.7** | 17.3 / 12.6 / 12.0 |

The nucleus read in `m31-nucleus-*.txt` (a second reader of the same files) gives seed 0 3.0 / 6.5 / 4.8 % and seed 1 20.6 / 18.6 /
17.4 % over the inner 6 px; the 6-12 px ring is under 2 % for both. The ring read (the mean residual of injected bright stars
over 3 to 6 px, in sigma, R / G / B; no bar was set for it): R3a seed 0 -1.37 / -0.57 / -0.58 with 70.7 % of stars dug in a
channel, seed 1 -1.25 / -0.63 / -0.59 with 71.8 %, against R2t -0.80 / -0.25 / -0.49 (49.8 %), R2w 57.1 %, R2i seed 0 60.2 % and
seed 1 42.0 %: the deepest rings of any arm.

**The predictions, held or missed.**

1. *A's draws clean, each seed at least 56.6 and the mean at or above 59.2:* **missed.** Seed 0 51.4, seed 1 56.1 (0.5 under
   the line), the mean 53.8, which is 5.4 under R2t.
2. *A's draws dug, the mean no worse than 32.4:* **missed.** 40.8 and 34.0, mean 37.4; seed 1 alone is 1.6 over R2t, seed 0
   is 8.4 over and past R2i seed 1's 40.0.
3. *Lobed twin, clean at least 55.0 and dug at most 37.9 per seed:* **missed for seed 0** (50.7 and 41.2), **held for seed 1**
   (55.1 and 35.5, both by a hair).
4. *Whole masters, Andromeda's dug no worse than R2t's 41.7 and moving toward R2w's, the Tarantula's left at 40.3 or below:*
   **held, both seeds.** Dug 29.0 and 39.0 (seed 0 level with R2w's 28.8), left 32.8 and 24.7 (seed 1's is the lowest of any
   arm, R2w's 31.5 the next).
5. *M42 flat at least 77.1 per seed:* **missed for seed 0** (60.1, the lowest of any arm, 23.4 under R2t), **held for seed 1**
   (81.5, 2.0 under R2t).
6. *A bright-band difference under the bars is a seed's:* the rule did not apply, because **the two seeds differ past every
   bar** (clean 4.7 against 2.6, dug 6.8 against 3.2 on A's draws; 4.4 against 3.1 and 5.7 against 3.9 on the lobed twin).

**The guards.** Purity passes for both (0.6 and 0.7 %, bar 1.0). **M31's nucleus fails for both:** seed 0's green is 6.4 % (6.5
by the second reader) against 5, seed 1's three channels are 16.7 to 20.3 %. The R2t baseline itself reads 6.1 % in green on
this guard, so seed 0 sits where R2t does and the guard's line is one R2t also crosses; seed 1 is another matter, a remover that
takes a nucleus. Both are read without `--protect-cores`, which inference applies (#1658, a galaxy's nucleus is kept from any
remover), so neither would damage M31 in use, but the guard exists to detect what else compact and bright a remover takes.
**M42 per session fails for seed 0** (M42 60.1 and HIP 80609's third view 91.4 are both more than 6.4 under R2t's 83.5 and 98.6),
and passes for seed 1 (no session more than 2.0 under R2t: M42 and the SMC each 2.0 under, HIP 80609 and Lobster within 1.3).

**The kill rule's verdict: killed.** The rule's first clause (either seed under 56.6 clean on A's draws AND the lobed twin
under 55.0) is met by **seed 0** (51.4 and 50.7); seed 1 is under the line on A's draws (56.1 against 56.6) but not on the lobed twin
(55.1 against 55.0), so the twin does not agree and the clause does not fire for it. The second clause (a guard failed) is met by both
seeds, through the nucleus. The third (both seeds' dug above 35.6) is not met, seed 1's 34.0 being under it. A pass needed no
win over R2t but clean guards, and neither seed has them. R3a is therefore **not a base for R3b**, and its two seeds are kept
as the arm's own data: the spread below is what the next arm on this recipe is read against.

**The seeds' spread, R3a's own bars from here.** The spread of two seeds of this recipe on the master-stretched sets, wider
than R2i's on every bright-band read:

| read | R2i's bar | R3a's bar |
|---|---|---|
| A's draws, clean / dug at 100-1000 sigma | 2.6 / 3.2 | **4.7 / 6.8** |
| lobed twin, clean / dug at 100-1000 sigma | 3.1 / 3.9 | **4.4 / 5.7** |
| A's draws, clean 5-20 / 20-100 sigma | 1.0 / 0.9 | 1.0 / 1.5 |
| M42's real stars flat | 6.4 | **21.4** |
| real tiles, all stars flat / dug | 1.0 / 1.3 | 4.5 / 2.6 |
| Andromeda dug / Tarantula left | | 10.0 / 8.1 |
| M31's nucleus, green | | 12.6 |

A recipe whose two seeds stand this far apart cannot be separated from R2t by one seed: R2t's 59.2 on A's draws stands 7.8 over
seed 0 and 3.1 over seed 1, and only seed 1 is inside the arm's own bar of 4.7.

**What surprised.** (1) **That training in the inference stretch did not lift R2t's recipe**: the pre-registration read R3a as
R2t's net with the domain skew removed, and R2t on A's draws barely moved with the stretch (+0.8 in colour), yet R3a's mean is
5.4 points under it clean and 5.0 over it dug. R3a also changes the data (synth11's four draws and F6's re-stretched clusters
against R2t's synth4), so R2t's recipe on the master's stretch is not isolated from the generator's change. (2) **The seed
spread**: no pair so far stood 4.7 or 6.8 apart on the bright band (R2i's stood 2.6 and 3.2), and seed 1 is the better
of the two on every guard and bright read except the nucleus, which it takes at 20 %. (3) **The nucleus**: seed 0 is level
with R2t there, seed 1 at 20 % is the first of ours to fail it since R2s2 (74 to 79 %). (4) **That seed 0's whole-master row is
nearly R2w's** (Andromeda 44.0 / 29.0 against 48.7 / 28.8, Tarantula core +0.42 in blue as R2w's), while its real tiles are
the worst of the arms, a mismatch between the whole masters and the tiles, the other way round to R2t's. (5) **The ring**:
every R3a seed digs 70 % of bright stars in some channel, 10 to 30 points over the other arms, with the loss at 0.00000 at the
end of a cosine schedule that records no best step.

**Candidate next arms, as issue drafts** (not filed; the owner picks).

1. **Two more seeds of R3a's recipe (seeds 2 and 3), 12 hours each on the 1070.** With a spread this wide, two seeds do not
   say what the recipe does; four would give a spread and a mean the next arm can be read against. Gate: the mean of four on A's
   draws against R2t's 59.2 and the nucleus guard, the cost 24 h of the 1070 for a baseline, not a candidate.
2. **R3b on R2w's recipe (R2i's net on the master's stretch) plus the dig penalty (#1362).** R2w is the arm with the best
   dig and the nucleus inside its guard (0.5 to 4.4 %); R3a shows R2t's four levels do not carry over to this stretch without
   an arm of their own. Gate: A's draws dug at 100-1000 sigma under R2w's 32.4 by more than 3.2, and R2w's whole-master rows kept.
3. **Score R3a's seeds on their best held-out step.** The cosine schedule ran to the last step and no best step is recorded; if
   the trainer's held-out loss (`--val-every 500`) is in the log and the 2,000-step checkpoints are kept, the step the
   held-out loss chose may not be the last, and a cheap read (a score step per checkpoint) would say whether the spread is
   overfitting. Gate: either seed's A's draws clean at 100-1000 sigma at or over 56.6 with the guards clean.
4. **A nucleus-and-core guard in the trainer's training mix.** The guard failed on seed 1 at 20 % and it was never a
   training signal; a set of real bright compact structure (M31's nucleus, the cores of other galaxies) held out of the
   removal target, or a term for it, as R3b2's real-tile term would be. Gate: M31's nucleus under 5 % in every channel
   without protection (`--no-protect-cores`), no loss on A's draws beyond the bars.
5. **Read the nucleus guard with protection as shipped (the default).** The guard is read with `--no-protect-cores`, a remover's own behaviour;
   R2t itself sits over 5 % in green on it. The owner decides whether the guard's line is 5 % or the shipped path's, which
   changes whether R3a seed 1 fails anything.

##### R3b's pre-registration (2026-10-27)

R3b (#1829) is R2w's recipe on the master's stretch plus the dig penalty (#1362, "The speckle teacher"), on the draws only.
R3a is not its base ("R3a's read", candidate 2): R2w is the arm with the best dig and the nucleus inside its guard, and R3a
showed R2t's four levels do not carry over to this stretch without an arm of their own.

**When this was written.** Before any R3b training step: nothing has trained on the 1070, and the numbers for W below were
read on the CPU from existing weights and held-out cells.

**Inputs, as checked (read-only, 2026-10-27, after rebasing the flight on main).**

| input | found |
|---|---|
| synth11's export | `C:/temp/tianwen-scratch/r2-train-synth11/export.done` exists (exported 2026-10-23), the one R2w trained on |
| synth11's cache | `C:/temp/tianwen-scratch/r2-synth11`: `meta.json` and `prepare.done` (4,520 train and 320 held-out cells, 3 channels, 4 injected draws, 11 slots, `sigma.f16` the per-pixel noise planes the penalty reads); R2w's log head says "cells: 4520 train / 320 val", 3-channel tiles only, the same 113 train / 8 val sessions |
| R2w's recipe | `C:/temp/e2/r2w-driver.ps1` and the head of `r2w.log`: `run-r2.ps1 -Tag r2w -Arms synth11 -SeedCount 1 -FirstSeed 0 -Patience 24 -Steps 480000 -CheckpointEvery 2000`, levels 2, no teacher, no extra cache, plateau schedule on the held-out objective over 240 cells, stopped at step 154,500 (best 6.11e-06 at 130,500) |
| the dig penalty | `--speckle-weight` / `--speckle-sigma` in `n2n_smoke.py` and `-SpeckleWeight` / `-SpeckleSigma` in `run-r2.ps1`, present after the rebase; the 21 speckle unit tests pass on CPU |
| the trainer | `C:/temp/e2/r3b-run/FROZEN-AT.txt` names the commit "fix(viewer): a right press on Enhance does nothing and E runs no deep-sky program on a planet's way (#1345)" |
| R2w's weights | `C:/temp/tianwen-scratch/r2-synth4/r2w_synth11_s0.pt` |

**The arm.** The line, run as the train step of `arm-step.ps1` (the flight's leg 2), `-Name r3b_synth11_s<seed>`:

`run-r2.ps1 -Tag r3b -Arms synth11 -Export C:/temp/tianwen-scratch/r2-train-synth11 -ExportLog C:/temp/tianwen-scratch/r2-train-synth11/export.log -LastChunk 130 -Scratch C:/temp/tianwen-scratch -SeedCount 1 -FirstSeed 0 -Patience 24 -Steps 480000 -CheckpointEvery 2000 -SpeckleWeight 10000`

R2w's recipe exactly: base 32, two levels, `--cond-map`, `--band-loss 3`, the plateau schedule, no teacher, no extra cache, no
clipping, `--speckle-sigma` at its default 100. The only difference from R2w is `--speckle-weight`. `-CheckpointEvery 2000` is R2w's
(its driver passed it, so a resumed run is possible), not a recipe change. Seed 0 first; seed 1 (`-FirstSeed 1`) runs only if seed 0
clears the gate, to read the spread of this recipe.

**W = 10,000, and why.** The term is `site_penalty`'s: the summed squared shortfall of the output's luminance cores below
sky less 4 sigma at the removed stars' sites, divided by the pixels the pixel term averages. Its docstring says a weight of 1
is "the same order as the plain objective". **Measured, it is not.** On 64 evenly spread held-out cells of r2-synth11 (two fixed
draws each, CPU, the trainer's own objective: pixel MSE plus 3 times the band terms, rim masked; `w_read.py`, `w_grad.py` in
`C:/temp/e2/r3b-measure`):

| weights | plain objective | speckle term, weight 1 | ratio |
|---|---|---|---|
| R2w | 8.55e-06 | 3.3e-12 | 3.9e-07 |
| R3a seed 0 | 8.43e-06 | 2.8e-11 | 3.4e-06 |

The penalty is sparse (793 sites of at least 100 sigma read in 64 cells; 7 of them, 0.9 %, speckled under R2w) and its sum is
spread over every pixel of the tile, so at weight 1 it is six orders under the objective and training would never feel it.
Matching the VALUES would take W of about 2.6 million, but the values are the wrong thing to match, since the term is zero on
most batches and a squared shortfall's gradient is not the loss's. The GRADIENT is what the optimiser sees: over 8 batches of 8
tiles on R2w's weights, the global gradient norm of the plain objective is 6.8e-04 on average, and of the penalty at weight 1
1.1e-08 on average (zero on 6 of 8 batches); on the two batches where it is active the ratio is 3e4 and 2.8e3. **W = 10,000 is
the round number inside that range**: where the penalty is active its gradient is 0.3 to 3.5 times the plain objective's, and
averaged over batches (the 6 with none) 0.17 times it, so it is felt where a core is dug and costs nothing where none is.
Matching the values (W of about 2.6e6) would put the gradient 90 to 900 times the plain objective's where active, with no clip in
R2w's recipe, and is not tried. (The docstring of `site_penalty` in `n2n_speckle.py` carries the "same order" claim and is wrong
by those six orders; it is left for the code flight, since this leg is docs only.) W is the one knob this arm turns; if it fails, the next arm is W at 1e5, not a second change.
The measurement is of R2w's weights, which are not the weights training starts from, so it says where W should sit once the
pixel term has done its work, which is the weights R2w ended on.

**Predictions, against R2w's rows on F2's sets** ("F2's read", `f2-sets.json`, 7 sessions), at 100-1000 sigma unless stated. R2w
is one seed, so the bars are R2i's two-seed bars (2.6 clean and 3.2 dug on A's draws, 3.1 and 3.9 on the lobed twin; M42 6.4).

1. **A's draws, dug:** R3b at most 29.2 (R2w's 32.4 less 3.2), the hope 27 or less. The term acts on exactly this: a core left
   more than 4 sigma under the sky.
2. **A's draws, clean:** no lower than 50.8 (R2w's 53.4 less 2.6). A one-sided penalty on cores below sky does not ask for
   more removal, so clean should not move; a loss here would be the penalty trading cores for something else.
3. **Lobed twin:** dug at most 29.3 (33.2 less 3.9), clean at least 49.1 (52.2 less 3.1).
4. **Dug saturated on A's draws** (R2w 17.5, the lowest of any arm): not above 20.7.
5. **Whole masters, kept, not won** (R2w's rows, plus the same bars): Andromeda's bright dark-sky stars dug at most 32.0 and flat
   at least 45.1 (R2w 28.8 / 48.7, less the R3a spread of 3.6 for flat); the Tarantula's left at most 34.7 and flat at least
   49.0 (31.5 / 56.5, the R3a spread 7.5 for flat); the held-out crowded Tarantula at least 74.4 and Sagittarius at least 93.5
   (77.8 and 94.2 less R3a's spreads); the Tarantula's core within 0.5 sigma of R2w's (-0.11 / -0.17 / +0.42, R / G / B) in
   each channel. The digs on whole masters are where the penalty should show most, Andromeda's dug count being 28.8 % for
   R2w, so an improvement there is welcome but is not asked for.
6. **Real tiles:** all 4,215 stars flat at least 94.6 (R2w 95.6 less R2i's 1.0), dug no more than 3.1 (R2w 1.8 plus 1.3). **M42's
   real stars flat at least 65.0** (R2w 71.4 less 6.4); the SMC (97 stars, no bar) reported. The penalty needs no truth, so the
   real stars are where it could do harm that no synthetic read shows: it leaves a core at the sky's level and nothing more, but
   a 100-sigma real star left as light is the failure to watch, and M42 over nebula is where R2w already fell.
7. **Training:** the logged penalty term falls through the run and the plateau still ends it (R2w's ended at step 154,500); the
   share of held-out sites read as speckled, 0.9 % under R2w, is at most 0.45 %. That count is 7 events over 793 sites, so it is
   logged and not gated.

**The gate (from #1829).** A's draws dug at 100-1000 sigma under R2w's 32.4 by more than 3.2 (so at most 29.2), **and** R2w's
whole-master rows kept as in prediction 5. Both, since the dig on the draws is what the term is for and the whole masters are
what R2w is for.

**Guards, a fail on any is a fail of the arm.** Purity (light taken where no star is) at most 1.0 %. M31's nucleus read without
`--protect-cores` at most 5 % in every channel (R2w's 0.5 to 4.4 %). M42 read per session, no session more than 6.4 points under
R2w's (M42 71.4, Lobster 98.0 / 99.4 / 99.7, HIP 80609 94.5 / 96.1 / 96.2, the SMC reported, "R3a's read" for the table).

**Kill rule.** The run is stopped at once if its loss goes non-finite, or if the logged penalty term is above the plain objective
at step 20,000 (W too heavy, the weights then fitting the penalty and not the pairs). The arm is killed as a base for R3b2 if a
guard fails, or if A's draws clean at 100-1000 sigma is under 50.8 AND the lobed twin agrees (under 49.1). A miss on the gate alone
(dug not under 29.2, whole masters kept) is reported and the arm kept as the W data point: the penalty at 10,000 then does not
move the dig, and the next arm is W at 1e5, one change. A pass on A's draws with a whole-master loss past prediction 5 reads as
the penalty trading the master's cores for the draws', and R3b2's real-tile term is then not filed until that is understood.
One seed is a single draw of a recipe whose spread is not known on this net (R2i's two seeds stood 2.6 and 3.2 apart, R3a's
4.7 and 6.8): a win under 6.8 on the dug count is read as a candidate until seed 1 says otherwise.

**Measured, seed 0 (2026-10-28).** `r3b_synth11_s0.pt`, W = 10000 over 100 sigma, patience 24, trained on the GTX 1070 in five
resumed runs from its 2,000-step checkpoints. The plateau ended it at step 195,500, the held-out objective's best 7.298369e-06 at
step 183,500, after four halvings to lr 1.25e-05. 833.9 min of training (13.9 h) as the trainer logs it, at 31.3 tiles/s at the
end. That is 0.256 s a step against R2w's 154,500 steps in 6.9 h (0.161 s), so the penalty costs 1.59 times R2w's step (R2w 50.1
tiles/s). The 833.9 min includes the first 2,000 steps at 7.1 tiles/s, run before the noise cap moved to one batched sort on the
GPU ("perf(training): the dig penalty's noise cap reads every cell in one batched sort on the GPU"). The kill rule did not trip:
the loss stayed finite and at step 20,000 the penalty term was 4.4e-11 against a plain objective of 2e-05; from there to the end
it logged 2e-13 to 4e-11. The trainer log holds no traceback, error or non-finite value, and the ONNX export's error log only
torch's deprecation warning for the TorchScript exporter. ONNX and contract:
`r3b-logs-master/score-r3b_synth11_s0/r3b_synth11_s0.onnx` and `.contract.json`.

**Measured, seed 1 (2026-10-28).** `r3b_synth11_s1.pt`, the same recipe, in four runs resumed from its 2,000-step
checkpoints. The plateau ended it at step 207,000, the held-out objective's best 6.909892e-06 at step 195,000, after four halvings
to lr 1.25e-05. 873.9 min of training (14.6 h) as logged, at 31.6 tiles/s at the end: 0.253 s a step, 1.58 times R2w's. At step
20,000 the penalty term was 2.1e-10 against a plain objective of 5e-05; at the end it logged 3e-13 to 1.3e-12. The trainer log
holds no traceback, error or non-finite value, and the export's error log only the same deprecation warning. ONNX and contract:
`r3b-logs-master/score-r3b_synth11_s1/r3b_synth11_s1.onnx` and `.contract.json`. Neither seed is scored here; predictions 1 to
7, the gate and the guards are read by the scoring leg.

#### R3b's read (#1829, 2026-10-28)

R3b (#1829) is R2w's recipe on the master's stretch plus the dig penalty (`--speckle-weight 10000`, `--speckle-sigma` 100) on the
synthetic draws, two seeds on the 1070. Both were scored as R3a was: F2's sets (#1827), the real tiles, the ring and the nucleus
(`score-arm.ps1`), the three whole masters (`score_whole.py`, tags `r3b_s0` and `r3b_s1`). **The gate is missed by both seeds,
and the kill rule trips for both.** The penalty did not take the dig out of A's draws: it sits at 32.7 and 35.3 where R2w's is
32.4. R3b is not a base for R3b2.

**A's draws, master-stretched (7 sessions), at 100-1000 sigma unless stated** (`f2_read.py`; the baselines' columns are those of
"F2's read" and "R3a's read"):

| | R2i seed 0 | R2i seed 1 | R2t | R2w | R3a seed 0 | R3a seed 1 | R3b seed 0 | R3b seed 1 | R3b spread |
|---|---|---|---|---|---|---|---|---|---|
| clean 5-20 sigma | 77.4 | 78.4 | 78.8 | 76.1 | 77.4 | 78.4 | 76.6 | 78.0 | 1.4 |
| clean 20-100 sigma | 70.6 | 71.5 | 73.5 | 69.9 | 71.2 | 72.7 | 68.1 | 69.8 | 1.7 |
| **clean 100-1000 sigma** | 53.5 | 50.9 | 59.2 | 53.4 | 51.4 | 56.1 | **48.5** | **50.1** | **1.6** |
| **dug 100-1000 sigma** | 36.7 | 40.0 | 32.4 | 32.4 | 40.8 | 34.0 | **32.7** | **35.3** | **2.6** |
| dug 20-100 sigma | 21.0 | 18.9 | 18.1 | 17.4 | 19.8 | 18.9 | 18.8 | 20.5 | 1.7 |
| clean saturated | 49.5 | 52.1 | 56.7 | 54.1 | 52.6 | 61.3 | 46.4 | 52.1 | 5.7 |
| dug saturated | 26.3 | 29.4 | 24.2 | 17.5 | 30.9 | 27.3 | 25.3 | 18.0 | 7.2 |
| clean in colour 100-1000 sigma | 30.2 | 27.4 | 36.1 | 30.4 | 29.6 | 34.6 | 16.5 | 14.5 | 2.0 |
| dug in colour 100-1000 sigma | 54.3 | 58.4 | 48.7 | 48.5 | 57.3 | 49.6 | 60.6 | 68.2 | 7.6 |
| core mean / rms at 100-1000 sigma | -0.34 / 1.41 | -0.49 / 1.31 | -0.32 / 1.26 | -0.02 / 1.75 | -0.48 / 1.51 | -0.26 / 1.37 | +0.06 / 1.60 | -0.17 / 1.43 | 0.23 / 0.17 |
| far sky | 0.25 | 0.26 | 0.23 | 0.26 | 0.26 | 0.29 | 0.25 | 0.23 | 0.02 |

The lobed twin, clean / dug at 100-1000 sigma: R2w 52.2 / 33.2, **R3b seed 0 47.5 / 34.1, seed 1 48.8 / 36.1** (spread 1.4 and
1.9). The plain twin is A's draws again, as F2 found.

**The real tiles** (320 master tiles, 53,108 catalogued stars; flat |<1| sigma, bright stars 100-1000 sigma,
`real-star-read-r3b_synth11_s{0,1}.txt`):

| | R2i seed 0 | R2i seed 1 | R2t | R2w | R3a seed 0 | R3a seed 1 | R3b seed 0 | R3b seed 1 |
|---|---|---|---|---|---|---|---|---|
| all 4,215 stars, flat | 96.4 | 95.4 | 97.4 | 95.6 | 92.4 | 96.9 | **91.1** | 95.9 |
| all, left / dug | 2.1 / 1.5 | 1.9 / 2.8 | 1.4 / 1.2 | 2.6 / 1.8 | 3.4 / 4.2 | 1.5 / 1.6 | 7.4 / 1.4 | 3.3 / 0.8 |
| **M42 (ASI294MC), 248 stars, flat** | 80.6 | 74.2 | 83.5 | 71.4 | 60.1 | 81.5 | **17.3** | 66.1 |
| M42, left / dug | 16.5 / 2.8 | 23.4 / 2.4 | 14.5 / 2.0 | 27.0 / 1.6 | 37.5 / 2.4 | 13.7 / 4.8 | 81.5 / 1.2 | 33.5 / 0.4 |
| Lobster (three views), flat | 98.9 / 99.5 / 99.9 | 98.2 / 99.7 / 100.0 | 98.7 / 99.8 / 99.9 | 98.0 / 99.4 / 99.7 | 97.0 / 98.7 / 98.1 | 98.0 / 99.4 / 99.5 | 98.4 / 99.7 / 99.4 | 97.7 / 99.8 / 99.6 |
| HIP 80609 (three views), flat | 94.8 / 96.4 / 98.2 | 88.6 / 95.7 / 97.3 | 95.6 / 96.8 / 98.6 | 94.5 / 96.1 / 96.2 | 89.5 / 93.2 / 91.4 | 96.2 / 97.1 / 97.3 | 92.0 / 95.2 / 95.3 | 95.1 / 97.3 / 97.3 |
| SMC (97 stars), flat | 67.0 | 91.8 | 93.8 | 79.4 | 62.9 | 91.8 | 43.3 | 89.7 |
| faint, 5-20 / 20-100 sigma flat | 94.8 / 97.1 | 96.4 / 98.3 | 95.4 / 98.7 | 94.3 / 97.5 | 92.9 / 96.4 | 94.5 / 98.2 | 94.8 / 98.0 | 95.9 / 97.3 |

The dig penalty did what it was written to on the real tiles: the dug share at 100-1000 sigma is 1.4 and 0.8 %
(R2t 1.2, R2i seed 0 1.5; seed 1's is the lowest of any arm), and HIP 80609's is 2.7 / 1.4 / 4.5 % and 0.8 / 0.7 / 0.9 % (R2w 4.5 / 2.7 / 3.8). It was paid for in light left:
seed 0 leaves 81.5 % of M42's bright stars above +1 sigma, 56.7 % of the SMC's.

**The whole masters** (`score_whole.py`, the master-stretch config; `f2-whole-master/r3b_s{0,1}/row.txt`):

| | R2i seed 0 | R2t | R2w | R3a seed 0 | R3a seed 1 | R3b seed 0 | R3b seed 1 | R3b spread |
|---|---|---|---|---|---|---|---|---|
| Andromeda bright dark-sky stars flat / dug | 32.8 / 36.6 | 40.6 / 41.7 | 48.7 / 28.8 | 44.0 / 29.0 | 47.6 / 39.0 | **40.7** / 31.3 | **40.6** / 30.3 | 0.1 / 1.0 |
| Andromeda blue, 1.5-3 / 3-4.5 px | -1.10 / -0.90 | -1.23 / -1.00 | -0.89 / -0.74 | -0.99 / -0.79 | -1.38 / -1.02 | -0.80 / -0.89 | -0.91 / -0.85 | 0.11 / 0.04 |
| Tarantula bright dark-sky stars flat / left | 35.8 / 60.6 | 57.2 / 40.3 | 56.5 / 31.5 | 43.5 / 32.8 | 51.0 / 24.7 | 57.5 / **40.6** | 46.7 / **52.2** | 10.8 / 11.6 |
| Tarantula core R / G / B | +0.89 / +1.06 / +0.61 | +0.84 / +0.38 / +0.90 | -0.11 / -0.17 / +0.42 | -0.05 / -0.17 / +0.42 | +0.10 / -0.08 / +0.07 | +0.23 / -0.09 / +0.82 | +0.28 / +0.26 / +0.93 | |
| held-out crowded Tarantula / BP 17-19 | 69.8 / 90.3 | 81.3 / 85.4 | 77.8 / 92.6 | 75.7 / 92.4 | 79.1 / 93.8 | 82.4 / 92.5 | 81.3 / 93.0 | 1.1 / 0.5 |
| purity (light taken where no star is) | 0.6 | 0.5 | | 0.6 | 0.7 | 0.5 | 0.6 | 0.1 |
| Sagittarius crowded | 95.0 | 95.2 | 94.2 | 94.2 | 93.5 | 94.6 | 96.3 | 1.7 |
| **M31's nucleus R / G / B, no `--protect-cores`, %** | 3.2 / 3.1 / 0.5 | 1.1 / 6.1 / 3.1 | 0.5 to 4.4 | 3.0 / 6.4 / 4.7 | 20.3 / 19.0 / 16.7 | **14.0 / 11.7 / 6.8** | 3.5 / 4.8 / **6.5** | 10.5 / 6.9 / 0.3 |

The nucleus read in `m31-nucleus-*.txt` gives seed 0 13.3 / 11.4 / 7.0 % and seed 1 3.4 / 5.2 / 6.5 % over the inner 6 px; the 6-12 px
ring is under 2 % for both. The ring read (mean residual of injected bright stars over 3 to 6 px, R / G / B, sigma): seed 0
-0.70 / -0.30 / -0.37 with 43.8 % of stars dug in a channel, seed 1 -0.82 / -0.57 / -0.59 with 55.9 %, against R2w 57.1 %, R2t
49.8 %, R3a 70.7 and 71.8 %: seed 0's ring is the second shallowest of any arm (R2i seed 1 42.0 %), seed 1's level with R2w's.

**The predictions, held or missed.**

1. *A's draws dug at 100-1000 sigma at most 29.2:* **missed by both.** 32.7 and 35.3, the mean 34.0, 1.6 over R2w.
2. *Clean at least 50.8:* **missed by both.** 48.5 and 50.1, the mean 49.3, 4.1 under R2w.
3. *Lobed twin dug at most 29.3 and clean at least 49.1:* **missed by both** (34.1 / 47.5 and 36.1 / 48.8).
4. *Dug saturated at most 20.7:* **missed for seed 0** (25.3), **held for seed 1** (18.0, R2w's 17.5).
5. *Whole masters kept:* **missed by both.** Held: Andromeda dug 31.3 and 30.3 (at most 32.0), the held-out crowded Tarantula 82.4
   and 81.3 (at least 74.4, the highest of any arm), Sagittarius 94.6 and 96.3, Tarantula flat 57.5 for seed 0. Missed:
   Andromeda flat 40.7 and 40.6 (at least 45.1), Tarantula left 40.6 and 52.2 (at most 34.7, seed 1's the highest of any arm),
   Tarantula flat 46.7 for seed 1 (49.0), and seed 1's core blue +0.93 is 0.51 from R2w's +0.42, a hair past the 0.5 line.
6. *Real tiles, all flat at least 94.6, dug at most 3.1, M42 flat at least 65.0:* **missed for seed 0** (91.1 all, M42 17.3, the
   dug share held at 1.4), **held for seed 1** (95.9, 0.8 dug, M42 66.1, 1.1 over the line).
7. *The penalty term falls, the speckled share at most 0.45 %:* **the term held** (4.4e-11 and 2.1e-10 at step 20,000, 2e-13 to
   1.3e-12 at the end, in the training legs' logs); **the speckled share was not read in this leg** (the trainer's log carries the
   term, not the share), so that half stays open. The gate was not set on it.

**The guards.** Purity passes (0.5 and 0.6 %, bar 1.0). **M31's nucleus fails for both:** seed 0 at 14.0 / 11.7 / 6.8 %, seed 1
at 3.5 / 4.8 / 6.5 % (blue over 5). **M42 fails for seed 0** (17.3 against the floor of 65.0, R2w's 71.4 less 6.4) and passes for
seed 1 (66.1, 1.1 over).

**The gate and the kill.** The gate (dug at most 29.2 AND R2w's whole-master rows kept) is **missed by both seeds**. The kill rule
(a guard fails, or clean under 50.8 AND the lobed twin under 49.1) **trips for both**: seed 0 on the nucleus, M42, and the
clean-and-twin pair; seed 1 on the nucleus's blue and the clean-and-twin pair (50.1 and 48.8, each a short step under the line).
R3b is **killed as the base for R3b2**. By the pre-registration a gate miss alone would have kept it as the W data point; the kill
adds that the whole recipe, not only the penalty, lost ground on the draws' clean count (R2w 53.4, R3b 48.5 and 50.1).

**The spread.** On A's draws the two seeds stand 1.6 apart on clean and 2.6 on dug, inside R2i's bars (2.6 / 3.2) and far inside
R3a's (4.7 / 6.8). On everything else they do not agree: M42 flat 17.3 against 66.1, the Tarantula's left 40.6 against 52.2, the
nucleus 14.0 against 3.5, dug saturated 25.3 against 18.0. A draws-only read would have called the arm steady; the real tiles and
the whole masters say it is not.

**What surprised.**

- **The penalty was satisfied almost at once and the dig did not move.** Its term was at 1e-10 by step 20,000 and 1e-13 at the
  end, yet A's draws dug is at R2w's level or above. Either 10,000 binds too weakly against the plain objective (the gradient
  ratio set W on R2w's weights, which already dug little by the penalty's measure) or what the penalty counts, a speckle over 100
  sigma after the noise cap, is not what "dug" counts in the eval. The two readings predict different things for W at 1e5.
- **Colour clean fell by half** (16.5 and 14.5 against R2w's 30.4 on A's draws) and colour dug rose (60.6 and 68.2 against 48.5).
  No other arm on this export moved colour like that; the penalty is a luminance-side term and the net may buy it with colour.
- **The penalty's win shows on the real tiles, as a loss of removal.** Dug on real stars fell to 1.4 and 0.8 %, and seed 0 left 81.5 % of M42's bright stars above +1 sigma. That is the timidity R3b2's pre-registration worries about, here
  without the real-tile term.
- **The held-out crowded Tarantula rose** (82.4 and 81.3, the best of any arm) with the Andromeda flat down (40.7, 40.6 against
  R2w's 48.7): the penalty trades the dark-sky bright stars for the crowded field.

**Candidate next arms** (drafts, not filed; none joins the milestone until the owner picks):

- **R3b-W: W at 1e5 on the same recipe, one change.** The pre-registration's own next arm for a gate miss. Cost 14 to 15 h a seed
  on the 1070. Read first whether the term at 1e5 still falls to zero, and the speckled share, since both of this read's
  readings of the miss (too weak, or a different thing counted) are tested by it. With R3b's colour and nucleus misses, a seed 1
  alone is not enough to start; run it with two seeds or not at all.
- **R3b-W-cond: the penalty on luminance only, colour left to the plain loss**, if the colour fall above is the penalty's. Cheaper
  to decide: re-score R3b's draws in luminance before any training.
- **R3b2: the dig penalty on real master tiles too.** Not filed. Its base is R2w, not R3b, and its pre-registration needs a timidity
  guard on M42 and the SMC (flat at least R2w's less 6.4), which R3b has just shown it can miss without the real-tile term.
- **R3c: the winner plus a teacher rebuilt from R3a's two seeds.** Not filed: R3a is killed and R3b has no winner, so there is
  nothing to put the teacher on top of. R2w stays the arm to beat.
- **A third seed of R2w on the master's stretch**, to give the plan the bar it is missing: R2w stands alone, so every comparison above
  is against one draw of its recipe.

#### R2w seed 1, and the bar it gives (2026-10-30)

R2w's recipe again at seed 1 (`r2w_synth11_s1`, 156,500 steps, best held-out loss 5.96e-6), scored on F2's master-stretched
scorecard (`training/star-removal/score-arm.ps1`, `f2-scorecard.master-stretch.json`). The real tiles, 100 to 1000 sigma, 4,215
stars (`real-star-read-r2w_synth11_s1.txt`):

| | R2w seed 0 | R2w seed 1 | R2t |
|---|---|---|---|
| flat \|<1\| | 95.6 | 96.0 | 97.4 |
| dug <-1 | 1.76 | 2.14 | 1.23 |
| M42 (ASI294MC), flat | 71.4 | 74.6 | 83.5 |
| SMC, flat | 79.4 | 91.8 | 93.8 |
| M31's nucleus, 0 to 6 px, R / G / B | 1.6 / 2.6 / 4.4 | 0.7 / **7.0 / 6.4** | |

The second seed moves the bright stars by less than half a point and the dug share by 0.4, but M31's nucleus from comfortably
under the 5 % guard to over it in green and blue, and the SMC by 12 points. So a single run of this recipe cannot read the
nucleus or the SMC, and every arm against R2w is read against both seeds.

#### R3f: R2w's recipe on the guarded draws (#1867), pre-registered 2026-10-30

#1867 refuses a diverged catalogued star at the injector (`InjectionPopulation`: a non-finite `LocalSigma`, or an unsaturated
amplitude past `OvershootMargin` (4) times the frame's clip in any channel). synth11 was exported before it, so its draws carry
those stars at amplitudes no real star has. R3f is R2w's recipe exactly (seed 0) on **synth13**, synth11's export command from a
binary at main as of #1915, on synth11's plates: the guard is the one difference. Rebuilding the plates is #1867's other half
(about 13 hours) and is not in this arm. Scored as R2w seed 1 was. The run is `C:/temp/e2/r3f-driver.ps1` (export, train, score),
the pre-registration `C:/temp/e2/r3f.prereg`.

- **P1:** dug at most 1.76 (the better R2w seed).
- **P2:** flat at least 95.6 (the worse R2w seed): no loss of bright-star removal.
- **P3:** M31's nucleus under the 5 % guard in every channel.
- **Adopt** when P1 and P2 hold. **Kill** when flat falls under 95.6 or dug rises over 2.14 (worse than both seeds). Between, it is
  inside R2w's own spread and reads as no measurable change; the plates are then the next lever, not a second seed of this arm.
- Reported beside it: how many catalogued stars the guard refused (`InjectionPopulation.Refused`), and M42.

**Read (2026-10-31): KILLED on both rules.** Stopped converged at step 136,000 (best held-out 5.94e-6 at 88,000, 6.8 hours), scored on the
320 real master tiles beside R2w's two seeds:

| 100-1000 sigma, 4215 stars | R3f | R2w s0 | R2w s1 |
|---|---|---|---|
| flat \|<1\| | **92.88** | 95.61 | 96.01 |
| light >+1 | 4.08 | 2.63 | 1.85 |
| dug <-1 | **3.04** | 1.76 | 2.14 |
| M42 flat | 45.2 | 71.4 | 74.6 |
| SMC flat | 63.9 | 79.4 | 91.8 |

Every session moves the same way: Lobster's dug share rises from under 0.5 % to 1 to 2 % a channel, HIP 80609's to 4 to 7 %, and
M42 leaves half its bright stars lit (52 % against 24 to 27 %). Refusing the impossible-amplitude draws did not teach the model
cleaner surroundings; it removed the brightest examples it learned bright-star removal from, and the 5-20 sigma flat fell too
(93.6 against 94.3 / 95.2). P3 was not read: the scorer's ring step failed on its baselines (`ring_annulus.py` took the
comma-joined list as one arm), and the nucleus step runs after it; neither changes a verdict both kill rules already give. The
export logs carry no `Refused` count, so how many stars the guard took out is unmeasured. Per the pre-registration, the plates
(#1867's other half, about 13 hours) are the next lever, not a second seed of this arm; and the guard should be read again WITH
rebuilt plates before it is kept in the export.

#### R3g: #1867 complete, the guard and the rebuilt plates (2026-11-01)

R2w's recipe (seed 0) on **synth14**: synth13's command and binary over the plates rebuilt with #1867's plate-builder guard
(`D:/Astro-Dataset/2026-10-12-full/starless-1867`), so the plates are the one change from R3f. Pre-registered in
`C:/temp/e2/r3g.prereg` before its export: **P1** dug at most 1.76, **P2** flat at least 95.6, **P3** M31's nucleus under 5 % in
every channel, **P4** flat above R3f's 92.88 and dug below its 3.04 (the plates, not the draws, carry the gain); adopt on P1 and
P2, kill on flat under 95.6 or dug over 2.14.

**Read: KILLED on both rules; P3 and P4 hold.** Scored as R3f was, with the ring step working this time:

| 100-1000 sigma, 4215 stars | R3g | R3f | R2w s0 | R2w s1 |
|---|---|---|---|---|
| flat \|<1\| | **94.35** | 92.88 | 95.61 | 96.01 |
| light >+1 / dug <-1 | 3.32 / **2.33** | 4.08 / 3.04 | 2.63 / 1.76 | 1.85 / 2.14 |
| M42 flat | 72.2 | 45.2 | 71.4 | 74.6 |
| SMC flat | **37.1** | 63.9 | 79.4 | 91.8 |
| ring 3-6 px, R / G / B sigma | -1.18 / -0.49 / -0.41 | - | -0.80 / -0.57 / -0.26 | -0.87 / -0.60 / -0.39 |
| M31 nucleus 0-6 px, R / G / B | +2.5 / -4.4 / +4.6 % | - | | |

The rebuilt plates win back most of what the guard alone lost (P4), so they were the bigger lever, but #1867 complete still
trains a worse remover than R2w on the old plates, and far worse on the SMC. R2w stays the default; R2t (97.41 flat, 1.23 dug)
is still the best real-tile read of any arm. Its SMC row is the next section's subject.

#### The SMC's stars: chromatic width and a smooth sky (2026-11-01)

The owner, on R3g's read: "figure out what can we do about SMC. i believe fixing SMC will also increase flat across the board".
Tracked as #1947.

**The SMC read splits by seed, not by recipe.** Every seed 1 stands near 90 % flat (R2i 91.8, R2w 91.8, R3a 91.8, R3b 89.7) and
every seed 0 at 37 to 79 (R2i 67.0, R2w 79.4, R3a 62.9, R3b 43.3, R3f 63.9, R3g 37.1). The cache is shared by every seed; the
seed is the weights' initialisation and the batch order (`n2n_smoke.py`), so an input the training data does not constrain is
answered by where the weights started.

**Blue stars are the worst bin in every session.** Read star by star on the 4,215 bright real stars (each star's colour its
channels' core excess, in the export's stretch): the stars with a blue share of 0.30 to 0.35 come out flat 84.5 to 95.3 % for
R2w, R3g and R2t, against 94.3 to 97.8 for every redder bin, and session by session alike (HIP 80609 74 to 83 against 95 to 97,
M42 36 to 59 against 75 to 86). The SMC is the extreme because 41 % of its bright stars are that blue (Lobster 16, M42 9, HIP
80609 3). The injector is not short of blue stars: its stars' blue share spans the real stars' (median 0.245 against 0.263, 90th
percentile 0.349 against 0.344), session by session.

**What the SMC leaves is green, and its stars are wider in green and blue.** R3g leaves green past +1 sigma on all of the SMC's
blue stars and 84 % of the rest; the L-eNhance night's stars are 1.23 times wider in green and blue than in red (OIII and
H-beta against H-alpha), where the training stars' injected (G+B)/2 over R width runs 0.635 / 0.783 / 1.115 (10th / 50th / 90th
percentile; the 62 L-Ultimate nights are most of them).

**The causal check (`dataset degrade --chromatic-width`, `ChromaticWidth`).** F2's random set (`f2-sets.json`, `sets.random`:
same bake, plates and arguments, its 7 validation sessions) exported three times, identical but for the option (own widths, 1.0,
1.25), each model inferred and scored, and read against the truth: per channel, the output's 3x3 core less the clean tile's, in
the draw's noise sigma, on the SAME stars in every arm (the band chosen once on the own export; the option draws nothing, so star
*i* of a tile is the same star in all three). `C:/temp/e2/chroma` holds the script, the exports and `chroma_read.py`.

| same 372 SMC stars, green left >+1 | own (about 1.23) | 1.0 | 1.25 |
|---|---|---|---|
| R2w seed 0 | 93.3 % | **75.8 %** | 94.6 % |
| R2w seed 1 | 90.1 % | **66.1 %** | 91.9 % |
| R3g | 99.2 % | **89.0 %** | 99.2 % |

**Width is a real cause, and not the main one.** Even at 1.0, red, which the option never touches, is left on 61 to 80 % of the
SMC's stars, against 11 to 34 % on HIP 80609 and Lobster; and drawing those two at 1.25 makes no model leave their green (3 to 9
%), they dig instead. What sets the SMC apart is its sky: its stretched tiles carry a noise sigma of **0.0010** against 0.0036
(HIP 80609) and 0.0045 (Lobster), and its brightest 0.1 % of pixels stand at 0.32 against 0.74 to 0.79. Only 1.7 % of the
training tiles are as smooth (7 of 130 training sessions have a median sigma under 0.0015, broadband wide fields, no narrowband
night). In ABSOLUTE units every model leaves +2 to +8e-3 at the SMC's stars in every channel, where the other sessions centre
near zero (HIP 80609 digs about 2e-3): a real under-removal, which the smooth sky makes 3 to 7 sigma. (This truth-based read is
far stricter than the real-star check's ring-relative one; compare its rows with each other, never with that check's 95 %.)

**Next (#1947):** a noise-level causal check first (a tile's noise raised to a set level, the SMC re-read at four times its own),
then R3h, the smooth tiles oversampled in the trainer with no re-export, which leaves the stars' shapes alone, the change every
arm that took M31's nucleus made (R2l, R2m, R2s, R2s2, R2v). A chromatic-width spread in the injector comes only after, read
against the nucleus guard first.

#### The SMC's noise check: not the sky, and the heaviest wings in the archive (2026-11-01)

#1947's first step, the owner's "Start". `dataset degrade --noise-scale` (`Options.NoiseScale`) carries every plate cell at a set
multiple of the master's noise, as if stacked from 1/scale^2 of its frames: the master's own noise at each pixel's level,
sqrt(scale^2 - 1) times over, from the cell's own stream, so the clean tile and every draw of the cell carry the same added noise,
while the plan, the render and the stretch are as without it (the SMC's 160 injection rows came out byte-identical to the plain
export's). The SMC alone re-exported at 2 and 4 times its noise, the plain arm the chromatic-width check's own export, the three
models inferred and read against the truth on the same 372 stars (`C:/temp/e2/noise`, `noise_read.py`). The tiles reached a
stretched sigma of 0.0010 / 0.0020 / 0.0040, the other sessions' level at four times.

| median left vs truth, R / G / B, x 1e-3 | plain | x2 | x4 |
|---|---|---|---|
| R2w seed 0 | +5.2 / +6.5 / +3.6 | +3.9 / +6.6 / +2.5 | +4.2 / +9.1 / +2.1 |
| R2w seed 1 | +2.8 / +4.9 / +1.0 | +3.1 / +5.3 / +1.7 | +4.0 / +7.2 / +2.8 |
| R3g | +2.0 / +7.6 / +4.6 | +2.7 / +8.5 / +3.6 | +3.6 / +8.8 / +2.3 |

**The smooth sky is not the cause.** The absolute leftover does not shrink with the noise: green, the SMC's main leftover, grows in
all three models, red and blue go either way. In sigma it falls (R3g's green left 99 / 94 / 80 %), but only because the sigma
rose: the read #1947 named in advance says the noise is not it, so R3h as planned (smooth tiles oversampled) is not run.

**Nor is the stars' absolute faintness** (`matched_read.py`, the plain export, every unsaturated injected star binned by its core
light in absolute stretched units). At the SMC's own light the other sessions' stars come out clean, the SMC's do not, and its
leftover grows with the star's light, a fixed share of each star left behind:

| G left, median x 1e-3 (share past +1e-3), R3g | SMC | HIP 80609 | Lobster |
|---|---|---|---|
| core light 0.03-0.1 | +3.6 (93 %) | +0.1 (41 %) | +0.7 (47 %) |
| core light 0.1-0.3 | +6.9 (99 %) | -0.0 (41 %) | -1.3 (31 %) |
| core light 0.3-1 | +9.4 (100 %) | -1.7 (27 %) | -1.9 (28 %) |

R2w's two seeds read the same way. **What is the SMC's own is its stars' shape**: its injected stars are drawn at a Moffat beta of
1.74 / 1.42 / 1.40 (R / G / B median), against 3.9 / 3.3 / 3.5 on HIP 80609 and 4.5 / 5.7 / 6.3 on Lobster, its field profile
being drawn per star from its own catalogue's fits. Over the 100 sessions of the stretch survey's export (`stretch-survey`, the
training plates through the same injector) the SMC's median star beta, 1.54, is the lowest of all; the training stars' 1st / 5th
/ 50th percentile is 1.67 / 1.99 / 3.80, 1.2 % of them under 1.8. Heavy wings in green and blue, on top of the 1.23 chromatic
width, put the SMC's stars at the far edge of the training population, and a remover that subtracts a typical star's shape
leaves part of each one.

**Next (#1947), a third causal check**: the SMC exported with every star's beta held at a training-typical value (an option in
the shape of `--chromatic-width`, drawing nothing), read as these were. If the leftover falls, the fix is the training stars'
beta range, a change to the stars' shapes, read against M31's nucleus guard before any arm is trained.

#### The SMC's wing check: heavy wings are the cause, and R3h (2026-11-01)

The owner: "we need to be fixing / implement things", "more checks more measurements always good". Three measurements, then a
training arm.

**Is the SMC's beta real?** A Moffat's beta sets how fast a star's light falls off past its core at a given FWHM: a high beta (4 to
6) is a compact star with little light in its skirt, a low one (1.4 to 1.7) keeps a broad, bright skirt. The SMC night's field
profile fits 1.74 / 1.42 / 1.40 (R / G / B); the same target through the same ASI585 five nights later, broadband, fits 5.2 / 3.6
/ 3.3, so it is that night's capture (the L-eNhance, or its focus), not the target or the camera. Read off bright, isolated,
unsaturated stars as an azimuthal median over the peak above the local sky (`C:/temp/e2/beta/wing_read.py`), the real stars and
the injected ones agree:

| green light over the peak, % | 2 px | 3 px | 4 px | 6 px | 8 px |
|---|---|---|---|---|---|
| SMC, real stars (177) | 25.7 | 12.5 | 6.1 | 1.9 | 0.9 |
| SMC, injected (27) | 27.2 | 13.8 | 6.6 | 2.1 | 0.9 |
| HIP 80609, real (3,579) | 16.3 | 4.1 | 0.8 | 0.0 | -0.1 |
| Lobster, real (5,500) | 13.2 | 2.8 | 0.6 | 0.0 | -0.1 |

**The causal check (`dataset degrade --star-beta`, `StarBeta`).** F2's random set exported twice more by the chromatic-width check's
command, every star's beta held at 3.8 (the training stars' median) and at 1.5 (the SMC's own), drawing nothing, so every star
keeps its place, brightness, colour, width and halo table; the three models inferred and read against the truth on the same stars
(`C:/temp/e2/beta`, `beta_read.py`):

| green median left, x 1e-3, R2w s0 / R2w s1 / R3g | own beta | all at 3.8 | all at 1.5 |
|---|---|---|---|
| SMC | +6.5 / +4.9 / +7.6 | **-0.6 / -2.1 / +0.7** | +5.6 / +3.8 / +6.4 |
| HIP 80609 | -2.3 / -3.6 / -2.4 | -2.5 / -4.2 / -2.8 | **+13.7 / +12.4 / +11.4** |
| Lobster | -1.7 / -2.9 / -1.6 | -3.3 / -4.3 / -3.5 | **+20.0 / +19.9 / +18.6** |

**It runs both ways.** Drawn with a typical star's wings, the SMC's stars are cleared by every model (R2w s0's green left past
+1e-3 falls from 93.5 to 14.8 %); drawn with the SMC's wings, the two sessions every model clears are left worse than the SMC ever
was. No model has learned a heavy-winged star: it subtracts a typical star's shape and leaves the rest of the skirt. The training
set holds few: of synth11's 113 training sessions 4 fit under beta 2.0 (160 of 4,520 cells), 18 under 2.5 (720 cells, 15.9 %);
its lowest is the Helix through the same ASI585 at 1.63.

**R3h (`C:/temp/e2/r3h.prereg`)**: R2w's recipe on synth11's export with one change, the trainer's new `--oversample-sessions` /
`--oversample-factor` (off, the draw and its rng stream are what they were): those 18 sessions' cells drawn three times as often,
36 % of the draws. No star shape changes, the change every arm that took M31's nucleus made, so the nucleus guard should hold. Its
bar: SMC flat at least 91.8 on seed 0 (R2w seed 1's), pooled flat at least 95.6 and dug at most 2.14, the nucleus under 5 % in
every channel; and the absolute SMC green median at most +2.0e-3 on the plain export. If oversampling cannot reach the SMC's wings
(nothing in training goes below 1.63), the next arm draws a share of the injected stars at a lower beta, read against the nucleus
guard first.

#### R3h: oversampling the low-beta sessions learns the field, not the wings (2026-11-02)

**Killed by its pre-registration (`C:/temp/e2/r3h.prereg`).** R2w's recipe on synth11 with the 18 low-beta sessions drawn three
times as often; it converged at step 165,500 (best held-out 6.48e-6 at 141,500), scored as R3g was:

| real tiles, 100-1000 sigma | flat \|<1\| | left >+1 | dug <-1 | SMC flat | SMC left | SMC dug | HIP 80609 dug (R/G/B) |
|---|---|---|---|---|---|---|---|
| R2w s0 | 95.6 | 2.6 | 1.76 | 79.4 | 13.4 | 7.2 | 4.5 / 2.7 / 3.8 |
| R2w s1 | 96.0 | 1.9 | 2.14 | 91.8 | 7.2 | 1.0 | 6.3 / 2.5 / 5.4 |
| **R3h s0** | **88.8** | 0.7 | **10.44** | 74.2 | 1.0 | 24.7 | 28.3 / 17.2 / 14.4 |

P1 (SMC flat at least 91.8) fails, P3 (pooled flat 95.6, dug 2.14) fails, the kill fires. P4 holds: M31's nucleus 3.7 / 0.2 / 2.4 %
over 0 to 6 px. P2 holds too: the SMC's absolute green median on the plain export is +1.51e-3 (R2w s0 +6.52). Light left over fell
everywhere (0.7 % pooled, the lowest of any arm, and 20-100 sigma flat reached 98.0); what rose is digging.

**What it learned, from the wing check's exports (`beta_read.py`, green median x 1e-3):**

| | SMC own | SMC at 3.8 | SMC at 1.5 | HIP 80609 own | HIP 80609 at 1.5 | Lobster own | Lobster at 1.5 |
|---|---|---|---|---|---|---|---|
| R2w s0 | +6.52 | -0.64 | +5.63 | -2.25 | +13.67 | -1.65 | +20.03 |
| R3h s0 | +1.51 | -1.34 | +0.88 | -4.39 | +13.82 | -1.46 | +20.49 |

R3h clears the SMC at either beta and leaves HIP 80609's and the Lobster's stars drawn at the SMC's beta exactly as R2w does. It
did not learn a heavy-winged star; it learned that a field LIKE the low-beta sessions' takes a deeper subtraction, and its typical
stars elsewhere are dug harder for it. A session's beta is a property of the session in the training data, so the network can
read it off everything else the session gives a cell (its stars' widths and colours, its noise, its background steered from the
master) rather than off the star, and oversampling only made that shortcut pay more.

#### R3i: a beta drawn per cell, on any plate (2026-11-02)

The fix follows: draw the wings independently of the session, so only the star can tell them. `dataset degrade --star-beta-draw
share,low,high` (`StarBetaDraw`) draws that share of the cells with every star at one beta uniform in [low, high], on whatever plate
the cell comes from, from each draw's own stream (the stars keep their places, brightness and colour; each star's beta is in
injections.jsonl). A cell's stars share one beta, as a real field's do, so a tile's bright stars show the faint ones' wings. The
arm: synth11's export recipe with `--star-beta-draw 0.3,1.3,2.5` (30 % of cells at the SMC's range and below; the archive's lowest
session fits 1.63), R2w's training recipe with no oversampling, read against R3h's bar plus the held-beta exports: HIP 80609 and
the Lobster at 1.5 cleared (green median under +2e-3) without digging their own stars past R2w's.

#### R3i's read: the wings are learned, and paid for in digging (2026-11-02)

**Killed by its pre-registration (`C:/temp/e2/r3i.prereg`), and the first arm whose mechanism held.** Exported from the binary of
PR #1961 (synth15, 08:52 to 12:26), trained on the 1070 to step 215,000 (best held-out 6.13e-6 at 203,000, four halvings, 9.6 h),
scored as R3h was:

| real tiles, 100-1000 sigma | flat \|<1\| | left >+1 | dug <-1 | SMC flat | SMC left | SMC dug | HIP 80609 dug (three views) | M31 nucleus 0-6 px, R / G / B |
|---|---|---|---|---|---|---|---|---|
| R2w s0 | 95.6 | 2.6 | 1.76 | 79.4 | 13.4 | 7.2 | 4.5 / 2.7 / 3.8 | |
| R2w s1 | 96.0 | 1.9 | 2.14 | 91.8 | 7.2 | 1.0 | 6.3 / 2.5 / 5.4 | |
| R2t s1 (four levels, synth4) | 97.4 | 1.4 | 1.23 | 93.8 | 6.2 | 0.0 | 4.2 / 2.9 / 0.7 | |
| **R3i s0** | **95.1** | 1.4 | **3.44** | **93.8** | **0.0** | 6.2 | **11.3** / 4.5 / 3.2 | +4.0 / **+7.7 / +10.2** % |

| green median x 1e-3 | SMC own | SMC at 1.5 | HIP 80609 own | HIP 80609 at 1.5 | Lobster own | Lobster at 1.5 |
|---|---|---|---|---|---|---|
| R2w s0 | +6.52 | +5.63 | -2.25 | +13.67 | -1.65 | +20.03 |
| R2w s1 | +4.88 | +3.76 | -3.64 | +12.41 | -2.86 | +19.90 |
| R3h s0 | +1.51 | +0.88 | -4.39 | +13.82 | -1.46 | +20.49 |
| **R3i s0** | **+1.30** | +0.67 | **-4.87** | **-0.10** | -1.64 | **+0.69** |

P1 holds (HIP 80609 and the Lobster drawn at the SMC's beta cleared, -0.10 and +0.69 against R2w's +13.7 and +20.0), P3 holds (the
SMC's own green +1.30), and the SMC's real stars are flat on 93.8 % with none left, R2t's figure and the best of any master-stretched
arm. P2 fails on HIP 80609 (own green -4.87 against -3.0, though R2w seed 1 also misses at -3.64), P4 fails on the pooled real tiles
(flat 95.1 against 95.6, dug 3.44 against 2.14) and P5 on M31's nucleus. So drawing the wings apart from the session did what R3h's
oversampling could not: the network now reads a heavy wing off the star. With 30 % of cells all at beta 1.3 to 2.5 it also expects
one where there is none, and digs ordinary bright stars (HIP 80609's first view most) and takes an extended core. The ring read agrees:
R/G/B -0.73 / -0.63 / -0.57 sigma at 3 to 6 px against R2w s0's -0.80 / -0.57 / -0.26, blue dug deeper. The next question is the dose.

#### R3j, R3k and R3l: the dose of drawn wings, pre-registered (2026-11-02)

Written after R3i's read and before any of the three trains (`C:/temp/e2/r3jkl.prereg`). Each is synth11's export from the binary of
PR #1961 with one change to `--star-beta-draw`, R2w's training recipe, seed 0, scored as R3i, trained one after another on the
1070 while the next export runs on the CPU:

| arm | export | `--star-beta-draw` | what it asks |
|---|---|---|---|
| R3j | synth16 | 0.5,1.3,2.5 | the dose up |
| R3k | synth17 | 0.15,1.3,2.5 | the dose down |
| R3l | synth18 | 0.3,1.3,6 | R3i's share over the archive's whole range, so beta under 2.5 is about 7.7 % of cells and the drawn cells span ordinary stars too: beta READ over a range against assumed from a narrow one |

**Bars**, R3i's unchanged: P1 the drawn-1.5 green median at most +2.0e-3 on HIP 80609 and the Lobster; P2 their own at least -3.0e-3;
P3 the SMC's own at most +2.0e-3; P4 pooled flat at least 95.6, dug at most 2.14, the SMC flat at least 91.8; P5 the nucleus under
5 % per channel. **Kill:** pooled flat under 95.6, dug over 2.14, or the nucleus guard broken. All five holding makes a candidate for
a second seed.

**Predictions**, from the dose reading: R3j keeps P1 and P3 and digs more than R3i (pooled dug over 3.44, HIP 80609's own green under
-4.87), and is killed; if it digs LESS, the dose reading is wrong, R3i's dig is its seed's, and R3i seed 1 comes before any other
share. R3k digs between R2w s1 and R3i or less, with P1's medians between R3i's and R2w's and the SMC's real flat still at least
91.8; passing every bar is the hope, not the expectation. R3l keeps less of P1 than R3i and digs at or under R2w. A three-point read
(0.15 / 0.3 / 0.5) monotone in both dig and P1 says the dose is the lever; a non-monotone one says a single seed cannot read it,
and the next step is seeds, not shares.

#### R3q and R3r: saturated stars as the masters show them (2026-11-10)

Tracked as #2088 (PR #2086). The owner's direction, 2026-11-07: "we need synthetic more aligned to real", toward realistic
bright stars.

**The gap.** Real-star check 3 (`training/star-removal/real_star_check3.py`) scores R3l on the val masters' own stars,
with a reference ring sized per star, a plane fitted on the output's ring, per-channel noise and neighbours masked. On the
truth twin it reads a perfect output 97-99 % clean and agrees with the truth 92-97 %. R3l s0 / s1 read HIP 80609's
saturated stars at 63.6 / 68.2 % clean and M42's 100-1000 sigma stars at 27.2 / 39.0 %, where the twin reads synthetic
bright stars at about 93 %. The difference is the training data's.

**Two causes, measured.**
- **Too few saturated stars.** The masters carry 2.45 saturated stars per 256 px tile over the 136 training sessions, and
  synth18 drew 0.25, a tenth (`training/star-removal/saturated_per_tile.py`). The old injector added at most one per draw,
  a quarter of the time. Both numbers are the first counter's, which skipped two of the 138 sessions and counted the stars
  centred in a drawn cell's margin (13 % more area, on the drawn side only; "R3r's Q1" below); they were not read again with
  the fixed counter, and a tenth is the size of the gap either way.
- **The wrong halo.** A saturated star's catalogued amplitude is a wing fit the clip inflates, and its halo does not scale
  with it: per amplitude, the faintest third of M42's saturated stars carry nine times the brightest third's light at 5 px.
  A per-amplitude table (`--bright-halos`) overshot and made the unsaturated bright stars less realistic, and was dropped.

**The options** (`dataset degrade --mode stars`, both off by default):
- `--saturated-own-share` draws each site saturated at the master's own share of saturated catalogue sites.
- `--saturated-measured` draws every saturated star the plate builder subtracted as the master shows it
  (`InjectionPopulation.MeasureSaturated`). It is read off the master less its plate, at the amplitude matching its own
  light 3 to 5 px out, with its own ring residual to 30 px as its halo. It is clipped only in the channels its light says it
  clipped, and drawn on the round profile it was measured against, whatever the cell draws for its other stars.

**The PR #2086 review** found three faults in the first version, all fixed before R3r: the halo was laid under the
cell's shaped profile (a drawn beta of 1.3 to 6 on 30 % of cells counted its wings twice); every channel was clipped at its
core, flat-topping channels that never clipped; and the halo was read off the master less a ring median, so the nebula
under a star became its halo.

**The probe** (`MeasuredSaturatedRenderProbe`, on demand) plans 1,500 saturated stars per val master with the training
recipe's beta draw, renders each alone and reads its absolute light in ring medians against the master's own saturated
stars, master less plate. At the fixed injector:

| master | 5 px | 8 px | 12 px | measured channels kept clipped |
|---|---|---|---|---|
| Lobster | 1.13 | 0.95 | 0.97 | 89 % |
| HIP 80609 | 0.97 | 1.04 | 1.00 | 93 % |
| M42 | 1.09 | 1.05 | 0.95 | 84 % |
| SMC (22 real stars) | 0.93 | 0.93 | 0.88 | 94 % |

Stars that fall back to the catalogued entry carry about half the measured stars' light; on SMC they are 15 % of the drawn
saturated stars.

**R3q, read 2026-11-10: killed.** synth20 = synth18's export with the two options, at the injector BEFORE the review's
fixes; R3l's recipe, seed 0. Without the options its binary reproduced synth18 byte for byte (160 of 160 injection rows,
40 of 40 tiles compared), and the first chunk drew 1.18x the masters' saturated stars per tile. Against R3l s0 / s1:

| bar | R3q | R3l s0 / s1 | verdict |
|---|---|---|---|
| HIP 80609 saturated >= 80 | 81.8 (input ring 36.4) | 63.6 / 68.2 | no evidence: n = 22, the ring variants disagree by 45 points |
| M42 100-1000 sigma >= 50 | 29.4 | 27.2 / 39.0 | missed |
| Lobster saturated within 3 of s0 | 94.8 | 100 / 94.8 | missed |
| truth tiles 20-100 >= 72, 100-1000 >= 54 | 66.9, 47.9 | 73.3, 54.5 | missed |
| Andromeda dug <= 47 | 31.7 | 44.8 / 45.3 | held, better |
| Tarantula dark flat >= 69 | 68.4 | 70.3 / 74.4 | missed by 0.6 |

Not barred, a regression: SMC's 20-100 sigma stars 45.2 % clean (94.9 / 89.8) and 100-1000 sigma 32.2 % (96.6 / 84.7),
pooled 20-100 ring left 18.6 % (2.3 / 3.5). R3q leaves faint and mid stars it used to take. The M31 nucleus was kept at
77-83 % (R3l 2-16 %). It reads as a test of that export, faults included.

**R3r, pre-registered 2026-11-10.** R3q again on the fixed injector: synth21 = the recipe in
`training/star-removal/export-synth.ps1` with `--saturated-own-share --saturated-measured`, its binary (`bh-bin6`) built at
PR #2086's commit "docs(star-remover): R3q's read and R3r pre-registered, with the export, check 3 and the per-tile count in the
repo (#2088)", and R3l's training exactly: `training/denoise/run-r2.ps1 -Tag r3r
-Arms synth21 -SeedCount 1 -FirstSeed 0 -Patience 24 -Steps 480000 -CheckpointEvery 2000` (its flux loss off, as R3l's).
Scored by `training/star-removal/score-arm.ps1` against R3l's baselines, `score_whole.py` over the held-out whole masters,
and `real_star_check3.py --sessions` on both ring variants.
- **Q1, the export, before training:** saturated stars per tile within 25 % of the masters', pooled over the 138 sessions
  and per session at the median.
- **Q2, check 3 against R3l s0 / s1:** HIP 80609 saturated at least 80 on BOTH ring variants (R3q passed one and failed
  the other); M42 100-1000 sigma at least 50; Lobster every band within 3 points of s0; and, from R3q's regression, SMC's
  20-100 and 100-1000 sigma bands not below R3l s1 by more than 5 points.
- **Q3, truth tiles (on-random):** 20-100 sigma at least 72, 100-1000 at least 54.
- **Q4, whole masters:** Andromeda dark dug at most 47, Tarantula dark flat at least 69.
- **Kill:** Q1 fails; or SMC's regression returns (the faults were not its cause); or HIP's saturated stars do not gain 8
  points over R3l's mean (66) on both rings while Q3 holds.
- **Prediction:** Q1 holds; HIP gains; SMC's regression goes, since its saturated stars drew a doubled wing on a third of
  cells and nebula as halo; M42's 100-1000 sigma band gains little, those stars being mostly unsaturated, which this arm
  does not touch. If M42 is the only miss, the next arm is the unsaturated bright class measured the same way.

**R3r's Q1, read 2026-11-11: holds, once the counter counted the tile.** As first run, `saturated_per_tile.py` read 1.27
pooled (1.31 per session at the median) and R3r was briefly stopped as killed. The counter was wrong in two ways:
- **It counted the margin.** A cell is drawn with about 8 px of margin about it, and a row lists the stars centred there
  too, which is 13 % more area than the tile.
- **It matched session ids as raw text.** It skipped the two sessions whose ids the writer escapes (a '+' as `+`).

Counting only stars centred inside the tile, over all 138 sessions, Q1 reads **1.12 pooled and 1.15 at the median**,
inside the bar, which did not move. What is left of the excess is mostly the recipe's own `--saturated-fraction` 0.25
extra star, which R3l drew too. The rest is the frame's saturated share applied to steered cells that hold a lower share
than the frame: 1.15 in the same cells. Training was never interrupted, and Q2 to Q4 are read as pre-registered.

**R3r, read 2026-11-11: fails its bars.** Trained to step 174,500 (best held-out 6.95e-6 at 150,500, converged after
the last halving). Against R3l s0 / s1, check 3 on its own ring and on the input's ring:

| bar | R3r own / input ring | R3l s0 / s1 own (input ring) | verdict |
|---|---|---|---|
| Q2 HIP 80609 saturated >= 80 on both | 72.7 / 54.5 | 63.6 / 68.2 (63.6 / 63.6) | missed |
| Q2 M42 100-1000 sigma >= 50 | 43.4 / 40.4 | 27.2 / 39.0 | missed, though the best yet |
| Q2 Lobster every band within 3 of s0 | saturated 94.8 / 84.5 | 100 / 94.8 (94.8 / 94.8) | missed: 3 of 58 stars, and 10 points on the input ring |
| Q2 SMC 20-100, 100-1000 not 5 below s1 | 97.7, 86.4 / 98.3, 93.2 | s1 89.8, 84.7 | held: the regression is gone |
| Q3 truth tiles 20-100 >= 72, 100-1000 >= 54 | 72.6, 52.8 | 73.3, 54.5 | 100-1000 missed by 1.2 |
| Q4 Andromeda dark dug <= 47 | 44.2 | 44.8 / 45.3 | held |
| Q4 Tarantula dark flat >= 69 | 68.7 | 70.3 / 74.4 | missed by 0.3 |

The kill on HIP fires: its saturated stars gain 6.7 points over R3l's mean on the own ring and lose 11.5 on the input's,
where the bar was 8 on both. The predictions on SMC (gone) and M42 (little gain) held; the one on HIP did not.

**What moved instead is the dig.** More saturated stars in training made the model take MORE light from saturated stars,
not less: check 3's pooled saturated "ring dug" is 54.5 % own and 63.5 % input ring (R3l 21 to 30 % and 56 to 57 %), and on
the truth tiles saturated stars are 53.6 % clean and 35.1 % dug (R3l s0 60.8 % and 24.2 %). The 100-1000 sigma band's
dug share on the truth tiles rose 1.4 points with it. The M31 nucleus was kept at 36 to 38 % (R3l 2 to 16 %, R3q 77 to
83 %). The first hypothesis, that the measured halo is drawn larger or brighter than the masters' stars hold, was then
measured and is wrong:
- **The dig is in the core, not the halo.** Per channel, output less reference in sigma, median over saturated stars:
  within 4.5 px R3r reads -0.92 / -1.19 / -0.75 on the real masters (reference: the input's 19-22 px plane), against R3l s0's
  -0.78 / -1.04 / -0.51 and SXT's -0.86 / -0.88 / -0.34; at 6-12 px the three agree within 0.1. On the truth tiles
  (reference: the truth) R3r reads 0.00 / -0.19 / -0.19 in the core against R3l s0's -0.01 / -0.20 / -0.11, the halo
  -0.02 to -0.04 for both. So R3r takes a fifth of a sigma more under a saturated core, most in blue.
- **The plates are not dug under the saturated stars the measurement reads.** Over 1,914 subtracted saturated stars in 24
  training plates (2026-10-12 bake), the plate's core within 4.5 px stands +0.09 / +0.15 / +0.05 sigma over its 19-22 px
  ring, 6-12 px +0.04 / +0.05 / +0.03: a measured star carries slightly less light than the master, not more.

Neither explains a core dig, which is under a clipped plateau, where the output can only guess the sky. What the saturated
cells teach about the sky under a clipped core is the next thing to read, in the injector's code before any measurement.
Read: nothing there biases it. A plateau clips at the larger of the star's level and the plate (`StarInjection.RenderStacked`)
and the target is the plate, so a clipped core teaches only what the input shows. And R3l's own two seeds differ by 0.3 to
0.5 sigma in HIP's saturated cores, more than R3r's shift, so one seed cannot say the dig is the data's.

**R3r seed 1, pre-registered 2026-11-11.** R3r exactly, `-FirstSeed 1`, scored the same way.
- **Seed noise:** s1's pooled saturated ring dug (own ring) at most 35 % (R3l 21 to 30 %). Then s0's dig was the seed, and
  R3r's bars are read again on the two seeds' mean.
- **The data's:** s1's saturated ring dug at least 45 %. Then R3r stays killed, and the next place to look is the measured
  stars' unclipped channels (a blue core drawn on the round profile from its 3-5 px light), measured before any arm.
- Between the two, R3r stays killed and the dig is called unresolved.

**R3r seed 1, read 2026-11-11: the dig was the seed's; on the two seeds' mean R3r still fails.** Trained to step 217,500
(best held-out 6.61e-6 at 193,500, converged after the last halving). Its pooled saturated ring dug is **13.6 %** on its
own ring (s0 54.5 %, R3l 21.1 / 30.0 %), so the seed-noise branch holds, and R3r's bars are read on the mean of s0 and s1:

| bar | s0 / s1 own (input ring) | two-seed mean own / input | R3l s0 / s1 own | verdict |
|---|---|---|---|---|
| Q2 HIP 80609 saturated >= 80 on both | 72.7 / 90.9 (54.5 / 72.7) | 81.8 / 63.6 | 63.6 / 68.2 | missed on the input ring |
| Q2 M42 100-1000 sigma >= 50 | 43.4 / 26.5 (40.4 / 22.8) | 35.0 / 31.6 | 27.2 / 39.0 | missed |
| Q2 Lobster every band within 3 of R3l s0 | saturated 94.8 / 100 (84.5 / 93.1) | 97.4 / 88.8 | 100 / 94.8 | missed on the input ring |
| Q2 SMC 20-100, 100-1000 not 5 below R3l s1 | 97.7, 86.4 / 97.2, 89.8 | 97.5, 88.1 | s1 89.8, 84.7 | held |
| Q3 truth tiles 20-100 >= 72, 100-1000 >= 54 | 72.6, 52.8 / 72.5, 53.4 | 72.6, 53.1 | 73.3, 54.5 / 73.2, 58.2 | 100-1000 missed by 0.9 |
| Q4 Andromeda dark dug <= 47 | 44.2 / 31.4 | 37.8 | 44.8 / 45.3 | held |
| Q4 Tarantula dark flat >= 69 | 68.7 / 76.3 | 72.5 | 70.3 / 74.4 | held |

The HIP kill fires on the mean too: its saturated stars gain 15.8 points over R3l's mean on the own ring and none on the
input ring (63.6 against R3l's 63.6). So R3r stays killed, now for misses that do not depend on one seed: M42's bright
stars, HIP and Lobster's saturated stars on the input ring, and the truth tiles' 100-1000 sigma band.

**What the two seeds say about the instrument.** One seed against the other moved the pooled saturated dig by 41 points,
HIP's saturated clean share by 18 (n = 22) and M42's 100-1000 sigma band by 17 (n = 136); R3l's seeds moved them 9, 5 and
12. On the truth twin (check 3's `--twin`) the four models' saturated stars read 85 to 89 % truly clean with no order
between the arms. A single-seed difference in the saturated or M42 rows of check 3 is therefore not evidence, and an arm
that is to move them needs two seeds before it is read. The rows with many stars (Lobster, HIP below saturation, the truth
tiles' bands, the whole masters) moved by 1 to 8 points between seeds. The M31 nucleus was kept at -4 / +17 / +27 % (s0
36 to 38 %, R3l 2 to 16 %).

Both seeds trained from the frozen run copy R3l used (`run-r2.ps1` and `n2n_smoke.py` as of "feat(training):
--oversample-sessions draws a listed set of sessions' cells more often (#1947)"), which is the repo's trainer less the
later `--flux-loss` option that R3r runs with off; its log's commit field is blank because that copy is outside the repo.

#### M42's bright stars: the channels' wings, outside the training range (2026-11-11)

M42's 100-1000 sigma stars are the one miss every arm shares (R3l, R3q, R3r), and in check 3 they fail as DUG (52 to 78 %,
SXT 7 %), on M42 alone: HIP, Lobster and SMC read about 99 % clean in that band. Measured on the real val tiles, output
less the input's 19-22 px plane, per channel, median over the band's unsaturated stars (226 on M42):
- **The dig is blue, at 2 to 3 px.** Blue reads -2.7 and -1.7 sigma at 2 and 3 px for R3r s0 and R3l s0 alike, where SXT
  reads -0.5 and 0.0; at 6 to 12 px every model agrees with SXT within 0.1. It is the core's shape, not the halo or the
  nebula under it.
- **No colour plane is offset** (centroids within 0.1 px of green on M42; the SMC's red, 0.26 px off, is the filter's).
- **M42's channels differ in their wings.** Its field PSF fits beta 3.06 / 1.72 / 4.54 (R / G / B): heavy green wings,
  compact blue, a blue-over-green beta ratio of **2.64**, which the real stars bear out (at 2 px green holds 0.23 of its peak,
  blue 0.16). The sessions that pass sit at 0.8 to 1.5 (HIP 80609 0.94 to 1.12, Lobster 0.78 to 1.46, SMC 0.94 to 1.11).
- **Training holds almost none of it.** synth21's drawn stars have a blue-over-green beta of 1.00 at the median and 1.35 at
  the 90th percentile; 1.2 % reach 2.4, and 5 of its 123 sessions reach 2 at the median. `--star-beta-draw` itself draws
  every channel of a held cell at ONE beta, so 30 % of the cells teach a ratio of exactly 1.

Measured by `training/star-removal/bright_channel_profile.py` and `channel_offset.py`.

So the model learned that blue's wing follows green's, and on M42 it takes a green-shaped wing out of a compact blue star.
The deploy point is outside the training range, the case `--star-beta-draw` was built for in one dimension and not in the
channels' relation. Every arm that widened star shapes took M31's nucleus (R2l, R2m, R2s, R2s2, R2v), so the arm that
draws the channels' betas apart is read against the nucleus guard as well as M42.

**R3s, pre-registered 2026-11-11.** One change from R3l: `--star-beta-draw 0.3,1.3,6,3`, whose fourth value draws a held
cell's red and blue betas each at a factor of its green's, log-uniform in [1/3, 3] (`StarBetaDraw.ChannelSpread`). The cells
the draw holds, their green beta and every other number are the draw R3l made: on one ASI533MC session's control export, 20
of 24 rows and their tiles were byte-identical and the 4 held rows differed only in red and blue beta.
- **The export:** synth22 = `training/star-removal/export-synth.ps1 -Name synth22 -BetaDraw 0.3,1.3,6,3` with no `-Extra`,
  its binary built at "feat(training): --star-beta-draw takes a channel spread, drawing a held cell's red and blue betas
  apart from green's" (the export's folder holds HEAD.txt), into `D:/temp/tianwen-scratch/r3-train-synth22`.
- **The training:** R3l's exactly, both seeds, from the same frozen trainer copy R3l and R3r ran (`run-r2.ps1 -Tag r3s
  -Arms synth22 -SeedCount 2 -FirstSeed 0 -Patience 24 -Steps 480000 -CheckpointEvery 2000`), so R3l s0 / s1 are the
  baseline with nothing else different.
- **Scored** by `score-arm.ps1`, `score_whole.py`, `real_star_check3.py --sessions` on both rings and
  `bright_channel_profile.py`, every bar on the two seeds' mean against R3l's.

The bars:
- **Q1, the export, before training:** 3 to 5.5 % of synth22's unsaturated stars drawn with blue at 2.4 times green's beta
  or more (`channel_beta_share.py`; synth21 1.2 %, all of it from the 70 % of cells the draw does not hold, so about 1.2 %
  plus 0.3 x 10 % = 4.2 % expected).
- **Q2, M42:** its 100-1000 sigma stars at least 50 % clean on the own ring (R3l 27.2 / 39.0, mean 33.1); its blue at 2 to
  3 px within 0.7 sigma of SXT's in `bright_channel_profile.py` (R3l s0 -2.7 / -1.7 against SXT -0.5 / 0.0).
- **Guards:** HIP 80609, Lobster and SMC at 20-100 and 100-1000 sigma no more than 3 points below R3l's mean on the own ring;
  truth tiles 20-100 at least 72 and 100-1000 at least 54; Andromeda dark dug at most 47 and Tarantula dark flat at least 69;
  the M31 nucleus at 0-6 px under 40 % in every channel on both seeds (R3l +1 to +14 %, R3r 17 to 38 %, the arms that
  took it 70 to 80 %).
- **Kill:** Q1 fails; or M42 under 40 on the mean; or a guard fails.
- **Prediction:** M42's blue dig falls toward SXT's and its band clears 50; the other sessions, whose channels' betas sit
  at 0.8 to 1.5, do not move beyond their seed spread; the saturated rows are not read (one seed's spread there is 18 to 41
  points). The nucleus is the risk.

**R3s, read 2026-11-12: killed by two guards; M42's band clears its bar and the nucleus is kept.** Both seeds trained to
convergence (s0 step 224,000, best held-out 6.61e-6 at 212,000; s1 best 6.54e-6 at 219,500). Every bar on the two seeds' mean:

| bar | R3s s0 / s1 | R3s mean | R3l s0 / s1 (mean) | verdict |
|---|---|---|---|---|
| Q1 blue at 2.4 times green's beta, 3 to 5.5 % of stars | | 4.12 % | synth21 1.2 % | held |
| Q2 M42 100-1000 sigma clean, own ring, >= 50 | 58.1 / 52.9 | 55.5 | 27.2 / 39.0 (33.1) | held |
| Q2 M42 blue at 2 and 3 px within 0.7 sigma of SXT's (-0.47, -0.03) | -2.31, -1.10 / -2.26, -1.14 | -2.29, -1.12 | -2.68, -1.70 / -2.99, -1.79 | missed, by 1.8 and 1.1 |
| guard HIP 80609 20-100, 100-1000, within 3 of R3l | 99.7, 98.4 / 99.9, 98.2 | 99.8, 98.3 | 99.8, 97.4 | held |
| guard Lobster 20-100, 100-1000 | 99.8, 99.4 / 99.8, 99.6 | 99.8, 99.5 | 99.8, 99.6 | held |
| guard SMC 20-100, 100-1000 | 83.6, 88.1 / 79.1, 83.1 | 81.4, 85.6 | 92.4, 90.7 | **failed, 11.0 and 5.1 below** |
| guard truth tiles 20-100 >= 72, 100-1000 >= 54 | 71.5, 53.2 / 72.6, 55.0 | 72.1, 54.1 | 73.3, 56.4 | held, by 0.1 |
| guard Andromeda dark dug <= 47 | 25.9 / 24.7 | 25.3 | 44.8 / 45.3 | held |
| guard Tarantula dark flat >= 69 | 69.3 / 65.1 | 67.2 | 70.3 / 74.4 | **failed** |
| guard M31 nucleus under 40 % in every channel, both seeds | -1.1, 2.3, 2.2 / 0.6, 1.9, 1.3 % | | +1 to +14 % | held |

- **The kill fires on the guards:** the SMC's stars at 20-100 and 100-1000 sigma, and Tarantula's dark sky. M42 is not
  under 40, and Q1 held.
- **The SMC fails as stars LEFT, not dug** (dug 0.0 and 0.0 to 1.7 %): its 20-100 sigma stars read 83.6 / 79.1 % clean where
  R3l's read 94.9 / 89.8. R3l's own seeds differ by 5 points there and by 12 at 100-1000 sigma (n = 59), so the 100-1000 row's
  5.1 is inside one seed's spread and the 20-100 row's 11.0 is not. The SMC is the dual-band session whose red sits 0.26 px
  off green by its filter; a model taught that the channels' wings may differ leaves more of such a star.
- **The prediction, in three parts.** M42's band clears 50, as predicted. Its blue dig falls toward SXT's at 3 px (-1.75 to
  -1.12) and barely at 2 px (-2.84 to -2.29), short of the bar: the channels' betas drawn apart are part of M42's dig, not
  all of it. "The other sessions do not move beyond their seed spread" failed on the SMC. The nucleus, the stated risk, was
  kept better than R3l kept it.
- **Two gains outside the bars:** Andromeda's dark sky is dug on 25.3 % where R3l dug 45.1, and M42's bright stars are dug on
  39.0 % (both seeds) where R3l dug 58 to 71. On the input ring M42 reads 48.5 / 47.1 (R3l 19.9 / 33.8).
- The saturated rows are not read, as registered.

Read from `C:/temp/e2/check3-r3s-own.txt`, `check3-r3s-input-ring.txt`, `bright-channel-r3s.txt`, `whole-r3s_synth22_s*.log`,
`q1-r3s.txt` and `r2-eval/<model>/on-random/starless-eval-v5.json` on DE1. Not adopted; what follows is the owner's to choose.

**The four models averaged, read after the fact (2026-11-13): every guard holds, both M42 bars are missed.** The saved
outputs of R3l s0 / s1 and R3s s0 / s1 averaged (`ens_mix4`; the whole masters through `score_whole.py --model` with the four
graphs). Not pre-registered, so it adopts nothing:

| bar | R3l mean | R3s mean | the four averaged |
|---|---|---|---|
| M42 100-1000 sigma clean, own ring, >= 50 | 33.1 | 55.5 | 47.1 |
| M42 blue at 2 and 3 px within 0.7 sigma of SXT's (-0.47, -0.03) | -2.68, -1.70 (s0) | -2.29, -1.12 | -2.53, -1.41 |
| SMC 20-100, 100-1000, within 3 of R3l | 92.4, 90.7 | 81.4, 85.6 | 93.2, 93.2 |
| truth tiles 20-100 >= 72, 100-1000 >= 54 | 73.3, 56.4 | 72.1, 54.1 | 74.8, 60.0 |
| Andromeda dark dug <= 47 | 45.1 | 25.3 | 36.2 |
| Tarantula dark flat >= 69 | 72.4 | 67.2 | 72.3 |
| M31 nucleus under 40 % in every channel | up to 15.8 % | up to 2.3 % | up to 7.8 % |

The SMC's miss in R3s is its GREEN wing left at 2 to 4 px (R3s +0.2 to +0.55 sigma where SXT reads -0.7 to -0.8; R3l already
leaves part of it), read by `bright_channel_profile.py` on the 20-100 sigma band. Averaging mends the guards and not M42's
blue dig, so M42 needs a trained arm. A 3-minute probe of the trainer on the 1070 (2026-11-13): 52.5 tiles a second at about
92 % GPU with the cache in memory, the rate of R3s's own run, so the card is the limit; read cold off the USB disk it was 18.

**R3t, pre-registered 2026-11-13 (the owner agreed the same day).** One change from R3s: the channel spread 2 where R3s
drew 3, `--star-beta-draw 0.3,1.3,6,2`, a held cell's red and blue betas each log-uniform in [1/2, 2] of its green's. The
question: does a narrower spread keep R3s's gain on M42 and leave the SMC's wings as R3l does.
- **The export:** synth23 = `export-synth.ps1 -Name synth23 -BetaDraw 0.3,1.3,6,2` with no `-Extra`, R3s's binary
  (`bh-bin7`, built at "feat(training): --star-beta-draw takes a channel spread, drawing a held cell's red and blue betas apart
  from green's"), into `D:/temp/tianwen-scratch/r3-train-synth23`.
- **The training:** R3s's exactly, from the same frozen trainer copy, **one seed first**: seed 0 is trained and scored
  alone; seed 1 is trained only if seed 0 holds every guard below and its M42 band reads at least 40. The cache is prepared
  on the SSD (`E:/tianwen-scratch`), which changes where the bytes are read from and nothing else.
- **Scored** as R3s was (`score-arm.ps1`, `score_whole.py`, `real_star_check3.py --sessions` on both rings,
  `bright_channel_profile.py`). With one seed the bars are read on seed 0 against R3l's mean and R3l's own seed spread is
  said beside every row; with two, on the mean.

The bars:
- **Q1, the export, before training:** blue at 1.6 times green's beta or more on 8 to 12 % of synth23's unsaturated stars
  (synth21, with no spread, 5.67 %, measured 2026-11-13; a held cell adds about 16 % of its stars), and at 2.4 times or
  more on 0.8 to 2.0 % (synth21 1.2 %; a spread of 2 draws nothing past 2).
- **Q2, M42:** as R3s's: its 100-1000 sigma stars at least 50 % clean on the own ring; its blue at 2 to 3 px within 0.7
  sigma of SXT's.
- **Guards:** as R3s's, every one: HIP 80609, Lobster and SMC at 20-100 and 100-1000 sigma no more than 3 points below
  R3l's mean on the own ring; truth tiles at least 72 and 54; Andromeda dark dug at most 47 and Tarantula dark flat at
  least 69; the M31 nucleus under 40 % in every channel.
- **Kill:** Q1 fails; or M42 under 40; or a guard fails. Tarantula's dark flat moves 4 points between R3l's and R3s's own
  seeds, so a seed 0 that fails on that row ALONE by under 4 points trains seed 1 and is read on the mean.
- **Prediction:** the SMC's two rows return to within 3 of R3l's. M42 lands between R3l's 33 and R3s's 55, and whether it
  clears 50 is the open part: M42's blue beta is 2.64 times its green's, outside a spread of 2, so the risk is that the arm
  gives M42's gain back. If it does, the read is that M42 needs the wide spread and the SMC's wing has to be mended another
  way.

**R3t's Q1, read 2026-11-14: holds on both bars.** `channel_beta_share.py` over synth23's 8,521,465 unsaturated RGB
stars in 123 sessions: **10.32 %** with blue at 1.6 times green's beta or more (bar 8 to 12 %) and **1.21 %** at 2.4
times or more (bar 0.8 to 2.0 %). Per star, blue over green's beta reads 0.72, 1.01, 1.61 and 4.64 at the 10th, 50th,
90th and 99th percentile. Seed 0's training started the same night on the SSD cache (4,840 cells, 19.5 GiB).

**R3t, read 2026-11-14: killed by the SMC's 100-1000 sigma guard on seed 0; M42's band keeps most of R3s's gain, its blue dig is given back.** Seed 0
trained to convergence (stopped at step 244,000, best held-out 6.49e-6 at 232,000). One seed, so every bar is read on seed 0
against R3l's mean, R3l's own seeds beside it:

| bar | R3t s0 | R3l s0 / s1 (mean) | R3s mean | verdict |
|---|---|---|---|---|
| Q1 blue at 1.6 and 2.4 times green's beta | 10.32 %, 1.21 % | | | held |
| Q2 M42 100-1000 sigma clean, own ring, >= 50 | 47.1 | 27.2 / 39.0 (33.1) | 55.5 | missed, by 2.9 (not under 40) |
| Q2 M42 blue at 2 and 3 px within 0.7 sigma of SXT's (-0.47, -0.03) | -2.88, -1.68 | -2.68, -1.70 / -2.99, -1.79 | -2.29, -1.12 | missed, by 2.4 and 1.7 |
| guard HIP 80609 20-100, 100-1000, within 3 of R3l | 99.7, 99.5 | 99.7, 97.7 / 99.9, 97.1 (99.8, 97.4) | 99.8, 98.3 | held |
| guard Lobster 20-100, 100-1000 | 99.8, 99.6 | 99.8, 99.5 / 99.8, 99.6 (99.8, 99.6) | 99.8, 99.5 | held |
| guard SMC 20-100, 100-1000 | 94.9, 83.1 | 94.9, 96.6 / 89.8, 84.7 (92.4, 90.7) | 81.4, 85.6 | 20-100 held; **100-1000 failed, 7.6 below** |
| guard truth tiles 20-100 >= 72, 100-1000 >= 54 | 72.0, 54.4 | (73.3, 56.4) | 72.1, 54.1 | held, at the bar |
| guard Andromeda dark dug <= 47 | 34.7 | 44.8 / 45.3 | 25.3 | held |
| guard Tarantula dark flat >= 69 | 72.1 | 70.3 / 74.4 | 67.2 | held |
| guard M31 nucleus under 40 % in every channel | 3.9, -0.1, 3.0 % | +1 to +14 % | up to 2.3 % | held |

- **The kill fires on one row, the SMC's 100-1000 sigma stars** (n = 59): 83.1 % clean against R3l's mean 90.7. R3l's own seeds
  read 96.6 and 84.7 there, so seed 0 sits 1.6 under R3l's lower seed; the registration reads it against the mean, and its
  one exception is Tarantula's row, so the arm is killed and seed 1 is not trained. Unlike R3s, whose SMC stars were LEFT, these
  are DUG (13.6 %, 8 stars; R3l 0.0 on both seeds).
- **The prediction held in part.** It said the SMC's two rows return to within 3 of R3l's mean: the 20-100 row did (94.9; R3s
  81.4) and the 100-1000 row did not (83.1 against 90.7, the row that kills the arm). Tarantula's dark sky returned too (72.1;
  R3s 67.2). M42 landed between R3l's 33 and R3s's 55, at 47.1, short of 50: its band keeps 14.0 of R3s's 22.4 points and its dug
  share about half (64.4, 39.0, 51.5), while its blue dig at 2 and 3 px is R3l's again (-2.88, -1.68), the stated risk.
- **The SMC's wing is not mended, it moved from green left to blue dug** (`bright-channel-r3t-s0.txt`): at 2 to 4 px R3t's blue
  reads -0.95, -1.04, -0.97 sigma where SXT reads -0.43, -0.44, -0.43 and R3l -0.39, -0.30, -0.10 (s0) and -0.56, -0.51, -0.24
  (s1). That, and not the n = 59 row alone, is why the SMC needs another fix. M42's blue beta is 2.64 times its green's, outside a
  spread of 2, which fits M42 needing the wide spread; that is read off one seed, and 47.1 sits 5.8 under R3s's lower seed, inside
  the 5 to 12 point seed spread this row has shown.
- **Outside the bars:** Andromeda's dark sky is dug on 34.7 % (R3l 45.1, R3s 25.3); M42's 100-1000 sigma stars are dug on 51.5 %
  (R3l 70.6 / 58.1, R3s 39.0).
- The saturated rows are not read, as registered.

Read on DE1 from `C:/temp/e2/check3-r3t-s0-own.txt` and `bright-channel-r3t-s0.txt`, and under `C:/temp/tianwen-scratch` from
`f2-whole-master/w-r3t_synth23_s0/row.txt`, `f2-reports-master/m31-nucleus-r3t_synth23_s0.txt` and
`f2-logs-master/score-r3t_synth23_s0/eval-on-random.log`. Check 3
and the channel profile were run by hand from the main checkout with the chain's own arguments, after the chain's copies failed
on a script folder that had gone. Not adopted; what follows is the owner's to choose.

**R3u, pre-registered 2026-11-14 (the owner chose "mixed" the same day).** One change from R3s and R3t: the channel spread is
3 for half the training set and 2 for the other half. The question: does a model that sees both spreads keep M42's band as
the spread of 3 did and the SMC's and Tarantula's guards as the spread of 2 did.
- **The training set, made with no new export:** `synth24` = `mix_caches.py` over the prepared caches of synth22 (spread 3)
  and synth23 (spread 2), seed 0. The two hold the same 4,840 cells in the same order (their `meta.json`, star tables and
  noise-plane flags are identical, and the tiles of the slots that hold no draw are identical); each cell's tiles and noise
  planes are copied whole from one of the two, exactly half the cells from each, the 320 held-out cells among them. Every
  cell of the mix is therefore a cell an export made, and a held cell's red and blue betas are log-uniform in [1/3, 3] of its
  green's in half the cells and in [1/2, 2] in the other half.
- **The training:** R3s's exactly, from the same frozen trainer copy (`C:/temp/e2/r3h-run`), on the SSD, **one seed first**
  as R3t: seed 1 is trained only if seed 0 holds every guard below and its M42 band reads at least 40.
- **Scored** as R3t was, from a frozen copy of `training/star-removal` outside the checkout and its worktrees.

The bars:
- **Q1, the mix, before training:** `mix.json` names 2,420 cells from each cache, and 40 cells drawn at random read back
  byte for byte equal to their named source and, where the two sources differ, unequal to the other.
- **Q2, M42:** as R3s's and R3t's: its 100-1000 sigma stars at least 50 % clean on the own ring; its blue at 2 to 3 px within
  0.7 sigma of SXT's.
- **Guards:** as R3t's, every one: HIP 80609, Lobster and SMC at 20-100 and 100-1000 sigma no more than 3 points below R3l's
  mean on the own ring; truth tiles at least 72 and 54; Andromeda dark dug at most 47 and Tarantula dark flat at least 69;
  the M31 nucleus under 40 % in every channel.
- **Kill:** Q1 fails; or M42 under 40; or a guard fails. As for R3t, a seed 0 that fails on Tarantula's row ALONE by under
  4 points trains seed 1 and is read on the mean.
- **Prediction:** the share of stars with blue at 2.4 times green's beta or more sits near 2.7 % (the mean of R3s's 4.12 and
  R3t's 1.21; not measured on the mix). M42's band lands between R3t's 47.1 and R3s's 55.5 and whether it clears 50 is open.
  M42's blue dig is missed again, since the spread of 3 alone missed it by 1.8 and 1.1 sigma. The risk is the SMC: R3s left
  its green wing and R3t dug its blue one, so a mix may do a part of each and fail the 100-1000 row a third time, and then
  the read is that the channel spread is not the lever for the SMC at any setting and this line stops.

**R3u, read 2026-11-15: killed on seed 0 by the SMC's two rows, the truth tiles and Tarantula; M42's band clears its bar; the
channel spread line stops.** Q1 held (2,420 cells from each cache; 40 cells read back equal to their named source in both of a
cell's arrays, `tiles.f16` and `sigma.f16`, and, on the 48 of those 80 arrays where the two sources differ, unequal to the other). Seed 0
converged at step 224,000 (best held-out 6.85e-6 at 212,000, on its own held-out cells, which differ from R3s's and R3t's
by their draws, so the three objectives are not compared). Every bar on seed 0:

| bar | R3u s0 | R3l s0 / s1 (mean) | R3s mean | R3t s0 | verdict |
|---|---|---|---|---|---|
| Q2 M42 100-1000 sigma clean, own ring, >= 50 | 53.7 | 27.2 / 39.0 (33.1) | 55.5 | 47.1 | held |
| Q2 M42 blue at 2 and 3 px within 0.7 sigma of SXT's (-0.47, -0.03) | -2.47, -1.10 | -2.68, -1.70 / -2.99, -1.79 | -2.29, -1.12 | -2.88, -1.68 | missed, by 2.0 and 1.1 |
| guard HIP 80609 20-100, 100-1000, within 3 of R3l | 99.7, 98.0 | (99.8, 97.4) | 99.8, 98.3 | 99.7, 99.5 | held |
| guard Lobster 20-100, 100-1000 | 99.8, 99.4 | (99.8, 99.6) | 99.8, 99.5 | 99.8, 99.6 | held |
| guard SMC 20-100, 100-1000 | 87.6, 76.3 | 94.9, 96.6 / 89.8, 84.7 (92.4, 90.7) | 81.4, 85.6 | 94.9, 83.1 | **failed, 4.8 and 14.4 below** |
| guard truth tiles 20-100 >= 72, 100-1000 >= 54 | 71.0, 49.9 | (73.3, 56.4) | 72.1, 54.1 | 72.0, 54.4 | **failed, by 1.0 and 4.1** |
| guard Andromeda dark dug <= 47 | 29.5 | (45.1) | 25.3 | 34.7 | held |
| guard Tarantula dark flat >= 69 | 65.0 | 70.3 / 74.4 | 67.2 | 72.1 | **failed, by 4.0** |
| guard M31 nucleus under 40 % in every channel | -6.8, 3.1, 2.7 % | +1 to +14 % | up to 2.3 % | 3.9, -0.1, 3.0 % | held |

- **The kill fires on three guards at once**, so Tarantula's row is not alone and seed 1 is not trained (its 4.0 is also not
  under the 4 points the rule allowed).
- **By the star shares the SMC fails as stars left, as under the spread of 3:** dug 0.0 % at 20-100 sigma and 3.4 % at
  100-1000 (n = 59, where R3l's own seeds differ by 12 points; 76.3 is 8.4 under R3l's lower seed). The 20-100 row's 4.8 sits
  at R3l's own seed spread of 5.
- **By the wing profile the SMC's blue is dug and its red and green are left** (`bright-channel-r3u-s0.txt`, the 100-1000
  sigma stars, at 2 to 4 px): blue -0.92, -0.80, -0.94 sigma where SXT reads -0.43, -0.44, -0.43; red +0.32, +0.39, -0.33
  against SXT's -0.48, -0.42, -0.63; green -0.52, +0.23, -0.05 against -0.89, -0.85, -0.79. Every spread setting digs the
  SMC's blue wing (R3s -0.56, -0.75, -0.97 and -0.79, -0.76, -0.71; R3t -0.95, -1.04, -0.97) and R3l does not (-0.39, -0.30,
  -0.10 and -0.56, -0.51, -0.24).
- **The truth tiles fail on both rows; the gaps to each comparator are below.** R3s held on its
  two-seed mean (72.1, 54.1) and its seed 0 alone read 71.5 and 53.2, under both bars; R3t's seed 0 read 72.0 and 54.4; R3l's
  seeds read 73.3 and 73.2 at 20-100 sigma and 54.5 and 58.2 at 100-1000. R3u's 20-100 row (71.0) is 0.5 under R3s's seed 0,
  1.0 under R3t's and 2.2 under R3l's lower seed, whose seeds differ there by 0.1. Its 100-1000 row (49.9) is 3.3 under R3s's
  seed 0, 4.5 under R3t's and 4.6 under R3l's lower seed, whose seeds differ there by 3.7: past that spread against R3t and
  R3l, inside it against R3s.
- **The prediction's three read clauses held; its fourth was not read.** M42's band landed between R3t's and R3s's and clears
  50 (53.7); its blue dig is missed again, at the spread of 3's own size (-2.47, -1.10 against R3s's -2.29, -1.12); the SMC,
  the stated risk, failed a third time. The share of stars with blue at 2.4 times green's beta (predicted near 2.7 %) was
  registered as not measured on the mix and has not been measured since. Outside the prediction, which said nothing of either:
  the truth tiles failed on both rows and Tarantula failed too, so the mix did not keep the spread of 2's guards, it kept the spread of 3's losses.
- **Outside the bars:** Andromeda's dark sky is dug on 29.5 %; M42's 100-1000 sigma stars are dug on 44.1 % (R3l 70.6 / 58.1,
  R3s 39.0, R3t 51.5). The saturated rows are not read, as registered.
- **The line stops, as registered:** three settings of the channel spread (3, 2, and the two mixed) each clear or nearly clear
  M42's band and each fail the SMC. The channel spread moves M42 and is not the lever for the SMC; M42's blue dig is not
  mended by it at any setting.

Read on DE1 from `C:/temp/e2/check3-r3u-s0-own.txt`, `bright-channel-r3u-s0.txt`, `q1-r3u.txt` and `r3u.log` (the
convergence), and under
`C:/temp/tianwen-scratch` from `f2-whole-master/w-r3u_synth24_s0/row.txt`, `f2-reports-master/m31-nucleus-r3u_synth24_s0.txt`
and `f2-logs-master/score-r3u_synth24_s0/eval-on-random.log`; the weights are `E:/tianwen-scratch/r2-synth24/r3u_synth24_s0.pt`.
Not adopted; what follows is the owner's to choose.

**R3v, pre-registered 2026-11-15 (the owner chose a new idea the same day and named the judges' size).** One change from R3s:
the model. R3s's data (synth22, the channel spread 3) and recipe exactly, on R2nc's four-level U-Net (`--levels 4 --max-ch
256`, 6.86 M weights against 0.81 M, with `--clip-grad 8`, which trained R2nc cleanly). The question: is the small model the
reason one behaviour for a star's wings cannot serve M42 and the SMC together.
- **Why this and not another setting:** three settings of the channel spread each moved M42 and each failed the SMC (R3s, R3t,
  R3u). SXT's weights file is 66.5 MB (about 16 M weights) and StarNet2's 131.5 MB (about 33 M); ours is the smallest by a
  factor of 20. R2nc, the same network on synth4 (2026-10-20), gained on faint stars and not on bright ones, so a gain here is
  not assumed. Two four-level arms already carry SMC rows, on another measure than the own-ring bands (97 stars, flat): R2t
  read 93.8 on one seed, and R3a, R2t's recipe on the master's stretch, read 62.9 on seed 0 and 91.8 on seed 1. So a
  four-level net can keep the SMC and does not do so on every seed; neither arm drew its channels apart, which is what fails
  the SMC here. The plan records the same of every recipe ("The SMC read splits by seed"), and R3l's own seeds differ on the
  own-ring rows by 5.1 (20-100) and 11.9 (100-1000).
- **The training:** the frozen trainer copy (`C:/temp/e2/r3h-run`), synth22's prepared cache where R3s trained on it
  (`D:/temp/tianwen-scratch/r2-synth22`), **one seed first**. A probe on the 1070
  (2026-11-15) read 35.7 tiles a second against the small model's 52.5, about 14 hours at R3s's step count.
- **Scored** as R3u was, from the frozen copy `C:/temp/e2/r3u-src`, with R3s's two seeds and R3l's as the columns beside it.

The bars, read on seed 0 against R3s's two seeds (the same data, the small model):
- **Q2, M42:** its 100-1000 sigma stars at least 50 % clean on the own ring (R3s 58.1 / 52.9); its blue at 2 to 3 px within
  0.7 sigma of SXT's (-0.47, -0.03; R3s -2.29, -1.12).
- **Guards:** as R3s's, every one: HIP 80609, Lobster and SMC at 20-100 and 100-1000 sigma no more than 3 points below R3l's
  mean on the own ring (the SMC 92.4 and 90.7; R3s 81.4 and 85.6); truth tiles at least 72 and 54; Andromeda dark dug at most
  47 and Tarantula dark flat at least 69 (R3s 69.3 / 65.1); the M31 nucleus under 40 % in every channel.
- **Kill:** M42 under 40 on seed 0, as for R3t and R3u (R3s's was on its two-seed mean; 50 is the Q2 bar, not the kill, and
  this row has shown a seed spread of 5 to 12 points); or a guard fails.
- **When seed 1 trains:** when seed 0 is not killed (every guard held and M42 at 40 or more, whether or not it clears 50);
  or when it is killed on Tarantula's row ALONE by under 4 points; or on the SMC's rows ALONE with each no more than 3 under
  R3l's LOWER seed, the 20-100 row at 86.8 or more (89.8 less 3) and the 100-1000 row at 81.7 or more (84.7 less 3), which
  is how R3u's read compared this row (R3u's 76.3 is 8.4 under the lower seed, outside it). This bound is looser than R3t's
  kill, which read the row against R3l's mean: R3t's seed 0 (94.9 / 83.1, both above 86.8 / 81.7) would have passed it and
  trained seed 1. Any other kill ends the arm on seed 0.
- **With two seeds, every bar is judged on their mean:** Q2 and each guard as written above, and the arm is killed on the
  mean by M42 under 40 or by any guard, whatever seed 0 read alone. The M31 nucleus is the exception, as for R3s: under 40 %
  in every channel on EACH seed, since a mean hides one seed that takes it.
- **What a kill means:** on a two-seed mean whose SMC rows, either one, stay more than 3 under R3l's with 8.5 times the
  weights, capacity at this size is not shown to be what holds the two fields apart. A mean killed by another guard with the
  SMC's rows held says the larger model keeps the SMC and loses elsewhere, which is read as it falls. A mean killed by M42
  under 40 says the larger model gives back the gain the channel spread bought on M42, and then capacity is not the direction
  for either field. On a kill that ends the
  arm on seed 0 (M42 under 40, another guard, or the SMC past its bound) nothing is concluded about capacity from one seed.
  In every case the line waits for the owner; the owner's second idea of the same day (more than one model, chosen per image by a measurement of its
  stars) is the candidate named, and it is not started unasked.
- **Amended 2026-11-15, before any score of R3v exists** (the first text was written with seed 0 at step 55,600 of its training; the seed 1 rule, the SMC
  bound and the mean rule were added later the same day, still before any score): the first registration
  had four defects, each named for what it was. Two were at odds with the plan's own seed spreads: it killed on M42 under
  50, and it drew the capacity conclusion from one seed of the SMC. One was arithmetic: "seven times the weights" for 6.86 M
  against 0.81 M. One was an internal contradiction: seed 1 trained "only if seed 0 holds every guard and its M42 band reads
  at least 50", against the Tarantula exception beside it. The lines above are the corrected ones; the reviews of the
  correction added the positive rule for seed 1, the SMC exception's bound (first set at R3l's mean less its full seed range,
  84.3 and 75.8, then tightened to R3l's lower seed less 3) and the rule that two seeds are judged on their mean. A last
  review, the same day and still before any score, added two things: to that rule its one exception, the M31 nucleus,
  judged on each seed as R3s's was; and to "What a kill means" its reading of a mean killed by M42 under 40.
- **Prediction:** the truth tiles rise (more weights fit the synthetic stars better; R2nc's did). M42's band holds at R3s's
  level, 50 to 60. The SMC is the open part and the point of the arm: I put it at even odds that its 20-100 row comes within
  3 of R3l's. M42's blue dig is missed again; nothing in a larger model addresses it.

**R3v's seed 0, read 2026-11-15: not killed, the first arm of this line to hold every guard; seed 1 trains.** It stopped
on its plateau at step 235,000 (best held-out 5.85e-6 at 211,000; the small model on the same data, R3s, ended at 6.61e-6 on seed 0 and 6.54e-6 on seed 1, so R3v is 11.5 percent
below R3s's seed 0),
after 13 hours on the 1070.

| bar | registered | R3v s0 | R3s s0 / s1 | R3l s0 / s1 | SXT | |
|---|---|---|---|---|---|---|
| M42 100-1000 clean, own ring | at least 50 (kill under 40) | 56.6 | 58.1 / 52.9 | 27.2 / 39.0 | 91.9 | cleared |
| M42 blue at 2 and 3 px, sigma | within 0.7 of SXT | -1.82, -1.03 | -2.31, -1.10 / -2.26, -1.14 | -2.68, -1.70 / -2.99, -1.79 | -0.47, -0.03 | missed, by 1.35 and 1.00 |
| SMC 20-100 clean | at least 89.4 | 97.2 | 83.6 / 79.1 | 94.9 / 89.8 | 98.3 | held |
| SMC 100-1000 clean | at least 87.7 | 89.8 | 88.1 / 83.1 | 96.6 / 84.7 | 96.6 | held, by 2.1 |
| HIP 80609 20-100, 100-1000 | at least 96.8, 94.4 | 99.7, 97.1 | 99.7, 98.4 / 99.9, 98.2 | 99.7, 97.7 / 99.9, 97.1 | 99.0, 98.9 | held |
| Lobster 20-100, 100-1000 | at least 96.8, 96.6 | 99.8, 99.3 | 99.8, 99.4 / 99.8, 99.6 | 99.8, 99.5 / 99.8, 99.6 | 99.5, 99.0 | held |
| truth tiles 20-100, 100-1000 | at least 72, 54 | 73.3, 58.1 | 71.5, 53.2 (s0) | 73.3, 73.2 and 54.5, 58.2 (the two seeds, row by row) | | held |
| Andromeda dark dug | at most 47 | 22.4 | | | | held |
| Tarantula dark flat | at least 69 | 71.9 | 69.3 / 65.1 | | | held |
| M31 nucleus, R / G / B | under 40 % each | -1.3, 5.2, 1.4 % | | | | held |

- **Q2 is half answered on this seed:** M42's band clears 50 and its blue at 2 to 3 px is still dug, 1.35 and 1.00 sigma under
  SXT's against the 0.7 allowed. The blue moved a little toward SXT (R3s -2.3, -1.1) and not into the bar.
- **The SMC is the news.** The 20-100 row reads 97.2, above both of R3l's seeds and 13.6 above R3s's seed 0 on the same
  data; the 100-1000 row reads 89.8 on 59 stars, 2.1 above its bar, with 8.5 % of them dug where R3l and R3s dug none or
  one. One seed: R3l's own seeds differ on these rows by 5.1 and 11.9, so nothing about capacity is concluded yet.
- **What it gave up:** HIP 80609's brightest stars. At 1000 sigma and over its clean share is 79.7 (69 stars) against R3l's
  91.3 / 94.2 and R3s's 89.9 / 94.2, with 18.8 % dug. That band carries no registered bar and does not kill; it is the
  first thing to look at on seed 1.
- **The prediction held on all four counts:** the truth tiles rose (73.3 / 58.1 against R3s's 71.5 / 53.2), M42's band
  stayed in 50 to 60, the SMC's 20-100 row came to R3l's level or above (97.2 against R3l's mean 92.4), inside the guard's 3 (the even-odds part), and the blue dig was missed.
- **Seed 1 trains by the registered rule** (seed 0 not killed), started the same evening on the same cache; every bar is
  then judged on the two-seed mean, the nucleus on each seed. Nothing is adopted on one seed.

Files (DE1): `C:/temp/e2/check3-r3v-s0-own.txt`, `check3-r3v-s0-input-ring.txt`, `bright-channel-r3v-s0.txt`, `r3v.log`;
under `C:/temp/tianwen-scratch`, `f2-whole-master/w-r3v_synth22_s0/row.txt`, `f2-reports-master/m31-nucleus-r3v_synth22_s0.txt`
and `f2-logs-master/score-r3v_synth22_s0/eval-on-random.log`; the weights are `D:/temp/tianwen-scratch/r2-synth22/r3v_synth22_s0.pt`.

**R3v's two seeds, read 2026-11-16: not killed on the mean; every guard holds, and Q2's blue is missed again.** Seed 1
stopped on its plateau at step 253,500 (best held-out 5.77e-6 at 229,500; seed 0 5.85e-6).

| bar | registered | seed 0 | seed 1 | mean | |
|---|---|---|---|---|---|
| M42 100-1000 clean, own ring | at least 50 (kill under 40) | 56.6 | 52.9 | 54.8 | cleared |
| M42 blue at 2 and 3 px, sigma | within 0.7 of SXT (-0.47, -0.03) | -1.82, -1.03 | -1.71, -0.88 | -1.77, -0.96 | missed, by 1.30 and 0.93 |
| SMC 20-100 clean | at least 89.4 | 97.2 | 89.8 | 93.5 | held |
| SMC 100-1000 clean | at least 87.7 | 89.8 | 91.5 | 90.7 | held |
| HIP 80609 20-100, 100-1000 | at least 96.8, 94.4 | 99.7, 97.1 | 99.7, 97.8 | 99.7, 97.5 | held |
| Lobster 20-100, 100-1000 | at least 96.8, 96.6 | 99.8, 99.3 | 99.7, 99.5 | 99.8, 99.4 | held |
| truth tiles 20-100, 100-1000 | at least 72, 54 | 73.3, 58.1 | 73.2, 56.4 | 73.3, 57.3 | held |
| Andromeda dark dug | at most 47 | 22.4 | 24.3 | 23.4 | held |
| Tarantula dark flat | at least 69 | 71.9 | 80.1 | 76.0 | held |
| M31 nucleus, R / G / B, each seed | under 40 % each | -1.0, 5.1, 1.2 % | 0.0, -0.3, 1.9 % | | held on each |

  The M31 row's figures are the whole-master `row.txt` values, the source the seed 1 step bar is registered on; the
  seed 0 table above reads the `m31-nucleus` report (-1.3, 5.2, 1.4 %) and seed 1's own report reads -0.2, -0.3, 1.7 %.
  Every triple is far under the bar.

- **Capacity is not refuted on two seeds:** both clear the bars, seed 1's SMC 20-100 by 0.4 (89.8 against 89.4), with
  the channel spread that failed them three times on the small model, and M42's band clears 50 on both. The two seeds
  differ by 7.4 on that row (R3l's own seeds by 5.1) and the 100-1000 row is 59 stars, so this is a hold, not an answer.
- **The guards are no-regression bars, not targets (the owner, 2026-11-16: "dark dug at most 47 is awfully high").**
  47 was set from R3l, which dug 45.1. R3v as trained still digs 22 to 24 % of Andromeda's bright dark-sky stars and
  leaves 68 to 70 % of that sky flat, which is not adoptable alone; what makes the difference is the step below, and an
  adoption is to be judged as the pair against SXT, row by row.
- **Outside the bars:** HIP 80609 at 1000 sigma and over reads 79.7 and 85.5 clean (R3l 91.3 / 94.2), dug 18.8 and 13.0 %.
- Files (DE1): `C:/temp/e2/check3-r3v-own.txt`, `check3-r3v-input-ring.txt`, `bright-channel-r3v.txt`; under
  `C:/temp/tianwen-scratch`, `f2-whole-master/w-r3v_synth22_s1/row.txt`, `f2-reports-master/m31-nucleus-r3v_synth22_s1.txt`
  and `f2-eval-master/r3v_synth22_s1/on-random/starless-eval-v5.json`. Nothing is adopted; adoption is the owner's.

**A classical post-step fills the digs, with no training (explored 2026-11-15 and 16, NOT pre-registered; #2088).** The
owner asked the same evening for star removal to be explored from several angles. R3v's seed 0 loses to SXT almost only
by DIGGING (ring dug, by star count, 4.7 % at 100-1000 sigma, 12.2 % at 1000 and over, 16.8 % saturated, against SXT's 0.7, 0.0, 2.1; the
session-weighted figures first written here, 13.6, 25.4, 26.0 against 2.2, 0.0, 3.6, overstate it), so
the first angle is a step after the model and not another arm:
- **The step** (`training/star-removal/dig_floor.py` on tiles, `whole_floor.py` on whole linear masters): where the model
  took a star (input less output, smoothed 1 px, over 1.5 sigma in any channel, grown 2 px), the sky under it is read from
  the output OUTSIDE that mask (a normalised Gaussian, 4 px). A pixel more than 2.5 sigma under that sky is a pinhole at a
  star's core and is set to it. Then the output's smooth part (1.5 px) is lifted where it sits under the sky, tapered to
  nothing at the mask's edge. In these two versions nothing is ever lowered, and the pixel noise is kept. That is `--pin 2.5`,
  "floor2" below; the first version ("floor") had no pinhole rule and no taper. floor3, further down and the one registered,
  brings back a BOUNDED lowering.
- **It was tuned on what it is scored on.** The first version's constants were chosen once, before any score. The second
  came from looking at ONE sheet of four M42 crops, where the first left dark pinholes at star centres that the ring
  measure (2 px outward) cannot see. So the real-star rows below are not a held-out read; for floor and floor2 the whole masters and the twin
  were scored after the constants were fixed and are. floor3's are not: its cap-up was chosen on the twin's rms (below),
  so its twin figures are the best of a sweep, and only seed 1 is a held-out read of it.

| R3v seed 0 | as trained | + floor | + floor2 | SXT |
|---|---|---|---|---|
| real stars clean, 20-100 sigma (this and the next six rows SESSION-WEIGHTED, four sessions equally; by count is the table further down) | 97.5 | 98.0 | 97.7 | 98.0 |
| 100-1000 sigma | 85.7 | 98.0 | 97.8 | 96.6 |
| 1000 sigma and over | 74.2 | 97.4 | 97.4 | 95.1 |
| saturated | 45.0 | 63.8 | 58.4 | 82.8 |
| M42 100-1000 clean / dug | 56.6 / 42.6 | 94.1 / 4.4 | 93.4 / 2.9 | 91.9 / 6.6 |
| SMC 100-1000 clean | 89.8 | 98.3 | 98.3 | 96.6 |
| twin, TRUE clean at 1000 and over / saturated | 93.4 / 88.3 | 97.5 / 95.7 | 97.2 / 93.6 | |
| twin, error against the plate under the mask, rms sigma | 1.92 | 1.13 | 1.01 | |
| twin, mean error under the mask, sigma | -0.51 | +0.03 | -0.00 | |
| Andromeda dark flat / dug | 69.5 / 22.4 | 87.6 / 0.6 | 83.9 / 0.2 | |
| Andromeda blue at 1.5-3 px, sigma | -0.79 | -0.27 | -0.05 | |
| Tarantula dark flat | 71.9 | 82.9 | 82.6 | |
| M31 nucleus, whole-master row, % | -1.0 / 5.1 / 1.2 | the same | the same | |

- **Against the truth it is a real gain, not the measure's:** on the twin the error under the removed stars falls from 1.92
  to 1.01 sigma and its mean from -0.51 to zero, and nothing changes outside the mask (0.127 before and after). The glow
  rows (the halo left round bright clipped stars, lower is better; read in full under floor3 below) move with floor2 from
  591.4 / 1721.6 to 597.1 / 1761.4 on Andromeda and from 965.5 / 1967.0 to 1175.7 / 1950.3 on Tarantula, whose median is 22 % WORSE.
- **The two-sided version is dead.** "fill" also removes what stands ABOVE the sky under a removed star: it reads best on
  every star row (saturated 91.3) and takes 72.8 / 70.7 / 68.1 % of the M31 nucleus. An unbounded lowering is dead; floor and floor2 only lift, and floor3 lowers
  by at most a twentieth of what the model took at that pixel, which moves the nucleus's green from 5.1 to 6.0 % and its blue
  from 1.2 to 1.6 % and is why the nucleus bar of the seed 1 test is not vacuous.
- **The step carries most of the gain on a model that digs, and is NOT harmless on one that leaves wings.** On R3l's seed 0
  (the small baseline, no channel spread) floor2 reads M42 100-1000 89.0 (27.2 as trained), the SMC 96.6, pooled 100-1000
  96.2 and saturated 74.5. On R3s's seed 0 it does not recover the SMC (83.1) and it LOWERS it: the SMC 100-1000 clean row
  goes 88.1 to 83.1 (20-100: 83.6 to 82.5), and pooled 1000-and-over clean goes 79.3 to 61.0 with ring left 0.4 to 39.0
  (`check3-floor2-arms.txt`; that pooled move is one SMC star, 100 to 0, see the n note below). The bar now registered
  for seed 1, "SMC rows not lowered by more than 1 point", was written after this read; R3s's seed 0 would have failed it under floor2. The likely cause, NOT measured: the sky under the
  mask is read from output pixels outside it that still hold a leftover star wing, so the lift overshoots and the fill
  sits above the true sky. A step that fills what a model leaves behind is only safe on a model that leaves no wings.
  R3v with the step is the best of the three everywhere but the saturated band.
- **What is still open, for floor and floor2:** the saturated band's session-weighted figures (58.4 with floor2 against SXT's
  82.8) are mostly ONE star: the SMC session has one saturated star, a quarter of that mean, and it reads 0 / 0 / 100 for
  floor / floor2 / floor3. By star count saturated clean is 78.9 as trained, 91.6 floor, 88.4 floor2, 91.6 floor3, SXT 89.4
  (the by-count table below). The pin rule does raise stars left a little (by count at 1000 and over 0.5 / 0.5 / 1.1). The
  mask covers 42 to 55 % of a crowded master, so a dark lane narrower than the 4 px sky scale that crosses a removed star
  is partly filled. And nothing here is in the product: the step is Python glue, and a remover that ships needs it in
  `TianWen.AI.Imaging`.
- **Floor or floor2, the trade (superseded: floor3 replaced floor2 as the registered version, and by star count floor3 is at
  or above floor on every band, 99.4 / 100.0 / 91.6 against 99.1 / 98.9 / 91.6).** floor2 was chosen from ONE sheet (four M42 crops) and was
  the one first pre-registered. Floor is at or above floor2 in every real-star row (higher in four: 20-100 98.0 vs 97.7, 100-1000
  98.0 vs 97.8, saturated 63.8 vs 58.4, M42 94.1 vs 93.4), on Andromeda dark flat (87.6 vs 83.9) and on the twin TRUE clean
  share (97.5 vs 97.2 at 1000 and over, 95.7 vs 93.6 saturated). Floor2 wins on dug (M42 2.9 vs 4.4, Andromeda 0.2 vs 0.6),
  the twin rms (1.01 vs 1.13), the twin mean (-0.00 vs +0.03) and Andromeda blue (-0.05 vs -0.27). So floor2 buys the
  pinhole and the smoothness the ring measure cannot see, and pays a few points of clean share, the saturated band most.
  The twin rms, mean and outside figures are a quarter sample (one tile in four, `twin_err.py`); the TRUE clean twin rows
  use every tile.
- **floor3 is in the product since 2026-11-18 (PR #2208, issue #2204).** `DigFill.Apply` in `TianWen.Lib`, run inside
  `NarrowbandCombination.SplitStarsAsync` after core protection and only for TianWen's own remover; on by default,
  `--no-fill-digs` on `image remove-stars` turns it off, and `image fill-digs` runs it alone on an existing plate. Its
  output is bit for bit the script's on Andromeda, the Tarantula and the mono Sagittarius master, and the whole path
  (R3z's weights, both defaults on) gives the Tarantula plate the scorecard read, with no pixel different. The scoring
  scripts pass `--no-fill-digs` and apply floor3 themselves, so every earlier row is reproducible. No remover of ours
  ships; this is the step a caller's named weights now get.
- **The third version, floor3, scored 2026-11-16: each correction capped by what the model took there.** Two flaws of
  floor2 showed on a second sheet (the SMC's one saturated star and M42's bright interior): in bright smooth nebula, where
  the noise is tiny, the pinhole rule and the lift drew contour edges; and the residue a model LEAVES at a saturated core
  was untouched. floor3 (`dig_floor.py --pin 2.5 --cap-up 2 --cap-down 0.05`, mode `floor3` of `whole_floor.py`) keeps
  floor2 and bounds it by the model's own removal at that pixel (input less output, smoothed 1 px, the largest within
  2 px): a lift is at most twice it, and what still stands above the sky is lowered by at most a twentieth of it. The cap
  on the lift was tried at 0.5, 2 and 4 on the twin (rms 1.28, 0.99, 1.00) and on that sheet; 2 was taken. So floor3 is
  tuned on seed 0 twice over. The cap on lowering, 0.05, was fixed once with no sweep, and with the M31 nucleus and the
  saturated sheet in view: it was picked to reach a saturated core's residue (the removal there is hundreds of sigma)
  and spare the nucleus. So the nucleus bar passing on seed 0 (green 5.1 to 6.0, 0.9 against 1) is by construction, and
  only seed 1 is a read of it.

| R3v seed 0 | as trained | + floor2 | + floor3 | SXT |
|---|---|---|---|---|
| real stars clean, 20-100 sigma (the four sessions weighted equally, as every row to "SMC") | 97.5 | 97.7 | 98.5 | 98.0 |
| 100-1000 sigma | 85.7 | 97.8 | 98.6 | 96.6 |
| 1000 sigma and over | 74.2 | 97.4 | 100.0 | 95.1 |
| saturated (clean / left) | 45.0 / 32.6 | 58.4 / 39.8 | 88.4 / 6.5 | 82.8 / 13.6 |
| M42 100-1000 clean | 56.6 | 93.4 | 94.9 | 91.9 |
| SMC 20-100 / 100-1000 clean | 97.2 / 89.8 | 97.2 / 98.3 | 98.9 / 100.0 | 98.3 / 96.6 |
| twin, TRUE clean at 1000 and over / saturated | 93.4 / 88.3 | 97.2 / 93.6 | 97.7 / 95.7 | |
| twin, rms error under the mask / mean, sigma (quarter sample) | 1.92 / -0.51 | 1.01 / -0.00 | 0.99 / -0.08 | |
| Andromeda dark flat / dug | 69.5 / 22.4 | 83.9 / 0.2 | 97.6 / 1.0 | |
| Tarantula dark flat / left | 71.9 / 16.0 | 82.6 / 16.7 | 93.9 / 4.9 | |
| Tarantula crowded / Sagittarius crowded | 83.8 / 93.7 | 85.5 / 93.2 | 88.7 / 95.5 | |
| M31 nucleus, whole-master row, % | -1.0 / 5.1 / 1.2 | the same | -1.0 / 6.0 / 1.6 | |
| glow, green, the row's two figures: Andromeda | 591.4 / 1721.6 | 597.1 / 1761.4 | 599.4 / 1549.7 | |
| glow: Tarantula | 965.5 / 1967.0 | 1175.7 / 1950.3 | 1121.1 / 1726.2 | |

  **By star count** (every scored star once; `real_star_check3.py` prints it as "by count" since 2026-11-16; this is the
  statistic of the seed 1 bars, and the session-weighted rows above overstate both the gap as trained and the gain, since
  M42's 136 stars and the SMC's 59 weigh as much as Lobster's 987):

| R3v seed 0, clean by star count (n) | as trained | + floor | + floor2 | + floor3 | SXT |
|---|---|---|---|---|---|
| 20-100 sigma (2510) | 98.9 | 99.2 | 99.0 | 99.3 | 98.8 |
| 100-1000 sigma (1739) | 94.9 | 99.1 | 99.1 | 99.4 | 98.3 |
| 1000 sigma and over (188) | 87.2 | 98.9 | 98.9 | 100.0 | 98.4 |
| saturated (95) | 78.9 | 91.6 | 88.4 | 91.6 | 89.4 |

  SXT is scored on 1738 stars at 100-1000 and 94 saturated: the check skips a window holding a NaN per model, so in
  those two bands the sets differ by one star, and the saturated gap read here (91.6 against 89.4) is about two stars.
  The saturated band has another side: dug by star count (the "by count dug" row) is 16.8 as trained, 3.2 with floor, 3.2 with floor2 and 5.3
  with floor3 (SXT 2.1), so floor3 digs two more stars than floor and floor2 (one HIP 80609, one Lobster; which of its two caps does it is NOT
  measured) while it takes the residue (left by count 6.3 as trained, 7.4 floor, 10.5 floor2, 4.2 floor3; SXT 8.5). Saturated dug is not a registered bar; it is reported on seed 1 and watched.

  With floor3 by count: R3l 99.1 / 100.0 / 93.7, R3s 99.0 / 100.0 / 91.6, R3j 99.2 / 99.5 / 92.6 (`check3-bycount.txt`).
  - **It repairs floor2's harm on a model that leaves wings:** on R3s's seed 0 the SMC rows go 83.6 / 88.1 as trained,
    82.5 / 83.1 with floor2 and 98.9 / 100.0 with floor3, and pooled 1000 and over 79.3, 61.0, 100.0
    (`check3-floor3-r3s.txt`). On R3l's seed 0 floor3 reads 98.2 at 100-1000, 100.0 at 1000 and over and 91.3 saturated.
  - **The glow rows, read the right way round (corrected 2026-11-16; an earlier text of this section called a fall a cost).**
    `glow_ring.py` sums the halo a remover LEAVES at 10 to 30 px round each bright clipped star, so LOWER is better; the
    row's two figures are the median and the p90 over those stars. SXT's Andromeda plate reads 242.8 / 574.8 in green with
    59 % of stars over the threshold; R3v reads 591.4 / 1721.6 and 89 %. With floor3 the p90 falls 10 % on Andromeda and
    12 % on Tarantula (a gain), Andromeda's median holds (591.4 to 599.4) and Tarantula's median RISES 16 % (965.5 to
    1121.1; 22 % with floor2). The likely cause, not measured: as trained, a dig inside the annulus cancels part of the
    halo in the sum, and filling the dig uncovers it. The nucleus's green moves 5.1 to 6.0 %.
  - **So the gap that is left is the wide halo, and no step judged here closes it (the ring-median step explored below removes it, by construction):** round bright clipped stars the model leaves
    about 2.5 times SXT's halo (green median 599 against 243) on 89 % of them against 59 %. That is the bright-halo line's
    own subject (R3q onward), a matter of what the synthetic stars' far wings teach, and the next angle.
- **With the step, earlier arms that were killed for digging read alike on stars, and the halo tells them apart
  (2026-11-16, seed 0 of each, floor3).** Three arms of the drawn-wings and channel-spread lines, all the small model:

| seed 0 + floor3 | real stars clean 100-1000 / 1000+ / saturated, SESSION-WEIGHTED (by count: the line under the by-count table) | Andromeda dark flat | Tarantula dark flat | nucleus R / G / B % | Andromeda halo, median / p90 / over | Tarantula halo |
|---|---|---|---|---|---|---|
| R3v (four-level, synth22) | 98.6 / 100.0 / 88.4 | 97.6 | 93.9 | -1.0 / 6.0 / 1.6 | 599 / 1550 / 89 % | 1121 / 1726 / 92 % |
| R3j (drawn wings, the dose up, synth16) | 98.2 / 99.6 / 90.2 | 97.5 | 93.0 | -0.4 / -1.2 / 4.5 | 416 / 1336 / 82 % | 395 / 785 / 54 % |
| R3u (spreads mixed, synth24) | 98.4 / 100.0 / 90.2 | 97.7 | 91.9 | -4.6 / 3.5 / 3.8 | 600 / 1487 / 90 % | 639 / 1008 / 69 % |
| R3i (synth15) | 97.8 / 99.6 / 90.2 | 97.5 | 92.4 | 3.3 / 7.8 / 11.7 | 484 / 1175 / 87 % | 265 / 665 / 54 % |
| SXT | 96.6 / 95.1 / 82.8 | | | | 243 / 575 / 59 % | not scored |

  - The star rows and the dark-flat rows no longer separate the arms: the step fills what each one dug (R3j as trained read
    M42 100-1000 31.6, Andromeda dark flat 41.1 and dug 45.0). What the kills of R3j, R3u and R3i rested on was mostly
    digging, which a step after the model now removes; their registrations stand as read, and nothing is un-killed here.
  - The halo is where they differ, by a factor of four on Tarantula, and seeds differ on it too (R3s's two seeds as trained:
    321 and 715). The arms that drew wider wings (R3j, R3i) leave the least; R3i pays with the nucleus (11.7 % blue).
    A dig hides halo in this measure: R3j as trained read 136 on Tarantula and reads 395 once its digs are filled.
  - **The candidate this points at, named and not started:** the drawn-wings data of R3j (synth16) on R3v's four-level
    model, judged WITH the step from the start, with the halo rows as its question. One seed is about 13 hours on the 1070.
  (`check3-floor3-haloarms.txt`; whole masters `w-r3j_s0+floor3`, `w-r3u_synth24_s0+floor3`, `w-r3i_synth15_s0+floor3`.)
- **The halo, looked at the same night (2026-11-16, explored, NOT pre-registered, nothing adopted).** Working files are
  on DE1 under `C:/temp/e2/explore` (`halo_ring_profile.py`, which is not the tracked `domain/halo_profile.py`, `twin_halo.py`, `halo_step.py`, `crescent.py`, sheet
  `halo-step.png`); none is in the repository.
  - **It is left on real stars only.** Round Andromeda's 71 bright clipped stars, R3v's seed 0 with floor3 leaves a ring
    (the median in each radius bin, over the MEAN of the plate's own 45-60 px ring, in the stretch, master sigma a pixel, at 6-10 / 10-15 / 15-20 /
    20-25 px): R 0.32 / 0.38 / 0.35 / 0.28, G 0.33 / 0.41 / 0.36 / 0.20, B 0.27 / 0.44 / 0.27 / 0.17. SXT leaves R 0.14 /
    0.02 / 0.10 / 0.08, G 0.10 / 0.09 / 0.13 / 0.11, B 0.15 / 0.30 / 0.17 / 0.10, so the gap is widest in red and green
    and small in blue at 10-15 px. On the twin, round 5,859 bright synthetic stars, the truth error at those radii is zero or
    slightly negative. So the model leaves no excess where the truth has none. That real stars carry wider halos than the drawn ones is the
    inference (the two figures are not on one footing: an excess over the plate's own ring against an error against
    truth); "What the classical plate keeps" below measures it.
  - **A ring-median step removes it.** Round each clipped compact star of the input, per channel, the plate's ring-median
    profile over its 45-60 px ring, smoothed 2 px in radius, positive part only, tapered out from 37 to 45 px, is
    subtracted. Andromeda's glow row goes 599.4 / 1549.7 / 89 % to 13.5 / 81.4 / 1 % and Tarantula's 1121.1 / 1726.2 /
    92 % to 13.7 / 223.3 / 15 %; the whole-master rows read again (dark flats, crowded, nucleus) are unchanged (nucleus
    -1.0 / 6.0 / 1.6; Tarantula's crowded BP17-19 and purity move by 0.1), and the tile rows are not re-scored, since the
    step runs on whole masters only. At random positions
    away from those stars the same step takes about a tenth as much by median (Andromeda, median sigma in 10-30 px, R / G / B:
    0.035 / 0.019 / 0.031 against 0.286 / 0.270 / 0.257; Tarantula 0.031 / 0.055 / 0.046 against 0.438 / 0.502 / 0.283)
    and about a fifth by mean (Andromeda 0.072 / 0.064 / 0.062 against 0.301 / 0.328 / 0.276), since some random
    positions do carry structure.
    The step picks its stars by its own rule (98 on Andromeda, 15 on Tarantula: clipped and compact, isolation not
    asked), where the glow row uses `glow_ring.py`'s 71 and 13.
  - **Read with care:** the step subtracts what the glow row sums, on the stars the row picks, so the row falling is by
    construction and says nothing about SXT; only the random-position control is evidence, and there is no truth for
    it, since the twin has no such halo. On the sheet the one or two brightest stars of each master keep an off-centre
    crescent and a dark disc at the core, which this step does not touch.
  - **The crescent is not a channel offset, and the lean is one SXT shares:** on Andromeda the input's wing offsets are |R-G| 0.31 px and
    |B-G| 0.76 px, far under the residue's lean of about 2.0 px (R / G / B 2.17 / 2.04 / 1.84; SXT 1.82 / 1.80 / 1.48), and
    the residue's dipole follows them weakly in red only (median cosine R +0.27, SXT +0.37) and not in blue (+0.05, SXT
    -0.08; green is the reference).
    What differs from SXT within 25 px, over all 71 stars, is the net excess: 630 / 584 / 600 sigma-pixels against 160 / 159 / 333.
- **Pre-registered for R3v's seed 1 (training, no score exists; amended 2026-11-16 before any score, from floor2 to floor3):** floor3 with exactly these constants (`dig_floor.py <model> floor --pin 2.5 --cap-up 2
  --cap-down 0.05 --tag floor3`; mode `floor3` of `whole_floor.py`) is applied to seed 1's outputs once it is scored, and every figure below is seed 1 with the step against
  seed 1 as trained. It is confirmed if all hold:
  - the real-star clean share, read off the "by count clean" row that `real_star_check3.py` prints (every scored star once over the four sessions, small sessions included; never the mean of four session figures: the
    SMC session has n=1 at 1000 and over, M42 n=11, and one SMC star moved R3s seed 0 pooled from 79.3 to 61.0), is at
    least 95 at 100-1000 sigma and at 1000 and over AND at least 3 points above seed 1 as trained on each (added
    2026-11-16 before any seed 1 score, on the review's finding that seed 0 as trained already reads 94.9 by count at
    100-1000, so 95 alone could hold with no step; seed 0 reads +4.5 and +12.8);
  - M42's 100-1000 clean row is at least 85 (the "Orion M42" line under 100-1000 that `real_star_check3.py --sessions`
    prints, with its n);
  - the SMC 20-100 and 100-1000 clean rows (the "SMC" lines of the same `--sessions` print; the rows with n of 5 or more) are not lowered by more than 1 point against
    seed 1 as trained; the 1000-and-over and saturated SMC rows, n=1, are reported only;
  - the M31 nucleus moves by under 1 point in every channel, read from the nucleus field of the whole-master `row.txt`
    (as trained, and after `whole_floor.py <w-tag> floor3` scored by `score_whole.py` with `--plates`);
  - the twin's rms error under the mask falls by at least a third, as `twin_err.py <seed 1 model> <seed 1 model>+floor3`
    prints it in its "rms in mask" column: one twin tile in four (i % 4), the mask taken from the as-trained model
    (seed 0 reads 1.920 to 0.989).
  The other whole-master rows (dark flats, crowded, glow) are not criteria but are reported, the glow rows with their direction (lower is better; a glow bar
  written here for a few hours on 2026-11-16 read the measure backwards and is withdrawn, before any seed 1 score). If the criteria hold, the step is proposed to the owner for the product; if not,
  it was fitted to seed 0. Any change to these bars after seed 1 is scored is post hoc.

- **Seed 1's read of the step (2026-11-16): four of the five bars hold and the twin bar is missed, so as registered it is
  NOT confirmed.** One run of the registered commands (`C:/temp/e2/explore/seed1-step.sh`, on a copy of the scripts as
  merged), seed 1 with floor3 against seed 1 as trained:

| registered bar | seed 1 as trained | + floor3 | |
|---|---|---|---|
| by count clean, 100-1000 sigma: at least 95 and 3 points up | 95.1 | 99.2 | held |
| by count clean, 1000 sigma and over: the same | 87.8 | 100.0 | held |
| M42 100-1000 clean: at least 85 | 52.9 | 93.4 | held |
| SMC 20-100 / 100-1000 clean: not lowered by more than 1 | 89.8 / 91.5 | 98.9 / 100.0 | held |
| M31 nucleus R / G / B: moves under 1 point | 0.0 / -0.3 / 1.9 % | 0.1 / -0.3 / 2.2 % | held |
| twin, rms in mask: falls by at least a third | 1.406 | 0.978 | **missed: 30.4 %** |

  - The step ends where it ended on seed 0 (0.978 against 0.989) and seed 1 began lower (1.406 against 1.920), so the
    fall is smaller. The bar was a fall of a third and is not moved; a bar on the level reached would have been the
    better registration, and that is said after the score, so it changes nothing here.
  - Reported only, as registered: Andromeda dark flat / dug 67.7 / 24.3 to 97.5 / 1.2; Tarantula dark flat / left 80.1 /
    13.8 to 93.5 / 5.3; Tarantula crowded 83.5 to 88.8, Sagittarius 93.7 to 95.4; saturated by count clean / dug / left
    74.7 / 17.9 / 18.9 to 91.6 / 5.3 / 4.2 (SXT 89.4 / 2.1 / 8.5), so it still digs more saturated stars than SXT; glow,
    green (lower is better), Andromeda 557.8 / 1496.5 to 596.7 / 1424.0 and Tarantula 482.4 / 1328.2 to 833.5 / 1436.5,
    the median rising as on seed 0.
  - The next step is the owner's: the gains on real stars and whole masters repeat on a seed the step was not tuned
    on, and one registered bar is missed. Files: `C:/temp/e2/explore/seed1/` (`check3-real.txt`, `check3-twin.txt`,
    `twin-err.txt` in `step.log`), `f2-whole-master/w-r3v_synth22_s1+floor3/row.txt`; 1:1 renderings of Andromeda and
    Tarantula, every column in the input master's stretch, in `C:/temp/e2/explore/render1to1/` (`render_1to1.py`).
- **Why the model digs, as far as it is read (2026-11-16; the owner: a model that does it itself is better than a step).**
  On the twin the mean error under the mask is -0.51 sigma on seed 0 and -0.30 on seed 1, in the stretched tiles the
  loss itself is computed on, so the bias is not a matter of the stretch. A dig term exists and is off in R3v: the
  speckle teacher (`--speckle-weight`, #1362, "R3b's read"), a ONE-SIDED penalty on the cores at removed sites, which
  on the small model took the dig off the real tiles (1.4 and 0.8 % dug) and paid in light left (81.5 % of M42's
  bright stars on seed 0). The step differs in kind: it takes the removal as given and restores the SMOOTH sky under
  it (smooth 2 px rms in mask 0.940 to 0.507 on seed 1). The recipe's loss is L2 plus three times a band term on
  the 2-4 and 4-8 px scales, so a broad dent costs little; that this is the cause is a hypothesis, NOT measured. The
  candidate it names, not registered and not started: a two-sided term on the smooth error under removed stars (or
  a coarser band), tried first as a continuation from R3v's weights, with the step's own figures as its bar.
- **M32 is removed (2026-11-16, found on the whole-frame sheet; no scorecard row reads it).** Andromeda's compact
  companion (master y 1480, x 2280) is taken entirely by every arm from R3i to R3v (-3 to +5 % of its core flux
  kept; R3v seed 1 reads +3.1 / +2.2 / +4.9 %, seed 0 +2.3 / +2.3 / +1.1, R3l seed 1 -3.0 / -2.7 / -3.0); SXT keeps about 42 %, StarNet2 about 5 %, R2w, R3a and R3b 5 to 15 %. The dig step cannot bring it back. A
  kept-M32 row is proposed as a guard for any next arm (`C:/temp/e2/explore/m32.py`, `m32_all.py`, sheet `m32.png`).
- **What the classical plate keeps round a bright star (2026-11-16; the owner asked whether drawing stars measured
  against a classical plate caps us at the classical remover: for the outer halo it does, in about half the sessions).** Over the 29 sessions
  of `plate_halo2.txt`, round
  the isolated saturated stars with at least 20 sigma at 3-5 px (`C:/temp/e2/explore/stars/plate_halo2.py`, luminance,
  each of master and plate over its own 45-60 px ring, in the catalogue's local sigma, median over a session's
  stars), the star's light at 6-10 / 10-15 / 15-20 / 20-25 px and what the plate keeps of it. The six rows were picked
  by hand, with no rule, as the plan's guard fields plus the Pleiades, and are not a sample; of all 29, 12 keep
  0.2 sigma or more at 10-15 px and 13 keep under 0.15:

| session | stars | master | plate keeps |
|---|---|---|---|
| HIP 80609 (ASI533MC Pro) | 33 | 3.18 / 0.82 / 0.41 / 0.16 | 0.47 / 0.37 / 0.19 / 0.15 |
| Lobster (QHY294PROC) | 45 | 4.11 / 1.04 / 0.37 / 0.13 | 0.27 / 0.23 / 0.25 / 0.16 |
| M42 LUM (ASI294MM) | 251 | 3.65 / 0.92 / 0.27 / 0.11 | 0.13 / 0.30 / 0.30 / 0.16 |
| Tarantula L-Quad (SV605CC) | 46 | 4.71 / 1.03 / 0.24 / 0.09 | 0.47 / 0.42 / 0.25 / 0.13 |
| Pleiades L-Quad (SV605CC) | 37 | 26.60 / 4.97 / 1.31 / 0.58 | 0.98 / 0.81 / 0.53 / 0.26 |
| SMC broadband (ASI585MC Pro) | 89 | 4.78 / 1.12 / 0.30 / 0.13 | -0.06 / 0.09 / 0.13 / 0.08 |

  - Where it keeps a halo (12 of 29 sessions), the classical remover takes the core and inner wing and leaves 0.2 to
    0.5 sigma a pixel from 6 to 25 px, which past 15 px is most or all of the star's light there (the Pleiades is
    the extreme: 0.98 / 0.81 sigma at 6-10 and 10-15 px, 40 to 45 % of its light kept past 15 px); in the 13 that keep
    under 0.15 at 10-15 px (the ASI585's, the wide Carina fields' four, Uranus on the SMC, the ASI533 2022 Tarantula and others) the plate is
    clean.
    `--saturated-measured` reads the master LESS the plate, so that halo is never drawn as a star, and the synthetic
    background keeps the plate's scales from 4 PSF widths up, so it is in the target as sky. R3v leaves 0.3 to 0.4
    sigma a pixel round Andromeda's stars (above), the same size; the two are different fields, so that match is
    circumstantial, not a controlled test.
  - The census agrees (S1c's instrument, `halo_census.py` and `halo_profile.py`, the drawn clipped stars' stretched
    light over real's, green over luminance, at 10 / 15 / 20 / 25 px; 3,000 tiles of each training export): synth22
    (R3v's data) 2 px cores 0.50 / 0.25 / 0.15 / 0.04 and 1 px cores 0.85 / 0.36 / 0.15 / 0.00, with 5 stars of a 3 to
    5 px core and none of 6 px in the sample against real's 1,827 and 403; synth18 the same; synth21 (measured
    saturated stars) 2 px 0.86 / 0.57 / 0.33 / 0.21; synth16 (R3j, drawn wings) 2 px 1.06 / 0.86 / 0.65 / 0.71 but
    its 3 to 5 px cores (11 stars) 1.73 / 1.97 / 1.70 / 1.95 times real at 10 / 15 / 20 / 25 px (`C:/temp/e2/explore/stars/profiles-*.json`).
  - **`degrade --halo-over-plate` draws it (2026-11-16, opt-in, with `--saturated-measured`; no training yet).** Round
    each measured saturated star the plate's own azimuthal median in 1 px rings to 45 px, less its median at 50 to 60 px,
    is read per amplitude (`InjectionPopulation.PlateRingExcess`); the session's median over its stars, never negative,
    nothing inside 5 px, is added to every measured star's table, and every other star draws the session's whole halo
    (the median of the stars' own tables plus that share), coming in from 8 to 14 px (`HaloOverPlate`). One star's rings
    hold the nebula under it, so only the median over the session is drawn; a session with fewer than eight measured
    stars whose 60 px stamp lies in the frame and holds no absent pixel draws as without the option, and its log line says
    so (`InjectionPopulation.HaloOverPlateStars`). A star is drawn only when its centre lies within the exporter's 8 px
    margin of a tile (`InjectionMarginPx`, unchanged, since moving it moves every draw), so a tile's edge does not hold
    the halo of a star further outside it, in the input or the target: no label is wrong, and a halo whose star is off the
    tile is not taught. It draws no random number and is off by default (`HaloOverPlateTests`).
  - **Its validation, on the census** (the eight validation sessions exported from one frozen binary with synth22's
    recipe plus `--saturated-own-share --saturated-measured`, control, and the same plus the option; 1,440 tiles each;
    the drawn clipped stars' stretched light over real's at 6 / 10 / 15 / 20 / 25 px, all rigs pooled):

| core | control | with the option |
|---|---|---|
| 1 px | 1.06 / 0.89 / 0.57 / 0.15 / 0.08 | 1.06 / 0.95 / 0.96 / 0.74 / 0.62 |
| 2 px | 0.84 / 0.62 / 0.48 / 0.31 / 0.21 | 0.85 / 0.75 / 0.75 / 0.73 / 0.56 |
| 3 to 5 px | 0.83 / 0.64 / 0.50 / 0.34 / 0.29 | 0.88 / 0.68 / 0.71 / 0.51 / 0.63 |

    Per rig against that rig's own real stars, 2 px cores at 15 / 20 / 25 px: QHY294PROC 0.52 / 0.16 / 0.11 to 0.72 /
    0.69 / 0.64; ASI533MC Pro 0.52 / 0.18 / 0.09 to 1.30 / 1.06 / 0.99; ASI294MC and ASI585MC Pro within 0.2 of the
    control (their validation sessions' plates are clean or hold too few measured stars). The inner profile does not
    move (4 px within 0.01). **What it leaves:** the ASI533MC Pro reads 1.1 to 1.3 times real at 15 px and 1.15 to 1.41
    at 10 px on 1 and 2 px cores, where its control already reads 1.6 to 1.85 at 6 px; a rig's real column is all its masters and its drawn
    column one validation session, so a factor of about 1.2 is inside the session mix, and the ramp was moved once
    (from 6 to 10 px, which read 1.77 and 1.25 at 10 px on that rig) and is not tuned further on this set. Stars of a
    6 px core and over read 2 to 3.5 times real at 15 to 25 px in the control already (46 drawn stars; 13 and 14
    real on the two rigs that have them). Looked at star by star (2026-11-16, the census's own records), that is mostly
    a comparison of unlike stars in an open-ended group: HIP 80609's master holds 6 such stars, of 6 and 9 px cores,
    and its 33 drawn ones are mostly copies of the brightest (9 and 10 px), while a rig's real median is its 6 and 7 px
    cores. What is left: the brightest drawn stars come out about a pixel wider than their source (10 px against 9),
    and M42's largest stars (cores of 18 to 36 px) are not drawn at all (the drawn ones end at 14).
  - **The classifier cannot see it:** star-crop AUC 0.728 +- 0.016 for the control and 0.728 +- 0.013 with the option
    (seeds 0 to 4, the bar's tiles), as a 24 px crop round a 20 to 500 sigma star must; it says the option does not
    change ordinary stars, nothing about the halo. Both read under synth11's 0.760 and synth4's 0.782.
  - Not done: a training export with the option, which needs an arm registered first (with the M32 row and the halo
    rows as its question). Files (DE1): `C:/temp/e2/explore/stars/` (`profiles-val-*.json`, `reads/cls-*.json`); the exports (`val-ctl`,
    `val-halo2`; `val-halo` is the first ramp) and the binaries `halo-bin1` and `halo-bin2` were moved to
    `D:/temp/tianwen-scratch/halo-validation/` the same day, C: being at 8 GB free.
    Both exports were made again from a binary at the review's fixes (`halo-bin3`: no stamp holding an absent pixel is
    read, one shared ring median): the control's 3,200 tile files are byte for byte the first control's, so the shared
    median moves no measured star; with the option 2,880 are identical and the 320 that differ are M42's (ASI294MC), the
    one session with a measured star near its canvas ring; every census figure above is unchanged to two decimals
    (`val-ctl3`, `val-halo3`, in the same folder on D:; `halo-bin3` is under `C:/temp/tianwen-scratch`).

- **SXT on the whole-master rows, scored with the same scorer (2026-11-16; the owner asked for targets, not the
  guards).** SXT's Tarantula plate was made that day with `tianwen image remove-stars` (RC-Astro's CLI); its Andromeda
  and Sagittarius plates are the files the earlier reads used (`f2-whole-master/w-sxt/row.txt`):

| whole-master row | SXT | R3v seed 0 / seed 1 as trained | + floor3, seed 0 / seed 1 |
|---|---|---|---|
| Andromeda dark flat | 98.6 | 69.5 / 67.7 | 97.6 / 97.5 |
| Andromeda dark dug | 1.4 | 22.4 / 24.3 | 1.0 / 1.2 |
| Andromeda blue, 1.5-3 / 3-4.5 px, sigma | -0.40 / -0.27 | -0.79, -0.69 / -0.75, -0.76 | -0.08, -0.22 / -0.12, -0.24 |
| Tarantula dark flat / left | 96.6 / 2.7 | 71.9, 16.0 / 80.1, 13.8 | 93.9, 4.9 / 93.5, 5.3 |
| Tarantula crowded | 96.1 | 83.8 / 83.5 | 88.7 / 88.8 |
| Tarantula BP 17-19 removed | 98.3 | 92.4 / 93.8 | 92.9 / 94.3 |
| purity (light taken where no star is; lower is better) | 3.1 | 0.6 / 0.5 | 0.5 / 0.5 |
| Sagittarius crowded | 99.6 | 93.7 / 93.7 | 95.5 / 95.4 |
| M31 nucleus, R / G / B % | 0.1 / 0.1 / 0.1 | -1.0, 5.1, 1.2 / 0.0, -0.3, 1.9 | -1.0, 6.0, 1.6 / 0.1, -0.3, 2.2 |
| glow, green, Andromeda (median / p90 / over) | 242.8 / 574.8 / 59 % | 591.4, 1721.6, 89 % / 557.8, 1496.5, 87 % | 599.4, 1549.7, 89 % / 596.7, 1424.0, 90 % |
| glow, green, Tarantula | -30.8 / 203.4 / 8 % | 965.5, 1967.0, 92 % / 482.4, 1328.2, 85 % | 1121.1, 1726.2, 92 % / 833.5, 1436.5, 85 % |

  With the step the pair is level with SXT on the dark-sky stars (97.5 flat and 1.2 dug against 98.6 and 1.4). What is
  left, in size order: the crowded fields (Tarantula 88.8 against 96.1, Sagittarius 95.4 against 99.6, BP 17-19 94
  against 98; R3v trained with no cluster scenes, its extra cache empty), then the glow (2.5 times SXT's on Andromeda,
  and SXT leaves none on the Tarantula). Ours take a sixth of the light SXT takes where no star is.
- **The twin error under removed stars, by scale (2026-11-16, `C:/temp/e2/explore/twin_bands.py`, the mask and the
  quarter sample of `twin_err.py`, the error split by differences of Gaussians of sigma 1, 2, 4 and 8 px, rms / mean in
  the draw's noise sigma):**

| scale | R3v seed 0 | + floor3 | R3v seed 1 | + floor3 |
|---|---|---|---|---|
| under 1 px | 0.635 / -0.006 (29 %) | 0.571 / +0.001 (61 %) | 0.560 / -0.006 (36 %) | 0.536 / +0.000 (51 %) |
| 1-2 px | 0.429 / -0.020 (13 %) | 0.292 / -0.006 (16 %) | 0.334 / -0.017 (13 %) | 0.280 / -0.007 (14 %) |
| 2-4 px | 0.456 / -0.056 | 0.223 / -0.010 | 0.329 / -0.037 | 0.218 / -0.009 |
| 4-8 px | 0.391 / -0.085 | 0.154 / -0.009 | 0.269 / -0.046 | 0.155 / -0.006 |
| over 8 px | 0.680 / -0.291 | 0.221 / -0.061 | 0.511 / -0.179 | 0.362 / -0.062 |

  The share in brackets is a band's part of the SUM of the five band
  squares, not of the masked squared error. Per seed the five bands read 29 / 13 / 15 / 11 / 33 % (seed 0) and
  36 / 13 / 13 / 8 / 30 % (seed 1) from under 1 px to over 8 px, so the band past 8 px is the largest on seed 0 (by 4
  points) and the second on seed 1, where under 1 px is; on both it holds most of the bias (about 63 %), and it is the
  band the recipe's detail term (2-4 and 4-8 px) does not weigh. The bands are not orthogonal
  (their squares sum to well under the total), so the shares are a guide, not a budget; and this shows where the
  error is, not that the loss put it there. R3w is the test of that.
- **R3w, pre-registered 2026-11-16 before any score (an exploration under the owner's standing goal; the owner: a
  model that does not dig is better than a step after it). One change from R3v: the detail term's scales.** R3v's
  recipe and data exactly (synth22, `--levels 4 --max-ch 256 --clip-grad 8`, L2, the stored noise plane), with
  `--band-scales "2,4 4,8 8,16 16,32" --band-loss 6` in place of `"2,4 4,8"` and 3 (each of the two fine bands keeps
  its weight of 1.5, and the two coarse bands get the same; the 16-32 px band's wider blur has a radius of 96 px on a
  224 px interior, reflect-padded, so it reads mostly as a level over the window and less as 16 to 32 px structure, the
  same on the output and the target, and is to be read that way), CONTINUED from R3v's finished weights
  (`--init-from r3v_synth22_s0.pt`, new in `n2n_smoke.py`: the model begins there, the optimiser, schedule and plateau
  are fresh) at `--lr 5e-5 --max-decays 2 --patience 24`, capped at 60,000 steps, seed 0. From the frozen copy
  `C:/temp/e2/r3w-run` (R3v's trainer copy plus `--init-from`); a 400-step smoke of the same line ran before this was
  written (its weights deleted, nothing scored). It is a continuation, not a fresh run, so a pass says the term can
  move a trained model, and a fresh run with the term is the arm after it.
  - **The question, read on the model AS TRAINED, no step:** does the dig go.
    The twin's mean in the mask (`twin_err.py r3w_synth22_s0`) is within 0.15 sigma of zero (R3v seed 0 -0.51; with
    floor3 -0.08) and its rms in the mask at most 1.30 (1.92; 0.99). By count clean (`real_star_check3.py`) at least
    97.5 at 100-1000 sigma and 95 at 1000 and over (R3v seed 0 94.9 and 87.2; with floor3 99.4 and 100.0; SXT 98.3 and
    98.4). Andromeda dark dug at most 8 and dark flat at least 90 (22.4 and 69.5; 1.0 and 97.6; SXT 1.4 and 98.6).
  - **Guards, against R3v seed 0:** HIP 80609 at 20-100 and 100-1000 sigma at least 96.8 and 94.4 and Lobster at least
    96.8 and 96.6 (R3v's own bars, which seed 0 held at 99.7 / 97.1 and 99.8 / 99.3; a term that weighs 8 to 32 px
    structure could move them, so they are not dropped); M42 100-1000 clean at least 50; SMC 20-100 at least 89.4 and 100-1000 at least
    87.7; truth tiles at least 72 and 54; Tarantula dark flat at least 69; the M31 nucleus under 10 % in every channel
    (R3v at most 6); and the failure the one-sided term had (R3b: the dig traded for light left): left by count at
    most 2.0 at 100-1000 sigma (R3v seed 0 0.3) and at most 12 saturated (6.3; SXT 8.5).
  - **No control arm, deliberately:** R3v continued under the same flags with its own `"2,4 4,8"` term at 3 is not
    registered. The bars sit far outside R3v's seed spread (dug 22.4 and 24.3 against a bar of 11.2), so extra steps at
    a low rate on a fresh optimiser cannot account for a pass; if R3w passes the question, that control is run before
    the term is credited to the next fresh arm.
  - **Kill:** a guard fails, or Andromeda dark dug is not at least halved (over 11.2).
  - **Between the two (added 2026-11-16, before any score, on the review of PR #2182):** a result that is not killed
    but misses a bar of the question (for example the dig halved and every guard held, with 1000 and over by count
    under 95, or dark dug above 8 and at most 11.2) does not pass and is not killed: no seed 1, the rows are
    reported, and the owner decides. The bars stay as registered.
  - **Reported, no bar:** the twin error by scale as above, the glow rows, the crowded rows, M32 kept, HIP 80609 at
    1000 and over, M42's blue at 2 and 3 px, and every figure with floor3 applied on top.
  - **Prediction:** the dig falls by more than half and the twin's mean comes inside 0.15; by count 100-1000 clears
    97.5 and 1000 and over does not reach 95 on a continuation this short; left rises a little and stays inside its
    guards; M42's blue is not fixed.
  - **If it passes its question:** the same from R3v's seed 1, then the term goes into the next fresh arm. If it is
    killed, the coarse bands are not the lever at this weight, and the step stays the route. Nothing is adopted either
    way; adoption is the owner's.

- **R3x, pre-registered 2026-11-16 before any score (the owner: "kick off what you can with the data already
  uploaded"). One change from R3v: the data.** R3j's export (synth16, the drawn wings at their highest dose, the
  candidate named above) on R3v's model and recipe exactly (`--levels 4 --max-ch 256 --clip-grad 8`, the detail term at
  `"2,4 4,8"` and 3, a fresh run, seed 0), from R3v's frozen trainer copy (`C:/temp/e2/r3h-run`). It trains on an Azure
  low-priority T4 as a Batch job that removes itself (`training/azure/launch-batch.ps1`, tag `r3xaz`; the cache is in
  the same region), so a seed's arithmetic is the T4's, as R2i's seed 1 was. It is the arm the uploaded data allows; it
  does not carry the halo over the plate, the cluster scenes or R3w's coarser bands, which wait for an export.
  - **Judged WITH floor3 from the start** (the step is applied as registered, `dig_floor.py ... --tag floor3`, and the
    as-trained rows are reported beside it): R3j's small model as trained dug 45.0 % of Andromeda's dark-sky stars, and
    a model that draws wide wings is expected to dig.
  - **The question, the glow (green median, lower is better, with floor3):** Andromeda at most 450 and Tarantula at
    most 500. R3v with floor3 reads 599.4 and 1121.1 on seed 0 and 596.7 and 833.5 on seed 1; R3j's small model with
    floor3 416 and 395; SXT 242.8 and -30.8.
  - **Guards, with floor3, against R3v with floor3:** by count clean at least 98.5 at 100-1000 sigma and 98 at 1000
    and over (R3v 99.4 and 100.0, 99.2 and 100.0); M42 100-1000 clean at least 88 (94.9, 93.4); SMC 20-100 and
    100-1000 clean at least 97 (98.9 and 100.0 on both seeds); Andromeda dark flat at least 95 and dug at most 3
    (97.6 and 1.0, 97.5 and 1.2); Tarantula dark flat at least 91 (93.9, 93.5); the M31 nucleus under 10 % in every
    channel (R3j's small model 4.5 at most; R3i paid 11.7 in blue for its wings); the twin's rms in the mask at most
    1.15 (0.989, 0.978).
  - **Kill:** a guard fails, or the glow misses its bar on BOTH masters. One master cleared and one missed is
    neither: no seed 1, the rows are reported, the owner decides.
  - **Reported, no bar:** every row as trained; M32 kept (every arm so far removes it, so a bar would only restate
    that); the crowded rows; Tarantula's glow p90 and share over; saturated dug and left by count; M42's blue at 2 and
    3 px; the twin error by scale.
  - **Prediction:** Tarantula's glow clears 500 and Andromeda's lands near its bar, either side; the star rows hold
    with floor3; as trained it digs more than R3v; the nucleus stays under 10 %; M32 is removed.
  - **If the question is cleared and no guard fails:** seed 1, and the halo's answer is then wider drawn wings on
    this model, to be weighed against the halo over the plate. Nothing is adopted; adoption is the owner's.

- **R3x's read (2026-11-17): killed on the glow, on both masters.** It trained on the T4 to step 160,500 (best held-out
  value 5.971e-6 at 148,500), the Batch job ended and its pool was deleted by Batch; scored on DE1 as R3w was.

  | row, with floor3 | registered | R3v seed 0 | R3x |
  |---|---|---|---|
  | Andromeda glow, green median | at most 450 | 599.4 | 663.4 |
  | Tarantula glow, green median | at most 500 | 1121.1 | 1252.3 |
  | by count clean, 100-1000 / 1000 and over | at least 98.5 / 98 | 99.4 / 100.0 | 99.2 / 99.5 |
  | M42 100-1000 clean | at least 88 | 94.9 | 94.1 |
  | SMC 20-100 / 100-1000 clean | at least 97 | 98.9 / 100.0 | 98.9 / 100.0 |
  | Andromeda dark flat / dug | at least 95 / at most 3 | 97.6 / 1.0 | 97.5 / 0.7 |
  | Tarantula dark flat | at least 91 | 93.9 | 92.3 |
  | M31 nucleus, largest channel | under 10 % | 6.0 % | 5.8 % |
  | twin rms in the mask (the registered way, the rim included) | at most 1.15 | 0.989 | 0.981 |

  Every guard held and the question failed on both masters, in the wrong direction: the widest drawn wings on the
  four-level model leave MORE glow than R3v, not less (as trained 708.8 and 1127.4). The prediction (Tarantula under
  500, Andromeda near its bar) was wrong; that the star rows hold with floor3, that it digs more as trained and that
  the nucleus stays under 10 % were right. The small model's glow under these wings (R3j with floor3, 416 and 395)
  did not carry to the larger model, so wider drawn wings are not the halo's answer on it.
  - **As trained, reported:** Andromeda dark flat 37.7 and dug 51.4 (R3v 69.5 and 22.4); by count clean 90.1 at
    100-1000 and 70.7 at 1000 and over (94.9, 87.2), dug 9.3 and 28.7; M42 100-1000 clean 20.6 with 77.2 dug (56.6,
    42.6); SMC 100-1000 72.9 (89.8); HIP 80609 100-1000 94.1 (97.1); Tarantula dark flat 69.7, left 23.6; crowded
    Tarantula 82.5, BP17-19 92.6, Sagittarius 94.4; M42's blue -1.66 and -1.11. The twin inside the rim reads as
    R3v's in its rms (0.704 against 0.700, mean -0.081 against -0.078; with floor3 0.759), an aggregate the faint
    stars dominate. By star (`real_star_check3.py --twin`, TRUE dug) the drawn stars are alike at 100-1000 sigma
    (0.8 against R3v's 0.9) and dug more often by R3x above that: 9.7 against 6.2 at 1000 and over (TRUE clean 89.6
    against 93.4) and 17.0 against 10.6 saturated (83.0 against 88.3). So drawn bright stars are dug about 1.6 times
    as often as by R3v, where real ones at 1000 and over are dug 2.4 times as often (28.7 against 12.2).
  - **The registered rows the lines above leave out.** M32 is removed, as predicted: R3x keeps 1.9 / 1.6 / 2.6 % of
    its core flux in red, green and blue (R3v 2.3 / 2.3 / 1.1, StarXTerminator 42 to 44). The glow's p90 and share
    over, with floor3: Andromeda 1881.6 and 92 % (R3v 1549.7 and 89 %), Tarantula 2129.1 and 92 % (1726.2 and 92 %).
    Saturated stars by count: as trained clean 68.4, dug 24.2, left 16.8 (R3v 78.9, 16.8, 6.3; StarXTerminator
    89.4, 2.1, 8.5), so as trained R3x leaves more saturated stars than R3v or StarXTerminator, a regression the
    guards do not read; with floor3 93.7, 4.2, 3.2 (R3v 91.6, 5.3, 4.2). The twin error by scale, rms in the mask in
    sigma, under 1 px / 1-2 / 2-4 / 4-8 / over 8 px: R3x 0.614 / 0.479 / 0.548 / 0.460 / 0.782 against R3v's 0.635 /
    0.429 / 0.456 / 0.391 / 0.680, so R3x is worse at every scale from 1 px up; with floor3 0.563 / 0.281 / 0.219 /
    0.155 / 0.230.
  - **What it adds to the flank reading above:** synth16 draws half its cells at beta 1.3 to 2.5 with no channel
    spread, the fattest stars of any export, and on the same model as R3v it digs more than twice as many real stars
    (51.4 against 22.4). That is consistent with the flank reading and with the channel spread alike, and does not
    tell them apart: R3x's export has no channel spread where R3v's has 3, the spread alone took the dig from 45.1
    (R3l) to 25.3 (R3s), and R3j on the small model with this same export dug 45.0, so 51.4 is what no spread
    already predicts. What it does show is that the same export digs alike on the small and the four-level model
    (45.0 and 51.4).

- **R3w's read (2026-11-16): killed.** It stopped at step 51,500 (best held-out value at 27,500). As trained:

  | bar | registered | R3v seed 0 | R3w |
  |---|---|---|---|
  | twin, mean in the mask | within 0.15 of zero | -0.51 | -0.507 |
  | twin, rms in the mask | at most 1.30 | 1.92 | 1.873 |
  | Andromeda dark dug / flat | at most 8 (kill over 11.2) / at least 90 | 22.4 / 69.5 | 29.7 / 62.7 |
  | by count clean, 100-1000 / 1000 and over | at least 97.5 / 95 | 94.9 / 87.2 | 94.7 / 86.2 |
  | SMC 100-1000 clean (guard) | at least 87.7 | 89.8 | 84.7 |

  Killed on the rule (the dig rose) and on the SMC guard. HIP 80609 100-1000 96.9, M42 100-1000 55.9 and the truth
  tiles (73.9 / 58.4) held. The bias past 8 px is unchanged (-0.285 against -0.291). With floor3 on top (R3w seed 0 plus
  floor3, its own whole-master read) Andromeda reads 97.5 flat / 1.2 dug and Tarantula dark 93.3 / 5.4 left, against R3v
  seed 0 plus floor3's 97.6 / 1.0 and 93.9 / 4.9 (the table above): the same within a tenth or so, so R3w adds nothing
  under floor3 either. The coarse bands are not the lever. One line of the prediction held:
  1000 and over by count did not reach 95 (86.2). The rest was wrong: the dig rose instead of falling by more than half,
  and the SMC guard failed (84.7 against 87.7).
- **Why no loss term could have done it: four measurements after R3w (2026-11-16).**
  1. **The model does not dig on its own kind of data.** The twin's figures read on the training cache's held-out
     cells (`train_dig.py`, 160 cells, two draws, the loss's own rim left out): mean in the mask -0.021 (R3v seed 0),
     -0.010 (seed 1), -0.024 (R3w); rms 0.50 on all three.
  2. **The twin's figures were the tile rim.** `twin_err.py` reads whole 256 px tiles, the 16 px rim included; the loss
     leaves the rim out and the whole-master runner gives it no weight (`StarRemoverRunner`'s triangle is zero there).
     The same twin tiles, the same model, the rim left out (`twin_err_inner.py`): R3v seed 0 mean
     -0.08 and rms 0.70 (the same tiles with the rim: -0.51 and 1.92, as in the table above; both are the quarter sample,
     `i % 4`, and the mask is R3v seed 0's), seed 1 -0.06 and 0.70, R3w -0.08 and 0.70. Inside the rim floor3 does not
     lower the twin's rms, it **raises** it: 0.70 to 0.76 on seed 0 (+9 %, mean -0.08 to -0.06) and 0.70 to 0.76 on seed 1
     (+8 %, mean -0.06 to -0.05), R3w 0.70 to 0.76 (mean -0.08 to -0.06). What floor3 filled on the twin was the rim, and
     on the pixels inference uses it costs a little rms, so the proposal of floor3 to the owner carries that rise. **So every twin
     "in the mask" figure above this point (R3v's 1.92 and 1.41, floor3's 0.99 and 0.98, the dig step's missed twin
     bar, R3w's two twin bars, the error by scale) describes pixels inference never uses, and is void as a measure of
     the dig.** The real-star rows and the whole-master rows are not touched by this: `real_star_check3.py` keeps its
     stars off the rim, and a whole master is run with the rim discarded. R3x's twin guard stays as registered and is
     read the registered way, with the inside-the-rim figure beside it.
  3. **On the twin, the wing draw does not move the error and the plate background does** (`dig-2x2.ps1`: the eight validation sessions
     exported four ways from one binary, 427 tiles each, the rim left out; R3v seed 0 / seed 1):

     | export | rms in the mask | mean in the mask |
     |---|---|---|
     | synthetic background, wings drawn | 0.524 / 0.514 | -0.020 / -0.008 |
     | synthetic background, no wing draw | 0.512 / 0.507 | -0.019 / -0.004 |
     | plate background, wings drawn | 0.750 / 0.749 | -0.092 / -0.079 |
     | plate background, no wing draw | 0.747 / 0.750 | -0.093 / -0.077 |

     A real plate costs about 0.07 sigma of bias and half again the rms; the wing draw nothing. This 2x2 reads the twin
     (drawn stars), which item 4 says are not dug, so it rules out the wing draw as a source of the twin's error and says
     the plate raises it; it cannot clear either factor as the cause of the dig on real stars, which only item 4's real
     rows measure.
  4. **Drawn stars on real plates are not dug; real stars in the same sessions are.** At 100-1000 sigma the twin's
     drawn stars are truly dug 0.9 % of the time (`real_star_check3.py --twin`, R3v seed 0; by star, and the TRUE dug,
     the output less the plate), the real stars of the same four sessions 4.7 % by count (the measured verdict, which
     the script's header says under-counts a wide dig; n=1739; M42 alone 42.6 %, 136 stars). The 13.6 % that stood here
     was the four sessions weighted equally, which the by-count rule above says overstates. The contrast stays, as about
     5 to 1 and on two instruments, not 15 to 1. On the real masters, against the plate (smooth there: nearly every bright site
     is inpainted, and its ring sits within 0.1 sigma of its local sky), R3v's whole-master output in rings round
     each catalogued star, in the star's catalogued sigma (`site_core.py`, medians; M42 1,560 stars and HIP 80609
     2,807 at 100-1000 sigma):

     | radius, px | 0-0.8 | 0.8-1.6 | 1.6-2.5 | 2.5-3.5 | 3.5-4.5 | 4.5-6 |
     |---|---|---|---|---|---|---|
     | M42, output less local sky | +0.59 | +0.57 | -2.75 | -0.50 | -0.17 | -0.01 |
     | M42, the star's own light there | 420 | 227 | 59 | 26 | 12 | 5.5 |
     | HIP 80609, output less local sky | +0.41 | -0.19 | -0.02 | -0.52 | -0.53 | -0.27 |

     On M42 the error is a shape error on its flank: a narrow ring taken at about 2 px, some 5 % of the star's light at
     that radius (2.75 / 59 = 4.7 %), which is the model removing a star a little wider than the real one. The core
     excess (+0.59, +0.57) is under the 1 sigma clean threshold and 0.1 to 0.3 % of the star's own light, so it is noise
     level, not light left. HIP 80609 shows no ring at 1.6-2.5 px (-0.02) and takes its -0.52 / -0.53 at 2.5-4.5 px, a
     different radius. Its own-light row (`site_core.py`, same six radii) is 360.5, 162.8, 39.9, 11.0, 4.0, 2.1, so its
     -0.52 at 2.5-3.5 px is 4.7 % of the star's light there (0.52 / 11.0, the same share as M42's 2.75 / 59) and its
     -0.53 at 3.5-4.5 px is 13 %. The share at the ring is alike on both instruments, the radius is not, so this ring is
     not shown to be general in where it sits. The
     rows above are in the star's catalogued sigma divided by root 3 (`site_core.py`, the mean of the three planes); the
     per-channel figures (`site_core_ch.py`) are in the catalogued sigma itself, root 3 smaller, which is why they do not
     average to the row (about -1.6 times root 3 is -2.8 against -2.75). On M42 per channel it is red (-2.68 at 1.6-2.5
     px) and blue (+1.25 at 0.8-1.6, -1.58 at 1.6-2.5), green barely (-0.54).
     A transplant (`transplant.py`: the same star layer moved 37, 53 px onto plate that never held a star) is the one
     test that holds the star layer fixed and moves the site, and it is not read as a verdict: the moved star carries its
     old site's noise as well as the new one's, which can explain a good deal by itself. Ring at 2-4 px, median in
     sigma, real site to moved: M42 100-1000 -1.40 to -0.03 and 1000 and over -1.88 to -3.99; HIP 80609 100-1000 -0.52
     to -0.42 and 1000 and over -1.10 to -1.72. Only M42 at 100-1000 loses its ring when the star is moved, and that is
     the reading the doubled noise has to explain (its core is left more, +1.57, with the ring unbiased); in the other
     three the ring stays or deepens, so there the dig follows the star and not the site. That points to flank shape
     over site for those rows, and leaves M42 100-1000 as the exception.
  - **What follows (from M42's rows; HIP's ring sits at a different radius).** The dig is SUSPECTED to be the gap between a drawn star's flank and a real one's, per channel and per star, at
    1.5 to 3.5 px (item 5 measures the gap on HIP 80609, Lobster and M42 and not on Andromeda, and its size does not match the
    dig where a beta draw can reach it; R3y below is the test, not a finding). A loss term on the present data cannot see it (R3w, and probably why R3b traded the dig for light
    left), wider or narrower wings past 4 px do not address it, and the halo over the plate (synth25) is about 8 px
    and beyond. The candidate that does: draw a share of the training stars from the masters' own measured stars (the
    master less its inpainted plate, which is the real star whole) instead of the field profile, as
    `--saturated-measured` does for clipped stars. It is not registered here; it needs injector work first.
  5. **The drawn star is fatter than the real one at equal core height, and the wing draw's green never went sharper than beta 6** (`flank_cmp.py`, the same evening). Real: the validation masters' tiles (`r2-real-val`) at the plate
     catalogue's unsaturated stars of 50 sigma and over. Drawn: the same sessions' stars on their plates from the
     current injector (`dig-2x2/plate-plain`, the field profile, no wing draw). Both in the tiles' stretched domain, sky
     taken at 12 to 20 px, stars binned by green's peak, each ring as a share of the core (0 to 0.8 px); medians, green,
     peak 0.25 to 0.5 (red and blue read the same way):

     | session | | 1.6-2.5 px | 2.5-3.5 | 3.5-4.5 | 4.5-6 |
     |---|---|---|---|---|---|
     | HIP 80609 | real (307 stars) | 0.215 | 0.062 | 0.018 | 0.007 |
     | | drawn, field profile (503) | 0.270 | 0.095 | 0.036 | 0.019 |
     | | drawn, `--star-beta 8` | 0.206 | 0.059 | 0.029 | 0.014 |
     | | drawn, `--star-beta 20` | 0.183 | 0.037 | 0.019 | 0.025 |
     | Lobster | real (388) | 0.169 | 0.034 | 0.008 | 0.003 |
     | | drawn, field profile (452) | 0.181 | 0.046 | 0.018 | 0.012 |
     | | drawn, `--star-beta 8` | 0.164 | 0.032 | 0.014 | 0.012 |
     | | drawn, `--star-beta 20` | 0.147 | 0.023 | 0.009 | 0.008 |
     | M42 | real (118) | 0.238 | 0.119 | 0.052 | 0.019 |
     | | drawn, field profile (147) | 0.250 | 0.103 | 0.040 | 0.016 |
     | | drawn, `--star-beta 8` | 0.138 | 0.020 | 0.002 | 0.001 |

     On HIP 80609 and Lobster the field profile draws 1.04 to 1.27 times real's light at 1.6 to 2.5 px and 1.2 to 1.6 times
     at 2.5 to 3.5 px, in every channel, and at 1.6 to 3.5 px real sits at or between the field profile and a Moffat of
     beta 8 at the field's width. Past 3.5 px that does not hold: real is under beta 8 (HIP 0.018 against 0.029 at 3.5-4.5 and
     0.007 against 0.014 at 4.5-6; Lobster 0.008 against 0.014 and 0.003 against 0.012) and about equal to beta 20 at 3.5-4.5
     (0.018 against 0.019, 0.008 against 0.009) and under it at 4.5-6. The far rings also have a floor: HIP's beta 20 reads
     0.025 at 4.5-6, above beta 8 (0.014) and the field profile (0.019), which a Moffat at fixed width cannot do (a higher
     beta is thinner), so something else sets about 0.01 to 0.02 there. The likeliest part is the field profile's residual table: a drawn
     beta moves the Moffat and keeps the table as it was (`StarBeta`'s remarks), so every beta arm carries the field's own
     far residual, while real reads under that level (0.007 and 0.003 at 4.5 to 6 px); it is not separated here from plate
     residual, noise or the 12 to 20 px sky annulus. The flank claim
     is therefore made for 1.6 to 3.5 px only; at 3.5 to 6 px the table cannot separate the arms from that floor, and a draw
     up to beta 12 stops short of real's far flank by it. M42 is the other kind: its real stars carry more at 2.5 to 4.5 px than the field profile and less at
     1.6 to 2.5 px in red and blue (0.217 and 0.169 against 0.249 and 0.209), and no single beta draws them. No
     wing draw trained so far went sharper than beta 6: R3i, R3j and R3k drew 1.3 to 2.5, R3l 0.3,1.3,6 and R3t and R3v
     0.3,1.3,6 with a channel spread of 2 and 3. That is the draw's high end for GREEN, not for every star: a held cell's red and
     blue betas are green's times a factor log-uniform in [1/spread, spread] (`StarBetaDraw.DrawCell`), so at spread 3 they
     reach 18, and the cells not held keep their field's own beta. In synth22 (the review's read of 1 record in 40 of 22,080)
     (`beta_over_share.py`, 552 records, 213,257 stars) green's beta is over 6 on 3.4 % of the stars, red's on 21 %, blue's on 19 %, and 34 % have at least one channel over 6.
     So sharp channels were in the training data; a star sharp in green, or in all three at once, by the draw, was not. Where the archive's own betas sit
     is not tabulated here (only the lowest fit, 1.63, and M42's 3.06 / 1.72 / 4.54), so nothing is claimed about a draw near 6
     against a session's own beta, and on Andromeda's dark sky the arms that drew wings without a channel spread dug 45.0 and 45.1 (R3j, R3l) against
     R2w's 28.8 with none. The stretched domain bends both sides alike at a matched peak; it is a
     comparison, not a profile in linear light.
- **R3y, pre-registered 2026-11-16 before any export or score. Three differences from R3v, named: the share of held cells 0.3 to 0.5, the upper beta 6 to 12, and the binary.**
  synth26 = `training/star-removal/export-synth.ps1 -Name synth26 -BetaDraw 0.5,1.3,12,3` with no `-Extra` (synth22's
  line with the share raised from 0.3 to 0.5 and the upper beta from 6 to 12). By `StarBetaDraw.DrawCell` (a cell is held
  with probability share, beta uniform in [low, high]) the cells under beta 6 go from 0.30 to 0.5 x (6-1.3)/(12-1.3) = 0.22,
  a quarter FEWER than R3v's, the cells over beta 6 are 0.5 x 6/10.7 = 0.28 (none in R3v), and the cells on the field
  profile are 0.50 (0.70 in R3v). The arm is the three together, from the binary frozen
  at `D:/temp/tianwen-scratch/bh-bin8` (main with the halo option, which is off; synth22 came from an older binary, so
  the export is not byte-comparable to synth22 outside the drawn cells and that is a third, unmeasured difference).
  **A change in Andromeda dark dug is read against the measured record**, whole master, as trained: R2w (no wing draw)
  28.8; R3j (0.5,1.3,2.5, the dose up) 45.0 and R3l (0.3,1.3,6) 45.1, level with each other; R3s (R3l's share and range
  with the channel spread 3) 25.3; R3v (R3s's data on the four-level model) 22.4 and 24.3. So drawing wings at all cost
  about 16 points, the dose and range between R3j and R3l moved nothing (R3l's "at or under R2w" was its prediction, and
  it failed), and the channel spread took the cost back. For R3y that means two things. The smaller share under beta 6
  (0.22 against 0.30) is not shown to predict less dig by itself, since two doses dug alike; it is not shown to be
  harmless either. And the held share rises from 0.3 to 0.5, so more cells carry the channel spread, the one change on
  record that cut the dig. A fall from 22.4, the pass bar and the prediction included, therefore cannot be credited to
  green's sharper betas alone: more spread cells could give it. The readings that speak to the sharp draw are no change
  or a rise, and, beside the dig, the ring at real sites on HIP 80609 at 2.5 to 4.5 px, which neither the dose nor the
  spread is on record as moving. A pass sends the arm on; it does not name the cause.
  Trained on R3v's model and recipe exactly (`--levels 4 --max-ch 256 --clip-grad 8`, the detail term at `"2,4 4,8"`
  and 3, a fresh run, seed 0) from R3v's frozen trainer copy, as a Batch job that removes itself (tag `r3yaz`).
  - **Q1, the export, before training** (`flank_cmp.py <session key> <export> --min-beta 8` on the validation sessions
    exported with the same line): the population is the drawn stars at green peak 0.25 to 0.5 whose green Beta (read from
    injections.jsonl) is 8 or over, about 0.5 x (12-8)/10.7 = 0.19 of all cells (0.37 of the held cells). Expected: at beta 8
    the table reads 0.206 against real's 0.215 on HIP and 0.164 against 0.169 on Lobster at 1.6 to 2.5 px, so this
    population's median is just under real's, and at least a quarter of it (the script's "under real median" column) lies
    under real's median at 1.6 to 2.5 px and at 2.5 to 3.5 px, on HIP 80609 and on Lobster, in green. Red and blue are
    reported, not barred (their betas are the cell's green beta times the channel spread of 3). The share of ALL drawn stars
    under real's median is reported beside it and is expected to be about 0.19 or under, short of a quarter, because half the
    cells stay on the field profile (HIP 1.6-2.5 px median 0.270 over real's 0.215); that is not a failure. Q1 reads 1.6 to
    3.5 px only. The far flank (3.5 to 6 px, where real is under beta 8 and about beta 20, above item 5's 0.01 to 0.02
    floor) is reported with no bar and is not reached by a draw to 12. If Q1 fails, the range does not reach real and the
    arm is not trained.
  - **Q1's read (2026-11-16, the validation sessions exported with the line, `dig-2x2/synth-draw12`): the quarter bar is cleared and did not discriminate; the contrast is the read.** Green, peak
    0.25 to 0.5, the stars at green beta 8 and over: 0.59 and 0.54 of them under real's median at 1.6 to 2.5 and 2.5 to
    3.5 px on HIP 80609 (108 stars; their median 0.201 and 0.055 against real's 0.215 and 0.062), 0.66 and 0.69 on Lobster
    (68 stars; 0.140 and 0.018 against 0.169 and 0.034). Red and blue, reported: HIP 0.48 / 0.51 and 0.51 / 0.51, Lobster
    0.74 / 0.69 and 0.65 / 0.59. Of ALL drawn stars 0.32 and 0.26 (HIP) and 0.38 and 0.31 (Lobster) are under real's median,
    where this registration expected about 0.19 or under, short of a quarter: that expectation failed on all four cells
    (a held cell's stars under beta 8 and the scatter of single stars count too), so the bar of a quarter is cleared by the
    whole export and could not have failed. What the read shows is the contrast: the stars at beta 8 and over sit under
    real's median about twice as often as all drawn stars (0.59 / 0.54 against 0.32 / 0.26 on HIP, 0.66 / 0.69 against
    0.38 / 0.31 on Lobster) and their median is at or under real's, where the whole export's is over it (HIP 0.252 and
    0.089 against 0.215 and 0.062). The arm goes on, on that contrast and not on the bar. The far
    flank, reported with no bar (green, the medians of the stars at beta 8 and over against real's): HIP 0.034 and 0.020
    against 0.018 and 0.007 at 3.5 to 4.5 and 4.5 to 6 px, Lobster 0.007 and 0.008 against 0.008 and 0.003; so it is not
    reached on HIP, as the registration said, and on Lobster only to 4.5 px. This
    registration's first wording of Q1, before the review replaced it (the lower quartile of all drawn stars under real's
    median in every channel), reads 11 cells of 12 on the same export: HIP's blue at 2.5 to 3.5 px misses (quartile 0.0345
    against a median of 0.0341). The export line is unchanged.
  - **The question, on the model AS TRAINED, no step:** Andromeda dark dug at most 11.2 (half of R3v seed 0's 22.4)
    with dark flat at least 80 (69.5), and by count clean at 100-1000 sigma at least 97 (94.9).
  - **Guards, as trained, against R3v seed 0:** M42 100-1000 clean at least 50 (56.6) and M42's blue ring at 1.6 to 2.5 px
    (`site_core_ch.py`, catalogued sigma; R3v's is -1.58, item 4) no lower than -3.08, 1.5 sigma further from zero (that
    allowance is as large as R3v's whole ring and lets the dig double without failing, so it is a loose guard); SMC 20-100 at least 89.4 and 100-1000 at least 87.7; HIP 80609
    100-1000 at least 94.4 and Lobster at least 96.6; left by count at most 2.0 at 100-1000 sigma (0.3) and at most 12
    saturated; truth tiles at least 72 and 54; Tarantula dark flat at least 69 and left at most 20 (16.0); the M31
    nucleus under 10 % in every channel.
  - **Kill:** a guard fails, or Andromeda dark dug stays over 16.8 (not cut by a quarter). Between 11.2 and 16.8 with
    every guard held it neither passes nor is killed: no seed 1, the rows are reported, the owner decides.
  - **Reported, no bar:** every row with floor3; the glow rows; the crowded rows; M32 kept; the output's ring profile
    at real sites on M42 and HIP 80609 (`site_core.py`) beside R3v's; the twin inside the rim.
  - **Prediction (the likely outcome is "owner decides"):** dug falls to between 10 and 16, almost wholly inside the 11.2 to 16.8 band that neither passes nor kills and HIP 80609's ring at 2.5 to 4.5 px rises towards zero; M42's
    ring at 1.6 to 2.5 px does not move (no beta draws its stars); left at 100-1000 rises to about 1; Tarantula's left
    rises a little.
  - **If it passes:** seed 1, and the measured-star draw above is then the way to reach M42's kind. If it is killed
    on the dig, the flank at equal core height is not the lever by a beta draw, and the measured stars are tried
    before the idea is dropped. Nothing is adopted; adoption is the owner's.
  - **Seed 1, registered 2026-11-17 on PR #2189 (a comment of 01:04 UTC) before any score of either seed** (the owner:
    "ideally we use both GPU and Azure"). R3y's recipe and data exactly, the trainer bundle the `r3yaz` job runs, only
    `--seed 1`, on the GTX 1070 (DE1) where seed 0 trains on the T4, so the pair's difference also holds the two GPUs'
    arithmetic. It is read with seed 0 against this registration as it stands: no new bar, none moved after either
    seed is seen. This replaces "If it passes: seed 1" above only in the order the seeds train; a kill by seed 0 is
    not undone by seed 1.
  - **Read 2026-11-18: both seeds killed, each by the dig itself and by guards. A sharper drawn flank by a beta draw is
    not the lever.** Seed 0 trained on the T4 (five evictions), seed 1 on the GTX 1070. As trained unless said:

    | row | bar | seed 0 | seed 1 | R3v seed 0 | verdict |
    |---|---|---|---|---|---|
    | Andromeda dark dug | at most 11.2; kill over 16.8 | 24.4 | 34.0 | 22.4 | **kill, both** |
    | Andromeda dark flat | at least 80 | 68.6 | 58.8 | 69.5 | failed, both |
    | by count clean, 100-1000 | at least 97 | 93.9 | 96.1 | 94.9 | failed, both |
    | M42 100-1000 clean | at least 50 | 42.6 | 64.0 | 56.6 | **failed, seed 0** |
    | M42 blue ring, 1.6 to 2.5 px, 100-1000 (`site_core_ch.py`) | no lower than -3.08 | -2.53 | -1.70 | -1.58 | held |
    | SMC 20-100, 100-1000 clean | at least 89.4, 87.7 | 98.3, 84.7 | 92.7, 91.5 | 97.2, 89.8 | **failed, seed 0** |
    | HIP 80609, Lobster 100-1000 clean | at least 94.4, 96.6 | 97.7, 99.4 | 98.7, 99.4 | 97.1, 99.3 | held |
    | left by count, 100-1000 and saturated | at most 2.0, 12 | 0.2, 7.4 | 0.6, 11.6 | 0.3, 6.3 | held |
    | Tarantula dark flat, left | at least 69, at most 20 | 74.8, 18.3 | 69.7, 23.3 | 71.9, 16.0 | **failed, seed 1 (left)** |
    | M31 nucleus R / G / B | under 10 % | 21.8 / 27.8 / 31.6 | 5.9 / 11.0 / 5.6 | | **failed, both** |

    The truth tiles guard (72 and 54) was read on 2026-11-18 (`f2-eval-master/<model>/on-random/starless-eval-v5.json`, the output's clean rate at 20-100 and 100-1000) and holds on both seeds, 73.3 / 55.7 and 72.9 / 54.2 (R3v 73.3 / 58.1, R3z 72.7 / 56.8, R4a's seeds 73.5 / 59.5 and 72.8 / 58.2); the verdict did not hang on it. R3v's dark dug is the figure the
    bar of 11.2 was set from (half of it).
    - **The prediction failed.** Dug was to fall to between 10 and 16; it rose on both seeds. The two side predictions
      held: M42's ring at 1.6 to 2.5 px did not move (all channels, 100-1000: R3v -2.75, seed 0 -3.10, seed 1 -2.55), and
      HIP 80609's ring at 2.5 to 4.5 px rose towards zero by under 0.1 sigma (R3v -0.52 / -0.53, seed 0 -0.44 / -0.47,
      seed 1 -0.44 / -0.45), far too little to count.
    - **The seeds differ by more than the arm does from R3v**: 9.6 points on the dig, 21.4 on M42's 100-1000 row, and
      the nucleus fails by a factor of three on one seed and by one point on the other. A one-seed difference of that
      size on these rows is not a finding, in this arm or an earlier one.
    - **With floor3 the two seeds read alike** and like R3v: Andromeda dark flat 97.7 / 97.5 with 1.3 / 1.2 dug,
      Tarantula dark flat 93.7 / 93.8 with 5.3 left on both, by count clean at 100-1000 99.3 / 99.1 and saturated
      92.6 / 93.7, Tarantula crowded 89.8 on both, BP 17-19 95.1 / 93.6, Sagittarius crowded 95.7 / 96.9. The glow is
      R3v's (Andromeda green median 546.4 / 559.4), as it should be: this arm has none of R3z's halo.
    - **Ring profiles at real sites (`site_core.py`, output less its own sky in noise sigma, 100-1000, radii 0.8 to
      1.6, 1.6 to 2.5, 2.5 to 3.5, 3.5 to 4.5 px).** M42: R3v 0.57 / -2.75 / -0.50 / -0.17, seed 0 0.07 / -3.10 / -0.82 /
      -0.57, seed 1 0.15 / -2.55 / -0.55 / -0.22, R3z 0.37 / -2.21 / -0.14 / 0.02. HIP 80609: R3v -0.19 / -0.02 / -0.52 /
      -0.53, seed 0 -0.27 / -0.20 / -0.44 / -0.47, seed 1 -0.34 / -0.11 / -0.44 / -0.45, R3z 0.12 / 0.26 / -0.21 / -0.31.
      R3z, whose data differ in the halo and the saturated stars and not in the flank, has the shallowest ring on both.
      The other two sessions, read 2026-11-18 (`site-run-rest.sh`, same rows; like `site-run.sh`, which made the
      M42 and HIP 80609 rows per model, it runs `site_remove.py`, the real-site half of `transplant.py` with one output
      folder per model, then `site_core.py`; only `site-run.sh` goes on to `site_core_ch.py`, for M42's blue, so the
      rows here are `site_core.py`'s). Lobster (5,818 stars): R3v -0.04 / 0.04 /
      -0.44 / -0.52, seed 0 -0.23 / 0.04 / -0.35 / -0.43, seed 1 -0.25 / -0.11 / -0.40 / -0.48. SMC (383 stars): R3v
      0.56 / 0.06 / -0.19 / -0.29, seed 0 0.00 / 0.15 / -0.16 / -0.44, seed 1 0.21 / 0.60 / 0.34 / -0.12. Neither has
      M42's ring at 1.6 to 2.5 px; Lobster has HIP 80609's shallow dip of about half a sigma from 2.5 to 4.5 px in
      all three models, and the SMC's rows differ between the seeds by more than they differ from R3v. So the sharper
      draw moved none of the four sessions' rings, and the deep ring is M42's alone.
    - **What follows, as registered:** the flank at equal core height is not the lever by a beta draw; the measured
      stars are tried before the idea is dropped, and that is R4b below. The beta draw to 12 stops here.
- **R3z, pre-registered 2026-11-17 before any score. The halo's other route: the stars' own measured halos and the
  halo the plate keeps, on R3v's model.** synth25 = `export-synth.ps1 -Name synth25 -BetaDraw 0.3,1.3,6,3 -Extra
  --saturated-own-share,--saturated-measured,--halo-over-plate` from `bh-bin8` (exported and uploaded in the night of
  2026-11-16; 4,840 cells, 4,520 train and 320 held out), trained on R3v's model and recipe exactly, seed 0, as a Batch
  job that removes itself (tag `r3zaz`). Against R3v's data it differs in four named things: saturated stars drawn at the
  master's own share, drawn as measured, the halo over the plate (validated above: the drawn ring at 15, 20 and 25 px
  goes from 0.48 / 0.31 / 0.21 of real to 0.75 / 0.73 / 0.56), and the binary. The first two were R3r's on the small
  model (killed there; its section above), so a kill here on a star guard may repeat R3r and does not by itself
  speak to the halo over the plate.
  - **Judged WITH floor3**, the as-trained rows beside it, as R3x was.
  - **The question, the glow (green median, lower is better, with floor3):** Andromeda at most 450 and Tarantula at most
    500 (R3x's bars; R3v 599.4 and 1121.1, R3x 663.4 and 1252.3, SXT 242.8 and -30.8).
  - **Guards, with floor3:** R3x's, unchanged: by count clean at least 98.5 at 100-1000 sigma and 98 at 1000 and over;
    M42 100-1000 clean at least 88; SMC 20-100 and 100-1000 clean at least 97; Andromeda dark flat at least 95 and dug at
    most 3; Tarantula dark flat at least 91; the M31 nucleus under 10 % in every channel; and, since the drawn halo is
    light the model is taught to take, M32 kept is reported with the nucleus. The twin's rms is read inside the rim
    (`twin_err_inner.py`) and reported, with no bar: the registered-way figure is the rim's.
  - **Kill:** a guard fails, or the glow misses its bar on BOTH masters. One master cleared and one missed is neither:
    no seed 1, the rows are reported, the owner decides.
  - **Reported, no bar:** every row as trained; saturated dug and left by count (the rows the measured stars are for);
    the glow's p90 and share over; the crowded rows; the ring profile at real sites (`site_core.py`).
  - **Prediction:** the glow falls on both masters against R3v, by less than is needed: Andromeda lands between 450 and
    600 and Tarantula stays over 500, because the drawn ring still holds a quarter less than real's and the faint
    stars' halo is the field's median, not their own. Saturated left falls. The guards hold with floor3.
  - **If the question is cleared:** seed 1. Nothing is adopted; adoption is the owner's.

- **R3z, read 2026-11-17: killed by one guard, M31's nucleus; the glow question is cleared on both masters, far past
  the prediction.** The job trained to step 173,000 (best held-out 7.49e-6 at 149,000, converged after the fourth
  halving; one preemption, resumed from its checkpoint), its pool gone when the weights were fetched. With floor3, as
  registered, beside R3v seed 0 with floor3 and SXT:

| row | bar | R3z | R3v s0 | SXT | verdict |
|---|---|---|---|---|---|
| glow, Andromeda, green median / p90 / over | at most 450 | -146.9 / 700.1 / 17 % | 599.4 / 1549.7 / 89 % | 242.8 / 574.8 / 59 % | cleared |
| glow, Tarantula, green median / p90 / over | at most 500 | -29.0 / 358.8 / 23 % | 1121.1 / 1726.2 / 92 % | -30.8 / 203.4 / 8 % | cleared |
| M31 nucleus, R / G / B, one view | under 10 % each | 9.8 / 31.3 / 39.2 % | -1.0 / 6.0 / 1.6 % | 0.1 / 0.1 / 0.1 % | **failed** |
| by count clean, 100-1000 sigma | at least 98.5 | 99.2 | 99.4 | 98.3 | held |
| by count clean, 1000 sigma and over | at least 98 | 100.0 | 100.0 | 98.4 | held |
| M42 100-1000 clean | at least 88 | 94.1 | 94.9 | 91.9 | held |
| SMC 20-100 / 100-1000 clean | at least 97 | 98.9 / 100.0 | 98.9 / 100.0 | 98.3 / 96.6 | held |
| Andromeda dark flat / dug | at least 95 / at most 3 | 97.8 / 1.0 | 97.6 / 1.0 | 98.6 / 1.4 | held |
| Tarantula dark flat | at least 91 | 95.1 | 93.9 | 96.6 | held |

  - **The kill is the registration's and stands.** The prediction was wrong on the question (it said Andromeda between
    450 and 600 and Tarantula over 500) and right that the guards other than the nucleus hold.
  - **The glow has a sign, and "lower is better" stops at zero.** The row is the plate less its local sky summed over
    the 10 to 30 px annulus (about 2,500 px), so R3v's 599.4 is +0.24 sigma a pixel, SXT's 242.8 is +0.10 and R3z's
    -146.9 is -0.06: R3z takes a little more than the halo on Andromeda. By channel, with floor3: Andromeda R / G / B
    -298.9 / -146.9 / -77.0 (SXT 156.7 / 242.8 / 391.4), Tarantula 347.3 / -29.0 / 234.0 (SXT -39.1 / -30.8 / -54.6).
    Green on the Tarantula is SXT's; its red and blue still keep light. A bar on this row is two-sided from here on.
  - **The nucleus is taken in the inner 6 px, in green and blue.** At eight views (`m31-nucleus-r3z_synth25_s0.txt`):
    0 to 6 px +5.3 / +24.3 / +34.2 %, 6 to 12 px -3.7 / +2.3 / +9.2 %. R3r, the small model with the same two saturated
    star options and no plate halo, took 17 to 38 % there, so the options that draw saturated stars as measured are
    the likely cause and the halo over the plate is not shown to be; the two cannot be separated by this arm, since
    `--halo-over-plate` needs `--saturated-measured`.
  - **With `--protect-cores` (#1658) the nucleus reads 0.0 / 0.0 / 0.0 % and no other whole-master row moves** by more
    than 0.1 (Tarantula's BP 17-19 as trained 91.8 against 91.9; every other figure of both rows identical, glow
    included). The scoring binary of October has no such option, so the row was made with `bh-bin8`, and the same
    binary without the option reproduces the October binary's rows to the last digit (tags `w-r3z_synth25_s0-pc` and
    `-b8`, each with `+floor3`). The mask protects one core on each master: Andromeda (2068, 1484), 2,944 px;
    the Tarantula (1460, 1516), 2,240 px, which is R136, a star cluster inside the nebula's brightest knot; the
    Sagittarius Triplet (1924, 1340), 2,048 px.
  - **What the mask protects over the whole store (the owner, 2026-11-17: core protection becomes the default if it
    protects cores only): it did not at this date; #2199 added the two rules and the default, PR #2200.** `ExtendedCoreMask.Find` at its defaults over the store's 201
    session masters (a probe calling the library, which reproduces the CLI's "protected" lines on the three whole
    masters): 95 masters get a core, 148 cores, with 11,066 catalogued stars inside them that a starless plate would
    keep, 737 of those at 100 sigma and over or saturated. Looked at one by one on a contact sheet and read by two
    numbers (the peak's distance from the border; the 90th percentile of the pixels about the peak over the
    large-scale map's peak): 11 cores are the frame's edge (within 16 px of it), 59 are one bright star and its halo
    or a tight group (Alnitak, Antares, Alcyone, HIP 80609, eta Carinae; that ratio 7.2 and over, most 20 to 900), and
    78 are a nebula's or galaxy's bright part or a globular's centre (ratio 1.0 to 5.5). The three whole masters'
    cores are of the last kind. The issue held the rule that drops the first two kinds and the default after it; both
    are on main since PR #2200 ("The survey repeated with the two rules" above: 77 cores kept, the three whole
    masters' cores unchanged), and the probe is in `training/star-removal/coremask-probe/`.
  - **As trained** (reported): Andromeda dark flat 59.3 dug 25.8 (R3v 69.5 / 22.4); by count clean 95.3 at 100-1000
    sigma and 93.1 at 1000 and over (94.9 / 87.2); M42 100-1000 clean 58.1 dug 39.0 (56.6 / 42.6); SMC 20-100 84.7 and
    100-1000 74.6 (97.2 / 89.8), its losses stars left, which floor3 takes; HIP 80609 99.1; Tarantula dark flat 78.5
    left 15.9; crowded Tarantula 83.6, BP 17-19 91.9, Sagittarius 96.2; glow -158.7 and -290.7.
  - **Saturated stars by count** (the rows the measured stars are for): as trained clean 84.2 dug 10.5 left 9.5 (R3v
    78.9 / 16.8 / 6.3); with floor3 93.7 / 5.3 / 1.1 (R3v 91.6 / 5.3 / 4.2, SXT 89.4 / 2.1 / 8.5). The prediction that
    saturated left falls holds with the step and not as trained.
  - **Crowded, with floor3:** Tarantula 87.9, BP 17-19 92.6, Sagittarius 97.3 (R3v 88.7 / 92.9 / 95.5; SXT 96.1 / 98.3 /
    99.6). The crowded fields are the largest gap left to SXT.
  - **The twin inside the rim** (`twin_err_inner.py`, R3v's mask): rms 0.769 as trained and 0.823 with floor3 (R3v
    0.700); the registered-way figure, the rim's, 2.907 and 1.091.
  - The ring profile at real sites, which the registration reports with no bar, is read beside R3y's above (M42 0.37
    / -2.21 / -0.14 / 0.02, HIP 80609 0.12 / 0.26 / -0.21 / -0.31): the shallowest ring of the four models on both.

- **R4a, pre-registered 2026-11-17 before any score. One change from R3z: the cluster scenes join the draw, for the
  crowded fields.** R3z's data (synth25) and F6's master-stretched cluster scenes (`r3-clusters-master/cache`, 2,592
  training cells of one draw, by their share of the tiles 12.5 % of the training draw, as R3a's log printed) on R3v's
  model and recipe exactly, seed 0, as a Batch job that removes itself (tag `r4aaz`, `launch-batch.ps1 -CacheBlob
  r2-synth25/cache -ExtraBlob r3-clusters-master/cache`). R3v, R3x, R3y and R3z trained with no cluster scenes.
  - **Judged with floor3 AND `--protect-cores`** (the owner, 2026-11-17: core protection becomes the default once its
    mask is shown to protect cores only; that validation is its own measurement and is not this arm's). The plates are made with
    `bh-bin8`, which reproduces the October scoring binary's rows on R3z to the last digit. The rows as trained and
    the nucleus without protection are reported beside.
  - **The question, the crowded fields, against R3z with floor3:** the Tarantula's crowded stars flat at least 92.0
    (R3z 87.9, SXT 96.1: half the gap) and Gaia BP 17-19 removed at least 95.4 (92.6, 98.3: half the gap).
  - **Guards:** R3z's star guards unchanged (by count clean at least 98.5 at 100-1000 sigma and 98 at 1000 and over;
    M42 100-1000 at least 88; SMC 20-100 and 100-1000 at least 97; Andromeda dark flat at least 95 and dug at most 3;
    Tarantula dark flat at least 91); the glow's green median within 450 of zero on Andromeda and within 500 on the
    Tarantula, either side; the M31 nucleus under 10 % in every channel with protection; purity at most 1.0 %; the
    Sagittarius Triplet's crowded stars flat at least 96.3 (R3z 97.3 less one point).
  - **Kill:** a guard fails, or the Tarantula's crowded flat stays under 89.9 (not 2 points over R3z). Between 89.9
    and 92.0 with every guard held, or one of the two bars cleared and the other missed, it neither passes nor is
    killed: no seed 1, the rows are reported, the owner decides.
  - **Reported, no bar:** every row as trained; the glow by channel, p90 and share over; saturated by count; the
    nucleus without protection; M32 kept; the twin inside the rim.
  - **Prediction:** the crowded rows rise by less than half the gap (the Tarantula's flat to about 90 or 91, BP 17-19
    to about 94), as R2q's clusters did on the small model; the glow and the star guards hold; the nucleus without
    protection is taken as R3z's is.
  - **If the question is cleared:** seed 1. Nothing is adopted; adoption is the owner's.
  - **Seed 1, registered 2026-11-18 before any score of either seed** (seed 0 is at about step 80,000 of its
    training). R3y's two seeds differed by 9.6 points on the dig, 21.4 on M42's 100-1000 row and a factor of three on
    the nucleus, so one seed does not settle rows like these. R4a's recipe and data exactly (the same trainer files,
    `--extra-cache` on the local copy of the cluster cache), only `--seed 1` (`r4a_synth25c_s1`), on the GTX 1070 while
    seed 0 trains on the T4. Read with seed 0 against this registration as it stands: no new bar, none moved. A
    question counts as cleared only if both seeds clear it; a kill by either seed stands. This replaces "If the
    question is cleared: seed 1" above only in the order the seeds train.
  - **Seed 0's read, 2026-11-18: KILLED, the Tarantula's crowded flat at 88.5 against the kill bar of 89.9.** The
    trainer stopped at step 225,500 (the held-out objective's best at 213,500). With floor3 and core protection, against
    R3z with floor3:

| row | bar | R4a seed 0 | R3z |
|---|---|---|---|
| Tarantula crowded flat (question) | at least 92.0, kill under 89.9 | **88.5** | 87.9 |
| Gaia BP 17-19 (question) | at least 95.4 | **94.1** | 92.6 |
| by count clean, 100-1000 / 1000 and over | 98.5 / 98 | 99.3 / 100.0 | |
| M42 100-1000 clean | 88 | 94.1 | |
| SMC 20-100 / 100-1000 clean | 97 | 98.9 / 100.0 | |
| Andromeda dark flat / dug | 95 / at most 3 | 98.1 / 1.0 | |
| Tarantula dark flat | 91 | 94.5 | |
| glow green median, Andromeda / Tarantula | within 450 / 500 | -73.7 / -190.6 | |
| M31 nucleus with protection | under 10 % | 0.0 / 0.0 / 0.0 | |
| purity | at most 1.0 % | 0.8 | |
| Sagittarius crowded flat | 96.3 | 96.4 | 97.3 |

    Every guard holds (the Sagittarius one by a tenth) and both question bars are missed: the crowded flat rose 0.6
    points and BP 17-19 rose 1.5, under the prediction's 90 to 91 and about 94. An eighth of the draw as cluster scenes
    does not close the crowded gap on this model. Reported, no bar: as trained, Andromeda dark flat 56.6 dug 34.8,
    Tarantula dark flat 70.5 left 23.3, Tarantula crowded 82.8, BP 93.3, Sagittarius 94.6, by count clean 100-1000
    95.5 (M42 55.1 dug 41.9; HIP 80609 99.3; Lobster 99.6; SMC 84.7), saturated by count clean 85.3 as trained and 93.7
    with floor3; the nucleus without protection 25.2 / 43.0 / 46.9 % as trained.
  - **Seed 1's read, 2026-11-18: KILLED on the same bar, and on the Sagittarius guard by a tenth.** It stopped at step
    192,000 (best at 180,000). With floor3 and core protection: the Tarantula's crowded flat **88.5**, the same as seed
    0 to the tenth; BP 17-19 **92.4** (seed 0 94.1, R3z 92.6); the Sagittarius crowded flat 96.2 against the guard's
    96.3; Andromeda dark flat 98.1 dug 1.1; Tarantula dark flat 94.5; by count clean 99.3 at 100-1000 and 100.0 at 1000
    and over; M42 100-1000 95.6; SMC 98.9 / 100.0; glow green median +50.1 / +97.3; nucleus 0.0 with protection;
    purity 0.7. As trained: Andromeda dark flat 66.1 dug 28.4, Tarantula crowded 84.3, BP 91.6, M42 100-1000 clean
    72.1 dug 25.7 (seed 0 55.1 and 41.9), the nucleus without protection -0.1 / -6.1 / 15.6 %. So the crowded flat
    does not move with the seed (88.5 twice) and BP 17-19 moves 1.7 points, which is as much as seed 0 gained on
    R3z: the cluster scenes buy nothing measurable on the crowded rows. As trained the two seeds again differ widely
    on the dig (6.4 points) and on M42 (17 points), as R3y's did.
  - **Where the crowded gap is (seed 0 with floor3, the Tarantula's 13,939 crowded stars):** flat 88.5 %, dug 2.1 %,
    **left 9.4 %**. The stars are left in, not dug, so the dig-fill step has nothing to do there.
  - **A second pass, registered 2026-11-18 before any score: R3z's seed 0 run again over its own starless plate.** No
    training. The plates of `w-r3z_synth25_s0-pc` (core protection on) go through `image remove-stars --protect-cores`
    a second time with the same weights and binary (`bh-bin8`), then floor3 against the ORIGINAL master, then the
    whole-master scorecard (`w-r3z_synth25_s0-pc2`). **Question,** R4a's: the Tarantula's crowded flat at least 92.0
    and BP 17-19 at least 95.4. **Guards,** those of R4a's that the scorecard reads: Andromeda dark flat at least 95
    and dug at most 3; Tarantula dark flat at least 91; the glow's green median within 450 and 500 of zero; the nucleus
    under 10 % with protection; purity at most 1.0 %; the Sagittarius crowded flat at least 96.3. **Kill:** a guard
    fails, or the crowded flat under 89.9. **Prediction:** the crowded flat rises to about 91 as the stars a first
    pass left lose their neighbours, and the cost shows in purity or in the light taken from the field, since a pass
    that takes 1 to 9 % too much flux does so twice.
  - **The second pass's read, 2026-11-18: KILLED, the crowded flat 89.1 against 89.9.** With floor3, one pass against
    two: the Tarantula's crowded flat 87.9 to **89.1** (left 10.1 to 8.9 %, dug 2.0 both), BP 17-19 92.6 to **94.2**,
    purity 0.7 to 0.8, the light taken from the field 79 to 80 %; every guard holds and nothing else moves (Andromeda
    dark flat 98.0 dug 0.9, Tarantula dark flat 95.7, Sagittarius crowded 97.3, glow green median -147.4 / -68.5,
    nucleus 0.0). The prediction of about 91 was too high and the cost it expected did not appear. Nine in ten of the
    crowded stars a first pass leaves are left by the second as well: the model does not see them as stars, with or
    without their neighbours, so a second pass is not the crowded fields' answer.
  - **The crowded gap is in the truth plate, not in the model (2026-11-18, a measurement, no arm).** In the
    Tarantula's densest 300 px cell StarXTerminator's plate sits below the input's own darkest tenth (red 0.01124
    against 0.01148; ours 0.01165), so it takes the unresolved light too, and after a 3 px box its spread is 1.6 / 1.1
    / 0.9 sigma (R / G / B) where ours is 3.5 / 2.1 / 1.5. On the SMC validation session, which has a truth plate
    (`crowd_texture.py`, R3z as trained), the model's plate has the TRUTH's spread in the five densest cells (0.85 /
    1.20 / 0.73 against 0.87 / 1.09 / 0.74 in the densest; 2.75 / 2.39 / 1.36 against 2.83 / 2.53 / 1.44 in the next)
    where a sparse cell reads 0.55: the model returns what the builder left. Split by the builder's own records
    (`mottle_split.py`, the luminance after a 3 px box less its 21 px median, white noise about 1; a sparse cell reads
    1.8): the pixels the builder never touched are 78 to 79 % of a dense cell, read 2.6 to 3.6 and hold 55 to 81 % of
    the structure; the candidates it left in (466 knots, 75 with no fit, 43 merged, 14 too narrow, of 25,157) are
    under 1 % of the pixels, read +13 to +22 and hold 5 to 36 %; the subtracted sites are the QUIETEST class (1.8 to
    2.3) and hold 5 to 10 %; the inpainted pixels 3 to 5 %. So the mottle is the stars the finder never found, in a
    field whose own noise is faint stars, and the blends the star test refuses; it is not the subtraction's marks.
    More training toward these plates cannot close the gap, which is why the cluster scenes and the second pass did
    not. What the plate should hold in such a field is the owner's (2026-10-20: the starless plate keeps the
    unresolved glow); the builder's finder and its star test in crowded cells are where a change would go.
  - **What the finder left, counted (2026-11-18, `leftover_peaks.py`, the same SMC plate).** The plate's luminance
    less its 21 px median through a Gaussian of 1.2 px, in the photon noise (a sparse cell's filtered spread, scaled
    by each cell's neighbour differences), local maxima per 300 px cell. The five densest cells hold 557 to 654
    catalogued stars each and still 91 to 133 maxima at 5 sigma, 226 to 271 at 4 and 447 to 513 at 3, where a sparse
    cell of 120 catalogued stars holds 17, 77 and 179. Of the 5 sigma leftovers 71 to 93 a cell lie further than 5 px
    from anything the builder catalogued, at a median of 5.4 to 5.8 sigma, so the finder's threshold misses about one
    star for every seven it finds and those sit just over it; 5 to 19 a cell are at a candidate it left in, and 0 to
    14 at a subtracted star. The plate's filtered spread is 1.3 to 1.5 times the photon noise in the dense cells and
    1.0 in the sparse one. So most of the mottle is stars between 2 and 5 sigma, under any threshold a finder can hold
    on noise alone, and the masks are not the cause (the inpainted pixels are 3 to 7 % of a dense cell). A finder for
    the truth plates of crowded fields, separate from `Image.FindStarsAsync` (the owner, 2026-11-18), is #2203.
  - **#2203 step 1, 2026-11-18: a lower threshold alone is not the answer.** `dataset starless-plates
    --detection-sigma` (branch `feat/crowded-plate-finder`) built the SMC, M42 and held-out Tarantula plates at 5, 4
    and 3.5 sigma (`threshold_cmp.py`; the Tarantula's plate is a diagnosis, never a training plate); the 5 sigma build
    reproduces the stored SMC plate to the star. **SMC, the five densest cells:** stars subtracted 2,920 / 3,993 /
    4,890, maxima left at 5 sigma 564 / 166 / 116 and at 4 sigma 1,269 / 612 / 270, but the structure only 3.21 / 3.01
    / 2.87. **M42, the five brightest cells:** candidates 718 / 1,047 / 1,389 and subtracted as stars 527 / 769 /
    1,057, the plate moved 7.6 and 8.8 sigma against the 5 sigma one: nebula texture taken. Core protection does not
    cover it (one region of 15,924 px round the Trapezium holds 5 of the 258 extra subtractions at 4 sigma and 10 of
    557 at 3.5), and it acts when the remover is used, not when a plate is built. **The Tarantula's densest cell,**
    spread after a 3 px box in sigma, R / G / B: StarXTerminator 1.59 / 1.05 / 0.90, the model (R3z with floor3) 3.52
    / 2.09 / 1.52, the classical plate at 5 sigma 5.81 / 3.60 / 2.76, at 4 sigma 5.13 / 3.27 / 2.56, at 3.5 sigma 4.80
    / 3.10 / 2.46, with 948 / 1,289 / 1,587 candidates of which 98 % pass the star test. So in a field this dense the
    builder's plate is rougher than the model's own output at every threshold: the field is confusion-limited, every
    resolution element holds a star, and subtracting stars one by one cannot reach a smooth plate. The star test's
    pass rate does not separate the cases well either (97 to 98 % in the star field, 90 to 91 % in 30 Doradus's
    core, 73 % in M42's). What a plate should be in a confusion-limited cell is the next step's question.
  - **#2203 step 2, 2026-11-18: where the grain is, and what teaches the model to keep it.** After a 7 px median the
    Tarantula's dense field still holds a grain in the 9 to 41 px band of 14.4 / 9.0 / 7.4 sigma of a 9 px mean (0.76
    / 0.39 / 0.35 % of the sky) where StarXTerminator's plate holds 2.9 / 2.3 / 2.1; it follows the catalogued stars'
    density only weakly (r = 0.20), so it is the chance clustering of stars under any threshold. StarXTerminator keeps
    the glow as a smooth surface at about 40 px and up (the same reddish clouds are in its plate) and drops the
    grain, so "the plate keeps the unresolved glow" and "no grain" do not exclude each other. A prototype
    (`plate_grain.py`: the detail between a 7 px and a 31 px median replaced by the smooth base where subtracted stars
    are dense and the detail's rms over 21 px is under twice the grain's own level, that level a line in the square
    root of the star density) takes the Tarantula's dense cells from 6.2 / 5.7 / 5.0 to 2.2 / 2.3 / 2.2 and from 6.5
    / 6.3 / 5.7 to 3.4 / 3.6 / 3.4 (StarXTerminator 0.8 to 1.2), leaves 30 Doradus's core as it was (61.9 / 68.9 /
    51.5 to 61.6 / 68.4 / 51.3) and M42 untouched; a variant that reached 1.6 cost 30 Doradus a quarter of its
    structure and was dropped. By the density test 153 of the 201 stored plates have crowded sky (eta Carinae at 135
    mm, Rosette, Seagull, Vela), not only the Magellanic fields, and a stored eta Carinae plate is peppered with
    faint stars across its whole frame. **But the training tiles do not carry the plate's pixels:** every export
    since synth18 draws its background synthetic (`--background synthetic`: the plate's coarse scales, fine ones
    synthesised to the plate's own local signal), and the injector draws brightness from the catalogue, which holds
    nothing under the builder's 5 sigma. So the model is never shown a star under 5 sigma to take, and the grain the
    plate holds comes back as fine texture the target keeps. The extension that draws the stars under the catalogue
    was built on 2026-10-23 (`--faint-stars`, "Synthetics S2" below), capped per tile at 0.1 sigma by the owner's
    decision of that day, and left "opt-in and capped, for a training arm to judge". No arm has trained with it.
    That arm is R4c.
- **Colour balance and gradient extraction before the remover change nothing that matters (2026-11-18, the owner's
  two questions; R3z as trained, HIP 80609 and M42, per catalogued star against the untouched run and the truth
  plate).** The input stretch is per channel (each channel's own minimum and median, `ChunkedNafnetRunner`), the
  export uses the same routine, and the trainer adds flips and quarter turns only. *A channel halved or quartered*
  (`wb_invariance.py`): the median change in a star's core is at most 0.03 sigma up to 1000 sigma and no star moves
  by more than 1 sigma; over 1000 sigma (about 250 stars a master) at most 0.14 sigma and the dug share within 3
  points. *The master flattened first* (`flatten_invariance.py`, the background put back on the output): with the
  classical extractor the dug share in the core moves by 1 to 2 points up in red and 2 to 10 down in green and blue
  (M42 100-1000 green 43.7 to 33.7), median changes at most 0.2 sigma; one row worse, M42's red ring at 2 to 4 px,
  38.9 to 45.5. GraXpert gives the same picture row for row (M42 100-1000 green 43.7 to 33.0, the red ring 38.9 to
  46.8). Two seeds of one recipe differ by about 10 points on these rows. `image flatten` writes on the unit scale
  whatever its input's (#2202), which the script undoes by the ratio of neighbour differences.
- **R4c, pre-registered 2026-11-18 before any score. One change from R3z: the stars under the catalogue are drawn.**
  synth28 = synth25's line plus `--faint-stars 3` (the reviewed floor, the default cap of 0.1 sigma a tile), from
  the same binary (`bh-bin8`), on the stored plates as they are; R3v's model and recipe exactly, seed 0, no cluster
  scenes, a Batch job that removes itself (tag `r4caz`).
  - **The question, with floor3 and core protection, against R3z:** Gaia BP 17-19 removed at least 95.4 (R3z 92.6,
    StarXTerminator 98.3: half the gap) and the Tarantula's crowded flat at least 92.0 (87.9, 96.1).
  - **Guards, R4a's:** by count clean at least 98.5 at 100-1000 sigma and 98 at 1000 and over; M42 100-1000 at least
    88; SMC 20-100 and 100-1000 at least 97; Andromeda dark flat at least 95 and dug at most 3; Tarantula dark flat at
    least 91; the glow's green median within 450 of zero on Andromeda and within 500 on the Tarantula; the M31 nucleus
    under 10 % with protection; purity at most 1.0 %; the Sagittarius crowded flat at least 96.3.
  - **Kill:** a guard fails, or BP 17-19 under 93.6 (not one point over R3z). Between the kill and the question's
    bars with every guard held it neither passes nor is killed; the rows are reported and the owner decides.
  - **Reported, no bar:** every row as trained; the grain in the Tarantula's two dense cells and 30 Doradus's core
    (the 9 to 41 px band, as above) beside R3z's and StarXTerminator's; the light taken from the field; the share of
    tiles the cap touched and the faint stars drawn and dropped a tile.
  - **Prediction:** BP 17-19 rises to about 94.5, since those are the stars the extension adds; purity rises toward
    1.0 as stars near the noise are taken; the Tarantula's crowded flat moves under a point and the dense cells'
    grain stays, because the cap stops the draw exactly where the field is dense. If so, the cap, not the model, is
    what holds the crowded fields, and raising it is the owner's (2026-10-23).
  - **If the question is cleared:** seed 1. Nothing is adopted; adoption is the owner's.
- **R4d, pre-registered 2026-11-18 before any score of R4c or itself. One change from R4c: the cap lifted.** The
  owner, 2026-11-18, on the finding above: "lets use the faint stars then if we have them". synth29 = synth28's line
  with `--faint-stars 3,1` (`export-synth.ps1 -FaintStars`): the same floor, the cap at 1 sigma a tile, which no
  validation tile reaches (uncapped at this floor the worst read 0.232, the SMC's mean 0.109), so every star the
  luminosity function asks for is drawn; the same binary (`bh-bin8`), R3v's model and recipe, seed 0, trained on the
  GTX 1070 while R4c trains on the T4. It is a diagnosis of what the cap holds back, not a proposal to drop it: the
  cap is the owner's decision of 2026-10-23 (unresolved glow belongs in the starless image) and stays the default.
  - **The question, with floor3 and core protection, against R4c:** the Tarantula's crowded flat at least 2 points
    over R4c's AND the grain in the Tarantula's two dense cells (the 9 to 41 px band) at most two thirds of R4c's.
  - **Guards, R4c's,** and one more for what the cap was set to protect: the light taken from the field on the
    Tarantula (green, the densest tenth of cells and the whole frame) no more than 5 points over R3z's 78 and 79 %,
    and 30 Doradus's core (cell 4,4) keeping at least 95 % of its 9 to 41 px band against R3z's plate.
  - **Kill:** a guard fails. With every guard held and the question missed, the cap is not what holds the crowded
    fields and the arm stops there.
  - **Prediction:** the dense cells' grain falls by about a third and the crowded flat rises 2 to 3 points; the
    light taken from the densest cells rises, toward the glow guard's edge, because a model taught to take every
    faint star takes part of the sea they sum to.
  - **Nothing is adopted;** a pass means the cap's value goes back to the owner with these numbers.
  - **A correction to a cell's name, 2026-11-19, before any score of R4d exists.** The extra guard above names 30
    Doradus's core "cell 4,4". In the numbering of `crowd_texture.py` and `band.py` (300 px cells, row then column,
    on the store's ASI585MC Pro session of 2024-10-02) cell 4,4 is the DENSEST star cell (1,300 catalogued stars, a
    band of about 5 sigma) and the bright nebula is cell 4,6 (544 stars, a band of 60 to 105 sigma, the size the
    section above gives for 30 Doradus's core). The name was wrong, not the guard: the guard is read on the bright
    cell, 4,6, as its words say (30 Doradus's core keeping at least 95 % of its band), and the question's two dense
    cells are 4,4 and 5,4. Read on 4,4 the guard would contradict the arm's own question.
  - **R4d's read, 2026-11-19 (seed 0, `r4d_synth29_s0`): KILLED on the guard for 30 Doradus's core, and the question
    missed on both halves.** The trainer stopped itself at step 245,000 (best at 233,000). With floor3 and core
    protection:

    | row | R4c | R4d | bar | read |
    |---|---|---|---|---|
    | Tarantula crowded flat | 90.4 | **91.2** (left 6.9 %, dug 1.8 %) | at least 92.4 | missed |
    | grain in dense cell 4,4, as trained, R / G / B | 4.6 / 3.8 / 3.0 | 4.3 / 3.8 / 3.1 | at most two thirds of R4c's | missed, unchanged |
    | grain in dense cell 5,4, as trained | 4.7 / 4.2 / 3.3 | 4.7 / 4.6 / 3.6 | at most two thirds of R4c's | missed, not lower |
    | 30 Doradus's core (bright cell 4,6), band as trained | 64.1 / 76.2 / 61.9 | **44.4 / 49.4 / 39.7** | at least 95 % of R3z's 60.4 / 65.6 / 48.8 | **failed: 74 / 75 / 81 %** |
    | light taken from the Tarantula field, green: densest tenth of cells, whole frame | 80 %, 82 % | 81 %, 82 % | at most 83 %, 84 % (R3z 78 %, 79 %) | held |
    | Gaia BP 17-19 | 96.0 | 96.0 | reported | |
    | by count clean, 100-1000 and 1000 and over | 99.3, 100.0 | 99.3, 100.0 | at least 98.5, 98 | held |
    | M42 100-1000 by count clean | 93.4 | 95.6 | at least 88 | held |
    | SMC 20-100 and 100-1000 by count clean | 99.4, 100.0 | 98.9, 100.0 | at least 97 | held |
    | Andromeda dark flat, dug | 98.2, 0.9 | 98.5, 0.7 | at least 95, at most 3 | held |
    | Tarantula dark flat | 95.5 | 96.5 | at least 91 | held |
    | glow green median, Andromeda and Tarantula | -45.2, -163.7 | -8.5, +106.5 | within 450, 500 | held |
    | M31 nucleus with protection | 0.0 % | 0.0 % | under 10 % | held |
    | purity | 0.9 | 0.8 | at most 1.0 | held |
    | Sagittarius crowded flat | 97.9 | 97.0 | at least 96.3 | held |

    So lifting the cap buys 0.8 of a point on the crowded flat and no less grain in the dense cells, and the arm
    fails its guard on 30 Doradus's core. The band is read on the remover's output as trained, without core
    protection, as R4c's and R3z's were; the cell's level is 0.994 to 0.995 of the plate's, so a lower band is
    structure flattened and not light taken. At a 3 px box (`crowd_texture.py`) the bright cell reads
    14.1 / 15.0 / 12.5 against R4c's 24.4 / 26.9 / 20.3 and the plate's 32.1 / 40.4 / 37.4. R4c's 80 % and 82 % for
    the light taken from the field were read on 2026-11-19 beside R4d's, from the same scorecard log; R4c's own
    read below was written before that. **The cap is not what holds the crowded fields; it stays the default, and
    the arm stops here.**
  - **What one seed carries of this read (measured 2026-11-19 after the read, on the review's question).** The band
    rows' spread between seeds had never been read. `band.py` on the two recipes with both seeds at hand, the same
    session, as trained, R / G / B:

    | cell | R3v seed 0 | R3v seed 1 | R4a seed 0 | R4a seed 1 |
    |---|---|---|---|---|
    | 4,4 (dense) | 5.1 / 4.7 / 3.9 | 5.2 / 5.0 / 4.1 | 4.6 / 4.2 / 3.4 | 4.3 / 3.8 / 3.3 |
    | 5,4 (dense) | 5.5 / 5.3 / 4.2 | 5.5 / 5.2 / 4.2 | 4.7 / 4.4 / 3.7 | 3.8 / 3.3 / 3.0 |
    | 4,6 (30 Doradus's core) | 82.5 / 105.0 / 101.7 | 79.2 / 100.8 / 87.1 | 59.4 / 70.5 / 51.9 | 82.7 / 107.8 / 90.8 |

    R4a's two seeds stand 39 to 75 % apart on the bright cell and 23 to 33 % apart on dense cell 5,4, on the same
    data and recipe but NOT the same machine: seed 0 trained on the T4 (tag `r4aaz`) and seed 1 on the GTX 1070
    (registration of R4a's seed 1). So these are a seed-and-machine spread, the confound no pair of runs
    separates (below). The R3v columns are no cleaner: seed 0 trained on the 1070, and the plan does not record
    where seed 1 trained, so they are read as a seed spread only if that is checked. Either way the bright cell's
    band is not a property of a recipe that one seed can show: R4d's
    44.4 / 49.4 / 39.7 is lower than any of the six other models read, which is as much as can be said, and "the
    cap lifted costs a quarter of the core" is NOT shown. The kill stands on the registered rule (a guard failed);
    the guard itself was badly chosen, since R4a's seed 0 would fail it against R4a's own seed 1.
  - **What is inside this spread and what is not, per cell and channel.** R4c's dense-cell grain under R3z's
    (4.6 / 3.8 / 3.0 against 4.8 / 4.3 / 3.6, so 4 / 12 / 17 % on 4,4) is judged cell by cell. On 5,4 R4c's
    6 / 11 / 13 % is inside R4a's 23 to 33 % and outside R3v's 0 to 2 %. On 4,4 the pairs differ by 3 to 10.5 %
    (R4a: 4.6/4.3, 4.2/3.8, 3.4/3.3) and 2 to 6 % (R3v). Read to one decimal, G's 12 % sits inside the rounding of
    R4a's 10.5 %, so only B (17 %, clear of every spread read) is outside the 4,4 spreads; R (4 %) is inside R4a's
    R gap (7 %) and outside R3v's (2 %). On 4,6 (the bright cell), R3v's seeds stand 4 / 4 / 17 % apart (82.5 /
    105.0 / 101.7 against 79.2 / 100.8 / 87.1) and R4a's 39 to 75 %. R4c's rise over R3z (64.1 / 76.2 / 61.9
    against 60.4 / 65.6 / 48.8) is 6 / 16 / 27 %: inside R4a's spread on every channel, outside R3v's on G and B
    (and on R, 6 against 4). R4d's fall from R4c (to 44.4 / 49.4 / 39.7) is 31 / 35 / 36 % of R4c's level, outside
    R3v's gap on every channel. Against R4a it is read on one basis: R4a's 39 / 53 / 75 % is the higher seed over the
    lower, so R4c over R4d (64.1 / 76.2 / 61.9 over 44.4 / 49.4 / 39.7) is 44 / 54 / 56 %, past R4a's gap on R by 5
    points and on G by 1, inside only on B. On the fall basis R4a's gaps are 28 / 35 / 43 % against R4d's 31 / 35 /
    36 %, the same picture. So R and G sit at the edge of R4a's gap and B well inside it. Which gap a faint-star
    recipe should be read against is not measured: R4d's
    data is synth25 plus faint stars, R4a's is synth25 plus cluster scenes and R3v's is synth22, so "not shown"
    above is against the widest gap read (R4a's). What one seed does carry here: the crowded flat (0.0 to 0.1
    between seeds), so the question's miss is firm.
  - **These spreads are of recipes without faint stars.** R3v and R4a draw none; R4c and R4d draw them, and R4c
    ran one seed and R4d one, so the faint-star recipe's own spread is not measured (the R4e registration says the
    same of R3v, R4a and R3y). "Inside this spread" reads as inside the spread of a recipe without faint stars,
    and the R3v columns read as a seed spread only if where its seed 1 trained is checked (the plan does not
    record it).
    R4d also trained on the GTX 1070 where R4c trained on a T4; no pair of runs separates the machine from the seed.
  - **The other side of the ledger, as trained.** R4d leans less on floor3 than R4c: Andromeda dark flat dug 19.5
    against 45.9, Tarantula dark flat left 14.3 against 33.1, by count clean 95.6 and 91.5 against 94.9 and 86.7,
    SMC 100-1000 clean 91.5 against 81.4. The plan holds seed pairs for these rows on recipes without faint stars,
    the same proxy as the band rows. (R3y's two seeds trained on two machines as R4a's did, seed 0 on the T4 and
    seed 1 on the GTX 1070, so its pairs are seed-and-machine pairs too.) Andromeda dig: R3y 9.6 points apart, R4a
    6.4; M42 100-1000 clean: R3y 21.4, R4a 17; R4d less R4c on the dig is 26.4, larger than any. Tarantula dark flat
    left: R3y 18.3 / 23.3, R3v 16.0 / 13.8 (gaps 5.0 and 2.2) against a difference of 18.8, larger than both. SMC
    100-1000 clean: R3y 84.7 / 91.5, R3v 89.8 / 91.5 (gaps 6.8 and 1.7) against 10.1, larger than both. So each of
    these three rows exceeds every measured pair of its own row. Gaps of 17 to 21 points do occur between seeds on a
    row that spreads widely (the M42 pairs above): the left row's 18.8 is inside that range and the SMC row's 10.1
    is below it, so both are beyond their own pairs but not beyond what a seed has moved on another row. Only the
    dig's 26.4 is beyond the widest pair measured on any row. The by-count clean rows are not paired here. The row
    the cap exists to protect goes the other way: the Tarantula's glow with floor3 reads p90 443.5 and 31 % over the
    bound against R4c's 143.4 and 8 % (inside its guard, and the larger of the two faint-star arms). R3v's pair for
    it (floor3, green, p90 1726.2 / 1436.5, over the bound 92 / 85 %, a no-faint-star spread) gaps 290 and 7 points;
    R4d less R4c is +300 and +23 points, so the p90 rise is about one R3v seed's gap and the share over about three
    times its gap. R3v reads at a far higher level, so the pair bounds a seed gap only roughly. So the ledger is
    mixed: the dig, the dark-flat left and SMC rows are beyond the measured seed gaps of their own rows, the glow's
    p90 is not and its share over probably is. This counts for the cap lifted only as the owner weighs it; it does
    not change the default.
  - Predictions: the grain falling
    by a third, missed (unchanged); the crowded flat rising 2 to 3, missed (0.8); the light taken from the densest
    cells rising, hardly (81 % against R4c's 80 %). Reported, no bar, as trained: Andromeda dark flat 69.4 dug 19.5
    (R4c 47.2 and 45.9), Tarantula dark flat 80.3 left 14.3, Tarantula crowded 86.7, BP 17-19 95.4, Sagittarius 96.4,
    the nucleus without protection 2.8 / 6.6 / 9.7 %, by count clean 95.6 at 100-1000 and 91.5 at 1000 and over, M42
    100-1000 clean 63.2 dug 34.6, SMC 100-1000 clean 91.5 dug 6.8; the Tarantula's glow with floor3 has a p90 of 443.5
    and 31 % of it over the bound (R4c 143.4 and 8 %), inside its guard and the larger of the two faint-star arms,
    R4c and R4d (R3v, which draws no faint star, reads 92 and 85 % over on its two seeds). Nothing is adopted.
- **R4c's read, 2026-11-19 (seed 0, `r4c_synth28_s0`): one bar of the question cleared, the other missed, every
  guard held. Not killed and not passed; the rows go to the owner.** With floor3 and core protection:

  | row | R3z | R4c | bar | StarXTerminator |
  |---|---|---|---|---|
  | Gaia BP 17-19 removed | 92.6 | 96.0 | at least 95.4, cleared | 98.3 |
  | Tarantula crowded flat | 87.9 | 90.4 | at least 92.0, missed | 96.1 |

  - **Guards, all held:** by count clean 99.3 at 100-1000 sigma and 100.0 at 1000 and over; M42 100-1000 93.4; SMC
    20-100 99.4 and 100-1000 100.0; Andromeda dark flat 98.2, dug 0.9; Tarantula dark flat 95.5; the glow's green
    median -45.2 on Andromeda and -163.7 on the Tarantula (p90 487.5 and 143.4, share over 22 % and 8 %); the M31
    nucleus 0.0 % with protection; purity 0.9 %; the Sagittarius crowded flat 97.9.
  - **All three predictions missed, each on the low side.** BP 17-19 was put at about 94.5 and came to 96.0; the
    crowded flat was put at under a point and rose 2.5; the dense cells' grain was put as staying and fell by 4 to
    17 % (below). On this one seed, drawing the stars under the catalogue closes 60 % of the gap to StarXTerminator
    on the faint stars and 30 % of it on the crowded flat, with the cap in place: the cap is not all that holds the
    crowded field. Seed 1 is not run. **What one seed is worth on these rows** (the three recipes run twice, with
    floor3): the crowded flat lay 0.0 to 0.1 apart between seeds (R3v 88.7 and 88.8, R4a 88.5 and 88.5, R3y 89.8 and
    89.8), so its 2.5 points are far outside a seed; BP 17-19 lay 1.4 to 1.7 apart (R3v 92.9 and 94.3, R4a 94.1 and
    92.4, R3y 95.1 and 93.6), so its 3.4 points are about two seeds' spread, and 96.0 is above the highest of those
    six runs by 0.9. Across arms the crowded flat has read 87.9 (R3z), 88.7 (R3v), 89.8 (R3y) and 90.0 (R4b) without
    any faint star, so R4c's 90.4 is the highest yet by 0.4 and 2.5 over its own base.
  - **As trained (no floor3, no protection) it digs more than R3z:** Andromeda dark flat 47.2 with 45.9 dug (R3z
    59.3 and 25.8), Tarantula dark flat 64.9 with 33.1 left (78.5, 15.9), Tarantula crowded 85.2 (83.6), BP 17-19
    95.5 (91.9), Sagittarius 97.2 (96.2), purity 0.9, the nucleus 24.4 / 37.0 / 29.4 %. By count as trained, 100-1000
    clean 94.9 (R3v 94.9), 1000 and over 86.7 (87.2); M42 100-1000 clean 61.0, dug 36.8 (R3v 56.6, 42.6); SMC 100-1000
    clean 81.4, dug 13.6 (R3v 89.8, 8.5). floor3 takes the dig away (the table above), and floor3 is in the product,
    but this model leans on it harder than R3z does.
  - **Reported, no bar. The grain in the Tarantula's dense cells** (`band.py`: through a 7 px median, the 9 px box
    mean less the 41 px one, rms over a 300 px cell in sigma of a 9 px mean; the store's ASI585MC Pro session of
    2024-10-02, each model as trained; this script's figures, which are not the earlier prototype's), R / G / B:

    | cell | truth plate | R3z | R4c |
    |---|---|---|---|
    | 4,4 (1,300 catalogued stars) | 4.8 / 5.0 / 5.2 | 4.8 / 4.3 / 3.6 | 4.6 / 3.8 / 3.0 |
    | 5,4 (1,153) | 6.9 / 7.2 / 6.2 | 5.0 / 4.7 / 3.8 | 4.7 / 4.2 / 3.3 |
    | 4,6 (544, the bright nebula) | 78.9 / 101.0 / 104.7 | 60.4 / 65.6 / 48.8 | 64.1 / 76.2 / 61.9 |

    The dense cells' grain is 4 to 17 % under R3z's (red least, blue most), and the bright cell's band is higher than
    in R3z's output. No level is rescaled: each output's median in a cell is 0.991 to 0.999 of the plate's (the
    script prints it), R4c's within 0.002 of R3z's, so the bright cell's higher band is structure kept and not light
    taken. At a 3 px box (`crowd_texture.py`) the dense cells read 1.46 / 1.26 / 0.93 and
    1.22 / 1.01 / 0.81 against R3z's 1.49 / 1.40 / 1.06 and 1.27 / 1.15 / 0.91. **The cap** dropped faint stars in
    6.7 % of synth28's 22,080 draws (a median of 119 stars dropped where it acted). StarXTerminator was not run on
    this session for this read, and the light taken from the field and the faint stars drawn a tile were not read (the light taken was read later, with R4d's: 80 % and 82 %).
  - **Seed 1 is not launched:** the registration ties it to a cleared question. What follows is the owner's.
- **R4e, pre-registered 2026-11-19 before any export or score. One change from R4c: each session's noise is drawn at
  the level its starless plate reads, not at the master's record.** The owner, 2026-11-19, on R4c's read and the
  finding of #1786 (a crowded master's recorded noise is read with its stars in, 1.11 to 1.5 times too high; "S4b:
  the level's cause" below): "go for that arm". synth30 = synth28's line (`--faint-stars 3`, the default cap) from
  `bh-bin10`, built 2026-11-19 12:03 at "docs(plans): R4c read: the faint stars clear BP 17-19 and miss the crowded
  flat's bar, every guard held; the zero-flux case (#2221)" (its `HEAD.txt`). That holds the default bound ("TIA1786:
  a stars export holds its noise level to the starless plate's by default") and not "TIA1786: an unreadable plate
  keeps the recorded level, and the row and the log say what was drawn" (#2224, merged after it): on this binary a
  plate with no readable block throws and fails its session, so a failed session is named, not skipped; R3v's model and recipe exactly, seed 0, a Batch job that removes itself
  (tag `r4eaz`). Before the export, `bh-bin10` with `--noise-as-recorded` is checked against synth28's own tiles on
  one session, so that the bound is shown to be the binary's one difference.
  - **What the change reaches.** Of the 138 training sessions the bound lowers the level on those crowded with
    RESOLVED stars (Lobster to 0.81 / 0.80 / 0.87 of the record, 29 of the store's 199 sessions under 0.90). The
    held-out Tarantula, SMC and Sagittarius masters are crowded with stars nobody catalogued, and their own plates
    read at or over their records by `noise_plate_all.py`'s lag-8 statistic (1.05 to 1.19). That statistic reads several
    percent higher than the one the bound uses (6 to 11 on the three sessions both were read on, #2231); by the bound's own reading the SMC session is at 0.97, so the bound
    lowers an SMC-kind session by about 3 %. The arm reaches the scored crowded rows barely, and mostly through what
    the model learns on the other kind of field.
  - **The question, with floor3 and core protection, against R4c:** the Tarantula's crowded flat at least 92.4 (R4c
    90.4, StarXTerminator 96.1). That row lay 0.0 to 0.1 apart between seeds on the three recipes run twice, so one
    seed answers it. Those spreads come from R3v, R4a and R3y, none of which draws faint stars, and R4c's own recipe
    was run once (seed 1 not launched), so this recipe's spread is not measured; at a 2-point bar that cannot change
    the call. 92.4 is R4c's 90.4 plus 2, as R4d's question has it (R4c's own bar was 92.0).
  - **Guards, R4c's:** by count clean at least 98.5 at 100-1000 sigma and 98 at 1000 and over; M42 100-1000 at least
    88; SMC 20-100 and 100-1000 at least 97; Andromeda dark flat at least 95 and dug at most 3; Tarantula dark flat at
    least 91; the glow's green median within 450 of zero on Andromeda and within 500 on the Tarantula; the M31 nucleus
    under 10 % with protection; purity at most 1.0 %; the Sagittarius crowded flat at least 96.3.
  - **Kill:** a guard fails, or the Tarantula's crowded flat under R4c's 90.4. The kill rests on the crowded flat and
    the guards alone: BP 17-19 has no bar here, so a bound that gave back R4c's faint-star gain (BP back at R3z's 92.6)
    would be reported, not killed. Between 90.4 and 92.4 with every guard
    held it neither passes nor is killed; the rows are reported and the owner decides.
  - **Reported, no bar:** Gaia BP 17-19 (96.0 on R4c; it lies 1.4 to 1.7 apart between seeds, so one seed cannot
    carry a bar on it); every row as trained, the dig most of all (Andromeda dark flat and its dug share, M42 and SMC
    100-1000 by count), since a model shown less noise round crowded stars may dig differently; the grain in the
    Tarantula's dense cells 4,4 and 5,4 and the bright cell 4,6 (`band.py`) beside R4c's; the ratios the export took,
    by session.
  - **Prediction:** the question is missed. The crowded flat moves by under a point either way, because the scored
    crowded masters are of the kind the bound barely touches (the SMC's ratio is 0.97); BP 17-19 stays inside a seed's spread of 96.0; the dig
    as trained changes little. If the crowded flat does rise by 2, the level of the training noise matters more to
    the crowded rows than the kind of field it was corrected on, and the noise's shape (#1847: the drawn noise reads
    4 to 10 % under the real plate at 8 px) is the next thing to correct.
  - **Nothing is adopted;** adoption is the owner's. R4d (the cap lifted) is still training and is read on its own
    registration.
  - **R4e's read, 2026-11-20 (seed 0, `r4e_synth30_s0`): KILLED. The Tarantula's crowded flat reads 89.1, under
    R4c's 90.4; every guard held.** The trainer stopped itself at step 194,500 of 480,000 (best at 170,500) and the
    Batch pool removed itself. The check asked for before the export held: `bh-bin10` with `--noise-as-recorded`
    wrote one session's 400 tile files (10P/Tempel) byte for byte as synth28 holds them, and none of its 160 rows
    carries a `PlateNoiseRatio`, so the bound is the binary's one difference. With floor3 and core protection
    (the chain the registration names; the tile rows are floor3 alone, as R4c's were):

    | row | R4c | R4e | bar | read |
    |---|---|---|---|---|
    | Tarantula crowded flat | 90.4 (left 7.7 %, dug 1.9 %) | **89.1** (left 8.9 %, dug 2.0 %) | at least 92.4, kill under 90.4 | **killed** |
    | by count clean, 100-1000 and 1000 and over | 99.3, 100.0 | 99.3, 100.0 | at least 98.5, 98 | held |
    | M42 100-1000 by count clean | 93.4 | 94.1 | at least 88 | held |
    | SMC 20-100 and 100-1000 by count clean | 99.4, 100.0 | 99.4, 100.0 | at least 97 | held |
    | Andromeda dark flat, dug | 98.2, 0.9 | 98.2, 0.9 | at least 95, at most 3 | held |
    | Tarantula dark flat | 95.5 | 95.1 | at least 91 | held |
    | glow green median, Andromeda and Tarantula | -45.2, -163.7 | -124.3, +143.3 | within 450, 500 | held |
    | M31 nucleus with protection | 0.0 % | 0.0 % | under 10 % | held |
    | purity | 0.9 | 0.9 | at most 1.0 | held |
    | Sagittarius crowded flat | 97.9 | 97.6 | at least 96.3 | held |
    | Gaia BP 17-19 | 96.0 | 94.7 | reported | |

  - **The renders, looked at before the rows** (`r4e_look.py`: 300 px crops at 2x, one stretch a region; input,
    StarXTerminator, R4c as trained, R4e as trained, R4e through the registered chain, R4e through the product's
    chain of 2026-11-20, which is protection without the stars and floor5). **30 Doradus's core:** as trained R4e
    reads as R4c does and close to StarXTerminator, R136 kept by all three, with dark pits where the cluster at the
    upper left stood, as R4c has. **The Lagoon's core:** as trained no star is left and the lanes are
    StarXTerminator's. **M31's nucleus:** R4c as trained shows a pink dent in it; R4e's is whole to the eye.
    **The Tarantula's crowded field** (320, 1435): StarXTerminator's plate is smooth and dark; R4c's and R4e's are
    mottled at 5 to 15 px, in colour, as trained and through both chains, and R4e's mottle is not less than R4c's.
    That picture is the crowded gap.
    **The Tarantula's knots** (1585, 705): StarXTerminator leaves the compact bright peaks standing, R4c takes
    them (smooth pink where they stood), R4e keeps more of the upper knot and takes the lower ones. **A bright
    star on Andromeda's dark sky:** gone in every column; R4e as trained leaves a faint dark disc that the fill
    evens. The registered chain shows its two known defects on R4e as on every model: floor3's flat patches and
    square holes, which floor4 and floor5 were made for (#2243, #2252), and the old protection's stars back in the
    Lagoon's core and round M31's nucleus (#2242). The product's chain shows neither on these six crops; it leaves
    the stepped edge of one fill at 30 Doradus's upper left, which floor5's section records.
  - **Reported, no bar: as trained (no floor3, no protection), against R4c.** Andromeda dark flat 85.4 with 10.5 dug
    (R4c 47.2 and 45.9); Tarantula dark flat 60.6 with 34.4 left (64.9, 33.1); Tarantula crowded 82.1 with 14.3
    left and 3.6 dug (85.2, 11.8, 3.0); BP 17-19 94.1 (95.5); Sagittarius 96.9 (97.2); purity 1.0 (0.9); the
    nucleus 6.0 / 21.2 / 9.9 % (24.4 / 37.0 / 29.4). By count as trained: 100-1000 clean 95.6 with 4.1 dug (94.9
    clean), 1000 and over 93.6 (86.7), saturated 78.9; M42 100-1000 clean 61.0 with 37.5 dug (61.0, 36.8); SMC
    100-1000 clean 86.4 with 11.9 dug (81.4, 13.6). **The glow:** the Tarantula's with floor3 has a p90 of 450.5
    and 38 % of it over the bound (R4c 143.4 and 8 %, R4d 443.5 and 31 %), inside its guard and the largest of
    the three faint-star arms (R3v, which draws none, reads p90 1726.2 and 1436.5 with 92 and 85 % over on its two
    seeds); as trained its median is -2.8 and its p90 696.3. Andromeda's with floor3: p90 360.6, 20 % over
    (R4c 487.5, 22 %). **The light taken from the Tarantula field,** green, with the chain: 79 % in the densest
    tenth of cells and 81 % over the frame (R4c 80 % and 82 %). **The twin** (`twin_err.py`, the truth known, rms
    inside the stars' mask in sigma): 2.806 as trained and 1.086 with floor3 (R4c 2.592 and 1.055); on R3v's mask
    2.790 where R3v reads 1.920 and R4c 2.560.
  - **Reported, no bar: the nebula rows** (the arm was registered before the rule that gives every arm nebula
    guards; Andromeda / Tarantula / Sagittarius). As trained: added 0.0 / 0.5 / 0.0 %; broad light taken 2.94 /
    3.98 / 4.47 (R4c 3.04 / 5.39 / 2.64, StarXTerminator 2.88 / 5.99 / 0.42); of the brightest tenth of the peaks
    18 of 77 stand on the Tarantula and 0 of 18 on the Lagoon (R4c 11 and 0, StarXTerminator 17 and 1). With
    floor3: added 0.5 / 3.8 / 1.8 %, broad 3.01 / 4.26 / 4.91. With protection alone: added nothing, broad 2.79 /
    3.77 / 3.59, standing 21 and 3. **The Lagoon's 4.47 is the highest any arm of the table reads** (R4b 3.50), 1.83
    over R4c where two seeds of one recipe have stood up to 1.3 apart, and over the brightest tenth of the Lagoon's
    nebula it reads 11.53. The crops above do not show it at their stretch; the row says this model takes more of
    the Lagoon's broad light than R4c, and the Tarantula's row says less.
  - **Reported, no bar: the grain in the Tarantula's cells** (`band.py`, the same session and script as R4c's
    table, each model as trained, R / G / B):

    | cell | truth plate | R4c | R4e |
    |---|---|---|---|
    | 4,4 (dense) | 4.8 / 5.0 / 5.2 | 4.6 / 3.8 / 3.0 | 4.5 / 4.1 / 3.5 |
    | 5,4 (dense) | 6.9 / 7.2 / 6.2 | 4.7 / 4.2 / 3.3 | 4.7 / 4.4 / 3.7 |
    | 4,6 (30 Doradus's core) | 78.9 / 101.0 / 104.7 | 64.1 / 76.2 / 61.9 | 76.0 / 83.9 / 71.2 |

    Against R4c the dense cells' grain is not lower: red is level, green 5 to 8 % higher and blue 12 to 17 %
    higher. The bright cell's band is 10 to 19 % higher, nearer the plate's. The pairs of seeds measured on these
    rows (the table of R3v's and R4a's above) stand up to 0.4 apart on cell 4,4, 1.1 on 5,4 and 39 on 4,6, so only
    the blue of cell 4,4 (0.5 over R4c) is outside a measured pair, by 0.1; none of these differences is shown to
    be the arm's. Each level is 0.993 to 0.999 of the plate's.
  - **Reported, no bar: the ratios the export took** (`PlateNoiseRatio` in synth30's `degradations.jsonl`, one
    value a channel a session, the level drawn over the master's record). **The bound reached far wider than this
    registration said.** Of the 113 sessions the trainer draws from, the level fell by over half a percent on 111,
    by over 5 % in some channel on 97, and by over 10 % in some channel on 51 (in every channel on 29); the
    sessions' mean ratio has a median of 0.92 and a lowest of 0.59 (an eta Carinae session, 0.59 / 0.55 / 0.64).
    The crowded fields of the scored kind are among them: the store's Tarantula session of 2024-10-02 drew at
    0.86 / 0.84 / 0.91, the SMC session of the same night at 0.88 / 0.88 / 0.92, the Sagittarius star cloud at
    0.90 / 0.90 / 0.93 and the five LMC colour sessions at 0.82 to 0.96. The registration's 0.97 for "the SMC
    session" is the narrowband one of 2024-09-27 (0.97 / 0.97 / 0.98), a validation session. Its count of 29 of the
    store's 199 sessions under 0.90 does not say how a session of three channels was counted; the export has every
    channel under 0.90 on 39 of its 138 sessions and some channel on 62. Lobster's 0.81 / 0.80 / 0.87 is as
    registered.
  - **The predictions.** The question missed: held. The crowded flat moving by under a point: missed, it fell 1.3
    with the chain and 3.1 as trained, on a row whose seeds lay 0.0 to 0.1 apart on the three recipes run twice.
    BP 17-19 inside a seed's spread of 96.0: held (94.7; seeds lay 1.4 to 1.7 apart). The dig as trained changing
    little: held on the M42 and SMC tiles (37.5 against 36.8 dug, 11.9 against 13.6), missed on Andromeda's master,
    where the dug share fell from 45.9 to 10.5, a difference of 35 points where the measured seed pairs of that row
    stand 9.6 and 6.4 apart and R4d less R4c was 26.4. And the prediction's reason was wrong: the bound did lower
    the training sessions of the scored fields, by 4 to 18 % (the ratios above), and the crowded flat fell all the
    same.
  - **What the read says, on one seed.** Drawing the noise at the plate's own level, a median of 8 % under the
    record, does not move the crowded rows toward StarXTerminator: more crowded stars are left, 8.9 % against 7.7 %
    with the chain (the crowded flat lay 0.0 to 0.1 apart between seeds with floor3, on the three recipes run twice,
    none of which draws faint stars) and 14.3 % against 11.8 % as trained, where no seed spread is measured. BP
    17-19 is 1.3 lower, which is inside a seed's spread and is not counted here. What it does change is on dark sky
    and in cores: less dug under Andromeda's bright stars and less of M31's nucleus taken as trained, both
    of which the product's post steps already cover. The Lagoon's broad light and the Tarantula's glow read worse.
    So the level of the training noise is not what holds the crowded fields, as the cap was not (R4d) and the
    faint stars were only in part (R4c). The cause the plan holds is still the one no arm has changed: the grain in
    the truth plate ("The crowded gap is in the truth plate" and "#2203 step 2" above). **The arm stops here.
    Nothing is adopted;** the bound stays the export's default, which is #1786's decision and not this arm's, and
    whether it should is the owner's to weigh from the rows above.
- **R4f, pre-registered 2026-11-20 before any export or score of it. One change from R4d: the plates the targets are
  drawn from have the grain of unresolved stars taken out of crowded sky (#2203).** The owner, 2026-11-20, asked
  which arm comes next after R4e's read: the grain-free targets on Azure, and a second arm on the 1070 (its own
  registration). Three arms changed the INPUT (R4c the stars under the catalogue, R4d their cap, R4e the noise's
  level) and none closed the crowded flat; this one changes the TARGET. synth31 = synth29's line exactly (R4d's:
  `bh-bin8`, `--faint-stars 3,1`, `--star-beta-draw 0.3,1.3,6,3`, `--saturated-own-share --saturated-measured
  --halo-over-plate`), with `export-synth.ps1 -PlatesRoot` naming the starless root whose plates went through
  `plate_grain.py` (T = 2, run over all 201 stored plates on 2026-11-18, the prototype of "#2203 step 2" above) and
  whose side files are the store's own, hard-linked beside them (`grain_store.py`). R3v's model and recipe, seed 0, a
  Batch job that removes itself (tag `r4faz`). The upload is capped at 10 Mb/s.
  - **What the plates are, looked at first** (`grain_look.py`, six plates at 1:1, stored beside the rule's). 160 of
    the 201 plates are changed and 41 left as they are (under eight crowded blocks). On a changed plate the rule
    smooths a median of 51 % of the pixels, and 92 to 98 % on the most crowded (eta Carinae at 135 mm, the Rosette,
    Vela, the Lagoon and Trifid, Omega Centauri). The pepper of faint stars the stored plates carry across such a
    frame is gone, the broad glow and the dark lanes stay, and a bright compact knot is kept whole. Two things to
    know: where a nebula's detail between 7 and 31 px is weak it is smoothed with the grain (the thin dark lanes of
    the Lagoon and Trifid plate come back softer), and the bright and dark pairs the builder left at bright stars are
    kept and now stand out of a smooth field. One pixel of one plate is newly absent (a Centaurus A plate's red, at
    its right edge); no other plate gained an absent pixel. **What the export does with such a pixel** (#2277, read
    from `DatasetDegradationExporter`'s stars mode, 2026-11-21): a NaN in any channel that reaches the frame's
    border is what `Image.AbsentPixels` calls absent, the canvas ring's rule, so the pixel is treated as ring. The
    synthetic background and the noised plate leave the plate's own value there and draw no level and no noise on
    it, the cell's mask hands it to the star renderer as absent, a value that is not finite is written through
    with no star's shot noise added, and the stretch and the noise calibrations leave it out of what they measure.
    The tile writer then clamps a NaN to 0 (`DatasetTileExporter.ExtractTileHalfs`), so a NaN that did reach an
    exported cell would be a zero in the target and in all four input draws, the path of the square of zeros below;
    it cannot by the bake's own rule, which samples cells only inside the NaN-free intersection of the session's
    frames, and a pixel on the plate's right edge lies outside every cell of a dithered session, whose rectangle
    of statistics stops short of the canvas edge (whether the Centaurus A session's does was not looked at). A
    cell is not skipped for holding one. Under R4f's line the one stamp read that is handed the absent mask is
    the halo over the plate's read of the measured saturated stars (`--halo-over-plate`,
    `InjectionPopulation.PlateRingExcess`, 60 px stamp): a star whose stamp holds an absent or non-finite pixel
    leaves the session's halo, never the cell, and a session with fewer than eight readable stars draws without
    the option (`MinHaloOverPlateStars`). The read of a saturated star itself (`--saturated-measured`, which
    R4f's line passes, a 36 px stamp) is handed no mask, so a zero ring does not refuse it; it does refuse a
    stamp where master less plate is not finite, so a NaN pixel inside it, the case here, costs that star its
    measurement: it is then drawn from its catalogue entry where the catalogue's pool would hold it (a star the
    builder subtracted with the field's Moffat and the catalogue reads as clipped in a channel), and left out
    otherwise. Where a drawn star lands depends on the
    placement: in a random-placement export, R4f's line included, `InjectionPopulation.RandomPosition` draws a
    position again when it lands on an absent pixel; under `--placement at-site` the star goes on its site
    through `Jitter`, which has no such test. `AddLobes` leaves out a lobe that would land on one under both
    placements, though R4f's line passes no `--star-lobes` and so draws no lobe at all. `MeasureBright` (`--bright-measured`, a 36 px stamp) refuses an absent stamp too, and R4f's
    line does not pass that option.
  - **What that changes in the tiles, checked before the export** (`r4f-check.ps1`: the store's Tarantula session
    of 2024-10-02 and the three Helix sessions of 2025-08-09, exported with this line from both roots;
    `tile_grain.py`). The sparse sessions' 120 target tiles are the same bytes from either root. The Tarantula's 40
    all differ: their texture between a 3 px and a 31 px box, in the tile's pixel noise, has a median of 0.78 /
    0.83 / 0.67 from the stored plate and 0.63 / 0.63 / 0.58 from the rule's (R / G / B), and a 90th percentile of
    3.73 / 3.75 / 4.45 against 1.27 / 1.60 / 1.20; the mean level is the same to two decimals. So the targets of a
    crowded session lose little in most cells and two thirds of their texture in the most textured ones. (The 90th
    percentile and that last sentence are withdrawn: see the correction below. The medians were read over the same
    40 tiles, 11 of them holding a block, and are replaced too: on the 29 tiles free of blocks the same statistic
    reads a median of 0.67 / 0.75 / 0.60 from the stored plate and 0.60 / 0.60 / 0.57 from the rule's, and a 90th
    percentile of 1.19 / 1.31 / 0.97 against 1.01 / 1.10 / 0.78, `tile_grain_clean.py`, #2289.)
  - **Three more differences from R4d, none chosen; the first two are small.** (1) The export draws its nebula
    knots where the plate's fine scales hold signal, so with the grain gone fewer are drawn in crowded sky: 27
    knots over the Tarantula session's 40 cells from the stored plate, 19 from the rule's. (2) R4d trained on the
    GTX 1070 and this arm trains on the T4. R4a's two seeds, one on each machine, read 88.5 and 88.5 on the
    crowded flat.
    (3) The exporter's defect below (added 2026-11-21 on the review's note, #2277): 11 of the Tarantula session's
    40 cells carry a square of zeros in synth29, in the target and in all four inputs, and none in synth31. What
    that alone moves on the crowded flat was not measured.
  - **A defect of the exporter found on the way** (read on synth29 and synth30; by the code it is in every export
    that lays its nebula knots along a steer): an elongated nebula knot laid along an orientation that is not a
    number is drawn as a square of NaN, 29 to 43 px, which the tile carries as zeros in the target and in all four
    input draws (`knot_angles.py`, `tile_holes.py`). synth29 and synth30 each hold 64 such knots of 1,052 elongated
    ones, in 54 of 5,520 cells (1.0 %) and 16 sessions, 11 of the cells on the Tarantula session. With the rule's
    plates that session has none. It is fixed apart from this arm; `bh-bin8` keeps it, so the other sessions'
    squares are in synth31 as they are in synth29. The fix (#2276, with this correction's PR: the fine orientation
    reads a luminance whose absent pixels are filled, and an angle that is not a number is no angle) was checked
    on the stored plates of the same four sessions with the fixed build (`knot-check.ps1`, `bh-bin11`): 16 of
    the 17 elongated knots without an angle before and none after, 12 of 160 target tiles and 48 of 640 input
    tiles holding a zero block before and none after, and the 9 to 41 px band of the Tarantula tiles that held no
    block the same from both builds (per tile 1.00 at the median, 0.93 to 1.05).
  - **A correction to the tiles' check, 2026-11-20, while synth31 exports and before any score of R4f exists.** The
    90th percentile above (3.73 / 3.75 / 4.45 against 1.27 / 1.60 / 1.20) read the defect's zero blocks, not the
    grain: 11 of the Tarantula session's 40 target tiles hold one from the stored plates and none from the rule's,
    and a square of zeros is a great deal of "texture" between two box means. So "two thirds of their texture in
    the most textured ones" was wrong. Read again on the 29 tiles free of blocks, with the plan's own statistic
    (`tile_band.py`, `tile_band_plate.py`: `band.py`'s 9 to 41 px band through a 7 px median, in sigma of a 9 px
    mean, R / G / B): the targets from the stored plates read a median of 2.9 / 2.9 / 2.5 and a 90th percentile of
    8.6 / 9.3 / 6.4, from the rule's plates 2.2 / 2.3 / 1.9 and 7.4 / 8.6 / 5.9. A sparse session's targets (Helix)
    read 1.4 / 1.4 / 1.4, and white noise alone 0.85. Per tile the band from the rule's plates is 0.94 / 0.94 /
    0.93 of the stored one at the median (lowest 0.59 / 0.54 / 0.59), because the change sits in one group of
    tiles. In green: the 9 tiles already at a sparse session's level (under 2.0) stay there (median 1.5 to 1.4);
    the 11 between 2.0 and 4.0 fall from a median of 2.9 to 2.3 (the largest falls 2.8 to 1.5, 3.2 to 2.1 and 3.5
    to 2.3; one stays at 3.5); the 9 at 4.0 and over, which hold nebula, read 8.3 and then 7.3 at the median, the
    three brightest (11.7 to 13.8) 2 to 3 % higher and four others 7 to 16 % lower. So **the arm takes about two
    fifths of the excess over a sparse session's level from the middle third of the crowded session's targets and
    leaves the rest much as they were**. Two more things the same table says. The export's target already holds
    less of the band than its plate does over the tile's own footprint (0.7 / 0.6 / 0.7 of it per tile from the
    stored plates), so the plate's grain was reaching the targets in part only. And the plates themselves fall
    further than the tiles (the plate's median over these footprints 4.1 / 4.6 / 3.5 to 3.0 / 2.9 / 2.2). The
    question, the guards and the kill rule above do not rest on the withdrawn figure and stand as registered; the
    prediction was written with it in mind, so its "1 to 3 points" is the upper end of what this dose can be
    expected to buy.
  - **A second correction, 2026-11-20, after the export and before any cache or score: the rule's first run did
    not know a canvas ring stored as zeros.** synth31's export ended with three sessions refused ("the starless
    plate did not read as linear": the SMC of 2024-10-02, which trains, and two that no list uses). The
    exporter's exit code did not show it; the cache step did, which refuses a listed session its root lacks.
    `plate_grain.py` kept only NaN absent. A master that is not on the unit scale stores its ring as exact zeros
    (89 of the 201 plates, 60 of the 138 exported sessions), and beside crowded sky the rule wrote its base plus
    noise into that ring: on 61 plates, 173 to 29,448 ring pixels each, some of them under zero (`grain_ring.py`).
    The product reads an all-zero pixel at the border as absent, so a changed ring pixel became sky, and on three
    plates the stretch then read the frame as not linear. The ring's blocks also went into the rule's own grain
    statistic. The earlier check of the plates ("one pixel of one plate newly absent") counted NaN and so could
    not see it. **The rule now hands a zero ring back as it came and keeps it out of its statistics**
    (`zero_ring`, the product's rule for zeros: exact zero in every channel, connected to the border;
    `test_plate_grain.py`), and a plate without such a ring comes out as the first run wrote it. Run again over
    all 201 plates (`starless-grain2`): 62 differ from the first run, every one of them a plate with a zero ring,
    and no ring pixel is changed on any. The 48 exported sessions whose plate differs (the three refused ones
    among them) were taken out of synth31 and exported again with the same line and binary
    (`repair_export.py`, `reexport.ps1`; the first run's rows and tiles are kept beside the export), and the cache
    is built from that. **The sky beside a ring** (`grain_ring_pull.py`, the question #2297's review asked: does the
    rule pull it toward the flat fill?) read, over the 89 plates with a zero ring, 0.01 % of the pixels within 20 px
    of the ring changed by over 10 %, against 0.00 % at 40 to 60 px; the worst plates 2.74 % near against 1.02 %
    far (figures from #2297's comment). A pull would show as the near band changing more with a sign; the medians
    show none, and the worst plates differ by under two points of a band. **What `grain_ring.py` itself read**
    (#2298, run over the store on 2026-11-21 against the corrected plates, `starless-grain2`): of the 201 plates
    89 hold a zero ring and on none is a ring pixel changed, and the 62 that differ from the first run all hold
    one. Its `near_lowered` (valid pixels within 20 px of the ring that the rule lowered by over 10 % of their
    value in any channel) is zero on 10 of the 62 and over zero on 52, with a median of 93.5 px a plate, 7,825 px
    at most and 50,496 px in all. Against the band it is counted in (17.8 million valid pixels within 20 px of a
    ring over the 89 plates, where the count is 50,503) that is 0.28 %. That band is the script's own and not the
    one the figures above use: 20 steps along rows and columns from the ring, a diamond, where
    `grain_ring_pull.py` measures 20 px in a straight line; and a pixel counts as lowered when the corrected
    plate is under 0.9 of the stored one, which is a drop only where the stored value is over zero: a pixel the
    rule left as it was counts too when a channel of it is under zero. On the three worst plates below that is
    90 of the 7,825 px, 637 of the Rim Nebula's 6,936 and none of the third, so counting only a channel over
    zero that dropped they read 4.28 %, 3.40 % and 2.69 %. The median
    plate has 0.005 % of its band lowered, 22 plates have over 0.1 % and 9 over 1 %, and the worst are
    narrowband frames: 4.33 % (eta Carinae of 2026-02-20 through the 3 nm filter, 7,825 of 180,917 px), 3.75 %
    (the Rim Nebula in SII of 2024-07-06) and 2.69 % (another plate of that eta Carinae session); what lies at
    their border was not looked at. The count has no far band of its own, so `grain_ring_pull.py` was run on
    those three: changed by over 10 % either way, the two eta Carinae plates read 7.63 % within 20 px against
    11.98 % at 40 to 60 px and 10.20 % against 21.48 %, and the Rim Nebula 9.60 % against 25.76 %. On all three
    the sky beside the ring changed less than the sky further in, so what the count holds on them is what the
    rule does anywhere on such a frame and not a pull toward the fill. The tiles' check above stands: its two sessions' plates hold no zero ring. The question,
    the guards and the kill rule are unchanged.
  - **Added 2026-11-20, before the cache exists and before any score: what the question reads, and two rows that
    read what this arm changes.** "The crowded rows read light between the stars" below shows that the Tarantula's
    crowded flat moves from 91.3 to 95.9 on R4d's own plate when only light at 15 px and over is changed, and that
    the plate then still looks mottled. This arm takes the grain out of the targets and leaves their level, so its
    registered question may be missed by a model that does what the arm is for. The question and the kill rule
    stay as registered. Beside them, with bars set now (the "Reported, no bar" line below lists the grain figure as
    reported; the bar on it is row (2)'s): (1) Gaia BP 17-19 removed with the level set aside
    (`gaia_compact.py`, through the product's chain) at least 93.5 (half the gap as first read: R4d 90.5,
    StarXTerminator 96.6, drawn with the script's earlier single random stream. Re-read the same day with its draw
    per remover and bin, before this arm's cache exists: R4d 90.7, StarXTerminator 96.6. The bar stays as written;
    the level-inclusive row stands 1.4 to 1.7 apart between seeds, this one's spread is not known); (2) the grain
    in the Tarantula's dense cells 4,4 and 5,4 as trained (`band.py`) at most 0.6 of R4d's in every channel (R4d
    4.3 / 3.8 / 3.1 and 4.7 / 4.6 / 3.6; two seeds of one recipe have stood 23 to 33 % apart on cell 5,4). The
    crowded flat between 91.3 and 93.3 with both of these cleared is not a kill and not a pass: it goes to the
    owner with the 1:1 crop of the dense field beside R4d's and StarXTerminator's. Under 91.3 is the kill below,
    whatever the rows read. **A row missed is only reported, never a kill and never a pass by itself:** a pass
    needs the flat at 93.3 or over with every guard held, and rows (1) and (2) missed beside such a flat go to
    the owner with the same crop, since they say the plate scores by level and still looks mottled. A guard
    failing is a kill, as registered.
  - **Added 2026-11-20 evening, while the cache uploads and before any score: what the target itself reads, and
    one guard it cannot hold.** The note above reasoned that the question reads mostly the level and may be missed
    by a model that does what the arm is for. That was wrong, and the control is under "Corrected the same
    evening: the crowded flat reads the lumps, not the level" below. `grain_rule_post.py` applies this arm's
    target rule to R4d's own plate through the chain, so the plate holds what a perfect learner of the target
    would write on the scoring masters. It reads the Tarantula's crowded flat at 97.8 (the question asks 93.3),
    BP 17-19 with the level set aside at 96.6 (row (1) asks 93.5), the blotch line of the Tarantula's densest
    cells at 0.12 (R4d 0.29, StarXTerminator 0.14), and every whole-master guard inside its bar (Andromeda dark
    flat 100.0 with 0.0 dug, Tarantula dark flat 98.3, the glow's green median -32.0 and 98.8, the nucleus 0.0,
    the Sagittarius crowded flat 99.7, the light taken from the Tarantula field 81 and 83 %) **except purity:
    2.6 against the guard's 1.0.** Purity counts the stars layer's light away from every catalogued star. The
    lumps this arm is meant to remove are such light, and a plate with only the judge's level taken reads 3.4 on
    it, so the row cannot tell the level from the grain. An arm that does what it is for would be killed by that
    guard as registered. **For this arm purity is therefore reported and is not a guard.** What it stood for
    stays guarded by the light taken from the Tarantula field (at most 83 and 84 %), which reads the level and
    nothing else: 98 and 106 on the level-only plate and on StarXTerminator's, 81 and 83 on the rule plate. The
    tiles' guards (by count, M42, the SMC) were not read on that plate. **The nebula guard on light added stays a
    guard as registered, and a plate that meets the target fails it on the Tarantula.** The first argument here
    (fresh noise, "a model draws none") did not close, as the review of #2302 showed, so the split was measured
    the same evening with `grain_rule_post.py --own-noise`: the plate keeps its own noise where the rule would
    draw fresh noise, so what it adds over the input is the rule's fill alone.

    | light added on the nebula, % | R4d through the chain | the rule plate, own noise (the fill) | the rule plate, fresh noise |
    |---|---|---|---|
    | Andromeda | 0.11 | 0.62 | 2.65 |
    | Tarantula | 0.65 | 2.09 | 4.27 |
    | Lagoon | 0.30 | 0.30 | 0.30 |

    Of the rule plate's 2.7 and 4.3 %, about 2.0 and 2.2 points are two independent noises standing apart (more
    than the 1.7 % a white noise allows by the row's threshold, a 3 px mean over one pixel sigma, since a stacked
    master's noise is correlated), and the fill adds 0.5 points on Andromeda and 1.4 on the Tarantula over R4d's
    own. As trained R4d reads 0.0, so a perfect learner of this target would read about 0.5 and 1.4 against the
    bar of 1.0. Unlike purity the row is not misread: on the Tarantula the newly added pixels lie where the input
    stands 3.6 to 9.7 sigma under its own 31 px median (median 6.5; `nebula_added_look.py`, whose sheet shows the
    largest of them as a dark pocket beside 30 Doradus's core). They are dark structure of the nebula that the
    rule fills, a cost of the target rule and not of the scorer. So the bar stays. R4f failing it on the
    Tarantula would be a finding against the rule (the nebula has to be held out of it), and the read is to say
    so in those words. The owner may rule otherwise before the read; until then the registration stands.
    Measured after it (`grain_rule_nebula_out.py`: the rule plate with `nebula_rows.py`'s own nebula mask, the
    input's large-scale light 20 sigma over the sky, held out of the rule): the light added returns to R4d's 0.1 /
    0.7 / 0.3 % and the crowded rows keep their gain (Tarantula 97.8, Sagittarius 99.7, BP 17-19 with the level
    set aside 96.4); the mask is 2.0 % of the Tarantula's frame before its feather (the rows above were scored on the plate that holds
    out the mask plus the tool's 8 px Gaussian feather, which is wider; the tool prints it as "held out with its
    feather", and an arm that holds the nebula out must quote both areas: here 2.0 and 4.2 % of the Tarantula's
    frame, 5.5 and 7.4 % of Andromeda's, 0.8 and 1.5 % of the Lagoon's). Held out at 5 sigma the mask is half that frame (45.2 %, and 49.9 % with the feather),
    since crowded sky is itself bright at that scale, and the gain is gone (crowded flat 91.9, BP 17-19 91.9). So
    the bright nebula can be held out of the rule at no cost to the rows, and the faint wisps beyond it cannot
    be protected by a brightness mask.
    The Lagoon's broad light on it is 2.04 (R4d through the chain 2.04)
    and 17 of the Tarantula's 77 brightest peaks stand. Reported beside row (2) from now on: the scorer's blotch
    line on the held-out Tarantula.
  - **The question, through the product's chain** (core protection without the stars, then floor5; R4d re-read
    through it on 2026-11-20 before this registration): the Tarantula's crowded flat at least 93.3, which is R4d's
    91.3 plus 2 (R4c 90.3, R4e 89.1, StarXTerminator 96.1). R4e's 89.1 is a read through this chain and not its
    registered read carried over: R4c, R4d and R4e were each run through it on 2026-11-20, and each arm's row is
    kept as `row.txt` in its `-pcs+floor5` folder of the whole-master run (`C:/temp/tianwen-scratch/f2-whole-master`
    on DE1). R4e reads 89.1 there, the same figure as its registered read with floor3 and the old protection.
  - **Guards, through the same chain,** R4c's: by count clean at least 98.5 at 100-1000 sigma and 98 at 1000 and
    over (the tiles with floor5); M42 100-1000 at least 88; SMC 20-100 and 100-1000 at least 97; Andromeda dark flat
    at least 95 and dug at most 3; Tarantula dark flat at least 91; the glow's green median within 450 of zero on
    Andromeda and within 500 on the Tarantula; the M31 nucleus under 10 %; purity at most 1.0 % (R4c's bar, **not a
    guard for R4f**: purity is reported for this arm, see "For this arm purity is therefore reported" above); the
    Sagittarius crowded flat at least 96.3. R4d's own: the light taken from the Tarantula field (green, the densest tenth of
    cells and the whole frame) at most 83 % and 84 % (R4d 81 and 82).
  - **Nebula guards, as trained** (the rule since 2026-11-19; R4d reads added 0.0 / 0.0 / 0.0, broad 2.88 / 5.86 /
    2.67, 17 of 77 and 0 of 18 standing): light added on at most 1.0 % of each master's nebula (**it stays a
    guard**; a plate that meets the target reads about 1.4 on the Tarantula from the rule's fill alone, see "The
    nebula guard on light added stays a guard" above); the Lagoon's broad
    light taken at most 4.0 (seeds of one recipe have stood up to 1.3 apart on it); of the brightest tenth of the
    Tarantula's peaks at least 11 of 77 standing (R4c's, the fewest of the faint-star arms; StarXTerminator 17).
  - **Kill:** a guard fails (purity is not one for R4f; light added on the nebula is one), or the crowded flat under R4d's 91.3. Between 91.3 and 93.3 with every guard held it
    neither passes nor is killed; the rows go to the owner.
  - **Reported, no bar:** purity through the chain (R4d 0.8, the rule plate 2.6, a level-only plate 3.4), and the
    scorer's blotch line on the held-out Tarantula (R4d 0.29, StarXTerminator 0.14, the rule plate 0.12); every row
    as trained (R4d: Andromeda dark flat 69.4 with 19.5 dug, Tarantula dark flat
    80.3, Tarantula crowded 86.7, BP 17-19 95.4); Gaia BP 17-19 through the chain (R4d 96.0); the grain in the
    Tarantula's dense cells 4,4 and 5,4 (`band.py`, R4d 4.3 / 3.8 / 3.1 and 4.7 / 4.6 / 3.6); the band of 30
    Doradus's core, cell 4,6, which carries no bar because two seeds of one recipe have stood 39 to 75 % apart on
    it (R4d 44.4 / 49.4 / 39.7, R4e 76.0 / 83.9 / 71.2); the twin error. Added 2026-11-20 while the cache
    uploaded: the bright stars of #2292, as trained and through the chain, the ring medians beside R4d's and
    StarXTerminator's. As first written this was HIP 80609 alone, on the trainer's validation session's master
    (`arms/r2-val.txt`). Widened the same evening, still before any score, to the fourteen stars of "A star that
    stands far out is left standing" below, pinned in `training/star-removal/bright_star_list.tsv` (name, master,
    x, y); thirteen of them are on masters no gradient step saw (two of those on the validation session's), so on
    those the row is held-out evidence. Widened once more that night, still before any score, to the twenty-four
    stars the census without a cap found (twenty-two of them on masters no gradient step saw); HIP 80609's star
    is Antares.
  - **Looked at before the rows are quoted:** the Lagoon's core, 30 Doradus's core, the Tarantula's knots, a
    crowded Tarantula field, M31's nucleus and a bright star on dark sky, as trained and through the product's
    chain, beside R4d and StarXTerminator. The crowded field is where a pass should show (StarXTerminator's is
    smooth, ours mottled); the Lagoon's lanes are where the rule's smoothing would show as a cost.
  - **Prediction:** the dense cells' grain as trained falls by a quarter to a half, since the target no longer
    holds it, and the crowded flat rises by 1 to 3 points, short of the bar or just at it. The Lagoon's broad
    light taken rises toward its guard, because a model taught that crowded sky is smooth has less reason to keep
    weak detail there. If the crowded flat does not move, the grain in the target is not what holds the crowded
    fields either, and what is left is the model's capacity or the judge's own smoothing of the glow.
  - **Nothing is adopted;** adoption is the owner's.
  - **R4f's read, 2026-11-21 (seed 0, `r4f_synth31_s0`): KILLED. The Tarantula's crowded flat reads 90.3, under
    R4d's 91.3; every guard held. Its training stopped improving at step 69,500, which none of R4c, R4d, R4e and R3x
    did (earlier stalls are arm B's seed 1, R2n and R3w, above), so the read says little about
    the target.** Through the product's chain unless a row says "as trained"; R4d's tile
    rows are this step's own, with floor5:

    | row | R4d | R4f | bar | read |
    |---|---|---|---|---|
    | Tarantula crowded flat | 91.3 | **90.3** | at least 93.3, killed under 91.3 | **killed** |
    | added row (1): Gaia BP 17-19 with the level set aside | 90.7 | 90.0 | at least 93.5 | missed |
    | added row (2): grain in dense cell 4,4, as trained, R / G / B | 4.3 / 3.8 / 3.1 | 3.8 / 3.3 / 2.7 | at most 0.6 of R4d's | missed: 0.88 / 0.87 / 0.87 |
    | added row (2): grain in dense cell 5,4, as trained | 4.7 / 4.6 / 3.6 | 4.4 / 4.0 / 3.3 | at most 0.6 of R4d's | missed: 0.94 / 0.87 / 0.92 |
    | by count clean, 100-1000 and 1000 and over | 99.2, 100.0 | 99.1, 100.0 | at least 98.5, 98 | held |
    | M42 100-1000 by count clean | 93.4 | 92.6 | at least 88 | held |
    | SMC 20-100 and 100-1000 by count clean | 98.9, 100.0 | 98.9, 100.0 | at least 97 | held |
    | Andromeda dark flat, dug | 98.5, 0.7 | 98.6, 0.7 | at least 95, at most 3 | held |
    | Tarantula dark flat | 96.5 | 96.1 | at least 91 | held |
    | glow green median, Andromeda and Tarantula | -8.5, +106.5 | -82.8, -270.5 | within 450, 500 | held |
    | M31 nucleus with protection | 0.0 % | 0.0 % | under 10 % | held |
    | Sagittarius crowded flat | 97.0 | 97.6 | at least 96.3 | held |
    | light taken from the Tarantula field, densest tenth and whole frame | 81, 82 % | 82, 84 % | at most 83, 84 % | held, the second at its bar |
    | light added on each master's nebula, as trained | 0.0 / 0.0 / 0.0 % | 0.0 / 0.1 / 0.0 % | at most 1.0 % | held |
    | the Lagoon's broad light taken, as trained | 2.67 | 1.51 | at most 4.0 | held |
    | brightest tenth of the Tarantula's peaks standing, as trained | 17 of 77 | 17 of 77 | at least 11 | held |

    - **Looked at before the rows** (the two sheets, each panel cropped to 1:1, and the bright stars' sheets). **The
      Tarantula's crowded field:** as mottled as R4d's, as trained and through the chain, with nothing of
      StarXTerminator's smooth field. **The Tarantula's knots:** taken more than R4d takes them (the upper knot a
      dim pink smudge, the lower ones gone), though the peaks row counts the same 17. **30 Doradus's core, the
      Lagoon's core with its lanes, M31's nucleus:** as with R4d. **Bright stars:** Maia of 2022-01-05 and Antares
      of 2026-04-21 are left whole, core and all, where R4d dims the core.
    - **The training.** The trainer stopped itself at step 129,500 of 480,000, and the Batch pool removed itself.
      The held-out objective was best at step 69,500 (1.08e-5, still at the first learning rate) and never
      improved again through all four halvings. Every figure below is a median of the trainer's window means
      (`VAL_WINDOW` in `n2n_smoke.py`, the mean of the last three scores, which is what its plateau rule and its
      "best" judge), read from its `val STEP held-out X window Y best Z at S lr L` lines in the logs
      `C:/temp/e2/r4faz-batch/r4faz.log`, `C:/temp/e2/r4d.log` (a GTX 1070 run, not Batch),
      `C:/temp/e2/r4eaz-batch/r4eaz.log`, `C:/temp/e2/r4caz-batch/r4caz.log` and
      `C:/temp/e2/r3xaz-batch/r3xaz.log`. **Nothing in its first learning rate marks R4f as slow.** The rate was first
      halved from step 57,000 in R4e, 63,500 in R4d, 77,500 in R4c and 82,000 in R4f (decided at step 81,500), so no
      one range of steps is "before the first halving" for all four, and at step 69,500 R4d's window of 1.04e-5 is
      one at the halved rate against R4f's 1.08e-5 at the first. At the first rate the best windows were R4f
      1.08e-5 (step 69,500), R4d 1.20e-5 (51,000), R4c 1.26e-5 (65,000) and R4e 1.50e-5 (44,500); R4f's is the
      lowest, which does not make it ahead, since each arm's figure is on its own draws and targets (the caveat
      below). What needs no scale across arms is each arm against itself. **They part at the halvings:** aligned on each arm's own first halving, the median from step 30,000
      to the halving and then over the next 60,000 steps is R4d 1.96e-5 then 9.82e-6, R4e 2.11e-5 then 9.95e-6 and
      R4c 2.10e-5 then 1.01e-5 (each about half), where R4f's is 1.76e-5 then 1.49e-5 (a fall of 15 percent, over
      the 47,500 steps it ran after its first halving); R4d went on to 7.85e-6 at step 233,000. So the flat stretch
      before the stop does not separate R4f from the arms that went on: its halvings bought almost nothing, where
      theirs halved the objective. **Nor did the later steps hold weights as good as the
      best:** R4f's windows sat at 1.08e-5 to 1.16e-5 through step 72,000, then came a spike (held-out 4.88e-5 at
      step 73,000, window 2.50e-5) with no recovery to a new best: only 7 of the 114 windows after step 72,500 reached 1.2e-5 or
      lower, and the final window is 1.57e-5, 45 percent above the best. That is the shape R2n's note records (above,
      "the deeper net blew up"). The first resume (after step 72,000) is not its cause: replaying steps 72,500, 73,000
      and 73,500 gives the same digits (1.433862e-05, 4.882240e-05, 1.355304e-05). The run was moved to a new node
      twice (resumed after steps 72,000 and 110,000); R4c, R4e and R3x were moved too, and each set a new best after
      its earlier resumes (R4c after steps 40,000, 76,000 and 98,000; R4e after 100,000; R3x after 34,000, 72,000
      and 104,000). R4c's and R3x's last resumes gained little or nothing: R4c's, after step 128,000, set no new
      best (its best stays 8.95e-6 at step 121,000 to the stop at 157,000, the lowest window after step 128,000
      9.01e-6), and R3x's, after 132,000, gained 0.15 percent (5.980e-6 to 5.971e-6). R4e's one resume is also its
      last, and its best fell by 14 percent after it (8.50e-6 at the resume, 7.28e-6 at step 170,500). **These figures are not like for like across arms:**
      each arm's held-out objective is read
      on its own validation draws (R4f on synth31's, whose targets are the grain-free plates; R4d on synth29's and R4e
      on synth30's, on the stored plates), and this plan already calls a held-out loss on another arm's draws "not
      comparable" (arm A and B, above). The gap says R4f's curve was higher after the spike, not by how much the model
      is worse. The trainer keeps the last step's weights, so the model scored is the one at the stop and not the one
      at its best (a window of 1.57e-5 against 1.08e-5). Why the held-out objective spiked at step 73,000 and did not
      recover is not known.
    - **Reported.** Purity 1.0 (R4d 0.8); the blotch line on the Tarantula's densest tenth 0.29 (0.29). As
      trained: Andromeda's dark flat 68.3 with 19.8 % dug (R4d 69.4, 19.5); the Tarantula's 70.0 with 28.1 % left
      (80.3, 14.3); its crowded flat 85.9 (86.7); BP 17-19 95.1 (95.4), and through the chain 95.5 (96.0); the M31
      nucleus taken in its worst channel 27.2 % (9.7); broad light taken on Andromeda and the Tarantula 2.91 and
      5.30 (2.88 and 5.86); the Tarantula's glow median -533 (-6.5). 30 Doradus's core, cell 4,6: 52.4 / 54.5 /
      39.8 (R4d 44.4 / 49.4 / 39.7, the plate 78.9 / 101.0 / 104.7). Faint stars left on bright background as
      trained: 11 % of 142 on the Lagoon's core (R4d 29) and 25 % of 321 on 30 Doradus (22). Where the BP 17-19
      stars are left, with the level set aside: R4f takes more than R4d in the most crowded fifth (85.7 against
      83.6) and less in the least crowded (90.0 against 94.0). The twin error: 2.49 sigma in the mask as trained,
      1.06 with floor5. The bright stars of #2292, 26 rows: inside 22 px R4f leaves over 1.2 times R4d's on 18 and
      less on none, and on five rows more than the master itself holds there (Maia of 2022-01-05 6,567 sigma
      against 4,649; also Merope, Alcyone and Electra of that night and Alcyone of 2022-01-04).
    - **Against the prediction.** The dense cells' grain as trained fell by 6 to 13 percent, where a quarter to a
      half was predicted. The crowded flat fell by 1.0, where a rise of 1 to 3 was predicted. The Lagoon's broad
      light taken fell from 2.67 to 1.51, where a rise toward its guard was predicted. All three missed.
    - **What it says.** By its registration the arm is killed, and nothing is adopted. It does NOT show that the
      grain in the target is not what holds the crowded fields: the prediction's last sentence, for a crowded flat
      that does not move, took a model trained as R4d was for granted, and this one was not. Its rows as trained
      (twice the stars left on the Tarantula's dark flat, the bright stars left whole) are those of a model that
      stopped learning early. Two things would separate the target from the training: the same arm run again
      (another seed, or the same seed on the other machine), and a trainer that keeps the weights of its best
      step. Neither is registered, and both are the owner's.
  - **R4f run again, registered 2026-11-21 before its launch and before any score of it (seed 1,
    `r4f_synth31_s1`). Nothing is changed from R4f but the seed.** The owner, 2026-11-21, on the two things the
    read names: "yeah lets rerun R4f". The cache is R4f's own, already in the container (`r2-synth31/cache`), so
    no export is made. The recipe is R4f's (the Batch task's, with `--levels 4 --max-ch 256 --clip-grad 8`). The
    trainer is R4f's frozen one plus `--save-best` only (PR #2329), which writes the weights of the best step
    beside the last step's and changes nothing the run computes: on a tiny cache with the arm's switches the
    frozen trainer without the patch, with it, and with it and the switch end on the same weights to the bit, and
    the best file equals the weights of a run stopped at that step. One Batch run on a low-priority T4, capped
    at 36 hours, whose pool removes itself.
    - **The question.** Was R4f's training an accident of one run, or does this target with this recipe train
      so? Only when it trains as the others did does the arm answer what R4f was registered to ask.
    - **The training is read first, from the trainer's log alone, before any row is scored.** One figure, the
      one R4f's read aligns the arms on: the median of the window means over the 60,000 steps after the first
      halving (or to the stop, the length then stated), as a share of the median from step 30,000 to that
      halving. R4d reads 0.50, R4e 0.47, R4c 0.48 and R4f 0.848. **0.60 or under: trained as the others.
      0.75 or over: stalled again.** Between the two it is called neither, and the rows are reported with that
      said. The figure is read against the curve before it is called: R3x reads 0.80 on it (6.06e-6 over 7.60e-6,
      its first halving at 101,000) and is not a stall. Its curve has two isolated spikes before the halving
      (held-out 1.934e-5 at step 74,500 and 1.101e-5 at 75,500 against 6.8e-6 around them, back to 7.0e-6 by
      77,000; 1.192e-5 at step 85,000, back to 6.8e-6 by 88,000), and both recovered within a few evaluations. It
      set a new best (5.971e-6 at 148,500) 47,500 steps after the halving; a long first-rate stretch (71,000 steps
      against 27,000 to 52,000 for the others) lifts the ratio. The spikes do not separate R4f from R3x: R4f's
      window was back inside its level before the spike six evaluations after it (1.13e-5 at step 76,000, against
      1.08e-5 to 1.16e-5 over steps 69,500 to 72,000), as R3x's was after five; its next evaluation, step 76,500,
      was a second and larger spike (held-out 1.04e-4, the window back to 1.25e-5 only at step 80,000), which R3x
      has no counterpart for. What separates them is the new best: R3x, R4d, R4e and R4c each set a new best
      window after their first halving, and R4f set none to its stop. **So a ratio of 0.75 or over is called
      stalled only when the run sets no new best window after its first halving; with a new best there it is
      called neither, and the rows are reported with that said.** A new best is one the trainer's own log
      prints (its "best ... at" column moving, which its plateau rule does for a window at least 0.1 percent
      under the best so far); any such counts, and no larger size is asked of it. That holds over 0.85 as under it: a long
      first-rate stretch can lift a run that went on improving past any ratio. R4f itself reads 0.848 and set
      no new best, so an exact repeat of it is called stalled. Spikes are described and decide nothing. 0.60 or
      under is trained, as launched; no threshold changes. **These sentences, from "The figure is read against the curve", were added after the
      launch**, on the review of this registration (PR #2335), which found that the figure as launched would
      have called R3x stalled: I had set the thresholds on R4c, R4d, R4e and R4f and left R3x out. The rule's
      sentences were changed six times on 2026-11-21, each timed here against the trainer's log (it started at
      10:00:46 and reads step 13,800 at 43.1 minutes and step 27,600 at 86.5):
      (1) at 10:40, about step 12,600, the R3x sentence and a band of 0.75 to 0.85 read against "a spike, a new
      best after the halving";
      (2) at 10:44, about step 13,800, a second wording, "a spike with no recovery", with both signs needed for
      stalled;
      (3) at 10:45, about step 14,400, the clause that one sign alone is called neither;
      (4) at 10:50, about step 15,800, the new best alone, spikes deciding nothing: the second wording was
      withdrawn, since by it R4f had recovered too;
      (5) at 11:27, about step 27,500, the test's reach to any ratio of 0.75 or over, R4f's 0.848 and its second
      spike;
      (6) at 12:07, about step 40,000, what counts as a new best.
      What the changes alter against the launched sentence, "0.75 or over: stalled again", is one outcome: a
      ratio of 0.75 or over with a new best after the halving read stalled as launched and reads neither now.
      Changes (1) to (4) made that so for a ratio of 0.75 to 0.85, and change (5) for a ratio over 0.85. Every
      held-out line of the log to step 39,000 is at the first learning rate, and the earliest first halving
      among the other arms was at step 57,000. So nothing the figure reads existed yet. This note was first
      written with change (3) and has been corrected since. The prediction's reasons below were reworded in
      the same review (they first said R4c, R4d and R4e each differ from R4f by one change of the export, which
      this plan's own lineage contradicts); the guess and its figures are as launched. The additions are the
      owner's to refuse; refused, the rule is the one sentence as launched.
    - **The read is R4f's, unchanged, on the last step's weights** as for every arm before: its bars, its
      guards, its kill rule and its two added rows stand as its registration and its read list them (the
      Tarantula's crowded flat through the product's chain 93.3 or over and killed under R4d's 91.3; BP 17-19
      with the level set aside 93.5; the dense cells' grain as trained 0.6 of R4d's). The 1:1 sheets are looked
      at before any row is quoted.
    - **The best step's weights are a reported column and decide nothing.** They are scored on the same rows
      and set beside the last step's. No arm before was read on a best step, so no bar or kill rule applies to
      them.
    - **What each outcome says.** Trained as the others and the crowded flat at its bar: the grain-free target
      works, and seed 0 was an accident of its training. Trained as the others and killed again: the grain in
      the target is not what holds the crowded fields, the answer R4f could not give. Stalled again: the stall
      is this target's with this recipe (two seeds of two) and the rows judge the target no more than R4f's
      did; what is then worth reading is the best step's column against R4d, and the next step is the
      trainer's (why the objective breaks and does not come back), not another export.
    - **Prediction, written before the launch.** It trains as the others (my guess, two chances in three: R4c,
      R4d and R4e train R3v's model and recipe on exports of the same line and none broke, and R4d is the one
      change neighbour of R4f, its target; the earlier stalls this plan records are arm B's seed 1, R2n and
      R3w). If
      it does, the dense cells' grain as trained falls by a quarter or more against R4d's, and the crowded flat
      rises over R4d's 91.3 but does not reach its bar of 93.3.
    - **The rerun's read, 2026-11-21 (seed 1, `r4f_synth31_s1`, the last step's weights): it trained as the
      others did, and it is KILLED again. The Tarantula's crowded flat reads 91.0, under R4d's 91.3; every guard
      held; both added rows are missed, and the dense cells hold as much grain as R4d's.** So the registered
      outcome is the third: the grain in the target is not what holds the crowded fields.
      - **The training, read first.** The Batch job ended by itself at step 207,500 of 480,000 ("converged: no
        gain after the last halving") and its pool removed itself. The rate was halved from steps 52,000, 96,500,
        157,500 and 180,500, and the best window moved after every one of them (8.98e-6 at step 84,000, 7.82e-6
        at 126,000, 7.59e-6 at 145,000, 7.27e-6 at 168,000, 7.12e-6 at 195,500, the best). The registered
        figure: the median of the window means from step 30,000 to the first halving is 1.85e-5 and over the
        60,000 steps after it 9.66e-6, a ratio of 0.52 (R4d 0.50, R4e 0.47, R4c 0.48, R4f's first run 0.848).
        0.60 or under is "trained as the others". The run was moved to another node once (resumed after step
        128,000, best then 7.82e-6 at 126,000) and went on to new bests after it. As with every arm the figure is
        on the arm's own draws and targets and is not a level to set beside another arm's.
      - **Looked at before the rows** (the two sheets of the registered spots, each panel cut out at full size;
        the notes were written before a row was opened). **The Tarantula's crowded field:** StarXTerminator's
        plate is flat and dark, R4d's and seed 1's are mottled with reddish lumps of some tens of pixels, as
        trained and through the chain; seed 1's cannot be told from R4d's by eye, and it shows a few compact
        star-like residues R4d's panel does not show at those places. **The Tarantula's knots:** both of ours
        take the bright compact knots StarXTerminator keeps; seed 1 keeps more of the upper knot's body than R4d
        and has no purple cast there. **30 Doradus's core:** seed 1 as trained is close to StarXTerminator's, and
        the purple patches R4d has (lower left of the core, the cluster at the upper left) are not in it; through
        the chain both show small star-like dots round the core and a dark notch at the upper left. **The
        Lagoon's core:** clean in all three, seed 1 with fewer faint dots than R4d. **M31's nucleus: seed 1 as
        trained dims it and turns it yellow,** where StarXTerminator and R4d keep it white; through the chain it
        is white again. **Bright stars:** their sheets were looked at after their rows had printed (Antares of
        2026-04-21 and Electra of 2022-01-05 are left nearly whole, where R4d dims the core).
      - **The rows.** Through the product's chain unless a row says "as trained"; R4d's and seed 0's tile rows
        are those of their own steps, with floor5:

        | row | R4d | R4f seed 0 | R4f seed 1 | bar | read |
        |---|---|---|---|---|---|
        | Tarantula crowded flat | 91.3 | 90.3 | **91.0** | at least 93.3, killed under 91.3 | **killed** |
        | added row (1): Gaia BP 17-19 with the level set aside | 90.7 | 90.0 | 89.9 | at least 93.5 | missed |
        | added row (2): grain in dense cell 4,4, as trained, R / G / B | 4.3 / 3.8 / 3.1 | 3.8 / 3.3 / 2.7 | 4.5 / 4.0 / 3.5 | at most 0.6 of R4d's | missed: 1.05 / 1.05 / 1.13 |
        | added row (2): grain in dense cell 5,4, as trained | 4.7 / 4.6 / 3.6 | 4.4 / 4.0 / 3.3 | 4.8 / 4.4 / 3.8 | at most 0.6 of R4d's | missed: 1.02 / 0.96 / 1.06 |
        | by count clean, 100-1000 and 1000 and over | 99.2, 100.0 | 99.1, 100.0 | 99.2, 100.0 | at least 98.5, 98 | held |
        | M42 100-1000 by count clean | 93.4 | 92.6 | 94.1 | at least 88 | held |
        | SMC 20-100 and 100-1000 by count clean | 98.9, 100.0 | 98.9, 100.0 | 98.9, 100.0 | at least 97 | held |
        | Andromeda dark flat, dug | 98.5, 0.7 | 98.6, 0.7 | 98.5, 0.9 | at least 95, at most 3 | held |
        | Tarantula dark flat | 96.5 | 96.1 | 95.9 | at least 91 | held |
        | glow green median, Andromeda and Tarantula | -8.5, +106.5 | -82.8, -270.5 | -135.2, +120.1 | within 450, 500 | held |
        | M31 nucleus with protection | 0.0 % | 0.0 % | 0.0 % | under 10 % | held |
        | Sagittarius crowded flat | 97.0 | 97.6 | 98.0 | at least 96.3 | held |
        | light taken from the Tarantula field, densest tenth and whole frame | 81, 82 % | 82, 84 % | 80, 81 % | at most 83, 84 % | held |
        | light added on each master's nebula, as trained | 0.0 / 0.0 / 0.0 % | 0.0 / 0.1 / 0.0 % | 0.0 / 0.2 / 0.1 % | at most 1.0 % | held |
        | the Lagoon's broad light taken, as trained | 2.67 | 1.51 | 1.07 | at most 4.0 | held |
        | brightest tenth of the Tarantula's peaks standing, as trained | 17 of 77 | 17 of 77 | 18 of 77 | at least 11 | held |

        The kill is by 0.3 of a point and stands as registered. The bar of 93.3 is missed by 2.3, and the plate
        that holds what the target asks for reads 97.8 on this row (`grain_rule_post.py`, above).
      - **Reported.** Purity 0.7 (R4d 0.8); the blotch line on the Tarantula's densest tenth 0.29 through the
        chain (R4d 0.29) and 0.38 as trained. As trained: Andromeda's dark flat 71.2 with 24.4 % dug (R4d 69.4,
        19.5); the Tarantula's 84.0 with 12.5 % left (80.3, 14.3; seed 0 had 70.0 and 28.1); its crowded flat
        86.2 (86.7); BP 17-19 95.2 (95.4), and through the chain 95.7 (96.0); **the M31 nucleus taken in its
        worst channel 38.9 %** (R4d 9.7, seed 0 27.2), which is what the sheet shows and which the protection
        undoes; broad light taken on Andromeda and the Tarantula 2.99 and 4.05 (2.88 and 5.86); the Tarantula's
        glow median -79 (-6.5). Light added on the Tarantula's nebula through the chain 1.05 % (R4d 0.65, seed 0
        0.72; the guard is the row as trained). 30 Doradus's core, cell 4,6: 75.7 / 97.0 / 84.2, which is 96,
        96 and 80 percent of the plate's 78.9 / 101.0 / 104.7 (R4d 44.4 / 49.4 / 39.7, seed 0 52.4 / 54.5 / 39.8),
        on the row two seeds of one recipe have stood 39 to 75 % apart on. Faint stars left on bright background as trained: 20 % of 142 on
        the Lagoon's core (R4d 29, seed 0 11) and 28 % of 321 on 30 Doradus (22, 25). Where the BP 17-19 stars are
        left, with the level set aside: seed 1 takes fewer than R4d in the most crowded fifth (81.0 against 83.6,
        StarXTerminator 97.1) and as many in the least crowded (94.2 against 94.0). The twin error: 1.45 sigma in
        the mask as trained (seed 0 2.49) and 1.09 with floor5. The bright stars of #2292, 26 rows: inside 22 px
        seed 1 leaves over 1.2 times R4d's on 15 and under R4d's by that factor on 3, 1.47 times at the median,
        and on none more than the master holds; through the chain the rows are the model's own but Alcyone of
        2022-01-05, which the chain hands back whole (4,033 against the model's 3,042): the chain scored here is
        the build frozen on 2026-11-20, before the clipped-star rule.
      - **The best step's weights (step 195,500, `--save-best`), reported and deciding nothing.** Scored on the
        same rows under the name `r4f_synth31_s1best`, their two sheets looked at first (as the last step's,
        M31's nucleus yellow as trained included). They read as the last step's do, 12,000 steps on: the
        Tarantula's crowded flat 91.2 through the chain (the last step 91.0) and 86.9 as trained (86.2); BP 17-19
        with the level set aside 90.1 (89.9); the dense cells 4.5 / 4.0 / 3.5 and 4.7 / 4.3 / 3.7 (4.5 / 4.0 /
        3.5 and 4.8 / 4.4 / 3.8); Andromeda's dark flat 98.4 with 0.9 % dug and the Tarantula's 96.1; the
        Sagittarius crowded flat 97.9; the glow's medians -157 and +120; the light taken from the Tarantula
        field 80 and 81 %; by count clean 99.1 and 100.0; light added on the nebula as trained 0.0 / 0.3 /
        0.0 %; the Lagoon's broad light 0.98; 18 of 77 peaks standing; the M31 nucleus taken as trained 42.4 %
        in its worst channel (38.9) and 0.0 with protection; the twin error 1.44 and 1.08. So the last step
        was not a worse model than the best one, and the weights the trainer keeps by default lose nothing on
        this run.
      - **Against the prediction.** It trained as the others, as guessed. The dense cells' grain as trained did
        not fall by a quarter: it is 2 to 13 percent ABOVE R4d's in five of the six figures. The crowded flat
        did not rise over 91.3. Two of three missed.
      - **What it says, and one measurement made after the read to see why** (`train_fit_band.py`, not
        registered). By its registration the arm is killed on both seeds and nothing is adopted. Seed 0 could
        be put down to its training; this one cannot. The first guess for a model that keeps the grain of a
        target that has none is that it cannot fit it, and that is not so: on the cache's own training cells of
        the two Tarantula sessions, in the cache's stretched domain and in units of the target's noise, the
        model's output on an input draw holds the target's grain and no more.

        | training cells of | target, R / G / B | input draw | the model's output | output over target per cell, median (10th to 90th percentile) |
        |---|---|---|---|---|
        | the ASI585MC Pro session of 2024-10-02 (40 cells, the one the dense cells are on) | 2.7 / 2.4 / 2.1 | 19.6 / 21.2 / 17.5 | 2.6 / 2.3 / 2.0 | 0.95 / 0.95 / 0.98 (0.88 to 1.02) |
        | the ASI533MC Pro session of 2022-12-01 (40 cells) | 2.0 / 2.3 / 2.2 | 12.4 / 14.7 / 10.2 | 1.9 / 2.2 / 1.9 | 0.97 / 0.94 / 0.94 (0.86 to 1.00) |

        The model (`r4f_synth31_s1`) is fed the stored per-pixel plane it trained on, as `--cond-map` models must be;
        it keeps 94 to 98 percent of the target's band, a little less, not more.
        So the model gives its target back on the inputs it trained on, crowded sky included. What the pairs
        ask of it is the other half: an input is its target plus light and nothing else (over the 40 cells no
        pixel of an input stands half a sigma under its target, and 37 / 38 / 35 % by channel, the mean over the cells,
        stand half a sigma over it; `train_fit_band.py` prints both shares), and that
        light is the drawn stars, which the export draws down to 3 sigma. The grain was taken out of the plate
        the target AND the input are made from, so no pair shows grain in the input that the target lacks, and
        nothing asks the model to take it out of a real frame. On the real master of that session (row (2), a
        linear frame and another unit than the table's) the dense cells read 4.5 / 4.0 / 3.5 and 4.8 / 4.4 / 3.8
        where the stored plate reads 4.8 / 5.0 / 5.2 and 6.9 / 7.2 / 6.2: the model's plate keeps 61 to 94 percent
        of the band the plate builder left there. That this light is the stars under the drawn floor is the
        plan's reading of 2026-11-18 and is not measured here. The arm changed what the target looks like in
        crowded sky and not what the model is asked to remove, which is why its two seeds and R4d read alike.
      - **What follows from it, none of it registered and all of it the owner's.** The grain has to be in the
        input and not in the target. Two ways are at hand: draw the stars under the floor, from each session's
        own luminosity function, far enough down that the drawn light carries the band the real cells hold
        (the export's floor is 3 sigma now and may not go under 2); or add the plate's own grain, the stored
        plate less the rule's, to the input alone. The first draws only stars, so it cannot teach the model to
        flatten a wisp; the second is the real residue and carries the weak nebula detail the rule smooths with
        it (what the rule as a post step did at 30 Doradus's edge, above). **The owner's guard of 2026-10-20
        stands over both:** unresolved glow belongs in the starless image, which is why the export refuses a
        floor under 2 sigma and caps a tile's faint light (`LuminosityFunction`). What StarXTerminator drops is
        the grain and what it keeps is the glow as a smooth surface at about 40 px and up (the note of
        2026-11-18), so the smooth part of whatever is added to the input has to be in the target too. The
        second way does that by construction (the rule keeps the 31 px glow). The first does it only if the
        drawn stars' light, smoothed to that scale, is added to the target, which the export does not do today
        and which lifts the floor the guard set.
      - **How deep the first way would have to draw, simulated the same night: no depth is enough**
        (`faint_depth_sim.py`). The session's catalogue holds 67,576 stars down to the builder's 5 sigma and
        fits the export's law with a slope of 1.213 over 10 to 300 sigma; scaled to a dense cell's own count
        there, the law expects 719 stars from 5 to 10 sigma in cell 4,4, where the catalogue has 761. A cell of
        unit pixel noise with the law's stars drawn from a floor up to 5 sigma (the stars a stored plate still
        holds), each a Gaussian of the catalogue's median width, read with `band.py`'s statistic:

        | stars drawn from | cell 4,4: stars a cell, band | cell 5,4: stars a cell, band |
        |---|---|---|
        | none (noise alone) | 0, 0.85 | 0, 0.85 |
        | 3 sigma (the export's floor now) | 1,109, 1.76 | 837, 1.62 |
        | 2 sigma (the lowest the guard allows) | 2,594, 2.27 | 2,035, 2.07 |
        | 1 sigma | 7,641, 2.77 | 5,951, 2.44 |
        | 0.25 sigma | 46,508, 3.09 | 36,276, 2.82 |
        | 0.1 sigma | 144,028, 3.20 | 112,292, 2.92 |
        | the stored plate, measured (R / G / B) | 4.8 / 5.0 / 5.2 | 6.9 / 7.2 / 6.2 |

        The band levels off: under about 1 sigma a deeper floor adds stars by the ten thousand and almost no
        grain, since a fainter star adds less to the variance than the brighter ones already there. Drawn all
        the way to 0.1 sigma the law's stars carry 3.2 and 2.9 where the plate holds 4.8 to 5.2 and 6.2 to
        7.2, which is 38 to 44 percent of the plate's variance in the first cell and 16 to 22 in the second.
        So the grain a crowded plate holds is not, for the most part, the law's stars under the detection
        limit, and the reading of 2026-11-18 holds for a part of it only. What the rest is was not measured
        (stars that cluster where the simulation scatters them evenly, what the builder left of the 1,300
        stars it subtracted in the cell, weak nebula). It is a simulation of the law and not of the sky: one
        width for every star, even scatter, the law held to the floor. For the two ways above it says that
        drawing deeper cannot put the real grain into the input by itself, and that the plate's own grain
        carries all of it, whatever it is made of.
      - **What StarXTerminator's own plate reads on those cells, made the same night, and what it says of
        row (2).** `band.py` on the judge's plate of the same session's master, with one less crowded cell
        (1,1, 604 catalogued stars) beside the two dense ones:

        | cell | the stored plate | StarXTerminator | R4d as trained | R4f seed 1 as trained |
        |---|---|---|---|---|
        | 4,4 | 4.8 / 5.0 / 5.2 | 5.3 / 5.3 / 3.8 | 4.3 / 3.8 / 3.1 | 4.5 / 4.0 / 3.5 |
        | 5,4 | 6.9 / 7.2 / 6.2 | 2.9 / 2.1 / 1.8 | 4.7 / 4.6 / 3.6 | 4.8 / 4.4 / 3.8 |
        | 1,1 | 5.2 / 6.0 / 4.6 | 0.8 / 1.1 / 1.3 | 3.9 / 4.4 / 2.9 | 3.8 / 4.2 / 2.9 |
        | 4,6 (30 Doradus's core) | 78.9 / 101.0 / 104.7 | 82.5 / 103.9 / 106.1 | 44.4 / 49.4 / 39.7 | 75.7 / 97.0 / 84.2 |

        In cell 1,1 the judge's plate is at the level of noise alone (0.85) and in cell 5,4 at 0.4 to 0.6 of
        ours. In cell 4,4 it holds MORE than ours and as much as the stored plate: what that cell holds
        between 9 and 41 px is something the judge keeps, so it is not grain, and row (2)'s bar there (0.6 of
        R4d's, 2.6 / 2.3 / 1.9) lay under the judge's own figure. **Row (2) was misread in cell 4,4 from the
        day it was written;** in cell 5,4 its bar was the judge's level (0.62 / 0.46 / 0.50 of R4d's). The row
        should be read on cells where the judge's plate is flat, and cell 4,4 is not one.
      - **Where the light sits** (`left_around_stars.py`: ours less the judge's on that master, channel mean,
        stacked about the catalogued stars by significance, in the master's pixel sigma, less the same stack
        about random places). About the stars of 10 sigma and over ours stands no more than 0.09 sigma over
        the judge's in any ring, and under it by up to 0.9 sigma at the core of the 40 to 100 sigma stars. About the 13,484 stars of 7 to 10 sigma ours holds 0.1 to 0.2 sigma in the inner 3 px, 2 to 7
        percent of the star. About the 29,849 stars of 5 to 7 sigma it holds 0.2 to 0.3 sigma in the inner
        3 px (the star itself stands 2.9 sigma at its core) and then a level of 0.11 to 0.12 sigma a pixel
        from 5 px out to 24 px over what it holds at random places; R4d's holds 0.13 to 0.15 there and the
        stored plate 0.13 to 0.16. So what ours keeps and the judge takes is not the stars the catalogue has.
        It is a faint light tens of pixels wide that stands where the faint stars are thick, which fits
        stars that cluster and is still not shown to be stars. Over a whole cell ours stands 1.13 sigma a
        pixel over the judge's on average in cell 1,1 and 1.08 in cell 5,4 (the level the plan read at 1.22 /
        0.89 / 0.56 on the held-out Tarantula on 2026-11-20), and the 9 px mean of that difference has an rms
        of 0.40 and 0.53 sigma, which is the mottle the sheet shows (the master, the judge's plate flat as
        noise, ours, the stored plate and the difference, cells 1,1 and 5,4 at 1:1; sent to the owner).
      - **The rule as a post step, tried a second time the same night: the rows are reached and the picture
        is not** (`crowd_smooth_v2.py`, a prototype on R4d's chain plates; no product change). The first try
        of 2026-11-20 read the crowded rows above the judge's and was no product step for what it looked like.
        This one keeps the plate's own noise where the rule smooths, holds the bright nebula out, and sets
        aside the surroundings of the bright stars the remover took (a peak of the stars layer over 200 sigma,
        a 19 px box: 2,248 stars and 8.4 % of the Tarantula's frame) before the detail is measured, so that a
        star's residue is not read as structure and kept as an island.

        | row | R4d through the chain | the rule, first try, nebula held out | the second try | StarXTerminator |
        |---|---|---|---|---|
        | Tarantula crowded flat | 91.3 | 97.8 | 97.3 | 96.1 |
        | Gaia BP 17-19, the scorer's row | 96.0 | 98.3 | 97.9 | 98.3 |
        | Sagittarius crowded flat | 97.0 | 99.7 | 99.4 | 99.6 |
        | Andromeda dark flat, dug | 98.5, 0.7 | 100.0, 0.0 | 100.0, 0.0 | 98.6, 1.4 |
        | Tarantula dark flat, left | 96.5, 2.7 | 98.3, 1.5 | 98.5, 1.4 | 96.6, 2.7 |
        | purity | 0.8 | 2.6 | 1.1 | 3.1 |
        | light added on each master's nebula, % | 0.1 / 0.7 / 0.3 | 0.1 / 0.7 / 0.3 | 0.1 / 0.7 / 0.3 | |

        By the rows it stands at the judge's on every crowded row with the guards held and purity back near
        R4d's. Looked at on a stretch that shows the faint field (the judge's median less 4 to plus 30 pixel
        sigma, the Tarantula's crowded field and 30 Doradus's edge at 1:1) it is not there. Where the judge's
        plate is dark and flat, the rule's is a smooth reddish haze: the glow, which the judge takes with the
        grain and the owner's guard keeps. The plate's own fine noise looks coarser beside the first try's
        drawn noise, and no better with what stands over 2.5 sigma redrawn. The faint wisps at 30 Doradus's
        edge are washed out as before, and a few islands still stand there. So the rows can be bought with a
        rule and the look cannot yet: the scorecard's crowded rows do not read what the eye objects to here,
        which is its blind side again. The sheets went to the owner.
      - **The rule is not what the judge does, cell by cell** (`rule_against_judge_cells.py`: the same session's
        master, the channels' mean, `band.py`'s statistic in the stored plate's own sigma, so the figures are
        larger than the per-channel ones above; the rule's plate is the one R4f's targets were drawn from).

        | cell | the stored plate | the rule's plate | R4f seed 1 as trained | StarXTerminator |
        |---|---|---|---|---|
        | 1,1 | 7.5 | 5.6 | 5.2 | 1.2 |
        | 5,4 | 9.1 | 7.3 | 6.0 | 3.2 |
        | 4,4 | 6.4 | 3.1 | 5.8 | 6.9 |
        | 4,6 (30 Doradus's core) | 126.0 | 125.9 | 115.2 | 129.9 |

        Where the judge flattens the field (cell 1,1) the rule leaves most of the band, 5.6 against 1.2, and
        where the judge keeps it (cell 4,4) the rule halves it, 3.1 against 6.9. So R4f's targets were not what
        the judge writes either: not free of the grain where it mattered, and smoothed where they should not
        have been. The layer the rule takes out of the plate (stored less rule) has an rms of its 9 px mean of
        0.47, 0.61 and 0.65 sigma in cells 1,1, 5,4 and 4,4, and it follows what ours keeps over the judge's with
        a correlation of 0.55, 0.24 and -0.16. That weighs on the second way above: the plate's own grain, as
        this rule draws it, is only partly what the judge takes and in cell 4,4 is structure it keeps.
      - **Clustering does not make up what the luminosity function lacks, short of stars sitting on the
        detected ones** (`faint_depth_sim.py --follow 20,8`: the same simulated stars on the same noise, placed
        a second time where the catalogue's own stars are dense, their count map smoothed by a Gaussian of
        20 px and of 8 px; the band with stars from 3, 1, 0.25 and 0.1 sigma):

        | cell, and the plate's band R / G / B | even scatter | following the catalogue's density at 20 px | at 8 px |
        |---|---|---|---|
        | 1,1 (5.2 / 6.0 / 4.6) | 1.21, 1.73, 1.97, 2.04 | 1.19, 1.78, 1.99, 2.07 | 1.20, 1.90, 2.71, 3.49 |
        | 5,4 (6.9 / 7.2 / 6.2) | 1.62, 2.44, 2.82, 2.92 | 1.63, 2.55, 2.86, 3.03 | 1.65, 2.86, 4.17, 5.29 |
        | 4,4 (4.8 / 5.0 / 5.2) | 1.76, 2.77, 3.09, 3.20 | 1.83, 2.95, 3.31, 3.34 | 1.88, 3.22, 4.71, 6.17 |

        Placed on the catalogue's density at 20 px the band rises by 0.2 at most over an even scatter. Placed
        at 8 px, which is stars sitting on the detected ones, it reaches the plate's in cell 4,4 from a floor
        of 0.25 sigma down and passes it at 0.1, and stays under it in cell 1,1 (3.49 against 4.6 to 6.0) and
        cell 5,4 (5.29 against 6.2 to 7.2) even at 0.1 sigma, where the law asks for 48 to 144 thousand stars
        in a cell that holds 600 to 1,300 catalogued ones. Light that tight about a detected star is nearer
        to what the builder left of each star it subtracted than to faint stars of their own. What the plate's
        grain is made of stays unmeasured.
      - **A row that reads both sides, tried on the scoring masters** (`judge_band_rows.py`). Row (2) read two
        cells picked by eye. Here the judge's own plate sorts every 300 px cell of a master: flat where its
        band is under 1.5 (noise alone reads 0.85), structure where it is 3 or over. Per plate, on the
        channels' mean: the median band on the flat cells with the share of them it leaves under 1.5, and on
        the structure cells its band over the judge's with the share within 0.8 to 1.25.

        | master and plate | flat cells: band, share under 1.5 | structure cells: over the judge's, share within 0.8 to 1.25 |
        |---|---|---|
        | **Tarantula** (49 cells: 2 flat, 31 structure) | | |
        | R4d as trained | 3.60, 0 % | 1.18, 42 % |
        | R4d through the chain | 3.29, 0 % | 1.07, 48 % |
        | R4f seed 1 through the chain | 3.14, 0 % | 1.09, 48 % |
        | the rule, first try, nebula held out | 1.36, 100 % | 0.81, 52 % |
        | the rule, second try | 1.44, 100 % | 0.89, 55 % |
        | **Sagittarius** (64 cells: 48 flat, 7 structure; the judge's own flat cells read 1.14) | | |
        | R4d as trained | 1.59, 44 % | 0.99, 86 % |
        | R4d through the chain | 1.53, 48 % | 0.99, 86 % |
        | R4f seed 1 through the chain | 1.48, 54 % | 0.98, 100 % |
        | the rule, first try, nebula held out | 1.17, 96 % | 0.92, 86 % |
        | the rule, second try | 1.17, 96 % | 0.94, 86 % |
        | **Andromeda** (77 cells: none flat, 9 structure) | | |
        | R4d through the chain | no flat cell | 1.00, 89 % |
        | R4f seed 1 through the chain | no flat cell | 0.99, 89 % |
        | the rule, first try and second | no flat cell | 0.95, 89 % |

        The models leave the mottle on the flat cells and keep the structure. The rule takes the mottle and
        with it a tenth to a fifth of the structure's band on the Tarantula: that column is the number for
        the wisps the eye saw washed out, which no row of the scorecard read. Only 2 of the Tarantula's 49
        cells are flat by the judge, so its "crowded flat" row, which reads the sites of stars, is not this
        row. Whether a pair of this kind joins the scorecard, and at what bars, is the owner's; no arm or step
        is judged on it here.
- **R4g, pre-registered 2026-11-20 before any export or score of it. One change from R4d: bright nebula cells join
  the draw, drawn on the stored plate, so their target keeps the nebula's own edges and knots.** The owner,
  2026-11-20, chose it with R4f ("That, plus the 1070 arm"): the arm put to him was training tiles on bright
  structured nebula with its knots kept in the target, against the nebula-taking, on the GTX 1070. It runs
  beside R4f and shares its parent, so the two reads can be set side by side.
  - **Why.** Since R3z's halo change the models take nebula with the stars: on the Lagoon the broad light taken
    reads 1.49 to 3.50 for six runs against 0.55 to 1.11 before it and StarXTerminator's 0.42 ("The scorecard's
    blind side" below). Two reads of the training pairs on 2026-11-19 (`audit.py`, `asked_broad.py`) say what the
    data shows the model there. Background at a stretched level of 0.4 and over (the sky sits at 0.25) is 0.5 % of
    synth28's tile area, against 55 % of a 300 px box on the Lagoon's core and 73 % on 30 Doradus's. And 86 % of
    synth25's draws hold a saturated star with its broad halo (3.12 a draw) against 25 % of synth22's (0.25), while
    bright structure in the target is 2.3 tiles' worth of 414. So the pairs ask for broad light to be taken round
    a bright core 12 times as often as before, almost never over a bright background that must stay.
  - **Why not through the synthetic line.** A sample export of the bake's bright cells with R4c's line (122 cells,
    8 sessions, 2026-11-19) was looked at on 2026-11-20 before this registration: 17 of the 122 targets hold black
    pits on over 1 % of their bright pixels, and the real master tiles of the same cells hold none. The synthetic
    background redraws a plate's fine scales as random texture of the plate's own strength there, and at a sharp
    edge (the dark bay beside the Trapezium, eta Carinae's lanes) that strength is 0.7 to 2.3 of the level, so the
    downward swings empty the dark side to zero (#2288; `bright_pits.py`, `bright_pit_cause.py`). The pits do not
    sit where the plate builder painted or subtracted. The stored plate of the same cells is a good starless
    picture of them (`bright_plate_look.py`, 40 cells of 28 sessions at 1:1 through the master's own stretch).
  - **The cells** (`bright_plate_cells.py`). The bake's bright cells by the one rule (`tianwen dataset
    bright-cells`: at least 10 % of a cell at a level of 0.45 to 0.95, outside each session's sample of 40), of the
    sessions in the trainer's own lists (`arms/r2-train.txt`, `arms/r2-val.txt`): 234 cells in 23 session ids. A
    cell takes the plate as its target when the builder inpainted at most 10 % of it, subtracted at most 20 % and
    left no source of 100 sigma or over unsubtracted. That leaves out what the look showed is no truth: a
    cluster's core (Omega Centauri, 27 cells, 48 to 75 % subtracted, the plate a noisy mush), a bright star's
    halo (HIP 80609), the Lagoon session of 2025-05-25 where a subtracted halo left a ring in the nebula (seen on
    two of its failing cells; 24 of its 30 fail) and eta Carinae at 135 mm. What the builder does at a saturated
    star was measured on two of them after the owner asked whether the halo is over-subtracted
    (`plate_halo_channels.py`, ring medians less the sky at 300 to 400 px, in pixel sigma, R / G / B): it cuts the
    star and its halo flat out to about 120 px and leaves the master as it is beyond. On plain sky (HIP 80609)
    that flat level is the halo's own at 120 px, a pedestal of 20 / 15.5 / 6 sigma above the sky, never under
    it, with the spikes' residues 4 to 5 sigma under the pedestal. Over nebula (the Lagoon cell) the flat level
    sits under its own rim by 3.4 to 5.8 sigma in R, 1.6 to 5.1 in B and, in G, from 0.3 above to 3.0 under (the plate's
    own ring at 100 to 120 px; figures below), with a patch 20 to 30 sigma lower: a dent in the nebula. **Where the two
    were read** (written 2026-11-21 for #2293; the registration named neither): `plate_halo_channels.py` takes a
    session master, its plate and an x, y, both files from the store (`session-masters/<stem>.fits` and
    `starless/plates/<stem>_plate.fits`). HIP 80609 is the star itself in the `flip=b` half of its session, stem
    `ZWO-ASI533MC-Pro_Optolong-L-Quad-Enhance_HIP-80609_2026-04-21_ZWO ASI533MC Pro_HIP 80609_Optolong L-Quad Enhance_flip=b`,
    at x 1362, y 2244: the plate's ring medians read 19.1 to 20.9 / 15.1 to 16.5 / 5.7 to 6.5 out to 100 px
    against the master's 20.9 / 16.4 / 6.7 at 100 to 120 px. (The session's master with no flip is another frame;
    the two brightest stars the review tried on it are not this star.) The Lagoon is the session of 2025-05-25
    with no flip, stem
    `ZWO-ASI585MC-Pro_Unidentified-Broadband-BlueCut_Lagoon-Nebula_2025-05-25_ZWO ASI585MC Pro_Lagoon Nebula_Unidentified-Broadband-BlueCut`,
    at x 1920, y 1312: the plate reads 35.0 to 37.4 / 24.5 to 27.8 / 25.2 to 28.6 between 8 and 100 px against
    40.8 / 27.5 / 30.3 at 100 to 120 px (the plate's own ring there; the master reads 40.6 / 27.8 / 30.9), and its 10th percentile falls to 10.1 / 4.9 / 2.0 at 22 to 40 px. Both
    were run again on 2026-11-21 and read the same. **The star's own cell is not one of the 126 that pass the
    gate, but its flattened disc reaches into six that do (three positions in each of two session ids):** the
    session's cells at 1603, 1214, at 1603, 1342 and at 1731, 1342 lie 62, 69 and 30 px from the star, so the disc
    of about 120 px reaches 58, 51 and 90 px into them. The same three positions pass under the session's `flip=a`
    id as well (`bright-plate-cells.tsv`); that half is co-registered, the same star at 1920, 1312 with the same
    dent (plate 10th percentile 12.3 / 7.2 / 3.7 at 22 to 40 px, flat level 39.3 to 42.8 / 27.3 to 31.0 / 26.0 to 29.5
    between 8 and 100 px, as for the no-flip copy, against 45.5 / 30.9 / 30.4 at 100 to 120 px, the plate's own ring
    there; the master reads 45.2 / 31.0 / 30.8), so the `flip=a` copy is the same sky, its targets drawn
    separately. `flip=b` has no passing cell. The dent is therefore in R4g's own targets, on part of six cells of
    126, and this registration did not say so. Only this star's reach was traced: the count is a floor, not the
    count of dented cells, since other saturated stars over nebula in the passing cells (eta Carinae, Orion) were
    not looked at, so "at least six" is what the table supports. One session is
    left out by name
    (`--exclude-session`), the Lagoon and Trifid of 2023-06-16 with its 8 cells: the Sagittarius master the
    question is read on is an OIII stack of 72 minutes from a frame of that camera's size, and whether its sky is
    that session's or the L-Ultimate session's of 2024-07-03 (which no list trains) was not established, so
    neither gives a plate cell. **126 cells pass: 120 training cells in 9 session ids and 6 held out** (M42 of
    2021-11-28, a validation session). By object 85 of the training cells are eta Carinae (two nights through one
    3 nm filter), 24 the Lagoon and Trifid, 11 Orion. Their median bright share is 38 %, the builder inpainted
    4.7 % and subtracted 9.7 % of the median cell, and it left 27 sources a cell unsubtracted, nine in ten of them
    judged a knot (0.7 a cell at 20 sigma and over). The cells lie on a 128 px grid, so neighbours share half
    their sky. No cell of the Tarantula or of Andromeda is in it.
  - **The export** (`export-bright-plates.ps1`): R4d's star line on the plate, `--background plate --stretch-from
    master --star-beta-draw 0.3,1.3,6,3 --saturated-own-share --saturated-measured --halo-over-plate
    --extra-cells <the list> --listed-only`, 8 draws a cell (the most a cell of the trainer's cache holds), from
    `bh-bin11` (main with the knot fix of #2276) with `--noise-as-recorded`, as R4d's binary drew.
    `--faint-stars` is not in it: the exporter takes it on a
    synthetic background only, and a plate holds its own faint stars. Then `tianwen dataset plate-masks` writes
    D4's mask beside every draw (the sky within two PSF widths of a source the plate kept is left out of the
    loss), and the cache `r2-bright1` is prepared with the trainer's lists and `--prepare-keep`.
  - **Checked before the export, on one session** (V1045 Ori, the Trapezium field, 7 cells, 2 draws,
    `r4g-preflight.ps1`): no target holds a pit (4 of the same 7 do through the synthetic line); the input's pixel
    noise on bright background is 1.00 of the real master's at the same cell (`bright_cell_noise.py`; through the
    synthetic line it was 0.99 at the median over 122 cells, 0.76 to 1.51 at the 10th and 90th percentile); the
    masks leave out 7.1 % of the pixels inside the rim, 114 sources a draw, as small discs on knots and texture
    peaks, with the edges and the broad light kept in the loss (`keep_look.py`).
  - **The training.** R4d's recipe and cache exactly (`r2-synth29`, R3v's flags, seed 0, the GTX 1070), with
    `--extra-cache r2-bright1 --extra-loss-mask --schedule-on main`. The bright cells are 960 of 19,040 input
    tiles, 5.0 % of the draw (the cluster scenes had 12.5 %, R2q): a tile of either cache is drawn as often as any
    other, and a bright target, with 8 draws to a main target's 4, twice as often as a main one. That takes
    bright background from 0.5 % of the area the model is shown to between 2 and 3 %. `--extra-loss-mask` is new with
    this registration's PR: an extra row reads its own draw's mask and a main row weighs every pixel, and a batch
    with no extra row takes the plain mean it always took. The plateau halves and stops on synth29's held-out
    part alone, as R4d's did. Model `r4g_synth29b1_s0`.
  - **The question, as trained:** the Lagoon's broad light taken at most 1.3 (R4d 2.67, R3v 0.56,
    StarXTerminator 0.42). Seeds of one recipe have stood 1.3 apart on that row, so the bar is a fall of more than
    that. The row is read on the Sagittarius master, a held-out field, but the Lagoon itself is in the bright
    cells through two training sessions of other rigs: the master is held out and the object is not. The other
    two masters cannot carry the question: on the Tarantula and Andromeda StarXTerminator takes as much broad
    light as R4d does (5.99 and 2.88 against 5.86 and 2.88).
  - **Guards, through the product's chain** (core protection without the stars, then floor5), R4f's: by count
    clean at least 98.5 at 100-1000 sigma and 98 at 1000 and over; M42 100-1000 at least 88; SMC 20-100 and
    100-1000 at least 97; Andromeda dark flat at least 95 and dug at most 3; Tarantula dark flat at least 91; the
    glow's green median within 450 of zero on Andromeda and within 500 on the Tarantula; the M31 nucleus under
    10 %; purity at most 1.0 % (**this bar stands for R4g**: it trains on the stored plate cells without the grain rule, as R4d does, so purity is not the question it is for R4f; the heading "R4f's" above is shared text only); the Sagittarius crowded flat at least 96.3; the Tarantula's crowded flat at least
    90.3 (R4d 91.3, a point under it); the light taken from the Tarantula field at most 83 % and 84 %.
  - **Nebula guards, as trained:** light added on at most 1.0 % of each master's nebula; of the brightest tenth
    of the Tarantula's peaks at least 11 of 77 standing (R4d 17). And one for what a plate target could teach, a
    star kept because the plate kept one like it: of the faint stars on bright background (`audit.py real`:
    contrast 3 to 10 in a 300 px box, a star being a peak of the input less StarXTerminator's image) at most 37 %
    left on the Lagoon's core and at most 27 % on 30 Doradus (R4d 29 % of 142 and 22 % of 321; each bar is R4d's
    figure plus twice its binomial error).
  - **Kill:** a guard fails, or the Lagoon's broad light taken is over R4d's 2.67. Between 1.3 and 2.67 with
    every guard held it neither passes nor is killed; the rows go to the owner.
  - **Reported, no bar:** every row as trained beside R4d's (Andromeda dark flat 69.4 with 19.5 dug, Tarantula
    dark flat 80.3, Tarantula crowded 86.7, BP 17-19 95.4); the faint stars left on bright background by level;
    M42's 100-1000 as trained; the extra cache's held-out objective beside its training objective at the best
    step, since 120 targets shown twice as often as a main one can be learnt by heart; the twin error. Added
    2026-11-20 while it trained, before any score: the Gaia rows with the level set aside (`gaia_compact.py`, R4d
    through the chain 90.7 at BP 17-19) and the bright stars of #2292 (first HIP 80609 alone, on the trainer's
    validation session's master; widened the same evening, still before any score, to the fourteen stars of "A
    star that stands far out is left standing" below, pinned in `training/star-removal/bright_star_list.tsv`,
    thirteen of them on masters no gradient step saw; and once more that night, still before any score, to the
    twenty-four the census without a cap found, twenty-two of them on such masters).
  - **Looked at before the rows are quoted:** the Lagoon's core with the picture of what was taken
    (`r4_taken.py`), 30 Doradus's core, the Tarantula's knots, M42's core on a validation tile, M31's nucleus and
    a bright star on dark sky, as trained and through the product's chain, beside R4d and StarXTerminator.
  - **Prediction:** the Lagoon's broad light taken falls to between 1.3 and 2.2, so the likely outcome is "the
    owner decides"; the faint stars left on bright background fall by a fifth on both cores; the star rows stay
    within a point of R4d's. What speaks against a clean pass: the dose is a twentieth of the draw, two thirds of
    the bright cells are one object through one filter, 126 cells is little sky, and the halo change that began
    the nebula-taking is still in every main row. (Added 2026-11-21, after the arm was read and killed, so it
    moves no bar: a fifth thing spoke against it and was not listed. At least six of the 126 cells hold part of the dent
    the builder leaves at a saturated star over nebula, "Where the two were read" above, which is the
    broad-light-taken failure this arm tests, in its own targets.)
  - **Nothing is adopted;** adoption is the owner's.
  - **R4g's read, 2026-11-20 (seed 0, `r4g_synth29b1_s0`): KILLED. It leaves 41 % and 50 % of the faint stars on
    bright background, over the bars of 37 and 27; the Lagoon's broad light falls to 1.37, short of 1.3 by 0.07.**
    The trainer stopped itself at step 178,500 of 480,000 (best at 166,500, where the main cache's held-out
    objective reads 8.73e-6 and the bright cells' 1.75e-5). As trained for the question and the nebula guards,
    through the product's chain for the rest (the tile rows with floor5):

    | row | R4d | R4g | bar | read |
    |---|---|---|---|---|
    | the Lagoon's broad light taken, as trained | 2.67 | **1.37** | at most 1.3, kill over 2.67 | missed by 0.07 |
    | faint stars left on bright background, the Lagoon's core | 29 % of 142 | **41 %** | at most 37 % | **failed** |
    | the same, 30 Doradus | 22 % of 321 | **50 %** | at most 27 % | **failed** |
    | light added on each master's nebula | 0.0 / 0.0 / 0.0 % | 0.05 / 0.12 / 0.11 % | at most 1.0 % | held |
    | brightest tenth of the Tarantula's peaks standing | 17 of 77 | 20 of 77 | at least 11 | held |
    | by count clean, 100-1000 and 1000 and over | 99.2, 100.0 | 99.2, 100.0 | at least 98.5, 98 | held |
    | M42 100-1000 by count clean | 93.4 | 93.4 | at least 88 | held |
    | SMC 20-100 and 100-1000 by count clean | 98.9, 100.0 | 98.9, 100.0 | at least 97 | held |
    | Andromeda dark flat, dug | 98.5, 0.7 | 98.1, 0.9 | at least 95, at most 3 | held |
    | Tarantula dark flat | 96.5 | 95.6 | at least 91 | held |
    | glow green median, Andromeda and Tarantula | -8.5, 106.5 | -125.3, 93.3 | within 450, 500 | held |
    | M31 nucleus | 0.0 % | 0.0 % | under 10 % | held |
    | purity | 0.8 | 0.8 | at most 1.0 | held |
    | Sagittarius crowded flat | 97.0 | 97.2 | at least 96.3 | held |
    | Tarantula crowded flat | 91.3 | 91.3 | at least 90.3 | held |
    | light taken from the Tarantula field, densest tenth and whole frame | 81, 82 % | 79, 82 % | at most 83, 84 % | held |

    On 30 Doradus R4g also leaves 16 % of the 330 stars of contrast 10 to 30 and 5 % of the 96 over 30, where R4d
    leaves 1 % and none.
  - **The renders, looked at before the rows** (`arm_look.py`, 300 px crops at 2x on one stretch a region; the
    Lagoon's taken light with `r4_taken.py`; M42's two brightest validation tiles). **30 Doradus's core:** R4g
    leaves a spray of stars left of the core, as trained and through the chain, where R4d leaves a few and
    StarXTerminator none. **The Lagoon's core:** the plates look alike in the broad light, with more faint specks
    on R4g's; in the picture of what was taken, R4d's two bright patches of nebula are down to one smaller patch
    beside the cluster, with thin dark streaks where a little light was added. **The Tarantula's knots:** both
    dim the knots' bright points, which StarXTerminator keeps. **M42's tiles, M31's nucleus and the bright star
    on Andromeda:** R4g as R4d.
  - **Against the prediction.** The Lagoon's broad light was predicted between 1.3 and 2.2 and reads 1.37: held.
    The faint stars left on bright background were predicted to fall by a fifth on both cores; they rose by two
    fifths on the Lagoon and more than doubled on 30 Doradus: failed, the other way. The star rows were predicted
    within a point of R4d's: through the chain they are (the largest move is the Tarantula's dark flat, 96.5 to
    95.6). As trained they are not: Andromeda's dark flat falls from 69.4 to 50.6 with 34.5 % dug and the
    Tarantula's from 80.3 to 74.4, which floor5 covers.
  - **Reported, no bar.** Broad light taken as trained on Andromeda / the Tarantula / the Lagoon: 2.83 / 4.17 /
    1.37 against R4d's 2.88 / 5.86 / 2.67, the Tarantula with no cell in the draw. Gaia BP 17-19 with the level
    set aside, through the chain: 91.4 (R4d 90.7). M42's 100-1000 as trained: 55.9 (R4d 63.2). The blotch line on
    the Tarantula's densest tenth: 0.30 (R4d 0.29). The twin error: 2.48 sigma in the mask as trained, 1.07 with
    floor5. The twenty-four bright stars of #2292: inside 22 px R4g leaves over 1.2 times R4d's on 21 and less on
    none, and on both Pleiades nights it leaves the bright Pleiads whole (Maia 3,057 of 3,134 sigma on
    2022-01-04, where R4d leaves 113). The uncertain star's two rows (south of Merope on both nights, in the
    list since #2311) were read after the fact on 2026-11-21: inside 22 px R4g leaves 1.17 and 1.15 times
    R4d's, and of StarXTerminator's take at 8-22 / 22-40 / 40-60 px it takes 94 / 68 / 12 and 92 / 54 / 8
    where R4d takes 97 / 76 / 18 and 94 / 55 / 2.
  - **What it says.** Plate targets teach the model to leave the nebula's broad light, on a master it holds no
    cell of. They teach it to keep stars with it: the plates kept sources (27 a cell at the median, nine in ten
    judged knots), the loss mask left out only the sky within two PSF widths of them, and the model learnt that
    compact light on bright background stays and that a very bright star stays. The direction is right and the
    target is not. A next arm on this line needs cells whose kept sources are not stars; none is registered, and
    that is the owner's. Both models were read at each master's own black point, which #2309 shows is set by one
    pixel a channel, so the bright-star rows of both stand at an arbitrary stretch; the kill does not rest on
    them.
- **The crowded rows read light between the stars (2026-11-20, no training).** The owner, looking at one bright
  star through StarXTerminator (#2292): "i feel that SXT real strength it that it can really model starlight, not
  just stars". Read on the scoring masters, that is what separates the crowded rows.
  - **Where the purity row's light lies** (`purity_parts.py`, the Tarantula, green). The row is the share of the
    stars layer's light farther than 4 px from every Gaia star to BP 19 (8 px from one brighter than BP 13). The
    plate holds 503,889 such stars, so only 22 % of the frame is "away from every one", and 21 of those 22 points
    lie 4 to 8 px from a catalogued star (8 to 16 px from a bright one). StarXTerminator's 3.09 % is 2.93 in that zone, 0.02 on compact peaks the
    catalogue lacks and 0.14 in the rest; R4d's 0.85 % is 0.83, 0.01 and 0.01. Per pixel StarXTerminator takes
    0.52 sigma in the zone and 0.41 in the rest, R4d 0.10 and 0.03, R4c 0.12 and 0.06, R3v 0.07 and 0.01. So the
    judge takes about 0.4 sigma a pixel everywhere between the catalogued stars, and our guard of "purity at most
    1.0 %" forbids exactly that: the judge reads 3.1.
  - **The diagnostic** (`judge_broad.py`, not a product step): R4d's plate through the product's chain less the
    median over 15 px of (that plate less StarXTerminator's). A compact difference, a star one of the two left,
    stays as ours; the smooth difference goes. On the Tarantula the part removed is 1.16 / 0.87 / 0.52 sigma a pixel
    at the median (R / G / B; 0.2 to 3.0 at the 10th and 90th percentile), on Sagittarius 0.13, on Andromeda 0.01 /
    0.02 / -0.01. The same with a 31 px median removes 1.16 / 0.86 / 0.52. The sigma is the noise of our whole valid
    plate (the MAD of horizontal neighbour differences where both pixels are valid, a strided sample of them). As
    first written the script divided by the noise of the first 2,000,000 valid pixels in raster order, the top
    strip of the frame, and read the Tarantula as 0.94 / 0.78 / 0.48; the other two masters' figures did not move.
    This sigma is a single per-channel number of the plate; it is not the per-pixel `_c1.rms.fits` map that
    `purity_parts.py` divides by, which follows the local noise, so the two are not interchangeable. The scores in
    the table below do not use it, and the plate the corrected script writes is byte for byte the one scored.
    Scored as any plate:

    | row | R4d through the chain | with the judge's light at 15 px and over | at 31 px and over | StarXTerminator |
    |---|---|---|---|---|
    | Tarantula crowded flat | 91.3 | 95.9 | 93.6 | 96.1 |
    | Gaia BP 17-19 removed | 96.0 | 98.7 | 98.8 | 98.3 |
    | purity | 0.8 | 3.1 | 3.2 | 3.1 |
    | Sagittarius crowded flat | 97.0 | 99.0 | 98.0 | 99.6 |
    | Tarantula dark flat / left | 96.5 / 2.7 | 97.0 / 2.5 | 96.9 / 2.4 | 96.6 / 2.7 |
    | Andromeda dark flat / dug | 98.5 / 0.7 | 99.6 / 0.4 | 99.1 / 0.6 | 98.6 / 1.4 |
    | Tarantula glow, green median / p90 | 106.5 / 443.5 | -52.9 / 58.5 | -53.2 / 102.1 | -30.8 / 203.4 |
    | Andromeda glow, green median / p90 | -8.5 / 342.2 | 245.7 / 531.7 | 207.3 / 447.6 | 242.8 / 574.8 |

    With the judge's light at 15 px and over and our own finer scales, the plate reads the judge's crowded rows
    within 0.2 to 0.6 of a point. Half of the Tarantula's crowded gap is a level beyond 31 px and half lies
    between 15 and 31 px (what that second half is made of is read below, with the level set aside; and "a level"
    is corrected further down: no part of the crowded flat's gap is the level). On Andromeda
    the difference runs the other way: there the judge leaves a glow round
    bright stars that ours takes, and the diagnostic plate inherits it.
  - **The same rows with the level set aside** (`gaia_compact.py`: the light taken in a star's 3 px disc less the
    median of the light taken in its own 16 to 24 px annulus, over the input's disc less its annulus). Removed over
    half, the Tarantula, as the row reads it and with the level aside: StarXTerminator 98.5 / 98.5 at BP under 13,
    96.5 / 96.4 at 13-15, 97.0 / 96.4 at 15-17, 98.4 / 96.6 at 17-19; R4d through the chain 98.4 / 98.4, 97.3 /
    97.3, 98.1 / 97.3 and 96.0 / 90.7 (the script's draw per remover and bin; with its first, single random stream
    the last pair read 96.1 / 90.5 and StarXTerminator's rows the same). So to BP 17 our model removes a star as
    fully as the judge or more, and at BP 17-19 it removes fewer, 90.7 against 96.6: a gap of six points that the
    row shows as 2.4 (98.4 less 96.0 in this script's draw; the scorer's own draw, in the table above, reads the
    same row as 98.3 against 96.0), because any level taken counts toward every star.
  - **The diagnostic plate at 15 px is not a level alone.** The same read on the two diagnostic plates, BP 17-19
    with the level set aside: 91.2 with the judge's light at 31 px and over, 93.0 at 15 px and over (R4d's own
    90.7, the judge's 96.6; the brighter bins do not move). So the 31 px plate changes a level and little else,
    and the 15 px plate also carries 2.3 of the six points of the faint stars' own removal: a 15 px median of
    (ours less the judge's) passes part of the compact difference under a faint star, or of several inside one
    box; which of the two was not separated. For the table above that splits the Tarantula's crowded gap of 4.8
    points: 2.3 is a level at 31 px and over, and the 2.3 between 15 and 31 px is partly the level and partly
    faint stars the judge removed and ours left. (Corrected the same evening, below: the 2.3 points at 31 px are
    not the level either. A plate with the level alone taken reads 91.4.)
  - **Where those six points are** (`gaia_where.py`, the Tarantula's 21,903 stars of BP 17-19 seen, the level set
    aside; R4d through the chain against the judge; as trained R4d reads 88.8, so the chain gives 1.9). One run of
    `gaia_where.py` gives all three figures: it is handed the Tarantula scoring master and three plates of it,
    StarXTerminator's as the judge, R4d's through the product's chain and R4d's as trained, and its first line
    reads 96.6, 90.7 and 88.8 removed. The script scores a seeded draw of 40,000 of the frame's 441,821 BP 17 to 19
    stars (`default_rng(1)`, the draw `gaia_compact.py` makes, so the totals agree); 21,903 of them are seen, and a
    run gives the same stars. The plates are the whole-master run's (`w-sxt/tarantula_starless.fits`,
    `w-r4d_synth29_s0-pcs+floor5/tarantula_starless.post.fits` and `w-r4d_synth29_s0/tarantula_starless.fits`
    under `C:/temp/tianwen-scratch/f2-whole-master`, the master `synthetic-clusters/tarantula.fits`), and the
    run's output is kept as `C:/temp/e2/explore/gaia-where.txt`. Run again on 2026-11-21 with the script as it is
    on main since #2313, on the same plates: every figure and every bin label below came out the same.

    | split | bin | share of the stars | judge | R4d | the bin's part of the 5.9 points |
    |---|---|---|---|---|---|
    | the star's excess over its seeing threshold | 1.0 to 1.5 | 21 % | 96.3 | 81.3 | 3.2 |
    | | 1.5 to 2 | 17 % | 97.2 | 87.7 | 1.6 |
    | | 2 to 3 | 22 % | 97.9 | 92.9 | 1.1 |
    | | 3 to 5 | 19 % | 97.2 | 95.3 | 0.4 |
    | | 5 to 10 | 11 % | 95.4 | 95.2 | 0.0 |
    | | 10 and over | 10 % | 93.6 | 97.6 | -0.4 |
    | catalogued stars within 24 px, in fifths | 6 to 51 | 20 % | 98.7 | 94.0 | 0.9 |
    | | 52 to 105 | 20 % | 95.4 | 92.8 | 0.6 |
    | | 106 to 185 | 20 % | 94.7 | 92.5 | 0.4 |
    | | 186 to 298 | 20 % | 97.1 | 90.8 | 1.3 |
    | | 299 to 725 | 20 % | 97.1 | 83.6 | 2.7 |
    | background over the frame's sky, local rms | under 2 | 62 % | 98.3 | 94.5 | 2.3 |
    | | 2 to 5 | 38 % | 94.7 | 84.9 | 3.7 |
    | | 5 to 10 | 0.6 % (121 stars) | 54.5 | 76.0 | -0.1 |
    | | 10 to 20 | 0.2 % (34 stars) | 35.3 | 76.5 | -0.1 |

    The background split has two more bins in the script, 20 to 50 and 50 and over, and a bin under 30 stars is
    not printed. Neither holds a star here: the four rows above count 13,476, 8,272, 121 and 34 stars, which is
    all 21,903.

    So 4.8 of the 5.9 points are stars under twice their seeing threshold, and 4.0 are in the two densest fifths,
    where the frame holds a catalogued star to every 2.5 to 10 square pixels (186 to 725 of them within 24 px, a
    disc of 1,810 square pixels: one every 1.6 to 3.1 px along a line). From five times the threshold up our model
    removes within 0.2 of a point of the judge's share or more (95.2 against 95.4 from 5 to 10, 97.6 against 93.6
    over 10), and on the little bright background this frame has it removes more.
    The Andromeda master carries no PIXORIG = 1, so the script refuses it and this is the Tarantula alone.
    **Looked at** (the script's `--sheet`: six stars of the densest fifth, under twice their threshold, that the
    judge removed and ours left, 48 px at 6x, on the input's scale and then each panel about its own median). The
    judge's panels are a dark, even floor with fine noise. Ours hold the field's light as soft lumps 5 to 15 px
    across (their height was not measured here), and in one of the six the core of a brighter star with a streak. The "star left"
    at the centre is one of those lumps. (As first written this said that in such a field removing the faint
    stars and taking the carpet's light are one act. They are two: the rule plate below removes the lumps at our
    own level.) What
    our plate shows there is neither of the two consistent pictures: not the judge's dark floor, and not a smooth
    glow at the carpet's level, which is what the rule of 2026-10-23 asks for and what R4f's targets hold.
  - **Looked at** (`judge_broad_look.py`: the 400 px box where the judge's broad take beyond ours is largest, at
    1:1 through the master's stretch). There our plate's median level stands 1.60 sigma over the judge's and the
    diagnostic plate's 0.00; that sigma is the crop's own, of the master's green channel, and not
    `judge_broad.py`'s. The 0.00 is so by construction (the plate is ours less the median of that difference) and
    only checks the arithmetic; the evidence here is the 1.60 and the look. The diagnostic plate is still mottled, where StarXTerminator's is smooth with its
    compact knots sharp. So the plate that scores like the judge does not look like the judge's. What the eye sees
    is the grain, the faint stars left standing (the six points at BP 17-19 are the catalogued part of them), and
    no bar reads it.
  - **How this sits with 2026-11-18's note** ("#2203 step 2" above: StarXTerminator keeps the glow as a smooth
    surface at about 40 px and up and drops the grain). Both hold. What it drops with the grain is the grain's own
    light, the faint stars', and the mean of that light is the level measured here; the surface it keeps is what
    is left. Ours leaves those stars, so it keeps their grain and their mean.
  - **Far wings are not it** (`bright_star_rings.py`, #2292): round the twelve brightest stars of each scoring
    master the judge takes 1 to 4 sigma at 8 to 22 px and nothing a ring median sees beyond 22 px, and our models
    since R4c match it there. No scoring master holds a star like the one on HIP 80609's master (Antares, by
    that master's WCS); the store's other masters do, and
    "A star that stands far out is left standing" below reads them.
  - **Corrected the same evening: the crowded flat reads the lumps, not the level** (two more plates, no
    training). The reading above took the 15 and 31 px plates' gain on the crowded flat for the judge's level. The
    control for that was missing. `judge_level.py` takes ONLY the level (ours less the judge's through a median of
    about 100 px, so nothing under about 50 px changes). `grain_rule_post.py` does the opposite: it keeps our
    level and smooths our plate with R4f's target rule (`plate_grain.py`: in crowded sky, where the detail between
    7 and 31 px is weak, the 31 px glow plus pixel noise; the crowding is read off the input's compact peaks,
    calibrated against the plate builder's own counts on five store sessions with `grain_rule_density_cal.py`).

    | row | R4d through the chain | the judge's level only | the judge's light at 31 px and over | at 15 px and over | our level kept, the grain rule | StarXTerminator |
    |---|---|---|---|---|---|---|
    | Tarantula crowded flat | 91.3 | 91.4 | 93.6 | 95.9 | 97.8 | 96.1 |
    | Sagittarius crowded flat | 97.0 | 97.1 | 98.0 | 99.0 | 99.7 | 99.6 |
    | Gaia BP 17-19, the scorer's row | 96.0 | 98.8 | 98.8 | 98.7 | 98.4 | 98.3 |
    | Gaia BP 17-19, the level set aside | 90.7 | 90.6 | 91.2 | 93.0 | 96.6 | 96.6 |
    | purity | 0.8 | 3.4 | 3.2 | 3.1 | 2.6 | 3.1 |
    | light taken from the Tarantula field, densest tenth / whole frame, % | 81 / 82 | 98 / 106 | 97 / 105 | 97 / 104 | 81 / 83 | 98 / 106 |
    | blotch-scale structure over the noise, the Tarantula's densest tenth | 0.29 | 0.29 | 0.29 | 0.26 | 0.12 | 0.14 |
    | Tarantula dark flat / left | 96.5 / 2.7 | 96.5 / 2.7 | 96.9 / 2.4 | 97.0 / 2.5 | 98.3 / 1.5 | 96.6 / 2.7 |
    | Andromeda dark flat / dug | 98.5 / 0.7 | 98.4 / 0.8 | 99.1 / 0.6 | 99.6 / 0.4 | 100.0 / 0.0 | 98.6 / 1.4 |

    - **The level does not move the crowded flat.** The level-only plate takes 1.22 / 0.89 / 0.56 sigma a pixel on
      the Tarantula, all of what the 15 and 31 px plates took, and the flat moves by 0.1. The level moves purity,
      the light taken from the field and the scorer's BP 17-19 row, and `gaia_compact.py`'s row is blind to it
      as meant (90.6).
    - **The lumps are the gap.** The rule plate keeps our level (against R4d's plate at 31 px: median 0.00 sigma,
      10th to 90th percentile -0.1 to +0.1; the light taken from the field 81 / 83 %) and reads both crowded flats
      above the judge's and BP 17-19 with the level set aside at the judge's 96.6. Of `gaia_where.py`'s bins only
      the densest fifth still trails by more than about a point (93.9 against 97.1). So the crowded flat reads structure between about 15
      and 100 px between the stars; the 31 px plate gains its 2.3 points with the blotch line unmoved, so part of
      that structure is wider than the blotch scale.
    - **The scorer already reads the grain on the held-out master:** the blotch line of `remover_check2.py`'s log
      (R4d 0.29, the judge 0.14), which no row carried.
    - **Looked at** (1:1, `judge_broad_look.py` with the rule plate in the third panel, and `gaia_where.py`'s
      sheet). The crowded field is a smooth glow at our level, but about a dozen star-shaped islands stand in a
      400 px crop where the rule kept our plate's residue round brighter stars. At 30 Doradus's edge the faint
      wisps outside the bright nebula are flattened, where the judge keeps them. M31's nucleus and dust lanes
      are as ours. Of the faint stars of the densest fifth the judge removed, 106 are still left (R4d 439), each
      a soft blob the rule kept. So the rule as a post step is a ceiling for the rows and not a product step as
      it stands.
    - **R4f's targets, looked at beside R4d's** (`target_pair_look.py`, the store's Tarantula session of
      2024-10-02, the five cells whose target changed most): smooth in crowded sky with the nebula's knots kept
      and no such islands (they are synthetic backgrounds steered by the plate, not the plate), a faint fine
      ripple at six times the contrast. R4d's targets in those cells hold the grain and the knot defect's zero
      blocks (#2276).
  - **What follows** (as corrected; the first version said that with the rule of 2026-10-23 kept, the level's
    share of these rows could not be reached by design, which the level-only plate refutes). (1) Two things
    separate our plate from the judge's in crowded sky and they are separable: the LEVEL the judge takes, and the
    LUMPS ours leaves between about 15 and 100 px. (2) The level moves purity, the light taken from the field and
    the scorer's BP 17-19 row, and no flat row. Our rules say not to take it (the owner's decision of 2026-10-23,
    the faint stars' cap), the field light guard holds it, and no star row needs it. (3) The lumps are the whole
    gap of the crowded flats and the six points at BP 17-19 with the level set aside: a plate without them, at our
    own level, reads the judge's rows or better. That is the grain R4f's targets no longer hold, so R4f is the
    arm on it, and these rows ask no decision of the owner. (4) Purity cannot guard such an arm (R4f's
    registration says what replaces it), and BP 17-19 is read with the level set aside. (5) Whether a product
    step could do it classically is open: the rule as it is leaves islands and flattens faint nebulosity. No arm
    and no step is registered on it.
- **A star that stands far out is left standing, on every such star in the store (2026-11-20, no training;
  #2292).** The star that started this was called HIP 80609 after its session; by its master's WCS it is Antares.
  Two things were measured: how many masters hold a star like it, and what our model and StarXTerminator do with
  each.
  - **The census** (`bright_census.py`): the store's 151 session masters that are not a flipped half, all 151
    searched. A candidate is every source that stands 100 pixel sigma over its frame's median and is the largest
    within 60 px, with no rank and no cap: 28,380 of them. The 1,185 whose ring at 100 to 140 px stands 3 sigma
    over their ring at 300 to 400 px were measured in rings out to 300 px, and 711 of those, in 76 masters, stand
    5 sigma or more at 100 to 140 px, the bar. A star's light falls with distance and a nebula's does not, so the
    rows over the bar are split by their profile: 127 FALLING (the 8 to 22 px ring at least four times the 100 to
    140 px ring) in 49 masters, and 584 FLAT.
  - **Looked at: all 127 falling rows, and the 133 flat rows nearest the cut** (a fall of 2 to 4;
    `bright_census_look.py`). Of the falling rows twenty-four are stars with a far halo of their own. Eighty-four
    are nebula: the cores of M42, the Lagoon, eta Carinae, 30 Doradus and the Trifid, and stars inside bright
    nebula, such as the Running Man's, whose far ring is the nebula's. Ten are fainter stars standing in a
    brighter Pleiad's halo. Two are one star on the Pleiades' nebulosity south of Merope, on both nights, whose
    far ring the eye cannot tell from the nebula (Alnitak in the Flame nebula and sigma Scorpii near Antares'
    reflection nebula were counted as stars the same way, by eye, so that star is in the list too, marked
    uncertain, and the reads run it). Four are steps at a frame's edge, and one each is a globular
    cluster, a galaxy and a planetary nebula. The flat rows looked at hold no star on dark sky: nebula, clusters,
    frame edges and faint stars inside a bright star's halo. The twenty-four read a fall of 14 to 468, so the cut
    at four has room.
  - **Twenty-four stars in eight sessions, and thirteen different stars** (named off each master's own WCS,
    `bright_star_names.py`: every one lies within half an arc minute of its catalogue position). Antares on three
    sessions and sigma Scorpii on two of them; eight Pleiads on each of two nights (Alcyone, Atlas, Electra, Maia,
    Merope, Taygeta, Asterope and 22 Tauri); Alnitak; iota Orionis; lambda Centauri. All twenty-four are pinned
    in `training/star-removal/bright_star_list.tsv` (name and night, master file, x, y), and after them the
    uncertain star's two rows.
  - **The names this section used before were wrong, and the count was a floor twice.** The stars had been named
    after their sessions and by rank. "HIP 80609" is Antares (HIP 80609 is that session's target, and the star
    with the halo in its frame is Antares); "Ophiuchus" and "Antares' field, the brighter" are the same star on
    two other sessions; "the fainter" of two of those sessions is sigma Scorpii; "south of M42" is iota Orionis;
    and "the Pleiades' brightest star, in the Merope nebula" is Maia, in the nebula round Maia. The first run took
    the four brightest peaks of a master and found ten such stars, the second took twelve and found fourteen,
    and the review of #2306 showed both times that a cap by brightness binds: peak brightness is flat among
    saturated stars, so the rank says nothing about a halo. The ten stars added now were behind that cap or
    nearer an edge than 420 px, which the first two versions did not read: Alcyone (the cluster's brightest),
    Atlas, Merope and 22 Tauri on both nights, Electra on the second, and lambda Centauri.
  - **What the count does not hold.** The fainter of two sources within 60 px of each other. A source within 60 px
    of a frame's edge (2,162 of the 28,380, 7.6 %). A star on nebula so bright that its own profile reads flat:
    it is among the flat rows, and its far ring is the nebula's. A source under 100 sigma on the quarter-scale
    read, which is never a candidate. And stars under the bar: Pleione reads 3.1 and
    4.3 sigma at 100 to 140 px on the two nights, and Celaeno does not pass the 3 sigma screen on either.
  - **Where the eight sessions sit in R2** (the `meta.json` of both `r2-synth29` and `r2-synth31`). Two train: the
    Ophiuchus cloud's of 2024-05-28 (Antares) and the Running Chicken's of 2026-02-20 (lambda Centauri). HIP
    80609's of 2026-04-21 (Antares, sigma Scorpii) is the trainer's VALIDATION session (its three views are under
    `val_sessions` and none under `train_sessions`), so no gradient step saw it. Antares' of 2023-05-15, the
    Pleiades' of 2022-01-04 and 2022-01-05, the Horsehead's of 2025-10-28 (Alnitak) and M42's of 2025-10-15 (iota
    Orionis) are in neither list. So twenty-two of the twenty-four are on masters no gradient step saw.
  - The census sigma is the mean of the channels over a central 512 px crop and the table's is each channel's own
    300 to 400 px annulus, so a census cut of 5 and a table figure are not one unit (iota Orionis reads 4.7 in
    the table and 6.3 in the census).
  - **What each remover leaves** (`bright_star_sheet.py`: ring medians less the frame's sky, in the master's pixel
    sigma, red, at 8-22 / 40-60 / 100-120 px). The R4d column is R4d as trained. Through the product's chain it
    reads the same to within 1.2 sigma from 8 px out on twenty-three of the twenty-four; on the twenty-fourth
    the chain puts the star back (below). Antares and sigma Scorpii of 2026-04-21 are read on that session's
    `flip=b` half, Antares at its peak there (x 1363, y 2245), and the rest on the session's unflipped master.

    | star and night | master | R4d | StarXTerminator | its session in R2 |
    |---|---|---|---|---|
    | Antares 2026-04-21 | 451 / 46.7 / 18.7 | 243 / 46.8 / 18.8 | 4.8 / 5.8 / 5.2 | validation |
    | sigma Sco 2026-04-21 | 35.4 / 6.7 / 4.0 | 7.9 / 6.2 / 3.8 | 2.9 / 3.3 / 3.2 | validation |
    | Antares 2024-05-28 | 442 / 26.0 / 4.9 | 98.7 / 25.6 / 5.0 | 0.7 / 0.4 / 0.8 | train |
    | Antares 2023-05-15 | 590 / 36.8 / 7.0 | 98.5 / 35.1 / 6.7 | 2.9 / 3.2 / 3.5 | neither |
    | sigma Sco 2023-05-15 | 64.3 / 11.0 / 6.8 | 12.4 / 9.9 / 6.3 | 7.0 / 7.5 / 6.0 | neither |
    | Taygeta 2022-01-05 | 1695 / 42.7 / 6.1 | 103 / 29.4 / 6.3 | 2.9 / 3.3 / 3.5 | neither |
    | Asterope 2022-01-05 | 720 / 22.8 / 6.2 | 33.6 / 17.1 / 6.3 | 3.6 / 3.9 / 3.4 | neither |
    | 22 Tau 2022-01-05 | 408 / 13.8 / 5.3 | 17.1 / 10.1 / 4.8 | 2.4 / 2.7 / 2.6 | neither |
    | Maia 2022-01-05 | 4649 / 120 / 33.2 | 2092 / 114 / 32.0 | 35.6 / 30.9 / 22.0 | neither |
    | Merope 2022-01-05 | 1713 / 85.6 / 26.9 | 459 / 78.7 / 27.4 | 25.5 / 20.4 / 20.2 | neither |
    | Alcyone 2022-01-05 | 4033 / 174 / 26.5 | 3335 / 165 / 25.6 | 6.7 / 5.6 / 5.7 | neither |
    | Electra 2022-01-05 | 3302 / 106 / 18.8 | 810 / 94.8 / 18.9 | 5.1 / 5.4 / 6.7 | neither |
    | Atlas 2022-01-05 | 2693 / 87.8 / 10.9 | 582 / 65.7 / 10.3 | 2.3 / 1.8 / 1.5 | neither |
    | Taygeta 2022-01-04 | 1353 / 29.7 / 3.9 | 28.5 / 9.0 / 3.1 | 1.3 / 1.6 / 1.9 | neither |
    | Asterope 2022-01-04 | 524 / 14.4 / 3.6 | 12.7 / 7.3 / 3.2 | 2.7 / 2.5 / 2.2 | neither |
    | 22 Tau 2022-01-04 | 272 / 8.2 / 3.2 | 5.5 / 3.4 / 2.4 | 1.5 / 1.8 / 1.8 | neither |
    | Maia 2022-01-04 | 3134 / 77.8 / 21.3 | 113 / 32.5 / 9.1 | 31.0 / 20.1 / 13.3 | neither |
    | Merope 2022-01-04 | 1563 / 53.5 / 16.8 | 46.0 / 17.1 / 6.8 | 17.2 / 14.6 / 12.1 | neither |
    | Alcyone 2022-01-04 | 2830 / 106 / 16.8 | 499 / 45.3 / 9.9 | 3.4 / 3.8 / 4.2 | neither |
    | Electra 2022-01-04 | 2056 / 66.1 / 11.8 | 114 / 22.9 / 6.8 | 5.0 / 4.8 / 4.2 | neither |
    | Atlas 2022-01-04 | 2431 / 54.9 / 6.9 | 52.1 / 18.0 / 5.9 | 0.8 / 0.7 / 1.2 | neither |
    | Alnitak 2025-10-28 | 529 / 30.0 / 5.3 | 17.1 / 11.6 / 5.3 | 0.9 / 1.3 / 2.9 | neither |
    | iota Ori 2025-10-15 | 194 / 11.1 / 4.7 | 12.4 / 7.5 / 4.5 | 3.1 / 3.2 / 3.2 | neither |
    | lambda Cen 2026-02-20 | 552 / 20.3 / 6.6 | 24.0 / 12.2 / 6.2 | 4.5 / 4.0 / 5.0 | train |

    Inside 22 px our model leaves 5.5 to 3,335 sigma. StarXTerminator leaves 1 to 7 there on twenty of the
    twenty-four, and 17 to 36 on Maia and Merope on both nights, which sit in their nebulae: its figures from
    22 px out are what the field holds (reflection nebula on the Pleiades and near Antares), and it keeps that
    nebula. At 100 to 120 px our ring stays within 1.3 sigma of the master's on twenty of the twenty-four; the
    four where our model takes more (5 to 12 sigma) are Maia, Merope, Alcyone and Electra on the master of
    2022-01-04.
  - **How far out our model takes anything, ring by ring** (the same file's rings: of the light StarXTerminator
    takes in a ring, the share R4d as trained takes; a dash where StarXTerminator takes under a sigma).

    | star and night | 8-22 px | 22-40 | 40-60 | 60-80 | 80-100 | 100-120 |
    |---|---|---|---|---|---|---|
    | Antares 2026-04-21 | 47 % | 5 | 0 | 0 | 0 | 0 |
    | Antares 2024-05-28 | 78 | 15 | 1 | 0 | 0 | 0 |
    | Antares 2023-05-15 | 84 | 38 | 5 | 0 | 4 | 8 |
    | sigma Sco 2026-04-21 | 85 | 38 | 13 | 11 | 15 | - |
    | sigma Sco 2023-05-15 | 91 | 59 | 31 | 27 | - | - |
    | Alcyone 2022-01-05 | 17 | 7 | 5 | 7 | 5 | 5 |
    | Maia 2022-01-05 | 55 | 24 | 6 | 3 | 6 | 11 |
    | Merope 2022-01-05 | 74 | 41 | 11 | 4 | 0 | 0 |
    | Electra 2022-01-05 | 76 | 39 | 11 | 1 | 0 | 0 |
    | Atlas 2022-01-05 | 78 | 53 | 26 | 8 | 7 | 7 |
    | Taygeta 2022-01-05 | 94 | 69 | 34 | 9 | 1 | 0 |
    | Asterope 2022-01-05 | 96 | 68 | 30 | 12 | 0 | 0 |
    | 22 Tau 2022-01-05 | 96 | 67 | 34 | 9 | 0 | 17 |
    | Alcyone 2022-01-04 | 82 | 69 | 59 | 61 | 60 | 55 |
    | Maia 2022-01-04 | 97 | 84 | 79 | 96 | 126 | 153 |
    | Merope 2022-01-04 | 98 | 91 | 94 | 117 | 166 | 212 |
    | Electra 2022-01-04 | 95 | 79 | 70 | 70 | 69 | 65 |
    | Atlas 2022-01-04 | 98 | 88 | 68 | 50 | 34 | 18 |
    | Taygeta 2022-01-04 | 98 | 88 | 74 | 69 | 52 | 38 |
    | Asterope 2022-01-04 | 98 | 79 | 59 | 55 | 42 | 27 |
    | 22 Tau 2022-01-04 | 99 | 85 | 76 | 62 | 34 | 56 |
    | Alnitak 2025-10-28 | 97 | 85 | 64 | 40 | 22 | 2 |
    | iota Ori 2025-10-15 | 95 | 66 | 46 | 29 | 17 | 13 |
    | lambda Cen 2026-02-20 | 96 | 82 | 50 | 21 | 11 | 27 |

    Four things show (a share is noisy where StarXTerminator takes only a few sigma, as in the outer rings of
    the fainter stars).
    - **Antares is barely touched beyond its innermost ring, on all three sessions:** 47, 78 and 84 percent
      there, then 5 to 38 at 22 to 40 px, and 0 to 8 beyond. All three were taken through a camera lens with an
      iris, whose star is
      a disc with many spikes.
    - **On the Pleiades the night decides, not the star.** On 2022-01-05 all eight are left from about 60 px
      out (1 to 12 percent at 60 to 80 px), and Alcyone and Maia already inside 22 px (17 and 55 percent). On
      2022-01-04 all eight are taken far out (50 to 117 percent at 60 to 80 px). The two masters are the same
      remote telescope and camera on two nights running. The earlier has the brighter sky (356 / 437 / 267
      against 252 / 298 / 216 in the files' units, R / G / B) and the larger noise (4.4 / 5.4 / 3.8 against
      3.4 / 4.4 / 3.5). What the model answers to was measured later that night: neither, but the stretch
      ("The stretch is the switch" below).
    - **On 2022-01-04 our model takes more than StarXTerminator** round Merope from 60 px out and round Maia
      from 80 px out (up to 212 and 153 percent): there it takes the nebula.
    - **On the rest** (sigma Scorpii twice, Alnitak, iota Orionis, lambda Centauri) it takes 85 to 97 percent of
      the innermost ring and a share that falls with distance.

    So there is no one reach, and where our model takes as far out as StarXTerminator it takes nebula with it.
  - **A colour cast where it does take the halo.** On 2022-01-04 every one of the eight comes out green. At 40 to
    60 px Maia's master reads 77.8 / 139 / 187 (R / G / B); StarXTerminator leaves 20.1 / 35.9 / 54.2, the
    nebula's own blue, and ours leaves 32.5 / 108 / 52.2: red and blue taken, green left. Alcyone, on dark sky,
    reads 106 / 163 / 197; StarXTerminator leaves 3.8 / 6.2 / 8.8 and ours 45.3 / 112 / 65.4. Looked at: on that
    night's plate Merope's nebula and Maia's come out green, and each star is a green core with a red pit below
    it. On 2022-01-05 the nebulae keep their colour, because there our model takes little in any channel and
    leaves the stars.
  - **Through the product's chain one star comes back whole (#2308).** On the master of 2022-01-05 our model as
    trained leaves Alcyone at 7,823 sigma inside 8 px (the master reads 22,412), and core protection takes what
    is left for a core. Its log reads "protected (3260, 2164), peak 7440 sigma, 1024 px", and the chain's plate
    holds the master's star again: 22,412 inside 8 px and 4,033 at 8 to 22 px. It is the only core protected on
    that master, and the catalogue check kept one of the 35 compact peaks inside it removed. On the seven other
    masters the chain protects nothing but M42's own core. Stars put back in cores are the third thing the
    owner's bar rules out (#2245, his line of 2026-11-19 as recorded there; the wording here is ours).
  - **The stretch is the switch, and three pixels set it (2026-11-20, late; no training; #2309).** The remover
    feeds its models one whole-frame stretch a channel (`ChunkedNafnetRunner.ApplyInputStretch`): the channel's
    minimum is subtracted, then a midtones balance puts the median above that minimum at 0.25. The minimum is
    one pixel. `stretch_swap.py` writes a master with another master's stretch numbers and nothing else changed,
    by planting a pixel or raising the few under the new minimum; `stretch_soften.py` scales a master's own.
    R4d as trained on each, the share of StarXTerminator's take at 8-22 / 22-40 / 40-60 / 60-80 px
    (`stretch_swap_rings.py`, then `stretch_swap_table.py`), and what is left at 40 to 60 px as in the table
    above: ring medians less the frame's sky, in the master's pixel sigma.

    | Alcyone | share taken, % | left at 40-60 px, R / G / B, sigma |
    |---|---|---|
    | 2022-01-05 as it is (master 174 / 262 / 298) | 17 / 7 / 5 / 7 | 165 / 233 / 260 |
    | with the other night's maximum and minimums (6 samples touched) | 80 / 71 / 64 / 65 | 67 / 169 / 82 |
    | with only the other night's maximum (3 samples) | 17 / 7 / 5 / 7 | 166 / 233 / 261 |
    | with only the other night's minimums (3 samples) | 80 / 71 / 64 / 65 | 67 / 169 / 82 |
    | every channel's minimum three times deeper (3 samples) | 89 / 68 / 34 / 16 | 116 / 167 / 183 |
    | 2022-01-04 as it is (master 106 / 163 / 197) | 82 / 69 / 59 / 61 | 45 / 112 / 65 |
    | with the other night's maximum and minimums (354 samples) | 17 / 8 / 5 / 3 | 101 / 147 / 178 |
    | with only the other night's minimums (295 samples) | 17 / 8 / 5 / 3 | 101 / 148 / 178 |

    The 354 are 295 dark pixels raised to the shallower minimums and 59 pixels of saturated star cores clipped to
    the lower maximum, so that arm changes stars. The 295 alone, which touch no star, were run after the review of
    #2311 asked for them and give the same answer on all eight. Three pixels do it one way and 295 the other: a
    minimum is deepened by one planted pixel and made shallower only by raising every pixel under it.

    - **All eight Pleiads of a frame move together**, each way: with the other night's minimums the stars of
      2022-01-05 are taken far out and green (Maia 97 / 83 / 82 / 100, Merope's nebula with her), and those of
      2022-01-04 stand with their colour. Looked at (512 px at 1:1): Merope's nebula turns green on the night it
      was blue and keeps its blue on the night it was green.
    - **The strength decides how much of the star goes.** With every channel softened alike, Alcyone gives up 89
      percent of her innermost ring and Maia 97, with no cast and the nebula kept, and the share falls with
      distance as on Alnitak. Antares on HIP 80609's master, softened the same way, gives up 81 percent of the
      innermost ring where it gave 47, and nothing beyond 40 px: the core goes, the disc and the spikes stay.
    - **The cast and the nebula taken follow the channels' balance, not the strength.** The two nights' minimums
      differ by a factor of 1.7 in red and in green and by almost nothing in blue (median above minimum 168 /
      400 / 174 on 2022-01-04 against 98 / 233 / 183 on 2022-01-05, in the files' units). With 2022-01-04's balance the model takes red and
      blue far out and leaves green. With all three softened alike by a factor of three, a softer stretch than
      either night's, there is no cast and the nebula stays.
    - **It is not the whole of #2292.** A stretch three times harder does not make Alnitak stand (97 / 85 / 64 /
      40 becomes 96 / 83 / 62 / 63), and no softening reaches Antares' disc. The far wing is still a matter of
      what the model was shown. The frame's maximum, its unit divisor, does not matter here.
    - **Refuted on the way** (`stretched_levels.py`): a ring's level in the stretched input does not say whether
      it is taken. Across the twenty-four stars rings at one level are taken 5 percent and 84 percent.
    - **The store** (`stretch_depths.py`, the 123 colour masters): the darkest pixel sits 8 to 228 noise sigma
      under its channel's sky (median 34, 10th to 90th percentile 19 to 94), and within a master the deepest
      channel is 1.5 times the shallowest at the median and twice or more on 22. The export stretches each
      session with its master's numbers, so every session trained at the strength and balance its darkest
      pixels happened to give it.
    - **A black point a fixed number of noise sigma under each channel's sky, tried at inference** on R4d as it
      is, no retrain (`stretch_depth_set.py ... centre`; 3 to 5 samples a frame). The sigma of this first try was
      the noise of the frame's central 512 px crop; by the sky noise the registered read below uses, its two
      depths are 69 to 94 and 138 to 188 sigma, differing by channel and night. The share taken at 8-22 / 22-40 /
      40-60 / 60-80 px:

      | | 2022-01-05 as it is | at 60 sigma | at 120 sigma | 2022-01-04 as it is | at 60 sigma | at 120 sigma |
      |---|---|---|---|---|---|---|
      | Alcyone | 17 / 7 / 5 / 7 | 67 / 48 / 37 / 32 | 89 / 69 / 52 / 39 | 82 / 69 / 59 / 61 | 81 / 57 / 45 / 35 | 93 / 69 / 52 / 42 |
      | Maia | 55 / 24 / 6 / 3 | 92 / 56 / 26 / 26 | 96 / 69 / 47 / 44 | 97 / 84 / 79 / 96 | 95 / 54 / 36 / 41 | 97 / 69 / 54 / 47 |
      | Merope | 74 / 41 / 11 / 4 | 87 / 60 / 35 / 23 | 94 / 72 / 50 / 35 | 98 / 91 / 94 / 117 | 94 / 71 / 51 / 32 | 96 / 77 / 55 / 39 |
      | Taygeta | 94 / 69 / 34 / 9 | 96 / 75 / 58 / 44 | 97 / 82 / 59 / 43 | 98 / 88 / 74 / 69 | 97 / 79 / 62 / 52 | 97 / 84 / 63 / 41 |

      The two nights read much closer at either depth than as they are (up to 113 points apart on these four
      stars, Merope at 60 to 80 px): at 120 sigma they differ by up to 7 points (Maia 47 against 54 at 40 to
      60 px), at 60 sigma by up to 16 (Merope 35 against 51, Maia 26 against 41, Alcyone 67 against 81). The two
      depths were not equal between the nights in sky sigma, as said above, so some of what is left may be that.
      Nothing is taken beyond
      StarXTerminator's take any more (Merope of 2022-01-04 at 100 to 120 px: 212 percent as it is, 32 and 64),
      and the colour holds: Alcyone of 2022-01-04 keeps 50 / 59 / 52 percent of her light at 40 to 60 px in R /
      G / B at 120 sigma, and 49 / 58 / 51 the next night, against 43 / 69 / 33 as it is. Looked at (Merope on
      both nights and Alcyone, 512 px at 1:1): the two nights' plates are alike, the nebula blue-grey with its
      streaks, and where the star was a faint ring with a green tinge and a small dark pit. So a black point that
      does not hang on one pixel brings the model's answers on the two nights together without a retrain; what
      it does to the scorecard's rows is the read registered next.
  - **The black point's read on the scoring masters, registered 2026-11-20 before any score of it.** No training
    and no arm: R4d as it is, on the three scoring masters written with every channel's minimum at its median less
    60 and less 120 noise sigma (`stretch_depth_set.py`), as trained and through the product's chain (protection
    without the stars, then floor5), scored as every arm is.
    - **The sigma is the sky's** (corrected before any score, the same night): the 20th percentile over the
      frame's 64 px blocks of each block's neighbour-difference sigma, since a central crop is 30 Doradus on the
      Tarantula master. By it the three masters' own depths are 45 / 40 / 48 on Andromeda, 9 / 11 / 10 on the
      Tarantula and 32 on the Sagittarius master (one plane): the Tarantula, which carries the crowded rows, is
      stretched far harder as it is than the other two, and 60 or 120 sigma softens it five- to thirteenfold.
    - **The question:** do the scorecard's rows hold at a black point that mends the bright stars? Through the
      chain, every whole-master guard of R4g's list: the Tarantula's crowded flat at least 90.3 (R4d 91.3), the
      Sagittarius crowded flat at least 96.3, Andromeda's dark flat at least 95 with at most 3 dug, the
      Tarantula's dark flat at least 91, purity at most 1.0 %, the light taken from the Tarantula field at most
      83 % and 84 %; the glow's green median within 450 of zero on Andromeda and within 500 on the Tarantula (R4d
      -8.5 and 106.5); the M31 nucleus under 10 % (R4d 0.0). And one row R4g's list reports without a bar: Gaia
      BP 17-19 with the level set aside at least 89.7 (R4d 90.7, a point under it). A softer black point is what
      risks cores and glow, so those two are guards here, not reported only.
    - **Nebula guards, as trained,** all of R4g's: light added on at most 1.0 % of each master's nebula; of the
      brightest tenth of the Tarantula's peaks at least 11 of 77 standing; of the faint stars on bright background
      (`audit.py real`) at most 37 % left on the Lagoon's core and at most 27 % on 30 Doradus (R4g's bars; R4d
      29 % of 142 and 22 % of 321). And one added for this read: the Lagoon's broad light taken no more than
      R4d's 2.67.
    - **Of R4g's list only the tile rows are left out:** by count clean at 100-1000 sigma and at 1000 and over,
      M42's 100-1000 and the SMC's two bands ("Not read here" below says why). A depth that passes the rows above
      is still unread on those.
    - **What is expected.** The bright stars gain, as on the Pleiades. The star rows are the risk: a softer stretch
      puts faint stars nearer the sky in the model's input, so the crowded flats and BP 17-19 may fall. The
      nebula rows may move either way, and no direction is predicted.
    - **Reading.** A depth at which every row above holds is put to the owner as a candidate for the product's
      black point and for the domain of a retrain. A depth at which a guard fails is not. If both fail, the rule
      as tried is no product step and the black point goes to a retrain alone. Nothing is adopted by this read.
    - **Reported, no bar:** every scorecard row beside R4d's as it is; the three masters' own depths; the
      twenty-four stars of `bright_star_list.tsv` at each depth, as trained, and the uncertain star's two rows.
    - **Not read here:** the rows scored on validation tiles (M42, the SMC, the counts by sigma). The tiles carry
      the export's stretch, so they are read only once a depth has held on the whole masters.
    - **Looked at before the rows are quoted:** the Lagoon's core, 30 Doradus's core, the Tarantula's crowded
      field and knots, M31's nucleus and a bright star on dark sky, at each depth beside R4d as it is and
      StarXTerminator.
    - **The read, 2026-11-21: both depths fail guards, so the rule as tried is no product step.** The masters as
      they are, sent through the same script and binary, give R4d's own rows to the last digit (as trained,
      through the chain, the nebula rows and the faint-star audit), so the three columns are like for like.

      | row | as it is | 60 sigma | 120 sigma | bar | read |
      |---|---|---|---|---|---|
      | Tarantula crowded flat, chain | 91.3 | 91.1 | **88.3** | at least 90.3 | held, **failed** |
      | Sagittarius crowded flat, chain | 97.0 | 97.2 | 97.2 | at least 96.3 | held |
      | Andromeda dark flat and dug, chain | 98.5, 0.7 | 98.5, 0.7 | 97.8, 0.8 | at least 95, at most 3 | held |
      | Tarantula dark flat, chain | 96.5 | 96.1 | 95.7 | at least 91 | held |
      | glow green median on Andromeda, chain | -8.5 | -66.3 | 13.1 | within 450 | held |
      | glow green median on the Tarantula, chain | 106.5 | 301.6 | **755.7** | within 500 | held, **failed** |
      | M31 nucleus, chain | 0.0 % | 0.0 % | 0.0 % | under 10 % | held |
      | purity, chain | 0.8 | 0.7 | 0.6 | at most 1.0 | held |
      | light taken from the Tarantula field, chain | 81, 82 % | 77, 82 % | 75, 79 % | at most 83, 84 % | held |
      | Gaia BP 17-19 with the level set aside, chain | 90.7 | 92.0 | **88.3** | at least 89.7 | held, **failed** |
      | light added on each master's nebula, as trained | 0.0 % | 0.0 % | 0.0 % | at most 1.0 % | held |
      | brightest tenth of the Tarantula's peaks standing, as trained | 17 of 77 | **1 of 77** | **2 of 77** | at least 11 | **failed** at both |
      | faint stars left on the Lagoon's core, as trained | 29 % | 29 % | 34 % | at most 37 % | held |
      | faint stars left on 30 Doradus, as trained | 22 % | 26 % | **35 %** | at most 27 % | held, **failed** |
      | the Lagoon's broad light taken, as trained | 2.67 | **2.93** | **2.83** | at most 2.67 | **failed** at both |

    - **Looked at before the rows** (`arm_look.py` at each depth: StarXTerminator, R4d as it is, 60, 120). **The
      Tarantula's knots:** as it is R4d dims them; at 60 and at 120 sigma their bright points are gone, as trained
      and through the chain, with small dark specks in the nebula below them. **30 Doradus's core and the
      Lagoon's:** the brightest part is flatter at each depth. **M31's nucleus, as trained:** dimmed, with a red
      stain beside it, at 60 sigma and gone at 120, a pink patch in its place; protection gives it back through
      the chain. **The Tarantula's crowded field, through the chain:** lumpier at each depth. **The bright star on
      Andromeda:** gone at every depth.
    - **Reported.** As trained, as it is / at 60 / at 120: Andromeda's dark flat 69.4 / 62.3 / 39.0 with 19.5 /
      21.6 / 38.0 % dug; the Tarantula's 80.3 / 80.9 / 68.8 with 14.3 / 7.1 / 12.8 % left; its crowded flat
      86.7 / 87.8 / 82.3; the M31 nucleus taken in its worst channel 9.7 / 33.3 / 72.9 %; broad light taken on
      Andromeda 2.88 / 3.16 / 4.04 and on the Tarantula 5.86 / 7.91 / 7.91; the Tarantula's glow median -6.5 /
      357 / 648. Through the chain the blotch line on the Tarantula's densest tenth reads 0.29 / 0.33 / 0.38.
    - **The stars of `bright_star_list.tsv` at each depth** (`stretch_depth_set.py` on their own eight masters, R4d
      as trained; the share of StarXTerminator's take at 8-22 / 22-40 / 40-60 / 60-80 px, as it is, at 60 and at
      120 sigma):

      | star and night | as it is | at 60 sigma | at 120 sigma |
      |---|---|---|---|
      | Antares 2026-04-21 | 47 / 5 / 0 / 0 | 72 / 14 / 1 / 0 | 81 / 29 / 2 / 1 |
      | Antares 2024-05-28 | 78 / 15 / 1 / 0 | 81 / 23 / 3 / 0 | 88 / 37 / 6 / 0 |
      | Antares 2023-05-15 | 84 / 38 / 5 / 0 | 87 / 38 / 4 / 0 | 89 / 48 / 8 / 0 |
      | Alcyone 2022-01-05 | 17 / 7 / 5 / 7 | 65 / 45 / 31 / 25 | 81 / 61 / 47 / 38 |
      | Alcyone 2022-01-04 | 82 / 69 / 59 / 61 | 80 / 55 / 40 / 35 | 91 / 66 / 50 / 41 |
      | Maia 2022-01-05 | 55 / 24 / 6 / 3 | 90 / 58 / 33 / 30 | 95 / 63 / 36 / 40 |
      | Maia 2022-01-04 | 97 / 84 / 79 / 96 | 94 / 58 / 36 / 39 | 97 / 65 / 55 / 50 |
      | Merope 2022-01-05 | 74 / 41 / 11 / 4 | 81 / 56 / 29 / 25 | 91 / 66 / 48 / 38 |
      | Merope 2022-01-04 | 98 / 91 / 94 / 117 | 92 / 69 / 49 / 43 | 96 / 75 / 56 / 35 |
      | Alnitak 2025-10-28 | 97 / 85 / 64 / 40 | 93 / 73 / 29 / 8 | 93 / 71 / 38 / 14 |
      | iota Ori 2025-10-15 | 95 / 66 / 46 / 29 | 95 / 59 / 30 / 19 | 94 / 49 / 13 / 5 |
      | lambda Cen 2026-02-20 | 96 / 82 / 50 / 21 | 97 / 87 / 64 / 26 | 96 / 87 / 58 / 18 |

      The table is a selection (12 of the 26 rows); the counts that follow are over all of them, so they cannot be
      read off it. Over all twenty-four and the uncertain star's two rows the innermost ring is taken under 80
      percent on 7 as they are, on 2 at 60 sigma and on 1 at 120, and nothing is taken beyond StarXTerminator's
      take at either depth, bar one ring: south of Merope 2022-01-04 reads 102 at 22-40 px at 60 sigma (every
      other ring at both depths is 100 or under). With the depth truly equal the two Pleiades nights stand within
      20 points at 60 sigma (Merope, 40-60 px) and 19 at 120 (Maia, 40-60 px) over all eight Pleiads, up to 113
      apart as they are. A ring where StarXTerminator takes under 3 sigma is left out, the floor of
      `stretch_swap_table.py` and `stretched_levels.py`. With the floor at 1 sigma
      (`bright_star_nights.py --floor 1`) the two read 31 and 38, both 22 Tau at 60-80 px, where StarXTerminator
      takes 2.6 sigma on 2022-01-04 and 5.0 on 2022-01-05 (shares 27 / 58 at 60 and 45 / 7 at 120): those rest
      on one noise-sized ring. (A correction of 2026-11-21 put 31 and 38 here without naming the floor, and
      called the first figures a reading of three stars, which was a guess. It is withdrawn: 20 and 19 stand.)
      So equal depth brings the nights together and does not make them one. It is still no clean plate: inside
      22 px Alcyone of 2022-01-05 keeps 1,403 and 756 sigma of her 3,335, where StarXTerminator leaves 7. Looked
      at (512 px at 1:1): Alcyone is a smaller core with a green fringe at each depth; Antares loses its core to
      a pink ring and keeps its disc and every spike. Alnitak and iota Orionis give up less far out at 60 than
      as they are. For Alnitak
      that fits its own black point, which is deeper than 60 in red and green (65.2 / 127.6 / 54.5), so 60 is
      the harder one there. Iota Orionis is not that: its master reads 49.1 / 57.8 / 40.8, so 60 sigma is a
      softer black point in all three channels, yet R4d gives up less there (40-60 px share 46 at the master's
      own depth, 30 at 60, 13 at 120). That one is unexplained. One master was done twice: the Ophiuchus master's
      canvas ring is NaN, the script's noise figure came out NaN and it wrote the frame unchanged without a word.
      It now reads only differences whose two pixels are there and stops with a message when it has no figure;
      the three scoring masters come out byte for byte as they were scored.
    - **Against what was expected.** The star rows were the named risk and held at 60 sigma (the crowded flat
      within 0.2, BP 17-19 with the level set aside up 1.3) and fell at 120. No direction was predicted for the
      nebula rows; they moved against us at both depths.
    - **What it says.** The strength of the stretch is one dial for everything bright and compact. Softer, R4d
      takes more of a bright star, as on the Pleiades and Antares (not on Alnitak, whose black point is deeper
      than 60 in two channels, nor on iota Orionis, which is unexplained), and with it the nebula's knots, the
      cores' peaks and M31's nucleus. The three scoring masters happen to sit where they read well: the
      Tarantula's own black point is 10 sigma under its sky, where its knots stand, against about 40 to 90 on
      the Pleiades masters. So the rule as tried, one black point at 60 or at 120 sigma, does not both mend the
      bright stars and keep the nebula. Nothing was read between 10 and 60 sigma (the Tarantula already fails the peaks
      guard at 60, 1 of 77), so a depth in between is not ruled out by these rows; one is registered below. The
      scorecard has been reading R4d at depths chosen by each master's darkest pixel. If no depth holds, what is
      left is the retrain: one black point by noise in the export and at inference, so that a star and a knot
      each look one way to the model on every frame. It is not registered, and it is the owner's.
    - **One more depth, 40 sigma: the text pushed at 07:04 on 2026-11-21 was registered before the run** (its
      wording was touched at 07:30; the read below says how). The review of this read asked
      what lies between the Tarantula's own 10 sigma and 60. The same script, binary, masters, guards and looks
      as the two depths above, and the stars of the list at 40 as trained. The three scoring masters are the
      scorecard's own copies and the stars' masters those of the 2026-10-12-full bake, as at 60 and 120; the
      script reads other depths on another bake's copy of a master.
      - **Predicted.** The Tarantula's brightest peaks fail again (17 of 77 stand at 10 sigma, 1 at 60). On the
        stars 40 lands between as they are and 60: the innermost ring taken under 80 percent on 3 to 6 of the 26
        rows (7 as they are, 2 at 60). The stars prediction is the less sure: for the Pleiades master of
        2022-01-05, whose own depths are 37.5 / 74.1 / 80.8, 40 sigma leaves red where it is and is about twice
        as hard in green and blue.
      - **Reading.** A depth is a candidate only if every guard holds and the stars read as at 60 (the innermost
        ring under 80 percent on at most 2 of the 26). If a guard fails at 40, the rule has failed at every depth
        tried from 40 up, and what is left under 40 is harder than the Pleiades masters' own black point in green
        and blue; the stars' rows at 40 then say whether a lower depth is worth a registration. If every guard
        holds and the stars are not mended, the next depth to register lies between 40 and 60. Nothing is adopted
        by this read.
    - **The read at 40 sigma, 2026-11-21: two guards fail and the stars are not mended, so it is no candidate.**
      The registration was pushed at 07:04 and the run started at 07:06. The step log's first lines showed four
      as-trained rows before the sheets were open; nothing was quoted from them before the look. The wording
      commit ("the 40 sigma registration names its masters' store; two wordings from the review") is stamped
      07:30 to 07:35 (+1100), AFTER the run began and while the star runs were still writing, so the
      registration "before any score" is the 07:04 text only; that commit named the masters' store and
      reworded two sentences, and left the prediction and the reading rule as pushed at 07:04. (Written 07:30,
      four minutes after the scorecard's rows were on disk and before they were opened; 07:35 is its move onto
      this branch.)

      | row | as it is | 40 sigma | bar | read |
      |---|---|---|---|---|
      | Tarantula crowded flat, chain | 91.3 | 91.8 | at least 90.3 | held |
      | Sagittarius crowded flat, chain | 97.0 | 97.2 | at least 96.3 | held |
      | Andromeda dark flat and dug, chain | 98.5, 0.7 | 98.4, 0.7 | at least 95, at most 3 | held |
      | Tarantula dark flat, chain | 96.5 | 96.1 | at least 91 | held |
      | glow green median on Andromeda, chain | -8.5 | -30.0 | within 450 | held |
      | glow green median on the Tarantula, chain | 106.5 | 76.9 | within 500 | held |
      | M31 nucleus, chain | 0.0 % | 0.0 % | under 10 % | held |
      | purity, chain | 0.8 | 0.7 | at most 1.0 | held |
      | light taken from the Tarantula field, chain | 81, 82 % | 78, 83 % | at most 83, 84 % | held |
      | Gaia BP 17-19 with the level set aside, chain | 90.7 | 92.9 | at least 89.7 | held |
      | light added on each master's nebula, as trained | 0.0 % | 0.0 % | at most 1.0 % | held |
      | brightest tenth of the Tarantula's peaks standing, as trained | 17 of 77 | **1 of 77** | at least 11 | **failed** |
      | faint stars left on the Lagoon's core, as trained | 29 % | 28 % | at most 37 % | held |
      | faint stars left on 30 Doradus, as trained | 22 % | 21 % | at most 27 % | held |
      | the Lagoon's broad light taken, as trained | 2.67 | **2.72** | at most 2.67 | **failed** |

      - **Looked at before the rows** (the same sheets, each panel also cropped to 1:1). **The Tarantula's
        knots:** their bright points are gone at 40 as at 60, as trained and through the chain, where R4d as it
        is leaves a pale trace of them. **30 Doradus's core:** flatter than as it is. **M31's nucleus, the
        Lagoon's core, the Tarantula's crowded field and the bright star on Andromeda:** as R4d leaves them now.
      - **Reported.** As trained, as it is / at 40: Andromeda's dark flat 69.4 / 73.2 with 19.5 / 14.4 % dug;
        the Tarantula's 80.3 / 85.7 with 14.3 / 6.8 % left; its crowded flat 86.7 / 88.9; the M31 nucleus taken
        in its worst channel 9.7 / 10.1 %; broad light taken on Andromeda 2.88 / 2.87 and on the Tarantula 5.86
        / 7.69; the Tarantula's glow median -6.5 / 232. Through the chain the blotch line on the Tarantula's
        densest tenth reads 0.29 / 0.31. The scoring masters' own depths by the script: Andromeda 45.0 / 40.4 /
        48.0, the Tarantula 9.3 / 11.0 / 10.2, Sagittarius 31.9.
      - **The stars at 40** (R4d as trained; the share of StarXTerminator's take at 8-22 / 22-40 / 40-60 /
        60-80 px; the same selection of 12 of the 26 rows):

        | star and night | as it is | at 40 sigma | at 60 sigma |
        |---|---|---|---|
        | Antares 2026-04-21 | 47 / 5 / 0 / 0 | 56 / 7 / 0 / 0 | 72 / 14 / 1 / 0 |
        | Antares 2024-05-28 | 78 / 15 / 1 / 0 | 71 / 12 / 2 / 0 | 81 / 23 / 3 / 0 |
        | Antares 2023-05-15 | 84 / 38 / 5 / 0 | 81 / 28 / 2 / 0 | 87 / 38 / 4 / 0 |
        | Alcyone 2022-01-05 | 17 / 7 / 5 / 7 | 65 / 39 / 29 / 20 | 65 / 45 / 31 / 25 |
        | Alcyone 2022-01-04 | 82 / 69 / 59 / 61 | 72 / 47 / 29 / 29 | 80 / 55 / 40 / 35 |
        | Maia 2022-01-05 | 55 / 24 / 6 / 3 | 84 / 52 / 38 / 32 | 90 / 58 / 33 / 30 |
        | Maia 2022-01-04 | 97 / 84 / 79 / 96 | 91 / 58 / 34 / 28 | 94 / 58 / 36 / 39 |
        | Merope 2022-01-05 | 74 / 41 / 11 / 4 | 75 / 52 / 33 / 25 | 81 / 56 / 29 / 25 |
        | Merope 2022-01-04 | 98 / 91 / 94 / 117 | 88 / 62 / 34 / 34 | 92 / 69 / 49 / 43 |
        | Alnitak 2025-10-28 | 97 / 85 / 64 / 40 | 92 / 72 / 23 / 5 | 93 / 73 / 29 / 8 |
        | iota Ori 2025-10-15 | 95 / 66 / 46 / 29 | 94 / 59 / 32 / 24 | 95 / 59 / 30 / 19 |
        | lambda Cen 2026-02-20 | 96 / 82 / 50 / 21 | 96 / 86 / 66 / 31 | 97 / 87 / 64 / 26 |

        Over all 26 rows the innermost ring is taken under 80 percent on 7 at 40, as on 7 as they are and 2 at
        60, and no ring reads over 100. The seven are not the same seven: Maia of 2022-01-05 leaves the list
        (55 to 84) and Alcyone of 2022-01-04 joins it (82 to 72). The two nights stand closest at 40, by a
        little: over all eight Pleiads the widest gap in these four rings, leaving out a ring where
        StarXTerminator takes under 3 sigma, is 15 points, against 20 at 60, 19 at 120 and 113 as they are (with
        the floor at 1 sigma the 60 and 120 figures are 31 and 38, set by one ring, 22 Tau at 60-80 px). No
        spread is stated, and 20 and 19 are close to 15. Inside 22 px Alcyone of 2022-01-05 keeps 1,419 sigma
        of her 3,335, as at 60 (1,403). Iota Orionis gives up less far out at 40 too (40-60 px 46 / 32 / 30),
        though 40 is the harder black point on its master and 60 the softer; still unexplained. Looked at (512
        px at 1:1): Alcyone is the smaller core with a green fringe that 60 leaves; Antares keeps its disc and
        every spike around a pink ring; iota Orionis leaves the same faint halo at each depth.
      - **Against what was predicted.** The peaks failed, as predicted. The stars did not land between: 7 of
        26, where 3 to 6 was predicted. The depth moved rows both ways, which a count of rows hides.
      - **What it says.** The knots lose their brightest peaks somewhere between the Tarantula's own 10 sigma
        and 40, and the stars' rows mend only from 60 up, so on these masters this rule has no depth that does
        both, as far as four depths show it (the innermost ring moves one way with the depth within a point: iota
        Ori 94 / 95 / 94, lambda Cen 96 / 97 / 96; the outer rings do not, Merope 2022-01-05 at 40-60 px reads
        33 / 29 / 48 at 40 / 60 / 120). No depth under 40 is registered: by the stars' innermost rows at 40 it
        has nothing to give the stars, and it is harder than the Pleiades masters' own black point in green and
        blue. At 40 the two nights stand 15 points apart, against 20 and 19 at 60 and 120 on the same floor: a
        little closer, not a different kind of reading. What all three depths show is that at one depth the
        nights read far more alike than as they are (113), which is what a retrain in one domain would build on.
        Which depth such a domain should have these rows cannot say: they are read on a model trained on masters
        whose own depths run from 8 to 228 sigma. The retrain is not registered, and it is the owner's.
  - **Looked at** (the sheets, 512 px at 1:1: master / StarXTerminator / R4d as trained / R4d through the chain,
    all twenty-four). StarXTerminator leaves a smooth field on every one, and Merope's and Maia's nebulae with
    their streaks intact. Ours leaves:
    - on Antares, on all three sessions, a glowing disc with every diffraction spike (the many spikes of an
      iris), and a faint disc with spikes even on sigma Scorpii, 35 sigma in its innermost ring;
    - on the Pleiades of 2022-01-05 each star's core with its spikes, Alcyone nearly whole, and on Asterope and
      22 Tauri a ring with a dark pit;
    - on the Pleiades of 2022-01-04 a small green core with a red pit and a green glow;
    - on Alnitak a faint smudge with a pit, and on lambda Centauri, whose four spikes cross the whole crop, a
      faint grey disc with a trace of the cross.

    Smaller bright stars in the same crops are removed.
  - **What follows.** (1) The likely causes, inferred and not measured. The injector's halo ends at 45 px
    (`InjectionPopulation.PlateHaloReachPx`) and it draws no spikes, and the ring table shows the take fading
    between 22 and 100 px on most stars; a lens's disc with many spikes is a shape no drawn star has, which would
    explain why Antares is barely touched. It does not explain the Pleiades: the same eight stars through the
    same telescope are left on one night and taken, nebula and all, on the night before. That was measured the
    same night and is the stretch's black point, one pixel a channel ("The stretch is the switch" above, #2309):
    a product defect of its own, and it means every drawn star so far was drawn into a domain whose strength
    and colour balance vary by chance. A black point that does not hang on one pixel and is the same for the
    three channels in noise terms (the median less a fixed number of noise sigma is the candidate), in the
    export and at inference, would make that domain one; it needs a retrain, it would come before any far wing
    is drawn, and it is not registered. The truth plates keep spikes by design
    (`ClassicalStarRemover`, "spikes are left whole") and cut the halo flat near 120 px, a further candidate
    cause. A halo injected past 45 px, and a star drawn with an iris's spikes, each read the same way, would
    separate those; neither has been run. (2) Such stars are rare: at least 8 of the 151 masters hold one (a floor, for what the count does not hold), thirteen
    different stars in all, and a session's 40 drawn cells rarely hold its one such star. Two of the eight
    sessions train, and their stars are removed no better than their kind: Antares where it trains reads as
    Antares on the two sessions that do not (78 percent of the innermost ring against 47 and 84), and lambda
    Centauri as Alnitak and iota Orionis, which do not train. So more data is unlikely to teach it; that has not
    been tested by adding sessions. If it holds, it has to be drawn, a far wing to a few hundred pixels and the
    spikes. (3) Drawing may
    not be enough for the farthest light. The model sees windows of 256 px (its contract's input;
    `OnnxTianWenStarRemover` steps them by half their interior), and the net's own reach is wider than that
    (about 294 px for four levels, "R2n" above), so the window is the limit: light farther than about 128 px from
    a star's core reaches the model mostly in windows that do not hold the star, where it cannot be told from
    sky. On the twenty-four stars the ring at 140 to 170 px stands 2 to 19 sigma over the sky. The disc, the
    spikes and the wing inside about 120 px are within one window. (4) Where our model does take a halo far out
    it takes nebula and shifts its colour (Merope's and Maia's nebulae above), so a longer reach has to come with
    the nebula rows that R4g's read carries. (5) The scoring masters hold no such star, so no row of the
    scorecard reads this. R4f's and R4g's reads run the stars of `bright_star_list.tsv` beside R4d and
    StarXTerminator (the list pins their inputs). Their registrations named that list when it held fourteen;
    it was extended to twenty-four before either arm had a score, which lengthens a read and moves no bar.
    (6) The chain's core protection can put such a star back whole (#2308). (7) Nothing is registered on it, and
    whether it comes before or after R4f's and R4g's reads is the owner's.
- **R4h and R4i, pre-registered 2026-11-21 at 20:10, before any export of either arm (the rounds that came later
  are under "How this text came to be" below): the black point by sky noise trained in (#2309), beside a control
  for the exporter.** The 40 sigma read above left the retrain to the owner.
  His word on it, 2026-11-21: "yeah queue in afterR4f"; and asked whether two arms with a control or one arm from
  the old exporter: "you decide". The session chose two arms, since the exporter has changed under R4d's recipe and
  every later arm needs that baseline. Both are R4d's line (`export-synth.ps1 -BetaDraw 0.3,1.3,6,3 -FaintStars 3,1
  -Extra --saturated-own-share,--saturated-measured,--halo-over-plate,--noise-as-recorded`), R3v's model and recipe
  with R4d's flags, seed 0, and both are exported from one binary built from main on 2026-11-21.
  - **The trainer both arms train from.** Not R4f's rerun copy as it stands: `C:/temp/e2/r4f1-run` is R3v's
    `r3h-run` plus `--save-best` only and holds no black point code (its `n2n_smoke.py` has no
    `load_black_point_sigma` or `BLACK_POINT_KEY`, the prepare step that writes `stretch_black_point_sigma` into the
    cache's `meta.json`, and its `n2n_export.py` has no `stretchBlackPointSigma`, the contract writer; both arrived
    with #2328, after #2329). Both arms train from one new frozen copy: that copy plus the black point parts of
    #2328 in `n2n_smoke.py` and `n2n_export.py` (48 and 23 changed lines; main's two files are not copied whole,
    only those changes are carried over; of `n2n_smoke.py`'s four hunks three apply by patch and the fourth, the
    `black_point` argument of the `ExtraCache.open` call, was written by hand),
    the same bytes for R4h and R4i, its `FROZEN-AT.txt` naming the commit. The
    "ends on the same weights to the bit" check of R4f's rerun is repeated on that copy, covering the added parts
    too (a tiny cache, the frozen copy without and with them, R4h's switches), all but that hand-written line: it
    lies on the `--extra-cache` path, which neither arm takes and the check does not run, so nothing here shows it
    right and nothing of either arm depends on it. The cache is prepared and the model
    exported with that copy's scripts, never with R4f's. **The check, run 2026-11-21 at 20:33, before any cache of
    either arm,** on the two-session preflight exports: the cache the new copy prepares from the export without a
    depth is the rerun copy's, every file the same bytes; sixty steps with the arm's switches end on the same
    weights to the bit, the last step's and the best step's; the new copy's prepare records
    `stretch_black_point_sigma` 40 for the export at 40 sigma and no key for the other, and its exporter writes
    `stretchBlackPointSigma` 40 into the one contract and nothing into the other.
  - **Training alike.** Both arms are one Batch run each on a low-priority T4, capped at 36 hours, as R4f's rerun,
    the same recipe, seed and step cap, so the control is a control. A pair that does not train alike (one
    evicted past the cap, one stalled) is read as the Kill bullet below says.
  - **Contract check, before the first score of either:** R4i's `contract.json` states `stretchBlackPointSigma`
    40 and R4h's states none. A silent contract and an unset feed agree, so the loader would not refuse R4i and
    the product would stretch at each channel's minimum, the mismatch #2309 exists to remove; the check is what
    catches it. It is also read before each launch, per arm. R4i's prepare prints that it stretched from 40
    sky-noise sigma and writes `stretch_black_point_sigma` 40 into the cache's `meta.json`; a cache of R4i's without
    that key is not trained from. R4h's prepare prints "each channel's minimum" and writes no such key; a cache of
    R4h's that carries one is not trained from, since that would spoil the control.
  - **R4h, the control (synth32):** the black point at each channel's minimum, as in every export so far. Its data
    differs from R4d's by what the exporter changed since `bh-bin8`; the knot fix of #2286 is known to change tiles.
    A preflight on one session before the export (the new binary without the flag against `bh-bin11`'s tiles of
    that session) says whether anything else does, and its result is written here before the export's first score.
    **The preflight, run 2026-11-21 from 20:14 to 20:24, before the export:** the two sessions of `knot-check.ps1`
    (the Tarantula of 2024-10-02 and the Helix of 2025-08-09) from the build frozen for these arms (`bh-bin12`,
    main at "Black point tests: the re-run is shown to skip its sessions ..." (#2377)). Without the flag 1,602 of
    its 1,603 files are `bh-bin11`'s bytes, all 1,600 tiles among them; the one that differs is
    `degradations.jsonl`, whose rows now carry `StretchBlackPointSigma` (null here). So on these sessions R4h's
    data is `bh-bin11`'s, and differs from R4d's by the knot fix alone. With `--black-point-sigma 40` all 1,600
    image files differ and every row says 40.
  - **R4i, the arm (synth33):** the same line with `--black-point-sigma 40`, and 40 sigma in its contract, so the
    product stretches a frame for it as its tiles were stretched. One change from R4h.
  - **Why 40.** The session's choice, not a measured optimum: the store's darkest pixels sit 8 to 228 sigma under
    their sky with a median of 34, and 40 is the depth nearest that median that has been read. The reads at
    inference cannot say which depth a model trained at one depth wants; they were read on a model trained on all
    of them.
  - **The training is read first,** by the rule registered for R4f's rerun: the median of the held-out window means
    over the 60,000 steps after the first halving, over the median from step 30,000 to that halving. At 0.75 or over
    with no new best after the first halving the run has stalled, and its rows are a stalled run's and not the
    target's.
  - **The question, on the models AS TRAINED, R4i against R4h** (the bright-star list's 26 rows and their sheets,
    the share of StarXTerminator's take by ring, as in the 40 sigma read): (1) the two Pleiades nights read alike:
    over the eight Pleiads the widest gap between the nights in the four rings from 8 to 80 px, leaving out a ring
    where StarXTerminator takes under 3 sigma, is at most 25 points (R4d as it is: 113; R4d with the depth set at
    inference: 15 to 20); AND (2) the innermost ring is taken under 80 percent on at most 4 of the 26 rows and on at
    least 3 fewer than on R4h (R4d as it is: 7; at 40 sigma at inference 7, at 60 sigma 2).
  - **Guards, on R4i:** every row of the 40 sigma read's table at its bar, read as trained or through the chain as
    that table reads it (the chain is core protection and the dig-fill as shipped): the Tarantula's crowded flat at
    least 90.3 and Sagittarius's 96.3; Andromeda's dark flat at least 95 with at most 3 % dug; the Tarantula's dark
    flat at least 91; the glow's green median within 450 on Andromeda and 500 on the Tarantula; M31's nucleus taken
    under 10 %; purity at most 1.0; the light taken from the Tarantula field at most 83 and 84 %; Gaia BP 17-19 with
    the level set aside at least 89.7; light added on each master's nebula at most 1.0 %; the brightest tenth of
    the Tarantula's peaks standing on at least 11 of 77; faint stars left on the Lagoon's core at most 37 % and on
    30 Doradus at most 27 %; the Lagoon's broad light taken at most 2.67. Those bars were set from R4d. A row R4h
    misses too is the exporter's and not the black point's: it is reported, and R4i is then held on that row to
    a value worse than R4h's by no more than the allowance the bar gave R4d (the gap between the bar and R4d's own
    figure in that table). For an at-least row that is R4h's value less the allowance; for an at-most row (dug,
    glow median, M31's nucleus taken, purity, light taken, light added, faint stars left, broad light taken) it is R4h's value plus it
    (on the Lagoon's faint stars 37 against R4d's 29 is 8 points).
  - **Kill:** a guard fails on R4i, the pair having trained alike and neither having stalled. **Cleared:** both
    halves of the question with every guard held, on the same terms. A stalled arm (the rule above), or a pair
    that does not both train as the others, is neither killed nor cleared: its rows are reported, as R4f's rerun
    was. Anything else is neither, and the rows say which half; that includes a ratio between 0.60 and 0.75, or
    0.75 or over with a new best, which the R4f rerun rule calls neither.
  - **R4h's own read:** every row above beside R4d's, as trained and through the chain. It has no bar of its own;
    it is the baseline R4i is read against, and it says what the exporter's changes did to R4d's rows.
  - **What a frame whose noise is no longer its own does (#2341), reported for R4i, deciding nothing about the
    arm.** The depth's black point is 40 times the frame's sky noise under its sky, so on a denoised frame it lies
    close under the sky and dark structure can fall beneath it. Each of the three scoring masters, smoothed by a
    Gaussian of 1.5 px (which cuts white noise to about a fifth), goes through R4i as trained: the nebula guards
    above (peaks standing, light added, broad light taken), and the sheets. Beside the share of covered pixels (the master's canvas ring, zeros or NaN, left out)
    under the black point of the smoothed copy goes the share under median minus 40 sigma at the frame's own noise
    of the unsmoothed master, so the figure has something to be read against, and where those pixels sit (lanes,
    or holes and dead pixels). A guard failing there does not kill R4i; it says the guard #2341 asks for is needed
    before any model with a depth ships.
  - **Reported, no bar:** every row for both models as trained and with each post step as its own column; the
    best step's weights (`.best.pt`) beside the last step's; all 26 ring rows; the colour of Merope's nebula on
    both nights. The 1:1 sheets are looked at before any row is read.
  - **Prediction.** R4h: every row within a point of R4d's, the peaks standing within 5 of R4d's 17. R4i: the
    nights' gap falls to 10 to 25 points, so the first half is cleared; the innermost ring is taken under 80
    percent on 3 to 5 rows (the bar is at most 4, so the range straddles it: 3 and 4 clear, 5 misses), counted by
    the innermost ring, the measure the bar counts: Antares' 2026-04-21 and 2024-05-28
    (56 and 71 at 40 sigma in the table above) among them and 2023-05-15 (81) not, though the sheets show the disc
    left on all three sessions. The peaks guard
    is the one most at risk, since the Tarantula's own black point is 10 sigma and at 40 its knots stand brighter,
    and it is predicted to hold at 11 to 20 of 77 because the drawn knots are stretched the same way.
  - **Nothing is adopted by these reads.** A cleared question goes to the owner with the numbers, and a second seed
    is owed before any adoption.
  - **How this text came to be, and what of it came after the export began.** The first text was pushed at
    20:10:54 on 2026-11-21. Its review found that the trainer copy it named cannot write a depth, and four smaller
    points; the review's fixer rewrote those parts at 20:19:34 (the trainer copy; both arms on a T4 capped at 36
    hours, where the first text named no hardware at all and a draft that was never pushed had R4i on the GTX
    1070; the contract check; the allowance on the at-most rows; what a stalled or uneven pair is; the count of
    Antares' rows; #2341's report by covered pixels), and the session added the preflight's result and a first form
    of this note at 20:27:58. Up to there nothing but the two-session preflight had been exported. **R4i's export
    began at 20:28:41.** Four rounds came after that, all before any cache, training run or score existed: the
    fixer's third at 20:44:20 (the pointer to the Kill bullet, M31's nucleus on the at-most list, the direction
    taken out of the prediction's 3 to 5 rows, the hunk made by hand, the read before the launch), the
    session's fourth at 20:56:35 (the read before the launch said per arm, the result of the trainer copy's
    check, this note retitled), the session's fifth at 21:17:06, of wording only (the
    prediction's 3 to 5 rows said to straddle the bar, where the text had them end at the bar's edge though 5 rows
    miss a bar of at most 4; which hunk was written by hand, and that the trainer copy's check does not reach it;
    the lead giving the first text's time in place of "before any export or score of either"), and a sixth, the
    push that carries this sentence, which gives the fifth its time and changes nothing else.
    The question, its bars and the guards' bars are as first pushed at 20:10:54; no round changed them.
- **The scorecard's blind side, found by the owner's eye (2026-11-19, #2245).** Asked for renders after weeks of
  rows alone, the owner pointed out within minutes, on the Lagoon, 30 Doradus and M31: flat blotches with floor3
  (#2243; their cause and floor4 are under "The dig floor inside bright nebula" below), bright stars back inside a core with core protection (#2242), faint residue stars as trained, and, once
  a row could show it, nebula taken with the stars. His words: "im a bit concerned that I have to point these out,
  it means that either training or scoring is not up to the task". It is the scoring.
  - **Why the rows passed them.** The star rows ask whether a star's site is FLAT; a flat patch inside structured
    nebula is flat, so the dig floor's fill counts as a success (it takes R4c's Andromeda dark flat from 47.2 to
    98.2). The nucleus row measures the core's light kept, and protection scores 0.0 % taken by putting the input
    back, stars included. The stars over bright background are a class of the detailed log only, pooled over the
    whole frame, never in the one-line row and never under a bar. Nothing read the nebula itself.
  - **The rows added, on every score from now on** (`nebula_rows.py`, run by `score_whole.py` on each master and
    carried in its row; the reference is the INPUT, not another remover). On the master's bright extended light
    (its large-scale map 20 sigma over the sky): **added**, the share of it where the starless image stands over
    the input by 3 sigma of a 3 px mean, which no removal can justify; **broad taken**, the 9 px median of input
    less starless, averaged in sigma; **peaks standing**, the share of the input's compact peaks over 14 pixel
    sigmas in their band that keep half their height, and of the brightest tenth of them. (The bar was first
    written as 50 sigma, the band's noise having been taken for 0.28 of the pixel's. For white noise it is 0.257,
    which would make the bar 54.5 of the band's own sigmas; on these masters, whose noise is correlated and whose
    band holds faint stars too, the band scatters by 1.46, 1.30 and 0.65 pixel sigmas, so the bar is 9.6, 10.8 and
    21.6 of them. Its one true name is 14 pixel sigmas, and it has not moved, #2247, #2262.) The tenth is few
    peaks, 77 on the Tarantula and 18 on the Lagoon, so it is given as a count: one peak of the Lagoon is 5.6 %.
  - **Every scored folder on disk re-read, 68 of them, no training** (Andromeda / Tarantula / Sagittarius):

    | image | Andromeda dug % | added % | broad taken | brightest tenth of the peaks standing, of 77 on the Tarantula / of 18 on the Lagoon |
    |---|---|---|---|---|
    | StarXTerminator | 1.4 | 0.0 / 0.0 / 0.0 | 2.88 / 5.99 / 0.42 | 17 / 1 |
    | R3v seed 0, as trained | 22.4 | 0.0 / 0.1 / 0.1 | 2.73 / 3.56 / 0.56 | 19 / 1 |
    | R3v seed 0 + floor3 | 1.0 | 0.6 / 4.2 / 4.0 | 2.78 / 3.68 / 0.63 | 19 / 1 |
    | R3z, as trained | 25.8 | 0.0 / 0.0 / 0.0 | 2.89 / 4.35 / 2.79 | 16 / 0 |
    | R4a seed 0, seed 1, as trained | 34.8, 28.4 | 0.0 / 0.0 to 0.3 / 0.0 | 3.20 / 4.24 / 1.80, 2.87 / 4.19 / 1.49 | 18 / 1, 18 / 0 |
    | R4b, as trained | 24.4 | 0.0 / 0.1 / 0.0 | 3.01 / 5.58 / 3.50 | 12 / 0 |
    | R4c, as trained | 45.9 | 0.0 / 0.0 / 0.0 | 3.04 / 5.39 / 2.64 | 11 / 0 |
    | R4c + floor3 | 0.9 | 0.5 / 2.4 / 3.0 | 3.10 / 5.89 / 2.82 | 11 / 0 |
    | R4c + core protection | 45.9 | 0.0 / 0.0 / 0.0 | 2.83 / 4.35 / 1.64 | 21 / 3 |
    | R4d, as trained | 19.5 | 0.0 / 0.0 / 0.0 | 2.88 / 5.86 / 2.67 | 17 / 0 |
    | R4d + core protection | 19.5 | 0.0 / 0.0 / 0.0 | 2.81 / 4.87 / 1.73 | 21 / 3 |

  - **What the re-read says.** (1) **floor3 puts light in:** every floored plate is brighter than its input on
    2.4 to 4.6 % of the Tarantula's nebula, 2.5 to 4.3 % of the Lagoon's and 0.5 to 0.8 % of M31's (the 23
    floored folders on disk on 2026-11-20; the highest are R3j's and R4a seed 0's, which the table does not carry).
    StarXTerminator and protection alone add nothing, and the as-trained rows of the table read 0.0 to 0.1; over
    all 33 unfloored folders seven models add a little of their own, 0.2 to 0.7 % on the Tarantula, and R3m 1.6 %
    on the Lagoon. The dark rows that stood level with StarXTerminator
    (Andromeda dark flat 97.5 to 98.5) were bought with that fill; as trained every model of ours digs under 19.5
    to 56 % of Andromeda's bright dark-sky stars (R4d to R3i, the second not in the table) against
    StarXTerminator's 1.4, and no data recipe since R2w has
    closed that. (2) **Protection puts stars back:** with it every model reads the same 21 of 77 on the
    Tarantula and 3 of 18 on the Lagoon, since it is the input that returns. The Lagoon shows it: 0 or 1 of 18
    without protection, StarXTerminator's 1. The Tarantula's column does not show it by itself: 21 is a rise of 2
    to 10 peaks over the same models as trained (11 to 19), and StarXTerminator leaves 17. (3) **The models since
    R3z take nebula:** on the Lagoon the broad light taken is 0.55 to 1.11 for R3v, R3w and R3y (five runs) and
    1.49 to 3.50 for R3z, R4a, R4b, R4c and R4d (six runs), against StarXTerminator's 0.42; the picture
    (`r4_taken.py`) shows the Lagoon's bright core in what R4c and R4d removed. Seeds of one recipe differ by up
    to 1.3 on this row (R3s 1.18 and 2.44), so this is read by family, and which change brought it (R3z's
    `--halo-over-plate` is the first of that family) is being checked on the training pairs. R4c's read above
    says "every guard held"; that is true of its registered guards and none of them read the nebula.
  - **What the rows do not do.** The broad row counts a real halo too, and the skirt of a bright star rightly
    taken (a 9 px median kills a faint star only: on a synthetic nebula a clean removal of 40 stars up to 3000
    sigma reads 0.08, `test_nebula_rows.py`). On the Tarantula StarXTerminator takes the
    most (5.99), so the row is read beside a judge's figure and with a picture, never alone. **Faint stars left
    on bright nebula are not read by anything yet:** peaks under the bar cannot be told from nebula knots this
    way, and the detector's list holds too few of them there (two in the Lagoon's brightest tenth). Until a row
    does, that is an eye check on the comparison page. Two attempts that did not discriminate are on #2245.
  - **The rule from here.** Every arm's registration carries nebula guards (added, broad taken on the Lagoon,
    peaks standing) beside its star rows; a model is read AS TRAINED, with each post step as a column of its own;
    and every read comes with 1:1 renders of the cores and bright nebula, looked at before the rows are quoted.
- **The zero-flux case (#1244 point 3), 2026-11-19: a starless plate through the remover comes back as the plate,
  except where the plate still holds stars.** `zero_flux.py`: a session's stored plate through `image remove-stars`
  as trained, the output less the plate over the middle three quarters of the frame, in the master's recorded sigma
  (the manifest's value times the exporter's unit divisor, the larger of 1 and the master's DATAMAX: 1.0255, 1.0362
  and 1.0135 on the three unit-scale masters, 84,827 on M42's). No level is rescaled: the plate's median over the
  output's is 0.9998 to 1.0006 in every channel of every run.
  R3v and R4b (seed 0 each) on Tempel, Lobster and the SMC: no bias over 0.05 sigma, an rms of 0.12 to 0.44, 0.07 to
  0.74 % of the pixels lowered by over a sigma and at most 0.01 % raised. On M42 (ASI533MC Pro, 2022-12-03) the rms is 9 to 17
  sigma: the builder left 1,432 catalogued stars unsubtracted there, and 98 to 99 % of the light the models take lies
  within 40 px of one of them (`zero_flux_where.py`), most of it in one region of about 17 thousand pixels at the
  nebula's core; whether
  that region loses nebula too this read cannot tell (protection was off). A plate is a zero-flux input only where
  it is starless; the synthetic clean tile an export writes beside every draw is the cleaner one and is not read yet.

- **`--bright-measured` (#2192, PR #2193) against real stars, 2026-11-17: it reaches M42's kind and does not move HIP
  80609's or Lobster's.** The eight validation sessions exported as item 5's plate-plain arm with the one option, from
  `bh-bin9` (main with PR #2193; `dig-2x2/plate-bright-measured`), read by `flank_cmp.py` with the drawn stars kept to
  those marked Measured. The option measures 1.1 to 4.6 % of a session's drawn stars (Lobster 1,801 of 60,902, M42 248
  of 5,420, HIP 80609 972 of 58,138, SMC 115 of 10,341) and 22 to 47 % of the brightest twentieth of the unsaturated
  ones, so the medians over all drawn stars do not move. The measured stars alone, the ring at 1.6 to 2.5 px over the
  core, peak 0.25 to 0.5, R / G / B, against real and against the field profile's draw:

| session | real | measured | field profile |
|---|---|---|---|
| M42 | 0.217 / 0.238 / 0.169 | 0.206 / 0.218 / 0.156 | 0.249 / 0.250 / 0.209 |
| HIP 80609 | 0.172 / 0.215 / 0.138 | 0.201 / 0.266 / 0.176 | 0.208 / 0.270 / 0.176 |
| Lobster | 0.191 / 0.169 / 0.199 | 0.243 / 0.222 / 0.222 | 0.205 / 0.181 / 0.206 |

  On M42 the measured stars sit 5 to 8 % under real where the field profile sits 5 to 24 % over (blue, the channel
  the model digs, 0.156 against 0.169 where it was 0.209), and at 2.5 to 3.5 px they hold 0.089 / 0.094 / 0.043
  against real's 0.107 / 0.119 / 0.061, a fifth to a third under. On HIP 80609 the measured stars are as fat as the
  field profile's, and on Lobster fatter (35 stars), so the option is not a fix for the flank of item 5 there; why a
  star drawn from its own master reads fatter than the real stars of its bin on those two is not found (the exporter's
  warp and the bin's selection are the candidates).
- **R4b, pre-registered 2026-11-17 before any score. One change from R3z's data: the unsaturated bright stars drawn as
  measured.** synth27 = synth25's line plus `--bright-measured` (`export-synth.ps1 -Name synth27 -BetaDraw 0.3,1.3,6,3
  -Extra --saturated-own-share,--saturated-measured,--halo-over-plate,--bright-measured`), from `bh-bin9`, a second
  named difference (synth25 came from `bh-bin8`); R3v's model and recipe exactly, seed 0, no cluster scenes, a Batch
  job that removes itself (tag `r4baz`).
  - **The question, on the model AS TRAINED, against R3z as trained:** M42's stars at 100-1000 sigma dug at most 26.0
    (R3z 39.0, R3v 42.6: a third off) with clean at least 68 (58.1).
  - **Guards, with floor3 and `--protect-cores`, R4a's:** by count clean at least 98.5 at 100-1000 sigma and 98 at
    1000 and over; M42 100-1000 at least 88; SMC 20-100 and 100-1000 at least 97; Andromeda dark flat at least 95 and
    dug at most 3; Tarantula dark flat at least 91; the glow's green median within 450 of zero on Andromeda and within
    500 on the Tarantula; the M31 nucleus under 10 % in every channel with protection; purity at most 1.0 %. As
    trained: HIP 80609 100-1000 clean at least 97.1 and Lobster at least 97.5 (R3z 99.1 and 99.5 less two points).
  - **Kill:** a guard fails, or M42's dug as trained stays over 33.0 (not cut by 6 points). Between 26.0 and 33.0 with
    every guard held it neither passes nor is killed: no seed 1, the rows are reported, the owner decides.
  - **Reported, no bar:** every row with floor3; M42's ring profile at real sites (`site_core_ch.py`) beside R3v's;
    the crowded rows; saturated by count; the nucleus without protection; the twin inside the rim.
  - **Prediction:** M42's dug falls to about 30, inside the band that neither passes nor kills, since the measured
    stars are a twentieth of the drawn ones and still a fifth under real past 2.5 px; HIP 80609 and Lobster do not
    move; the glow and the guards hold as R3z's did.
  - **If the question is cleared:** seed 1, and the cluster scenes of R4a join if R4a's read allows. Nothing is
    adopted; adoption is the owner's.
  - **Read 2026-11-18, seed 0 (`r4b_synth27_s0`, 240,000 steps): the question is CLEARED, narrowly, and every guard
    holds.** The prediction (about 30, neither pass nor kill) was wrong on the good side.

    | row | bar | R4b | R3z | R3v |
    |---|---|---|---|---|
    | M42 100-1000 as trained, dug (136 stars) | at most 26.0 | 25.0 | 39.0 | 42.6 |
    | M42 100-1000 as trained, clean | at least 68 | 73.5 | 58.1 | 56.6 |
    | by count clean, 100-1000 / 1000 and over, floor3 | 98.5 / 98 | 99.3 / 100.0 | | 99.4 / 100.0 |
    | M42 100-1000, floor3 | at least 88 | 94.1 | | 94.9 |
    | SMC 20-100 / 100-1000, floor3 | at least 97 | 98.9 / 100.0 | | 98.9 / 100.0 |
    | Andromeda dark flat / dug, floor3 | at least 95 / at most 3 | 98.4 / 0.7 | | |
    | Tarantula dark flat, floor3 | at least 91 | 94.5 | | |
    | glow green median, Andromeda / Tarantula, floor3 | within 450 / 500 | +32.1 / +192.2 | | |
    | M31 nucleus with protection | under 10 % | 0.0 in every channel | | |
    | purity | at most 1.0 % | 0.9 | | |
    | HIP 80609 / Lobster 100-1000 as trained, clean | at least 97.1 / 97.5 | 98.6 / 99.5 | 99.1 / 99.5 | 97.1 / 99.3 |

    The margin on the question is two stars: 34 of 136 dug, 35 (25.7 %) would still pass and 36 (26.5 %) would have
    missed the bar. One seed does not settle
    a margin that size, which is what the registered seed 1 is for.
  - **Reported, no bar.** As trained the brighter bands moved the same way: at 1000 sigma and over clean 89.8 by wing
    (R3v 74.2), M42's 11 stars dug 27.3 % (R3v 81.8), HIP 80609's 69 dug 4.3 % (18.8); saturated M42 dug 64.3 %
    (85.7). One row moved the wrong way as trained: the SMC at 100-1000 sigma (59 stars) clean 81.4 dug 16.9, where
    R3v has 89.8 and 8.5; with floor3 it is 100.0, so the guard holds, but the measured stars cost the SMC as trained.
    Crowded rows with floor3: Tarantula crowded 90.0, BP 17-19 94.7, Sagittarius 96.1, so the crowded gap is where
    R3z left it, as expected of an arm that changes only the bright stars. M42's ring profile at real sites, blue,
    100 sigma and over (1,560 sites, in the star's sigma, radii 0 / 0.8 / 1.6 / 2.5 / 3.5 px): -0.47 / +0.58 / -1.47
    / -0.68 / -0.21; the dig is still the ring at 1.6 to 2.5 px. The twin inside the rim: rms in mask 2.35 as trained
    (R3v 1.92), 1.06 with floor3.
  - **Next by the registration: seed 1.** Not launched: the owner's word of 2026-11-18 is that the open ends of #2207
    are worked down before another day-long run. The cluster scenes do not join (R4a was killed).

Every script here that runs the remover (`transplant.py`, `site_remove.py`, `crowd_texture.py`, `wb_invariance.py`,
`flatten_invariance.py`) gives the model AS TRAINED: it passes `--no-fill-digs` and `--no-protect-cores` where the
build has them (the rows in this plan were made with a build from before both steps). No model reads another's cached
plate: `site_remove.py` keeps them apart by folder (`SITE_OUT`, one per model, set by `site-run.sh`), the others by
naming the output for the model as well as the session. `site_core.py` and `site_core_ch.py` read either: `SITE_OUT`'s
folder, or transplant.py's with `TIANWEN_SR_MODEL` to pick the model.

Files (DE1): R3y's Q1 export is made by `training/star-removal/flank-q1-export.ps1` (the arm `synth-draw12` under
`dig-2x2/`) and the synth22 shares over beta 6 by `beta_over_share.py`; item 5's instrument is `training/star-removal/flank_cmp.py` and the exports behind its beta rows are made by
`flank-exports.ps1` (the plate-plain arm of `dig-2x2.ps1` plus `--star-beta 8` and `--star-beta 20`, outputs `plate-beta8` and
`plate-beta20` under `dig-2x2/`); `flank_cmp.py` takes `TIANWEN_DATASET` and `TIANWEN_REAL_VAL_ROOT` (the folder holding
`r2-real-val`), and its console output is the item 5 table. Item 5's inputs: `r2-real-val`, `dig-2x2/plate-plain`,
`dig-2x2/plate-beta8` and `dig-2x2/plate-beta20`. The four-measurements scripts are in `training/star-removal/` (`train_dig.py`, `export_dig.py`, `export_dig2.py`,
`twin_err_inner.py`, `dig-2x2.ps1`, `site_core.py`, `site_core_ch.py`, `transplant.py`. Paths: `transplant.py`, `site_core.py` and `site_core_ch.py` read `TIANWEN_DATASET` and
`TIANWEN_TRANSPLANT` (and `transplant.py` `TIANWEN_CLI`, the tianwen.exe) with DE1 defaults; `twin_err_inner.py` reads
`TIANWEN_SR_SCRATCH`; `dig-2x2.ps1` has its D: and C: paths written in, as DE1 only; `export_dig.py`, `export_dig2.py`
(the `RIM` variant of it) and `train_dig.py` take theirs as arguments), their outputs in `C:/temp/e2/explore/` (`dig-2x2-r3v_synth22_s0.pt.txt` and `_s1`, `transplant.txt`,
`r3w/` for R3w's reads); and `C:/temp/e2/explore/` also holds (`check3-floor-real.txt`, `check3-floor-twin.txt`, `check3-floor2-real.txt`,
`check3-floor2-twin.txt`, `check3-floor2-arms.txt`, the sheets `r3v-floor-m42.png` and `saturated-stars.png`); the outputs are
`f2-eval-master/<model>+floor`, `+fill`, `+floor2`, `+floor3` and `f2-whole-master/w-r3v_synth22_s0+floor`, `+fill`, `+floor2`, `+floor3`;
floor3's reads are `check3-floor3-real.txt`, `check3-floor3-twin.txt`, `check3-floor3-r3s.txt` and the sheets `saturated-smc-m42.png` and `caps.png`.

#### The dig floor inside bright nebula: floor4, and what it leaves (2026-11-20)

**Issues:** #2243 (floor3 leaves flat patches where it fills inside bright nebula: the subject here), #2252 (a square
hole where a bright star sits on a bump of nebula, which floor4 does not reach).

The flat patches the owner marked in 30 Doradus and the Lagoon are floor3's, and they have one cause: inside a core
the step does not know the sky it fills to. The instruments named below that are not under `training/star-removal`
(`floor_diag.py`, `floor_depth.py`, `floor_spread.py`, `floor4.py`, `floor4_read.py`, `floor4_look.py`,
`floor4_spots_disk.py`, `digfill-bench.ps1`) are working files in `C:/temp/e2/explore` on the development machine,
and the sheets are in `D:/temp/tianwen-scratch/r4-look`.

- **What floor3 does there** (`floor_diag.py`; R4c seed 0, the two 220 px core crops). The set of "removed star"
  pixels is one blob, 57.5 % of the 30 Doradus crop and 31.3 % of the Lagoon's, since the model takes broad light in
  a core and the stars are dense. The sky the floor fills to is then spread in across the blob from its far edges
  (12.2 % and 6.6 % of the crops take the far, 12 px Gaussian), and it is flat where the nebula is not. Two things
  follow. **A flat lift:** a pixel 2.5 sigma under that sky is set to it, and in a core that is real dark
  structure; 72 to 89 % of the pixels floor3 lifts by over a sigma there are such pinned pixels, and 2.4 to 4.5 %
  of the crop ends over the INPUT by a sigma. **A square lowering:** where the nebula stands over that far sky the
  lowering runs to its cap, and the cap (a twentieth of what the model took, the largest within 2 px) has the
  square footprint of a bright star's core. Those are the grey patches and the row of squares.
- **Depth inside the set does not separate it** (`floor_depth.py`). On dark sky 93 to 99 % of the set and 87 to
  98 % of the lift lie within 6 px of the set's edge, but in nebula so do 59 to 90 % of the lift and 84 to 99 % of
  the pixels that end over the input.
- **The scatter of the sky does** (`floor_spread.py`): the scatter of the plate OUTSIDE the set under the very
  Gaussian that makes the sky (the near one, or the far one where the near one reaches too little), in pixel noise,
  the largest of the channels. R4c seed 0, Andromeda / Tarantula / Sagittarius:

  | where floor3 acts | median | 95th percentile | 99th percentile |
  |---|---|---|---|
  | dark sky, every pixel of the set | 1.14 / 1.02 / 0.95 | 1.40 / 1.30 / 1.14 | 1.73 / 1.93 / 1.83 |
  | nebula, pixels it pins | 2.03 / 3.71 / 5.33 | 14.1 / 9.1 / 23.4 | |
  | nebula, pixels left over the input | 3.50 / 3.97 / 5.15 | 17.6 / 9.5 / 22.6 | |

- **floor4** (`whole_floor.py` mode `floor4`, `dig_floor.py --fade 2 4 --tag floor4`, and `DigFill` in the product):
  floor3 with its whole correction, lift and lowering, multiplied by a trust that is 1 up to a scatter of 2 sigmas,
  0 from 4, and linear between. The scatter is taken in float64: it is the mean of squares less the squared mean,
  and in float32 a level of a few thousand sigmas reads as no scatter at all (the masters lie at 77 to 830 sigma,
  where float32 is exact at the median and within 7 % at the 1st and 99th percentile, so the exploration below
  stands).
- **Explored on R4c seed 0, the model the thresholds were read on, so NOT a held-out read.** Three fade ranges
  (prototype `floor4.py`, scatter in float32). Whole masters:

  | R4c seed 0 | as trained | floor3 | fade 1.25 to 2 | fade 1.5 to 3 | fade 2 to 4 |
  |---|---|---|---|---|---|
  | Andromeda dark flat / dug | 47.2 / 45.9 | 98.2 / 0.9 | 98.1 / 1.0 | 98.2 / 0.9 | 98.2 / 0.9 |
  | Tarantula dark flat / left | 64.9 / 33.1 | 95.5 / 3.6 | 95.3 / 3.8 | 95.5 / 3.7 | 95.5 / 3.6 |
  | Tarantula crowded / Sagittarius crowded | 85.2 / 97.2 | 90.4 / 97.9 | 90.3 / 97.9 | 90.3 / 97.9 | 90.4 / 97.9 |
  | M31 nucleus taken, % | 24.4 / 37.0 / 29.4 | 27.5 / 40.2 / 33.1 | as trained | as trained | as trained |
  | nebula, light added, % | 0.0 / 0.0 / 0.0 | 0.5 / 2.4 / 3.0 | 0.0 / 0.2 / 0.0 | 0.1 / 0.4 / 0.1 | 0.1 / 0.8 / 0.3 |
  | nebula, broad light taken | 3.04 / 5.39 / 2.64 | 3.10 / 5.89 / 2.82 | 3.07 / 5.55 / 2.63 | 3.07 / 5.58 / 2.63 | 3.07 / 5.63 / 2.63 |

  Real stars on the validation tiles, by star count (`real_star_check3.py --sessions`):

  | R4c seed 0 | as trained | floor3 | fade 1.5 to 3 | fade 2 to 4 | SXT |
  |---|---|---|---|---|---|
  | clean, 20-100 / 100-1000 / 1000 and over / saturated | 98.8 / 94.9 / 86.7 / 81.1 | 99.4 / 99.3 / 100.0 / 91.6 | 99.4 / 99.1 / 100.0 / 90.5 | 99.4 / 99.3 / 100.0 / 91.6 | 98.8 / 98.3 / 98.4 / 89.4 |
  | M42 100-1000, clean / dug | 61.0 / 36.8 | 93.4 / 4.4 | 92.6 / 6.6 | 94.1 / 5.1 | 91.9 / 6.6 |
  | saturated, left | 5.3 | 2.1 | 5.3 | 4.2 | 8.5 |

  The fill of real digs on M42's nebula survives the fade: the dug stars there sit where the sky is smooth. **2 to
  4 is taken:** it leaves floor3 whole up to the 99th percentile of dark sky and is level with floor3 on the four
  clean rows by count (M42's dug rises by one star of 136 and the saturated light left by two of 95), at the price
  of 0.8 % of the Tarantula's nebula still lit against 0.4.
- **Looked at** (`D:/temp/tianwen-scratch/r4-look`). `floor4-tarantula-*` and `floor4-sag-*`, the two cores at 3x
  with what each version changed: the grey patches of 30 Doradus and the Lagoon's squares and round patch are gone
  with every fade range. `floor4-spots-floor4b-*`, the six strongest corrections left inside each master's nebula at
  8x: dig fills, and on the Tarantula three holes (below).
- **What it leaves, #2252: a square hole where a bright star sits on a bump of nebula whose surroundings are
  smooth.** The Tarantula at (1601, 710), 8 sigma deep, at (1351, 1408) and at (1568, 699); the sky's scatter there
  is 1.6 to 2.8, so the fade does not reach them. Three rules that stop the lowering at a pedestal (the height the
  neighbouring pixels of the set are left standing at) were tried on R4c seed 0 and dropped. Carried by every pixel
  the cap bites: the holes go and the twin error falls (1.044 against floor3's 1.055), but Tarantula dark flat
  falls from 95.5 to 89.3 and left rises from 3.6 to 10.1. Carried only by light standing over the sky by more
  than the model took: the rows hold (95.4 / 3.8) and the holes stay. The first rule over 12 px with half its
  weight as support: 92.3 / 7.2, holes fainter. The rule that removes the holes also stops the floor lowering the
  residue of dark-sky stars whose wings stand a little over the sky. What does tell a knot from that residue, and
  the step that uses it, is the next section (floor5).
- **Two limits of the measure.** The noise reading (the input less its 5 px median) takes fine structure for
  noise: a 12 sigma ripple of period 16 to 20 px reads the noise 1.75 times too high, so the scatter in noise units
  is low exactly where structure is fine. And it is one number per pixel for both the lift and the lowering.
- **The product is the script, bit for bit.** `dig_fill_parity.py` on R4c seed 0: `tianwen image fill-digs` against
  `whole_floor.py floor4`, no value different on Andromeda, the Tarantula or the mono Sagittarius master (72.6
  million values in seven planes, of which the step changed 2.6 to 5.2 million a plane).
- **Pre-registered for R3v seed 0 and R4d seed 0 (2026-11-20; no floor4 score of either exists, and neither was
  looked at while the thresholds were set).** floor4 exactly as above (`whole_floor.py <w-tag> floor4`,
  `dig_floor.py <model> floor --pin 2.5 --cap-up 2 --cap-down 0.05 --fade 2 4 --tag floor4`), each model's own plate
  as trained, read against the SAME model's floor3 from the same run of each check. On each of the two models:
  1. **The question, nebula:** light added is at most half of floor3's on the Tarantula and on the Sagittarius
     master (floor3: R3v 4.2 / 4.0 %, R4d 3.0 / 4.2 %).
  2. **Guards, whole masters** (floor3's figure less or plus the margin): Andromeda dark flat and Tarantula dark
     flat not lower by more than 0.5; Andromeda dug and Tarantula left not higher by more than 0.5; Tarantula
     crowded, BP 17-19 and Sagittarius crowded not lower by more than 0.5; the M31 nucleus row not above the
     as-trained row in any channel.
  3. **Guards, tiles by star count:** clean at 20-100, 100-1000 and 1000 and over not lower by more than 0.3;
     saturated clean not lower by more than 2.1 (two stars of 95); M42 100-1000 dug not higher by more than 3.0
     (four stars of 136).
  4. **Guard, truth:** the twin error under the mask (`twin_err.py`, rms) not higher by more than 0.02.
  5. **Renders first:** the two cores and the six strongest corrections inside each master's nebula, looked at
     before a row is quoted.
  A guard missed on either model is reported as missed, and the fade range is not re-tuned on these two models.
  Adoption (the product's default changing from floor3 to floor4) is the owner's.
- **Read, 2026-11-20: every bar holds on both models, two of them at their limit.** The renders were looked at first
  (`floor4-look-<model>.png`, `floor4-spots-final-<model>.png`): on R3v and on R4d the grey patches of 30 Doradus and
  the Lagoon's patches and squares are gone with floor4, and the strongest corrections left inside nebula are dig
  fills, the holes of #2252, and one dig in structure that ends part filled (below). The rows, each model's floor3
  and floor4 from one run of each check (`floor4_read.py` checks every bar):

  | | R3v seed 0: as trained | floor3 | floor4 | R4d seed 0: as trained | floor3 | floor4 |
  |---|---|---|---|---|---|---|
  | nebula, light added, %, Tarantula / Sagittarius (the question: at most half of floor3's) | 0.1 / 0.1 | 4.2 / 4.0 | **1.2 / 0.3** | 0.0 / 0.0 | 3.0 / 4.2 | **0.8 / 0.3** |
  | Andromeda dark flat / dug | 69.5 / 22.4 | 97.6 / 1.0 | 97.6 / 1.0 | 69.4 / 19.5 | 98.5 / 0.7 | 98.5 / 0.7 |
  | Tarantula dark flat / left | 71.9 / 16.0 | 93.9 / 4.9 | 93.8 / 5.0 | 80.3 / 14.3 | 96.5 / 2.7 | 96.5 / 2.7 |
  | Tarantula crowded / BP 17-19 / Sagittarius crowded | 83.8 / 92.4 / 93.7 | 88.7 / 92.9 / 95.5 | 88.7 / 92.9 / 95.5 | 86.7 / 95.4 / 96.4 | 91.2 / 96.0 / 97.0 | 91.3 / 96.0 / 97.0 |
  | M31 nucleus taken, % | -1.0 / 5.1 / 1.2 | -1.0 / 6.0 / 1.6 | as trained | 2.8 / 6.6 / 9.7 | 3.0 / 7.5 / 11.2 | as trained |
  | tiles by count, clean, 20-100 / 100-1000 / 1000 and over | 98.9 / 94.9 / 87.2 | 99.3 / 99.4 / 100.0 | 99.3 / 99.2 / 100.0 | 98.8 / 95.6 / 91.5 | 99.3 / 99.3 / 100.0 | 99.3 / 99.2 / 100.0 |
  | tiles by count, saturated clean (bar: at most 2.1 lower) | 78.9 | 91.6 | 89.5 | 85.3 | 93.7 | 93.7 |
  | M42 100-1000, dug (bar: at most 3.0 higher) | 42.6 | 3.7 | 5.9 | 34.6 | 2.9 | 5.9 |
  | twin error under the mask, rms (bar: at most 0.02 higher) | 1.920 | 0.989 | 0.992 | 2.260 | 1.080 | 1.073 |

  StarXTerminator on the same tiles: clean 98.8 / 98.3 / 98.4 / 89.4, M42 100-1000 dug 6.6.
  - **At their limit:** R3v's saturated clean falls by exactly two stars of 95 (and its saturated dug rises from 5.3
    to 8.4, three stars; not a bar), and R4d's M42 dug rises by exactly four stars of 136. Both are stars inside
    M42's nebula, where the fade holds the fill back. So floor4 gives up a few fills at bright stars inside nebula
    for the patches; on M42 it stands at or under StarXTerminator's dug figure.
  - **Also on R4c, the product's exact step** (`whole_floor.py floor4`, scatter in float64): the rows of the
    exploration table's "fade 2 to 4" column to the digit, and the twin error 1.049 against floor3's 1.055.
  - **A dig in structure ends part filled.** The Tarantula at (1363, 1445): under a bright group the model dug a
    hole of over 20 sigma into structured nebula. floor3 filled it with a flat wedge; floor4 fills only the side
    where the sky is trusted, and the filled band ends in a stepped edge (5.5 sigma between neighbours on R3v; R4d's
    plate shows a stepped band at the same place). Where the model digs into structure no fill is right; this is
    recorded on #2252.
  - **Cost in memory and time, measured after the merge (#2254).** As merged, floor4 held whole planes in double
    for the scatter and doubled the step's peak memory. The scatter is now summed row by row, and only over runs of
    the pixels of removed stars (the far Gaussian answers for 0.2 to 2.6 % of a frame's pixels, scattered over 22 to
    98 % of its rows), which holds no whole plane in double and is the same sum in the same order: the product is
    still the script bit for bit on the three masters, and a test compares the trust with a plain whole-frame
    computation on a frame narrower than the far Gaussian reaches. `image fill-digs`, each case run alone three
    times (`digfill-bench.ps1`; peak working set the largest of the three, time the fastest):

    | frame | floor3 | floor4 as merged | floor4, scatter by runs |
    |---|---|---|---|
    | 12.2 megapixel colour (the Andromeda master) | 2,405 MB, 7.4 s | 4,545 MB, 11.1 s | 2,738 MB, 8.5 s |
    | 24.4 megapixel colour (that master joined to its mirror image) | 5,509 MB, 14.8 s | 12,393 MB, 24.0 s | 5,143 MB, 16.5 s |

#### The dig floor where the set stands far from its sky: floor5 (2026-11-20)

**Issue:** #2252 (a square hole where a bright star sits on a bump of nebula, which floor4 does not reach).

floor4 trusts the sky it corrects to when the plate outside the removed-star set is smooth there. Inside a set that is
one large blob (crowded stars, or a model that takes broad light) that sky is spread in from the blob's far edges, and
the far pixels can agree with one another without being the level under the blob. Two things floor4 still does there,
both found by looking at its plates: it **lowers a nebula knot** between the star cores to the far sky, and it **lifts
a dark globule** toward it. The instruments named below (`holes_look.py`, `floor5.py`, `floor5_diag.py`,
`floor5_squares.py`, `floor5_left.py`, `floor5-final.sh`, `floor5_read.py`, `digfill-bench5.ps1`) are working files in
`C:/temp/e2/explore` on the development machine, and the sheets are in `D:/temp/tianwen-scratch/r4-look`.

- **What the holes are** (`holes_look.py`; R4c seed 0, the Tarantula, each spot at 10x beside the input, the plate as
  trained and StarXTerminator's). At (1601, 710), (1351, 1408) and (1568, 699) the model took a star of 523, 561 and
  945 sigma and left the knot it sat on: the plate under the star stands 14.1, 9.5 and 1.2 sigma over its
  surroundings as trained (StarXTerminator 18.5, 11.1 and 22.7), and floor4 takes it to 5.6, 5.1 and -1.3, lowering
  by up to 10.9, 7.0 and 12.8 sigma. The light it lowers is the nebula's: its colour is R:G:B 3.8:1:0.6, 2.9:1:0.7 and
  4.8:1:1.1 against 1.3:1:0.9, 1.6:1:0.8 and 1.7:1:0.8 for what the model took as the star. The lowering is cut by its
  cap (a twentieth of what the model took, the largest within 2 px), per channel, so it shows as a 5 px square of
  another colour. Every star on the knot gets one; most are 2 to 3 sigma deep.
- **The other side, the fill.** At (1088, 1339) a dark globule with bright stars on it lies 3.9 sigma under its
  surroundings as trained and 4.2 on StarXTerminator's plate; floor4 lifts it to 2.7 under, a grey patch with a bright
  spot of 8 sigma. At (1363, 1445), which the floor4 section records, the hole is the model's (StarXTerminator fills
  that place) and the fill is right; its stepped edge is the trust's, and stays.
- **Size does not separate a knot from residue** (`floor5.py` variants `lf3`, `lf4`: the lowering stops at the plate's
  own floor about the pixel, its opening with a disc of 3 or 4 px). The floor stands over the sky on 58 to 84 % of the
  set and 57 to 78 % of floor4's lowering is given up, on dark sky too: Tarantula dark flat falls from 95.5 to 68.2
  and 71.7 (left 31.3 and 27.8), Andromeda dark flat to 90.8 and 94.6. The light a model leaves under a dark-sky
  star is as broad as a knot. This is the pedestal rules' failure again (floor4 section), reached another way.
- **How far the set as a whole stands from its sky does** (`floor5_diag.py`). The plate less the sky, averaged over
  the set under the Gaussian of the near sky (4 px), in pixel noise, per channel. Where floor4 lowers by over 0.3
  sigma, the highest channel (R4c seed 0; dark sky is where the input's large-scale map stands under 5 sigma over
  the frame's sky, bright extended light over 20):

  | where floor4 lowers | Andromeda | Tarantula | Sagittarius |
  |---|---|---|---|
  | dark sky, 10th / 50th / 90th / 99th percentile | 0.19 / 0.42 / 0.77 / 1.68 | 0.26 / 0.52 / 0.89 / 1.61 | 0.06 / 0.21 / 0.41 / 1.01 |
  | dark sky, share over 2 sigma: of the pixels / of the lowering | 0.6 / 2.3 % | 0.6 / 1.1 % | 0.2 / 0.5 % |
  | bright extended light, 10th / 50th / 90th / 99th | 0.43 / 1.31 / 78 / 674 | 5.6 / 22.9 / 117 / 178 | 0.30 / 9.0 / 46 / 100 |
  | bright extended light, share over 2 sigma: of the pixels / of the lowering | 31 / 68 % | 99.1 / 99.7 % | 77 / 88 % |

  At the three holes it reads 29.4, 15.8 and 5.6.
- **The same measure for the fill, and why it needs the input.** Where floor4 lifts by over 0.3 sigma on dark sky the
  lowest channel lies 0.2 to 0.4 sigma under the sky at the median and 0.6 to 1.2 at the 1st percentile; not one
  pixel in a thousand lies 2 under. On bright extended light 13, 16 and 37 % of the lifted pixels do (38, 21 and 67 %
  of the lift), and the globule reads -4.9. Eight of those places were looked at: M31's dust lanes, the Lagoon's
  lanes and globule, the two Tarantula spots. But a real dig on bright nebula lies as far under: a gate on the
  plate's level alone (`lw`) left one more M42 star dug in two bands of the tiles (100-1000: 5.1 to 5.9 %, saturated:
  6.3 to 7.4 %). What tells the two apart is the input: a globule is dark there too, a dig is the model's. So where
  the plate says far under, the input's own floor about the pixel is read against the same sky: the value a fifth of the way up its 1
  px-smoothed values, over a 15 px window, so that a star may cover most of the window. The globule
  stays dark and the dig at (1363, 1445) is filled as before.
- **floor5** (`whole_floor.py` mode `floor5`, `dig_floor.py --level 2 4` with `--fade 2 4`): floor4, and
  1. the lowering is multiplied by a weight that is 1 while the set's highest channel stands under 2 sigma over its
     sky, 0 from 4, linear between;
  2. the fill (pinholes and lift) is multiplied by a weight that is 1 unless the set's lowest channel lies 2 to 4
     sigma under its sky AND the input's own floor does: the larger of the two weights.

  It changes 0.05 % of Andromeda's values against floor4, 0.5 % of the Tarantula's and 0.05 % of Sagittarius's.
- **Explored on R4c seed 0, the model the rule was read on, so NOT a held-out read** (`floor5-final.sh`: the repo's
  two scripts, the scorecard, the nebula rows, the tiles). The count in the middle is `floor5_squares.py`: pixels of
  extended light (the input's large-scale map over 5 sigma above the sky) that a step moves by over 3 pixel-noise
  sigmas against the plate as trained, in any channel.

  | R4c seed 0 | as trained | floor4 | floor5 |
  |---|---|---|---|
  | Andromeda dark flat / dug | 47.2 / 45.9 | 98.2 / 0.9 | 98.2 / 0.9 |
  | Tarantula dark flat / left | 64.9 / 33.1 | 95.5 / 3.6 | 95.5 / 3.7 |
  | Tarantula crowded / BP 17-19 / Sagittarius crowded | 85.2 / 95.5 / 97.2 | 90.4 / 96.0 / 97.9 | 90.3 / 96.0 / 97.9 |
  | M31 nucleus taken, % | 24.4 / 37.0 / 29.4 | as trained | as trained |
  | nebula, light added, % | 0.0 / 0.0 / 0.0 | 0.1 / 0.8 / 0.3 | 0.1 / 0.7 / 0.3 |
  | nebula, broad light taken | 3.04 / 5.39 / 2.64 | 3.07 / 5.63 / 2.63 | 3.07 / 5.49 / 2.61 |
  | extended light LOWERED by over 3 sigma, px (floor3: 1,546 / 5,206 / 1,164) | | 244 / 2,078 / 60 | 23 / 22 / 0 |
  | extended light lifted by over 3 sigma, px | | 102,005 / 8,221 / 428 | 101,956 / 7,954 / 429 |
  | tiles by count, clean, 20-100 / 100-1000 / 1000 and over / saturated | 98.8 / 94.9 / 86.7 / 81.1 | 99.4 / 99.3 / 100.0 / 91.6 | 99.4 / 99.3 / 100.0 / 91.6 |
  | M42 100-1000, clean / dug | 61.0 / 36.8 | 94.1 / 5.1 | 94.1 / 5.1 |
  | saturated, dug / left | 16.8 / 5.3 | 6.3 / 4.2 | 7.4 / 4.2 |
  | twin error under the mask, rms | 2.592 | 1.049 | 1.053 |

  One more saturated star of M42 reads dug (one of 95); it is the lowering's gate that does it, not the fill's (the
  tiles with the lowering's gate alone read the same 7.4).
- **Looked at** (`holes-r4c_synth28_s0-lx.png` and `-lv.png`, 10x; `fills-r4c_synth28_s0-a.png` and `-b.png`). The
  three knots keep their light and no square is left on them; the globule at (1088, 1339) stays as dark as the model
  left it, with the pits the model dug where its stars were; the dig at (1363, 1445) is filled as floor4 fills it,
  stepped edge included; M31's dust lanes and the Lagoon's globule are unchanged.
- **Pre-registered for R3v seed 0 and R4d seed 0 (2026-11-20; no floor5 score of either exists, and neither was
  looked at while the rule was made).** floor5 exactly as above (`whole_floor.py <w-tag> floor5`,
  `dig_floor.py <model> floor --pin 2.5 --cap-up 2 --cap-down 0.05 --fade 2 4 --level 2 4 --tag floor5`), each
  model's own plate as trained, read against the SAME model's floor4 from the same run of each check. The sheets at
  the five places above and at each model's three strongest lowerings left on extended light are looked at before
  the rows. On each of the two models:
  1. **The question:** the pixels of extended light lowered by over 3 sigma are at most a fifth of floor4's, on
     every master where floor4 has 100 or more.
  2. **Guards, whole masters** (floor4's figure less or plus the margin): Andromeda dark flat and Tarantula dark
     flat not lower by more than 0.5; Andromeda dug and Tarantula left not higher by more than 0.5; Tarantula
     crowded, BP 17-19 and Sagittarius crowded not lower by more than 0.5; the M31 nucleus row not above the
     as-trained row in any channel; nebula light added not higher than floor4's by more than 0.1 on any master, and
     broad light taken not higher than floor4's by more than 0.02.
  3. **Guards, tiles by star count:** clean at 20-100, 100-1000 and 1000 and over not lower by more than 0.3;
     saturated clean not lower by more than 1.1 (one star of 95) and saturated dug not higher by more than 2.1
     (two stars); M42 100-1000 dug not higher by more than 0.8 (one star of 136); the twin error under the mask not
     higher by more than 0.02.
- **Read (2026-11-20): every bar holds on both models** (`floor5_read.py`). The sheets first
  (`floor5-spots-<model>.png`, `floor5-left-<model>.png`). On both models the three knots keep their light and no
  square is left on them, where floor4's plates show squares at two of the three (red on R4d and faint on R3v at
  (1601, 710), dark on both at (1568, 699)); the globule stays dark; the dig at (1363, 1445) is filled as floor4 fills it, stepped edge
  included, and on R4d a grey triangle of fill beside it stays as well. The strongest lowerings left on extended
  light, 3.4 to 4.0 sigma, lie on the residue of bright stars (the Tarantula at (600, 1390) and (528, 380) on R3v,
  at (1401, 1495) on R4d), not on nebula. Then the rows:

  | | R3v seed 0: as trained | floor4 | floor5 | R4d seed 0: as trained | floor4 | floor5 |
  |---|---|---|---|---|---|---|
  | extended light lowered by over 3 sigma, px, And / Tar / Sag (the question: at most a fifth of floor4's where that is 100 or more) | | 571 / 3,344 / 21 | **94 / 127** / 0 | | 213 / 1,863 / 13 | **23 / 21** / 0 |
  | extended light lifted by over 3 sigma, px | | 120,921 / 19,677 / 274 | 120,893 / 19,240 / 271 | | 101,485 / 11,913 / 450 | 101,414 / 11,468 / 425 |
  | Andromeda dark flat / dug | 69.5 / 22.4 | 97.6 / 1.0 | 97.6 / 1.0 | 69.4 / 19.5 | 98.5 / 0.7 | 98.5 / 0.7 |
  | Tarantula dark flat / left | 71.9 / 16.0 | 93.8 / 5.0 | 93.7 / 5.1 | 80.3 / 14.3 | 96.5 / 2.7 | 96.5 / 2.7 |
  | Tarantula crowded / BP 17-19 / Sagittarius crowded | 83.8 / 92.4 / 93.7 | 88.7 / 92.9 / 95.5 | 88.7 / 92.8 / 95.5 | 86.7 / 95.4 / 96.4 | 91.3 / 96.0 / 97.0 | 91.3 / 96.0 / 97.0 |
  | M31 nucleus taken, % | -1.0 / 5.1 / 1.2 | as trained | as trained | 2.8 / 6.6 / 9.7 | as trained | as trained |
  | nebula, light added, % | 0.0 / 0.1 / 0.1 | 0.1 / 1.2 / 0.3 | 0.1 / 1.1 / 0.3 | 0.0 / 0.0 / 0.0 | 0.1 / 0.8 / 0.3 | 0.1 / 0.7 / 0.3 |
  | nebula, broad light taken | 2.73 / 3.56 / 0.56 | 2.77 / 3.55 / 0.57 | 2.77 / 3.45 / 0.56 | 2.88 / 5.86 / 2.67 | 2.90 / 6.03 / 2.67 | 2.90 / 5.91 / 2.65 |
  | tiles by count, clean, 20-100 / 100-1000 / 1000 and over / saturated | 98.9 / 94.9 / 87.2 / 78.9 | 99.3 / 99.2 / 100.0 / 89.5 | 99.3 / 99.2 / 100.0 / 90.5 | 98.8 / 95.6 / 91.5 / 85.3 | 99.3 / 99.2 / 100.0 / 93.7 | 99.4 / 99.2 / 100.0 / 93.7 |
  | M42 100-1000, dug | 42.6 | 5.9 | 5.9 | 34.6 | 5.9 | 5.9 |
  | saturated, dug (bar: at most 2.1 higher) | 16.8 | 8.4 | 7.4 | 10.5 | 4.2 | 5.3 |
  | twin error under the mask, rms (bar: at most 0.02 higher) | 1.920 | 0.992 | 1.006 | 2.260 | 1.073 | 1.078 |

  - **Nearest their bars:** R3v's Andromeda count, 94 of 571 (16 %, the bar a fifth), and R3v's twin error, 0.014
    higher of the 0.02 allowed: where a twin's star was drawn on bright structure, the residue the model leaves under
    it is no longer lowered. One saturated star more reads dug on R4d and one fewer on R3v.
  - **What floor5 does not do.** A fill that the trust lets through in part still ends in a stepped edge (the wedge
    at (1363, 1445), R4d's triangle beside it). Inside a globule the fill is held back as a whole, so the pits the
    model dug where its stars were stay as dug. A knot narrower than the core of the star on it reads as residue and
    is lowered. And the lowering is still cut per channel, so what it does take can change a colour.
  - **The product is the script, bit for bit.** `dig_fill_parity.py` on R4c seed 0: `tianwen image fill-digs` against
    `whole_floor.py floor5`, no value different on the three masters (seven planes). Two tests hold the two gates:
    a knot under stars 5 px apart is left bit for bit where a faint one is lowered, and one plate with a dip of 14
    sigmas is left alone where the input holds the dip and filled where the input is level.
  - **Cost** (`digfill-bench5.ps1`, each case run alone three times; peak working set the largest of the three,
    time the fastest). The step reuses two planes it is done with and runs its Gaussians through two of its own;
    with a new plane per Gaussian it added 233 MB at 12.2 megapixels and a gigabyte at 24.4.

    | frame | floor4 | floor5 |
    |---|---|---|
    | 12.2 megapixel colour (the Andromeda master) | 2,738 MB, 8.5 s | 2,878 MB, 9.3 s |
    | 24.4 megapixel colour (that master joined to its mirror image) | 5,054 MB, 16.3 s | 5,081 MB, 18.2 s |

#### The synthetics review: what tells the training data from real, measured (2026-10-21)

The owner, 2026-10-21: "review our synthetics, they do still do not look natural enough ... we should be able to measure
our existing corpus for certain values, including doing some domain analysis". Measured over the 8 validation sessions,
40 cells each, through the exporter's own stretch in all three sources: the real masters (`r2-real-val`), synth4's own
training inputs (stars on the synthetic background) and A's draws (stars on the real starless plates). Two instruments,
kept in `training/star-removal/domain/` (every path an argument, `training/README.md`; they lived in `C:/temp/e2/domain` on DE1): statistics in each tile's own noise sigma (`domain_stats.py`), and a small CNN told to tell
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

**The classifier's own spread, read first (2026-10-25, F4's gate).** The review read each AUC from one net. `domain_classifier.py`
now takes `--seeds` and trains each fold's net at each seed on the same crops, so the spread over seeds is the classifier's
noise, which a change to an export must clear. Run on DE1 (job j59, 34 minutes, seeds 0 to 4, 6 CPU threads), on the review's
tiles: `r2-real-val` against synth4's training inputs and against A's draws, the 8 validation sessions, one camera left out at a
time of the QHY294PROC, ASI294MC, ASI533MC Pro and ASI585MC Pro. The AUC is the mean over the four held-out cameras, then the
mean and sample standard deviation over the five seeds.

| crops | synth4: mean +- std (seeds 0 to 4) | A's draws: mean +- std (seeds 0 to 4) |
|---|---|---|
| star-centred 24 px, a 20 to 500 sigma star | 0.782 +- 0.009 (0.792, 0.779, 0.789, 0.783, 0.770) | 0.732 +- 0.043 (0.739, 0.709, 0.805, 0.706, 0.700) |
| noise: 16 px, nothing over 4 sigma, high-passed | 0.705 +- 0.007 (0.709, 0.696, 0.714, 0.702, 0.702) | 0.615 +- 0.004 (0.616, 0.619, 0.617, 0.609, 0.616) |

Crops, real / synthetic: stars 18124 / 18396 (synth4) and 18681 (draws); noise 25481 / 30867 and 30351. The review's single reads
(0.79 and 0.73 on stars, 0.70 and 0.61 on noise) are inside these spreads.

- **Synth4's reads are tight, A's draws' star read is not.** Four of the five seeds on the draws' star crops sit at 0.700 to
  0.739 and seed 2 at 0.805, so the spread (0.043) is one seed's. Without it the draws read 0.714 +- 0.017. The ASI294MC fold
  moves most (0.594 to 0.785 over seeds on the draws, 0.638 to 0.680 on synth4); the ASI533MC Pro fold hardly at all (0.826
  to 0.868 and 0.855 to 0.873). The mean over cameras averages some of that down, so a single camera's AUC needs a wider
  margin than the mean does.
- **The bar a new export's star-crop AUC must clear to count (F4):** its mean over at least five seeds, on the same seeds and
  the same tiles, is lower than the baseline's by more than twice the larger of the two spreads. Against synth4 that is
  below 0.764 (0.782 - 2 x 0.009). Against A's draws it is below 0.646 (0.732 - 2 x 0.043), or below 0.68 on the four-seed
  spread (0.714 - 2 x 0.017) if seed 2's outlier is read as the classifier's and not the draws'; read the draws' star AUC at
  seeds 0 to 9 before using the tighter bar. A move inside that is the classifier. F4's other half, no drawn star outside
  every real master's relation, is not a classifier read and has no spread.
- **F5 (#1781) reads its noise-crop AUC with the same scripts**: `--variants noise --seeds 0,1,2,3,4`. Its gate is below synth4's
  0.70, and synth4's own noise read is 0.705 +- 0.007, so 0.70 is inside the spread: a new export's noise AUC counts as below
  it only past about 0.69 (0.705 - 2 x 0.007), and A's draws' 0.615 +- 0.004 is the floor that read has shown on real starless
  plates.
- The JSONs and logs stay on DE1 (`C:/temp/tia1241-domain/out`, `cls-synth4.json`, `cls-draws.json`; job logs j59 and j62);
  the classifier that ran is byte-identical to the committed `domain_classifier.py`.

**The brightness trend, per rig (the design, 2026-10-25; F4's leg `brightness-gains`; measured in "S1b's read" below).**
`tianwen dataset star-shapes` reads each channel's wing gain with a brightness term. Nothing below has been run on the
masters: only synthetic fields pin it (`StarInjectionTests`), so the real slopes, and whether the faint bands now read in
the review's direction, are the next measurement.

- **The sky bias that flipped the faint bands is measured and taken off, not weighted away.** `StarShape.Light` is a SUM over
  the 3 to 8 px annulus minus the MEDIAN of the 22 to 28 px annulus, so a sky whose mean is not its median (skewed noise,
  unresolved stars' glow, a gradient) leaves a fixed level in every star's light, whatever the star's amplitude. Per
  catalogued amplitude it is large on a faint star and nothing on a bright one, which is the old remark (a faint band
  reading the opposite way to the bright one). `StarShape.BlankField` reads it where it can be read alone: `Light` at a
  grid of positions (at least 48 px apart, at most 600) with no catalogued star of 5 sigma or more within 31 px, the median
  of each position's light over the pixels it counts being a per-pixel level (`StarShapeChannel.SkyBias`), taken off every
  real star's light and rings before they are divided by the amplitude (`Light(..., skyBias)`, `Rings(..., skyBias)`).
  A star's own wing in its sky annulus is not in this and needs no correction: the drawn star is read through the same
  estimator. **Opt-in, `--sky-bias`** (`StarShapeRun.Options.CorrectSkyBias`, `Measure(correctSkyBias:)`): by default the light is
  read as R2s2 read it, with no bias taken off, so a plain run re-measures the adopted recipe's file. On a synthetic field of skewed noise whose true gain is the
  same at every brightness, the uncorrected faint band reads 1.63 for a true 1.4 and the slope -0.05 to -0.12; corrected,
  1.38 and 0.01.
- **Bands can go down to about 20 sigma, on request** (`StarShape.TrendBandEdges`: 20, 50, 100, 200, 500, 1000, asked for as
  `--band-edges 20 50 100 200 500 1000`; the default is `DefaultBandEdges`, one band from 1000, which is the row every file held
  before the trend and so carries no slope). Each band records the median significance of its stars
  (`StarShapeBand.MedianSignificance`), where its gain is read, and each channel its star count and a **weight**
  (`StarShape.BandWeight`): stars over the squared scatter of one star's light ratio, an intrinsic 10 % plus the light's own
  error against the sky's (the blank field's robust spread over the star's light), in quadrature. A faint band's light is a
  fraction of the sky's error, so it counts for little however many stars it holds; a band under 10 stars or on a bisection
  bound counts for nothing.
- **The slope** (`StarShapeRow.LogSlopes`, per channel) is d ln(gain) / d ln(significance): the weighted least-squares line
  over a master's readable bands (`StarShape.OwnSlopes`), and **per rig** (the master's `INSTRUME`, `StarShapeRow.Rig`) the
  same fit pooled over every master of the camera, each about its own weighted means so that every master keeps its own
  level and only the rise is shared (`StarShape.PoolSlopesPerRig`, run once the run is done; opt-in, `--pool-by camera`, the
  default being `none`, each master keeping its own). A
  rig with no readable pair of bands, or a master with no rig, keeps its own.
- **The draw** (`StarShape.LogGains`): with no slope, or a slope of 0, a star takes its band's gain, as every row did, so a
  row written before reads back and draws the same. With one, the brightest READ band's own log gain plus the slope times the
  log of the significance over that band's median, the significance clamped to the range the READ bands measured (the faintest
  read band's median to the brightest's, so a star fainter than every band, or a saturated one, holds the end's gain), and the
  gain held to 0.25 to 8. A read band is one `Points` yields: a weight above 0, and a gain off both bounds. A master with fewer
  than two of them draws its band step, slope or not. (The first version anchored on the band with the greatest reference
  whether or not it was read; the correction, "fix(star-removal): the brightness trend's level and clamp come from the bands
  that were read", is described in "S1b's read" below, and the export measured there was drawn before it.)
- **Judged by:** the pooled slopes per camera against the review's ratio (0.82 at 20-50 sigma to 1.81 at 100-200 on the
  ASI294MC, 1.4 to 1.7 and none on the ASI585); F4's own gate is the star-crop AUC bar above, read on an export drawn with
  the trend, `degrade --star-shapes` on the new file.

**The channel relation, held to the real envelope (`degrade --clamp-wing-relations`, 2026-10-25, #1241's leg
`channel-relation-clamp`; opt-in; first run in "S1b's read" below).** `degrade --star-shapes` on a file with a slope already draws
each star at the gain of its own significance (`InjectionPopulation.Plan`, `StarShape.LogGains`; a saturated star the brightest
band's), but a slope per channel moves each channel's wing on its own, so a drawn star's channels can leave every real
master's relation (R2s's lesson: Andromeda's blue). The option holds them in:

- **The envelope** (`ChannelWingEnvelope.Read`, over the star-shapes file's masters) is each channel's real light 3 to 8 px
  out per amplitude (`StarShapeChannel.RealLight`) as a ratio to green's, in logs, least and greatest over every band that is
  read to better than the stars' own scatter (`MinReliability` 0.5 of the weight the stars alone carry; a faint band's ratio
  is the sky's error, and would only widen it; a row from before the weight was kept counts for nothing). One box per channel
  count: the file carries no filter, so a colour sensor's masters of every filter share it, the widest honest envelope; a
  mono master has no relation and is never clamped. Narrower, per filter family, needs the filter in the file.
- **The clamp** (`WingRelationClamp`, in `InjectionPopulation.Plan`, after the trend and any channel factors) moves a channel
  outside its bounds to the edge by solving its wing gain, green's never touched: the light a gain draws, as `DrawnLight`
  reads it, is a line in the gain (two readings per channel fix it), so the edge is one division. It draws no random number,
  so the stars keep their places, brightness and colour, and every lobe, saturated and faint star goes through it.
- **Said, not silent.** Each injection row carries `WingClamped` (the stars a channel of which was moved) and `WingUnreached`
  (of those, the ones whose gain hit 0.25 or 8 before the edge, a core that alone carries more of the light than the edge
  allows, so the star is still outside); both are written only when non-zero, and the session's log line gives their totals.
  The "no drawn star outside the envelope" gate holds where `WingUnreached` is 0, so read that number on the first real export.
- **Refused with** `--channel-wing-cut` (which departs from the envelope on purpose) and `--channel-relations`; it needs
  `--star-shapes`. Pinned by `StarInjectionTests` (`ATrendDrawsBrighterStarsWithStrongerWings`,
  `AClampedStarsChannelsStayInsideTheEnvelopeTheRealMastersSpan`, `AnEdgeTheWingGainCannotReachIsSaidAndTakenToTheGainsBound`)
  and `DatasetDegradationExporterTests.AWingRelationClampHoldsEveryStarToTheRealEnvelopeAndNothingElseMoves`, where an envelope no
  star leaves changes no byte of any tile or row.

##### S1b's read: the trend's export reads further from real, not nearer, and is not adopted (2026-10-26)

Run on DE1 from the flight's branch at "training(star-removal): s1b_read.py, the shape, wing-band and clamp reports F4's gate
is read from", built Release and frozen in `C:/temp/tianwen-scratch/tia1241-s1b/bin` (`FROZEN-AT.txt` there and in the export).
Three steps, each a pilot job: `tianwen dataset star-shapes` over synth11's 138 training masters (the store
`D:/Astro-Dataset/2026-10-12-full`, its `starless` plates, with the options the trend needs, `--band-edges 20 50 100 200 500 1000 --sky-bias --pool-by camera`, which
were the run's defaults when it was made and are opt-in now), then
`dataset degrade` with synth11's frozen recipe exactly (`--mode stars --shape warped --warp-sigma 0.5 --warp-sigma-drizzle 0
--cells 40 --draws 4 --seed 1 --background synthetic --steer 0.5,2,0.8,1 --nebula-knots --bright-knots-wide --stretch-from
master`) plus `--star-shapes` on the new file and `--clamp-wing-relations`, over the validation sessions only (four `--session`
substrings, which also took a ninth session the instruments do not read) into `synth-s1b`, then both instruments
(`training/star-removal/domain/s1b_read.py reads`, seeds 0 to 4, the same tiles as the bar). synth11, the same recipe without
the trend, is read beside it as the control: the bar was set on synth4, which is the plate's stretch, and synth11 is synth4's
draws on the master's. synth4 was read again in the same run and reproduced the bar seed for seed (0.782 +- 0.009).

**The slopes, per rig** (d ln gain / d ln significance, R / G / B, pooled over the rig's masters, each keeping its own level):

| rig | masters | slope R / G / B |
|---|---|---|
| ZWO ASI533MC Pro | 92 | +0.25 / +0.24 / +0.21 |
| ZWO ASI585MC Pro | 15 | +0.31 / +0.20 / +0.47 |
| ZWO ASI294MC | 7 | +0.58 / +0.43 / +0.39 |
| QHY294PROC | 6 | +0.32 / +0.27 / +0.14 |
| Uranus-C (IMX585) | 2 | -0.31 / 0.00 / -0.10 |
| ZWO ASI294MM (mono) | 7 | +0.38 |
| the other monos (QHY178M, QHY183M, ASI183MM, ASI1600MM Pro, one unnamed QHY) and the ASI462MC | 1 to 4 each | -0.37 to +0.38, or none read |

The ASI294MC's +0.4 to +0.6 is what the review's ratio implies (0.82 at about 35 sigma to 1.81 at about 150 is a slope near
0.55). The ASI585MC Pro, where the review saw no shortfall, reads +0.2 to +0.5 all the same. **The faint bands sit on the gain's
floor.** The median band gain over a rig's masters is the lower bound, 0.25, at 20 sigma on every colour rig and at 50 sigma on
the ASI533MC Pro (75 masters) and the QHY294PROC. It is 0.4 to 0.6 at 100 sigma, and rises to about 1 only at 500 to 1000. A
band on a bound weighs nothing in the fit, so the slope is read off the 100 to 1000 sigma bands, and the draw then extrapolates
it down to the faintest band's median.

**The wing, real over synthetic** (`domain_stats.py`, the median over the stars of a band of the 3-5 px light over the
peak, the mean of the three channels' ratios; the review's table for synth4 is reproduced to 0.04):

| sigma | 20-50 | 50-100 | 100-200 | 200-500 |
|---|---|---|---|---|
| real over synth4 (the review: 0.82 / 1.24 / 1.81 / 1.47) | 0.82 | 1.25 | 1.77 | 1.53 |
| real over synth11 (the control) | 0.67 | 0.78 | 1.34 | 1.38 |
| real over s1b | 1.13 | 1.21 | 2.43 | 1.48 |

| 3-5 px light over the peak, R / G / B | 5-20 sigma | 20-100 | 100-1000 |
|---|---|---|---|
| real | 0.012 / 0.012 / 0.004 | 0.022 / 0.022 / 0.018 | 0.088 / 0.095 / 0.055 |
| synth11 | 0.014 / 0.019 / 0.018 | 0.030 / 0.034 / 0.026 | 0.068 / 0.083 / 0.043 |
| s1b | 0.007 / 0.010 / 0.009 | 0.017 / 0.019 / 0.017 | 0.037 / 0.048 / 0.027 |

The trend takes light out of the wing at every brightness up to 200 sigma. Below 100 sigma it overshoots real and lands on the
other side (synth11 0.67 / 0.78, s1b 1.13 / 1.21). From 100 to 200 sigma it widens a gap that was already there (1.34 to 2.43).
Only 200 to 500 sigma is left about where it was.

**The classifier** (`domain_classifier.py`, leave one camera out, the mean over the four held-out cameras, then over seeds 0
to 4):

| crops | synth4 (the bar's baseline) | synth11 (control) | s1b |
|---|---|---|---|
| star-centred 24 px | 0.782 +- 0.009 | 0.760 +- 0.002 | **0.860 +- 0.006** (0.852, 0.855, 0.865, 0.866, 0.860) |
| noise, 16 px | 0.705 +- 0.007 | 0.690 +- 0.014 | 0.685 +- 0.007 |

Every camera's star AUC rises with the trend: QHY294PROC 0.78-0.80 to 0.89-0.90, ASI294MC 0.58-0.65 to 0.72-0.77, ASI533MC
Pro 0.83-0.87 to 0.91-0.93, ASI585MC Pro 0.76-0.80 to 0.84-0.90. The noise crops do not move (inside synth11's spread), so it
is the stars that changed.

**The clamp moved nothing:** over the 1,440 drawn tiles, 556,778 stars, `WingClamped` 0 and `WingUnreached` 0. The real
envelope holds every drawn star's channel relation, so that half of the gate holds, but on this export nothing tested it.

**The export's anchor was a flaw (found in review, 2026-10-26).** `StarShape.LogGains` took each master's level, and the bright
end of its clamp, from the band with the greatest reference whether or not that band was read. A band under 10 stars is
lent the nearest band's gain at weight 0, and its reference is its own few stars' median, or the band's edge (1000) when it holds
none, so the draw's level was a lent gain at a significance above every band that was measured. At the +0.25 to +0.5 slopes
above that pulls every fainter star's gain down by 10 to 30 %, in proportion to how far the empty band sits above the last
read one (`ln(1000 / 300) * 0.3` is 0.36 in the log, 30 %, for a last read band at 300). **In 50 of the 138 masters in
`star-shapes.jsonl` the brightest band by reference has weight 0** (41 of them in all three colour channels, the other 9
single-channel; no star in it, its reference the 1000 edge). The other 88 were anchored on a read band and drew as designed.
The code now takes the level and both clamp ends from the read bands, and a master with fewer than two draws its band step
(`AnUnreadableBrightestBandDoesNotAnchorTheTrend`). **Nothing was exported again**, so the table above is the export as it was
drawn, and how much of its numbers the anchor owns was not measured.

**Verdict: not adopted for R3d, and #1241 stays open. The anchor does not change it.** The gate asks the star-crop AUC to
fall past the classifier's spread, and it rose instead: by 0.10 against synth11 and by 0.08 against synth4, eleven and nine
times the larger spread. The anchor cannot be the whole of that. It touches 50 masters in 138 and only lowers faint stars'
gains, by at most the 10 to 30 % above, where the faint bands sit on the gain's floor in `Measure` itself (0.25 at 20 sigma on
every colour rig), so a corrected anchor lifts a 20 to 50 sigma star's gain by a fraction of what separates it from real, and
the other 88 masters, anchored on a read band, are not touched by it. It could move the numbers some; it was not tested. The likely cause is in the gains, not the trend's shape. The linear per-amplitude measure says a drawn
star at 20 to 200 sigma needs a quarter to half of its wing. The stretched-domain statistic, which the classifier agrees with,
says it needs as much or more. A gain stuck on its floor in nearly every master is a measure that found no solution, not a
rig's wing. **The next step:**
1. Find why the faint and mid bands land on the floor before drawing from them again. Read a run without `--sky-bias` against the
   corrected file over the same masters. If the bands leave the floor without the correction, the blank-field bias is
   over-subtracting: a blank position's light still holds unresolved stars' glow, which a real star's sky annulus also holds.
2. Then check the measure against the statistic that judges it: a drawn star at its measured gain should read real's 3-5 px
   light through the exporter's stretch, band by band.
3. Only then export again, drawn with the corrected anchor, which also says how much of this read the old one owned. The
   classifier reads such an export in about 40 minutes, which makes it the cheap test of any next gain table.

Everything stays on DE1 under `C:/temp/tianwen-scratch/tia1241-s1b`: `star-shapes.jsonl`, `band-gains.txt`, `synth-s1b`,
`degrade.log`, and `reads/` (`stats.json`, `cls-*.json`, the classifier logs). `--star-shapes` without a slope draws as before,
and the trend and the clamp are opt-in: `star-shapes` with no options measures the one-band, no-bias, unpooled row R2s2 and synth11
were drawn from (`TheDefaultMeasureIsTheOneBandRowWithNoBiasAndNoSlope`), the trend takes `--band-edges 20 50 100 200 500 1000
--sky-bias --pool-by camera`, and the clamp takes `degrade --clamp-wing-relations`.

##### S1c: the outer halo (2026-10-29)

Tracked by #1870 (an outer halo component for injected stars, measured per rig, validated on an 8-session export before any
training). The wing the S1b read left open is the light past 10 px: the stretched profile table (`halo_profile.py`) showed
the real clipped stars of a 2 px core keeping 1.2 times synth11's light at 4 to 6 px and twice it at 10 px, and cores of 3 px
and up keeping a long tail synth11 never draws. `tianwen dataset degrade --mode stars --outer-halo <halo-profiles.json>` is
the opt-in answer: a per-rig low-beta Moffat under each star, fitted to the linear clipped-star profiles over 4 to 40 px,
scaled with the star's amplitude and recorded per star as `OuterHalo` in `injections.jsonl`. A rig the file lacks draws none.

Run on DE1 from the flight's branch at "OuterHalo tests: match the fit's per-peak reading, give the exporter test a synth11
group (#1870)", built Release and frozen in `C:/temp/tianwen-scratch/tia1870/bin` (`FROZEN-AT.txt` there and in the export
records the hash). That build is the review's go-around: the fit pools only the groups synth11 also has, reads each group's
excess per peak (`measured/measured[0] - drawn/drawn[0]`, weighted by stars) rather than as an absolute fraction, searches beta
from 0.6 to 12 and alpha from 1 to 200 px, and says ", ON THE SEARCH BOUND" in its `InWords` when a fit lands within 3 % of a
bound. The first read (go 0, on a fit that pooled unmatched groups as raw excess and stopped beta at 4) is superseded and kept
only on disk.

The export is `synth-halo`, S1b's method with synth11's recipe and `--outer-halo` added, over the same validation set: 9
session ids from 5 captures, since the Lobster (QHY294PROC) and HIP 80609 (ASI533MC Pro) captures each count three times with
their flip variants, beside M42 RGB (ASI294MC), M42 LUM (ASI294MM) and the SMC (ASI585MC Pro). 1,440 degraded and 360 clean
tiles, nothing skipped or failed; 1,280 of the degraded tiles carry `OuterHalo`. The other 160 are the ASI585MC Pro's: its blue
channel had 4 positive points past 4 px, one short of the fit's 5, so with no fit and no luminance fallback the whole rig draws
no halo.

**The fits, and where they hit the bound.** Four of the ten channel fits sit on or next to the search bound:

| rig | channel fits | bound |
|---|---|---|
| QHY294PROC | alpha 76 to 110 px, fraction 4e-5 to 8e-5 per peak | R beta 11.88 and B beta 11.82, next to the beta bound of 12 |
| ASI294MC | R and B alpha 2 to 3 px, beta about 1; G alpha 1.0, fraction 0.233 | **G on the alpha bound** |
| ASI294MM | L | **on the beta bound** (12) |
| ASI533MC Pro | beta 3.8 to 7.4, alpha 27 to 42 px | none |
| ASI585MC Pro | B: 4 positive points | no fit, rig dropped |

The bound hits are not a range to widen. Inside 10 px the per-peak excess is the difference between the real and the drawn
CORE shapes, not a halo: it is large and positive at 4 px and falls fast, so a Moffat fitted to it wants a tiny alpha (the
ASI294MC's G) or, once those points are excluded by sign, a steep beta (QHY294PROC, ASI294MM). The halo itself shows only from
about 10 to 20 px, at about 1e-5 to 2e-4 of the peak. Fitting only the positive points over 4 to 40 px lets the core mismatch
choose the shape, and the bound is where that choice runs.

**The matched-core table** (`halo_census.py`, `halo_profile.py profile`, then `matched_core.py`: the stretched median of the
light over the peak for clipped stars, grouped by the star's core width). "masters range" is the lowest and highest real
luminance median over the rigs with at least 5 such stars in the group, so a synthetic row outside it is further from every rig
than the rigs are from each other. The real column is the pooled real luminance. In both profile files the label "synth11"
means whichever export was censused.

New export (16 rigs in the range; synthetic stars: 1,385 of 1 px, 371 of 2 px, 20 of 3 to 5 px, none of 6 px and up):

| core | r (px) | real | masters range | halo R / G / B |
|---|---|---|---|---|
| 1 px | 4 | 0.0565 | 0.0126 to 0.6205 | 0.0613 / 0.0668 / 0.0378 |
| 1 px | 10 | 0.0029 | 0.0014 to 0.1200 | 0.0042 / 0.0030 / 0.0019 |
| 1 px | 20 | 0.0004 | 0.0001 to 0.0069 | 0.0005 / 0.0006 / 0.0002 |
| 1 px | 30 | 0.0001 | 0.0000 to 0.0049 | 0.0002 / 0.0002 / 0.0000 |
| 2 px | 4 | 0.1742 | 0.0979 to 0.6091 | 0.1083 / 0.1029 / **0.0678 (below)** |
| 2 px | 10 | 0.0109 | 0.0059 to 0.1025 | 0.0079 / **0.0055 (below)** / **0.0038 (below)** |
| 2 px | 20 | 0.0015 | 0.0009 to 0.0070 | 0.0013 / 0.0009 (on the line) / **0.0008 (below)** |
| 2 px | 30 | 0.0004 | 0.0002 to 0.0015 | 0.0006 / 0.0006 / 0.0002 (on the line) |
| 3 to 5 px | 4 | 0.5088 | 0.1661 to 0.6804 | 0.4602 / 0.4784 / 0.4102 |
| 3 to 5 px | 10 | 0.0672 | 0.0273 to 0.2201 | 0.0580 / 0.0497 / 0.0452 |
| 3 to 5 px | 20 | 0.0085 | 0.0050 to 0.0321 | 0.0066 / 0.0081 / 0.0057 |
| 3 to 5 px | 30 | 0.0025 | 0.0015 to 0.0107 | 0.0020 / 0.0034 / 0.0022 |

The control, synth11, luminance (14 rigs; 13 for 3 to 5 px, 8 for 6 px and up; synthetic stars 1,686 / 271 / 14 / 10):

| core | r (px) | real | synth11 | inside the range |
|---|---|---|---|---|
| 1 px | 4 / 10 / 20 / 30 | 0.0555 / 0.0030 / 0.0004 / 0.0001 | 0.0527 / 0.0018 / 0.0000 / -0.0001 | yes, yes, no, no |
| 2 px | 4 / 10 / 20 / 30 | 0.1723 / 0.0113 / 0.0016 / 0.0004 | 0.1198 / 0.0042 / 0.0002 / 0.0001 | yes, no, no, no |
| 3 to 5 px | 4 / 10 / 20 / 30 | 0.5130 / 0.0700 / 0.0089 / 0.0025 | 0.4077 / 0.0432 / 0.0026 / 0.0005 | yes, yes, no, no |
| 6 px and up | 4 / 10 / 20 / 30 | 0.7033 / 0.5174 / 0.1128 / 0.0428 | 0.7500 / 0.7498 / 0.6181 / 0.0156 | no, no, no, yes |

Per rig, 2 px cores, the halo export's G against the rig's real luminance at 4 / 10 / 20 / 30 px:

| rig | real | halo G |
|---|---|---|
| QHY294PROC | 0.1262 / 0.0069 / 0.0011 / 0.0002 | 0.0858 / 0.0055 / 0.0009 / 0.0007 |
| ASI533MC Pro | 0.0979 / 0.0059 / 0.0011 / 0.0003 | 0.1579 / 0.0038 / 0.0004 / -0.0002 |
| ASI294MC | 0.2091 / 0.0133 / 0.0012 / 0.0004 | 0.2657 / 0.0155 / 0.0032 / 0.0008 |
| ASI585MC Pro (no halo) | 0.2530 / 0.0156 / 0.0016 / 0.0004 | 0.3883 / 0.0434 / 0.0028 / 0.0022 |

(The ASI294MM has 1 px stars only, 43 of them.)

Read: the go-around's fit no longer overdraws. Every 1 px and 3 to 5 px row of every channel is inside the masters' range, at 20
and 30 px too, where the control's 1 px and 3 to 5 px rows were under it, and the ASI294MC, which go 0 drew sixty to seventy
times real at 20 and 30 px, now reads 2.7 and 2 times. The 2 px group is still short: blue under the range from 4 to 20 px and
green under it at 10 px, sitting on the line at 20 px, so the halo has not lifted the group the S1b read flagged. The halo also
changes the core: at 4 px the ASI533MC Pro's 2 px stars read 1.6 times real while its 10 to 30 px light is under real, which is
the core mismatch the fit took for a halo. The export drew no star of 6 px and up, so that row has no reading.

**The wing** (`domain_stats.py` through `s1b_read.py wing-table`, the median over the stars of a band of the 3-5 px light over
the peak, 320 tiles per source; the 500 sigma band and up holds no stars):

| 3-5 px light over the peak, R / G / B | 20-50 sigma | 50-100 | 100-200 | 200-500 |
|---|---|---|---|---|
| real | 0.018 / 0.018 / 0.015 | 0.037 / 0.036 / 0.026 | 0.075 / 0.083 / 0.049 | 0.160 / 0.187 / 0.107 |
| halo | 0.025 / 0.028 / 0.023 | 0.045 / 0.049 / 0.033 | 0.057 / 0.071 / 0.033 | 0.132 / 0.147 / 0.070 |
| synth11 | 0.024 / 0.028 / 0.022 | 0.044 / 0.049 / 0.033 | 0.056 / 0.068 / 0.033 | 0.128 / 0.136 / 0.071 |
| real over the halo export (mean of channels) | 0.67 | 0.78 | 1.31 | 1.34 |
| real over synth11 | 0.67 | 0.78 | 1.34 | 1.38 |

The halo moves the 3 to 5 px light by a few percent at most, as a 10 px and outward component should. S1b's shortfall at 100 to
500 sigma and its over-bright faint bands are as they were.

**The classifier** (`domain_classifier.py`, leave one camera out, the mean over the four held-out cameras, then over seeds 0 to
4; F4's gate: a pass is a mean below synth4's 0.782 by more than twice the larger spread, so below 0.764 on synth4's spread of
0.009 or on a smaller one):

| crops | synth4 (the bar's baseline) | synth11 (control, this run) | halo |
|---|---|---|---|
| star-centred 24 px | 0.782 +- 0.009 | 0.760 +- 0.002 | **0.768 +- 0.014** (0.758, 0.776, 0.764, 0.755, 0.787) |
| noise, 16 px | 0.705 +- 0.007 | 0.690 +- 0.014 | 0.701 +- 0.009 (0.699, 0.687, 0.712, 0.703, 0.704) |

Per held-out camera, star crops, the mean over seeds 0 to 4, halo against synth11: QHY294PROC 0.778 against 0.785, ASI294MC
0.678 against 0.624, ASI533MC Pro 0.848 against 0.856, ASI585MC Pro 0.768 against 0.777. Three cameras move under a hundredth;
the ASI294MC, the rig whose G fit sits on the alpha bound, is easier to tell from real with the halo than without it, and it
carries most of the halo's spread (0.63 to 0.72 over the seeds against 0.58 to 0.65). The noise crops sit inside the baselines'
spread.

The halo fails the gate. Its mean, 0.768, is above the bar of 0.764, its drop from synth4 (0.014) is under twice its own spread
(0.028), and it is 0.008 above its own control rather than below it.

**Verdict for the owner: do not train on this export.** With the fit corrected the halo neither overdraws nor helps: the
classifier reads it no closer to real than synth11 (0.768 +- 0.014 against 0.760 +- 0.002), short of F4's bar, and the
matched-core table shows why. The rows the halo was meant to lift, the 2 px group past 10 px, are still at or under the masters'
range, while the fit's shape is set by the core mismatch inside 10 px, which puts four of the ten fits on the search bound.
The go-0 read that recommended keeping the halo as an opt-in rested on a fit that pooled unmatched groups and overdrew, and it
is withdrawn.

**The next step:** fit the halo only where it is a halo, from 10 to 12 px outward, so the core mismatch no longer chooses
alpha and beta; or drop the Moffat and draw a measured per-rig residual table (the per-peak excess at each radius past 10 px,
read straight off `halo-profiles.json`), which has no bound to hit. Either way the ASI585MC Pro needs a fallback so a channel
short of points does not drop the rig, and the read is the same one: export, then the matched-core table and the classifier at
seeds 0 to 4. A train waits on a star-crop mean under 0.764 and under synth11's.

Everything stays on DE1 under `C:/temp/tianwen-scratch/tia1870`: `bin` (the frozen build and `FROZEN-AT.txt`), `synth-halo`
(the export), `census.json`, `census-halo.json`, `census-synth11.json`, `halo-profiles.json` (the per-rig table the export was
drawn from), `halo-profiles-newexport.json` and `halo-profiles-synth11ctl.json` (the profiles read above), and `reads/`
(`stats.json`, `cls-halo.json`, `cls-synth11.json`, the logs). Go 0 is kept beside it in `synth-halo-go0` and `reads-go0`,
which also holds `matched_core.py`. No model was trained.

#### Synthetics S2: the star population below the catalogue

Tracked by #1737. The injected brightness follows a luminosity function fitted to the real tiles' star counts and
extended below the catalogue's detection limit, so the input holds the faint stars a real master does (60 % of the real
stars over 5 sigma are under 20 sigma, against 53 % injected) instead of three times the real share of bright ones.

**Built (2026-10-23), opt-in:** `dataset degrade --faint-stars <floor>[,<cap>]` (`LuminosityFunction`,
`InjectionPopulation.Plan`). Each master's own catalogue is fitted with a truncated power law over 10 to 300 sigma, and
the deficit under that law is counted per eighth of a decade from the floor (3 sigma) up to 10 sigma. The deficit is
drawn as individual point sources on a random stream of its own and rendered in a pass of its own. Each drawn star is
flagged `Faint` on its injection row. Without the option every draw and every clean master tile is byte-identical to
the export before. Two checks prove it on the capped export. Against a plain export of M42 alone (the one session
exported without the option), M42's master tiles and its injection rows other than the faint stars match. Over all 8
sessions, against the uncapped export, every master tile and every degraded tile the cap left untouched matches.

**The owner's decision (2026-10-23): each tile's faint light is capped, and the floor stays at 3.** Unresolved glow
belongs in the starless image, so the extension is held per tile, not on the session average. The measure is the light
of a tile's stars under 5 sigma (the catalogue's own included), as a mean level over the tile in the sky's sigma at the
master's depth. The cap is 0.1 sigma by default (`LuminosityFunction.DefaultCapSigma`). It truncates the faint draw in
drawn order and takes no random draw, so a capped tile's faint stars are the head of the uncapped tile's, and
`FaintCapped` on the row says how many were dropped. Uncapped, the validation tiles averaged 0.056 sigma with the worst
at 0.232. SMC averaged 0.109, with 14.5 % of its injected light under 5 sigma, against synth4's 0.019 (worst 0.096). The
preview at 0.1 touched 33 of the 320 validation tiles, 18 of them SMC's, and took SMC's mean from 0.109 to 0.082 sigma.
The export bears it out: 33 tiles touched, SMC 0.082 sigma (worst tile 0.1005), all sessions 0.052. SMC's faint stars
went from 137 a tile to 80. `StarInjectionTests` pins the drawn distribution, the cap and the draws the cap leaves alone.

**The read (2026-10-23), the 8 validation sessions, 40 cells each, `--faint-stars 3` at the default cap.** The review's
instruments were re-run on copies. Real's tiles are the masters as exported. Each synthetic is read twice: as exported,
and re-stretched by the master's own stretch in place of the plate's. The second reading takes out a confound the
review's table carried.

**Corrected (2026-10-23, the colours leg):** the re-stretched columns were first read through a re-stretch that measured
the master's stretch with the wrong absence rule. It skipped every zero and NaN of a channel. The exporter
(`Image.MtfStretchParameters` with `Image.AbsentPixels`) skips only the canvas ring: border-connected pixels that are
zero in every channel or NaN in any. On Lobster's flip b and the three HIP 80609 masters that moved green's floor and
balance, so green read 6 to 15 % high on bright pixels. The corrected re-stretch reproduces every validation session's
real tiles from its master to f16 rounding, and synth11 to f16 rounding as well. synth11 is synth4's export
re-stretched by the exporter itself (`--stretch-from master`); the worst pixel is 6.1e-4. The table and the bullets
below are the corrected read. Where the first read differed, its value follows in brackets.

| | real | synth4 as exported | synth4 re-stretched | faint3c as exported | faint3c re-stretched |
|---|---|---|---|---|---|
| stars over 5 sigma a tile | 140.2 | 108.6 | 111.1 | 110.5 | 113.3 |
| share at 5-20 / 20-100 / 100-1000 sigma | 59.6 / 38.3 / 2.07 % | 52.9 / 41.1 / 6.00 % | 54.7 / 43.0 / 2.33 % (54.3 / 43.2 / 2.56) | 53.7 / 40.5 / 5.84 % | 55.6 / 42.1 / 2.27 % (55.2 / 42.3 / 2.48) |
| star colour R/G, B/G at 20-100 sigma | 1.09 / 0.63 | 0.96 / 0.81 | 1.11 / 0.64 (1.24 / 0.75) | 0.96 / 0.81 | 1.11 / 0.65 (1.24 / 0.75) |
| classifier, whole crop | | 0.81 (the review) | 0.818 (0.799) | 0.772 | 0.757 (0.770) |
| classifier, star-centred crops | | 0.79 (the review) | 0.764 (0.761) | 0.780 | 0.716 |

- **The stretch made the top-heavy population.** The plate's stretch is not the master's. Read through the master's own,
  synth4's share over 100 sigma falls from 6.0 % to 2.33 %, against real's 2.07 %, with no change to the stars. A
  synthetic's share is compared re-stretched from here on. The cause is #1776: the export stretched a tile with the
  plate's parameters, not the master's. It predicts that R2w's export (synth11, `--stretch-from master`) already has
  the right share over 100 sigma, as exported.
- **The faint extension moves the shares 0.9 point and the count by 2 a tile.** At 5-20 sigma that leaves 55.6 % against
  59.6 %, 4.0 points short of the 1 point asked. Over 100 sigma, 2.27 % against 2.07 % is within it. The count is
  113.3 a tile against 140.2, 19 % short of the 10 % asked. Per session, the count is short where the field is crowded:
  Lobster 136/126/131 against 188/159/175, HIP 80609 129/112/115 against 172/138/140. SMC is at 82 against 87, and M42
  over at 76 against 63.
- **The count's shortfall is not in the population.** A Lobster tile is injected with about 415 catalogue stars over 5
  sigma (147 at 5-10, 105 at 10-20, 121 at 20-100, 41 over 100), and the faint draw adds 25 at 5-10. Yet the detector
  finds 136 on the synthetic tile and 188 on the real one. Missing stars cannot be the cause, since more are injected
  over 5 sigma than are found on either. What the detector loses is in how a synthetic star reads: its profile, the
  warp's blur and blending in a crowded field. That is S1's and the PSF's ground, not the luminosity function's.
- **The noise level does not explain the count.** The synthetic's sigma is above real's on Lobster (0.0080 against
  0.0068), which raises its 5-sigma threshold in flux. So each synthetic tile was recounted at the paired real tile's
  threshold: the same session and cell, the same estimator and detector, and the same 256 px footprint, both tiles
  through the master's stretch. The capped tiles come to 115.9 a tile, 17.3 % short, against 19.2 % at their own sigma;
  synth4 comes to 113.0, 19.4 % short. The per-tile sigma ratio's median is 1.057 over all sessions (first read 0.994).
  On Lobster's three sessions it is 1.17, 1.11 and 1.13, and the recount gains 6, 4 and 5 stars a tile against
  shortfalls of 53, 34 and 44. On HIP 80609 it is 1.02 to 1.06, and the recount gains 0.5 to 3 stars. It is 1.00 on M42
  and SMC. The count barely answers its threshold, because the detector keeps one peak in 13 px. In a crowded field it
  counts the separate peaks it can see, not the stars over 5 sigma, which is where blending and the profile come in.
- **The classifier finds the faint stars harder to tell, read re-stretched: star crops 0.764 to 0.716, whole crops 0.818
  to 0.757.** By camera, the star crops are QHY294PROC 0.780 to 0.733, ASI294MC 0.646 to 0.636, ASI533MC Pro 0.855 to
  0.806 and ASI585MC Pro 0.776 to 0.689. As exported, the star crops are at 0.780, against the review's 0.79 for synth4.
  The statistics miss what the faint stars change, since nearly all of them sit under the 5 sigma every statistic
  counts from. The classifier's spread over seeds has not been read, so a 0.048 move is a lead, not a result.

**Verdict:** the Done-when is not met. The star crops' AUC moves, but that move is a lead until the classifier's spread
over seeds is read. The stars' count and their 5-20 share are short for a reason that stars added below the catalogue
cannot fix, and the noise level is not that reason either. The option stays in as built, opt-in and capped, for
a training arm to judge. The corrected re-stretch leaves this verdict as it was. The 5-20 share is 4.0 points short,
not within 1. The count is 17 to 19 % short, not within 10 %.

#### Synthetics S3: the stars' colours

Tracked by #1738. The injected colours follow the real stars' colour distribution at each brightness. The pool's
mid-brightness stars are bluer than the fields' (B/G 0.81 against 0.63 at 20-100 sigma); the bright stars agree.

**The read (2026-10-23): the "too blue" stars were never there, and S3 closes on the read with no new option.** Two
stretch errors made them, and neither is in the star colours. As exported, the export stretches a stars pair with the
starless plate's parameters, not the master's (#1776). Re-stretched, the S2 read used a re-stretch whose absence rule
was not the exporter's, which moved green's master floor on four of the eight sessions (S2's read above, "Corrected").
Read through the exporter's own master stretch, the injected colours already match real's in every band. The injector
draws a whole catalogued star and keeps its amplitude per channel (`InjectionPopulation`, R1). The pool refuses a
diverged fit (#1867): a star whose `LocalSigma` is not finite (a fit with its centre outside the frame; a star on the absent
ring keeps its sigma), and an
unsaturated one whose amplitude in any channel is above `OvershootMargin` (4) times the frame's clip (the master's unit-scale
divisor) less its own sky. Neither is drawn, nor carried as a lobe; both still count as sites, and
`InjectionPopulation.Refused` says how many.
The plate builder refuses the overshooting half at the source (#1867): `ClassicalStarRemover` leaves an unsaturated fit whose
amplitude in any channel is non-finite or above `OvershootMargin` (4) times that channel's own brightest covered sample
(`AmplitudeOvershoots`, no sky taken off) as `NoFit` before subtracting it, so no reader of the catalogue meets a diverged fit as a
Subtracted Moffat. A fitted pair passes the same check and stays a knot if either star fails it; that is a backstop, since the
pair's own acceptance tests (centres within 2.5 FWHM of the wide fit, a cost under it) have not been seen to let one through. The margin, in both guards, departs from the
leg's wording ("above the ceiling less the star's sky"): the ceiling is a pixel mean and a Moffat's continuous peak sits above it,
at worst about 1.7 times for a sharp unsaturated star, so 1x would refuse real stars. The margin assumes stars of at least
about 1 px FWHM: a pixel-integrated beta-3 Moffat on a pixel corner peaks 3.2 to 3.4 times its brightest pixel at 1.0 px and
4.5 to 4.9 times at 0.8 px (#1873's review), so a fully undersampled field would lose real stars to `NoFit`. None is catalogued:
over the 201 sessions of the 2026-10-12 bake the sharpest median sub FWHM is 1.60 px and the sharpest single sub 1.56 px
(`stats/psf-sessions.jsonl`), so a field under 1 px is a new case to measure before it is baked. The plate builder takes no sky off because
the fit's sky is the luminance's: taken off a colour channel's ceiling, it left a channel whose brightest sample sat under the
luminance sky a threshold of 0, and refused every star with light in it (#1873's review); against a divergence of 1e12 the margin
dwarfs any sky. A fit centred outside the frame with a sane amplitude is still subtracted, saturated or not, deliberately: on a
ringless master that is a real star the frame's edge cut (the Orion master's 700-sigma star was fitted 3 px past the last present
column), and refusing it would leave half a star on the starless plate. Its row's `LocalSigma` is NaN, and that is the marker the
next reader sees: the injector refuses it. The one edge case the guard does refuse is deliberate: a star whose core lies off the
covered sky, fitted to a peak past 4x the covered maximum, reads as unsaturated (no core pixels to flag) and is left as `NoFit`
(`AnUnsaturatedEdgeFitAboveTheCeilingIsNotSubtracted`), because with no core in the data nothing constrains that amplitude, and a
subtraction at it is the huge hole this guard exists to stop. The cost is that star's wings left on the plate. The injector's guard also covers catalogues built before this, read as before (the
catalogue format is unchanged). **Plates and the export must be
rebuilt**, and the synthetics review's instruments (`training/star-removal/domain/`) read on that re-export before any
training run uses it; that re-export is the owner's. A drawn colour is
therefore a real star's colour at that star's own brightness. An option drawing colours from the real distribution
would draw what the pool already draws. The training session's call (2026-10-23) was to add none.

Measured over the 8 validation sessions, 40 cells each, with the review's `domain_stats` colour: the median, over stars
in each band, of the stretched peak over its 7 to 8 px annulus, R/G and B/G. Real is the masters as exported. synth4 is
read as exported and re-stretched to the master's own parameters. The re-stretch uses the exporter's absence rule and
matches synth11 to f16 rounding; synth11 is the same export re-stretched by the exporter itself.

| median R/G, B/G | 5-20 sigma | 20-100 sigma | 100-1000 sigma |
|---|---|---|---|
| real | 1.333 / 0.510 | 1.086 / 0.627 | 1.014 / 0.912 |
| synth4 as exported (the plate's stretch, #1776) | 0.969 / 0.777 | 0.959 / 0.808 | 0.990 / 0.919 |
| synth4 re-stretched | 1.325 / 0.532 | 1.109 / 0.644 | 1.049 / 0.857 |
| synth4 re-stretched, the session mix held to real's | 1.321 / 0.527 | 1.091 / 0.639 | 1.046 / 0.862 |
| real over the same sessions | 1.333 / 0.510 | 1.086 / 0.627 | 1.027 / 0.896 |

- **Within 0.05 of real in every band, re-stretched, except B/G over 100 sigma pooled (0.055).** That is the session
  mix, not colour. Re-stretched, Lobster's default session has no synthetic star over 100 sigma against 125 real ones
  (B/G 0.95), and M42's B/G 0.77 to 0.81 weighs more among the synthetic stars. Each synthetic session's stars were
  resampled to real's count of that session in that band, over the sessions both hold. That leaves every band within
  0.035: +0.019 / -0.035 over 100 sigma, each median's bootstrap spread 0.006 or under.
- **Lobster's missing stars over 100 sigma are a ceiling, not a cap.** Through the master's stretch no pixel can read
  more than (1 - its sky) over the tile's sigma. On Lobster's 40 cells that is 111 sigma on the real tiles (88 to 122)
  and 95 on the synthetic (89 to 101), because the synthetic's stretched luminance noise is 16 % higher (0.00785
  against 0.00676, the same sky of 0.249). Both reach the stretched ceiling: the brightest pixel is 0.999 on each. The
  real tiles' brightest stars read 107 at the median and 115 at most, the synthetic's 91 and 97, so 32 real cells hold
  a star over 100 and no synthetic cell can. The injector holds the bright end. Unsaturated draws reach linear
  amplitudes of 0.99 / 1.25 / 0.90, past the stretch's 0.99 level (0.47 / 0.63 / 0.62). 13 saturated draws on these
  cells clip at 0.83 to 0.95, against the real stars' brightest 0.92 to 0.96 over the plate. So the band's edge sits
  in noise units, and the gap is the synthetic noise level on this rig: 1.11 to 1.17 times real's on Lobster's three
  sessions, 1.00 on M42 and SMC (S2's recount). That level is tracked by #1786. Where to look is the noise anchor's
  calibration on the QHY294PROC. **Flight TIA1781 (#1786) found a real mechanism, the synthetic background's texture carrying the
  plate's own shot noise a second time, and fixed it in code; it does not carry Lobster's excess** (1.161 to 1.159, see "S4b: the
  noise level (#1786)" below). Lobster's 1.16 is still unexplained and #1786 stays open.
- **As exported, the gap is the stretch alone.** Through the plate's stretch, B/G is 0.27 high at 5-20 sigma and 0.18
  at 20-100, and R/G 0.36 and 0.13 low. Through the master's, the same tiles are within 0.035. Once tiles are exported
  `--stretch-from master` (#1776, synth11), the as-exported column is the re-stretched one.
- **The catalogue is the master's own colour.** At a catalogued star's site, the master's peak over its annulus gives
  HIP 80609's 30-100 sigma stars 1.37 / 0.72 against the catalogue's 1.47 / 0.75. Lobster's flip b gives 0.59 / 0.44
  against 0.58 / 0.42. The stars found on the tiles were read again in linear, taken back through the master's
  stretch. There, synth4 and real agree per session: Lobster 0.60 / 0.43 against 0.62 / 0.42 at 20-100 sigma, HIP 80609
  1.37 / 0.74 against 1.38 / 0.74, and their skies agree to 1e-4. The 20-100 sigma colour depends on the session: R/G is
  0.96 to 1.04 on Lobster and 1.21 to 1.33 on HIP 80609. A pooled median is therefore a mix of sessions as much as of
  stars.
- **What is left is per session and changes sign with brightness.** SMC reads R/G 1.31 against 1.19 at 20-100 sigma
  and 1.54 against 1.67 at 5-20. HIP 80609 reads 1.48 against 1.55 at 5-20. A colour population would move both bands
  the same way. Blending in a crowded field and a star read into the next band do not, so this is S2's count ground.
- **Star-centred crops: the classifier's AUC is 0.764 for synth4 re-stretched** (0.818 for the whole crop). By camera,
  QHY294PROC 0.780, ASI294MC 0.646, ASI533MC Pro 0.855 and ASI585MC Pro 0.776. As exported, the review read 0.79. The
  stars still give the synthetics away, but not by their colour: the star crops are normalised per channel to the tile's
  own sigma, and colour already agrees. What is left is S1's halos and the count's profile and blending.

**Verdict:** #1738 closes on this read, with nothing to build. The Done-when holds on tiles re-stretched to the
master, with the session mix held (every band within 0.035). Pooled, it holds except B/G over 100 sigma, 0.055, from a
session with no synthetic star there. The as-exported numbers are the #1776 stretch, which `--stretch-from master`
removes. Instruments: `C:/temp/tianwen-scratch/TIA1737/colours` (`restretch2.py`, `bands.py`, `matched.py`,
`lincolour.py`, `catvsmaster.py`, `vs-synth11.py`); the old `restretch.py` is marked faulty in its own header.

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

##### S4b: the shared noise drawn with real's cross-structure

Tracked by #1781 (the step S4's verdict named). **What was built:** `dataset degrade --channel-noise-structure` (stars mode only, off by default, and not with `--channel-noise-correlation`, which stays as it is). Its read on the real sessions is "S4b: the read" below.

- **The measure** (`ChannelNoiseStructure.Measure`): off each session's starless plate, the same instrument as S4's (the residual from a 5x5 median, the same absence rule, the robust sum-and-difference estimator), but on square blocks of the plate, so a pair is read at every offset (dy, dx) from -2 to 2, both signs, and each channel's own lag-1 is read for the log. The ASI533's R-B on the diagonal is a lag the measure can now hold.
- **The draw** (`ChannelNoiseStructure.Mix`): channel c is `sqrt(1 - v_c)` times its own field, as the draw made it, plus a 5x5 kernel over ONE shared white field. The channels' cross-covariance at an offset is then the cross-correlation of their kernels. The kernels are fitted (Levenberg-Marquardt, a few starts) to the measure at every offset up to two, with a soft zero beyond, so a kernel stays compact.
- **Why the fit goes through the instrument.** The measure is of a median's residual, and the residual takes a good part of a structure smooth over a few pixels: a draw whose R-G is 0.144 at zero lag and 0.088 at one pixel reads 0.098 and 0.046. Fitting the kernels to the measure as raw correlations would draw about a third too little, and the classifier would read it through the same residual a second time. The fit therefore maps the kernels' correlation through the residual first, modelled as a 5x5 mean's (its autocorrelation is a fixed 9x9 table; on that draw it predicts the median's read to 0.005), so what is drawn reads, by the same measure, as what was measured. The residual's variance is taken as white noise's (0.96), so a draw whose own shape is far smoother than real's reads a little high; the warp calibration is what keeps those shapes together.
- **Each channel's POOLED lag-1 is held (not per axis).** The shared part has a lag-1 of its own, which would raise the channel's. The channel's own field is high-passed (itself less a fraction of the mean of its four neighbours, solved in closed form from the field's own lag sums) so the sum's pooled horizontal-plus-vertical raw lag-1 is the field's again, and scaled to unit variance. The per-axis lag-1 is not held, and a target beyond the beta clamp is not reached (silently); the read found both matter (see "S4b: the read").
- **An unreachable measure is shrunk, and says so.** A measure that needs kernels with more than half a channel's variance, or that the fit cannot reach to 0.01 rms, is taken at 0.85 of itself and refitted until it can be (`Shrink`; the export's log says by how much it was shrunk toward independent). A plate with no noise to read is identity and draws nothing.
- **Its own random stream.** The shared field is drawn from a `Random` seeded off the draw seed (a different constant for the stars' noise and the synthetic background's), and nothing else is drawn from it. The fields the draw's own stream makes are the ones it made without the option, and an export with it off is what it was; the degradation row carries `NoiseStructure` and `NoiseStructureShrink` only when it is on.
- **Pinned by** `StarInjectionTests` (a known diagonal structure is fitted without shrink, and drawn fields, white and warped, carry every lag as the measure reads it, hold unit variance and each channel's lag-1 and leave the draw's own stream where it was; a plate drawn with a known structure reads it back through the real median to 0.03; an unreachable measure is shrunk) and `DatasetDegradationExporterTests` (a noise-free plate reads none, and the export is the one without the option to the byte).

**The read** (per rig, the lagged cross-correlations against real, and the noise-only AUC over three seeds against synth4, as exported and re-stretched) is "S4b: the read" below; it found the own lag-1 is held only pooled, not per axis.

##### S4b: the noise level (#1786)

Folded into this flight (F5). The stars export's noise ran 1.11 to 1.17 times the real master's on the QHY294PROC (Lobster)
and 1.00 on M42 and SMC. **A mechanism is named from the code and reproduced on a constructed plate, but it does not carry
Lobster's excess: the per-session re-read on the 8 validation sessions is in "S4b: the level re-read on the validation sessions"
below, and Lobster stays at 1.16 after the fix.** The constructed-plate numbers in this section describe the mechanism only.

- **Not the noise anchor.** The exporter draws `sigma(level) * field` with the master's own recorded calibration
  (`CellMasterCalibration`: the master row's `NoiseSigma`, times `sqrt(N)` for one sub and back by `masterDepth`), and
  `SigmaAt` is the same model the master was measured by. Nothing in that chain is rig-specific.
- **The synthetic background's texture carried the plate's shot noise.** `SyntheticBackground.SignalAmplitude` sets the
  texture's amplitude at each replaced scale (1 to 8 px at a 4-PSF keep) as the plate's local energy less its noise, and it
  took ONE noise variance for the whole plate (the clean pixels' robust variance, which the sky dominates), times
  `NoiseMargin` 1.2. A master's noise is not flat: its variance is linear in the level above the pedestal. Over a nebula at
  four times the sky the plate's own noise is twice the sky's, and the excess (3 sky variances of 4) was read as TEXTURE.
  The exporter then drew its level-dependent noise on top, so the fine scales, and with them the adjacent-difference
  estimate, held the noise twice. It scales with how far the nebula stands above the sky, so it is largest on a dark-sky rig
  shooting a bright nebula (Lobster) and absent on a field whose sky is as bright as its nebula (M42, SMC read 1.00).
- **Reproduced** (`SyntheticBackgroundTests.ASyntheticNebulaOnAShotNoiseRampReadsTheNoiseOfThePlateItCameFromAndNotMore`):
  a 512 px one-channel plate, sky 0.05, a smooth nebula rising to 4x the sky, noise on the shot ramp and nothing else in it.
  The background built from it and drawn with the exporter's noise read **1.20 times the plate's adjacent-difference sigma**
  over the nebula's middle 96 px (the estimator of #1786's table). That is the band's order (1.11 to 1.17) only by construction
  of the plate: the real re-read below shows the mechanism is not what Lobster's tiles hold. Texture amplitude at the nebula's top averaged 0.38 of the local noise at the first scale that failed the bound (the
  test's 0.3), from a plate that holds nothing but noise (`ANebulaOnAShotNoiseRampHoldsNoTextureAtTheScalesOfItsOwnNoise`).
- **The fix** (`SyntheticBackground.NoiseVarianceRamp`, in `Build` per channel): the noise taken out of a pixel's local energy
  is the sky's variance times the plate's own ramp at that pixel (`SigmaAt`'s clamp, `MinVarianceFraction` to
  `MaxVarianceFactor`, from the plate's coarse level above its pedestal, the sky the lower sixth of the clean levels), and the
  sky's variance is read as detail over the ramp's root. On a flat plate the ramp is 1 and the amplitude is what it was, so a
  sky-only field is unchanged. On the constructed plate the sigma ratio falls to within 1.05 and the texture holds none of the
  nebula's noise (both tests pin it, and failed at 1.20 and 0.38 before).
- **Not changed:** the large-scale-driven path (`LargeScale`, which takes its own measured noise per band), `FineStructure`'s
  steering read and the tail read, which still take one noise per plate. They read orientation and kurtosis, not the energy
  that is drawn.
- **Still owed, on the machine that holds the sessions:** re-export Lobster's three sessions and one of each other rig with
  the stars arm, re-read the median per-tile sigma ratio by `domain_stats` through the master's stretch, and expect Lobster at
  1.05 or under with M42, SMC and HIP 80609 not past 1.05. If Lobster is still above, the next suspect is the lag-1 of the warped
  field against real's (the adjacent-difference estimate falls with correlation), which S4b's per-channel structure now measures.
  Every export baked before this change drew the doubled noise. **Answered below** ("S4b: the level re-read on the validation
  sessions"): Lobster did not move.

##### S4b: the level re-read on the validation sessions

Flight #1781 (F5, `export-and-level`). The 8 validation sessions were exported twice with synth4's recipe plus the #1786 fix:
**base** (`--channel-noise-structure` off) and **struct** (on). Star placement is identical in both on all 320 tiles, and both
match synth4's injections but for the new `Lobe` field and 50 `Knots` peak amplitudes (the fix reshaping the background texture).
The struct arm's `NoiseStructureShrink` is 1 in every session (no shrink was needed). The ratio is synthetic over real adjacent-difference
sigma (1.4826 x median |difference| / sqrt 2 per channel, absent pixels masked), the mean of the 3 channels per tile, the median over the
session's paired tiles, all through the master's stretch (synthetic tiles re-stretched with the exporter's `restretch2` recipe, not the
faulty first one). Median per session, before the fix (synth4) and after it:

| session | synth4 | base | struct |
|---|---|---|---|
| Lobster | 1.161 | 1.159 | 1.156 |
| Lobster flip a | 1.092 | 1.092 | 1.095 |
| Lobster flip b | 1.104 | 1.104 | 1.082 |
| M42 | 1.000 | 1.000 | 1.000 |
| SMC | 1.000 | 1.000 | 1.000 |
| HIP 80609 | 0.997 | 0.997 | 0.997 |
| HIP 80609 flip a | 1.012 | 1.012 | 1.008 |
| HIP 80609 flip b | 1.018 | 1.018 | 1.019 |

The synth4 column is this leg's read of synth4's tiles by the one estimator above (the mean of the three channels per tile, then the
median over the session), so Lobster's flips read 1.092 and 1.104 here where #1786 recorded 1.11 and 1.13: the difference is
the estimator (this leg's mean of three channels per tile) or the re-stretch recipe (the corrected `restretch2`), not measured apart,
and the columns of this table compare with each other, not with #1786's.

- **Lobster stays above the 1.05 bar**: 1.159 (base) and 1.156 (struct) on the main session, 1.08 to 1.10 on its flips. The fix did
  not move it (1.161 before). Its red and green channels carry the excess (about 1.19) and blue sits at about 1.10.
- **M42 (1.000), SMC (1.000) and HIP 80609 (0.997 to 1.019) are inside the bar in every arm.** Nothing the fix or the structure
  changes moved them past 1.05.
- **So #1786's constructed plate did not reproduce what Lobster's tiles hold.** The doubled-noise mechanism is real in the code and
  pinned by its tests, but it is not what carries Lobster's excess on the real sessions. Not fixed in this leg.
- **The next suspect is the one #1786 named: the lag-1 of the warped field against real's** (the adjacent-difference estimate falls
  as the lag-1 rises, so a field with less lag-1 reads noisier at equal variance). The read below gives it a size: on the QHY294PROC
  rig the synthetic own lag-1 is 0.158, 0.160 and 0.146 (R, G, B) against real's 0.205, 0.179 and 0.157, which is worth a factor of
  about 1.03 on red and 1.01 on green. That is a part of the gap, not the 1.16.

##### S4b: the level's cause, a crowded master has no quiet block (2026-11-19, #1786)

Found on synth22, synth25 and synth28 with the store's own tiles, every figure in linear (a tile is taken back through its
recorded stretch) and over the master's recorded sigma. Instruments: `training/star-removal/domain/noise_*.py`.

- **The drawn noise is at the recorded level.** A synthetic clean tile (background and noise, no star) reads 0.94 to 1.09 of the
  recorded sigma at a lag of 8 px on 20 of the 22 sessions read, Lobster's among them (1.006, 0.975, 0.983); the two outside are
  the ASI294MM's M42 luminance (2.09, real's own tile 1.43) and the ASI585MC Pro's Tarantula (0.79 to 0.87). The lag-1 reading
  is lower in both real and synthetic, as correlated noise reads.
- **The recorded level itself is too high on a crowded master.** `StretchedNoise.EstimateCalibration` answers with the quietest
  quarter of its 32 px blocks, because texture only adds to a block's reading. The Lobster master holds 95 thousand catalogued stars,
  about eight per block, so no block is quiet. Two readings that do not share that fault agree with each other:

  | session | catalogued stars per 256 px tile | the plate, over the record (R, G, B) | the master away from its stars, over the record |
  |---|---|---|---|
  | Lobster Nebula, QHY294PROC | 502 | 0.76, 0.76, 0.86 | 0.73, 0.74, 0.84 |
  | Running Chicken, QHY178M luminance | 762 | 0.65 | 0.67 |
  | SMC, ASI585MC Pro (2024-09-27) | 176 | 0.97, 0.97, 0.98 | 0.96, 0.96, 0.97 |
  | 10P/Tempel 2, QHY294PROC | 129 | 0.96, 0.95, 0.96 | 0.97, 0.96, 0.97 |
  | Helix, QHY178M luminance | 34 | 0.99 | 0.99 |

  Both are the estimator's statistic as `noise_masked.py` reads it, in linear (the frame less its 4 px low-pass, MAD per block,
  the lower quartile): once over the starless plate, once over the master's pixels outside a disc round every catalogued star.
  **The option's own figure differs a little**: C# reads the plate through the stretch, divides each block by what the noise model
  says its level carries, and takes the result at the master's background. On Lobster that gave 0.81, 0.80, 0.87 where this
  table says 0.76, 0.76, 0.86; the session's log line and `PlateNoiseRatio` print the C# figure.
- **It grows with the star count.** Over the 199 sessions of the store, the plate's difference noise at a lag of 8 px over the
  record (`noise_plate_all.py`; a different statistic, which reads a few percent higher than the one above) has a median of 1.06 under
  100 stars per tile, 1.04 from 100 to 250, 1.00 from 250 to 400 and 0.92 over 400, and it is under 0.90 on 29 sessions, down to
  0.67 on an eta Carinae master. The Magellanic, Tarantula and Sagittarius masters read 1.05 to 1.19: their plates keep the grain
  of stars nobody catalogued.
- **So every export so far drew its crowded sessions too noisy**, by the table's ratio. This is the 1.11 to 1.17 of #1786: at the
  same 40 cells, synth22's Lobster draws read 1.30, 1.24, 1.19 at a lag of 8 px where the real tiles read 1.18, 1.11, 1.10, with
  the star light itself matched (13.5 % of real pixels and 12.7 % of drawn ones carry over 1 sigma of star).
- **The fix is the default since 2026-11-19 (the owner's word; first an option, `--noise-from-plate`, PRs #2215 and #2217)**:
  each session's level is the recorded one held to its plate's own estimate, per channel. `dataset degrade --noise-as-recorded`
  gives the level every export up to synth29 was drawn at, and an arm that repeats one of their recipes passes it
  (`export-synth.ps1 -Extra '--noise-as-recorded'`); the arms then running export from frozen binaries and are not touched.
  With `--noise-scale` nothing is bounded. The bound is `StretchedNoise.BoundedByPlate`; a plate that reads more changes
  nothing, and neither does one that reads under half its master (no real plate does, the store's lowest is 0.65; the log
  warns) or one with no 32 px block to read (the log warns; the session is not failed). Under `--noise-scale` the level
  stays the recorded one on purpose: the cell is the plate's own, and the noise added to it stays sized against the record
  so the export compares with the #1947 noise-check arms. On a crowded master such a cell therefore still carries the
  overstated level (`docs/known-limitations.md`). The noise is drawn at the bounded level, and the injection floor, a star's significance and the conditioning plane
  are read against it; a synthetic background's knots and kept cores and the faint stars' cap keep the recorded level their
  ranges were set against, so the scene is the one an export at the recorded level draws. The session's log line and each
  row's `PlateNoiseRatio` carry the ratio.
- **Checked on an export of five sessions** (Lobster and its two flips, Tempel, the SMC, one draw each, no faint stars): the ratios
  taken were 0.81, 0.80, 0.87 on Lobster, 0.96 on Tempel and 0.97 on the SMC. Lobster's draws went from 1.30, 1.24, 1.19 to 1.08,
  1.01, 1.04 against real's 1.18, 1.11, 1.10 at a lag of 8 px: from 10 % over to 8 % under. Tempel's and the SMC's draws went
  from 1 to 2 % under real to 5 % under.
- **What is left under real at 8 px, measured 2026-11-19: about a fifth of it is the stars under 5 sigma, the rest
  is the noise's shape.** The same five sessions exported with the default bound and `--faint-stars 3`, read at the same
  cells (`noise_paired.py`; difference noise at a lag of 8 px over the recorded sigma, R / G / B):

  | session | real master | draw, bounded | draw, bounded and faint stars | real plate | clean tile |
  |---|---|---|---|---|---|
  | Lobster Nebula | 1.18 / 1.11 / 1.10 | 1.08 / 1.01 / 1.04 | 1.11 / 1.04 / 1.05 | 0.90 / 0.85 / 0.91 | 0.82 / 0.78 / 0.86 |
  | the same, flip a | 1.13 / 1.09 / 1.09 | 1.09 / 1.03 / 1.03 | 1.11 / 1.05 / 1.04 | 0.96 / 0.92 / 0.96 | 0.89 / 0.85 / 0.90 |
  | the same, flip b | 1.14 / 1.11 / 1.11 | 1.06 / 1.01 / 1.03 | 1.09 / 1.03 / 1.03 | 0.92 / 0.88 / 0.95 | 0.85 / 0.82 / 0.88 |
  | 10P/Tempel 2 | 1.07 / 1.07 / 1.06 | 1.02 / 1.01 / 1.01 | 1.03 / 1.01 / 1.01 | 1.02 / 1.01 / 1.01 | 0.97 / 0.95 / 0.96 |
  | SMC (2024-09-27) | 1.14 / 1.19 / 1.12 | 1.08 / 1.13 / 1.09 | 1.10 / 1.14 / 1.10 | 1.05 / 1.06 / 1.04 | 0.99 / 1.00 / 1.01 |

  The faint stars add 0.3 to 3 % to a draw (most on Lobster), and with them the draws stand 1 to 7 % under the real
  tiles, 4 to 6 % on most rows, where the recorded level stood 10 % over on Lobster. Of the gap from the bounded draw
  to the real master that is 0.22 of the 0.99 the table's fifteen cells sum to, a fifth: 6 % on Tempel and 19 to
  31 % on the other four (as first written the heading said a third; these are the table's two-decimal figures).
  The rest is not stars: the clean tile (no star at all) reads 3 to 9 % under the real PLATE at this lag on every
  session (the table's figures, the SMC's blue the least; 4 to 10 as first written), the sparse Tempel included,
  while the two agree in the estimator's band (the frame less its 4 px low-pass). So the real noise holds more power
  at 8 px and beyond than the drawn noise does at equal fine-scale noise, which is the shape #1847 names (the drawn
  field's lag-1 is under real's too). The level is right where the estimator reads it; the shape is #1847's.
- **R4e is the arm registered on it** ("R4e, pre-registered 2026-11-19" in the arms above): R4c's recipe exported with the default bound and `--faint-stars 3`, the export this section measured. Whether a model trained on it does better on the crowded rows is R4e's question. Answered 2026-11-20: it does not ("R4e's read" under that registration: the Tarantula's crowded flat 89.1 against R4c's 90.4, killed).

##### S4b: the read

Residual cross-correlation of the tiles AS EXPORTED (not re-stretched), by rig, at lags (dy, dx) 0 to 2, against real, synth4 and the
zero-lag `corr` arm of #1739. `RG 0,1` is red against green one pixel along x; `RR 0,1` is a channel's own lag-1. The full table is in
`lagcorr.txt` beside the exports (`C:/temp/tianwen-scratch/tia1781`). All rigs pooled:

| source | RG 0,0 | GB 0,0 | RB 0,0 | RG 0,1 | GB 0,1 | RB 0,1 | RG 1,0 | RG 1,1 | RG 0,2 | RR 0,1 | GG 0,1 | BB 0,1 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| real | .099 | .037 | .029 | .052 | .028 | .018 | .050 | .025 | -.003 | .205 | .181 | .162 |
| synth4 | .021 | .011 | .010 | .009 | .004 | .004 | .009 | .006 | .001 | .173 | .167 | .155 |
| corr (#1739) | .079 | .027 | .019 | .017 | .007 | .005 | .019 | .003 | -.005 | .173 | .167 | .155 |
| base | .021 | .011 | .010 | .009 | .004 | .004 | .009 | .006 | .001 | .173 | .168 | .155 |
| struct | .079 | .031 | .024 | .041 | .026 | .013 | .046 | .012 | -.008 | .175 | .138 | .170 |

By rig, red-green at lag (0,0) and (0,1), and the own lag-1 of green (real / struct):

| rig | RG 0,0 | RG 0,1 | GG 0,1 |
|---|---|---|---|
| QHY294PROC | .101 / .090 | .053 / .041 | .179 / .120 |
| ZWO ASI294MC | .109 / .119 | .042 / .047 | .261 / .327 |
| ZWO ASI533MC Pro | .094 / .062 | .060 / .050 | .171 / .105 |
| ZWO ASI585MC Pro | .066 / .050 | .048 / .034 | .169 / .286 |

- **Base is synth4**: with the structure off the cross-correlation is the near-zero of synth4 at every lag, so the #1786 fix changes
  none of it (as expected; it moves a level, not a structure).
- **Struct draws real's lagged cross-structure**: the lag (0,1) and (1,0) red-green, green-blue and red-blue values come to within
  about .01 of real's pooled (RG 0,1 .041 against .052; GB .026 against .028; RG 1,0 .046 against .050), where `corr` had
  only the zero lag (RG 0,0 .079 against real's .099, and lag (0,1) .017) and synth4 none. The (1,1) and (0,2) values are small in real
  and small in struct (RG 1,1 .012 against .025).
- **Where it overshoots or undershoots**: on the ASI533MC Pro the struct blue lags are too strong (RB 1,1 +.078 against real's +.050,
  RB 0,2 -.050 against -.022). Pooled zero-lag red-green is .079 against real's .099, the same as `corr`'s.
- **The structure moved a channel's OWN lag-1 the wrong way on green**: pooled green .138 against real's .181 (base .168), by rig
  QHY294PROC .120 against .179, ASI533MC Pro .105 against .171, but ASI585MC Pro .286 against .169 and ASI294MC .327 against .261.
  Red and blue are within about .03 of real's pooled. The structure is drawn for the cross terms, and the own lag-1 is not held to
  real's; that is the second thing the next leg should look at beside the level.
- **Why the own lag-1 moved** (a probe on the real 585, 533 and QHY294 plates: `ChannelNoiseStructure.Measure`, then `Mix` over
  `NoiseField.Warped(256, 256, 16, rng, 0.5)`, 8 seeds; a copy of `HoldLagOne` matched `Mix` bit for bit):
  (a) `HoldLagOne` holds only the POOLED horizontal-plus-vertical raw lag-1, with an isotropic 4-neighbour high-pass, so the per-axis
  lag-1, which the classifier reads along x, moves freely. On the 585 green's raw lag-1 goes x .444 to .599 and y .453 to .296, and its x
  box-residual .299 to .463; that is the tiles' .154 to .286.
  (b) The fit (`FitKernels`/`Residuals`) constrains only the cross-correlations, so it accepts kernels whose shapes the plate does not
  have. The 585's green kernel is horizontally elongated (own lag-1 x .82, y -.17) and paired with a vertical red, although the plate's
  cross-correlations and its green residual are near-isotropic. The 533's green kernel is a checkerboard (box residual along x -0.50).
  (c) On the 533 red's shared share sits at the 0.5 cap, and `HoldLagOne`'s beta pins at -1.95 in 2 of 3 seeds, so even the pooled raw
  lag-1 is not held (.460 to .445). A beta at its clamp is silent in the export's log.

The noise-only classifier (`domain_classifier.py`, mode `noise`: 16 px crops with no pixel over 4 sigma above the local sky, less
their 5 px box mean; leave one camera out over the 8 sessions), run on the CPU at seeds 0, 1 and 2 over base and struct as exported
and re-stretched to the master's stretch (`-rs`). Its AUC is the mean of the four held-out cameras' folds. synth4, `corr` and their
`-rs` rows are #1739's runs (`tia1739/cls.txt`); this leg's are in `C:/temp/tianwen-scratch/tia1781/cls.txt`. Mean over the three
seeds, the seeds' range in brackets:

| source | AUC | QHY294PROC | ZWO ASI294MC | ZWO ASI533MC Pro | ZWO ASI585MC Pro |
|---|---|---|---|---|---|
| synth4 | .699 (.687 to .715) | .729 | .616 | .660 | .792 |
| corr (#1739) | .726 (.714 to .745) | .737 | .629 | .745 | .796 |
| base | .697 (.690 to .706) | .731 | .601 | .654 | .801 |
| struct | .659 (.646 to .681) | .818 | .587 | .739 | .492 |
| synth4-rs | .690 (.675 to .701) | .724 | .606 | .655 | .775 |
| corr-rs (#1739) | .721 (.716 to .727) | .732 | .624 | .734 | .795 |
| base-rs | .691 (.678 to .706) | .720 | .613 | .642 | .789 |
| struct-rs | .671 (.661 to .690) | .819 | .587 | .761 | .516 |

- **Base is synth4 to the classifier**: .697 against .699, every rig within .015, inside synth4's own spread. The #1786 fix moved
  nothing a noise crop shows, as the level read above found.
- **Struct's mean falls below synth4's spread** (.659 against .687 to .715; re-stretched .671 against .675 to .701), but **the fall
  is one rig's, and two rigs rise**. On the ASI585MC Pro the AUC drops from .80 to .49 (.52 re-stretched), and at seed 2 to .43, under
  chance: the classifier trained on the other three rigs now reads the 585's synthetic crops as more real than real, which is a
  difference too, not a match. The QHY294PROC rises from .73 to .82 and the ASI533MC Pro from .66 to .74 (.76 re-stretched), both far
  outside synth4's spread; the ASI294MC is unmoved (.59 against .60 to .62).
- **The rises follow the own lag-1 the structure moved** (the lag table above): green's lag-1 fell to .120 against real's .179 on the
  QHY294PROC and to .105 against .171 on the ASI533MC Pro, and rose to .286 against .169 on the ASI585MC Pro. The cross-structure came to
  real's, and each channel's own neighbourhood went away from it, by rig in opposite directions; a noise crop sees the second more than
  the first. `corr` (the zero lag alone) raised every rig, the ASI533MC Pro by .085 (.660 to .745); struct trades the zero-lag gap for a lag-1 one.
- **Lobster's sigma ratio stays 1.16** (base 1.159, struct 1.156), past the gate's 1.05.

**Verdict: F5's gate is NOT met.** The noise-crop AUC is below 0.70 on the mean (struct .659, struct-rs .671), but only because one
rig's crops crossed to the far side of chance while the QHY294PROC and ASI533MC Pro became easier to tell (.82 and .74, from .73 and
.66), and Lobster's sigma ratio is 1.16, not within 1.05. `--channel-noise-structure` is **not proposed for adoption** and stays off by
default; the owner decides.

###### S4b: what it needs before another read

Tracked by #1847 (the follow-up; #1781 is closed by the PR that built the option, and this is what it did not deliver). Needed: the kernels' own shapes constrained, per axis, and each axis's lag-1
held (today only the pooled horizontal-plus-vertical raw lag-1 is held; see the diagnosis above), and Lobster's level excess found
(red and green at 1.19, blue 1.10, the #1786 mechanism ruled out above; #1786 stays open).

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

Tracked by #902. State 2026-11-11: P4.0 and P4.1 are done (2026-10-03). The arms run since (R2a to R3s in
section 6) took their names from the rounds, not from this table's R2 to R5. P4.4, a shipped opt-in v1, is #1702
and is open.

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
