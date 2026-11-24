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
  key of it, and the same for a denoise, a deconvolution and the stellar sharpening left out. Nor is a role the
  program leaves out on EVERY image: where a deblurrer serves mono and colour alike
  (`SharpenPipeline.DeblurServesEveryInput`) its whole-frame deblur always runs, so the stellar sharpening never
  does and the starless deconvolution only with its blend stated, and a status says `deconvolve left out` as the run
  does. An in-house enhancer's name carries its model file, so a retrained model is another key. An RC-Astro
  role's name carries the model version a run uses, as "RC-Astro NoiseXTerminator (nxt) ML3.1": the newest one
  installed, read from what the CLI says of itself (`rc-astro --json`, `IRcAstroCli.ModelVersion`) and not from the
  licence line, whose "licensed through ML3" covers ML3.1. A new model is another key. The CLI's own version is
  not in the name: 2.6.10 and 2.6.11 gave the same pixels to rounding, and a key that held it would run every
  RC-Astro step again after each update for nothing; it is said in the result's header. Naming an RC-Astro role asks whether
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

**What a no-pixel step does with what sits beside its input's file.** A step that writes no pixels (the plate solve)
hands on its input with the input's file path kept (`OperationInput.FromDerivation(name, path, ...)`), so a crop after
it reads the master's coverage sidecar as it would on the input: `[solve, crop auto]` crops as `[crop auto]` does.
Its verb agrees: `tianwen solve -o` carries the map sidecars to the file it writes, as a crop of the whole frame does
(`MasterCrop.CarrySidecars`), so `tianwen solve -o` then `image autocrop` finds them too. `-o` cannot name the input
(`--update-fits` is the flag that writes it).

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

**The solved image is written from the solution as a header holds it** (`PlateSolutionFile.AsHeld`,
`SolveOperation.WriteSolved`). A card keeps fewer digits than a double, and a long number is written in another form
than a short one, so the solver's answer and the same answer read back from a file would give two files that differ in a
card's text and in no digit. Held once, a solution is written the same way ever after, which is what makes the verb's
file and a step's export one file.

- `tianwen solve` builds `SolveSettings` and calls `SolveOperation.SolveAsync`, and writes the solved image with
  `SolveOperation.WriteSolved`. It gains `-o`: the image with the solution in a file of its own, the input left as it
  is, which is how a project's solve step is done from a terminal (`--update-fits` still writes the input in place).
- **Which solver answered is not in the step's key.** The solvers are tried in order of priority until one answers,
  and every answer is a solution of the same sky; installing another solver must not run again every step after a
  solve. A host with no solver has the operation, and its step says so when it runs.
- A solve that finds nothing is a refusal of the step, kept nowhere and tried again by the next run.

## The operations

`tianwen workbench operations` prints this list from the code: each operation with what it takes and gives beside
its own image, and every setting at its default. An edit names an operation by its id.

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
| `denoise-colour` | stored; green and blue as extra results | `image denoise --colour` | `green`, `blue`. Its own image is red |
| `remove-stars` | stored, one run for several; the stars as a second result | `image remove-stars` | the other masters of the run |
| `combine` | stored | `image combine` | `green`, `blue`, and optionally `ha`. Its own image is red |
| `enhance` | stored | `image sharpen` | |
| `solve` | no pixels: the plate solution is stored | `solve` | |
| `planetary-stack` | stored; the sharpened master as an extra result | `planetary stack --auto` | none. Its own image is the capture, a SER file |
| `planetary-compose` | stored; the luminance as an extra result, where there is one | `planetary compose` | the other mono stacks, any number, as a set |
| `planetary-sharpen` | stored | `planetary sharpen --auto` | none. Its own image is a planetary master |
| `stack` | stored; each group's master a result named by the run | `stack` | none. Its own image is a folder of frames |

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
  --colour`, where the order of three planes is their colour, is for that reason not the denoise operation but one of
  its own, with fixed inputs (below).
- **A result can have a second result beside it** (`OperationDescription.ExtraResults`): star removal gives the starless
  plate and, as `stars`, the stars it took out. Each extra result is kept in the store under a key of its own, and a
  step is kept only when all of its results are there.
- **Its verb takes `-o` once for each master**, in the masters' order, and `image remove-stars` takes `--stars-output`
  the same way, so a step's command line names every file it writes. Two results named into one file are refused:
  the second would be written over the first.
- **`Joint` is said once in effect.** The description says it for a listing and a window, the type
  (`IJointOperation`) for the runner, and `OperationCatalog` refuses an operation whose two disagree, a shared run
  that names images by role, and extra results on an operation that cannot be told where they go.

## Fixed inputs, several results

`image denoise --colour` puts three mono planes through the denoiser as ONE colour image and hands back three
(`denoise-colour`; `IMultiResultOperation`, `MultiResultOperation<TSettings>`). Which plane is which colour is its
place among the inputs: the edit list's own image is red, and it takes green and blue.

- **Red's plane is the step's own result; green's and blue's are its extra results**, each kept in the store under
  `StepKey.OfResult(step, name)`. An image of the project starts from one with `from` and `result`.
- **The step is kept only when all three are in the store**, as a shared run's is.
- **A catalog refuses an operation that names extra results and is neither kind**: nothing could tell it where they
  go, so its step would never be whole and would run on every run.

```json
{ "name": "red-denoised", "from": "red-starless", "edits": [ { "operation": "denoise-colour", "inputs": [ "green-starless", "blue-starless" ] } ] },
{ "name": "green-denoised", "from": "red-denoised", "result": "green" },
{ "name": "blue-denoised", "from": "red-denoised", "result": "blue" }
```

## A capture as an image's original

A project's image can be a planetary capture (a SER file). Its key is the digest of the file's bytes like any
original's, and its first edit is `planetary-stack`, which reads the capture by its path; any other operation refuses
it as a file that is no FITS image by its name, before any reader is handed it ("<path> is no FITS image, and this
operation takes one", from `OperationInput`).

`planetary-stack` is AUTO (`PlanetaryAuto`): the capture identified, then stacked and sharpened at the measured
defaults. It runs the three routines `planetary stack --auto` and the viewer's Auto view run, in their order and with
nothing between: `PlanetaryIdentification.IdentifyAsync`, `PlanetaryAuto.RunAsync`, `PlanetaryAuto.Write`. The linear
master is the step's result, and the sharpened master its extra result (`sharpened`).

- **Its settings say the capture** (`planet`, `filterNm`, `apertureMm`, `design`) in place of what it is read to be,
  and choose three things of the finished masters (`PlanetaryAutoChoices`, the owner's call of 2026-11-23): `strength`,
  how far past the truth the derived sharpening goes; `luminanceOnly`, a colour master's luminance sharpened once;
  and `colourBalance` with `colourSaturation`, the balance to the planet's own colour or none. None of them changes
  how the capture is stacked, which stays the measured defaults.
- **A changed setting stacks the capture again**, since the step is one run. A strength is tried on
  `planetary-sharpen` (below), which only sharpens. The stack step has the choices all the same, because its
  sharpened master is the one sharpened before the colour balance, which is the pipeline's order.
- **A step never asks what telescope a camera was last said to be on** (`PlanetaryTelescopeMemory`). That is this
  computer's memory, and a step's result follows from its capture and its settings. The verb's
  `--no-telescope-memory` is the same, and a step's command line says it.
- **What a capture says of itself is more than its bytes**: its capture program's settings file beside it, and its
  file's and folders' names. The step's key holds the bytes and the settings, so a capture moved to a folder named for
  another telescope keeps its key. State in the settings what the result must not depend on.
- **The verb names AUTO's two files** with `--master` and `--sharpened`, so a step's line names what it writes.

```json
{ "name": "jupiter", "path": "captures/Jupiter_Red_2024-12-15T12_56_44.ser", "edits": [ { "operation": "planetary-stack", "settings": { "apertureMm": 254, "design": "newtonian" } } ] },
{ "name": "jupiter-sharpened", "from": "jupiter", "result": "sharpened" }
```

## A planetary master sharpened again

`planetary-sharpen` sharpens a master without stacking it again: one a compose joined, one a step cropped, or the
linear master of a stack. It runs two routines in their order and nothing between,
`PlanetaryIdentification.IdentifyMasterAsync` and `PlanetaryAuto.Sharpen`, which is `PlanetaryBestStack.Sharpen` with
the options AUTO stacks with, so a master is sharpened one way whether it has just been stacked or was read from a
file.

- **Its settings are the stack step's** (`PlanetaryIdentitySettings`: `planet`, `filterNm`, `apertureMm`, `design`):
  what is said of the master, in place of what its header says. Like the stack's, the step never asks what telescope
  a camera was last said to be on. It also takes `strength` and `luminanceOnly`, the two of AUTO's choices that are
  about sharpening, and it is where a strength is tried: a changed one sharpens again and stacks nothing. The colour
  balance is not among its settings, since a written master is not balanced again.
- **Where the gains cannot be derived the master is still sharpened**, by the planetary preset, as a stack's is: no
  planet with a rotation model, no instant, no telescope, or a limb that does not fit. The step's words say which,
  and it is no refusal.
- **A colour master is sharpened as it was written**, its colour balance already in it. A stack sharpens before it
  balances; a master read from a file has no unbalanced form to go back to.
- **The verb's `--sharpened <file>` is the step**: with `--auto` it calls the step's routine
  (`PlanetarySharpenOperation.SharpenToAsync`) and writes that one file. Without it `planetary sharpen --auto` is the
  verb's research path, which reads and scores as it goes. The two give the same file where the gains are derived, and
  `WorkbenchPlanetaryTests` holds them to it there. Where they are not, the research path may refuse (exit 1 with "name
  the planet", "no time in its header; give --utc" or "the planet's limb could not be fitted") and the step writes the
  preset-sharpened master.
- **A master with no file has no names to read**: the input a crop hands over is not a file, so no file or folder name
  is taken for the master's (the process's working directory never is), and the step follows from its header and its
  settings alone.

```json
{ "name": "jupiter", "from": "green", "edits": [ { "operation": "planetary-compose", "inputs": [ "red", "blue" ] } ] },
{ "name": "jupiter-sharpened", "from": "jupiter", "edits": [ { "operation": "planetary-sharpen", "settings": { "apertureMm": 254, "design": "newtonian" } } ] }
```

## Any number of images, as a set

`planetary compose` joins every mono stack of a night into one colour master, however many there are
(`planetary-compose`; the description says `AnyNumber`). The edit list's own image is one of the stacks and `inputs`
names the others.

- **The images are a set.** The runner sorts them by key, the edit list's own among them, before it hands them over.
  So the step's key and its result do not depend on which stack's list holds the edit, nor on the order it names the
  others in, and two lists that hold the same edit over the same stacks share one result. An image handed in twice is
  refused. What each image IS is then read off the image, never off its place: a stack's planet, instant and filter
  are in its header or its file's name.
- **A result a step may leave out** (`OperationResultRole.Optional`): the stacks through an IR-pass or an L filter are
  joined as the `luminance`, which is the step's extra result where there is one. The step is whole in the store
  without it: its main result's entry in the index names what it left out (`StepStoreEntry.LeftOut`), so a result it
  gave and the store lost later makes the step run again, where one it left out does not. An image that starts from a
  result the step left out is refused in words, by a status and a run alike, and so is an image one hop past it (its
  refusal carries the reason); an edit of either is `Blocked`.
- **Only a step with results of its own can leave one out** (`MultiResultOperation<TSettings>`, with fixed inputs or a
  set). A run several images share gives every result for every image, and one it does not write is a refusal of the
  step. A catalog refuses an operation that says `AnyNumber` and also names images by their place, or shares its run.
- **It runs the verb's routines** in their order: `PlanetaryComposition.Ingest` for each stack,
  `PlanetaryComposition.Run`, and `PlanetaryComposeOperation.WriteAsync`, which the verb's `compose`, `join` and `lrgb`
  write through too. A file is read as the verb reads it, so a TIFF another stacker wrote is a stack. The verb's
  preview picture is its own, and no step writes one.
- **Its settings are the verb's options**: `planet`, `lrgb`, `viewingWeights`, `bandWeights`, `relight`,
  `ringFeatherInner` with `ringFeatherOuter`, and `colourBalance`. The verb's `--utc` and `--filter` label every stack
  alike, which is for one stack; a step has neither.
- **A stack's file name can be one of its labels** (a WinJUPOS-style name, where its header says nothing), and a name
  is not in a key, as with a capture above: a stack renamed to another filter keeps its key. A stack a compose step
  wrote carries its labels in its header.
- **The verb names the luminance's file** with `--luminance-output`, so a step's line names what it writes.

```json
{ "name": "red", "path": "stacks/2024-12-15-1256_7-R-Jup.tif" },
{ "name": "green", "path": "stacks/2024-12-15-1258_2-G-Jup.tif" },
{ "name": "blue", "path": "stacks/2024-12-15-1259_7-B-Jup.tif" },
{ "name": "ir", "path": "stacks/2024-12-15-1305_7-IR-Jup.tif" },
{ "name": "jupiter", "from": "green", "edits": [ { "operation": "planetary-compose", "inputs": [ "red", "blue", "ir" ] } ] },
{ "name": "jupiter-luminance", "from": "jupiter", "result": "luminance" }
```

## A set of frames as an image's original

A project's image can be a folder: a night's lights with its calibration frames (#2467). Its first edit is the step
that stacks it, which reads the folder by its path; any other operation refuses it as no image, in words.

- **Its key is every frame's place and digest**: each frame's path inside the folder and the digest of its bytes, in
  the order of the places (`FrameDigestIndex.KeyOf`). A frame is a FITS file as the stacker's scan lists them, or the
  sidecar that tells the scan a folder's filter (`.tianwen-meta.json`); any other file is no part of the set. A frame
  renamed is another set, since the scan reads a rejection and a suspicion of cloud off a frame's name.
- **A frame is not read twice to say the same thing** (the owner's call of 2026-11-23). The digests are remembered
  beside the store, in `store/frames.json`, under each file's path, size and last write time, and a frame whose size
  and time have not moved is not read again. The key is still made of content: the same bytes under a new time cost
  one read and change no key.
- **A stamp is not a checksum**, as `FitsHeaderIndex` says of itself, which made the same trade for the same disk.
  Bytes changed in place under an unchanged size and time are served stale. `--verify-frames` on `status` and `run`
  reads every frame and corrects the index (`WorkbenchRunOptions.VerifyFrames`). This is the one place the rule
  "nothing is decided by a date" is narrowed, and only for a set of frames.
- **A status keeps what it read.** It writes no result, and it does write the index: reading a night again to run
  what a status has just described is the cost the index is there to save.
- **The project's folder stays outside the frames.** A folder that holds the project's store is refused, since a
  result would be one of the set it was made from. Nothing is exported among an original's frames either.
- **A folder with no frame is no set**: it is refused in words ("holds no frame": an unmounted mount point, a tree of
  junctions, a mistyped folder) and its walk forgets nothing, so the disk coming back is not read again. The index
  drops a frame that is gone from a folder that was walked, and everything under a folder no image names any more.
- **A folder is read once per run**, however many images name it, and a `--verify-frames` reading serves a plain ask of
  the same folder. The walk ends between two frames on cancellation.
- **`--image` does not limit which sets are read**: every image is planned and reported, so every set of frames is
  digested (or taken from the index) whatever image is named. Only what counts as needed narrows.
- **Only an operation with `OperationDescription.TakesFrameSet` takes a folder**; any other is refused one in words by
  a status as by a run. The stack step's leg sets it.
- A status and a run say how many frames were read for their digest and how many the index knew.

The step that stacks a set is `stack` (below, "The stack of a set of frames"); `WorkbenchFrameSetTests` holds the
set and its key with a stand-in.

## Results named by the run

A stack gives a master for each group its scan finds, so what it gives is not known until it has run. An operation
says so with `NamedByRun` (`IRunNamedOperation`, `RunNamedOperation<TSettings>`), and answers a run with its results:
each one's name, its file, and the files that belong beside it.

- **Each result is in the store under `StepKey.OfResult(step, name)`**, as an extra result of any step is, and an
  image of the project starts from one with `from` and `result`.
- **The step keeps the list of the names** under its own key (`<digest>.results.json`, `StepResultsList`). So a status
  knows what a stack gave without stacking, a name the run did not give is refused in words with the names it gave,
  and the step is kept only when its list is there with every result the list names.
- **What belongs beside a result is beside it in the store.** A master's map sidecars and its manifest are found by
  the master's name with another ending, so they are stored under the result's name there with their own endings
  (`<digest>.coverage.fits`), and the store's index names them (`StepStoreEntry.Companions`). A result is there only
  when they all are. A crop that starts from a stored master carries them as it does for a master in a folder.
- **The step itself has no image.** An edit after it in the same list, an image that starts from it and names no
  result, an edit that takes it as its other image, and an export of it are each refused in words.
- **Its verb writes into a folder**, so the place its command line names is a folder: where its results would go,
  under the names the verb gives them. Its run writes into a folder of its own inside the store, and the store takes
  from it what the run named.
- A file that is no FITS by its name is refused by `OperationInput` in words and never handed to the FITS reader: a
  capture given to an operation that takes an image, or a step's list of results.

```json
{ "name": "night", "path": "D:/Astro/2026-11-20", "edits": [ { "operation": "stack" } ] },
{ "name": "m31-luminance", "from": "night", "result": "M31_L_300s", "edits": [ { "operation": "crop", "settings": { "auto": true } } ] }
```

`WorkbenchNamedResultsTests` holds the kind with a stand-in; the `stack` operation of this example is the next
section.

## The stack of a set of frames

`stack` (`StackOperation`) is `tianwen stack` as a step: a folder of a night's lights with its calibration frames is
scanned, grouped, calibrated, registered and integrated, and each group's master is a result of the step under the
group's name (the verb writes it as `master_<group>.fits`), with its map sidecars and its manifest beside it in the
store.

- **Its settings are the 24 options that choose what is stacked and how** (the owner's call of 2026-11-23): which
  frames and groups (`groupFilter`, `groupExclude`, `groupTemperatureTolerance`, `splitByPierSide`,
  `includeMaybeCloud`, `includeIntegrations`, `qualityRejectSigma`, `rejectCloud`, `requireGainMatch`), how they are
  registered (`centroidDebayer`, `snrMin`, `minStars`, `quadStars`, `referenceFrame`, `warpInterpolation`,
  `colourAlign`) and how they are integrated (`strategy`, `stackDebayer`, `rejectLowSigma`, `rejectHighSigma`,
  `hotPixelSigma`, `drizzlePixfrac`, `drizzleMinFrames`, `bayerDrizzle`). Each default is its option's, read off
  `StackingOptions` where the option reads it.
- **The verb builds its options from the same settings** (`StackOperation.OptionsOf`), then adds what only it does: a
  rendition, an enhance, a comet run. An option and its setting cannot come to mean two things:
  `WorkbenchStackTests` replays a step's line, with every setting away from its default, through the verb's real
  option definitions and compares the pipeline's options whole.
- **No plate solve and no picture.** A solve is a step of its own (`solve`), and nothing is rendered: the step's line
  says `--no-plate-solve --output-format none`.
- **A step never asks what this computer remembers.** `tianwen stack` judges cloud against the clear nights the
  viewer recorded on this computer (`LightGradeReferenceStore`); a step judges on the group's own baselines
  (`StackingOptions.ClearNightReferences` off), and its line says `--no-clear-night-references`, the verb's option
  for the same. It is the rule the planetary stack follows with `--no-telescope-memory`. Nor does it choose an
  unstated strategy by this computer's free memory and disk: `StackingOptions.NominalProbe` chooses against a fixed
  nominal host (32 GB, 1 TB SSD) and refuses in words where this computer cannot fit the one chosen, and the line says
  `--nominal-host`, the verb's option for the same.
- **A setting that changes no pixel is refused**, where the verb warns and goes on: a drizzle's two settings where no
  drizzle can run, and `bayerDrizzle` off beside a stated drizzle. A filter that filters nothing is left out, never
  written empty, so each thing is said one way and has one key.
- **A group the stack skipped is no result.** The step says each one with the stack's reason, and an image that
  starts from it is told what the step did give. A set no group of which gave a master is the step's refusal.
- **`referenceFrame` is looked for in a frame's whole path**, as the verb looks, and the set's key holds each frame's
  place inside the folder only: name the frame by its file name.
- The auto-crop the verb writes beside each master, and the calibration masters a run builds, are in the folder the
  run writes into, which the store does not keep. A crop is a derived step (`crop` with `auto`); a stack that runs
  again builds its calibration masters again (below).

`WorkbenchStackTests` holds the step's master and its maps to the verb's, to the byte.

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
For a step over a set of images the `input=` lines are sorted too, and that digest is the step's own key.

An original file's key is the digest of its bytes (`StepKey.OfFile`), computed on every status and run. Keys are made
from keys, so a stored result is never hashed. Nothing is decided by a file's date: the same bytes under a new date
run nothing, and other bytes under the old date run everything after them (`WorkbenchRunnerTests`). A set of frames is
keyed from its frames' digests, and those are not read again while a frame's size and time stand (above).

The map sidecars beside a master are not part of its key. The stacker writes them with the master, so one does not
change without the other.

## The step store

`<project>/store/`, one `<digest>.fits` a stored step, and `index.json` naming every result with its key, its
operation and its size. The finding of a step that writes no pixels is a file there too, named by the finding's key.
`frames.json` beside them holds the digests of the project's sets of frames (`FrameDigestIndex`); it is no result
and names none. A result can have files that belong beside it, named as it is with their own endings, and a step
whose results are named by its run keeps their list as `<digest>.results.json` (above).

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

`tianwen workbench status <project>` prints the plan with every step's command line and writes no result (it keeps
the digests of a set of frames it read). `tianwen workbench run <project> [--image <name>] [--export <file>]` runs it.
Both take `--verify-frames`. `tianwen workbench operations` lists the
operations with their settings at their defaults. Exit 1 is a project that cannot be read, exit 2 a step or an export
that gave no result.

**What an automatic setting resolved to is said, never left to be guessed.** A status and a run print `served by:`
under each step that something outside the operation serves (`StepReport.ServedBy`, the text the key holds): the
enhancer of each role, the gradient corrector, the catalogue. It is said for a kept step as for one that runs. Where
a choice is made on the image and not on the settings, the step's own lines say it as it runs, and its verb says the
same words: the enhance says which enhancer serves each step of its program and the SCNR it ends on
(`EnhanceOperation.ProgramAsync`), and a stack says the strategy that made each master and where it came from, as
"integrated as BayerDrizzle (chosen for the nominal computer)", "(chosen for this computer)" or "(stated)"
(`StackOperation.GroupLine`, `GroupResult.Strategy`).

**Whose models served is one word, and the file says it too.** Where the enhancers say whose they are
(`IEnhancerAvailability.OriginUnder`, which a deferred RC-Astro role answers), a step's line carries `[rc]` (every
such role is RC-Astro's), `[tw]` (every one is TianWen's own) or `[mix]` after its name (`IOperation.ServedTag`,
`EnhancerOrigin.TagOf`); the gradient corrector and anything that does not say count for neither. It is no part of
the key. An enhance result's header carries the same, so the file says it wherever it is copied
(`EnhanceOperation.HeaderOf`, which `image sharpen` writes too): one card for each role a step asked (`ENHDEBLR`,
`ENHGRAD`, `ENHSTARS`, `ENHSTELL`, `ENHDECON`, `ENHDENOI`) with the enhancer's name, cut at the 68 characters a FITS
string card holds; `ENHBY` with the one word; and `ENHTOOL` with the tool that ran RC-Astro's models and its
version ("rc-astro 2.6.11").

## Adding an operation

1. Its settings: a `sealed record` of settable properties with defaults in `OperationSettings.cs`, and a
   `[JsonSerializable]` line on `OperationSettingsJsonContext`.
2. The class: `Operation<TSettings>` (stored), `DerivedOperation<TSettings>`, `JointOperation<TSettings>` (one run
   for several images), `MultiResultOperation<TSettings>` (several results of one step, with fixed inputs or a set),
   `RunNamedOperation<TSettings>` (results named by the run) or `FindingOperation<TSettings>` (no pixels). It reads
   through `OperationInput`
   (`TryRead`, or `TryReadMaster` where the verb masks the absent ring) and writes with `OperationOutput.Write`. It
   calls the existing routine and adds nothing.
3. The verb calls it. Where the verb needs more than the file (a companion picture, a second output), the operation
   offers the work without the files, as `GradientOperation.ExtractAsync` does, and both write with the same writer.
4. A line in `WorkbenchServiceCollectionExtensions.AddWorkbench`.
5. A step in `WorkbenchVerbParityTests`, so its command line and its file are held to the verb's. The enhance's is in
   `WorkbenchEnhanceParityTests`: the step and the verb there share one pipeline of stand-in enhancers. A verb that is
   not a `tianwen image` one has the same test beside it (`WorkbenchSolveTests` for `tianwen solve`,
   `WorkbenchStackTests` for `tianwen stack`).

## Not here yet

Each is a later phase (W2 to W9), or the comet run (#2489), which is outside W1, and none is assumed by the code above.

- **Operations.** None of W1's is left to wrap: every colour step of #2465 is here, so is the canonical enhance
  (#2466), so are the planetary steps (#2468: by the owner's call of 2026-11-23 the stack, the compose and the
  sharpening, with AUTO's three choices), and so is the stack of a set of frames (#2467).
- **Planetary grading, and a look.** `planetary grade` scores a capture's frames and writes nothing a later step
  reads, so it is a fact about a capture and W9's (#2236), as statistics are. `planetary colour` is a measurement
  against OPAL's maps and not what balances a master; the balance is inside the stack and the compose. A look
  (`planetary look`) is a rendering of the linear master and never a new master, so it goes with how a result is
  shown and exported.
- **A comet run as a step** (#2489): `--comet`, `--remove-stars` and `--manifest` are not W1: they are their own
  issue, by the owner's call of 2026-11-23, and none of them is a setting of `stack`.
- **What a stack keeps between its runs.** A `stack` step that runs again builds its calibration masters again, and
  judges cloud without the clear nights a rig keeps. The masters and what each light takes, and the subs' table
  with its gates, are W4's (#1644); a stack re-integrated from its manifest is W5's (#1645).
- **Statistics** (`image stats`, `image sources`, `image channel-offsets`). A measurement changes nothing a later step
  reads, so as an edit of a list it would only give every step after it another key. It is a fact about a step's
  result, which is W9's (#2236): measured when a gate or a panel asks, and kept by the key of the result it measures.
- **Preconditions and how an operation previews**: the facts are W9's (#2236), the preview W12's (#2239).
- **The enhance program's steps as edits of their own.** The enhance is one step today (above). Each of its steps
  shown and switched by itself is W6's (#1646).
- **Progress.** An operation reports lines (`say`); the engines' own `IProgress` (a step's percentage) is not passed
  through the contract yet, and the window (W2, #1642) needs it.
