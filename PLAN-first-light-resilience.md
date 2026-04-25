# Plan of Plans: First-Light Failure Resilience

Meta-plan coordinating the work needed before the Session implementation is
trustworthy on a real clear-sky night with unattended operation.

Design detail lives in the sub-plans. This doc only owns: **what's in scope,
what isn't, and in what order we tackle it**.

## Sub-plans

1. [`PLAN-driver-resilience.md`](PLAN-driver-resilience.md) — per-call
   reconnect + retry + fault-counter escalation. Wraps hot-path driver
   calls so a USB hiccup, COM fault, or TCP drop doesn't end the session.
   **Status: SHIPPED (merged to main).** See [`ARCH-driver-resilience.md`](ARCH-driver-resilience.md).
2. [`PLAN-fov-obstruction-detection.md`](PLAN-fov-obstruction-detection.md) —
   predictive scout frame + altitude-nudge disambiguation + trajectory-aware
   wait decision, so a target behind a tree is detected fast and either
   waited out or skipped cleanly. **Status: SHIPPED on branch
   `fov-obstruction-detection`.** See [`ARCH-fov-obstruction.md`](ARCH-fov-obstruction.md).
   **Known gaps not closed by v1** (detail in the plan + arch doc):
   - First observation of the night returns `Healthy` unconditionally
     (no prior baseline to compare against).
   - Guider calibration slew (`Session.Lifecycle.cs:19`) is not scouted —
     tracked as TODO L147.
   - Guider field is not exposured separately (imaging-OTA only).
3. *(optional backlog)* `PLAN-site-horizon-mask.md` — static per-azimuth
   horizon profile so the scheduler can pre-reject known-obstructed targets
   at plan-build time instead of rediscovering them at runtime. Only spin
   this up if sub-plans 1+2 in production still show too many runtime scout
   trips against known obstructions. The first-observation-of-the-night gap
   in sub-plan 2 is one of the strongest arguments for actually shipping this:
   a static horizon mask catches obstructions *before* the night begins, so it
   doesn't need a prior baseline.

## Goal

Failure resilience — *"a single bad event should not end the session"*. The
two classes of bad event that matter:

- **Environmental** — fixed FOV obstruction (tree, building, neighbour's
  roof) or localised cloud blocks the current target for some or all of
  the allocated window. Addressed by sub-plan 2.
- **Equipment** — USB cable glitches, mount loses serial handshake, guider
  WS disconnects, ASCOM COM throws. Addressed by sub-plan 1.

## Non-goals (explicit)

- **Non-linear scheduling.** `Session.ObservationLoopAsync` is deliberately
  sequential: one target at a time, order determined by `ObservationScheduler`
  at schedule-build time. Re-ranking or branching mid-session is out of
  scope — the user has confirmed the current model is by design. Every new
  resilience behaviour must compose with linear execution (skip / advance /
  repeat), not replace it.
- **Multi-target concurrent OTAs.** See `CLAUDE.md` -> "Single-mount /
  multi-OTA invariant". All OTAs share pointing, so any "branch" decision
  applies to the full OTA set as a unit.
- **Silent retry of dead hardware.** Resilience has to terminate. The
  fault-counter + escalation path in sub-plan 1 is the explicit boundary.

## Prior art (what's already in the runtime)

Before adding anything, the baseline the sub-plans build on:

- Flat altitude gate (`Configuration.MinHeightAboveHorizon`).
- Rising-target wait (`EstimateTimeUntilTargetRisesAsync` +
  `MaxWaitForRisingTarget`).
- Spare fallback (`Observations.TryGetNextSpare`).
- In-flight deterioration detection + `WaitForConditionRecoveryAsync`
  (`Session.Imaging.cs:665-691`, default `ConditionRecoveryTimeout = 10 min`).
- Focus drift detection via HFD regression on per-observation baselines.
- `LoggerCatchExtensions.CatchAsync` — single-shot swallow-and-log, used
  for telemetry in `PollDeviceStatesAsync`.

Sub-plan 2 feeds into the existing `WaitForConditionRecoveryAsync` rather
than replacing it; sub-plan 1 generalises the `CatchAsync` pattern into a
reconnecting wrapper.

## Sequencing

Ship in order:

1. **Sub-plan 1 (driver resilience)** first. Highest ROI per hour of work,
   and sub-plan 2's scout frame itself depends on driver calls being
   resilient. Phased as PR-B1..B5 inside its doc; sub-plan 2 can start
   once PR-B1..B3 are in.
2. **Sub-plan 2 (FOV obstruction detection)** second. Predicated on a
   resilient imaging pipeline under it.
3. **Sub-plan 3 (site horizon mask)** only if warranted by operational
   experience after 1+2.

## Cross-cutting conventions

Apply across all sub-plans:

- Use `ITimeProvider.SleepAsync` everywhere — never `Task.Delay(..., tp, ct)`
  (memory: `FakeTimeProvider` test compatibility).
- Every new driver-call wrapper and every new scout path gets a logger
  scope with `["Device"]` / `["Phase"]` so SEQ queries work.
- New `SessionConfiguration` fields land with sensible defaults and full
  XML docs; no "please configure before using" behaviour.
- Tests for new Session-level behaviour use the cooperative time-pump
  pattern (memory: `ExternalTimePump = true` + `Advance` loop) — never
  `SleepAsync(subExposure)` inside a pump loop.

## Memory trail

After each sub-plan ships, the corresponding memory entry becomes a
**pointer** to the PLAN doc, not a duplicate of the design:

- `project_fov_obstruction_detection.md` -> `PLAN-fov-obstruction-detection.md`
- New `project_driver_resilience.md` -> `PLAN-driver-resilience.md`
- New `project_first_light_resilience.md` -> this meta-plan (trail map for
  future sessions)
