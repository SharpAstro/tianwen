# TODO -- Sequencing & Polar Alignment

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

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

- [x] Integrate scheduler into `Session.RunAsync` flow — currently `ObservationLoopAsync` iterates linearly; needs to respect `ScheduledObservation.Start` times (wait until scheduled start before slewing). **Shipped** (branch `feat/top-5-todo`): `WaitForScheduledStartAsync` at the top of `ObservationLoopAsync` waits until `Start - ScheduledStartLeadTime` (default 3 min, covers slew + center + guider settle); same-/past-Start schedules short-circuit (linear advance unchanged), lead-adjusted start beyond session end skips the observation cleanly, late starts proceed unclamped. Chunked SleepAsync on the mount clock (cancellation-responsive). See `docs/plans/scheduled-starts.md`
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
- [ ] Polar align: optionally rolling-median the recovered **axis vector** (not the WCS center) over `RefineMedianWindow` ticks to beat down the near-pole plate-solve noise floor (~1' p-p in unit-vector terms at Dec~-89.97°, geometric -- see `docs/known-limitations.md`). A prototype existed in `f7d7470^` (medianed wcsCenter, reverted); apply it to `_refiner.RefineAxis(...)`'s output in `RefineAsync` instead. Add `RefineMedianWindow=5` to `PolarAlignmentConfiguration` (test configs = 1 for determinism). Only if pole noise proves unacceptable in practice.
- [ ] Polar align: polar-asterism overlay -- project Octans / σ Oct (SCP) and Polaris / Kochab / UMi (NCP) through the live refine WCS and label them on the mini viewer, so the user can visually confirm the field is on the right pole even when the gauge readout is noisy. New `PolarAsterism.cs` constants -> project via `wcs.SkyToPixel` in `RefineAsync` -> `ImmutableArray<(string,double,double)> AsterismMarkers` on `PolarOverlay` -> `VkMiniViewerWidget.DrawWcsAnnotationOverlay`.
- [ ] Baseline HFD per-target: key by observation index (not telescope index), smart refocus-on-target-change — skip refocus when recent focus is good, establish new baseline from median of first N frames; `AlwaysRefocusOnNewTarget` config option
- [x] FOV obstruction detection: compare first-frame metrics against previous target's baseline; if anomalous, nudge mount up by one frame radius — if metrics recover, exit imaging loop for this target (tree/building in FOV) — `Session.Imaging.Obstruction.cs` `ScoutAndProbeAsync` + `NudgeTestAsync` + `EstimateObstructionClearTimeAsync`; trajectory-aware wait if obstruction will clear in `<= ObstructionClearFractionOfRemaining`. See `docs/plans/fov-obstruction-detection.md` + `docs/architecture/fov-obstruction.md`.
- [x] FOV obstruction scout: absolute expected-star-count oracle for the **first observation of the night** — today `ScoutAndProbeAsync` returns `Healthy` unconditionally for the first target because there's no prior-observation baseline to compare against. A target behind a tree at the very start of the night is missed until the in-flight condition-deterioration check trips, by which time we've burned guider-start + several full-length exposures. Fix needs an absolute oracle that doesn't rely on a prior baseline — either a per-target catalog-derived expectation (use stars in field from the catalog, weight by limiting magnitude for the OTA × exposure × filter) or a cross-session per-(galactic-latitude × peak-magnitude) cache populated from successful baselines and keyed on FOV center. See `docs/plans/fov-obstruction-detection.md` "Known limitations" #1. **SHIPPED** (branch `feat/top-5-todo`, A+C): the design pivoted from catalog-floor-only to a **zenith calibration anchor** — the rough-focus zenith frame (always unobstructed, ~air mass 1) calibrates `detected/catalog-predicted` = transparency x detection efficiency. A (oracle): first scout compared to `catalog(target, scout-limit, airmass-dimmed) x zenithEfficiency x OracleFactor`, shortfall -> existing `NudgeTestAsync`; catalog-floor fallback (efficiency 1.0) when no valid gauge; narrowband + sparse-field + missing-DB guards. C (cloud gate): crushed zenith efficiency (< `CloudGateEfficiencyFloor`) routes to hold-and-re-gauge after rough focus. New: `StarDetectionModel`, `CatalogStarCounter`, `NightSkyGauge`, `Session.Imaging.SkyGauge.cs`; 4 `SessionConfiguration` knobs; 16 unit + 2 functional tests. B (transparency HUD readout from `EffectiveLimitMag`) remains the follow-up.
- [x] Live site pressure/temperature for refraction (`IMountDriver.cs:395-396`) — **DONE** (branch `feat/top-5-todo`): new pure `SiteConditions.Resolve(IWeatherDriver?)` (live weather -> standard, per value) + `ApplyTo(Transform)`; `TryGetTransformAsync(SiteConditions, ct)` overload drops the `1010`/`10` hardcode (standard tier auto-derives pressure from elevation); `Session.ResolveSiteConditions()` reads the live weather driver and feeds the 4 session transform call sites; polar alignment swapped to the same resolver. Matters for refraction at low altitudes (lat=15° pole sees ~3.4' refraction lift; lat=35° still ~1.4'); unblocks polar-alignment refraction-aware decomposition (`docs/plans/polar-alignment.md`). **Design note:** pressure/temperature are varying values (unlike the static lat/long/elevation), so they are deliberately NOT stored in the profile — read live from the weather driver each call (which caches its own last reading), and the no-weather fallback derives pressure from the profile's static elevation. A proposed `ProfileData`/`SessionConfiguration` override + Equipment-tab editor was dropped for that reason.
- [x] OnStep mount driver: extend `MeadeLX200ProtocolMountDriverBase` with `:GX`/`:SX` commands, native pier side, park/unpark, `FakeOnStepSerialDevice` for testing — `OnStepMountDriver<TDevice>` shipped using `:GU#` (bundled status), `:Gm#` (pier), `:hP-:hR-:hQ` (park 0/1), `:Te/:Td` (tracking on/off), `:TK/:TS` (rates). Serial + WiFi (`TcpSerialConnection` + mDNS `_telescope._tcp.local`). Follow-ups tracked at line 237 below.
- [ ] Faster imaging loop tick: reduce to `GCD/6` clamped `[1s, 5s]` — fix `FakeMeadeLX200SerialDevice` slew timer interleaving (immediate axis positioning instead of 100ms step timer)
- [x] `SessionFactory.Create(proposals)` hardcodes `defaultObservationTime = 30min` — should use planner's computed windows (handoff slider positions) or at least divide the dark window evenly among targets — `PlannerActions.ApplyHandoffWindows` (and `ComputeVisibleTimeInWindow` helper) now projects each pinned target's handoff slider window into `ProposedObservation.ObservationTime`, clipped to when the target is at or above `MinHeightAboveHorizon`. Wired into `BuildSchedule`; `defaultObservationTime` becomes a true fallback for proposals without a slider/profile.
- [x] Gracefully stop a session (`HostedSession.cs:39`) — `Session.RunAsync`'s `try/finally` (`Session.cs:288-300`) always runs `Finalise(CancellationToken.None)` (park mount, warm cameras, close covers) when the session token is cancelled. `HostedSession.StopAsync` cancels the token then disposes — gracefulness happens via the cancellation finally path. (The inline TODO comment at `HostedSession.cs:112` is now stale — should be removed.)
- [x] Wait until 5 min to astro dark — `WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync` runs at `Session.cs:260` before cooling/focus/calibration. `IsPolarAligned` check removed (TianWen has its own polar alignment now, no need to gate on an external flag).
- [ ] Maybe slew slightly above/below 0 declination to avoid trees, etc. (`Session.Lifecycle.cs:19` — guider calibration slew). NOT covered by `ScoutAndProbeAsync` because the scout is OTA-imaging-only, runs after centering, and requires a prior-observation baseline. See `docs/plans/fov-obstruction-detection.md` "Known limitations" section for what would need to change to unify these.
- [x] **Calibrate guider EAST of the meridian, not west** — DONE (`173e3b4`, PR #30, shipped v5.0.862): `CalibrateGuiderAsync` slewed to HA **+0.5h** (30 min WEST of the meridian), already past the GEM flip boundary onto the opposite pier side from where rising targets are acquired, which inverts the Dec guide-calibration sense and sits on the ambiguous flip edge → **Dec runaway** during/after calibration. Now slews to HA **−0.5h** (30 min EAST, still approaching the meridian) so the mount stays on its pre-flip pier side and the Dec sense matches the imaging side (`HA = LST − RA`, HA < 0 = east = before crossing). Both-hemisphere `[Theory]` (Vienna + Melbourne) asserts post-calibration HA is east and within ~40 min of the meridian.
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
- [x] Device disconnect resilience in imaging loop — when mount/camera/guider disconnects, attempt reconnect with backoff instead of immediately advancing to next observation; only bail after N retries or timeout — `ResilientCall` wrapper + `Session.ErrorHandling.ResilientInvokeAsync` + per-driver fault counter + `DeviceUnrecoverable` escalation. See `docs/plans/driver-resilience.md` + `docs/architecture/driver-resilience.md`.
- [x] Altitude check distinguishes rising vs setting targets — `EstimateTimeUntilTargetRisesAsync` samples altitude at 5-min intervals; if rising and within `MaxWaitForRisingTarget` (default 15 min), waits then retries slew; otherwise tries spare targets then advances
- [x] Write `FOCALLEN` and `FOCUSPOS` to FITS output headers (currently read on load but never written)
- [x] Write `DATAMIN` to FITS output headers (only `DATAMAX` was written)
- [x] `FocusDriftThreshold` default changed from 1.3 (30%) to 1.07 (7%); already a `SessionConfiguration` setting
- [ ] Discrete auto-focus triggers as explicit, configurable conditions on top of today's single-frame HFD-drift check (`ImagingLoopAsync` + `FocusDriftThreshold`): after filter change, after N exposures, after a temperature delta, after elapsed time, and a suppress-when-approaching-meridian guard. The HFR-regression-over-N-frames trigger is already tracked in [TODO.md](../../TODO.md); the avoid-AF-near-meridian note is in [inbox.md](inbox.md). Folding them into one trigger set makes each refocus decision explicit and unit-testable instead of a single ratio heuristic.

## Flat-frame acquisition (automation)

`FrameType.Flat`, the cover/calibrator device (`ICoverDriver`), and the stacking calibrator
(`MasterFrameBuilder`) all exist. **Phase 1 SHIPPED** (panel/calibrator flats): see
[docs/plans/flat-frame-automation.md](../plans/flat-frame-automation.md).

- [x] Auto-exposure flat routine: bracket exposure to converge mean/median ADU to a target (~0.5 of full well) per filter -- pure `FlatExposureSolver` (`Imaging/Calibration/`) + `Session.TakeFlatsAsync` driving the cover/calibrator panel brightness. 12 + 2 tests.
- [ ] Sky-flat (twilight) variant: ramp exposure as sky brightness changes through dusk/dawn (Phase 2)
- [x] **Per-OTA fan-out** -- each OTA flats its own train; `TakeFlatsAsync` loops `Setup.Telescopes`, gated on a controllable calibrator (flip-flat or standalone lightbox), per-filter via `SwitchFilterIfNeededAsync`.
- [x] Sequence integration: opt-in `SessionConfiguration.TakeFlatsOnSessionEnd` end-of-session block (after the observation loop, before `Finalise` warms cameras), writing `FrameType.Flat` frames under `Flats/<date>/<filter>/Flat/` that the stacker's `MasterFrameBuilder` consumes automatically (grouped by FITS headers, not path).
- [ ] **Manual flat-panel mode -- OUT OF SESSION** (Phase 3): a dumb EL panel the user switches on by hand has no driver to gate on, so it can't live in the unattended end-of-session hook. It belongs on the on-demand surface (CLI/API), where the user explicitly starts a flat run that skips all cover/calibrator control and just auto-exposes + captures against the manual light. UI shape: a dropdown to pick the illumination source with a **light-bulb (💡)** entry for the manual panel (alongside auto-calibrator / sky-flat). Misconfigured illumination just fails the solver gracefully.
- [ ] Auto-pick flats by matching object time + filter at stack time (also noted in [inbox.md](inbox.md))

