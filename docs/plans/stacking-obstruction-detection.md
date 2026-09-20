# Plan: detect an obstruction from the frames themselves, in stacking

Status: **RESEARCHED, NOT STARTED.** Backlog `#48` (issue #307). The gap and the design below are
established against the code; **every threshold is deliberately unset**, because the machine that
did this research has no archive attached (`D:` lives on the desktop). The measurement pass is the
first task, not a formality, and it is written out in full so the next session can start cold.

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
| **Obstruction** (roof, tree, dome slit) | collapse, sharply bounded | **DARKER** -- it blocks skyglow |
| **Cloud / haze** | fall, frame-wide and soft-edged | **BRIGHTER** -- it scatters light back |
| **Dew / frost** | fall, frame-wide, worst at the edges | brighter, and HFD widens with it |

So the rule is not "few stars here" but "few stars here AND this region is darker than the rest of
this frame". A tree is darker AND fractal-edged; a roofline is darker AND straight; a dome slit is
the inverse geometry (a bright stripe of sky inside a dark surround), which the same two numbers
describe with the signs swapped.

**Bounded by construction, and that is the point**: the comparison is within ONE frame, so
transparency, moon, gradient and exposure all cancel. A frame-wide drop moves every cell and is
therefore not an obstruction, which is exactly the discrimination `FrameQualityFilter` cannot make.

### 3. The remedy is a MASK, not a drop

`PixelRejection.MarkAbsent` and the coverage machinery already exist (`#67`, merged in #315:
`IntegrationResult.Coverage`, the `<stem>.coverage.fits` sidecar, `TryReadCoverageMap`). An
obstructed sub should contribute the four fifths of itself that are sky, with the obstructed cells
marked absent, rather than being dropped whole. Dropping is the fallback for a frame that is mostly
obstructed.

Two consequences that follow and must not be missed:

- The coverage map already carries "no frame covered this pixel", so a masked region lands in the
  SAME field the canvas ring uses, and every downstream consumer (the auto-crop's coverage tier, the
  gradient report's mask, `IntegratedMaster.Labelled`'s all-NaN guard) understands it already.
- **A mask is a weight change, so the normaliser must see it.** A frame whose cells are partly
  absent must not be normalised on a statistic taken over the absent region.

### 4. Where it goes

A pure classifier beside the existing gate, not inside it:

- `src/TianWen.Lib/Imaging/Stacking/FrameObstructionDetector.cs` -- new, pure: takes the star list,
  the frame, the grid size, returns per-cell verdicts plus a frame-level classification.
- `FrameQualityFilter` / `FrameRejectReason` -- one new flag for a frame rejected as
  mostly-obstructed. Flags already, so it composes.
- `StackingPipeline` / the register loop -- carry the mask into integration.
- `RegistrationCensus` -- report obstructed cells per frame, so the census says it the way it says
  every other drop cause since `#58`.

## The measurement pass: do this FIRST, on the desktop

**None of the thresholds above are written down, on purpose.** Grid size, "collapse", "darker" and
"mostly obstructed" are all numbers that need a real distribution behind them, and this repo's rule
is that a threshold without a measurement is a guess that ships. The archive is on the desktop
(`D:\Astro-Organized`, `D:\Astro-Pics`, `D:\Astro-Unsorted`); this research was done on the laptop,
which has only `C:` and a Google Drive mount, so the measurement could not be run.

What the next session needs to do, in order:

1. **Find the obstructed sessions.** The bake's per-sub census (`#58`) records the stage that
   dropped every sub with its HFD, ellipticity, star count and epoch; the ledger is
   `stats/sessions.jsonl` (`#53`). Look for sessions with a RUN of `StarCountTooLow` drops
   concentrated at one end of the night -- an obstruction is monotonic in time (the target sets
   behind the roof and stays there), where cloud is intermittent. Candidate sessions are also worth
   asking the owner for directly: he knows which targets his roofline eats.
2. **Confirm by eye on one session, at 1:1.** Render the subs either side of the transition. An
   obstruction has an EDGE; confirm which kind (straight roofline, fractal tree, slit) before
   fitting anything to it. Judge at 1:1, never by a band median -- the deconvolver work records why.
3. **Measure the cell statistics across that transition**, in a throwaway script under
   `tools/` (the pattern `tools/coverage-edge-walk/` and `tools/openngc-audit/` set). For each sub,
   the 8x8 grid of (star count, background median), and the same for a clean session as the control.
   The numbers that come out of this ARE the thresholds:
   - how far a cell's star count falls, as a fraction of that frame's own cell median;
   - how far its background falls, in the same relative terms and in noise units;
   - how many contiguous cells an obstruction occupies at its smallest worth catching;
   - and the separation between those and the cloud/haze control, which is what says the
     discriminator works at all. **If the two distributions overlap, the sign rule is wrong and the
     design above needs revisiting before any of it is built.**
4. **Only then** write the detector, with the measured numbers as the defaults and the measurement
   quoted at the constant, the way `BadPixelDetection` and `OverlayEngine`'s thresholds are.

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
