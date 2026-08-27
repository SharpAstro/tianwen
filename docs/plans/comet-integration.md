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
- **A raw Bayer mosaic is FINE, contrary to the first reading here.** `sxt` reports a mosaic as `x1`
  and whole-frame noise appeared to rise 11%, which looked like it was fighting the CFA grid. Measured
  properly -- per CFA plane, on background cells -- it adds no noise and leaves the CFA structure
  untouched; the 11% was an artifact of taking a MAD across a mosaic. See the section below.
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

## Per-frame `sxt` and Bayer drizzle are COMPATIBLE (measured 2026-08-25)

This was written the other way round first, on the strength of a single observation -- `sxt` handed a
mosaic reports it as `x1` and whole-frame noise rose 11% -- and the conclusion was that star removal
needs a debayered frame, which Bayer drizzle cannot provide since it defers demosaicing by design.

**Both halves of that were wrong.** Measured on raw light 0120:

| check | result |
|---|---|
| CFA structure preserved? | `|G1-G2|` median **identical to six decimals** (0.001282 before and after) |
| inpainting CFA-aware? | channel levels at star sites stay separated (R 0.028 / G 0.045 / B 0.035), tracking the background's own levels rather than converging on a common value |
| noise added? | per CFA plane, on cells `sxt` barely touched: **+1.1% / +0.1% / +0.1% / -0.3%** |

**The +11% was a measurement artifact.** A MAD taken across a whole MOSAIC is dominated by the
photosite levels themselves -- R 0.027, G 0.047, B 0.036 -- not by noise, so removing stars shifts
that distribution and the number moves for reasons that have nothing to do with the data quality.
Measuring per CFA plane, on background cells, shows no degradation.

So `sxt` takes a mosaic and returns a valid mosaic. **Artifact 3 can remove stars from the RAW lights
and still Bayer-drizzle them**, keeping drizzle's sampling on 135 dithered OSC subs, and no per-frame
debayer is forced.

Still open, and not answered by the above: whether star removal is as GOOD on a mosaic as on RGB. The
tests here show it does no structural harm and adds no noise; they do not compare removal quality
against a debayer-first run, which would need a way to emit a debayered single sub (no CLI does today).
The visual check was clean and the comet survived, so this is a refinement rather than a risk.

### `sxt` must be handed `[0, 1]`, and on ADU it fails SILENTLY (measured 2026-08-25)

Every prior caller of `IStarRemover` was `SharpenPipeline` on a MASTER, which is unit-scaled by the
time it gets there, so nothing in the repo had ever handed a star remover ADU. A calibrated 16-bit
sub is the first thing that does, and RC-Astro normalises internally and CLIPS what is already above
its range. The whole plate comes back **uniformly 1.0**: no exception, no warning, exit code 0, a
135-frame run that takes its full 18.7 minutes and writes a master that is a white rectangle.

The same real sub, through the same `rc-astro sxt` call, either side of one division:

| input | min | med | p99.9 | max |
|---|---|---|---|---|
| ADU in | 1796 | 3928 | 5372 | 65535 |
| **ADU out** | **1** | **1** | **1** | **1** |
| `/65535` in | 0.0274 | 0.0599 | 0.0820 | 1.0 |
| `/65535` out | 0.0276 | 0.0550 | 0.0729 | 0.1425 |

Two consequences worth stating separately, because fixing only the first leaves a subtler bug:

- **The divisor is the CONTAINER full scale (`BitDepth.UnsignedFullScale`), never the frame's own
  peak.** `Image.UnitScaleDivisor` falls back to the observed peak when nothing declares a saturation
  point, and that peak moves frame to frame -- a different star saturates, calibration divides by a
  different flat value at whichever pixel is brightest. Normalising a SEQUENCE frame-by-frame
  therefore rescales each frame slightly differently, which is invisible per plate and shows up in
  the master as photometric scatter. Hence `Image.ScaleToFullScaleInPlace(fullScale)`. Measured on
  the 10P set, plates written with the shared divisor agree to 0.3% (medians 0.033968 / 0.033854).
- **The plate then DECLARES its scale with `SATURATE = 1.0`**, and the two drizzle producers ask
  `UnitScaleDivisor` rather than `MaxValue`. A starless plate's brightest pixel was a star, and the
  stars are gone, so its peak understates its full scale by ~7x; a producer inferring the scale from
  the peak lands the comet layer 7x brighter than the star layer it exists to be screen-combined
  with. The switch is a no-op for raw subs, which declare no `SATURATE` and fall back to the peak
  exactly as before.

Pinned by `StackingPipelineStarlessTest`, which records what the remover was handed. Its bound is
`max <= 1` **plus** "at least one frame below full scale": the second half is what distinguishes a
shared divisor from a per-frame one, since dividing each frame by its own peak also satisfies the
first half, at exactly 1.0 every time.

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

Also worth pinning if the AHD path is ever used for one layer: **`StackDebayerAlg`** (AHD by default,
with `CentroidDebayerAlg` VNG for registration only). Two layers debayered differently differ subtly in
colour and sharpness before any intended processing touches them. Moot while both layers Bayer-drizzle,
which the mosaic measurement above makes possible.

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
5. ~~**The screen combine** of artifacts 1 and 3.~~ Done 2026-08-27, and not as a screen: the run
   writes the composite in linear light (see the last section).
6. ~~**A test pinning the compose itself.**~~ Done 2026-08-27: `CometComposeTests` puts a rotated,
   dithered frame through `CometCompose.ToCometGrid` and asserts the target lands on the reference
   pixel while a star is displaced by exactly the drift, and that the reversed operand order is
   wrong whenever the frame is rotated (at zero rotation both orders agree, which is why the test
   rotates).

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

**`<ROOT>` is only safe on a single-session folder, and the second box is not one.** The desktop
archive `C:/temp/astro/2026-08 SV545` carries a second night beside the comet one (Lobster Nebula 354
lights, SMC 240), so a recursive root run over-reaches in three ways, each of them silent.
`tag-filter` has **no `--expect` guard**, a card that is absent rather than wrong being the whole
point, so it stamps the filter onto those 594 unrelated lights as well. `tag-object` picks up the six
`PROC` products, which declare `IMAGETYP=LIGHT` and still hold the typo, tagging 141 where this box
tagged 135. And `tag-site-elevation` sweeps `PROC/` and `MASTER/` too, 1120 frames against the 525
here. Drive it **per subtree by `--path`** instead, and the exclusions hold by construction rather
than by luck.

That box was amended on 2026-08-25: `OBJECT` on the 135 comet lights only, so `PROC/` keeps its typo;
`FILTER` on 780, being both nights' lights plus the flats, the operator's own Siril product
`SMC/PROC/Small_Magellanic_Cloud-RGB-crop-lpc-cbg.fits` having already recorded `IDAS LPS-D3` for that
night; and `SITEELEV` on the same 780, which leaves its 320 bias, dark and dark-flat frames still
reading 120. That last divergence is harmless by construction: the elevation feeds the topocentric
Horizons query, which is driven off light epochs, and `MasterGroupKey` never reads it.

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

## The two-layer composite, and what it cost to learn (measured 2026-08-26)

The four artifacts are the star layer, the comet-aligned stack of raws, the comet LAYER
(`stack --remove-stars`), and the composite of the first and third. Getting the composite right
turned up five defects, four of them silent, and they share a root cause worth stating first:
**every AI-enhancer failure here was an input outside the distribution the model expects, and each
returned a plausible wrong answer rather than an error.**

| what the remover was handed | what came back |
|---|---|
| ADU (sky ~3900) instead of `[0, 1]` | a uniformly 1.0 plate, every pixel white |
| a raw Bayer mosaic instead of an image | channel-asymmetric residue: magenta streaks |
| a 0.5-normalised master instead of a sky background | the entire coma routed into the STARS plate |
| a stars-only plate (background ~0) stretched alone | 3.4% sky leakage stretched into a fake nebula |

That argues for one normalisation guard at the enhancer boundary rather than four point-fixes. None
of the four is fixed generally; the first is fixed at its call site.

### A star in a CFA mosaic is a checkerboard, not a PSF

Neighbouring pixels are different colours, and a remover trained on ordinary astronomical images
reads them as adjacent samples of one signal. Measured on a real calibrated 60 s sub, 419 stars,
residual tails in units of the frame's own noise:

| input shape | R tail | G tail | B tail | R hole | G hole | B hole |
|---|---|---|---|---|---|---|
| whole mosaic | **+15.94** | +5.22 | +3.98 | -3.71 | **-6.35** | -4.20 |
| four CFA planes | +5.73 | +5.67 | +4.81 | -3.71 | -3.61 | -3.68 |
| full-res RGB | +4.17 | +3.93 | +3.83 | -3.71 | -3.62 | -3.68 |
| full-res RGB + white balance | +4.18 | +3.97 | +3.83 | -3.71 | -3.62 | -3.68 |

Red-positive with green-negative IS magenta, and it is unique to the mosaic row. **White-balancing
first does nothing** (identical to two decimals), so the interleaving is the mechanism and the colour
balance is not. Worth recording as refuted, because it is the intuitive hypothesis.

### But splitting eats the comet, and the ordering says why

| star-removal input | magenta | comet flux kept |
|---|---|---|
| whole mosaic | present | **85.7%** |
| four CFA planes | gone (red residue -88%, green residual turns positive) | 22.4%, visible donut |
| full-res RGB | (best on stars) | 0.0% |

Splitting halves the raster, so a 9 px HWHM coma becomes 4.5 px, which is a star as far as the model
is concerned. Comet survival tracks how badly INTERLEAVED the input is, which suggests **the
checkerboard is at once the cause of the coloured residue and the reason the coma survives at all**.
If that holds, no choice of input shape separates them and the residue is something to clean up
afterwards. Hence `--star-removal-mode`, defaulting to `Mosaic`: the residue is cosmetic, a nucleus
the remover ate is not recoverable at any later stage.

Caveat on the RGB row: measured on a MASTER, where the comet is far brighter than in a 60 s sub, so
it does not strictly settle the per-frame case.

### Rejection: the comet layer and the star layer want opposite thresholds

`BuildRejector` picks the KIND by frame count; the sigma pair is a separate decision, which is why
`--reject-low-sigma` / `--reject-high-sigma` substitute into whichever kind the count chose. The
defaults are asymmetric the star-KEEPING way (`high = 5` at N >= 30).

Comet layer, 135 frames, tightening high 5 to 2.5:

| | rejected | residue (% of sky) | coma flux | median |
|---|---|---|---|---|
| drizzle, no rejection | none | 1.80-2.31% | -- | 0.0201 |
| AHD, SigmaClip(3, 5) | 0.75% | 1.22% | 100% | 0.500647 |
| AHD, SigmaClip(3, 2.5) | 2.74% | 1.06% | **96.9%** | 0.499657 |

13% less residue for 3.1% of the comet, uniform across radius (ratio 0.960-0.973 from r=0 to r=40),
plus a -0.198% median bias from the asymmetric clip. A bad trade, and the reason is worth keeping:
**the coma IS at risk from a tight high sigma**, not because it is ever an outlier -- it is not --
but because the clip bites the noise tail *riding on* the coma, and that bias is signal-dependent.
Keep the stock (3, 5) for a comet layer.

The star layer wants the opposite, because there the comet MOVES and IS the outlier. Star-aligned,
AHD + SigmaClip(3, 2.5) against the drizzle master, sampled along the nucleus track:

| offset from track | drizzle, no rejection | with rejection |
|---|---|---|
| 0 px (nucleus trail) | 35.28% of sky | **9.85%** (-72%) |
| 15 px | 6.79% | 5.91% (-13%) |
| 30 px | 3.82% | 3.36% (-12%) |
| 120 px | 0.48% | 0.49% (+2%) |

The compact trail is a genuine temporal outlier and goes; the diffuse smear is present in EVERY frame
at those pixels and rejection structurally cannot see it. Drizzle does no kappa-sigma rejection at
all, which is why the trail survived into the original star layer.

### Identifying an artifact: measure the STAGE, not the picture

The composite showed a bright 9-arcmin patch that looked like a galaxy, dark lane and all. SIMBAD has
nothing there brighter than B=15.86. Three hypotheses each looked plausible (a real object, an optical
ghost, the coma smear); tracing by stage settled it in one pass:

| stage | blob amplitude |
|---|---|
| AHD+rejected master | +0.24 sigma, absent |
| rescaled, `sxt` input | +0.24 sigma, absent |
| `sxt` STARLESS output | +0.40 sigma, absent |
| `sxt` STARS output | **+372 sigma** |

The star/starless split leaks ~3.4% of the diffuse sky into the stars side. Invisible normally;
enormous once that plate is stretched on a near-zero background. **The fix was to delete a step**:
star extraction only existed to keep the comet smear out of the star layer, and rejection had already
removed it, so compositing onto the rejected master directly removes the artifact's habitat.

### Compositing rules the failures taught

- **Composite in LINEAR light and render once.** Masking and background-subtracting in display space
  leaves a visible mask edge, because the stretch is nonlinear so a constant subtraction zeroes
  nothing. In linear light the layers simply add; screen is only a display-space approximation of it.
- **Never clip the masked contribution at zero.** Clipping rectifies the noise, its mean goes
  positive, and the whole disc gains a pedestal that reads as a hard-edged circle.
- **Feather the mask.** Smoothstep, r=38 solid to r=85 zero, against a coma reaching background by
  r~40.
- **Remove trail residue by SHAPE, not brightness.** A median despeckle cannot: the coma core is a
  bright compact feature, so any threshold catching a streak also catches the comet (a 15 px median
  took 27% off the peak). Every trail in a comet-aligned stack runs along ONE known direction, the
  drift vector, so a grey opening with a line laid ACROSS it erases anything narrower than the line
  and leaves the round coma alone. Red lost 21.1% of masked flux to it against 3.7% for green and
  blue, which is the selectivity wanted.
- **Protect the core from that opening.** An opening takes a local min then max, so it flattens a
  peak as readily as a streak (the contribution fell 37%, all at the middle). Apply it outside a core
  radius with a smooth handover.
- **Placement is astrometric, not guessed.** A comet layer cannot carry a WCS -- its stars are gone
  and its solve is correctly rejected -- so the target pixel is JPL's topocentric position at the
  REFERENCE epoch pushed through the star layer's WCS. The reference epoch is the right instant
  because at dt = 0 the comet compose is the identity.

### Still open

- **`sxt` removes the comet's central condensation.** It is compact and star-like, so the model takes
  it and leaves the diffuse coma. The 27% flux loss at r=0 understates it: what goes is the sharp part
  that makes a comet read as a comet. The core should come from the raws, or removal should be masked
  away from the comet.
- **Two masks, and the coverage arithmetic that limits them here.** The clean design is to mask the
  comet OUT of every frame when building the star layer (deterministic where rejection is statistical,
  and it would take the smear with it) and mask it IN, protected, when building the comet layer. Both
  need the comet's per-frame position, which `--comet` already derives from a reference-frame solve
  plus Horizons. **But 10P moves only 44.7 px across the 3.5 h session against an ~80 px coma**, so
  for a pixel mid-track a 15 px nucleus mask blanks ~67% of frames (33% coverage left, usable) while a
  40 px coma mask blanks 100% and leaves a hole. The design is strictly better on a fast mover; on
  this target it only half-applies. Note `DrizzleStrategy` already honours a per-frame bad-pixel mask
  by skipping deposition, so a comet mask is that mechanism with a different source.
- ~~**No compositing feature exists.** All of the above lives in scratch scripts.~~ Shipped
  2026-08-27 as `master_<slug>_composite.fits`; see the last section. The scratch version's mask,
  feather and opening are all unnecessary once the body is a model rather than a masked crop.

## The star layer: SUBTRACT the comet, do not exclude it and do not reject it (shipped 2026-08-26)

A `--comet` run now writes two masters, `master_<slug>.fits` (comet-aligned) and
`master_<slug>_stars.fits` (star-aligned, comet excluded per frame). One run, because the two layers
are combinable only if they agree on the reference frame, the canvas origin, the debayer, the
rejector and the frame set, and two separate runs can differ in all five. `--no-star-layer` opts out;
`--comet-mask-arcsec` sizes the exclusion (default 240").

Raised by the user: the comet moves far enough across a session that a pixel it ruins early is clean
background late, so the frames where it is in the way can simply be dropped per pixel.

### Rejection provably cannot do this job, and the rejection map says so

The idea it replaces was "tighten the sigma and let the comet fall out as an outlier". Measured on
C/2025 R2 (SWAN), the rejection map of a star-aligned AHD stack runs **0.086 along the track against
0.036 baseline** -- about five points extra, where the body is actually present in a third of the
frames at those pixels. A third of the samples being elevated is not an outlier population; it is
enough to inflate the very sigma meant to detect it. This is the same split the 10P work found from
the other side: rejection took the compact nucleus trail from 35.28% of sky to 9.85%, and left the
diffuse coma untouched because it is present in EVERY frame at those pixels.

Exclusion has no such limit because it never has to detect anything. The ephemeris already says where
the body is in each frame, and it is the same rate the comet compose consumes.

### The geometry, measured before anything was written

| | |
|---|---|
| travel across session | 357 px (ephemeris fit; 356 px from star-trail length, independently) |
| rate | 245.2 px/h at PA 8.9 deg, straightness residual 0.156 px |
| coma reach | 60 px per wedge typical, 100 px in its worst |
| tail | **none measurable** -- 2.1-2.6 sigma in four wedges anti-trailward, 60 px elsewhere |
| smear left in the star master | 1.76 sigma ridge, 1.03 at 40 px, 0.33 at 80 px, gone by 125 |

No tail means a ROUND mask suffices, which removes the awkward part of the idea. A body with a real
tail needs an elongated region and `CometMask` is where that would go.

The cost is smaller than it first looks and is self-limiting. A pixel is blanked only while the body
is within R of it, so a pixel at perpendicular distance p loses `2*sqrt(R^2-p^2)/travel` of the
session, NOT `2R/travel`; averaged across the band that is `pi*R/(2*travel)`, tapering to zero at the
band edge. At R=84 px against 357 px of travel: 35% of frames over a band covering **0.9% of the
canvas**, about 1.24x the noise there, in exchange for deleting a smooth 1.76 sigma ridge. A smooth
ridge is far more damaging than pixel noise, because it is exactly what subtracts as a negative ghost
when the layers are combined.

### The anchor is the one new quantity, and it is the one that can be silently wrong

A rate is a difference, so any constant offset cancels and the fit is immune to the 0-based/1-based
`SkyToPixel` question. An absolute position has no such protection, so the repo rule ("never subtract
1 from a solver-built WCS") is load-bearing here in a way it is not for the rate. `CometRate` now
carries `AnchorPx` + `AnchorEpoch`, which is the fit's own intercept -- already computed and
previously discarded, and a better answer than projecting one sample because the same least-squares
averaging that makes the slope robust makes it robust too.

**In REFERENCE space, never canvas space.** Each layer computes its own union bounding box from its
own transforms, so the canvas shift DIFFERS PER LAYER and a canvas anchor built for one is wrong for
the other. The shift is a pure translation, so the rate is the same number in either basis; the
anchor is not. Staying in reference space means neither the mask nor the anchor ever has to know
which layer is being built.

`CometMask.Punch` returns the pixels it blanked and the pipeline warns when that is zero across every
frame. The body is on the sensor by construction -- it is what the session was pointed at -- so zero
is the signature of an anchor in the wrong basis, and the resulting master would carry an untouched
comet while looking entirely plausible. Nothing about the pixels says so, so the count has to.

### A masked layer needs a strategy that NORMALISES, which an unmasked one does not

This is the part the measurement found and the design did not anticipate. The first working version
picked `BayerDrizzle` (auto-selected at 89 RGGB frames) and the result was wrong in a way that looked
almost right.

Differencing masked against unmasked isolates exactly what the mask removed. Across the track it was
a clean coma -- 2.89 sigma at the centreline, zero by 70 px, and `+0.0000` sigma over the 7.75M pixels
further than 700 px away. Along the track it should be roughly FLAT, because the body sweeps every
track position equally. It was not:

| along track | removed |
|---|---|
| -150..-50 (late-session end) | +4.50 sigma |
| -50..0 | +2.67 |
| 0..50 | +0.55 |
| 50..200 (early-session end) | **-1.08, -0.72, -0.82** |

Negative means masking made the layer BRIGHTER there, which no comet residual can do.

**Cause: the sky rose 504 ADU (1.6%) monotonically across the session as the field set, and
`DrizzleStrategy` does no per-frame normalisation** (it touches neither `Normalizer` nor
`Integrator`; `TilePipelinedDrizzle` likewise). Ordinarily that costs nothing, because every interior
pixel averages the same frames and a session-long trend is one constant across the whole master. A
mask breaks that premise: it removes a different, time-contiguous slice of frames at each pixel along
the track, turning the temporal trend into spatial structure precisely where the layer is supposed to
be cleanest.

So a masked layer excludes four strategy kinds, for two different reasons, both silent:

- `TilePipelined`, `TilePipelinedDrizzle` -- bypass the producers entirely, re-loading and warping
  each raw light themselves per tile from `RawLightSources`. The mask is never applied at all.
- `BayerDrizzle`, `TilePipelinedDrizzle` -- no per-frame normalisation, as above.

A forced `--strategy` of any of them is dropped for that layer with a warning rather than honoured.

### Result, same strategy on both sides

`InRamAllFrames`, sidereal, 89 frames, identical 3065x3037 canvas, mask the only difference:

| perp px | unmasked | masked |
|---|---|---|
| 0-10 | 2.43 sigma | **0.39** |
| 10-20 | 2.49 | **0.40** |
| 20-35 | 2.15 | **0.38** |
| 35-50 | 1.58 | **0.33** |
| 50-70 | 1.03 | **0.32** |
| 70-95 | 0.58 | 0.43 |
| 95-125 | 0.28 | 0.27 |
| 125-160 | 0.11 | 0.11 |

The peaked ridge is gone (**-84%**) and what remains is flat at ~0.35 sigma, converging with the
control beyond 95 px -- that is the real sky of this field (18h22m -14d, beside M16), correctly left
alone. Along-track removal runs +0.52 to +2.11 sigma with no negative lobe, tapering to 0.00 outside
the track; the mid-track hump is geometry, not bias, since a mid-track pixel collects coma from the
body both approaching and receding while an end-of-track pixel only gets one side.

**Do not compare a masked layer against a differently-integrated one.** The first verification did,
reading 1.53 sigma "still present" against a 1.76 sigma AHD+rejection baseline, and concluded the
mask had barely worked. Two variables had changed at once (mask, and drizzle's absence of rejection),
and the pair was uninterpretable in either direction.

### The mask is the FALLBACK. Subtracting a model of the body is the method.

Everything above about masking is true and it still ships, but only for a host with no `IStarRemover`
registered. Two measurements retired it as the default.

**Masking is arithmetically impossible on a slow body.** It works only where the travel greatly
exceeds the coma, and that is not the common case. C/2025 R2 travels 357 px against a smear reaching
165 px, so full coverage costs `2*165/357` = 92% of the session at the centreline, which is marginal.
**10P/Tempel 2 travels 45 px in 3.5 hours** (12.8 px/h; Horizons, topocentric, over the real session),
so every radius past 23 px masks 100% of the frames and nothing is left to stack. Not worse than
subtraction there: impossible.

**And stopping short leaves a worse artifact than the smear.** At R=84 the removal fell to exactly
0.00 beyond 90 px while the coma was still 0.38 sigma there, so the profile ran dip-then-step and put
two bars either side of the track. The bar's brightness IS the coma's brightness at whatever radius
the mask stops.

Subtraction has no geometry to satisfy. Every frame survives, so there is no coverage hole, no noise
band, no edge and no bars, and the wings and tail come out because they are in the model rather than
approximated by a circle.

### Where the model comes from, and the one thing that decides whether it works

`CometModel`: the body's own light, isolated, subtracted from each frame at the comet-relative
position, with the amplitude FITTED per frame (`sum(d*m)/sum(m*m)` against a per-CFA-colour local
background) rather than derived. Fitting is what makes it robust to transparency, to the master's
normalisation, and to the units of whatever produced it. The fitted amplitude ran 87 on one path and
1580 on another, and neither needed a constant anywhere.

**The model must come from a comet layer built from PER-FRAME star-removed plates** (`--remove-stars`,
artifact 3). That is the whole difference between working and not:

| model source | comet at ridge | streaks away from track (p0.5 / min) |
|---|---|---|
| untreated control | 2.38 sigma | -- |
| mask, R=84 | 0.40 sigma | -- (but 56 of 89 frames, and bars) |
| comet master minus its own `sxt` | 0.04 sigma | **-0.415 / -0.68 sigma** |
| **stacked from starless plates** | **0.30 sigma** | **-0.035 / -0.29 sigma** |

The third row is the trap. On a comet-aligned plate **every star IS a trail, and a star remover takes
trails as readily as it takes the comet**, so the difference holds the body PLUS whatever trail flux
went with it. Subtracted at 89 comet-relative positions, each survivor smears into a dark streak.
Measured on the manual pair at r=600-1300: median +0.20 sigma, p99 **+0.47 sigma**. An earlier check
passed that same plate as clean by asking only for the fraction above 1 sigma, which was 0.0000. The
wrong question, and it cost several rounds of trying to filter trails back out afterwards
(`OpenAcrossTrails`, a rank opening, three competing reach criteria, all retained only for the
fallback path). Stack the comet layer from starless plates and none of it is needed.

Cost: per-frame `sxt` ran 602 s for 89 frames, so the comet layer goes from ~40 s to ~10 min.

### Five preconditions that are NOT properties of this feature, and each broke it silently

Every one belongs to the surrounding system, was already known somewhere in the repo, and produced a
plausible wrong answer rather than an error.

1. **The anchor epoch is not the reference epoch.** `CometRate.AnchorPx` describes the first
   ephemeris sample; the compose is `translate(-rate * (t_i - t_REF))`, so on the comet grid the body
   sits at `anchor + rate * (t_ref - t_anchor)`. At 245 px/hr that is hundreds of pixels, and the
   model was cropped from blank sky.
2. **A comet-aligned canvas carries NaN**, and RC-Astro answers an ALL-NaN plate for an input holding
   any. `SharpenPipeline` already guards this way. Crop first: the box is inside the covered region.
3. **A star remover is a neural net and cares where its input sits in [0,1].** The comet layer is
   auto-picked as `BayerDrizzle`, which does not normalise, so its background sits at 0.0145 against
   the 0.5 the technique was proven on. `sxt` then found only the peak and left the whole coma
   (radial medians 0.000028 at r=20 against a 0.000077 noise floor). Normalise the crop first.
4. **`--remove-stars` used to REPLACE the frame list**, so both layers saw starless plates. The
   starless frame now rides alongside the original in `matched`.
5. **Each layer needs the calibrator its own input wants.** `integrationCalibrator` is deliberately a
   no-op under `--remove-stars` because those plates were calibrated before removal; the star layer
   reads raw originals and needs the real one. Getting this wrong integrates uncalibrated frames into
   a perfectly plausible master.

### Rejection was switched off wherever a frame did not contribute, and always had been

Not a comet bug at all, found through one. **No `IPixelRejector` handled NaN.** Every comparison
against NaN is false, so quickselect returns nonsense, MAD comes out NaN, the `mad <= 0` degenerate
guard does not fire (also false), both bounds become NaN, and `v < NaN` / `v > NaN` are both false.
Nothing is rejected and the loop breaks on its first pass. Silently.

Warped frames carry NaN borders, so **canvas edges have never had rejection** in any stack this
codebase has produced. It became visible only when `CometMask` put NaN mid-frame and hot pixels
survived there as clumps: rejection rate 0.0000 inside the band against 0.026-0.034 outside.

Fixed across all five rejectors via `PixelRejection.MarkAbsent`, plus the two order-statistic ones now
take their percentiles over the REAL sample count (a NaN sorts to an unspecified end, so "drop the
highest k" could spend the whole budget on samples that were never there). An absent sample counts as
NOT rejected in the returned tally, or the rejection map paints every canvas edge as heavily rejected
when nothing was. Pinned by `RejectorAbsentSampleTests`, verified to fail 4/16 with the fix removed.

### Measuring this: the band median is the wrong statistic

Three real defects were found by looking at the rendered frame at 1:1 after the radial profile had
called it clean. A median across a band averages over exactly the structures that matter: an edge, a
thin streak, a texture change. What each needed instead:

- **the bars** at the mask edge: a FINE profile (15 px bins), not 25 px ones;
- **the "checkerboard"**: not CFA at all (sub-lattice spread 0.006 sigma on track against 0.015 off,
  and no autocorrelation bump at lag 2) but correlated noise. Only 1.09x the rms yet ~2x the
  correlation at 6-8 px, so the blobs grow while the per-pixel scatter barely moves;
- **the streaks**: p0.5 and min of the difference, never its median, which read +0.001 while
  individual pixels ran to -0.68.

And **never compare a treated layer against a differently-integrated one.** The first verification
read "1.53 sigma, ridge still present" against a 1.76 sigma baseline and concluded the mask had
barely worked; that pair differed in mask AND in drizzle-versus-rejection, and was uninterpretable in
either direction.

## The model reaches as far as each channel's coma does, and the run writes the composite (2026-08-27)

Yesterday's star layer (panel 2 of `swan-starless-model.png`) still carried a diffuse band along the
track at 1:1, after the radial profile had called it 0.30 sigma. Measured against the untreated
control on the same canvas and strategy, with the track pinned ANALYTICALLY from the run log (body at
the reference epoch, rate, canvas origin) rather than fitted from the residual, luminance, band
medians in units of the master's own sigma:

| perp px | control | model cut at 100 px (26th) | per-channel asymptote (27th) |
|---|---|---|---|
| 0-10 | 2.36 | 0.39 | -0.19 |
| 35-50 | 1.53 | 0.19 | -0.17 |
| 70-95 | 0.57 | 0.08 | -0.17 |
| 95-125 | 0.28 | **0.19** | -0.17 |
| 125-160 | 0.12 | 0.13 | -0.16 |
| 200-260 | -0.08 | -0.08 | -0.15 |
| 340-450 | -0.11 | -0.11 | -0.13 |

The 26th's column tells the story on its own: the ridge falls to 0.08 by 70-95 px and then RISES
again to 0.19 at 95-125, which is where the model stopped. From there out it is the control,
untouched.

**Cause: the reach was judged on `planes[0]` (red) against a floor of 1% of the peak.** SWAN is a
gas-rich comet: green peaks at 0.288 in the comet layer against red's 0.099. Red falls to 1% of its
peak at ~125 px, and the model was cut at 100. The green profile of the starless comet layer, in that
plate's own sigma: **1.37 at 100 px, 0.92 at 120, 0.48 at 160, 0.26 at 200, 0.10 at 300, 0.05 at
400.** Everything past 100 px stayed in all 89 frames and smeared along the track. A relative floor
is the wrong test regardless of channel: a coma's wings fall roughly as 1/r, so at any fixed fraction
of the peak there is still coherent signal in the annulus, and the same model is subtracted from
every frame, so it does not average away.

**Fix: each channel's annular-median profile is followed outward until it stops falling** (three
consecutive annuli without a new minimum). The minimum IS that channel's pedestal, the radius where
it sits is that channel's reach, and beyond its reach the plane is zero. Reaches came out **210 / 420
/ 330 px** (R / G / B). The rule also stops correctly short of the sky gradient, which turns every
channel's profile back upward past ~440 px (the far-field median at r > 450 was therefore never the
coma's asymptote either). Pinned by `CometModelTests.TheReachFollowsEachChannelsOwnProfileNotChannelZero`
against a 1/r^2 coma with SWAN's channel balance, and by `TheAsymptoteIsWhereTheProfileStopsFalling`
on a profile that falls, flattens and rises.

Three things were changed alongside, each found by reading the code against the measurement:

- **The centre was rounded to a whole pixel while the sampler assumed the exact one.** The crop is
  cut at `round(centre)`, and `ToModel` mapped the crop centre to the unrounded position, so the
  model sat up to 0.5 px off, which the code's own comment says subtracts a dipole. The sub-pixel
  remainder now travels in `_centre`. `TheModelIsPlacedSubPixelWhenAddedBack` recovers a target at
  (700.35, 380.8) to 0.15 px.
- **The amplitude is the MEDIAN of per-pixel ratios `d/m` over an annulus of the coma, not a
  least-squares fit over the core.** Two things bias least squares the same way and neither is
  clipped away: a bright star's halo inside the fit region (its core is clipped, its wings are not),
  and the nucleus, which the star remover took OUT of the plates the model was stacked from while
  every frame still has it, so it is positive `d` exactly where `m` is largest and dominates
  `sum(d*m)`. On SWAN the least-squares version (core r<80 px, one 3-sigma clip; mean amplitude 1711)
  left the track flat and hid the problem. **On 10P it dug a bowl: -1.13 sigma at 10-20 px from the
  track against a 12.2 sigma control, tapering to zero by 125 px, with a +2 to +3.5 sigma LINE of
  leftover condensation running along the 45 px track inside it** (|perp| < 4 px), the field star 40 px
  from the body pushing the same way. Each contaminant is a minority of an annulus and a median is
  blind to a minority. The annulus starts at 12 px (several seeing discs, past any footprint a star
  remover takes out of a point source) and stops where the brightest channel has fallen to 15% of its
  peak, because a ratio amplifies a sky error by `1/m`. Pinned by
  `AMissingNucleusAndABrightNeighbourDoNotInflateTheAmplitude` (a 30% core deficit plus a
  12000-count halo star 40 px out; amplitude within 3%) and `...AndTheBodySubtractsOut` (2000
  recovered within 2% from a frame carrying 60 stars). The re-measurement of both datasets with this
  estimator is the next entry.
- **The body is evaluated at the reference frame's MID-exposure**, where its light is centred
  (`CometCompose.BodyOnGrid`). At 245 px/h and 30 s that is 1.0 px along the track. The comet layer's
  peak pixel sits on the start-of-exposure prediction and its squared-weighted centroid 2.1 px further
  along, 1.1 px past the mid-exposure one; the coma's asymmetry explains a pixel, and the ridge
  measurement cannot resolve the remainder. The compose itself is indifferent (every frame shares one
  exposure length, so start differences equal mid differences); only the absolute position moves.

**Is the new layer over-subtracted?** Its band medians sit at -0.13 to -0.19 where the control's far
bands read -0.11, so the question is fair. Two readings say no. What was removed (control minus new)
runs **2.55, 1.71, 1.17, 0.74, 0.45, 0.29, 0.15, 0.07, 0.04, 0.02** sigma from the track out to
340-450 px: monotone, no plateau, no step at the 420 px reach. A pedestal error in the model would be
a disc, constant to the reach and then a drop, and there is none. And the new layer's own medians
vary by 0.06 sigma across 0-450 px against the control's 2.5 sigma; the absolute offset from the
reference strip (perp 500-800, in M16's neighbourhood) is the strip's, not the layer's. The p0.5
column (-0.78 to -0.86 in the track bands, -0.96 at 340-450) shows no dug holes either.

**The composite is written by the run.** `master_<slug>_composite.fits` is the star layer with the
body added back ONCE (`CometModel.AddTo`), at `BodyOnGrid` carried onto the star canvas by that
layer's own shift: the same reference-space point the subtraction used, so there is no WCS, no
centroid and no way for the two to disagree. Units are measured, not assumed: the model is in the
comet layer's pixels and the gain per channel is the ratio of the two masters' sky medians, which came
out **0.9993 / 1.0020 / 1.0021** because both layers normalise to 0.5, and would come out ~34 if one
of them had drizzled. It goes through the same `WriteMasterAsync` as every master, so it plate-solves
and gets its own SPCC (it has stars AND the comet, which neither layer alone offers), and is stamped
`ALIGNON = Composite` with the drift and its source. `--no-composite` skips it. The scratch version
(`composite_linear.py`) needed a typed RA/Dec, a centroid that locked onto a star 40 px away and
missed by 46 px, a feathered disc, and an opening across the trails; with the body a model, none of
that exists.

**Iterating no longer costs ten minutes of `sxt`.** The per-frame starless plates now live at
`<out>/starless/<slug>/`, beside the `masters/` cache and NOT under `_staging`, which is wiped at the
start of every group. A re-run into the same `-o` reuses a plate whose `SRCDGST` and `STARMODE`
match the light and the requested mode (a plate with no mode card predates the card and was made in
the default). The plates also carry a TianWen `SWCREATE` now, so the scan's provenance skip drops
them; they used to inherit the capture software's. This run: **13.0 min -> 3.3 min**, 89 of 89
reused.

The two `PrepareFrame` copies inside `IntegrateLayerAsync` (one per producer) are one local function
now, and the compose arithmetic lives once in `CometCompose` rather than three times inline.

Still open from the list above: the two-mask design where the coverage arithmetic allows it. `sxt`
taking the central condensation stopped being open the same day: with the amplitude read off the
annulus, the condensation the model lacked stayed in every frame of the star layer as a thin line
along the track (10P: +7 sigma at 2-4 px over the 45 px of travel), and the fix, a comet-aligned
median stack of the RAW frames' central window spliced into the model, is the next section.

## The nucleus comes from the raw frames, and every channel has its own amplitude (2026-08-27, later)

Three findings on the way from the baseline commit to a clean 10P star layer, each measured before
it was fixed and each pinned by a test afterwards.

### The annular median took the bowl out and left the line

With the amplitude read as the median of `d/m` over the 12 px to 15%-of-peak annulus (never the
centre), 10P's star layer against the untreated control, luminance:

| perp px | control | least squares over the core | annular median |
|---|---|---|---|
| 0-10 | 12.18 | -0.43 | +0.95 |
| 10-20 | 7.07 | **-1.13** | -0.11 |
| 20-35 | 4.45 | -0.72 | -0.08 |
| 35-50 | 3.10 | -0.40 | +0.03 |
| 50-95 | 1.92 / 1.21 | -0.24 / -0.16 | +0.02 / +0.01 |

The bowl is gone (amplitude 3540 -> 3107, -12%), and what the bowl had been partly cancelling now
stands alone: a line along the 45 px track, **+4.9 / +7.0 / +2.9 sigma at 0-2 / 2-4 / 4-7 px**, the
condensation the star remover took out of the plates. SWAN kept its flat track (-0.04 at 0-10 against
-0.16 far, a 0.12 sigma spread; amplitude 1711 -> 1641).

### The condensation is a star, and it is bright

Measured on the reference frame's raw pixels: the nucleus peaks at **8656 ADU, +5544 above the green
sky**, within one pixel of the ephemeris position, while the coma 12-16 px out is +178 above sky. So
it is a point source thirty times the surface brightness of the coma it sits in, and exactly what a
star remover is built to take. The model built from starless plates cannot carry it, whatever the fit
does.

**`CometRawCore`**: a comet-aligned MEDIAN stack of the RAW frames' 81x81 window around the body,
deposited forward per photosite into the nearest cell of that colour's plane, with the body on the
centre cell whatever its sub-pixel position (135 frames in 9.6 s; 89 in 4.2 s). A median over frames
is enough because on the comet grid every star trails through a cell for a few frames out of
many. **`CometModel.SpliceCore`** relates the two by `raw = a * model + b` over the annulus just
outside the splice (12-30 px), where the model has its coma and the raw stack's median has shed the
trails, and replaces the model inside 12 px (feathered to 18) with `(raw - b) / a`. On 10P the gains
came back 3145 / 3133 / 3037 against a fitted coma amplitude of 3107, which is the consistency check
the two estimates owe each other, and the model's centre went from 0.069 to 1.61 in green: the
nucleus is 23x the diffuse core it sits in.

| 10P, |perp| bands within the track | 0-2 | 2-4 | 4-7 | 7-10 | 10-15 | 15-20 |
|---|---|---|---|---|---|---|
| annular median, no core | +4.85 | +7.00 | +2.87 | +0.53 | -0.12 | -0.27 |
| annular median + raw core | -0.54 | -0.77 | -0.69 | -0.43 | -0.19 | -0.26 |

The line is gone. What replaced it is a shallow trough with a few positive spikes (p99 +26 sigma in
the track strip): the spliced core is ONE nucleus, the median over the session, while each frame's
nucleus is as sharp as that frame's seeing, and the coma's amplitude knows nothing about that. So the
core takes **its own per-frame amplitude** (`FitCoreScales`, the median ratio inside 12 px), blended
into the coma's over the feather. Flux is then matched per frame; the width mismatch a seeing-varying
point source leaves against a median stack is what remains, and it is small.

### One amplitude for three channels was wrong from the start

The raw-core splice logged its gains per channel, and on SWAN they were **1237 / 1700 / 1996**
(R / G / B) against a single fitted amplitude of 1641. The comet layer normalises each channel to its
own sky, so the model's three channels are in three different units, and a pooled amplitude sits near
the channel with the most photosites (green) while the others are wrong by the ratio of the units.
Per channel against the control, SWAN's star layer read **R -0.84 sigma, G -0.10, B +0.36** at the
track: red over-subtracted by a third, blue under-subtracted by a fifth, a colour cast along the track
that every luminance measurement above had cancelled out and could not see. 10P did not show it only
because its gains happened to agree (3145 / 3133 / 3037). `FitScales` / `FitCoreScales` /
`SubtractFrom` now work per model channel, each photosite contributing to and taking from the channel
its CFA colour names; a channel too thin to fit borrows the median of the others.

SWAN, per channel, at the track (0-20 px) against each channel's own far-field floor (260-450 px):

| | R | G | B |
|---|---|---|---|
| control | +1.80 (floor -0.16) | +2.65 (-0.10) | +1.97 (-0.08) |
| pooled amplitude 1641 | **-0.85** | -0.10 | **+0.36** |
| per-channel amplitudes 1172 / 1650 / 1959 | -0.07 | -0.14 | -0.08 |

The per-channel amplitudes agree with the raw-core splice's independently fitted gains
(1237 / 1700 / 1996) to within 5%, which is the cross-check the two estimates owe each other.

### Where 10P ends up

Everything above together, against the untreated control on the same canvas:

| 10P star layer | 0-10 px | 10-20 | 20-35 | 35-50 | fine bins 0-2 / 2-4 / 4-7 / 7-10 |
|---|---|---|---|---|---|
| control | 12.18 | 7.07 | 4.45 | 3.10 | |
| least squares over the core | -0.43 | -1.13 | -0.72 | -0.40 | +2.1 / +3.5 / -0.2 / -2.1 |
| annular median | +0.95 | -0.11 | -0.08 | +0.03 | +4.9 / +7.0 / +2.9 / +0.5 |
| + nucleus from the raw frames | -0.31 | -0.13 | -0.07 | +0.03 | -0.5 / -0.8 / -0.7 / -0.4 |
| + per-channel and per-frame core amplitudes | **-0.06** | **-0.06** | **-0.07** | +0.04 | **+0.1 / 0.0 / -0.2 / -0.1** |

Per channel at 0-20 px: R +0.08, G -0.14, B -0.06 against a control of +4.98 / +9.06 / +8.21;
amplitudes 2974 / 3153 / 3043. A 12 sigma smear on the slowest mover in the archive is under a tenth
of a sigma in every band, every channel and every fine bin, and the composite carries the nucleus at
its measured brightness. What is left in the track strip is the frame-to-frame width mismatch of a
point source against a median nucleus (p99 +27 sigma over a few pixels), which no single core can
remove and which the composite does not show.

Pinned by `AMissingNucleusAndABrightNeighbourDoNotInflateTheAmplitude`,
`TheNucleusIsRestoredFromTheRawCoreInTheModelsOwnUnits` (a 30% core deficit restored; a frame whose
nucleus is 40% brighter than the median reads a 1.4x core amplitude and subtracts clean),
`EachChannelGetsItsOwnAmplitude` (1500 / 2000 / 2500 recovered within 3%, under 1% of each channel's
core left), and `CometRawCoreTests` (the body on the centre cell in every colour from 36 dithered,
drifting frames, and a fixed star trailing out of the median).

**Measure colour work per channel.** A luminance mean is the right statistic for a ridge and the wrong
one for a cast: it averages red's deficit against blue's excess and reports flat.

### Colour: the SWAN composite rendered blue, and three SPCC defects were behind it, not the filter

The layers are right and the render was not. SPCC on the SWAN star layer and composite solved
**WB = (1.937, 1.000, 3.259)**, blue gained 3.3x, and painted the sky, the star halos and the
C2-emitting coma saturated blue. The first explanation reached for was the filter: the frames were shot
through the **Optolong L-Quad Enhance**, and its name suggests the narrowband case the
narrowband-colour plan marks as blocked. **That was wrong.** Read off the curve we digitised, the
L-Quad Enhance passes 385-419, 444-496, 500-531, 559-571 and 633-704 nm, about 200 nm of the 300 nm
visible, with notches at NaI 589 (0%) and Hg 546 (1%): four broad windows, an LPS-D3-class
light-pollution filter, and SPCC should work through it as it does through the D3. So the Gaia spectra
extraction is not what this frame needs. Nor does the filter's absence from the model matter much:
SPCC divides the star's band ratios by the white reference's through the SAME throughput, so a curve
missing from both cancels to first order (measured: declaring it moved SWAN's fit from
(1.879, 2.996) to (1.821, 2.717)). The frames say `FILTER = 'None'` because the SV605CC has no wheel;
the fix for that is the `.tianwen-meta.json` sidecar at the scan root, never a header rewrite.

**Defect 1, the clip test, was real but was not SWAN's cause.** The log said **"dropped 969 of 1211
matched stars for a clipped aperture pixel (>= 0.9800, 98% of frame peak 1.0000)"**. The clip level
was `saturationFraction * image.MaxValue`, and `MasterPostProcessor` had just rewrapped the master with
`MaxValue = 1.0` so the histogram and stretch would treat it as unit-scaled. A normalised master has
its sky at 0.5 and its stars far above 1.0 (SWAN's star layer peaks at **36 / 75 / 34** in R / G / B),
so "at least 98% of 1.0" was true of every bright star. On 10P it dropped **545 of 545**, SPCC gave up,
and the sky-background fallback was the only reason 10P looked right. Fixed in `ExtractPhotometry`: the
ceiling is the OBSERVED peak read from the pixels, per channel. 10P then converged for the first time,
(0.969, 1.000, 1.209) from 500 of 543 matches, and rendered a green coma with near-neutral stars. SWAN
dropped 2 of 1211 and fitted **(1.879, 1.000, 2.996)**, the same blue triple, which is how a fix that
was correct got attributed a symptom it did not own. The record of that wrong attribution is kept
here on purpose.

**Defect 2, the matcher, was SWAN's cause.** Replicating `MatchStars` on the composite: the tolerance
probe reported a median WCS residual of **15" with a 10" MAD** on a plate the solver had just fitted to
0.23 px, sized the match radius to its **30" cap**, and the passes accepted **1288 matches from a
footprint holding 340 Tycho-2 stars**. The probe took a nearest-catalogue-neighbour residual from
EVERY detection, and on a deep master 5088 detections stand against those 340 stars, so the "residual"
was a random distance for fourteen of every fifteen samples; the passes then let every faint anonymous
detection within the radius inherit a bright neighbour's B-V. The tell was in the photometry: observed
star colour was **flat across every B-V bin** (R/G 0.43 to 0.45 from B-V 0 to 2.4) while the model
walked from 0.67 to 1.57, so the fit degenerated to "make the median star look like a B-V 0.5 star".
10P, with 2940 detections against 1315 catalogue stars, was not fooled (6.5", 599 matches). Fixed in
`MatchStars`: detections are taken brightest first and **each catalogue star may be claimed once**, in
the probe and in both passes (`SpccFunnel.Duplicate` counts the refusals). Tycho-2's own limit is why
this needs no sample-size constant: the catalogue stars ARE the brightest detections, and once every
one in the field is claimed no anonymous detection can find an unclaimed one. SWAN now probes to 5"
and matches 597 one-to-one, and its observed colours track B-V (R/G 0.40 to 0.57 across the range).

**Defect 3: the observed colour range was compressed ~3-4x against the SED model, on both datasets,
and it was the STACK, not SPCC.** With the matching right, SWAN's stars ran R/G 0.40 to 0.57 where the
model expects 0.67 to 1.57, and 10P's 0.84 to 0.97 against 0.57 to 1.99; the implied per-bin white
balance drifted with B-V (10P: R 0.68 at B-V 0 to 2.07 at 1.6). Refuted in turn: raw-frame saturation
(10P's 200 brightest stars sit at 65535 in the raw lights, but rejecting by 50% of peak or by
peak-to-flux leaves every bin's implied WB unchanged, and V 11-12 stars at 6% of peak compress the
same), aperture losses (curve of growth flat in colour r=3 to r=20, per-channel FWHMs within 7%), the
white reference (the Sb template sits between G8III and K0V by shape) and the Tycho-2 B-V column
(checks against literature where BT exists). Then the raw frame itself: among 273 unsaturated stars
the CFA photometry spans R/G p90/p10 = **x4.5**, and the master's stars span **x1.56**. The stack
compresses colour.

Two causes, one measured to the root. **(a) The normaliser anchored every frame on its MINIMUM
pixel.** `Normalizer` maps `out = (in - floor) * target / (median - floor)`, and `floor` was the
per-channel minimum, so one pixel set the gain of a whole frame and channel: a hot pixel, a cosmic
ray, a demosaic overshoot beside a saturated star, or a flat that reaches zero in a corner (the
calibrator divides by `max(flat, epsilon)` and makes a ~1e9 spike). Sampling 9 frames of the SWAN
session through the AHD debayer, the red channel's gain wandered **x0.85 to x3.71**, green x1.0 to
x2.27, blue x0.76 to x2.28, each channel on its own; VNG x0.57 to x1.0; and through MHC one such spike
put the min near -1e9 and integrated the whole star layer to a constant 0.5 (39874 of 40000 patch
pixels exactly 0.5, thirteen distinct values in the layer). Frames enter the rejector with random
per-channel gains, and no colour calibration downstream can undo what that does to a star's colour.
Fixed: the floor is the frame's `Pedestal` (the calibrated zero every frame of a group shares),
`NormalizationStats.PerChannelMin` is `PerChannelFloor`, and `Normalizer.ComputeScale` is the one
source for the in-RAM and streaming integrators. Pinned by
`NormalizerTests.Apply_AnOutlierPixelDoesNotChangeTheFrameGain`. Absolute normalised levels quoted
above this entry (peaks 36/75/34, the model amplitudes) are in the OLD units. **(b) The AHD
debayer's phase 4 runs a 3x3 median over (R-G) and (B-G) at every pixel**, which replaces a 2-3 px
star's chroma with the surrounding sky's: on one frame the same 400 stars span x2.93 in R/G through
MHC (linear), x1.80 through VNG and x1.65 through AHD. MHC is not a drop-in default: its kernel
overshoots to -10k ADU beside saturated stars, a dark ring. Whether (a) alone restores the colour, or
(b) needs a change too (AHD without phase 4, or MHC with its overshoot clamped), is what the runs in
`runs/starless-floor` and `runs/onerun-floor` decide; the next entry reads them.

**Read so far (SWAN, `runs/starless-floor`, AHD on the fixed normaliser):** SPCC fits
**(0.376, 1.000, 1.481)** from 544 one-to-one matches (3 clipped), but the master's stars still span
only **x1.58** in R/G and x1.30 in B/G against the raw frame's x4.5. So (a) was a real photometric
defect and is fixed, and (b), the AHD chroma median, is the compressor. The 10P counterpart is in
`runs/onerun-floor`, unread.

**Continue here.** (1) Run `SpccMatchProbe` on both `-floor` composites (`TIANWEN_COMET_PROBE_ROOT`):
the implied WB per B-V bin is the yardstick and it will still drift. (2) Fix the debayer: the
candidates are AHD without its phase-4 median (or the median applied only where the homogeneity map
switched direction, which is the zipper case it exists for) and MHC with its overshoot clamped at the
neighbourhood's range; measure both with `DebayerColourProbe` + `spread_fits.py` on the single frame
(target: the x2.9 MHC reaches on 400 stars without the -10k lobes) before changing `StackDebayerAlg`,
then re-stack SWAN and read the spread and the per-bin WB. (3) Cut the 1:1 colour crops
of the new composites (`floor_panels.py` in the session scratchpad did old-vs-new) and compare against
the user's APP renders of SWAN, which are the reference: green coma with a warm core, M16 orange-red,
warm-white stars. (4) The `.tianwen-meta.json` sidecar declaring the L-Quad Enhance sits only in the
`C:/temp` working copy of SWAN; the D: archive needs the same file beside its lights.

Two rules from it. **A rewrapped `MaxValue` is a display convention, not a saturation level**: anything
asking "is this pixel clipped" must read the peak from the pixels. And **a matcher that lets a detection
claim any catalogue star will be right exactly when the frame is no deeper than the catalogue**; the
funnel's `Detected` against the catalogue count in the footprint is the first thing to read.

Two things for the runner rather than the reader: in Git Bash a junction is `cmd //c mklink /J`, since
MSYS rewrites a bare `/c` into `C:\` and opens an interactive `cmd` that exits on EOF having done
nothing (the cache junctions for the neutral-WB render silently did not exist, and the run redid ten
minutes of `sxt`); and the starless cache is per output directory, so a comparison render into a new
`-o` pays the `sxt` pass again unless the cache is shared.
