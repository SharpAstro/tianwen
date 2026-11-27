# The processing app (`tianwen-edit`)

The window over the workbench's runner: a project's images, each a list of steps, one screen that shows where the list
stands and lets a person run it. The design is [the stacking workbench plan](../plans/stacking-workbench.md) ("W2 The
app's shell", #1642); what a step is and how the runner decides is [workbench-operations.md](workbench-operations.md).
The program is `TianWen.UI.Workbench`; everything that is not SDL, Vulkan or the window lives in `TianWen.UI.Abstractions`.

## The shared host

`WorkbenchHost<TSurface>` is the whole of the app's input handling, between-frame work, redraw question and paint. The
window (`Program.cs`) and the headless harness (`WorkbenchE2E` in the tests) both construct that one class, as
`StandaloneViewerHost` is for `tianwen-fits`, so the path a test drives is the path the window runs. A new host step goes
into the host, never into `Program.cs`.

The pieces it holds:

- `WorkbenchState`: the project on show, the selected step and the loop's flags. What the runner said is one immutable
  `WorkbenchView` (the project, the image, its report, which outputs exist and what the original is, all worked out off the
  render thread with the answer), replaced whole, so the render thread never sees half an answer and never asks the disk.
  The state itself holds mutable flags and the selection, set from the runner's answer or a click on the selection only.
- `WorkbenchActions`: what the window does (open a project, choose an image or a step, copy a command line, start a run).
  It asks the runner off the render thread and its answer is adopted in `BeforeFrame`, where the document may change.
- `WorkbenchJobs`: one run at a time (see "Jobs and cancel").
- `WorkbenchViewDriver`: gives the viewer the file for the chosen view.
- `WorkbenchScreen<TSurface>`: the layout.

## The screen and the layout

One `Layout` tree, built each frame from the state: the top bar (the project's folder, the image choice where the project
has several, New project and Open project), the step bar, a
340-wide panel on the left and, beside it, the view row with the viewer's own renderer in a keyed canvas. The viewer's
toolbar is cut to the controls that are about looking (`ViewerOffer`): no Open file, Enhance, crop or solve, because
those are operations here, and no file list.

The look follows `.github/docs/ui-changes.md`. Specifically:

- A step's number and title are one label (`WorkbenchStepItem.Label`), shared by the bar cell, the panel heading and the
  hover text, so a cut title is cut with an ellipsis and its whole text is on hover.
- Step words and marks use the palette's body or error ink and meet contrast in every palette; Night is judged against a
  lower floor than Light and Dark.
- The image choice is the engine's dropdown (`Layout.Builder.DropdownBox` and `Dropdown` over a `DropdownMenuState`): the
  box shows the image on show, ellipsised with the whole name on hover, and its menu lists every image of the project to
  pick directly. The menu hangs under the box where the frame arranged it, as wide as the box, and is painted after the
  viewer, so it is over the canvas. `Dropdown` takes the list's width as a design size while it places the anchor in
  surface units, so the box's width goes back through the scale (`Scale.ToDesignX`).
- The panel scrolls (`WithScroll`) rather than cutting what does not fit; the job's lines scroll and follow the end.
- Text in the panel wraps word by word. A word wider than its flow (a path in a command line) is broken into the longest
  pieces the engine measures as fitting the width the engine arranged that flow at, after a folder separator where one
  lies in the second half of a piece. The screen arranges once, reads each flow's arranged width, and arranges again in
  the same frame only when one changed (the first frame, a resize, a new DPI), so no width is restated by hand. The pieces
  sit edge to edge (the space between words is a word's own trailing space, not a gap between leaves), so a broken path
  never reads as two words.
- `WorkbenchCaptureTests` renders the guided screen at 1280x800 and 2000x1100 in Dark and Night, and 2000x1100 at DPI 1.5,
  with 14 steps, long titles, a refused step with its reason, a long command line and run lines in the job panel. It
  asserts that no text is cut without an ellipsis and its whole text on hover, that step words and marks meet contrast
  against the cell's painted fill, and that the step row's controls share one height and one middle. With
  `TIANWEN_KEEP_TEST_OUTPUT=1` it keeps a PNG per case in the test binary's folder. The class is in `ThemeCollection`
  because it applies a theme, which is process-wide.
- The test host paints the chrome on a CPU surface and **paints no image**: the viewer's draw of a picture is Vulkan's. So
  every capture, Result and Comparison included, shows the screen's chrome over an empty canvas; a capture named Result
  or Comparison shows which view is chosen and what the panel says, not the picture or the split. The picture in a live
  window is the controller's to look at.

## Step-bar states

Each cell shows the number, the title and a word from the runner's status, never from a click: kept, will run, not needed,
derived, off, refused, blocked, and a "changed" note when a step's settings (or a step before it) are not what the last run made it from.
A step that is running is not a state of the bar: the job panel shows it. A refused step (the operation is unknown, or its settings are refused) says why in the panel, and the steps after it are
blocked. Selecting a step shows its panel: the words and reason, Run, the job, Views, Settings and Command line (the line
the verb would take, to copy; "There is no command line until the step can be planned" when it is refused).

## Run to here and run on

- **Run to here** makes the image up to the selected step. Its label is the runner's word for what that would do (a status
  through that step), read once the answer is in, so the button never promises what the runner did not say.
- **Run on from step N** goes on from the first step that is not kept. The switch "Run on until a step needs me" (on by
  default) takes the run to the end of the list and stops at a step that is refused or blocked, selecting it; off, a run
  makes only the next step that is not kept and stops with it selected.

## Jobs and cancel

`WorkbenchJobs` starts `WorkbenchRunner.RunAsync` on the background tracker with a token linked to the app's, takes the
engine's `IProgress` and log lines into one immutable `WorkbenchJobState` (running, step, title, fraction where the
operation reports one, every line, how it ended) and says when it ended. A second start while one runs is refused, and no
screen offers it. The panel shows the running step, a bar, the lines, and Cancel, which ends the run at its next check of
the token. A run ends as Finished, Stopped (at a refused or blocked step, with the reason), Cancelled or Failed (an
exception). What a run did to the steps is never read from the job: the settled callback asks the runner afresh.

The cancel source is made before a run is published and travels in the same `WorkbenchJobState` swap that says it is
running (`Owner`), so Cancel finds it from the first moment `Running` is true, and a run's end clears only its own state:
a start pressed on the very frame the previous run's end asked for keeps its own source and can be cancelled
(`WorkbenchJobTests.AStartTheMomentTheFirstRunEndedCanBeCancelledAndKeepsItsOwnSource`).

While a run goes it writes the step store, so nothing else asks the runner for a status: no plan is asked for, and the
image choice and Open project are dimmed with the reason on hover (`WorkbenchActions.ChoiceWaitsForTheRun`);
`OpenProject` and `ChooseImage` refuse too. When the run ends, the image on show is read afresh, unless a read of
another answer is already on its way, which the run's end then leaves alone rather than superseding it through the one
status slot. A project's path is resolved inside that read, off the render thread.

## The three views

Original, Result (the selected step's stored result) and Comparison (the step's input against its result, through the
viewer's `SplitCompareController`). A view the selected step cannot offer is not offered, and the canvas shows the
original with the words saying why: a step that is not made yet, one that is refused or blocked, one that wrote no pixels
(a finding, the plate solve), one whose results are named by its run (a stack's masters, which has no image of its own),
and a first step, which has no input to compare. An original the viewer cannot draw (a folder of frames, a SER capture)
is described in words. Which pictures a step offers is worked out once with the runner's answer, off the render thread, and
every part of a frame reads that one value.

## New project

A "New project" button stands beside "Open project" in the top bar, on one row of one height and one middle. It makes a
project from a folder of captured frames: the project holds one step, the stack, and "Run to here" stacks the capture with
the run's progress and cancel, as for any step.

- **Two folder dialogs, in this order**, both through `IFileDialogs.OpenFolderAsync`: "New project: choose the folder for
  the project" (it opens where the current project is), then "New project: choose the folder of the capture to stack" (it
  opens at the project folder just chosen). Cancelling either one changes nothing: no file is written, no message is shown.
- **The work is the Lib routine's**, `WorkbenchProjects.NewAsync`, the one the `new` verb calls. `WorkbenchActions.NewProject`
  runs both dialogs and the routine through the background tracker, never on the render thread, and then opens the new
  project through `OpenProject`, so the step bar shows the stack step selected and the status comes from the runner.
- **A refusal is said in words.** The routine refuses a folder that already holds a `project.json`, a missing capture folder,
  a capture folder equal to the project folder, one inside the step store and a project folder inside the capture folder (the
  stack would read `project.json` and the store among the frames). A failure the routine throws (a project folder that is a
  file, a read-only folder, a drive that went away) is said in the same words. The words are kept in
  `WorkbenchState.NewProjectRefusal` and shown in a strip under the top bar ("No project was made: ..."), ellipsised with the
  whole text on hover. The strip's row is always reserved (empty while there is no refusal), so its appearing moves nothing under the pointer; the
  next successful open clears it.
- **While a run goes the button is dimmed** with the same reason as Open project (`WorkbenchActions.ChoiceWaitsForTheRun`),
  and `NewProject` refuses too, so no dialog opens: a run writes the store and no other project is read meanwhile.
- **With no project open the two buttons take the accent**, the colour of an action, since they are all there is to do; with
  a project open both are standard buttons. The empty step bar says "Open a project, or make a new one from a capture
  folder, to see the steps of an image."
- **A long name never pushes the buttons off the bar.** The project's folder (cut at its start) and a single image name
  (cut at its end) share the width the buttons leave, each with its whole text on hover.
- Tests: `WorkbenchE2ETests` presses the button with two faked folder picks at DPI 1 and 1.5 (the project is the Lib
  routine's, byte for byte, with one selected stack step; a refusal is said; a cancelled dialog changes nothing; a run
  dims it). No stack is run. `WorkbenchCaptureTests` renders the empty window, a new project, a long capture path and a
  refusal at 1280x800 and 2000x1100 in Dark and Night, and at DPI 1.5.

## The stack's masters and "Process this master"

A stack names its results by its run (`IRunNamedOperation`), so the stack step has no image of its own. Its panel therefore
has a "Masters" section between the run controls and the views, fed from `StepReport.Results` (one master per target and
filter group), by `WorkbenchStepPanel.Masters`.

- **Before the stack has run** the section says "The masters come once the stack has run: one for each target and filter
  group." and lists none. A stack that ran and gave none says "The stack gave no masters."
- **After it** each master is one row: its group name (cut at its end, the whole name on hover) and a "Process this master"
  button. All rows share one height and one middle, and the buttons share one width column, so a long group name gives way
  and never pushes a button off the panel.
- **A press is the Lib routine's work**, `WorkbenchProjects.AddMasterChainAsync`, the one the `workbench process` verb calls. It
  adds the enhance's chain as single steps (starless, stars, result, with the result named for the group, made unique with a
  `-2` suffix when the name is taken). `WorkbenchActions.ProcessMaster` runs it through the background tracker, never on the
  render thread, then opens the project again with the chain's result image selected.
- **A second press for the same group is not refused**: the routine names the new chain uniquely, so both are kept.
- **A refusal is said in words** beside the list (`WorkbenchState.MasterRefusal`, "<group> was not processed: ..."), for
  example a stack whose files the store has lost ("run it first"). The next open of a project clears it.
- **The buttons are dimmed with their reason** while a run goes (`ChoiceWaitsForTheRun`), while a status is being read and
  while an earlier press is still laying out its chain (`WorkbenchState.IsProcessingMaster`); `ProcessMaster` refuses in the
  same cases. While a press runs, `OpenProject` and `ChooseImage` refuse too, so a choice made meanwhile is not landed on
  by the chain's own open (which goes through a private path that skips that guard).
- **The chain is added under the project file's lock**, re-reading `project.json` there, so a second press or the CLI while
  the app is open never drops the other's images.
- Tests: `WorkbenchMastersE2ETests` at DPI 1 and 1.5 (no masters before the stack; one row per master after it; a press adds
  the chain as the routine does and shows its result; a second press is named uniquely; a lost master is refused in words;
  the dimmed buttons). `WorkbenchCaptureTests` renders one master and several long group names at 1280x800 and 2000x1100
  in Dark and Night, and at DPI 1.5.

## A comparison the viewer could not keep

A Comparison keeps the step's input pixels as the before half. When the viewer declines (it does under memory pressure,
`ShouldSkipBeforePixelCache`) the person gets the result alone, and the Views section says "The comparison was not kept for
want of memory" (`WorkbenchState.ComparisonDeclined`, set by `WorkbenchViewDriver`, cleared when another picture is asked for).

## What W2 leaves out

- Editing a step's settings: the panel shows them (W6, W12).
- Gates, and a workflow's own editor (W9).
- The other screens: the planetary steps (W3), deep-sky import, inspect and calibrate (W4), register and integrate (W5),
  processing and colour (W6), profiles and batches (W7).
- Packaging and release (W8).
