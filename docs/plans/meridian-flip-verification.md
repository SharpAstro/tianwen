# Meridian flip verification from the image (and the guider-sense fix that rides with it)

## Painpoint

A German equatorial mount's meridian flip is a physical event the session must confirm before it
keeps imaging: the OTA is now on the other side of the pier, the field is rotated 180 deg, and the
guider's Dec correction sense has changed. Get "did it flip?" wrong and the night is quietly ruined --
the guider fights the star, every sub trails, and the run reports success.

Today the session decides the flip happened on ONE signal. `Session.Imaging.cs`'s
`PerformMeridianFlipAsync` re-slews with a small westward RA offset and then:

```
if (newHourAngle > 0) return CompleteMeridianFlipAsync(...);   // "the flip actually happened"
```

**On a mount that reports a COMPUTED pier side that check cannot fail, and cannot detect a flip that
did not happen.** `IMountDriver.PointingStateSource` is `Computed` for the LX200 base driver, SGP and
`FakeMountDriver` (only SkyWatcher, OnStep, ASCOM and Alpaca are `Measured`); on those, both the pier
side and the hour angle are derived from the clock, so HA is `> 0` the instant the pointing is west of
the meridian **whether or not the tube physically flipped**. If the mount's own firmware declines the
flip in that moment -- the case the user hit on an LX85: a goto that just misses the firmware's flip
window and tracks straight through -- the session reads HA `> 0`, declares `Success`, sets
`hasFlipped`, reverses the filter ladder, and (via `CompleteMeridianFlipAsync`) flips the guider's Dec
calibration and restarts guiding, all with the OTA physically UNFLIPPED and now guiding with an
inverted Dec. It is the computed-state twin of the SkyWatcher "`HA > 0` is trivially true forever"
trap that CLAUDE.md's meridian-flip section already warns about and that was closed for SkyWatcher by
gating on the destination side; the computed-state path still trusts HA alone.

N.I.N.A. is no better here (checked against the local clone, `~/source/repos/other/nina`):
`AscomTelescope.MeridianFlip` verifies a flip by re-reading `SideOfPier` and retrying/`SetPierSide`,
but an LX200 ASCOM driver *computes* `SideOfPier` the same way, so it is equally blind. Its
`RotateImageAfterFlip` is a cosmetic `SetImageRotation(+180)` on the display, and `Recenter` is a
best-effort plate solve that restores POINTING and never inspects the solved orientation. No amateur
tool uses the image as flip proof.

## The image is the witness, and it is nearly free

A German flip is a **pure 180 deg field rotation**: parity (handedness) is fixed by the optical train
and does not change across a flip, so this holds regardless of the mirror count in the train -- only the
rotation flips. The evidence is already on hand:

- The post-flip recenter runs a full plate solve (`CenterOnTargetAsync` ->
  `PlateSolveAndSyncCoreAsync`) that returns a `WCS` with a CD matrix. The CD matrix's rotation is the
  field position angle. No extra exposure is needed -- this solve already happens.
- The last PRE-flip solved `WCS` is already retained: `Session` keeps `_plateSolveHistory`, a queue of
  `PlateSolveRecord` each carrying `Solution: WCS?`. So the comparison needs NO new session state, just
  the most-recent successful centering solution before the flip.

The test: let `dPA` be (post-flip solved PA) minus (last pre-flip solved PA), wrapped to (-180, 180].

- `|dPA - 180|` small  => the field rotated 180 deg => the mount PHYSICALLY flipped. Trust it even if a
  computed pier side disagrees (this is also how a firmware AUTO-flip with no goto -- the OnStep/LX85
  case -- is recognised on a measured-state mount that already reports it, and now on a computed one).
- `|dPA|` small AFTER A COMMANDED FLIP => the flip did NOT happen. Retry (the loop already retries up
  to `maxFlipAttempts`) and, if it never rotates, FAIL the observation loudly instead of imaging on
  with a false `hasFlipped` and an inverted guider.

Measured-state mounts keep their current pier-side path; the image becomes a CROSS-CHECK there, and the
PRIMARY proof only where the pier side is computed. A future rotator makes the raw PA ambiguous, so the
check must be gated "no rotator moved across the flip" when `DeviceType.Rotator` exists (see
[rotator.md](rotator.md)); until then there is none.

## The guider-sense bug this exposes (independent, rides along)

`GuiderCalibrationResult.WithMeridianFlip()` (`Devices/Guider/GuiderCalibration.cs`) negates ONLY the
Dec rate/displacement and leaves the RA (camera) angle unchanged, with a doc comment that states the
physics backwards ("the Dec guide RESPONSE on the sensor inverts but the RA response does not").
Trace it on a real GEM whose field is rotated 180 deg on the sensor:

- **RA**: the axis turns the same way, so a "west" pulse still moves the pointing west on the sky; on a
  sensor rotated 180 deg that response INVERTS. Always.
- **Dec**: SkyWatcher's Dec pulse is axis-based (the driver's own note: "sky sense reverses post-flip"),
  so a "north" pulse now moves the sky south -- and the sensor is rotated too; the two reversals cancel
  and the Dec response on the sensor is UNCHANGED. Only a mount that keeps sky-relative Dec
  (a compensating ASCOM driver) inverts Dec on the sensor as well.

So `WithMeridianFlip` has it inverted on both axes for the common (axis-based-Dec) mount. It matches
PHD2's model only when read as "RA + 180 always, Dec + 180 only for sky-relative-Dec mounts" -- the
opposite of what the code does. This has never been caught because **`FakeCameraDriver` renders a
constant `GuideRotationDeg = 15 deg` and never rolls the field with the pier side**: in the fake the
Dec-only inversion the code applies is exactly what the fake shows, so the fake is self-consistent with
the bug. The `reverseDecAfterFlip` query key keeps its name but its MEANING inverts to PHD2's.

## Why one fake change unblocks both

Both the missed-flip detection and the guider-sense correction are untestable today for the same
reason: the fake camera does not rotate its rendered field on a flip. Fixing that is the enabling step
and belongs first. Once the fake rolls 180 deg when the coupled mount's pier side differs from the
reference state:

- an E2E on a **computed-state** fake mount can drive a goto that misses the flip window (mount reports
  HA `> 0` but pier side and image unchanged) and assert the session today FALSELY succeeds -- then that
  the PA check catches it;
- an E2E on the **fake SkyWatcher** can assert the post-flip guide frame is rotated 180 deg and that the
  guider converges, which fails against the current `WithMeridianFlip` and passes after the sense fix.

## Phasing

- **P0 -- Fake camera rotates on flip.** `FakeCameraDriver` adds 180 deg to its render roll (guide AND
  main path) when the coupled mount's reported pier side differs from a captured reference. Test-first:
  write the computed-state missed-flip E2E and watch it PASS WRONGLY (the defect), and a
  fake-SkyWatcher post-flip-rotation E2E. `GuideRotationDeg` stays the constant baseline roll; the flip
  adds to it. No production behaviour changes yet.
- **P1 -- `WCS.RotationDeg`.** A rotation/position-angle accessor derived from the CD matrix
  (`atan2(CD2_1, CD1_1)` in the standard convention; `WCS` today exposes `CD1_1..CD2_2` and
  `HasCDMatrix` but no rotation -- the CROTA handling in `WCS.FromHeader` is read-path only). Unit-test
  on a known CD matrix and on a matrix rotated 180 deg from it.
- **P2 -- PA cross-check in the flip path.** In `PerformMeridianFlipAsync` /
  `CompleteMeridianFlipAsync`, compute `dPA` from the last pre-flip successful centering solve in
  `_plateSolveHistory` against the post-flip centering solve. On a computed-state mount the PA verdict
  OVERRIDES the HA-only check: a commanded flip with `dPA ~ 0` retries and then fails the observation
  (`ImageLoopNextAction.RepeatCurrentObservation` already exists as the failed-flip exit); an auto-flip
  with `dPA ~ 180` is accepted even when the computed pier side did not move. Measured-state mounts log
  the PA agreement as a cross-check but keep their pier-side verdict.
- **P3 -- Feed the truth to the guider, and fix `WithMeridianFlip`.** The "really flipped" answer from
  P2 (not the computed pier side) drives whether the guider flips its calibration, and
  `WithMeridianFlip` is corrected to "RA + 180 always, Dec + 180 only for sky-relative-Dec mounts"
  (the `reverseDecAfterFlip` switch keeps its name, meaning inverted), now testable via P0.

P0-P2 are the safety win (detect and fail-loud on a missed commanded flip; accept a real auto-flip).
P3 is the guiding-quality correction and can follow.

## Invariants to preserve

- **Never re-introduce an HA-only flip-success check** on a computed-state mount -- that is the whole
  bug. Gate the PA override on `PointingStateSource == Computed`; measured-state mounts already have a
  real pier side and the PA is a cross-check only.
- **The check is parity-independent by construction** (a flip is a rotation, not a reflection); do not
  fold any mirror-count or handedness term into it.
- **Gate on "no rotator moved"** once `DeviceType.Rotator` lands, or a deliberate framing rotation
  reads as a flip.
- **P0 must not change any production behaviour** -- it only makes the fake honest, so the two E2Es can
  see the defects before the logic moves.

## Status

**NOT STARTED** (design captured 2026-08-30 from a session discussion; both defects currently live only
in that conversation). No meridian-flip plan existed before this; the flip behaviour is otherwise pinned
only by `SessionObservationLoopTests` (the GEM-only `[Theory]` and the across-meridian flip case) and
touched by `mount-safety-limits.md` as the limit's neighbour. Related but distinct:
`docs/todo/drivers.md` "post-meridian-flip re-rotate" (driving a physical rotator to preserve framing,
not verifying the flip) and `docs/todo/sequencing.md` "audit that every exit path leaves a sane flip
state".
