# Plan: detect an obstruction from the frames themselves, in stacking

Status: **RESEARCHED, MEASURED ON ONE REAL OBSTRUCTION, NOT STARTED.** Backlog `#48` (issue #307).
The gap and the design below are established against the code. The thresholds were deliberately
unset, because the machine that did the research has no archive attached (`D:` lives on the
desktop); the desktop has since measured the first of them against a session with a KNOWN
obstruction, and the numbers are in "What one real obstruction measures" below. They changed the
remedy, so read that section before the design. The cloud control is still outstanding, and until
it is taken the sign rule is corroborated on one side only.

Sibling of, and deliberately separate from,
[`fov-obstruction-detection.md`](fov-obstruction-detection.md) and
[`obstruction-first-light-oracle.md`](obstruction-first-light-oracle.md): those are the LIVE,
session-side problem -- decide before committing a target window, using the catalogue as the oracle
and the zenith as the calibration anchor. This one is POST HOC, over a folder of subs, with no
session, no mount, no catalogue expectation and no zenith frame: only the pixels.

## The gap

**The stacking gate is entirely whole-frame scalars.** `FrameQualityFilter` decides from
`FrameMetrics` -- median HFD, median FWHM, median ellipticity, star count -- and `FrameRejectReason`
carries three flags, each a session-relative tail test. An obstruction is SPATIAL, so it falls
between them in two directions:

- **A partial obstruction is KEPT.** A roof over a fifth of the frame need not push the frame's
  TOTAL star count below a session-relative threshold, so the frame passes the gate and contributes
  a dark wedge to the integration. The rejection stage then sees a minority of frames disagreeing
  with the majority over that region, which is what sigma clipping is for -- but on a session where
  the obstruction creeps across the frame all night, the "minority" grows and the clip starts eating
  real signal instead.
- **Cloud and obstruction are indistinguishable to it.** Thin cloud drops the star count too. Same
  flag, same number, completely different remedy: cloud is a WAIT, a roof is a SLEW (live) or a MASK
  (post hoc).

It is the lesson CLAUDE.md already states one level down for CFA mosaics, restated for geometry:
**a whole-frame statistic describes none of its regions.**

## The design

### 1. The statistic is per CELL, and the input is already in hand

Divide the frame into a coarse grid (start at 8x8; the cell has to be big enough to hold a
believable number of stars at the session's density and small enough to resolve a roofline). Per
cell, two numbers:

- **Star count**, from the star list the registration pass already detected. Nothing new is
  computed -- the same property that made `#58` free.
- **Background level**, from the same per-cell pixels (median, or the pedestal-aware statistic the
  stacker already uses; see `Image.Pedestal` and the CFA rule below).

### 2. The discriminator is the SIGN of the background change

This is the part worth getting right, because it separates two causes with an identical star-count
symptom:

| | stars in the region | background in the region |
|---|---|---|
| **Obstruction** (roof, tree, dome slit, powerline) | fall, bounded | **DARKER** -- it blocks skyglow |
| **Cloud / haze** | fall, frame-wide and soft-edged | **BRIGHTER** -- it scatters light back |
| **Dew / frost** | fall, frame-wide, worst at the edges | brighter, and HFD widens with it |

So the rule is not "few stars here" but "few stars here AND this region is darker than the rest of
this frame". A tree is darker AND fractal-edged; a roofline is darker AND straight; a dome slit is
the inverse geometry (a bright stripe of sky inside a dark surround), which the same two numbers
describe with the signs swapped.

**An obstruction comes in two regimes and only one of them is opaque.** This table first said the
stars "collapse"; the measurement below says that word is right for one regime and would miss the
other outright.

- **Occluding**: a roofline, a trunk, a dome slit edge. Far enough away to be near focus, opaque, so
  the region goes to the dark floor and the stars in it are gone.
- **Attenuating**: a powerline, a thin branch, anything close enough to be far outside focus. It is
  a partial shade, not a shutter. The archive's one known case takes the background down **15%** and
  the star count down to **0.5 to 0.75** of the same frame's clear cells -- both real, neither a
  collapse. A threshold written for the first regime is silent on the second.

So a threshold on EITHER number must sit at the attenuating depths, and the AND is what keeps that
low bar from firing on noise.

**Bounded by construction, and that is the point**: the comparison is within ONE frame, so
transparency, moon, gradient and exposure all cancel. A frame-wide drop moves every cell and is
therefore not an obstruction, which is exactly the discrimination `FrameQualityFilter` cannot make.

**But the SCENE does not cancel, and on a bright target it dominates.** This paragraph used to end
at the line above, and it is incomplete in a way that matters: a within-frame comparison removes
everything that is uniform across the frame, which is not the same as removing everything that is
not a defect. An extended target has structure of its own, brighter on nebulosity and **darker in
its dust lanes**, and a dark lane is bounded and darker, which is the entire obstruction signature.

Measured on `eta-Car-Nebula/2025-01-14` (ASI585, L-eNhance, 125 subs): **every frame flags 150 to
250 cells of 576** below 0.95, with cells up to 1.58 above, which is the Carina nebula and the
Keyhole, not an obstruction. Against the two real obstructions that is a quarter to a half of the
frame flagged on every sub of the session.

**Frame-to-frame correlation of the cell map separates a MOVING obstruction and nothing else:**

| session | corr between frames | what it means |
|---|---|---|
| Helix powerline 2022-08-31 | **-0.15** | moves across the field, so the map does not repeat |
| eta Car 2025-01-14 | +0.89 | the scene, fixed on the sky |
| Helix roof 2026-08-01 | **+0.998** | fixed on the sensor, and indistinguishable from a scene by this test |

So the discriminator that remains is the one the pipeline already computes and this plan had not
reached for: **the scene is fixed in SKY coordinates and an obstruction is fixed in SENSOR
coordinates.** Registration is exactly that mapping, it runs anyway, and under it a dust lane lands
on the same sky cell in every sub while a roof does not. Any per-cell test therefore belongs on the
residual against the session's own registered cell map, never on a single frame's cell median
alone. Without that, a nebula field is a permanent false positive and the detector would have
masked or dropped every sub of eta Carinae.

### 3. The remedy is a DROP by default; the cauterise is the exception

**There is no edge to cut at.** This section first said the opposite -- mask the obstructed cells,
keep the rest, drop only a mostly-obstructed frame -- and the measurement below refutes it for the
one obstruction the archive can show. **An obstruction does not end where its shadow is darkest; it
ends where the light bending around it stops mattering, and those are hundreds of pixels apart.**
Measured perpendicular to the band, the deficit runs 16.2% at the core to 10% at 90 px, 4% at 150 px
and back inside 1% only past 195 px, a smooth ramp with no step anywhere along it. A near-field
body is far outside focus, so almost all of what it casts is penumbra rather than shadow, with
diffraction at the boundary underneath that; the geometric edge is the one place in the profile
nothing marks.

So a mask drawn where the cells look dark keeps the whole ramp. That is worse than keeping nothing,
because what it keeps is a smooth GRADIENT: background extraction fits it, the normaliser's level
sees it, and the flat cannot know about it. A gradient that only some frames carry is exactly the
input those three stages have no defence against.

**Therefore: an obstructed frame is REJECTED, whole, by default.** It is the cheap answer and
usually the free one -- the ground-truth session loses 6 subs of 46, which costs 7% in noise on the
master, against a wedge of unflattenable gradient in every pixel of it. Say it in the census
(`#58`) so the count is visible rather than silent.

**The cauterise is the opt-in, for when the frames are not plentiful.** Excise the shadow AND the
whole ramp -- not the dark cells, the dark cells plus a measured margin past the point where the
profile has recovered -- and mark it absent through `PixelRejection.MarkAbsent` and the coverage
machinery that already exists (`#67`, merged in #315: `IntegrationResult.Coverage`, the
`<stem>.coverage.fits` sidecar, `TryReadCoverageMap`). On the ground-truth session that costs 13.2%
of the frame for the shadow alone and 23.3% with a 150 px margin, which is the honest price and is
why it is not the default.

Three consequences that follow and must not be missed:

- **The margin is the whole point of the cauterise, and it is a MEASURED distance, not a cell.** Cut
  to the cell boundary and the ramp's tail is still inside the kept region.
- The coverage map already carries "no frame covered this pixel", so a cauterised region lands in
  the SAME field the canvas ring uses, and every downstream consumer (the auto-crop's coverage tier,
  the gradient report's mask, `IntegratedMaster.Labelled`'s all-NaN guard) understands it already.
- **A cauterise is a weight change, so the normaliser must see it.** A frame whose cells are partly
  absent must not be normalised on a statistic taken over the absent region.

### 4. Where it goes

A pure classifier beside the existing gate, not inside it:

- `src/TianWen.Lib/Imaging/Stacking/FrameObstructionDetector.cs` -- new, pure: takes the star list,
  the frame, the grid size, returns per-cell verdicts plus a frame-level classification.
- `FrameQualityFilter` / `FrameRejectReason` -- one new flag for an obstructed frame, which by
  default drops it. Flags already, so it composes.
- `StackingPipeline` / the register loop -- carry a cauterise mask into integration when the option
  asks for one; by default there is no mask, because there is no kept frame.
- The cauterise is an OPTION on the stacking options, off by default, and it is the only thing in
  this feature with a margin to configure. Nothing else acquires a knob.
- `RegistrationCensus` -- report obstructed cells per frame, so the census says it the way it says
  every other drop cause since `#58`.

## What one real obstruction measures

**The ground truth is a powerline, and the owner knew where it was.** Step 1 below proposes finding
obstructed sessions from the census; asking was faster and is the reason this section exists at all.

The session is `E:/Astro/SharpCap Captures/Helix Nebula RGB 120s -4deg 121g 11o`: 2022-08-31,
ASI533MC Pro at 135 mm (5.74 arcsec/px), 46 x 120 s, SharpCap 4.0, RGGB. **It is on `E:` only**, not
in `D:/Astro-Pics`, which also makes it the first real hit for the E: reconciliation (`#35`).
Statistics below are over ONE photosite population of the mosaic (the G on the top row), never the
mosaic as a whole, per the CFA trap at the end of this plan.

**Six frames of 46, then nothing.** 24x24 grid, each cell against its own frame's cell median:

| frame | time (UTC) | cells below 0.95 | background in band | stars/cell, band / clear |
|---|---|---|---|---|
| 1 | 12:19:55 | 53 | 0.883 | 0.60 |
| 2 | 12:21:56 | 53 | 0.886 | 0.75 |
| 3 | 12:23:56 | 54 | 0.883 | 0.64 |
| 4 | 12:25:57 | 45 | 0.883 | 0.50 |
| 5 | 12:27:57 | 26 | 0.886 | 0.50 |
| 6 | 12:29:58 | 8 | 0.894 | 0.59 |
| 7 to 46 | 12:31 onward | **0** | -- | -- |

What it establishes, and what it costs:

- **The sign rule holds and the within-frame comparison does what it claims.** The band is DARKER,
  never brighter. Meanwhile the frame's own median falls 3508 to 3154 ADU across the night as the
  sky darkens, roughly ten times the band's depth, and cancels completely because every comparison
  is inside one frame.
- **The geometry is what the design predicts**: a straight band at +9.8 degrees, bounded, sweeping
  monotonically off the frame as the mount tracks. Cloud has neither the straightness nor the
  monotone exit.
- **Neither number collapses** (see regimes above): 15% on the background, 0.5 to 0.75 on the stars.
- **The profile has no edge.** Perpendicular to the band: -16.2% at the core, -10.4% at 85 px,
  -4.3% at 145 px, under 1% only past 195 px and clear by 225, symmetric, with no step and no
  overshoot resolvable at 10 px bins (the data quantises in 4 ADU steps, so a fringe below that is
  invisible here; at 5.74 arcsec/px on a body metres away it would be anyway). Penumbra dominates;
  the geometric edge is unmarked.
- **Cutting it is not cheap.** Shadow alone 13.2% of the frame, plus a 50 px margin 16.5%, plus
  100 px 19.9%, plus 150 px 23.3%, plus 250 px 30.0%. Dropping the six frames instead costs 7% in
  master noise.

### The archive carries a LABELLED corpus, and it says the regimes are three

N.I.N.A. renames a frame the operator grades out with a `BAD_` prefix, and `CaptureRejection`
already keeps those out of every frame source (`FitsFolderFrameSource`, pinned by
`FitsFolderFrameSourceTests`; the bake reports them as `rejected-at-capture`). So they are a free
ground-truth set that is already excluded from stacking: 66 frames under `D:/Astro-Pics`, 27 under
`D:/Astro-Organized`. The owner says they are mostly roof, some trees.

Run through the same grid, they do not describe one defect. They describe three, and the powerline
is a fourth:

| case | what it is | depth | extent | shape | stars, band / clear |
|---|---|---|---|---|---|
| `Helix-Nebula/2026-08-01`, 21 of 21 `BAD_` | roof cutting the APERTURE | 7% | 175 of 576 cells | a smooth illumination ramp spanning ~2000 px, **no edge anywhere** | **0.89 to 0.96** |
| Helix 2022-08-31 frames 1 to 6 | powerline in the FIELD | 16% | 53 of 576 cells | straight band, 210 px penumbra each side | 0.50 to 0.75 |
| `2026-02-20 BAD LIGHT EXAMPLES`, 33 | **a CLOUD-OUT**, dawn only at the end | see below | whole frame | clouds over at 17:15 and never recovers | **0.07** |
| `C-101/2026-08-01`, 4 of 4 `BAD_` | unknown | none | none | flat to 0.5%, sky level normal for the night | n/a |

Four things follow, and each one changes something above.

- **An aperture cut is not a shadow, it is a VIGNETTE that moves.** A roof edge across the aperture
  does not silhouette onto the sensor; it removes part of the beam, so every pixel dims and the loss
  varies smoothly across the field. Cutting it out costs **61% of the frame** at zero margin. There
  is no version of masking that survives this case, and it is the commonest one in the corpus.
- **The AND with a star collapse is REFUTED, on the strongest case available.** The roof frames sit
  7% down on background with **0.89 to 0.96** of the clear-cell star count. A rule that requires both
  is silent on the obstruction the owner names first. Background deficit with spatial structure is
  the primary signal; the star count corroborates where it happens to move, and gates nothing.
- **A frame-wide defect is a DIFFERENT test and the per-cell one is deliberately blind to it.** It
  wants the frame's own sky level and star count against the SESSION's run, a per-frame
  session-relative statistic, beside a full-well check. Two statistics, two reference frames, and
  conflating them is how one of them ends up unable to fire.

### The cloud control, and why the cell test could not see it

`2026-02-20 BAD LIGHT EXAMPLES` was first read here as "dawn to saturation", from the level series
alone, with the per-cell test reporting almost every frame flat. **Both readings were wrong, and a
contact sheet settled it in one look.** The session is a CLOUD-OUT: it clouds over at 17:15 and
never recovers, and only the last six frames are dawn.

| frame | time | sky level | stars vs the clear frames |
|---|---|---|---|
| 1 to 2 | 16:55 to 17:14 | 896 | 1.32 to 1.44 |
| 3 | 17:15:58 | 904 | 1.00, cloud arrives, cell-map correlation to the next frame drops to 0.50 |
| 4 to 6 | 17:16 to 17:19 | 952 to 1078 | 0.67, 0.30, 0.13 |
| 7 to 26 | 17:20 to 18:58 | 1032 to 1528 | **0.07** |
| 27 to 31 | 19:14 to 19:29 | 3840 to 57878 | 0.00, dawn on top of the overcast |

**Cloud takes 93 percent of the stars while raising the sky 70 percent.** Against that, the roof took
4 to 11 percent of the stars and moved the level not at all, and the powerline took 25 to 50 percent.

Two faults kept the cell test from seeing any of it, and both are structural rather than a tuning
miss. It flags cells BELOW their frame's median, and cloud is the bright side. And normalising each
frame by its OWN cell median subtracts exactly the frame-wide brightening cloud causes: a sheet
covering most of the field moves the median with it and the ratios collapse toward 1.

**So the DEPTH of the per-cell structure is not a discriminator at all**: cloud 3 to 11 percent, roof
7 percent, powerline 16 percent, completely overlapping. The sign rule is real but it does not live
in a within-frame statistic. Cloud is frame-level and session-relative (level up, stars collapsing);
obstruction is cell-level and frame-relative (a bounded darker region, stars barely moving).

**A method note worth keeping: the first contact sheet showed pure noise and no stars in any frame,
which read as evidence.** It was decimating, taking every 5th pixel, and a 2 to 3 px star falls
between samples. Downsample a frame by block MAXIMUM when the question involves point sources.

### Can the existing gate see any of this? Measured, by running its own arithmetic

`FrameQualityFilter` is session-relative: it rejects a frame whose star count falls below
`median - sigma * 1.4826 * MAD`, capped by a keep floor. **`tianwen stack` leaves
`StackingOptions.QualityRejectSigma` null, so the gate does not run there at all**; the dataset bake
sets sigma 3 with a 0.5 reject cap. Fed the measured per-frame star counts of the three sessions:

| session | median | MAD | threshold | flagged |
|---|---|---|---|---|
| powerline, 6 of 46 obstructed | 4149 | 39 | 3976 | **all 6**, plus 3 more; the cap allows 23 |
| cloud-out, 24 of 33 clouded | 271 | 111 | **-223** | **none** |
| roof, 21 of 21 obstructed | 795 | 8 | 759 | **none** |

**The powerline is already caught, by the bake.** That is a correction to the gap this plan opens
with: a partial obstruction is not necessarily kept, and this one is not. It survives only in
`tianwen stack`, where the gate is off by default.

**The other two are invisible for the same structural reason: a session-relative outlier test cannot
see a defect that affects the MAJORITY of the session**, because the majority defines the median.
The cloud-out's median star count IS the clouded state, its threshold comes out NEGATIVE, and the
three frames holding literally zero detected stars pass the gate; the two clear frames are the
outliers, on the side nothing tests. The roof's 21 frames all carry it equally, so there is no
in-session contrast at all.

This is the sharpest argument in the plan for a detector that reads the FRAME rather than the
session, and it also says where the cheap win is: **an ABSOLUTE floor costs nothing and catches the
zero-star frames that a relative test cannot.** Registration's own quad match is the only thing
standing between those frames and the integration today.

### Before any of that: the two paths already disagree, and nothing says so

Fix this first, because it is a live divergence rather than a missing feature. **`tianwen stack` and
the dataset bake answer "is this frame fit to integrate" differently in four ways**, and the same
session stacked both ways produces two different masters with nothing in either output saying which
rule built it:

| | `tianwen stack` | dataset bake |
|---|---|---|
| does the gate run | **no**, `StackingOptions.QualityRejectSigma` is null | yes, `DatasetBuildOptions.QualityRejectSigma` 3 |
| reject cap | `FrameQualityFilter.DefaultMaxRejectFraction`, 0.20 | `QualityMaxRejectFraction`, 0.5 |
| a frame with ZERO detected stars | **no check anywhere on this path** | `SessionFrameAnalyzer` drops it before the filter |

The powerline session is the case in hand: six frames integrated by `stack`, all six dropped by the
bake. The zero-star row is the sharpest, because it is not a threshold disagreement at all. A frame
the detector found no stars in is unconditionally dropped on one path and unconditionally kept on
the other.

**None of this was decided.** The option's own doc says the null default "preserves the
pre-this-feature behaviour", a compatibility default from the omnibus that introduced
`FrameQualityFilter` ("stacking: Kabsch refinement + drizzle + SPCC + diagnostics omnibus", #8),
never revisited. It is the divergence class CLAUDE.md warns about under one job, one tool: a seam
with no file to review, where the two halves drift without anything failing.

So the frame-admission rule wants ONE definition that both paths read, with the absolute floor in
it, and a per-path override only where a real difference is intended and stated (the bake genuinely
prefers purity over yield, which is what its 0.5 cap says). Doing that first also gives this plan's
detector one place to land instead of two.
- **`BAD_` is a reliable POSITIVE and nothing else.** It does not say why (the corpus holds dawn and
  saturation under the same prefix as roof), and absence of it does not mean clean: the 2022
  powerline frames carry no prefix at all, because the capture software of the day had no grading
  and nobody renamed them. So **the unlabelled frames are NOT the clean control**, and the detector's
  actual job is the ones nobody marked.

The three scripts that took these numbers are
[`tools/obstruction-cell-stats/`](../../tools/obstruction-cell-stats/), in the pattern
`tools/coverage-edge-walk/` sets: point them at any folder of subs and they re-derive the table
above, which is what makes the cloud control a command rather than a rewrite.

**One method trap, found the hard way.** The first star-count pass thresholded each cell against its
OWN median and MAD, and reported a perfectly flat star count across the band, which reads as "the
star half of the rule never fires". It was the proxy defeating itself: inside the band the median
and the MAD are both lower, so the detection threshold follows the obstruction down. Threshold ONCE
per frame, off the clear cells, and the 0.50 to 0.75 above appears. Any per-cell statistic used to
test a per-cell defect owes the same check.

## The measurement pass: do this FIRST, on the desktop

**None of the thresholds above were written down, on purpose.** Grid size, "collapse", "darker" and
"mostly obstructed" are all numbers that need a real distribution behind them, and this repo's rule
is that a threshold without a measurement is a guess that ships. The archive is on the desktop
(`D:\Astro-Organized`, `D:\Astro-Pics`, `D:\Astro-Unsorted`, plus `E:` for the SharpCap captures);
the research was done on the laptop, which has only `C:` and a Google Drive mount.

Steps 1 to 3 are DONE for the obstruction side, on the Helix powerline session above. What remains:

1. ~~**Find the obstructed sessions.**~~ Done by asking the owner, which beat the census. The census
   route still stands for finding the rest: the bake's per-sub record (`#58`) carries the stage that
   dropped every sub with its HFD, ellipticity, star count and epoch. Look for a RUN of
   `StarCountTooLow` drops at one end of a night -- an obstruction is monotonic in time, where cloud
   is intermittent. **On the evidence above that search will under-report**: a 15% attenuation never
   pushed a whole frame under any session-relative star threshold, so this session's six frames were
   never dropped by anything and would not appear in such a search at all.
2. ~~**Confirm by eye, at 1:1.**~~ Confirmed by profile instead, which answers the same question
   with a number: straight, +9.8 degrees, bounded, no edge. The 1:1 look is still worth taking
   before fitting an edge model to a TREE, whose boundary is the one this cannot stand in for.
3. ~~**Measure the cell statistics across the transition.**~~ Done for the obstruction; see above.
   **The CLOUD CONTROL IS STILL OUTSTANDING and is the half that can still refute the design**, and
   the search for one is worth recording, because it says where NOT to look next.

   A screen over the filed sessions found three whose sky level is not smooth (mean absolute
   residual about a 9-frame running median, in units of the data's own quantisation step: HIP-80609
   2026-04-21 at 9.8, Tarantula 2025-10-18 at 8.6, eta Car 2025-01-14 at 7.2, against 0.01 to 0.14
   for three clear controls, a fifty-fold separation). **The roughest of them is not cloud.** Its
   residuals are 1.2 to 1.8 percent of the level, and its rough frames carry LESS cell structure
   than its own smooth ones (4.5 cells below 0.95 against 10.4, 1.9 above 1.05 against 3.5), so
   there is nothing spatial to measure a sign on.

   That is probably not a gap in the screen. **A filed session is a SURVIVOR**: the operator grades
   a clouded frame out with a `BAD_` prefix and does not file a clouded night at all, so the filed
   tree is close to the worst place to look for one. Look instead in the unfiled `Astro-Pics`
   captures, or shoot one deliberately. Whichever it is, the control must still show:
   - that the cloud cells are BRIGHTER, which is the whole discriminator;
   - that they are frame-wide and soft-edged where the obstruction is bounded and straight;
   - and that the depths do not overlap. **If they do, the sign rule is wrong and the design above
     needs revisiting before any of it is built.**

   Two traps the screen itself hit, both the same shape, both worth avoiding next time. A roughness
   statistic on a quantised level series **measures its own floor**: the first pass scored six of ten
   sessions at exactly 0.000, which was the 4 ADU step, and the second pass scored all six at zero
   because a MEDIAN absolute residual over quantised data is zero by construction (a running median
   returns one of the data's own values). Use the mean, quote it in units of the step so the floor is
   visible, and **order the series by `DATE-OBS`, never by filename**: a session captured in two runs
   concatenates out of order, which put 105 of 175 Tarantula frames in the wrong place and injected
   roughness that was pure bookkeeping.
4. **Only then** write the detector, with the measured numbers as the defaults and the measurement
   quoted at the constant, the way `BadPixelDetection` and `OverlayEngine`'s thresholds are.
5. **Re-measure the margin on a SECOND obstruction before shipping the cauterise.** 210 px is one
   body at one distance on one train; the ramp width scales with how far outside focus the body is,
   so it is a property of the obstruction, not a constant of the feature.

### Two traps this area already knows about

- **A whole-frame statistic over a CFA mosaic describes none of its four populations**
  (CLAUDE.md, at length). A per-cell background on an undebayered OSC frame has the same problem one
  level down: split by photosite before taking a median, and keep any subsample stride ODD so an
  undeclared mosaic cannot phase-lock.
- **A saturated core is not an obstruction and must not read as one.** The Great Orion Trapezium
  already fooled the rejection into writing an interior NaN island (`#51`); a cell containing a
  blown core has a strange star count and a strange background, and the detector needs to not care.

## Not in scope

The live session path. `ScoutAndProbeAsync` plus the zenith gauge already own that and have shipped;
this plan must not grow a second answer to the same question. The one thing worth sharing when both
exist is the classifier's geometry, not its policy.

## Conflicts

None with PR #324 (atlas hover / OpenNGC audit): that touches the sky-map UI, `OpenNgcCorrections`
and `tools/openngc-audit`, none of which appears above. The only shared files in the repo's usual
sense are `CLAUDE.md`, `TODO.md` and `docs/plans/summary.md`, and only in different sections.
