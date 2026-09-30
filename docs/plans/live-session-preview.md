# Live Session preview: live view and the viewer's measurements (plan)

## Status

**NOT STARTED** (raised by the user 2026-09-30, while testing a Canon EOS 6D through the preview: "we also need a few
more controls in the preview like show stars, HFD, FWHM, and all the other stuff, image stats, also the histogram, not
just a Canon thing really", and a live view in the preview "which is useful beyond just planetary"). Milestone
`live-session-preview` (#1108 to #1113, one a section). Nothing here is Canon's: every camera the node drives previews through the same pane.

## What exists, and is reused

- **The preview pane** (`LiveSessionTab.Preview.cs`) draws a preview frame through `LiveFramePreviewSource` and a toolbar
  of its OWN, a small hand-built subset of the viewer's: Fit, 1:1, stretch mode, stretch preset, boost and the WCS grid
  (once a Solve has placed the frame). Nothing else of the viewer reaches it.
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

**#1108.**

The pane's toolbar and overlays are the viewer's, not a copy that has fallen behind it: stars, histogram, statistics,
channel, debayer and white balance, with the same keys and the same popovers. One widget on every host (the rule the
night calendar and the object panel follow), so the TUI's Sixel pane gets what it can draw. The preview-only pieces
(the OTA buttons, Capture and Save, the grid that waits for a Solve) stay on the pane. **The decision in it**: the pane
becomes an `ImageRendererBase` over the preview source, or the viewer's toolbar and overlay painting move to a piece
both use. The second keeps the pane's chrome its own; the first removes a second toolbar for good.

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
pane and the viewer. P2's star numbers sit above them, and a Solve's result stays where it is.

## P4: live view in the Preview mode

**#1111.**

A live view, for framing, focusing, collimation and checking a flat panel's light, without the Planetary mode's
stacking: continuous frames from any `IVideoCameraDriver`, drawn in the same pane through the same stream the Planetary
mode uses (`FrameStreamWire`, shared memory, the client asking for each frame). Its own node run, claiming the camera,
ending unwatched as the planetary one does (`INodeRun.EndsUnwatched`), with exposure and gain live, and P2's measurements
at a cadence the camera can afford. A Capture from live view takes a still at the settings it shows. Canon Live View and
the fake cameras work now; the DAL cameras once #813 gives them native video. **The decision in it**: a toggle within
the Preview mode, or a third mode beside Preview and Planetary.

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

## Out of scope

Stacking in the preview (that is the Planetary mode's, and a deep-sky live stack is an idea of its own); the Bahtinov analyser; recording the live view to disk (#814 is the Planetary mode's recording).
