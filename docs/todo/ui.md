# TODO -- UI & Rendering

**The open items are GitHub issues** labelled [`area:ui`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%3Aui) since 2026-09-24, when this file was migrated. What is left here is the DONE archive, kept for the measurements and reasons it records. Never add an open `- [ ]` here: open an issue.

## Live Session Tab (Phase 2, Polish)

- [x] Guide star profile bitmap from guider (rendered in GuiderTab star profile panel)
- [x] Dither event markers on guide graph

## TUI Equipment tab (found during the 2026-07-29 cell-buffer session)

- [x] Wire the OTA header's `[X]` to a click: **DONE (2026-07-29).** The glyph was painted but never
  bound, so the only route to removing an OTA was an undiscoverable key. Now:
  - The `[X]` registers a click via `ScrollableList.RegisterRowSpanHits` and arms the same two-step
    confirm the key does, so a stray click cannot delete outright.
  - **The key is `Ctrl+X`, not a bare `X`** (user call). Removing an OTA destroys a configured optical
    train, and a bare letter is precisely what blind key injection walks into; a run of `X`es armed
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

## TUI terminal integration (Windows Terminal / ConEmu), explored 2026-08-08

The TUI already writes the terminal title (OSC 0, change-gated in the `TuiSubCommand` render block)
and Console.Lib already speaks OSC 8 (hyperlinks), OSC 52 (clipboard) and Sixel, so surfacing session
state through the terminal is a small addition that follows an existing pattern:

## An overlay's keyboard routing lives nowhere near the overlay (found + fixed 2026-08-14)

- [x] **An open overlay now claims the keyboard by being PAINTED, and the host asks once: DONE.**
  `DropdownMenuState.HandleKeyDown` always knew how to handle arrows / Enter / Escape, but only ran if a
  host added a case for it in its own `HandleInput` switch, in a different file from both the widget and
  its declaration. There were four dropdowns; three had that case and the Live Session mode pill did not,
  which is why it had no keyboard highlight at all. Nothing reported it: the menu opened, drew and took
  mouse clicks, so the only symptom was arrow keys doing nothing.

  `RenderDropdownMenu` sets `Ui.KeyboardClaimant` as it draws (`IKeyboardClaimant`, on the per-window
  `WindowUiSettings`), and each host asks once before its own routing. **Four per-widget routing cases
  became zero.** Paint order is z-order, so the topmost overlay claims last and wins with no host
  arbitrating; a claimant no longer displayed returns false, so a stale claim needs no clearing (which is
  what makes this simpler than `CaretRect`, where a per-frame reset would be wrong because `PaintLayout`
  runs twice a frame).

  Same shape `Layout.Builder.TextInput` retired for text fields: the declaration IS the registration.
  A new overlay is keyboard-navigable by existing.

## FITS Viewer

- [x] **Before/after split slider** (P0-P2a done 2026-08-18; P3 reload-on-press remains) -- a draggable divider comparing two renditions of the same frame; see [docs/plans/before-after-slider.md](../plans/before-after-slider.md). Uniform A/B (stretch / WB / curves) is free; pixel A/B (enhance) keeps the pre-enhance textures by handle transfer, with reload-on-press as the fallback under memory pressure. Raised 2026-08-18 after Enhance turned out to swap the image with no way back.
- [x] **Star overlay rings sat half a pixel up-left of the blob on a BAYER MOSAIC** (reported + fixed 2026-08-18; the second report of a ring offset, and unrelated to the ticked "+0.5px offset" item below, which is a real fix for the 3-channel case). **Detection never runs on a CFA mosaic**: `FindStarsAsync` debayers to mono first and measures there. And `DebayerBilinearMonoAsync` stores, at output index `(y, x)`, the mean of the 2x2 quad whose TOP-LEFT is `(y, x)` -- whose centre is mosaic `(y + 0.5, x + 0.5)`. So the mono image samples the mosaic half a pixel down-right of where it indexes it, and a centroid measured on it reads half a pixel SMALL in the mosaic coordinates every caller consumes. At 1:1 that is invisible; zoomed to 4x it is the clear two pixels that got reported.
  - **Measured, and the first measurement lied.** A reference centroid computed on the real frame in a window centred on the DETECTED position reported only +0.225 / +0.231 px, because truncating at `v <= 0` pulls the reference toward the very answer under test. Planting stars at known sub-pixel positions instead returns **exactly -0.5000 on both axes for every star**, with a mono control at 0.0000 -- which also proves the debayer is the cause, since nothing else differs between the two paths. Both live on: `BayerCentroidGroundTruthTests` (synthetic, tight) and `BayerCentroidShiftProbe` (real frame, now -0.001 / -0.010).
  - **Corrected at the seam** (`Image.BilinearMonoGridOffset` + `StarList.ShiftedBy`), where the coordinate space changes, rather than by re-centring the debayer. This moves no pixel values, so star counts, HFDs and every byte-pinned detector expectation are untouched (verified: all 12 `FindStarsFromFitsFileTests` cases unchanged). Re-centring would average a different set of neighbours and shift all of them. `StarMask` is deliberately NOT translated -- it indexes the pixels the measurement actually ran on.
- [x] **`?` button pinned right, with space reserved so nothing overlaps it** (2026-08-18). `RightAlignedToolbarActions` is measured BEFORE the left run is placed, so the left run stops short rather than sliding under it -- an overlapped button is worse than an absent one, because it is still registered and takes the click aimed at what is drawn over it. Two things fell out of it: the sizing walk is now ONE pass (`LayOutToolbarButtons` -> `_toolbarBoxes`) that the paint and `HitTestToolbar` both read, deleting the second implementation the hit test used to carry (rule 3, draw == hit); and `"?"` moved out of the core button table so both variants keep it last -- the with-Enhance table used to `Add()` past it and ran group 5 before group 4. Pinned by `ViewerToolbarLayoutTests`, incl. that the button does not move when a neighbour relabels, which is the only assertion a screenshot cannot make.
- [x] **A docked strip could overrun its container** (found + fixed 2026-08-18; the fix is in **DIR.Lib**, `DockLayout<T>.Dock`, so it needs a DIR.Lib release to ship). `Dock` never clamped `size` to what remained, and both halves of the consequence are invisible at the call site: a `Right` strip resolves its x as `Right - size`, so an over-large one walks LEFT past its container and paints over its siblings, and the fill rect is handed the NEGATIVE leftover. Measured live via `describe_layout` at surface width 733: `infoPanel` arranged at `x=283 w=450` inside a parent starting at 459, straight over the split divider at 450..459, and `image` at `w=-176`. So the panel therefore painted over the divider. **This was NOT the cause of the reported ungrabbable-divider bug** (see the entry below) -- it was found while chasing it, reproduces only once a window is narrow enough to over-commit the band, and the user's window was fullscreen. Recorded separately so the wrong attribution does not survive. `Layout.Engine.ArrangeSplit` right above it already clamped, with a comment saying consumer-owned extents must be; `DockLayout` simply never got the same treatment. Pinned in DIR.Lib (`LayoutEngineTests.Dock_*`, incl. `Recompute`, which replays recorded sizes against a new root and needs the identical clamp) and at the consumer by `ViewerFileListResizeTests.HoweverFarTheDividerIsDraggedThePanesStillTileTheBand`.
- [x] **The file-list divider looked unresizable because CURSOR FEEDBACK was gated behind the redraw decision** (reported + fixed 2026-08-18; user-diagnosed). `TianWen.UI.FitsViewer` computed the cursor at the END of `OnRender`, and `OnRender` is gated by `CheckNeedsRedraw`. A pointer move that changes no pixel requests no redraw, so on that path the cursor was never recomputed and the pointer kept whatever kind it last had. The dead zone is every part of the window that repaints for nothing: **the letterbox around the image**, empty file-list space, the gap beside a panel. Unless the image happened to sit flush against the divider, the approach to the handle crossed letterbox, no frame was drawn, no resize cursor appeared -- and a handle with no cursor reads as not being a handle. The PRESS worked throughout (`HandleMouseDown` hit-tests directly), which is exactly why it came alive "once I started dragging it", and why window size was irrelevant. Fixed by setting the cursor on `MouseMove` inside `OnPointerInput`, which needs no frame at all and is the shape **`TianWen.UI.Gui` already used** -- the viewer was the odd one out. Still called after a paint too, for a repaint that changes which regions sit under a stationary pointer.
  - **Lesson worth keeping: a redraw-gated frame loop must not carry work that is not drawing.** Anything a host does at the end of its render -- cursors, hover state, tooltips, telemetry -- is silently skipped exactly where the UI is quiet, and "quiet" is not the same as "nothing to report". The tooltip hover bug fixed hours earlier in this same session was the identical shape one layer up; that one at least needed a repaint, so it could be fixed by requesting one. A cursor needs no repaint, so requesting one would have been the wrong fix.
- [x] **Toolbar wraps to two rows on a narrow window** (user's own suggestion, done 2026-08-19). The unblock was ordering, not new machinery: the band's height is an input to `ComputeLayout`, and what decides it is the measured labels against the window width -- so `PrepareToolbarLayout` measures the run BEFORE the layout pass and `ComputeLayout` reserves `BaseToolbarHeight * _toolbarRows`. `WalkToolbarRows` is then pure arithmetic over the already-measured widths, run twice (once for the row COUNT before the pass, once for the POSITIONS after it, against the arranged band) so no label is measured twice and the two answers cannot drift. The placement walk is capped at the rows that were actually reserved, so it can never paint a row the band has no room for.
  - **The bar did not fit an ORDINARY window, and nobody had noticed.** The full run needs ~1.6k px; every window narrower than that had been silently dropping its tail, which at 1400 px is SPCC and NeutBg -- two of the buttons whose whole job is colour calibration. The tell was in the test suite, not the app: `ViewerToolbarLayoutTests` used a 1400 px surface as its "wide" baseline, so the pinning tests had been asserting against a bar that was already over-committed. The baseline is now 2400 (stated, with the reason) and 1400 became `OrdinaryWindowW`, the wrap case.
  - **Help stays on the FIRST row**, not the last: a corner that moves down whenever the wrap count changes is exactly the drift the pin exists to prevent, and the wrap count changes with the window. A wrapped row also starts flush under the one above with no leading group gap -- the row break already says what the gap would have.
  - **Two rows is a cap, deliberately.** The band comes out of the image pane, so an unbounded wrap turns a small window into a toolbar with a picture attached; past two rows the tail is still dropped, and at that width the open question is which panel should yield (the item above), not how tall the toolbar may grow.
  - Pinned by `ViewerToolbarLayoutTests`: the wrap keeps every button, the band doubles rather than the second row painting over the image, no two buttons overlap at all (the invariant that replaced "everything is left of help", which stopped being equivalent once rows exist), and every rect stays inside the band. All three were seen to FAIL with `MaxToolbarRows` forced to 1.
- [x] **Toolbar marks, wave 1 (app-drawn).** The `BayerSwatchWidth`/`DrawBayerSwatch` pair generalised into
  `ToolbarMarkWidth`/`HasToolbarMark`/`DrawToolbarMark`, plus `MarkGap` as the ONE place the mark-to-label
  spacing is derived (measure and paint both call it, so a label cannot land a gap away from where its
  button was sized for it). Three marks, all `Content.Fill`-style because colour is the information:
  a stateful RGB triple for **Channel**, a 45-degree hash lattice for **Grid**, a spiral for **Objects**
  and a tapered four-armed star for **Stars**. Channel, Grid and Objects go WITHOUT a text label -- the
  width is the whole point, and a mark beside the word it replaces gives none back. Stars keeps a label
  because the label is the COUNT; before the pass has run the word "Stars" stands where the number will
  be, which is what marks it as not-yet-run.
    - **Measured**: about 130 design units of text removed. That showed up as four toolbar WRAP tests
      failing on their own guard assertions, because the fixture width was chosen to be just-too-narrow
      and the bar no longer was. `OrdinaryWindowW` dropped 1400 -> 1150 and
      `TheOrdinaryWindowIsStillNarrowerThanTheRun` now pins the premise and reports both numbers, since
      the four guards could only say "not two rows", never why.
    - **A colour mark dims by losing BRIGHTNESS, not by taking the label's grey ink** (`DimIfDisabled`),
      because its hue is the information. Without that a disabled button with no label reads as live,
      which is exactly what Channel did the moment its text went: a one-channel frame disables channel
      selection, the button correctly registers no click region at all, and it still painted three fully
      saturated bars.
    - **Objects prefers U+1F300 from a colour-emoji face and falls back to the drawn spiral.** A font
      designer has already solved a spiral at icon size and three attempts at geometry did not (see
      below). The fallback is not hedging: this project bundles no emoji face, so a Linux host resolves
      none, and a missing glyph draws NOTHING rather than a placeholder. The emoji cannot dim, so if
      Objects ever becomes a disableable button the geometry has to win.
    - **The RGB triple went on Channel, not on Calibrate/SPCC as this entry originally said.** On
      Calibrate/SPCC the words ARE the differentiator (photometric vs spectrophotometric), so the same
      mark on two adjacent buttons would make them more confusable and save nothing, since the labels
      would have to stay. On Channel it replaces the label outright and encodes which channel is live.
    - **A mark is conditional on the frame having the thing depicted**, following the swatch's own rule:
      `ChannelView.Channel0..2` are not colours, so those fall back to naming themselves.
    - Objects keeps a bare `...` while the object DB is unloaded. That is not its name, it is the warning
      that the first press pays for the load, and a tooltip shows too late to set that expectation.
    - Failures worth not repeating, none of them visible until the marks were magnified. An **outlined**
      ellipse with any core reads as an **eye** -- the eye is in the OUTLINE, not the pupil, so elongating
      the core into a lens along the same axis did not help. Filling that ellipse by over-thickening its
      stroke does not work either: a stroke inks half its thickness either side of the path, so it fills
      along the minor axis and leaves a lens-shaped hole along the MAJOR one, which is a ring with a dark
      middle -- the eye again. Only a shape swept from CIRCLES fills, because the trick needs both radii
      equal. And a bowed graticule cannot work at this size at all: a tenth of the width of bow is ONE
      pixel, while sampling the bow with two segments puts a kink on the mark's own midline and reads as
      a hexagon.
    - **Judge a mark at its real pixel size, and get the real pixels.** The `sdl-ui-inspector`
      screenshot is DOWNSCALED (a 2902 px framebuffer arrived as ~1999 px, a factor of 0.69), which
      silently destroys exactly the sub-pixel detail an icon is made of -- a tapered star arrived as a
      plain `+` and was judged broken when it was fine. Use a DPI-aware `PrintWindow` capture
      (`SetThreadDpiAwarenessContext(-4)`, or PowerShell virtualises the coordinates and downscales too)
      plus nearest-neighbour magnification.
- [x] Remove debug `Console.Error.WriteLine` WCS output from `Program.cs` DONE (2026-06-02): none present in `TianWen.UI.FitsViewer/Program.cs` (all logging via `ILogger`).
- [x] Support rec601/rec2020 luminance weighting options in luma stretch (2026-05-11); see Stretch / Image Processing section.
- [x] Annotation overlay (object names from catalogs when plate-solved)
- [x] Star detection overlay: `FitsDocument.DetectStarsAsync()` runs as background task,
      draws HFD-sized green circles, shows count/HFR/FWHM in status bar (S key toggle)
- [x] Background neutralization toggle: N key and toolbar `NeutBg` button; computes pivot1 gains from `ScanBackgroundRegion` and applies via GPU shader
- [x] SPCC color calibration via W key: tries spectrophotometric (Pickles SED + system throughput) first, falls back to sky-background method; toolbar `SPCC` button
- [x] Clip star overlay circles to image viewport + fix centroid alignment (+0.5px offset)
- [x] Replace custom `AsyncLazy<T>` with `DotNext.Threading.AsyncLazy<T>` (already a dependency in TianWen.Lib)
- [x] Use a `WeakReference<AstroImageDocument>` cache (keyed by file path) so that cycling through
      images can reuse recently loaded documents without keeping them pinned in memory
      (`DocumentCache` with `ConditionalWeakTable` + `WeakReference<T>`)

## Planetary: the histogram has to answer "what exposure?" (user, 2026-09-14)

**The ask, verbatim:** *"in the planetary code its kind of important to know where the exp time is
w.r.t. to histogram (usually 8-bits there though), so gauge the min exp time required to reach a
certain histogram etc"*.

Planetary is the one place a histogram is a **capture control** rather than a display aid. The whole
technique is short exposures at the frame rate the seeing allows, and the exposure is chosen by where
the disk's peak sits: too low and the stack is read-noise limited, too high and the highlights clip
and no amount of stacking gets them back. SharpCap and FireCapture both put a target line on the
histogram for exactly this. Today the planetary preview draws a histogram (it renders through
`LiveStackPreviewSource`, which is document-backed, so it has one) but says nothing about the
exposure that produced it -- the number is right there in `PlanetaryCaptureController`, unjoined.

**Shape.** Exposure and signal are linear above the offset, so the gauge is arithmetic, not a model:
with the frame's peak (or a high percentile, which is what to use -- a hot pixel must not set the
scale) at `p` of full scale for exposure `t`, the exposure reaching a target `T` is
`t * (T - offset) / (p - offset)`. Report it beside the histogram as "peak 38% -- 1.9x for 75%", and
refuse to extrapolate through a clipped frame (if `p` is already at full scale the true peak is
unknown and the only honest answer is "clipping, shorten it").

Three things to get right, none of them the arithmetic:

- **8-bit is the normal case here** and full scale is the CONTAINER's, not `Image.MaxValue` (the
  observed peak). `ImageMeta.SensorFullScaleAdu` is the field that already means this; a video stream
  that does not carry one falls back to the bit depth's full scale, never to the frame's own maximum --
  dividing by the observed peak would report every frame as perfectly exposed.
- **Percentile, not maximum**, and taken on the DISK rather than the whole frame: a planetary frame is
  mostly black sky, so a whole-frame percentile is a percentile of the background. The segmentation
  already exists (`GlobalAligner`'s disk centroid).
- **It belongs to the capture controller, not the viewer.** The viewer draws a histogram for any
  source; only the planetary path knows the exposure, the offset and the gain, and only it can act on
  the answer. `PlanetaryCaptureController` already owns all three and already drives the exposure.

Not started. Wants a real capture to calibrate the target against (75% of full scale is the usual
advice for a linear sensor; worth checking against what the SV605CC actually does).

## SdlVulkan.Renderer

- [x] Font atlas corruption: root cause: shared upload buffer race with `MaxFramesInFlight=2`. Frame N+1's `Flush` overwrites the upload buffer while frame N's `vkCmdCopyBufferToImage` is still reading it. Fixed with `vkDeviceWaitIdle()` before upload buffer reuse.
- [x] Replace `vkDeviceWaitIdle` in font atlas `Flush` with per-frame upload buffers (like `_vertexBuffers`) to avoid GPU stall on every glyph upload; `VkFontAtlas` + `VkSdfFontAtlas` now keep an N-slot ring indexed by `ctx.CurrentFrame`; `MaxFramesInFlight` exposed as `public const` on `VulkanContext` (commit `3ccd6a2`).
- [x] SDF font atlas: `Grow()` / `CreateImage` used to transition the fresh `VkImage` via `ctx.ExecuteOneShot`, which submits a side cmd buffer to the graphics queue while the frame's cmd buffer is recording; some drivers reject this with `VK_ERROR_INITIALIZATION_FAILED` from the next `vkQueueSubmit`. Fixed: deferred initial transition to the next `Flush` via `_needsInitialTransition` flag; initial atlas dim now scales with `SdfRasterSize` (`2048²` at 128px raster) so `Grow()` rarely fires during typical startup UI anyway (commit `30fcdf7`).
- [x] `VkTexture.CreateDeferred`: pixel-format parameter; was hard-coded to `B8G8R8A8Unorm`, which forced RGBA-producing CPU renderers (altitude chart via `RgbaImageRenderer`) to run a per-pixel swizzle loop before upload. Now takes `VkFormat format = B8G8R8A8Unorm` so callers can pass `R8G8B8A8Unorm` with RGBA bytes directly (commit `90f877a`); `VkPlannerTab` dropped its CPU swizzle loop.

### SdlEventLoop (DONE, all consumers now use the shared loop)
- [x] Add `DropFile` event support (`EventType.DropFile`); `Action<string>? OnDropFile`
- [x] Multi-button mouse: `OnMouseDown` passes button ID + click count (`Func<byte, float, float, byte, bool>?`)
- [x] `OnMouseUp` passes button ID (`Action<byte>?`)
- [x] `OnMouseWheel` passes tracked mouse position (no more hardcoded 0, 0)
- [x] F11 fullscreen removed from loop, each consumer handles it in `OnKeyDown`
- [x] Migrated `TianWen.UI.FitsViewer/Program.cs` to use `SdlEventLoop`
- [x] Touch input: pinch-to-zoom via `SDL_EVENT_FINGER_*` events; two-finger tracking + scale computation in `SdlEventLoop` (`OnPinch`/`OnPinchEnd`), consumed by `SkyMapTab` via `InputEvent.Pinch`/`PinchEnd` ("Add touch pinch zoom, center-point zoom, grid dedup, Alt/Az grid + mode")

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
- [x] **Panning near the pole (EQ) / zenith (horizon) swings the field.** **DONE (2026-08-05), root fix,
  not the mitigation.** `SkyMapState.CenterRoll` now stores the third degree of freedom and
  `ComputeViewMatrix` takes NO reference direction, so it cannot be singular and the hardcoded
  `(1,0,0)` fallback is gone. `HandleDrag` rotates the whole frame (forward AND right) and decomposes
  via `FrameToCenter`, so a pan is rigid wherever the view points. `UpdateRollForReference` (one call
  site, `SkyMapUbo.Write`, which is where the zenith is known) keeps the mode's promise of north-up /
  zenith-up, and HOLDS the roll inside 5 degrees of the reference, where the reference cannot name an
  up direction. Two things found while doing it. (1) The frame `right = (sinRA, -cosRA, 0)` is unit and
  perpendicular to forward at EVERY Dec, and `forward x zhat` is exactly `cosDec` times it, so
  **roll 0 reproduces the old matrix identically** wherever the old one was well-conditioned; that is
  what makes this a rewrite rather than a change of look, and it is pinned against a legacy-oracle
  matrix. Equatorial mode therefore needs no realignment at all (north-up IS roll 0). (2) Only Horizon
  mode could actually reach the singularity: EQ is fenced off by `NormalizeCenter`'s +/-89.5 Dec clamp,
  which remains as a separate projection guard, so the pole itself is still 0.5 degrees out of reach.
  **First cut had a visible seam and the user caught it in the GUI**, so it was fixed on top and the
  lesson is worth keeping. Holding the roll inside a lock cone and re-levelling outside it means
  crossing the boundary snaps the accumulated roll away in ONE frame: measured at 63 degrees at
  Dec 85, which reads as the view flipping, not panning. Two causes, both now closed. The re-level ran
  every frame INCLUDING mid-drag, so it fought the gesture that owns the roll; it is now suppressed
  while `IsDragging`. And **Equatorial mode never needed a cone at all**, since north-up is
  analytically roll 0 at every Dec in this frame, so there was nothing ill-conditioned to protect
  against; the cone is now Horizon-only, where pointing at the zenith genuinely leaves "zenith up"
  undefined. Re-levelling also *approaches* its target (a quarter of the remaining angle per frame,
  shortest way round, landing exactly), so it reads as a movement rather than a jump; an
  already-level view is inside the snap distance immediately and is untouched, which is the common
  case since the Equatorial target is a constant 0. Pinned by `SkyMapViewOrientationTests`, including
  the reported symptom as a number (a 0.1 degree pan at Dec 89.5 turns the field by 0.1 degrees, not
  by the roughly 115x amplified angle `1 / cos(Dec)` gives) and the 63 degree roll travelling back
  without a leap. Verified live in the GUI: panning RA 2.50h to 4.07h at Dec 85 keeps the SCP directly
  below the view centre in both frames. Original analysis:
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
- [x] **The web sky map has no grid by default.** **DONE (2026-08-05), and the recorded root cause was
  wrong.** `SkyMapState.ShowGrid` already defaults to `true` and the WebGL pipeline honours it, so
  there was nothing to turn on; the guess to "check the initial state" would have found nothing. The
  real defect is that the two pipelines each carried their own scale-selection rule and the browser's
  had a **lower FOV bound** (`fov >= minFov`). The scales are **complementary**, not alternatives:
  `BuildGridLines` omits every line a coarser scale draws, so gating one off deletes its lines instead
  of thinning the grid. Below 30 degrees the browser therefore dropped scale 0 and with it the
  celestial equator, the +/-30 and +/-60 parallels and the 0h/6h/12h/18h meridians, leaving only the
  in-between lines: zoom in, and the anchors vanish. It also drew at a flat alpha (0x70 against the
  desktop's 0xB0 at full fade), so a crowded scale never faded and the grid was dimmer everywhere.
  Selection, fade and colour now live in `SkyMapGpuGeometry.TryGetGridFade` / `GridColorAt`, used by
  both pipelines. Pinned by `SkyMapGridScaleTests` (the anchor scale is active across the whole
  [0.5, 180] zoom clamp, some scale is always active, fine scales still drop when the view is wide).
  The Horizon-mode remark also does not hold up: `ShowAltAzGrid` is opt-in and `ShowHorizon` is on by
  default on **both** surfaces, so an alt-az grid is not a web-only gap and the horizon line is
  already the reference. Original analysis:

## Sky Map + web input (reported and fixed 2026-08-06)

Five user-reported items, all fixed the same day. Two of the causes recorded when these were first
written turned out to be wrong; both corrections are kept below, because each was the plausible
reading and the next person will reach for it again.

- [x] **Panning jerks, and the view keeps rotating after the mouse is released.** Three causes. The
  two below were both real and both fixed, but **neither was the reported symptom** -- that was (3),
  found only after the reporter said it was still happening and that it eased away from the pole.
  Recorded in this order because the order is the lesson: (1) and (2) were the visible, plausible
  suspects, and fixing them changed how fast the wrong thing happened rather than stopping it.
  1. `SkyMapState.UpdateRollForReference` eased `CenterRoll` toward the mode's reference by a flat
     fraction of the remaining angle **per FRAME**, so the travel took a fixed number of frames and
     therefore a duration that scaled with frame time: the same ten frames are 0.17 s at 60 fps and a
     full second at 10 fps, which is why it read as a settle on the desktop and as the view turning
     by itself on the web build. Now an exponential rate **per second** (time constant 0.058 s, which
     reproduces the old feel exactly at 60 fps), measured from a monotonic `Stopwatch` read, with an
     optional explicit `deltaSeconds` for callers that step deterministically. Pinned by
     `SkyMapViewOrientationTests`: the same simulated half-second converges the same at 5 fps and at
     60 fps.
  2. `SkyMapTab` refreshed its cached viewing time **once per second** and reused that instant for
     every frame in between. In horizon framing the roll reference is the zenith, a function of that
     time, so the realignment target and the whole view matrix stepped at 1 Hz, which during a slow
     pan reads as the sky twitching. The cache now interpolates between syncs (`_cachedLiveTime +
     elapsed`), so the viewing time is continuous while `GetUtcNow` still runs only once a second.
     **The reporter diagnosed this one unaided.**

  3. **The real one.** `UpdateRollForReference` servoed `CenterRoll` to the mode's ABSOLUTE
     reference every frame, and in Equatorial that reference is the constant 0 -- so it had no
     legitimate work at all (celestial north does not move) and existed only to undo the user's own
     pan. A drag rotates the whole frame rigidly and near the pole legitimately earns a large roll,
     because a change of RA at the pole IS a rotation; that roll is precisely what holds the sky
     still under the pointer. Measured over a 100 px pan: 82.5 deg of roll earned at Dec -89, 56.7 at
     -85, 13.0 at -60, 4.4 at -30, exactly 0 at the equator -- which is the "less severe away from the
     pole" the reporter noticed, and why (1) and (2) could never have explained it. The grabbed star
     landed under the cursor at release at every declination and was then thrown **131.9 px** by the
     unwind, further than the 100 px the gesture had moved it.

     The roll now follows how far the reference MOVED since the previous frame instead of servoing to
     its value: identically nothing in Equatorial, the sky's own rotation in Horizon (so the horizon
     still self-levels over a session, carrying a gesture's offset along untouched). Nothing re-levels
     unprompted any more, so **L** levels the view and the status strip shows a `[L]evel` hint only
     while the view is off its reference; **P** re-establishes the new mode's frame on a switch.
     Pinned by `SkyMapPanTests`, which measures what the user actually sees -- where the grabbed star
     is, in pixels -- in both modes, at the zenith, and just outside the old lock cone.

     **Behaviour change:** the atlas now stays where you drag it, so a pole pan leaves the view
     genuinely tilted until L. That is the cost of keeping the sky under the pointer, and it is the
     right trade only because the alternative is the view moving on its own after you let go.
  - **WRONG when first recorded:** that the easing also fought the drag. It does not; there has been
    an explicit `if (IsDragging) return false` guard since the pole fix. Do not "fix" it again.

- [x] **A pinned comet does not appear in the atlas, while pinned fixed objects do** (reported for
  10P). Both causes were real and either was sufficient. The pinned-landmark path gathers out of
  `ICelestialObjectDB`, where comets deliberately are not, so a pinned comet index matched nothing;
  and the comet layer that could draw it filtered on the zoom-aware magnitude limit with no pinned
  bypass. `DrawCometLabels` now takes the pinned set, the layer runs when anything is pinned even
  with the comet toggle off, and a pinned comet draws with the shared pinned halo whatever its
  magnitude. The rule is a pure predicate (`SkyMapState.ShouldDrawCometMarker`) so it is testable
  without a renderer, which matters because the comet layer is the ONLY route a comet has onto the
  map. Halo colour moved to `OverlayEngine.PinnedHaloColor` so a pinned comet and a pinned DSO cannot
  drift apart.

- [x] **A pinned comet's RA/Dec was a snapshot.** The sky-map markers were already live to the clock
  tick; the frozen value was the planner pin, which resolves its position once when the search result
  is built. `PlannerActions.RefreshCometProposalPositions` now re-resolves every pinned comet from
  `ICometRepository` on the proposal-change hook (bounded, not per frame), rewriting only when the
  comet has moved more than an arcminute so the render thread's `ImmutableArray` is not churned.
  Identity is untouched because `IsSameObject` matches solar-system bodies by `CatalogIndex`.

- [x] **[web] The selected object is in the URL** as `?object=<canonical>`, alongside the existing
  `?view=`. Written with replace-state so a session of clicking does not bury the entry the user
  arrived on, carried across a view switch, and resolved back through the SEARCH resolver
  (`SkyMapSearchActions.TrySelectByToken`) rather than a catalog lookup, so comets restore too. A
  deep link parsed before the catalog loads is retried once the tab exists.

- [x] **[web] A touchpad pinch is treated as a pinch.** A browser reports one as a wheel event with
  `ctrlKey` set, so it never reached `HandlePinchZoom` or got tagged `PinchSource.Touchpad`, and the
  touchSCREEN pinch felt better because it is genuinely different code arriving as a real
  `InputEvent.Pinch`. `OnWheel` now branches on `ctrlKey`, emits a relative-scale `Pinch` +
  `PinchEnd` (a wheel stream has no end event, and a latched `IsPinching` would swallow every later
  drag), and normalises by `DeltaMode` first, since the divisor was a hardcoded 100 whether the browser
  reported pixels, lines or pages, which made one notch a 15% zoom in Chrome and 0.45% in Firefox.

## Selected text is invisible in every text field (reported 2026-09-07, seen on the web atlas)

- [x] **The selection highlight was painted OVER the glyphs**, so selected text disappeared (fixed 2026-09-08, DIR.Lib 8.14).
  `TextInputRenderer` (DIR.Lib) draws the text run first and fills the selection rect afterwards
  (`colors.Selection`, default `RGBAColor32(60, 90, 150, 180)`); at alpha 180 over thin glyph ink there
  is nothing left to read. The user's screenshot (web sky atlas, F3) shows the field as a plain
  blue-grey block, with the result row beneath it already narrowed to `C30`, which is the text that
  should have been legible in the box.
  - **Not atlas-specific and not web-specific.** `SkyMapSearchActions.OpenSearch` ends with
    `Activate()` then `SelectAll()`, so every F3 arrives with the previous query selected. That is
    correct behaviour (the next keystroke replaces it) and merely the most common way to meet the bug:
    the same renderer serves every `Layout.Builder.TextInput` in the GUI, the viewer and the web build,
    so Ctrl+A, a double-click or a drag-select hides its own text anywhere.
  - **Two candidate fixes, both in DIR.Lib.** Paint the highlight BEFORE the run, which is the
    conventional order and costs nothing; or keep the order and repaint the selected sub-run in a
    contrasting colour on top, which is what an opaque highlight would need and which adds a
    `SelectedText` palette entry every theme then has to state. Prefer the first. The colour is not the
    culprit: no highlight drawn last can leave dark glyphs legible under it.
  - **Fixed by moving the fill under the run**, the first of the two candidates: two lines, no new palette
    entry. Pinned in DIR.Lib by
    `LayoutTextInputTests.SelectedText_KeepsItsContrast_BecauseTheHighlightIsPaintedUnderTheGlyphs`, which
    renders a real face and measures mean ink contrast against the field's own local background rather than
    asserting geometry (a geometry-only test cannot see this, which is how it shipped). Measured: **125.3
    unselected, 106.5 with the highlight underneath (85%), 37.1 with it over the top (29.6%, the alpha
    residual exactly)**; the bound is 60%, and the test was seen to fail at 37.1 with the fill moved back.
  - Reaches TianWen when DIR.Lib 8.14 is published and `src/Directory.Packages.props` re-pins from
    `8.13.*`. Every field in the GUI, the viewer and the web build gets it at once.

## Sky Map: label collisions, a "pictures" mode, and hover highlight (user's note 2026-09-19)

Three items from one note, root-caused against the code rather than left as a bare repro request.

- [x] **A "show objects with picture" mode. DONE 2026-09-20.** Shipped as
  `SkyMapState.ShowOnlyObjectsWithPicture`, a SUB-SETTING of the object overlay rather than a layer of
  its own -- it draws nothing, it only narrows what [O] and [D] already admit -- keyed `I` and drawn
  indented under "Objects" in the layer palette, unavailable (dimmed, key unhandled) while [O] is off.
  The per-object camera mark this builds on stays exactly as it was; what was missing was the filter.
  - **One predicate, three callers**: `OverlayEngine.PassesLayerFilter` / `ApplyLayerFilter`, asked by
    the desktop's background gather, the browser / offline primitive path AND the click resolver. The
    first two were already two hand-maintained copies of the [O]/[D] rule; a third copy of a four-term
    rule was not worth writing, and the click gate is the one that *must* agree or a filtered-out
    object stays selectable through apparently-empty sky.
  - **It does NOT reach the dark-nebula layer.** It was first written to (on the argument that "only
    with photo" is about the whole overlay), and that was reversed in "the review's two confirmed
    bugs": the row is a sub-setting of [O], indented under it and unavailable while [O] is off, so a
    filter reaching a layer [O] does not govern contradicted the control's shape -- and with [O] off
    and [D] on it went on emptying the layer while the row read as off, with no way to reach it.
    Pinned by `TheFilterDoesNotReachTheDarkNebulaLayer`. A pinned target survives it, as it survives
    every other filter on that path.
  - **It is in both gather cache keys** (`PrimOverlayKey`, `OverlayGatherKey`), because it strips the
    CACHED candidate list: without that, switching it on keeps serving the list gathered before it and
    switching it off never brings the objects back.
- [x] **Hover recolours the object that would be selected. DONE 2026-09-20, and it did NOT reopen the
  settled decision.** That decision was about which gesture SELECTS (still the click); this is the
  question it left open -- on a field of overlapping markers nothing told you which object a click
  would take. `SkyMapState.HoverTarget` + `SkyMapTab.Hover.cs` paint a translucent wash under that
  object; the press still decides. Very likely the same complaint as the 2026-06-08 self-note "sky
  atlas bug: obj selection" (see [inbox.md](inbox.md)).
  - **The load-bearing property is that the wash and the click come from ONE resolver**
    (`SkyMapSearchActions.TryResolveHit`, which `SelectObjectByClick` and the new
    `ResolveHoverAtScreenPoint` both call). Two hit tests written the same way is exactly how a wash
    over one object and a panel about another happens, and that is worse than no wash.
  - **Ctrl is deliberately not honoured by the hover.** The modifier is read at the press and a hover
    carries none, so guessing would be wrong precisely when the user is holding Ctrl to pick a star
    out of a nebula.
  - **One `FillEllipse`, no new machinery.** All three renderers implement it natively (Vulkan and
    WebGL as a single distance-field quad), so the wash needs no instance stream, no cache key and no
    shader -- and drawing it FIRST of the annotation layers is what makes a pointer resting over the
    search modal or the layer palette harmless without any of them claiming the pointer.
  - **At most one resolve per painted frame**, bounded by a checked-in benchmark
    (`SkyMapHoverResolveBenchmarks`, Release, win-arm64): over an OBJECT ~9 us at any zoom, over
    bare STAR FIELD 157 us at 1 degree, 134 at 10, and ~1.7 us by 60, with 0 B on every row; before
    the fixes, 389 us / 225 KB and 357, and 166 us / 27.6 KB after the first of them. **The two paths differ by 16x** (44x before) -- the DSO pass
    runs first and the star pass never runs when it matches -- which the original single number hid.
    **The gap is that short-circuit and NOTHING else**: the nine index cells derive from the
    unprojected pointer and are identical at every zoom, and what decides whether they are walked is
    the DSO pass's hit test floored at a fixed 20 SCREEN px -- 0.020 deg of sky at 1 degree FOV
    against 4.200 at 170. Within the pass, `TryLookupByIndex` x1094 was 320 of the 389 us and 197 of
    the 225 KB, six times the nine cell lookups (53 us), and over half of it a constellation lookup
    precessing every candidate through heap arrays to fill a field the hit test never reads. **Fixed
    2026-09-20**: the precession and the base91 index decode are allocation-free for every caller in
    the program, and the star pass reads a Tycho-2 candidate through `TryGetTycho2Star` and looks up
    only the winner in full (`Tycho2LiteLookupParityTests` pins that the two read the same star).
    **Fixed 2026-09-21**: the cells are walked through `IRaDecIndex.EnumerateCell` (the struct
    `RaDecCell`, scanning the Tycho-2 regions as the caller advances) instead of the indexer's
    per-cell `List`, wrapper and iterator, and the composite grid is cached on the database, which
    took the last 27.6 KB and the 288 B to nothing (`RaDecCellEnumerationTests`, whole sky, against
    the catalogue bucketed independently). Attribution is `SkyMapHoverResolveCostProbe` (`TIANWEN_HOVER_PROBE=1 DOTNET_TieredCompilation=0`;
    with tiering on it ranks by JIT order, which is where a "905 ns" once quoted here came from). An
    earlier version of this entry blamed `GetStarsInCell` for both halves, and one before it credited
    `EffectiveMagnitudeLimit`, which is the minor term and runs the other way. The figures first
    published here came from a Debug test run and were about 5x pessimistic as well as blended; that
    is what the benchmark replaced.
  - **The hover target is dropped when the view moves**, compared at draw time against the view it was
    resolved for rather than cleared at each of the five call sites that move it.

## Charts and the web showcase (user's notes 2026-08-27)

- [x] **Milky Way texture in the web sky map: DONE (2026-09-17).** The desktop path had it
  ([skymap-milkyway](../plans/skymap-milkyway.md)); the WebGL pipeline did not. Shipped as that plan's
  Phase 6, on WebGl.Renderer 1.33 (SharpAstro/WebGl.Renderer#9).

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

## SignalBus / render-thread invariants

- [x] **No device connect/disconnect may run its synchronous prefix on the render thread** (DONE
      2026-07-04). `SignalBus.ProcessPending` runs per-frame on the render thread and invokes async
      handlers *inline* (`var task = handler(signal)`) up to their first yielding `await`; the
      `BackgroundTaskTracker` only tracks the already-started task, it does **not** offload the
      prefix. A driver that blocks before its first await (ASCOM COM `Connected = true/false`
      busy-spinning `Application.DoEvents()`: Gemini FlatPanel, iOptron, GS Server) therefore froze
      the GUI. Fix: all four connect/disconnect sites route through
      `AppSignalHandler.RunDeviceOpOffRenderThreadAsync` (a `Task.Run` offload). **Invariant for new
      code:** any signal handler that may call a blocking driver op must offload it the same way,
      never `await hub.XAsync(...)` directly in an inline-invoked handler. The deeper ASCOM
      correctness fix (STA + message pump) is [../plans/ascom-com-sta-message-pump.md](../plans/ascom-com-sta-message-pump.md).
