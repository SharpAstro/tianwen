# PLAN: Mount safety limits (hour-angle + horizon)

> Status: **NOT STARTED**. Design captured 2026-08-29 after the `Finalise` tracking fix
> (PR #205) surfaced that **there are no hour-angle or altitude safety limits anywhere in the
> codebase** -- `grep` finds no `HALimit`, `LimitReached` or `SafetyLimit` -- so a failed meridian
> flip has no backstop that stops a mount tracking into the pier.

## The gap

`MeridianFlipDecision` decides *whether to flip*. It classifies hour angle into
`EastOfMeridian` / `InObstructionZone` / `InFlipWindow` / `PastFlipWindow` and, past the window,
either commands a flip or continues imaging. **Nothing anywhere asks "should this mount still be
moving at all?"** The failure it leaves open: a flip that fails (`RepeatCurrentObservation` and then
does not recover), a mount whose reported pier side is wrong, a manual slew from the GUI with no
session running, or simply an unattended rig left tracking -- and in every case the mount keeps
tracking west until something physical stops it.

The adjacent things that exist and are **not** this:

| Exists | What it actually does | Why it is not a limit |
|---|---|---|
| `SessionConfiguration.MinHeightAboveHorizon` | Planner/scheduler constraint | Decides what to *schedule*; never stops a moving mount |
| `MeridianFlipObstructionZoneMinutesBefore` | Pauses *exposure starts* near the meridian | Pauses the camera, not the mount; tracking continues |
| `MeridianFlipDecision.PastFlipWindow` | Routes to `CommandFlip` | An instruction to flip, not a refusal to continue |
| `Finalise` park + stop-tracking | End-of-run shutdown | Only at the end of a run that reached its end |
| `ISafetyMonitor` (TODO.md) | Weather/roof device polling | A different input; would *feed* this, not replace it |

## Reference: what GSServer does

Green Swamp Server (`../../../sebgod/GSServer`, compare against `origin/master` --
`rmorgan001/GSServer`) is the closest well-worn implementation, and it is a *driver*, which is the
key difference (see "The layering question" below). Read
`GS.Server/SkyTelescope/SkyServer.cs::CheckAxisLimits`.

**Its whole vocabulary is 12 settings**, and the shape is worth copying almost wholesale:

```
LimitsOn                                   master switch; off => alarm cleared, return

HourAngleLimit                             degrees past meridian  -> WARNING only
AxisTrackingLimit                          extra degrees; Hour+Axis = totLimit -> ACTION
LimitTracking / LimitPark / ParkLimitName  what the meridian action does

AxisHzTrackingLimit                        altitude floor -> ACTION
HzLimitTracking / HzLimitPark / ParkHzLimitName   what the horizon action does

AxisLimitX / AxisLowerLimitY / AxisUpperLimitY    raw axis travel (AltAz + fork)
```

Six design decisions in there that are worth taking:

1. **Two thresholds, not one.** `HourAngleLimit` lights a warning; `HourAngleLimit +
   AxisTrackingLimit` takes action. The user gets told before anything is taken away from them.
2. **Two independent actions, both opt-in**: stop tracking, and/or park. Neither implies the other.
3. **Park goes to a NAMED park position, and degrades to `StopAxes()` when that position is
   missing.** "Park somewhere safe" falling back to "just stop" is the right degradation; a limit
   that does nothing because a park position was renamed is worse than one that halts.
4. **Alignment-mode aware, and not symmetrically.** GEM checks the primary axis against an hour-angle
   limit; AltAz drives its meridian action off the **Y** (Alt) axis while fork/polar drives it off
   **X**. A single "HA limit" is a GEM concept and does not transfer.
5. **The horizon test is gated on `Tracking`, and on a GEM ALSO on `SideOfPier == pierEast`** -- only
   the descending side can drive the tube into the ground, so the other side would be a false alarm.
6. **The whole horizon block is skipped when both its actions are off** ("Skip all if set to do
   nothing"), so an unconfigured limit costs nothing per poll.

Plus one implementation guard: **`SlewState != SlewType.SlewPark`, commented "only hit this once
while in limit"** -- without it the limit re-issues the park on every poll while the mount is still
inside the limit and travelling to the park position.

**Two cautions about the source.** The local `sebgod` fork's checked-out branch is **14 ahead / 208
behind** `origin/master`, and `SkyServer.cs` + `SkySettings.cs` differ by +4599/-1534 lines between
them -- the older tree has **no horizon limit at all**, so reading the working copy gives a
materially wrong picture of GSS's model. Read `origin/master`. And GSS is **GPL-3.0**: TianWen is
AGPL-3.0-or-later so vendoring would be lawful, but this plan takes the *shape* and writes its own
code, as with every other GSS-derived piece here.

## The layering question, which decides everything else

**GSS is the driver and owns the axes. TianWen is a client of many drivers, and several of them --
GSS itself, ASCOM drivers, OnStep -- already enforce their own limits.** That produces three
constraints GSS never had:

- **TianWen must not fight a driver that already stops.** If GSS parks at its own limit, TianWen
  must observe that and not issue a second, different park. Detection is enough: the mount stopped
  tracking or reports `AtPark`, and the session should conclude rather than "recover".
- **Axis angles are the RIGHT quantity, and we can have them on some mounts but not all.** GSS
  evaluates `_appAxes.X`, the mount's own primary-axis angle, not an hour angle -- correctly, because
  **it is the axis that collides with the pier**. Hour angle is a sky quantity that equals the axis
  angle only on a correctly-synced mount, which is exactly what a rig is NOT when something has
  already gone wrong: a bad sync, a mis-reported pier side or a failed flip all break the equality
  precisely when the limit matters most.
  **The seam for this already exists and is half-built.** `IMountDriver.GetAxisPositionAsync(axis)`
  returns `long?` encoder steps (added for worm-gear PE phase) and already uses `null` to mean "this
  driver cannot say", which is the exact shape a limit needs. The native SkyWatcher driver decodes
  `_posRa` / `_posDec` against `_cprRa` / `_cprDec` and applies the southern-hemisphere mirroring in
  `StepsToRa` / `StepsToDec`, so it can produce a true axis angle today. ASCOM/Alpaca cannot in
  general, and the SkyGuider Pro cannot at all.
  So the limit is **two-tier by construction**: an axis-angle limit where the driver models the axis
  (a real mechanical limit, GSS parity), and an HA/altitude limit where it does not (a weaker
  sky-coordinate approximation). See P1b. The tiers must be *labelled* in the UI and the log -- a
  user who believes they have a mechanical limit and actually has a sky-coordinate one will set the
  threshold too tight and get spurious stops, or too loose and get none.
- **A limit that only runs during a session is half a safety net.** The GUI can slew, jog and track
  with no session at all (`MountActions.SlewToJ2000Async`, the sky-map goto, manual axis moves).

## Phasing

| Phase | What | Status |
|-------|------|--------|
| P0 | **`MountLimits` pure decider** (`TianWen.Lib/Sequencing/` beside `MeridianFlipDecision`): `Evaluate(hourAngleHours, altitudeDeg, isTracking, alreadyActed, MountLimitConfiguration) -> MountLimitVerdict`. No I/O, no driver, no clock. `MountLimitConfiguration` landed with it rather than waiting for P1, because the decider cannot be written without it; P1 keeps the PLACEMENT (profile persistence + UI). **Two departures from the sketch:** it takes no pier side and no alignment mode -- see the note below. | **DONE** (30 tests, 3 sabotages verified) |
| P1b | **Axis modelling: `GetAxisAngleAsync(TelescopeAxis) -> double?`** (degrees from the mount's home position, signed, hemisphere-corrected), implemented natively by the SkyWatcher driver from steps + CPR and returning `null` everywhere else. **Angle, not steps**: the driver owns its home convention (`0x800000`) and the southern mirroring, and leaking steps + CPR would make every caller re-derive both -- the bug `StepsToRa` already exists to prevent. This is what upgrades the limit from approximation to mechanical truth on the mounts that can support it, and it is independently useful (a true pier-side derivation, PE phase, a mechanical-position readout). | NOT STARTED |
| P1 | **Profile placement + UI.** Persist `MountLimitConfiguration` (the record itself shipped in P0) and give it an editor. **Lives on the PROFILE, not `SessionConfiguration`** -- it is a static fact about the mount's geometry AND the tube bolted to it, exactly like `OTAData`, and it must apply to a manual slew with no session. | **DONE** for persistence + plumbing; the editor UI is not built |
| P2 | **Session enforcement.** Evaluate in `PollDeviceStatesAsync` (already the one place that refreshes `_mountState` with HA and pier side, and already called from every slew wait and the imaging tick). Verdict routes to a new `ImageLoopNextAction.LimitReached`, finalising the run the same way `DeviceUnrecoverable` does. Reuses `ResilientInvokeAsync` for the stop/park. | **DONE** (6 tests, 2 sabotages verified) |
| P3 | **Enforcement outside a session** -- the half GSS gets for free. A `MountLimitWatcher` hosted alongside the device hub, polling any *connected* mount on a slow cadence (5 s) whether or not a session owns it. Must respect the hub lease: a run owns the mount, so the watcher only observes and lets the session act; with no run it acts itself. | NOT STARTED |
| P4 | **Surface it.** A limit is useless if it fires silently: notification feed entry, a `LimitAlarm`-equivalent state on `ISessionTelemetry` for the Home board's rig card, and the warning threshold shown as a countdown next to the existing flip countdown (`MeridianFlipUtc`). | NOT STARTED |
| P5 | **Driver-enforced limits, observed not duplicated.** Detect "the mount stopped itself" (tracking off / `AtPark` when we did not command it) and report it as a limit event rather than a device fault, so a GSS-managed rig does not read as a malfunction. | NOT STARTED |

### P0 as built: two departures from the sketch above

**No pier side, and no alignment mode.** The sketch passed both, following GSServer, whose GEM
horizon test reads `SideOfPier == pierEast`. The intent there is right -- act only when the pointing
is getting WORSE -- but the signal is wrong for us twice over:

- Altitude is maximal at upper transit and falls monotonically until lower transit, so **`HA > 0` IS
  "descending"**, exactly, in both hemispheres, with no dependence on any driver convention. It needs
  no alignment mode either, and it is true for fork and AltAz mounts, which have no pier side at all
  for GSS's version of the test to read.
- **Our SkyWatcher driver derives pier side from the Dec encoder** and reports `Normal` while a GEM
  tracks west (the meridian-flip oscillation invariant in `CLAUDE.md`). Gating on it would disable the
  horizon limit on exactly the mount that most needs it, silently.

**Warn and action are a threshold plus a non-negative EXTRA, not two absolute thresholds.** The two
limits run in opposite directions -- hour angle rises toward its limit, altitude falls toward its own --
so a pair of absolute numbers can be edited into an order that acts before it warns, differently for
each limit. `MeridianActionDeg = Warn + max(0, Extra)` and `HorizonWarnDeg = Action + max(0, Extra)`
make warn-before-action hold by construction in both directions. This is GSServer's own shape for the
meridian (`HourAngleLimit + AxisTrackingLimit`), generalised to the horizon, which in GSS has no
warning stage at all.

**A verdict names the limit DRIVING the response, not the only one breached.** Ranking is on what the
verdict would DO: any action outranks any warning, and among actions the stronger response wins, since
`Park` is a superset of `StopTracking`. `Meridian` breaks an exact tie. The test for this had to put
the two responses far apart to bite -- written with the natural `StopTracking`-vs-`Park` pair it passes
against a rank that ignores the action/warning distinction entirely, because both sides tie and the
meridian wins on the tie-break. Right answer, wrong reason, and the broken rank would have shipped.

## Invariants (set now, before code exists)

- **The decider is pure and the two thresholds are separate.** Warn and act are different numbers,
  and the act threshold is never below the warn threshold; clamp rather than trust config.
- **A limit NEVER overrides an explicit human action in the direction of safety.** Stopping and
  parking are always allowed; a limit must not, for instance, refuse a slew that would move the
  mount *away* from the limit. GSS gets this for free by acting only on position; a TianWen
  implementation that gates commands must check the direction of travel or it will trap the rig.
- **The action fires ONCE per entry into the limit**, not per poll -- GSS's `SlewState !=
  SlewType.SlewPark` guard. The TianWen analogue is a latch cleared only when the verdict returns to
  `None`, which is also what makes "log it in the notification feed" tolerable.
- **Horizon is gated on tracking and, on a GEM, on the descending pier side.** An east-pointing
  scope at low altitude is rising and is not a hazard; treating it as one makes the limit fire every
  night at the start of every low target and the user turns it off.
- **`MinHeightAboveHorizon` is NOT this and must not be reused for it.** One is "do not schedule
  that", the other is "stop the motor". Sharing the number would make raising a scheduling
  preference silently arm a safety stop.
- **Axis angle wins when available; HA is the fallback, never a cross-check.** If both are
  available and they DISAGREE, that is a sync fault worth surfacing -- but the limit must act on the
  axis, because that is the thing with a pier in its way. Do not average them, and do not require
  both to agree before acting: an unsynced mount is the case the limit exists for.
- **A limit that cannot be evaluated does not fire.** `double.NaN` hour angle (transform
  unavailable, driver read failed) means unknown; unknown must never mean "in limit", or a flaky
  driver read parks the mount mid-target. The opposite failure -- a rig with no HA at all -- is
  covered by the fact that the limit is opt-in per profile.
- **Never gate on `LiveSessionState.IsRunning`.** The same rule the device-ownership work already
  records: a flat run has `IsRunning == false`, and the hosted API and Alpaca plane never see a UI
  flag at all.

## Open questions (decide at the phase, not now)

- **Does P3 belong in the hub or in the hosted server?** A watcher that acts on hardware nobody
  leased is a new kind of actor in a codebase whose whole ownership model is "a run claims the rig".
  The safest reading is that the watcher acts only when *nothing* holds a lease, which is precisely
  when the hub knows the rig is idle -- but that wants writing down against `DeviceOwnershipGate`
  before it is built.
- **Do we get park positions at all?** GSS's named-park model presumes a list of park positions;
  TianWen has `ParkAsync` and nothing else. The fallback (`StopAxes` equivalent = stop tracking,
  abort slew) may be all v1 can offer, which is fine and should be stated rather than discovered.
- **AltAz mounts.** `docs/plans/altaz-mount-support.md` exists; GSS's AltAz limits are axis-travel
  limits, which TianWen cannot see. Probably out of scope for v1 -- say so explicitly.

## Related

- [`MeridianFlipDecision`](../../src/TianWen.Lib/Sequencing/MeridianFlipDecision.cs) -- the zone
  classifier this extends rather than replaces, and the model for a pure decider plus a
  both-hemispheres `[Theory]`.
- `TODO.md` -- the SafetyMonitor entry (ASCOM `ISafetyMonitor` polling), which is a *different
  input* to the same kind of action and should share P2's routing.
- [`driver-resilience.md`](../architecture/driver-resilience.md) -- a limit stop must go through
  `ResilientInvokeAsync`, and must not be mistaken for a device fault (P5).
- [`gss-parity-audit.md`](gss-parity-audit.md) -- the rest of the GSServer sweep: which of its
  pulse/slew/queue fixes apply to us and which are structurally impossible here. Read its
  "Read `origin/master`, not a local checkout" note before quoting any GSS behaviour below.

## P1 + P2 as built

**The profile is the source of truth; `Setup` is the run's projection of it.**
`ProfileData.MountLimits` is nullable so an older profile deserialises unchanged and reads as
"never configured", which the shipped defaults answer as disabled. `SessionFactory` copies it onto
`Setup` -- the record that already answers "what hardware am I driving" -- rather than onto the
per-run `SessionConfiguration`, which keeps the invariant intact while giving the session something
to read. Note this changes the base64 `data=` segment of every profile URI, which is expected and
is what `ProfileTests` pins.

**Altitude is GEOMETRIC, and that is a decision rather than an approximation.**
`SiteContext.AltitudeDegrees(hourAngleHours, decDeg)` is new, sitting beside the `IsAboveHorizon`
that already cached `sinLat`/`cosLat` (the class's own remarks had flagged altitude as a missing
use). Refraction lifts a body by up to ~34 arcmin at the horizon, so a refracted altitude reports
the tube HIGHER than it is and a limit keyed on it fires late -- in the one regime where late is
the whole failure. A tripod leg is not lifted by the atmosphere. It takes an hour angle rather than
an RA because the caller reads HA straight off the mount, and going RA -> HA via `LST` would
re-introduce a clock the mount has already accounted for.

**Enforcement is on the POLL, not the imaging tick.** `PollDeviceStatesAsync` is what every slew
wait and focus routine already calls; a limit evaluated only between exposures would watch a mount
drive into a pier during a goto and say nothing.

**Acting does not gate the exit.** Whether the stop succeeded or not, the imaging loop is told to
finish via the new `ImageLoopNextAction.LimitReached` -- a limit we could not act on is a stronger
reason to end the night, not a weaker one. `LimitReached` is deliberately distinct from
`DeviceUnrecoverable`: nothing is broken, the rig reached the edge of where it may point, and
collapsing the two would report a working mount as a faulty one and send somebody out to check
cables at 3am.

**Tracking is stopped in BOTH responses, park included.** A park is motion across a path nothing has
checked, so the axis should not still be driving toward the limit while it is under way -- and if
the park then fails, a stopped mount is a better place to have left the rig than a tracking one. A
`Park` response on a mount that cannot park logs and settles for the stop.

**One test lesson.** `SessionMountLimitTests` places the mount by SYNC, not by slew: a slew only
BEGINS, and the fake advances with the fake clock which these tests never pump, so a slewed mount
stays exactly where it was and every assertion passes for the wrong reason. Tracking is also
switched on explicitly, because a freshly built test session has never initialised a mount and
starts with tracking OFF -- asserting "still tracking" without that passes with enforcement deleted.

## Correcting the physics, and making the limit the clamp

**It is the TUBE that collides, not the counterweight.** Earlier notes here and in the code said the
meridian limit is about "the counterweight shaft meeting the pier". That is backwards. Tracking past
the meridian on a GEM swings the counterweight UP, above the OTA, and the tube DOWN toward the pier
and tripod. Three consequences, none of which the first cut modelled:

- **The margin is set by the OPTICS, not the ballast.** A long refractor or Newtonian -- plus dew
  shield, focuser, camera train, whatever hangs off the back -- runs out of room far sooner than a
  short lens on the same mount.
- **It varies with DECLINATION.** A tube near the pole lies close to the RA axis and barely sweeps as
  the mount tracks; one near the equator sweeps the widest arc and reaches the pier soonest.
- **So a single hour-angle threshold is a conservative APPROXIMATION of a three-variable envelope**
  (hour angle x declination x tube geometry), not the true bound. It must be set for the worst case
  the rig actually images: the lowest declination it visits, with the longest tube fitted. Stating
  this is the honest version; pretending the threshold is exact is how somebody sets it from a
  high-Dec test and finds the pier at Dec 0.

**The meridian limit is now in MINUTES, matching the flip.** It was degrees, while
`MeridianFlipEarliestMinutesAfter` / `MeridianFlipLatestMinutesAfter` were minutes -- two settings
bounding the same axis in two units, whose defaults (5 and 10 in each) looked identical and were
not: 5 deg is 20 min. The horizon limit stays in degrees, because altitude genuinely is an angle and
has no time analogue.

**The limit is the ultimate clamp.** `MountLimitConfiguration.ClampFlipLatestMinutes` caps the flip
deadline at the action threshold less `FlipClearanceMinutes` (5), and `MeridianFlipDecision`
applies it internally so no caller can classify against an unclamped window by forgetting to ask.

- **The direction of the dependency is the point.** How long to keep imaging before flipping is a
  PREFERENCE; where the tube meets the pier is a FACT. The fact caps the preference.
- **The tempting inverse is wrong.** Deriving the limit as "flip deadline plus a margin" -- the same
  threshold-plus-EXTRA trick this record already uses for warn/act -- would let a preference move a
  safety bound: raise the flip deadline to an hour and the mechanical limit follows it into the pier.
- **Without the clamp the two simply race, and the limit wins**, which is the worst outcome: the
  mount is stopped at the very moment it was about to do the right thing, ending the night instead
  of flipping. This matters most for exactly the rigs that need it -- a GEM that cannot flip before
  the meridian must track past it, and the user raises the flip deadline to do so.

Pinned by `MountLimitClampsFlipTests`, including that the limit does NOT move when the flip
preference does, and one sabotage (clamp removed).
