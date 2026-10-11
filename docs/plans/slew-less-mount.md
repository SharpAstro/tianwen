# Slew-less mount: a session on a mount that cannot goto

**Status: P1 BUILT (2026-10-11), P2-P4 NOT STARTED.** The session assumed a goto mount at every step, so it could not
run on the iOptron SkyGuider Pro at all. This plan is the design for the MoveAxis fallback (#1495) and the Milky Way
panorama mode (#1500) on top of it. The mosaic placement it needs shipped first (#1501, PR #1502). P1, arrival and
centring, is `Session.PointWithoutGotoAsync` (`Session.SlewLess.cs`).

## Why: the session cannot run on the SkyGuider Pro today

`SgpMountDriverBase` says what the mount can do: `CanSlew` false, `CanSync` true, `CanMoveAxis(Primary)` only, seven
`AxisRates` from 1 to 144 times sidereal, and a `BeginSlewRaDecAsync` that throws "SGP does not support goto slewing".
Nothing under `src/TianWen.Lib/Sequencing` asked any of those questions (the one `CanMoveAxis` check was polar
alignment's). Read at main on 2026-10-11, a run failed in two places, both closed by P1:

- **Rough focus fails the run.** `InitialRoughFocusAsync` begins with `BeginSlewToZenithAsync`, which throws "Device
  does not support slewing" when `CanSlewAsync` is false. Nothing catches it short of `RunAsync`, and the run ends
  `Failed`.
- **Past that, the observation loop never gives up.** A slew that throws is caught at `Session.Imaging.cs` (the
  `catch` after the slew block), logged as "retrying", and the loop `continue`s on the SAME observation, with no
  count and no backoff (`NonIdempotentAction` makes one attempt per pass). Only the session's end or a cancel stops
  it. This is a bug on any mount whose slew throws, and P1 bounds it for all of them.

## What the owner decided (2026-10-11)

- "SGP is German equatorial, just because it doesn't have an axis motor doesn't mean it's not that alignment", and
  "That is what we have the can move Xis for": `AlignmentMode.GermanPolar` stays, and which axes have a motor is
  `CanMoveAxis`, axis by axis.
- "I envision plate solving plus showing the distance in the atlas when moving", and "Well we still need to slew RA
  first": the session moves RA with the motor, and only Dec is the operator's.
- "flip should still wait on manual recentering, but it should still move RA automatically otherwise the camera might
  crash into the tripod legs. We then keep in state of waiting for DEC recentering."
- "One observation mode I was planning on using is milkyway panorama, this would always rewind RA and keep shooting at
  same DEC angle", "this would need a new sequencing mode I presume", and "order the panels so that we avoid the
  meridian (this should ideally already be the default behaviour)" (now #1501).

## The pointing routine: RA by motor, Dec by the operator, a solve between

Tracked by #1495. On a goto mount nothing changes. On a mount where `CanSlewAsync` is false and `CanSync` is true,
every place the session would slew to a target goes through ONE routine instead, and centring is the same routine,
since arriving and centring are both "solve, compare, correct" with a different correction.

1. **Solve and sync.** `PlateSolveAndSyncCoreAsync` as today: a short exposure, a solve, a sync, so the mount's
   modelled position is the sky's.
2. **Check the target is above the minimum height**, as `BeginSlewToTargetAsync` does today, with the same
   `TargetBelowHorizonLimit` answer, so the rising-target wait and the spares work unchanged.
3. **Move RA** when the solved RA is off by more than the tolerance (below).
4. **Wait for Dec** when the solved Dec is off by more than the tolerance (below).
5. **Solve and compare again.** Within the threshold on both axes, the target is reached: sync, latch the pointing
   state (as a goto's landing does), and continue as a goto mount would.

### The RA move

- **The rate is one of the mount's own `AxisRates`**: the fastest whose move lasts at least a few seconds, so the
  timing error is a small share of the move. A half turn at the top rate, 144 times sidereal (about 0.6 deg/s), takes
  about five minutes.
- **The move is timed, never trusted.** The SkyGuider Pro has no encoder: `MoveAxisAsync(Primary, rate)`, an
  `ITimeProvider.SleepAsync` for the computed duration, `MoveAxisAsync(Primary, 0)`, then a solve. The residual is
  corrected by a second, slower move, up to a bound.
- **The sign is measured, never assumed.** Whether a positive rate moves the pointing east or west is read off the
  first move's solve; a move that made the residual grow reverses. (The planetary recentre's mount jog has the same
  unknown and leaves it uncalibrated, which an unattended arrival cannot.)
- **Tracking is not stopped.** Whether the mount's move rate adds to tracking or replaces it is absorbed by the
  closed loop; the plan does not depend on it.

### The Dec wait

The owner's "state of waiting for DEC recentering". The session asks once and then measures:

- **One prompt** when the wait begins, with fixed text, since a prompt cannot change while open (the mirror keys it on
  title and message): "Turn the Dec axis to -05 23' for M42. The session carries on by itself once a plate solve
  finds the target", with Continue = "Solve now" and Cancel = "Skip this target". Answering asserts nothing, since
  the solve decides, so it defaults to "Solve now" when nobody can answer (`defaultIfUnanswerable: true`), never to a
  skip. It is raised through `Session.RaisePrompt`, the one place a prompt is born, which the gates share.
- **The live distance is the activity line in P1** ("Waiting for Dec to be moved 5.4 deg south to M42"), which already
  reaches the GUI and a node's clients as `CurrentActivity`, so P1 changes no wire type. A structured
  `PointingRequest` on `ISessionTelemetry` (the target, the last solved centre, the remaining Dec and RA) comes with
  the atlas (P4, #623), which is what needs more than a sentence.
- **A solve every few seconds** while waiting, the same short exposure as centring. Once Dec is within tolerance the
  wait ends by itself, whether or not anyone answered the prompt.
- **Unattended, it waits out the observation's slot**, then advances to the next observation (see the open
  questions). It never assumes the operator moved.

### Where a solve looks, and the setup solve it needs

A session solve searches 10 degrees around what the mount BELIEVES (`PlateSolveAndSyncCoreAsync`), and on this mount
nothing moves that belief but a sync: a timed move and a hand-turned Dec both leave it behind. So the routine keeps it
near the truth: after a move it syncs the mount to where the move should have gone (`BelieveAsync`), and during the
Dec wait a failed solve looks at the target's Dec next, where the operator is heading. Each solve then syncs the truth.
**The run needs one solve and sync at setup** (the GUI's solve and sync, `MountActions.SolveAndSyncAsync`): the
SkyGuider Pro believes it points at the pole until told otherwise, and nothing in the routine can stand in for that
first solve short of a blind one.

### How close is arrived

Centring asks for 1 arcminute, which nobody turns a Dec knob to by hand. The routine arrives within the larger of the
requested threshold and a twentieth of the field's short side (`SlewLessThresholdArcmin`): 51' for a 50 mm lens on
APS-C, 10' at 250 mm. A wide field frames its target as well at a twentieth of itself.

### What the session does with each slew it makes

| Step | On a goto mount | On a slew-less mount |
|---|---|---|
| Arrival at a target (`ObservationLoopAsync`) | goto, then centring | the routine |
| Centring (`CenterOnTargetAsync`'s re-slew) | goto again | the routine's correction: RA move, Dec wait |
| Rough focus (`InitialRoughFocusAsync`, zenith) | slews near the zenith | focuses where the rig points: the operator aims it during setup; no zenith sky gauge, which assumes the zenith |
| Guider calibration (`CalibrateGuiderAsync`, HA -0.5 h) | slews east of the meridian | calibrates on the first target after its arrival; the mount guides RA only |
| Obstruction nudge (a Dec offset) | slews and scouts | skipped: it would need the operator |
| Sky flats near the zenith | slews | not offered; the calibrator path stays |
| Meridian flip | goto to the other side | RA by motor at once, then the Dec wait (below). Until P2, a flip due ends the target rather than repeating it past the meridian |
| A slew that throws | retried without bound (a bug) | bounded on every mount: after a few failures the observation advances |

### The meridian flip

When `MeridianFlipDecision` calls for a flip, the RA half cannot wait (the camera meets the tripod legs), so the
session turns RA by the half turn at once, then enters the Dec wait, now for the flipped Dec. The solve that finds the
target again decides whether the flip happened, from the field's rotation (`MeridianFlipVerification`), which needs
the driver's pointing state to be `Computed`, not today's `None` (#1483's tenth item). With #1501 a mosaic is placed
so it needs no flip; a single target that crosses still does.

## The fake that knows where it points

`FakeSgpMountDriver` cannot test any of this today: its RA changes only on a sync, `MoveAxisAsync` moves nothing, and
the fake camera renders the stamped target because the driver is not an `IFakeTruePointingSource` (only the
Skywatcher fake is). The fake becomes one:

- **A true pointing**, separate from what the driver believes: RA moved by `MoveAxisAsync` at its rate times a small
  deliberate error, so the closed loop has something to correct, and held while tracking.
- **Dec moved only by the test's operator**: a `PromptRequested` subscriber that sets the true Dec, in two steps when
  a test wants a near miss first.
- **A session test with `mountPort: "SGP"`** (`FakeDevice.CreateMountDriver` already wires it): a run arrives at two
  targets, the second at the same Dec with no prompt at all, and a flip that turns RA before it asks.

## The panorama mode

Tracked by #1500. A strip of panels at one Dec, RA turned back between them, imaged unattended once the Dec is set.

- **Its plan**: a strip is `MosaicGenerator`'s one-row case; an overload takes the strip's RA extent at a Dec.
- **Its order and placement**: west first and wholly on one side of the meridian (#1501). On a slew-less mount that
  placement is REQUIRED, not preferred: #1501's fallbacks (each panel on one side, then flagged) both need a flip, and
  a flip here needs the operator. So the scheduler takes a strict rule for such a mount and declines a strip it cannot
  place, saying why.
- **Its run**: each panel's arrival is the pointing routine, and a panel at the strip's Dec needs no Dec wait, so the
  routine never asks. Nothing else in the observation loop changes; the new part is the planner's way to make a strip.

## The atlas's distance

Tracked by #623 (the atlas half). The sky map already draws the mount's position (`SkyMapState.MountOverlay`) and the
schedule's targets. It adds the `PointingRequest`: a line from the last solved centre to the target, and the remaining
distance per axis, during a Dec wait.

## Driver fixes this leans on

From #1483 (the SkyGuider Pro driver's confirmed defects) and #810 (a lost reply must throw):

- **#810, `IsTrackingAsync` on a lost reply** answers "not tracking", and two such polls end the run as
  `DriverEnforcedStop`. Wanted before any real night, with or without this plan.
- **#1483 item 2, the RA model nothing reads**: between solves the driver's RA is stale after a move. The routine syncs
  after every solve, so the routine does not depend on it; the meridian limit between solves does.
- **#1483 item 5**, a move on a closed port still sets the moving flag, and **the tenth item**, the pointing state
  `Computed`, which P2's flip verification needs.

## Phases

| Phase | What | Issue | State |
|---|---|---|---|
| P1 | The pointing routine for arrival and centring; each auxiliary slew answered as in the table; the slew retry bounded on every mount; the fake's true pointing; session tests on `mountPort: "SGP"` (`SlewLessSessionTests`: M42 reached an hour of RA and five degrees of Dec away with one question and two RA moves at a 3 % move error; a target at the rig's Dec with none) | #1495 | built 2026-10-11 |
| P2 | The flip: RA at once, the Dec wait, verification by the image (needs #1483's tenth item) | #1495 | not started |
| P3 | The panorama strip: the generator overload, the planner's entry, the scheduler's strict rule | #1500 | not started |
| P4 | The atlas draws the `PointingRequest` | #623 | not started |

## Open questions for the owner

1. **Unattended Dec wait.** Recommended: wait out the observation's slot, then advance. The alternative is to wait
   for the operator for ever.
2. **Rough focus with no slew.** Recommended: focus wherever the rig points at the start, since the operator aims it
   during setup.
3. **The Dec wait as a `SessionPhase` or as an activity.** The owner's words read as a state ("keep in state of
   waiting"), and a phase shows in the phase timeline. But the run's phases are coarse stages in a fixed order, and a
   flip would leave `Observing` and come back. Recommended: a phase, `WaitingForOperator`, once every reader of the
   phase is checked for an assumption that `Observing` is entered once.
