# Layout-Driven UI (99% goal)

**Goal (user, 2026-07-21):** the GUI should be ~99% layout-driven. Every piece of chrome -- headers,
buttons, rows, panels, labels, progress bars -- is placed by an arranged `DIR.Lib.Layout` tree
(`Layout.Builder` + `RenderLayout`/`PaintLayout`). Direct pixel math survives only inside **keyed
`Fill`-leaf painters** (charts, histogram, image placement, sky-map raster, on-image overlays) and
**control internals** (`TrackSlider`, `ListScrollController.DrawScrollBar`) -- the "1%".

**The goal is LESS CODE, not just fewer lines per file** (user clarification, 2026-07-21). Splitting a
big tab into partials is prep, not the win; the win is the DSL collapsing hand-computed
`FillRect`+`DrawText`+coordinate-math into terse builder chains. **Key finding from L1+L2:** layout
conversion shrinks *positioning* code, not business logic -- so the payoff is uneven. A **viz-heavy tab**
(GuiderTab: charts, guide graph, star profile, target scatter) barely shrinks, because the raster
painters that dominate its line count stay pixel by design (L1 was net +lines: the chrome that
converted is a minority, the tree-building added scaffolding). A **form-heavy tab** (EquipmentTab:
rows, cells, buttons, labels) shrinks a lot -- the mount/camera readout rows dropped ~60% of their
positioning code (commit net -37). So target the form/chrome tabs for the code-reduction win; convert
viz tabs for the *consistency* wins (draw==hit, describe_layout coverage, DPI-once) knowing they won't
shrink.

This is the placement/paint counterpart of the interaction-primitives project
([interaction-primitives.md](interaction-primitives.md) fixed *input*; this fixes *geometry*). The
taxonomy + rules live in
[../architecture/widgets-and-controls.md](../architecture/widgets-and-controls.md).

## Audit (measured 2026-07-21)

`FillRect(`/`DrawText(`/`DrawLine(` call sites in `TianWen.UI.Abstractions`: **479 across 25 files**.

| File(s) | Direct draws | Verdict |
|---|---:|---|
| `LiveSessionTab.{Panels,Preview,Polar,Strips,Flats,cs}` | 144 | chrome -> convert (image-aligned preview overlays -- reticle, ROI rect -- stay pixel) |
| `GuiderTab.cs` | 70 | chrome + stats panel -> convert; the guide-graph raster stays a Fill leaf |
| `EquipmentTab.cs` | 60 | rows are ALREADY layout micro-trees; kill the `cursor +=` stitching between them |
| `SessionTab.cs` | 35 | config panel already the gold standard; remaining non-config chrome -> convert |
| `SkyMapTab.Search.cs` | 29 | F3 modal + object info panel -> convert |
| Viewer chrome: `ImageRendererBase.{InfoPanel,Toolbar,FileList,Transport}` | 32 | convert; sliders stay on the `TrackSlider` control |
| `PlannerTab.cs` | 11 | list rows + details lines -> row templates |
| `NotificationsTab.cs` | 8 | header + row content -> row template |
| Raster + control internals: `AltitudeChartRenderer` 30, `LiveSessionTab.Charts` 22, `SkyMapTab.cs` 14, `Histogram` 4, `Overlays` 6, `SkyMapRenderer` 5, `ObjectOverlay` 3, `TrackSlider` 3, `ImageRendererBase.cs` 3 | ~90 | **stays pixel by design** (the 1%) |

So ~390 call sites are convertible chrome. Three anti-patterns account for nearly all of it:

1. **Micro-trees stitched with a pixel cursor.** `EquipmentTab` builds 22 per-row `Layout` trees but
   places each one at `new RectF32(x + padding, cursor, ...)` with hand-maintained `cursor += fieldH + 2`
   between them -- the VStack should own the stacking. (Historical residue of incremental conversion,
   not a DSL gap.)
2. **Raw-draw panels.** `GuiderTab` has zero layout usage; the LiveSession side panels
   (per-OTA status, mount block, exposure progress, polar/flats panels, control strips), the viewer's
   InfoPanel/Toolbar/Transport/StatusBar content, the sky-map F3 modal, and the Notifications
   header/rows are all placed with `pad * dpiScale` arithmetic.
3. **Hand-placed row content inside `VisibleRows()` rects.** The controller hands each row its rect,
   then the consumer computes stripe/timestamp/message x-offsets manually. A row is a trivial
   HStack (fixed | fixed | star) arranged into `rowRect`.

## Target patterns (both already proven in-tree)

- **One tree per panel, scrolled by the root offset** -- `SessionTab.RenderConfigPanel`
  (`SessionTab.cs` ~757-790): build the WHOLE form as one tree (`SessionConfigLayout.Build`), arrange
  it at `rect.Y - scroll.Offset * lineHeight` with the full content height, cull arranged nodes that
  don't intersect the viewport (so off-screen clickables never register), `PushClip`, `PaintLayout`.
  This is the migration target for every side panel and for Equipment's cursor-kill.
- **Keyed Fill leaves for raster regions** -- `PlannerTab.BuildFrameLayout` + `RectOfFill`, and the
  viewer's `ComputeLayout` single pass: the tree places the region, the pixel painter draws INSIDE
  the arranged rect it reads back. Charts/histogram/image/sky-map stay exactly this.
- **Row templates** -- per-visible-row trees arranged into the `VisibleRows()` rect
  (`RenderLayout(rowTree, rowRect, ...)`). Consumer-owned, per the standing decision that
  heterogeneous rows preclude a DIR.Lib render-callback list widget; a shared helper is a DIR.Lib
  6.16+ evaluation item, not a blocker.

## Phases

All tianwen-only (the current DIR.Lib layout API suffices -- same situation as P5); no release
dependency, does NOT wait on the 6.15 chain. Verification bar per phase: arranged-rect pins +
offline `RgbaImageRenderer` pixel-render tests (the `PlannerTabLayoutTests` pattern), and the
UI-refactor bar is "all elements present + similar footprint", not pixel-perfect.

| Phase | Scope | Notes |
|---|---|---|
| L1 | **GuiderTab** full conversion -- **DONE 2026-07-21** | The whole tab is ONE tree (`BuildFrameTree`: header HStack, stats VStack via list-building instead of cursor arithmetic, panel titles, empty-state Text leaves); the four raster panes (camera / profile plot / target scatter / graph) paint inside keyed Fill leaves via the `drawFill` callback, with pane backgrounds on the nodes. Placeholder states are their own tree. 70 raw draw sites -> 0 outside the Fill painters. New `GuiderTabLayoutTests` (6: arranged-rect pins at 1x + 2x DPI, placeholder, sentinel paint sweep at 3 sizes) + `InternalsVisibleTo` for UI.Abstractions (rect test seams stay internal). Live-smoked: tab now appears in `describe_layout` (was invisible -- no DSL usage before); frame_stats 2.1 ms avg (floor 40). Guiding-state layout pinned offline (needs a running session to live-smoke). |
| L2 | **EquipmentTab cursor-kill** -- **MOSTLY DONE 2026-07-21** | Prep: 2220-line `EquipmentTab.cs` split into 6 concern partials (core / ProfilePanel / DeviceList / Telemetry / DeviceSettings / FilterTable). Converted (all live-verified with connected fakes): mount-status + camera-cooler readout rows, cooler-controls row, device-settings rows (cycle / stepper / string editor), filter-table header + rows -> HStack/VStack trees (net ~-136 across commits). **`RenderProfilePanel` section walk DONE 2026-07-21:** every `PanelSection` returns a `Layout.Node?` and the panel is ONE `VStack(sections).Pad` + a single `RenderLayout` with a `_profilePanelFills` dispatch table (site/guide-FL/OTA/cooler-setpoint/device-string text inputs, cooler sparkline, slot reachability dot + row separator, section hairlines); `SlotRow(indicatorFillKey)` + `LabeledInputRow(fillKey)` added so multiple Fills route through one dispatcher; overflow clipped (replaces the "Add OTA if it fits" check); net -75, inspector-verified. **Remaining (intentionally deferred):** the device-list ROW body (badge/name/status columns interleaved with reachability/confirm-strip/segment business logic + inset badge pill + status-dot square -- poor reduction-per-risk, stays). |
| L3 | **LiveSessionTab side panels + strips** -- **PARTIAL 2026-07-21** | Converted: running-session mount block; **preview per-OTA panels DONE** -- `RenderPreviewOTAPanels` is ONE `Dock(HStack(columns), Bottom(mount))` (each column a padded `VStack` of name/temp/focuser-jog/goto/filter/capture-controls; capture controls their own `VStack`), killing the `px = i*panelW` column cursor + per-column `y += rowH` + the `maxY` mount reservation; `_previewFills` dispatch for the goto text-input + capture progress bar + mount hairline; net -12, inspector-verified. Polar + Flats setup panels already ONE tree. Remaining: control strips (`Strips.cs`). Charts + preview image stay Fill leaves; image-aligned overlays (reticle, ROI) stay pixel. |
| L4 | **Viewer chrome** -- **MINIMAL, mostly intentionally-pixel 2026-07-21** | Finding: the viewer chrome is dominated by **interactive controls** (Toolbar with per-button hover hit-testing + `_toolbarButtonBounds` dropdown anchors + a separate `HitTestToolbar`; WB / wavelet / transport-scrub **drag sliders** via `DrawTrackSlider`) and **raster** (histogram) -- all the intentionally-pixel categories, NOT static chrome. StatusBar is already a single joined-text `RenderTextBar` (no positioning math). FileList already on `ListScrollController`. So L4 has almost nothing to convert -- the plan overestimated it. `TrackSlider` promotion is U1 of [controls-upstreaming.md](controls-upstreaming.md). |
| L5 | **SkyMap F3 modal, Notifications, Planner rows** -- **PARTIAL 2026-07-21** | Notifications rows -> tree (done, live-verified). Remaining: SkyMap F3 search modal (has an interactive text input; the results-list rows are convertible) + Planner rows/details (chart + list already largely tree-driven from P3). Pairs with U2 (`SearchInteraction` base) but doesn't depend on it. |

## Perf note

Trees are per-frame allocated records; Planner (frame), Session (whole form), and Equipment
(micro-trees) already rebuild per frame with no measured problem. Per-visible-row templates add
~hundreds of small allocs/frame for a full list -- measure at the L1/L3 checkpoints
(`frame_stats`, `TianWen.UI.Benchmarks` if suspicious) before optimizing. Contingency (only if
measured): a pooled/arena `Layout.Builder` in DIR.Lib -- a 6.16+ item, do not pre-build it.

## The endpoint: ONE tree per panel, not per-row rects (user, 2026-07-21)

A per-row `RenderLayout(rowTree, new RectF32(x0, y, w, rowH))` with a `y += rowH` cursor between
rows is **pixel calc in disguise** -- it kills the *within-row* math but keeps the *between-row*
placement as hand-computed pixels. It's the halfway house, not the goal. The endpoint is **one
`RenderLayout(panelTree, rect)` per panel**, where `rect` is the ONLY constructed RectF32 (the
arrangement boundary the host hands the tab): rows stack via `VStack`, bottom-pinned buttons via
`Dock.Bottom`, raster regions via keyed `Fill` leaves. No internal cursor, no per-row rect.
Canonical example: `LiveSessionTab.Flats.cs` `RenderFlatsSidePanel` -- a `Dock(contentVStack,
Bottom(buttonsVStack))` rooted at the panel rect; Start/Cancel are placed by the Dock, not a
computed `buttonY`. The earlier per-row-rect conversions (mount block, telemetry rows, settings/
filter rows) are a step short and get pulled to this shape as they're revisited.

Legitimate constructed rects: (1) the panel's own rect from the host chrome arrangement; (2) a
keyed `Fill` leaf's arranged rect handed back to a raster painter via `drawFill`. Anything else --
a rect built from `x0 + something`, `y`, a `rowH` cursor -- is the smell.

## What stays pixel by design (the refined taxonomy)

The L1-L4 conversions surfaced a sharper rule than "raster stays pixel": layout conversion applies to
**static chrome** (labels, rows, panels, buttons, status text, steppers). Two categories legitimately
stay pixel, and trying to force them into the DSL is a mistake:

1. **Raster content** -- charts (altitude, guide graph, star profile, target scatter, V-curve),
   histogram, sky-map star field, cooling sparklines, the image itself (`ConfineToViewport`), and
   on-image overlays (guide reticle, planetary ROI). Keyed `Fill` leaves are their correct form; the
   painter draws inside the arranged rect.
2. **Interactive controls with per-element rect coupling** -- a `.Bg` is set at tree-BUILD time, before
   arrange, so anything whose appearance/behaviour depends on its own arranged rect resists the tree:
   the viewer **Toolbar** (per-button hover highlight from mouseX/Y vs the button rect + dropdown
   anchoring off `_toolbarButtonBounds`), the **drag sliders** (`DrawTrackSlider` captures a hit-band
   rect the drag math reads back), and the **transport scrub**. These are `TrackSlider`/toolbar
   controls, the same "control internals" bucket as `ListScrollController.DrawScrollBar`.

**Update (2026-07-22):** hairline separators and fractional progress-bar gauges are **no longer
pixel** -- both are now declarative nodes. A separator is a coloured `Box`/`Spacer().Bg(...)` leaf
(the engine paints any node's `Background`); a progress bar is `FormRowLayout.ProgressBar(fraction,
track, fill, label?)` -- a track `Box` overlaid with a fractional-width fill (two `Star`-weighted
spacers) plus an optional centred label, composed from `Box`/`Overlay`/`HStack` with no `Fill`
painter. Both render on every surface and are `describe_layout`-visible. `ProgressBar` is a
promotion candidate for `DIR.Lib.Layout.Builder` (recorded in `controls-upstreaming.md`).

## Non-goals

- The two categories above (raster + interactive controls) -- forcing them into the DSL is the mistake.
- The TUI (`Console.Lib` widgets are a different, already-declarative model).
- A DIR.Lib list *widget* (heterogeneous rows decision stands; row templates stay consumer-owned).
- A blind atomic rewrite of a whole tab's panel where the sections are heterogeneous *interactive*
  content is fine WHEN each section returns its own `Layout.Node` and a single `RenderLayout` stacks
  them (done for Equipment `RenderProfilePanel` + LiveSession preview) -- what to avoid is a monolithic
  hand-built tree that re-inlines the section bodies instead of keeping them as node-returning builders.

## Status (2026-07-21)

The convertible chrome is converted; ~16 commits, full unit suite green, full solution 0-warning,
live-verified across Equipment (connected fakes), SkyMap (info panel + search modal), Notifications,
LiveSession (preview + Polar/Flats setup panels, connected fakes), Guider. **Done / mostly-done:**
L1 GuiderTab (full); L2 Equipment (telemetry, cooler controls, device settings, filter rows -- net
~-136); L3 LiveSession (running + preview mount blocks, preview jog row, **Polar setup panel as ONE
tree** net -94, **Flats setup panel as ONE tree** net -30, phase pills / source toggle / Cancel-Done);
L5 Notifications rows + SkyMap object info panel. The Polar + Flats setup panels are the reference
implementation of the endpoint pattern (one `Dock(contentVStack, Bottom(buttons))` per panel, no
internal cursor). **Intentionally NOT converted** (the two stay-pixel categories above + genuinely-
minimal cases): the viewer chrome (L4 -- interactive toolbar/sliders/transport + histogram); all
charts/timelines/guide-graphs/sparklines/error-gauges; the device-list row (business-logic-tangled);
lone single-`DrawText`-into-a-rect cases + hairline separators (now keyed 1px Fill leaves inside the
one-tree panels) + modal-card overlays + fractional progress gauges (keyed Fill leaves). **Both
remaining cursor-stitch orchestrators are now DONE (2026-07-21):** the Equipment `RenderProfilePanel`
section walk -- every `PanelSection` returns a `Layout.Node?` (or null when hidden) and the panel is one
`VStack(sections).Pad` rendered by a single `RenderLayout` with a `_profilePanelFills` dispatch table
for the text-input / sparkline / slot-dot / hairline Fill leaves (net -75); and the LiveSession preview
per-OTA panel walk -- `RenderPreviewOTAPanels` is one `Dock(HStack(columns), Bottom(mount))` (each column
a padded `VStack`, capture controls their own `VStack`), killing the `px = i*panelW` column cursor + the
per-column `y += rowH` + the `maxY` mount reservation, with a `_previewFills` dispatch table (net -12).
Both inspector-verified with connected fakes (all sub-panels expand + reflow correctly; the exposure/gain
steppers stretch, Capture right-anchored). **Load-bearing gotcha:** a returned multi-row section `VStack`
must be `.WStar()` -- an Auto-width VStack collapses to its intrinsic width (starved the exposure stepper
to w=0), caught via the live `describe_layout` tree.

## Remaining direct-draw inventory (audit 2026-07-21)

Per-file grep of `FillRect` / `DrawText` / line-circle / `RenderButton` across the tab + viewer files,
sorted into the three buckets. Buckets 1-2 are **by design** (see the taxonomy above); bucket 3 is the
actual remaining convertible work.

**Bucket 1 -- raster inside `Fill`-leaf painters / scene render (STAYS, correct):** these draw into a
rect the layout tree already arranged, they are not chrome.
- `GuiderTab.cs` (33 FillRect / 14 DrawText / 9 line-circle) -- the 4 panes (camera / profile plot /
  target scatter / graph) painted in the `drawFill` callback.
- `LiveSessionTab.Charts.cs` (20 FillRect) -- guide graph.
- `EquipmentTab.Telemetry.cs` line-circle -- cooler sparkline (in its Fill painter).
- `ImageRendererBase.Histogram.cs`, `.Overlays.cs` (grid / star / WCS on the image), `SkyMapTab.cs` +
  `SkyMapTab.ObjectOverlay.cs` (star/line/marker render) -- the image + sky-map scene.

**Bucket 2 -- interactive controls that intentionally stay pixel (per-element rect coupling):** `.Bg`
is set at tree-build time (pre-arrange), so a control whose look/behaviour needs its OWN arranged rect
resists the DSL. Pairs with `docs/plans/controls-upstreaming.md` U1 (TrackSlider) / U2 (Search).
- `ImageRendererBase.Toolbar.cs` (6 FillRect) -- viewer toolbar buttons (hover from mouseXY-vs-rect +
  `_toolbarButtonBounds` dropdown anchors + separate `HitTestToolbar`).
- `ImageRendererBase.TrackSlider.cs` (3) -- WB / wavelet / transport drag sliders (capture a hit-band
  the drag reads back).
- `ImageRendererBase.Transport.cs` (3) -- SER scrub. `ImageRendererBase.FileList.cs` -- file-list rows
  (on `ListScrollController`, drawn into row rects).
- `RenderButton(...)` is itself a direct-draw helper (FillRect + DrawText + register-click), still used
  for ~10 individual buttons (device-list Connect/Disconnect, strips ABORT, a few in Planner / SkyMap /
  Flats). Converting these to `Text.Bg.Clickable` nodes is cosmetic-only; low priority.

**Bucket 3 -- convertible chrome. The whole LiveSession family + SessionTab are now DONE (2026-07-22);
what remains is SkyMap/Planner/Equipment chrome.**
- **DONE** `LiveSessionTab.Panels.cs` -- the RUNNING-session `RenderOTAPanels` is ONE
  `Dock(HStack(per-OTA columns), Bottom(mount))` (mirrors the preview path): each column a padded VStack
  (name, temp + cooling sparkline [keyed Fill raster], focuser/filter, exposure state [label +
  `ProgressBar`], V-curve [keyed Fill raster, `.Stretch()`]); mount docked full-width; dividers are `Box`
  nodes. `_previewFills` renamed `_otaPanelFills` (shared preview+running). Inspector-verified.
- **DONE** `LiveSessionTab.Strips.cs` -- top strip (phase pill + activity + progress = one HStack) and
  bottom strip (`VStack(Box hairline, HStack[guide-graph Fill * | RMS | ABORT node])`); guide graph stays
  a keyed Fill. Inspector-verified.
- **DONE** `LiveSessionTab.Polar.cs` -- RUNNING polar readouts are `Dock(content VStack, Bottom(Cancel/
  Done))`; the Az/Alt error needles + LEDs stay a raster `polarGauges` Fill. Setup panel was already one tree.
- **DONE** `SessionTab.cs` -- Camera Settings + Observations panels + Start button are trees (the config
  panel was already the gold standard). The per-row exposure value cell stays a keyed Fill (double-click
  edit region preserved); dropped the `MeasureValueColumnWidth` pre-pass (Star cells self-align).
  Inspector-verified.
- **DONE** `SkyMapTab.Search.cs` -- F3 modal (chrome + results rows) was already one tree; the
  selection **info panel** is now fully declarative too: its six text rows are a VStack, the close
  button + action buttons are Clickable nodes. The four `RenderButton` action buttons (Goto / View in
  Planner / Pin, or the mount branch's Solve & Sync) became ONE right-aligned HStack of Clickable Text
  nodes (leading Star spacer + trailing fixed margin), killing the right-to-left `x -= btnW + gap`
  cursor. What stays raster: the floating panel's own bg+border FillRects (minimal), the on-map
  crosshair / shape marker, the sky-path polyline + event labels, and the comet vmag sparkline (all
  primitive-based overlays). Screenshot-verified (mount + DSO panels, commit -> centre -> panel flow).
- `PlannerTab.cs` (8 / 4 / 1) -- planner chart (raster) + list rows. **REMAINING.**
- `EquipmentTab.DeviceList.cs` (4 / 6 / 2) -- device-list ROW body + Connect/Disconnect. **Deferred by
  design** (badge/name/status columns tangled with reachability/confirm-strip/segment business logic +
  inset badge pill + status-dot square -- poor reduction-per-risk).
- **DONE** `EquipmentTab.cs` -- tab-level chrome. `RenderProfileView` (the always-visible split) is now
  ONE `Dock(HStack[profilePanel Fill | 1px Box separator | deviceList Fill], Bottom(bottomBar Fill))` --
  each region a keyed Fill whose arranged rect drives the existing sub-renderer, backgrounds on the nodes,
  no `PixelLayout` docking cursor. `RenderProfileCreation` (form: header + name input keyed Fill + Create
  Clickable + docked hint bar) and `RenderNoProfile` (centred message + Create via star spacers) also
  converted. **Nested `RenderLayout` inside a `drawFill` verified re-entrancy-safe** (PaintLayout iterates
  a local immutable array; only appends to the region tracker + `_capturedLayout`) -- the profile panel's
  own tree paints correctly inside the outer view tree. Screenshot-verified. The device-list ROW body
  stays deferred (below).

**New reusable control:** `FormRowLayout.ProgressBar(fraction, track, fill, label?)` -- declarative
fractional bar (Box + Overlay + Star-weighted fill + optional centred label), pinned by
`ProgressBarLayoutTests` (7). Replaced the hand-drawn two-`FillRect` gauges in the preview capture bar +
the running exposure bar. Bucket 2's remaining genuinely-pixel controls (viewer Toolbar hover, the
`TrackSlider` drag family, the SER Transport scrub) are the DIR.Lib-upstreaming items (U1) in
`controls-upstreaming.md`, not tianwen chrome; the standalone `RenderButton` sites converted opportunistically
(ABORT, Start Session) as their panels were rewritten.

## Definition of done

Direct `Renderer` draw calls in widget code appear only inside Fill-leaf painters, interactive-control
internals (toolbar/sliders/scrollbar), and the minimal cases above; all cursor-stitched / hand-computed
`x/y/w/h` chrome flows through `RenderLayout`/`PaintLayout`, which also buys: draw==hit everywhere,
`describe_layout` inspector coverage (unattended tests can see every label), DPI applied once by the
engine, and web-port reuse of the same trees. **Reached** for the convertible chrome.
