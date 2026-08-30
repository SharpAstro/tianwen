# NINA Parity Tracker

N.I.N.A. is the reference full-featured .NET imaging suite several TianWen items exist to close
a gap against, but the comparison has never lived in one place -- it surfaces piecemeal across
`TODO.md`, `docs/todo/*.md`, and individual `docs/plans/*.md`. **This file is an INDEX, not a
duplicate.** Each row states the verdict and points at the doc that carries the real detail. When
a linked item's status changes, flip it here too; do not let this file and its target drift apart,
and do not copy paragraphs out of the pointed-to doc into this one.

Snapshot taken 2026-08-29 against the codebase at `ca107495`. Re-derive in full only when asked --
a partial refresh drifting piecemeal is exactly how the `viewer-memory-footprint` row in
[summary.md](summary.md) was found reading "NOT STARTED" long after its own phasing table said
otherwise. Don't repeat that here.

## Sequencing & automation

| Area | Status | Detail |
|---|---|---|
| Session orchestrator (init->twilight->cool->focus->AF->guide-calib->observe->flats->finalise) | DONE (different shape) | `Session.RunAsync`, CLAUDE.md "Session" section. Config/planner-driven, not NINA's drag-drop instruction list -- a deliberate difference, not a gap. |
| Flat-frame automation (calibrator + twilight sky-flats + manual panel + on-demand) | DONE | [flat-frame-automation.md](flat-frame-automation.md) |
| Multi-night progress / per-target completion ledger (NINA's Target Scheduler analogue) | NOT STARTED | [multi-night-progress.md](multi-night-progress.md) -- P0-P4 unbuilt |
| Resume an interrupted session (mosaic cut short by dew) | NOT STARTED | [docs/todo/sequencing.md](../todo/sequencing.md) L124-134 -- open question whether "resume" is a distinct verb or falls out of the ledger above once it is panel-aware |
| Discrete autofocus trigger conditions (after filter change / N exposures / temperature delta / elapsed time) | NOT STARTED | [docs/todo/sequencing.md](../todo/sequencing.md) L105-106 -- only the HFD-trend trigger exists today |
| Audit that every session exit path stops/parks/flips safely | IN PROGRESS | [docs/todo/sequencing.md](../todo/sequencing.md) L108-123 -- STOP cell fixed 2026-08-29, FLIP cell is the mount-safety-limits work below, rest of the matrix unexamined |

## Focus

| Area | Status | Detail |
|---|---|---|
| V-curve autofocus + hyperbola fit + per-target baseline | DONE | CLAUDE.md "Focus-drift refocus trigger" |
| Backlash compensation | DONE (different approach) | CLAUDE.md "Backlash Auto-Tuning" -- opportunistic per-AF inference from the verification exposure, not NINA's manual measurement routine |
| Temperature-compensated focus (predictive trigger + open-loop compensation) | NOT STARTED | [docs/todo/sequencing.md](../todo/sequencing.md) L77-78 -- coefficients already measured from the archive (5.37-5.86 steps/C, pooled/median/travel estimators agree), gating logic designed, nothing wired into `Session` |

## Guiding

| Area | Status | Detail |
|---|---|---|
| Built-in guider + PHD2, dithering, calibration, ST-4/camera pulse routing | DONE | CLAUDE.md guiding sections |
| Neural guide model refinements (pretrained model, wider/deeper MLP, real-time telemetry) | PARTIAL / ongoing | [docs/todo/guider.md](../todo/guider.md) |
| MetaGuide support (NINA has native MetaMonitor integration; TianWen has neither an external-guider listener nor the video/lucky-guiding technique itself) | NOT STARTED | [video-guiding.md](video-guiding.md) -- covers both halves: supporting MetaGuide as an external guider (UDP telemetry listener, PHD2-shaped `IGuider` may not fit) and adopting its hot-spot/rolling-average technique internally |

## Plate solving & polar alignment

| Area | Status | Detail |
|---|---|---|
| Plate solving (built-in catalog + ASTAP + astrometry.net fallback) | DONE | CLAUDE.md "Plate Solving" |
| Plate-solver performance (parity with ASTAP's ~162ms, we're ~1158ms) | PLANNED, not started | [plate-solver-performance.md](plate-solver-performance.md) |
| Polar alignment | DONE ~85% | [polar-alignment.md](polar-alignment.md) -- refraction-corrected apparent-pole overlay + rolling-median pole vector still open |

## Mount control

| Area | Status | Detail |
|---|---|---|
| Meridian flip (with oscillation/safety guards) | DONE | CLAUDE.md "Meridian-flip oscillation invariant" |
| Mount safety limits (hour-angle / altitude) | MECHANISM + SERVER-SIDE OUT-OF-SESSION ENFORCEMENT DONE, GUI + CONFIG UI + NOTIFICATION NOT WIRED | [mount-safety-limits.md](mount-safety-limits.md) -- P0/P2 shipped 2026-08-29; P3 (`MountLimitWatcher`) shipped 2026-08-30 for `tianwen-server` only (the GUI's own manual-slew path -- the scenario P3 exists for -- is not yet wired, since the GUI isn't an ASP.NET host); still no config UI and no notification-feed/Home-board surfacing when a limit fires |
| Rotator (per-OTA field rotation) | NOT STARTED | [rotator.md](rotator.md), [docs/todo/drivers.md](../todo/drivers.md) L90-106 |
| Dome (slaved to mount) | NOT STARTED | [docs/todo/drivers.md](../todo/drivers.md) L107-119 -- no dedicated plan doc yet |
| SafetyMonitor (ASCOM `ISafetyMonitorV3`) | NOT STARTED | [TODO.md](../../TODO.md) "Next Up"; [docs/todo/drivers.md](../todo/drivers.md) L120-121 |
| Alt-az mount support | PARTIAL | [altaz-mount-support.md](altaz-mount-support.md) -- Phase 0 (report-only) shipped; Phase 1 (actual goto/track) not started; Phase 3 (long-exposure imaging) blocked on the Rotator device type |

## Camera/device driver coverage

| Area | Status | Detail |
|---|---|---|
| ZWO/QHY native, Canon, ASCOM, Alpaca | DONE | CLAUDE.md solution structure + device sections |
| PlayerOne / ToupTek / SVBony native drivers | NOT STARTED | [TODO.md](../../TODO.md) L117 -- NINA's `ToupTekAlike` pattern noted as a possible shim shape to share with `ZWODeviceSource` |

## Remote / API

| Area | Status | Detail |
|---|---|---|
| ninaAPI v2 shim (Touch N Stars compatibility) + native multi-OTA hosting API | DONE, all 4 phases | [hosting-api.md](../architecture/hosting-api.md) |
| Remote rigs / multi-rig Home dashboard | DONE, P1-P5 | [remote-profile.md](remote-profile.md) -- exceeds NINA, which has no native multi-rig view |

## Image processing

| Area | Status | Detail |
|---|---|---|
| Deep-sky stacking pipeline | DONE, exceeds NINA's scope | CLAUDE.md "Deep-Sky Stacking + Enhance Pipeline" |
| AI enhancement (SAS Pro AI4 / RC-Astro / in-house N2N) | DONE | [rc-astro-enhancers.md](rc-astro-enhancers.md), [ai-denoise-deconv.md](ai-denoise-deconv.md) |
| Planetary lucky-imaging stack | DONE except native ZWO/QHY live-video capture | [planetary-stacking.md](planetary-stacking.md) -- Phase D (`DALCameraDriver` native video) not implemented |

## Deliberately NOT replicated (philosophy difference, not a gap)

- No plugin marketplace/architecture -- vendor integrations are native and in-repo instead.
- No drag-drop sequencer instruction editor -- the Session orchestrator is config/planner-driven.
- Temperature-compensated focus is designed to be host-side and measured from archive data, not
  firmware-EEPROM-driven (see the Focus row above -- "not started" is about wiring, not the design).

## Biggest remaining gaps, ranked

1. Dome + Rotator + SafetyMonitor -- three standard ASCOM device types, zero implementation.
2. Mount safety limits need a config UI, the GUI's own manual-slew enforcement (server-side
   out-of-session enforcement shipped 2026-08-30), and notification surfacing -- the mechanism
   works, nobody can turn it on from a GUI or see it fire yet.
3. Multi-night progress tracking + resume-interrupted-session.
4. PlayerOne / ToupTek / SVBony native camera support.
5. Discrete autofocus trigger conditions beyond the HFD trend.

## Maintenance rule

Update the STATUS cell here whenever a linked plan or TODO item changes state. Do not let this
file say DONE while the source doc still says NOT STARTED, or vice versa.
