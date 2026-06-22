# TODO -- Devices & Drivers

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Camera / ICameraDriver

- [ ] Consider using external temp sensor if no heatsink temp is available (`ICameraDriver.cs:314`)

## Rotator (new device type, per-OTA)

No field-rotator support today: there is no `IRotatorDriver` and no `DeviceType.Rotator` (only WCS
position-angle math exists). Wrapping one is the same dispatch-interop pattern already proven for
`ICoverDriver`/`ISwitchDriver` -- ASCOM exposes `IRotatorV4` (Position / IsMoving / MoveAbsolute /
mechanical-vs-sky PA / Reverse) and Alpaca mirrors it. No vendor-native rotator SDKs exist, so
ASCOM + Alpaca is full coverage for this device class. **Full phased plan: [docs/plans/rotator.md](../plans/rotator.md).**

- [ ] `IRotatorDriver` interface (mechanical position, sky PA, IsMoving, MoveAbsolute, MoveMechanical, Reverse, StepSize) + `DeviceType.Rotator`
- [ ] `AscomRotatorDriver` (wrap a new `AscomDispatchRotator`, mirror `AscomCoverCalibratorDriver`) + `AlpacaRotatorDriver`
- [ ] `FakeRotatorDriver` (settle model + reverse) for tests
- [ ] **Per-OTA wiring** -- the rotator lives on each `Setup.Telescopes[i]` next to its camera/FW/focuser, NOT as a singleton on the mount. Multi-OTA rigs frame each tube independently, so this is the design constraint that makes it harder than a single-train app's one rotator.
- [ ] Framing-angle automation: a target carries a desired sky PA; on slew/center, drive each OTA's rotator to its PA using the plate-solved field rotation
- [ ] Post-meridian-flip re-rotate: a GEM flip rotates the field 180deg, so re-issue the PA to preserve framing (today nothing drives a physical rotator across a flip)
- [ ] `$$ROTATORANGLE$$` (per-OTA) token for the configurable FITS path/header template (pairs with the path-template item in `sequencing.md`)
- [ ] Equipment-tab slot + profile URI persistence (sky-PA offset, reverse flag)

## Dome (new device type, per-site)

No dome support today. ASCOM `IDomeV3` / Alpaca expose shutter + azimuth; the real value is
**slaving** the dome to the single `Setup.Mount` -- compute the topocentric dome azimuth from scope
coordinates + pier side + mount/dome geometry. A per-site singleton (one dome per mount), so
simpler than the per-OTA rotator.

- [ ] `IDomeDriver` interface (shutter open/close, slew-to-az, IsSlewing, CanSlave, park) + `DeviceType.Dome`
- [ ] `AscomDomeDriver` + `AlpacaDomeDriver` (same dispatch-interop pattern)
- [ ] `FakeDomeDriver` for tests
- [ ] Dome-follower loop: target az from mount RA/Dec + pier side + geometry offsets; resync on slew + meridian flip; park the dome on session finalise
- [ ] Imaging gate: hold capture while the shutter is not open or the dome is still slewing (mirror the existing safety-gate pattern)

> The third commonly-missing device type, **SafetyMonitor** (ASCOM `ISafetyMonitorV3`, also a
> per-site singleton), is already tracked in [TODO.md](../../TODO.md) "Next Up".

## DAL Camera Driver

- [ ] Implement trigger for ReadoutMode (`DALCameraDriver.cs:290`)
- [ ] Add proper exceptions for `SetCCDTemperature` setter (`DALCameraDriver.cs:381`)
- [ ] Add proper exceptions for `Offset` getter (`DALCameraDriver.cs:661`)
- [ ] Support auto-exposure (`DALCameraDriver.cs:848`)

## Alpaca Drivers

- [ ] Query tracking rates from Alpaca when endpoint supports enumeration (`AlpacaTelescopeDriver.cs:46`)
- [ ] Parse axis rates from Alpaca response (`AlpacaTelescopeDriver.cs:315`)
- [x] Implement string[] and int[] typed getters for filter names and focus offsets (`AlpacaClient.cs`)
- [ ] Parse string[] from Alpaca for `Offsets` (`AlpacaCameraDriver.cs:241`)
- [ ] Parse string[] from Alpaca for `Gains` (`AlpacaCameraDriver.cs:254`)
- [x] Alpaca `imagearray` endpoint requires special binary handling — done via the `application/imagebytes` binary transfer (`AlpacaImageBytes.DecodeChannel` + `AlpacaClient.GetImageArrayBytesAsync`); `GetImageReadyAsync` downloads + decodes once on first-ready into `ImageData`/`ChannelBuffer`. `AddAlpaca()` now wired into CLI/Server/GUI. (PR #51)
- [ ] Async call to `lastexposureduration` endpoint (`AlpacaCameraDriver.cs:294`)

## ASCOM Drivers

- [ ] Implement axis rates for telescope (`AscomTelescopeDriver.cs:320`)
- [ ] Support ASCOM `Setup()` method — call the driver's native setup dialog for device-specific configuration

## Mount / Meade LX200 Protocol

- [ ] Implement effective `:Gm#` command — ask Johansen (Melbourne) if he knows how to get it or how to use `:E;` to retrieve state
- [ ] Determine precision based on firmware/patchlevel (`MeadeLX200ProtocolMountDriverBase.cs:43`)
- [ ] LX800 fixed GW response not being terminated issue, account for that (`MeadeLX200ProtocolMountDriverBase.cs:143`)
- [ ] Pier side detection only works for GEM mounts (`MeadeLX200ProtocolMountDriverBase.cs:305`)
- [ ] Support `:RgSS.S#` to set guide rate on AutoStar II (`MeadeLX200ProtocolMountDriverBase.cs:573,583`)
- [ ] Verify `:Q#` stops pulse guiding as well (`MeadeLX200ProtocolMountDriverBase.cs:873`)
- [x] Use standard atmosphere for `SitePressure` (`IMountDriver.cs:344`) — DONE (branch `feat/top-5-todo`): the `1010` hardcode is gone; `TryGetTransformAsync` now leaves `SitePressure` unset for the standard tier (`SiteConditions.Standard`), so `Transform` auto-derives it barometrically from elevation (more accurate at altitude than a flat 1010).
- [x] Check online or via connected devices for `SiteTemperature` (`IMountDriver.cs:345`) — DONE (branch `feat/top-5-todo`): `SiteConditions.Resolve` consults a connected `IWeatherDriver` (live), else standard, per value. Session resolves it via `Session.ResolveSiteConditions()`; polar alignment uses the same resolver. (No profile-stored override — temp/pressure vary.)
- [ ] Handle refraction — assumes driver does not support/do refraction (`IMountDriver.cs:347`) — still open; the `Refraction = true` assumption (per-driver native-refraction handling) is deliberately out of scope of `docs/plans/site-conditions.md`.

## Mount / Skywatcher Protocol (gaps vs GSServer reference, `../../other/GSServer/GS.SkyWatcher`)

Findings from comparing `SkywatcherMountDriverBase` against GSServer's `SkyWatcher.cs` `AxisPulse`/`AxisSlew` + `Commands.cs` (2026-06-10). **Fixed 2026-06-11** (commits `71de9b7`, `541213d`, `72b6e53`, `84880c5` on `fix/guider-slewing-calibration`), validated against wire transcripts recorded from GSServer's own client code: `tools/GssOracle` drives `GS.SkyWatcher` headless against a scripted serial port (no ASCOM/COM); the recorded transcripts are pinned as the `gss-oracle-transcripts.json` fixture (`SkywatcherGssOracleTests`).

- [x] **`:G` motion-mode payload is a fake-only dialect** — DONE (`71de9b7`): real 2-char `<func><dir>` format via `SkywatcherProtocol.EncodeMotionMode(func, forward, southernHemisphere)` + `SkywatcherMotionFunc` enum (speed bit inverts between goto and slew); all five driver call sites and the fake's parser flipped atomically; `speedChar` dead variable replaced by the func selection. 16-payload table test + oracle round-trip test.
- [x] **Southern-hemisphere direction bit** — DONE (`71de9b7`): every `:G` carries dir bit1 below the equator, tracking/RA pulses run the worm in REVERSE in the south (GSS EqS passes the negated rate), and the steps↔sky conversions mirror with it (`StepsToRa`/`RaToSteps`/`StepsToDec`/`DecToSteps`, equivalent of GSS `Axes.AxesAppToMount` `a[0] = 180 − a[0]`). Fake auto-resumes post-GOTO tracking per the stored hemisphere bit. Southern tracking/goto/pulse functional tests pin it.
- [x] **Pulse guide via live `:I` rate change** — DONE (`541213d`): RA pulse while tracking sends only `:I` (combined rate) then `:I` (sidereal restore, in `finally` so cancellation can't leave the rate stuck); f=1.0 East commands sidereal/1000 instead of halting. Wire-contract test asserts no `:G`/`:J`/`:K` during a tracking RA pulse.
- [x] **Dec pulse as micro-GOTO (GSS `DecPulseGoTo`)** — DONE (`84880c5`): opt-in `?decPulseGoto=true` mount URI key (advanced device setting on `SkywatcherDevice` + fake); duration → exact steps → relative low-speed GOTO (`:G` func 2 + `:H` + `:M 0` + `:J`) polled to FullStop, 3.5 s cap. Rate-based stays default.
- [x] **Wait for FullStop after `:K` before issuing `:G`** — DONE (`72b6e53`): `StopAxisAndWaitAsync` (25 ms polls, re-stop every 5, 3.5 s cap) used by `BeginSlewRaDecAsync`/`ParkAsync`/`MoveAxisAsync`; `SetTrackingAsync(true)` is status-driven (already-running-in-tracking-direction → live `:I` only, GSS `rateChangeOnly`). The fake now REJECTS `:G` while running with `!2` like real firmware, so the whole suite enforces the contract.
- [x] **Minimum pulse duration floor** — DONE (`541213d`): 20 ms floor at the top of `PulseGuideAsync`, dropped before touching the wire.
- [x] **`:f` axis-status reply is 3 nibbles, not 6 hex chars** (found via the GSS oracle) — DONE (`71de9b7`): nibble0 = mode/dir/high-speed bits, nibble1 = running, nibble2 = init; driver parse + fake reply flipped together.
- [x] **GOTO `:M` break-point increment + speed-tier selection** (found via the GSS oracle) — DONE (`71de9b7`): `:H` then `:M` (3500 high-speed / 0 low-speed) then `:J`; low-speed GOTO func within the 640-sidereal-second margin, high-speed beyond it.
- [x] **Iterative goto refinement (EQMOD-style)** — DONE (2026-06-11, believed/true split branch): the goto's RA target steps encode the HA at COMMAND time, so a long slew landed late by the slew duration of sidereal motion (~9' for a multi-hour swing). `IsSlewingAsync` is the completion-detection point: when the axes stop with a goto pending and the residual exceeds 30", it re-issues a short refinement goto (max 2 passes, `Interlocked` gate against concurrent pollers); callers' wait-for-completion loops are unchanged because the mount keeps reporting "slewing" during refinement. `AbortSlewAsync`/`ParkAsync` disarm the pending target. Validated by `GivenTopocentricSkywatcherWhenSlewingToJ2000TargetThenJ2000ReadbackMatches` (readback err 9.4' -> <3').
- [ ] Dec backlash compensation in pulse guide — GSS converts configured backlash steps into extra pulse duration (capped +1000 ms so PHD2's 2 s return expectation holds, `SkyWatcher.cs:500-506`). We have per-focuser backlash inference; mounts have none. Lower priority: the built-in guider's calibration absorbs steady-state Dec lash partially.
- [ ] Verify on real hardware whether EQ6-class firmware auto-resumes sidereal tracking after a GOTO (the fake models auto-resume; GSS does not rely on it). Low risk either way: `Session` re-ensures tracking via `EnsureTrackingAsync` before focus/imaging, which is status-driven and firmware-legal now.
- [ ] Verify iterative goto refinement on real hardware (EQMOD does the same multi-pass goto; our 30" tolerance / 2-pass cap may need tuning against real motor ramp + stop-wait times).

## Mount / Believed vs True Pointing (fakes) + Sky-Map Solve & Sync

DONE 2026-06-11 (believed/true split). A real mount's encoders only know the BELIEVED pointing;
hidden alignment errors (polar misalignment, cone error) are only observable through a camera.
The fakes now model this honestly, and the sky map gained the discovery tool:

- [x] `FakeSkywatcherMountDriver` public `GetRA/GetDec` report the believed (encoder) pointing; the misaligned TRUE pointing moved to the internal `IFakeTruePointingSource.GetTruePointingNativeAsync` seam (all three regimes: near-pole encoder sweep, pre-sync axis tilt, post-sync tracking drift + believed-deviation term).
- [x] `FakeCameraDriver` guide path renders from the true seam; main path shifts the stamped `Target` by the per-exposure `(true - believed)` J2000 delta so plate solves of main frames reveal the hidden error. `FakeGuider.SaveImageAsync` stamps WCS from the true seam (polar-align sim signal preserved). Shared conversion extracted to `EquatorialFrameConversion.TopocentricToJ2000` (one path with `IMountDriver.GetRaDecJ2000Async`).
- [x] Sky-map mount reticle is clickable -> mount info panel -> **Solve & Sync** button: `MountActions.SolveAndSyncAsync` (stamp + preview capture + plate solve + `SyncRaDec` via the profile transform). Marker jumps to truth on the next telemetry poll; re-slew stays the user's decision. Uses the OTA's MAIN camera (`OTAs[OtaIndex].Camera`, index 0 from the button). This is the only truthful-marker path for slew-less trackers (SkyGuider Pro: `CanSlew=false`, `CanSync=true`). Verified end-to-end in the GUI: blind goto lands marker ON target; Solve & Sync revealed a 6.5' cone error; re-goto landed true; second solve showed 1.3' residual (pure tracking drift).
- [ ] Optional: per-OTA picker for Solve & Sync on multi-OTA rigs (button currently posts OTA 0).
- [ ] Optional: expose Solve & Sync exposure/gain/binning in the UI (currently 5 s / camera default / bin 1).

## Device Management

- [ ] Try to parse URI manually in Profile fallback (`Profile.cs:130`)

## Protocol Support

- [ ] GSS ServoCAT / SiTech protocol support + simulator
- [x] iOptron SkyGuider Pro (SGP) mount driver — `SgpMountDriverBase<T>` with custom serial protocol at 28800 baud, RA-only axis, pulse guiding via timed move, CameraSnap support, `FakeSgpSerialDevice` for testing
- [ ] iOptron SkyGuider Pro: investigate patching the SGP handbox firmware (STM32F103, same as iOptron SmartEQ) to support the standard iOptron serial protocol, enabling features like position reporting and goto
- [ ] iOptron SkyGuider Pro: device identity — no UUID mechanism available (firmware has no user string storage, doesn't read STM32 hardware UID); falls back to firmware version + port name
- [ ] Generic iOptron serial protocol support (SmartEQ, CEM series) — same 28800 baud, similar command set but with position feedback
- [ ] SGP pulse guiding should restore previous speed not just siderial (wait Pulse guiding is wrong, it will be 1x siderial but SGP has a different guide rate configured) or make this configurable; alternative: if guide rate is 0.5, half guide pulse time by 2

