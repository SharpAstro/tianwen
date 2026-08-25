# Comet / moving-target integration

Status: **WIP.** One plumbing change is in (`StackingOptions.CometRatePxPerHour` plus the compose in
`StackingPipeline`). The rate it consumes is currently derived by hand. Everything in "Not done"
below is outstanding, and this doc exists so the measurements are not lost with the shell history
that produced them.

## What this is for

A comet is sharp in a stack only if the stack is registered on the comet. Registering on the stars
trails it, and registering on the comet trails the stars, so a finished image needs both and a
combine. The target is four artifacts:

1. **Star layer.** Ordinary star-aligned stack, then StarXTerminator keeping the STARS plate.
2. **Comet-aligned stack** of the raw lights (this is what the plumbing enables).
3. **Comet layer.** SXT each of the 135 raw lights keeping the STARLESS plate, then comet-align and
   stack those. Removing stars per frame before integrating is what keeps the trails out, rather
   than trying to reject them statistically afterwards.
4. **Combine** 1 and 3 with a screen blend.

RC-Astro CLI is present and does the SXT work: `C:/Program Files/RC-Astro/CLI/rc-astro` v2.6.5,
verbs `bxt` / `sxt` / `nxt`, driven through `RcAstroEnhancerBase`'s NDJSON protocol.

## The dataset this was measured on

| | |
|---|---|
| lights | `C:/temp/10p_tempel/2026-08-16/LIGHT`, 135 x 60 s (was `C:/temp/astro/2026-08 SV545/...` on the other box) |
| camera | QHY294C Pro, gain 1600, offset 20, -5 C, RGGB 4164x2795 |
| optics | SV 545 (f/4.5 petzval), **203 mm**; the frames' `FOCALLEN` says 205, entered by mistake |
| filter | IDAS LPS-D3, **now stamped into the 135 lights and the 51 flats** (see "The headers were amended") |
| target | 10P/Tempel 2 |
| site | -37.876389, 145.178056 |
| span | 10:53:18 to 14:25:34 UTC, 3.538 h |

**Only the raw data crossed to the second machine, so everything derived has to be rebuilt** (checked
2026-08-25). `C:/temp/10p_tempel` holds the lights, the calibration frames, Astro Pixel Processor's
own masters under `MASTER/` and its integration under `PROC/`, plus `C:/temp/10ptempel` with the
`comet.tif` / `stars.tif` plates. Absent here: the `C:/temp/comet-stack` staging tree, the
`C:/temp/comet-out` star-aligned master, and the cached ephemeris below. Treat the three paths that
follow as a record of what the first box had, not as inputs you can open.

The staging tree was **hard links**, not copies: LIGHT 135, BIAS 200, DARK 60, DARKFLAT 60, FLAT 51,
506 files and 11.2 GB referenced with nothing duplicated. Directory junctions were tried first and
are invisible to recursive enumeration (`Get-ChildItem -Recurse` walks 0 files through one), so hard
links are the working answer when rebuilding it.

`C:/temp/comet-out` held the star-aligned master, `master_10pTemepl_light_60s_-5C_g1600_drizzle*.fits`,
4215x2884x3 float, `STACK_N=135`, plus a `masters/` calibration cache. **Its name preserves the
`OBJECT` typo, which is the tell that it predates the header fix**: a re-run now produces
`master_10PTempel2_...` and, more to the point, a different white balance, since SPCC can finally see
the filter. So artifact 1 has to be rebuilt regardless of which machine you are on.

## Measured

The comet track, from two independent routes that agree:

| | |
|---|---|
| rate | **dx +10.897 px/hr, dy +6.412 px/hr** (12.64 px/hr, PA 152 deg) |
| total drift | 44.7 px over 3.538 h |
| per 60 s sub | **0.211 px**, so no intra-frame trailing and no need to split subs |
| departure from linear | 0.185 px worst case, 0.080 rms (a quadratic term would give 0.019) |

The field, which is what says the rate is usable:

| | |
|---|---|
| solved plate scale | **4.7172"/px**, i.e. a focal length of 202.4 mm against an actual **202.5 mm** |
| header `FOCALLEN` | 205, entered by mistake, so anything derived from it (4.6586"/px) is 1.2% out |
| dither / drift | 88.6 px, i.e. **twice the comet's own track** |
| field rotation | 0.0368 deg, monotonic across the run |
| scale stability | 0.028% |
| median FWHM | 2.15 px (HFD 2.65), already at critical sampling |
| SIP rms after the solver fix | 0.11 px |

**That first row is an independent check on the solve, not a complaint about the header.** The
solver recovered 202.4 mm knowing only the pixel size and the stars; the optics are 202.5. Agreeing
to 0.05% with a number it never saw is the strongest evidence here that the WCS can be trusted
quantitatively -- which is exactly what the ephemeris-to-pixel step in item 1 needs of it. The 205 in
`FOCALLEN` was a configuration slip rather than an optical fact, so the 1.2% was never the glass
disagreeing with the solve; it was the profile disagreeing with both. Treat 203 mm as the figure of
record for this rig.

## Two things that decide the design

**The compose happens in CANVAS space, after the star solution.** Dither is 88.6 px against a
44.7 px comet track, so in frame pixels the dither dominates and a frame-space shift is simply the
wrong quantity. Once the star solution has absorbed dither and field rotation, canvas space is the
one basis where the target's own motion is clean. A rate is also invariant under the later canvas
shift, which is a constant translation, so it needs no knowledge of the final canvas origin. The
reference frame needs no special case because its dt is zero.

`System.Numerics.Matrix3x2` uses the row-vector convention, so `starSolution * translation` applies
the star solution first and then translates. That ordering IS the canvas-space property; reversing
the operands silently gives a frame-space shift that looks plausible and is wrong.

**A geocentric ephemeris is not good enough, and this is now measured rather than asserted.** Two
frozen Horizons tracks of the same body over the same instants, differing only in where the observer
stood, are pinned by `CometRateSolverTests.AGeocentricEphemerisWouldGetTheRateWrong`: the fitted
rates differ by **0.780 px/hr, i.e. 2.73 px accumulated over the run** (the hand-derived figure was
2.74) against a 0.11 px SIP residual.

**The error is in HEADING, not speed**, which is the opposite of what the numbers first suggest and
the reason a magnitude comparison would wave it through. The two speeds are 12.646 and 12.805 px/hr,
within 0.16 of each other; the rate VECTORS differ by 0.780 px/hr because the parallax term sweeps
ACROSS the field rather than along the comet's path. That is **3.44 degrees** of heading error, and a
heading error is precisely what smears a stack over three and a half hours. Note also that what
corrupts a rate is the parallax term's CHANGE, not its size: the offset itself runs 3.27 px down to
0.68 px here, and a constant offset would simply shift where the comet sits on the canvas, which
registration absorbs without complaint.

**And the solver's own residual will not catch a heading error -- it PREFERS one.** `MaxResidualPx`
measures how straight a track was, never whether it pointed the right way, and the geocentric track
is *straighter*: parallax is exactly what bends the topocentric one, so removing the observer removes
the curvature along with the correctness. Measured, **0.0142 px geocentric against 0.1589 px
topocentric** -- a factor of ten in favour of the answer that is wrong by 3.44 degrees. A quality gate
on that number alone would not merely fail to reject a bad ephemeris, it would rank it first.

The budget is tight enough to make this matter: holding the cross-track smear under the WCS's own
0.11 px residual over a 44.7 px track allows a heading error of **0.141 degrees**, and geocentric is
24x that. Two defences follow, and neither is the residual. Ask topocentrically by construction,
which is what `HorizonsObserverSource` does; and check the prediction against the nucleus's own
measured centroids (item 2 below), where a heading error appears as a GROWING cross-track offset --
about 2.7 px by the end of this run against a 2.15 px FWHM, which is unmissable.

So `CometEphemeris.TryGetEquatorialJ2000`, which is
geocentric, cannot drive this; it needs a Horizons OBSERVER ephemeris at the site in the header's
`SITELAT`/`SITELONG`. One WAS cached at `C:/temp/eph-1min.txt`, 221 samples on a 1-minute grid; it did
not cross to the second machine, so it needs re-fetching. The target to ask about can now be read off
the frames rather than typed, since `OBJECT` is `10P/Tempel 2` and `CometDesignation.TryParse` takes
`10P` off the front of it.

Worth being precise about why the mount being polar aligned does not remove this: diurnal parallax
is a change in the OBSERVER's position, not a rotation of the field, so no amount of tracking
accuracy addresses it.

## The rate is derived in code (was items 1 and 3, done)

`stack --comet [designation]` registers on the body with no hand-computed constant anywhere.
`--comet-rate dx,dy` is the offline counterpart and the override, and wins when both are given.

The chain, all of it inside `ProcessLightGroupAsync` before the first frame is registered:

| Step | Where | Note |
|---|---|---|
| Resolve the designation | `CometTrackRequest.TryBuild` | `--comet` with no value reads `OBJECT`; `10P/Tempel 2` compacts to `10P`, which is what Horizons' `DES=` takes |
| Derive the window | same | spans every frame in the group, padded one step each side so no epoch is extrapolated |
| Plate-solve the REFERENCE frame | `CatalogPlateSolver` | the one place the pipeline solves anything but the finished master |
| Fetch a topocentric track | `HorizonsObserverSource` | `SITE_COORD` from the frames' own `SITELAT`/`SITELONG`/`SITEELEV` |
| Fit the canvas rate | `CometRateSolver` | OLS per axis, reports a straightness residual |

Three decisions worth not re-litigating:

- **The reference frame has to be solved, and nothing solved it before.** Registration is
  frame-to-frame star-quad matching and never needed to know where the sky was; the only solve was of
  the finished master, in `MasterPostProcessor`, which is far too late. The master's WCS would
  actually serve -- it differs from the reference frame's by a translation, which a rate is immune to
  -- but only after integration, and the rate is needed during it.
- **An unknown site is a refusal, not a geocentric fallback.** Horizons answers a geocentric query
  perfectly happily and the result is wrong by 3.4 degrees of heading. Since it also fits a
  *straighter* line than the correct track, nothing downstream can catch it, so declining and letting
  the caller pass `--comet-rate` is the only defence. `SITEELEV` is different and does default to sea
  level: it is worth well under a thousandth of a pixel.
- **The residual is logged and nothing gates on it.** It measures how straight the track was, never
  whether it pointed the right way. It is there to catch a body too fast for one linear rate.

`SITEELEV` now round-trips through `ImageMeta.SiteElevation`; the Horizons query already took an
elevation, so reading it beat passing a zero that looks like a measurement.

**Reading the body off `OBJECT` needed a parser fix, and it also fixed the search box** -- a separate
function that would otherwise have made the leniency look done while still failing.
`CatalogUtils.TryGuessCatalogFormat` decides a digit-leading string is a comet via `IsNumberedShape`,
and was handing it SPACE-STRIPPED input. That is fatal to the distinction, because the only thing
separating a named comet from a catalogued object that merely starts with digits is the orbit letter
sitting immediately against the number -- and stripped, `10P Tempel` and `30 Doradus` are both
`<digits><PDI><letters>`. The guesser now probes the original string too, and the probe still refuses
a bare letter tail, so 30 Doradus keeps its own catalog. Both directions are pinned.

## The colour calibration is stamped, and inheritable (was item 1, done)

The master now carries `WBSOURCE` / `WBRED` / `WBGREEN` / `WBBLUE`, and `stack --inherit-wb <master.fits>`
takes a calibration off another master instead of solving its own. It reads back as
`ImageMeta.ColourCalibration`.

**The justification turned out to be broader than "the comet layer is starless", and the stamp is what
revealed it.** The first comet-aligned master stamped `WBSOURCE = SKYBG` with a triple of
(2.000, 1.000, 1.667) -- both outer values pinned at the grey-world clamp -- where the star-aligned
master of the same night solved a real `SPCC` (1.522, 1.000, 1.776). A comet-aligned stack **cannot
calibrate itself even with its stars present**: they are 45 px streaks, so the photometric fit has
nothing to match against the catalogue and silently falls back. That was equally true before this
change; there was simply no way to see it. So artifact 2 needs an inherited calibration just as much as
artifact 3 does, and the header is now the thing that says which one a file got.

Two design points worth keeping:

- **The stamp is an APPEND, not a card in `IntegrationFitsWriter`.** The white balance does not exist
  when the FITS is written -- SPCC is solved inside `MasterPreviewRenderer`, by which point the file is
  on disk. Appending with `FitsHeaderEditor` costs milliseconds; reordering the write around the render
  would be a real refactor for nothing.
- **Only the white balance is inherited.** Background neutralisation stays per-plate, solved from a
  plate's own pixels, because grafting one plate's bg-neut onto another whose background differs
  double-corrects it into a cast -- the regression `--split-plates` already learned. The measured sky
  background is NOT stamped -- but see the open question below, because that call rests on a precedent
  that may not transfer.

`FitsHeaderEditor` grew `SetNumericCardAsync` for this (a WB multiplier is a number, and the string
setter would have quoted it, making it a string to any reader that types its cards).

## What the star-removal experiment settled (2026-08-25, on the Adreno box)

Measured before building anything, because two of these decide the shape of artifacts 3 and 4.

- **`sxt` does NOT remove the comet, and that is structural rather than lucky.** The worry was
  reasonable -- a nucleus is point-like and a single 60 s sub has low SNR -- but StarXTerminator
  removes POINT sources, and a coma is extended, so it reads as nebulosity. Tested on raw light
  0120: every point star stripped, the coma plainly intact and in fact easier to see with the stars
  gone. **A protective mask is therefore insurance, not a prerequisite.**
- **If a mask is ever needed, derive it from the comet-aligned MASTER** (the highest comet SNR we
  own) and map it back into each frame through the per-frame offsets we already compute. `rc-astro
  sxt` has no mask option -- only `--stars`, `--unscreen` and device flags -- which is not an
  omission: PixInsight applies masks at the PROCESS level too, and SXT never sees one there either.
- **Feed it calibrated RGB, never the raw Bayer mosaic.** The test fed a mosaic, which `sxt` reports
  as `x1` and processes as mono; it fought the CFA grid and pushed noise **UP 11%**. It still kept
  the comet, which is the robustness result, but the production path must debayer first.
- **Registration comes from the STAR-BEARING raws, not from the nucleus alone.** Centroiding the
  nucleus on a starless frame looks attractive -- the stars that would confuse it are gone -- but a
  single centroid has no redundancy, and one star `sxt` misses, or a bright DSO, can capture it and
  wreck the stack silently. The quad match over thousands of stars is robust by construction.
  Nucleus centroiding is a REFINEMENT on top (item 1), never the primary mechanism.
- **Cost:** 28.8 s for an 11 MP frame on the Adreno X1-85, so 135 lights is about **65 minutes**.
  GPU is essential to that: RC-Astro 2.6.5 reports `auto` selecting the Adreno, and an earlier
  CPU-only run was a stale device verdict from the previous build, cleared by updating.

Which fixes the order for artifact 3: **offsets from the raws -> `sxt` the calibrated lights ->
stack the starless with those offsets.** Step 1 is why the transform cache below is a prerequisite
rather than an optimisation: a starless frame cannot be star-registered, so its transform has to
come from the original it was derived from.

## Per-frame `sxt` and Bayer drizzle are mutually exclusive

Bayer drizzle "skips the debayer step and projects each raw CFA sample onto the output grid as a
drop" -- it defers demosaicing to the very end, by design. Whether `sxt` NEEDS a debayered frame is **not yet established**, and
the answer decides this whole section.

What is measured: handed a mosaic it reports the image as `x1`, processes it as mono, removes the
stars, keeps the comet -- and noise goes UP 11%, where the same product on a 3-channel stacked
master took noise DOWN 4.5-7.1%. **That contrast does not isolate the CFA**, because the two runs
differ in two ways at once: mosaic vs RGB, and a single sub vs a 135-frame stack. The +11% may be
the CFA grid or may simply be what star removal does at single-sub SNR.

The test that separates them is the SAME sub both ways -- debayer then `sxt`, against mosaic then
`sxt`. Until that is run, treat the exclusivity below as PROVISIONAL.

**If `sxt` does need a debayered frame, the comet layer cannot be Bayer-drizzled**, since drizzle
defers demosaicing by design and per-frame star removal cannot wait for it. The cost is real: 135
dithered OSC subs is drizzle's best case. **If instead `sxt` round-trips a mosaic cleanly, the
trade disappears entirely** -- remove the stars from the raws and Bayer-drizzle those, keeping both.

| layer | Bayer drizzle |
|---|---|
| star layer, and artifact 2 (stars intact) | yes |
| comet layer / artifact 3 (`sxt` per frame) | **no** -- must debayer first |

**Watch this for the screen combine.** The two layers are then integrated by different strategies.
Harmless today, because drizzle Phase 1 runs at `OutputScale=10` -- the same grid as the reference
-- so both masters come out the same size. If Phase 2's classical 2x sub-Bayer drizzle ships, the
star layer becomes twice the comet layer's scale and the combine needs a resample. Worth a check
rather than a surprise.

If both are ever wanted at once, the answer is a rejection stage inside drizzle (DrizzlePac does
this with a median image and blot-back comparison), not abandoning drizzle -- but that is a
feature, not a flag.

## The rejector is tuned backwards for a comet stack (raised by the user, being measured)

In Astro Pixel Processor the user reached for **average + MAD rejection** on comet stacks,
"mostly to avoid excessive star streaks". That is not a preference, it is the mechanism, and our
defaults currently work against it in two ways.

**The logic inverts between the two alignments.** In a star-aligned stack a star occupies the same
canvas pixel in every frame, so it is signal, and `BuildRejector`'s generous `HighSigma: 5` exists
precisely to keep it. In a COMET-aligned stack the same star sweeps across the canvas at
12.64 px/hr against a 2.15 px FWHM, so any given pixel sees it for roughly **10 frames out of
135** -- it is now an OUTLIER, and rejection is exactly what removes the trail.

**And on this data no rejector runs at all.** 135 RGGB frames auto-select Bayer drizzle, which
integrates through a per-cell coverage map and has no kappa-sigma stage, so every streak is
retained faithfully. That, rather than anything about the compose, is why artifact 2's trails are
so stark.

Two consequences to settle by measurement: whether `--no-bayer-drizzle` alone (which restores
`SigmaClip(3, 5)`) already suppresses the trails, and whether comet mode should tighten the high
side further -- there is no knob for the pixel rejector today, only `--quality-reject-sigma`, which
drops whole FRAMES and is a different thing entirely.

**Rejection is the SECOND line, not the method.** Removing the stars per-frame with `sxt` BEFORE
stacking stays the plan, and rejection cleans up after it. Two reasons it cannot substitute:

- **It leaves residuals.** MAD rejection in APP was reached for to avoid *excessive* streaks, not
  to eliminate them -- a partial trail is still a trail.
- **It costs real signal exactly where the trails were.** Every pixel a trail crossed loses those
  frames from the average, so the trail PATHS come out noisier than the rest of the frame. Removing
  the star at frame level leaves clean data everywhere instead, and the integration keeps its full
  depth.

The two compose well in that order: `sxt` takes the stars, comet alignment smears whatever it
missed -- faint stars under its detection threshold, and residual cores -- into low-amplitude
outliers, which is precisely what kappa-sigma is good at. So the measurement below is worth having
for how much the second line contributes, not as a route to skipping the first.

## A stack MANIFEST, not a star-list cache (raised by the user 2026-08-25)

Earlier framing had this as a per-frame star-list cache, justified by speed and by artifact 3 needing
the raws' transforms for their starless siblings. That undersold it. **The real requirement is that
layers meant to be combined are built from IDENTICAL inputs**, and every run currently re-derives
everything from scratch.

Three things must be pinned, in increasing order of how badly they bite:

1. **The frame list.** A frame the star stack dropped -- `SKIP (too few stars)`, `no quad fit`, or
   `--quality-reject-sigma` -- must not appear in the comet stack, and vice versa. Otherwise the two
   layers have different depth and different noise, and a frame excluded for bad stars is exactly the
   frame you do not want silently contributing to the other layer.
2. **The per-frame transform.** Artifact 3's starless plates cannot be star-registered at all, so
   their transforms have to come from the originals they were derived from.
3. **The reference frame.** Picked by composite PSF score INDEPENDENTLY per run. A different
   reference means a different canvas origin and orientation, so the two layers do not overlay --
   the screen combine is then meaningless rather than merely inconsistent. Nothing pins this today;
   the runs have agreed only because the inputs and the scoring happened to be identical.

So the artifact is a **manifest** written by the star-aligned run and consumed by every later one:
the ordered frame list with each frame's fate (matched / skipped and why), the reference frame's
identity, and each matched frame's solved `Matrix3x2`. A later run supplies its own compose -- the
comet translation -- on top of transforms it did not re-derive.

It subsumes the cache: reusing the transforms skips measure AND register, which the stage table puts
at 44.6% + 3.8% of wall clock. But speed is the side effect. Reproducibility is the point, and
"re-run it and get a different reference frame" is the failure it exists to prevent.

Open: whether the manifest keys frames by path or by a digest of the FITS DATA section. The digest is
more honest -- today's `SITEELEV` amendment rewrote 525 headers and changed every mtime without
touching a pixel, and star positions depend only on pixels -- but a path is what a human reads in a
log. Probably both: digest for identity, path for legibility.

## Not done

Roughly in dependency order.

1. **A data-derived comet/sky mask** (raised by the user 2026-08-25; not started, and do not lose
   it -- with an extended object in the field it stops being optional).

   **The failure it fixes.** `sxt` removes POINT sources, so it correctly LEAVES an extended DSO.
   On a field like C/2025 R2 (SWAN) beside M16 that is exactly wrong for the comet layer: M16
   survives star removal, then the comet-aligned integration SMEARS it into streaks. Screen-combine
   that against the star layer, which has M16 sharp, and the nebula appears TWICE -- once crisp,
   once as a streak lying over it. 10P's field is empty enough to hide this entirely, which is why
   it has to be designed against the SWAN data rather than discovered there.

   **The mask comes from data we already produce, with no catalogue and nothing hand-painted.** The
   two stacks are a discriminator when read together:

   | | star-aligned stack | comet-aligned stack |
   |---|---|---|
   | stars, DSOs, nebulosity | sharp | smeared |
   | the comet | smeared | sharp |

   So *sharp here and smeared there* separates comet from sky by construction. Cheap forms to try
   first: per-pixel local variance / gradient energy in each stack, or simply the ratio of the two
   after matching their backgrounds. The comet layer then keeps only what the mask calls comet, and
   everything else comes from the star layer, where it is sharp.

   **This also subsumes the nucleus-protection use.** If `sxt` ever does eat a nucleus (it did not
   on 10P -- see the experiment above), the same mask is what restores it, and it is derived rather
   than a hand-placed disc around an ephemeris prediction.

   **The mask must be TAIL-shaped, not disc-shaped, and that rules out the obvious first
   implementation.** A disc around the ephemeris position is the natural thing to reach for and it
   clips the tail -- on the SWAN field the tail runs well out from the coma and stays faint where it
   crosses unrelated red nebulosity, so a generous disc would either cut it or swallow the nebula.
   The sharp-vs-smeared discriminator needs no shape assumption at all: the tail is sharp in the
   comet-aligned stack and smeared in the star-aligned one wherever it happens to reach, so it is
   selected by the same test as the coma.

   **The rigorous form: subtract a SKY MODEL from every light before comet-stacking** (the user's
   "scientific albeit extensive" option, and the one that handles stars AND nebulosity in a single
   operation).

   The star-aligned stack is already a deep, high-SNR model of the sky. For each light, warp that
   model into the frame's own geometry -- the inverse of the star transform we compute anyway --
   scale it for transparency and exposure, and subtract. What remains is the comet plus noise.
   Comet-align those residuals and stack. Stars and M16 both go, because both are sky, so this
   subsumes `sxt` rather than sitting beside it.

   **Building the model needs no mask, because of the same inversion that removes star trails.** The
   comet moves, so at any SKY pixel it is present in only ~10 of 135 frames -- an outlier -- and
   kappa-sigma rejection in the STAR-aligned stack removes it. Each alignment makes the other object
   the outlier:

   | | star-aligned + rejection | comet-aligned + rejection |
   |---|---|---|
   | yields | the sky, comet removed | the comet, sky suppressed |

   So a comet-free sky model falls out of a stack we already build, by turning rejection UP rather
   than by masking anything.

   What makes it "extensive" rather than obviously-do-this: the subtraction has to be
   photometrically honest per frame (transparency, airmass and sky level all vary across a night, so
   a single global scale will leave residuals that themselves smear), it needs the model resampled
   into each frame without introducing interpolation artifacts at star cores, and any error in it
   lands directly on the comet, which is the faint thing being measured. Worth prototyping against
   the SWAN field where the failure is visible, not against 10P where it is not.

   **Two passes, feeding each other** (the user's refinement, for the SWAN field; NOT needed for
   10P). One mask is not enough, because the thing that must be suppressed in the comet layer is
   specifically the EXTENDED NON-STELLAR sky -- `sxt` already handles the point sources, and the
   comet must not be suppressed along with them. So:

   1. From the comet-aligned stack, take what is sharp -> the **comet** mask (coma plus tail).
   2. From the star-aligned stack, remove the comet using that mask, then take what is sharp and
      non-stellar -> the **DSO** mask: M16 and any other extended structure.
   3. Suppress (2) in the comet layer; it comes from the star layer instead, where it is sharp.

   Each stack supplies the exclusion the other one needs, which is what makes it a loop rather than
   two independent thresholds.

   **10P does not need any of this**, and that is worth stating so the SWAN work is not
   retro-fitted onto it: its field holds only a few very small background galaxies, which
   comet-centred stacking smears and rejection then removes -- the desired outcome, reached for
   free. The mask is a SWAN-class requirement, driven by a bright extended nebula in frame.

   Open questions worth measuring rather than arguing: whether the mask needs feathering to avoid a
   visible seam where the coma meets the sky; whether it should be built at full resolution or
   downsampled and upscaled (a mask carries structure, not detail); and whether the smeared-M16
   residue in the comet layer is better masked out or subtracted, given the star layer already has
   the real thing.

2. **Does the background need to travel too?** Open, and deliberately not answered by assertion. The
   `--split-plates` rule says each plate solves its own background neutralisation, and that is why only
   the white balance is inherited -- but that rule governs plates carved from ONE stack, sharing the
   same pixels, whereas the star layer and the comet layer are independent integrations. There is a
   specific mechanism that could break the analogy: a comet-aligned stack SMEARS star flux across the
   frame, and field starlight is not colour-neutral, so its measured per-channel background may be
   biased relative to the star-aligned master's. A screen combine would show that as a cast.
   **Measurable rather than arguable**: once artifact 3 exists, compute both masters' background
   triples and compare. If they differ by more than the bg-neut gains' own scale (they are affine
   about 1.0 against a ~0.002 background, so read them at F4), the background has to be inherited as
   well and `BKGR`/`BKGG`/`BKGB` join the stamp.
3. **Treat the ephemeris as a SEED, not the answer.** Then centroid the nucleus per frame as if it
   were a star, fit a smooth track through the centroids with outlier rejection, and use that. The
   fit residuals double as a per-frame quality gate, which the ephemeris alone cannot give. **This is
   also the only check that can catch a wrong heading**, since the straightness residual demonstrably
   prefers the wrong track: a heading error shows here as a GROWING cross-track offset, ~2.7 px by the
   end of this run against a 2.15 px FWHM.
4. **Per-frame SXT over 135 lights**, keeping the starless plate, then integrate those comet-aligned.
   Needs item 1 first, or the layer has no colour.
5. **The screen combine** of artifacts 1 and 3.
6. **A test pinning the compose itself.** A synthetic pair with a known rate and a known dither,
   asserting the target lands on the same canvas pixel and the stars do not. That would catch an
   operand-order slip, which is the one failure the derivation chain cannot.

## The headers were amended (was item 7, done)

The frames now carry the three cards they should have carried at capture. All were written with
`FitsHeaderEditor` through the CLI, which rewrites only the primary header and copies every other
byte verbatim; frames were digested from the data section before and after and **0 had altered pixel
data or size** (186 for the first two amendments, a 30-frame sample for the third, which also
confirmed 0 header-length changes -- the card already existed, so it was replaced in place).

```
tianwen dataset tag-object --path <LIGHT> --object "10P/Tempel 2" --expect "10p Temepl" --apply
tianwen dataset tag-filter --path <LIGHT> --filter "IDAS LPS-D3" --apply
tianwen dataset tag-filter --path <FLAT>  --filter "IDAS LPS-D3" --apply
tianwen dataset tag-site-elevation --path <ROOT> --elevation 74 --expect 120 --apply
```

**`SITEELEV = 74.0`, on every frame type rather than the lights alone**, because where the rig stood
is true of a dark and a bias as much as of a light. The capture profile had recorded 120 m against a
true 74 m. **This corrects the RECORD, not a measurement**: 46 m is 0.0007% of an Earth radius, so it
moves the derived comet rate by 0.00002 px over the night -- against a 0.11 px registration residual,
which is to say never. The number that would matter is latitude or longitude, and those are why an
unknown site is a refusal rather than a fallback.

Two things came out of doing it. `FitsHeaderEditor` only knew how to write STRING cards, and a number
put through that path comes out quoted -- which reads correctly to a human and is a *string* to
anything that types its cards; `SetNumericCardAsync` + `FormatNumericCard` write the fixed format
(unquoted, right-justified to byte 30) and share every guard with the string path so the two cannot
drift. And the first run silently skipped a `MASTERDARKFLAT`, which exposed a `DarkFlat` missing from
the command's default frame-type list while its own help claimed "every frame type" -- a real gap for
anyone using the standard CMOS dark-flat workflow, not just for this one derived file.

**`FILTER = 'IDAS LPS-D3'`, on the flats as well as the lights.** The flat panel's light went through
the same glass, and `MasterGroupKey` compares lights to flat masters on filter, so tagging only the
lights would leave the pair disagreeing about the optical train. It would not have *broken* anything
here -- a filter mismatch is a 1000-point penalty in `MatchMaster`, not a gate, and this dataset has
exactly one flat group, so it would still have won on being the only candidate -- but the agreement
should be true rather than merely harmless. Bias, darks and dark-flats were left alone: no light
passes, and `MasterGroupKey` documents the filter as empty for them.

What the card buys, measured through `FilterCurveDatabase.BuildChannelThroughputs` on frame 0001:

| | R | G | B |
|---|---|---|---|
| before (no `FILTER`) | 0.3550 | 0.4064 | 0.2386 |
| after (`IDAS LPS-D3`) | 0.2488 | 0.4562 | 0.2951 |

Red loses 30% of its weight, which is the D3 being what it is: a notch filter suppressing NaI
589.0/589.6 and OI 630.0/636.4, all of them in the red CFA passband. Before the card, SPCC integrated
its stellar SEDs against QE x CFA alone -- i.e. modelled an optical train with no filter in it -- and
returned a white balance for a camera that was never used. This is the whole reason the digitised
curve exists; the frames just could not reach it.

**`OBJECT = '10P/Tempel 2'`, corrected from `'10p Temepl'`.** Not cosmetic in two ways.
`LightGroupKey` partitions lights by `OBJECT` and its slug names the master, so the typo was being
baked into every output filename (`master_10pTemepl_...`). And the card is the only place a frame
says which body it is, which the ephemeris work in item 1 has to read back -- so the *form* matters
as much as the spelling: `CometDesignation.TryParse` takes the designation off the front of
`10P/Tempel 2` and answers `10P`.

Two things deliberately left as they were. The `PROC/` outputs carry the same typo and
`FILTER = 'RGB'`, but they are Siril products of this data rather than inputs, and for the `-cbg`
plates already colour-calibrated `RGB` is the honest answer. And the `MASTER/` calibration masters
are another tool's, which TianWen rebuilds from the raw frames anyway.

## Also worth knowing

The nonlinearity number (0.185 px) is the argument for keeping a single linear rate for now, and the
place a quadratic term would go is documented on the option itself. Do not add one speculatively:
it is currently below the registration's own residual, so it would be fitting noise.

## Star-tracked capture only, and whether that can be detected

**Everything here assumes the mount tracked the STARS**, which is what this dataset did. The other
way round is a real technique -- drive the mount at the comet's own rate -- and it is not supported.

**The registration maths is the same either way, which is the non-obvious part.** If the mount
follows the comet, then between frames the STARS move across the sensor; star registration undoes
exactly that, so after it the comet drifts across the canvas at the same rate it would have under
star tracking. `CometRatePxPerHour` therefore applies unchanged. Nothing about the compose needs to
know which mode produced the frames.

What differs is **which object is trailed INSIDE each sub**, and that is not recoverable by any
alignment:

| | star-tracked | comet-tracked |
|---|---|---|
| stars | points | trailed by rate x exposure |
| comet | trailed by rate x exposure | point |
| star layer (artifact 1) | good | trailed, unusable |
| comet layer (artifact 3) | nucleus slightly trailed | sharp |

So comet tracking buys a better comet and ruins the stars, which is why the usual answer for a slow
comet is to track the stars and accept a marginally soft nucleus.

**Can an unattended run tell which it got?** In principle yes, from the field stars' elongation and
its position angle: comet-tracked frames elongate every star along the ephemeris motion vector by
rate x exposure, and we already measure per-frame median HFD and ellipticity (the inputs
`--quality-reject-sigma` uses). Comparing the measured elongation axis against the motion vector the
ephemeris predicts is a clean, cheap discriminator.

**On this dataset it is neither possible nor necessary**, and the numbers say why. At 12.64 px/hr a
60 s sub trails 0.211 px, which against a 2.15 px FWHM adds in quadrature to 2.160 px -- a **0.48%**
elongation, far under the frame-to-frame scatter of the measurement itself. Reaching a 10%
elongation would need a ~1 px trail, i.e. about 59 px/hr at this exposure, five times this comet:

| sub | trail | elongation |
|---|---|---|
| 60 s | 0.211 px | 0.48% |
| 120 s | 0.421 px | 1.90% |
| 300 s | 1.053 px | 11.4% |
| 600 s | 2.107 px | 40.0% |

The same arithmetic that makes the mode undetectable makes it **irrelevant**: where the trail is a
fraction of the seeing disc, the two capture modes produce near-identical data, and the question only
becomes worth asking when rate x exposure approaches the FWHM. A detector should therefore be gated
on that product rather than run unconditionally -- below roughly half a FWHM it would be reporting
noise. Until one exists, an unattended run should assume star tracking, which is both the common case
and the one that degrades gracefully if it is wrong.
