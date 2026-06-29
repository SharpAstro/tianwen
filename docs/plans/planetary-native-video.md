# Planetary Native Video: ZWO + QHY raw streaming (Phase D) and Canon Live View (Phase E)

Companion to [`live-planetary-capture.md`](live-planetary-capture.md). That plan built the whole live
planetary lucky-imaging slice (capture contract, frame stream, rolling-window stack, the planetary Live
Session mode, COM recenter) against the `FakeCameraDriver` and a universal rapid-exposure fallback -- **no
external SDK releases**. Phases A/B/C/F are shipped.

This doc fleshes out the two **hardware** phases that were only one-line rows in that plan: real camera
video behind the same `IVideoCameraDriver` contract. They are pure drop-in implementations of an interface
the whole pipeline already consumes -- nothing above `IVideoCameraDriver` changes.

- **Phase D** -- native ZWO + QHY raw video through the shared `DALCameraDriver` (a `TianWen.DAL` ->
  `ZWOptical.SDK` + `QHYCCD.SDK` -> `TianWen` NuGet release chain).
- **Phase E** -- Canon Live View (JPEG) via FC.SDK; no DAL release.

## The contract being implemented (recap)

`IVideoCameraDriver : ICameraDriver` (`src/TianWen.Lib/Devices/IVideoCameraDriver.cs`). Seven members:

| Member | Semantics |
|---|---|
| `bool CanVideoCapture` | true if the camera can stream |
| `bool CanJogRoi` | true if the readout window can be panned mid-stream without restart |
| `int DroppedFrames` | SDK-reported drops since the current capture started; 0 if not streaming / unsupported |
| `RoiRect VideoRoi` | live readout window (origin + size); the recenter loop reads remaining pan range from it |
| `IAsyncEnumerable<Image> CaptureVideoAsync(VideoCaptureOptions, ct)` | start -> yield -> stop folded into one; cancel = stop; one concurrent stream |
| `ValueTask JogRoiAsync(dx, dy, ct)` | pan the window (the fast mount-free recenter actuator); snap to alignment, clamp to sensor |
| `ValueTask ApplyVideoControlsAsync(VideoCaptureOptions, ct)` | live-tune exposure / gain on a running stream |

**Reference implementation: `FakeCameraDriver`** (`src/TianWen.Lib/Devices/Fake/FakeCameraDriver.cs:1346-1567`).
Both native drivers mirror its shape exactly:

- A single-concurrent-stream gate (`Interlocked.CompareExchange(ref _videoActive, 1, 0)`), reset in `finally`.
- ROI **size** re-read from `NumX`/`NumY` every frame (so a live resize takes effect next frame); ROI
  **position** owned by the capture-loop thread and moved only by `JogRoiAsync` drained on that loop.
- Cancel is the stop signal (`yield break` on OCE; disconnect mid-stream also breaks cleanly).
- `RoiConstraints` reported from the camera (`RoiConstraints`/`RoiRect` in `src/TianWen.Lib/Devices/RoiConstraints.cs`)
  so the ROI picker snaps to the vendor's alignment rule. The fake already reports ZWO-style 8/2.

`VideoCaptureOptions(TimeSpan Exposure, short? Gain = null, bool HighSpeedMode = true)`.

---

# Phase D -- native ZWO + QHY raw video

## D.0 Where the code goes (and why it covers both vendors from one place)

```
  IVideoCameraDriver  (src/TianWen.Lib/Devices/IVideoCameraDriver.cs)  -- exists
        ^ implemented by
  DALCameraDriver<TDevice, TDeviceInfo>  (src/TianWen.Lib/Devices/DAL/DALCameraDriver.cs)  -- ICameraDriver today
        |  where TDeviceInfo : struct, ICMOSNativeInterface
        |  drives the native SDK through the struct:
  ICMOSNativeInterface  (TianWen.DAL/ICMOSNativeInterface.cs)  -- single-frame only today; ADD video methods
        ^ implemented by
   +----------------------------+-----------------------------+
   | ASI_CAMERA_INFO            | QHYCCD_CAMERA_INFO          |
   | (ZWOptical.SDK, readonly   | (QHYCCD.SDK, mutable struct;|
   |  struct; ASICamera2.cs)    |  QHYCamera.cs:47)           |
```

Because `DALCameraDriver` is generic over `TDeviceInfo : struct, ICMOSNativeInterface`, putting the streaming
verbs on `ICMOSNativeInterface` makes the **video loop live once in `DALCameraDriver`** and dispatch to either
vendor's native calls through the struct. ZWO and QHY are both covered by a single `IVideoCameraDriver`
implementation. This is the "from one place" backbone the plan calls for.

The single-frame path already proves the pattern: `DALCameraDriver.StartExposureAsync`
(`DALCameraDriver.cs:787`) calls `_deviceInfo.SetROIFormat` / `SetStartPosition` / `StartLightExposure` /
`GetDataAfterExposure` (the `ICMOSNativeInterface` members at `ICMOSNativeInterface.cs:58-78`), and
`DownloadImage` (`DALCameraDriver.cs:685`) marshals the native buffer into a recycled `float[,]` Channel. The
video path is the streaming analogue of exactly this.

## D.1 (DAL) extend `ICMOSNativeInterface` with the streaming verbs

`ICMOSNativeInterface` (`TianWen.DAL/ICMOSNativeInterface.cs`) has **no** video methods today -- it is purely
single-frame (`StartLightExposure`/`StartDarkExposure`/`StopExposure`/`GetExposureStatus`/`GetStartPosition`/
`SetStartPosition`/`GetROIFormat`/`SetROIFormat`/`GetDataAfterExposure`). Add the minimal streaming surface:

```csharp
// New on ICMOSNativeInterface (sync native calls; the async loop lives in DALCameraDriver):
CMOSErrorCode SetStreamMode(bool live);                         // ZWO: no-op + ASIStartVideoCapture toggles; QHY: SetQHYCCDStreamMode(1/0)
CMOSErrorCode StartVideoCapture();                              // ZWO: ASIStartVideoCapture; QHY: BeginQHYCCDLive
CMOSErrorCode StopVideoCapture();                               // ZWO: ASIStopVideoCapture; QHY: StopQHYCCDLive
CMOSErrorCode GetVideoFrame(IntPtr buffer, int bufferSize, int waitMs); // ZWO: ASIGetVideoData; QHY: GetQHYCCDLiveFrame
bool TryGetDroppedFrames(out int dropped);                      // ZWO: ASIGetDroppedFrames (NEEDS BINDING); QHY: false
bool CanLivePanRoi { get; }                                     // ZWO: true (ASISetStartPos live); QHY: false (verify on HW)
```

**Decision (recommended): extend `ICMOSNativeInterface` rather than introduce a separate
`ICMOSVideoInterface`.** The generic constraint on `DALCameraDriver` already requires `ICMOSNativeInterface`,
both vendors support streaming natively, and a capability gate is unnecessary when every concrete
implementer can satisfy it. Alternative (flag in a Decisions section below): a separate optional
`ICMOSVideoInterface` the driver probes with `_deviceInfo is ICMOSVideoInterface` -- cleaner if a future
CMOS device lacks video, but neither current vendor does, so it would be speculative generality.

Adding interface members is a **breaking change for any other implementer** of `ICMOSNativeInterface`; the
only two are `ASI_CAMERA_INFO` and `QHYCCD_CAMERA_INFO`, both updated in this phase. Bump `TianWen.DAL` minor.

## D.2 (ZWOptical.SDK) bind the missing P/Invoke + wrap the video calls on `ASI_CAMERA_INFO`

The video P/Invokes already exist as top-level `ASICamera2` statics, but are **not** exposed through
`ASI_CAMERA_INFO` / `ICMOSNativeInterface`:

| Native call | Status | Site |
|---|---|---|
| `ASIStartVideoCapture(int)` | bound | `ASICamera2.cs:487` |
| `ASIStopVideoCapture(int)` | bound | `ASICamera2.cs:491` |
| `ASIGetVideoData(int, IntPtr, int, int)` | bound | `ASICamera2.cs:494` |
| `ASISetStartPos(int, int, int)` | bound | `ASICamera2.cs:478` (live-pannable per header) |
| `ASIGetStartPos(int, out, out)` | bound | `ASICamera2.cs:482` |
| `ASISetROIFormat` / `ASIGetROIFormat` | bound | `ASICamera2.cs:466` / `:470` |
| `ASISetCameraMode` / `ASIGetCameraMode` | bound | `ASICamera2.cs:530` / `:526` |
| **`ASIGetDroppedFrames(int, out int)`** | **ABSENT** | declared `ASICamera2.h:529`, not bound -- ADD a `LibraryImport` |

Work:
1. Add the `[LibraryImport]` for `ASIGetDroppedFrames` (mirror the other partial declarations).
2. Implement the new `ICMOSNativeInterface` members on `ASI_CAMERA_INFO` (it already implements the interface,
   `ASICamera2.cs:13`), each calling the static with the cached camera id:
   - `SetStreamMode(live)` -> no-op (ZWO enters video mode via `ASIStartVideoCapture`; not a mode flag).
   - `StartVideoCapture()` -> `ASIStartVideoCapture(id)`; `StopVideoCapture()` -> `ASIStopVideoCapture(id)`.
   - `GetVideoFrame(buf, size, waitMs)` -> `ASIGetVideoData(id, buf, size, waitMs)`.
   - `TryGetDroppedFrames(out d)` -> `ASIGetDroppedFrames(id, out d)`.
   - `CanLivePanRoi => true` (header `ASICamera2.h:476-477`: "you can call this API to move the ROI area when
     video is streaming"). The existing `SetStartPosition` (= `ASISetStartPos`) is the live jog.

**ROI rules (header, authoritative):** width % 8 == 0, height % 2 == 0; ASI120 (USB 2.0) additionally
width * height % 1024 == 0. `ASISetROIFormat` requires **stopping capture first** (`.h:432`); `ASISetStartPos`
is live (`.h:476`). Buffer size: 8-bit mono = w*h, 16-bit = w*h*2, RGB24 = w*h*3 (`.h:618-625`). `waitMs` -1
waits forever; recommend `exposure*2 + 500 ms`.

These map cleanly onto `RoiConstraints { WidthStep=8, HeightStep=2, OriginStepX=8, OriginStepY=2 }` already
defined for the fake.

## D.3 (QHYCCD.SDK) wire the bound-but-unused live calls on `QHYCCD_CAMERA_INFO`

The live P/Invokes are present but never called (only single-frame is used today):

| Native call | Status | Site |
|---|---|---|
| `SetQHYCCDStreamMode(IntPtr, byte)` | bound; called only with `mode=0` (`QHYCamera.cs:154, 418`) | `QHYCamera.cs:721` |
| `BeginQHYCCDLive(IntPtr)` | bound, unused | `QHYCamera.cs:807` |
| `GetQHYCCDLiveFrame(IntPtr, out w,h,bpp,channels, IntPtr)` | bound, unused | `QHYCamera.cs:811` |
| `StopQHYCCDLive(IntPtr)` | bound, unused | `QHYCamera.cs:815` |
| `SetQHYCCDResolution(IntPtr, x, y, xSize, ySize)` | bound (origin + size together) | `QHYCamera.cs:767` |

Work -- implement the new `ICMOSNativeInterface` members on `QHYCCD_CAMERA_INFO` (mutable struct,
`QHYCamera.cs:47`):
- `SetStreamMode(live)` -> `SetQHYCCDStreamMode(handle, (byte)(live ? 1 : 0))`. **Order matters**: stream
  mode must be set **before** `BeginQHYCCDLive` (and the single-frame path's existing `mode=0` calls stay).
- `StartVideoCapture()` -> `BeginQHYCCDLive(handle)`; `StopVideoCapture()` -> `StopQHYCCDLive(handle)`.
- `GetVideoFrame(buf, size, waitMs)` -> loop `GetQHYCCDLiveFrame(handle, out w,h,bpp,ch, buf)` (QHY returns a
  non-success code until a frame is ready; the DAL loop polls with a short sleep, bounded by `waitMs`).
- `TryGetDroppedFrames(out d)` -> `d = 0; return false` (QHY SDK has no direct dropped-frame counter; the
  driver can still infer gaps from timestamps if needed -- out of scope for v1).
- `CanLivePanRoi => false` initially: QHY sets ROI via `SetQHYCCDResolution` (origin + size together) and
  live-pan support is **not confirmed** by the header. Conservatively report false -> the recenter loop falls
  back to mount jog for QHY. Revisit on hardware (see Risks); if `SetQHYCCDResolution` works mid-live, flip to
  true and route `SetStartPosition` to it.

`CONTROL_ID.CAM_LIVEVIDEOMODE` (`QHYCamera.cs:660`) / `CAM_SINGLEFRAMEMODE` (`:659`) are capability-query IDs
(used with `IsQHYCCDControlAvailable`) -- gate `CanVideoCapture` on `CAM_LIVEVIDEOMODE` being available.

## D.4 (DALCameraDriver) implement `IVideoCameraDriver` -- the one shared video loop

Change the class declaration (`DALCameraDriver.cs:14`) to also implement `IVideoCameraDriver`. Add, mirroring
`FakeCameraDriver`:

- **State**: `int _videoActive` (single-stream gate), capture-loop-owned `_videoRoiStartX/Y/Width/Height`,
  `int _droppedFrames`, current exposure ticks. The ROI-window fields are owned by the capture loop (lock-free
  by ownership, exactly as the fake documents at `FakeCameraDriver.cs:1355-1357`).
- **Buffers**: today there is a single `_nativeBuffer` (`Marshal.AllocCoTaskMem`, `DALCameraDriver.cs:38`,
  `AllocateNativeBuffer:958`) for the one single-shot frame. The stream needs to overlap SDK fill with
  managed decode: a **double native buffer** (fill frame N+1 while marshalling frame N into a `float[,]`).
  Correct-first version: one reusable native buffer + one reusable `float[,]` per yield (the consumer
  `LiveCameraFrameStream` copies on push, so the driver may recycle its `float[,]` immediately after the
  yield returns -- reuse the existing `_freeBuffers` recycle pool at `DALCameraDriver.cs:45`). The
  double-buffer is the throughput optimization, not a correctness requirement.
- **`CaptureVideoAsync`**: gate `_videoActive`; apply gain + exposure via `SetControlValue(CMOSControlType.Gain/Exposure, ...)`
  (the same setters `StartExposureAsync` uses, `DALCameraDriver.cs:866-887`); `SetROIFormat` + `SetStartPosition`
  to the configured ROI; `SetStreamMode(true)`; `StartVideoCapture()`; then loop: `GetVideoFrame(buf, size,
  waitMs)` -> marshal to `float[,]` (reuse the `DownloadImage` switch on bit depth, `DALCameraDriver.cs:718-750`)
  -> wrap as `Image` -> `yield return`. `finally`: `StopVideoCapture()`, `SetStreamMode(false)`, clear
  `_videoActive`. Run off the render thread (the controller already does).
- **`JogRoiAsync`**: ZWO (`CanLivePanRoi`) -> `SetStartPosition` live, snapped to the alignment step and
  clamped to the sensor (the fake's `& ~1` becomes `& ~7` for width-step-8 X). QHY -> not live-pannable in v1,
  so `CanJogRoi` is false and the recenter loop never calls this.
- **`ApplyVideoControlsAsync`**: stage new exposure/gain; the loop applies them between frames via
  `SetControlValue` (exposure is live on both vendors; gain too). ROI **size** changes go through `NumX`/`NumY`
  -> the loop detects the change, must `StopVideoCapture` -> `SetROIFormat` -> `StartVideoCapture` (ZWO
  `ASISetROIFormat` requires stop-first; QHY restart is safe). ROI **position** through `JogRoiAsync`.
- **`VideoRoi`**: report `(_videoRoiStartX, _videoRoiStartY, _videoRoiWidth, _videoRoiHeight)` while streaming,
  sensor-sized window otherwise (mirror `FakeCameraDriver.cs:1419-1425`).
- **`DroppedFrames`**: `_deviceInfo.TryGetDroppedFrames(out d) ? d : 0`.
- **`RoiConstraints`** override: ZWO `8/2/8/2`; QHY its real steps (TBD from QHY behavior, default to the
  sensor-free `RoiConstraints.ForSensor` until confirmed).
- **`CanVideoCapture`**: ZWO -> true; QHY -> `IsQHYCCDControlAvailable(CAM_LIVEVIDEOMODE)`.

**Mutual exclusion with single-shot**: streaming and `StartExposureAsync` must not run together (the camera is
in one mode). Gate `StartExposureAsync` to throw / no-op while `_videoActive == 1`, mirroring the fake's
"stream OR expose" rule (`FakeCameraDriver.cs:1352`).

## D.5 ROI semantics summary (the load-bearing per-vendor difference)

| Operation | ZWO | QHY |
|---|---|---|
| Change ROI **size** mid-stream | stop -> `ASISetROIFormat` -> start | stop -> `SetQHYCCDResolution` -> start |
| Pan ROI **position** mid-stream | **live** `ASISetStartPos` (no restart) | not confirmed -> stop/restart or disabled (`CanJogRoi=false`) |
| `CanJogRoi` (v1) | true | false (mount jog fallback) |
| Dropped-frame count | `ASIGetDroppedFrames` (after binding) | none (0) |

The recenter loop already handles `CanJogRoi == false` by falling back to mount jog (Phase C,
`PlanetaryRecenterController.Decide`), so QHY degrades gracefully to coarse mount recenter with zero extra
work.

## D.6 Release chain (the NuGet dance -- the High-risk part)

`DAL -> ZWOptical.SDK + QHYCCD.SDK -> TianWen`, each step waiting for NuGet before the next (per
`release-lib` skill + the no-local-nupkg rule):

1. Bump + push **`TianWen.DAL`** (new `ICMOSNativeInterface` members). Wait for NuGet (`dotnet package search
   TianWen.DAL --exact-match`). `TianWen.DAL` is **not** sibling auto-detected (CLAUDE.md table) -- CI consumes
   it as a `PackageReference` only, so the published version is mandatory before downstream builds.
2. In parallel, repin + bump + push **`ZWOptical.SDK`** (bind `ASIGetDroppedFrames` + `ASI_CAMERA_INFO` video
   members) and **`QHYCCD.SDK`** (wire the live calls on `QHYCCD_CAMERA_INFO`) to the exact published DAL
   version. Wait for both on NuGet.
3. Bump **`TianWen`** `src/Directory.Packages.props` to the three exact published versions; implement
   `DALCameraDriver : IVideoCameraDriver`; push.

None of ZWOptical.SDK / QHYCCD.SDK / TianWen.DAL is in the `UseLocalSiblings` auto-detect set, so local dev
also consumes published NuGet packages for them -- there is no ProjectReference shortcut. Plan for the full
publish latency.

## D.7 Tests + hardware smoke

- **Unit (no hardware)**: the DAL video loop's vendor-agnostic logic (buffer marshalling per bit depth, ROI
  size-change restart, `JogRoiAsync` snap/clamp, single-stream gate, dropped-frame passthrough) is testable
  against a stub `ICMOSNativeInterface` that returns synthetic frames -- mirror the existing
  `LiveCameraFrameStreamTests` / `FakeCameraVideoTests` in-memory approach. No SDK needed for these.
- **Hardware smoke (D itself)**: real ZWO + QHY raw-video run -- fps, dropped frames, live ROI pan mid-stream
  (ZWO), ROI size change, exposure/gain live-tune. Confirm the stacked planet renders through the existing
  preview. Read the Debug log for the capture heartbeat + per-stack timing already emitted by the controller.

## D Risks / open decisions

- **Interface extension vs separate `ICMOSVideoInterface`** (D.1). Recommended: extend. Flag for sign-off.
- **QHY live ROI pan** (D.3/D.5): unconfirmed by header. v1 ships `CanJogRoi=false` for QHY (mount fallback);
  promote to live-pan only after hardware confirms `SetQHYCCDResolution` works mid-live.
- **Double-buffer vs single-buffer** (D.4): ship single-buffer correct first, add the double-buffer if fps is
  buffer-bound (the stack is align-bound at ~85-89%, so the native fill is unlikely to be the bottleneck).
- **Bit depth**: planetary lucky-imaging usually runs 8-bit high-speed for fps; the loop must honor the
  `VideoCaptureOptions.HighSpeedMode` flag (ZWO `CMOSControlType.HighSpeedMode`, already in `CMOSControlType`).

---

# Phase E -- Canon Live View (JPEG)

Canon EOS bodies stream a live host feed only one way through FC.SDK: **Live View (EVF) JPEG**. That feed has
two regimes -- full-frame (downscaled, framing quality) and **5x/10x zoom** (a near-1:1-pixel crop of a small
region). The zoom regime is the real planetary mode for a DSLR and is what this phase targets (E.3); the
full-frame regime is the framing / centering view. In-camera **movie recording** (the AVI/MP4/MOV the body
writes to its card) is a different thing and is **not** the live-stream source -- see E.4 for why, and for the
separate offline path that *can* use a recorded movie.

## E.0 Layering

```
  IVideoCameraDriver
        ^ implemented by
  CanonCameraDriver  (src/TianWen.Lib/Devices/Canon/CanonCameraDriver.cs)  -- ICameraDriver today (single-shot CR2)
        |  holds CanonCamera _camera
  CanonCamera  (FC.SDK, src/FC.SDK/Canon/CanonCamera.cs)  -- Live View already complete
```

No DAL. FC.SDK does not implement `ICMOSNativeInterface`; Canon is a separate hierarchy. The Live View API is
already shipped and unwired -- Phase E is purely a TianWen-side driver addition.

## E.1 `CanonCameraDriver` implements `IVideoCameraDriver`

The FC.SDK surface is complete:

| FC.SDK method | Returns | Site |
|---|---|---|
| `StartLiveViewAsync(ct)` | `Task<EdsError>` (sets `Evf_OutputDevice = PC`, `InitiateViewfinderAsync`) | `CanonCamera.cs:382` |
| `GetLiveViewFrameAsync(ct)` | `Task<(EdsError Error, byte[] JpegData)>` -- **raw JPEG bytes** | `CanonCamera.cs:391` |
| `StopLiveViewAsync(ct)` | `Task<EdsError>` (`TerminateViewfinderAsync`, resets `Evf_OutputDevice = TFT`) | `CanonCamera.cs:394` |

Work -- add `IVideoCameraDriver` to the declaration (`CanonCameraDriver.cs:23`) and:
- **`CanVideoCapture`** -> true (any Live-View-capable EOS; gate on connect succeeding if a body rejects EVF).
- **`CaptureVideoAsync`**: gate a single-stream `_videoActive`; ensure mutual exclusion with the single-shot
  `StartExposureAsync` path (`CanonCameraDriver.cs:498` -- Tv/Bulb + background CR2 download must not run while
  streaming). `StartLiveViewAsync`; for planetary work set the EVF zoom to 5x/10x (E.3); loop
  `GetLiveViewFrameAsync` -> JPEG-decode to `Image` (E.2) -> `yield return`; pace at the requested exposure
  (Canon EVF runs ~15-30 fps regardless; treat `Exposure` as a poll cadence floor). `finally`:
  `StopLiveViewAsync`, clear the gate. Cancel = stop.
- **`ApplyVideoControlsAsync`**: ISO via the existing `SetGainAsync` (`CanonCameraDriver.cs:395`, ISO index
  table). Exposure on EVF is largely auto / not a true integration time; surface that limitation (see Risks).
- **`DroppedFrames`** -> 0 (EVF has no drop counter).
- **`VideoRoi`** reflects the EVF zoom rect (origin + crop size) when zoomed, so the recenter loop knows the
  remaining pan range before an edge; a sensor-sized window when not zoomed. (Zoom is discrete 1x/5x/10x, so
  the ROI *size* is fixed per zoom level, not freely sized.) This is what `JogRoiAsync` pans (E.3).

## E.2 JPEG -> `Image` decode

`GetLiveViewFrameAsync` returns a camera-**processed** JPEG (demosaiced + white-balanced + tone-mapped RGB),
typically ~1024x680. So a Canon EVF frame is a **3-channel RGB `Image`**, not a raw CFA mosaic. The stacker
already handles a colour master, so this drops straight into the live-stack pipeline. The EVF feed is always
lossy JPEG + camera-processed (no raw CFA over Live View -- a Canon hardware limitation); at full-frame it is
framing quality, but **zoomed to 5x/10x it is at native pixel scale** over the crop (E.3), which is the usable
planetary regime. Surface the "processed JPEG, not raw" caveat in the panel, but do not write the path off as
framing-only.

Decode path: a small `byte[] JPEG -> Image` helper. The repo's single-shot Canon path already decodes via
`Image.TryReadImageFile` (Magick.NET, `CanonCameraDriver.cs:582`); for the in-memory EVF JPEG prefer a
stream/byte-span decode (StbImageSharp family is already a dependency and AOT-clean) to avoid a temp-file
round-trip per frame. Normalize to `[0,1]` at the driver boundary (the `LiveCameraFrameStream.DeepCopy`
convention) so the preview master is display-ready.

## E.3 EVF zoom = the Canon planetary mode + the ROI-jog actuator

This is the heart of Phase E for planets. Setting `Evf_Zoom` (`0x507`, `EdsPropertyId.cs:59`) to 5x or 10x
makes the Live View feed a **near-1:1-pixel magnified crop** of a small sensor region -- exactly what DSLR
planetary shooters use for Jupiter / Saturn. `Evf_ZoomPosition` (`0x508`, `:60`) / `Evf_ZoomRect` (`0x541`,
`:70`) move that crop across the sensor; `CanonZoom` (PTP op `0x9158`, `PtpOperationCode.cs:47`) is the
underlying operation. All three exist as property IDs / op codes but have **no typed wrappers** on
`CanonCamera` today -- they are reachable via the generic `GetPropertyAsync`/`SetPropertyAsync`, so Phase E
adds thin `SetEvfZoomAsync` / `SetEvfZoomPositionAsync` helpers (or drives the generics directly).

Because the zoom crop is pannable, it **is** the Canon ROI jog:
- **`CanJogRoi` -> true** (when zoomed). `JogRoiAsync(dx, dy)` writes a new `Evf_ZoomPosition` -- the same
  mount-free recenter actuator ZWO gets from `ASISetStartPos`. The Phase C recenter loop drives it unchanged.
- **`VideoRoi`** reports the zoom rect (origin + crop size), so the recenter math knows the remaining pan
  range before an edge.
- When **not** zoomed (full-frame framing), `CanJogRoi` is false and recenter falls back to mount jog -- the
  Phase C fallback already covers this, so the two regimes degrade cleanly into each other.

The original "defer it, `CanJogRoi=false`" stance was wrong: EVF zoom is not a nicety, it is the only way to
get planetary-useful resolution **and** a host-side ROI jog out of a Canon, and it is fully reachable through
the already-published FC.SDK. The remaining risk is per-body quirks in the zoom-position units (verify on
hardware; the Phase C per-axis cap bounds a wrong guess to a small mis-pan, never a runaway).

## E.4 Canon movie recording: out of scope for the *live* path (and the offline path that uses it)

Canon bodies record movies (AVI / MP4 / MOV), and FC.SDK carries the property IDs (`Record` `0x510`,
`FixedMovie`, `MovieParam`, `MovieHFRSetting`, ... in `EdsPropertyId.cs`; `RecordingTime` event at
`CanonEventType.cs:14`). It is **not** the source for the live `IVideoCameraDriver` stream, for three reasons
intrinsic to the format -- not SDK gaps:
1. **Temporal compression.** Movies are H.264 / HEVC; lucky-imaging selects + aligns thousands of
   *independent* sharp frames, and inter-frame compression smears exactly the per-frame detail the grader /
   aligner needs. (SER is raw + per-frame for precisely this reason.)
2. **Processed, not raw.** Movie frames are demosaiced + white-balanced + tone-mapped in-camera.
3. **Card-resident, not a host stream.** Recording writes to the SD card; you download the file *after* it
   stops -- it never arrives as the per-frame host feed the rolling-window stacker consumes. FC.SDK exposes
   only the `Record` property (drive via `SetPropertyAsync`), not a record-and-stream method.

**The legitimate use of a recorded movie is offline, through the *batch* stacker -- a separate feature, not
Phase E.** The planetary engine's batch path (`LuckyImagingStacker`, CLI `tianwen planetary-stack`) ingests a
frame sequence offline via `IPlanetaryFrameStream`. A Canon-recorded MOV / MP4 -> decode frames -> a
`MovieFrameStream : IPlanetaryFrameStream` -> the batch stack is real and worthwhile for a DSLR shooter (still
H.264-hampered vs SER, but it is their reality). That is a **file-ingest** addition alongside the SER path, not
a live driver, so it is cross-linked here and tracked separately (out of scope for this plan).

## E.5 Tests + hardware smoke

- **Unit**: the JPEG-decode-to-`Image` helper (a fixed EVF JPEG fixture -> expected dimensions + `[0,1]`
  range + 3 channels); the single-stream / mutual-exclusion gate; and the `JogRoiAsync` -> `Evf_ZoomPosition`
  mapping (offset -> new zoom position, clamped to the sensor) against a stub `CanonCamera`. No camera needed.
- **Hardware smoke**: a real EOS body -> planetary mode -> capture full-frame (framing) then set 5x/10x zoom
  -> confirm the magnified crop streams + stacks, and that `JogRoiAsync` pans the crop (recenter follows a
  drifting planet via the zoom position, no mount disturbance).

## E Risks / open decisions

- **EVF exposure is not a true integration time** -- the user can frame and EAA-stack but cannot set a real
  planetary sub-exposure (EVF gain via ISO works; shutter is EVF-auto). Surface this in the panel when the
  camera is a Canon (a capability note, not a hard block).
- **Zoom-position units vary per body** (E.3): `Evf_ZoomPosition` coordinates + the 5x/10x crop size differ
  across EOS generations. Verify the offset -> position mapping on hardware; the Phase C per-axis cap bounds a
  wrong guess to a small mis-pan, so it is never a runaway.
- **JPEG decoder choice** (E.2): StbImageSharp in-memory vs the existing Magick.NET path. Recommend
  StbImageSharp for per-frame speed + AOT cleanliness; confirm it decodes Canon EVF JPEGs.
- **Offline movie ingest** (E.4) is a separate, optional feature (a `MovieFrameStream : IPlanetaryFrameStream`
  for the batch stacker), not part of this live-capture plan. Decide separately whether to build it.
- **No DAL / SDK release** -- FC.SDK Live View is already published. Phase E is a single TianWen commit, hence
  **Low** risk vs Phase D's multi-repo chain.

---

# Sequencing

E before D: Phase E is a single in-tree commit against an already-published FC.SDK (Low risk), so it lands the
first **real-hardware** planetary capture quickly. Phase D is the High-risk multi-repo NuGet chain and the
true raw lucky-imaging path; tackle it second when the fake-validated pipeline + Canon EAA path are both
proven end to end.

| Phase | Scope | SDK/DAL release? | Risk |
|---|---|:--:|:--:|
| E | `CanonCameraDriver : IVideoCameraDriver` via FC.SDK Live View + EVF zoom (JPEG) | no | Low |
| D | `ICMOSNativeInterface` video verbs + ZWO/QHY native + `DALCameraDriver : IVideoCameraDriver` | yes (DAL -> 2 SDKs -> TianWen) | High |

# Docs to ship with the feature

- Flip the Phase D / E rows in [`live-planetary-capture.md`](live-planetary-capture.md) to DONE with a one-line
  retrospective each (the project's done-log style).
- Add a CLAUDE.md architecture note: `DALCameraDriver`/`CanonCameraDriver` now implement `IVideoCameraDriver`;
  the per-vendor ROI-jog table (ZWO live-pan / QHY+Canon mount-fallback).
- Tick the planetary-stacking Phase 12 + TODO.md.
