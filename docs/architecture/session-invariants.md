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
