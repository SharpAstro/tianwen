# Stacking Workbench: from a folder of frames to a finished picture, in one window

**Status: NOT STARTED.** A plan for the owner to decide on before any code (the owner, 2026-10-17: "do we already have a milestone
for a GUI based stacking app? not just planetary etc", then "yeah lets do that"). Milestone `stacking-workbench`.

## Why, and what changes from before

The engines are done and measured, and almost all of them are reachable only from the command line:

- **Deep sky** is `tianwen stack` (`StackingPipeline`) and the `tianwen image` verbs (enhance, colour composition, gradient,
  deblur, star removal, LRGB). Nothing in a window drives them, inspects the subs a run rejected, or re-runs one step.
- **Planetary** is `planetary stack`, `compose`, `sharpen`, `look`, plus one path in a window: `tianwen-fits` stacks a SER through its
  Frames / Live / Auto switch (`PlanetaryAuto`), with no step a user can go back to.

`stacking.md` ruled a stacking GUI out ("A standalone stacking GUI app is not in scope ... If a dedicated stacking workbench becomes
desirable later it would be a separate project rather than an extension of tianwen", and its open question 4, "No new 'Stacking
tab' needed"). This plan is that later decision, taken by the owner, and `stacking.md` now points here.

What prompted it: Strata (Mac Observatory, a $34.99 macOS planetary app), whose page the owner kept under
`OneDrive/Dokumente/Astro-Info/strata`. Seven steps left to right in one window (Import, Analyze, Stack, Derotate, Sharpen,
Colour, Export), each step's work kept so the user can go back, a processing profile replayed over a folder, "No AI, only your
data". Its demo files are captures this repo already judges against (the Meade 16-inch Saturn SER of 2023-10-10, 17,934 frames, and
a KL-IR685 Jupiter from the same Cloudy Nights thread). Where its engine differs from ours the measurements stand: it suggests
keeping 20 % of the frames where ours keeps 50 % for a stack that is sharpened (#1083, measured on every twin); its wavelets are
octave-spaced as ours are (`ATrousWaveletTransform`, the holes doubling each layer), with sliders and repeated "passes" where ours
derives each layer's gain from the stack's own limb; its deconvolution "calculates the blur from your telescope, camera and filter",
which is our aperture target, the default since #1632. Strata is planetary, lunar and solar only. This workbench is deep sky AND
planetary, which no one tool does well.

## What is reused, step by step

Every step below is a routine that exists, has a CLI verb, reports progress and writes its result to disk:

| Step | Routine | CLI | Progress a window can show | What it leaves on disk |
|---|---|---|---|---|
| Deep-sky scan | `FitsFolderFrameSource`, `SessionDiscovery.ScanAsync`, `FitsHeaderIndex` | `tianwen stack`, `dataset build` | `StackingProgress` (`StackingPhase.Scanning`, `ScanSummary`) | the header index |
| Deep-sky grading | `FrameQualityFilter.Filter` (`FrameMetrics`, reject reasons, `FrameGateLimits`) | `image stats` | per group | the manifest's fate of every frame |
| Calibration | `MasterFrameBuilder`, `CalibrationResolver`, `Calibrator` | `stack`, `dataset masters` | `BuildingMasters` | `masters/` cache, `CalibrationProvenance` |
| Registration | `FrameRegistration`, `StackManifest` | `stack --manifest`, `--reference-frame` | `Registering` | `master_<slug>.manifest.json` (reference, transforms, fates) |
| Integration | `Normalizer`, the rejectors, `IntegrationStrategySelector` (drizzle) | `stack --strategy` | `Integrating` (`IntegrationProgress`) | the master and its `.rejection` / `.coverage` / `.badpixels` / `.stderr` maps |
| Deep-sky processing | `SharpenPipeline.CanonicalProgram` / `ProcessAsync`, `MasterPostProcessor` | `stack --enhance`, `image sharpen`, `image flatten`, `image autocrop` | `EnhanceProgress` | `_sharpened` / `_autocrop` masters |
| Mono colour | `ColourComposition.RunAsync` and its steps | `image align`, `linear-fit`, `lrgb`, `combine` | per step | a FITS per step, `FLUXSCAL` carried |
| Planetary analysis | `FrameGrader` (`Grade`, `SelectBest`, the cut / smear / dim / corrupt zeros), `FrameGradeCache` | `planetary grade`, `keeps` | `IProgress<double>` | `.grades` |
| Planetary stack | `PlanetaryBestStack.RunAsync`, `PlanetaryAuto`, `PlanetaryIdentification` | `planetary stack [--auto]` | `IProgress<double>` | `master_<name>.fits` |
| Planetary de-rotation | `PlanetaryComposition` (`Ingest`, `Register`, `Derotate`, `Join`) | `planetary compose ...` | `Action<string>` lines | a FITS per step |
| Planetary sharpening | `PlanetarySharpening.Sharpen` (derived gains, the aperture target, strength stops) | `planetary sharpen` | none yet (seconds) | `_sharpened.fits` and its preview |
| Planetary colour | `PlanetaryChannelAlignment`, `PlanetaryColourBalance`, `PlanetaryColourLook` | `planetary colour`, `look` | none yet | the balanced master, a look's rendering |

The viewer's pieces carry over: `ImageRendererBase` (the image, stretch, histogram, tone and white-balance popovers, the info strip),
`SplitCompareController` (before and after), `StandaloneViewerHost` (router, toolbar policy, between-frames steps, which runs headless
in CI), and the in-process job pattern of `EnhanceActions.EnhanceAsync` and `ViewerController.StartBatch` (a task, a linked
cancellation, a `SynchronousProgress` into state the frame reads).

## The rules, set before any code

1. **One routine per step, the CLI verb's.** The workbench is a host: it calls exactly what the verb calls and nothing between steps
   a file would not carry, as `ColourComposition.RunAsync` and `PlanetaryBestStack` already do for theirs. A step that cannot be
   expressed as a verb's routine is missing from Lib, and goes there first.
2. **Every step's result is a file, and the next step reads that file.** Going back and changing one step re-runs only the steps after
   it, decided by the digest of each step's inputs and settings, never by modification times.
3. **Every step shows its command line**, so what the window made is reproducible from a terminal and a bug report can carry it.
4. **The defaults are the measured best, and say so**: the keep, the stack's defaults, the derived gains and the aperture target, the
   colour balance. A knob shows its default and the measurement behind it; anything past the truth (a strength, a look) is labelled as
   taste.
5. **Nothing the gate removed is hidden.** Every frame a grade or gate rejected is listed with its reason (`FrameQualityFilter`'s
   reasons, `FrameGrader`'s zeros), and the user can put it back.
6. **Long work is a job with progress and cancel, off the render thread**, through the engines' own `IProgress`; no driver call or
   file read on the render thread (the repo's existing rules).
7. **The user's own data only.** An AI step (the enhance roles) runs only when asked and says which backend served it.

## The steps

### Deep sky

1. **Import**: a folder or a dataset root. The scan groups lights by target, filter, exposure and session, and lists the calibration
   frames it found and what each light would take.
2. **Inspect**: every sub's metrics (FWHM, HFD, ellipticity, stars, background, the guiding of its own exposure from
   `ImageMeta.Guiding`, which no stacking code reads today, #450), sortable, with the gate's limits drawn on each column; a blink
   through the subs; reject or restore by hand.
3. **Calibrate**: the masters built or taken from the cache, which master each light takes and why (`CalibrationResolver`'s penalties),
   a pre-calibrated light's `CALSTAT` honoured.
4. **Register**: the reference frame (chosen, or set), each frame's transform and residual from the manifest, the frames that did not
   register and why (#1543).
5. **Integrate**: the rejector and the strategy (drizzle where it pays), the weighting once it exists (#451), the result with its
   rejection and coverage maps.
6. **Process**: the canonical program as toggles in order (autocrop, gradient, SPCC white balance, deblur, denoise, star removal),
   each with before and after.
7. **Colour**: mono masters composed (`ColourComposition`), the stretch, a look.
8. **Export**: FITS, TIFF, PNG, JPEG XR, with the provenance and every step's command line in the file.

### Planetary

1. **Import**: SER and AVI files, or a folder of them; a night's captures ranked against its best (`planetary survey` and the grades
   already measure it).
2. **Analyze**: each capture's quality curve, the frames the grader zeroed and why, the keep (50 % by default for a sharpened stack,
   #1083; derived per capture once #817 is done).
3. **Stack**: the alignment points on the planet, drizzle where the sampling asks for it (each channel's cutoff, which the sharpening
   already reads, tells whether a capture is undersampled), the master.
4. **De-rotate**: several stacks of one night joined through the spheroid (`PlanetaryComposition`), Saturn's globe turned under rings
   that stay.
5. **Sharpen**: the derived gains drawn per layer with the transfer they make, the target (the aperture where the pixels resolve the
   telescope, #1632), the strength stops past the truth as an option.
6. **Colour**: the channels aligned by their limbs, the balance to the planet's own colour through the camera's matrix, a look
   (Boosted, Posted) as a rendering.
7. **Export**: as for deep sky.

## The project, and a processing profile

A project is a folder: `project.json` (the inputs, each step's settings, each step's output and the digest it was made from) beside
the step outputs. A **processing profile** is a project's settings without its inputs; a batch replays one over many inputs, headless
first (`tianwen workbench run <profile> <inputs>`), so a night of captures runs unattended and the window shows the queue.

## Where it lives (W0, the owner's decision)

- **A. A mode of `tianwen-fits`** (Astro Photo Viewer): already in the Store and on macOS, already stacks a SER. But the viewer is a
  viewer; a project, a step bar and batches change what the Store app is, and its size.
- **B. A new app, `tianwen-stack` (name open), in this repo**: an AOT binary on the same SdlVulkan and DIR.Lib stack, sharing the
  viewer's widgets and `StandaloneViewerHost`. One more binary to package (a release asset, a `.dmg`, perhaps an MSIX), and the
  viewer gains "Open in the workbench".
- **C. A tab of `tianwen-gui`**: the GUI is the rig's app, bound to its node; processing yesterday's data should not need a node,
  and the GUI does not register the AI enhancers.

**Recommendation: B**, in this repo rather than a separate project, since every engine it hosts is `TianWen.Lib`'s and a second repo
would only add a package hop to each change. Running its jobs on the node (`NodeJobs`, for a rig's own computer to process its night)
is later and optional: the node has no stacking endpoint today, and its enhance endpoint is not on the job model yet
(`server-enhance-job-model.md`).

## Phasing

| Phase | Scope | Depends on | Issue |
|---|---|---|---|
| W0 | The owner picks where it lives (A, B or C) and its name | | #1640 |
| W1 | The step runner and the project, headless | W0 | #1641 |
| W2 | The app's shell: the step bar, the views, jobs | W1 | #1642 |
| W3 | The planetary steps | W2 | #1643 |
| W4 | Deep sky: import, inspect, calibrate | W2 | #1644 |
| W5 | Deep sky: register and integrate | W4 | #1645 |
| W6 | Processing and colour, both kinds | W3, W5 | #1646 |
| W7 | Profiles and batches | W6 | #1647 |
| W8 | Packaging and release | W7 | #1648 |

### W0 Where it lives

Tracked as #1640. Decided by the owner from the three choices above, with the name.

### W1 The step runner and the project, headless

Tracked as #1641. A step contract in Lib (inputs, settings, the routine, its output, the digest), the project file, and invalidation by digest; every step
listed above wrapped around its existing routine, with no logic of its own. `tianwen workbench run|status` drives it, so the whole
contract is tested in CI with no window: a project run twice re-runs nothing, a changed setting re-runs only the steps after it, and
each step's output is bit-identical to its CLI verb's.

### W2 The app's shell

Tracked as #1642. The step bar left to right, Original / Enhanced / Comparison views (`SplitCompareController`), a job panel with progress and cancel fed
by the engines' `IProgress`, each step's command line. Built so `StandaloneViewerHost`'s headless test path drives it in CI at DPI 1
and 1.5.

### W3 The planetary steps

Tracked as #1643. Import to Export for planetary, at parity with the viewer's Auto view (the same master to the bit for the same capture and options),
then each step's own view: the quality curve and the zeroed frames, the alignment points, the derived gains, the colour.

### W4 Deep sky: import, inspect and calibrate

Tracked as #1644. The scan's groups, the subs' metrics table with the gate's limits, the blink, reject and restore, the masters and what each light takes.

### W5 Deep sky: register and integrate

Tracked as #1645. The manifest's transforms and residuals, the frames that failed, the integration and its maps; a rejected frame restored re-integrates
from the manifest, not from scratch.

### W6 Processing and colour

Tracked as #1646. The canonical enhance program as ordered toggles with before and after, mono colour composition, the planetary colour step's balance
and looks.

### W7 Profiles and batches

Tracked as #1647. A profile saved from a project, replayed over a folder headless and in the window's queue.

### W8 Packaging and release

Tracked as #1648. The binary in the release assets, a `.dmg`, and the Store question if W0 chose B.

## Not in it

Mosaics, live stacking during a session (the session's own path, `stacking.md` Phase 15), comet-specific integration beyond what
`tianwen stack --comet` already does, and lunar or solar surfaces (Strata's other half) until the planetary steps stand.
