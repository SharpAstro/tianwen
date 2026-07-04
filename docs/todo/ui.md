# TODO -- UI & Rendering

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Live Session Tab (Phase 2 — Polish)

- [x] Guide star profile bitmap from guider (rendered in GuiderTab star profile panel)
- [ ] Extract `GuiderContent` shared helpers (TianWen.UI.Abstractions) — `TuiGuiderTab` and the GPU `GuiderTab<TSurface>` currently inline their formatting / sparkline logic. Mirror the `LiveSessionActions` pattern: `FormatGuidePhase(phase)`, `FormatStarInfo(metrics)`, `FormatSettleProgress(current, target)`, `BuildErrorSparkline(samples, axis, width)` -> Unicode string, `GetErrorGraphPoints(samples, axis, timeWindow)` -> points for the GPU line graph, `GetBullseyePoints(samples, count)` -> (ra, dec) scatter. Lets both the TUI and GPU tabs share the same phase strings and error-graph data derivation instead of duplicating.
- [ ] Inline V-curve charts in focus history panel
- [ ] Per-filter frame count breakdown in stats
- [ ] Meridian flip countdown indicator
- [x] Dither event markers on guide graph
- [ ] Click exposure log entry → open in Viewer tab
- [ ] Exposure log thumbnails: 128px height, preserve aspect ratio
- [ ] Finalise as background task — keep UI responsive during park/warmup after abort/complete

## FITS Viewer

- [ ] Rename HDR button/label to "Compress Highlights"
- [x] Remove debug `Console.Error.WriteLine` WCS output from `Program.cs` DONE (2026-06-02): none present in `TianWen.UI.FitsViewer/Program.cs` (all logging via `ILogger`).
- [x] Support rec601/rec2020 luminance weighting options in luma stretch (2026-05-11) — see Stretch / Image Processing section.
- [ ] Grid label formatting: show arc-seconds for very narrow FOVs
- [ ] Crosshair / reticle overlay at image center
- [x] Annotation overlay (object names from catalogs when plate-solved)
- [x] Star detection overlay: `FitsDocument.DetectStarsAsync()` runs as background task,
      draws HFD-sized green circles, shows count/HFR/FWHM in status bar (S key toggle)
- [x] Background neutralization toggle: N key and toolbar `NeutBg` button — computes pivot1 gains from `ScanBackgroundRegion` and applies via GPU shader
- [x] SPCC color calibration via W key — tries spectrophotometric (Pickles SED + system throughput) first, falls back to sky-background method; toolbar `SPCC` button
- [x] Clip star overlay circles to image viewport + fix centroid alignment (+0.5px offset)
- [ ] Remember last opened folder and recent images across sessions
- [ ] Continuous image advance when holding arrow keys (advance every ~1 second while pressed)
- [ ] Display original bit depth before normalization (e.g. "16-bit" in status bar) when available from FITS header
- [ ] Star profile tooltip: show radial profile plot (flux vs. distance) when mouse hovers over a detected star
- [ ] Named star labels: match detected stars against Tycho2 via WCS→RA/Dec projection,
      label with cross-catalog names (HIP, HD) using `TryGetCrossIndices`
- [x] Replace custom `AsyncLazy<T>` with `DotNext.Threading.AsyncLazy<T>` (already a dependency in TianWen.Lib)
- [x] Use a `WeakReference<AstroImageDocument>` cache (keyed by file path) so that cycling through
      images can reuse recently loaded documents without keeping them pinned in memory
      (`DocumentCache` with `ConditionalWeakTable` + `WeakReference<T>`)
- [ ] Investigate `DotNext.Threading.RandomAccessCache<TKey, TValue>` (or similar bounded cache)
      as an alternative to `WeakReference` for the document cache — may offer better eviction control

## SdlVulkan.Renderer

- [x] Font atlas corruption — root cause: shared upload buffer race with `MaxFramesInFlight=2`. Frame N+1's `Flush` overwrites the upload buffer while frame N's `vkCmdCopyBufferToImage` is still reading it. Fixed with `vkDeviceWaitIdle()` before upload buffer reuse.
- [x] Replace `vkDeviceWaitIdle` in font atlas `Flush` with per-frame upload buffers (like `_vertexBuffers`) to avoid GPU stall on every glyph upload — `VkFontAtlas` + `VkSdfFontAtlas` now keep an N-slot ring indexed by `ctx.CurrentFrame`; `MaxFramesInFlight` exposed as `public const` on `VulkanContext` (commit `3ccd6a2`).
- [x] SDF font atlas: `Grow()` / `CreateImage` used to transition the fresh `VkImage` via `ctx.ExecuteOneShot`, which submits a side cmd buffer to the graphics queue while the frame's cmd buffer is recording — some drivers reject this with `VK_ERROR_INITIALIZATION_FAILED` from the next `vkQueueSubmit`. Fixed: deferred initial transition to the next `Flush` via `_needsInitialTransition` flag; initial atlas dim now scales with `SdfRasterSize` (`2048²` at 128px raster) so `Grow()` rarely fires during typical startup UI anyway (commit `30fcdf7`).
- [x] `VkTexture.CreateDeferred`: pixel-format parameter — was hard-coded to `B8G8R8A8Unorm`, which forced RGBA-producing CPU renderers (altitude chart via `RgbaImageRenderer`) to run a per-pixel swizzle loop before upload. Now takes `VkFormat format = B8G8R8A8Unorm` so callers can pass `R8G8B8A8Unorm` with RGBA bytes directly (commit `90f877a`); `VkPlannerTab` dropped its CPU swizzle loop.
- [ ] `VkSdfFontAtlas.Grow()` mid-frame hazard — destroys the old `VkImage` and calls `vkUpdateDescriptorSets` while the frame's cmd buffer is still recording. Works on current drivers but is spec-grey (`VUID-vkUpdateDescriptorSets-pDescriptorWrites-06993` forbids updating a descriptor set that is in use by a pending submission). If we ever see corruption or validation noise tied to `Grow()`, defer the destroy + descriptor update to the next `OnPreRenderPass` (same pattern as `VkPlannerTab`'s deferred texture swap). Not pre-emptively worth fixing — the initial-atlas bump in `30fcdf7` makes `Grow()` rare, and there is no known observed corruption.
- [ ] `SdlVulkanWindow.Create` should take the SDL `WindowFlags` as a parameter instead of hardcoding `WindowFlags.Vulkan | WindowFlags.Resizable | WindowFlags.Maximized`. Default keeps `Maximized` (matches today's behaviour) but callers can opt out — e.g. to launch at the supplied `1280×900` non-maximized, or to force fullscreen at startup. Both `TianWen.UI.Gui/Program.cs:74` and `TianWen.UI.FitsViewer/Program.cs` (same `Create` call) pick up the change for free. Consider exposing as an overload `Create(title, width, height, WindowFlags extraFlags)` with `Vulkan | Resizable` always on, `Maximized` added by default but overridable.

### SdlEventLoop (DONE — all consumers now use the shared loop)
- [x] Add `DropFile` event support (`EventType.DropFile`) — `Action<string>? OnDropFile`
- [x] Multi-button mouse: `OnMouseDown` passes button ID + click count (`Func<byte, float, float, byte, bool>?`)
- [x] `OnMouseUp` passes button ID (`Action<byte>?`)
- [x] `OnMouseWheel` passes tracked mouse position (no more hardcoded 0, 0)
- [x] F11 fullscreen removed from loop — each consumer handles it in `OnKeyDown`
- [x] Migrated `TianWen.UI.FitsViewer/Program.cs` to use `SdlEventLoop`
- [x] Touch input: pinch-to-zoom via `SDL_EVENT_FINGER_*` events — two-finger tracking + scale computation in `SdlEventLoop` (`OnPinch`/`OnPinchEnd`), consumed by `SkyMapTab` via `InputEvent.Pinch`/`PinchEnd` (2f0b484)

Vulkan/SDL migration rationale moved to `../SdlVulkan.Renderer/README.md` ("Rationale: Why SDL3 + Vortice.Vulkan" section).

## Sky Map (first-open perf, LOW PRIORITY)

Context: the first Sky Atlas open stalled ~800 ms in dev. Most of it is already fixed or
AOT-free after the `perf(skymap)` commits (async Milky Way decode + VSOP87 pre-warm); full
anatomy in the `reference_skymap_first_open_perf` memory. These two are the remaining optional
levers, both low priority because production (NativeAOT) first-open is already fast.

- [ ] (b) Pre-warm `VkSkyMapPipeline` at GUI startup. The ~140 ms pipeline shaderc compile
  (runtime GLSL-to-SPIR-V) is the ONLY real production first-open cost; NativeAOT does not
  eliminate it. Construct the pipeline once `renderer.Context` is live (overlapping the
  cold-start font-atlas warmup) so it is off the first tab-open frame. Higher risk: touches the
  GPU-context lifecycle. Alternative: compile the sky-map shaders to SPIR-V offline at build
  time (measured ~117 ms, earlier deemed not worth the MSBuild machinery; revisit if pursuing).
- [ ] (c) Data-encode the VSOP87 coefficients (astrometry). `MarsX.cs` etc. are ~24 giant
  `GetX/GetY/GetZ` methods of thousands of inline `x += c*Math.Cos(p + f*t)` statements (~3.6 MB
  of source). Re-encode as `static readonly double[]` (or a packed binary resource) plus one
  generic evaluation loop. Eliminates the dev-only ~330 ms first-call JIT (measured 467 ms dev
  vs 7 ms AOT), shrinks the AOT binary, and speeds the AOT publish. Full accuracy retained (same
  coefficients) so the GOTO/pointing consumers (`Transform.cs`) stay correct. Pure cleanup, NOT
  a production-perf fix. Cross-ref: also tracked under astrometry.

## SignalBus / render-thread invariants

- [x] **No device connect/disconnect may run its synchronous prefix on the render thread** (DONE
      2026-07-04). `SignalBus.ProcessPending` runs per-frame on the render thread and invokes async
      handlers *inline* (`var task = handler(signal)`) up to their first yielding `await` — the
      `BackgroundTaskTracker` only tracks the already-started task, it does **not** offload the
      prefix. A driver that blocks before its first await (ASCOM COM `Connected = true/false`
      busy-spinning `Application.DoEvents()` — Gemini FlatPanel, iOptron, GS Server) therefore froze
      the GUI. Fix: all four connect/disconnect sites route through
      `AppSignalHandler.RunDeviceOpOffRenderThreadAsync` (a `Task.Run` offload). **Invariant for new
      code:** any signal handler that may call a blocking driver op must offload it the same way —
      never `await hub.XAsync(...)` directly in an inline-invoked handler. The deeper ASCOM
      correctness fix (STA + message pump) is [../plans/ascom-com-sta-message-pump.md](../plans/ascom-com-sta-message-pump.md).
- [ ] Consider fixing this at the `SignalBus` level (DIR.Lib): the documented contract says async
      handlers are "submitted to the tracker," but the implementation runs their prefix inline.
      Making `tracker.Run(() => handler(signal), ...)` invoke the handler *inside* the tracked
      delegate would offload every async handler — but it's a broad DIR.Lib behaviour change (some
      handlers may rely on running their prefix on the render thread) and needs its own release, so
      the per-call-site offload above is the surgical fix for now.

