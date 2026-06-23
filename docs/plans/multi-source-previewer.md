# Multi-Source Previewer (FITS + TIFF + SER) in tianwen-fits

## Context

`tianwen-fits` is the standalone image viewer (a small AOT WinExe over `ImageRendererBase`). A separate
prototype — the standalone `SER.Viewer` in the `SER.Lib` sibling repo — proved that SER planetary-video
playback with GPU debayer + stretch works well on win-arm64. Rather than ship and maintain two viewers,
the SER capability folds **into `tianwen-fits`**: it opens FITS + TIFF + **SER** and auto-switches into a
frame-playback mode for SER. `SER.Lib` stays as the standalone SharpAstro NuGet (format reader/writer +
`SerImaging` decode + tests); TianWen consumes it.

**The load-bearing constraint is performance.** A SER is a video (thousands of frames). Pushing each frame
through `AstroImageDocument` (which runs `ComputeStretchStatsAsync`, normalize-in-place, debayer, and
`ChannelBuffer` allocation) would be a per-frame trap at 30 fps. So the viewer becomes a **multi-source
previewer**: the renderer previews an `IPreviewSource`, not specifically an `AstroImageDocument`. A still
image (FITS/TIFF) is a one-frame source; a SER is an N-frame source. **Stretch stats + white balance are
computed once** (from a representative frame); per frame we only do a cheap raw read (MMF seek → reused
buffer → `[0,1]` convert) and one `UploadChannelTexture`, reusing the fixed uniforms — the GPU does
debayer + stretch. An `Image` is materialized only when something actually needs one (snapshot/export,
Phase-6 stacking) — never in the playback loop.

Logic lives **high**: pure decode/bridge in `TianWen.Lib`, source/state/UI in `TianWen.UI.Abstractions`,
GPU shader in `TianWen.UI.Shared` (the project rule: no GPU in `.Lib`/`.Abstractions`). Because the FITS
viewer's `ViewerState` is the **same singleton** the GUI's viewer tab uses, everything added here (SER
mode, manual WB) lights up in the GUI viewer tab too.

## Reused infrastructure (not rebuilt)

- **GPU debayer** — `VkFitsImagePipeline` already does in-shader demosaic (`ImageSourceMode=RawBayer`) on a
  raw mosaic + `BayerOffsetX/Y`; `ImageRendererBase.UploadDocumentTextures` routes `SensorType.RGGB`
  1-channel images there.
- **GPU white balance** — `StretchUniforms.WhiteBalance (R,G,B)` is applied in the GLSL `stretchChannel`;
  `ComputeStretchUniforms` already scales stats by it (auto WB via the Calibrate / SPCC toolbar actions).
- **Zoom / pan / panels / histogram / toolbar / layout** — all in `ImageRendererBase` (`.Abstractions`).
- **TIFF** is already supported (`.tif`/`.tiff` in `AstroImageDocument.SupportedExtensions`). Only SER is new.

## Architecture

- **`IPreviewSource`** (`TianWen.UI.Abstractions`): geometry (`Width/Height`, `ChannelCount`,
  `ImageSourceMode`, `BayerOffsetX/Y`), `TryGetChannelData(channel, view)` → current-frame `[0,1]` span,
  `ComputeStretchUniforms(state, normalize)` (cached stats), histogram data, and frame nav
  (`FrameCount`, `FrameIndex`, `SelectFrame(n)`). Kept **stream-friendly** (no hard-coded MMF/seek
  assumption) so a future live-camera source drops in. `AstroImageDocument` implements it additively
  (FrameCount=1); still-only features (WCS/annotation/solve/ColorCalibration) stay on `AstroImageDocument`.
- **`SerImageBridge`** (`TianWen.Lib/Imaging/SerImageBridge.cs`, pure): `SerColorId` → `SensorType` +
  Bayer offset; `FillUnitFloat` decodes a frame to `[0,1]` into caller-reused buffers (hot path, zero
  alloc; Bayer/mono → 1 mosaic channel, RGB/BGR → 3 de-interleaved channels with BGR→RGB swap);
  `ToImage` materializes a full `Image` for snapshot/export/stacking. **Done.**
- **`SerPreviewSource`** (`TianWen.UI.Abstractions`): wraps a `SerReader`; computes stretch stats once
  from frame 0; `SelectFrame` does the cheap `FillUnitFloat` into reused buffers.
- **Playback**: `ViewerState` gains `IsSequence`, `FrameIndex`, `FrameCount`, `IsPlaying`, `PlaybackFps`,
  `ManualWhiteBalance`. The render-loop tick advances frames by wall-clock × fps and re-uploads via the
  cheap path; a transport bar (play/pause/scrub/fps + frame & timestamp readout) shows only when
  `IsSequence`.

## Layer placement

| Concern | Project |
|---|---|
| `SerColorId`→`SensorType` map, raw→`[0,1]` convert, `SerImageBridge.ToImage` | `TianWen.Lib/Imaging/` |
| `IPreviewSource`, `SerPreviewSource`, `ViewerState` SER+WB fields, `AstroImageDocument` impl, `ViewerController` open/playback, transport + WB UI | `TianWen.UI.Abstractions` |
| MHC debayer shader branch | `TianWen.UI.Shared` (`VkFitsImagePipeline`) |
| `.ser` arg/registration | `TianWen.UI.FitsViewer` |

## Phasing

| Phase | Scope | Status |
|---|---|---|
| 1 | Wire SER.Lib sibling (`Directory.Build.props`/`Directory.Packages.props`/`TianWen.Lib.csproj`) + pure `SerImageBridge` + tests | **DONE** |
| 2 | `IPreviewSource` + make `AstroImageDocument` implement it; migrate `ImageRendererBase`/`ViewerController` (FITS/TIFF unchanged) | **DONE** |
| 3 | `SerPreviewSource` + `.ser` open + auto-switch; `SupportedExtensions`/filters += `.ser` (OS `FileAssociationRegistrar` left FITS-only, as it is for TIFF/CR2/CR3) | **DONE** |
| 3.5 | HDD-validation hardening: linear-default stretch for SER; lazy trailer in `SerReader` (no end-of-file seek per open); cancel + supersede in-flight loads (off-thread, never block the render thread) | **DONE** |
| 4 | Playback + transport bar. **Off-thread, frame-paced** decode-ahead: `ISequencePlaybackSource` + double-buffered `SerPreviewSource` (background decode into a back buffer, render-thread swap on publish); `SequencePlayer` decodes the next frame ahead during the inter-frame gap and publishes only when due, so `CheckNeedsRedraw` returns true only on a frame change -> the loop idles between frames (low GPU, mirroring the standalone). Display fps capped at 30 (the file's **nominal** capture fps is shown in the readout); per-frame histogram **recycled in place** (no per-frame alloc, tracks the current frame); transport bar = play/pause + scrub + frame/timestamp/nominal-fps readout; keys Space/Tab (play/pause), Left/Right (step), Home/End, Up/Down (speed). | **DONE** |
| 5 | Malvar-He-Cutler debayer in `VkFitsImagePipeline` (selectable; bilinear stays as fallback) + CPU mirror. The shader's RawBayer path gained four `stretchBlend.z`-selected demosaic modes -- bilinear colour / **MHC** colour / raw mosaic (grey CFA) / monochrome -- and `Image.DebayerMHCAsync` is the CPU mirror, pinned **pixel-equal** to the canonical `SerImaging.DebayerMhc` by `DebayerMhcTests`. `DebayerAlgorithm` gained `MHC` (appended); `GpuDebayerMode` maps None->raw, BilinearMono->mono, VNG->bilinear, AHD/MHC->MHC, so each menu entry behaves as its name implies and the default AHD gives MHC colour (matching the standalone). The Debayer toolbar selector is enabled for **any** RGGB `IPreviewSource` (SER included, not just `AstroImageDocument`), switches **live mid-stream** (the playback loop re-derives the mode every published frame; paused switches force a one-shot re-upload), and the dropdown highlights the active item on open. | **DONE** |
| 6 | Manual R/G/B WB sliders → `ComputeStretchUniforms` → GPU `WhiteBalance` (shared across formats) | NOT STARTED |
| 7 | Remove the standalone `SER.Viewer` from `SER.Lib` (library + tests only) | NOT STARTED |

## Verification

- **Unit**: `SerImageBridgeTests` (mapping + raw→`[0,1]` + de-interleave/BGR swap) — **passing**; existing
  FITS-viewer/stretch tests stay green as the `IPreviewSource` migration regression guard.
- **End-to-end (still)**: `tianwen-fits <a.fits>` / `<a.tiff>` open unchanged (no transport bar).
- **End-to-end (SER)**: `tianwen-fits <jupiter>.ser` auto-switches to playback, debayered, smooth
  play/pause/scrub, zoom/pan/Ctrl+0 work, manual WB shifts colour, MHC vs bilinear visibly cleaner,
  **CPU stays low during playback** (no per-frame stats), Esc exits clean. Build win-arm64.
- **GUI**: the GUI viewer tab gains SER + WB for free (shared `ViewerState`).

## Follow-ups (out of scope here)

- **Live astro-camera video preview**: a live capture stream is just another `IPreviewSource` (current
  frame pushed by the camera, not seeked) — focus loops / planetary-capture monitoring. The interface is
  kept seek-agnostic to allow this later.
- `SerFrameSource : IFrameSource` for the stacking pipeline (closes the `stacking.md` SER deferral).
- SER export/writer UI; CYGM Bayer families (TianWen models only RGGB) fall back to mono.
- **SER.Lib release dependency**: the Phase-3.5 lazy-trailer change shipped in `SER.Lib` (PR #1, merged +
  published). TianWen pins `SER.Lib` as floating `1.0.*` in `Directory.Packages.props`, so a restore
  auto-resolves to it — no manual repin needed. The public API is unchanged; Phase 4 is purely
  TianWen-side (no further `SER.Lib` change).
- **No blocking I/O on the render thread** (standing review check, **upheld in Phase 4**): all `.ser` disk
  access (header, frame decode, lazy fps/timestamp trailer) stays off the render thread. The trailer is
  materialised once at open (off-thread) and cached as managed `SerPreviewSource` fields; per-frame decode
  runs on a background `Task` into a back buffer (`TryStartDecode`); the render thread only swaps the
  finished buffer in (`TryPublishDecoded`) and uploads it. Never `SelectFrame` inline in `OnRender`.
