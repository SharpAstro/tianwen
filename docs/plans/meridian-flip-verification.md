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

- **P0 -- Fake camera rotates on flip. DONE.** Built differently from this sketch, and the difference
  is the point: the roll is an ABSOLUTE function of where the tube is (`CameraRollDeg` plus 180 while
  the mount is `ThroughThePole`), not a delta from a captured reference. A reference is history; the
  field orientation is not, and a flip and a flip back must land on the same two orientations however
  the rig got there.
  - **The camera reads a MECHANICAL pier side, never the mount's report**
    (`IFakeMechanicalPointingStateSource`, beside `IFakeTruePointingSource` and the same idea one
    concept over: what the instrument DOES versus what the mount SAYS). Keying the render to the
    report would have made the fake agree with the lie -- a computed-state mount reports the flip the
    instant the pointing crosses -- so the fake could never have shown a missed flip at all.
  - **`FakeMountDriver` grew a real tube state**, changed only by MOTION: a goto puts it on the
    destination's side, `SetSideOfPierAsync` commands it there, and tracking moves it not at all. The
    gap between that and the hour-angle-computed report is therefore emergent -- the missed flip falls
    out of the model instead of being staged by a test hook. **A sync must NOT touch it** (a sync says
    where the mount points, not where the tube is); an early cut had it re-derive the state from HA
    and plate-solve centring then silently "flipped" the mount every time it solved west.
  - **The guide scope's offset became polar and instrument-framed** (`GuideOffsetArcmin` +
    `GuideOffsetBearingDeg`, replacing the `GuideConeErrorRa/DecArcmin` sky-frame pair). It is bolted
    to the OTA, so it turns over WITH the flip; stated as RA/Dec components it silently stayed put and
    left the guide scope aiming somewhere the rig cannot put it. It rotates with the mount's half turn
    only -- clocking a camera in its focuser does not re-aim the scope it sits in.
  - Pinned by `FakeCameraMountCouplingTests`: a fake-SkyWatcher flip rolls the detected star pattern
    onto `(W - x, H - y)` (and the identity does NOT explain the same pair, so the measurement is
    known to have seen the change), and a computed-state mount tracking past the meridian does NOT
    roll it. Each was seen to fail with its own half of the mechanism removed.
- **P1 -- `WCS.RotationDeg`. DONE.** The position angle of the sensor's +Y axis, north through east:
  `atan2(CD1_2, CD2_2)`, not this sketch's `atan2(CD2_1, CD1_1)`. Both differ by 180 across a flip so
  either would serve the test, but only the +Y form is the conventional image orientation and reads
  **0 for a north-up frame** rather than 180 -- and someone will eventually read the number at face
  value. `RotationDeltaDeg` is the wrapped difference, over a new
  `CoordinateUtils.ConditionDegreesSigned` ((-180, 180], the form a DIFFERENCE wants, beside the
  existing [0, 360) `ConditionDegrees` a POSITION wants). Pinned by `WcsRotationTests`, including that
  a mirrored optical train does not move the answer.
- **P2 -- PA cross-check in the flip path. DONE.** `MeridianFlipVerification.FromSolves` is the pure
  judgement (`Flipped` / `NotFlipped` / `Inconclusive`, beside `MeridianFlipDecision` and
  `MountLimits`); `Session.Imaging.cs` takes the pre-flip reference before anything moves and compares
  it against the recentre's own solve, gated on `PointingStateSource == Computed` exactly as planned.
  Three things worth knowing that the sketch did not anticipate:
  - **The `AlreadyFlipped` path is the likelier way in, not the commanded one.** On a computed-state
    mount the reported pier side turns over as the POINTING crosses, so `pierSideChanged` fires, the
    loop concludes the firmware auto-flipped and **skips the slew entirely**. The session now finds the
    field unmoved and COMMANDS the flip rather than failing the observation -- a better answer than the
    sketch's, since nothing was ever attempted.
  - **An unreadable witness must never overrule a readable one.** `Inconclusive` covers a missing
    solve, a centre-only solve (which is what `FakePlateSolver` and any solver reporting coordinates
    alone returns) and a rotation no pier flip can produce; all three fall back to the mount's report.
    Without that, every rig on such a solver would fail every flip.
  - **The pre/post pair must come from ONE camera.** `LastFieldOrientationSolve(otaName)` filters on
    the OTA and excludes `GuiderFocus`: sensors sit at different rolls in their focusers, so a pair
    drawn from two of them differs by that constant and says nothing about the pier.
  Pinned by `MeridianFlipVerificationTests` (the classifier) and `MeridianFlipVerificationSessionTests`
  (through the session; seen to fail with the override removed, leaving the tube through the pole).
- **P3 -- Feed the truth to the guider, and fix `WithMeridianFlip`. DONE.**
  - **The sense fix is in.** `WithMeridianFlip(bool decIsSkyRelative = false)` now rotates the measured
    axis ANGLES rather than negating rates: `CameraAngleRad + PI` always, `DecAngleRad + PI` only for a
    sky-relative-Dec mount. Rotating the angle rather than negating the rate matters because
    `TransformToMountAxes` decomposes the pixel error against those angles -- negating a rate flips the
    pulse sign but leaves the basis claiming the axis still points the old way, which is equivalent
    only if the axes are exactly orthogonal, the very assumption that record refuses to make.
  - **The old test's premise WAS the bug.** `GivenCalibratedGuiderWhenMeridianFlipThenCorrectionsStillConverge`
    defined its post-flip rig as "RA unchanged, Dec inverted" and then verified the code that assumes
    exactly that -- circular. Corrected to the real physics (RA inverted, Dec unchanged for an
    axis-based mount), it fails against the old implementation by running the error from 6.40 px to
    **69.46 px** on both sensor conventions. It now also covers the sky-relative-Dec convention, which
    the parameterless API could not express. Its old comment claiming an end-to-end fake session
    "could not catch a flip sign error here" is obsolete: P0 made the fake roll, and
    `FakeCameraMountCouplingTests` pins that rotation on rendered pixels.
  - **`reverseDecAfterFlip` keeps its meaning and its `true` default.** It gates whether a detected
    flip re-orients the calibration at all -- which is what it always meant; only the answer to "what
    does re-orienting DO" changed, and that was never the switch's job. The internal property is
    renamed `ReorientCalibrationOnFlip` and the UI label to "Reorient on Flip", because a name
    asserting the wrong physics is exactly what let this bug live. The URI key keeps its PHD2 spelling
    so existing profiles keep working.
  - **The Dec convention is a fact about the MOUNT, not a preference**, so it is deliberately not a
    user setting: `DecIsSkyRelative` is a private constant-valued property (false -- every mount family
    here is axis-based), kept as a named seam rather than a literal for the day a compensating driver
    turns up. At that point it wants to come from `IMountDriver` the way `PointingStateSource` does,
    not from a switch a user has to guess at.
  - **`Session.GetSideOfPierAsync` is the canonical pier side.** Everything that needs to know where
    the tube is asks it rather than the mount: a `Measured` driver is believed verbatim, a `Computed`
    one only until the session knows better. The latch lives on the slewing-to-idle edge in
    `PollDeviceStatesAsync` -- a goto is the only thing in ordinary operation that carries a tube
    across the pier, so the landing is the one moment the report is certainly right, and a verified
    flip is the only other thing that moves it. The built-in guider reads it through a
    `PointingStateOracle` delegate the session sets at init: a delegate rather than a session
    reference, because the dependency runs session -> guider and must never run back. Unset, the
    guider asks the mount exactly as before, which is right for one driven on its own.

P0-P2 are the safety win (detect and fail-loud on a missed commanded flip; accept a real auto-flip)
and are SHIPPED. P3 is the guiding-quality correction and can follow.

### The one gap P0-P2 left, and why it is not in the flip logic

**`CatalogPlateSolver` cannot lock onto a `FakeCameraDriver` synthetic field**, so the session-level
tests stub the pixels-to-WCS step with a solver that reports the roll the camera rendered at. Measured
on a one-degree field: 43 detected stars against 160 catalog anchors, and every solve refused by the
acceptance gate as indistinguishable from noise. That is the fake's star-density model disagreeing
with the solver's expectations -- the render's magnitude cutoff is SNR-derived per
`SyntheticStarFieldRenderer.DetectabilityMagCutoff` while the solver draws its anchor pool from the
catalog independently -- and it is worth closing on its own account, since it currently makes the
whole plate-solve half of the session untestable against fakes. The pixels-to-angle half is covered
instead by `WcsRotationTests` (the CD matrix maths) and `VelaMosaicFieldTests` (the solver, on real
fields).

Related: `SessionTestHelper.CreateSessionAsync` gained `coupleCameraToMount`, which connects the mount
through the `IDeviceHub` so `FakeCameraDriver` can find it. That is the PRODUCTION shape -- every real
host connects through the hub -- but turning it on for the whole suite switches on the guide camera's
drift and the main camera's hidden polar misalignment at the same time, which wedged the SkyWatcher
meridian-limit test. It is opt-in for now; making it the default is its own piece of work.

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

**P0-P3 DONE (2026-08-30), PR #212.** The session no longer takes a computed-state mount's word for a
flip: it reads the field rotation off the recentre's own plate solve and, when the frame says nothing
turned, commands the flip instead of imaging on from the wrong side. `WithMeridianFlip` is corrected on
both axes and its old test's circular premise with it, and `Session.GetSideOfPierAsync` is now the one
canonical pier side that the imaging loop and the guider both read.

What is left is not in the flip logic: the fake's star density (above), and the rotator gate once
`DeviceType.Rotator` exists.

Design captured 2026-08-30 from a session discussion. No meridian-flip plan existed before this; the
flip behaviour is otherwise pinned by `SessionObservationLoopTests` (the GEM-only `[Theory]` and the
across-meridian flip case) and touched by `mount-safety-limits.md` as the limit's neighbour. Related but distinct:
`docs/todo/drivers.md` "post-meridian-flip re-rotate" (driving a physical rotator to preserve framing,
not verifying the flip) and `docs/todo/sequencing.md` "audit that every exit path leaves a sane flip
state".
