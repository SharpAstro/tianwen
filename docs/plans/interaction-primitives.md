# Interaction primitives: ListScrollController + TapOrDragGesture + PanZoomController (DIR.Lib 6.15)

**Status: P1 SHIPPED + P3 CORE DONE + P4 DONE (local working copies, 2026-07-21). Design approved
2026-07-20. P2 (NuGet lockstep release) + P5 pending. P3 remainder: web E2E touch-scroll test + the
four-lib repin (repin batches with P2).**

Trigger: "planner target list doesn't scroll with touch or trackpad" + the observation that the
scroll wiring is verbose, duplicated per list instance, and "unworthy of a GUI library -- a list
widget should just have proper scrolling by virtue of being a list widget". A full audit confirmed
the problem is systemic, not local (inventory below). The interim trackpad fix shipped as tianwen
`b3437e02` (fractional wheel accumulator in `ScrollBar.HandleWheel`) and is absorbed/deleted by
this work.

## Audit inventory (2026-07-20, commit `b3437e02`)

### Scroll implementations: 7 hand-rolled sites, 4 conventions

| # | Widget | Where | Convention | Scrollbar | Notes |
|---|--------|-------|-----------|-----------|-------|
| 1 | Planner target list | `PlannerTab.cs:94-97,590-628` | row-indexed via `ScrollBar` | interactive | `EnsureVisible`; per-row + pin-button clickables |
| 2 | Equipment device list | `EquipmentTab.cs:82-140,880-897` | row-indexed via `ScrollBar`, offset on `EquipmentTabState.DeviceScrollOffset` | interactive | bespoke `ScrollActiveSlotDeviceIntoView`; rows carry segmented buttons + confirm strips |
| 3 | Notifications list | `NotificationsTab.cs:26-167` | pixel (`BaseRowHeight=32`) | decorative FillRect thumb, non-interactive | upper clamp only lands in `Render`, not the handler |
| 4 | Session config panel | `SessionTab.cs:76-149,683-782` | pixel (`ScrollLineHeight`), offset on `SessionTabState.ConfigScrollOffset` | none (PushClip only) | see unit-mismatch below |
| 5 | LiveSession exposure log | `LiveSessionTab.Panels.cs:438-479`, `.Input.cs:117-120` | row-indexed, **bottom-anchored** (offset grows scrolling UP into history) | none | +-1 step wheel, no upper clamp |
| 6 | FITS viewer file list | `ImageRendererBase.FileList.cs:21-92`, `ViewerActions.cs:275-279` | row-indexed, `-(int)scrollY*3` | decorative, non-interactive | **still carries the trackpad truncation bug** (interim fix didn't cover it); bound is `Count-1` not `Count-visible` |
| 7 | `HitTestFileList` | `ImageRendererBase.FileList.cs:97-122` | -- | -- | **dead code, zero callers** -- delete |
| -- | SkyMap F3 results; DIR.Lib dropdown | `SkyMapTab.Search.cs:204-254`; `PixelWidgetBase.cs:188-284` | deliberately clip at visibleRows, no scroll | -- | legit degenerate mode the new API must support zero-config |

### Drag state machines: 8 (3 shareable kernels)

1. `ScrollBarDragState` thumb drag (`ScrollBar.cs:29-141`) -- absolute-position grip math, shared Planner+Equipment.
2. `PlannerSliderInteraction` handoff sliders -- click-to-place + drag.
3. SkyMap pan-drag + click-vs-drag (`SkyMapTab.cs:868-1042`, `SkyMapTab.Search.cs:781-801`) -- the codebase's **only** tap-vs-drag slop (`ClickDragThresholdPx = 4f`, squared-distance + `_mouseDownOnMap` gate).
4. `ImageRendererBase.TrackSlider` -- already unified (WB/wavelet/scrub); **stays as-is** (continuous-value family, not row scroll).
5. `ImageRendererBase` viewport pan + cursor-anchored zoom (`Input.cs:315-515`, `ViewerActions.cs:307-334`).
6. FITS file-list resize divider -- hit region from DIR.Lib `Layout.Split(dividerHit:)`, width math hand-rolled.
7. `LiveSessionTab` preview pan/zoom (`LiveSessionTab.Input.cs:95-141`) -- **byte-for-byte duplicate of #5's zoom formula** against the same `ViewerState` type. Largest exact duplication found.
8. `VkPlanetaryTab` PiP ROI drag (`VkPlanetaryTab.cs:95-100,607-629`).

### Cross-host unit mismatch (latent landmine)

`SessionTabState.ConfigScrollOffset` is doc'd "pixels" and written as pixels by the GUI
(`SessionTab.HandleConfigScroll`) but written/read as a **row index** by the TUI
(`TuiSessionTab.cs:139,164,166` via `Console.Lib.ScrollableList.ScrollOffset`). Not a live bug only
because the hosts never share an instance. The atom model (below) fixes this by construction.

### External readers a refactor must preserve

- `TuiSessionTab` reads/writes `ConfigScrollOffset` (rows) -- migrate the field to canonical atoms.
- `ViewerActionsTests.ScrollFileList_ClampsToValidRange` pins `ViewerActions.ScrollFileList` -- update deliberately in wave 2.
- Web wheel bridge (`Planner.razor:1172-1179`, `scrollY = -deltaY/100`) produces sub-1.0 deltas -- the accumulator behaviour is a hard requirement.
- `PlannerTab.ScrollOffset` / `State.DeviceScrollOffset`: no external readers; free to internalise.

## Design

### The "atom" unit (agreed 2026-07-20)

The controller's unit is the **atom: the smallest logical scrollable unit** of the surface (list
row, config line, log entry; TUI: one cell row). Internal position is ONE `float` in **fractional
atoms** -- this single number subsumes three mechanisms that were separate in the interim design:

- **Wheel notch**: `Offset += WheelStep` atoms.
- **Trackpad / web bridge**: `Offset += scrollY * WheelStep` -- the fractional part IS the carry;
  the forget-the-`ref float`-accumulator regression (FileList today) becomes unrepresentable.
- **Touch/mouse drag**: `Offset += deltaPx / atomExtentPx` -- pixel-smooth by construction.

Atoms are host- and DPI-independent (`AtomOffset` survives a monitor move; the TUI's atom is one
cell row, the GUI's is `rowHeight*dpiScale` px, both agree on the count), which is what dissolves
the `ConfigScrollOffset` pixels-vs-rows mismatch. Row-snapped lists set `SnapToAtom = true`
(round on gesture end); smooth surfaces don't.

Constraints baked in (validated against the inventory): **uniform atom extent** (every scrollable
list found has a fixed row height; variable-extent surfaces like TabBar's tabs are out of scope);
name is "atom", deliberately not "quantum" (avoid confusion with photometry/quantum-efficiency
vocabulary in this codebase).

### API sketch (DIR.Lib, freeze against the inventory before coding)

```csharp
public sealed class ListScrollController
{
    // per-frame, idempotent geometry
    void SetExtent(RectF32 viewport, float atomExtentPx, int totalAtoms, float dpiScale);

    float Offset { get; }             // fractional atoms, clamped [0, totalAtoms - visibleAtoms]
    int   FirstVisibleAtom { get; }   // floor(Offset) -> draw-loop start (anchor-aware)
    float SubAtomPx { get; }          // frac(Offset) * atomExtentPx -> smooth draw shift (0 when snapped)
    int   AtomOffset { get; set; }    // snapped int accessor -> persistence / legacy state fields
    int   VisibleAtoms { get; }
    bool  SnapToAtom { get; set; }
    ScrollAnchor Anchor { get; set; } // Top (default) | Bottom (LiveSession exposure log)

    bool HandleInput(in InputEvent evt);   // wheel + thumb drag + surface tap-or-drag; ONE forward
    void EnsureVisible(int atom, int marginAtoms = 0);  // no-op if already visible; margin variant
    int? TakeAtomTap();                    // tap-on-release -> atom index (row select)
    void DrawScrollBar(...);               // optional layer: interactive | decorative | none
    event Action? Changed;                 // -> NeedsRedraw
}

public struct TapOrDragGesture   // extracted from SkyMapTab's ClickDragThresholdPx=4f trio
{
    // press-arm -> slop (4px * dpiScale, squared-distance) -> commit-as-tap | escalate-to-drag
    // + the "did MouseDown land on me" gating flag; used by ListScrollController internally,
    // adoptable by SkyMap (click-vs-pan) and future surfaces.
}

public sealed class PanZoomController
{
    // BeginPan/UpdatePan/EndPan + cursor-anchored zoom on (PanOffset, Zoom, ZoomToFit) --
    // dedupes ImageRendererBase.Input.cs:497-511 and its byte-for-byte LiveSessionTab copy.
}
```

### Interaction decisions

- **Tap-on-release row select**: the two main lists' row-select migrates from per-row
  `RegisterClickable` to `TakeAtomTap()`. Unregistered rows -> `hit == null` -> event falls to
  `HandleInput` -> controller arms; NO host/dispatch changes needed (web `Planner.razor` and
  desktop `GuiEventHandlerBase` both fall through unclaimed presses already).
- **Sub-element buttons inside rows** (pin toggle, connect segments, confirm strips, steppers)
  stay press-dispatch registered clickables ("later registration wins" convention). Documented
  edge: drag starting ON a button won't scroll.
- **Mouse drag-to-scroll comes free with touch** -- one finger arrives as MouseDown/Move/Up on
  both platforms, indistinguishable from mouse; harmless on selection-only lists.
- One-finger touch = drag-scroll; two-finger pinch stays unhandled by lists (sky-map zoom only).

## Phases

| Phase | Repo / release | Content |
|-------|----------------|---------|
| P1 | DIR.Lib 6.15 | **✅ DONE (2026-07-21).** `TapOrDragGesture` + `ListScrollController` + `PanZoomController` + headless unit tests (accumulate/clamp/snap, slop arm->tap vs arm->drag + suppression, EnsureVisible+margin, thumb grip math -- unify with Console.Lib `ScrollableList`'s structurally-identical formula, bottom-anchor mode, clip-only degenerate mode, axis parameter). API frozen only after every inventory row maps onto it on paper. 36 new tests green; full DIR.Lib suite 507/0. See "P1 shipped" below. |
| P2 | Console.Lib 3.9, SdlVulkan.Renderer 6.28, WebGl.Renderer 1.11 | Lockstep no-code rebuilds (standing rule on a DIR.Lib minor). |
| P3 | tianwen wave 1 | **CORE DONE (2026-07-21):** Planner + Equipment lists -> controller (touch/mouse drag-to-scroll + tap-on-release select + trackpad wheel accumulator); `ScrollBar.cs`/`ScrollBarTests.cs` deleted (coverage in DIR.Lib); `EquipmentTabState.DeviceScrollOffset` removed (now in the controller); offline `RgbaImageRenderer` input-sequence tests shipped (`PlannerTabScrollTests`: drag scrolls w/o selecting, tap selects w/o scrolling, sub-unit wheel accumulates, row-body-unclaimed/pin-button-registered). Full solution builds 0-warning; DIR.Lib 507/0, tianwen unit 3386/0. **Remaining:** warm-page web E2E touch-scroll (CDP one-finger drag via `CanvasGestures`) + `getPlannerListState ?e2e=1` hook; the four-lib repin (batches with the P2 release). Migration was gated on verified host routing = HitTestAndDispatch-first then HandleInput-on-miss (desktop `GuiEventHandlerBase` + web `Planner.razor`, both mirror). **Live GUI smoke (inspector, 2026-07-21) PASSED:** Planner wheel-scroll (pinned rows scroll off the top), drag-scroll, and tap-select (selected a row -> chart + details followed); Equipment 104-device list renders + scrolls with the On|Off segments intact; render thread stayed ALIVE, no exceptions in gui-stderr. |
| P4 | tianwen wave 2 | **DONE (2026-07-21), all four lists migrated.** **Notifications** (`1af0e38b`) -- smooth (non-snap) controller, decorative scrollbar, `VisibleRows()` smooth path; fixes the wheel truncation + missing handler upper-clamp; public pixel `ScrollOffset` removed. **FITS FileList** (`674996b0`) -- the entangled one, untangled: the controller owns the fractional offset (trackpad accumulator survives frames; `MaxOffset = Count - visible` fixes the wrong `Count-1` bound), `ScanFolder` requests its initial top via a one-shot `ViewerState.PendingFileListScrollTop` (never a per-frame int mirror, which would reset the accumulator), dead `HitTestFileList` deleted; `Decorative` not `Interactive` (see the inventory-mapping note). **Session config** (`f8e38ee6`) -- `ConfigScrollOffset` becomes canonical atoms in BOTH hosts (the TUI needed zero changes: its `ScrollableList` rows already ARE atoms), dissolving the px-vs-rows unit mismatch. **LiveSession exposure log** (`7bcc481c`) -- `{Anchor=Bottom}` tail-follow: pinned to the newest entry until the user wheels up into history, re-pinned automatically when a session restart clears the log (DIR.Lib's fits-again rule, sibling `3d2f273`); the wheel is now viewport-gated by the controller (the old code scrolled the log from anywhere on the tab); both `LiveSessionState.*ScrollOffset` fields deleted (focus history clips and never scrolled). **Interactive-everywhere follow-up** (same day, user-ruled: "a list should be interactive" -- no wheel-only lists remain): FileList flipped `{Interactive}` with tap-on-release select routed in both viewer hosts (kills the mouse-down `ListItemHit`; fixes the standalone's `MouseUp(0, 0)`); Notifications flipped `{Interactive}` + body drag (tap discarded); LiveSession log gains body drag (Mode stays `None`), with the log press tried BEFORE the preview pan so a press on the log can no longer grab a zoomed preview. Live-verified via inspector: drag scrolls with zero file loads, tap loads exactly one. |
| P5 | tianwen wave 3 | `PanZoomController` adoption in `ImageRendererBase` + `LiveSessionTab` (delete the duplicated zoom formula); optionally `VkPlanetaryTab` PiP drag onto `TapOrDragGesture`. |

## P1 shipped (DIR.Lib 6.15) — frozen API + inventory mapping

Three new files in `../DIR.Lib/src/DIR.Lib/` (`TapOrDragGesture.cs`, `ListScrollController.cs`,
`PanZoomController.cs`); `VersionPrefix` 6.14.0 -> 6.15.0. Tests in `../DIR.Lib/src/DIR.Lib.Tests/`.

**Two faithful corrections to the sketch (recorded so the intent is clear):**

1. `HandleInput` is **by value** (`bool HandleInput(InputEvent evt)`), not `in InputEvent` — DIR.Lib's
   existing `IWidget`/`PixelWidgetBase` convention, and `InputEvent` is a reference-type record so `in`
   buys nothing.
2. `TapOrDragGesture`'s slop radius is passed through `Arm(x, y, mods, dpiScale, slopPx = 4f)`, **not** a
   struct property initializer. A `private TapOrDragGesture _gesture;` field is `default`-initialized,
   which bypasses initializers — a `SlopPx = 4f` initializer would silently be `0`, and a zero slop
   classifies **every** press as a drag (taps would never register). Method defaults are always honored,
   so the slop lives there. (This is the `record struct default-ctor` gotcha applied to a plain struct.)

**Frozen surface:**

- `TapOrDragGesture` (struct): `Arm` / `Update(x,y)->bool isDragging` / `Release(x,y)->GestureOutcome` /
  `Cancel`; `State`/`IsArmed`/`IsDragging`/`DownModifiers`/`DownPosition`. `Release` re-checks slop so a
  host that never pumps `Update` still classifies; `Update` latches drag so a wander-back stays a drag.
- `ListScrollController` (class): `SetExtent(RectF32, atomExtentPx, totalAtoms, dpiScale)` (silent,
  per-frame) · `Offset`/`FirstVisibleAtom`/`SubAtomPx`/`AtomOffset`/`VisibleAtoms`/`MaxOffset`/`TotalAtoms`
  · `Anchor`(Top|Bottom) · `Mode`(None|Decorative|Interactive) · `Axis`(Vertical|Horizontal) · `SnapToAtom`
  · `WheelStepAtoms` · `HandleInput` · `EnsureVisible(atom, margin)` · `TakeAtomTap` · `DrawScrollBar(fillRect)`
  · `ContentCrossExtentPx` · `event Changed` · **`ContentArea`** + **`VisibleRows()`** (added in P3: the
  scrollbar-reserved content rect + a zero-alloc `foreach (atom, rect)` struct enumerator, so consumers own
  NO row-placement / content-width / overflow math -- just the per-row content build).
- `PanZoomController` (class): `PanOffset`/`Zoom`/`ZoomToFit` · `MinZoom`/`MaxZoom`/`ZoomStep` ·
  `BeginPan`/`UpdatePan`/`EndPan`/`IsPanning` · `ZoomAtCursor(delta,cx,cy,viewport)` /
  `ZoomByFactor(factor,cx,cy,viewport)` · `Reset`/`FitToView` · `event Changed`.

**Every inventory row maps** (the freeze gate): #1 Planner + #2 Equipment -> `{SnapToAtom, Interactive, Top}`
+ `TakeAtomTap` (sub-buttons stay registered clickables); #3 Notifications -> `{Interactive}`, tap discarded
(no row action); #4 Session config -> `{None}`, field becomes canonical atoms via `AtomOffset` (dissolves
the TUI/GUI px-vs-rows mismatch); #5 LiveSession log -> `{Anchor=Bottom, None}` (tail-follow; body drag +
wheel viewport-gated); #6 FITS FileList -> `{SnapToAtom, Interactive}` + tap-on-release select via
`TakeAtomTap` (fractional `Offset` fixes trackpad truncation, `MaxOffset = total-visible` fixes the wrong
bound). The first FileList migration shipped `{Decorative}` keeping the historical mouse-down `ListItemHit`
select, because the viewer's bespoke self-dispatch input model has no unclaimed-press fall-through and a
bare mode flip silently broke select; the interactive-everywhere pass (below) then routed
press/move/release to the controller in BOTH viewer hosts -- the embedded `HandleViewerMouse*` path and the
standalone's `Program.cs` via the public `HandleFileListInput` -- and fixed the standalone's synthesized
`MouseUp(0, 0)` (release coords now come from the cached mouse position, the trick the GUI host had
documented but the FitsViewer never replicated); #7 dead `HitTestFileList` -> delete; degenerate
F3/dropdown -> `total <= visible` clips zero-config.
Mode gates ONLY the thumb: body drag-to-scroll, wheel, and tap detection work in any mode PROVIDED the
host routes MouseDown/Move/Up to `HandleInput` -- a "Decorative" list that only routes the wheel is
wheel-only in practice, which is what the interactive-everywhere pass eliminated.
Drag machines: `ScrollBarDragState`->controller thumb; SkyMap tap-vs-drag->`TapOrDragGesture`; viewer +
LiveSession pan/zoom->`PanZoomController` (dedupes the byte-for-byte copy). Slider / split-divider / PiP
stay out of scope.

## Host default input wiring (user suggestion, SHIPPED 2026-07-21)

SdlVulkan.Renderer 6.28 (`72ae0d2`, rides the pending release chain): the loop synthesizes DIR.Lib
`InputEvent`s for mouse down/move/up/wheel and delivers them through ONE optional
`SdlWindowView.OnPointerInput` callback, so every consumer `Program.cs` stops hand-wiring the four
mouse lambdas. Motivating bug class: the FitsViewer shipped `MouseUp(0, 0)` for months because the
GUI's cached-position trick was never replicated -- and it turns out SDL's button-up event carries
real X/Y all along (only the legacy `Action<byte>` callback signature dropped them), so the
synthesized `MouseUp` is strictly better than any cached position.
`SdlWindowView.DispatchPointer{Down,Move,Up,Wheel}` is the single fan-out to legacy callbacks +
`OnPointerInput`, used by BOTH the SDL pump and the DebugInspector's synthesized input
(click/drag/scroll/press_hold) -- without that, apps on `OnPointerInput` would be invisible to the
inspector (its click/drag invoked the raw callbacks directly), silently breaking the unattended
GUI-testing workflow. Inspector releases now carry real coordinates too (drag ends at x2/y2, hold at
the press point), so tap-vs-drag classification works for synthesized gestures. Both tianwen hosts
(GUI + FitsViewer) rewired to one `OnPointerInput` switch; keyboard/text stay on
`OnKeyDown`/`OnTextInput` (app-level chords, no synthesis pitfalls). Re-verified live through the
new path: inspector drag = scroll with zero file loads, tap = exactly one load.

## Out of scope / deferred

- `TrackSlider` (already unified; continuous-value family, not row scroll).
- Momentum/inertia scrolling (needs animation ticks; pairs with the redraw-abstraction backlog item in `docs/todo/`).
- F3-results / dropdown scroll adoption (clip-only today by design; degenerate mode keeps the door open).
- TabBar horizontal overflow (variable-width tabs violate uniform atom extent; the axis parameter leaves room for a future variant).
- `PlannerSliderInteraction` re-basing onto `TapOrDragGesture` (click-to-place semantics differ; opportunistic later).

## Verification summary

DIR.Lib headless controller tests; tianwen offline render + input-sequence tests over
`RgbaImageRenderer`; new warm-page E2E one-finger-drag test pinning the original bug report
end-to-end; full suite + CI-parity restore before each push, per the release-lib chain rules.
