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
| `Session.RunFlatsOnlyAsync` (+ `ConnectForFlatsAsync` / `FinaliseFlatsAsync`) | `TianWen.Lib/Sequencing/Session.FlatsOnDemand.cs` | **On-demand** entry point (`ISession.RunFlatsOnlyAsync`): self-contained connect -> cool -> capture -> finalise, skipping wait-for-dark / focus / guider / observation loop. Connects only the flat-relevant device subset (cameras / covers / filter wheels / focusers, plus the mount for sky-flats -- **never the guider**), then dispatches to the same `TakeFlatsAsync` / `TakeSkyFlatsAsync`. Shares the per-OTA connect (`ConnectTelescopeAsync`) + observable-state alloc (`AllocateObservableState`) with `RunAsync`; a focused finaliser avoids the full `Finalise`'s guider/park steps a flat run never used. |
| `FlatsSubCommand` (`tianwen flats`) | `TianWen.Cli/FlatsSubCommand.cs` | CLI surface: resolves the active profile, builds the flat config (site from `ProfileData`), `ISessionFactory.InitializeAsync` + `Create([])` + `RunFlatsOnlyAsync`, reports phases + written-frame count. `--source calibrator\|sky`, `--period dawn\|dusk`, `--count/--target/--tolerance/--min-exposure/--max-exposure/--initial-exposure/--brightness/--brackets`. |
| `POST /api/v1/session/flats` | `TianWen.Hosting/Api/SessionEndpoints.cs` + `Dto/FlatsRequestDto.cs` | API surface: mirrors `/session/start` (409 if running, `?profileId=` or active, background run, poll `/state`). Body = `FlatsRequestDto` (source / period / flat knobs; site falls back to the mount's own). AOT: `FlatsRequestDto` registered in `HostingJsonContext`. |
| `FlatRunParsing` | `TianWen.Lib/Sequencing/FlatRunParsing.cs` | Single source of truth mapping the `source` / `period` strings onto their enums (case-insensitive + aliases), shared by the CLI and the API endpoint so the accepted spellings never drift (mirrors `EnhanceOptions.TryParse`). |
| Config knobs | `SessionConfiguration` | Panel: `TakeFlatsOnSessionEnd`, `FlatTargetAduFraction` (0.5), `FlatAduTolerance` (0.05), `FlatMaxBrackets` (6), `FlatsPerFilter` (15), `FlatInitialExposure`/`FlatMinExposure`/`FlatMaxExposure`, `FlatCalibratorBrightnessPercent` (50). Sky: `FlatSource` (**Calibrator / TwilightSky**; a manual panel is a `ManualCoverDevice`, not a source), `TakeSkyFlatsAtDusk`, `FlatSkyMeridianTilt` (1 h), `FlatSkyMaxDuration` (25 min), `FlatSkySettleInterval` (20 s), `FlatSkySunAltitude{Bright,Dark}Deg` (-3 / -14). |
| `SessionPhase.Flats` | `SessionPhase` | Phase ("Taking Flats" UI label), reused for panel + sky (dawn + dusk) + on-demand blocks. |

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

## Capture flow (Phase 3 -- on-demand + manual panel)

The end-of-session hooks are for a fully unattended night. Phase 3 adds an **on-demand** surface so the
same routines can be triggered outside a session (e.g. a dedicated flat-panel session in daylight, or
catching a twilight window without imaging), plus a **manual-panel device** (`ManualCoverDevice`) for a
dumb, hand-switched panel -- modelled as a degenerate `ICoverDriver` (like `ManualFilterWheelDevice`) so it
flows through the ordinary calibrator path with no session branching.

- **`ISession.RunFlatsOnlyAsync(TwilightPeriod, ct)`** (`Session.FlatsOnDemand.cs`) is the shared entry
  point behind both the CLI and the API. It runs a self-contained cycle -- `SetPhase(Initialising)` ->
  `ConnectForFlatsAsync` -> `SetPhase(Cooling)` cool-to-setpoint -> `SetPhase(Flats)` dispatch ->
  `finally` `FinaliseFlatsAsync` (`SetPhase(Finalising)` then the terminal phase) -- with the same
  try/catch/finally shape (and abort/exception -> `Aborted`/`Failed`) as `RunAsync`, but **none** of the
  wait-for-dark / focus / guider-calibration / observation-loop stages.
  - **`ConnectForFlatsAsync`** connects only the flat-relevant device subset: each OTA's camera / focuser
    / filter wheel / cover (via the shared `ConnectTelescopeAsync`, extracted from `InitialisationAsync`),
    **plus the mount only for sky-flats** (slew + tracking) and **never the guider**. Configured site
    drives the mount sync + denorm stamp; the mount's own site is the fallback when the profile has none.
  - **`FinaliseFlatsAsync`** is a focused counterpart to `Finalise`: abort exposures, close covers, warm
    cameras, park + disconnect the mount when connected, disconnect the OTA devices. Because it never
    touches the guider, it avoids the full finaliser's spurious "partial shutdown" report for devices a
    flat run never used.
  - Dispatch: `TwilightSky` calls `TakeSkyFlatsAsync(period)` **directly** so the caller-chosen dawn/dusk
    is honoured (`TakeFlatsAsync` would default to Dawn); `Calibrator` flows through `TakeFlatsAsync`.
- **Manual panel = a device** (`ManualCoverDevice` + `ManualCoverDriver`, `TianWen.Lib/Devices/`): a dumb
  hand-switched panel (e.g. an analog LED pad with a physical brightness knob) modelled as a degenerate
  `ICoverDriver`, mirroring `ManualFilterWheelDevice`/`Driver`. It reports `GetCoverStateAsync => NotPresent`
  (no flap), `BeginOpen`/`BeginClose` no-op, and `BeginCalibratorOn` reports the panel `Ready` on demand
  (trusting the user switched it on) with no analog-brightness control -- so the exposure solver does the
  levelling and misconfigured light fails the solver gracefully. Assigned to an OTA's cover slot, it runs
  through the **same** `Calibrator` path -- there is no `ManualPanel` source and no session branching.
  Registered via `AddDeviceType(uri => new ManualCoverDevice(uri))` in `AddDevices()` so a stored
  `CoverCalibrator://ManualCoverDevice/manual` URI round-trips through `DeviceHub.TryGetDeviceFromUri` (the
  resolution path `SessionFactory` uses) -- closing a latent gap `ManualFilterWheelDevice` still has (no
  keyed factory registered).
- **CLI** `tianwen flats` and **API** `POST /api/v1/session/flats` are thin adapters over
  `RunFlatsOnlyAsync`; source/period strings map through the shared `FlatRunParsing`. Output contract is
  unchanged (`Flats/<date>/<filter>/Flat/`).

## Phasing

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | Panel/calibrator flats: `FlatExposureSolver` + `TakeFlatsAsync` + config + `SessionPhase.Flats` + end-of-session hook + tests | **DONE** |
| 2 | Twilight **sky-flats** (dawn + dusk): `SkyFlatExposureSolver` + `TakeSkyFlatsAsync` + anti-solar zenith pointing (tracking off) + solar-altitude window gate + `FlatSource` dispatch + dusk `RunAsync` hook + config + tests | **DONE** |
| 3 | **On-demand surface** (`ISession.RunFlatsOnlyAsync` + CLI `tianwen flats` + `POST /api/v1/session/flats`) + **manual flat-panel device** (`ManualCoverDevice`/`ManualCoverDriver`, a degenerate `ICoverDriver` captured through the ordinary calibrator path; keyed-factory registered so it round-trips). | **DONE** |

## Deferred: GUI flats tab (assign manual panel + source dropdown + prompt)

The manual-panel **device** ships in Phase 3 (`ManualCoverDevice`/`ManualCoverDriver`, assigned to an OTA
cover slot and captured through the `Calibrator` path). What remains deferred is a **GUI** surface: an
equipment affordance to **add / assign a Manual Light Panel** (a **light-bulb 💡** entry, the way a Manual
Filter Holder is added), an illumination-source **dropdown** (calibrator / sky), and a fully interactive
"switch the panel on, press Continue" prompt for the manual panel. The GUI has no document/flats tab yet,
so there is nowhere to host it; the on-demand CLI + API are the surfaces for now. (The manual driver
already reports `Ready` on demand and the solver handles misconfigured light gracefully -- only the
interactive UI is missing.)

## Tests

- `FlatExposureSolverTests` (12) -- panel convergence under the linear panel model, both clamp directions,
  every fail mode (too-bright-at-min / too-dim-at-max / out-of-brackets), and the near-zero
  divide-by-zero guard.
- `SkyFlatExposureSolverTests` (10) -- in-tolerance capture with a re-centred next exposure, recoverable
  adjust, and the direction-aware wait/stop classification (dawn too-dim -> Wait, dawn too-bright -> Stop,
  dusk too-bright -> Wait, dusk too-dim -> Stop), clamp + near-zero guard.
- `SessionFlatsTests` (4) -- calibrator orchestration: 4 filters x N flats written into `Flat` frame-type
  folders, calibrator off + cover closed afterwards; the no-calibrator skip writes nothing; a
  **`ManualCoverDevice`** assigned to the OTA writes flats through the **same** calibrator path (proving no
  branching); and the **on-demand `RunFlatsOnlyAsync`** happy path connects -> cools -> captures ->
  finalises to `SessionPhase.Complete` with the expected frame count.
- `ManualCoverDriverTests` (4) -- the manual panel as a degenerate `ICoverDriver`: cover `NotPresent` +
  no-op flap, calibrator `Ready`-on-demand then `Off`, `MaxBrightness` 255, and the keyed-factory
  round-trip through `IDeviceHub.TryGetDeviceFromUri`.
- `FakeCoverCalibratorDiscoveryTests` (4) -- the fake cover/calibrator is discoverable and models both a
  flip-flat and a flap-less driver panel (`hasCover=false` -> `NotPresent`, calibrator still cycles).
- `SessionSkyFlatsTests` (3) -- sky-flat orchestration: dawn + dusk each write 4 filters x N flats into
  `Flat` folders with tracking off; and the window-already-past gate skips without writing. Shares
  `[Collection("Flats")]` with `SessionFlatsTests` so the shared-output-folder tests run sequentially.
