# Operations, edit lists and the step store

The headless core of the processing application (Astro Photo Stacker): what an operation is, how an image's edits are
kept, where their results go and what decides whether a step runs. The design and the why are in
[stacking-workbench.md](../plans/stacking-workbench.md) ("An operation", "The edit list"); this page is how the code
does it. Phase W1, #1641. The code is `src/TianWen.Lib/Imaging/Workbench/`, and the verb is `tianwen workbench`.

## The four things

| Thing | Type | What it is |
|---|---|---|
| Operation | `IOperation`, `Operation<TSettings>` | One unit of work: typed settings, one routine, one command-line verb |
| Edit list | `ProjectImage.Edits` | An original file and the ordered edits made to it. The original is never written |
| Step store | `StepStore` | A folder of results, one file a stored step, named by the step's key |
| Project | `WorkbenchProject`, `project.json` | A folder: the images with their edit lists, beside the store |

`WorkbenchRunner` reads a project, works out every step's key, takes from the store each step whose key is there and
runs the rest.

## An operation

An operation is described once (`OperationDescription`: id, version, title, verb, how its output is kept, the images it
takes besides its own, its settings) and everything else reads that.

| Member | What it is for |
|---|---|
| `Description` | What it is. The id and the version are part of every step's key |
| `DefaultSettings`, `TryReadSettings` | Settings as JSON, read into their canonical form: every setting written, in the type's own order, on one line. A setting the operation does not have is refused |
| `Refusal(settings)` | Says in words why these settings cannot be run, before anything is read |
| `ServedBy(settings)` | What makes the pixels beyond the operation's own code: the backend and model of a step a model serves. Part of the key |
| `CommandLine(settings, inputs, output)` | The command that does the step from a terminal, program name first |
| `RunAsync(settings, inputs, outputPath, say, ct)` | The work: reads its inputs, writes one FITS file. The verb's routine |
| `Derive(settings, inputs)` | `IDerivedOperation` only: the result as an input for the next step, with no file |
| `FindAsync`, `Apply`, `Write` | `IFindingOperation` only: find it out and write the finding; the input with the finding applied, for the next step; the result as the file the verb writes, from a finding made before |

The rules that bite:

- **A verb's work is its operation's routine.** The verb builds the settings from its options and calls the
  operation; the runner reaches the same method through `IOperation`. `image linear-fit` calls
  `LinearFitOperation.RunAsync`; `image flatten` calls `GradientOperation.ExtractAsync` and writes with
  `OperationOutput.Write`; `image crop` and `image autocrop` build `CropSettings` and call `CropOperation.TryResolve`
  (the four numbers, or `MasterAutoCrop.Scan`, handing the scan back for the verb's declined and margin lines) and
  `CropOperation.Write` (`MasterCrop`); `--dry-run` stops between the two. An operation adds nothing between its routines. `WorkbenchVerbParityTests` runs every step's
  printed command line through the real `tianwen image` command and compares the files to the byte.
  **`image sharpen` shares less than the others.** It takes its start and its program from `EnhanceOperation`
  (`Start`, `ProgramAsync`) and then runs the pipeline and writes its files itself, because it also stretches the
  plates and can write each by itself; `EnhanceOperation.RunAsync` runs the pipeline and writes for a project. So the
  two share the program and not the run, and `WorkbenchEnhanceParityTests` is the one thing that holds their files
  together.
- **A setting is a settable property with its default.** The JSON source generator gives an absent init-only property
  its type's default, not the declared one, so `{ get; set; } = default` is the only form. The type goes into
  `OperationSettingsJsonContext`, which refuses a member the type does not have: a misspelt setting must not run at its
  default and say nothing.
- **Other pixels for the same settings is a new `Version`.** A changed algorithm or a changed default raises it, so a
  result of the old code is never taken for the new code's. Adding a setting changes the canonical settings, and with
  them every key of that operation; that costs a re-run and is never wrong. The enhance's default program is pinned
  step for step by a test (`WorkbenchEnhanceParityTests`), so a change to `LinearEnhanceProgram.For` fails there until
  the version is raised with it.
- **What serves the step is in its key.** `GradientOperation.ServedBy` is `classical` or the registered corrector's
  name, so on a computer where `auto` means GraXpert a result is not taken for the classical fit's. The enhance's is the
  pipeline's word for every role (`SharpenPipeline.DescribeRoles`), asked with the options the step's settings make:
  a role whose backend follows from them is named by the one they choose (`IEnhancerAvailability.NameUnder`), so a
  step that says `backend: tianWen` is keyed by the in-house model where RC-Astro is licensed too, and one that
  says `gradientBackend: classical` by the classical fit where GraXpert is installed too. A role the settings
  leave out is not named: a step with `gradient` off runs no corrector, so installing or removing GraXpert moves no
  key of it, and the same for a denoise, a deconvolution and the stellar sharpening left out. An in-house
  enhancer's name carries its model file, so a retrained model is another key. Naming an RC-Astro role asks whether
  its product is licensed, which is a
  subprocess, so **the runner asks what serves once for an operation and its settings in a plan**
  (`WorkbenchServedTests`), on a status as on a run.
- **A refusal is an answer, not a throw.** `OperationOutcome` says which kind (`Input`, `Failed`, `NotServed`: the
  verbs' exit codes) and why. A cancellation is thrown and never caught as a failure.
- **A stored step reads a file and writes a file.** `OperationInput` is a file, or the result of a derived step. Only a
  derived step hands its result over in memory, and only to the step after it.

## Stored and derived

- **Stored** (`gradient`, `linear-fit`, `enhance`): the result is written into the store by the run that made it, and
  found there by its key ever after.
- **Derived** (`crop`): the edit holds the rectangle or the rule (`auto`); the pixels are cut out of the input when the
  next step asks, and no file is written. A derived step has a key like any other, so a changed rectangle changes the
  key of every step after it. Its `RunAsync` is still its verb's routine: `tianwen workbench run --image <name>
  --export <file>` writes a derived result as the file `image crop` would write.

**What a derived crop carries.** Handed on in memory it is the pixels and the moved plate solution, which is all the
stored operations here read. Written as a file from a FILE it also carries the alignment and stack cards and the map
sidecars (`MasterCrop.CropLoaded`). An operation that reads a map sidecar after a derived crop finds none; none exists
yet, and the one that will (SPCC's saturation map, #2284) has to settle this first.

## An operation that writes no pixels

A plate solve finds something out about its input and changes no pixel (`OperationOutputKind.Finding`, the plan's
"metadata"; `IFindingOperation`, `FindingOperation<TSettings>`).

- **What the store keeps is the finding**, a small file, under a key of its own (`StepKey.OfFinding(step)`). The step's
  own key names the image the step gives, which has no file in the store; a step's command line names that place, so a
  file written there by hand is never taken for the finding.
- **The step after it takes its input with the finding applied** (`Apply`): the pixels of what is before the step, and
  the finding. So such a step needs what is before it whenever it is needed itself, even when its finding is kept.
- **It is not run again while its finding is in the store** (`Kept`), and a refusal keeps nothing.
- **Written as a file it is its verb's file.** `workbench run --export` writes it from the finding the run made or kept
  (`Write`), and finds nothing out a second time.

**The plate solve** (`solve`, `tianwen solve`) is the first. Its finding is the plate solution as a FITS header with no
data (`PlateSolutionFile`): the cards `WCS.WriteToHeader` writes, read back through `WCS.FromHeader`. So the step after
a solve works on the numbers a verb reads off the solved image, to the last digit a card holds.

- `tianwen solve` builds `SolveSettings` and calls `SolveOperation.SolveAsync`, and writes the solved image with
  `SolveOperation.WriteSolved`. It gains `-o`: the image with the solution in a file of its own, the input left as it
  is, which is how a project's solve step is done from a terminal (`--update-fits` still writes the input in place).
- **Which solver answered is not in the step's key.** The solvers are tried in order of priority until one answers,
  and every answer is a solution of the same sky; installing another solver must not run again every step after a
  solve. A host with no solver has the operation, and its step says so when it runs.
- A solve that finds nothing is a refusal of the step, kept nowhere and tried again by the next run.

## The operations

`tianwen workbench operations` prints this list from the code, with every setting at its default. An edit names an
operation by its id.

| Id | Kept | Verb | The images it takes after its own |
|---|---|---|---|
| `crop` | derived | `image crop`, `image autocrop` | |
| `gradient` | stored | `image flatten` | |
| `linear-fit` | stored | `image linear-fit` | `reference`: the master whose scale it is put on |
| `align` | stored | `image align` | `reference`: the master whose grid it is put on |
| `continuum` | stored | `image continuum` | `continuum`: the broadband master the continuum is read from |
| `add-line` | stored | `image add-line` | `line`: the narrowband master whose emission goes in |
| `luminance` | stored | `image luminance` | `red`, `blue`, and optionally a measure copy of red, green and blue. Its own image is green |
| `lrgb` | stored | `image lrgb` | `luminance`: the luminance whose detail it is given |
| `add-stars` | stored | `image add-stars` | `stars`: the stars a star removal took out |
| `fill-digs` | stored | `image fill-digs` | `input`: the master the stars were removed from. Its own image is the starless plate |
| `protect-cores` | stored | `image protect-cores` | `input`: the master the stars were removed from. Its own image is the starless plate |
| `match-psf` | stored, one run for several | `image match-psf` | the other masters of the run |
| `deblur` | stored, one run for several | `image deblur` | the other masters of the run |
| `denoise` | stored, one run for several | `image denoise` | the other masters of the run |
| `remove-stars` | stored, one run for several; the stars as a second result | `image remove-stars` | the other masters of the run |
| `combine` | stored | `image combine` | `green`, `blue`, and optionally `ha`. Its own image is red |
| `enhance` | stored | `image sharpen` | |
| `solve` | no pixels: the plate solution is stored | `solve` | |

- **An optional image comes last** (`OperationInputRole.Optional`): an edit that names fewer images leaves the last ones
  out. `luminance` takes two, or five with its three measure copies; any other number is refused.
- **Several masters on one grid are read in one place** (`OperationInputs.TryReadMasters`): each as a master, its
  absent ring masked, and a refusal in words when they are not on one grid.
- **Two verbs changed so a step's line can name its file.** `image lrgb` takes `-o`, and `image align -o` takes a FITS
  file where one master is aligned (it still takes a folder for several).
- **The star catalogue of a protected core is `CoreCatalogue`** in Lib: which catalogue is asked, read once, and what is
  said about it. The verbs and the operations call it; what would answer is in the step's key.

## A run several images share

Deblur, denoise, star removal and the blur match put several masters through ONE run, on one scale, so a scale measured
between two of them still holds between their results (`IJointOperation`, `JointOperation<TSettings>`; the description
says `Joint`). Each master gets its own result.

- **Each image's edit names the others.** `inputs` is then the other images of the run, any number of them; none is a
  run of one. An image handed in twice is refused.
- **The run happens once.** The runner sorts the images by key before it hands them over, so every edit list of one run
  asks for the same run whatever order it names the others in. The first list that needs it runs it and every image's
  result goes into the store, each under its own key; the other lists find theirs there (`Kept`).
- **A run that is refused is refused once.** What a step, or the run several images share, came to is kept for the
  length of the call under its key: the other edit lists that ask for it are told the same and nothing runs again, so
  a remover that fails after minutes of work is not paid for once for each master. The next call tries again.
- **So a result must not depend on the order of the images.** That is the operation's promise. `image denoise
  --colour`, where the order of three planes is their colour, is for that reason not the denoise operation.
- **A result can have a second result beside it** (`OperationDescription.ExtraResults`): star removal gives the starless
  plate and, as `stars`, the stars it took out. Each extra result is kept in the store under a key of its own, and a
  step is kept only when all of its results are there.
- **Its verb takes `-o` once for each master**, in the masters' order, and `image remove-stars` takes `--stars-output`
  the same way, so a step's command line names every file it writes. Two results named into one file are refused:
  the second would be written over the first.
- **`Joint` is said once in effect.** The description says it for a listing and a window, the type
  (`IJointOperation`) for the runner, and `OperationCatalog` refuses an operation whose two disagree, a shared run
  that names images by role, and extra results on an operation that cannot be told where they go.

## An image that starts from another

A project's image is a file, or it starts from another image of the project (`from`): that image as its own edits
leave it. With `result` it starts from an extra result of that image instead, the one the last of its edits gives
under that name. Where that last edit cannot be made (it is refused or blocked, or no operation has its name), the
image has no such result and says so: an earlier edit's is never taken for it.

```json
{ "name": "red", "path": "masters/master_red.fits", "edits": [ { "operation": "crop", "settings": { "auto": true } } ] },
{ "name": "green", "path": "masters/master_green.fits", "edits": [ { "operation": "crop", "settings": { "auto": true } } ] },
{ "name": "red-starless", "from": "red", "edits": [ { "operation": "remove-stars", "inputs": [ "green" ] } ] },
{ "name": "green-starless", "from": "green", "edits": [ { "operation": "remove-stars", "inputs": [ "red" ] } ] },
{ "name": "red-stars", "from": "red-starless", "result": "stars" }
```

This is what lets a run of several images be written down without a circle. `red-starless` and `green-starless` each
name the other's image BEFORE the run (`red`, `green`), and go on from their own. Had `red`'s own list held the star
removal and named `green`, and `green`'s named `red`, each would take the other as its edits leave it, which is after
the run: a circle, and it is refused.

## The enhance as one step

`EnhanceOperation` (`enhance`, #2466) runs the canonical program as ONE stored step. Its settings say which steps of
`LinearEnhanceProgram` are kept and at what strength, and that record turns them into the `SharpenStep` records, which
stay the pipeline's in-memory program ([stacking-workbench.md](../plans/stacking-workbench.md), "An operation"). No
setting can switch on a step no enhancer serves. A value `image sharpen` reads as "not stated" (a pass count below 1, a
strength below 0) is refused as a setting, so no step keeps what its own command line would drop.

## A step's key

`StepKey.OfStep` digests this text with XxHash128 (`ContentDigest`, as the stack manifest does):

```
tianwen-step/1
operation=<id>@<version>
served=<what serves it, or empty>
settings=<canonical settings JSON>
input=<key of the edit list's image as the edits before leave it>
input=<key of each extra input, in order>
```

For a run several images share, the `input=` lines are the images' keys in sorted order, and that digest is the run's
key. One image's result of it is `StepKey.OfMember(run, image)`, and an extra result of a step is
`StepKey.OfResult(step, name)`. The finding of a step that writes no pixels is kept under `StepKey.OfFinding(step)`.

An original file's key is the digest of its bytes (`StepKey.OfFile`), computed on every status and run. Keys are made
from keys, so a stored result is never hashed. Nothing is decided by a file's date: the same bytes under a new date
run nothing, and other bytes under the old date run everything after them (`WorkbenchRunnerTests`).

The map sidecars beside a master are not part of its key. The stacker writes them with the master, so one does not
change without the other.

## The step store

`<project>/store/`, one `<digest>.fits` a stored step, and `index.json` naming every result with its key, its
operation and its size. The finding of a step that writes no pixels is a file there too, named by the finding's key.

- **A result appears whole or not at all.** A step writes into `store/.staging/<guid>/` and the file is renamed into
  place when the step has succeeded; the index gains its entry after that, under the folder's lock
  (`SharedFile.UpdateJsonAsync`), so two programs on one store are safe.
- **The store holds what its index names.** A file in the folder that the index does not name is never read as a
  result and never removed. A run that ended between the rename and the index leaves such a file, and the next run
  writes over it.
- **It is a cache.** Losing a file costs a run. An index that cannot be read names nothing, so every step runs again.
- Limits, the disk reserve and eviction are W11 (#2238) and work from the index.

## The project file

`project.json`, written by hand today and by the application later. Paths are absolute or relative to the project's
folder. A member the type does not have is refused.

```json
{
  "schemaVersion": 1,
  "images": [
    {
      "name": "green",
      "path": "masters/master_green.fits",
      "edits": [
        { "operation": "crop", "settings": { "auto": true } },
        { "operation": "gradient", "settings": { "backend": "classical", "degree": 2 } }
      ]
    },
    {
      "name": "red",
      "path": "masters/master_red.fits",
      "edits": [
        { "operation": "crop", "settings": { "auto": true } },
        { "operation": "gradient", "settings": { "backend": "classical" }, "enabled": false },
        { "operation": "linear-fit", "inputs": [ "green" ] }
      ]
    }
  ]
}
```

- **`inputs`** names other images of the project, one for each of the operation's extra inputs: each is that image as
  its own edits leave it. Images that take one another in a circle are refused.
- **`enabled: false`** passes an edit over: the edit after it takes what the edit before it made. Switched on again,
  its earlier result is in the store and nothing runs.
- **`digest`, `key` and `output`** are written by a run, so a status can say what changed since. They are never read
  to decide whether to run.

## The runner

`WorkbenchRunner.StatusAsync` and `RunAsync`, the same plan with and without the work:

1. Every image's steps and keys are worked out, each image once. An edit that cannot be planned (an unknown
   operation, settings it refuses, a missing input) is `Refused`, and what is after it is `Blocked`.
2. What is asked for (every image, or `--image`) is marked needed from the end backwards. **A step whose result is in
   the store needs nothing before it**, so an earlier result that is gone is not made again for its sake
   (`NotNeeded`).
3. A run makes each image as far as it could be planned: a stored step that is needed and not in the store runs
   (`Ran`), one that is there is `Kept`, a derived step is `Derived`. A step that writes no pixels is `Ran` or `Kept`
   by its finding.
4. A run records each step's key and result in the project file, and writes it only when something changed.

`tianwen workbench status <project>` prints the plan with every step's command line and writes nothing.
`tianwen workbench run <project> [--image <name>] [--export <file>]` runs it. `tianwen workbench operations` lists the
operations with their settings at their defaults. Exit 1 is a project that cannot be read, exit 2 a step or an export
that gave no result.

## Adding an operation

1. Its settings: a `sealed record` of settable properties with defaults in `OperationSettings.cs`, and a
   `[JsonSerializable]` line on `OperationSettingsJsonContext`.
2. The class: `Operation<TSettings>` (stored), `DerivedOperation<TSettings>`, `JointOperation<TSettings>` (one run
   for several images) or `FindingOperation<TSettings>` (no pixels). It reads through `OperationInput`
   (`TryRead`, or `TryReadMaster` where the verb masks the absent ring) and writes with `OperationOutput.Write`. It
   calls the existing routine and adds nothing.
3. The verb calls it. Where the verb needs more than the file (a companion picture, a second output), the operation
   offers the work without the files, as `GradientOperation.ExtractAsync` does, and both write with the same writer.
4. A line in `WorkbenchServiceCollectionExtensions.AddWorkbench`.
5. A step in `WorkbenchVerbParityTests`, so its command line and its file are held to the verb's. The enhance's is in
   `WorkbenchEnhanceParityTests`: the step and the verb there share one pipeline of stand-in enhancers. A verb that is
   not a `tianwen image` one has the same test beside it (`WorkbenchSolveTests` for `tianwen solve`).

## Not here yet

Each is a later leg of W1 (#1641) or a later phase, and none is assumed by the code above.

- **The remaining operations.** The deep-sky frame steps (#2467) and the planetary steps (#2468). Every colour step of
  #2465 is here but the one below, and so is the canonical enhance (#2466).
- **`image denoise --colour`** (#2465): three planes denoised as one colour image, where the order of the planes is
  their colour, so it is not a run several images share.
- **A set of frames as an input** (calibrate, register, integrate; #2467).
- **Statistics** (`image stats`, `image sources`, `image channel-offsets`). A measurement changes nothing a later step
  reads, so as an edit of a list it would only give every step after it another key. It is a fact about a step's
  result, which is W9's (#2236): measured when a gate or a panel asks, and kept by the key of the result it measures.
- **Preconditions and how an operation previews**: the facts are W9's (#2236), the preview W12's (#2239).
- **The enhance program's steps as edits of their own.** The enhance is one step today (above). Each of its steps
  shown and switched by itself is W6's (#1646).
- **Progress.** An operation reports lines (`say`); the engines' own `IProgress` (a step's percentage) is not passed
  through the contract yet, and the window (W2, #1642) needs it.
