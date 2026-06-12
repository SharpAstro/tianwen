# TODOs

## High Priority

- [x] MiniViewer: optional lightweight mode that skips storing UnstretchedImage — for live preview where we never re-stretch, just keep stats + GPU texture. Saves ~140MB per displayed frame
- [x] Cache altitude chart as texture — only re-render the mouse follower overlay on hover, not the entire chart. Currently 20% GPU on mouse hover due to full chart redraw per frame
- [x] TianWen.Hosting remote API — ASP.NET Core Minimal API + WebSocket for headless Raspi operation. Multi-OTA native routes (`/api/v1/ota/{index}/camera/info`) with ninaAPI v2 compatibility shim (`/v2/api/*` → OTA[0]) so Touch N Stars works for single-scope setups. All 4 phases complete: read-only state, control, ninaAPI shim (equipment info/control, sequence, images, WebSocket, device lifecycle, guider graph, move-axis), profile CRUD + pending target queue. `tianwen-server` headless executable published as AOT binary for all platforms
- [ ] PlayerOne Astronomy / ToupTek / SVBony native drivers — these vendors use ZWO-compatible SDKs with different library prefixes (PlayerOne: `PlayerOneCamera`, ToupTek: `toupcam`/`starshootg`, SVBony: `SVBCameraSDK`). Investigate sharing `ZWODeviceSource`/`ZWOCameraDriver` infrastructure with a pluggable SDK shim rather than duplicating per vendor. NINA uses a `ToupTekAlike` pattern for this family. Cameras, filter wheels, and focusers where applicable
- [ ] Catalog cold-start Phase 2 (pre-bake init state) — see `PLAN-catalog-binary-format.md` § Phase 2. **2A SHIPPED 2026-05-05:** `hd_hip_cross.bin.gz` snapshot (~350 ms saved). **2B SHIPPED 2026-05-05:** `simbad_merge.bin.gz` snapshot (~180 ms saved). **2C deferred:** Tycho-2 bulk load; BFS pooling not started.

## Flaky CI Tests

- [x] `SessionImagingTests.GivenHighAltitudeTarget...HighUtilization` — fixed: cooperative time pump (`ExternalTimePump + Advance`)
- [x] `SessionImagingTests.GivenDitherEveryNth...DitheringTriggered` — fixed: same root cause (SleepAsync pump race)
- [x] `SessionImagingTests.GivenFocusDrift...AutoRefocusTriggered` — fixed: same root cause
- [x] `SessionPhaseTests.AbortDuringCooling_StopsRampAndWarmsBack` — fixed: removed wall-clock CancellationTokenSource timeouts

## Next Up

- [x] QHYCCD device support — native camera, filter wheel (camera-cable + standalone serial QHYCFW3), and QFOC focuser (Standard + High Precision) drivers. JSON-over-serial protocol for QFOC with typed records and AOT-safe `QfocJsonContext`. Three-phase discovery in `QHYDeviceSource`: cameras → serial probe → camera-cable CFW check
- [x] Weather overlay in planner — hourly forecast from Open-Meteo (free, no API key) with layered color emoji (rain/snow/thunder/fog/cloud/sun/moon), file-cached with 1h TTL + offline fallback. Weather as full device type (IWeatherDriver) with equipment/profile integration
- [x] Planner: show Moon phase + position — altitude curve on the chart with phase emoji (hemisphere-aware). Uses Meeus lunar ephemeris via VSOP87a pipeline
- [x] Moon penalty in target scoring — penalise targets within ~30° of a bright Moon (illumination × proximity factor). Compute angular separation per target in ObservationScheduler.ScoreTarget. **Shipped** (branch `feat/moon-avoidance`): per-bin `MoonGrid` (illumination × quadratic proximity, Moon-below-horizon gate); radius is an optional param (default 30, ON) on Schedule/TonightsBest/ScoreTarget. See `PLAN-moon-avoidance.md`
- [ ] Live viewer: camera switching — allow selecting which OTA's camera to preview in both GUI MiniViewer and TUI Sixel preview (currently always shows first available). PARTIAL (verified 2026-06-02): GUI DONE (`MiniViewerState.SelectedCameraIndex` + `#1`/`#2` toolbar toggles, `LiveSessionTab.cs:373`); TUI Sixel preview still always takes first available (`TuiLiveSessionTab.cs:644`).
- [x] Guider graph: connect dots with lines (Bresenham or anti-aliased) instead of scatter dots — users expect smooth curves like PHD2
- [x] Guider graph: scrolling window (last N samples) with dynamic Y scale and grid lines at integer arcsec
- [x] Guider graph: reuse the existing LiveSessionTab guide graph widget — the guider tab should show a larger version of the same graph, not a separate implementation. Extract shared graph rendering
- [x] DIR.Lib: add `FillEllipse`/`FillCircle`/`DrawEllipse`/`DrawCircle`/`DrawLine` primitives to `PixelWidgetBase` — `DrawLine` and `DrawEllipse` on abstract `Renderer` with CPU-optimized overrides on `RgbaImageRenderer` (midpoint ellipse, scanline quad, Span.Fill); GPU-optimized overrides on `VkRenderer` (rotated quad via FlatPipeline, ring shader via EllipsePipeline). Benchmarks in `DIR.Lib.Benchmarks`
- [x] Guider graph: show applied correction pulses (RA/Dec duration bars) alongside error — log-scaled bars (blue RA / orange Dec) extending up/down from zero line
- [ ] SyntheticStarFieldRenderer: refactor 20-parameter methods into records/structs
- [ ] Sky map: GPU text labels — move constellation names, planet labels, and overlay labels into the GPU sky-map pipeline (glyph atlas + instanced quads, like Stellarium). Currently all text is CPU-drawn via `PixelWidgetBase.DrawText`. The 1-frame desync during fast pans was fixed by per-swapchain-image UBO (commit ee38783), but full GPU text would eliminate the CPU/GPU render-pass split entirely and enable projected text that follows the stereographic distortion.
- [ ] Sky map: `[R]`efraction grid — toggle a second coordinate grid drawn in JNow + refraction-corrected (apparent) coordinates on top of the existing J2000 grid. Shows where objects actually appear from the observer's current site right now vs. the catalog J2000 positions. Full `Transform.SetJ2000 → RAApparent/DECApparent` (refraction on, site pressure/temperature from profile) for each grid line, tessellated like `BuildGridBuffers`. Near-zenith shift is ~0.35° precession alone; near the horizon the refraction bend stacks on top, reaching ~0.6° at 0° altitude. Makes the mount reticle's J2000 offset intuitive — the JNow grid passes through the reticle by construction for a topocentric-reporting mount.
- [ ] Sky map: Stellarium-style time adjuster — step the observation instant relative to now (e.g. press `+1h` / `+1d` and it becomes Thursday 23:04 etc.), not a pick-a-date. Stores an offset from wall clock (minutes, hours, days, weeks) so the user can scrub forward and back. **Plan ready:** see `PLAN-skymap-time-scrub.md`. Drives:
    - sky color (feeds `SkyMapState.GetSunAltitudeDegCached` with the adjusted instant)
    - LST so stars / crosshair / horizon rotate correctly
    - planet positions via `VSOP87a.Reduce`
    - horizon fill and below-horizon label dimming
  Must replace / extend the current "isPlanningTonight" bool path so the sky stays correctly coloured when a time offset is applied. HUD should show the scrubbed time prominently vs. wall clock so the user never confuses the two. A "reset to now" shortcut (e.g. `0`) returns offset to zero.
- [x] Guider graph: show dither events (markers/shading) — yellow dashed vertical lines at dither events, dim yellow settling shading
- [x] Guider tab: keep looping guide camera frames during centering/slewing — call `LoopAsync` when not guiding so the guide camera feed stays live. Currently the guide loop stops during centering and the tab shows "Waiting for guider"
- [x] Guider tab: show calibration frames — render guide camera during calibration phase with star position and profile. Remaining: star movement vectors, step count, and calibration progress overlay
- [ ] Guider: adaptive image-ready polling — sleep until near the expected end of exposure (N − `ImageReadyPollInterval`), then poll every 10ms, and in the final ~10ms poll every 1ms. Avoids wasting CPU on long sleeps while minimising latency at exposure end. Applies to `BuiltInGuiderDriver.CaptureGuideFrameAsync` and any other image-ready poll loop
- [x] Fake camera: apply mount tracking drift as pixel offset to star positions — DONE (PR #15 + PR #19): guide cam self-resolves the coupled mount from `IDeviceHub`, snapshots J2000 pointing per exposure and renders the deviation as pixel offset (`MountDriftPixels`); ST-4 forwards to the coupled mount so corrections physically move the encoders; `GuiderCalibration` converges end-to-end
- [x] Guider tab: guide camera image + crosshair (done). Remaining: star close-up + 1D intensity profile
  - [x] Add to `IDeviceDependentGuider`: `Image? LastGuideFrame`, `(float,float)? GuideStarPosition`, `float? GuideStarSNR`, `float? GuideStarHFD`
  - [x] Surface on `ISession` via `LiveSessionState.PollSession`
  - [x] `BuiltInGuiderDriver`: expose from `GuideLoop`'s `GuiderCentroidTracker`
  - [x] `FakeGuider`: generate synthetic guide frames with star field
  - [x] GUI: guide camera Canvas + crosshair overlay + SNR + frame counter
  - [x] GUI: star profile panel with 1D H/V intensity cross-sections + Gaussian fits + FWHM
  - [ ] PHD2: no image (show placeholder), SNR/mass from event stream only. PARTIAL (verified 2026-06-02): placeholder DONE (`GuiderTabState.PlaceholderReason`); SNR/mass from PHD2 event stream still TODO (`OpenPHD2GuiderDriver` leaves `GuideStarSNR` null).
- [x] Live session: show dither state — guider header shows `[Settling 0.42px]` with live distance, `[Paused (Slewing)]` during slews, correction arrows `[Guiding →142ms ↑38ms]`
- [ ] Cooling graph: same scrolling window treatment
- [ ] VSOP87 vectorization — convert 43K lines of hardcoded `amplitude * Cos(phase + frequency * t)` into coefficient arrays, evaluate with `Vector256<double>` (AVX2). Process 4 terms per iteration. Requires source generator or one-time conversion of all planet files (EarthX/Y/Z, MarsX/Y/Z, etc.)
- [ ] CLI: `train-guide-model` command for offline epoch training of the neural guide model — connects to mount + guide camera, records guide data for N worm cycles, then runs `TrainEpoch` with real PE data as teacher signal. Produces a base `.ngm` model file for the optical train. Aimed at permanent setups where users can invest a one-time training session to get a high-quality starting model. The online trainer (`TrainOnBatch`) should eventually converge to the same quality — offline training just gets there faster by seeing many PE cycles upfront instead of learning incrementally
- [ ] Equipment tab: fully data-driven profile panel — replace hardcoded `RenderProfileSlot` calls (mount, guider, guider cam/foc) with a declarative slot model that includes special sections (site editing, focal length input, device settings) as metadata. Currently core slots are hardcoded and only "extra" slots (weather, future types) are rendered dynamically via `EquipmentContent.GetExtraProfileSlots`. Goal: single loop over all slots with pluggable section renderers.
- [ ] Equipment tab: generic per-device settings pane — each device type (camera, mount, guider, etc.) should declare configurable properties via URI query params (like BuiltInGuiderDevice), and the equipment tab renders them automatically. FakeDevice should carry PE amplitude, period, guide rate etc. as URI params so FakeCameraDriver initializes from them instead of hardcoded defaults
- [ ] Fake camera: shift/change star field during slews — currently renders the same fixed seed regardless of mount pointing
- [x] Fake camera: scale synthetic background noise with exposure duration in `SyntheticStarFieldRenderer` — long subs (≥60s) have unrealistically clean backgrounds, causing per-channel stretch to produce degenerate parameters. Real cameras accumulate sky glow + dark current + read noise over time. DONE (2026-06-02): sky background scales by exposure on all paths (`skyLevel = skyBackground * exposureSeconds`, `SyntheticStarFieldRenderer.cs:132,270,836`); star flux scales too, read noise stays fixed (correct).

- [x] Fake filter wheels should have pre-installed filters (realistic filter sets per device ID)
- [ ] Planner: disambiguate duplicate common names — when multiple catalog entries share the same display name (e.g. NGC 4038 and NGC 4039 both named "Antennae Galaxies"), append the catalog designation in brackets: "Antennae Galaxies (NGC 4038)"
- [x] Planner: full rescan when site coordinates change significantly (>1°) instead of fast-path recompute — currently changing lat from -37 to 50 keeps southern-hemisphere targets with 0° altitude. DONE (2026-06-02): `AppSignalHandler.cs:489-510` runs full `ComputeTonightsBestAsync` when |Δlat| or |Δlon| > 1°, else fast-path `RecomputeForDate`.
- [ ] Extract VkImageRenderer UI layout to Abstractions — toolbar, file list, status bar, hit testing are renderer-agnostic; image rendering + texture upload stay Vulkan-specific in Shared
- [ ] Viewer tab renders at (0,0) ignoring contentRect — refactor ImageRendererBase.Render to accept a contentRect like PlannerTab/SessionTab/EquipmentTab so it works correctly when embedded in the tabbed GUI
- [x] Pinned items in planner should persist to disk — auto-save/load via `PlannerPersistence` keyed by profile+date, stored under `{OutputFolder}/Planner/{profileId}/{date}.json`
- [ ] Seed focuser `MaxStep` from hardware during ZWO EAF discovery (same `seedQueryParams` pattern as EFW slot count)
- [ ] Remember last focus position in profile URI after auto-focus (save after every auto-focus attempt, whether successful or not) so the focuser can start near the last known good position on next session
- [ ] HFD drift detection via linear regression over last N frames (NINA uses `AutofocusAfterHFRIncreaseTrigger` with configurable `SampleSize` and `Amount` threshold) — more robust than single-frame ratio comparison, reduces false refocus triggers
- [ ] Use IWeatherDriver ambient temperature for camera warm-up — when no hardware weather station or external temp sensor (Pegasus Astro) provides heat sink temp, pass ambient temp from weather driver as a denormalised property to the camera driver (via Session orchestration, not direct driver-to-driver coupling). Use as ambient target for `CoolCamerasToAmbientAsync` ramp
- [ ] SafetyMonitor integration — ASCOM `ISafetyMonitor` driver polling (5s interval watchdog) that can interrupt imaging and stop tracking when unsafe. Gate on safety in dither, meridian flip, and centering triggers. Park scope on unsafe condition.
- [ ] TUI Sixel preview in live session tab — render last captured frame as Sixel in a right panel (ConsoleImageRenderer + EncodeSixel). Needs async CreateFromImageAsync in the render loop or pre-render on frame arrival.
- [ ] `TuiCellRenderer<CellBuffer>` for live position view — implement `Renderer<TSurface>` over a terminal cell grid so the live-session forms widgets (target name, alt/az, tracking state, guider status, dither/settling indicators) render natively over SSH without Sixel. Box-drawing chars (`┌─┐│└─┘`) for frames, block shading (`█▓▒░`) for fills/bars, midpoint ellipse → perimeter glyphs for circles, truecolor or 256-cube depending on `$TERM`. Map xterm mouse + kitty keyboard → existing `InputEvent`. Sky map degrades to a non-spatial summary (current target, alt/az, next slew, visible-object count) driven by the same SignalBus data — Sixel/kitty graphics protocol stays as the optional "I really want pixels" mode for terminals that support it. Unlocks headless scope-host operation without leaving the SignalBus + widget-tree abstractions.
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

- [x] Integrate scheduler into `Session.RunAsync` flow — currently `ObservationLoopAsync` iterates linearly; needs to respect `ScheduledObservation.Start` times (wait until scheduled start before slewing). **Shipped** (branch `feat/top-5-todo`): `WaitForScheduledStartAsync` at the top of `ObservationLoopAsync` waits until `Start - ScheduledStartLeadTime` (default 3 min, covers slew + center + guider settle); same-/past-Start schedules short-circuit (linear advance unchanged), lead-adjusted start beyond session end skips the observation cleanly, late starts proceed unclamped. Chunked SleepAsync on the mount clock (cancellation-responsive). See `PLAN-scheduled-starts.md`
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
- [ ] FOV obstruction scout: absolute expected-star-count oracle for the **first observation of the night** — today `ScoutAndProbeAsync` returns `Healthy` unconditionally for the first target because there's no prior-observation baseline to compare against. A target behind a tree at the very start of the night is missed until the in-flight condition-deterioration check trips, by which time we've burned guider-start + several full-length exposures. Fix needs an absolute oracle that doesn't rely on a prior baseline — either a per-target catalog-derived expectation (use stars in field from the catalog, weight by limiting magnitude for the OTA × exposure × filter) or a cross-session per-(galactic-latitude × peak-magnitude) cache populated from successful baselines and keyed on FOV center. See `PLAN-fov-obstruction-detection.md` "Known limitations" #1. **Plan ready:** see `PLAN-obstruction-first-light-oracle.md` (catalog-floor approach chosen)
- [ ] Live site pressure/temperature for refraction (`IMountDriver.cs:395-396`) — replace hardcoded `SitePressure = 1010` / `SiteTemperature = 10` with: (1) live `IWeatherDriver.Pressure` / `Temperature` if a weather device is connected (works for `OpenMeteoDriver` + any ASCOM ObservingConditions device), (2) new `ProfileData.SitePressureHPa` / `SiteTemperatureCelsius` user-configurable fallback, (3) standard atmosphere as last resort. Matters for refraction at low altitudes (lat=15° pole sees ~3.4' refraction lift; lat=35° still ~1.4'). Same fix unblocks polar alignment refraction-aware decomposition — see `PLAN-polar-alignment.md` "Refraction at low pole altitudes". **Plan ready:** see `PLAN-site-conditions.md`
- [x] OnStep mount driver: extend `MeadeLX200ProtocolMountDriverBase` with `:GX`/`:SX` commands, native pier side, park/unpark, `FakeOnStepSerialDevice` for testing — `OnStepMountDriver<TDevice>` shipped using `:GU#` (bundled status), `:Gm#` (pier), `:hP-:hR-:hQ` (park 0/1), `:Te/:Td` (tracking on/off), `:TK/:TS` (rates). Serial + WiFi (`TcpSerialConnection` + mDNS `_telescope._tcp.local`). Follow-ups tracked at line 237 below.
- [ ] Faster imaging loop tick: reduce to `GCD/6` clamped `[1s, 5s]` — fix `FakeMeadeLX200SerialDevice` slew timer interleaving (immediate axis positioning instead of 100ms step timer)
- [x] `SessionFactory.Create(proposals)` hardcodes `defaultObservationTime = 30min` — should use planner's computed windows (handoff slider positions) or at least divide the dark window evenly among targets — `PlannerActions.ApplyHandoffWindows` (and `ComputeVisibleTimeInWindow` helper) now projects each pinned target's handoff slider window into `ProposedObservation.ObservationTime`, clipped to when the target is at or above `MinHeightAboveHorizon`. Wired into `BuildSchedule`; `defaultObservationTime` becomes a true fallback for proposals without a slider/profile.
- [x] Gracefully stop a session (`HostedSession.cs:39`) — `Session.RunAsync`'s `try/finally` (`Session.cs:288-300`) always runs `Finalise(CancellationToken.None)` (park mount, warm cameras, close covers) when the session token is cancelled. `HostedSession.StopAsync` cancels the token then disposes — gracefulness happens via the cancellation finally path. (The inline TODO comment at `HostedSession.cs:112` is now stale — should be removed.)
- [x] Wait until 5 min to astro dark — `WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync` runs at `Session.cs:260` before cooling/focus/calibration. `IsPolarAligned` check removed (TianWen has its own polar alignment now, no need to gate on an external flag).
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
- [ ] Write an MCP server for TianWen (expose session status, device state, observation schedule). PARTIAL (verified 2026-06-02): `TianWen.AI.MCP` (`tianwen-mcp`) ships `FitsTools` (Header/Stats/FindStars/PlateSolve/Pixels), `CatalogTools` (Lookup), `LogTools` (Tail). Session-status / device-state / observation-schedule tools still TODO (planned `stack.*`/`profile.*`/`devices.*`/`app.*` categories are doc-only in `Program.cs`).

## Imaging

- [ ] Not sure if `SensorType` LRGB check is correct (`SensorType.cs:54`)
- [ ] Find bounding box of non-NaN region in `Image.cs` (for stacked images with NaN borders)
- [ ] Star detection noise robustness: `FindStarsAsync` with `snrMin: 5` picks up false positives from shot noise halos around bright stars (e.g. M42 synthetic field: 49 rendered stars → 64 detected). Consider deblending or a minimum star separation filter to reject noise peaks near bright stars.
- [ ] **AHD debayer: SIMD via output-tile chunking** — Phase 3 (homogeneity comparison, ~70% of AHD's cost) is currently scalar with `Unsafe.Add` (commit 958e42e). To vectorise, process 8 output pixels per `Vector<float>` lane: the 5×5 neighbourhoods of consecutive x positions overlap heavily, so each dx offset becomes a single AVX2 load that serves all 8 pixels. Realistic landing: AHD 298 ms → ~140-150 ms (another ~2× on top of what we have). Non-trivial: the `if (diffH < diffV) homH++ else homV++` branch needs `Vector.GreaterThan` + masked accumulate, the direction-select tail needs `Vector.ConditionalSelect`, and `Vector.Sum`'s tree-add will likely change FP rounding order vs scalar sequential add → `DebayerRegressionTests` hashes will need repinning. Code complexity ~3-5× current scalar+unsafe path. Worth taking on if/when AHD perf dominates wall-clock for big groups (SoL 60s and similar 200+ frame stacks). See `Image.Debayer.cs:559-643` Phase 3 + the discussion in commit 958e42e for design context.

## AI Enhancement

Shipped on branch `ai-enhancement` (Phases 0-6 of `PLAN-ai-enhancement.md`): `IStarRemover` + `IStellarSharpener` + `INonStellarDeconvolver` atomic enhancers, `SharpenPipeline` orchestrator (additive + screen modes), shared `ChunkedNafnetRunner`, MTF helpers on `Image.Stretch.cs`, `ChunkedInference` tile/stitch, `HfdPsfEstimator`, `tianwen image {sharpen,remove-stars}` CLI. Items below are deferred follow-ups.

### Deferred CLI verbs (image group)

Each verb maps to an enhancer / classical implementation that hasn't been wired yet. CLI shape mirrors the shipped `tianwen image sharpen` (input FITS, `-o output`, default `<input>_<verb>.fits`).

- [ ] `tianwen image denoise` -- wraps `deep_denoise_{color,mono}_AI4.onnx`. New `IDenoiseEnhancer` interface + ONNX impl following the same shape as the three shipped enhancers.
- [ ] `tianwen image denoise-walking` -- specialised walking-noise variant via `deep_denoise_*_AI4_1w.onnx`. Could be a flag on `denoise` rather than a separate verb.
- [ ] `tianwen image upscale 2x|3x|4x` -- wraps `superres_{2,3,4}x.onnx`. New `IUpscaleEnhancer`. Output dimensions are scale * input.
- [ ] `tianwen image remove-trails` -- `satelliteRemovalAI4.onnx`. Per `PLAN-stacking.md` this logically belongs in the stacking pipeline as a pre-rejection filter; standalone single-image verb is also useful.
- [ ] `tianwen image correct-aberration` -- optical aberration correction (coma, astigmatism, off-axis distortion). Models hosted in `riccardoalberghi/abberation_models` (different repo + release cadence than AI4); needs a separate fetcher branch in `tools/tianwen-ai-models-fetch.ps1` + runtime self-bootstrap.
- [ ] `tianwen image flatten` -- ABE gradient removal (classical poly + RBF, no AI). See `PLAN-background-extraction.md`.
- [ ] `tianwen image stretch` -- apply MTF stretch for display / PNG export.
- [ ] `tianwen image debayer` -- Bayer raw → RGB.
- [ ] `tianwen image calibrate` -- apply master bias/dark/flat (wraps the calibrator types from `PLAN-stacking.md`).
- [ ] `tianwen image stats` -- HFD/FWHM/background/SNR.
- [ ] `tianwen image info` -- print FITS headers.
- [ ] `tianwen image histogram` -- text or PNG output.
- [ ] `tianwen image crop`, `tianwen image resize`, `tianwen image convert <fits|tiff|png|jpg>` -- existing IO methods on `Image`; just need CLI surfacing.

### Other deferred AI work

- [ ] **Deployment / runtime self-bootstrap of model files.** Today the AI enhancers depend on `%LOCALAPPDATA%\TianWen\models` being populated by the dev script `tools/tianwen-ai-models-fetch.ps1`; shipped binaries can't expect that. Need (a) in-app first-launch fetch with progress UI, (b) `tianwen models fetch` CLI sub-command (programmatic equivalent of the pwsh script). Hardlink-from-SAS-Pro fast path stays as a power-user optimisation.
- [ ] `tianwen models list` -- show which models are present under `%LOCALAPPDATA%\TianWen\models` and which are missing per the expected manifest. Complement to `tianwen models fetch`.
- [ ] **Classical (non-AI) fallbacks** via `AddTianWenClassicalEnhancers()` extension, `TryAddSingleton` so `AddTianWenAi` wins when models present. Lucy-Richardson `INonStellarDeconvolver`, unsharp-mask `IStellarSharpener`, bilateral/NLM denoise. No classical fallback for `IStarRemover` -- no respectable analogue.
- [ ] **Hexagon NPU acceleration on win-arm64.** AI4 ships pure FP32; QNN HTP wants INT8/INT16 or a pre-compiled `.serialized.bin`. Either upstream re-export at INT8 or our own ORT QNN compile pass. Current behaviour: FP32 nodes per-node-fall-back to CPU on win-arm64 -- works, just doesn't use the NPU. The new per-phase timing log (`infer={ms}ms`) is the diagnostic that surfaces this.
- [ ] **Per-chunk PSF re-measurement for `INonStellarDeconvolver`.** v1 `HfdPsfEstimator` returns a whole-image scalar; SAS Pro re-measures PSF per chunk via SEP to capture tilt/coma variation across the field. Would land as a new `SepPerChunkPsfEstimator` (port of SAS Pro's `measure_psf_radius`) registered through `IPsfEstimator` -- no changes to the deconvolver needed.
- [ ] **GUI menu entry for `SharpenPipeline`.** Surface the same flow through the GUI's processing menu so non-CLI users can run AI sharpen against the currently-loaded image. Reuses `SharpenPipeline` from DI; UI is a checkbox set per `SharpenRequest` field.
- [ ] **Star plate hue preservation under clipping.** `StretchStarsStep` is per-channel MTF (`Image.StarStretch` = fixed-midtones MTF with `m = 1/(3^amount+1)`), so a bright star that pegs one channel at 1.0 post-stretch but not the others collapses toward white -- an A0 blue and a K2 orange both end up grey. Existing `Image.StretchLumaPixelCpu` (Y'/Y chrominance scaling, used by the viewer's Luma stretch mode) already does the right math. Plan: add a `LumaBlend` knob to `StretchStarsStep` mirroring `StretchUniforms.LumaBlend`; 0 = today's per-channel (sensor-accurate, can clip), 1 = pure Y'/Y (no hue shift under any stretch), ~0.7 default for "coloured stars even at heavy stretch without going artificial". Zero overhead when `LumaBlend=0`. ~80 LOC + 2-3 unit tests against synthetic clipping cases.
- [ ] **Frank Sackenheim's colour-boost (saturation) option on star stretch.** The original SAS Pro `StarStretch` script ships a "Saturate" slider that increases star chroma -- gives the blue / orange end of the stellar spectrum visible punch without changing brightness ordering. Typical impl: RGB -> HSV (or LCh), multiply S/C by 1.x, back to RGB. Pairs naturally with the hue-preserving Luma-blend variant above; you stretch luminance, then optionally boost saturation in the same pass. Add as `StretchStarsStep.SaturationBoost` (default 1.0 = no-op). Cite SAS Pro `star_stretch.py` in the xmldoc for the exact ratio.
- [x] **Productionise the GHS starless stretch.** DONE on branch `ghs-converge` per [PLAN-ghs.md](PLAN-ghs.md). The dim-output problem was the convergence target -- median-target = 0.25 left the bg peak (mode) below 0.25 for typical astro frames where median sits above mode (signal tail). Resolved by adding mode-target convergence (`--ghs-target Mode`) + Cranfield's canonical multi-stage chain (`--ghs-stages 3`: stage 1 + BackgroundReduce + stage 2 b=2.5/hp=0.95 + stage 3 b=-1/hp=0.99 log). Canonical recipe: `--dual-stretch --starless-stretch-mode Ghs --ghs-target Mode --ghs-target-value 0.25 --ghs-stages 3`. **GHS stays opt-in per `feedback_ghs_not_default`** -- MTF remains the default starless stretch; user explicitly decided against promotion to `SharpenRequest.Canonical()` because GHS is a different aesthetic, not a universal upgrade. Outstanding: {broadband, narrowband, single-light} corpus validation outside SoL drizzle. The parameter-prediction model idea is parked -- the multi-stage canonical chain with mode convergence covers the in-corpus failure modes without ML.

## Stretch / Image Processing

Learnings from PixInsight Statistical Stretch (SetiAstro, v2.3).

- [x] Luma-only stretch mode (Rec. 709 luminance, stretch Y, scale RGB by Y'/Y)
- [x] HDR compression in GPU shader (Hermite soft-knee, `uHdrAmount`/`uHdrKnee` uniforms)
- [x] Normalize after stretch (2026-05-11) — `StretchUniforms.NormalizeScale` carries a precomputed `1/max` so the GPU stays single-pass. `Image.PredictPostStretchMaxScale` walks the top non-zero histogram bin of each channel and pushes it through the full chain (stretch + curves + HDR); CPU and GPU multiply the post-HDR value before the final clamp. Producer surfaces a `normalize: bool` knob on `AstroImageDocument.ComputeStretchUniforms`; tests in `StretchTests_NewPipeline.GivenColorFitsWithHdrWhenNormalizingThenPeakLiftedToFullRange` + `GpuStretchPipelineTests.GpuMatchesCpuForHdrNormalize`.
- [x] Iterative convergence — `Image.ConvergeStretchFactor` bisects stretchFactor using histogram until post-stretch median converges to target (0.25). Gated by `AstroImageDocument.UseIterativeConvergence`. **Bisection direction was inverted (fixed 2026-05-10)**; **WB-aware (median/mad/binNorm scaled by `whiteBalance` scalar) since 2026-05-10** so converged factor matches per-channel rendering when SPCC/skyBg WB is active.
- [x] Star-masked background extraction — `GetStarMaskedMedianAndMADScaledToUnit` recomputes median/MAD excluding star pixels after detection; `StarMaskedStats`/`StarMaskedLumaStats` preferred in `ComputeStretchUniforms`. **Two bugs fixed 2026-05-10**: (1) returned median in raw pixel-value space while the unmasked twin returns pedestal-subtracted — now consistent; (2) MAD floor `invMax * 0.5f` collapsed to 0.5 after `ScaleFloatValuesToUnitInPlace`'d images had `MaxValue=1`, pinning every masked MAD at half the dynamic range — replaced with fixed `0.5/65535` bin-width floor.
- [x] CPU mirror of GLSL stretch — `Image.StretchChannelCpu` / `StretchLumaPixelCpu` / `ApplyHdr` / `RenderStretchedRgba` (full image → RGBA buffer). `ConsoleImageRenderer` and `StretchTests_NewPipeline` route through these; both must produce visually equivalent output to the GLSL fragment shader for the same `StretchUniforms`.
- [x] Tycho-2 photometric color calibration — `Tycho2ColorCalibration.ComputeWhiteBalance` matches detected stars to Tycho-2, extracts aperture photometry, computes WB multipliers; flows through GPU UBO and CPU path
- [x] SPCC spectrophotometric color calibration — `Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance` integrates Pickles SED × system throughput (QE × CFA × filter) per matched star, fits WB multipliers; `AstroImageDocument.ComputeSpccColorCalibrationAsync` surfaces to viewer; `W` key tries SPCC first, falls back to sky-bg method. **Verified end-to-end** by `StretchTests_NewPipeline.GivenSyntheticStarFieldWhenSpccCalibratedThenWritesTiff` — projects Tycho-2 stars onto a synthetic Sony OSC field with matching synthetic WCS, runs SPCC against IMX533 QE × Sony CFA throughputs.
- [x] Background neutralization (pivot1 mode) — `BackgroundNeutralization.ComputeGains` ports SETI Astro Suite Pro's highlight-protecting neutralization; uses existing `ScanBackgroundRegion` for dark-region sampling; GPU shader applies `out = norm * g + (1-g)` before white balance; `N` key toggle, toolbar button. Algebraically verified equivalent to SETI's `out = 1 - (1 - val) * g`.
- [x] Fritsch-Carlson spline curves — `FritschCarlsonSpline` struct with monotonic cubic Hermite interpolation; `applyCurveLUT` in GLSL shader via 33-knot UBO; `ApplyCurveLut` CPU path. **`ComputeKnots33` capacity bug fixed 2026-05-10** (would crash GUI when user pressed Shift+B to toggle curve mode); array now sized to 33 floats with no padding so CPU/GPU divisor (lut.Length-1 vs hardcoded 32) align.
- [x] WB-vs-shadow coordinate-space mismatch fixed (2026-05-10) — `ComputeStretchUniforms` now scales per-channel median+mad by WB before deriving shadows/midtones/rescale, so post-WB norm and shadow live in the same space and channels reduced by WB don't clamp to zero.
- [x] SASP filter/sensor/SED data tracked in git (2026-05-10) — `filter_curves.gs.gz`, `sensor_qe.gs.gz`, `pickles_sed.gs.gz` exempt from the gitignore wildcard so CI can load them. Total +3 MB; only changes when SASP-data upstream changes.
- [x] Test verification overhaul (2026-05-10) — `StretchTests_NewPipeline` asserts every `StretchUniforms` field (Pedestal/Shadows/Midtones/Rescale/WhiteBalance/BackgroundNeutralization/CurveData) plus per-channel byte means after rendering. `StretchTestBase` got per-channel float-range + AutoLevel quantum-range assertions for all 4 legacy stretch test files. Catches per-channel collapse regressions.
- [x] **Mesa lavapipe CPU/GPU divergence — root cause was a dangling-pointer bug in `SdlVulkan.Renderer/VkPipelineSet.cs`** (resolved 2026-05-11 evening). NOT a Mesa bug.

  **Actual root cause**: `new VkPipelineColorBlendStateCreateInfo(blendAttachment)` (Vortice.Vulkan 3.2.1 constructor that takes a single attachment by value) stores `pAttachments = &attachment` pointing at the constructor's stack frame, which is reclaimed when the constructor returns. The graphics-pipeline create then reads garbage `VkBlendOp` from that location. On ARM64 the post-frame stack happened to contain values that decoded to valid blend ops; on x86_64 it contained values outside the valid `VkBlendOp` enum range. Release Mesa silently passed the garbage through `vk_blend_op_to_pipe`, producing zeroed-out fragment writes for primitives and the partial channel corruption we observed when the clear color was non-zero.

  **Fix**: in `SdlVulkan.Renderer/src/SdlVulkan.Renderer/VkPipelineSet.cs::CreatePipeline`, replace the single-arg constructor with an explicit `stackalloc VkPipelineColorBlendAttachmentState[1]` whose lifetime spans the `vkCreateGraphicsPipeline` call, then `pAttachments = blendAttachments; attachmentCount = 1`. The local `tools/lavapipe-repro` rebuilt against the fix reports the expected nonzero pixel counts on x86_64 lavapipe with Mesa 25.2.8 / LLVM 20.1.2 / 256-bit AVX2: FillRectangle=18200, DrawRectangle=2752, DrawLine=180-236, FillEllipse=15380, DrawEllipse=1272.

  **How we found it**: built Mesa 25.2.8 from source with `-Dbuildtype=debug -Dshared-llvm=enabled -Dgallium-drivers=llvmpipe -Dvulkan-drivers=swrast -Dplatforms=` and pointed the repro at `lvp_devenv_icd.x86_64.json` via `VK_DRIVER_FILES`. The debug build trips the assertion `vk_blend_op_to_pipe: Invalid blend op` in `src/vulkan/runtime/vk_blend.c:66` and tells us the value passed to `vkCmdBindPipeline`'s blend op was bogus. Distro Mesa is shipped without `--enable-debug`, so `LP_DEBUG=llvm` is a no-op and validation layers don't catch this; only the assertion in debug Mesa surfaces it.

  **Follow-ups** (all DONE, verified 2026-06-12):
  - Commit the fix to `SdlVulkan.Renderer` — done, published (tianwen consumes 6.0 as of PR #21).
  - Bump `SdlVulkan.Renderer` minor and publish via `/release-lib` — done.
  - Bump tianwen `Directory.Packages.props` to consume the new version — done (`549b612` bumped 5.1 -> 6.0).
  - Revert the `Assert.Skip(llvmpipe)` guards in `GpuStretchPipelineTests`, `VkHistogramPipelineTests`, `VkRendererPrimitiveTests` — done, no llvmpipe skips remain in the tree.
  - Delete `.github/workflows/test-mesa-latest.yml` — done, only `dotnet.yml` remains.
  - `lavapipe-bug-report-draft.md` deleted — no upstream bug to file.

- [x] Luma blend (2026-05-11) — `StretchUniforms.LumaBlend` (0 = pure linked, 1 = pure luma, default 1 preserves status-quo Luma-mode behaviour). Producer always populates `LumaStretch` (scalar Luma MTF params) and per-channel linked `Shadows/Midtones/Rescale` in Luma mode so the shader has both branches ready; GLSL `mix(linked, luma, lumaBlend)` inside the Luma branch. Tests: `StretchTests_NewPipeline.GivenColorFitsWhenBlendingLumaWithLinkedThenOutputInterpolates` + `GpuStretchPipelineTests.GpuMatchesCpuForLumaBlend`.
- [x] Rec.601 / Rec.2020 luma weighting (2026-05-11) — new `LumaWeighting` enum, `StretchUniforms.LumaWeights` `(R,G,B)` triple, resolved by producer; CPU mirror + GLSL Luma branch + `ComputePostStretchBackground` all read from the uniform. Default Rec.709 keeps existing callers on the same numerical path. Tests: `StretchTests_NewPipeline.GivenColorFitsWhenSwitchingLumaWeightingThenWeightsFlowThrough` + `GpuStretchPipelineTests.GpuMatchesCpuForLumaWeightingProfiles`.
- [x] Sensor-derived luma weights (2026-05-11) — `LumaWeighting.SensorMatched` resolves through `FilterCurveDatabase.TryComputeSensorLumaWeights(meta, ...)`, which integrates the doc's `BuildChannelThroughputs` (sensor QE x Sony CFA R/G/B) and normalises to sum to 1. Helper retries with `SensorType.RGGB` so debayered OSC images still resolve to the sensor-specific triple; gated on a recognised SensorModel so typos fall back to Rec.709 instead of silently returning CFA-only weights. Pure producer-side wire-up via `AstroImageDocument.ResolveLumaWeights`; no UBO / shader churn. Sample weights: IMX533 (0.29,0.36,0.34), IMX571 (0.35,0.37,0.28), IMX455 (0.30,0.37,0.34) -- broadband response (no photopic V(lambda) convolution, since the database doesn't ship it). Tests: `FilterCurveDatabaseTests.TryComputeSensorLumaWeights_*` + `StretchTests_NewPipeline.GivenOscMetaWhenLumaWeightingIsSensorMatched...` + `GpuStretchPipelineTests.GpuMatchesCpuForSensorMatchedLumaWeights`.
- [ ] Per-channel convergence — `ConvergeStretchFactor` runs once on luma stats; for Linked/Unlinked the converged factor is approximate per channel (still uses single factor with per-channel WB-scaled stats). Per-channel convergence would tighten the post-stretch median per channel; bigger refactor (factor becomes a triple).

## FITS Viewer

- [ ] Rename HDR button/label to "Compress Highlights"
- [x] Remove debug `Console.Error.WriteLine` WCS output from `Program.cs` DONE (2026-06-02): none present in `TianWen.UI.FitsViewer/Program.cs` (all logging via `ILogger`).
- [x] Support rec601/rec2020 luminance weighting options in luma stretch (2026-05-11) — see Stretch / Image Processing section.
- [ ] Grid label formatting: show arc-seconds for very narrow FOVs
- [ ] Crosshair / reticle overlay at image center
- [x] Annotation overlay (object names from catalogs when plate-solved)
- [x] Star detection overlay: `FitsDocument.DetectStarsAsync()` runs as background task,
      draws HFD-sized green circles, shows count/HFR/FWHM in status bar (S key toggle)
- [x] Background neutralization toggle: N key and toolbar `NeutBg` button — computes pivot1 gains from `ScanBackgroundRegion` and applies via GPU shader
- [x] SPCC color calibration via W key — tries spectrophotometric (Pickles SED + system throughput) first, falls back to sky-background method; toolbar `SPCC` button
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
- [ ] Better Tycho VT->V transform (Bessell 2000) for the moderately-red population. Today `CelestialObjectDB.cs` uses the ESA *linear* relation `V = VT - 0.090(BT-VT)`, `B-V = 0.850(BT-VT)` — duplicated in the single-star decode (`TryGetTycho2StarByTycId`) and the bulk render loop (`CopyTycho2Stars`). Per the ESA Tycho Catalogue **Vol 1 §2.2** (formulas 2.2.1/2.2.2) this is valid only for `-0.2 < (BT-VT) < 1.8` **and only for unreddened main-sequence stars**. The same doc's Field T5 note is the stronger caveat: the catalog's own V (derived via the fuller transform in **§1.3 Appendix 4**) has *"much larger systematic errors ... especially for red stars, i.e. with B-V > 1.5 mag."* Antares is `B-V = 1.84` — so per ESA itself, **no Tycho VT->V transform reliably yields its V**, independent of the colour-range bound. Bessell (2000, PASP 112, 961) is a better fit but is a cubic-spline **lookup table defined only to `(BT-VT) = 2.0`**, and Antares (`BT-VT ≈ 2.20`) / R Leporis (`≈ 5.80`) are beyond even that. That's why `PreferCrossRefMagnitude` (commit aad748e) defers bright stars to a curated SIMBAD/HR V, and that backstop must stay regardless of which transform we use. Adopting Bessell would still help the `BT-VT ≈ 1.5–2.0`, `B-V < 1.5` population (mostly the rendered Tycho buffer): (1) source the exact table accurately (paper / AstroCalc source / §1.3 App.4 — do **not** guess coefficients), (2) unify the two transform sites into one helper, (3) re-baseline every Tycho-magnitude test incl. R Lep's pinned `8.28` (extrapolated). Refs: ESA Tycho Cat Vol 1 §2.2 + §1.3 App.4 (local: `OneDrive/Dokumente/Astro-Info/TYC_Photometry_sect2_02.pdf`), Bessell 2000 (`iopscience.iop.org/article/10.1086/316598`), projectpluto.com/photomet.htm.

## Astrometry / Plate Solving

- [ ] Extract distortion model (SIP polynomial coefficients) from plate solver output
- [ ] Implement image undistortion using extracted distortion model
- [x] `CatalogPlateSolver` can't solve drizzle outputs from the CLI (`tianwen solve <fits>`) -- root cause was **`ICelestialObjectDB.InitDBAsync` was never called from the CLI's solve path**. The `StackingPipeline` path works because `MasterPostProcessor.cs:114` explicitly awaits `InitDBAsync(waitForTycho2BulkLoad: true, ct)` before invoking the solver; the CLI's `solve` subcommand skipped it. Without init, the catalog query returned 0 stars and the solver bailed in ~50 ms with no useful diagnostic (the ctor accepted `ILogger? logger = null` and DI's non-generic `ILogger` resolution silently left it `null`, so internal `_logger?.LogDebug` lines never fired). Fix: (1) self-init inside `CatalogPlateSolver.SolveImageAsync` via the idempotent `_isInitialized` fast path so any caller works; (2) DI registration switched to a factory lambda in `AstrometryServiceCollectionExtensions.cs` that resolves `ILogger<CatalogPlateSolver>` and upcasts to the ctor's non-generic `ILogger`. Verified: SoL drizzle + drizzle_autocrop both solve cleanly via CLI (RA=11.196h Dec=-61.35°, 887/969 and 663/753 stars matched; ~580 ms cold including Tycho-2 bulk decode, ~70 ms warm).

## Astrometry / Catalogs (Queries)

- [ ] Check if SIMBAD supports angular size + dimensions in queries

## Testing

- [ ] `ObjectType.IsStar()` helper method
- [ ] VDB has objects listed as `Be*`, but in HIP we only know stars (`*`) (`CelestialObjectDBTests.cs:73`)
- [x] Read WCS from FITS file in `FakePlateSolver` (`FakePlateSolver.cs:26`) DONE (2026-06-02): `SolveFileAsync` falls back to `Image.TryReadFitsFile(...)` WCS when no `CatalogPlateSolver` is injected (`FakePlateSolver.cs:50-54`).
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
- [x] Support ST-4 guide port as guiding output — `PulseGuideRouter` + `PulseGuideSource` (`?pulseGuideSource=Auto|Camera|Mount` on the guider URI) routes corrections through `ICameraDriver.PulseGuideAsync`. `Auto` prefers the mount (commit 8a08691): camera `CanPulseGuide` only proves an ST-4 *socket* exists (`HasST4Port`), not that a cable is connected
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
- [ ] Port debayer algos out for FC.SDK.Raw to consume — `Image.Debayer.cs` / `DebayerAlgorithm.cs` / `DebayerAlgorithmExtensions.cs` are pure Bayer-mosaic operations and don't depend on TianWen-specific types beyond `Image`/`Channel`. FC.SDK.Raw currently stops at the raw `ushort[]` mosaic on `CanonRawFile.BayerMosaic` (by design — astronomical stacking only needs the mosaic), but downstream consumers that want a sensible default JPEG render have to roll their own demosaic. Extract to DIR.Lib (or a new `SharpAstro.Imaging`/`SharpAstro.Debayer` package) so both TianWen and FC.SDK.Raw consume the same implementation; keep the 5×5 BilinearMono as the default and the simple 2×2 bilinear as a fallback. As of FC.SDK.Raw 1.4 the parallel ushort-based `CanonDemosaic.Bilinear`/`Ahd` already exist for consumer raw-render use cases — TianWen's float-based copies are intentional duplication for the stretch-aware astronomical path.

## Colour: Unified camera→sRGB matrix

The dcraw `adobe_coeff` 3×3 (now shipped via `FC.SDK.Raw.CanonCameraProfiles`) handles Canon CR2 sensible-default rendering. For OSC astro cameras (ZWO / QHY / etc.) and for Canon bodies whose spectral data is publicly available, we can derive the matrix from first principles — same QE × CFA spectral integration that `Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance` already does for SPCC WB. Three pieces, in order:

- [ ] **Add `ImageMeta.CameraToSrgbMatrix`** — nullable `float[]` (9 floats, row-major). Importers populate when known. Render pipeline applies after debayer + WB, before stretch. Identity when null (preserves current behaviour for FITS / TIFF / unknown sensors). This is the generic slot — it doesn't care whether the matrix came from a factory table or was derived from spectral curves.

- [ ] **`FilterCurveDatabase.TryComputeCameraToSrgbMatrix(sensorModel)`** — closed-form integral over the same QE × CFA curves SPCC already loads. For each sRGB primary, integrate against `QE(λ) × CFA_c(λ)` per channel to get the camera-RGB response; invert the resulting 3×3. No stars needed, no per-image fit — pure spectral algebra. Pre-condition: `FilterCurveDatabase.TryGet` returns spectral data for the sensor.

- [ ] **Jiang et al spectral CSV importer** — Stanford 2013 measured camera spectral response (QE × CFA per channel) for ~28 cameras including Canon EOS 5D Mark II / III, 1D X, 40D / 60D, Nikon D40 / D700 / D5100, several Sony / Olympus / Fuji bodies. Public CSV download. Small Python or C# tool that normalises to TianWen's `FilterCurveDatabase` `.gs.gz` format. Once imported, those camera models go through the spectral matrix path; cameras without entries fall back to `CanonCameraProfiles` (Canon) or identity (everything else).

Dispatch order on CR2 import: try spectral matrix first (best — first-principles); fall back to dcraw matrix (factory-curated); fall back to identity (warn). For non-Canon raws (NEF / ARW / etc.) only the spectral path applies until / unless a vendor-specific factory table lands too.

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

## Inbox: consolidated from Slack self-notes (2026-06-02)

New, still-actionable TianWen items lifted from the Slack "messages to self" brain-dump (Mar-May 2026),
deduped against the rest of this file. Date in parens is when the note was written. Triage into the
sections above when picked up. Notes that turned out to be already DONE or already tracked elsewhere are
intentionally NOT repeated here.

### Sky Map
- [x] **Pan/zoom jank at sub-90deg FOV (worst with SCP in view)** — FIXED 2026-06-11: the overlay Phase A cache (`VkSkyMapTab.RenderObjectOverlay`) was keyed on the exact view matrix below `WideFovThresholdDeg`, so every drag frame re-ran the catalog grid scan (`GatherSkyMapOverlayCandidates`; pole-in-view = full-RA Dec strip, ~16k cell lookups -> 100-240 ms/frame; ~5k cells elsewhere -> 40-90 ms). Fix: key on the unprojected view centre quantized to FOV/8 cells + FOV quantized to ~10% log steps, and widen the gather margin to `max(1deg, 0.15 x FOV)` (RA scaled 1/cos dec) so the cached set covers every view inside a cell; Phase B's per-frame projection culls as before. Measured at the SCP all-layers-on: 8 zoom/time stimuli -> ONE 93 ms frame (the legitimate cell-boundary rebuild) vs 1-3 slow frames per stimulus before.
- [x] Optional follow-up: move overlay Phase A (candidate gather) to a background task (the `TryApplyPendingStarBuild` pattern) so even cell-boundary rebuilds never block a frame — DONE 2026-06-11 (PR #22, `5d501c1`): Phase A gather runs off the render thread.
- [x] Search box + click-to-goto (slew to clicked object) (2026-04-16) — DONE: search panel (`OpenSkyMapSearchSignal` + query-changed incremental results) and click-select (`SkyMapClickSelectSignal`) both open the info panel, whose Goto button slews the connected mount (`SkyMapSlewToObjectSignal`); object labels are click-targets too (PR #24).
- [ ] Compass markers + horizon markers (2026-04-16)
- [ ] "N" key jumps the sky to local midnight (2026-04-18) (pairs with the time-adjuster item above)
- [x] "Show in planner" action from the sky map (2026-04-18) — DONE: "View in Planner" button in the info panel posts `ViewInPlannerSignal` (button width fixed in PR #24).
- [ ] Compute edge crossings (clip constellation / grid lines at the viewport edge) (2026-04-04)
- [ ] Load Gaia stars from Stellarium `.dat` files (the 3-vector unit-pos pipeline is already DONE; only the loader is missing) (2026-04-04, 2026-05-19)
- [ ] Bake a nebulosity layer into the baked Milky Way background image (2026-04-18)
- [ ] Share more rendering code between the Sky Map and the FITS viewer (2026-04-04)

### Planner / Session GUI
- [ ] Second planner view: all unique pinned objects plotted over their bounding visibility timespan (2026-04-18) (confirmed not implemented)
- [ ] Indicate a "light" / coverage marker under targets that actually have scheduled exposure time (2026-03-25) (the Tonight tab already goes read-only with Start disabled during a running session; only the per-target coverage marker is missing)
- [ ] Site change should unpin pinned targets when coordinates change, and must NOT invalidate cooler setpoint temps (2026-03-27) (unpin: confirmed not done)
- [ ] Planner input bugs: Ctrl+V paste does nothing, input field too small, Enter does not commit the "Today" date edit (2026-04-07)
- [ ] Replace the Live Session tab icon with a Milky Way image (2026-03-24) (currently the camera-flash emoji)
- [ ] Make the Windows taskbar entry more dynamic (progress / session state) (2026-04-02)

### Equipment / device UX
- [ ] Gate "Connect All" on discovery completion (2026-04-30)
- [ ] Clicking a device class should ensure all devices of that class are visible; vendor text is hard to read (2026-04-23)
- [ ] Better feedback than logging "Expected Camera, got mount" on a type mismatch (2026-04-23)
- [ ] "Hold Shift reveals extra options" pattern (e.g. Shift on discover; Shift = loop instead of single-click preview) (2026-04-16)
- [ ] Manual device creator UI (host / port fields) (2026-04-20) (overlaps the "Add unseen device" OnStep follow-up above)

### Sequencing / Session
- [ ] Avoid auto-focus when approaching the meridian (2026-05-14)
- [ ] Custom horizon file support (2026-03-17) (overlaps the deferred horizon-mask sub-plan)
- [ ] Configurable parking position (2026-03-17)
- [ ] Memoize pier side / polarity (2026-03-17)
- [ ] Spares: compute from higher-priority list items that conflict with the accepted schedule, prefer same object type (2026-03-23) (refines the existing spare-target fallback)
- [ ] Revisit imaging / guider / polar-align loop tick rate; see if it can be increased in real (non-fake) time (2026-05-01) (pairs with the GCD/6 faster-tick item above)

### Drivers / hardware
- [ ] Canon lens stepper as a special focuser: model manual vs automatic telephoto lenses as a special optical system so we know when auto-focus is usable; test that manual focus works during a session (2026-04-19)

### Stacker (no section exists yet)
- [ ] Support 3rd-party master frames (bias/dark/flat from other tools) (2026-05-19)
- [ ] Auto-pick flats by matching object time + filter (2026-05-19)
- [ ] Download Gaia SP stars (2026-05-19) (same source as the Sky Map Gaia loader)

### Stretch / Astrometry
- [ ] Auto-stretch ("MML") should use the object DB for grounding (object type + shape) (2026-05-07)
- [ ] Debug why so few stars match in Tycho-2 SPCC (2026-05-19)
- [ ] MCP: "best of tonight / this week / this month" tools (2026-05-21) (pairs with the MCP server + generalise-TonightsBest items above)

### Build / infra / docs
- [ ] Shrink git fetch size (~500 MB of `.zip` / `.gz` / `.lzip` data files) (2026-04-19)
- [ ] Create a subset of the emoji font to cut size (2026-03-26) (pairs with fetch-size)
- [ ] Mention FC.SDK in the skills docs (2026-04-19)
- [ ] Investigate AOT trim warnings: LibUsbDotNet (IL2104 / IL3053), CSharpFITS (IL3053) (2026-04-19)
- [ ] CI: ensure publish does not run while tests are still going; reduce server AOT publish warnings (2026-04-19)
- [ ] App self-update detection (2026-04-26)

### Code quality
- [ ] Move `RGBAColor32Extensions.cs` to a base layer (DIR.Lib) (2026-04-26)
- [ ] Use `Vector2` where we currently pass `PointF`-style pairs (2026-04-10)
- [ ] Document / clarify how `ResilientCall` interacts with collision detection (2026-04-26)
- [ ] Maybe support .NET Standard 2.0 for wider lib reuse (2026-05-02)
