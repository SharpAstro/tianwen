# DPI scale: one owner, stop the parameter threading

**Status: DONE (2026-07-22, same day as planned).** D1 + D2 + D3 all landed. Follow-on to
[layout-driven-ui.md](layout-driven-ui.md) (the engine applies DPI once) and
[controls-upstreaming.md](controls-upstreaming.md) (U1 exposed the seam: `DrawTrackSlider`
promoted to DIR.Lib had to take `dpiScale` as a parameter because the DIR.Lib base had no
`DpiScale` while tianwen's `ImageRendererBase` did).

**Outcome:** `PixelWidgetBase.DpiScale` (virtual, default 1) is the single owner; the GUI's
`VkGuiRenderer` overrides the setter to propagate to its ten child widgets at startup/resize; the
web sets it per frame from `devicePixelRatio`; the TUI stays at the default 1. All 74 widget-method
`float dpiScale` parameters are gone (only the 3 by-design non-widget helpers keep one:
`GuideGraphRenderer.ComputeWindow`, `OverlayEngine` x2); `SkyMapTab._lastDpiScale` is retired
(input reads the property). Methods that still need the value locally open with
`var dpiScale = DpiScale;` so the ~170 px multiplications were untouched. The `dpiScale: 1f`
device-px escape hatch survives (explicit arg overrides the property; pinned by a DIR.Lib test).
Verified: full solution + web build 0/0; 122 layout/tab/skymap tests green (incl. the dpiScale=2
pixel renders, now property-driven); live GUI smoke at the real DisplayScale (Planner + Equipment
render identically to pre-sweep screenshots).

**Follow-on F1 (`fontPath`/`emojiFontPath`): DONE (2026-07-22).** Same shape as DPI -- a per-window
value (host resolves the font once) threaded through every widget signature, plus the nullable
`emojiFontPath` twin. Landed as the direct `DpiScale` mirror the user chose (two plain properties, not
a bundling record):

- **F1a (DIR.Lib):** `PixelWidgetBase.FontPath { get; set; } = string.Empty` (non-null; empty = the
  text helpers no-op, no throw) + `EmojiFontPath { get; set; }` (nullable), both `virtual`. The layout
  helpers (`RenderLayout`/`ArrangeLayout`/`PaintLayout`) took `string fontPath` -> `string? fontPath =
  null` resolving `?? FontPath`. Pinned by two `LayoutPainterTests` (omitted arg uses the property; an
  explicit `fontPath` overrides it -- the emoji-run escape hatch).
- **F1b (wiring):** deleted `ImageRendererBase._fontPath` + its `FontPath` property (now inherited;
  rewrote ~12 `_fontPath is null` guards to `string.IsNullOrEmpty(FontPath)` and dropped a
  null-forgiving `_fontPath!`); `VkGuiRenderer` overrides `FontPath`/`EmojiFontPath` to propagate to
  its 7 plain child tabs (the 2 embedded `VkImageRenderer` viewers + the `VkPlanetaryTab` self-resolve
  their font, deliberately not pushed) and reads its own sidebar/status chrome from the properties; web
  `Planner.razor` sets them per frame.
- **F1c (sweep):** removed the `string fontPath` + `string? emojiFontPath` parameters from every widget
  `Render`/helper across Equipment, LiveSession, SkyMap, Guider, Session, Planner, Notifications,
  `IPlanetaryViewWidget`, `ImageRendererBase.Layout`, `VkPlannerTab`, `VkPlanetaryTab`, `VkSkyMapTab`,
  `WebSkyMapTab` + tests. Methods that draw open with `var fontPath = FontPath;` (mirroring
  `var dpiScale = DpiScale;`); pure pass-throughs just drop the param. **fontSize stays a per-call
  parameter** -- it is a derived, per-region value (base * dpi, headers * 1.3, emoji size ...), never a
  per-window constant, so bundling it (the record idea) would have re-coupled a varying value to the two
  constants and reintroduced the threading. The two static NON-widget renderers (`AltitudeChartRenderer`,
  `SkyMapRenderer`) keep their font params and are fed `FontPath`/`EmojiFontPath`.

Verified: full solution + web 0/0; 95 layout/tab/skymap tests + 7 DIR.Lib LayoutPainter tests; GUI smoke
at DisplayScale != 1 (Planner labels + weather-emoji glyphs, SkyMap constellation/grid/status labels,
sidebar tooltip all render). `emojiFontPath` turned out to be vestigial dead pass-through in the Equipment
family (never reached a `DrawText`) -- removed outright, no consumer lost. Rides the same unreleased
DIR.Lib 6.16 as DPI/U1/U5; "no push before NuGet" still applies.

## Sweep: where DPI lives today

### Origins (one per host, all "the window's scale")

| Host | Source | Delivery |
|---|---|---|
| GUI (`TianWen.UI.Gui/Program.cs:112,213`) | `sdlWindow.DisplayScale` | `VkGuiRenderer.DpiScale` property, set at startup + on every `OnResize` |
| FitsViewer (`TianWen.UI.FitsViewer/Program.cs:164,185`) | `sdlWindow.DisplayScale` | `ImageRendererBase.DpiScale` property, same pattern |
| Web (`Pages/Planner.razor:918,922`) | `window.devicePixelRatio` clamped in `webgl-canvas.js`, surfaced as `_metrics.DevicePixelRatio` | passed per `Render(...)` call as an argument |
| TUI (Console) | none -- cell grid | implicitly 1 |

### Storage / threading conventions (FIVE coexist)

1. **Property on the widget** -- `ImageRendererBase.DpiScale { get; set; }` (+ ~16 derived px
   properties `FontSize => BaseFontSize * DpiScale` etc.), `VkGuiRenderer.DpiScale`,
   `VkPlanetaryTab.DpiScale` (assigned at Render entry: `DpiScale = dpiScale;` -- the
   transitional pattern this plan generalises).
2. **Parameter threading** -- every tab `Render(..., float dpiScale, ...)` and every internal
   helper re-forwards it: **77 `float dpiScale` parameters across 23 files** in
   `TianWen.UI.Abstractions`. No call site ever passes anything other than the host value
   (except the 1f trick below).
3. **Context-record field** -- `Overlays/ViewportLayout.DpiScale` (a data snapshot the overlay
   engine reads; reasonable, stays).
4. **Per-call parameter in DIR.Lib** -- `RenderLayout`/`ArrangeLayout`/`PaintLayout`
   (`dpiScale = 1f`), `PixelMeasureContext` ctor, `DrawTrackSlider` (new, U1),
   `ListScrollController.SetExtent`, `TapOrDragGesture.Arm`.
5. **Render-time cache for input** -- `SkyMapTab._lastDpiScale` (`SkyMapTab.cs:58,127`): input
   events carry no DPI, so the tap-vs-drag slop caches the last render's value. Proof the
   parameter convention fails at input time.

### Consumption

- **174 `* dpiScale` / `* DpiScale` multiplications across 24 files** in UI.Abstractions --
  hand pixel math. Declining as chrome moves to arranged trees (the engine scales once via
  `PixelMeasureContext.ToSurface`); the legitimate residue is raster painters scaling stroke
  widths / marker radii (GuiderTab 26, SkyMapTab.Search 25 ...).
- **4 `dpiScale: 1f` call sites** (PlannerTab:494, Strips:384, Preview:135, Flats:190) -- the
  "device-px trick": a sub-render already holding device-pixel sizes feeds a tree at scale 1.
  Works, but it puts TWO unit conventions (design units vs device px) in the same file,
  distinguishable only by reading the call.

## Problems

- The U1 promotion demonstrated the seam: every generic control DIR.Lib gains needs its own
  `dpiScale` parameter because the base class has no owner for it.
- 77 parameters of pure plumbing that always carry the same per-window value.
- Input handlers can't see DPI at all (hence `_lastDpiScale`).
- Mixed unit conventions at the `dpiScale: 1f` sites.

## Decision: `DpiScale` becomes a `PixelWidgetBase` property (DIR.Lib)

A widget instance belongs to exactly one window/renderer, so a **per-widget property** is the
correct owner (a static would break multi-window; a parameter is per-call noise for a per-window
value). The host sets it at startup + resize -- exactly what GUI/FitsViewer already do for their
two existing properties.

### D1 -- DIR.Lib (rides 6.16 with U1/U5)

- `PixelWidgetBase<TSurface>.DpiScale { get; set; } = 1f`.
- `RenderLayout` / `ArrangeLayout` / `PaintLayout` / `DrawTrackSlider` change their
  `float dpiScale = 1f` parameter to `float? dpiScale = null`, resolved as `?? DpiScale`.
  Source-compatible: existing callers passing a float still compile; callers omitting it get the
  property (default 1f -- identical for consumers that never set it, e.g. chess/PTV).
- `ListScrollController` / `TapOrDragGesture` are plain controllers, not widgets -- they keep
  their explicit dpi (the owning widget passes its property, as today).

### D2 -- tianwen wiring

- Delete `ImageRendererBase.DpiScale` (now inherited). Hosts keep setting it (no change).
- `VkGuiRenderer` pushes its `DpiScale` to every tab widget at startup + `Resize` (tabs are
  long-lived fields), instead of threading through `Render` params.
- Transitional: each tab assigns `DpiScale = dpiScale;` at `Render` entry (the `VkPlanetaryTab`
  pattern) so internals can switch to the property before the signature changes.
- Web: `Planner.razor` sets `tab.DpiScale = (float)_metrics.DevicePixelRatio` when metrics
  change, drops the argument when D3 lands.

### D3 -- signature sweep (mechanical, per-file)

- Remove the 77 `float dpiScale` parameters (incl. `IPlanetaryViewWidget`) as each file's
  internals read the property; delete `SkyMapTab._lastDpiScale` (input handlers read
  `DpiScale` directly).
- The `dpiScale: 1f` trick sites either stay explicit (`dpiScale: 1f` still works) or convert
  their trees to design units -- decide per site; no forced change.

### Non-goals

- `ViewportLayout.DpiScale` stays (a value snapshot in a data record is fine).
- The TUI stays DPI-free (property defaults to 1).
- Driving the 174 raster multiplications to zero is NOT a goal -- stroke widths / marker radii
  in Fill painters legitimately scale; they just read the property instead of a param.

## Verification

- D1: DIR.Lib builds + existing layout tests; a headless test that a widget with `DpiScale = 2`
  arranges a `RowH(10)` child to 20 px with no argument passed.
- D2/D3: tianwen 0/0 build + the 46 layout tests + `PlannerTabLayoutTests` pixel renders stay
  byte-identical (they pass explicit dpi, unaffected); live GUI smoke at DisplayScale != 1
  (the win-arm64 laptop runs 1.5x) -- toolbar, planner, viewer chrome, sky-map overlay.
- Rides DIR.Lib 6.16 + tianwen repin; "no push before NuGet" applies.
