# TODO -- Devices & Drivers

**The open items are GitHub issues** labelled [`area:drivers`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%3Adrivers) since 2026-09-24, when this file was migrated. What is left here is the DONE archive, kept for the measurements and reasons it records. Never add an open `- [ ]` here: open an issue.

## Camera / ICameraDriver

- [x] **Canon Live View video: EVF-zoom planetary regime + host-side ROI jog (Phase E zoom-pan).** Phase E
      *core* shipped 2026-07-16; the zoom crop and its pannable ROI shipped 2026-08-03 on FC.SDK `3.0.751`.
      `NumX` snaps to a zoom level, `VideoRoi` reports the body's own rect, `CanJogRoi` / `JogRoiAsync` pan it,
      and `CanJogRoi` is true only while magnified and the body advertises the pan operation, so 1x still falls
      back to mount jog by design. **This entry described the blocker wrongly, which is why it outlived three
      FC.SDK releases**, and the mistake is the reusable part: it asked for typed accessors for
      `Evf_ZoomPosition` (0x508) and `Evf_ZoomRect` (0x541) on the theory that they were properties whose
      payloads were too wide for a 4-byte `SetPropValue`. Over PTP **those properties do not exist at all**;
      they are EDSDK's model of the feature. The camera takes zoom and pan as *operations* (`0x9158` / `0x9159`)
      and reports the crop as a record inside the live-view frame, so no accessor of any width could ever have
      unblocked it. State a blocker in terms of the protocol, and never date one on a release. Not yet verified
      from TianWen on hardware (the FC.SDK side is, on a 6D), and per-body zoom-position units vary, so the
      Phase C per-axis cap is still what bounds a wrong guess to a small mis-pan. Detail:
      [../plans/planetary-native-video.md](../plans/planetary-native-video.md) Phase E.

## Cover / Calibrator (`ICoverDriver`)

Shipped: ASCOM/Alpaca `CoverCalibrator`, the discoverable fake (flip-flat + flap-less `hasCover=false`
variants), `ManualCoverDevice` (a hand-switched panel as a degenerate driver), and a native ASCOM-free
serial driver for the Gemini FlatPanel Lite (`AddGemini()`; wire spec:
[docs/architecture/gemini-flatpanel-lite-protocol.md](../architecture/gemini-flatpanel-lite-protocol.md)).
The driver's connect asserts DTR+RTS (opt-in `IExternal.OpenSerialDeviceAsync(..., assertControlLines: true)`)
and re-verifies identity on a nominally-open connection (`SerialPort.IsOpen` is not liveness -- a dead CH341
keeps reporting open), rebuilding the connection when the handshake goes silent.

- [x] **Gemini FlatPanel Lite: validated against real hardware** (`fix/gemini-flat-panel`, 2026-07-04, FW 205
      on a CH341/COM3). Both driver connect (ramp + beep, reproducible via a live-hardware test gated on
      `TIANWEN_GEMINI_FPLITE_PORT`) and **auto-discovery** now work. Real hardware corrected the spec + code:
      (1) response sigil is **`*`** not `>` (`ParsePayload` accepts both); (2) ~2 s **boot delay** after open
      (sleep-through, not poll-through, writing during boot yields dropped writes + duplicate replies that
      desync); (3) every command is **acked** incl. actions (drain in `SendAsync`); (4) DTR **is** required
      cold (the "not required" reading was a confound). Discovery: `ISerialProbe.Warmup` + `AssertControlLines`
      (isolated pass 2 only), and probes moved to the cancellable **`SynchronousReads`** path (async
      `SerialPort.BaseStream` reads spuriously abort on CH34x). See the protocol doc + [../plans/soft-discovery.md](../plans/soft-discovery.md).

## Rotator (new device type, per-OTA)

No field-rotator support today: there is no `IRotatorDriver` and no `DeviceType.Rotator` (only WCS
position-angle math exists). Wrapping one is the same dispatch-interop pattern already proven for
`ICoverDriver`/`ISwitchDriver` -- ASCOM exposes `IRotatorV4` (Position / IsMoving / MoveAbsolute /
mechanical-vs-sky PA / Reverse) and Alpaca mirrors it. No vendor-native rotator SDKs exist, so
ASCOM + Alpaca is full coverage for this device class. **Full phased plan: [docs/plans/rotator.md](../plans/rotator.md).**

## Dome (new device type, per-site)

No dome support today. ASCOM `IDomeV3` / Alpaca expose shutter + azimuth; the real value is
**slaving** the dome to the single `Setup.Mount` -- compute the topocentric dome azimuth from scope
coordinates + pier side + mount/dome geometry. A per-site singleton (one dome per mount), so
simpler than the per-OTA rotator.

> The third commonly-missing device type, **SafetyMonitor** (ASCOM `ISafetyMonitorV3`, also a
> per-site singleton), is already tracked in [TODO.md](../../TODO.md) "Next Up".

## Alpaca Drivers

- [x] Implement string[] and int[] typed getters for filter names and focus offsets (`AlpacaClient.cs`)
- [x] Alpaca `imagearray` endpoint requires special binary handling; done via the `application/imagebytes` binary transfer (`AlpacaImageBytes.DecodeChannel` + `AlpacaClient.GetImageArrayBytesAsync`); `GetImageReadyAsync` downloads + decodes once on first-ready into `ImageData`/`ChannelBuffer`. `AddAlpaca()` now wired into CLI/Server/GUI. (PR #51)
- [x] Alpaca camera: recycle frame buffers; **DONE (2026-07-06, same-day as the audit)**: `AlpacaCameraDriver` now carries a DAL-style `_freeBuffers` `ConcurrentBag`; `AlpacaImageBytes.DecodeChannel(payload, recycled)` decodes into the recycled buffer when the shape matches (drops it to GC on an ROI/bin change), and `onRelease` returns the `float[,]` to the bag; a steady capture loop no longer allocates a fresh full-frame LOH array per frame. Pinned by the recycle tests in `AlpacaImageBytesTests`.

## ASCOM Drivers

- [x] ASCOM camera: cache `ImageData` on first read; **DONE (2026-07-06, same-day as the audit)**: `AscomCameraDriver.ImageData` now materialises the COM `ImageArray` exactly once per exposure into `_imageData` (cleared by `ReleaseImageData` + `StartExposureAsync`, restoring the "reads null after `GetImageAsync`" contract), attaches a recycling `ChannelBuffer`, and `Channel.FromWxHImageData(sourceData, recycled)` converts into a recycled buffer from the DAL-style `_freeBuffers` bag when the shape matches.

## Mount / Meade LX200 Protocol

- `:Q#` stopping pulse guiding too (`MeadeLX200ProtocolMountDriverBase.cs:873`): bench check, bench issue #647.
- [x] Use standard atmosphere for `SitePressure` (`IMountDriver.cs:344`); DONE (branch `feat/top-5-todo`): the `1010` hardcode is gone; `TryGetTransformAsync` now leaves `SitePressure` unset for the standard tier (`SiteConditions.Standard`), so `Transform` auto-derives it barometrically from elevation (more accurate at altitude than a flat 1010).
- [x] Check online or via connected devices for `SiteTemperature` (`IMountDriver.cs:345`); DONE (branch `feat/top-5-todo`): `SiteConditions.Resolve` consults a connected `IWeatherDriver` (live), else standard, per value. Session resolves it via `Session.ResolveSiteConditions()`; polar alignment uses the same resolver. (No profile-stored override, temp/pressure vary.)

## Mount / Skywatcher Protocol (gaps vs GSServer reference, `../../other/GSServer/GS.SkyWatcher`)

Findings from comparing `SkywatcherMountDriverBase` against GSServer's `SkyWatcher.cs` `AxisPulse`/`AxisSlew` + `Commands.cs` (2026-06-10). **Fixed 2026-06-11** (commits `71de9b7`, `541213d`, `72b6e53`, `84880c5` on `fix/guider-slewing-calibration`), validated against wire transcripts recorded from GSServer's own client code: `tools/GssOracle` drives `GS.SkyWatcher` headless against a scripted serial port (no ASCOM/COM); the recorded transcripts are pinned as the `gss-oracle-transcripts.json` fixture (`SkywatcherGssOracleTests`).

- [x] **`:G` motion-mode payload is a fake-only dialect**; DONE (`71de9b7`): real 2-char `<func><dir>` format via `SkywatcherProtocol.EncodeMotionMode(func, forward, southernHemisphere)` + `SkywatcherMotionFunc` enum (speed bit inverts between goto and slew); all five driver call sites and the fake's parser flipped atomically; `speedChar` dead variable replaced by the func selection. 16-payload table test + oracle round-trip test.
- [x] **Southern-hemisphere direction bit**: DONE (`71de9b7`): every `:G` carries dir bit1 below the equator, tracking/RA pulses run the worm in REVERSE in the south (GSS EqS passes the negated rate), and the steps↔sky conversions mirror with it (`StepsToRa`/`RaToSteps`/`StepsToDec`/`DecToSteps`, equivalent of GSS `Axes.AxesAppToMount` `a[0] = 180 − a[0]`). Fake auto-resumes post-GOTO tracking per the stored hemisphere bit. Southern tracking/goto/pulse functional tests pin it.
- [x] **Pulse guide via live `:I` rate change**; DONE (`541213d`): RA pulse while tracking sends only `:I` (combined rate) then `:I` (sidereal restore, in `finally` so cancellation can't leave the rate stuck); f=1.0 East commands sidereal/1000 instead of halting. Wire-contract test asserts no `:G`/`:J`/`:K` during a tracking RA pulse.
- [x] **Dec pulse as micro-GOTO (GSS `DecPulseGoTo`)**: DONE (`84880c5`): opt-in `?decPulseGoto=true` mount URI key (advanced device setting on `SkywatcherDevice` + fake); duration → exact steps → relative low-speed GOTO (`:G` func 2 + `:H` + `:M 0` + `:J`) polled to FullStop, 3.5 s cap. Rate-based stays default.
- [x] **Wait for FullStop after `:K` before issuing `:G`**; DONE (`72b6e53`): `StopAxisAndWaitAsync` (25 ms polls, re-stop every 5, 3.5 s cap) used by `BeginSlewRaDecAsync`/`ParkAsync`/`MoveAxisAsync`; `SetTrackingAsync(true)` is status-driven (already-running-in-tracking-direction → live `:I` only, GSS `rateChangeOnly`). The fake now REJECTS `:G` while running with `!2` like real firmware, so the whole suite enforces the contract.
- [x] **Minimum pulse duration floor**: DONE (`541213d`): 20 ms floor at the top of `StartPulseGuideAsync`, dropped before touching the wire.
- [x] **`:f` axis-status reply is 3 nibbles, not 6 hex chars** (found via the GSS oracle); DONE (`71de9b7`): nibble0 = mode/dir/high-speed bits, nibble1 = running, nibble2 = init; driver parse + fake reply flipped together.
- [x] **GOTO `:M` break-point increment + speed-tier selection** (found via the GSS oracle); DONE (`71de9b7`): `:H` then `:M` (3500 high-speed / 0 low-speed) then `:J`; low-speed GOTO func within the 640-sidereal-second margin, high-speed beyond it.
- [x] **Iterative goto refinement (EQMOD-style)**: DONE (2026-06-11, believed/true split branch): the goto's RA target steps encode the HA at COMMAND time, so a long slew landed late by the slew duration of sidereal motion (~9' for a multi-hour swing). `IsSlewingAsync` is the completion-detection point: when the axes stop with a goto pending and the residual exceeds 30", it re-issues a short refinement goto (max 2 passes, `Interlocked` gate against concurrent pollers); callers' wait-for-completion loops are unchanged because the mount keeps reporting "slewing" during refinement. `AbortSlewAsync`/`ParkAsync` disarm the pending target. Validated by `GivenTopocentricSkywatcherWhenSlewingToJ2000TargetThenJ2000ReadbackMatches` (readback err 9.4' -> <3').
- [x] **A goto chooses the axis SOLUTION from the target's pier side (GSS `Axes.RaDecToAxesXy`)**: DONE
  2026-08-30 (`SkyToSteps(ra, dec, PointingState)`): east of the meridian -> through the pole (RA axis
  +12 h, Dec axis mirrored through home), decided once per goto and kept for refinement passes; a sync
  keeps the half the Dec encoder is in; `StepsToRa` reads the half off the Dec encoder; home boundary
  inclusive (Normal). Before this every target took the straight solution (eastern targets
  counterweight-UP) and a meridian "flip" re-slewed to identical steps. `GetAxisAngleAsync` exposes the
  folded, hemisphere-corrected axis angle for the mount safety limit's mechanical tier. `SetSideOfPierAsync`
  is the forced flip since 2026-08-30. Hardware validation of all of it: [hardware-validation.md](hardware-validation.md)
  items 1-3. (The EQ6 tracking-after-goto, goto-refinement and Dec-backlash bench checks moved there too,
  items 4, 5 and 10.)

## Mount / Believed vs True Pointing (fakes) + Sky-Map Solve & Sync

DONE 2026-06-11 (believed/true split). A real mount's encoders only know the BELIEVED pointing;
hidden alignment errors (polar misalignment, cone error) are only observable through a camera.
The fakes now model this honestly, and the sky map gained the discovery tool:

- [x] `FakeSkywatcherMountDriver` public `GetRA/GetDec` report the believed (encoder) pointing; the misaligned TRUE pointing moved to the internal `IFakeTruePointingSource.GetTruePointingNativeAsync` seam (all three regimes: near-pole encoder sweep, pre-sync axis tilt, post-sync tracking drift + believed-deviation term).
- [x] `FakeCameraDriver` guide path renders from the true seam; main path shifts the stamped `Target` by the per-exposure `(true - believed)` J2000 delta so plate solves of main frames reveal the hidden error. `FakeGuider.SaveImageAsync` stamps WCS from the true seam (polar-align sim signal preserved). Shared conversion extracted to `EquatorialFrameConversion.TopocentricToJ2000` (one path with `IMountDriver.GetRaDecJ2000Async`).
- [x] Sky-map mount reticle is clickable -> mount info panel -> **Solve & Sync** button: `MountActions.SolveAndSyncAsync` (stamp + preview capture + plate solve + `SyncRaDec` via the profile transform). Marker jumps to truth on the next telemetry poll; re-slew stays the user's decision. Uses the OTA's MAIN camera (`OTAs[OtaIndex].Camera`, index 0 from the button). This is the only truthful-marker path for slew-less trackers (SkyGuider Pro: `CanSlew=false`, `CanSync=true`). Verified end-to-end in the GUI: blind goto lands marker ON target; Solve & Sync revealed a 6.5' cone error; re-goto landed true; second solve showed 1.3' residual (pure tracking drift).

## Protocol Support

- [x] iOptron SkyGuider Pro (SGP) mount driver: `SgpMountDriverBase<T>` with custom serial protocol at 28800 baud, RA-only axis, pulse guiding via timed move, CameraSnap support, `FakeSgpSerialDevice` for testing
- iOptron SkyGuider Pro handbox firmware patch feasibility (STM32F103): needs the handbox + a probe, bench issue #657.

