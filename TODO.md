# TODOs

## High Priority

- [x] MiniViewer: optional lightweight mode that skips storing UnstretchedImage — for live preview where we never re-stretch, just keep stats + GPU texture. Saves ~140MB per displayed frame
- [x] Cache altitude chart as texture — only re-render the mouse follower overlay on hover, not the entire chart. Currently 20% GPU on mouse hover due to full chart redraw per frame
- [x] TianWen.Lib.Hosting remote API — ASP.NET Core Minimal API + WebSocket for headless Raspi operation. Multi-OTA native routes (`/api/v1/ota/{index}/camera/info`) with ninaAPI v2 compatibility shim (`/v2/api/*` → OTA[0]) so Touch N Stars works for single-scope setups. All 4 phases complete: read-only state, control, ninaAPI shim (equipment info/control, sequence, images, WebSocket, device lifecycle, guider graph, move-axis), profile CRUD + pending target queue. `tianwen-server` headless executable published as AOT binary for all platforms
- [ ] PlayerOne Astronomy / ToupTek / SVBony native drivers — these vendors use ZWO-compatible SDKs with different library prefixes (PlayerOne: `PlayerOneCamera`, ToupTek: `toupcam`/`starshootg`, SVBony: `SVBCameraSDK`). Investigate sharing `ZWODeviceSource`/`ZWOCameraDriver` infrastructure with a pluggable SDK shim rather than duplicating per vendor. NINA uses a `ToupTekAlike` pattern for this family. Cameras, filter wheels, and focusers where applicable

## Flaky CI Tests

- [x] `SessionImagingTests.GivenHighAltitudeTarget...HighUtilization` — fixed: cooperative time pump (`ExternalTimePump + Advance`)
- [x] `SessionImagingTests.GivenDitherEveryNth...DitheringTriggered` — fixed: same root cause (SleepAsync pump race)
- [x] `SessionImagingTests.GivenFocusDrift...AutoRefocusTriggered` — fixed: same root cause
- [x] `SessionPhaseTests.AbortDuringCooling_StopsRampAndWarmsBack` — fixed: removed wall-clock CancellationTokenSource timeouts

## Next Up

- [x] QHYCCD device support — native camera, filter wheel (camera-cable + standalone serial QHYCFW3), and QFOC focuser (Standard + High Precision) drivers. JSON-over-serial protocol for QFOC with typed records and AOT-safe `QfocJsonContext`. Three-phase discovery in `QHYDeviceSource`: cameras → serial probe → camera-cable CFW check
- [x] Weather overlay in planner — hourly forecast from Open-Meteo (free, no API key) with layered color emoji (rain/snow/thunder/fog/cloud/sun/moon), file-cached with 1h TTL + offline fallback. Weather as full device type (IWeatherDriver) with equipment/profile integration
- [x] Planner: show Moon phase + position — altitude curve on the chart with phase emoji (hemisphere-aware). Uses Meeus lunar ephemeris via VSOP87a pipeline
- [ ] Moon penalty in target scoring — penalise targets within ~30° of a bright Moon (illumination × proximity factor). Compute angular separation per target in ObservationScheduler.ScoreTarget
- [ ] Live viewer: camera switching — allow selecting which OTA's camera to preview in both GUI MiniViewer and TUI Sixel preview (currently always shows first available)
- [x] Guider graph: connect dots with lines (Bresenham or anti-aliased) instead of scatter dots — users expect smooth curves like PHD2
- [x] Guider graph: scrolling window (last N samples) with dynamic Y scale and grid lines at integer arcsec
- [x] Guider graph: reuse the existing LiveSessionTab guide graph widget — the guider tab should show a larger version of the same graph, not a separate implementation. Extract shared graph rendering
- [ ] DIR.Lib: add `FillEllipse`/`FillCircle`/`DrawEllipse`/`DrawCircle` primitives to `PixelWidgetBase` — currently everything is built from `FillRect` scanlines
- [x] Guider graph: show applied correction pulses (RA/Dec duration bars) alongside error — log-scaled bars (blue RA / orange Dec) extending up/down from zero line
- [ ] SyntheticStarFieldRenderer: refactor 20-parameter methods into records/structs
- [x] Guider graph: show dither events (markers/shading) — yellow dashed vertical lines at dither events, dim yellow settling shading
- [x] Guider tab: keep looping guide camera frames during centering/slewing — call `LoopAsync` when not guiding so the guide camera feed stays live. Currently the guide loop stops during centering and the tab shows "Waiting for guider"
- [x] Guider tab: show calibration frames — render guide camera during calibration phase with star position and profile. Remaining: star movement vectors, step count, and calibration progress overlay
- [ ] Fake camera: apply mount tracking drift as pixel offset to star positions — `SyntheticStarFieldRenderer` produces a fixed star field so pulse guide corrections are invisible, causing `GuiderCalibration` to never converge (zero displacement). Need to read accumulated RA/Dec drift from `FakeMountDriver` and translate to pixel shift
- [x] Guider tab: guide camera image + crosshair (done). Remaining: star close-up + 1D intensity profile
  - [x] Add to `IDeviceDependentGuider`: `Image? LastGuideFrame`, `(float,float)? GuideStarPosition`, `float? GuideStarSNR`, `float? GuideStarHFD`
  - [x] Surface on `ISession` via `LiveSessionState.PollSession`
  - [x] `BuiltInGuiderDriver`: expose from `GuideLoop`'s `GuiderCentroidTracker`
  - [x] `FakeGuider`: generate synthetic guide frames with star field
  - [x] GUI: guide camera Canvas + crosshair overlay + SNR + frame counter
  - [x] GUI: star profile panel with 1D H/V intensity cross-sections + Gaussian fits + FWHM
  - [ ] PHD2: no image (show placeholder), SNR/mass from event stream only
- [x] Live session: show dither state — guider header shows `[Settling 0.42px]` with live distance, `[Paused (Slewing)]` during slews, correction arrows `[Guiding →142ms ↑38ms]`
- [ ] Cooling graph: same scrolling window treatment
- [ ] VSOP87 vectorization — convert 43K lines of hardcoded `amplitude * Cos(phase + frequency * t)` into coefficient arrays, evaluate with `Vector256<double>` (AVX2). Process 4 terms per iteration. Requires source generator or one-time conversion of all planet files (EarthX/Y/Z, MarsX/Y/Z, etc.)
- [ ] CLI: `train-guide-model` command for offline epoch training of the neural guide model — connects to mount + guide camera, records guide data for N worm cycles, then runs `TrainEpoch` with real PE data as teacher signal. Produces a base `.ngm` model file for the optical train. Aimed at permanent setups where users can invest a one-time training session to get a high-quality starting model. The online trainer (`TrainOnBatch`) should eventually converge to the same quality — offline training just gets there faster by seeing many PE cycles upfront instead of learning incrementally
- [ ] Equipment tab: fully data-driven profile panel — replace hardcoded `RenderProfileSlot` calls (mount, guider, guider cam/foc) with a declarative slot model that includes special sections (site editing, focal length input, device settings) as metadata. Currently core slots are hardcoded and only "extra" slots (weather, future types) are rendered dynamically via `EquipmentContent.GetExtraProfileSlots`. Goal: single loop over all slots with pluggable section renderers.
- [ ] Equipment tab: generic per-device settings pane — each device type (camera, mount, guider, etc.) should declare configurable properties via URI query params (like BuiltInGuiderDevice), and the equipment tab renders them automatically. FakeDevice should carry PE amplitude, period, guide rate etc. as URI params so FakeCameraDriver initializes from them instead of hardcoded defaults
- [ ] Fake camera: shift/change star field during slews — currently renders the same fixed seed regardless of mount pointing
- [ ] Fake camera: scale synthetic background noise with exposure duration in `SyntheticStarFieldRenderer` — long subs (≥60s) have unrealistically clean backgrounds, causing per-channel stretch to produce degenerate parameters. Real cameras accumulate sky glow + dark current + read noise over time.

- [x] Fake filter wheels should have pre-installed filters (realistic filter sets per device ID)
- [ ] Planner: disambiguate duplicate common names — when multiple catalog entries share the same display name (e.g. NGC 4038 and NGC 4039 both named "Antennae Galaxies"), append the catalog designation in brackets: "Antennae Galaxies (NGC 4038)"
- [ ] Planner: full rescan when site coordinates change significantly (>1°) instead of fast-path recompute — currently changing lat from -37 to 50 keeps southern-hemisphere targets with 0° altitude
- [ ] Extract VkImageRenderer UI layout to Abstractions — toolbar, file list, status bar, hit testing are renderer-agnostic; image rendering + texture upload stay Vulkan-specific in Shared
- [ ] Viewer tab renders at (0,0) ignoring contentRect — refactor ImageRendererBase.Render to accept a contentRect like PlannerTab/SessionTab/EquipmentTab so it works correctly when embedded in the tabbed GUI
- [x] Pinned items in planner should persist to disk — auto-save/load via `PlannerPersistence` keyed by profile+date, stored under `{OutputFolder}/Planner/{profileId}/{date}.json`
- [ ] Seed focuser `MaxStep` from hardware during ZWO EAF discovery (same `seedQueryParams` pattern as EFW slot count)
- [ ] Remember last focus position in profile URI after auto-focus (save after every auto-focus attempt, whether successful or not) so the focuser can start near the last known good position on next session
- [ ] HFD drift detection via linear regression over last N frames (NINA uses `AutofocusAfterHFRIncreaseTrigger` with configurable `SampleSize` and `Amount` threshold) — more robust than single-frame ratio comparison, reduces false refocus triggers
- [ ] Use IWeatherDriver ambient temperature for camera warm-up — when no hardware weather station or external temp sensor (Pegasus Astro) provides heat sink temp, pass ambient temp from weather driver as a denormalised property to the camera driver (via Session orchestration, not direct driver-to-driver coupling). Use as ambient target for `CoolCamerasToAmbientAsync` ramp
- [ ] SafetyMonitor integration — ASCOM `ISafetyMonitor` driver polling (5s interval watchdog) that can interrupt imaging and stop tracking when unsafe. Gate on safety in dither, meridian flip, and centering triggers. Park scope on unsafe condition.
- [ ] TUI Sixel preview in live session tab — render last captured frame as Sixel in a right panel (ConsoleImageRenderer + EncodeSixel). Needs async CreateFromImageAsync in the render loop or pre-render on frame arrival.
- [ ] SDL window icon for non-Windows — `<ApplicationIcon>` only embeds in the PE for Windows. On Linux/macOS, need `SDL.SetWindowIcon` with a surface loaded via `SDL_image.IMG_Load` (requires adding SDL3_image package) or `SDL.LoadBMP` (requires BMP conversion). Also set `.desktop` file icon on Linux.

## Observation Scheduler (PLAN-SessionTests.md)

### Done

- [x] **ObservationPriority enum** — `High`, `Normal`, `Low`, `Spare` priority levels
- [x] **ProposedObservation record** — user-facing proposal with optional gain/offset/exposure/duration
- [x] **ScheduledObservation record** — resolved observation with concrete start/duration/gain/offset (replaces old `Observation`)
- [x] **ScheduledObservationTree** — `IReadOnlyList<ScheduledObservation>` with per-slot spare target fallback via `TryGetNextSpare`
- [x] **TargetScore** — altitude-integrated scoring with elevation profile and optimal window
- [x] **DeviceQueryKey enum** — typed URI query key access with C# 14 extension block (`gain`, `offset`, `latitude`, etc.)
- [x] **ObservationScheduler.CalculateNightWindow** — computes night boundaries with high-latitude fallback chain (AmateurAstroTwilight → NauticalTwilight for evening, AstroTwilight → NauticalTwilight for morning); handles polar night (24h window) and post-midnight twilight onset (Dublin summer solstice)
- [x] **ObservationScheduler.ScoreTarget** — altitude-above-minimum scoring across time bins with optimal window extraction
- [x] **ObservationScheduler.Schedule** — full scheduling pipeline: score → sort by priority/score → allocate time bins → attach spare targets per slot → resolve nullable defaults
- [x] **ObservationScheduler.ResolveGain/ResolveOffset** — 3-tier resolution: explicit → URI query → interpolation/default
- [x] **SOFAHelper bug fix** — `AmateurAstronomicalTwilight` case had `altitiude0 = AMATEUR_ASRONOMICAL_TWILIGHT` (assignment) instead of `altitiude0 -= AMATEUR_ASRONOMICAL_TWILIGHT` (subtraction)
- [x] **Session spare target fallback** — when primary target is below horizon or slew impossible, try spare targets before advancing to next slot
- [x] **SessionFactory.Create(proposals)** — new overload that builds `Transform` from mount URI, resolves defaults from camera URI, and calls `ObservationScheduler.Schedule`
- [x] **SessionFactory refactored** — extracted `CreateSetup` helper to share device wiring between the two `Create` overloads
- [x] **SessionConfiguration.DefaultSubExposure** — new optional field for scheduler default resolution
- [x] **ISession/ISessionFactory updated** — `PlannedObservations` → `Observations` (tree), `Observation` → `ScheduledObservation` throughout
- [x] **Tests**: 18 tests in `ObservationSchedulerTests` covering scoring, scheduling, priority ordering, spare target attachment, gain/offset resolution, night window calculation (Vienna summer, Melbourne winter, Germany winter solstice, Dublin summer solstice, Tromsø polar night), and full schedule-with-calculated-window integration
- [X] Unify scoring into a single path (remove one-Fast variant)

### Not Yet Done

- [ ] Integrate scheduler into `Session.RunAsync` flow — currently `ObservationLoopAsync` iterates linearly; needs to respect `ScheduledObservation.Start` times (wait until scheduled start before slewing)
- [x] Time-aware observation switching — `ImagingLoopAsync` computes `maxTicks` from `observation.Duration` and advances when `tickCount >= maxTicks`
- [x] Weather/cloud interruption handling — condition deterioration detection via star count ratio vs baseline; pauses guiding, polls with test exposures, resumes or advances after configurable timeout (`ConditionDeteriorationThreshold`, `ConditionRecoveryTimeout`); synthetic cloud simulation in `SyntheticStarFieldRenderer` for testing
- [ ] Multi-night scheduling — carry over incomplete observations to next session with accumulated exposure tracking
- [x] Filter support in ProposedObservation — `ImmutableArray<FilterExposure>? FilterPlan` with `FilterPlanBuilder.BuildAutoFilterPlan` altitude-ladder ordering
- [x] Mosaic panel support — `MosaicGenerator` computes panel grids, `ProposedObservation.MosaicGroupId` links panels for contiguous scheduling with RA-ascending (meridian-aware) ordering
- [ ] Scoring: calculate how large the object is in pixels on the sensor (normalizes across different telescopes)
- [ ] Scheduler UI/CLI integration — expose `ProposedObservation` input and `ScheduledObservationTree` output in CLI and future UI
- [ ] Generalise `TonightsBest` to accept an arbitrary LST / `DateTimeOffset` (not just current UTC)
- [ ] Persistent observation database — save/load proposals and completed exposure history
- [ ] Use custom TIFF instead of Magicks for both reading and writing (both the tiling and striping one)
- [ ] Use custom PNG (we have reading but will need writing too, thumbnails)
- [ ] Support arbitrary image formats for loading and saving using Magick.NET for all the other formats

## Session Test Plan Progress (PLAN-SessionTests.md phases)

- [x] **Phase 2**: FakeCamera cooling simulation (commit 9ae4490)
- [x] **Phase 3**: FakeFocuser temperature + focus model (commit 25ce32d)
- [x] **Phase 4**: Synthetic star field renderer (commit 6fee8fb)
- [x] **Phase 5 partial**: Backlash property on IFocuserDriver, FocusDirection 2x2 matrix (commit 25ce32d)
- [x] **Phase 6 partial**: AutoFocusAsync with V-curve + hyperbola fitting, per-target baseline HFD (commits 25ce32d, 68d061c)
- [x] **Phase 1**: FakeGuider state machine — full state machine (Idle, Looping, Calibrating, Guiding, Settling) with atomic transitions
- [ ] **Phase 5 remaining**: BacklashMeasurement.MeasureAsync, backlash-compensated moves
- [x] **Phase 6 remaining**: Focus drift detection in ImagingLoopAsync (HFD threshold check + auto-refocus trigger)
- [ ] **Phase 7a**: Observation duration enforcement in imaging loop
- [x] **Phase 7b**: PeriodicTimer replacing hand-rolled sleep/overslept timing
- [ ] **Phase 7c**: Full Session integration tests (tests 1-12 from plan)

## Sequencing / Session

- [ ] Baseline HFD per-target: key by observation index (not telescope index), smart refocus-on-target-change — skip refocus when recent focus is good, establish new baseline from median of first N frames; `AlwaysRefocusOnNewTarget` config option
- [ ] FOV obstruction detection: compare first-frame metrics against previous target's baseline; if anomalous, nudge mount up by one frame radius — if metrics recover, exit imaging loop for this target (tree/building in FOV)
- [ ] OnStep mount driver: extend `MeadeLX200ProtocolMountDriverBase` with `:GX`/`:SX` commands, native pier side, park/unpark, `FakeOnStepSerialDevice` for testing
- [ ] Faster imaging loop tick: reduce to `GCD/6` clamped `[1s, 5s]` — fix `FakeMeadeLX200SerialDevice` slew timer interleaving (immediate axis positioning instead of 100ms step timer)
- [ ] `SessionFactory.Create(proposals)` hardcodes `defaultObservationTime = 30min` — should use planner's computed windows (handoff slider positions) or at least divide the dark window evenly among targets
- [ ] Gracefully stop a session (`HostedSession.cs:39`)
- [ ] Wait until 5 min to astro dark, and/or implement `IExternal.IsPolarAligned` (`Session.cs:61`)
- [ ] Maybe slew slightly above/below 0 declination to avoid trees, etc. (`Session.cs:235`)
- [x] Plate solve, sync, and re-slew after initial slew — `PlateSolveAndSyncAsync` called after slew in `ObservationLoopAsync` and `InitialRoughFocusAsync`
- [x] ~~Wait until target rises again instead of skipping~~ — replaced by spare target fallback in observation loop, todo
      Maybe we should estimate how long it will take for the target to appear, i.e. by slewing where it _will_ be in lets say half an hour and see if we can get more stars
      etc there
- [ ] Plate solve and re-slew during observation (`Session.cs:467`)
- [ ] Per-camera exposure calculation, e.g. via f/ratio (`Session.cs:540`)
- [x] Stop exposures before meridian flip (if we can, and if there are any) — `PerformMeridianFlipAsync` stops guider, waits for slew completion, smart exposure handling (<30s wait / >30s abort)
- [x] Stop guiding, flip, resync, verify, and restart guiding — `PerformMeridianFlipAsync` stops capture, re-slews with RA offset, verifies HA flipped positive, restarts guiding loop
- [ ] Make FITS output path template configurable (`Session.IO.cs:16`) — frame type already in path as `{target}/{date}/{filter}/{frameType}/`
- [ ] FOV obstruction detection: if first frames on a new target show HFD way higher or star count way lower than previous target's baseline, nudge mount up in altitude by one frame radius and re-check — if metrics recover, something is blocking the FOV (tree, building); make this a new imaging loop exit condition
- [x] Switch `ImagingLoopAsync` to `PeriodicTimer` instead of hand-rolled sleep/overslept timing
- [ ] Device disconnect resilience in imaging loop — when mount/camera/guider disconnects, attempt reconnect with backoff instead of immediately advancing to next observation; only bail after N retries or timeout
- [x] Altitude check distinguishes rising vs setting targets — `EstimateTimeUntilTargetRisesAsync` samples altitude at 5-min intervals; if rising and within `MaxWaitForRisingTarget` (default 15 min), waits then retries slew; otherwise tries spare targets then advances
- [x] Write `FOCALLEN` and `FOCUSPOS` to FITS output headers (currently read on load but never written)
- [x] Write `DATAMIN` to FITS output headers (only `DATAMAX` was written)
- [x] `FocusDriftThreshold` default changed from 1.3 (30%) to 1.07 (7%); already a `SessionConfiguration` setting

## Live Session Tab (Phase 2 — Polish)

- [x] Guide star profile bitmap from guider (rendered in GuiderTab star profile panel)
- [ ] Inline V-curve charts in focus history panel
- [ ] Per-filter frame count breakdown in stats
- [ ] Meridian flip countdown indicator
- [x] Dither event markers on guide graph
- [ ] Click exposure log entry → open in Viewer tab
- [ ] Exposure log thumbnails: 128px height, preserve aspect ratio
- [ ] Finalise as background task — keep UI responsive during park/warmup after abort/complete

## Flaky Tests

- [x] `SessionObservationLoopTests.GivenRefocusOnNewTargetWhenSwitchingTargetsThenBaselineStoredPerTarget` — fixed: cooperative time pump, `[Collection("Session")]` serialization, removed wall-clock timeouts

## Camera / ICameraDriver

- [ ] Consider using external temp sensor if no heatsink temp is available (`ICameraDriver.cs:314`)

## DAL Camera Driver

- [ ] Implement trigger for ReadoutMode (`DALCameraDriver.cs:290`)
- [ ] Add proper exceptions for `SetCCDTemperature` setter (`DALCameraDriver.cs:381`)
- [ ] Add proper exceptions for `Offset` getter (`DALCameraDriver.cs:661`)
- [ ] Support auto-exposure (`DALCameraDriver.cs:848`)

## Alpaca Drivers

- [ ] Query tracking rates from Alpaca when endpoint supports enumeration (`AlpacaTelescopeDriver.cs:46`)
- [ ] Parse axis rates from Alpaca response (`AlpacaTelescopeDriver.cs:315`)
- [x] Implement string[] and int[] typed getters for filter names and focus offsets (`AlpacaClient.cs`)
- [ ] Parse string[] from Alpaca for `Offsets` (`AlpacaCameraDriver.cs:238`)
- [ ] Parse string[] from Alpaca for `Gains` (`AlpacaCameraDriver.cs:248`)
- [ ] Alpaca `imagearray` endpoint requires special binary handling (`AlpacaCameraDriver.cs:258`)
- [ ] Async call to `lastexposureduration` endpoint (`AlpacaCameraDriver.cs:262`)

## ASCOM Drivers

- [ ] Implement axis rates for telescope (`AscomTelescopeDriver.cs:320`)
- [ ] Support ASCOM `Setup()` method — call the driver's native setup dialog for device-specific configuration

## Mount / Meade LX200 Protocol

- [ ] Implement effective `:Gm#` command — ask Johansen (Melbourne) if he knows how to get it or how to use `:E;` to retrieve state
- [ ] Determine precision based on firmware/patchlevel (`MeadeLX200ProtocolMountDriverBase.cs:43`)
- [ ] LX800 fixed GW response not being terminated, account for that (`MeadeLX200ProtocolMountDriverBase.cs:143`)
- [ ] Pier side detection only works for GEM mounts (`MeadeLX200ProtocolMountDriverBase.cs:305`)
- [ ] Support `:RgSS.S#` to set guide rate on AutoStar II (`MeadeLX200ProtocolMountDriverBase.cs:573,583`)
- [ ] Verify `:Q#` stops pulse guiding as well (`MeadeLX200ProtocolMountDriverBase.cs:873`)
- [ ] Use standard atmosphere for `SitePressure` (`IMountDriver.cs:344`)
- [ ] Check online or via connected devices for `SiteTemperature` (`IMountDriver.cs:345`)
- [ ] Handle refraction — assumes driver does not support/do refraction (`IMountDriver.cs:347`)

## Device Management

- [ ] Try to parse URI manually in Profile fallback (`Profile.cs:130`)

## Code Quality / Architecture

- [x] **Async transport layer — `ConnectSerialDevice` is async at heart now.** Done: `DeviceBase.ConnectSerialDeviceAsync` returns `ValueTask<ISerialConnection?>`; `IExternal.OpenSerialDeviceAsync` wraps the synchronous BCL `SerialPort.Open` in `Task.Run` so no driver thread blocks; `TcpSerialConnection.CreateAsync` awaits `TcpClient.ConnectAsync` cooperatively with a cancellable 3 s timeout; every override (`MeadeDevice` via base, `OnStepDevice`, `SkywatcherDevice`, `FakeDevice`, `IOptronDevice`) and every caller (`MeadeLX200ProtocolMountDriverBase`, `SgpMountDriverBase`, `SkywatcherMountDriverBase`, `QHYFocuserDriver`, `QHYSerialControlledFilterWheelDriver`, 5 device-source scanners) updated in one commit.
- [ ] **Signal handler cleanup — route, don't implement.** Audit of `AppSignalHandler.cs` against the CLAUDE.md rule found these violations:
  - [ ] `StartSessionSignal` (~line 1230) — **violates** — inlines transform construction, schedule→observations copy loop, `config with { ... }` site+setpoint injection, factory init+create. Extract `SessionBootstrapper.BuildAndStartAsync(plannerState, sessionState, liveSessionState, profile, factory, tracker, ct)` or `LiveSessionActions`
  - [ ] `TakePreviewSignal` (~line 1385) — **violates** — full camera-capture sequence (gain, binning, start exposure, `while` loop polling `GetImageReadyAsync`). Extract `EquipmentActions.CapturePreviewAsync(camera, sig, timeProvider, ct)`
  - [ ] `ConnectDeviceSignal` (~line 911) — `foreach` over `eqState.DiscoveredDevices` with `DeviceBase.SameDevice` match. Extract `EquipmentActions.ResolveDeviceForConnect(hub, discoveredDevices, uri)`
  - [ ] `AssignDeviceSignal` (~line 830) — auto-disconnect of orphaned previous device inline (`GetDisconnectSafetyAsync` + branch + `hub.DisconnectAsync` + status). Extract `EquipmentActions.AutoDisconnectOrphanAsync(hub, prevSlotUri, expectedType, logger, ct)`
  - [ ] `SetCoolerSetpointSignal` (~line 1080) / `SetCoolerOffSignal` (~line 1123) — two-step temp+cooler sequences. Extract `EquipmentActions.SetCoolerSetpointAsync` / `SetCoolerOffAsync`
  - [ ] `UpdateProfileSignal` (~line 1143) + `AssignDeviceSignal` — conditional `FetchWeatherForecastAsync` duplicated. Extract shared `RefreshWeatherIfNeededAsync(prevWeatherUri, newWeatherUri, ct)`
  - [ ] `SaveSnapshotSignal` (~line 1466) — file-naming policy inline (`"Snapshot"` subfolder, date-stamped folder, `GetSafeFileName`). Extract `SnapshotPersistence.SaveAsync(image, otaIndex, external, timeProvider, ct)`
  - [ ] `JogFocuserSignal` (~line 1541) — read-pos + compute-target + `BeginMoveAsync`. Extract `EquipmentActions.JogFocuserAsync(focuser, steps, ct)`
- [ ] **OnStep follow-ups** (leftover from the OnStep commit series):
  - [ ] MoveAxis via `:Mn/:Ms/:Me/:Mw#` + `:Qe/Qw/Qn/Qs#` + `:RA/:RE` rates — enables direct jog buttons in GUI
  - [ ] Per-axis guide-rate setter via `:Rn#` (index 0–9) + `:GX90#` query — enables `CanSetGuideRates = true` on the OnStep override
  - [ ] Test `EquipmentActions.ReconcileAllProfilesAsync` with a fake `IExternal` that captures `AtomicWriteJsonAsync` — orchestration layer currently untested; unit tests only cover `ReconcileProfileData`
  - [ ] mDNS bind fallback — if port 5353 is owned by Bonjour/Avahi, bind to an ephemeral UDP port and accept unicast responses (currently silently returns empty results). Common on macOS
  - [ ] "Add unseen device" button in equipment tab — today WiFi OnStep mounts that don't advertise mDNS require hand-editing the profile JSON. Add a modal with host + port fields that constructs an `OnStepDevice` and injects it into discovery cache
  - [ ] Parse SRV records in `ParseMdnsResponse` to pick up non-default TCP ports. Currently assume 9999; some firmware advertises a different port via SRV
- [ ] Split `IDeviceSource<T>` discovery role from per-device driver role. Several drivers fuse both into one class and rely on a placeholder/"default root device" ctor so DI can construct the singleton:
  - `OpenPHD2GuiderDriver` — singleton ctor synthesizes a `MakeDefaultRootDevice(external.DefaultGuiderAddress)` just to satisfy `_guiderDevice`; only `_equipmentProfiles` is meaningful in the discovery role
  - `QHYDeviceSource` / `ZWODeviceSource` / `AscomDeviceIterator` etc. — review for the same smell
  - Proper fix: separate `OpenPHD2DeviceSource : IDeviceSource<OpenPHD2GuiderDevice>` (no device field) from `OpenPHD2GuiderDriver : IGuider` (constructed only via `OpenPHD2GuiderDevice.NewInstanceFromDevice`). Mirror pattern across other dual-role classes
- [ ] Replace `IReadOnlyList<T>` in parameters with `ReadOnlySpan<T>`, return types with `ImmutableArray<T>` — gradual migration for better perf semantics and thread safety
- [ ] Abstract redraw flag propagation in TUI main loop — register `INeedsRedraw` state objects instead of listing `plannerState.NeedsRedraw || sessionState.NeedsRedraw || ...` manually
- [ ] Live Session tab: `RollingGraphWidget<TSurface>` extracted to DIR.Lib (reusable for guide graph, cooling graph, future charts)

## External / Infrastructure

- [ ] Free unmanaged resources and override finalizer in `External.Dispose` (`External.cs:85-91`)
- [ ] Actually ensure that FITS library writes async (`IExternal.cs:226`)
- [ ] Write an MCP server for TianWen (expose session status, device state, observation schedule)

## Imaging

- [ ] Not sure if `SensorType` LRGB check is correct (`SensorType.cs:54`)
- [ ] Find bounding box of non-NaN region in `Image.cs` (for stacked images with NaN borders)
- [ ] Star detection noise robustness: `FindStarsAsync` with `snrMin: 5` picks up false positives from shot noise halos around bright stars (e.g. M42 synthetic field: 49 rendered stars → 64 detected). Consider deblending or a minimum star separation filter to reject noise peaks near bright stars.

## Stretch / Image Processing

Learnings from PixInsight Statistical Stretch (SetiAstro, v2.3).

- [x] Luma-only stretch mode (Rec. 709 luminance, stretch Y, scale RGB by Y'/Y)
- [x] HDR compression in GPU shader (Hermite soft-knee, `uHdrAmount`/`uHdrKnee` uniforms)
- [ ] Normalize after stretch — `x / max(x)` to fill full [0,1] range
- [ ] Iterative convergence — multiple stretch iterations until median converges to target
- [ ] Luma blend — smoothly blend between linked and luma-only results

## FITS Viewer

- [ ] Rename HDR button/label to "Compress Highlights"
- [ ] Remove debug `Console.Error.WriteLine` WCS output from `Program.cs`
- [ ] Support rec601/rec2020 luminance weighting options in luma stretch
- [ ] Grid label formatting: show arc-seconds for very narrow FOVs
- [ ] Crosshair / reticle overlay at image center
- [x] Annotation overlay (object names from catalogs when plate-solved)
- [x] Star detection overlay: `FitsDocument.DetectStarsAsync()` runs as background task,
      draws HFD-sized green circles, shows count/HFR/FWHM in status bar (S key toggle)
- [x] Clip star overlay circles to image viewport + fix centroid alignment (+0.5px offset)
- [ ] Remember last opened folder and recent images across sessions
- [ ] Continuous image advance when holding arrow keys (advance every ~1 second while pressed)
- [ ] Display original bit depth before normalization (e.g. "16-bit" in status bar) when available from FITS header
- [ ] Star profile tooltip: show radial profile plot (flux vs. distance) when mouse hovers over a detected star
- [ ] Named star labels: match detected stars against Tycho2 via WCS→RA/Dec projection,
      label with cross-catalog names (HIP, HD) using `TryGetCrossIndices`
- [x] Replace custom `AsyncLazy<T>` with `DotNext.Threading.AsyncLazy<T>` (already a dependency in TianWen.Lib)
- [x] Use a `WeakReference<AstroImageDocument>` cache (keyed by file path) so that cycling through
      images can reuse recently loaded documents without keeping them pinned in memory
      (`DocumentCache` with `ConditionalWeakTable` + `WeakReference<T>`)
- [ ] Investigate `DotNext.Threading.RandomAccessCache<TKey, TValue>` (or similar bounded cache)
      as an alternative to `WeakReference` for the document cache — may offer better eviction control

## Astrometry / Catalogs

- [x] Update lib to accept spans in `CatalogUtils` (`CatalogUtils.cs:326,360`)

## Astrometry / Plate Solving

- [ ] Extract distortion model (SIP polynomial coefficients) from plate solver output
- [ ] Implement image undistortion using extracted distortion model

## Astrometry / Catalogs (Queries)

- [ ] Check if SIMBAD supports angular size + dimensions in queries

## Testing

- [ ] `ObjectType.IsStar()` helper method
- [ ] VDB has objects listed as `Be*`, but in HIP we only know stars (`*`) (`CelestialObjectDBTests.cs:73`)
- [ ] Read WCS from FITS file in `FakePlateSolver` (`FakePlateSolver.cs:26`)
- [ ] See if fake mounts (`FakeMountDriver` and `FakeMeadeLX200ProtocolMountDriver`) can share a mount-specific base class

## Statistics

- [x] Find a faster way to multiply all values in an array/span (`StatisticsHelper.cs:167`)
      Replaced manual `Vector<T>` loops in `StatisticsHelper`, `VectorMath`, `Image`, and DSP
      classes with `System.Numerics.Tensors` (`TensorPrimitives`) — SIMD-accelerated one-liners.
- [x] Run star detection and use the mask to exclude stars from background estimation.
      `ScanBackgroundRegion` accepts optional `BitMatrix? starMask`, re-scanned with
      48×48 squares after detection. Star mask reused from `StarList.StarMask`.

## Guider

- [ ] `appState` parameter should probably be an enum (`GuiderStateChangedEventArgs.cs:34`)
- [ ] Decide whether to ship a pretrained neural guide model (or train from scratch per-mount)
- [ ] Guider profile should use profile id (not name) for model persistence and lookup
- [ ] Write guide logs (CSV) into folder next to model weights for post-session analysis
- [ ] Investigate if increasing neural model parameters (wider/deeper MLP) improves guide accuracy
- [ ] Investigate improving pretrained model with real-time mount telemetry data
- [x] Built-in guider receives same mount driver instance via `IMountDependentGuider` wiring in `SessionFactory`
- [ ] Support ST-4 guide port as guiding output (DAL already detects `HasST4Port`)
- [ ] Support snap/shutter-release port for external camera triggering

## Protocol Support

- [ ] GSS ServoCAT / SiTech protocol support + simulator
- [x] iOptron SkyGuider Pro (SGP) mount driver — `SgpMountDriverBase<T>` with custom serial protocol at 28800 baud, RA-only axis, pulse guiding via timed move, CameraSnap support, `FakeSgpSerialDevice` for testing
- [ ] iOptron SkyGuider Pro: investigate patching the SGP handbox firmware (STM32F103, same as iOptron SmartEQ) to support the standard iOptron serial protocol, enabling features like position reporting and goto
- [ ] iOptron SkyGuider Pro: device identity — no UUID mechanism available (firmware has no user string storage, doesn't read STM32 hardware UID); falls back to firmware version + port name
- [ ] Generic iOptron serial protocol support (SmartEQ, CEM series) — same 28800 baud, similar command set but with position feedback
- [ ] SGP pulse guiding should restore previous speed not just siderial (wait Pulse guiding is wrong, it will be 1x siderial but SGP has a different guide rate configured) or make this configurable; alternative: if guide rate is 0.5, half guide pulse time by 2

## Upstream Extraction (to SharpAstro NuGet packages)

- [ ] Move `FileDialogHelper` to DIR.Lib — cross-platform native file picker (comdlg32/zenity/osascript), zero TianWen dependencies
- [ ] Move `Stat/` DSP suite to DIR.Lib — 12 files: FFT, DFT, 25+ window functions, Catmull-Rom splines, StatisticsHelper, AggregationMethod; all pure math with no astro imports (note: DFT/FFT missing namespace declarations)

## SdlVulkan.Renderer

- [x] Font atlas corruption — root cause: shared upload buffer race with `MaxFramesInFlight=2`. Frame N+1's `Flush` overwrites the upload buffer while frame N's `vkCmdCopyBufferToImage` is still reading it. Fixed with `vkDeviceWaitIdle()` before upload buffer reuse.
- [ ] Replace `vkDeviceWaitIdle` in font atlas `Flush` with per-frame upload buffers (like `_vertexBuffers`) to avoid GPU stall on every glyph upload. The current fix blocks the GPU pipeline unnecessarily after startup.

### SdlEventLoop (DONE — all consumers now use the shared loop)
- [x] Add `DropFile` event support (`EventType.DropFile`) — `Action<string>? OnDropFile`
- [x] Multi-button mouse: `OnMouseDown` passes button ID + click count (`Func<byte, float, float, byte, bool>?`)
- [x] `OnMouseUp` passes button ID (`Action<byte>?`)
- [x] `OnMouseWheel` passes tracked mouse position (no more hardcoded 0, 0)
- [x] F11 fullscreen removed from loop — each consumer handles it in `OnKeyDown`
- [x] Migrated `TianWen.UI.FitsViewer/Program.cs` to use `SdlEventLoop`
- [ ] Touch input: pinch-to-zoom via `SDL_EVENT_FINGER_*` or `SDL_EVENT_MULTIGESTURE` events (single-finger drag works as mouse emulation)

## Vulkan Migration / HDR Display Output

Investigation into whether Silk.NET can support HDR display output (HDR10, scRGB, wide color gamut).

### Current Status: OpenGL Cannot Do HDR on Windows

- GPU vendors (NVIDIA, AMD) block 10-bit and floating-point pixel formats for OpenGL in windowed mode
- Windows HDR compositor (DWM) requires a DXGI swapchain, which only DirectX can drive natively
- GLFW has no HDR support — [issue #890](https://github.com/glfw/glfw/issues/890) open since 2016, never implemented
- GLFW 3.4 (Feb 2024) shipped without it; a proposed `GLFW_FLOAT_PIXEL_TYPE` patch was never merged
- Silk.NET's `WindowHintBool` ends at `SrgbCapable`/`DoubleBuffer` — no float pixel type or HDR color space

### Vulkan as Alternative

Vulkan supports HDR output via `VK_EXT_swapchain_colorspace` + HDR10 surface formats.
Silk.NET provides Vulkan bindings (`Silk.NET.Vulkan`).

#### Platform Support Comparison

| Platform | OpenGL | Vulkan | HDR possible? |
|----------|--------|--------|---------------|
| Windows  | Native | Native | Yes (Vulkan HDR swapchain) |
| Linux    | Native | Native | Yes (if compositor supports) |
| macOS    | Deprecated (frozen at 4.1) | MoltenVK | No (Metal HDR needs separate path) |
| Android  | OpenGL ES | Native | Yes (Android 10+) |
| iOS      | OpenGL ES (deprecated) | MoltenVK | No (same as macOS) |
| Web/WASM | WebGL  | **No**  | No |

#### Shader Migration Effort: Low

GLSL shaders compile to SPIR-V with minimal mechanical changes:
- `#version 330 core` → `#version 450`
- `uniform float uFoo;` → `layout(binding=0) uniform UBO { float uFoo; };` (pack into UBOs)
- `uniform sampler2D uTex;` → `layout(binding=1) uniform sampler2D uTex;` (explicit binding)
- Compile to SPIR-V at build time via `glslc`/`glslangValidator` or `Silk.NET.Shaderc` at runtime
- All shader math (MTF stretch, Hermite soft-knee, WCS deprojection, histogram) stays identical

#### API Migration Effort: High

The real work is replacing OpenGL API calls in `GlImageRenderer.cs` (~2000 lines):
swapchain setup, descriptor sets, pipeline objects, command buffers, synchronization.

#### Known Issues

- **macOS regression**: Silk.NET 2.21+ cannot create GLFW Vulkan windows on macOS
  ([#2440](https://github.com/dotnet/Silk.NET/issues/2440)); 2.20 worked
- **MoltenVK not fully conformant**: translates Vulkan to Metal, supports Vulkan 1.4 but
  some features missing; HDR swapchain extensions may not be implemented
- **Web target lost**: Vulkan has no browser support (WebGPU would be the path forward)

### Silk.NET Status (Incumbent)

- **v2.23.0** (Jan 2026) — stable, quarterly maintenance releases
- **3.0**: `develop/3.0` branch exists, tracking issue [#209](https://github.com/dotnet/Silk.NET/issues/209)
  open since June 2020 (5.5+ years). Complete rewrite of bindings generation. No release date.
  Lead developer (Perksey) less active. WebGPU bindings planned for 3.0.
- Current Silk.NET surface in TianWen is well-contained: 4 source files (`GlImageRenderer.cs`,
  `GlShaderProgram.cs`, `GlFontAtlas.cs`, `Program.cs`), 3 NuGet packages
- AOT works with trimmer warning suppressions already in place
- **Verdict**: Not dead, but 3.0 has been in development for years. "Stale" criticism has merit
  for anyone waiting on Vulkan/WebGPU improvements. 2.x works fine for current OpenGL usage.

### Alternatives Evaluated (March 2026)

#### Veldrid — Avoid (Dead Project)

- Last commit: March 2024 (2 years ago). Latest NuGet: v4.9.0 (Feb 2023). 159 open issues.
- Clean abstraction (Vulkan, D3D11, Metal, OpenGL) but author (mellinoe) has moved on
- Targets .NET 6 / netstandard2.0, not .NET 10. No AOT testing. No HDR.

#### Avalonia + GPU Interop — Consider If Full UI Rewrite Desired

- 30K+ stars, extremely active (committed yesterday). .NET 10 supported.
- Has `GpuInterop` sample with Vulkan demo via `CompositionDrawingSurface`
- Gives proper UI framework (menus, panels, dialogs) — could replace hand-built text/panel rendering
- **But**: GPU interop is low-level — you manage your own Vulkan context inside a compositor callback.
  HDR depends on SkiaSharp compositor pipeline (no HDR). AOT improving but Avalonia is large.
- Migration effort: Very high. Only worth it if also replacing the hand-built UI.

#### SDL3 (.NET bindings) — Best Near-Term Migration Path

- SDL3 itself: 15K stars, committed yesterday, extremely battle-tested
- Three competing .NET bindings: **ppy/SDL3-CS** (osu! team, most production-tested),
  edwardgushchin/SDL3-CS, flibitijibibo/SDL3-CS
- SDL3 has native Vulkan surface creation + new **SDL_GPU** abstraction (Vulkan/D3D12/Metal
  with automatic shader cross-compilation)
- **SDL3 + keep OpenGL**: replaces only GLFW windowing/input, preserves all GLSL shaders and
  GL rendering. Migration effort: **medium** (SDL3 windowing maps closely to GLFW concepts)
- **SDL3 + SDL_GPU**: higher-level Vulkan-like API, handles shader translation. Medium-high effort.
- SDL3 has HDR output support at the windowing level
- AOT: P/Invoke should work, untested with .NET 10 AOT specifically

#### Evergine Vulkan.NET — Best Raw Vulkan Bindings

- 284 stars, committed yesterday. Source-generated from Vulkan headers (always up-to-date, v1.4.341).
- Targets .NET 8+. NuGet: `Evergine.Bindings.Vulkan`
- Full HDR access via raw Vulkan swapchain formats
- **But**: raw bindings only — all Vulkan boilerplate is your problem. No windowing (pair with SDL3).
- Migration effort: Very high. 5-10x more code than OpenGL for the same result.

#### Vortice.Vulkan — Best Raw Vulkan Ecosystem

- 371 stars (Vulkan), 1.1K stars (Windows/D3D). Last commit: Feb 2026. Only 2 open issues.
- Explicitly targets net9.0 + net10.0. By amerkoleci (also builds Alimer engine).
- Bundles VMA (Vulkan Memory Allocator), SPIRV-Cross, and shaderc in one package
- Same migration effort as Evergine but better ecosystem (VMA + shaderc bundled)
- Single maintainer (bus factor of 1)

#### WebGPU via wgpu-native — Future Option (Not Ready)

- wgpu-native: 1.2K stars, committed yesterday. Translates to Vulkan/D3D12/Metal.
- .NET bindings immature: Evergine WebGPU.NET (Nov 2025), WebGPUSharp (14 stars)
- Shader language is WGSL (GLSL would need porting). HDR not yet in WebGPU spec.
- Revisit in 1-2 years when .NET bindings mature.

### Vortice.Vulkan + edwardgushchin/SDL3-CS — Platform Matrix (Recommended Combo)

Vortice.Vulkan is pure managed C# bindings (`delegate* unmanaged` function pointers, no P/Invoke).
Uses system Vulkan loader. Explicitly `IsAotCompatible = true`. Targets net9.0 + net10.0.
Companion packages (VMA, SPIRV-Cross, shaderc) ship natives for all platforms including Android.

edwardgushchin/SDL3-CS uses `LibraryImport` (source-generated, AOT-safe).
`SDL3-CS.Native` NuGet ships desktop natives. Android works but needs manual lib bundling.

| Platform | Vulkan | SDL3 native | AOT | HDR |
|----------|--------|-------------|-----|-----|
| Windows x64 | Native | NuGet | Yes | Yes (Vulkan HDR swapchain) |
| Windows ARM64 | Native | NuGet | Yes | Yes |
| Linux x64 | Native (Mesa/NVIDIA) | NuGet | Yes | Possible (Wayland + Vulkan) |
| Linux ARM64 | Native (Mesa) | NuGet | Yes | Limited |
| macOS x64 | MoltenVK | NuGet | Yes | MoltenVK limitations |
| macOS ARM64 | MoltenVK | NuGet | Yes | MoltenVK limitations |
| Android | Native | Manual bundling | Partial | Yes |
| iOS | MoltenVK (must bundle) | Not shipped | Yes | Limited |

SDL3 HDR support: `SDL.window.HDR_enabled`, `SDL.window.SDR_white_level`, `SDL.window.HDR_headroom`
display properties, plus PQ (ST 2084) and HLG transfer characteristics. Combined with Vulkan
`VK_COLOR_SPACE_HDR10_ST2084_EXT` swapchain, full HDR output is achievable.

SDL3 Vulkan surface creation: `SDL.VulkanLoadLibrary()` (auto-finds MoltenVK on macOS),
`SDL.VulkanCreateSurface()` → `VkSurfaceKHR`, pairs directly with Vortice.Vulkan rendering.

### Option: Contributing Upstream Fixes to Silk.NET

#### macOS Vulkan Regression (#2440) — Small Fix, Uncertain Merge Timeline

Root cause: GLFW 3.4 changed Vulkan detection on macOS. `glfwVulkanSupported()` can't find
the Vulkan loader even though Silk.NET ships it (`Silk.NET.Vulkan.Loader.Native`).
GLFW 3.4 added `glfwInitVulkanLoader()` which could solve this.

Possible fixes:
1. Call `glfwInitVulkanLoader()` with a custom `vkGetInstanceProcAddr` before `glfwInit()`
2. Set `VK_ICD_FILENAMES` environment variable to point at bundled MoltenVK ICD
3. Ensure Vulkan loader is on `DYLD_LIBRARY_PATH`

**Status**: No PRs submitted, zero maintainer engagement on the issue. Worth contributing
but may sit unmerged — 2.x is in maintenance mode (14-month gap between 2.22 and 2.23),
team is focused on 3.0. Trivial PRs merge in 0-11 days; no evidence of substantive
external feature PRs merging recently.

#### HDR Support — Blocked by GLFW Architecture

HDR is **not feasible** within Silk.NET's current GLFW-based windowing:
- GLFW has no API for HDR pixel formats, transfer functions, or color spaces
- GLFW's own HDR issue ([#890](https://github.com/glfw/glfw/issues/890)) open since 2016, never implemented
- Silk.NET's Vulkan bindings already cover all HDR swapchain extensions — the blocker is purely windowing
- Would require replacing GLFW with SDL3 as windowing backend (huge change) or platform-specific code

| Path | macOS fix | HDR | Effort | Risk |
|------|-----------|-----|--------|------|
| Fix Silk.NET upstream | Small PR, may wait months | **Blocked by GLFW** | Low for macOS, impossible for HDR | PR rot |
| Vortice.Vulkan + SDL3-CS | SDL3 auto-detects MoltenVK | Full HDR built into SDL3 | High (rewrite renderer) | Two active projects |

**Verdict**: Contributing the macOS fix is worth doing regardless (small PR, helps community).
But it doesn't solve HDR — migration is the only path for that.

### Comparison Matrix

| Option | Maintenance | Vulkan | HDR | AOT | Migration | Shaders kept? |
|--------|------------|--------|-----|-----|-----------|---------------|
| Silk.NET 2.x (stay) | Moderate | Via 3.0 someday | No | Yes | None | Yes |
| Silk.NET 2.x + macOS PR | Moderate | Yes (with fix) | No | Yes | None | Yes |
| SDL3 + OpenGL | Excellent | Surface only | **No** | Yes | Medium | **Yes** |
| SDL3 + SDL_GPU | Excellent | Under the hood | Possible | Yes | Medium-high | Rewrite to SDL_GPU |
| Vortice.Vulkan + SDL3 | Good | Full | **Yes** | **Yes** | Very high | GLSL→SPIR-V |
| Evergine Vulkan.NET + SDL3 | Excellent | Full | **Yes** | **Yes** | Very high | GLSL→SPIR-V |
| Avalonia + Vulkan interop | Excellent | Yes (interop) | No | Improving | Very high | Rewrite |
| WebGPU/wgpu | Weak (.NET) | Under the hood | Not yet | Possible | High | GLSL→WGSL |

Note: SDL3 + OpenGL HDR corrected to **No** — SDL3's OpenGL renderer hardcodes `SDL_COLORSPACE_SRGB`
as the only accepted output. No float pixel formats, no scRGB, no HDR10 via OpenGL on any platform.

### Recommended Strategy

1. **Short term**: Stay on Silk.NET 2.x. It works, it's AOT-compatible, and usage is well-contained.
   Consider submitting a PR for the macOS Vulkan regression (#2440).

2. **If Silk.NET becomes untenable**: Migrate windowing to **SDL3 (edwardgushchin/SDL3-CS) + keep OpenGL**.
   This replaces only GLFW (windowing/input) while preserving all GLSL shaders and GL code.
   Modest effort since SDL3 windowing concepts map closely to GLFW. Prefer edwardgushchin/SDL3-CS
   over ppy/SDL3-CS for AOT compatibility (`LibraryImport` vs old `DllImport`).

3. **For Vulkan/HDR**: Use **SDL3 for windowing** + **Vortice.Vulkan** for rendering
   (includes VMA + shaderc). Compile GLSL to SPIR-V at build time. Major rewrite of
   `GlImageRenderer.cs` but shader math stays identical. This is the only path to HDR output.

4. **For full UI overhaul**: Consider **Avalonia** if the hand-built text/panel rendering becomes
   a maintenance burden. Biggest investment but gives a real UI framework.

5. **Watch**: SDL3_GPU maturity (could simplify Vulkan), WebGPU .NET bindings, Silk.NET 3.0.
