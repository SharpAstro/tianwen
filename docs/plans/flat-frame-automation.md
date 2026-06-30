# Flat-frame acquisition (automation)

Automated flat capture so a fully unattended session no longer needs a manual flats step.
`FrameType.Flat`, the cover/calibrator device (`ICoverDriver`), and the stacking calibrator
(`MasterFrameBuilder`) already existed; what was missing was the *capture* routine.

## Architecture

| Piece | Location | Role |
|-------|----------|------|
| `FlatExposureSolver` (pure) | `TianWen.Lib/Imaging/Calibration/` | Auto-exposure convergence: linear-panel-model bracketing toward a target ADU fraction. `Capture` / `Adjust` / `Fail`. Side-effect-free, unit-tested. |
| `Session.TakeFlatsAsync` (+ helpers) | `TianWen.Lib/Sequencing/Session.Flats.cs` | Per-OTA orchestration. Reuses `MoveTelescopeCoversToStateAsync`, `SwitchFilterIfNeededAsync`, `CameraExposureActions.StampDenormAsync`, `ResilientInvokeAsync`, and the FITS writer. |
| Config knobs | `SessionConfiguration` | `TakeFlatsOnSessionEnd`, `FlatTargetAduFraction` (0.5), `FlatAduTolerance` (0.05), `FlatMaxBrackets` (6), `FlatsPerFilter` (15), `FlatInitialExposure`/`FlatMinExposure`/`FlatMaxExposure`, `FlatCalibratorBrightnessPercent` (50). |
| `SessionPhase.Flats` | `SessionPhase` | New phase ("Taking Flats" UI label). |

## Capture flow (Phase 1 -- panel/calibrator flats)

Runs in `RunAsync` after `ObservationLoopAsync` on **normal completion only** (an abort/exception
skips straight to `Finalise`), and **before** `Finalise` warms the cameras -- so flats are taken at
the imaging setpoint temperature. Gated on `TakeFlatsOnSessionEnd`. The same `TakeFlatsAsync` is the
on-demand entry point a future CLI/API surface calls.

Per OTA:
1. Close the cover (`MoveTelescopeCoversToStateAsync(Closed)`; `NotPresent` covers are a no-op).
2. Gate on a controllable calibrator panel (`GetCalibratorStateAsync != NotPresent`); else **skip with a
   warning**. Turn the panel on at `FlatCalibratorBrightnessPercent` of `MaxBrightness` and poll until `Ready`.
3. For each installed filter (or a single no-filter pass): switch + settle, stamp denorm, then run the
   auto-exposure loop (metering frames are measured + discarded) until `FlatExposureSolver` returns
   `Capture`; shoot `FlatsPerFilter` frames at the converged exposure.
4. Turn the calibrator off (cover left closed for `Finalise`).

**Output contract:** frames carry `IMAGETYP/FRAMETYP=Flat` plus the same denormalised metadata as
lights (filter, `CCD-TEMP`, gain, offset, binning, dimensions, sensor), written under
`Flats/<date>/<filter>/Flat/`. The path is cosmetic -- `StackingPipeline.ScanDataRoot` discovers them
anywhere under `DataRoot` and `MasterFrameBuilder` groups by `MasterGroupKey` (filter included). No
extra stacker wiring.

**Supported hardware:** any OTA whose `ICoverDriver` exposes a calibrator -- a flip-flat (motorised
cover *with* built-in panel: close then illuminate) **or** a standalone lightbox / fixed panel (panel
on; the close step is a graceful no-op). A motorised cover with **no** panel, or an OTA with no flat
device, is skipped.

## Phasing

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | Panel/calibrator flats: `FlatExposureSolver` + `TakeFlatsAsync` + config + `SessionPhase.Flats` + end-of-session hook + tests | **DONE** |
| 2 | Twilight **sky-flats**: slew to the anti-solar zenith, ramp exposure as sky brightness changes, twilight-window timing. The solver is reused per-frame; the extra work is pointing + timing. | NOT STARTED |
| 3 | **On-demand surface** (CLI `tianwen flats` + `POST /api/v1/session/flats`) + **manual flat-panel mode**. See below. | NOT STARTED |

## Deferred: manual flat-panel mode (out-of-session)

For a **manual** flat panel (a dumb EL panel the user switches on by hand -- no ASCOM/Alpaca
control), capture is **out of session**: it does not belong in the unattended end-of-session hook
(there is no human to switch the panel on, and the automated path requires a controllable
calibrator). It belongs on the **on-demand surface** (Phase 3), where the user explicitly starts a
flat run.

UI shape (when built): a **dropdown** to pick the flat illumination source -- a **light-bulb (light
bulb / 💡)** entry for the manual panel alongside the auto calibrator / sky-flat options. In manual
mode the routine skips all cover/calibrator hardware control and just runs the auto-exposure +
capture against whatever light the user has arranged; misconfigured illumination simply fails the
solver gracefully ("too dim at max exposure"). A fully interactive "switch the panel on, press
Continue" prompt would also live here, not in the unattended session.

## Tests

- `FlatExposureSolverTests` (12) -- convergence under the linear panel model, both clamp directions,
  every fail mode (too-bright-at-min / too-dim-at-max / out-of-brackets), and the near-zero
  divide-by-zero guard.
- `SessionFlatsTests` (2) -- orchestration: 4 filters x N flats written into `Flat` frame-type folders,
  calibrator off + cover closed afterwards; and the no-calibrator skip writes nothing.
