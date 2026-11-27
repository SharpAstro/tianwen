# Stacking Workbench: from a folder of frames to a finished picture, in one window

**Status: W0 DECIDED 2026-11-22, W1 DONE 2026-11-23 (the operation contract, the step store, the project and the runner, with twenty-three operations; the last leg was #2490), W10 PARTIAL (image crop and the W10 audit are done), the rest NOT STARTED.** A plan the owner decided W0 of (the owner, 2026-10-17: "do we already have a milestone
for a GUI based stacking app? not just planetary etc", then "yeah lets do that"). Milestone `stacking-workbench`.

**Re-cut 2026-11-19** from a stacker into the processing application: image editing as operations, a non-destructive edit list,
workflows with gates, a guided view and a windowed one. What changed and why is in
[The re-cut of 2026-11-19](#the-re-cut-of-2026-11-19); W0 to W8 keep their numbers and W9 to W13 are new.

## Why, and what changes from before

The engines are done and measured, and almost all of them are reachable only from the command line:

- **Deep sky** is `tianwen stack` (`StackingPipeline`) and the `tianwen image` verbs (enhance, colour composition, gradient,
  deblur, star removal, LRGB). Nothing in a window drives them, inspects the subs a run rejected, or re-runs one step.
- **Planetary** is `planetary stack`, `compose`, `sharpen`, `look`, plus one path in a window: `tianwen-fits` stacks a SER through its
  Raw / Live / Full switch (`PlanetaryAuto`), with no step a user can go back to.

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
   each with before and after. Star removal carries its **core protection** (`image remove-stars`, on by default, `--no-protect-cores` turns it off, #1658): the
   brightest part of each galaxy or nebula is kept out of the stars layer whichever remover runs, a nucleus like M31's or a knot like
   the Lagoon's that a remover otherwise takes as a star. The step draws the cores it found on the image, and the user drops one or
   adds a region with a click. StarXTerminator leaves that to a hand-drawn mask (the owner, 2026-10-19).
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

## The re-cut of 2026-11-19

The owner, in one sitting on 2026-11-19, after DSPX was looked at ([pixinsight-parity.md](pixinsight-parity.md) "DSPX"):

- "we might have this as a stand-alone app as well. at most the tianwen gui as is will get a live stacking view."
- "in addition to processes, it would be good if we also support workflows (a combination of processes, simple version is just a
  step by step apply of processes, but it could also include gates, like if mono/narrowband/... if luminance data present, .... if
  >60 frames do bayer drizzle, ..."
- "i wonder if the live stacking is just a switch mode in the frame preview (during session)"
- "Astro pixel processor has a per step view which I very much like, but the PIxinsight/this new thing is a lot more flexible, so
  maybe if we have a pre-build workflow that is a bit modelled for what we do in the viewer, with plate solving, spcc (plus of
  course the registration and whatnot)"
- "should we also have an optional MDI mode? happy to have a switch somewhere for that (guided view would be SDI)"
- "remember there's also an image editing component to this. so one of the processes is crop, and an auto-crop (currently we have
  this in gpu int he viewer but I feel this might not be enough, probably okay in guided view). one of the guiding principles are
  non-destructive editing (Image class), but I do see if you open 5 masters etc we might have to put intermediate files to
  hard-disk. I feel we already have some support for this."

So the plan is no longer a stacker plus an enhancer. It is four things, each built on the one below it:

| Thing | What it is | Where it is planned |
|---|---|---|
| **Operation** | One unit of work with typed settings, one routine in Lib, one CLI verb. Crop, gradient, SPCC, denoise, register, integrate | [An operation](#an-operation), W1, W10 |
| **Edit list** | An image is its original file plus an ordered list of operation instances. Nothing is written over the original | [The edit list](#the-edit-list-non-destructive-by-construction), W1 |
| **Workflow** | An edit list without its inputs, plus gates and per-group loops. A guided view is a workflow drawn as a step bar | [Workflows](#workflows-operations-in-order-with-gates), W9 |
| **Project** | A folder: the inputs, the edit lists, the store of step outputs | [The project](#the-project-and-a-processing-profile), W1 |

**The name: operation** (the owner, 2026-11-22). Until then this plan, its issues and the drawing said *process*,
PixInsight's word, and the owner's words quoted above still do. *Function* was the other candidate; it reads as a formula,
which this plan leaves out. In code the type is never a bare `Process`, which is an operating-system process here
(`BoundedProcess`). The drawing's screen is still named Process, as drawn.

**The drawing.** The design is DSN-1, kept in #1640 with its renders on the branch `design/1640` (six versions on 2026-11-19: the
Guided, Stack, Process and Workflow screens, crop, the windowed mode, the edit list). It is a drawing made without reading the
code. Where it and this plan disagree, the plan says so in the section concerned, and the code decides.

### An operation

An operation is described once, in Lib, and everything else reads the description: the panel in the window, the workflow file, the
edit list, the command line shown under a step (rule 3).

| Part of the description | What it is for |
|---|---|
| Id and version | The digest of a step (rule 2). A retrained model or a changed algorithm is a new version, so an old output is never taken for a new one |
| Settings | A record, serialised to JSON through a source-generated context (AOT). Each setting carries its default and, where there is one, the measurement behind it (rule 4) |
| Inputs | How many images, and of what kind: one image, several (combine, LRGB), or a set of frames (calibrate, register, integrate) |
| Preconditions | Facts the input must have: linear, plate-solved, three channels, a coverage plane. The same facts a workflow gate tests ([The facts](#the-facts-a-gate-may-test)), so an operation the image is not ready for is shown as such, with the reason |
| How its output is kept | **Stored** (a file in the step store: denoise, gradient, integrate), **derived** (recomputed from its input when asked, in milliseconds, never stored: crop, a flip, white-balance gains, a stretch) or **metadata** (it writes no pixels: plate solve, star detection, statistics) |
| How it previews | **In the shader** (stretch, white balance, crop: free while dragging), **on a region** (run on the visible part of the image and shown split: denoise, deblur) or **not at all** (a whole-frame fit such as gradient or SPCC, whose answer for a region is a different answer) |
| Progress and cancel | The engine's own `IProgress`, as rule 6 |

**What the code holds** (W1, done; #1641; [workbench-operations.md](../architecture/workbench-operations.md)):
the id and version, the settings, the images an operation takes, how its output is kept (stored, derived, or as
metadata: a finding in the store and no pixels, with the plate solve as the first, #2469), what serves it, its command
line, and the lines it reports. The preconditions come with the facts (W9), and how it previews with the panel that
shows it (W12).

**There is one description, not two.** `SharpenStep` and its records (`DeblurStep`, `GradientCorrectionStep`, `RemoveStarsStep`,
the stretch steps, `RecombineStep`) are already a typed list of operation settings that `SharpenPipeline` runs; they are not
serialisable today. W1 decides whether those records become the operation settings or are wrapped by them, and whichever it picks
there is no second hierarchy beside them.

**W1 picked: wrapped** (2026-11-23, #1641). An operation's settings are a class of settable properties, each with its
default, read and written through a source-generated JSON context. The records cannot be that as they stand: they are
immutable and positional, they are one hierarchy that a JSON file could only tell apart by a type tag, and one of them
(`WhiteBalanceStep`) holds a measured calibration, which is a result and not a setting. So the records stay what they
are, the pipeline's program in memory, and the enhance operation turns its settings into them in one place. No operation carries a step of its own beside them.

**A preview on a region is honest about what it is.** An operation whose thresholds are in noise units of the whole frame, or whose
model normalises over the whole input, does not give the same pixels on a crop as on the frame. The description says which
operations preview faithfully on a region; the others show the region preview labelled as approximate, or none.

### The edit list: non-destructive by construction

`Image` is already logically immutable ([image-pipeline.md](../architecture/image-pipeline.md), "Image mutability"): every
operation returns a new image, and the five in-place exceptions are named `Adopt*` or pinned by a test. The edit list is that
rule taken to the file level:

- **The original file is never written.** An edit list names it and its digest. Every output goes to the step store.
- **A step's key is a digest** of its input's key, the operation id and version, its settings, and what served it (the backend and
  model of an AI step, rule 7). The key is computed from keys, so a stored output is never hashed.
- **The step store is addressed by that key.** Switching an edit off and on again, or going back and forward, finds the file and
  runs nothing. Changing edit 3 of 7 re-runs 4 to 7 and nothing before (rule 2).
- **An edit has an on/off switch and its settings can be reopened.** That is DSN-1's "Edits (original untouched)" panel, and its
  button reads Keep rather than Apply.
- **One image can feed several lists.** Star removal makes two images (starless and stars) from one; a combine takes several
  lists' heads as its inputs by key. So the structure is a graph of keys, drawn per image as a list. In the project file an
  edit names the other images it takes (`inputs`), each as its own edits leave it; images that take one another in a circle
  are refused.
- **An image can start from another** (`from`), or from an extra result of one of its steps (`result`): the stars a star
  removal took out are an image of their own that way (#2465).
- **Several images can go through one run.** Deblur, denoise, star removal and the blur match put masters through together
  on one scale. Each master's list names the others, the run's key is made from all of them in sorted order, and it
  happens once; the lists that did not start it find their results in the store. A window draws that as one edit on
  each of the images; in the file each is an image that starts from the master as it was before the run, which is what
  keeps the graph free of circles ([workbench-operations.md](../architecture/workbench-operations.md)).
- **Reordering edits** is possible wherever the preconditions still hold, and costs a re-run from the first moved edit. It is not
  in the first version ([Decisions for the owner](#decisions-for-the-owner), 7).

The viewer already remembers a display per file (`ViewerDisplayMemory`, #1814) and already chooses between keeping a pre-enhance
image and re-reading it (`EnhanceRevertPolicy`). The edit list replaces neither: it is the workbench's, and the viewer stays a
viewer.

### Crop and auto-crop

What exists, read from the code on 2026-11-19:

- `Image.Crop(PixelRect)` makes the cropped image, `WCS.CroppedTo` moves the solution with it, and
  `AlignmentProvenance.ForCrop` carries the provenance.
- **The viewer's auto-crop is a display clip.** `ViewerState.DisplayCrop` hides pixels in the draw; the comment at the toggle
  says "the crop only ever hid pixels that were there the whole time". It is passed to Enhance as a rectangle, so the pipeline
  does see it.
- **The rectangle solver is `MasterAutoCrop.Scan` in `TianWen.Lib/Imaging/Stacking`** (#2237), beside `MasterCrop`. It takes the
  master's coverage plane where the sidecar has one (`Image.CoverageCropRectangle`), else the edge walk over the pixels
  (`CoverageEdgeWalk` from `Image.LargestCoveredRectangle`), under a `MasterAutoCropRule`: the policy for a refused edge
  (`CoverageTrimPolicy.KeepEveryPixel` for a person looking, the producing policy with a declined fraction for a model) and an
  optional margin. It releases the coverage image it reads. The viewer calls it with `MasterAutoCropRule.ForViewing`.
- `DatasetMasterInventory.SkyPatchOf` keeps its own exact-else-union answer: its fallback runs no edge walk, and taking the
  routine's tier would trim the walk's edges and move the patch.
- `image autocrop` is the CLI verb, and **it builds `CropSettings` and calls `CropOperation`**, whose `TryResolve` calls `MasterAutoCrop.Scan` with `MasterAutoCropRule.ForModel(--trim-declined,
  --margin)`, keeping only parsing and printing. A rectangle the user gives is `image crop <input> --x --y --width --height [-o]` (#2244;
  the owner, 2026-11-19: "we can add a crop verb, takes in four parameters, but that is a seperate thing"). It calls
  `CropOperation` too (`TryResolve`, then `Write`), which goes on to `MasterCrop` in Lib, which refuses a rectangle not wholly inside the image in words and writes nothing, moves the WCS
  (`WCS.CroppedTo`) and carries the alignment provenance (`AlignmentProvenance.ForCrop`) and the stack cards (`STACK_N`,
  `SNAPSHOT`, `REJ_TOT`, `REJ_RATE`, `STRATEGY`; the two rejection cards stay those of the uncropped master). `image
  autocrop` writes through the same routine, so its output carries the same. The pixels are read once per crop.
- **The maps that describe the master's pixels are cropped with it** (#2274): coverage (or a drizzle master's weight
  plane), the rejection fraction, the saturated-frame count and the standard error, each through its own reader and
  writer and each by the same rectangle (`MasterCrop.CropLoaded`). A crop therefore keeps a coverage plane of its own,
  so `MasterAutoCrop.Scan` on it answers from the exact tier, and SPCC still finds its saturation map. The rejection
  map's `REJ_RATE` is the cropped map's own average. A map that is not on the master's grid is left out and named, a
  map of an earlier file beside the output is removed first, and a refused crop touches none. The bad pixel map is
  neither carried nor removed: it is the sensor's photosites, not the canvas's pixels, so a crop onto the master's
  own path keeps the master's. One whole map is held at a time (read, cropped, given back), not four beside the
  master. Until this was written both verbs left every map behind, as `autocrop` had before #2244.

What the workbench needs, and why the display clip is not enough for it: an operation after the crop must be handed the cropped
pixels (a gradient fit must not see the ragged edge), an export must be the cropped image with a moved WCS, and the user must be
able to set the rectangle by hand.

- **Crop is one operation**, and auto-crop is not a second one. Its setting is either a rectangle or a rule ("where every frame
  covers", "the largest covered rectangle"), resolved against the input when the step runs, so a workflow carries the rule from
  one master to the next. By hand: drag the frame, fix an aspect ratio, type the four edges.
- **Its output is derived, not stored.** The edit holds the rectangle; `Image.Crop` on its input takes milliseconds and no file
  is written. Widening the crop later is a changed setting, as DSN-1 v6 draws it. Built as `CropOperation` (#1641): the
  rule is its `auto` setting, resolved by `MasterAutoCrop.Scan` each time the step is made. Handed to the next step it is
  the pixels and the moved plate solution; the cards and the map sidecars `image crop` carries go with it only when it is
  written as a file from a file. An operation that reads a map sidecar after a crop (SPCC's saturation map, #2284) has to
  settle how the maps travel first.
- **While the frame is dragged the display clip draws it**, over the uncropped texture, so nothing is uploaded again. The clip
  and `Image.Crop` are given the same rectangle from one place; that they agree to the pixel is a test.
- **The rectangle solver is in Lib** (rule 1, and the repo's rule that pure math lives in `TianWen.Lib`): the viewer and
  `image autocrop` (through `CropOperation.TryResolve`) call `MasterAutoCrop` today, and so does the workbench. W10.
- **`tianwen stack` does not call it yet.** Its autocrop (`MasterPostProcessor`) is the registration intersection (the
  rectangle every frame's outline covers) clipped by the in-memory coverage (`ClipToCoverage`, `LargestCoveredRectangle` at
  `AbsenceFraction` 0.5 over 8 px blocks, where absence is a block under half of full weight), and has no edge walk. The
  solver's coverage tier reads the sidecar's plane over 16 px blocks (on a frame-count plane the block median must reach every
  frame, minFraction 1.0; 0.95 applies to a drizzle-weight plane only), and its fallback walks noise. The two rules differ: the stack's clip was tuned for a hole inside the rim
  (the SV605CC Tarantula corner wedge) where the viewer's 0.95 lost 34 good columns. Question for the owner (#2291): should `stack` call the routine?
  Unifying would change what `stack` writes: its `_autocrop` FITS and display outputs would take the sidecar's rectangle (or the
  walk's) in place of the intersection, losing or keeping some border columns, and the corner-wedge case would need its
  threshold carried into the routine as a rule field first. Not changed in this leg.
- **Guided uses the automatic rectangle** with no handles shown unless asked for, which is the viewer's behaviour and what the
  owner judged enough there.
- DSN-1 says "cuts where fewer than 90 % of frames overlap". That number is the drawing's. `CoverageCropRectangle` has no such
  threshold today; if one is wanted it is a setting with a measured default, not a constant in a panel.

Rotation and flips are a separate operation (`Image.Affine` exists; no verb). A quarter turn and a flip lose nothing; any other
angle resamples, and "north up" needs a plate solve first, which is a precondition.

### Where the pixels live: memory, the GPU, the disk

The owner's case is five masters open at once, each with intermediates. A three-channel 6000 x 4000 float master is about 275 MB of
planes; five of them, each with six stored edits beside the original, are 35 files and about 9.4 GB.

**What already exists** (all of it built for the viewer, [viewer-memory-footprint.md](viewer-memory-footprint.md)):

| Piece | What it does today |
|---|---|
| `DocumentCache.TotalBudgetBytes` | A total across open documents, 384 MB by default (three times `EnhanceRevertPolicy.RetainBudgetBytes`), a settable number and never an OS memory reading |
| `DocumentCache.Admit` | When a document opens over the budget, the least recently used document the file can give back releases its planes |
| `AstroImageDocument.EnsurePlanesAsync`, `PlanesPin` | A consumer reads the planes back from the file, off the render thread, and holds a pin while it reads; a pinned document is never released |
| `IReadoutTileSource` (`FitsReadoutTiles`, `FpackReadoutTiles`, `TiffReadoutTiles`) | The cursor readout without planes: 64 x 64 tiles read from the file |
| `EnhanceRevertPolicy.Decide` | Keep the pre-enhance image (instant revert) or re-read it (no memory), by its size |
| `ScratchSpace.Refusal` | A write that would leave less than a reserve free on its drive is refused in words before a byte is written |
| The verbs | Every step already writes a FITS, and `tianwen stack` writes the master beside its `.rejection`, `.coverage`, `.badpixels` and `.stderr` sidecars |

**What is missing for the workbench:**

1. **A result that exists only in memory can never be released.** `DocumentCache` skips "an in-memory frame, a crop", because
   nothing can give the planes back, and an Enhance result looks to be such a document (W11 confirms it first). Rule 2 closes this without new machinery: a stored
   step's output is written to the step store by the job that made it, the document then takes that file as its readout source,
   and from there it is an ordinary file-backed document the cache can release. What is new is small: attaching a readout source
   to a document after its file is written.
2. **The budget is the viewer's.** 384 MB holds one large master, and it is a constant: the viewer measures nothing.
   **The owner, 2026-11-19:** "maybe we measure the free space on program start, page size etc, and basically try to avoid
   swapping". The stacker already measures: `HostSnapshot.Take` reads the memory the process can still grow into without paging
   (`GC.GetGCMemoryInfo`) and the free space of a drive, and `IntegrationProbe` sizes a stack from it. So the workbench takes
   **one snapshot when it starts** and sets `TotalBudgetBytes` and the step store's reserve from it, with a setting to override
   each. Once per run keeps what `EnhanceRevertPolicy` protects (a decision does not flip because another program allocated
   between two clicks) and still fits the machine. What fraction of the free memory is the budget is measured in W11 against the
   one thing asked for, no swapping, and is not guessed here. DSN-1's bar ("12.5 of 16 GB in use") shows resident planes against
   that budget, not the machine's memory.
3. **Release happens only when a document opens** (the owner's answer on #1852). In the workbench a finished step is a new
   document, so the same trigger fires after every step; nothing to add until a measurement says otherwise.
4. **The GPU has no budget at all.** A released document keeps its texture. With several images tiled that is the next thing to
   run out, and its size depends on the texture format and the mips, so it is **measured before it is designed** (the pipeline's
   own image sizes, the `StagingBufferSize` pattern, never working set). The likely answer is that only a visible image holds a
   full texture.
5. **The step store grows.** It is a cache, since everything in it can be made again from the originals and the edit lists:
   - Every write asks `ScratchSpace.Refusal` first, against a reserve that is a setting.
   - When the store passes its own size limit, the least recently used outputs go first. An output that is the current head of an
     open edit list is kept. An evicted edit is shown as "will be recomputed".
   - **It deletes only files it wrote**, found through its own index of keys, inside its own folder. Never an original, never a
     file by pattern.
   - Outputs are plain float FITS. FITS.Lib cannot write a compressed file, so the store is not compressed.
6. **Where the store is.** A project's is its folder. An image opened with no project gets a store under the app's data folder
   until it is saved as a project; the folder is a setting, because a system drive is often the small one. A new directory goes
   into the tree in `docs/architecture/runtime-data.md` in the same change.

Derived edits (crop, flips, white balance, stretch) store nothing, and their pixels exist only while a consumer reads them, so an
image that has only been cropped and stretched costs what its original costs.

## Workflows: operations in order, with gates

A workflow is an edit list with the inputs taken out and three things added.

- **A step**: an operation and its settings.
- **A gate**: one fact, a comparison, and two branches. A branch is a list of steps and may hold another gate, so a workflow is a
  tree and never a loop.
- **For each**: a body run once per group of the inputs (per filter, per target, per session). "Stack each filter" is this, and
  it is what makes a mono workflow one workflow.

It is a JSON file. There is no expression language and no scripting in it ([Not in it](#not-in-it)).

### The facts a gate may test

A closed list, each one a function in Lib with a test, shared with the operations' preconditions:

| Fact | Known when |
|---|---|
| Sensor is mono or colour (and its Bayer pattern) | After the scan |
| The filters present: luminance, RGB, which narrowband lines | After the scan |
| The number of lights, per group | After the scan, and again after grading removes some |
| Calibration frames present: darks, flats, bias, for which groups | After the scan |
| The kind of target: deep sky, planetary, a comet | After the scan (`PlanetaryIdentification`, the session's target) |
| Sampling: pixel scale against the measured FWHM | After grading |
| The frames are dithered | After registration |
| The image is linear, is plate-solved, has a coverage plane, has three channels | Per image, at the step |
| An enhancer serves this role (`IEnhancerAvailability.Serves`) | At the step |

A fact not yet known when a workflow is looked at is drawn as "decided after step N". DSN-1's **dry run** evaluates what can be
known from the scan and marks the branch the data would take.

### A gate shows a decision, it does not replace one

The owner's example is "if >60 frames do bayer drizzle". Today that choice is not a frame count: `IntegrationStrategySelector`
gates each strategy on whether it fits and ranks the survivors on fidelity and speed. A built-in workflow's gate **calls the
decider the pipeline already has** and shows its answer and its reason; it never carries a second threshold beside it (rule 4:
the defaults are the measured best). A user's own gate may test any fact against any number, and that is the user's choice,
labelled as theirs.

### The built-in workflows, and what makes one correct

- **Deep sky, as `tianwen stack` and the viewer do it**: scan, grade, calibrate, register, integrate, crop, plate solve, white
  balance by SPCC, gradient, deblur, denoise, stretch, export. This is the "pre-build workflow modelled for what we do in the
  viewer". Its order is **read off the code** when W9 writes it (`StackingPipeline`, `MasterPostProcessor.WriteMasterAsync`,
  `LinearEnhanceProgram.For`), not off the drawing, whose ten steps put crop before the solve.
- **Its test is parity**: run headless on a fixture, it leaves the same files, to the bit, as `tianwen stack --enhance` on the
  same input. A built-in workflow that drifts from the pipeline is a bug in the workflow.
- **Mono LRGB** and **narrowband** follow from `ColourComposition.RunAsync` the same way (the palettes wait on
  [narrowband-colour.md](narrowband-colour.md) P1). **Planetary** follows from `PlanetaryBestStack` and `PlanetaryAuto`.
- A user's edit list can be **saved as a workflow**, which is how a reusable one is made without an editor.

### The guided view is a workflow drawn as steps

Astro Pixel Processor's per-step view, which the owner likes, is one fixed workflow. Here the step bar is drawn from whichever
workflow is open: its top-level steps left to right, a gate drawn as the branch the data took. The guided view is the easy road
and always shows one image.

**Any step can be started directly, as in APP** (the owner, 2026-11-19: "you can just go straight to 6. Integrate and start
integration and it will run (or re-run if required) all previous steps unless they are compatible"). That is rule 2 read
forwards: starting step 6 computes the key of every step before it, takes from the step store each one whose key is there, and
runs the rest in order. So there is no separate "run all" and no way to integrate over stale calibration. The bar says, before
anything starts, which earlier steps will run and which are kept.

**It runs until a step needs the user** (a failed solve, frames to look at), with a switch to stop at every step (the owner,
2026-11-19: yes). "Open as workflow" leads to the editor, where gates and extra steps are. W2 builds the bar; it needs
W9 for anything but a fixed list.

## Two views: guided (one image) and the workspace (several)

The owner: an optional MDI mode behind a switch, the guided view staying single-document.

- **Guided**: one image, the step bar, one step's panel. No switch.
- **Workspace**: the operation list, the edit list of the active image, and several images open. Built in this order, each usable
  without the next:
  1. **Tabs.** One image visible at a time. DIR.Lib has the tab strip (`TabBar`, `TabStripLayout`); the documents are already
     many (`DocumentCache`). Nothing new in the library.
  2. **Tiles.** Two or four images side by side with linked zoom and pan, which is what comparing a starless plate with its
     stars needs. Each pane is a viewer with its own state; the active pane is the one an operation applies to, and it is named in
     words, not only outlined. One focus owner per window still holds.
  3. **Free-floating, overlapping windows** (DSN-1's Cascade) come last and are **DIR.Lib's work, not this plan's**. DIR.Lib has
     tab strips and no dock, splitter or overlapping child window; the widget is SharpAstro/DIR.Lib#276 (the owner, 2026-11-19:
     "adding cascade to DIR.Lib can be a flight on its own"). The workspace takes it as a third arrangement once it exists and
     waits on it for nothing.

## Live stacking is the session's, as a switch on the frame preview

The owner's direction is that `tianwen-gui` gets at most a live stacking view, and that it may be "just a switch mode in the frame
preview (during session)". That is what [stacking.md](stacking.md) already plans in its Phase 15 (a live-preview toggle, "No new
Stacking tab needed"), tracked as #899, and DSN-1 v3 draws it: a Frame / Live stack switch on the Live Session tab's preview, the
strip below reporting the stack. It is not part of this app and not in this milestone. DSN-1's two questions about it (which
workflow a live stack uses, and whether the preview switches to the stack by itself) are #899's.

## The operations: what exists, and what is missing

Checked line by line against the code on 2026-11-20: the commands built in `TianWen.Cli` (`ImageSubCommand` and its partials,
`StackSubCommand`, `SolveSubCommand`), the public methods of `Image`, and each routine named below, which all exist under
that name. Every non-empty Missing cell names the open issue that carries it.

| Group | Operation | Routine | Verb | Missing |
|---|---|---|---|---|
| Geometry | Crop, auto-crop | `Image.Crop`, `WCS.CroppedTo`, `MasterCrop`, `MasterAutoCrop` (the rectangle solver, in Lib) | `image crop` (a given rectangle, #2244), `image autocrop` (through `CropOperation`, which calls `MasterAutoCrop`) | `tianwen stack` still crops by its own rule: whether it should call `MasterAutoCrop` (#2291, see Crop and auto-crop) |
| Geometry | Rotate, flip | `Image.TransformAsync` and `WarpToReferenceGridAsync` (a matrix; no named rotate or flip; `Image.Affine` is a per-value line, not geometry) | none | The routine and verb; the WCS carried (#2280) |
| Geometry | Resample, bin | `Image.BilinearResize` (WCS passed through unscaled), `Image.Downsample` | none | The verbs; the WCS scaled (#2281) |
| Geometry | Align to a reference | `FrameRegistration` | `image align` | |
| Astrometry | Plate solve | the solver | `tianwen solve` | |
| Background | Gradient | `ClassicalBackgroundExtractor`, the GraXpert backend | `image flatten` | |
| Colour | White balance by SPCC | `Tycho2ColorCalibration.ComputeWhiteBalance`, `Image.WithWhiteBalanceApplied`, the `ColourCalibration` record, `WhiteBalanceStep` | inside `stack`, `image sharpen` and `image render` (which prints the triple); `--white-balance R,G,B` hands one back | Settled by reading the code: no verb solves it and writes a FITS (#2284) |
| Colour | Linear fit, LRGB, combine, continuum | `ColourComposition` and its steps, `NarrowbandCombination.AddLineFrom` | `image linear-fit`, `lrgb`, `combine`, `continuum`, `luminance`, `add-line` | |
| Colour | Saturation, SCNR, highlight compression, background reduction | `Image.Saturate`, `SubtractiveChromaticNoise`, `CompressHighlights`, `ReduceBackground` | none of their own (`image render --saturation` reaches saturation) | The verbs (#2282) |
| Restoration | Deblur, denoise, PSF match | the enhancer roles | `image deblur`, `denoise`, `match-psf`, `sharpen` | |
| Stars | Remove, add back, protect cores, fill digs | `NarrowbandCombination.SplitStarsAsync`, `ExtendedCoreMask`, `DigFill.Apply` | `image remove-stars`, `add-stars`, `protect-cores`, `fill-digs` | |
| Tone | Stretch (MTF, GHS, asinh, star stretch) | `Image.MtfStretch`, `GeneralizedHyperbolicStretch`, `AsinhStretch`, `StarStretch` | `image render` (PNG, HDR PQ PNG or JPEG XR only) | Settled by reading the code: no verb writes a stretched FITS (#2285) |
| Combining | Add, subtract, multiply, blend, screen | `Image.Add`, `Subtract`, `Multiply`, `Lerp`, `Screen`, `Unscreen` | `image add-stars`, `image add-line` (a narrowband line into a broadband channel, `NarrowbandCombination.AddLineFrom`) | Fixed-form blends as a verb (#2283). An expression language ("pixel math") is not planned |
| Masks | Range mask, blend through a mask | `Image.LuminanceRangeMask`, `Binarize`, `BlendThroughMask` | none | A mask as an image an operation takes (W13, #2240) |
| Analysis | Statistics, sources | `Image.GetStats`, the detector | `image stats`, `image sources`, `image channel-offsets` | |

## Where it lives (W0, the owner's decision)

- **A. A mode of `tianwen-fits`** (Astro Photo Viewer): already in the Store and on macOS, already stacks a SER. But the viewer is a
  viewer; a project, a step bar and batches change what the Store app is, and its size.
- **B. A new app in this repo, the product Astro Photo Stacker (the owner's names, 2026-11-26, recorded on #1640: executable `tianwen-edit`, project `TianWen.UI.Workbench`)**: an AOT binary on the same SdlVulkan and DIR.Lib stack, sharing the
  viewer's widgets and `StandaloneViewerHost`. One more binary to package (a release asset, a `.dmg`, perhaps an MSIX), and the
  viewer gains "Open in the workbench".
- **C. A tab of `tianwen-gui`**: the GUI is the rig's app, bound to its node; processing yesterday's data should not need a node,
  and the GUI does not register the AI enhancers.

**Recommendation: B**, in this repo rather than a separate project, since every engine it hosts is `TianWen.Lib`'s and a second repo
would only add a package hop to each change. Running its jobs on the node (`NodeJobs`, for a rig's own computer to process its night)
is later and optional: the node has no stacking endpoint today, and its enhance endpoint is not on the job model yet
(`server-enhance-job-model.md`).

**The viewer stays a viewer.** `tianwen-fits` keeps its Enhance toggle and its auto-crop as they are and gains "Open in the
workbench". What the two share (the renderer, the popovers, the compare slider, the crop solver once it is in Lib) lives in
`TianWen.UI.Abstractions` and Lib, so a master looks the same in both.

**The owner's direction, 2026-11-19** (after DSPX was looked at, [pixinsight-parity.md](pixinsight-parity.md) "DSPX"): the
processing application is likely a **stand-alone app**, and **`tianwen-gui` as it is gets at most a live stacking view**, so C
is out as the home of the workbench. That leans to B; the final pick and the name stay open in #1640. The same day the scope
widened to the full processing application DSPX points at, and the plan was re-cut
([The re-cut of 2026-11-19](#the-re-cut-of-2026-11-19)).

**Decided 2026-11-22: a product of its own, named Astro Photo Stacker.** The owner reserved that name in Partner Center that
day, beside Astro Photo Viewer (`tianwen-fits`). A Store product of its own is B. A search the same day found no application
of that exact name, in the Microsoft Store or elsewhere; the nearest are "Astro Stacker: Star Images" on Apple's App Store and
two GitHub projects named AstroStacker and astro-stacker, and trademark registers were not searched. Still open in #1640: the
executable's name (`tianwen-edit` is the working one) and the drawing.

The owner's words the same day, on what the other two apps keep: "yes stand-alone. the existing gui currently only have live
stacking planned, and the viewer will keep the planet stacking for now", and then "but we should also be able to handle .ser
in the stacker app". So `tianwen-gui` gets its live stacking view and no more of this plan. Astro Photo Stacker handles a SER
through its planetary steps (W3), as this plan had it, and `tianwen-fits` keeps stacking a SER as it does today: for a time
both do, on the same engine (`PlanetaryBestStack`, `PlanetaryAuto`, `PlanetarySharpening`). Whether the viewer's is retired
later is not decided.

## Phasing

| Phase | Scope | Depends on | Issue |
|---|---|---|---|
| W0 | The owner picks where it lives (A, B or C) and its name. **Decided 2026-11-22: B, Astro Photo Stacker** | | #1640 |
| W1 | The operation contract, the edit list, the step store and the project, headless | W0 | #1641 |
| W2 | The app's shell: the guided view's step bar, the views, jobs | W1, and W9 for a workflow with gates | #1642 |
| W3 | The planetary steps | W2 | #1643 |
| W4 | Deep sky: import, inspect, calibrate | W2 | #1644 |
| W5 | Deep sky: register and integrate | W4 | #1645 |
| W6 | Processing and colour, both kinds | W3, W5 | #1646 |
| W7 | Saved workflows and batches | W6, W9 | #1647 |
| W8 | Packaging and release | W7 | #1648 |
| W9 | Workflows and gates, headless | W1 | #2236 |
| W10 | The missing operation verbs, Lib and CLI first | none: it can start today | #2237 |
| W11 | Memory, GPU and disk for many open images | W1 | #2238 |
| W12 | The workspace view: tabs, then tiles | W2, W11 | #2239 |
| W13 | Masks | W12 | #2240 |

W10 waits on nothing, the owner's W0 included: its verbs are useful from the command line whether or not the app is ever built.

### W0 Where it lives

Tracked as #1640. Decided by the owner from the three choices above, with the name. **Decided 2026-11-22**: B, a product of
its own, named Astro Photo Stacker ([Where it lives](#where-it-lives-w0-the-owners-decision)). The owner's names (2026-11-26): executable `tianwen-edit`, project
`TianWen.UI.Workbench`.

### W1 The step runner and the project, headless

Tracked as #1641. A step contract in Lib (inputs, settings, the routine, its output, the digest), the project file, and invalidation by digest; every step
listed above wrapped around its existing routine, with no logic of its own. Since the re-cut the contract is
[An operation](#an-operation), the project holds [edit lists](#the-edit-list-non-destructive-by-construction), and step outputs go to a
store addressed by the step's key, so a step run before is found and not run again. Derived steps (crop) store nothing. `tianwen workbench run|status` drives it, so the whole
contract is tested in CI with no window: a project run twice re-runs nothing, a changed setting re-runs only the steps after it, and
each step's output is bit-identical to its CLI verb's.

**The first leg is built** (2026-11-23). How the code does it is
[workbench-operations.md](../architecture/workbench-operations.md); in short:

- **The contract** is `Operation<TSettings>` in `TianWen.Lib.Imaging.Workbench`. A verb builds the settings from its
  options and calls the operation, and the runner reaches the same method, so the two are one routine.
- **A step's key** is a digest of its inputs' keys, the operation's id and version, its settings in canonical form and
  what serves it. An original file's key is the digest of its bytes. Nothing is decided by a date.
- **The step store** is `store/` in the project's folder: one file a stored step, named by its key, and an index of what
  the store wrote. A result appears whole or not at all.
- **The project** is `project.json`: the images, each an original file and its edits, and the other images an edit takes.
- **The runner** pulls from the end: a step whose result is in the store needs nothing before it.
- **Three operations**: crop (derived; a rectangle, or the auto-crop rule), gradient (stored; `image flatten`'s routine)
  and linear fit (stored; it takes its reference from another image).
- **`tianwen workbench status|run|operations`**, and `run --export` to write a derived result as the file its verb writes.
- **The tests** are the three the phase names. The third runs every step's printed command line through the real
  `tianwen image` command and compares the two files to the byte, which holds rule 3 as well as rule 1.

**The legs after the first**, each with its own issue, all built by 2026-11-23, so W1 is done and #1641 closes with
them:

1. The other colour steps (#2465). Built: `align`, `continuum`, `add-line`, `luminance`, `lrgb`, `add-stars`,
   `fill-digs`, `protect-cores`, the four that put several masters through one run, `match-psf`, `deblur`, `denoise`
   and `remove-stars` (two results: the starless plate and the stars), `combine`, the recipe as one step, and
   `denoise-colour` (`image denoise --colour`: three planes in fixed places as one colour image, and three results).
   None is left.
2. The canonical enhance (#2466). Built as `EnhanceOperation`: its settings say which steps of `LinearEnhanceProgram`
   are kept and at what strength, the program turns them into the `SharpenStep` records in the one place that already
   did, and `image sharpen` takes its program from the operation and adds only its own stretches. Its steps as edits of
   their own (the toggles of the Process step, each with before and after) are W6's.
3. The deep-sky frame steps (#2467): scan, grade, calibrate, register and integrate, which take a set of frames, not an
   image. Decided by the owner on 2026-11-23: a set's key comes from a digest index beside the store (a frame whose
   size and time have not moved is not read again; `--verify-frames` reads them all), there is one `stack` step
   around `tianwen stack` since that is the one verb, each master is a result of it under its group's name, its
   settings are the 24 options that choose which frames are stacked and how they are registered and integrated, and
   a comet run is not W1: it is its own issue (#2489). Built: a folder as an image's original, its key and the index
   (`FrameDigestIndex`); results named by the run (`IRunNamedOperation`), each in the store under its name with
   the files that belong beside it, the step keeping the list of them; and the `stack` operation
   (`StackOperation`), whose settings the verb builds its own options from. A step judges cloud on the group's own
   baselines and never against the clear nights this computer keeps (`--no-clear-night-references`). None is left.
4. The planetary steps (#2468): grade, stack, de-rotate, sharpen, colour. Built: the AUTO stack (`planetary-stack`,
   the routine of `planetary stack --auto`): a capture is an image's original, its linear master the step's result and
   its sharpened master a result beside it; and the de-rotation and join of several mono stacks
   (`planetary-compose`, the routine of `planetary compose`): any number of stacks as a set are one step, the colour
   master its result and the luminance a result it gives where a stack through an IR-pass or an L filter was among
   them; and the sharpening of a master again (`planetary-sharpen`, the routine of `planetary sharpen --auto`), which
   is how a composed or a cropped master is sharpened. The owner's call of 2026-11-23 closes the leg with these
   three: AUTO takes three choices about the finished masters (`PlanetaryAutoChoices`: the strength past the derived
   gains, luminance-only sharpening, the colour balance's saturation or none), on the stack step and, for the first
   two, on the sharpen step. Not steps: grading (`planetary grade`) writes nothing a later step reads, so it is one
   of [the facts a gate may test](#the-facts-a-gate-may-test), W9's; `planetary colour` measures masters against
   OPAL's maps and is not what balances one; and a look (`planetary look`) is a rendering and never a new master, so
   it belongs to W3's colour step and the export.
5. An operation that writes no pixels (#2469). Built: the kind (the plan's "metadata": the finding is kept in the
   store, and the step after it takes its input with the finding applied) and the plate solve as the first, `tianwen
   solve` calling it and gaining `-o`. Statistics is not a step of an edit list: a measurement changes nothing a later
   step reads, so as an edit it would only re-key every step after it. It is one of [the facts a gate may
   test](#the-facts-a-gate-may-test), which W9 owns.

**The comet run after W1** (#2489), built in two legs on 2026-11-25. The first: `comet-stack`, the frames registered
on the comet with the star layer and the composite beside each group's master, at parity with `tianwen stack
--comet-track`. **Decided by the owner on 2026-11-25: the comet's track is a file**, never a question a step asks
Horizons while it runs, since a step's key is fixed before it runs and Horizons' answer is not. `tianwen comet track`
asks once and writes it; the project holds it as an original, read by its bytes, so the key holds the ephemeris and an
offline run keeps its star layer. The second: the stars removed from every frame (`removeStars`, the comet layer), and
**a run that follows another, decided by the owner the same day**: one image after the track, the earlier stack's
master, whose manifest drives the run and whose white balance it takes where asked (`--manifest`, `--inherit-wb`;
only a master that states one gives it: an original the verb wrote with a picture or with an inherited balance, or
the master of a run that itself inherited one, since a workbench step's master has none of its own). How the code
does it: [workbench-operations.md](../architecture/workbench-operations.md), "A comet's night stacked on the comet".

### W2 The app's shell

Tracked as #1642. **Built** (2026-11-26): how it works is [workbench-app.md](../architecture/workbench-app.md); what it leaves out
is settings editing (W6, W12), gates (W9), the other screens (W4 to W6) and packaging (W8). The design as planned: the step bar left to right, Original / Enhanced / Comparison views (`SplitCompareController`), a job panel with progress and cancel fed
by the engines' `IProgress`, each step's command line. Built so `StandaloneViewerHost`'s headless test path drives it in CI at DPI 1
and 1.5.

#1642's follow-up #2547, "New project from a capture folder, stack it, then process each master", **built** (2026-11-27): a
New project button makes a project from a capture folder (the `new` verb's routine, `WorkbenchProjects.NewAsync`), "Run to
here" stacks it, and the stack step's panel then lists its masters from `StepReport.Results`, each with a "Process this
master" button that adds the enhance's chain of single steps through the Lib routine, off the render thread. Left out, as
the issue says: choosing which master settings the chain takes (it is the enhance's defaults; the settings are then edited
as for any step, W6 and W12), processing every master in one press, and a stack's own image view (a stack with results named
by its run has no single file to show). How it works: [workbench-app.md](../architecture/workbench-app.md), "New project"
and "The stack's masters and Process this master".

**Redesign, #2553, headless half built (2026-11-27):** the owner's mockup is kept in `docs/designs/workbench/` (Import,
Workflow, Main). New project now takes the capture folder alone and keeps the project and every result in `<folder>\workdir`
(the stack never reads it); `CaptureDetection` says what the folder holds (groups, each file's kind and why, an unsure mark,
Needs a look) over the stack's own scan and matching; a per-file reassignment is a stack-step setting (`reassign`, `tianwen
stack --reassign`) that changes the step's key and is taken by the stack. Verbs: `workbench new <folder>`, `workbench
detected`, `workbench reassign`. The screens (import, workflow choice, the tree) are the next legs. See
[workbench-app.md](../architecture/workbench-app.md), "What is in the folder".

### W3 The planetary steps

Tracked as #1643. Import to Export for planetary, at parity with the viewer's Full view (the same master to the bit for the same capture and options),
then each step's own view: the quality curve and the zeroed frames, the alignment points, the derived gains, the colour.

### W4 Deep sky: import, inspect and calibrate

Tracked as #1644. The scan's groups, the subs' metrics table with the gate's limits, the blink, reject and restore, the masters and what each light takes.

### W5 Deep sky: register and integrate

Tracked as #1645. The manifest's transforms and residuals, the frames that failed, the integration and its maps; a rejected frame restored re-integrates
from the manifest, not from scratch.

### W6 Processing and colour

Tracked as #1646. The canonical enhance program as ordered toggles with before and after, mono colour composition, the planetary colour step's balance
and looks. Star removal's core protection (#1658) is drawn on the image there, its cores editable by a click (Deep sky, step 6).

### W7 Profiles and batches

Tracked as #1647. A profile saved from a project, replayed over a folder headless and in the window's queue. Since the re-cut a
profile is a [workflow](#workflows-operations-in-order-with-gates) (W9 runs one headless); what is left here is saving one from the
window, the queue, and a night of captures run unattended.

### W8 Packaging and release

Tracked as #1648. The binary in the release assets, a `.dmg`, and the Store question if W0 chose B.

### W9 Workflows and gates, headless

Tracked as #2236. The workflow file (steps, gates, for each), the facts in Lib each with its test
([The facts](#the-facts-a-gate-may-test)), the dry run, and `tianwen workbench run <workflow> <inputs>`. The built-in deep-sky
workflow, with its parity test against `tianwen stack --enhance`. A built-in gate calls the pipeline's own decider
([A gate shows a decision](#a-gate-shows-a-decision-it-does-not-replace-one)). No window.

**The enhance as the first built-in workflow: one routine per step (2026-11-26).** Measured on the 10P master on
2026-11-24, a chain of the single steps (deblur, gradient, remove-stars, denoise, add-stars) was not the one-step enhance:
the single deblur kept a lifted star's light and wrote a peak near 3.0 that every reader took for ADU (#2504), the two star
removals and the two denoises were different routines, and SCNR on the stars had no step. The owner chose, 2026-11-26:
- **the one deblur keeps the lift, and the file says its scale** (`UNITREF`, #2504, done in #2528);
- **the one star removal is the guarded split**, cores protected and the plate held at or under its input
  (`NarrowbandCombination.SplitStarsAsync`);
- **the one denoise is the enhance's gated passes** (`NarrowbandCombination.DenoisePassesAsync`), which `image denoise` now
  runs too;
- **SCNR on the stars is its own operation** (`scnr`, `image scnr`, the SCNR part of #2282).

So the enhance program's deblur, star removal and starless denoise call the single steps' routines (`enhance` version 2;
`denoise`, `denoise-colour`, `remove-stars` version 2; `combine` version 3), each model taking a lifted frame on its own
range and handing it back on the frame's scale. A plate that fits `[0, 1]` goes to a star remover or a denoiser as it is,
as the enhance has always handed it (NoiseXTerminator reads its automatic strength off the noise it is handed); only one
that does not fit is divided by its peak. Two more steps of the enhance had no single step that did the same, and now have one, by the same
rule: the **white balance** the enhance puts in first (`white-balance`, `image white-balance`, on the enhance's own
`EnhanceOperation.WhiteBalanceToApplyAsync`; without it the chain's stars were never balanced, and SCNR refuses unbalanced
colour), and the **sky levelled** after the gradient (`gradient` version 2, `Image.WithBackgroundNeutralised` as the enhance
calls it, with the canvas ring put back after the level). The recombine is `add-stars`' sum (`NarrowbandCombination.WithStars`), and the whole-frame program's denoise is the
same gated passes. The 2026-11-24 measure's green share of star light (0.58 in the chain, 0.64 in the enhance) is the balance
the chain did not put in.

**The enhance laid out as its chain** (`EnhanceChain`, `tianwen workbench chain <project> <image>`): an image whose one edit is
an enhance gets the chain beside it, read off the enhance's own program for that image (no second list of steps):
`<name>-starless` (white-balance, deblur, gradient, remove-stars, denoise), `<name>-stars` (SCNR on the stars it took out,
where the enhance runs it) and `<name>` (add-stars). Each step is then kept in the store on its own, so changing the
denoise runs the denoise and the add-stars, not the whole enhance. A program or a setting no single step can follow (the
stellar sharpen, the starless deconvolution, the screen split, a blend under one, a stated backend, the deblurrer's own
sharpening) is refused in words. `EnhanceChainTests` runs both through one project on stand-in enhancers and holds the
chain's result to the enhance's, pixel for pixel, on a mono master and on a ringed colour master that states an SPCC
balance (the white balance, the sky level and SCNR in the chain). The same comparison on the 10P master with RC-Astro's models runs on DE1
(#2534). What is left for W9 proper is the general workflow file with gates.

### W10 The missing operation verbs

Tracked as #2237. The audit of [the operation table](#the-operations-what-exists-and-what-is-missing) against the code is done
(2026-11-20); it files one small PR each as #2280 (rotate and flip), #2281 (resample and bin), #2282 (saturation, SCNR,
highlight compression, background reduction), #2283 (a fixed-form blend), #2284 (SPCC white balance written to a FITS) and
#2285 (a stretch that writes a FITS). `Image.Invert`, `GaussianBlur`, `ContrastBoost` and `Divide` also have no verb; none is planned
until the owner asks.

Done by this flight (#2237): the crop-rectangle solver moved out of `TianWen.UI.Abstractions` into Lib as `MasterAutoCrop`,
with the viewer and `image autocrop` calling it; `image crop` to four given numbers (#2244) already exists. `tianwen stack`
does not call the routine yet: whether it should is #2291 (see Crop and auto-crop).

Still to fly, each as a Lib routine with a verb and tests, one small PR each, none needing the app: rotate and flip (#2280),
resample and bin (#2281), and the colour and tone operations that are `Image` methods with no verb (#2282 to #2285). SCNR
went first, as the enhance retrofit's step (`image scnr`, 2026-11-26); saturation, highlight compression and background
reduction are what is left of #2282.

### W11 Memory, GPU and disk for many open images

Tracked as #2238. [Where the pixels live](#where-the-pixels-live-memory-the-gpu-the-disk), items 1 to 6: a stored step's document takes its
file as its readout source and becomes releasable; the budget as a setting; the step store's size limit, its reserve
(`ScratchSpace.Refusal`) and its eviction, which deletes only what its index says it wrote; and the GPU measurement that decides
whether a hidden image keeps its texture. Tested as `ViewerMemoryReleaseTests` are: by counting reads and bytes, never by
working set.

### W12 The workspace view

Tracked as #2239. [Two views](#two-views-guided-one-image-and-the-workspace-several): the operation list, the active image's edit list with
on/off and reopen, an operation's panel with its region preview, several images as tabs, then as tiles with linked zoom and pan.
Captures at narrow and wide sizes in the PR, as `.github/docs/ui-changes.md` asks.

### W13 Masks

Tracked as #2240. A mask is an image. Any operation may take one, and its result is the input and the processed image blended
through it (`Image.BlendThroughMask`), so every operation gains masking from one wrapper and none grows a mask option of its own.
Making masks: a range mask (`Image.LuminanceRangeMask`), a star mask from the stars layer a remover already makes, and the cores
star removal protects (#1658). Last, because nothing before it depends on it.

## Decisions for the owner

Each with a recommendation. The first is W0's; the rest came out of DSN-1's questions and this plan.

1. **The name**, and B confirmed as where it lives (#1640). **Decided 2026-11-22: Astro Photo Stacker, a product of its
   own (B).** The executable's name is still open.
2. **Does the guided view stop at every step, or run until a step needs you** (a failed solve, frames to review)?
   **Decided 2026-11-19: run until needed, with a switch, and any step can be started directly**, which runs or re-runs what is
   before it ([The guided view](#the-guided-view-is-a-workflow-drawn-as-steps)).
3. **Does a workflow with gates open in the guided view?** Recommended: yes, the bar showing the branch taken.
4. **May a gate's branch hold several steps, or another gate?** Recommended: yes; a workflow is a tree.
5. **Does the Stack screen simply run a workflow?** **Decided 2026-11-19: yes.** One path: its panel becomes the workflow's
   steps.
6. **The memory budget, the step store's folder, its size limit and the disk reserve**: the defaults. **Direction
   2026-11-19: measured once at start, to avoid swapping** ([Where the pixels live](#where-the-pixels-live-memory-the-gpu-the-disk),
   item 2); the fractions are W11's to measure. The store's folder is still open. `ScratchSpace`'s 109 GB is the planetary verbs'
   reserve on one drive and is not a default for an app.
7. **Reordering edits by dragging**: recommended not in the first version.
8. **The windowed mode**: tabs and tiles, overlapping windows left out (windows that float over one another and are dragged about,
   PixInsight's way, which DSN-1 offers as "Cascade"); the switch in the workspace only, the guided view always one image (the
   owner's word). **Decided 2026-11-19:** tabs and tiles here; the overlapping kind is a piece of work of its own in DIR.Lib
   (SharpAstro/DIR.Lib#276), taken up by the workspace when it exists.
9. **Pixel math as typed expressions** (DSN-1 draws "starless + 0.8 * stars"): recommended fixed-form blends only.
10. **XISF reading**, the first door a PixInsight user meets ([pixinsight-parity.md](pixinsight-parity.md) "DSPX"): not in this
    plan; it needs its own.

## Not in it

Mosaics, live stacking during a session (the session's own path, `stacking.md` Phase 15, #899;
[above](#live-stacking-is-the-sessions-as-a-switch-on-the-frame-preview)), comet-specific integration beyond what
`tianwen stack --comet` already does, and lunar or solar surfaces (Strata's other half) until the planetary steps stand.

Since the re-cut, also: a scripting language and typed pixel-math expressions (DSPX ships Python; a workflow here is a JSON tree
of operations), photo-editor tools (layers, clone stamp, text, PSD), XISF, and running a workflow on a node. Overlapping child windows are
DIR.Lib's (SharpAstro/DIR.Lib#276).
