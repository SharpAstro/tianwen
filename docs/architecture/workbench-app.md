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

One `Layout` tree, built each frame from the state: the top bar (the project's folder, the choice of image where the project has several, and Open project), the step bar, a
340-wide panel on the left and, beside it, the view row with the viewer's own renderer in a keyed canvas. The viewer's
toolbar is cut to the controls that are about looking (`ViewerOffer`): no Open file, Enhance, crop or solve, because
those are operations here, and no file list.

The look follows `.github/docs/ui-changes.md`. Specifically:

- A step's number and title are one label (`WorkbenchStepItem.Label`), shared by the bar cell, the panel heading and the
  hover text, so a cut title is cut with an ellipsis and its whole text is on hover.
- Step words and marks use the palette's body or error ink and meet contrast in every palette; Night is judged against a
  lower floor than Light and Dark.
- The panel scrolls (`WithScroll`) rather than cutting what does not fit; the job's lines scroll and follow the end.
- `WorkbenchCaptureTests` renders the guided screen at 1280x800 and 2000x1100 in Dark and Night, and 2000x1100 at DPI 1.5,
  with 14 steps, long titles, a refused step with its reason, a long command line and run lines in the job panel. It
  asserts that no text is cut without an ellipsis and its whole text on hover, that step words and marks meet contrast
  against the cell's painted fill, and that the step row's controls share one height and one middle. With
  `TIANWEN_KEEP_TEST_OUTPUT=1` it keeps a PNG per case in the test binary's folder. The class is in `ThemeCollection`
  because it applies a theme, which is process-wide.

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

## The three views

Original, Result (the selected step's stored result) and Comparison (the step's input against its result, through the
viewer's `SplitCompareController`). A view the selected step cannot offer is not offered, and the canvas shows the
original with the words saying why: a step that is not made yet, one that is refused or blocked, one that wrote no pixels
(a finding, the plate solve), one whose results are named by its run (a stack's masters, which has no image of its own),
and a first step, which has no input to compare. An original the viewer cannot draw (a folder of frames, a SER capture)
is described in words. Which pictures a step offers is worked out once with the runner's answer, off the render thread, and
every part of a frame reads that one value.

## What W2 leaves out

- Editing a step's settings: the panel shows them (W6, W12).
- Gates, and a workflow's own editor (W9).
- The other screens: the planetary steps (W3), deep-sky import, inspect and calibrate (W4), register and integrate (W5),
  processing and colour (W6), profiles and batches (W7).
- Packaging and release (W8).
