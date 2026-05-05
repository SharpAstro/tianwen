# TODOs

## High Priority

- [x] MiniViewer: optional lightweight mode that skips storing UnstretchedImage — for live preview where we never re-stretch, just keep stats + GPU texture. Saves ~140MB per displayed frame
- [x] Cache altitude chart as texture — only re-render the mouse follower overlay on hover, not the entire chart. Currently 20% GPU on mouse hover due to full chart redraw per frame
- [x] TianWen.Hosting remote API — ASP.NET Core Minimal API + WebSocket for headless Raspi operation. Multi-OTA native routes (`/api/v1/ota/{index}/camera/info`) with ninaAPI v2 compatibility shim (`/v2/api/*` → OTA[0]) so Touch N Stars works for single-scope setups. All 4 phases complete: read-only state, control, ninaAPI shim (equipment info/control, sequence, images, WebSocket, device lifecycle, guider graph, move-axis), profile CRUD + pending target queue. `tianwen-server` headless executable published as AOT binary for all platforms
- [ ] PlayerOne Astronomy / ToupTek / SVBony native drivers — these vendors use ZWO-compatible SDKs with different library prefixes (PlayerOne: `PlayerOneCamera`, ToupTek: `toupcam`/`starshootg`, SVBony: `SVBCameraSDK`). Investigate sharing `ZWODeviceSource`/`ZWOCameraDriver` infrastructure with a pluggable SDK shim rather than duplicating per vendor. NINA uses a `ToupTekAlike` pattern for this family. Cameras, filter wheels, and focusers where applicable
- [ ] Catalog cold-start Phase 2 (pre-bake init state) — see `PLAN-catalog-binary-format.md` § Phase 2. **2A SHIPPED 2026-05-05:** `tools/precompute-hd-hip-cross/` bakes the post-`BuildHdHipCrossIndicesViaTyc` state into `hd_hip_cross.bin.gz`; runtime apply takes ~110 ms vs ~460 ms live (~350 ms saved). Two CI guards in `HdHipCrossSnapshotTests` (freshness + apply-vs-live state). Run `tools/precompute-hd-hip-cross.ps1` to re-bake when catalog inputs change. **2B remaining:** same pattern for SIMBAD merge state (~150 ms target).

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
- [x] DIR.Lib: add `FillEllipse`/`FillCircle`/`DrawEllipse`/`DrawCircle`/`DrawLine` primitives to `PixelWidgetBase` — `DrawLine` and `DrawEllipse` on abstract `Renderer` with CPU-optimized overrides on `RgbaImageRenderer` (midpoint ellipse, scanline quad, Span.Fill); GPU-optimized overrides on `VkRenderer` (rotated quad via FlatPipeline, ring shader via EllipsePipeline). Benchmarks in `DIR.Lib.Benchmarks`
- [x] Guider graph: show applied correction pulses (RA/Dec duration bars) alongside error — log-scaled bars (blue RA / orange Dec) extending up/down from zero line
- [ ] SyntheticStarFieldRenderer: refactor 20-parameter methods into records/structs
- [ ] Sky map: GPU text labels — move constellation names, planet labels, and overlay labels into the GPU sky-map pipeline (glyph atlas + instanced quads, like Stellarium). Currently all text is CPU-drawn via `PixelWidgetBase.DrawText`. The 1-frame desync during fast pans was fixed by per-swapchain-image UBO (commit ee38783), but full GPU text would eliminate the CPU/GPU render-pass split entirely and enable projected text that follows the stereographic distortion.
- [ ] Sky map: `[R]`efraction grid — toggle a second coordinate grid drawn in JNow + refraction-corrected (apparent) coordinates on top of the existing J2000 grid. Shows where objects actually appear from the observer's current site right now vs. the catalog J2000 positions. Full `Transform.SetJ2000 → RAApparent/DECApparent` (refraction on, site pressure/temperature from profile) for each grid line, tessellated like `BuildGridBuffers`. Near-zenith shift is ~0.35° precession alone; near the horizon the refraction bend stacks on top, reaching ~0.6° at 0° altitude. Makes the mount reticle's J2000 offset intuitive — the JNow grid passes through the reticle by construction for a topocentric-reporting mount.
- [ ] Sky map: Stellarium-style time adjuster — step the observation instant relative to now (e.g. press `+1h` / `+1d` and it becomes Thursday 23:04 etc.), not a pick-a-date. Stores an offset from wall clock (minutes, hours, days, weeks) so the user can scrub forward and back. Drives:
    - sky color (feeds `SkyMapState.GetSunAltitudeDegCached` with the adjusted instant)
    - LST so stars / crosshair / horizon rotate correctly
    - planet positions via `VSOP87a.Reduce`
    - horizon fill and below-horizon label dimming
  Must replace / extend the current "isPlanningTonight" bool path so the sky stays correctly coloured when a time offset is applied. HUD should show the scrubbed time prominently vs. wall clock so the user never confuses the two. A "reset to now" shortcut (e.g. `0`) returns offset to zero.
- [x] Guider graph: show dither events (markers/shading) — yellow dashed vertical lines at dither events, dim yellow settling shading
- [x] Guider tab: keep looping guide camera frames during centering/slewing — call `LoopAsync` when not guiding so the guide camera feed stays live. Currently the guide loop stops during centering and the tab shows "Waiting for guider"
- [x] Guider tab: show calibration frames — render guide camera during calibration phase with star position and profile. Remaining: star movement vectors, step count, and calibration progress overlay
- [ ] Guider: adaptive image-ready polling — sleep until near the expected end of exposure (N − `ImageReadyPollInterval`), then poll every 10ms, and in the final ~10ms poll every 1ms. Avoids wasting CPU on long sleeps while minimising latency at exposure end. Applies to `BuiltInGuiderDriver.CaptureGuideFrameAsync` and any other image-ready poll loop
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

## Observation Scheduler

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

## Session Test Plan Progress

- [x] **Phase 2**: FakeCamera cooling simulation (commit 9ae4490)
- [x] **Phase 3**: FakeFocuser temperature + focus model (commit 25ce32d)
- [x] **Phase 4**: Synthetic star field renderer (commit 6fee8fb)
- [x] **Phase 5 partial**: Backlash property on IFocuserDriver, FocusDirection 2x2 matrix (commit 25ce32d)
- [x] **Phase 6 partial**: AutoFocusAsync with V-curve + hyperbola fitting, per-target baseline HFD (commits 25ce32d, 68d061c)
- [x] **Phase 1**: FakeGuider state machine — full state machine (Idle, Looping, Calibrating, Guiding, Settling) with atomic transitions
- [x] **Phase 5 remaining**: ~~`BacklashMeasurement.MeasureAsync` standalone 3-scan routine~~ — superseded by opportunistic per-AutoFocus inference (cloudynights "no need to measure" approach). `BacklashEstimator.InferFromVerification` (`TianWen.Lib/Astrometry/Focus/BacklashEstimator.cs`) inverts the hyperbola fit against the verification HFD that AutoFocus already takes; mechanical lag = `H⁻¹(verifyHfd) − bestPos`, B = currentOvershoot + lag. Per-focuser EWMA (α=0.3) updated each AutoFocus, sized into next-run overshoot via `BACKLASH_OVERSHOOT_SAFETY = 1.5`. EWMA + sample count + timestamp persisted to `Profiles/BacklashHistory/<focuserDeviceId>.json` via `BacklashHistoryPersistence`; rounded values mirrored back to focuser URI on session-end via `EquipmentActions.SaveBacklashEstimatesIfChangedAsync`. `MeasureBacklashIfUnknown` config flag dropped (no separate routine to gate). `MoveWithCompensationAsync` extension shipped earlier as `BacklashCompensation.MoveWithCompensationAsync`.
- [x] **Phase 6 remaining**: Focus drift detection in ImagingLoopAsync (HFD threshold check + auto-refocus trigger)
- [x] **Phase 7a**: Observation duration enforcement in imaging loop — when `observation.Duration != TimeSpan.MaxValue` and elapsed time exceeds Duration, break. `TimeSpan.MaxValue` means "as long as possible" (bounded by session end time). — done at `Session.Imaging.cs:547` (`tickCount >= maxTicks` check, `maxTicks` derived from `observation.Duration / tickSec` at line 300).
- [x] **Phase 7b**: PeriodicTimer replacing hand-rolled sleep/overslept timing
- [ ] **Phase 7c**: Full Session integration tests in new `SessionTests.cs`, constructing `Session` directly with fake devices:
    1. Single observation lifecycle (phases 1, 2)
    2. Multiple observation sequencing (1, 2)
    3. Cancellation with finalize (1, 2)
    4. Twilight boundary stop (1, 2)
    5. Camera cooling ramp (2)
    6. Observation duration limit (7a)
    7. Star field contains detectable stars (3, 4)
    8. Star field is plate-solvable (3, 4)
    9. Focus drift triggers auto-refocus — set `FakeFocuserDriver._tempDriftRate` high, advance `FakeTimeProvider` during imaging loop, assert 30% HFD increase detected and focuser moved to new best position (3, 4, 6)
    10. ~~Backlash measurement converges — assert `BacklashMeasurement.MeasureAsync` returns value close to `_trueBacklash` (3, 4, 5)~~ — superseded; covered by `BacklashEstimatorTests` (10 tests) + `BacklashHistoryPersistenceTests` (4 tests) + functional AutoFocus suite using `FakeFocuserDriver._trueBacklashIn/Out` (3, 4, 5)
    11. Auto-focus finds correct position — start focuser at 800, true best focus at 1000; assert `FocusSolution.BestFocus` is within ±5 steps of 1000 (3, 4, 5, 6)
    12. Temperature drift causes focus shift — start at best focus, advance time so temperature drops 2°C (shifts true best focus by `2 * tempCoefficient = 10` steps), assert HFD grows, refocus recovers (3, 4, 6)

## Sequencing / Session

- [ ] Polar alignment + FakeCameraDriver near SCP / NCP: ASTAP needs 44+ stars in its search window and a 1.7° FOV at Dec=±90° renders only ~17 stars under the per-exposure magnitude cutoff (`magCutoff = min(12, 7 + 2.5*log10(exposureSec))`). With the search-origin fix shipped in the polar align routine, the built-in `CatalogPlateSolver` now gets a chance first (it only needs 6 matched stars), so ASTAP failure is no longer fatal. If `CatalogPlateSolver` also can't find 6 matches near the pole, bump the fake-cam mag cutoff for polar-align mode (force ~mag 12 always) or document that demos require pointing off-pole.
- [ ] Polar alignment: refraction-corrected pole position in `PolarOverlay` — `PolarAlignmentSession.RefineAsync` currently sets `RefractedPoleRaHours/DecDeg` to the J2000 true pole (true == refracted). The arcmin gauges already use the refraction-aware `DecomposeAxisError`, so the numbers are correct, but the overlay rings draw on the true pole rather than the apparent pole. Implement inverse SOFA topo→J2000 (push the true pole through `J2000ToTopo` to get apparent (Az, Alt), then back through topo→J2000 with refraction off) so the rings centre on the refracted pole and the user sees two distinct crosses. Matters most at low latitudes (lat=15° gives ~3.4' separation at horizon-pole; lat=35° still ~1.4').
- [ ] Baseline HFD per-target: key by observation index (not telescope index), smart refocus-on-target-change — skip refocus when recent focus is good, establish new baseline from median of first N frames; `AlwaysRefocusOnNewTarget` config option
- [x] FOV obstruction detection: compare first-frame metrics against previous target's baseline; if anomalous, nudge mount up by one frame radius — if metrics recover, exit imaging loop for this target (tree/building in FOV) — `Session.Imaging.Obstruction.cs` `ScoutAndProbeAsync` + `NudgeTestAsync` + `EstimateObstructionClearTimeAsync`; trajectory-aware wait if obstruction will clear in `<= ObstructionClearFractionOfRemaining`. See `PLAN-fov-obstruction-detection.md` + `ARCH-fov-obstruction.md`.
- [ ] FOV obstruction scout: absolute expected-star-count oracle for the **first observation of the night** — today `ScoutAndProbeAsync` returns `Healthy` unconditionally for the first target because there's no prior-observation baseline to compare against. A target behind a tree at the very start of the night is missed until the in-flight condition-deterioration check trips, by which time we've burned guider-start + several full-length exposures. Fix needs an absolute oracle that doesn't rely on a prior baseline — either a per-target catalog-derived expectation (use stars in field from the catalog, weight by limiting magnitude for the OTA × exposure × filter) or a cross-session per-(galactic-latitude × peak-magnitude) cache populated from successful baselines and keyed on FOV center. See `PLAN-fov-obstruction-detection.md` "Known limitations" #1.
- [ ] Live site pressure/temperature for refraction (`IMountDriver.cs:395-396`) — replace hardcoded `SitePressure = 1010` / `SiteTemperature = 10` with: (1) live `IWeatherDriver.Pressure` / `Temperature` if a weather device is connected (works for `OpenMeteoDriver` + any ASCOM ObservingConditions device), (2) new `ProfileData.SitePressureHPa` / `SiteTemperatureCelsius` user-configurable fallback, (3) standard atmosphere as last resort. Matters for refraction at low altitudes (lat=15° pole sees ~3.4' refraction lift; lat=35° still ~1.4'). Same fix unblocks polar alignment refraction-aware decomposition — see `PLAN-polar-alignment.md` "Refraction at low pole altitudes".
- [x] OnStep mount driver: extend `MeadeLX200ProtocolMountDriverBase` with `:GX`/`:SX` commands, native pier side, park/unpark, `FakeOnStepSerialDevice` for testing — `OnStepMountDriver<TDevice>` shipped using `:GU#` (bundled status), `:Gm#` (pier), `:hP-:hR-:hQ` (park 0/1), `:Te/:Td` (tracking on/off), `:TK/:TS` (rates). Serial + WiFi (`TcpSerialConnection` + mDNS `_telescope._tcp.local`). Follow-ups tracked at line 237 below.
- [ ] Faster imaging loop tick: reduce to `GCD/6` clamped `[1s, 5s]` — fix `FakeMeadeLX200SerialDevice` slew timer interleaving (immediate axis positioning instead of 100ms step timer)
- [x] `SessionFactory.Create(proposals)` hardcodes `defaultObservationTime = 30min` — should use planner's computed windows (handoff slider positions) or at least divide the dark window evenly among targets — `PlannerActions.ApplyHandoffWindows` (and `ComputeVisibleTimeInWindow` helper) now projects each pinned target's handoff slider window into `ProposedObservation.ObservationTime`, clipped to when the target is at or above `MinHeightAboveHorizon`. Wired into `BuildSchedule`; `defaultObservationTime` becomes a true fallback for proposals without a slider/profile.
- [x] Gracefully stop a session (`HostedSession.cs:39`) — `Session.RunAsync`'s `try/finally` (`Session.cs:288-300`) always runs `Finalise(CancellationToken.None)` (park mount, warm cameras, close covers) when the session token is cancelled. `HostedSession.StopAsync` cancels the token then disposes — gracefulness happens via the cancellation finally path. (The inline TODO comment at `HostedSession.cs:112` is now stale — should be removed.)
- [ ] Wait until 5 min to astro dark, and/or implement `IExternal.IsPolarAligned` (`Session.cs:61`) — first half done: `WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync` runs at `Session.cs:260` before cooling/focus/calibration. `IsPolarAligned` half still open.
- [ ] Maybe slew slightly above/below 0 declination to avoid trees, etc. (`Session.Lifecycle.cs:19` — guider calibration slew). NOT covered by `ScoutAndProbeAsync` because the scout is OTA-imaging-only, runs after centering, and requires a prior-observation baseline. See `PLAN-fov-obstruction-detection.md` "Known limitations" section for what would need to change to unify these.
- [x] Plate solve, sync, and re-slew after initial slew — `PlateSolveAndSyncAsync` called after slew in `ObservationLoopAsync` and `InitialRoughFocusAsync`
- [x] ~~Wait until target rises again instead of skipping~~ — replaced by spare target fallback in observation loop, todo
      Maybe we should estimate how long it will take for the target to appear, i.e. by slewing where it _will_ be in lets say half an hour and see if we can get more stars
      etc there
- [ ] Plate solve and re-slew during observation (`Session.cs:467`)
- [ ] Per-camera exposure calculation, e.g. via f/ratio (`Session.cs:540`)
- [x] Stop exposures before meridian flip (if we can, and if there are any) — `PerformMeridianFlipAsync` stops guider, waits for slew completion, smart exposure handling (<30s wait / >30s abort)
- [x] Stop guiding, flip, resync, verify, and restart guiding — `PerformMeridianFlipAsync` stops capture, re-slews with RA offset, verifies HA flipped positive, restarts guiding loop
- [ ] Make FITS output path template configurable (`Session.IO.cs:16`) — frame type already in path as `{target}/{date}/{filter}/{frameType}/`
- [x] FOV obstruction detection: if first frames on a new target show HFD way higher or star count way lower than previous target's baseline, nudge mount up in altitude by one frame radius and re-check — if metrics recover, something is blocking the FOV (tree, building); make this a new imaging loop exit condition — duplicate of above, both shipped together
- [x] Switch `ImagingLoopAsync` to `PeriodicTimer` instead of hand-rolled sleep/overslept timing
- [x] Device disconnect resilience in imaging loop — when mount/camera/guider disconnects, attempt reconnect with backoff instead of immediately advancing to next observation; only bail after N retries or timeout — `ResilientCall` wrapper + `Session.ErrorHandling.ResilientInvokeAsync` + per-driver fault counter + `DeviceUnrecoverable` escalation. See `PLAN-driver-resilience.md` + `ARCH-driver-resilience.md`.
- [x] Altitude check distinguishes rising vs setting targets — `EstimateTimeUntilTargetRisesAsync` samples altitude at 5-min intervals; if rising and within `MaxWaitForRisingTarget` (default 15 min), waits then retries slew; otherwise tries spare targets then advances
- [x] Write `FOCALLEN` and `FOCUSPOS` to FITS output headers (currently read on load but never written)
- [x] Write `DATAMIN` to FITS output headers (only `DATAMAX` was written)
- [x] `FocusDriftThreshold` default changed from 1.3 (30%) to 1.07 (7%); already a `SessionConfiguration` setting

## Live Session Tab (Phase 2 — Polish)

- [x] Guide star profile bitmap from guider (rendered in GuiderTab star profile panel)
- [ ] Extract `GuiderContent` shared helpers (TianWen.UI.Abstractions) — `TuiGuiderTab` and the GPU `GuiderTab<TSurface>` currently inline their formatting / sparkline logic. Mirror the `LiveSessionActions` pattern: `FormatGuidePhase(phase)`, `FormatStarInfo(metrics)`, `FormatSettleProgress(current, target)`, `BuildErrorSparkline(samples, axis, width)` -> Unicode string, `GetErrorGraphPoints(samples, axis, timeWindow)` -> points for the GPU line graph, `GetBullseyePoints(samples, count)` -> (ra, dec) scatter. Lets both the TUI and GPU tabs share the same phase strings and error-graph data derivation instead of duplicating.
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
- [ ] LX800 fixed GW response not being terminated issue, account for that (`MeadeLX200ProtocolMountDriverBase.cs:143`)
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
- [x] **Migrate remaining `appState.StatusMessage = …` sites to `appState.AppendNotification(when, sev, msg)`.** Swept `AppSignalHandler.cs` (site-recompute, Goto validation, discovery results, assign/connect/disconnect/force-disconnect result+failure, cooler setpoint, warm-and-disconnect, warm-and-cooler-off, cooler off, session start validation + finalizer phase + cancel/fail, preview/snapshot/plate-solve/jog result+failure) and `Program.cs` (site warning, warming-cameras prompt, shutdown initial-state). Kept pure transient progress hints as plain assignments: `Recomputing…`, `Discovering devices…`, `Building schedule…`, `Initialising session…`, `Plate solving…`, Sun-slew confirmation prompt, shutdown pending-count ticker, ESC-to-quit prompt.

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
- [x] Replace `vkDeviceWaitIdle` in font atlas `Flush` with per-frame upload buffers (like `_vertexBuffers`) to avoid GPU stall on every glyph upload — `VkFontAtlas` + `VkSdfFontAtlas` now keep an N-slot ring indexed by `ctx.CurrentFrame`; `MaxFramesInFlight` exposed as `public const` on `VulkanContext` (commit `3ccd6a2`).
- [x] SDF font atlas: `Grow()` / `CreateImage` used to transition the fresh `VkImage` via `ctx.ExecuteOneShot`, which submits a side cmd buffer to the graphics queue while the frame's cmd buffer is recording — some drivers reject this with `VK_ERROR_INITIALIZATION_FAILED` from the next `vkQueueSubmit`. Fixed: deferred initial transition to the next `Flush` via `_needsInitialTransition` flag; initial atlas dim now scales with `SdfRasterSize` (`2048²` at 128px raster) so `Grow()` rarely fires during typical startup UI anyway (commit `30fcdf7`).
- [x] `VkTexture.CreateDeferred`: pixel-format parameter — was hard-coded to `B8G8R8A8Unorm`, which forced RGBA-producing CPU renderers (altitude chart via `RgbaImageRenderer`) to run a per-pixel swizzle loop before upload. Now takes `VkFormat format = B8G8R8A8Unorm` so callers can pass `R8G8B8A8Unorm` with RGBA bytes directly (commit `90f877a`); `VkPlannerTab` dropped its CPU swizzle loop.
- [ ] `VkSdfFontAtlas.Grow()` mid-frame hazard — destroys the old `VkImage` and calls `vkUpdateDescriptorSets` while the frame's cmd buffer is still recording. Works on current drivers but is spec-grey (`VUID-vkUpdateDescriptorSets-pDescriptorWrites-06993` forbids updating a descriptor set that is in use by a pending submission). If we ever see corruption or validation noise tied to `Grow()`, defer the destroy + descriptor update to the next `OnPreRenderPass` (same pattern as `VkPlannerTab`'s deferred texture swap). Not pre-emptively worth fixing — the initial-atlas bump in `30fcdf7` makes `Grow()` rare, and there is no known observed corruption.
- [ ] `SdlVulkanWindow.Create` should take the SDL `WindowFlags` as a parameter instead of hardcoding `WindowFlags.Vulkan | WindowFlags.Resizable | WindowFlags.Maximized`. Default keeps `Maximized` (matches today's behaviour) but callers can opt out — e.g. to launch at the supplied `1280×900` non-maximized, or to force fullscreen at startup. Both `TianWen.UI.Gui/Program.cs:74` and `TianWen.UI.FitsViewer/Program.cs` (same `Create` call) pick up the change for free. Consider exposing as an overload `Create(title, width, height, WindowFlags extraFlags)` with `Vulkan | Resizable` always on, `Maximized` added by default but overridable.

### SdlEventLoop (DONE — all consumers now use the shared loop)
- [x] Add `DropFile` event support (`EventType.DropFile`) — `Action<string>? OnDropFile`
- [x] Multi-button mouse: `OnMouseDown` passes button ID + click count (`Func<byte, float, float, byte, bool>?`)
- [x] `OnMouseUp` passes button ID (`Action<byte>?`)
- [x] `OnMouseWheel` passes tracked mouse position (no more hardcoded 0, 0)
- [x] F11 fullscreen removed from loop — each consumer handles it in `OnKeyDown`
- [x] Migrated `TianWen.UI.FitsViewer/Program.cs` to use `SdlEventLoop`
- [x] Touch input: pinch-to-zoom via `SDL_EVENT_FINGER_*` events — two-finger tracking + scale computation in `SdlEventLoop` (`OnPinch`/`OnPinchEnd`), consumed by `SkyMapTab` via `InputEvent.Pinch`/`PinchEnd` (2f0b484)

Vulkan/SDL migration rationale moved to `../SdlVulkan.Renderer/README.md` ("Rationale: Why SDL3 + Vortice.Vulkan" section).
