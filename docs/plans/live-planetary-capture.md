# Live Planetary Capture (Phase 12 of planetary-stacking)

Capture from a **live camera in video / streaming mode**, feed the existing rolling-window lucky-imaging
stacker, preview it in a dedicated **🪐 tab**, and hold the planet centred with a **Centre-of-Mass
recenter loop** (fast ROI jog, with mount jog as the coarse opt-in fallback). Vendor-neutral: ZWO, QHY,
Canon and any other `ICameraDriver` are all in scope.

Everything **above** `IPlanetaryFrameStream` is reused unmodified -- the seam was designed for this
(`planetary-stacking.md` Phase 1). This adds a capture contract below the seam, a push frame-stream at the
seam, a controller + tab on top, and a recenter controller.

## Layering

```
  GUI 🪐 PlanetaryTab : ImageRendererBase        (reuse RAW/STACK + wavelet sliders + GPU stretch/WB)
        | driven by
  PlanetaryCaptureController (UI.Abstractions)    Tick() (follow latest + publish + sharpen)
        | owns
  PlanetaryCapture (Lib)                           capture loop, live controls, recenter (the node runs it too)
        | fills
  LiveStackPreviewSource  (EXISTS)  : IPreviewSource
        | wraps
  RollingWindowStacker    (EXISTS - pull-only via LoadAsync)
        | pulls frames from
  IPlanetaryFrameStream   (EXISTS - THE seam)
        ^
  LiveCameraFrameStream   - growing ring buffer; copies pushed frames, shares on load
        | enumerates
  IVideoCameraDriver : ICameraDriver   CanVideoCapture / CaptureVideoAsync (IAsyncEnumerable<Image>) /
        |                              CanJogRoi / JogRoiAsync / DroppedFrames
   +----+---------------------------+----------------------------+
   |                                |                            |
 DALCameraDriver            CanonCameraDriver           (any ICameraDriver)
 (ZWO + QHY native raw)     (FC.SDK Live View, JPEG)    rapid-exposure fallback
 [Phase D]                  [Phase E]                   = PlanetaryCaptureController.RapidExposureFramesAsync
                                                          (a helper, NOT a wrapper class)

  PlanetaryRecenterController [Phase C]: PlanetaryDisk.CenterOfMass -> pixel offset ->
     ROI jog (auto, capability-gated)  ->  mount jog (opt-in, coarse fallback near sensor edge)
```

## Decisions

- **Recenter = ROI auto, mount opt-in.** ROI jog (shift the readout window) auto-engages -- instant, zero
  mount disturbance. Mount jog stays behind an explicit toggle + deadband and fires only as the coarse
  fallback near the sensor edge (or when the camera can't ROI-jog).
- **One vendor-neutral contract: `IVideoCameraDriver : ICameraDriver`.** Start/yield/stop are folded into
  a single `CaptureVideoAsync(opts, ct) -> IAsyncEnumerable<Image>` (cancel = stop; no illegal
  "started-but-not-draining" state).
- **Rapid-exposure fallback is a helper, not a `~60-member` wrapper class.** The capture controller picks
  the native video path (`camera is IVideoCameraDriver`) or falls back to a `StartExposureAsync` ->
  `GetImageReadyAsync` -> `GetImageAsync` loop. Avoids the `ICameraDriver` passthrough boilerplate.
- **Fake-first vertical slice.** Built + tested against `FakeCameraDriver` (synthetic drifting planet +
  software ROI) so the whole loop is CI / inspector testable with zero external SDK releases.

## Phases

| Phase | Scope | SDK/DAL release? | Status |
|---|---|:--:|:--:|
| A | `IVideoCameraDriver` + `LiveCameraFrameStream` + engine gaps (non-owning stream, follow-latest, empty-stream guard) | no | DONE 2026-06-24 |
| B | rapid-exposure fallback + Fake video (`SyntheticPlanetRenderer`) + `PlanetaryCaptureController` + 🪐 tab + signals | no | **DONE**. The GUI shipped as the Live Session **Planetary mode** (`LiveSessionMode.Planetary`), not a tab, as decided on 2026-06-24 (below); this row said "GUI tab remaining" until 2026-09-25 |
| C | `PlanetaryRecenterController` (ROI auto + mount opt-in) + `JogMountSignal` + manual nudge | no | DONE 2026-06-28 |
| D | native ZWO + QHY raw video (DAL `ICMOSNativeInterface` + ring buffer + ROI-jog bypass) | **yes (DAL -> 2 SDKs -> TianWen)** | **DONE for ZWO and ToupTek** ("feat(devices): a DAL camera streams native video (#813)": one shared loop, `DALCameraDriver.Video.cs`, 92 frames a second at 640x360 on an ASI462MC; ToupTek since ToupTek.SDK 1.2, #1596: 196.8 frames a second at 640x480 on a G3M678M, read off a recording's SER, a stalled stream brought back by a restart, a reopen or a reset, ["Video at the Saturn window on the ToupTek"](#video-at-the-saturn-window-on-the-touptek-measured-2026-10-15)). **QHY and Player One do not stream yet**: their SDKs implement none of the DAL's video members, so they run the single-exposure loop, and #813 stays open for them. Detailed plan: [planetary-native-video.md](planetary-native-video.md) |
| E | Canon Live View `IVideoCameraDriver` (JPEG) via FC.SDK | core: no / zoom-pan: yes | **core DONE 2026-07-16** (full-frame stream, `CanJogRoi=false`); **EVF zoom + pan DONE 2026-08-03** on FC.SDK 3.0.751 ("feat(canon): the magnified EVF crop is the planetary ROI, and it pans"; E.3 in the detail doc, where this row had said deferred). Detail: [planetary-native-video.md](planetary-native-video.md) |

> **Phases D + E are fleshed out in [`planetary-native-video.md`](planetary-native-video.md)** (the two
> real-hardware phases behind the same `IVideoCameraDriver` contract; grounded in the actual sibling-SDK
> symbol inventory). Recommended order there is **E before D** (E is a single low-risk in-tree commit against
> the already-published FC.SDK Live View; D is the High-risk DAL -> 2 SDKs -> TianWen NuGet chain).
| F | decompose the ~3000-line `ImageRendererBase` (see below) | no | DONE 2026-06-28 |
| G | a mono night: a Record control in the GUI (#1344), then a filter-wheel sequence as a node run (#1340) | no | **PARTIAL**: the Record control DONE 2026-10-16 (#1344); the sequence (#1340) NOT STARTED ([Phase G](#phase-g-a-mono-filter-wheel-sequence)) |
| H | focusing on the planet: a burst's sharpness, a readout for focusing by hand, an autofocus, per-filter offsets (#1341) | no | NOT STARTED ([Phase H](#phase-h-focusing-on-the-planet)) |
| I | a recording writes its camera settings beside it (#1342) | no | NOT STARTED ([Phase I](#phase-i-a-recording-says-how-it-was-taken)) |

## Phase F: decompose `ImageRendererBase` (~3000 lines)

`ImageRendererBase<TSurface>` has grown into one file covering layout, toolbar + dropdowns, file list,
WCS grid labels, star/object/annotation overlays, histogram, info panel (+ WB & wavelet sliders),
status bar, SER transport bar, and all keyboard/mouse handling. It's already organised by `// ----`
region banners, so the lowest-risk decomposition is **`partial class` files by concern** (no behaviour
change, pure file organisation):

- `ImageRendererBase.Layout.cs`: `ComputeLayout` / `ComputeImagePlacement` / `ViewerLayout` / content region
- `ImageRendererBase.Toolbar.cs`: toolbar + dropdown overlays + hit-test
- `ImageRendererBase.FileList.cs`: file-list sidebar + hit-test
- `ImageRendererBase.Overlays.cs`: grid labels + star/object/WCS-annotation overlays
- `ImageRendererBase.Histogram.cs`: histogram rect + render + LOG button
- `ImageRendererBase.InfoPanel.cs`: info panel + WB sliders + wavelet sliders
- `ImageRendererBase.StatusBar.cs`: status bar
- `ImageRendererBase.Transport.cs`: SER transport bar + scrub
- `ImageRendererBase.Input.cs`: keyboard + mouse handlers
- `ImageRendererBase.cs`: the abstract GPU seam + `Render` orchestration + shared fields

**Real dedup (not just splitting):** the WB sliders, the 6 wavelet-layer sliders, and the transport scrub
are all the same widget; a horizontal **press / drag / release track** with a fill + handle + a
cursor-X -> value mapping against a captured track rect. Extract a single `TrackSlider` helper (render +
`BeginDragAt`/`UpdateDrag` against a `RectF32`) and have all three call it. That removes the triplicated
slider math, not just the file length. **`magic` (source generators) is NOT warranted**; there's no
repetitive generated surface here; partials + one extracted widget is the right tool.

### Phase F DONE (2026-06-28)

`ImageRendererBase<TSurface>` (was one 3,149-line file) is now a `partial class` across 11 files, organised
by concern -- pure file organisation, **no behaviour change**:

- `ImageRendererBase.cs` -- abstract GPU seam + `Render` orchestration + shared fields/constants/colours +
  the `DrawText`/`MeasureText`/`DrawWrappedTextLine` text helpers + `UploadDocumentTextures` + `Resize`.
- `.Layout.cs` -- the single-source `ComputeLayout` / `ComputeImagePlacement` / `ConfineToViewport`, the
  `ViewerLayout`/`ImagePlacement` records, the `_layout`/`_placement` fields, content-region seam.
- `.Toolbar.cs` -- toolbar render + the shared dropdown overlays + button enable/active/label + hit-test +
  the `DefaultToolbarButtons` set.
- `.FileList.cs` / `.Overlays.cs` (grid labels + star + object + WCS-annotation) / `.Histogram.cs` /
  `.InfoPanel.cs` (info panel + WB + wavelet sliders) / `.StatusBar.cs` / `.Transport.cs` (SER scrub) /
  `.Input.cs` (all keyboard + mouse handlers).
- `.TrackSlider.cs` -- the extracted dedup. `DrawTrackSlider(trackX, trackW, barCenterY, handleY, handleH,
  frac, fillColor, hitBand, hit)` + `static TrackFrac(RectF32, px)` are the one render + one drag-math
  primitive; the WB sliders, the 6 wavelet sliders, and the transport scrub all call them (geometry is
  byte-identical to the old triplicated sites; only the fill colour + per-frame fraction vary).

Done as an exact line-range split (verbatim byte copies, member-line count reconciled) + a
behaviour-preserving helper extraction. Full solution builds 0 warnings / 0 errors; the FITS viewer (the
`VkImageRenderer` concretion of this class) renders unchanged live (toolbar / file list / image pane /
chrome all correct).

A+B+C = a complete, demonstrable 🪐 live-stacking tab with recenter on the Fake camera (no external
releases). D and E are hardware/quality upgrades behind the same contract.

## What's done (backend)

- `TianWen.Lib/Devices/IVideoCameraDriver.cs` -- the contract + `VideoCaptureOptions`.
- `TianWen.Lib/Imaging/Planetary/LiveCameraFrameStream.cs` -- bounded ring, copy-on-push, share-on-load
  (the stacker's `Release()` is a no-op on bufferless images), thread-safe.
- `TianWen.Lib/Devices/Fake/SyntheticPlanetRenderer.cs` -- limb-darkened banded disk + oval spot +
  per-frame seeing blur (lucky-imaging grading) + noise; deterministic.
- `FakeCameraDriver : IVideoCameraDriver` -- streams a drifting synthetic planet into a software ROI window
  over the virtual sensor; `JogRoiAsync` pans the readout (the COM-recenter actuator), `DroppedFrames`.
- `TianWen.Lib/Imaging/Planetary/PlanetaryCapture.cs` -- the camera capture loop + the frame stream, the live
  controls and the recenter; native video or rapid-exposure fallback, picked by capability. Lifted out of the
  controller for P5 part 5 of `hardware-in-the-server.md` (#934), so the node runs the same loop.
  Its start is two halves, `TryPrepare` (camera, claim, window, a refusal in words) and `StartPrepared` (the loop,
  on the token it is given); `TryStart` is both, for the GUI. A new window size is snapped to the camera's rule in
  the loop, for every host.
- `TianWen.Hosting/NodePlanetary.cs` -- the node's run (`/api/v1/planetary`, P5 part 5b): the same capture, the
  rolling stack on the run's own task (`RollingWindowStacker`, the master published linear), and the live frame
  copied at display rate by `FrameSampler` (Lib), both served as frames (`planetary/live`, `planetary/master`).
  It stops once no client has watched it for the detach grace.
- `TianWen.Hosting.Contracts/Api/FrameStreamWire.cs` -- both frames as streams (P5 part 5c): a WebSocket per source,
  the client asking for each frame and the node answering with the newest, so a slow view skips frames instead of
  falling behind; `NodeTransport.OpenFrameStreamAsync` on the client. The live frame is dated when it arrived.
- `TianWen.Lib/Imaging/Planetary/SerRecording.cs` -- a recording to disk (P5 part 5d): `PlanetaryCapture.TryStartRecording`
  writes every frame for a duration into a SER file (`PlanetaryCapture.RecordingPath`, `Planetary/<date>/` beside the
  snapshots), in the camera's own shape and the depth it streams (8 bits for an 8-bit stream, else 16) with each frame's
  arrival time; the loop converts and queues, a writer task of its own does the disk (a frame the disk cannot take is
  dropped and counted); a frame of another size ends it. The node's `/api/v1/planetary/record` finishes its duration
  unwatched. **The GUI's Planetary mode asks for one with its Record control** (#1344, Phase G: `PlanetaryRecordControl`,
  `StartPlanetaryRecordingSignal`); the CLI does not yet. #814's memory-mapped SER, the recording and the frame ring in one,
  is the optimisation still ahead.
- `TianWen.UI.Abstractions/PlanetaryCaptureController.cs` -- owns a `PlanetaryCapture`; `Tick()` (render
  thread) follows latest + publishes the master + pushes wavelet-sharpen changes.
- `ViewerState.BuildWaveletOptions()` -- the single source for live-stack wavelet options, now shared by
  `ViewerController` (tianwen-fits) and `PlanetaryCaptureController` (GUI).
- Tests: `FakeCameraVideoTests` (renderer determinism/brightness/sharpness; fake video frame shape, ROI
  jog pans the planet, end-to-end into the rolling stacker), `PlanetaryCaptureControllerTests`
  (capture -> live stack -> master end to end; Start idempotency / Stop safety), plus the Phase A
  `LiveCameraFrameStreamTests`.

## GUI tab (Phase B): what's wired vs the decided design

**Wired + working (2026-06-24):** `GuiTab.Planetary` (emoji `\U0001FA90`) + `TabOrder` + `TabChrome` +
`Ctrl+Y`; `RenderContent`/`ActiveTab` dispatch; `StartVideoCaptureSignal`/`StopVideoCaptureSignal` ->
`AppSignalHandler` -> the controller (`PlanetaryCaptureActions.ConfigureRoi`); DI registration; DEBUG
inspector signal factories (`StartVideoCapture`/`StopVideoCapture`). **Capture + stack confirmed live in
the GUI** (frames flowing, fps/frame/dropped readout). A first-cut display used `VkMiniViewerWidget`
(rect-bounded); REPLACED by the decision below.

**Critical fix during bring-up:** the stream layout must come from the ACTUAL first frame, not the
camera's `SensorType`. A colour sensor whose video frames are mono (the fake; a native colour stream is a
1-channel Bayer mosaic) was sizing the stream as RGB and dropping every frame ("nothing shows up").
Pinned by `Color_sensor_with_mono_video_frames_still_stacks`. Exposure is floored to 1 ms.

### Decided design (user, 2026-06-24): the planetary tab and the FITS viewer SHARE the viewer widget

The 🪐 tab must reuse the **real** `ImageRendererBase` viewer (same widget the FITS viewer uses), not a
stripped mini viewer, so it gets the full stretch pipeline + RAW/STACK toggle + 6 wavelet-sharpen
sliders + WB + histogram + zoom/pan "for free", and the two surfaces can't drift. The blocker: the shared
viewer hard-anchors its layout to the full window (`tianwen-fits` is full-screen), so it must learn to
render at a **content-rect origin** to embed in the GUI content area (below the sidebar + top status bar).

Remaining steps (in order):

1. **DONE + verified (2026-06-24): common-model layout in `ImageRendererBase`.** Rather than threading an
   origin offset through every hand-placed chrome site (the `cursor += h` anti-pattern the Layout DSL exists
   to kill), the widget now arranges its **whole** region, toolbar + content + status bar, as ONE
   `Layout.Builder.Dock(middle, Top(Fill "toolbar", BaseToolbarHeight), Bottom(Fill "statusBar",
   BaseStatusBarHeight))` rooted at a **content rect** (`SetContentRegion(RectF32)`; default empty = full
   surface). Every chrome rect (toolbar/fileList/image/infoPanel/statusBar) is read from the arranged tree;
   no `FillRect(0,0,Width,…)` remains. The GPU image/histogram quads keep projecting over the full surface
   (projW/projH = `Width`/`Height`). So embedding == "arrange at a different root rect," exactly how every GUI
   tab already arranges within the rect `VkGuiRenderer.GetContentArea()` hands it. `tianwen-fits` reduces to
   identical geometry (region = full surface → `Dock` carves toolbar/status = the old `below` band) and was
   confirmed visually unchanged on a SER sequence (toolbar/filelist/infopanel/histogram/transport all correct).
2. **DONE + verified (2026-06-24): `VkPlanetaryTab` now IS the shared viewer + a capture strip.** Instead of
   a third copy of the GPU delegation, `VkPlanetaryTab : VkImageRenderer` (the shared `TianWen.UI.Shared`
   concretion `tianwen-fits` uses, now unsealed). Its `RenderTab(controller, state, contentRect, dpi, font)`:
   syncs the surface size (full-surface GPU projection), one-time sets `ShowStacked`/`ShowInfoPanel=true` +
   `ShowFileList=false`, `SetContentRegion(contentRect minus the strip)`, `Tick()` + upload + `Render(Source,
   state)`, then paints the capture strip on top (registered AFTER the base's `BeginFrame` so its clickable
   survives). The dead, duplicate `VkViewerTab` (no `GuiTab.Viewer`, zero consumers) and the old mini-viewer
   `PlanetaryCaptureTab<TSurface>` were deleted. Verified live: the embedded viewer chrome + capture strip
   render correctly in the GUI content area.
   - **Input-model bridge.** The GUI's `HandleMouseDown` pre-dispatches via `HitTestAndDispatch` + per-region
     `OnClick` and short-circuits on a hit; the viewer's toolbar buttons + WB/wavelet/transport sliders carry
     no `OnClick` (they need the press X/Y) and dispatch inside `HandleViewerMouseDown`. New marker
     `ISelfDispatchingInputWidget` (implemented by `ImageRendererBase`) makes `GuiEventHandlerBase` route the
     raw press straight to the tab's `HandleInput` (chrome still gets first crack). This is why `VkViewerTab`
     was never wireable before. (`MouseMove`/`Up`/`Wheel` already routed to `HandleInput`.)
   - **Toolbar curation (subclass-controlled).** The button list is now `protected virtual ImmutableArray<…>
     ToolbarButtons` on the base (render + hit-test both read it; replaced 2026-10-01 by the host's
     `ToolbarOffer`, step 3 of P1 in `live-session-preview.md`). `VkPlanetaryTab` overrides it to the
     planetary-relevant subset (STF/Link/Params/Channel/Debayer/HDR/Fit/1:1), HIDING Open/Boost + the whole
     astrometry/star group (Plate Solve/Grid/Objects/Stars/Calibrate/NeutBg/SPCC) -- a disk has no stars,
     nothing to plate-solve, no SPCC.
   - **Remaining for full step-2 verification:** start a capture against a connected fake camera and confirm
     the stacked planet renders through the stretch pipeline (no white-out) + the wavelet sliders appear in
     the info panel (needs equipment connected first).
3. **DONE + verified (2026-06-24): capture control strip.** `[●] [Start/Stop]  Exp -[10 ms]+  Gain -[100]+
   ROI -[640x320]+`, with a right-aligned `fps / frames / dropped` readout while capturing. Exposure +
   ROI step through preset lists, gain steps +/-10; the steppers are **disabled during capture** (changing
   them mid-run is a no-op until restart, so Stop to reconfigure). The settings are tab fields posted on
   Start via `StartVideoCaptureSignal(ExposureMs, Gain, RoiWidth, RoiHeight)`. Built as OnClick steppers
   (NOT text inputs): the planetary tab is an `ISelfDispatchingInputWidget`, so the GUI routes the press to
   the viewer's `HandleViewerMouseDown` -> `HitTestAndDispatch`, which fires per-region OnClicks; a text
   input would need the GUI's focus path that the self-dispatch route bypasses. Verified live (Exp 10->20,
   ROI 640x320->640x480 via the inspector). Note the default ROI is rectangular 640x320 and the strip posts
   explicit values (the old `new StartVideoCaptureSignal()` relied on record-struct defaults that don't apply
   -- see [[record-struct-default-ctor-gotcha]]). Camera-select is deferred (uses OTA[0] for now).
4. **Planet/moon GOTO + Solve & Center** (lean): pick a planet/moon -> slew the mount to its current
   ephemeris position. Reuse the sky-map GOTO/slew plumbing + ephemerides (VSOP87/Moon). Picker style TBD
   (body-button row is the cleanest fit for the self-dispatch input model; dropdown/search have z-order /
   text-focus friction).
   - **Solve & Center** (planets are small + the scope won't be spot-on): a separate capture-panel action
     that takes its OWN longer star exposure (the live planetary frame has no stars to solve, which is why
     Plate Solve is off the planetary *toolbar*). Reuses the polar-alignment machinery verbatim:
     `AdaptiveExposureRamp.ProbeAsync(captureSource, solver, ramp, minStars, ...)` walks an exposure ramp
     (default 100 ms -> 5 s; use a planetary ramp ~1-10 s for the narrow long-FL FOV) until a solve wins,
     returning a `CaptureAndSolveResult` with WCS. Wrap the capture camera in an `ICaptureSource` (as
     `MainCameraCaptureSource` does). Flow: GOTO rough-slew -> full-frame + ramp solve -> offset from the
     planet's current ephemeris RA/Dec -> slew to centre -> restore planetary ROI + short exposure -> resume
     video. Gated on a connected mount + a plate solver. GOTO is the coarse step; Solve & Center is the fine.

### DECIDED (user, 2026-06-24): planetary becomes a Live Session *mode*, not a standalone tab

Confirmed after mapping the Live Session screen. There is already a precedent: **polar align is itself a Live
Session mode**, picked from a "mode pill" dropdown. Planetary slots in as the third mode, reusing the focuser
jog / mount / plate-solve / preview that already live there ("we also need the focuser etc"). Structure found:

- `LiveSessionMode { Preview=0, Session=1, PolarAlign=2 }` (`LiveSessionMode.cs`); add **`Planetary = 3`**.
- Mode-pill dropdown `["Preview","Polar Align"]` at `LiveSessionTab.cs:582-584`; add **"Planetary"**. The
  `onSelect` (lines 585-635) has DELICATE polar teardown (CancelPolarAlignmentSignal / CTS / PolarPhase) --
  generalise it to an idx->target-mode transition carefully; leaving PolarAlign must still tear the routine
  down, and the Cancel signal flips Mode back to Preview asynchronously (watch the polar->planetary direct
  switch for a mode race -- simplest: only allow planetary from non-polar, or set mode after the handler).
- Focuser jog already exists in the Live Session OTA panel (`«‹›»` -> `JogFocuserSignal`, `LiveSessionTab.cs:
  1488-1551`) -- planetary mode reuses it as-is.
- **Center viewer -- consolidation candidate (user, 2026-06-24): do we need BOTH a mini viewer and the full
  viewer?** Live Session uses `VkMiniViewerWidget` (`IMiniViewerWidget`); planetary needs the full
  `VkImageRenderer` (stretch + RAW/STACK + wavelet). But the full viewer is now configurable *down* --
  content-rect embedding + overridable `ToolbarButtons` (hide all) + `ShowInfoPanel/ShowFileList/ShowHistogram`
  off + the `WcsAnnotation` overlay system polar already needs. So the mini viewer "is the full viewer with
  fewer options". **Preferred direction:** instead of injecting a separate `IPlanetaryViewWidget` seam, host
  ONE configurable viewer in Live Session whose options change per mode (Preview = bare image; PolarAlign = +
  pole/ring overlay; Planetary = full + capture). Replaces `VkMiniViewerWidget` at its 3 consumers (Live
  Session, Guider, polar). **Verify before committing** (decides seam-vs-consolidate): (1) a *lightweight*
  `Image`->`IPreviewSource` adapter for live raw frames (no per-frame star detect / heavy `AdoptImageAsync`)
  -- the mini viewer takes `QueueImage(Image)`, the full viewer takes `IPreviewSource`+`UploadDocumentTextures`;
  (2) the Guider's reticle/overlay maps onto `WcsAnnotation`/overlays; (3) per-frame layout-pass cost is
  negligible with chrome off for a fast guide-cam preview. Related to Phase F (viewer decomposition).
  If consolidation is clean, the migration's center step becomes "configure the one viewer per mode" (no new
  seam); if not, fall back to the injected `IPlanetaryViewWidget` seam.

  **VERDICT (assessed 2026-06-24): feasible, MEDIUM effort -- the mini viewer is NOT just "fewer options".**
  It has 3 non-cosmetic behaviours the full viewer lacks: (1) **subsampled stretch stats on the render thread**
  (strided median/MAD ~1M samples -> 30 fps on a 61 MP sensor; full viewer does full-frame async
  `ComputeStretchStatsAsync`); (2) **`FreezeStretchStats`** -- a one-shot stat lock that is a *polar-align
  correctness* requirement (stops the histogram re-firing on every 5 s exposure), no `ViewerState` equivalent
  today; (3) **chromeless layout** -- the full viewer ALWAYS runs `ComputeLayout`/`PaintLayout` + draws toolbar
  (~40 px) + status bar (~24 px); no `HideChrome` flag. Plus there is NO lightweight `Image`->`IPreviewSource`
  (`AdoptImageAsync` runs full stats + `ScaleFloatValuesToUnitInPlace`, i.e. consumes/mutates the source).
  Good: the **guider reticle + polar overlays are drawn by the consumers** (GuiderTab / LiveSessionTab) OUTSIDE
  the widget, so they survive a swap unchanged.
  - **Clean path to one viewer:** add (a) a minimal `LiveFramePreviewSource : IPreviewSource` keeping the
    subsampled-stats trick, (b) `FreezeStretchStats` on `ViewerState` + a per-frame skip in
    `UploadDocumentTextures`/`ComputeStretchUniforms`, (c) a `HideChrome` flag in `ComputeLayout` dropping the
    toolbar/status rows -> then the full viewer is a strict superset and `VkMiniViewerWidget` is deletable.
  - **DECIDED sequencing (de-risk):** do NOT entangle consolidation with the planetary migration. Planetary
    migration now uses the full viewer for planetary mode (it needs stretch/wavelet/RAW-STACK) via the seam;
    preview/polar/guider keep the mini viewer. Consolidation is a SEPARATE follow-up pass (the 3 capabilities
    above -> swap the 3 mini-viewer consumers -> delete it), with `FreezeStretchStats`/polar correctness the
    careful bit. Tracked alongside Phase F (viewer decomposition).

  **DONE (2026-06-29): one viewer, mini viewer deleted.** `VkMiniViewerWidget` + `IMiniViewerWidget` +
  `MiniViewerState` are removed; the Live Session preview, polar-align, and guide-cam all host the shared
  full viewer (`ImageRendererBase`/`VkImageRenderer`) configured chromeless. The three capabilities the mini
  viewer had that the full viewer lacked are now first-class on the full viewer:
  - `LiveFramePreviewSource : IPreviewSource` (`TianWen.UI.Abstractions`) -- normalises each frame to [0,1] and
    keeps the subsampled median/MAD stretch-stats trick (`GetPedestralMedianAndMADScaledToUnit` strided to
    ~1M samples); `ComputeStretchUniforms` delegates to the shared static `AstroImageDocument.ComputeStretchUniforms`
    (one path). `AcceptFrame(image, freezeStats)` does the freeze (reuse cached stats; one-shot recompute on the
    off->on edge). **It must expose a non-empty `PerChannelBackground` sized to the channel count** -- the
    renderer's `StretchUniforms.ComputePostStretchBackground` reads `[0..2]` and an empty array crashes
    (pinned by `LiveFramePreviewSourceTests`). 7 unit tests.
  - `ViewerState.HideChrome` -- `ComputeLayout` drops the toolbar/status rows; `Render` skips painting them.
  - `ViewerState.FreezeStretchStats` (consumer sets it from polar phase, passes to `AcceptFrame`).
  - `ImageRendererBase.OverrideWcs` -- supplies the grid + `WcsAnnotation` projection WCS for a document-less
    live source (a plate-solved preview frame). Replaced 2026-10-01 by the source's own findings
    (`IPreviewSource.Findings`, step 2 of P1 in `live-session-preview.md`). `SetSurfaceSize(w,h)` sets the GPU projection dims without
    `OnResize` (the embedded viewer shares the host renderer's surface). The guide reticle / polar rings stay
    consumer-drawn on top after `Render` (each `PixelWidgetBase` has its own clickable tracker, so the embedded
    viewer's `BeginFrame` doesn't wipe the host tab's clickables). Verified live: preview renders through the
    full stretch pipeline; plate-solve -> `OverrideWcs` -> the GPU RA/Dec grid renders the pole grid.
- **Mode-aware sidebar icon** (new user ask): the Live Session icon reflects `LiveSessionState.Mode` --
  extend the per-frame override at `VkGuiRenderer.cs:250-254` (today only running -> camera-with-flash).
  Proposed: Preview = camera `\U0001F4F7`, running session = camera-flash `\U0001F4F8`, PolarAlign = compass
  `\U0001F9ED` (find-the-pole; user was unsure -- compass is the proposal), Planetary = ringed planet
  `\U0001FA90`.
- **Retire `GuiTab.Planetary`** (the standalone tab) once the center-view swap lands -- or keep the sidebar
  entry as a shortcut that switches to Live Session + sets `Mode = Planetary`. Don't leave both live.

The planetary work already shipped (shared-viewer embedding via content-rect, `ISelfDispatchingInputWidget`
input bridge, curated `ToolbarButtons`, capture-strip steppers) is widget-level and ports over unchanged --
it just gets hosted by Live Session's planetary mode instead of a tab.

**Migration order:** (1) `LiveSessionMode.Planetary` + dropdown entry + careful transition; (2) the
`IPlanetaryViewWidget` seam + center-view swap when `Mode==Planetary`; (3) mode-aware sidebar icon; (4) move
the capture strip + reuse focuser jog into the planetary-mode panels; (5) retire the 🪐 tab; then the richer
controls (ROI PiP + constraints, fake test controls, GOTO + Solve&Center) layer on.

**Steps 1-3 DONE + verified live (2026-06-24).** Live Session mode pill now lists Preview / Polar Align /
Planetary; selecting Planetary swaps the centre to the full `VkImageRenderer` viewer + capture strip
(via `IPlanetaryViewWidget`, one `VkPlanetaryTab` instance shared with the standalone tab during migration;
`LiveSessionTab` forwards mouse input to it), and the Live Session sidebar icon switches by mode (camera /
compass `\U0001F9ED` / planet `\U0001FA90`). **Bug found + fixed en route:** `DIR.Lib`
`PixelWidgetBase.RenderDropdownMenu` clipped the LAST dropdown item -- its loop guard
`itemY + rowH <= y + dropdownH` compares an accumulated `itemY` against a multiplied `dropdownH = N*rowH`, so
a sub-pixel float-rounding error dropped the 3rd ("Planetary") row (background drew, text/click did not).
Fixed with a `+0.5f` epsilon on the guard. **Needs a DIR.Lib NuGet release** for the fix to reach CI/released
builds (local dev picks it up via ProjectReference; no API change, so not a build blocker -- the clip just
persists in released builds until shipped).

**Step 5 DONE + verified live (2026-06-24): standalone 🪐 tab retired.** Removed `GuiTab.Planetary` from the
enum + `TabOrder`, its `TabChrome` entry, the `ActiveTab` + `RenderContent` cases, and the `Ctrl+Y` shortcut;
`StartVideoCaptureSignal` now lands in Live Session planetary mode (`liveSessionState.Mode = Planetary;
ActiveTab = LiveSession`) instead of the old tab. `_planetaryTab` stays constructed + wired as the Live
Session `PlanetaryView` (+ FrameCount++ / Dispose). Verified: sidebar shows 7 tabs, no standalone planetary;
full solution builds clean.

**Step 4a + 4b DONE + verified live (2026-06-25).** The interim top capture strip became a **left control
panel** (~280 design-units, carved from the content rect like the strip was) built with the
`DIR.Lib.Layout` engine (a `Layout.Builder` VStack of fixed-height rows -- weights + spacers, no pixel
math). It holds a **CAPTURE** section (Start/Stop, Exp/Gain/ROI steppers, fps/frames/dropped readout, moved
off the top strip) and a **FOCUSER** section (position + temp readout + a `[«][‹][»][»]` jog row that posts
the same `JogFocuserSignal(0, ±10/±100)` the Live Session OTA panel uses -- one focuser-control path). The
seam (`IPlanetaryViewWidget.RenderPlanetary`) now carries `PreviewOTATelemetry` (OTA 0's focuser) from
`LiveSessionTab`; the viewer fills the content minus the panel via `SetContentRegion`. Verified: panel
renders, focuser shows the connected fake (Pos 980, 15 deg C), capture runs at the readout's fps.

**Shutdown slowness during planetary capture (found via 4b verification, fixed 2026-06-25).** Quitting
*while a planetary capture was running* left the GUI "Not Responding" for ~25-30 s before it finally exited
(intermittently). **Root cause** (user's diagnosis, confirmed): the rolling-window stacker's fold/evict
loops only polled the token indirectly via `IPlanetaryFrameStream.LoadAsync`. For a **file-backed SER**
stream that I/O check aborts promptly (so it "worked in the standalone tab / SER playback"); but the live
path's blocking `_stackTask.Wait()` in `LiveStackPreviewSource.Dispose` (the planetary controller disposes
it WITHOUT ViewerController's IsBusy gate, on the shutdown thread) waited on a window stack that didn't
abort fast enough. **Fix (the cancellation token threaded + respected everywhere):**
1. The capture is **linked** to the app shutdown token -- `Start` receives `backgroundCts.Token`, so
   `backgroundCts.Cancel()` in `RequestQuit` cancels it (no imperative `Stop()` in the quit path).
2. `RollingWindowStacker`'s fold/evict loops (incremental + rebuild) now `ThrowIfCancellationRequested()`
   **every iteration**, so a cancel aborts the window stack promptly (not after the whole window).
3. No thread-blocking waits in the shutdown path. `LiveStackPreviewSource` is now `IAsyncDisposable`:
   `DisposeAsync` **awaits** the in-flight stack via `task.WaitAsync(3 s)` (the planetary controller disposes
   it async); the sync `Dispose` defers cleanup to a continuation instead of `.Wait()`. `StopAsync` accepts a
   `CancellationToken` (`DisposeAsync` passes a 3 s timeout) and `tracker.DrainAsync()` is `WaitAsync`-bounded
   to 5 s -- backstops so no drain can hang unbounded (the timeout the Program.cs comment always promised).
4. `FakeCameraDriver.CaptureVideoAsync` bails (`yield break`) if the camera disconnects mid-stream.

19 planetary unit tests pass. Verified: a quit-during-capture run now disconnects the cameras and exits
promptly (exit 0, no post-disconnect heartbeat spam, vs. the ~30 s "Not Responding" before). Intermittency
made inspector ESC automation flaky, so a final confirm in the real close-the-window flow is welcome.

**Step 4c DONE + tested (2026-06-25): ROI PiP + `RoiConstraints` + draw-on-stream.** The ROI is now a
free rect within hardware constraints, not a fixed list:
- **New `RoiConstraints` / `RoiRect` Lib types** (`TianWen.Lib/Devices/RoiConstraints.cs`): step / alignment
  (`WidthStep`/`HeightStep`/`OriginStepX`/`OriginStepY`) + min/max, with pure `Snap(RoiRect)` (size -> step,
  origin -> step, clamp to sensor). `ICameraDriver.RoiConstraints` is a **default interface method** returning
  a free step-1 rect over the sensor; `FakeCameraDriver` overrides it with ZWO-style 8 / 2 so the snap path is
  exercised end-to-end. `RoiConstraintsTests` pins the math (16 cases).
- **Queried from the connected camera off the render thread**: `SampleOTATelemetryAsync` snapshots the camera's
  `CameraXSize/YSize` + `RoiConstraints` into `PreviewOTATelemetry` (sensor dims + constraints), so the picker
  reflects the REAL camera pre-capture without ever touching the driver on the render thread (a fallback sensor
  is used until a camera reports). `PlanetaryCaptureActions.ConfigureRoi` now snaps via `RoiConstraints.Snap`
  (single source of truth).
- **ROI section in the left panel** (`VkPlanetaryTab`): a **PiP** sensor-proportioned thumbnail with the red
  ROI rectangle, size presets snapped to the constraints, `[<][>][^][v]` pan (snapped + clamped), and a
  **`[x] Show ROI on image`** toggle that outlines the displayed frame (= the ROI crop) on the live stream
  (clamped to the viewer area). Editing is idle-only; `Start` posts the snapped `_roi` size.
- **Deferred by decision:** drag-to-set the ROI directly on the image (user chose PiP + controls; the drag
  would conflict with the viewer's pan/zoom and needs a mode toggle), and pan-into-capture-origin (the readout
  origin stays driver-centred until the **Phase C** recenter loop pans it -- pan here positions the PiP/overlay
  SELECTION). `CurrentImageRect` was added to `ImageRendererBase` for the overlay.

### Frame rate at full frame: the stream's depth, readout and bandwidth

**Shipped 2026-09-28, #1044.** The ZWO ASI462MC gave SharpCap 136 frames a second at full frame (1936x1096, RAW8, High
Speed Mode on, Turbo USB 100) and TianWen 8 to 17. Traced one layer at a time on the camera, starting from the raw SDK
(the user: isolate it first through DAL alone):

| Raw ZWOptical.SDK video, full frame | fps |
|---|---|
| RAW8, USB bandwidth 100, high-speed readout on | 136.0 |
| RAW8, bandwidth 100, high speed off | 63.8 |
| RAW8, bandwidth 50, high speed on | 68.0 |
| RAW16, bandwidth 100 (the high-speed readout changes nothing in 16 bits) | 63.8 |
| RAW16, bandwidth 50: what TianWen's stream asked for | 31.9 |

The exposure made no difference between 32 us and 1 ms, and the rate follows the bandwidth, so the USB transfer is the
limit at every setting.

- **Two settings were the loss.** The stream read out in the camera's single-exposure depth (16 bits, the highest it
  has) at the USB bandwidth of 50 a connect sets. A stream's depth, its high-speed readout and its bandwidth are now its
  own (`VideoCaptureOptions`): it reads out in the depth it asks for, with the high-speed readout on unless asked
  otherwise, and takes the body's whole bandwidth while it runs, giving back what it found as it ends. The camera's
  single exposures keep their own depth, so a planetary capture in 8 bits leaves the next deep-sky frame in 16.
- **The planetary panel switches both**: a Depth choice among the depths the camera streams in (its device state's
  `VideoBitDepths`) and High speed where it has one (`CanFastReadout`), at the start and live. A change restarts the
  stream inside the driver, as a new window size does. The wire carries them on `PlanetaryRequestDto` and
  `PlanetaryControlsDto`, and `PlanetaryStateDto.BitDepth` says what the frames came in. The panel starts on the
  camera's own depth.
- **An 8-bit frame declares an 8-bit full scale** (`DALCameraDriver.MaxAduFor`, one rule for a single exposure and a
  stream). The stream stamped the camera's full scale, which on an 8-bit frame is the 16-bit readout's (4095 on a
  12-bit sensor), and the live stack would have divided samples of 0 to 255 by it.
- **An 8-bit stream records an 8-bit SER** (`SerRecording`). A SER has one depth, so switching it mid-recording ends
  the recording, as a new size does.
- **Through TianWen, in process** (`ZwoVideoRateProbe`, Release, 8 s a layer, each layer added to the one before):

  | Full frame | RAW16 | RAW8 |
  |---|---|---|
  | the driver's stream alone | 63.1 | 132.1 |
  | + the frame ring's push | 63.0 (2.7 ms p50) | 135.2 (1.5 ms) |
  | + a recording's append | 63.2 (3.5 ms), none dropped | 131.7 (3.6 ms), none dropped |
  | the capture loop, recenter off | 63.8 | 132.8 |
  | the capture loop, recenter on | 33.3 | 44.2 |
  | + the node's live stack, recenter off | 63.0 | 133.0 |

- **Recentering is opt-in** (the user, 2026-09-28: it needs a calibration phase, and the operator makes sure the rig is
  ready for it). It was on by default, and it costs about 15 ms a frame at full frame in process (133 frames a second
  to 44 in 8 bits) and more in a node (31.9 to 19.5 in 16 bits); making it cheaper, and why a node pays more, is #1045.
- **A node keeps up.** A Release node measured before the switches existed captured every frame the camera sent with
  recentering off (31.9 a second in 16 bits at bandwidth 50) with no client stream, a shared-memory stream or a byte
  stream alike; a client read just under 30, the live frame being sampled at most every 33 ms.
- **The live stack is slow at full frame**: a master took 0.4 to 6.4 s in process, on about one core, so the live
  master updates once every few seconds. It does not slow the capture; #1046.

### Reframing on the full sensor

**Open, #1033.** "Show ROI on image" outlines the frame on show, and during a capture that frame IS the ROI crop, so
the outline is the whole image and says nothing (the ZWO ASI462MC live check, 2026-09-28). What the user wants
instead: while an ROI capture runs, an option to take one full-sensor frame, show it with the current ROI and a
proposed one moved and resized on the image, and a confirm card that commits the proposal (a pan of the running
window when the size is unchanged, else a restart at the new size) or cancels. It replaces the checkbox, and the
drag deferred above stops conflicting with the viewer's pan and zoom once it happens on a still frame in a mode of
its own.

### Image-based Jupiter simulation (started 2026-06-25)

P2 to P4 are **tracked by #818**. Pointed at a star, with a coma term added, the same PSF chain is the test rig for
[camera-collimation.md](camera-collimation.md) and for the video autofocus in [focus-tolerance.md](focus-tolerance.md).

The fake's procedural disk is replaced by a REAL
Jupiter image (NASA/ESA Hubble OPAL 2024, public domain) put through the optical chain, so the fake is a
physically-faithful lucky-imaging test rig. Phased:
- **P1 DONE + tested:** `JupiterTextureRenderer` loads the embedded `jupiter.rgb.gz` (cropped disk, gzipped
  raw RGB, 8-byte LE w/h header -- the repo's `.bin.gz` convention, zero new deps), scales the disk to the
  physically-correct **equatorial radius** (`SimJupiterArcsec / pixelScale`, pixelScale from `PixelSizeX` +
  `FocalLength`; falls back to `PlanetRadiusPixels` when no FL is configured, e.g. unit tests), applies
  Jupiter's geometric oblateness (polar = 0.935 x equatorial), and reuses `SyntheticPlanetRenderer`'s seeing
  blur + shot/read-noise compose (extracted to a shared `ComposeWithNoise`). `RenderVideoFrame` now calls it.
  Output is mono luminance (Rec.709) -- the fake's existing 1-channel path. `SimApertureMm` added for P2.
  `JupiterTextureRendererTests` + the existing `FakeCameraVideoTests` (COM / brightness / jog / stack) pass.
- **P2 (next):** the PSF chain -- diffraction (Airy, from `SimApertureMm`) + per-frame seeing + defocus from
  the fake focuser position (jog the focuser -> watch it sharpen).
- **P3:** per-frame turbulence (tip-tilt + low-order warp) + exposure/gain-driven shot+read noise.
- **P4:** `IVideoSimulationControls` (the 4d panel) to drive seeing / noise / turbulence / defocus live, plus
  a colour (Bayer) emission mode so the stack debayers to a colour Jupiter.

Remaining: P2-P4 above; (4d) the `IVideoSimulationControls` test panel folds into P4; then GOTO +
Solve&Center, and Phase C (COM recenter + the deferred ROI drag-to-set + origin plumbing).

### SharpCap-informed UI redesign (user, 2026-06-24, with a SharpCap Pro reference shot)

"We don't need all controls" -- the viewer already owns histogram + stretch, so don't re-create those. Two
concrete upgrades over the interim top-strip steppers:

- **ROI is a picture-in-picture, not a W x H stepper.** A small sensor-proportioned thumbnail with a red ROI
  rectangle the user positions (Pan/Tilt), sized from a preset/drag, **plus an optional overlay** of that
  rectangle on the main image. (SharpCap's "Track Planet: Center / Lock" beside it is exactly the Phase C
  COM recenter -- wire the checkboxes as placeholders now, behaviour in C.)
- **ROI is a constrained free rect QUERIED FROM THE CAMERA, not a hardcoded list.** The interim
  `VkPlanetaryTab.RoiPresets` static array is WRONG -- ROI is *free choice within hardware constraints*, and
  the presets SharpCap shows are just snapped-to-constraint convenience shortcuts. **Verified the constraint
  source:** the ZWO rule is documented ONLY in the raw SDK header (`ZWOptical.SDK/include/ASICamera2.h:438-439`:
  `iWidth%8 == 0`, `iHeight%2 == 0`; `ASISetStartPos` centres by default + moves the ROI during streaming).
  The DAL `ICMOSNativeInterface` already has the primitives (`SetROIFormat(w,h,bin,fmt)`,
  `Set/GetStartPosition`) but **no constraint metadata**; `ICameraDriver` (`NumX/NumY/StartX/StartY/Bin*`)
  has no step/align/validation either. So nothing is queryable today -- it must be ADDED:
  - **New `RoiConstraints`** (MaxWidth/MaxHeight = sensor at bin, `WidthStep`/`HeightStep` [ZWO 8/2],
    `OriginStepX/Y`, MinWidth/MinHeight) exposed by the camera (+ `ICMOSNativeInterface` for native). Default
    = step 1 / max = sensor for ASCOM + the fake; ZWO/QHY report their real steps (QHY rule TBD from its SDK
    at Phase D). The UI snaps any chosen rect to the step and clamps to max.
  - **Draw-the-ROI-on-the-stream** (SharpCap does this): a draggable red selection rect directly on the main
    video image is the PRIMARY way to set the ROI (drag -> snap to constraints -> `SetROIFormat`+`SetStartPos`).
    The PiP sensor thumbnail mirrors it; size presets are snapped shortcuts.
  - The picker/overlay needs the *connected* camera (for its constraints + sensor dims), which the controller
    only holds during capture -- so the tab must resolve it up front (pass the connected `ICameraDriver` into
    `RenderTab`, resolved from `appState.DeviceHub` + active OTA in `RenderContent`).
- **Fake-hardware test controls** (only when the camera supports it): Focus Offset (defocus blur), Random
  Seeing (per-frame blur variation), Random Noise, X/Y Offset (drift) -- so the lucky-imaging grading +
  stacking can be exercised realistically (mix of sharp/soft frames) with no real hardware. These drive the
  `SyntheticPlanetRenderer` / `FakeCameraDriver`. **Keep shared code fake-agnostic:** expose them via a
  capability interface (e.g. `IVideoSimulationControls`) the fake implements + the controller's camera is
  tested against (`controller.Camera is IVideoSimulationControls`), NOT a fake-type check (per the
  no-fake-special-casing rule). `SyntheticPlanetRenderer` already has blur/noise + the driver has drift, so
  this is mostly exposing existing knobs as runtime-settable + a panel.

**Proposed layout** (keeps the viewer's right info panel for stretch/wavelet/WB/histogram intact): the tab
carves a **left control panel** (~300 px) out of the content rect -- like it already carves the top strip --
holding *Capture* (Start/Stop, Exp, Gain), *Region of Interest* (the PiP + size + pan + overlay toggle +
Track placeholders), and *Testing (Fake)* (defocus/seeing/noise/drift). The shared viewer fills the rest via
`SetContentRegion(content minus left panel [minus top strip])`. Sub-steps: (3b) left-panel scaffold + move
capture controls in; (3c) ROI PiP + pan + on-image overlay; (3d) fake test controls via the capability
interface. The interim top-strip steppers (step 3, done) fold into the panel.
5. **Phase C: mount jog recenter + calibration** (see Phase C row): COM -> ROI auto + mount-jog opt-in +
   directional nudge buttons + a short calibration.

Verify unattended via the fake-device + `sdl-ui-inspector` harness (per CLAUDE.md): open 🪐, post
`StartVideoCapture`, assert frames climb + a stretched (not blown) planet renders + sliders present.

### Shipped 2026-06-28: colour capture + realistic noise + live controls + render-thread diagnostics

Closes most of P4 (colour) + the live-control gap and adds hang diagnostics:

- **Colour (Bayer) capture.** The fake's RGGB sensor emits a raw Bayer **mosaic** in video mode; the capture
  loop derives the stream layout from the ACTUAL frame (1ch + `SensorType.RGGB` -> `SplitCfa`, not the assumed
  RGB that dropped every mono-shaped frame) -> per-photosite stack -> single demosaic -> COLOUR master, so the
  wavelet deblur runs on real colour data. (`JupiterTextureRenderer.RenderBayer`.)
- **ADU -> [0,1] at the stream boundary** (`LiveCameraFrameStream.DeepCopy`): a live camera delivers ADU
  (MaxValue = full-scale) but the planetary master declares MaxValue = 1; without normalising, the viewer
  clamped every pixel to white (flat frame). Fixed for mono + colour; the SER path (already [0,1]) is
  unaffected (scale == 1 passthrough). This was the "flat white/black preview" bug.
- **Exposure-driven brightness + linear preview.** Disk peak scales with exposure x gain (10 ms at gain 100
  ~ 50% of full well); planetary preview defaults to `StretchMode.None` so the disk + belts show at true
  relative brightness instead of an auto-stretch blowing the disk to white.
- **Realistic noise** (`SyntheticPlanetRenderer.ComposeWithNoise`): shot+read noise in the **electron domain**
  (Poisson in electrons, not ADU), calibrated against a real planetary SER (~340 e- effective full-well,
  ~1.3 e- read -> ~8% per-frame disk grain at 10 ms). Single frames realistically grainy; the stack averages
  clean.
- **ROI overlay clamped to the image area** (not the viewer rect that overlapped the right info panel).
- **Live Exp / Gain / ROI size / ROI pan during capture** (supersedes "steppers disabled during capture" in
  step 3 above): `IVideoCameraDriver.ApplyVideoControlsAsync`; the render thread stages the change (Volatile /
  Interlocked), the capture loop drains + applies it after the next frame; a ROI-size change rebuilds the
  frame stream + swaps the preview source. The fake's ROI-window state is capture-loop-owned (removed the
  needless `_videoRoiLock`).
- **Two concurrency fixes:** lock-free `Image.InvalidateStarListCache` (dropped a synchronous
  `SemaphoreSlim.Wait()`); removed the dead `FakeCameraDriver._videoRoiLock`.
- **Hang diagnostics.** (1) Defensive breadcrumbs in the live path (`GUI_*.log`, Debug+WARN):
  live-control-applied, a 250-frame capture heartbeat, and per-stack start/done/duration + a long-running WARN
  in `LiveStackPreviewSource`. (2) A render-thread **watchdog in the inspector** -- `render_liveness`
  (`SdlVulkan.Renderer.Inspector` 6.8) classifies ALIVE/BLOCKED/DEAD via a short-budget ping that round-trips
  ON the render thread; on BLOCKED it prints the `dotnet-stack` to capture the frozen frame. (A reported
  "hang" did NOT reproduce on the rebuilt binary; these are the diagnosis trail if it recurs.)

Tests green: `FakeCameraVideoTests`, `PlanetaryCaptureControllerTests`, `JupiterTextureRendererTests`,
`LiveCameraFrameStreamTests`, + star-detection.

### Phase C DONE 2026-06-28: COM recenter loop (ROI auto + mount opt-in) + manual mount nudge

The "hold the planet centred" loop. Layering: a **pure** decision in `TianWen.Lib`, the capture-loop hook +
mount actuation in the controller, and a RECENTER panel section on top.

- **`PlanetaryRecenterController.Decide(...)`** (`TianWen.Lib/Imaging/Planetary/`, pure/static): disk COM ->
  per-axis offset from the frame centre -> **per-axis deadband** (a large offset on one axis never drags the
  other, otherwise-centred axis -- the subtle bug a distance deadband would have) -> a damped (`Gain`) ROI jog
  toward the disk on each axis that has pan range, and a **coarse mount nudge** (sized `offset x
  PixelScaleArcsec`, capped) on any axis that is edge-blocked (window at the sensor edge, no pan range, or the
  camera can't ROI-jog). Returns a `RecenterDecision` (ROI dx/dy + signed RA/Dec arcsec + telemetry offsets);
  both actuators can fire in one frame. Mount sign is **uncalibrated** (`FlipRa`/`FlipDec`), so the cap bounds a
  wrong guess to a small mis-move, never a runaway -- the manual nudge buttons are the safe primary mount control.
- **`IVideoCameraDriver.VideoRoi`** (new) -- the live readout window (origin + size) so the loop knows how much
  pan range is left before an edge. The fake reports its capture-loop-owned ROI window; not-streaming -> a
  sensor-sized window.
- **Capture-loop hook** (`PlanetaryCapture.MaybeRecenterAsync`): runs on the capture loop (off the
  render thread) after each push on the still-alive frame -- `PlanetaryDisk.BoundingBox` + `CenterOfMass` ->
  `Decide` -> stage a ROI jog (drained by `ApplyPendingControlsAsync` the same iteration) and/or fire the mount
  pulse. Config (`auto`, `mountJog`, deadband, gain, flips) is staged by the render thread via
  `ConfigureRecenter`; the mount + pixel scale are attached on Start (`AttachMount`). Auto-recenter is opt-in OFF
  since 2026-09-28 (it was ON: ROI-only and zero-disturbance, but it needs the operator ready for it and costs the loop
  about 15 ms a frame at full frame; "Frame rate at full frame"); mount jog opt-in OFF.
- **Mount actuation -- one shared actuator.** `MountNudge.PulseArcsecAsync` (Lib, was the GUI's
  `MountActions.PulseGuideArcsecAsync`) converts arcsec -> a guide-rate-sized, capped `StartPulseGuideAsync`;
  used by BOTH the auto loop (single-flight + cooldown, fired non-blocking so it never stalls the high-fps loop)
  and the manual `JogMountSignal` (N/S/E/W buttons, focuser-jog routing model: gated on no-running-session + a
  connected pulse-guide mount). **The capture claims only the camera, so the auto loop's nudge asks
  `DeviceOwnershipGate` over the mount first** and is dropped while another run or a job holds it
  (`PlanetaryCaptureTests`); it used to pulse regardless, whatever held the mount.
- **RECENTER panel section** (`VkPlanetaryTab`, between REGION and FOCUSER): `[x] Auto-recenter (ROI)`, Deadband
  + Gain steppers, a live `off dx,dy px [actuator]` readout while capturing, `[ ] Mount jog (coarse)`, and a
  `Nudge N/S/E/W` row. Config synced to the controller once per render. The duplicate `[x]/[ ]` overlay-toggle
  row was refactored into a shared `CheckRow` helper.
- Tests: `PlanetaryRecenterControllerTests` (11 pure cases: centred/deadband/ROI jog sign + clamp/edge handoff/
  mount-disabled/no-ROI-cap mount-only/flip/cap/no-scale) + two e2e in `PlanetaryCaptureControllerTests`
  (auto-recenter jogs the window to follow a drifting fake planet; auto-off leaves it fixed). 40 planetary
  tests green; full solution builds 0/0.

**Out of scope (deferred):** a guider-style mount-jog **calibration** (to learn the on-sky sign + scale instead
of the uncalibrated flip flags), and drag-to-set ROI on the image. The coarse auto mount-jog + manual nudge are
enough for first light; calibration is the natural next refinement. Confirming the sign on a real planet is
bench issue #651.

## A mono planetary night (2026-10-08)

**What it is measured against: the owner's Saturn of 2026-10-07** (`E:/Astro/Saturn/2026-10-07/Light`), taken with SharpCap's
sequencer through the 254 mm Newtonian and a barlow (about 1750 mm) on a ToupTek 678 mono (2 um pixels) behind a ZWO filter wheel,
on an equatorial mount. Ten sets of Red, Green and Blue between 11:34 and 13:36 UTC; each file 10,000 frames of 640x480 MONO8 (158 s
at 63 frames a second); a set about 7.7 minutes; one folder, with a `.CameraSettings.txt` beside each file. Within a set nothing
changed. Between the first and second sets the gain went from 1620 to 2252 and the black level from 11 to 17, and between the third
and fourth the owner refocused by hand (12993 to 12987), focusing on SharpCap's FFT score, which the owner found worked fairly well.

**What TianWen could not have done that night**, in the order it has to be built:
1. **Stream from that camera** (#813): ZWO streams natively, and ToupTek since ToupTek.SDK 1.2 (TIA2, "Video at the Saturn window on
   the ToupTek" below); a QHY or Player One body still runs the single-exposure loop.
2. **Record from the GUI** (#1344): **done 2026-10-16**. The node records (`SerRecording`) and the Planetary mode's Record control
   asks it to ([Phase G](#phase-g-a-mono-filter-wheel-sequence)); the CLI still does not.
3. **Say how a file was taken** (#1342, Phase I): our recording writes no exposure, gain, offset or focuser position.
4. **Run the sequence** (#1340, Phase G): nothing moves the filter wheel during a planetary capture.
5. **Focus on the planet** (#1341, Phase H): every focus routine and aid measures stars.

The processing side of such a night is the restoration plan's: each file stacked on its own, the run read as a session per filter
(#1336), the stacks composed with one north decided across the run, and a filter's stacks put on one level before they are averaged
(#1337).

### Frame rate at the Saturn ROI on the ToupTek (measured 2026-10-14)

**Issue:** #813 (its "measure first" item: the rate the single-exposure loop reaches with a small ROI on a real body).

*Since ToupTek.SDK 1.2 this body streams, and a live capture takes video, not the trigger loop measured here: "Video at the Saturn
window on the ToupTek (measured 2026-10-15)" below. A single exposure still takes the trigger.*

The same G3M678M, on this desktop, at the night's region: 640x480 at (1716, 546), bin 1, gain 2252. The black level differs between
the layers: the scratch console leaves it at the camera's own, while TianWen's driver sets it to a tenth of its range at connect
(`DALCameraDriver`'s connect defaults) and the probe keeps that. It is an offset added to every sample, not a readout setting, so it
moves no rate; neither layer varied it. Two layers, so the gap shows: the camera's ceiling in the VENDOR's video mode, and
TianWen's own path, which for this body is not video at all. ToupTek.SDK leaves `CanVideoCapture` false and takes each frame by the
SDK's software trigger, so a live capture is `PlanetaryCapture.RapidExposureFramesAsync`: expose, poll, read, repeat.

**The camera's ceiling (vendor video mode).** Frames delivered over 3 s, after a 1 s settle; no frame lost in any run (the frame
info's sequence numbers run without a gap, though in one run the counter jumped twice), and the SDK's own `get_FrameRate` agrees
with the count to 0.7 fps on every run that did not stall:

| Exposure | 8-bit, speed 0 | 8-bit, speed 1 | 8-bit, speed 2 | 16-bit, speed 0 | 16-bit, speed 1 | 16-bit, speed 2 |
|---|---|---|---|---|---|---|
| 15.625 ms | 63.4 | 63.4 | 63.5 | 48.9 | 62.9 | 63.5 |
| 15 ms | 65.8 | 66.2 | 66.3 | 49.3 | 65.8 | 65.8 |
| **5 ms** | 98.1 | 131.0 | **196.8** | 49.1 | 65.7 | 98.3 |
| 2 ms | 98.3 | 131.0 | 200.5 | 49.2 | 65.8 | 98.2 |
| 1 ms | 98.1 | 131.1 | **200.8** | 49.2 | 65.6 | 98.5 |
| 0.1 ms (the minimum) | 98.5 | 131.2 | 200.5 | 49.3 | 65.6 | 98.2 |

- **About 200 frames a second is the most this region gets, in 8 bits at speed 2** (`put_Speed`; `MaxSpeed` is 2, and 2 is also the
  camera's default after every open). 16 bits halves it: about 98.
- **The speed level scales the row readout**: 480 rows take 4.98 ms at speed 2, 7.63 at speed 1 and 10.2 at speed 0 (8 bits), so
  8 bits at speed 0 runs as fast as 16 bits at speed 2.
- **Above the readout a frame is its exposure**: 15.625 ms gives 62.9 to 63.5 fps and 15 ms gives 65.8 to 66.3 at every speed and depth
  whose readout is shorter (all but 16 bits at speed 0, whose 20.3 ms readout holds it at 49). 5 ms in 8 bits at speed 2 sits just past
  the readout, at 196.8 against 200.6.
- **SharpCap's "USB Speed=1" is taken to be this level** (SharpCap does not say). At the night's exposures it cost nothing: the night's
  files read 63.3 to 63.4 fps at 15.625 ms and mostly 65.9 to 66.0 at 15 ms, the exposure-bound rates above. At 5 ms it would hold
  8 bits at 131 fps against speed 2's 197.
- **Width does not count**, as the SDK README says: at the minimum exposure 3840x480 gives 200.3 fps against 640x480's 200.1 (8 bits,
  speed 2), 98.2 against 98.5 (16 bits, speed 2), 130.9 against 131.0 (8 bits, speed 1) and 65.3 against 65.4 (16 bits, speed 1).
- **The README's numbers hold, and so does its per-row model at 480 rows.** Full frame at speed 2 reproduced its 47 and 23 fps
  (46.8 and 23.3). Its five heights fit a frame time of 0.27 ms plus 9.72 us a row in 8 bits and 0.58 ms plus 19.9 us in 16 (least
  squares), which predicts 4.94 ms (202 fps) and 10.1 ms (99 fps) at 480 rows; measured, 4.98 ms (200.5 to 200.8) and 10.2 ms (98.2
  to 98.5). ToupTek's own figures (47 fps at 3840x2160, over 1500 in 8 bits and 740 in 16 at 3840x40) are the README's; neither
  source gives 480 rows.
- **Video mode stalls, and a stop and restart on one handle sets it off.** With the stream stopped, reconfigured and restarted on the
  same handle for each run, 14 of 46 runs delivered no frame at all, every call succeeding; `TOUPCAM_OPTION_DEVICE_RESET` cleared 13
  of them (the camera back in 0.8 s), one stayed dark after it, and one more run stopped mid-stream. Opened afresh for each run, 1 of
  the 44 runs at this region stalled at its start, the first, straight after the full frame had stopped mid-stream (twice, at both
  depths). A video path for this body needs the reset the DAL driver already makes after two lost exposures.
- **The vendor's trigger loop stalled too**, so the trigger is not immune. On one handle, stopped and restarted per run, the fifth run
  (8 bits at 1 ms) had its triggers go unanswered for minutes, and that console, which then had no reset, was stopped by hand. The
  next console's first run, on a fresh open, had three triggers go unanswered; a `DEVICE_RESET` cleared it, and after it that run and
  the other 23 lost nothing.

**TianWen's path (software trigger).** `src/TianWen.Lib.Tests/ToupTekFrameRateProbe.cs`, gated on `TIANWEN_TOUPTEK_RATE_PROBE=1` (never
`TIANWEN_TOUPTEK_PROBE`, which resets the camera), in process through `ToupTekCameraDriver`: the bare driver polled as fast as it
answers, the bare driver sleeping 5 ms between polls as the capture loop does, and `PlanetaryCapture` whole. Frames over 3 s after
1 s, every frame 640x480 at the depth asked for. The two driver layers counted no lost exposure (one that ended without a frame); the
capture loop reports none, so its losses are read only from its frame counts: within 0.7 fps of the driver's at the same 5 ms sleep,
where one loss would have stopped the count, since the driver gives a lost exposure up only 15 s past its end. The binding never
sets a speed, so the camera runs at its default, 2:

| Exposure | Depth | Driver, no sleep | Driver, 5 ms sleep | `PlanetaryCapture` | Trigger to ready (median, no sleep) |
|---|---|---|---|---|---|
| 15.625 ms | 8 | 12.8 | 10.6 | 10.6 | 77.6 ms |
| 15 ms | 8 | 12.8 | 10.7 | 10.6 | 78.0 ms |
| **5 ms** | 8 | 16.0 | 12.8 | **12.9** | 62.2 ms |
| 2 ms | 8 | 16.0 | 12.8 | 13.0 | 62.3 ms |
| 1 ms | 8 | 16.0 | 12.8 | 13.0 | 62.3 ms |
| 0.1 ms | 8 | 16.0 | 12.8 | 12.9 (floored to 1 ms) | 62.2 ms |
| 15.625 ms | 16 | 12.7 | 10.6 | 10.6 | 77.6 ms |
| 15 ms | 16 | 12.0 | 10.3 | 11.0 | 79.5 ms |
| 5 ms | 16 | 12.7 | 10.6 | 10.6 | 78.5 ms |
| 2 ms | 16 | 12.6 | 10.5 | 10.6 | 78.2 ms |
| 1 ms | 16 | 12.8 | 10.7 | 10.6 | 78.0 ms |
| 0.1 ms | 16 | 12.8 | 10.5 | 10.7 (floored to 1 ms) | 77.8 ms |

- **The trigger loop runs 6 to 16 times below the camera's video mode**: 12.9 fps against 196.8 at 5 ms in 8 bits, 10.6 against 66 at
  the night's 15 ms. At 5 ms it collects light 6.5 % of the time, against 98 % in video.
- **The time is the SDK's trigger, not TianWen's code.** Reading a frame costs 0.2 to 0.3 ms. The same trigger loop written on the
  vendor's binding alone (`Trigger(1)`, wait for `EVENT_IMAGE`, pull in the callback, repeat) takes the same 62.4 and 78 ms. Those are
  4 and 5 ticks of Windows' default 15.625 ms timer, and they fall to 20 ms (49 fps, 8 bits at 5 ms) and 30 ms (33 fps at 15 ms) in
  a process that asks for a 1 ms timer (`timeBeginPeriod(1)`, which TianWen never calls); 16 bits then reads 31 to 36 ms. So the SDK's
  trigger path waits on its process's timer. Even with a fine one, a triggered frame of 5 ms in 8 bits takes 20 ms where its exposure
  and readout are 10: each frame is exposed, read and handed over before the next trigger, where video overlaps them.
- **The capture loop's 5 ms sleep costs one more tick** (78 and 93 ms), since a 5 ms sleep also rounds up to the 15.6 ms timer, and
  `PlanetaryCapture` reads within 0.7 fps of the bare driver sleeping 5 ms. Its 1 ms exposure floor makes the minimum read as 1 ms.

**What 5 ms costs in signal.** On 12:21 Red (15 ms, gain 2252, black level 17; 100 frames of its SER averaged) the sky reads 16.6 ADU,
the planet's lit pixels a median of 94 and its brightest 186 (one frame's brightest is 207 of 255). At 5 ms and the same gain the
signal above the sky is a third: the brightest about 73 ADU, the median about 42.5. Three times the gain, 6756 (the SDK's percent, 100
being unity), puts the level back and is inside `get_ExpoAGainRange`, 100 to 15000; the SDK does not say where in that range the gain
stops being analogue. The level is not the signal: each frame then holds a third of the photons, so its shot-noise SNR falls by the
square root of 3, which three times the frames win back in a stack only where the frames come three times as fast, that is in video
(197 fps at 5 ms against 66 at 15 ms). In TianWen's trigger loop 5 ms buys 12.9 fps against 10.6.

**The recipe for the camera's ceiling** (a scratch console on ToupTek's own `reference/toupcam.cs` and `lib/win-x64` natives from the
ToupTek.SDK clone, not committed): on an open handle, `put_Option(UPSIDE_DOWN, 0)`, `RAW = 1`, `BITDEPTH = 0` (8) or `1` (16),
`TRIGGER = 0` (video), `put_AutoExpoEnable(false)`, `put_ExpoTime` in microseconds, `put_ExpoAGain(2252)`, `put_Speed(0 to 2)`,
`put_Roi(1716, 546, 640, 480)`; `StartPullModeWithCallback`, pulling every frame in the `EVENT_IMAGE` callback with `PullImage` (V4
frame info); settle 1 s; count the frames delivered over 3 s by a `Stopwatch`; read `get_FrameRate` beside it; `Stop`. Open afresh for
each run, and reset (`DEVICE_RESET`, then find the camera again) a run that delivers nothing in 2 s. The trigger comparison is the same
with `TRIGGER = 1`, no speed set, and `Trigger(1)` then a wait for the callback in a loop, once as the process starts and once after
`timeBeginPeriod(1)`.

### Video at the Saturn window on the ToupTek (measured 2026-10-15)

**Issue:** #813 (ToupTek joins ZWO in the shared DAL video loop; TIA2).

ToupTek.SDK 1.2 (SharpAstro/ToupTek.SDK#4) implements the DAL's video members on the camera's free-running mode, so the G3M678M now
streams through `DALCameraDriver.Video.cs`, the loop ZWO already used, instead of the trigger loop above. Same camera and window
(640x480 at 1716, 546; a stream starts centred and is panned there), 8 bits, 5 ms, gain 2252, speed 2 (a stream takes the top
`BandwidthOverload`, which on this SDK is the speed level). Measured in process by `ToupTekFrameRateProbe` (Release, gated on
`TIANWEN_TOUPTEK_RATE_PROBE=1`): the driver's stream read with nothing done to a frame, plus the ring's push, plus a recording's
append (both on the reader, as the capture loop does them), then `PlanetaryCapture` whole, 20 s a layer after 1 s; and recordings
through the capture loop, read back off their SER.

**The machine was busy, and that is what found the weakness.** Other flights' planetary sharpening held all 16 cores at 100 % for
every run here, with 126 to 127 threads waiting to run (`\System\Processor Queue Length`). A capture loop meets a milder form of it
whenever the node's live stack shares its process.

**Before: the one-frame hand-off gave the reader a fraction of the frames.** The driver's own thread read 197 a second in every layer
(3,935 to 3,948 in 20 s); its reader, woken through the thread pool, was often 50 to 200 ms late, and each frame that arrived while it
was late replaced the one waiting:

| Layer (one-frame hand-off) | Reader's fps | Replaced in 20 s | Body dropped | Between frames p50 / p99 / max | Reader's own cost p50 / p99 |
|---|---|---|---|---|---|
| the driver's stream alone | 83.9 | 2,233 | 35 | 8.0 / 48.5 / 201.0 ms | |
| + the ring's push | 41.0 | 3,090 | 42 | 22.1 / 71.3 / 150.8 ms | push 0.35 / 1.05 ms |
| + a recording's append | 99.3 | 1,915 | 0 | 5.6 / 52.9 / 139.2 ms | append 1.78 / 3.92 ms |
| `PlanetaryCapture` whole | 51.0 | 2,915 | 0 | 6.6 / 90.0 / 165.5 ms | |

A minute's recording through the capture loop held **1,974 frames over 59.98 s, 32.9 fps** off its SER: the driver read 11,839, the
hand-off replaced 9,853. The rates between layers do not rank (41 below 84) because each layer is a draw from how late the reader
happened to be; the frames' own cost was never the limit, about 2 ms of the 5.

**What changed, and why each:**
- **A bounded queue instead of one slot** (`VideoHandOffMaxFrames`, 64; and at most `VideoHandOffBudgetBytes`, 256 MiB, of planes, so
  a full-frame window queues 8). A reader that pauses catches up from it; one slower than the camera for good still drops, the OLDEST
  first, so it sees frames a queue old at worst. In every run after it at most 15 frames waited (75 ms), a quarter of the queue. A
  queue that holds only for a recording was the alternative; one rule for every reader is simpler, and a live view on a reader that
  keeps up sees no added latency, the queue being empty.
- **The stream's thread at the highest priority.** It spends nearly all its time waiting on the body, and a frame it is late for waits
  in the SDK's ring (64 frames here) and then is dropped by the camera. The body dropped 35 and 42 frames in two of the layers before
  and none in any layer or recording after, under the same load; the before runs were not repeated to pin the priority as the cause.
- **A recording's conversion in one vector pass** (`RawPixelConversion.NarrowToByte`, `SerImageBridge.FillSerFrame`): the scalar loop
  was 1.78 ms of a frame at p50, now 0.94 under the same load.
- **Frames stamped with the driver's read time** (`PlanetaryCapture` takes a stream's frame's own time for the ring and a recording), so
  a queue between the driver and the loop does not bunch a recording's timestamps, and a stall shows as the gap it was. A camera that
  does not stream stamps a frame with when its exposure started, so its frames keep the capture's own time, as before, and its
  recording names no gaps (a slow download is no stall).

| Layer (queue, after) | Reader's fps | Replaced in 20 s | Body dropped | Between frames p50 / p99 / max | Most waiting | Reader's own cost p50 / p99 |
|---|---|---|---|---|---|---|
| the driver's stream alone | 194.3 | 0 | 0 | 5.07 / 10.98 / 17.8 ms | 5 | |
| + the ring's push | 193.9 | 0 | 0 | 5.11 / 9.92 / 15.6 ms | 9 | push 0.32 / 0.75 ms |
| + a recording's append | 194.2 | 0 | 0 | 5.09 / 9.37 / 13.3 ms | 11 | append 0.94 / 1.50 ms |
| `PlanetaryCapture` whole | 196.7 | 0 | 0 | 5.17 / 12.11 / 48.1 ms | 9 | |

**The bar (decided with the controller): at least 190 fps read off the SER, frame count over its own timestamp span, 60 s, three
runs, nothing replaced, the body's drops at or below SharpCap's (about 55 in 100,000).** Met on every run:

| Recording, 60 s | SER frames | Timestamp span | fps off the SER | Read by the driver | Replaced | Body dropped | Most waiting | Gaps over 0.5 s |
|---|---|---|---|---|---|---|---|---|
| run 1 | 11,804 | 59.99 s | **196.8** | 11,825 | 0 | 0 | 13 | none |
| run 2 | 11,806 | 60.00 s | **196.8** | 11,823 | 0 | 0 | 15 | none |
| run 3 | 11,806 | 60.00 s | **196.8** | 11,818 | 0 | 0 | 13 | none |
| (the first, before the three) | 11,805 | 60.00 s | 196.8 | 11,811 | 0 | 0 | 11 | none |

196.8 is the camera's own rate at this window (the vendor's console above, the SDK's probe likewise). Each stream's log counts 6 frames
the body lost, all within its first 250 frames, which take in the start and the pan to the Saturn window and come before the recording
begins: the pan accounts for one (ToupTek.SDK measured one frame a pan), the start for the rest, not looked into further. Nothing went
to disk late: the recording's writer dropped none.

**A stalled stream is brought back, not ended.** This camera stalls about once in 1,250 s of streaming at speed 2, in periods, with
no warning, and ToupTek.SDK 1.2 answers `GeneralError` from the frame call about 1 s (the exposure plus 1 s) after the last frame;
`CameraRemoved` for a disconnect, `Timeout` for no frame in the wait. The shared loop used to end its stream on any answer but a frame
or `Timeout`. Now an answer that is neither (nor `CameraRemoved`, which still ends it) is a stall, and the loop brings it back by the
cheapest rung the SDK's soak measured to work: **restart** the stream on the same handle (10 of 11 stalls, about 6.5 s, 5.2 of them in
the stop of a stalled camera); then **close and reopen** the camera; then **reset** it as a replug would (`ResetDevice`, 30.4 s for the
one stall that needed it). Nudging the speed or the window was measured to undo none, so it is not a rung. In
`DALCameraDriver.Video.cs`:
- each run of the body's stream between a start and a stop is a segment on a thread of its own; the rungs run between segments, so the
  reader, which only waits on the queue, is never inside them, and a stop is honoured between rungs, during a reset camera's wait to
  reappear, and within one frame wait of a segment (a close and reopen, a couple of seconds, finishes first, since a stop half way would
  leave the camera closed);
- every setting the stream had (exposure, gain, the window as panned, depth, readout mode, the whole bandwidth) is set again on every
  start, and the reopen and reset share the find-again-and-restore path a lost single exposure's reset already took
  (`DALCameraDriver.Recovery.cs`);
- a stream healthy for `VideoHealthyAfter` (30 s) since it came back starts its next stall from a restart again; one that stalls sooner
  goes on up; a rung's stream that answers the stall again, or answers only Timeout for longer than the stream's silence limit
  (`VideoSilenceWatch`, below), has failed; the same silence mid-stream is a stall of its own (a body that goes quiet rather than
  saying so, as `ASIGetVideoData` does with no frame); past the last rung the stream ends with a plain reason ("stopped sending frames ... neither a restart of the stream, nor
  closing and opening the camera, nor a reset brought it back", or, for a body that cannot be reset, to unplug it);
- **the silence limit is the STREAM's** (`VideoSilenceWatch`, carried across every rung, never a segment's, so a rung's first frame
  is held to what the stream has shown it needs), and is the longest of: twice the exposure plus 1 s, at least 2 s; and twice the
  longest silence any of the stream's last 8 frames came after, plus 1 s, at most 15 s, which is also the limit before the stream
  has a frame to go by. Two edges of the exposure's limit alone, raised in review round 2 of #1605 and found on no camera here
  (no ZWO attached), are why: **(a)** a body that finishes the frame in flight at the OLD exposure after it is lowered from over
  about 2 s (a ZWO may; the G3M678M cuts it short) was called stalled by the new, shorter limit, a restart and a gap for nothing,
  so after a change the limit holds the longer of the old and new exposure until two frames have arrived, as ToupTek.SDK's
  `OnExposureChanged` arms its own timeout; **(b)** a healthy stream whose frame period is its READOUT, over 2 s (a 60 MP ZWO at
  full frame, 16 bits, over USB 2: about 3 s), was stalled on every frame and the ladder gave up on it, where the stream before
  the watchdog waited for ever, so the frames' own silences widen the limit, capped at 15 s and aging out after 8 frames so one
  USB hiccup does not widen it for the rest of the stream, and forgotten when the exposure, the window's size, the depth or the
  readout mode changes (a pan keeps them, its readout being the same, so a recentre never loosens the watchdog; a binning is a
  stream's from its start). **So a body that slows and then dies is called stalled within twice its longest recent silence plus
  1 s, at most 15 s** (or its exposure's limit, if longer), and one that never sends its first frame, or stops within the first
  three after a change, within 15 s. `VideoSilenceWatchTests` pins the rules, and `DALCameraVideoTests` both edges on a scripted
  body whose frames come after a number of waits: `LoweringTheExposureMidStreamWaitsOutTheFrameInFlight`,
  `AStreamWhoseFramePeriodIsItsReadoutKeepsStreaming` and `ASlowStreamThatGoesSilentIsBroughtBackAndItsFirstFrameAfterIsNoStall`;
- stalls and recoveries are counted beside the body's dropped frames and logged, each recovery with its rung and its gap.

**Measured on the camera** (the probe's soak arm, `TIANWEN_TOUPTEK_SOAK_MINUTES=25`, the capture loop streaming the centred 640x480
window at 5 ms with no recording, under the same load): in 25.0 minutes the stream stalled twice, both `GeneralError`, and a restart
brought each back, **6.5 s and 6.6 s from the last frame to the next**, 5.3 s of each in the stop of the stalled camera, as ToupTek.SDK
measured (1 s to notice, about 5.2 s in the stop, the rest the start). The soak read 292,594 frames, 195.0 a second over the whole
25 minutes with the two gaps in it; the hand-off replaced none, the body dropped none, and at most 38 frames waited for the reader.
Neither the reopen nor the reset was needed, so on the camera only the first rung has run; the others are pinned by the tests below.

It is generic: ZWO gets the same ladder, without the reset (`CanResetDevice` is false there). No adaptive speed: nothing precedes a
stall (none of 11 had a dropped frame in the 10 s before it) and speed 2 delivers the most frames even counting its stalls
(ToupTek.SDK's README, "The speed level, and whether to adapt it"). `DALCameraVideoTests` pins each rung, the give-up, `CameraRemoved`,
the settings restored, a body that goes silent after a restart, the ladder climbing and starting again, a stop during a recovery, and
the queue.

**What a SER looks like across a recovery.** Each frame carries the time its driver read it, so the two frames either side of a stall
are consecutive in the file and their trailer timestamps the gap apart (about 6.5 s for a restart): every frame taken is there and none
is invented, and nothing pretends the run was continuous. A rate read as frame count over the timestamp span counts the gap as time
without frames (a minute of frames at 196.8 with a 6.5 s gap reads about 178 over the span: 11,808 over 66.5 s), and a de-rotation
reads the true times. The recording names
each step longer than the exposure plus 1 s (`SerRecording.Gaps`) and logs them as it closes (`SerRecordingTests`).

**Recentering after a pan.** A pan blocks its caller 32 to 285 ms at 5 ms while the camera streams on into the SDK's ring, and the frames
still there when it returns are read after it, under the new `VideoRoi`, though they were taken under the old; the queue adds a few
more read before the pan. Measured on such a frame, the disk shows the offset the pan has just corrected, and the recenter loop would
correct it twice. `IVideoCameraDriver.IsUnderVideoRoi(frame)` answers whether a frame was taken under the window with every jog asked
for applied: the DAL loop counts jogs asked and applied, and after a pan asks for frames without waiting until the SDK has none, the
first frame it has to wait for being the first under the new window. `PlanetaryCapture` measures no other frame.

### Video through the node at the Saturn window (measured 2026-10-16)

**Issue:** #813 (TIA2's node leg).

Since P6 (#936) the GUI's planetary capture is its node's run: `tianwen-server` holds the camera, runs `PlanetaryCapture`, stacks and
records; the GUI starts it, sends the panel's controls and shows the live frame and the masters it reads back. The section above
measured the capture loop in one process. This one measures it through the node, with the GUI's own client watching.

**How.** `NodePlanetaryRateProbe` (Release, gated on `TIANWEN_TOUPTEK_RATE_PROBE=1`): a real `tianwen-server` (its keeper,
`--local-only`, a scratch `TIANWEN_DATA_ROOT` and a socket of its own) holds the ToupTek in a profile. The GUI's own
`AppSignalHandler`, composed as `GuiNodeHarness` composes it and connected local-only, starts the capture with the panel's
`StartVideoCaptureSignal` (640x480, 8 bits, 5 ms, gain 2252) and jogs it to (1716, 546) through the panel's controller. Its
`PlanetaryCaptureController` shows RAW the whole time, reading the camera's live frame through shared memory, its render thread
driven at 60 Hz. The recordings in the table below were asked for on the handler's own connection to the node
(`POST /api/v1/planetary/record`), the request a Record control sends, since the GUI had none yet; since #1344 the probe posts the
Record control's own `StartPlanetaryRecordingSignal` and follows the recording on the state the panel reads
([below](#the-record-control-through-the-node-at-the-saturn-window)). Each recording is read back off its SER as the section
above reads it (`SerRateReadBack`). In one session and in this order, so each comparison is adjacent in time: the section above's
in-process recording, a node recording with no view attached, the recordings with the view, the in-process recording again. The
node's own counts (the hand-off's replaced frames, its stalls) come from its log, read once it has exited.

**The bar (decided with the co-pilot):** each recording with the view at least 190 fps off its SER, nothing replaced by the
hand-off, nothing dropped by the body or the disk, no step between two frames' timestamps over 100 ms (20 frame times, a third
of the SDK's 64-frame ring), and within 3 % of the same session's in-process rate; a recording the camera stalled in is reported
apart and made again. Met, 60 s each, the box 13 to 23 % busy:

| Recording, 60 s | fps off the SER | Steps p50 / p99 / max | Replaced | Body / disk dropped | Hand-off peak | The view, live frames a second shown / read |
|---|---|---|---|---|---|---|
| in process, before | 196.8 | 5.08 / 5.21 / 15.2 ms | 0 | 0 / 0 | 5 | |
| node, no view | 196.8 | 5.08 / 5.20 / 13.8 ms | 0 | 0 / 0 | 14 | |
| node, view (run 1) | **196.8** | 5.08 / 5.21 / 5.8 ms | 0 | 0 / 0 | 2 | 30.1 / 30.3 |
| node, view (run 6) | **196.8** | 5.08 / 5.20 / 5.8 ms | 0 | 0 / 0 | 4 | 29.8 / 30.3 |
| node, view (run 7) | **196.8** | 5.08 / 5.21 / 5.8 ms | 0 | 0 / 0 | 5 | 30.0 / 30.3 |
| in process, after | 196.8 | 5.08 / 5.18 / 5.4 ms | 0 | 0 / 0 | 1 | |

196.8 is the camera's own rate at this window (200 at most at 5 ms), so the node adds nothing measurable: 100.0 % of in process
in every run. Every live frame the view read came through shared memory; the node samples one each 33 ms, so a view reads about
30 a second whatever the camera's rate, drop-to-latest, and a view that lags takes fewer without costing the recording a frame.
The node stacked 10 or 11 masters a minute meanwhile. Runs 2 to 5 met the camera's stalls (below) and are reported apart:
197.0 to 197.4 fps outside their gaps, nothing replaced. Through the whole node part, the node, its keeper and the client owned
no TCP listener and no UDP endpoint at all (the node listens on its socket alone, `--local-only`), sampled each second from
Windows' owner tables (`OwnedEndpoints`, which the probe first shows finding its own loopback sockets).

**With the box loaded at normal priority, the capture loses frames in process and through the node alike.** The same probe
with 20 s recordings, 32 spinning processes at normal priority on the 16 cores (100 % busy, 83 to 89 threads waiting), two
rounds per arm, the node with its Server GC as shipped (A) and with `DOTNET_gcServer=0` (B):

| Arm (32 spinners) | in process, before | node, no view | node, view | in process, after |
|---|---|---|---|---|
| A, round 1 | 196.8 fps, 0 replaced | 142.6, 1,169 (peak 64) | 140.7, 1,157 (peak 64) | 191.4, 108 |
| B, round 1 | 136.8, 1,217 | 196.6, 0 (peak 62) | 156.4, 732 (peak 64), a stall | 162.1, 656 |
| A, round 2 | 130.4, a stall | 135.1, 1,413 (peak 64) | 140.1, 1,237 (peak 64) | 152.9, 895 |
| B, round 2 | 196.7, 1 | 186.6, 230 (peak 64) | 125.6, 1,425 (peak 64) | 145.5, 1,016 |

Unloaded, both arms gave 196.8 fps with the view and nothing replaced, four runs of four. So the GC is not where the frames go,
and neither is the node: under normal-priority saturation the capture loop's reader is late past the hand-off's 64 frames
(320 ms) and the hand-off drops the oldest, in either process. The reader is a thread pool continuation at normal priority:
`PlanetaryCapture` runs its loop through `Task.Run` and `await foreach (... .ConfigureAwait(false))`, and the DAL's bounded channel
completes the reader's `WaitToReadAsync` on the pool, while the driver's own thread, at the highest priority, goes on reading (the
body dropped nothing). In the node the live stack's `ParallelFor` takes the same pool. The section above held at 100 % because
that load (other flights' sharpening grids, started by Task Scheduler) ran below normal priority. A first gate attempt under another
checkout's compiler and test suite (87 % busy) met one 101 ms step, over the bar by 1 ms; the gate above was run when that load
had gone, as the bar says.

**The camera stalled far more often than the section above measured.** Across the day's 55 streams (all at speed 2, the top
level every stream takes), 16 stalled, 19 times in about 1,970 s of streaming: one in about 104 s, against one in 750 s in the
section above's 25-minute soak and about one in 1,250 s in ToupTek.SDK's. A restart brought back 14 (6.5 to 6.9 s from the last
frame to the next), a close and reopen 3 (7.6 to 7.7 s), a reset 2 (23.2 s); all `GeneralError`. They came in periods (one
round of four probes had none, the next three had 13) and at any time into a stream (7 to 58 s). In process, where every run's
stream is the first after an open, 4 in about 670 s; through the node, its first stream after the connect 4 in 340 s and its
later streams 11 in 958 s: too few to say the first stream after an open, or the node, stalls more. Nothing but the probe's own
processes had `toupcam.dll` loaded. A finding for ToupTek.SDK; at 6.5 s a stall costs about 1,300 frames.

**A node on a scratch data root keeps its images under it.** `External.ImageOutputFolder` was the user's Pictures folder whatever
`TIANWEN_DATA_ROOT` said, so this probe's node would have recorded 3.6 GB a minute there, and a functional test's dark library had
been leaving six darks there on every run. It is now `Images` under an overridden data root
([runtime-data.md](../architecture/runtime-data.md)).

**The real window.** The gate drives the GUI's client, not its renderer, so it does not cover the draw. `tools/planetary-gui-evidence.ps1`
opens the real `tianwen-gui` on such a node, local-only, on the left half of the screen, asks a person for the clicks the GUI takes no
other way (the Live Session tab, the Planetary mode pill, the panel's settings, Start), jogs to the Saturn window, then asks for the
Record control's Record (#1344), takes the window by `PrintWindow` halfway through the recording and reads the SER back
(`NodePlanetaryRateProbe.TheRealGuiWindowShowsTheCaptureLiveWhileTheNodeRecords`); `-CheckOnly` proves it with nobody at the desk.
**`PrintWindow` takes a black client area while the user's session is disconnected** (`quser` says `Disc`, no monitor active):
the GUI draws (its log has frames) but nothing composes it, so the picture needs a session on a screen, which the person at the
desk brings.

### The Record control through the node at the Saturn window

**Issue:** #1344 (measured 2026-10-16, after #1609).

The same probe, its recordings with the view now started by the Record control's own `StartPlanetaryRecordingSignal` and followed
on the recording's state as the panel reads it (`NodePlanetaryCapture.Recording`), the tracker drained only for the request. One
60 s recording with the view (`TIANWEN_TOUPTEK_RECORD_RUNS=1`), the other worktrees' builds waited for, the box 14 to 21 % busy of
16 cores (another flight's training on about one core):

| Recording, 60 s | fps off the SER | SER frames over its span | Steps p50 / p99 / max | Replaced | Body / disk dropped | Host CPU |
|---|---|---|---|---|---|---|
| in process, before | 175.6, a stall (6.52 s), 197.0 outside it | 10,535 over 59.99 s | 5.08 / 5.20 / 6523 ms | 0 | 0 / 0 | 18 % |
| node, no view | 175.6, a stall (6.51 s), 197.0 outside it | 10,537 over 60.00 s | 5.08 / 5.21 / 6515 ms | 0 | 0 / 0 | 21 % |
| node, view, by the Record control's signal | **196.8** | 11,805 over 60.00 s | 5.08 / 5.20 / 7.5 ms | 0 | 0 / 0 | 21 % |
| in process, after | 196.8 | 11,805 over 60.00 s | 5.08 / 5.19 / 6.4 ms | 0 | 0 / 0 | 14 % |

The recording the control started is 100.0 % of the session's in-process rate, its view reading 30.3 live frames a second through
shared memory, the node stacking 11 masters meanwhile, and no TCP listener or UDP endpoint of the node, its keeper or the client.
The two stalls are the camera's `GeneralError` ones measured above, each brought back by a restart in 6.5 s, reported apart.

### The capture's reader above a busy machine (measured 2026-10-16)

**Issue:** #813 (TIA2's capture-priority leg).

The section above found that with the box saturated at normal priority the capture loses frames in process and through the node
alike, because the capture loop's reader was a thread pool continuation at normal priority. This leg lifts the reader, and only
the reader, and measures it the way node-rate did: the ToupTek at the Saturn window, 60 s recordings read back off their SER, in
process (`ToupTekFrameRateProbe.ARecordingAtTheSaturnWindowKeepsEveryFrame`) and through the node with the GUI's live view
(`NodePlanetaryRateProbe`), under 32 spinning processes at normal priority (100 % of the 16 cores), at below-normal priority, and
with the box quiet. A recording the camera stalled in is reported apart and made again (up to 5 more).

**What changed, and why each.**
- **The loop runs on a thread of its own at AboveNormal** (`CaptureReaderThread`): above any normal-priority load, below the
  driver's Highest stream thread and the OS. The thread is also the loop's `SynchronizationContext`, and the loop's awaits keep
  their context, so every frame resumes there. It waits for work by blocking on a semaphore, on purpose: an await would hand the
  wake back to the pool, a spin would hold a core with no frame coming. The process's priority is untouched, so the live stack and
  the view stay below the reader. Every driver gets it, the loop being one for all.
- **The DAL's frame wait keeps its reader's context**, so the stream's thread wakes the reader straight, never through the pool.
- **The recording's writer runs on a thread of its own too**: the loop shares its queue's lock and the byte pool's with it, and a
  pool writer held off while it held one held the loop up behind it.
- **The loop's running Debug lines are written by the pool** (a host's file log is one lock for the process, flushed under it).
- Tried and not kept: holding the process's GC at `SustainedLowLatency` while a capture runs (below: no better, and it is a
  process-wide mode).

**Measured first, in process under the 32 spinners, one clean recording each** (the reader's waits counted by the driver):

| Variant | fps off the SER | Replaced | Waits woken on the pool | Hand-off peak | Reader's longest gap |
|---|---|---|---|---|---|
| before: the loop on the pool | 154.1 to 182.5 | 464 to 1,290 | every one | 64 | |
| the loop on its own thread | 196.7 | 2 | all 5,423 | 64 | 340 ms |
| + the DAL's wait keeps its context | 196.7 | 6 | none of 6,218 | 64 | 357 ms |
| + the writer on its own thread | **196.8** | **0** | none | 32 | 165 ms |

The collections were not where the in-process gaps went: 5 to 8 a minute, 8 to 23 ms of pause in all.

**The A/B, 60 s recordings the camera did not stall in** (fps off the SER; replaced by the hand-off; the disk's drops; the most
frames that waited in the hand-off). The body dropped no frame in any of them. Before the fix nothing woke the reader but the pool.

| Load | Arm | Before the fix | After the fix |
|---|---|---|---|
| 32 normal spinners | in process | 182.5 (875 replaced), 178.3 (1,086; disk 21); all three of the in-process probe's own recordings stalled: 154.1, 158.4, 167.8 (464 to 1,290), peak 64 in all | **196.8** (0), **196.5** (0; disk 15), **196.7** (6, 0.05 %); peaks 31, 54, 64 |
| 32 normal spinners | node, view | 169.5 (1,793), 162.7 (2,076), peak 64 | below |
| below-normal | in process | 196.8 (0), 196.8 (0), 196.7 (1); peaks 52, 31, 64 | 196.8 (0) x3; peaks 11, 2, 1 |
| below-normal | node, view | 196.8 (0), 196.6 (9), 196.8 (0) | 196.8 (0) x3; peaks 4, 3, 4 |
| quiet | in process | 196.8 (node-rate's gate, earlier the same day) | 196.8 (0) x3; peaks 9, 1, 1 |
| quiet | node, view | 196.8 (0) x3 (node-rate's gate) | 196.8 (0) x3; peaks 3, 4, 5 |

**Through the node under the 32 spinners the bar is met by most recordings, not all.** Every recording reads at least 194.0 fps
off its SER and the body drops nothing, but the hand-off still replaced a fraction of a percent in some. Each set below is three
clean recordings with the view, in order:

| Set (32 normal spinners, through the node, with the view) | fps off the SER | Replaced (share) | Disk dropped |
|---|---|---|---|
| the fix, first set | 196.2, 196.5, 195.2 | 32 (0.27 %), 32 (0.27 %), **92 (0.79 %)** | 29, 17, 0 |
| + the Debug lines off the reader | 196.0, 196.2, 194.0 | 31 (0.26 %), 0, 26 (0.22 %) | 43, 33, 151 |
| Server GC (as shipped), again | 196.4, 196.6, 196.8 | 17 (0.14 %), 0, 0 | 43, 7, 0 |
| `DOTNET_gcServer=0` | 196.8, 196.6, 196.7 | 5 (0.04 %), 15 (0.13 %), 0 | 0, 9, 2 |
| Server GC + `SustainedLowLatency` while capturing (not kept) | 196.6, 196.5, 196.4 | 28 (0.24 %), 31 (0.26 %), 57 (0.48 %) | 9, 49, 0 |

Before the fix the same arm replaced 1,793 to 3,263 a minute. What is left is the node's collections: each capture there saw 3 to
9 gen 2 collections pausing 283 to 592 ms in all (in process, 0 to 4 and 11 to 77 ms). A pause holds every managed thread, the
reader and the writer included, and its suspension waits for every starved normal-priority thread (the live stack's workers among
them) to reach a safe point; a pause on top of a backlog passes the hand-off's 64 frames (a third of a second). The disk was not
slow: the frames it was charged with fell out of the writer's queue while its writes took 1 to 5 ms (865 ms once), the writer held
with the reader. `SustainedLowLatency` kept none of it away: its captures still saw 2 to 8 gen 2 collections pausing 489 to
1,327 ms in all, the node's heap 1.3 to 1.6 GB and its working set 1.4 to 2.7 GB, so it was taken out again; each capture now
says as it ends what the process collected and paused, and its heap and working set. **The node arm is accepted as measured**:
of the fifteen clean recordings with the view under the 32 spinners, five replaced under 0.1 % (four of them none), and the worst
replaced 0.79 %, at 100 % CPU. What allocates in the node's live stack and view, and the hand-off's depth (64 frames, about a
third of a second) against pauses of up to about 0.6 s, are a follow-up issue.

**The view and the node still answer under the load, and are bounded in the probe** (`NodePlanetaryRateProbe`): the node answers
`GET /api/v1/planetary` at p99 under 250 ms, a gain staged through the GUI's client is on the camera within 1 s outside a stall,
and the view shows at least what it showed before the fix at the same load (10.7 live frames a second under the 32 spinners, 24.6
below-normal, 29.8 quiet). Under the 32 spinners after the fix the node answered at p99 124 to 205 ms (181 to 194 before), gains
landed in 12 to 586 ms (23 to 556 before), and the view showed 13.4 to 20.1 frames a second (10.7 and 13.6 before) but once 9.1,
**under its floor**, which the gate failed and which stands as measured. Below-normal the node answered at p99 2.5 to 2.9 ms (32
to 62 before) and the view showed 29.7 to 29.9 (24.6 to 28.4); quiet, p99 0.5 ms and 29.8 to 30.0. The reader uses about a
quarter of a core at 197 frames a second and, with no frame coming, no processor time (`PlanetaryCaptureReaderTests`).

**The camera stalled as often after the fix as before.** Counted from every stream's own closing line, the stall rate is today's
and the box's, not the fix's: 11 stalls in 1,349 s of streaming before (one in 123 s; under the 32 spinners 7 in 655 s,
below-normal 4 in 694 s), 25 in 3,032 s after (one in 121 s; under the 32 spinners 10 in 1,272 s, below-normal 10 in 1,005 s,
quiet 5 in 755 s), against one in 1,250 s in ToupTek.SDK's soak. A stall costs a recording about 6.5 s.

**The probes.** The node gate's in-process baseline now takes only recordings that fail nothing by themselves (review round 2 of
#1609), and its window check reads the window the camera has from the DAL's own line once the body has it, not the jog that asked
for it. Each recording waits until no other build or test uses CPU (`TIANWEN_RATE_PROBE_QUIET_BUT`: the compiler server names no
worktree, and another worker's csc took 5 cores in a gated run). The driver counts where its reader woke (`VideoReaderWakesOnPool`),
a recording keeps its longest write, and each capture says as it ends where its frames ran and what the collections paused.

### Phase G: a mono filter-wheel sequence

**Issues:** #1344 (the Record control, first), #1340 (the sequence).

**The Record control (#1344, done 2026-10-16).** A RECORD section in the Planetary mode's panel, under the capture: a duration in
seconds (a minute to start), Record and Stop, and what the node says of its recording (the file, the frames written and dropped and
the time left; once it has ended, why, or why it failed). It is `PlanetaryRecordControl`, a declaration the GUI's panel and a test
paint alike, and it asks through `StartPlanetaryRecordingSignal` and `StopPlanetaryRecordingSignal`, which `AppSignalHandler` sends
through `PlanetaryCaptureController.RecordAsync` to the capture's OWN node, the one the controller started it on, never the view's
command target: a control of the run on screen goes to its own node (CLAUDE.md, Remote Rigs; review round 1 of #1610). The node
stays the recording's one writer, finishes it unwatched, and refuses in words a record with no capture going on or one already
recording; with no capture on show the controller says the same. The panel reads the recording back from the capture's state
(`NodePlanetaryCapture.Recording`, polled every 250 ms with the rest), the time left by this computer's clock. Neither is offered
with no capture on show: Record while the field states a duration, Stop while a recording goes on. Pinned by `PlanetaryRecordingThroughTheNodeTests` (through the node) and `PlanetaryRecordControlTests` (the presses). Its
settings beside the file are #1342's (Phase I); the sequence below records one file a step through the same route.

- **A run of the node** (`INodeRun`, as the planetary capture is), claiming the camera, the filter wheel and the focuser (the
  per-filter offset moves it). The mount is touched only by the recentre's nudges, which ask `DeviceOwnershipGate` as they do now.
  A refused start names the run going on.
- **Steps and sets.** A step is a filter, an exposure, a gain, an offset, a bit depth, and a frame count or a duration; a set is the
  steps in order. The run repeats the set N times, until a time, or until the planet sinks below an altitude, with an optional pause
  between sets and an optional refocus every K sets (Phase H).
- **One filter change, the session's.** `Session.SwitchFilterIfNeededAsync` (the wheel moved, its arrival awaited, the focus offset
  applied relative to the luminance filter) is hoisted into one routine both call; the offset rule is never copied.
- **A stream's controls are the ones set when it starts** (the DAL's video contract). Between steps the run stops the stream, moves
  the wheel, sets the controls and restarts, then drops the first frames after the change before it records.
- **Each step is one `SerRecording`**, named by `PlanetaryCaptureName.RecordingFileName` (planet, filter, time) into one folder per
  run. The survey then reads the run as one session per filter (#1336), and `planetary compose` takes the stacks as they are.
- **How long a step lasts is the planet's turn.** The stacker de-rotates a file only once the turn moves the disk's middle a pixel,
  and it reads north from the file's own quarters, which a bland globe cannot tell apart: on 11:34 Red the two ways read 0.01585 and
  0.01589. A file kept under that turn needs no north at all, and files far apart in time get theirs at compose, from two stacks of
  one filter. So the editor offers, for the planet and the pixel scale, the duration at which the turn reaches a pixel, computed from
  the ephemeris before any frame is taken.
- **The live view runs through every step**: the rolling stack shows each filter as it records.

**Needs** native video on the camera (#813). **Done when** a functional test on the fake camera, wheel and focuser runs two sets of
R, G and B into six SERs named per filter, the wheel moved five times and each offset applied, and the survey reads three sessions;
then a night on the bench.

### Phase H: focusing on the planet

**Issue:** #1341.

What exists: the focuser jog in the panel's FOCUSER section, and the live view for focusing by eye (#1111). Every routine that reads
focus reads stars: the session's V-curve autofocus, F6's planned video autofocus (`focus-tolerance.md`) and the focus aid (#1112).

**What reads a planet's focus.** R4 measured it (`planetary-restoration.md`): at 8 bits the Laplacian ranks frames near chance
against their true transfer, while the gradient grader and an FFT's power in a mid band (`FftHighBandEstimator`, 0.06 to 0.12 cycles
a pixel there) rank them at +0.87 to +0.99. The FFT score is the family SharpCap's FFT focus belongs to, the one the owner focused
with on 2026-10-07. Its band is a choice that matters: the finest band of an 8-bit frame is its noise, and R4's band was read at
one train's sampling, so the band is set as a fraction of the pupil's cutoff (0.45 to 0.63 cycles a pixel on the 2026-10-07 Saturn),
never in fixed cycles a pixel. One frame's sharpness is mostly the seeing's, so a
focus reading is a BURST's: a high percentile (the best tenth) of the score over one to two seconds of frames at one focuser position.
The limb fit's edge width on a stack of the burst's best frames is a second reading, in pixels, that does not depend on how much
contrast the planet shows that night.

- **One routine** for the burst reading (the gradient score and the FFT band score side by side until the validation below picks),
  behind a live readout and the autofocus alike.
- **Two new `SampleKind`s through the star autofocus's own `MetricSampleMap`**, whose fit is chosen by the kind:
  - `LimbBlur`, the limb fit's blur sigma in pixels on a stack of the burst's best frames, is a WIDTH like HFD. The seeing's blur
    and the defocus add in quadrature, `sigma(x) = sqrt(s0^2 + k^2 (x - p)^2)`, which is the hyperbola `Hyperbola.FindBestHyperbolaFit`
    already fits, so it drops in unchanged and reads absolutely. It costs a limb fit (2 to 3 s cold) a position.
  - `FftBandPower` is a POWER, highest at focus and relative only (its level is the planet's and the night's), so the hyperbola does
    not fit it. Under a blur near Gaussian at its band, `ln P(x) = A - B (x - p)^2`: a parabola in the log, a three-parameter
    relative fit, which is in effect SharpCap's relative curve (SharpCap offers no hyperbolic autofocus on it).
  - A burst is aggregated by a high PERCENTILE, which `AggregationMethod` gains (its `Maximum` throws in `MetricSampleMap.Aggregate`
    today, and one lucky frame is no reading). Both may stay: the power for the live readout, the blur for the autofocus.
- **A live readout** in the FOCUSER section, with the best so far marked: a V by hand.
- **An autofocus**: the focuser stepped across a range, a burst at each position, a peak fitted through the top readings, the focuser
  moved to it through its backlash rule (`BacklashEstimator`'s overshoot), and a last burst to verify.
- **Per-filter offsets** measured by the same routine and written to the filter wheel's profile (`InstalledFilter`), which the
  sequence's filter change then applies.
- **A refocus step** in the sequence: every K sets, or when the readout's trend falls below the set's start.

**Validated before it is used**, by a rule written first: on twins at a known defocus (`planetary degrade --defocus-nm`) and on the
fake camera's defocus (#818), the fitted peak lies within one step's depth of focus of zero defocus, and the reading falls
monotonically with defocus across the range swept. The same rule picks between the gradient and the FFT band score, and the band.
**Needs** native video (#813): at a single-exposure loop's rate one burst takes minutes.

### Phase I: a recording says how it was taken

**Issue:** #1342.

SharpCap writes a `.CameraSettings.txt` beside every SER (exposure, gain, black level, frame rate, the focuser's position and
temperature, the filter, the mount's position, the capture's start, middle and end), and the survey reads it into
`CaptureRecord.Settings`; that file is how the 2026-10-07 night's gain change and refocus were found. Our `SerRecording` writes the
SER header (camera, telescope) and the name (planet, filter, #1179), and nothing else.

- Write the settings beside each recording in a key=value form the survey's reader already takes.
- `planetary stack` carries the exposure and gain into the master's header, so a compose can say why two stacks' levels differ
  (#1337).
- **Done when** a recording on the fake camera round-trips its settings through the survey.

## Rules in full (moved from CLAUDE.md, 2026-10-09)

CLAUDE.md keeps one line per rule; this is the full text of its live-capture rules, moved verbatim
(the rest of its planetary rules are in `planetary-restoration.md`, under the same heading). "The plan
doc" for the Canon path is `planetary-stacking.md`, "Live-capture drivers and the recenter loop (shipped)".

- **Live capture: camera ADU normalises to [0,1] at the stream boundary** (`LiveCameraFrameStream.DeepCopy`);
  a colour sensor's video frame is a 1-channel Bayer mosaic, and the stream layout derives from the
  ACTUAL frame, **NOT** the camera's `SensorType`; **no driver call crosses onto the render thread**
  (it stages, the capture loop drains + applies). Preview defaults to **linear** (`StretchMode.None`).
- **Read the plan doc before touching the Canon path**: it is a list of five things that fail
  SILENTLY. **Recentering is opt-in** (the user, 2026-09-28; it costs the loop about 15 ms a frame at full frame), and
  so is the mount jog, whose **sign is uncalibrated**.
- **A stream's depth, high-speed readout and USB bandwidth are its own** (`VideoCaptureOptions`), never the camera's
  settings, so a planetary capture in 8 bits leaves the next deep-sky frame in 16; a DAL stream takes the whole
  bandwidth while it runs (16 bits at the 50 a connect sets was 31.9 frames a second on an ASI462MC, 8 bits at 100 is
  136). **A frame declares the full scale of the depth it was READ OUT in** (`DALCameraDriver.MaxAduFor`): the camera's
  16-bit full scale on an 8-bit frame puts it at a sixteenth of its brightness in the live stack.
- **The capture loop is `PlanetaryCapture` (Lib), ONE for the GUI and the node, and for the Preview's live view** (`LiveCaptureKind.LiveView`, `NodeLiveView`, #1111: the whole sensor at the Preview's binning, no frame kept): the camera, the stream, the
  live controls and the recenter. The GUI's `PlanetaryCaptureController` starts the node's run, sends the panel's
  controls only as they CHANGE (`NodePlanetaryCapture`: the panel pushes its recenter every frame) and shows the
  masters the node streams through the same `LiveStackPreviewSource` a SER playback uses (`NodeMasters`); the node
  stacks it on its run's own task (`NodePlanetary`) and serves the live frame, copied at display rate by
  `FrameSampler`, and the linear master as frames (`planetary/live`, `planetary/master`). **A live view STREAMS them
  and the client ASKS for each frame** (`FrameStreamWire`, drop-to-latest): a send is done once the kernel has the
  bytes, and a loopback socket buffers dozens of planetary frames, so a node that sent on its own fed a reader that
  paused every stale frame of the pause (measured). **Over this machine's socket every frame goes through shared
  memory** (P4b, #932), a stream's and a `/frames/{source}/latest` fetch's alike: the node answers with the slot the
  frame waits in (`FrameSlotDto`), a section per stream or per fetched source (`NodeFrameSlots`) and a seqlock per slot,
  the slot holding `FrameWire`'s own bytes so one reader serves both carriers; a TCP client, a host without
  `AddNodeSharedMemory`, or a section the system refuses gets bytes, and `FRAME-AVAILABLE` stays a hint.
  **A recording to disk (`SerRecording`) never slows the capture**:
  the loop converts and queues, a writer task of its own does the disk, and a frame the disk cannot take is dropped
  and counted. It finishes its duration unwatched. **It says what it was taken of and through** (#1179): its name is the planet
  the mount points at and the filter in the beam (`PlanetaryCaptureName.RecordingFileName`, `PointedAt`), its SER header the camera
  and the OTA's telescope (`TelescopeField`), which the viewer's Auto stack and `planetary stack` read back.
  It claims only the camera, so **a recenter nudge asks `DeviceOwnershipGate` over the mount first**.
