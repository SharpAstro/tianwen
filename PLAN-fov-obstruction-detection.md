# Plan: Live FOV Obstruction Detection

Sub-plan of [`PLAN-first-light-resilience.md`](PLAN-first-light-resilience.md).

Goal: detect fixed FOV obstructions (tree, building, neighbour's roof) as
early as possible in a target window, decide whether they'll clear within
a useful fraction of the allocated time, and otherwise advance cleanly — so
we don't waste 40 min of a 60 min allocation trying to image through bark.

## Current state

Already in `Session.Imaging.cs`:

- Per-observation baselines (`_baselineByObservation`) of star count + HFD.
- In-flight deterioration check at `Session.Imaging.cs:665-691`: if
  `starCountRatio < Configuration.ConditionDeteriorationThreshold` (default
  `0.5f`), pause guiding, call `WaitForConditionRecoveryAsync`
  (default `ConditionRecoveryTimeout = 10 min`), resume or advance.
- `FrameMetrics.FromStarList` + `CircularBuffer<FrameMetrics>` history per OTA.

What's missing:

1. **Predictive probe** before committing to the target window. Today we
   only notice the problem after the regular imaging loop has already
   started, which means we've already paid auto-focus-on-new-target (if
   enabled), guider start, full-length exposures.
2. **Disambiguation** between obstruction (target will not clear without a
   slew) and transient conditions (thin cloud, dew — handled correctly by
   `WaitForConditionRecoveryAsync`).
3. **Trajectory-aware wait decision**: if the target's altitude/azimuth
   track will move it out of the obstruction in a fraction of remaining
   allocation, wait rather than skip.

## Design

### Phase 1 — Scout frame + compare to expected

After `CenterOnTargetAsync` succeeds and *before* the refocus /
`StartGuidingLoopAsync` block, run `ScoutAndProbeAsync(observation, ...)`:

1. Take one short exposure per OTA at `Configuration.ScoutExposure`
   (default 10 s). Run the existing star-detection pipeline; produce a
   `FrameMetrics` per OTA.
2. Compute an "expected star count" band for each OTA using:
   - Target magnitude / galactic latitude (proxy for field density), OR
   - The last successfully-imaged target's baseline if available (already
     stored in `_baselineByObservation` for the previous observation index).
3. Decision table (per OTA, then union):

   | Metric                             | Decision                     |
   |------------------------------------|------------------------------|
   | StarCount ≥ 0.7× expected          | Healthy — proceed            |
   | StarCount in 0.3×–0.7× band        | Borderline — run nudge test  |
   | StarCount < 0.3× expected          | Severe — run nudge test      |

   If any OTA is borderline/severe the **whole rig** runs the nudge test
   (single-mount invariant — we can't test per-OTA in isolation).

### Phase 2 — Altitude-nudge disambiguation

If Phase 1 flagged borderline/severe:

1. Slew the mount up in altitude by
   `Configuration.ObstructionNudgeRadii × (half-FOV of widest OTA)`. Default
   `ObstructionNudgeRadii = 1.0`.
2. Take another `ScoutExposure`-length frame per OTA.
3. Compare to the Phase 1 metrics:
   - StarCount **recovers** toward healthy band (e.g. ratio doubles) →
     classify as **obstruction** (light is being blocked by a fixed object
     at lower altitude).
   - StarCount **still bad** → classify as **transparency** (clouds, dew,
     Milky Way absorption). Hand off to existing recovery flow.
4. Always re-slew to the target's actual coordinates at the end of the
   nudge test — even if we're about to advance, leaving the mount mispointed
   is bad hygiene.

### Phase 3 — Trajectory-aware wait decision (obstruction case)

Given obstruction and the target's ephemeris:

1. Compute when (if ever) the target's altitude at the session's current
   azimuth window clears the obstruction. Proxy: nudge succeeded at
   altitude `A + nudge`, so target clears when its natural altitude
   reaches `A + nudge` along its track. Use existing ephemeris utilities.
2. Let `remainingAllocation` = `observation.EndTime - now` (or the session's
   `MaxWaitForRisingTarget` equivalent if allocation isn't bounded).
3. If `clearTime - now < Configuration.ObstructionClearFractionOfRemaining
    × remainingAllocation` (default `0.2`):
   - Sleep until `clearTime + small margin`.
   - Re-run Phase 1 scout.
   - If healthy, proceed to normal imaging; else escalate to advance.
4. Otherwise: `return ImageLoopNextAction.AdvanceToNextObservation`.
   Primary target stays in the schedule (it may be winnable on a future
   night when tree geometry is different — or via a spare swap now).

### Phase 4 — Integration with existing recovery loop (transparency case)

If nudge classified as transparency, fall through to the existing
`WaitForConditionRecoveryAsync` path. No new code — the predictive probe
has simply moved the entry to that path earlier (before we've committed
to full-length exposures) instead of discovering it after three ruined
frames.

## Touch points

New code:

- `Session.Imaging.Obstruction.cs` — new partial: `ScoutAndProbeAsync`,
  `NudgeTestAsync`, `EstimateClearTimeAsync`, helpers. Keeping it out of the
  already-large `Session.Imaging.cs` for readability.
- `SessionConfiguration` additions (with sensible defaults):
  - `TimeSpan ScoutExposure = TimeSpan.FromSeconds(10)`
  - `float ObstructionStarCountRatioHealthy = 0.7f`
  - `float ObstructionStarCountRatioSevere = 0.3f`
  - `float ObstructionNudgeRadii = 1.0f`
  - `float ObstructionClearFractionOfRemaining = 0.2f`
  - `bool SaveScoutFrames = false` (default discard; on for debugging)

Edits:

- `Session.Imaging.cs` `ObservationLoopAsync`: insert scout call between
  `CenterOnTargetAsync` (line ~107) and `StartGuidingLoopAsync` (line 124).
  Branch on the returned decision.
- `ISession` / session event surface: consider emitting a new
  `ScoutCompletedEventArgs` so the live-session UI can show the scout
  frame + decision. Optional for v1; can ship without.
- Tests in `TianWen.Lib.Tests.Functional` — extend `SessionTestHelper` with
  a scriptable star-count oracle per observation index so unit tests can
  drive the decision table without a real image pipeline.

## New data structures

```csharp
internal readonly record struct ScoutResult(
    FrameMetrics[] Metrics,
    ScoutClassification Classification,
    TimeSpan? EstimatedClearIn);

internal enum ScoutClassification
{
    Healthy,
    Transparency,   // clouds / dew — goes to WaitForConditionRecoveryAsync
    Obstruction,    // fixed FOV block — goes to trajectory check
}
```

## Risks

- **Expected-star-count model is hard.** The Phase 1 band needs a proxy for
  "what StarCount should this FOV give". Options, cheapest first:
  - Use last-target baseline only (no model). Fragile if last target was a
    very different field (M42 vs. a thin high-gal-lat blank).
  - Cache StarCount of successful targets per OTA across sessions and key
    by (peak magnitude, gal-lat quintile). Start collecting now, use later.
  - Full per-target "expected" derived from the catalog star distribution.
    Over-engineered for v1.
  - **Start with last-target baseline, flag in the plan to revisit.**
- **Nudge slew cost.** 1× FOV-radius slew + settle + scout exposure is
  ~20-40 s — acceptable once per target, not acceptable in a loop. The
  decision must be definitive (no "nudge again").
- **Guider state during scout.** Guider should stay stopped through the
  scout/nudge sequence — we already `StopCaptureAsync` before the slew, so
  the current flow already has the guider off when we'd insert the scout.
- **Meridian flip interaction.** Scout frames use the mount's current
  pier side; don't trigger a flip during the nudge. Easy — just don't
  cross HA=0 during the up-offset.

## Open questions

- Should `ScoutExposure` be per-OTA or global? Single-mount invariant says
  all OTAs shoot simultaneously, but per-OTA gain/filter means a 10 s LUM
  frame is not the same quality as a 10 s Ha frame. Probably global with
  longest-filter dominance; revisit.
- If sub-plan B (driver resilience) isn't in yet, a scout-frame failure
  (exposure error) could still kill the session. Gate this plan's merge
  on B, or wrap the scout calls in explicit try/catch for now.
- Where does the "3× FOV radius" boundary live for choosing nudge
  direction (up vs. cardinal)? Up is the safest default (obstructions are
  almost always tree/building below); keep v1 up-only.

## Out of scope

- **Static azimuth horizon mask** — that's a separate optional sub-plan
  (`PLAN-site-horizon-mask.md` if we spin it up). This plan handles *unknown*
  obstructions at runtime; the mask handles *known* ones at schedule time.
- **Per-pixel obstruction mapping** (learning the exact shape of the tree
  line from accumulated scout failures). Future work; requires a persisted
  map structure.

## Memory updates after landing

Replace the content of `project_fov_obstruction_detection.md` with a pointer
to this PLAN doc (two-line "designed in `PLAN-fov-obstruction-detection.md`;
status: <phase>"). Don't double-source design.
