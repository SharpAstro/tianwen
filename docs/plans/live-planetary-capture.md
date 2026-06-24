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
  PlanetaryCaptureController (UI.Abstractions)    capture loop + Tick() (follow latest + publish + sharpen)
        | owns
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
| B | rapid-exposure fallback + Fake video (`SyntheticPlanetRenderer`) + `PlanetaryCaptureController` + 🪐 tab + signals | no | backend DONE; GUI tab remaining |
| C | `PlanetaryRecenterController` (ROI auto + mount opt-in) + `JogMountSignal` + manual nudge | no | TODO |
| D | native ZWO + QHY raw video (DAL `ICMOSNativeInterface` + ring buffer + ROI-jog bypass) | **yes (DAL -> 2 SDKs -> TianWen)** | TODO |
| E | Canon Live View `IVideoCameraDriver` (JPEG) via FC.SDK | no | TODO |
| F | decompose the ~3000-line `ImageRendererBase` (see below) | no | TODO |

## Phase F: decompose `ImageRendererBase` (~3000 lines)

`ImageRendererBase<TSurface>` has grown into one file covering layout, toolbar + dropdowns, file list,
WCS grid labels, star/object/annotation overlays, histogram, info panel (+ WB & wavelet sliders),
status bar, SER transport bar, and all keyboard/mouse handling. It's already organised by `// ----`
region banners, so the lowest-risk decomposition is **`partial class` files by concern** (no behaviour
change, pure file organisation):

- `ImageRendererBase.Layout.cs` — `ComputeLayout` / `ComputeImagePlacement` / `ViewerLayout` / content region
- `ImageRendererBase.Toolbar.cs` — toolbar + dropdown overlays + hit-test
- `ImageRendererBase.FileList.cs` — file-list sidebar + hit-test
- `ImageRendererBase.Overlays.cs` — grid labels + star/object/WCS-annotation overlays
- `ImageRendererBase.Histogram.cs` — histogram rect + render + LOG button
- `ImageRendererBase.InfoPanel.cs` — info panel + WB sliders + wavelet sliders
- `ImageRendererBase.StatusBar.cs` — status bar
- `ImageRendererBase.Transport.cs` — SER transport bar + scrub
- `ImageRendererBase.Input.cs` — keyboard + mouse handlers
- `ImageRendererBase.cs` — the abstract GPU seam + `Render` orchestration + shared fields

**Real dedup (not just splitting):** the WB sliders, the 6 wavelet-layer sliders, and the transport scrub
are all the same widget — a horizontal **press / drag / release track** with a fill + handle + a
cursor-X -> value mapping against a captured track rect. Extract a single `TrackSlider` helper (render +
`BeginDragAt`/`UpdateDrag` against a `RectF32`) and have all three call it. That removes the triplicated
slider math, not just the file length. **`magic` (source generators) is NOT warranted** — there's no
repetitive generated surface here; partials + one extracted widget is the right tool.

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
- `TianWen.UI.Abstractions/PlanetaryCaptureController.cs` -- owns the camera capture loop + the frame
  stream; `Tick()` (render thread) follows latest + publishes the master + pushes wavelet-sharpen changes.
  Native video or rapid-exposure fallback, picked by capability.
- `ViewerState.BuildWaveletOptions()` -- the single source for live-stack wavelet options, now shared by
  `ViewerController` (tianwen-fits) and `PlanetaryCaptureController` (GUI).
- Tests: `FakeCameraVideoTests` (renderer determinism/brightness/sharpness; fake video frame shape, ROI
  jog pans the planet, end-to-end into the rolling stacker), `PlanetaryCaptureControllerTests`
  (capture -> live stack -> master end to end; Start idempotency / Stop safety), plus the Phase A
  `LiveCameraFrameStreamTests`.

## GUI tab (Phase B) — what's wired vs the decided design

**Wired + working (2026-06-24):** `GuiTab.Planetary` (emoji `\U0001FA90`) + `TabOrder` + `TabChrome` +
`Ctrl+Y`; `RenderContent`/`ActiveTab` dispatch; `StartVideoCaptureSignal`/`StopVideoCaptureSignal` ->
`AppSignalHandler` -> the controller (`PlanetaryCaptureActions.ConfigureRoi`); DI registration; DEBUG
inspector signal factories (`StartVideoCapture`/`StopVideoCapture`). **Capture + stack confirmed live in
the GUI** (frames flowing, fps/frame/dropped readout). A first-cut display used `VkMiniViewerWidget`
(rect-bounded) — REPLACED by the decision below.

**Critical fix during bring-up:** the stream layout must come from the ACTUAL first frame, not the
camera's `SensorType`. A colour sensor whose video frames are mono (the fake; a native colour stream is a
1-channel Bayer mosaic) was sizing the stream as RGB and dropping every frame ("nothing shows up").
Pinned by `Color_sensor_with_mono_video_frames_still_stacks`. Exposure is floored to 1 ms.

### Decided design (user, 2026-06-24): the planetary tab and the FITS viewer SHARE the viewer widget

The 🪐 tab must reuse the **real** `ImageRendererBase` viewer (same widget the FITS viewer uses), not a
stripped mini viewer — so it gets the full stretch pipeline + RAW/STACK toggle + 6 wavelet-sharpen
sliders + WB + histogram + zoom/pan "for free", and the two surfaces can't drift. The blocker: the shared
viewer hard-anchors its layout to the full window (`tianwen-fits` is full-screen), so it must learn to
render at a **content-rect origin** to embed in the GUI content area (below the sidebar + top status bar).

Remaining steps (in order):

1. **DONE + verified (2026-06-24): common-model layout in `ImageRendererBase`.** Rather than threading an
   origin offset through every hand-placed chrome site (the `cursor += h` anti-pattern the Layout DSL exists
   to kill), the widget now arranges its **whole** region — toolbar + content + status bar — as ONE
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
     ToolbarButtons` on the base (render + hit-test both read it). `VkPlanetaryTab` overrides it to the
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

Remaining: (4c) ROI PiP + `RoiConstraints` (queried from the camera) + draw-on-stream; (4d) fake
`IVideoSimulationControls` test panel (defocus/seeing/noise/drift); then GOTO + Solve&Center, and Phase C
(COM recenter).

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
  source:** the ZWO rule is documented ONLY in the raw SDK header (`zwo-sdk-nuget/include/ASICamera2.h:438-439`:
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
5. **Phase C — mount jog recenter + calibration** (see Phase C row): COM -> ROI auto + mount-jog opt-in +
   directional nudge buttons + a short calibration.

Verify unattended via the fake-device + `sdl-ui-inspector` harness (per CLAUDE.md): open 🪐, post
`StartVideoCapture`, assert frames climb + a stretched (not blown) planet renders + sliders present.
