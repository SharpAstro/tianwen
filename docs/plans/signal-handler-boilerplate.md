# Signal-Handler Boilerplate Reduction (`AppSignalHandler`)

**Status: NOT STARTED** (plan drafted 2026-07-03, after the two "route, don't implement" passes: PR #68 + PR #70)

## Context

The "route, don't implement" rule is now fully satisfied: every subscribe lambda and TextInput
callback takes the signal payload, calls one or two helpers, and reflects results into UI state
(`docs/todo/infra.md`, completed 2026-07-03). `AppSignalHandler.cs` went 2,991 -> 2,519 lines
across the two extraction passes.

The remaining ~2,519 lines decompose as ~800 lines of class scaffolding (usings, ~15 injected
fields, ctor, shared helper methods like `InitializePlannerAsync` / `FetchWeatherForecastAsync`),
~1,650 lines across 42 thin routing handlers, and a small tail. The handlers are *individually*
thin, but they hand-roll the same three idioms over and over. Measured on 2026-07-03 (commit
`bf4cc98`):

| Idiom | Count | Cost today |
|-------|------:|------------|
| `appState.AppendNotification(_timeProvider.GetUtcNow(), severity, msg)` + `NeedsRedraw = true` | 72 | ~3 lines each (~215 lines) |
| Guard ladders: `liveSessionState.IsRunning` (12), `ActiveProfile is ...` (23), `DeviceHub is ...` (21), `TryGetConnectedDriver` (16) | ~72 guards | 5-6 lines each (~380 lines) |
| `tracker.Run` + `try/catch(OCE)/catch(Exception)/finally` + busy-flag + redraw scaffolding | 16 runs / 24 catch blocks | ~15-20 lines each (~280 lines) |

That is roughly **850-900 lines that are the same three shapes**, not 42 unique handlers.
Target outcome: every handler reads as *guard, guard, route* in 6-12 lines; the file lands
around ~1,700 lines with the helpers themselves ~120 lines in one place.

## Non-goals (deliberate)

- **No fluent pipeline / DSL** (`bus.Handle<T>().Require(...).Run(...)`) and **no MediatR-style
  pipeline behaviors**. The `SignalBus` was explicitly chosen *against* framework-ness
  (CLAUDE.md "Signal Handler Pattern"). A combinator chain wrecks stack traces and hides the one
  thing signal handlers must keep visible: what runs inline on the render thread vs what is
  submitted to the tracker.
- **No source generator.** The variation between handlers (which states get `NeedsRedraw`,
  per-OTA busy arrays, follow-ups like the weather refetch) means a generator grows escape
  hatches until it is just code again -- with a build-time dependency on top.
- **Not the partial-class split.** Splitting `AppSignalHandler` into by-area partials
  (`.SkyMap` / `.Equipment` / `.Session` / `.Polar`, the `LiveSessionTab` treatment) is an
  orthogonal, purely organizational lever. It can follow this plan if file size still bothers,
  but it removes zero boilerplate on its own.
- **Do not weaken the SignalBus delivery contract.** Sync subscribers run inline on the render
  thread; async subscribers are submitted to `BackgroundTaskTracker`. Several handlers rely on a
  *sync prefix* (immediate UI feedback: busy flag, tab switch, status message) followed by an
  explicit `tracker.Run` for the slow part. The helpers must preserve that split point, not
  paper over it with an async-everywhere rewrite.

## Design: three tiers of plain instance helpers

All helpers are `private` instance methods on `AppSignalHandler` -- it already holds every
dependency they need (`_appState`, `_timeProvider`, `_tracker`, `_logger`). No new types, no new
registration, no behavior change; call sites stay greppable and step-debuggable.

### Tier 1 -- `Notify()`: one-line notifications

```csharp
/// <summary>AppendNotification with the current timestamp + redraw. The redraw always hits
/// appState; pass additional states (skyMapState, liveSessionState, ...) when the handler's
/// surface needs the kick too.</summary>
private void Notify(NotificationSeverity severity, string message)
```

Every `AppendNotification(GetUtcNow...) + NeedsRedraw` pair becomes
`Notify(NotificationSeverity.Warning, "Camera not connected");`. 72 sites, ~2 lines saved each,
and the `GetUtcNow()` timestamping can never be forgotten again. If a handful of sites need
extra `NeedsRedraw` targets, prefer setting them explicitly at the call site over a params
array -- explicit redraw targets are load-bearing (see `feedback_preserve_redraw_after_signals`).

### Tier 2 -- `TryRequire*` guards: ladders become one-liners

```csharp
private bool TryRequireIdle(string action);                     // session-not-running; notifies "Cannot {action} while a session is running"
private bool TryRequireProfile([NotNullWhen(true)] out Profile? profile, out ProfileData data);
private bool TryRequireHub([NotNullWhen(true)] out IDeviceHub? hub);
private bool TryRequireConnected<T>(Uri? uri, string label, [NotNullWhen(true)] out T? driver)
    where T : class, IDeviceDriver;                             // TryGetConnectedDriver + notify "{label} not connected"
```

A typical handler prologue goes from ~25 lines to:

```csharp
if (!TryRequireIdle("preview")) return;
if (!TryRequireProfile(out var profile, out var pdata)) return;
if (!TryRequireHub(out var hub)) return;
if (!TryRequireConnected<ICameraDriver>(ota.Camera, "Camera", out var camera)) return;
```

Notes:
- `[NotNullWhen(true)]` so no `!` downstream (per `feedback_member_not_null_when`).
- Default messages with an optional `string? message = null` override cover the context-specific
  wording ("Mount is not connected -- connect it from the Equipment tab first").
- Some guards are silent today (`if (appState.DeviceHub is not { } hub) return;` with no
  notification). Keep that distinction: a `notify: false` optional parameter, or simply leave
  silent one-line guards as-is -- they are already one line.

### Tier 3 -- `RunTracked()`: the async error/busy scaffolding

```csharp
/// <summary>tracker.Run with the standard error surface: OCE is logged (and optionally
/// notified), any other exception is logged + notified as "{failurePrefix}: {ex.Message}",
/// and onFinally (busy-flag clear + redraws) always runs.</summary>
private void RunTracked(
    string name,
    string failurePrefix,
    Func<CancellationToken, Task> work,
    Action? onFinally = null,
    string? cancelMessage = null)
```

Standardizes the 16 `tracker.Run(async () => { try ... catch (OperationCanceledException)
{ log/notify } catch (Exception ex) { log + notify Error } finally { clear busy; redraw } },
name)` blocks. The handler keeps its sync prefix (busy-flag set, status message, tab switch)
inline before the `RunTracked` call -- the render-thread/background split point stays explicit
and identical to today. OCE handling today varies (some notify "cancelled", some log-only):
`cancelMessage: null` = log-only, non-null = notify too, so each call site keeps its current
behavior by construction.

### Tier 4 (optional, decide AFTER Tiers 1-3) -- `Wire<T>` for trivial device actions

~8-10 handlers are pure "resolve driver -> one EquipmentActions call -> ok/fail notification"
(`SetCoolerSetpointSignal`, `SetCoolerOffSignal`, `WarmAndCoolerOffSignal`, disconnect variants,
...). A one-line registration could collapse each:

```csharp
WireCameraAction<SetCoolerOffSignal>(sig => sig.DeviceUri,
    (camera, ct) => EquipmentActions.SetCoolerOffAsync(camera, ct),
    ok: "Cooler off", fail: "Cooler off failed");
```

Hold this until Tiers 1-3 land: with guards + Notify + RunTracked those handlers are already
~8 lines each, and the table only pays for itself if it needs zero escape hatches. If more than
one wired handler needs a special case, drop Tier 4 -- heterogeneity is the reason the audit
existed.

## Phasing

| Phase | Scope | Deliverable | Est. effect |
|-------|-------|-------------|-------------|
| 1 | `Notify()` + mechanical sweep of all 72 `AppendNotification` sites | one helper + sweep commit | ~-140 lines |
| 2 | `TryRequireIdle/Profile/Hub/Connected<T>` + sweep of the ~72 guard ladders | four helpers + sweep commit | ~-250 lines |
| 3 | `RunTracked()` + sweep of the 16 tracked-run blocks | one helper + sweep commit | ~-180 lines |
| 4 (optional) | `Wire<T>` registration for the trivial device-action handlers | decide after Phase 3; drop if any escape hatch is needed | ~-60 lines |
| 5 (follow-up, separate PR) | by-area partial split (`.SkyMap` / `.Equipment` / `.Session` / `.Polar`) if file size still warrants it | organizational only | file count, not lines |

Phases 1-3 are one PR (they sweep the same 42 handlers; splitting them would triple the review
of identical call sites). Each phase is its own commit so the diff reads as
helper-then-mechanical-application.

## Verification

- **Behavior-preservation is the whole game.** The sweeps are mechanical rewrites of
  notification/guard/error shapes; the risk is a dropped `NeedsRedraw`, a lost custom message,
  or an OCE that used to notify and now only logs. Mitigations:
  - Sweep with exact-match discipline: any site whose shape deviates from the idiom is left
    untouched and listed in the PR description (no forcing).
  - `grep -c AppendNotification` / `NeedsRedraw` / `tracker.Run` before/after; every removed
    occurrence must be accounted for by a helper call.
- Unit tests: the helpers themselves get direct tests (Notify stamps time + severity + redraw;
  each TryRequire notifies once and returns false on the missing-dependency case; RunTracked
  routes OCE vs Exception vs success, always runs onFinally). `GuiAppState` +
  `FakeTimeProvider` are constructible in `TianWen.Lib.Tests` (see `RouteOnlyExtractionTests`).
- Full unit + functional suites + 0-warning build (the usual bar).
- Live smoke via the DEBUG inspector: trigger a guard failure (Goto with no mount connected)
  and a tracked failure path; confirm the notification feed + redraw behave identically.

## Risks / open questions

- **Message drift:** the guard helpers introduce default messages; ~10 sites have bespoke
  wording that must be passed through the override parameter, not homogenized. The sweep rule:
  when in doubt, keep the existing string verbatim.
- **`skyMapState.NeedsRedraw` reach:** `Notify` only kicks `appState`. Sky-map handlers that
  also redraw `skyMapState` keep that line explicitly -- do not grow `Notify` into a
  redraw-everything hammer (over-redraw hides missing-redraw bugs and costs frames).
- **TUI host parity:** `AppSignalHandler` is shared between GPU and terminal hosts; the helpers
  are instance methods on it, so both hosts get them automatically. Nothing host-specific in
  any tier.
