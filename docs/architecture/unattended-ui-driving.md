# Driving the GUI and the TUI unattended

Moved out of `CLAUDE.md` (2026-08-22), which keeps the pieces that compose and the rules that bite
and points here for the surfaces in detail. Two inspectors, one transport
(`DIR.Lib.Diagnostics.DebugInspectorCore`), both DEBUG-only and compiled out of Release entirely:
`DebugInspector` for the SDL+Vulkan hosts and `ConsoleDebugInspector` for the TUI, each with an MCP
sidecar registered in `.mcp.json`.

Related: [../plans/mcp.md](../plans/mcp.md) is the *product* MCP server (`tianwen-mcp`) and has
nothing to do with these inspectors; [../plans/device-simulator-ci.md](../plans/device-simulator-ci.md)
is the live-simulator suite, which is the other half of "no human in the loop".

---

## Unattended end-to-end GUI testing with fake devices

Drive a full `RunAsync` session against simulated hardware with **no human in the loop and no
screenshot-poll-and-OCR**. Three pieces compose:

1. **A fake-device profile.** Fakes share the real URI shape with host `FakeDevice` (`FakeDeviceSource`):
   `Mount://FakeDevice/FakeMount1?latitude=…&longitude=…&port=SkyWatcher`, `Camera://FakeDevice/FakeCamera1`,
   `Camera://FakeDevice/FakeGuideCam`, `Guider://FakeDevice/FakeGuider1`, `Focuser://FakeDevice/FakeFocuser1`,
   `FilterWheel://FakeDevice/FakeFilterWheel1`, `Weather://FakeDevice/FakeWeather1`. Discovery surfaces **two**
   cover/calibrators (both ASCOM-`CoverCalibrator` class): `CoverCalibrator://FakeDevice/FakeCoverCalibrator1`
   is a flip-flat (motorised cover flap + panel), and `CoverCalibrator://FakeDevice/FakeCoverCalibrator2?hasCover=false`
   is a driver-controlled light panel with **no** flap (models the Gemini FlatPanel Lite, reports
   `CoverStatus.NotPresent`, calibrator only). `hasCover=false` on the URI is what selects the flap-less
   behaviour in `FakeCoverDriver`; absent = flip-flat. **`port=SkyWatcher`** on
   the mount selects `FakeSkywatcherMountDriver` (believed/true pointing seam + polar-misalignment + worm PE,
   the variant that exercises the meridian-flip and Dec-sense paths); omit `port` for the lightweight
   believed-only `FakeMountDriver`. Fakes only surface from discovery when `IncludeFake:true`; the GUI
   auto-includes them at startup when the active profile already references any fake URI
   (`Program.cs` → `ProfileData.ReferencesAnyFakeDevice`), otherwise Shift+Discover. `ProfileData.SiteLatitude/
   Longitude` **must** match the mount URI's `latitude/longitude` (a split site throws "Could not calculate
   timezone"). Canonical wiring (URIs, connect order, guider→mount `LinkDevices`, guide-scope FL):
   `SessionTestHelper.CreateSessionAsync(mountPort:"SkyWatcher", latitude, longitude)`.

2. **Anchor the clock** with `TIANWEN_NOW` (see the TimeProvider section of `CLAUDE.md`) to a real night at that site,
   so the planner computes visible targets and the session leaves `WaitingForDark` at once instead of stalling
   in daylight.

3. **Drive + observe via the DEBUG inspector, not screenshots.** A **DEBUG** GUI build attaches
   `DebugInspector` (`Program.cs`, compiled out of Release entirely), exposing this process to the
   `sdl-ui-inspector` MCP sidecar (`.mcp.json` → `dnx SdlVulkan.Renderer.Inspector`, UDP-multicast discovery).
   It gives six surfaces:
   - **Describe/state snapshot** (the `AppState` block): `activeTab`, `profile`, `sessionRunning`, `phase`,
     `mountConnected/Name/RaJ2000/DecJ2000/mountSlewing/mountTracking`, `lastNotification`, sky-map viewport,
     `liveSessionMode` (Preview/PolarAlign/Planetary/Flats) + `flatRunActive`/`flatStatus`.
     **Poll this for coarse session state** (phase transitions, stuck-slewing, notifications); it replaces a
     screenshot+OCR loop.
   - **Programmatic signals** (`SignalFactories`): the **whole app bus is postable by name**; the
     `SignalFactories` map is **source-generated** over EVERY `*Signal` type in `TianWen.UI.Abstractions`
     (`DIR.Lib.SourceGenerators.SignalDirectoryGenerator` → `SignalDirectory.BuildFactories(bus)`), so
     `list_signals` returns all ~40 (e.g. `StartSession`, `StartFlats{source,flatsPerFilter}`,
     `RespondSessionPrompt{proceed}`, `SkyMapSetView`, `SkyMapSolveSync`, `DiscoverDevices{includeFake}`).
     JSON keys are the camelCase parameter names; a missing field falls back to the signal's declared ctor
     default. **No runtime reflection** (the generator is DEBUG-gated + emits `bus.Post(new T(...))`), and
     the generator + its `DIR.Lib.SignalJson` binder live in **DIR.Lib**; nothing here is TianWen-specific,
     so any `SignalBus` consumer gets the directory for free. A signal with a required *complex* payload
     (e.g. a `TextInputState`/`ProfileData`) is skipped (not JSON-postable). Posting `StartSession` runs the
     whole `RunAsync` with no clicking; posting `StartFlats` drives a flat run regardless of the visible mode.
   - **Clickable regions** (`GetRegions`, `describe_ui`): click-by-label for any action without a dedicated signal.
     `click` / `click_label` take a **clicks count** (`SdlVulkan.Renderer.Inspector` 7.16+), so a double-click
     affordance is drivable at all; the whole run is delivered, not just its last press, because SDL reports a
     double as TWO button-downs (counted 1 then 2) and an app is entitled to act on both -- sending only the
     count-2 press would drive a sequence no real mouse can produce. `scroll` takes a **modifier** (7.17+); it
     used to hard-code None, which left Ctrl+wheel zoom and Shift+wheel undrivable, and an app reading the
     modifier off global keyboard state (nothing but a real key press moves that) saw no synthesized input at all.
   - **Arranged layout tree** (`GetLayout`, `describe_layout`, `SdlVulkan.Renderer.Inspector` 6.9+): the FULL
     `DIR.Lib.Layout` tree the chrome + active tab painted this frame; every node with its `depth` (pre-order,
     so the flat list reconstructs the nesting), `kind` (Stack/Dock/Grid/Overlay/Split/Leaf), rect, `axis`,
     `columns`, `text`+`fontSize`, `fillKey`, `bg`, and `hitRole`/`hitLabel`. The STRUCTURAL counterpart to the
     clickable-only `describe_ui` (which only shows interactive leaves); use it to debug placement (clipping,
     gaps, why a panel is the size it is, nesting). Widgets retain their arranged tree via
     `PixelWidgetBase.GetCapturedLayout()`. **Capture is UNCONDITIONAL as of DIR.Lib 8.8** -- the old
     `LayoutInspection.Enabled` gate that `DebugInspector.Attach` used to flip is obsolete, is no longer
     read, and is scheduled for deletion at the next DIR.Lib major, so do not assert the "production
     paints carry no overhead" claim it used to buy. Empty if the app draws without the layout DSL.
   - **Render-thread watchdog** (`render_liveness`, `SdlVulkan.Renderer.Inspector` 6.8+): the inspector runs
     every command (incl. `ping`) ON the render thread, so a `ping` that round-trips proves the render loop is
     pumping; a connected-but-silent probe means it's blocked (a hang) while the process is still up.
     `render_liveness` classifies ALIVE/BLOCKED/DEAD (and on BLOCKED prints the `dotnet-stack report -p <pid>`
     to capture the frozen frame); `watchSeconds>0` polls until it wedges. Use this, not screenshot/describe,
     to decide IF the render thread is stuck (those also block when it is).
   - **Validation report** (`validation_report`, and read it with the gate in mind): **a zero message count
     is evidence of correctness only when `active` is true.** `active` is the DEBUG + `SDLVK_VALIDATION=1`
     gate AND `layerAvailable`, and before SdlVulkan.Renderer 7.11 only the gate was reported, so a host
     with no Khronos validation layer installed answered `enabled: true` with zero messages and zero sync
     hazards, indistinguishable from a clean run. That reading is what sent a device-loss investigation
     upstream down the wrong path. Install the Vulkan SDK's layer before believing a clean report.

   **A GPU fault names itself now** (SdlVulkan.Renderer 7.11, which is where the `Shaders/` + swapchain path
   got its validation-layer pass). `VK_ERROR_DEVICE_LOST` is terminal and logs event 115 instead of entering
   swapchain recovery, which could never work once the device is gone and used to surface as a
   "recovery storm" (event 110) that reads like a workload problem. So a wedge and a dead device are now
   distinguishable in `GUI_*.log`, and `render_liveness` BLOCKED means the render thread, not the device.
   Event 501 additionally names the selected GPU (device, type, driver + API version, queue family, how many
   were enumerated), so a report is attributable to hardware from our own log; selection now prefers a
   discrete GPU, which is a preference and a no-op on an integrated-only box.

   `StartSession` needs ≥1 pinned target (`PlannerState.Proposals.Length > 0`, else it no-ops with "pin
   targets in the Planner first"). Planner pins persist **per-profile** to `AppData/Planner` and reload at
   startup (`PlannerPersistence.TryLoadAsync`), so pin once and every later unattended run reuses them.

**Ground truth for fine telemetry is the Debug log, not the inspector snapshot.** The `AppState` snapshot
reads `LiveSessionState`, which can lag during the guide loop; per-frame guide stats (errDec/corrDec/RMS),
HA, and pier side come from `%LOCALAPPDATA%/TianWen/Logs/<date>/GUI_*.log`. The describe path is the right
tool for orchestration and coarse state; the log is the source of truth for what the drivers actually did.

## Driving the TUI unattended (the terminal inspector)

The TUI has the same treatment, via `ConsoleDebugInspector` (Console.Lib 4.3+) on the same
`DIR.Lib.Diagnostics.DebugInspectorCore` transport the GPU inspector now uses, with the
`Console.Lib.Inspector` MCP sidecar in `.mcp.json` alongside `sdl-ui-inspector`. Wired in
`TuiSubCommand.RunTuiAsync`, `Pump()`ed once per loop iteration.

**A terminal reads back as TEXT, which is the one thing a GPU surface cannot offer.** `screen` / `row` /
`cell` report the **front** cell buffer -- what was actually emitted, not a parallel model that can drift
-- so an assertion is words ("row 4 is `Guider  Built-in Guider`", "the board header says `table (window
too small for cards)`") instead of a screenshot to eyeball. `cell` adds the resolved pen, which is how a
colour bug gets caught at all: a glyph that is present but drawn `#000000` on `#000000` is invisible on
screen yet indistinguishable from a correct one in the text dump. `appState` is the curated snapshot
(hand-written JSON -- `Utf8JsonWriter`, never a reflective overload, since an AOT consumer disables
reflective JSON), named to match the GUI's fields wherever they mean the same thing. `inputLog` is the
event trace, written **before** dispatch so a swallowed event still appears.

One gotcha: the modifier parameter is **`mods`** (`"Ctrl"`, `"ctrl+shift"`), not a `ctrl` boolean, and the
verb echoes what it resolved -- a dropped chord is otherwise invisible, since bare `G` is usually a
different valid binding.

**The diffing cell buffer is ON unconditionally** (Console.Lib 4.7): a clock tick emits ONE cell (pinned by
`TuiTabBarTests.AClockTick_EmitsOnlyTheFlippedDigits`, which drives the REAL bar into a real `CellBuffer`),
and the old DEC-2026 synchronized-output wrapper is gone -- it only ever hid the full-row repaint the buffer
now prevents. Getting there surfaced five bugs whose lessons are pinned in tests; the shapes to not
reintroduce:

- **Never rely on leftover SGR state.** Text used to be painted foreground-only ("the cells keep whatever
  Background was painted underneath") and unselected TUI rows emitted no style at all -- both invisible on a
  live terminal, both wrong the moment a cell buffer must name a colour per cell. `CellLayout` now resolves
  a text cell's background from the TREE (a depth-keyed stack of enclosing backgrounds); every
  `EquipmentFieldItem` row states its own pen (`StyleRow`). An UNSTATED colour is alpha-zero -> SGR 39/49
  (the terminal's default), never black -- a terminal cell does not composite, so alpha 0 cannot mean
  "transparent".
- **Attributes must be stated in both directions.** The cell sink emitted `ReverseOn` and never
  `ReverseOff`, so one reversed cell (a text cursor) inverted everything painted after it.
- **A cursor move must not flush, and moves ride the same byte stream as the glyphs.**
  `TerminalViewport.SetCursorPosition` used to call `parent.Flush()` per move -- on a buffered terminal that
  ships the HALF-PAINTED diff (blanks over the old text, then labels one by one), which was the
  once-per-second top-bar flicker; only the frame's owner flushes (`TuiSubCommand`'s render `finally`).
  And the sink moves via a CUP escape, not Win32 `SetCursorPosition` -- one ordered sequence, one delivery
  mechanism.
- **Diagnose repaints from the log, not the screen.** The TUI logs `TUI paint: N frames, M cells (K opaque)`
  once a second (totals diffed across the interval -- a per-LAST-flush read is how the mid-paint flush bug
  hid from the first version of this accounting), plus the exact emitted runs in Debug
  (`CellBuffer.CollectFlushDiagnostics`). Steady state is ~1 cell/tick; a high opaque share means an
  unmodelled SGR is bypassing the diff, not that the diff is doing badly.

Sixel composes with the buffer: `Canvas` declares its region via `BeginRawOutput` / `MarkRawRegion`,
verified on the Guider tab (every sampled canvas cell reports `kind=Image`, so the diff breaks its runs
around the picture instead of blanking it). Ctrl+H also works now -- Console.Lib's byte table special-cased
`0x08` as Backspace, shadowing the general `0x01..0x1A -> letter+Ctrl` rule (the Backspace KEY sends DEL
0x7F), so Ctrl+H was the one unbindable letter.
