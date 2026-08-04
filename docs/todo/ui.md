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

## TUI Equipment tab (found during the 2026-07-29 cell-buffer session)

- [x] Wire the OTA header's `[X]` to a click — **DONE (2026-07-29).** The glyph was painted but never
  bound, so the only route to removing an OTA was an undiscoverable key. Now:
  - The `[X]` registers a click via `ScrollableList.RegisterRowSpanHits` and arms the same two-step
    confirm the key does, so a stray click cannot delete outright.
  - **The key is `Ctrl+X`, not a bare `X`** (user call). Removing an OTA destroys a configured optical
    train, and a bare letter is precisely what blind key injection walks into — a run of `X`es armed
    and confirmed in alternation, one OTA gone per pair, which is how a live profile got emptied. The
    confirm requires the chord again (`IsRemoveOtaChord`, unit-pinned), so one stray press cannot get
    past the guard either. Advertised as `Ctrl+X:remove OTA` in the status bar.
  - **Superseded 2026-07-30 by the durable fix** (Console.Lib 4.10, rows as `Layout.Node`). It first
    shipped with draw and hit derived from ONE static (`EquipmentFieldItem.DeleteActionColumns`) as a
    stand-in for draw==hit, left-anchored one space after the title because a formatted-string row
    cannot know its usable width (`ScrollableList` hands the formatter `width - 1` once a scrollbar
    shows), so a right-anchored span drifted by a column exactly when the list overflowed. The `[X]` is
    now a clickable NODE carrying `ButtonHit($"RemoveOta{i}")`, arranged into the content width and
    resolved via `ScrollableList.DispatchRowHit` -- so the static is gone and the glyph is
    **right-anchored**, where the GUI's `[Remove]` sits.
  - The `IsEditingSite` note needs no code change: site-edit mode owning the keys is correct, and it
    was the *automation* that was wrong to send keys without verifying the mode. The chord removes
    most of the residual risk regardless. Pinned by `TuiEquipmentRowTests`.
- [ ] Wire the slot row's `[On|Off]` and `[>]` to clicks -- the same defect the `[X]` had, on the two
  affordances beside it: both are painted on every device-slot row and neither is bound, so connecting a
  device or opening the assignment picker is keyboard-only (`O`, `Enter`). Not done with the 4.10 row port
  deliberately: the geometry half is now a one-line `.Clickable(...)` per cell, but the row has no handler
  to bind -- it would need the tab's connect/disconnect and assign flows threaded in as callbacks, and the
  disconnect path carries a safety pre-check plus a confirm strip that must not be bypassed. Model it on
  `EquipmentFieldItem.OnRemoveOta`.
- [ ] Restore the settings-list selection after a tab switch — `Attach` rebuilds the list and the
  cursor resets to row 0, so switching away and back loses the white-on-blue selected row (measured
  via the console inspector: row 3 `Mount` returns as unselected). Remember the cursor index per tab
  instance across `Attach`, clamped to the rebuilt item count.

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

## Sky Map (interaction + selection, reported 2026-08-03)

Three user-reported defects. Root causes below are from reading the code, not from a repro run, so
confirm before fixing; the line numbers are as of this entry.

- [x] **A selected extended object gets a circle instead of its own shape.** **DONE (2026-08-05).** The
  size gate is now an inflate: `TryDrawShapeMarker` scales BOTH semi-axes by one factor from the new
  shared `OverlayEngine.EllipseLegibilityScale` (floor 10 px on the semi-major axis, plus a 1.15 slack
  that applies at every size so the ring sits just outside the object's own outline instead of coinciding
  with it), so the marker keeps the object's real axis ratio and position angle at any zoom. The scale is
  deliberately uniform, and the helper is shared with both pinned-halo paths. The crosshair now appears
  only for genuinely shapeless entries: no usable shape, a star (`ChooseMarkerKind`), or a degenerate
  projection. Pinned by four `OverlayEngineTests` cases (floor, ratio preserved, large shape untouched,
  degenerate size yields no NaN). Original analysis:
  already exists (`SkyMapTab.Search.cs` `TryDrawShapeMarker`, which traces the true ellipse through the
  shared `OverlayEngine.ComputeEllipseScreenAxes`), but it **bails when `semiMajorPx < 10f * dpiScale`**
  and falls back to a fixed `DrawCircle(sx, sy, 14f * dpiScale, ...)` plus crosshair
  (`SkyMapTab.Search.cs:449`). At ordinary zooms most galaxies project under that floor, so the
  fallback circle is *larger* than the ellipse still being drawn underneath it, which is exactly the
  "shape is an ellipse, selection is a circle" mismatch. **What the user asked for:** keep the object's
  own shape and make it read as selected, either inflated (same shape, grown to a legibility floor) or
  same size with a thicker stroke / different colour. So the fix is to replace the size gate with an
  inflate-to-minimum on the ellipse path, and keep the crosshair strictly for genuinely shapeless
  entries (stars, which `ChooseMarkerKind` already separates out for a good reason).
- [x] **The pinned-target halo is a circle even for an ellipse marker.** **DONE (2026-08-05).** Both
  halo paths now trace the marker's own ellipse scaled by `OverlayEngine.EllipseLegibilityScale`
  (uniform, so the axis ratio and position angle survive), and the 1.5x / 16 px / 3 px numbers moved
  to `OverlayEngine.PinnedHalo*` because they were restated in the CPU code, the GPU code, and both
  comments. Note the report named one site but there were **two**: `VkSkyMapTab` had the same defect
  independently, and since that is the desktop GPU path, fixing only the CPU one would have left the
  GUI unchanged. Pinned by `SkyMapPinnedHaloTests`, which asserts halo and marker are similar figures
  by comparing their radii about their own centroids (a rotated ellipse's bounding box is near-square
  at PA 45, so a box cannot see elongation at all). Original analysis:
  `SkyMapTab.ObjectOverlay.cs:130-140` computes `haloPx` from `e.SemiMajArcmin` for an
  `OverlayCandidateMarker.Ellipse` and then draws it with `DrawCircle`, so an elongated object gets a
  circular halo sized to its *major* axis. `DrawOverlayEllipse` is right there in the same file.
- [ ] **Panning near the pole (EQ) / zenith (horizon) swings the field.** Not a pan-math bug:
  `SkyMapTab.HandleDrag` builds a correct great-circle quaternion. The problem is that it stores only
  **two** of the rotation's three degrees of freedom (`State.CenterRA` / `CenterDec`) and throws the
  roll away, so every frame re-derives orientation in `SkyMapState.ComputeViewMatrix` as
  `right = forward x upRef` with `upRef` = celestial pole (EQ) or local zenith (Horizon). As the centre
  approaches that reference, `rLen` goes to 0, so the right-vector *direction* becomes arbitrarily
  sensitive to a tiny mouse move (the field spins), and at the singularity the code snaps to a hardcoded
  `(1,0,0)`, which is a visible discontinuity. **The user's suggested mitigation was to silently pan in
  alt-az while in EQ near the pole and in EQ while in alt-az near the zenith.** That would work in the
  sense of avoiding each frame's own singular point, but it only *moves* the singularity and adds a
  mode-dependent discontinuity at the swap, so prefer the root fix: carry the accumulated orientation
  (the quaternion, or a roll angle beside the centre) and let the view matrix come from it, so the pole
  stops being a special place at all. The arbitrary-right fallback then becomes unreachable rather than
  merely rare.
- [ ] **The web sky map has no grid by default**, which is what makes the above disorientation land
  instead of being readable. Check the web showcase's initial `SkyMapState` against the desktop default
  and turn the grid on, at least in Horizon mode where there is no other horizon reference.

## Planner (reported 2026-08-03)

- [x] **The "Observation Schedule" title is drawn outside its own chart area, and on top of the twilight
  labels.** **DONE (2026-08-05).** Both halves fixed: the rect starts at `areaX`, and `VerticalLayout`
  now allocates the space above the plot top-down (title band, weather band, twilight-label band), with
  `DrawTwilightZones` anchoring its rows off the same constants so the reservation cannot drift from
  what is drawn. Two things the analysis below did not anticipate. Reserving the title outright would
  have made `plotH` NEGATIVE on the ~117 px portrait chart that `PlannerTabLayoutTests` pins, so the
  top margin is shaved to keep a minimum plot and the title is then **dropped rather than overlapped**,
  which is what makes "no two rows share space" true at every size instead of only comfortable ones.
  And `GetChartPlotLayout` / `GetWeatherBandLayout` restated the same arithmetic, so they now share
  `VerticalLayout` with `Render`, which is also the only reason the hit-test geometry still matches.
  Pinned by `AltitudeChartTitleLayoutTests` (centring at three `areaX` offsets, no label overlap at
  five heights including 2400 px where the old code happened to work, the too-short case, and getter
  vs renderer agreement). Original analysis:
  1. **Horizontal:** `var titleRect = MakeRect(0, areaY + 2, w, titleH)` (line ~198) passes `x = 0`
     where `w = areaW`, so the title is centred at `areaW / 2` instead of `areaX + areaW / 2`. It is the
     **only** `MakeRect(0, ...)` in the file; every other element offsets by `areaX` / `plotX`. With the
     planner list occupying the left column, the title therefore slides left out of the chart column.
     One-line fix: `MakeRect(areaX, areaY + 2, w, titleH)`.
  2. **Vertical:** the title occupies `areaY + 2` down to `areaY + 2 + h/35`, while the twilight zone
     labels are anchored *upward* from the plot at `plotY - 24` and `plotY - 10`
     (`labelRow0Y`, whose text rect starts at `labelRow0Y - 14`), with
     `plotY = areaY + max(30, h/22) + WeatherMargin`. With no weather forecast, the title clears the top
     label row only when `2 + h/35 < h/22 - 38`, i.e. above about **2370 px** of chart height, so at
     every realistic size they overlap. That is the "Civ" / "Naut." collision in the report. Reserve the
     title's height in `yMarginTop` (as `WeatherMargin` already does for the weather band) rather than
     letting two independently-anchored rows share the space.

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

