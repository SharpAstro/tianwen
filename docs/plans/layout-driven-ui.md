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
| L2 | **EquipmentTab cursor-kill + SessionTab leftovers** -- **IN PROGRESS 2026-07-21** | Prep: 2220-line `EquipmentTab.cs` split into 6 concern partials (core / ProfilePanel / DeviceList / Telemetry / DeviceSettings / FilterTable). Converted: mount-status + camera-cooler readout rows -> HStack-of-cells trees (net -37; live-verified with connected fakes). **Remaining:** the per-section cursor-stitch walk in `RenderProfilePanel` -> one arranged VStack (each section returns a `Layout.Node`, raster/text-input escape hatches as keyed Fills dispatched by a `_panelFills` table); device-list row body; device-settings + filter-table row loops. The connected-device panels need live-smoke (ConnectAllDevices signal + expand the pane) since they don't render offline. |
| L3 | **LiveSessionTab side panels + strips** | Panels/Strips/Flats/Polar; `FormRowLayout` already provides steppers/pills/labeled rows. Charts + preview stay Fill leaves; image-aligned overlays stay pixel. Second perf checkpoint. |
| L4 | **Viewer chrome content** | Toolbar buttons, InfoPanel line stack, Transport buttons, StatusBar, FileList row template. `TrackSlider` stays a control (U1 of [controls-upstreaming.md](controls-upstreaming.md) promotes it). |
| L5 | **SkyMap F3 modal + object info panel, Notifications rows, Planner rows/details** | Pairs naturally with U2 (`SearchInteraction` base) but doesn't depend on it. |

## Perf note

Trees are per-frame allocated records; Planner (frame), Session (whole form), and Equipment
(micro-trees) already rebuild per frame with no measured problem. Per-visible-row templates add
~hundreds of small allocs/frame for a full list -- measure at the L1/L3 checkpoints
(`frame_stats`, `TianWen.UI.Benchmarks` if suspicious) before optimizing. Contingency (only if
measured): a pooled/arena `Layout.Builder` in DIR.Lib -- a 6.16+ item, do not pre-build it.

## Non-goals

- Charts, histogram, sky-map star field, image placement (`ConfineToViewport`), and on-image
  overlays to the DSL -- they ARE raster content; keyed Fill leaves are their correct form.
- The TUI (`Console.Lib` widgets are a different, already-declarative model).
- A DIR.Lib list *widget* (heterogeneous rows decision stands; row templates stay consumer-owned).

## Definition of done

Direct `Renderer` draw calls in widget code appear ONLY inside Fill-leaf painters and controls.
Everything else flows through `RenderLayout`/`PaintLayout`, which also buys: draw==hit everywhere,
complete `describe_layout` inspector coverage (unattended tests can see every label), DPI applied
once by the engine, and web-port reuse of the same trees.
