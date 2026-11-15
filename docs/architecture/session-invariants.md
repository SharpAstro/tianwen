# Session invariants

Paragraphs moved out of CLAUDE.md that have no other owning document. Each is a rule that bites.

## Moved from CLAUDE.md, 2026-09-29

**Session failure surfacing (`ISession.FailureReason`):** when a run ends `SessionPhase.Failed`, the
session carries a plain-language, user-actionable reason (which device to check, what to do), surfaced
verbatim by the GUI notification feed, the hosted `/state` endpoint (`SessionStateDto.FailureReason`)
and the CLI. Throw `SessionFailedException(userMessage, inner)` for failures with a clear user
explanation (the inner exception carries the technical cause to the log); anything unhandled falls to
the generic catch ("Unexpected error: …"). Init device connects go through `ConnectOrFailAsync`
(`Session.Lifecycle.cs`), which names the device + telescope and is **deliberately fail-fast** -- a
device that cannot connect at init makes the night pointless (a flip-flat we cannot open leaves the OTA
blind), so fail there rather than discover it at dawn. The END-of-session flat block is the opposite:
best-effort, so a flats failure after a successful night never flips the session to Failed. Pinned by
`SessionFailureReasonTests`.

## Moved from CLAUDE.md, 2026-10-09

CLAUDE.md keeps these as one-line rules; this is the full text, verbatim.

`Session` (`TianWen.Lib/Sequencing/Session.cs`) is the central orchestrator. **Single-mount /
multi-OTA invariant**: `Setup.Telescopes` is plural for dual-/triple-saddle rigs, but there is exactly
one `Setup.Mount`. All OTAs share pointing and the current target. Multi-OTA buys parallel capture
(per-OTA camera/filter wheel/focuser) and per-OTA focus/filter/baseline state. Any future "branch"
or "re-order" logic must operate on the OTA set as a single unit.

`RunAsync` workflow: `InitialisationAsync` → wait for twilight → `CoolCamerasToSetpointAsync` →
`InitialRoughFocusAsync` → `AutoFocusAllTelescopesAsync` → `CalibrateGuiderAsync` → `ObservationLoopAsync`.
See the class XML doc + the relevant `docs/plans/*.md` for details on each phase.

**Guider calibration pier-side invariant:** `CalibrateGuiderAsync` (`Session.Lifecycle.cs`) slews to
HA **−0.5h** (30 min *east* of the meridian, target still approaching transit) before calibrating, NOT
west. `HA = LST − RA`, so HA < 0 = east = *before* crossing. East keeps the GEM on its pre-flip pier
side for the whole calibration, so the learned Dec guide sense matches the side rising targets are
imaged on. Calibrating west (HA > 0) is past the flip boundary on the opposite pier side → inverted Dec
sense + ambiguous flip-edge → Dec runaway. Hemisphere-independent (only apparent left/right mirrors in
the south); pinned by a both-hemisphere `[Theory]` in `SessionLifecycleTests`.

`ObservationLoopAsync` waits until `ScheduledObservation.Start - ScheduledStartLeadTime` (default 3 min,
covering slew + center + guider settle) before slewing to each target, via `WaitForScheduledStartAsync`
(`Session.Timing.cs`), so the scheduler's altitude-optimised slot times are honored, on the same mount
clock (`GetMountUtcNowAsync`) as the loop condition. Same-Start / past-Start schedules (the hosted API
stamping `Start = now`, legacy callers, existing tests) short-circuit the wait and advance linearly.
Late starts proceed unclamped (the full `Duration` still runs); a lead-adjusted start beyond session end
skips the observation cleanly.

**Meridian-flip oscillation invariant:** `MeridianFlipDecision.DecideFlipAction` must be gated so the
imaging loop can never re-issue a flip it already performed. Two backstops, in order: `if (hasFlipped)
return Continue` (a per-observation flag set after a successful flip in `Session.Imaging.cs`), then
`if (pierSideChanged) return AlreadyFlipped`. The HA-zone switch only reaches `CommandFlip` when
`!alreadyOnCorrectSide`, where `alreadyOnCorrectSide` compares the current pier side against
`DestinationSideOfPierAsync(target)`. **Load-bearing on SkyWatcher**, whose pier side is the Dec encoder
(the MECHANICAL state, which tracking never changes), so an unflipped GEM tracking west keeps reporting
the state it was slewed in and a naive "flip when HA > 0" is trivially true forever -> stuck `Slewing`,
zero exposures. **Never re-introduce an HA-only flip check**; gate on the *destination* side + the
`hasFlipped` memory. Pinned by `MeridianFlipDecisionTests` + a `mountPort:"SkyWatcher"` loop test.

**No-astro-dark night-window fallback:** `SessionEndTimeAsync` (`Session.Timing.cs`) derives the dark
window via `ObservationScheduler.CalculateNightWindow`, which has a fallback chain (astronomical −18° →
amateur-astro −15° → nautical −12° → polar-night 24h). It must **never** demand `EventTimes(...).Count == 1`
for astronomical twilight: at high-summer mid-latitudes (e.g. 50.9°N at solstice the sun bottoms ~−15.7°)
the sun never reaches −18°, and the old strict read threw, killing the session at a site that simply has
no astro-dark. Pinned by a no-dark German-solstice test in `SessionLifecycleTests`.

## Cloud while imaging (#1977)

N.I.N.A. once refocused the owner's rig because the stars looked bad, and they looked bad because of cloud. A refocus, a
plate solve or a focus trend fed by a cloudy frame wastes time at best and moves focus or the pointing the wrong way at
worst, so the session asks of every light, as it lands, whether it was shot through cloud.

**One judge, the viewer's.** `LiveCloudJudge` (`TianWen.Lib.Imaging.Grading`) grades each light on the stars the imaging loop
already found on it (`LightGrading.Grade`) and answers through `LightGrading.Verdict`, the one place the viewer's file list
and Grade section judge a frame too, so the session and the viewer cannot disagree. One judge per OTA per observation (a new
target starts with none). It is handed the rig's clear nights the viewer recorded (`LightGradeReferenceStore`,
`Viewer/clear-sky.json`, #1976), so a target cloudy from its first light is judged against another night rather than being
its own clear baseline; with none on file the run's own frames decide, exactly as before. A light with no grade (no stars, no
usable score) is NOT JUDGED, which leaves the state as it was: never read it as clear. On by default,
`SessionConfiguration.DetectCloudWhileImaging`; `false` images through cloud as before #1977.

**What a cloud light changes, and what a clear one gives back.** Every verdict lands through ONE method,
`Session.RecordLatestLightCloud`, which keeps, per OTA, when the run of cloud began (null while the latest judged light was
clear). While an OTA's latest light is cloud:

- the light is written `MAYBE_CLOUD_frame_...fits` (`CaptureRejection.SuspectedCloudPrefix`, `QueuedImageWrite.SuspectedCloud`),
  the nearly-done frame kept before a flip included;
- it adds no sample to the focus-drift window (`_frameMetricsHistory`) and runs no drift check, no baseline and no star-count
  test of its own: its HFD and star count describe the sky, not the focus;
- a meridian flip skips its recentre and its field-rotation check (`CompleteMeridianFlipAsync`): the verdict is
  `FlipEvidence.Inconclusive` and the mount's own report is believed. Pinned, with a clear-again positive control, by
  `MeridianFlipVerificationSessionTests.AFlipUnderCloudRunsNoPlateSolveAndOnceClearTheSolvesResume`.

The next clear light resumes all three. **What it does not give back**: a flip made under cloud is not recentred once the sky
clears, so the target stays where the flip's goto left it for the rest of that observation (the next observation centres as
usual). Accepted, since the issue asks only that the solve be skipped; a recentre on the first clear light would be its own change.

**Cloud that lasts is a condition deterioration.** Cloud on an OTA for longer than `SessionConfiguration.CloudWaitTimeout`
(null is `DefaultCloudWaitTimeout`, 15 minutes) takes the existing path (`PauseForConditionRecoveryAsync`, shared with the
star-count test): guiding paused, a wait up to `ConditionRecoveryTimeout` for the sky to come back to the drift baseline (or,
with none yet, the last clear light's metrics; never a clear light at all gives the target up at once), then imaging
resumes or the observation is given up. Parking stays with the safety and weather paths. **This is a behaviour change for
every run on the defaults**: a sudden cloud-over used to pause guiding at once through the star-count test (no frames
written); it now images on for up to 15 minutes, every frame saved `MAYBE_CLOUD_`, before the same pause. Long enough that a
passing cloud does not abandon the target, short enough that an overcast hour does not burn the observation into suspect
files. `SessionImagingTests.GivenCloudsRollingInWhenStarCountDropsThenConditionDetected` keeps the star-count path covered
by turning the judge off; `SessionCloudTests` covers the cloud path.

**`MAYBE_CLOUD_` is a suspicion, never a reject.** `CaptureRejection.IsRejectedAtCapture` is false for it, so every `BAD_`
reader keeps its meaning. The stacker leaves such frames out by default and says how many (`StackingOptions.IncludeSuspectedCloud`,
`tianwen stack --include-maybe-cloud`). The viewer draws it with its own mark (`GradeMark.SuspectedCloud`: a sun behind cloud in
colour, the cloud mask at half ink in Night), inks neither thumb as its state, and its verdict settles it through the ordinary
background rename: Cloud confirms it as `BAD_CLOUD_`, Good drops the prefix (`CaptureRejection.MarkedPath` strips it in the
same rename). The Grade section counts the folder's suspected frames apart from the rejected and the unmarked
(`FolderMarkTally`, read off the listed names, no I/O).

**The fake sky holds a floor.** `FakeCameraDriver.SkyMottle` (1 % by default, a fixed gain per 64 px block) gives the
simulated clear sky the fixed structure a real one carries. Without it a clear fake sky is shot noise alone, whose
patchiness is the ratio of two noise draws, and the judge called about one clear frame in five cloud: every fake-driven
session, the functional suites and an unattended GUI or TUI run alike, would write `MAYBE_CLOUD_` files and thin its focus
history at that rate. **The field must be smooth**: a block keeps its gain across its interior and eases to its
neighbour's over 8 px either side of the edge. A hard step there was found as faint "stars" on a starless overcast frame,
and the built-in guider re-acquired one every frame where it should have failed acquisition
(`FakeCameraMountCouplingTests.GivenCloudWhenGuidingRestartsWithReusedCalibrationThenAcquisitionFailsLoudly` hung).

## Moved from CLAUDE.md, 2026-11-15

The text `CLAUDE.md` carried until 2026-11-15, where this document did not already hold it; `CLAUDE.md` keeps one line
per rule and points here.

### Session (from CLAUDE.md)

- **A mount that cannot goto (`MountPointsWithoutGoto`: the SkyGuider Pro) is pointed by ONE routine,
  `PointWithoutGotoAsync`** (`Session.SlewLess.cs`): RA by its own motor, timed and never trusted, Dec by the operator in a
  wait the session ends itself once a solve finds the target. Every slew the session makes asks first, so a new one owes the
  same question. `docs/plans/slew-less-mount.md`.
- **A goto or Home never unparks a mount** (the owner, 2026-10-30): on a parked mount it is REFUSED, naming the Unpark step
  (`MountGoto.IsParkedAsync`), since unattended unparking moves hardware nobody may be watching (a tiny dome). Unpark is the
  operator's own command. **A run is the one exception, behind `SessionConfiguration.UnparkMount` (on by default, as N.I.N.A.
  unparks on a sequence start)**: initialisation only CHECKS (and fails at once when the run may not unpark), and
  `Session.UnparkForRunAsync` unparks at the run's first move, after the wait for dark or before the dusk sky flats, never
  during the evening wait. **Going home is ONE routine, `DeviceOperations.GoHomeAsync`** (the Home button and a goto to the
  site's pole): it asks NO software horizon limit (home is not an observing position, and near the equator the pole sits under
  any limit) and stops tracking on arrival, where an ordinary goto starts it.
- **Whether a flip HAPPENED is read off the IMAGE wherever the pointing state is `Computed`** (LX200 base, SGP):
  `WCS.RotationDeg` against the recentre's own solve, `MeridianFlipVerification.FromSolves`, `Inconclusive` falls
  back to the mount. `FakeMountDriver`'s tube state only MOTION changes (**a sync must not touch it**).
  `docs/plans/meridian-flip-verification.md`.
- **Focus-drift refocus is a TREND** (`FocusDriftDetector.EstimateTrendHfd` over the last
  `SessionConfiguration.FocusDriftSampleSize` comparable frames, `FrameMetrics.IsComparableTo`, against
  `FocusDriftThreshold`): **the LSQ divisor is the INCLUDED-sample count**; **the window is cleared on a drift
  refocus and on target change**; **one baseline per ACQUISITION SETTING** (`AcquisitionSetting`, #820). Assert
  through `Session.DriftRefocusCount`. `docs/plans/focus-tolerance.md`.
- **A light judged CLOUD as it lands is a suspicion the session acts on, never a reject** (#1977, `LiveCloudJudge`, the
  viewer grade's own `LightGrading.Verdict`, with the rig's clear nights; on by `SessionConfiguration.DetectCloudWhileImaging`):
  written `MAYBE_CLOUD_`, no drift refocus, no focus-history sample, and while the OTA's latest light is cloud a flip runs no
  plate solve (`FlipEvidence.Inconclusive`, the mount believed); a clear light resumes all of it, and every verdict lands
  through ONE method, `Session.RecordLatestLightCloud`. Cloud past `CloudWaitTimeout` (15 min) takes the
  condition-deterioration path, so a sudden cloud-over now images on for up to 15 minutes instead of pausing at once.
  In full: `docs/architecture/session-invariants.md`, "Cloud while imaging".

**Mount safety limits are NOT the meridian flip.** `MountLimits.Evaluate` (`Sequencing/MountLimits.cs`, pure) is
the mechanical bound, where the TUBE meets the pier or the ground; a flip is a *scheduling* choice. Derivations,
the GSServer sweep, live verification: `docs/plans/mount-safety-limits.md`, `docs/plans/gss-parity-audit.md`.
What bites:

- **`IMountDriver.GetAxisAngleAsync` (MECHANICAL, SkyWatcher only) WINS when present** (`MountLimitVerdict.Basis`).
  **Only a MEASURED pointing state, or one the SESSION verified, may drive it** (`MountLimits.TrustedPointingState`;
  `Session._verifiedPointingState`; `MountLimitWatcher` keeps the two-argument form).
- **Warn and act are a threshold plus a non-negative EXTRA**; **`alreadyActed` is a latch that downgrades to
  `Warn`, never clears**.
- **The meridian limit is in MINUTES and is the ULTIMATE CLAMP on the flip** (inside `MeridianFlipDecision`);
  never derive the limit from the flip.
- **Config lives on `ProfileData.MountLimits`**, never `SessionConfiguration`; **enforcement is in
  `PollDeviceStatesAsync`** and routes to `ImageLoopNextAction.LimitReached`, NOT `DeviceUnrecoverable`;
  **parking is opt-in for both limits**.
- **A mount that stops tracking unasked is a LIMIT EVENT, not a fault** (`Session.DetectDriverEnforcedStop`;
  `_raPulseOnStoppedAxis`).
- **Test traps:** `default(PointingState)` is `Normal`, SILENT for the meridian test, so place the mount by SYNC;
  **a run's site is `Session.Site`** (#798, `MountSiteExtensions`): a NaN altitude switches the HORIZON test off.
- **The verdict is telemetry** to the Home card's Flip column, on CLASS transitions only.
- **`MountLimitWatcher` (`Sequencing/`) is the enforcement half with no session running**: each 5 s it matches a
  connected mount against every discovered profile's `Mount` and skips one a session leases; a `BackgroundService`
  in `tianwen-server` only, since P6 (#936).

**A guide pulse is TWO methods, and picking the wrong one is silent.** `StartPulseGuideAsync` (`IMountDriver` /
`ICameraDriver` / `IPulseGuideTarget`) commands the hardware and RETURNS; `PulseGuideAsync`
(`PulseGuideTargetExtensions`) starts AND waits, which a caller almost always means. **Every driver honours the
primitive**: **the in-flight count rises BEFORE the first write and falls only when the hold ends** (GSS #109),
and **a failed restore parks in `_pendingPulseFault`**, re-thrown from the next `StartPulseGuideAsync` *and*
`IsPulseGuidingAsync`. **A test for the non-blocking starter needs `ExternalTimePump` and a `[Fact(Timeout=…)]`**.
`docs/plans/gss-parity-audit.md`.

### Backlash Auto-Tuning (from CLAUDE.md)

Every successful AutoFocus opportunistically infers per-direction backlash from the verification
exposure (no separate measurement routine, based on the cloudynights "no need to measure backlash,
just overshoot enough" approach). The hyperbola fit predicts HFD at `bestPos`; the verification frame
measures actual HFD at the mechanical position the focuser landed on. Inverting the fit
(`Hyperbola.StepsToFocus`) gives the lag, and `B = currentOvershoot + lag`. Per-focuser EWMA (α=0.3)
sized to `B × 1.5`. State persists to `Profiles/BacklashHistory/<focuserDeviceId>.json` and rounded
values mirror back to the focuser URI's `focuserBacklashIn`/`focuserBacklashOut` query keys at
session-end. Wire-up: `BacklashEstimator`, `BacklashHistoryPersistence`, `Session.Focus`,
`EquipmentActions.SaveBacklashEstimatesIfChangedAsync`.
