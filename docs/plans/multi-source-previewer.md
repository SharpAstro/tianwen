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
| 2 | `IPreviewSource` + make `AstroImageDocument` implement it; migrate `ImageRendererBase`/`ViewerController` (FITS/TIFF unchanged) | NOT STARTED |
| 3 | `SerPreviewSource` + `.ser` open + auto-switch; `SupportedExtensions`/filters/`FileAssociationRegistrar` += `.ser` | NOT STARTED |
| 4 | Playback + transport bar (cheap per-frame upload reusing fixed uniforms) | NOT STARTED |
| 5 | Port Malvar-He-Cutler debayer into `VkFitsImagePipeline` (selectable; bilinear stays as fallback) | NOT STARTED |
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
