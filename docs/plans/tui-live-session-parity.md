# Plan: TUI Live Session on Par with GUI

Bring `TuiLiveSessionTab` (857 lines) up to visual and functional parity with
the GPU `LiveSessionTab<TSurface>` (1732 lines, consumed by `VkLiveSessionTab`).

## Current Parity Matrix

| Feature | GPU | TUI | Gap |
|---------|-----|-----|-----|
| Top strip (phase + activity + counter) | ✅ `RenderTopStrip` | ✅ `RenderTopBar` | none |
| Per-OTA panels (temp, focus, filter, exposure) | ✅ `RenderOTAPanels` | ✅ `BuildCameraRows` | none |
| Preview OTA panels (steppers) | ✅ `RenderPreviewOTAPanels` | ✅ `BuildPreviewRows` | none |
| Exposure log | ✅ `RenderExposureLog` | ✅ `BuildExposureLogRows` | none |
| Cooling sparklines | ✅ `RenderMiniSparkline` (per-camera color) | ⚠️ `BuildSparkline` (monochrome) | colors |
| Mini image viewer | ✅ `VkMiniViewerWidget` (pan/zoom) | ⚠️ Sixel Canvas (static) | zoom/pan |
| Mini viewer toolbar | ✅ Clickable Fit/1:1/T/S/B | ⚠️ Text labels only | clickable |
| Twilight timeline (preview) | ✅ `RenderPreviewTimeline` | ❌ | **missing** |
| Phase timeline (session) | ✅ `RenderTimeline` | ❌ | **missing** |
| Compact guide graph (bottom strip) | ✅ `RenderCompactGuideGraph` | ❌ (RMS text only) | **missing** |
| V-curve overlay during autofocus | ✅ `RenderVCurveChart` | ❌ | **missing** |
| Preview mount section | ✅ `RenderPreviewMountSection` | ❌ | **missing** |
| ABORT button + confirm modal | ✅ button + overlay | ⚠️ status bar hint | **missing modal** |

## Strategy

Use `Canvas<RgbaImage>` + `RgbaImageRenderer` for pixel-heavy pieces (timeline,
guide graph, V-curve, cooling sparklines). `RgbaImageRenderer` exposes the same
`FillRect` / `DrawText` / `DrawLine` primitives used by the GPU path, so the
existing draw code in `LiveSessionTab<TSurface>` can be extracted into static
helpers that take a `Renderer<TSurface>` and called from both hosts.

Use `Console.Lib` native widgets (`TextBar`, `ScrollableList`, `MenuBase`) for
everything that's fundamentally text (mount section, buttons, status bars).

`GuideGraphRenderer` already holds the pure math (`ComputeYScale`, `ComputeWindow`,
`ErrorToY`) — shared by both hosts. The per-host render loop reads those and
drives `FillRect` / `DrawLine`.

## Phases

### Phase 1: Extract renderer-agnostic draw helpers (prep)

- [ ] Move `RenderCompactGuideGraph` body from `LiveSessionTab<TSurface>` into a
      static `GuideGraphRenderer.Render<TSurface>(Renderer<TSurface>, RectF32,
      GuideState, float dpiScale)` that both GPU + TUI call. GPU path loses
      ~80 lines of duplication.
- [ ] Same for `RenderVCurveChart` → `VCurveRenderer.Render<TSurface>(...)`.
- [ ] Same for `RenderTimeline` / `RenderPreviewTimeline` → `SessionTimelineRenderer`.
- [ ] Same for `RenderMiniSparkline` → `CoolingSparklineRenderer`.
- [ ] No TUI changes in this phase — purely moving code so the GPU tab shrinks
      and the TUI tab gets the same entry points.

### Phase 2: Twilight / phase timeline band in TUI

- [ ] Add a third `Panel.Dock(DockStyle.Top, 2)` row below `_guideBar` for a
      timeline strip (2 cells tall ≈ 40px Sixel band on typical terminal).
- [ ] Wrap a `Canvas<RgbaImage>` + `RgbaImageRenderer` — render via
      `SessionTimelineRenderer.Render` each frame.
- [ ] Session-running → phase bars; preview → twilight bands. Branching already
      exists in the shared helper.
- [ ] Now-needle position animates on each frame (existing `NeedsRedraw` wiring).

### Phase 3: Compact guide graph in TUI bottom strip

- [ ] Replace current `_guideBar` `TextBar` with a horizontal Panel split:
      left = `Canvas` (guide graph ≈ 60% width), right = `TextBar` (RMS text
      ≈ 30%), far right = `ABORT` button/`TextBar`.
- [ ] Render via `GuideGraphRenderer.Render` from Phase 1.
- [ ] Dither markers + settling shade already handled in shared code.
- [ ] Height: 4–5 cells tall (enough Sixel resolution for RA/Dec lines +
      correction bars to be legible).

### Phase 4: V-curve overlay during auto-focus

- [ ] When `liveState.Phase is SessionPhase.AutoFocus` **and** the selected
      OTA has `FocusSamples.Length >= 2`, overlay the V-curve Canvas on top of
      (or replacing) the Sixel preview area.
- [ ] Toggle automatic: show V-curve while auto-focus runs, fall back to
      preview image when phase changes.
- [ ] Uses `VCurveRenderer` from Phase 1.

### Phase 5: Preview mount section

- [ ] Extend `BuildPreviewRows` to append a mount block at the end (after all
      OTAs): `HeadingRow` with status-color dot (green tracking / yellow slewing /
      dim idle), `TextRow` for RA / Dec / pier / HA.
- [ ] Read from `liveState.PreviewMountState` + `liveState.PreviewMountDisplayName`.

### Phase 6: Clickable mini-viewer toolbar

- [ ] Replace `RenderPreviewToolbar` text-only rendering with `ActionRow` buttons
      (reuses the existing `_rows` / `Tracker.Register` plumbing for hit testing).
- [ ] Buttons: `[Fit] [1:1] [Raw/Lnk/Unl/Lum] [S±] [B]` — clicking each toggles
      `_viewerState` the same way the keyboard handlers do today.

### Phase 7: ABORT button + modal overlay

- [ ] Add an `ActionRow` on the bottom strip (right side) with a red `ABORT`
      button when `liveState.IsRunning` — posts the same signal the keyboard
      path does.
- [ ] When `liveState.ShowAbortConfirm`, render a centered modal: red bg
      `Canvas` + text `"Confirm ABORT? Enter to confirm, Escape to cancel"`.
      Use a `PopupLayer` or an overlay Panel. The existing Enter/Escape
      handlers already work; this is purely visual.

### Phase 8: Per-camera sparkline colors

- [ ] `BuildSparkline` already picks from a fixed Unicode set; swap the
      `TextRow` styling to a per-camera `SgrColor` (cycle through 4 ANSI colors
      matching `CameraTempColors` intent).
- [ ] Or: swap to a `Canvas` sparkline that uses the same
      `CameraTempColors` / `CameraPowerColors` RGBA palette as the GPU.

### Phase 9: Polish + tests

- [ ] Integration test: render `TuiLiveSessionTab` into an `RgbaImage`, snapshot
      each frame across all phases (preview / cooling / autofocus / observing /
      abort-confirm) and compare pixel bounds to make sure nothing regresses.
- [ ] Update memory: `project_tui_live_session_parity.md` with what shipped,
      remove from `project_dual_view_design.md` "Remaining phases" list since
      phases 4–5 there are covered by this plan.

## Why this ordering

- Phase 1 (extract helpers) is the de-risking step — it's a pure refactor with
  no behavior change, confirmed by the GPU tab still rendering identically
  before any TUI changes land. Everything downstream reuses those helpers.
- Phase 2 → 3 adds the most visible "oh yeah, this looks like the GUI now"
  polish: the twilight band + guide graph are what make the live session feel
  glanceable.
- Phases 4–5 are contextual (V-curve only during autofocus, mount only in
  preview) so they're less load-bearing on first impression.
- Phases 6–7 are interaction parity (buttons, modal) — lower priority than
  the information density phases.
- Phase 8 is cosmetic polish.

## Non-goals

- No new state model. `LiveSessionState` is already shared; this plan is purely
  about rendering that state through a different host.
- No new signals. All actions (`TakePreviewSignal`, `ConfirmAbortSessionSignal`,
  etc.) already exist and are consumed by `AppSignalHandler`.
- Not trying to match GPU pixel-for-pixel — terminal cell grid + Sixel tiling
  put a hard lower bound on visual fidelity. The goal is functional parity and
  the same information at a glance, not a screenshot diff.
