# Flat-frame acquisition (automation)

Automated flat capture so a fully unattended session no longer needs a manual flats step.
`FrameType.Flat`, the cover/calibrator device (`ICoverDriver`), and the stacking calibrator
(`MasterFrameBuilder`) already existed; what was missing was the *capture* routine.

## Architecture

| Piece | Location | Role |
|-------|----------|------|
| `FlatExposureSolver` (pure) | `TianWen.Lib/Imaging/Calibration/` | Panel auto-exposure convergence: linear-panel-model bracketing toward a target ADU fraction. `Capture` / `Adjust` / `Fail`. Side-effect-free, unit-tested. |
| `SkyFlatExposureSolver` (pure) | `TianWen.Lib/Imaging/Calibration/` | Twilight sky-flat per-frame decision. Wraps `FlatExposureSolver` and adds twilight-direction awareness: `Capture` (with a re-centred next exposure for the drifting sky) / `Adjust` / `Wait` (sky ramping toward target) / `Stop` (window closed). Side-effect-free, unit-tested. |
| `Session.TakeFlatsAsync` (+ helpers) | `TianWen.Lib/Sequencing/Session.Flats.cs` | End-of-session dispatcher (panel vs `TwilightSky` = dawn) + per-OTA panel orchestration. Reuses `MoveTelescopeCoversToStateAsync`, `SwitchFilterIfNeededAsync`, `CameraExposureActions.StampDenormAsync`, `ResilientInvokeAsync`, and the FITS writer. |
| `Session.TakeSkyFlatsAsync` | `TianWen.Lib/Sequencing/Session.SkyFlats.cs` | Twilight sky-flat orchestration (dawn + dusk). Opens covers, solar-altitude window gate, `BeginSlewToZenithAsync` anti-solar tilt, tracking off, per-OTA/filter re-metered capture. Reuses the panel path's shared helpers + FITS writer. |
| Config knobs | `SessionConfiguration` | Panel: `TakeFlatsOnSessionEnd`, `FlatTargetAduFraction` (0.5), `FlatAduTolerance` (0.05), `FlatMaxBrackets` (6), `FlatsPerFilter` (15), `FlatInitialExposure`/`FlatMinExposure`/`FlatMaxExposure`, `FlatCalibratorBrightnessPercent` (50). Sky: `FlatSource` (Calibrator/TwilightSky), `TakeSkyFlatsAtDusk`, `FlatSkyMeridianTilt` (1 h), `FlatSkyMaxDuration` (25 min), `FlatSkySettleInterval` (20 s), `FlatSkySunAltitude{Bright,Dark}Deg` (-3 / -14). |
| `SessionPhase.Flats` | `SessionPhase` | Phase ("Taking Flats" UI label), reused for panel + sky (dawn + dusk) blocks. |

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

## Capture flow (Phase 2 -- twilight sky-flats)

`Session.TakeSkyFlatsAsync(TwilightPeriod)` (`Session.SkyFlats.cs`) is the sky-flat orchestration, run
from **two independently-gated hooks** so both windows can be captured in one night (dusk is insurance
against a clouded dawn):

- **Dawn** (`TwilightPeriod.Dawn`): the end-of-session block dispatches here when
  `FlatSource == TwilightSky` (same `TakeFlatsAsync` hook + `TakeFlatsOnSessionEnd` gate as panel flats).
  The sky brightens -> exposures shorten -> the window closes for a filter when it is too bright at the
  minimum exposure.
- **Dusk** (`TwilightPeriod.Dusk`): a **new** `RunAsync` hook at session start -- after the initial device
  poll, **before** the wait-for-dark (so the sky is still in twilight) -- gated on `TakeSkyFlatsAtDusk`.
  It cools to the imaging setpoint first (so dusk flats match the light-frame temperature). The sky
  darkens -> exposures lengthen -> the window closes for a filter when it is too dim at the maximum
  exposure. Dusk flats run at whatever focus the focuser is at (pre-AutoFocus): a known focus-match
  tradeoff accepted for the cloud-insurance value.

Flow:
1. Open covers (`MoveTelescopeCoversToStateAsync(Open)`) -- opposite of the panel path.
2. **Coarse solar-altitude gate:** `VSOP87a.Reduce(CatalogIndex.Sol, ...)` gives the topocentric solar
   altitude; skip the whole run if the window has already passed in the terminal direction (dawn: sun
   already above `FlatSkySunAltitudeBrightDeg`; dusk: sun already below `FlatSkySunAltitudeDarkDeg`).
3. Slew near the zenith tilted toward the anti-solar sky (`BeginSlewToZenithAsync(distMeridian)` at
   Dec = site latitude; **west** at dawn / **east** at dusk by `FlatSkyMeridianTilt`) to minimise the
   twilight gradient across the frame, then **tracking OFF** so the field drifts and stars average out of
   the flat master's rejection (no dither slews needed).
4. Per OTA / installed filter, **re-meter every frame** (the sky drifts, so there is no converge-once):
   `SkyFlatExposureSolver.Decide(period, ...)` returns `Capture` (keep the in-tolerance frame, re-centre
   the *next* exposure against the drift), `Adjust` (discard, re-meter), `Wait` (pinned at a bound but the
   sky is ramping toward the target -- sleep `FlatSkySettleInterval` and retry), or `Stop` (this filter's
   window has closed; move to the next filter). The exposure is carried across filters as a warm start.
   The whole run is bounded by `FlatSkyMaxDuration`.

**Output contract is identical to the panel path** (`IMAGETYP/FRAMETYP=Flat` + light-frame denorm
metadata under `Flats/<date>/<filter>/Flat/`), so the stacker consumes sky-flats with no extra wiring.

## Phasing

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | Panel/calibrator flats: `FlatExposureSolver` + `TakeFlatsAsync` + config + `SessionPhase.Flats` + end-of-session hook + tests | **DONE** |
| 2 | Twilight **sky-flats** (dawn + dusk): `SkyFlatExposureSolver` + `TakeSkyFlatsAsync` + anti-solar zenith pointing (tracking off) + solar-altitude window gate + `FlatSource` dispatch + dusk `RunAsync` hook + config + tests | **DONE** |
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

- `FlatExposureSolverTests` (12) -- panel convergence under the linear panel model, both clamp directions,
  every fail mode (too-bright-at-min / too-dim-at-max / out-of-brackets), and the near-zero
  divide-by-zero guard.
- `SkyFlatExposureSolverTests` (10) -- in-tolerance capture with a re-centred next exposure, recoverable
  adjust, and the direction-aware wait/stop classification (dawn too-dim -> Wait, dawn too-bright -> Stop,
  dusk too-bright -> Wait, dusk too-dim -> Stop), clamp + near-zero guard.
- `SessionFlatsTests` (2) -- panel orchestration: 4 filters x N flats written into `Flat` frame-type
  folders, calibrator off + cover closed afterwards; and the no-calibrator skip writes nothing.
- `SessionSkyFlatsTests` (3) -- sky-flat orchestration: dawn + dusk each write 4 filters x N flats into
  `Flat` folders with tracking off; and the window-already-past gate skips without writing. Shares
  `[Collection("Flats")]` with `SessionFlatsTests` so the shared-output-folder tests run sequentially.
