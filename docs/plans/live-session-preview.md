# Live Session preview: live view and the viewer's measurements (plan)

## Status

**IN PROGRESS** (P1 done 2026-10-01, P4 done 2026-10-03; raised by the user 2026-09-30, while testing a Canon EOS 6D through the preview: "we also need a few
more controls in the preview like show stars, HFD, FWHM, and all the other stuff, image stats, also the histogram, not
just a Canon thing really", and a live view in the preview "which is useful beyond just planetary"). Milestone
`live-session-preview` (#1108 to #1113, #1124, #1215 to #1220, #1225, #1227 and #681, one a section). Nothing here is Canon's: every camera the node drives previews through the same pane.

## What exists, and is reused

- **The preview pane** is already the viewer's widget: `VkGuiRenderer` hands the Live Session tab a `VkImageRenderer`
  (`LiveSessionTab.PreviewView`, the widget `tianwen-fits` and the planetary view are), configured chromeless
  (`ViewerState.HideChrome`) over a `LiveFramePreviewSource`. In place of the viewer's toolbar the tab draws one of its
  OWN (`LiveSessionTab.RenderMiniViewerToolbar`), a small hand-built subset: Fit, 1:1, its own stretch cycle, stretch
  preset, boost and the WCS grid (once a Solve has placed the frame), under labels of its own. Nothing else of the
  viewer's chrome reaches it.
- **The viewer** (`ImageRendererBase`, `ViewerState`, the GUI's Viewer tab and `tianwen-fits`) already has what the
  preview lacks: the star overlay (`ShowStarOverlay`), the histogram (`ShowHistogram`, `HistogramLogScale`), the
  statistics table (`InfoPanelData.GetStatisticsTable`), channel views, the debayer menu and white balance.
- **Star detection** is `Image.FindStarsAsync` (a `StarList`), and a session already reduces each frame to
  `FrameMetrics` (star count, median HFD, median FWHM) for its drift refocus.
- **The side panel** on the right of the pane is the session's exposure log. In the Preview mode it only says "Preview
  Mode", plus a Solve's result once there is one: an empty column otherwise.
- **Live video** exists in the Planetary mode only: `IVideoCameraDriver`, `LiveCameraFrameStream`, the node's run
  (`PlanetaryCapture`), frames copied at display rate by `FrameSampler` and streamed through `FrameStreamWire` (shared
  memory over this machine's socket), shown by `LiveStackPreviewSource`. Canon Live View streams today; the DAL cameras
  (ZWO, QHY, Player One, ToupTek) are single-frame until native video lands (#813, `live-planetary-capture.md` D).
  `multi-source-previewer.md` already named the follow-up: "a live astro-camera video stream is just another
  `IPreviewSource`".

## P1: the preview pane is the viewer

**#1108.** Designed 2026-10-01 with the user.

The pane's toolbar and overlays become the viewer's, not a copy that has fallen behind it: stars, histogram,
statistics, channel, debayer and white balance, with the same keys and the same popovers. **Everything of the file
viewer is left out**: the file list, Open, A|B, Enhance and the auto-crop (a single frame has no stacking edge). The
preview-only pieces stay on the pane: the OTA buttons, Capture and Stop, the focuser jog, the mount section and its
Save, which writes the node's FITS. The viewer's own Save, the picture as seen, is not offered there, so the pane never
shows two Saves that do different things. **The TUI is not part of this**: its preview draws through
`ConsoleImageRenderer`, not this widget.

Since the pane already is the viewer's widget (see above), the work is not choosing a widget. It is that **switching
the chrome on is not enough**: half the viewer's buttons read an `AstroImageDocument` (Stars, Plate solve, Save,
Channel, Colour calibrate and the statistics table, `ImageRendererBase.Toolbar.cs`, `InfoPanelData.GetStatisticsTable`),
and a preview frame deliberately has none. A document owns a full-resolution copy of its frame and takes its statistics
over every pixel when it is built: per preview frame that is a new 80 MB plane on a 6D (5472 x 3648 floats) and a
full-frame walk, freed only by the collector, where `LiveFramePreviewSource` reuses its planes and samples about a
million pixels. So P1 first softens the viewer's reliance on the document.

### What the viewer reads off the document

The renderer draws an `IPreviewSource` (pixels, display statistics, frames) and reaches everything else with `source
as AstroImageDocument`: about 80 read sites over its partial classes, 37 members of the document. Two places already
work around it: `ImageRendererBase.OverrideWcs` exists only so a source without a document (polar alignment) can have a
WCS, and `SerPreviewSource` and `LiveStackPreviewSource` each build a whole document inside just for its statistics
(#1124). By what each read is for (2026-10-01):

| Group | Reads | Needs the document? |
|---|---|---|
| Geometry, sensor type, metadata (`UnstretchedImage.Width`, `.ChannelCount`, `.ImageMeta`) | ~25 | No: the source has the geometry, the metadata is one property |
| The statistics table (`ChannelStatistics`, the measured backgrounds) | ~6 | No: `ChannelStatistics` is on the source already |
| The WCS | 22 | No: a small fact about the frame |
| Stars, HFR, FWHM | ~16 | No: the same |
| The pixel readout (`ViewerActions.UpdateCursorFromScreenPosition`) | ~4 | No: the source's pixel data serves it |
| The file path | 11 | Only for a file |
| Save as seen, Enhance, the auto-crop | ~8 | Yes: the whole image |
| SPCC colour calibration, the held display anchor, an enhance result | ~23 | Yes: still-image features |

### The model: the picture, what is known about it, and what can be done to it

1. **The picture is `IPreviewSource`, widened a little**: the frame's `ImageMeta`, and its measured backgrounds
   (defaulting to the ones the source has). The geometry, metadata, statistics table and pixel readout then read the
   source.
2. **What is known about the frame is a small immutable record on the source**: the WCS, the star list, and the median
   HFD, FWHM and star count (a `FrameFindings`, or whatever the code names it). It is replaced in one reference write
   when a solve or a detection lands, never edited. A document fills it locally, as it does today; the live preview
   fills it from the node, P2's measurements and the node's solve. `OverrideWcs` goes.
3. **What can be done, and where it runs, is the HOST's**: an interface the renderer is given, which also says which
   actions the host offers. `ViewerController.HandleToolbarAction` already is this for `tianwen-fits` (Open, plate solve,
   Enhance, crop, Save), running all of it locally. The Live Session preview's host solves through the node, draws the
   node's stars (the window detects nothing itself) and offers none of the file viewer's actions; the planetary view's
   offers its narrow set. **The toolbar's buttons are what the host offers**, which replaces the three ways they are
   chosen today: the `ToolbarButtons` override in `VkPlanetaryTab`, the `EnhanceAvailable` flag, and the four toolbars
   composed for sky and Enhance. Hidden still means it can never apply (the host does not offer it); dim still means it
   can apply later (offered, not possible yet).

**The document stays what it is**: the one source that owns a whole image, and the only one whose host offers A|B, the
held display, Enhance, the crop and Save as seen. Those keep reading it, gated by the host's offer, so the preview
never reaches them.

**Not a polymorphic document** (a base class with a light live subclass): the expensive part is the document's own
contract, an adopted full-resolution `Image` and full-frame statistics taken as it is built, and the 46 reads of
`UnstretchedImage` assume a whole `Image` is there, so a light subclass would have to fake one. Turned round, the
document implements the small contracts and nothing pretends to be a document.

### Steps

Each its own commit, and nothing visible changes for a document before the last; `ViewerE2E` (`tianwen-fits` run
through `StandaloneViewerHost` without a window, at DPI 1 and 1.5) and the viewer tests hold steps 1 to 3 to that.
A source that is not a document gains what only needed the picture, as step 1 found.

1. Geometry, metadata, the statistics table and the pixel readout through `IPreviewSource` (**done**, 2026-10-01:
   `ImageMeta`, `SampleAt` and the measured backgrounds on the source, `ViewerActions.ReadPixel`, the toolbar's
   `HasPicture`; pinned by `ViewerReadsTheSourceTests`). **It found the planetary view's toolbar dead**: STF, Link,
   Params, Channel, Tone and Fit asked for a document, which its source never is, so six of its eight buttons were
   dim, and a dim button registers no press. They work on any picture now, and the planetary view and a SER gained
   the pointer's pixel readout. Which sources show the metadata and statistics sections is still a document's
   only, until step 3 makes it the host's.
2. The findings record on the source; `OverrideWcs` removed (**done**, 2026-10-01: `FrameFindings` in Lib, the
   document's `Wcs`, `Stars`, `AverageHFR`, `AverageFWHM` and `IsPlateSolved` cut into its `Findings` in one wave,
   a solve and a detection swapping the record by compare and exchange so neither drops the other's; the live
   source's findings set by its host and dropped with each new frame). A solved live frame's grid is now
   labelled, as a file's is. **The status line said HFR over the median HFD**, a diameter, so it read twice the
   radius it named; it says HFD now.
3. The host's actions and what it offers; the three toolbar mechanisms removed (**done**, 2026-10-01). As built, the
   offer is a VALUE, `ToolbarOffer`: the one canonical table of buttons (order, labels, groups), from which a host
   picks actions (`FileViewer`, `Planetary`, `With`), set as `ImageRendererBase.Offer`. Running stays where it was
   and needed no new interface: the host's `ToolbarPressPolicy` for a press, the signal for a key, and a key for
   an action only a host runs (Open, Save, the crop, the solve, Enhance) acts only where it is offered; Ctrl+O
   unoffered is swallowed rather than falling through to O. Pinned by `ToolbarOfferTests`. The info panel's
   metadata and statistics stay a document's: a SER's statistics are its first frame's, so widening them waits
   for P3.
4. The preview pane switches its chrome on, with its own host: `RenderMiniViewerToolbar` and the pane's own Solve
   button are deleted (**done**, 2026-10-01). The tab is the viewer's host: `ToolbarOffer.LivePreview`, a
   `ToolbarPressPolicy` that runs the solve for the OTA whose frame is on show (`PlateSolvePreviewSignal`) and leaves
   every other press to the viewer (the policy now answers whether it ran a press), and `HostCanRun`, which says when
   that solve can run on a frame that is no document. The viewer is a routed child of the tab, and gets the pointer
   and **the viewer's keys** (the user's call, 2026-10-01: the keys its tooltips name; the preview's own T, S and B
   went), never the window's (Escape, Tab, Space, the arrows, F11). A key does what its button does and only where
   the button is offered (`ActionOfKey`, folded the same day into the viewer's one keyboard declaration,
   `ViewerShortcuts.ButtonFor`), and P, E, Shift+C and Ctrl+O PRESS their buttons, so the host that runs a
   button runs its key: the four signals they posted are gone. The tab's copies of the viewer's pan, zoom and keys
   went with it, the planetary view takes the viewer's keys too, and the GUI hands the preview the planner's
   catalogue, so the objects overlay works on a solved frame. With more than one OTA a picker row stays above the
   viewer. Pinned by `LiveSessionPreviewViewerTests`.

## P2: every preview frame is measured

**#1109.**

Each preview frame, and each live frame at a reduced cadence, is measured once: the detected stars, each with HFD and
FWHM, and the frame's star count, median HFD, median FWHM and eccentricity (`FrameMetrics` widened). **Measured on the
node**, where the frame is, so the TUI, a remote rig's window and the CLI get the same numbers without each running a
star detection of its own, and they cross the wire with the frame (its metadata beside `FrameWire`, or the preview
job's result). The overlay draws the stars; hovering one shows its HFD and FWHM. A frame too dark or too bright to
measure says so rather than showing zeros.

## P3: statistics and the histogram in the side panel

**#1110.**

The empty Preview Mode column carries the frame's readouts: per channel minimum, maximum, mean, median and standard
deviation, the fraction at the white point (a blown frame is visible before it is saved), and the histogram, log scale
by default, per channel for a colour frame. Through the viewer's statistics and histogram, one set of numbers for the
pane and the viewer: after P1's first step the table is `InfoPanelData.GetStatisticsTable` over the source and the
histogram is the viewer's own, so this is where they are shown, not a second measurement. P2's star numbers sit above them, and a Solve's result stays where it is.

## P4: live view in the Preview mode

**#1111, done 2026-10-03.** A live view, for framing, focusing, collimation and checking a flat panel's light, without
the Planetary mode's stacking. **A toggle within the Preview mode, not a mode of its own** (the user, 2026-10-03): a
[Live] button per OTA beside [Capture], since everything a live view needs is the Preview's already (the viewer's toolbar,
the exposure, gain and binning a still takes, the focuser jog, the mount section).

As built:

- **The node's run is the planetary capture loop with nothing kept** (`NodeLiveView`, `PlanetaryCapture` with
  `LiveCaptureKind.LiveView`): no ring of frames, its own name in its refusals and its claim on the camera ("live view"),
  over the WHOLE sensor at the Preview's binning (`PlanetaryCapture.ConfigureRoi` with a zero window). It claims only the
  camera, so the focuser and the mount stay free to move while it runs. `POST`/`GET`/`DELETE /api/v1/live` and
  `PUT /api/v1/live/controls` (exposure, gain), `NodeRunKind.LiveView`; it ends unwatched as the planetary run does.
- **Its frames go as the planetary live frame does**: sampled at display rate into `FrameSources.LiveView` (`live`),
  streamed to the window through `FrameStreamWire` over shared memory, the client asking for each.
- **The GUI** (`LiveViewController`) shows the live frame in the pane while it runs for the OTA on show, sends the
  steppers' changes as they change, and says the camera's rate. **[Capture] ends the live view and takes the still**,
  which the pane then shows: a Canon's Live View and its shutter exclude each other, and a still that went back to live
  at once would hide what it took. A solve and Save act on the still the node keeps, never a live frame, which it keeps
  nowhere, so neither is offered on one.
- **A camera without native video live-views through the short-exposure loop**, which now takes a new exposure from the
  next frame; it used to keep the start's for as long as it ran.

What the Canon EOS 6D measured: a Live View frame is a 960 x 640 JPEG of 176 KB; its USB fetch is 19 to 20 ms; its
decode 63 ms in Debug and 9 to 10 ms in an AOT node (6.2 ms, 8.6 with the widening to floats, in a Release benchmark);
sampling and publishing it 0.4 ms; taking it into the pane 31 ms of a Debug window's render thread. **An AOT node streams
28.7 frames a second, the body's own rate** (14 to 20 polls in 100 found no new frame); a Debug window showed about 15
of them, held by that intake (P14). Two things the body taught:

- **The driver paced Live View by the requested exposure**, clamped to 15 to 500 ms, so a live view asked at a still's
  5 s ran at 1.5 frames a second (8.8 in a Debug node once the pace went). An EVF has no integration time; the driver
  polls at the body's rate and backs off only when no frame is ready.
- **A Canon's Live View brightness is its exposure SIMULATION of the shutter speed set**, which the last still left
  behind: a 1 s still turned a dim live view blown. The live view now sets the shutter speed a still of the Preview's
  exposure would take (`ClosestTv`, at most 30 s), at its start and as the stepper moves, so the stepper brightens and
  dims it as it would the still.
- **A rendered frame is display data, and Auto stretched it as linear**: unlinked, a curve per channel fitted to a
  frame whose red sat at full scale, turned it into a two-level red channel and a teal picture. The file viewer already
  opened such a frame with the stretch off (`Image.DetectPreStretched`, 8 bits settle it); the live pane never asked. A
  pre-stretched live frame (`IPreviewSource.IsPreStretched`) now opens with the stretch off, the still's stretch comes
  back with the still, and STF on stretches it Linked (`ChannelsAlreadyAgree`: the camera balanced its channels), so a
  dark night's live view can still be lifted without a cast.

- **The battery died mid live view**, which said so nowhere a user looks: the failure reached only the watching
  window's status row and the node's log, and a disconnect of the gone body showed a raw COM error. A run that faults
  now says so in the node's notifications (`NodeRuns.NoteFault`, the planetary capture too), and a disconnect of a body
  that has gone succeeds. Noticing a device that drops off with no run going on is #1224.

Measurements at the camera's rate (P2) wait for P2.

Left for later: taking a live frame in off the render thread ([P14](#p14-a-live-frame-is-taken-in-off-the-render-thread)),
the camera's own JPEG for a remote live view ([P13](#p13-a-remote-live-view-sends-the-cameras-own-jpeg)), and a Canon's
lens focused while live ([P12](#p12-a-canon-lens-focused-while-live)).

## P5: a focus aid across frames

**#1112.**

While focusing by hand, the pane plots median HFD and FWHM over the last frames (previews, or live frames at P2's
cadence), with the best so far marked and the current frame's value large enough to read at the eyepiece distance: a
manual focus curve. The same numbers the session's drift refocus reads, so a focus judged by hand and one judged by
the autofocus agree. A Bahtinov mask analyser is a separate idea, not in this plan.

## P6: the frames taken, in the side panel

**#1113.**

The last previews, newest first, each with its exposure, gain or ISO, time, median and P2's HFD, FWHM and star count;
selecting one shows it again (the node keeps the latest few, as it keeps the latest one today). What makes "was the last
one better" answerable without saving every frame to compare.

## P7: the SER and live-stack sources stop building a document for statistics

**#1124.**

`SerPreviewSource` builds an `AstroImageDocument` of frame 0 (`_statsFrame`) and `LiveStackPreviewSource` one of each
published master (`_doc`), each for its stretch statistics, histograms and backgrounds: a full-resolution copy and a
full-frame walk apiece. Once P1's first step has statistics standing on their own, through the collectors
`LiveFramePreviewSource` already calls (`StretchSolver.CollectPerChannelStats`, `StretchSolver.CollectChannelHistograms`),
both can take them without a document. **Not yet checked**: how much else `LiveStackPreviewSource` relies on its
document for. After P1.

## The Preview mode as N.I.N.A.'s imaging tab

Raised by the user 2026-10-03: "make preview a lot more like session, with focusing, and what not, just like NINA really
when not in sequence mode". N.I.N.A.'s imaging tab, outside its sequencer, is a camera loop with every device's panel
beside it: focuser and autofocus, filter wheel, mount and plate solve, guider, the HFR history and image statistics. The
Preview mode is that tab here. What it has, and where the rest is planned:

| N.I.N.A. | The Preview mode |
|---|---|
| Take one exposure, loop exposures | [Capture]; [Live] (P4), the short-exposure loop where a camera has no video |
| Image statistics, histogram, HFR history, image history | P3, P2, P5, P6 |
| Focuser: move, autofocus run | Jog and goto today; an autofocus run, [P8](#p8-autofocus-on-demand) |
| Filter wheel | Its name today; a change, [P11](#p11-change-the-filter) |
| Telescope: slew, plate solve, centre | The mount's position and a solve today; slew and centre, [P10](#p10-slew-to-a-target-and-centre-it) |
| Guider: start, stop, calibrate, graph | The Guider tab's graph only; [P9](#p9-guiding-outside-a-session) |

## P8: autofocus on demand

**#1215.** An autofocus run from the Preview mode, the session's own routine (`Session.AutoFocusAllTelescopesAsync`, the hyperbola
fit and the backlash estimate) as the node's run, claiming the OTA's camera and focuser, with the curve drawn as it is
taken and the result kept as the session keeps it. The session's routine must not get a second copy.

## P9: guiding outside a session

**#1216.** Start, stop and calibrate the guider from the Preview mode, as the node's run or job, the Guider tab drawing what it
does. The session's calibration rules hold (`CalibrateGuiderAsync`: east of the meridian, the pier side kept), so a
calibration made here is one a session would make.

## P10: slew to a target and centre it

**#1217.** Slew to a target (the planner's, the sky map's, a typed name) and centre it by solving and re-slewing, the session's own
`CenterOnTargetAsync`, as the node's job on the mount, the solve through the OTA's camera.

## P11: change the filter

**#1218.** The filter wheel's row in the Preview mode becomes a choice, through the node's `/devices/filterwheel/change`, which
exists; the column shows only the filter's name today.

## P12: a Canon lens focused while live

**#681; the first step done 2026-10-03.** The Preview's Lens row (Near and Far, three sizes each, as one to three carets) shows while a camera that drives its own lens is live (`ILensFocusCamera`, `LiveViewStateDto.CanDriveLens`), each step sent at once (`PUT /api/v1/live/controls` with `LensStep`) and taken between two frames. Verified on a 6D: three large Near steps moved focus from the room's back wall to the foreground. A Canon body drives its lens (FC.SDK's `DriveLensAsync`, Near and Far in three step sizes) only while Live View runs, which
P4 gives: Near and Far buttons on the focuser row while a Canon's live view runs, for focusing by eye or on a Bahtinov
mask. As a focuser for autofocus (#681) it needs a position the lens never reports: a made-up one from a nominal start,
moved only in the smallest step so its units stay one size (the larger steps have no known ratio to it), and Live View
raised around each move when none runs. Backlash in the lens motor is unmeasured.

## P13: a remote live view sends the camera's own JPEG

**#1219.** A live frame crosses as floats (`FrameWire`): 7 MB for a Canon's 960 x 640 frame, which arrived as a 176 KB JPEG. Over
this machine's shared memory that is a copy; over TCP to a remote rig, at 30 frames a second, it is more than any LAN
carries. A frame that came as JPEG goes as that JPEG (motion JPEG in spirit: each frame on its own, never a codec that
predicts across frames, which smears the faint stars a focus is judged by), a third `FrameSampleFormat` a client asks
for, decoded off its render thread.

## P14: a live frame is taken in off the render thread

**#1220.** `LiveFramePreviewSource.AcceptFrame` normalises a frame and takes its statistics on the render thread: 31 ms for a
960 x 640 colour frame in a Debug build, every frame at a live view's rate. It is the planetary live frame's path too.
Done on the reading task and handed over ready, the render thread only uploads.

## P15: one exposure control in two stages

**#1225.** One exposure control for the Preview mode and the Planetary mode, in two stages as SharpCap's is: a range first
(a button group, `ms | s`), then the value within it (a log slider, and the ± stepper for exact 1-2-5 steps). Today there
are two hand-written ladders, the Preview's (`LiveSessionActions.PreviewExposureSteps`, reaching 1 ms since P4 took it
below 0.1 s for the live view) and the Planetary panel's (`VkPlanetaryTab.ExposurePresetsMs`, 0.5 to 500 ms); one
slider across 1 ms to 300 s spans five and a half decades, which is why the range comes first.

## P16: a magnified live view and its jog

**#1227.** The live view at 1x, 5x or 10x, the magnified window jogged around the frame: what focusing on a star needs,
with the Lens row (P12) moving the focus while it is watched. A Canon offers three Live View crops
(`CanonCameraDriver.ZoomForWindow`: a threshold, 4.96x for 5x, about a second to apply while pre-zoom frames keep coming)
and pans a magnified one (`JogRoiAsync`, `CanJogRoi`); the planetary capture drives both already, the live view asks for
the whole sensor. A Zoom choice on the Live row sent as a window size, jog arrows (or a drag on the picture) sent as
`JogX`/`JogY`, and an inset marking where the crop sits; a camera with native video takes the same as a readout window.
**What the zoom buys is resolution, not only size**: at 1x a 6D's ~960 x 640 Live View frame averages about 5.7 x 5.7 sensor pixels into each of its own, so a star 2 to 3 pixels across is under one; at 5x the body sends a 1104 x 736 sensor crop as the frame, about one sensor pixel each (native); 10x upsamples that crop about 2x, larger but with no more detail. The JPEG's compression stays at every zoom.

## Out of scope

Stacking in the preview (that is the Planetary mode's, and a deep-sky live stack is an idea of its own); the Bahtinov analyser; recording the live view to disk (#814 is the Planetary mode's recording).
