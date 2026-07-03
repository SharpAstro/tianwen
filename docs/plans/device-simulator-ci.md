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
  - Poll loops use a real `Stopwatch` + `Task.Delay` (the fake `IExternal` clock would busy-spin);
    the driver calls used here don't sleep internally, so nothing stalls.
- **`AscomDeviceTests`** -- moved here from `.Functional`, re-gated `Debugger.IsAttached` ->
  `SimulatorGate.AscomCiEnabled` (still Windows-only) so CI can run it on a provisioned Windows runner.

## CI (`.github/workflows/simulators.yml`, `workflow_dispatch`)

`gh workflow run simulators.yml [-f suite=alpaca|ascom|both]` (default `both`).

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

**Phases 1-3 DONE** (branch `test/device-simulator-ci`):

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | New `TianWen.Lib.Tests.Simulators` project (csproj, slnx, `InternalsVisibleTo`, `xunit.runner.json` serial), `SimulatorGate`, move + re-gate `AscomDeviceTests` | DONE |
| 2 | `AlpacaSimulatorTests` -- management API + camera ImageBytes + telescope/focuser/filterwheel/covercalibrator | DONE |
| 3 | `simulators.yml` on-demand workflow (catalogs artifact + Linux OmniSim + Windows ASCOM Platform) | DONE |

Verified locally: the project builds and all 12 sim-gated cases skip cleanly with no env var set.
The against-a-live-simulator green run happens on the first `workflow_dispatch`.

## Deferred / known risks

- **First dispatch is the real validation.** OmniSim's exact launch flags and the ASCOM Platform
  silent-install switches are handled empirically (the OmniSim step is self-diagnosing: it discovers
  the port from the log and dumps the log on failure). Specific simulator behaviours (e.g. a device
  type OmniSim doesn't expose, or a covercalibrator that needs the cover closed first) may need small
  assertion tweaks after the first run -- the tests are independent, so one failing case never blocks
  the others.
- **UDP discovery (`AlpacaDeviceSource.DiscoverAsync`) is still untested** -- these tests
  deliberately bypass it (direct-addressing) because multicast is unreliable on hosted runners.
- **A nightly `schedule:` trigger** could be added later if we want unattended regression coverage
  without remembering to dispatch.
- **Rotator + alt-az** device coverage will slot into the same project once those drivers land
  (see [rotator](rotator.md), [altaz-mount-support](altaz-mount-support.md)).
