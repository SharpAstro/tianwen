# Device-Simulator Integration Tests (on-demand CI)

Drive the real TianWen device drivers against **live device simulators** in CI, closing two
long-standing coverage gaps:

1. **Alpaca HTTP round-trip was unverified.** `AlpacaImageBytesTests` byte-pins the ImageBytes
   decoder, but nothing had ever exercised the actual HTTP path (`AlpacaClient` +
   `Alpaca*Driver` -> a real Alpaca server), so the camera `application/imagebytes` negotiation +
   transfer was "byte-exact + unit-pinned but the HTTP round-trip is unverified" (per the Alpaca
   note in `CLAUDE.md`).
2. **The native ASCOM (COM) tests never ran in CI.** `AscomDeviceTests` gated its device-touching
   cases on `Debugger.IsAttached`, i.e. a developer stepping through them on Windows -- so CI (no
   debugger) always skipped them.

## Why this shape

- **Alpaca is cross-platform HTTP**, so it runs on the existing Linux runners against the
  **ASCOM Alpaca Simulators ("OmniSim")** -- a self-contained server that speaks every device type
  incl. the camera ImageBytes protocol. This is the high-value, low-cost win.
- **Native ASCOM is Windows-only COM**, so it needs a `windows-latest` runner with a silent
  **ASCOM Platform** install (registers the `ASCOM.Simulator.*` COM drivers). Higher cost / more
  flake-prone, and it only exercises the COM-marshalling seam -- the device *semantics* are already
  covered once Alpaca hits the same simulators.
- Both are **too heavy to run on every push/PR** (an OmniSim download; a full Platform install), so
  they live in a dedicated **`workflow_dispatch`** workflow, mirroring how `publish-apps`/`release`
  are dispatch-gated. The fast PR loop (`test-unit` / `test-functional`) is untouched.

## Structure

A dedicated **`TianWen.Lib.Tests.Simulators`** project (separate from the fast unit
`TianWen.Lib.Tests` and the fake-device `TianWen.Lib.Tests.Functional` suites, so neither ever
depends on an external process, and each simulator gets its own CI job).

- **`SimulatorGate`** -- central opt-in. Every test skips (not fails) unless its env var is set, so a
  bare `dotnet test` on a machine with no simulator is green:
  - `TIANWEN_ALPACA_SIM` = base URL of a running OmniSim, e.g. `http://localhost:11111`.
  - `TIANWEN_ASCOM_CI` = any non-empty value; the native ASCOM Platform + simulators must be
    installed (Windows only).
- **`AlpacaSimulatorTests`** -- resolves devices through the real **management API**
  (`/management/v1/configureddevices`) rather than UDP discovery (unreliable on runners) and builds
  directly-addressed `AlpacaDevice`s, then drives each device type through the production
  `AlpacaClient` + drivers:
  - `ManagementApi_ListsConfiguredDevices`
  - `Camera_ExposesAndDownloadsViaImageBytes` -- **the payoff**: `GetImageAsync` ->
    `GetImageArrayBytesAsync` (`Accept: application/imagebytes`) -> `AlpacaImageBytes.DecodeChannel`.
  - `Telescope_ConnectsReadsCoordinatesAndTracking`, `Focuser_MovesToAbsolutePosition`,
    `FilterWheel_MovesToPosition`, `CoverCalibrator_ReadsStateAndTogglesCalibrator`.
  - `Camera_ReadsSensorGainOffsetAndReadoutMetadata` -- pins the connect-time metadata that used to
    be stubbed (see "Driver bugs found" below); capability-aware so it validates whichever gain/offset
    mode the sim exposes. `Switch_Connects` -- a connect smoke test (`ISwitchDriver` has no ops yet).
  - Poll loops go through the shared `SimulatorTestHelpers.WaitAsync`, which uses `ITimeProvider`
    (`SleepAsync` + `GetElapsedTime`) rather than raw `Task.Delay`/`Stopwatch` -- but the suites pass a
    real `SystemTimeProvider.Instance` (NOT the fake clock, whose auto-advancing `SleepAsync` would
    busy-spin against a live server). The `AddAlpaca` service provider registers the same real clock.
- **`AscomDeviceTests`** -- moved here from `.Functional`, re-gated `Debugger.IsAttached` ->
  `SimulatorGate.AscomCiEnabled` (still Windows-only) so CI can run it on a provisioned Windows runner.
  Now mirrors the Alpaca device coverage: focuser move, filter-wheel move, and cover-calibrator toggle
  in addition to the original camera / telescope / device-type cases.

## Driver bugs found (the suite paid for itself)

The first live Alpaca dispatch caught two pre-existing driver bugs -- both "this path never actually
worked" defects that had shipped because nothing exercised the HTTP round-trip:

- **Mono cameras could not connect** -- `AlpacaCameraDriver` read `BayerOffsetX/Y` unconditionally at
  init, but mono/direct-colour sensors throw `PropertyNotImplemented` per ICameraV3. Now gated on sensor type.
- **The filter wheel never populated its slots** -- `AlpacaFilterWheelDriver` never overrode the base
  no-op `InitDeviceAsync`, so `Filters` was always empty and `BeginMoveAsync` always threw.

A follow-up audit fixed the same class of latent stub in `AlpacaCameraDriver`, each now pinned by
`Camera_ReadsSensorGainOffsetAndReadoutMetadata` / the exposure-duration assertion:

- `Gains` / `Offsets` were hardcoded `[]` (gain/offset-*mode* cameras got empty dropdowns) -- now parse
  the `gains`/`offsets` string lists at init.
- `LastExposureDuration` was hardcoded `null` (zeroed the FITS `EXPTIME` on every Alpaca frame) -- now
  baselined at `StartExposure` and refined from the server's `lastexposureduration`.
- `ReadoutMode` get/set were local-only no-ops -- now round-trip through the server via the
  `readoutmodes` index list.

## CI (`.github/workflows/simulators.yml`)

Two entry points:
- **`workflow_dispatch`**: `gh workflow run simulators.yml [-f suite=alpaca|ascom|both]` (default `both`).
- **`schedule`** (weekly, Mondays 06:00 UTC): runs the **Alpaca leg only** as an unattended regression
  guard (`inputs.suite` is empty on a schedule event, so the `alpaca-sim` `if` also checks
  `github.event_name == 'schedule'` while `ascom-sim` does not). The Windows ASCOM leg stays
  dispatch-only -- a full Platform install every week is not worth it for a COM seam the Alpaca run
  already covers semantically.

- **`catalogs`** (ubuntu) -- the only job needing `lzip` + the `*.lz` LFS objects; builds
  `TianWen.Lib` to produce the `*.gs.gz` preprocessed catalogs and uploads them as the
  `preprocessed-catalogs` artifact (the proven `dotnet.yml` pattern). This is what lets the Windows
  leg build without `lzip`.
- **`alpaca-sim`** (ubuntu, `needs: catalogs`) -- downloads the self-contained OmniSim
  (`ascom.alpaca.simulators.linux-x64.tar.xz`), launches it headless, discovers the port it bound
  (from its startup log, falling back to the Alpaca default 11111), exports `TIANWEN_ALPACA_SIM`,
  and runs the suite. Uploads the OmniSim log as an artifact.
- **`ascom-sim`** (windows, `needs: catalogs`) -- silent-installs the ASCOM Platform
  (`/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`), sets `TIANWEN_ASCOM_CI=1`, runs the suite.
- Neither sim job pulls LFS: the sim tests load nothing from LFS, and the embedded `*.bin.gz` (LFS
  pointer text here) is never read at runtime. `PreprocessCatalogs` is skipped via the downloaded
  `*.gs.gz` + an mtime touch.

## Running locally

```bash
# Alpaca: start an OmniSim (download from the ASCOM.Alpaca.Simulators releases), then:
TIANWEN_ALPACA_SIM=http://localhost:11111 dotnet test TianWen.Lib.Tests.Simulators

# ASCOM (Windows, ASCOM Platform + simulators installed):
TIANWEN_ASCOM_CI=1 dotnet test TianWen.Lib.Tests.Simulators
```

## Status

**Phases 1-4 DONE + shipped** (merged via #72/#73; follow-up expansion in progress):

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | New `TianWen.Lib.Tests.Simulators` project (csproj, slnx, `InternalsVisibleTo`, `xunit.runner.json` serial), `SimulatorGate`, move + re-gate `AscomDeviceTests` | DONE |
| 2 | `AlpacaSimulatorTests` -- management API + camera ImageBytes + telescope/focuser/filterwheel/covercalibrator | DONE |
| 3 | `simulators.yml` workflow (catalogs artifact + Linux OmniSim + Windows ASCOM Platform) | DONE |
| 4 | **Live validation** -- Alpaca suite green against OmniSim v0.4.0 (found + fixed 2 driver bugs, see above) | DONE |
| 5 | Weekly Alpaca `schedule:` guard; Alpaca camera-metadata + switch tests; driver-stub fixes; ASCOM device-coverage mirror | DONE (this change) |

Verified: the Alpaca suite is green against a live OmniSim; the project builds and every sim-gated case
skips cleanly with no env var set. The Windows `ascom-sim` leg is validated via `-f suite=ascom` dispatch.

## Deferred / known risks

- **ASCOM array-property marshaling (SAFEARRAY) -- RESOLVED.** `ComVariant.As<T[]>()` cannot marshal
  SAFEARRAYs, which broke camera `ImageArray` + filter-wheel `Names`; fixed with manual SAFEARRAY
  marshaling (`SafeArrayMarshal`), taking the ASCOM leg to 10/10. Root-cause record:
  [ascom-safearray-marshaling.md](ascom-safearray-marshaling.md).
- **UDP discovery (`AlpacaDeviceSource.DiscoverAsync`) is still untested** -- these tests
  deliberately bypass it (direct-addressing) because multicast is unreliable on hosted runners.
- **The sim's camera gain/offset mode is unknown up front**, so the metadata test is capability-aware:
  it asserts whichever mode OmniSim exposes and logs the rest. If OmniSim reports no gain control at
  all, the gain-list parsing goes exercised only by the (separate) `AlpacaImageBytesTests` unit pins.
- **Rotator + alt-az** device coverage will slot into the same project once those drivers land
  (see [rotator](rotator.md), [altaz-mount-support](altaz-mount-support.md)).
