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

The rules that bite:

- **A verb's work is its operation's routine.** The verb builds the settings from its options and calls the
  operation; the runner reaches the same method through `IOperation`. `image linear-fit` calls
  `LinearFitOperation.RunAsync`; `image flatten` calls `GradientOperation.ExtractAsync` and writes with
  `OperationOutput.Write`; `image crop` and `image autocrop` build `CropSettings` and call `CropOperation.TryResolve`
  (the four numbers, or `MasterAutoCrop.Scan`, handing the scan back for the verb's declined and margin lines) and
  `CropOperation.Write` (`MasterCrop`); `--dry-run` stops between the two. An operation adds nothing between its routines. `WorkbenchVerbParityTests` runs every step's
  printed command line through the real `tianwen image` command and compares the files to the byte.
- **A setting is a settable property with its default.** The JSON source generator gives an absent init-only property
  its type's default, not the declared one, so `{ get; set; } = default` is the only form. The type goes into
  `OperationSettingsJsonContext`, which refuses a member the type does not have: a misspelt setting must not run at its
  default and say nothing.
- **Other pixels for the same settings is a new `Version`.** A changed algorithm or a changed default raises it, so a
  result of the old code is never taken for the new code's. Adding a setting changes the canonical settings, and with
  them every key of that operation; that costs a re-run and is never wrong.
- **What serves the step is in its key.** `GradientOperation.ServedBy` is `classical` or the registered corrector's
  name, so on a computer where `auto` means GraXpert a result is not taken for the classical fit's.
- **A refusal is an answer, not a throw.** `OperationOutcome` says which kind (`Input`, `Failed`, `NotServed`: the
  verbs' exit codes) and why. A cancellation is thrown and never caught as a failure.
- **A stored step reads a file and writes a file.** `OperationInput` is a file, or the result of a derived step. Only a
  derived step hands its result over in memory, and only to the step after it.

## Stored and derived

- **Stored** (`gradient`, `linear-fit`): the result is written into the store by the run that made it, and found there
  by its key ever after.
- **Derived** (`crop`): the edit holds the rectangle or the rule (`auto`); the pixels are cut out of the input when the
  next step asks, and no file is written. A derived step has a key like any other, so a changed rectangle changes the
  key of every step after it. Its `RunAsync` is still its verb's routine: `tianwen workbench run --image <name>
  --export <file>` writes a derived result as the file `image crop` would write.

**What a derived crop carries.** Handed on in memory it is the pixels and the moved plate solution, which is all the
stored operations here read. Written as a file from a FILE it also carries the alignment and stack cards and the map
sidecars (`MasterCrop.CropLoaded`). An operation that reads a map sidecar after a derived crop finds none; none exists
yet, and the one that will (SPCC's saturation map, #2284) has to settle this first.

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

An original file's key is the digest of its bytes (`StepKey.OfFile`), computed on every status and run. Keys are made
from keys, so a stored result is never hashed. Nothing is decided by a file's date: the same bytes under a new date
run nothing, and other bytes under the old date run everything after them (`WorkbenchRunnerTests`).

The map sidecars beside a master are not part of its key. The stacker writes them with the master, so one does not
change without the other.

## The step store

`<project>/store/`, one `<digest>.fits` a stored step, and `index.json` naming every result with its key, its
operation and its size.

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
   (`Ran`), one that is there is `Kept`, a derived step is `Derived`.
4. A run records each step's key and result in the project file, and writes it only when something changed.

`tianwen workbench status <project>` prints the plan with every step's command line and writes nothing.
`tianwen workbench run <project> [--image <name>] [--export <file>]` runs it. `tianwen workbench operations` lists the
operations with their settings at their defaults. Exit 1 is a project that cannot be read, exit 2 a step or an export
that gave no result.

## Adding an operation

1. Its settings: a `sealed record` of settable properties with defaults in `OperationSettings.cs`, and a
   `[JsonSerializable]` line on `OperationSettingsJsonContext`.
2. The class: `Operation<TSettings>` (stored) or `DerivedOperation<TSettings>`. It reads through `OperationInput`
   (`TryRead`, or `TryReadMaster` where the verb masks the absent ring) and writes with `OperationOutput.Write`. It
   calls the existing routine and adds nothing.
3. The verb calls it. Where the verb needs more than the file (a companion picture, a second output), the operation
   offers the work without the files, as `GradientOperation.ExtractAsync` does, and both write with the same writer.
4. A line in `WorkbenchServiceCollectionExtensions.AddWorkbench`.
5. A step in `WorkbenchVerbParityTests`, so its command line and its file are held to the verb's.

## Not here yet

Each is a later leg of W1 (#1641) or a later phase, and none is assumed by the code above.

- **The remaining operations.** The other colour steps (#2465: `match-psf`, `deblur`, `denoise`, `remove-stars`,
  `protect-cores`, `fill-digs`, `continuum`, `add-line`, `luminance`, `lrgb`, `add-stars`, `align`, `combine`), the
  deep-sky frame steps (#2467) and the planetary steps (#2468).
- **A step with two results** (star removal: the starless plate and the stars), and a second image that starts from
  one of them (#2465). The store's index entry and the project's `inputs` are where it goes.
- **A set of frames as an input** (calibrate, register, integrate; #2467), and an operation that writes no pixels
  (plate solve, statistics; #2469).
- **Preconditions and how an operation previews**: the facts are W9's (#2236), the preview W12's (#2239).
- **The enhance program's steps as edits of their own.** `EnhanceOperation` (#2466) runs the canonical program as ONE
  step: its settings say which steps of `LinearEnhanceProgram` are kept, and that record turns them into the
  `SharpenStep` records, which stay the pipeline's in-memory program
  ([stacking-workbench.md](../plans/stacking-workbench.md), "An operation"). `image sharpen` takes its program from
  the operation (`Start`, `ProgramAsync`) and adds only its own stretches. What serves an enhance is the pipeline's
  word (`SharpenPipeline.DescribeRoles`). Each step shown and switched by itself is W6's (#1646).
- **Progress.** An operation reports lines (`say`); the engines' own `IProgress` (a step's percentage) is not passed
  through the contract yet, and the window (W2, #1642) needs it.
