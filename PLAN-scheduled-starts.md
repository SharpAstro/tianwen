# Plan: Honor ScheduledObservation.Start in the Observation Loop

Status: NOT STARTED. Authored 2026-06-12 for hand-off; all file/line facts verified against main @ `ae85cd8`.

Goal: `ObservationLoopAsync` currently iterates the schedule linearly and starts each
target the moment the previous one finishes - `ScheduledObservation.Start` is computed
by the scheduler but never consumed. Make the loop wait until a future scheduled start
before slewing, so the scheduler's altitude-optimised slot allocation actually happens
at the allocated times. TODO.md (Observation Scheduler / Not Yet Done): "Integrate
scheduler into Session.RunAsync flow - currently ObservationLoopAsync iterates linearly;
needs to respect ScheduledObservation.Start times (wait until scheduled start before slewing)".

## Current state (verified facts)

- `ScheduledObservation` (`src/TianWen.Lib/Sequencing/ScheduledObservation.cs`):
  `record ScheduledObservation(Target Target, DateTimeOffset Start, TimeSpan Duration, bool AcrossMeridian, ImmutableArray<FilterExposure> FilterPlan, int? Gain, int? Offset, ObservationPriority Priority = Normal)`.
- `Start` is populated by `ObservationScheduler.Schedule` as
  `astroDark + TimeBinDuration * bestStartBin` (ObservationScheduler.cs:325; 30-min bins,
  contiguous blocks, **gaps between observations are possible** when no contiguous block
  fits at the preferred position). Spares inherit their primary slot's Start/Duration.
- **The only production reader of `Start`** is
  `WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync` (`Session.Timing.cs:25-54`),
  which reads `Observations[0].Start` to time pre-session cooling
  (`waitUntil = firstStart - 10 min`, then `_timeProvider.SleepAsync(diff, ct)`).
- `ObservationLoopAsync` (`src/TianWen.Lib/Sequencing/Session.Imaging.cs:20-216`):
  - Loop condition (lines 28-31): `ActiveObservation is not null && GetMountUtcNowAsync() < sessionEndTime && !ct.IsCancellationRequested`.
  - `sessionEndTime` = next morning astronomical twilight (`SessionEndTimeAsync`, Session.Timing.cs:56-76).
  - Slew at line 55 (`BeginSlewToTargetAsync`); below-horizon rising-target wait at
    lines 57-75 (`EstimateTimeUntilTargetRisesAsync` then `_timeProvider.SleepAsync(waitTime, ct)`);
    spare fallback at lines 79-101 (`Observations.TryGetNextSpare(_activeObservation, ref _spareIndex)`).
  - Advancing: `AdvanceObservation()` = `Interlocked.Increment(ref _activeObservation)` + `_spareIndex = 0` (Session.cs:186).
  - After `ImagingLoopAsync`: `AdvanceToNextObservation` -> advance + continue;
    `RepeatCurrentObservation` -> continue; `DeviceUnrecoverable`/other -> break (lines 193-214).
- `ImagingLoopAsync` bounds its own runtime via `maxTicks = observation.Duration / tickSec`
  (Session.Imaging.cs:357, check at 661-669) - Duration is enforced, Start is not.
- Hosted API `/session/start` (`src/TianWen.Hosting/Api/SessionEndpoints.cs:96-106`)
  stamps **all** pending targets with `Start = timeProvider.GetUtcNow()` - i.e. every
  hosted observation's Start is already in the past by the time the loop sees it.
- Functional tests (`src/TianWen.Lib.Tests.Functional/SessionObservationLoopTests.cs`):
  all existing tests pass identical `Start = WinterNightStart` to every observation.
  Harness: `CreateWinterSessionAsync(observations)` -> `AdvanceObservationForTest()` ->
  `RunObservationLoopWithTimePumpAsync` (ExternalTimePump=true, `Task.Run` the loop,
  `WaitForFirstWaiterAsync`, pump `Advance(5s)` up to 24h fake time).

## Design

### Where the wait goes

At the **top of the observation loop body**, after `observation` is resolved and before
the slew attempt (i.e. before Session.Imaging.cs line 53):

```csharp
var waitOutcome = await WaitForScheduledStartAsync(observation, sessionEndTime, cancellationToken);
if (waitOutcome == ScheduledStartOutcome.SessionEnded) break;   // start is beyond session end
// Proceed/StartedLate both fall through to the slew
```

Waiting before the slew (not after) is deliberate: slewing 2 hours early and letting the
mount track on-target wastes nothing but does run the RA worm toward the meridian and,
worse, the subsequent `CenterOnTargetAsync` + refocus + guider start would also run
early and then go stale. Slew lead time (slew + centering + guider settle, typically
1-3 min) is handled with a configurable lead so imaging starts close to `Start`.

### `WaitForScheduledStartAsync` (new, Session.Timing.cs)

```csharp
internal enum ScheduledStartOutcome { Proceed, StartedLate, SessionEnded }

internal async ValueTask<ScheduledStartOutcome> WaitForScheduledStartAsync(
    ScheduledObservation observation, DateTimeOffset sessionEndTime, CancellationToken ct)
```

Behavior:

1. `lead = Configuration.ScheduledStartLeadTime` (new knob, default 3 min - covers
   slew + center + guider start so the first light frame lands near `Start`).
2. `waitUntil = observation.Start - lead`; `now = await GetMountUtcNowAsync()` (same
   clock the loop condition uses - do not mix `_timeProvider.GetUtcNow()` with mount
   UTC here).
3. `now >= waitUntil` -> return `Proceed` immediately (also covers `Start` in the past:
   the hosted API path and all existing tests hit this branch, so **current behavior is
   preserved exactly** for same-Start schedules).
4. `waitUntil >= sessionEndTime` -> log warning, return `SessionEnded` (loop breaks;
   the existing loop-condition guard would otherwise spin a pointless slew).
5. Else wait: sleep in chunks of `min(remaining, 1 min)` via `_timeProvider.SleepAsync`
   re-reading the clock each iteration (chunked so cancellation is responsive and so
   the fake-time pump in tests advances it naturally). Log once at start:
   "Waiting {Wait} until scheduled start {Start:o} of {Target}".
6. If on wake `now > observation.Start + observation.Duration` (we somehow overslept the
   whole slot - only possible with a pathological clock) -> log warning, return
   `StartedLate`; v1 still proceeds (see policy below).

Late-start policy (v1): when `now > Start` on entry (behind schedule because the
previous target overran or recovery waits piled up), log an information line with the
lateness and proceed without clamping. `ImagingLoopAsync` still runs the full
`Duration`, which pushes subsequent targets later - acceptable v1 semantics, matches
today's behavior, and the scheduler's slot gaps absorb small slips. A
`ClampLateStartToSlot` knob is listed as out of scope.

### Spares

Spares inherit the primary's Start, and the spare path only triggers after a failed
slew of the primary (i.e. after the wait already happened). No extra wait for spares -
verify no second `WaitForScheduledStartAsync` call sneaks into the spare branch.

### Config

`SessionConfiguration` (positional record struct, append trailing params):

- `TimeSpan ScheduledStartLeadTime = default` -> treat `default` as 3 min via a
  property or normalise in ctor-consuming code (follow the existing
  `MaxWaitForRisingTarget ?? TimeSpan.FromMinutes(15)` pattern at Session.Imaging.cs:59:
  make it `TimeSpan? ScheduledStartLeadTime = null` and `?? TimeSpan.FromMinutes(3)` at
  the use site).
- No enable/disable flag needed: schedules with all-equal or past Starts (hosted API,
  legacy) short-circuit at step 3.

### Interaction with the pre-session cooling wait

`WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync` already waits until
`Observations[0].Start - 10 min` before cooling/focus/calibration, so for observation 0
the new wait typically resolves to <= ~7 min (10 min head start minus focus/calibration
time minus 3 min lead) - correct and harmless. Mention in the log line which phase is
waiting so session logs stay legible.

## Phases

| Phase | Work | Est. |
|------:|------|------|
| 1 | `WaitForScheduledStartAsync` + `ScheduledStartOutcome` in Session.Timing.cs + `ScheduledStartLeadTime` knob | S |
| 2 | Call site at top of `ObservationLoopAsync` body + log lines | S |
| 3 | Functional tests (below) | M |
| 4 | Docs: CLAUDE.md Session section sentence + TODO.md tick + PLAN-summary row | S |

## Tests (`SessionObservationLoopTests` harness, `[Collection("Session")]`)

All tests reuse `CreateWinterSessionAsync` + `RunObservationLoopWithTimePumpAsync`
(ExternalTimePump pattern - see feedback_external_time_pump_for_loops; never
`SleepAsync` in the pump loop).

1. `GivenSecondObservationStartsLaterWhenLoopRunsThenImagingWaitsForScheduledStart` -
   two visible targets; obs[1].Start = WinterNightStart + 2h. Assert: frames for
   obs[1] only after fake-clock >= obs[1].Start - lead (capture `FrameWritten` event
   timestamps or assert via `CurrentObservationIndex` progression against pumped time);
   total frames > 0 for both.
2. `GivenAllObservationsSameStartWhenLoopRunsThenBehaviorUnchanged` - regression twin
   of the existing three-targets test: identical Starts, assert no added wall-clock
   waiting (loop completes within the same pumped-time budget the existing test uses).
3. `GivenStartBeyondSessionEndWhenLoopRunsThenObservationSkippedCleanly` -
   obs[1].Start past morning twilight: loop ends after obs[0], no slew attempt for
   obs[1] (assert via phase events or frame counts), session completes.
4. `GivenLoopBehindScheduleWhenStartInPastThenProceedsImmediatelyAndLogsLateStart` -
   obs[1].Start 1h before obs[0] finishes: no wait inserted, frames still produced.
5. Cancellation: cancel during the wait -> loop exits promptly, `Finalise` runs
   (extend test 1 with a cancel at a pumped timestamp inside the wait window). Remember
   feedback_log_oce_anyway if any catch is added.

Run: `cd src && dotnet build`, then unit suite, then
`dotnet test TianWen.Lib.Tests.Functional --no-build --filter "FullyQualifiedName~SessionObservationLoop"`
(never unit + functional in parallel).

## Out of scope

- `ClampLateStartToSlot` (shrink Duration when starting late so the next slot is honored).
- Re-running `ObservationScheduler.Schedule` mid-session when drift accumulates
  (re-planning is the "Multi-night scheduling" item's territory).
- Hosted API calling `ObservationScheduler.Schedule` on `/session/start` instead of
  stamping `Start = now` (separate API work item; this plan keeps that path no-wait).
- Parking/tracking policy during long gaps (>30 min idle between slots: today the mount
  keeps tracking the old target; stopping tracking during gaps is a follow-up knob).
