# Plan: Stellarium-Style Time Scrubber for the Sky Map

Status: NOT STARTED. Authored 2026-06-12 for hand-off; all file/line facts verified against main @ `ae85cd8`.

Goal: step the sky map's observation instant relative to NOW (+1h, +1d, ...) as an
**offset from the wall clock** (so the scrubbed instant keeps advancing with real time),
driving sky color, LST (star/horizon/crosshair rotation), planet+Moon positions, horizon
fill, and below-horizon label dimming. HUD shows the scrubbed time prominently vs wall
clock; `0` resets to now. TODO.md Sky Map item (line ~33) with sub-bullets, plus the
paired Inbox item: `"N" key jumps the sky to local midnight`.

## Current state (verified facts)

The "viewing time" plumbing already exists and is threaded through almost everything -
this feature is mostly a new offset source + key bindings + HUD polish, NOT a rendering
refactor:

- `SkyMapTab<TSurface>.Render` (`src/TianWen.UI.Abstractions/SkyMapTab.cs:119-125`)
  derives the single time value used everywhere:
  ```csharp
  var viewingTime = plannerState.PlanningDate?.ToUniversalTime() ?? _cachedLiveTime;
  ```
  `_cachedLiveTime` = `timeProvider.GetUtcNow()` refreshed at most once per second.
- Everything downstream consumes `viewingTime`:
  - `SiteContext.Create(siteLat, siteLon, viewingTime)` (line 128) -> `site.LST`
    (IAU 1982 GMST, `src/TianWen.Lib/Astrometry/SOFA/SiteContext.cs:34-67`) ->
    UBO `sinLST/cosLST` (`VkSkyMapPipeline.UpdateUbo`, `src/TianWen.UI.Shared/VkSkyMapPipeline.cs:967-968`)
    -> GPU horizon clip + horizon fill shader + zenith/view matrix; static-geometry
    cache key `(site.LST, showHorizon, showAltAz)` (`VkSkyMapTab.cs:178`); meridian line
    (`VkSkyMapTab.cs:191`); `site.IsAboveHorizon` label dimming (SkyMapTab.cs:514,553 +
    VkSkyMapTab.cs:403,467,515,885).
  - `State.GetSunAltitudeDegCached(viewingTime, siteLat, siteLon)`
    (`SkyMapState.cs:209-226`, 10 s drift-keyed cache) -> sky background palette
    (`SkyBackgroundColorForSunAltitude`, lines 277-299) + Milky Way alpha
    (`VkSkyMapTab.cs:222-225`).
  - `State.GetPlanetPositionsCached(viewingTime)` (`SkyMapState.cs:251-269`,
    exact-DateTimeOffset cache key) -> all planets incl. Moon via `VSOP87a.ReduceJ2000`.
  - `DrawInfoStrip(..., viewingTime, plannerState.SiteTimeZone, isTimeShifted)`
    (SkyMapTab.cs:571-599): already renders site-local time, blue `TimeShiftColor` +
    full `yyyy-MM-dd HH:mm` when `isTimeShifted` (currently `PlanningDate.HasValue`).
- Existing absolute-date mechanism (the thing to extend, not duplicate):
  `PlannerState.PlanningDate` (`DateTimeOffset?`, null = live), mutated by
  `PlannerActions.ShiftPlanningDate/ShiftPlanningHours/ResetPlanningDate`
  (PlannerActions.cs:27-57) which set `NeedsRecompute = true` (full planner recompute -
  targets, night window, altitude profiles). Sky-map keys already bound:
  Left/Right = +-1 day, Up/Down = +-1 hour, `T` = reset (SkyMapTab.cs:814-893).
- Key availability in `SkyMapTab.HandleKey`: digits `D0-D9` all free; `N` free;
  `E F I J K L Q R U V W X Y Z`, `Home/End/PageUp/PageDown`, `F1/F2/F4-F12` free.
  Taken: `G H B C S A O D M P`, `Plus/Minus` (mag limit), arrows, `T`, `F3` (search).
- TUI has **no sky map tab** (TuiSubCommand.cs:79) - all key logic lives in the shared
  base `SkyMapTab<TSurface>`; only the GPU path exercises it. No TUI parity work.
- Mount overlay + slew ETA deliberately use polled hardware state + wall clock
  (`VkGuiRenderer.PopulateSkyMapMountOverlay` lines 528-596; `UpdateSlewEta` line 614
  calls `timeProvider.GetUtcNow()`): physical reality, MUST NOT follow scrubbed time.
- Global status bar wall clock: `VkGuiRenderer.RenderStatusBar` lines 320-321 (site-local
  per feedback_time_display_local_no_bcl_now) - remains the wall-clock anchor on screen.

## Design

### Offset, not absolute

New state on `SkyMapState` (sky-map-scoped; deliberately NOT on `PlannerState` so
scrubbing never triggers planner recompute):

```csharp
/// <summary>Scrub offset added to the live wall clock for sky rendering. Zero = live.
/// Stored as an offset (Stellarium-style) so the scrubbed instant keeps advancing
/// with real time. Not persisted.</summary>
public TimeSpan TimeOffset { get; set; }   // default TimeSpan.Zero
```

`viewingTime` derivation in `SkyMapTab.Render` becomes:

```csharp
var baseTime = plannerState.PlanningDate?.ToUniversalTime() ?? _cachedLiveTime;
var viewingTime = baseTime + State.TimeOffset;
var isTimeShifted = plannerState.PlanningDate.HasValue || State.TimeOffset != TimeSpan.Zero;
```

Composition rule: `PlanningDate` (absolute, planner-driven) remains the base when set;
the scrub offset stacks on top. Reset (`0`) clears ONLY the offset. The existing `T`
key keeps clearing `PlanningDate` (and should also clear the offset for a full
"back to live" - both resets in `T`, offset-only reset in `0`).

Caches need no changes: `GetPlanetPositionsCached` keys on the exact DateTimeOffset
(already invalidates every 1 s tick today); `GetSunAltitudeDegCached` keys on >10 s
drift, which a scrub step exceeds by construction.

### Key bindings (in `SkyMapTab.HandleKey`)

Re-point the existing time keys at the offset and add granularity:

| Key | Action |
|-----|--------|
| `Up` / `Down` | `TimeOffset += / -= 1 hour` (was ShiftPlanningHours) |
| `Left` / `Right` | `TimeOffset -= / += 1 day` (was ShiftPlanningDate) |
| `PageUp` / `PageDown` | `TimeOffset += / -= 1 week` |
| `Shift+Up` / `Shift+Down` | `TimeOffset += / -= 10 min` (if InputKey carries modifiers here; check `InputEvent` - if not available skip, do not contort) |
| `0` (D0) | `TimeOffset = TimeSpan.Zero` |
| `N` | jump to next local midnight: `TimeOffset = nextLocalMidnight - nowLocal` (compute in site-local via `plannerState.SiteTimeZone`; "next" = tonight's upcoming 00:00, i.e. if already past midnight and before noon, offset may be ~0..12 h negative to land on the CURRENT night's midnight - define as: midnight of the current astronomical night: `today 24:00` if local time >= 12:00, else `today 00:00`) |
| `T` | full reset: `PlannerActions.ResetPlanningDate(state)` AND `TimeOffset = Zero` |

Rationale for re-pointing the arrows: the current arrow bindings mutate
`PlanningDate` + `NeedsRecompute`, which is a heavyweight planner recompute per
keypress and entangles the planner tab's date with casual sky scrubbing. The planner
tab keeps its own date controls; the sky map becomes purely visual. Mention this
behavior change in the PR description. Each key sets `NeedsRedraw` (via whatever
mechanism the existing toggles use) and NEVER `NeedsRecompute`.

Hold-to-repeat: rely on OS key repeat (SDL sends repeated KeyDown) - no custom repeat logic.

### HUD (DrawInfoStrip)

Extend the existing shifted-mode display (signature already takes
`viewingTime, siteTimeZone, isTimeShifted`):

- Shifted: blue `TimeShiftColor` text `"yyyy-MM-dd HH:mm  (+2d 03h)"` - scrubbed
  site-local instant plus a compact signed offset chip rendered from
  `State.TimeOffset` (+ `PlanningDate` delta when set). Negative: `(-5h)`. ASCII only.
- Live: unchanged `HH:mm:ss` grey.
- The wall clock remains visible in the global status bar, satisfying "the user never
  confuses the two"; do not duplicate it inside the strip unless it reads ambiguously
  in practice (judgement call at verification).
- All displayed times site-local via `.ToOffset(siteTimeZone)`
  (feedback_time_display_local_no_bcl_now).

Offset formatting helper: `static string FormatOffset(TimeSpan o)` -> `"+3h"`,
`"-1d 02h"`, `"+2w"` style, largest-two-units; unit-test it (pure function).

### What stays on the wall clock (do not touch)

- `PopulateSkyMapMountOverlay` / `UpdateSlewEta` (mount = physical reality).
- The slew-target marker + ETA from PR #24.
- `_cachedLiveTime` refresh cadence.
The mount reticle rendering against a scrubbed sky is CORRECT behavior: the reticle
projects via RA/Dec, and a scrubbed LST rotates the horizon/alt-az layer around it -
exactly what Stellarium does with a telescope marker. No special-casing.

### Inspector verification hooks

`SkyMapViewControlSignal`/readback shipped on main (`dd97b9d`) for scripted view
control - extend or reuse to read `TimeOffset` in `describe_ui` appState JSON so the
live verification below can assert the offset state (small addition to the state dump,
mirrors existing fields).

## Phases

| Phase | Work | Est. |
|------:|------|------|
| 1 | `SkyMapState.TimeOffset` + derivation change in `SkyMapTab.Render` + `FormatOffset` helper + unit tests | S |
| 2 | Key bindings incl. `N` midnight jump + `0`/`T` resets; remove planner-date mutation from sky-map arrows | M |
| 3 | HUD offset chip in `DrawInfoStrip` | S |
| 4 | Inspector appState exposure + live GUI verification (below) | S |
| 5 | Docs: TODO ticks (time-adjuster + "N to midnight" items), PLAN-summary row | S |

## Tests

Unit (`TianWen.Lib.Tests` / UI.Abstractions tests live in the same suite):

1. `FormatOffset` cases: zero, +3h, -90m -> `-1h 30m`, +9d -> `+1w 2d`, sign handling.
2. `SkyMapState`-level: `GetSunAltitudeDegCached` returns night palette for
   `now + 12h` vs day for `now` (pick a site/date where that holds, e.g. Vienna June
   noon); proves the offset propagates to sun altitude.
3. Midnight-jump math as a pure helper (`ComputeMidnightOffset(nowLocal)`) with cases:
   21:30 -> +2h30m, 02:00 -> -2h (current night), 11:59 -> -11h59m boundary, 12:01 ->
   +11h59m.

Live GUI verification (run-gui skill + sdl-ui-inspector, fake profile):

- Press `Up` x3: screenshot shows sky rotated ~45 min of RA... (LST shift) and info
  strip shows `(+3h)` in blue; planner tab targets list did NOT recompute (no
  "Recomputing..." status).
- `N` at a daytime wall clock: sky turns night palette, strip shows midnight.
- `0` returns to live (strip grey `HH:mm:ss`).
- Mount reticle stays at its RA/Dec through all of the above (overlay unchanged
  between screenshots modulo horizon-layer rotation).

## Out of scope

- Persisting `TimeOffset` across sessions (deliberately ephemeral).
- Animating/continuous time-lapse playback (Stellarium's `J`/`K`/`L` rates).
- Scrubbing the planner schedule/altitude charts (planner keeps `PlanningDate`).
- Proper-motion epoch adjustments for star positions during scrub (Tycho-2 epoch
  handling stays as-is; sub-arcsec at +-days of scrub).
- TUI sky map (does not exist).
