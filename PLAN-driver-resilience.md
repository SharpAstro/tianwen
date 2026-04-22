# Plan: Driver-Level Resilience (Reconnect + Retry)

Sub-plan of [`PLAN-first-light-resilience.md`](PLAN-first-light-resilience.md).

Goal: a single dropped USB cable, COM glitch, or TCP disconnect must **not**
end the session. Today every driver call in the imaging hot path is a naked
`await` — the first exception bubbles to `Session.RunAsync`'s outer catch,
`SessionPhase.Failed` gets set, and we finalise.

This is the highest-priority first-light-readiness item. Ship it before the
FOV work because the FOV scout frame itself depends on these calls being
resilient.

## Current state

What we have:

- `IDeviceDriver.Connected` (atomic int gate at `DeviceDriverBase.cs:50`).
- `IDeviceDriver.ConnectAsync` / `DisconnectAsync` (cheap, idempotent — both
  funnel through `SetConnectionStateAsync`).
- `LoggerCatchExtensions.CatchAsync(...)` — catches-and-logs a single
  call, returns a default value. Used in `Session.PollDeviceStatesAsync`
  for telemetry reads. No retry, no reconnect.
- `Session.RunAsync`'s outer `catch (Exception)` that sets `Failed`.

What's missing:

- A retry/backoff wrapper that distinguishes **idempotent** reads (safe to
  repeat) from **non-idempotent** actions (`StartExposureAsync`,
  dither, slew command issue).
- A reconnect step between retries when `Connected == false`.
- A per-device fault counter so repeated transient faults escalate before
  the session silently grinds.
- An explicit "in-flight exposure invalidated by reconnect" state — today
  the camera-driver contract is undefined if the USB drops mid-exposure.

## Design

### Phase 1 — `ResilientCall` helper

New file `TianWen.Lib/Sequencing/ResilientCall.cs`. Central primitive:

```csharp
internal static class ResilientCall
{
    public static ValueTask<T> InvokeAsync<T>(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask<T>> op,
        ResilientCallOptions options,
        ILogger logger,
        CancellationToken cancellationToken);

    public static ValueTask InvokeAsync(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask> op,
        ResilientCallOptions options,
        ILogger logger,
        CancellationToken cancellationToken);
}

internal readonly record struct ResilientCallOptions(
    int MaxAttempts,
    TimeSpan InitialBackoff,
    double BackoffMultiplier,
    bool IsIdempotent,
    string OperationName)
{
    public static readonly ResilientCallOptions IdempotentRead = new(
        MaxAttempts: 3, InitialBackoff: TimeSpan.FromMilliseconds(250),
        BackoffMultiplier: 3.0, IsIdempotent: true, OperationName: "read");

    public static readonly ResilientCallOptions NonIdempotentAction = new(
        MaxAttempts: 1, InitialBackoff: TimeSpan.Zero,
        BackoffMultiplier: 1.0, IsIdempotent: false, OperationName: "action");
}
```

Semantics:

1. Call `op(ct)`. If it succeeds, return.
2. On transient exception (IO / SerialPort / TCP / ObjectDisposed /
   specific ASCOM COM HRESULTs), if `IsIdempotent` and we have attempts
   left:
   - Wait `InitialBackoff × (BackoffMultiplier^(attempt-1))`.
   - If `driver.Connected == false`, call `driver.ConnectAsync(ct)`.
     Swallow its exception but count the reconnect as one attempt.
   - Retry `op`.
3. On `OperationCanceledException` when `ct` fired, rethrow immediately.
4. On exhausted attempts or non-idempotent failure, rethrow the last
   exception — the caller decides the session-level response.

### Phase 2 — Hot-path audit

Wrap the driver calls in `Session.Imaging.cs` and `Session.Focus.cs`:

| Call site (approx) | Call | Idempotency |
|--------------------|------|-------------|
| `Session.Imaging.cs:50,66,79` | `mount.Driver.BeginSlewToTargetAsync` | **Non-idempotent** — issuing twice could cancel the first and re-queue. Wrap with `NonIdempotentAction` (no retry) but pre-call `EnsureConnectedAsync`. |
| `Session.Imaging.cs:95` | `WaitForSlewCompleteAsync` | Idempotent (polls). `IdempotentRead`. |
| `Session.Imaging.cs:104` | `GetHourAngleAsync` | `IdempotentRead`. |
| `Session.Imaging.cs:377` | `camera.Driver.StartExposureAsync` | **Non-idempotent critical** — retry requires explicit state handling, see Phase 3. |
| `Session.Imaging.cs:417,543,1049` | `camDriver.GetImageAsync` | Idempotent-with-caveat: reading the buffer twice is fine, but if the camera dropped the exposure, this returns empty. Wrap with `IdempotentRead` and treat empty-image as "exposure invalidated". |
| `Session.Focus.cs` focuser calls | `MoveToAsync`, `GetPositionAsync` | Position read = idempotent. `MoveToAsync` = non-idempotent but targets absolute coordinates, so retry after reconnect is actually safe — special-case with `MaxAttempts=2`. |
| Filter wheel `SetPositionAsync` | | Absolute target, same reasoning as focuser move. `MaxAttempts=2`. |
| Guider start / pause / unpause | | Guider has its own retry surface (`GuidingTries`). Wrap only the outer `StartGuidingLoopAsync` and `DitherWaitAsync` with `NonIdempotentAction`. |

Every wrapped call uses a logger scope with `["Device"] = driver.Name` so
reconnect attempts are greppable.

### Phase 3 — In-flight exposure handling

The nasty case: USB drops during a 300 s exposure.

1. `StartExposureAsync` returned success; image is counting down in the
   camera.
2. USB disconnects; `GetImageAsync` throws or returns empty.
3. Driver fault counter increments; `ResilientCall` reconnects.
4. Camera state post-reconnect is **device-specific** — ZWO / QHY / ASCOM
   all differ. Spec: a reconnect during an active exposure invalidates
   the in-flight frame. The imaging loop treats `GetImageAsync` returning
   empty-after-reconnect as a "lost frame" event:
   - Log at Warning, not Error.
   - Do **not** count towards `observation.TotalFramesRequired`.
   - Re-issue `StartExposureAsync` with the same parameters (same filter,
     gain, exposure) and continue.
5. If two consecutive frames are lost, trip the per-device fault counter
   and propagate via the new `ImageLoopNextAction.DeviceUnrecoverable`
   path (see Phase 4).

### Phase 4 — Escalation boundary

Resilience must terminate — "USB bump survives, dead mount doesn't pretend
to be alive".

1. Each driver gets a `faultCount` accumulator in the session (not in the
   driver — session-scoped). `ResilientCall` increments it on every
   reconnect attempt.
2. Config: `SessionConfiguration.DeviceFaultEscalationThreshold = 5`.
3. When any driver crosses the threshold, the session pauses imaging,
   logs a summary, and either:
   - Runs a longer reconnect (e.g. `ConnectAsync` with a 30 s timeout),
     resets the counter, and retries. — OR —
   - Returns `ImageLoopNextAction.DeviceUnrecoverable`, setting
     `SessionPhase.Failed` cleanly (finalise still runs).
4. The counter decays on sustained healthy operation (e.g. `-1` every 10
   successful frames) so a bad hour on Tuesday doesn't poison Wednesday's
   session.

### Phase 5 — Telemetry poll proactive reconnect

`Session.PollDeviceStatesAsync` already catches-and-swallows telemetry
read failures via `CatchAsync`. Add: if three consecutive polls for the
same device return default (i.e. failed), proactively call
`ResilientCall.InvokeAsync(driver, driver.ConnectAsync, ...)` so by the
time the next exposure tries to start, reconnect is already in progress.

## Touch points

New:
- `src/TianWen.Lib/Sequencing/ResilientCall.cs`.
- `src/TianWen.Lib/Sequencing/ResilientCallOptions.cs`.
- Test file `src/TianWen.Lib.Tests/ResilientCallTests.cs` with a
  `FakeFlakyDriver` that can inject N consecutive failures.

Edited:
- `Session.Imaging.cs` — every driver call in the hot path (see audit table).
- `Session.Focus.cs` — focuser move + read wrappers.
- `Session.cs` — fault-counter dictionary, decay logic, escalation check in
  `RunAsync`'s outer try/catch.
- `SessionConfiguration.cs` — new fields.
- `ImagingLoopResult.cs` — new `DeviceUnrecoverable` variant.
- `Session.PollDeviceStatesAsync` — proactive reconnect hook.

## Risks

- **Double-exposure bug.** If `StartExposureAsync` succeeds but the ACK
  is lost, retry would double-expose. The audit table already marks it
  `NonIdempotentAction` with `MaxAttempts=1`; the resilience for exposure
  lives in the `GetImageAsync` + re-issue flow (Phase 3), not in retrying
  `StartExposureAsync`.
- **ASCOM COM quirks.** Recent upstream commit `77d9dcc` ("ASCOM: contain
  COM throws so a dead hub can't fail-fast the process") suggests COM
  drivers can throw in surprising ways. `ResilientCall`'s transient-
  exception filter needs to include the specific HRESULTs documented in
  `AscomDeviceDriverBase`'s containment logic.
- **Mount slew queueing.** If the mount is mid-slew and loses connection,
  `BeginSlewToTargetAsync` on reconnect may queue behind the original.
  Before reissuing a slew, call `AbortSlew` first (most drivers have it).
  Document the pattern in `ResilientCall`'s slew-specific helper.
- **Reconnect storms.** Two flaky devices could retrigger each other.
  Fault counter + escalation threshold is the safety valve.

## Open questions

- **Does ZWOptical.SDK re-enumerate the camera on USB re-plug with the
  same device id?** If not, reconnect after a real re-plug needs to
  re-resolve via `IDeviceUriRegistry`, which is a bigger change than this
  plan describes. Verify on real hardware before sub-plan A merges.
- **Guider-specific reconnect.** PHD2 exposes a WS; `TcpSerialConnection`
  doesn't own the guider connection (it's a separate `IGuiderDriver`).
  Does that driver already have its own reconnect? If yes, skip Phase 4
  escalation for the guider; if no, add to the audit list.
- **FakeTimeProvider interaction.** Retries use `ITimeProvider.SleepAsync`
  so tests can advance fake time — confirm `Task.Delay(duration,
  timeProvider, ct)` is NOT used anywhere in the new code (memory:
  `SleepAsync` is mandatory for testability).

## Phasing / PR breakdown

Each phase can be a standalone PR:

1. **PR-B1**: `ResilientCall` + `ResilientCallOptions` + unit tests.
   Zero behavioural change; library pieces only.
2. **PR-B2**: wrap idempotent reads in `Session.Imaging` + `Session.Focus`.
   Low-risk; audit table rows marked `IdempotentRead`.
3. **PR-B3**: wrap non-idempotent actions (slew, exposure start, dither)
   with the `AbortSlew`-first pattern for mount and lost-frame flow for
   camera.
4. **PR-B4**: fault-counter + escalation path (`DeviceUnrecoverable`).
5. **PR-B5**: proactive reconnect from `PollDeviceStatesAsync`.

Gate sub-plan A's merge on at least PR-B1..B3 being in.

## Memory updates after landing

Add `project_driver_resilience.md` with a two-line pointer to this plan
and the PR list. Update `CLAUDE.md`'s Session section to mention
`ResilientCall` as the expected wrapper for hot-path driver calls so
future work doesn't regress by adding a naked `await mount.Driver.*`.
