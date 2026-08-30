# PLAN: Mount safety limits (hour-angle + horizon)

> Status: **ALL PHASES DONE as of 2026-08-30** (P0, P1 incl. the editor UI, P1b for SkyWatcher, P2, P3 for
> both hosts, P4, P5). Open: hardware validation of the SkyWatcher axis-solution change, and the
> follow-ups listed under "What is still open" at the end.
> **Corrected 2026-08-30: the meridian test takes the mount's POINTING STATE** -- read on hour angle
> alone it stopped every rig ~30 min after a successful flip. See "The meridian test needs the
> pointing state" below, which also records a SkyWatcher-driver finding fixed the same day
> (`SkyToSteps`) and an LX200-base one still open.
> Design captured 2026-08-29 after the `Finalise` tracking fix (PR #205) surfaced that **there were
> no hour-angle or altitude safety limits anywhere in the codebase** -- a failed meridian flip had no
> backstop that stopped a mount tracking into the pier. P0 (the pure decider) and P2 (session
> enforcement) shipped the same day; P3's `MountLimitWatcher` (enforcement with no session running)
> shipped for `tianwen-server` afterward -- see its own section below for what it does and does not
> yet cover.

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

Green Swamp Server (`../../other/GSServer` relative to this repo, compare against `origin/master` --
`rmorgan001/GSServer`, `eb7e92c` at the time of writing) is the closest well-worn implementation, and it is a *driver*, which is the
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
| P0 | **`MountLimits` pure decider** (`TianWen.Lib/Sequencing/` beside `MeridianFlipDecision`): `Evaluate(hourAngleHours, pointingState, altitudeDeg, isTracking, alreadyActed, MountLimitConfiguration) -> MountLimitVerdict`. No I/O, no driver, no clock. `MountLimitConfiguration` landed with it rather than waiting for P1, because the decider cannot be written without it; P1 keeps the PLACEMENT (profile persistence + UI). **Two departures from the sketch:** the HORIZON test takes no pier side, and neither test takes an alignment mode -- see the note below. The MERIDIAN test takes the pointing state, as of 2026-08-30. | **DONE** (39 cases, 3 sabotages verified; pointing state added 2026-08-30, seen to fail first) |
| P1b | **Axis modelling: `GetAxisAngleAsync(TelescopeAxis) -> double?`** (degrees from the mount's home position, signed, hemisphere-corrected), implemented natively by the SkyWatcher driver from steps + CPR and returning `null` everywhere else. **Angle, not steps**: the driver owns its home convention (`0x800000`) and the southern mirroring, and leaking steps + CPR would make every caller re-derive both -- the bug `StepsToRa` already exists to prevent. This is what upgrades the limit from approximation to mechanical truth on the mounts that can support it, and it is independently useful (a true pier-side derivation, PE phase, a mechanical-position readout). | **DONE for SkyWatcher** (2026-08-30): `IMountDriver.GetAxisAngleAsync`, null on every other driver, and `MountLimits.Evaluate` prefers it -- see "P1b as built" |
| P1 | **Profile placement + UI.** Persist `MountLimitConfiguration` (the record itself shipped in P0) and give it an editor. **Lives on the PROFILE, not `SessionConfiguration`** -- it is a static fact about the mount's geometry AND the tube bolted to it, exactly like `OTAData`, and it must apply to a manual slew with no session. | **DONE** (editor UI 2026-08-30: `PanelSection.MountLimits` on the profile panel, and the flip settings' first UI as a "Meridian Flip" config group whose deadline carries the limit's clamp as a caveat -- see "P1 editor UI as built") |
| P2 | **Session enforcement.** Evaluate in `PollDeviceStatesAsync` (already the one place that refreshes `_mountState` with HA and pier side, and already called from every slew wait and the imaging tick). Verdict routes to a new `ImageLoopNextAction.LimitReached`, finalising the run the same way `DeviceUnrecoverable` does. Reuses `ResilientInvokeAsync` for the stop/park. | **DONE** (9 tests, 2 sabotages verified) |
| P3 | **Enforcement outside a session** -- the half GSS gets for free. A `MountLimitWatcher` hosted alongside the device hub, polling any *connected* mount on a slow cadence (5 s) whether or not a session owns it. Must respect the hub lease: a run owns the mount, so the watcher only observes and lets the session act; with no run it acts itself. | **DONE for both hosts** (13 tests, 2 sabotages verified): `BackgroundService` in `tianwen-server`, `tracker.Run(watcher.RunAsync)` from `tianwen-gui`'s composition root (2026-08-30) -- see "P3 as built" |
| P4 | **Surface it.** A limit is useless if it fires silently: notification feed entry, a `LimitAlarm`-equivalent state on `ISessionTelemetry` for the Home board's rig card, and the warning threshold shown as a countdown next to the existing flip countdown (`MeridianFlipUtc`). | **DONE** (2026-08-30): `ISessionTelemetry.MountLimitVerdict`, `MountLimitDto` on the wire, `RemoteSessionMirror`, `LiveSessionState`, the Home board (Flip column doubles as the limit countdown; a detail row) and both notification feeds on class transitions -- see \"P4 + P5 as built\" |
| P5 | **Driver-enforced limits, observed not duplicated.** Detect "the mount stopped itself" (tracking off / `AtPark` when we did not command it) and report it as a limit event rather than a device fault, so a GSS-managed rig does not read as a malfunction. | **DONE** (2026-08-30): `MountLimitKind.DriverEnforced`, latched by `Session.DetectDriverEnforcedStop` -- see \"P4 + P5 as built\" |

### P0 as built: two departures from the sketch above

**No pier side for the HORIZON test, and no alignment mode.** The sketch passed both, following
GSServer, whose GEM horizon test reads `SideOfPier == pierEast`. The intent there is right -- act only
when the pointing is getting WORSE -- but altitude is a SKY quantity: it is maximal at upper transit and
falls monotonically until lower transit, so **`HA > 0` IS "descending"**, exactly, in both hemispheres,
with no dependence on any driver convention. It needs no alignment mode either, and it is true for fork
and AltAz mounts, which have no pier side at all for GSS's version of the test to read.

*(The first cut extended "no pier side" to the meridian test as well, arguing that our SkyWatcher
driver's encoder-derived pier side "reports `Normal` while a GEM tracks west". That was the wrong
lesson: for the MERIDIAN test the pointing state is exactly the mechanical fact wanted, and dropping it
stopped every rig shortly after its flip. Corrected 2026-08-30 -- see "The meridian test needs the
pointing state" below.)*

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
- **Horizon is gated on tracking and on DESCENDING, which is `HA > 0`, never a pier side.** An
  east-pointing scope at low altitude is rising and is not a hazard; treating it as one makes the limit
  fire every night at the start of every low target and the user turns it off. (Written as "the
  descending pier side" before P0 was built; see "P0 as built" for why the sky answers this directly.)
- **The meridian test reads the POINTING STATE, and a driver that cannot say gets the weaker tier.**
  `Normal ? -HA : HA` -- the same hour angle is toward the pier before a flip and away from it after.
  `Unknown` keeps the hour-angle reading; a wrong report gives a wrong limit, which no arithmetic in
  the decider can repair, so a driver's pointing state is part of this feature's correctness surface.
- **`MinHeightAboveHorizon` is NOT this and must not be reused for it.** One is "do not schedule
  that", the other is "stop the motor". Sharing the number would make raising a scheduling
  preference silently arm a safety stop.
- **Axis angle wins when available; HA is the fallback, never a cross-check.** If both are
  available and they DISAGREE, that is a sync fault worth surfacing -- but the limit must act on the
  axis, because that is the thing with a pier in its way. Do not average them, and do not require
  both to agree before acting: an unsynced mount is the case the limit exists for. *(Implemented
  2026-08-30: `Evaluate` takes `primaryAxisAngleDeg` and does not consult HA or the pointing state
  for the meridian test when it is present; the disagreement is not yet surfaced anywhere.)*
- **A limit that cannot be evaluated does not fire.** `double.NaN` hour angle (transform
  unavailable, driver read failed) means unknown; unknown must never mean "in limit", or a flaky
  driver read parks the mount mid-target. The opposite failure -- a rig with no HA at all -- is
  covered by the fact that the limit is opt-in per profile.
- **Never gate on `LiveSessionState.IsRunning`.** The same rule the device-ownership work already
  records: a flat run has `IsRunning == false`, and the hosted API and Alpaca plane never see a UI
  flag at all.

## Open questions (decide at the phase, not now)

- ~~**Does P3 belong in the hub or in the hosted server?**~~ **RESOLVED, see "P3 as built" below.**
  Neither, exactly: the watcher's own logic (`MountLimitWatcher`) lives beside `IDeviceHub` in
  `TianWen.Lib` so it is available to every host, but *driving* it (calling `RunAsync`) is a per-host
  decision, because `TianWen.Lib` takes no dependency on `Microsoft.Extensions.Hosting` and "the
  active profile" is tracked differently by the GUI, the server and the CLI. It acts only when
  `IDeviceHub.TryGetLease` says nothing holds the mount, exactly the safest reading this question
  proposed.
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

## P1 editor UI: design notes (from the 2026-08-29 session; built 2026-08-30, see "P1 editor UI as built")

The config persists and enforces, but the only way to set it is hand-editing
`%LOCALAPPDATA%/TianWen/Profiles/<id>.json`. What the editor should be, so the next session does not
start from nothing:

- **Placement.** A `PanelSection.MountLimits` after `PanelSection.Site()` in
  `EquipmentContent.GetProfilePanelSections`, rendered in `EquipmentTab.ProfilePanel.cs`. Follow
  `BuildSite` exactly -- it is the closest analogue: a display row with `[>]`, an edit mode with inputs
  and a Save row. Needs `EquipmentActions.SetMountLimits` (pure `data with { ... }`, so unknown fields
  round-trip), an `EditMountLimitsSignal`, `IsEditingMountLimits` plus `TextInputState`s on
  `EquipmentTabState`, and a subscribe in `AppSignalHandler.Equipment.cs` mirroring `EditSiteSignal`
  (route only: one helper call, reflect into state).
- **The flip settings get their first UI here, and are validated against the limit.** The user asked
  for this. `MeridianFlipEarliestMinutesAfter` / `MeridianFlipLatestMinutesAfter` are edited nowhere
  today, so whatever editor is built owns them: show both and the limit in MINUTES (the unit fix in
  `c04400f1` is what makes them comparable), and flag or refuse a flip deadline that
  `ClampFlipLatestMinutes` would clamp -- the clamp keeps the rig safe silently, the editor should say
  so out loud.
- **Ask for the number in terms the user can measure.** The envelope is Dec- and tube-dependent (see
  "Correcting the physics"), so present the meridian threshold as "how far past the meridian this rig
  can track before the tube meets the pier, at your LOWEST imaging Dec with the LONGEST tube fitted",
  not as an exact figure. Suggested to the user 2026-08-29, unanswered.
- **Label the tier.** `MountLimitVerdict.Basis` exists for this: the editor (and P4's surfacing) should
  say whether this rig's meridian limit will be measured on the RA axis (SkyWatcher) or estimated from
  the hour angle (everything else), because the two are set differently -- an estimate needs margin
  for clock/site/sync error, a measured axis does not.

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

## P3 as built

**`MountLimitWatcher` (`TianWen.Lib/Sequencing/`) is host-agnostic by construction.** It depends on
only `IDeviceHub` and `IDeviceDiscovery` -- both already singletons in every host -- and exposes a
plain `RunAsync(CancellationToken)` loop, not an `IHostedService`/`BackgroundService`: `TianWen.Lib`
takes no dependency on `Microsoft.Extensions.Hosting`, so a host wires the loop up as it sees fit.
`TianWen.Server` gets a thin `MountLimitWatcherService : BackgroundService` in `TianWen.Hosting`,
registered alongside `IHostedSession` -- node-scoped, not session-scoped, so it keeps running whether
or not a session is active.

**No "active profile" abstraction exists across hosts, so the watcher does not use one.** The GUI, the
hosted server (`IHostedSession.ActiveProfileId`) and the CLI each track "which profile" differently,
and unifying that was explicitly out of scope for this phase. Instead, every tick it re-discovers all
profiles (`IDeviceDiscovery.DiscoverOnlyDeviceType(DeviceType.Profile, ct)`, the same mechanism
`ProfileEndpoints` uses) and matches each connected mount's URI against every profile's
`ProfileData.Mount`, using whichever one matches for that mount's `MountLimitConfiguration` and
`SiteLatitude`. Re-discovering every 5 s is deliberate, not an oversight: it is what lets a limit just
enabled in the profile editor take effect without a restart, and a handful of small JSON files costs
nothing worth caching around.

**Altitude uses the LATITUDE-only static overload** (`SiteContext.AltitudeDegrees(latitudeDeg,
hourAngleHours, decDeg)`), not `SiteContext.Create(lat, lon, timeProvider)`: altitude from HA + Dec +
Lat has no dependence on longitude or the clock (LST is only needed to go from RA to HA, and the
watcher already reads HA straight off the mount), so the watcher needs no `ITimeProvider` read on this
path at all -- only for its own poll-interval sleep.

**The per-entry latch is keyed on mount URI** (`ConcurrentDictionary<Uri, byte>`, the per-key
in-flight-set shape from CLAUDE.md's background-task-state table), not a single field like `Session`
uses for its one rig: the hub can have more than one mount connected across profiles, and each needs
its own "have I already acted since the last clear verdict" memory.

**Respecting the lease is a plain skip, not a softer action.** `hub.TryGetLease(deviceUri, out _)`
returning true means a session already owns this mount and is already evaluating the same
`MountLimits.Evaluate` on its own poll -- the watcher does not also warn or log, it simply does
nothing for that mount this tick, so the two are never both acting on the same axis.

**Verified with 2 sabotages** (both against the two lines this feature is actually *for*): disabling
the lease check made `ALeasedMountIsNeverActedOnEvenPastTheLimit` fail (the watcher stopped tracking
on a leased mount); disabling the per-mount latch made `TheActionFiresOnceAndThenLatchesUntilClear`
fail (`SetTrackingAsync(false)` fired twice instead of once) -- the GSS-derived failure mode this
plan's invariants exist to prevent (a park re-commanded every poll, never arriving).

**Deliberately left for a follow-up, not silently dropped:**

- ~~The GUI's own manual-slew path is not yet covered.~~ **DONE 2026-08-30**: `tianwen-gui`'s
  `Program.cs` drives `MountLimitWatcher.RunAsync` through its `BackgroundTaskTracker` on the
  background token, the way it drives `LanDiscovery` -- quitting stops it without touching a running
  session, whose leased mount the watcher skips on its own. No per-frame tick: the 5 s loop is the
  same one the server runs, so the two hosts cannot drift.
- ~~P1b is unaffected~~: **the watcher now reads the axis angle too** (NaN when the driver has none),
  and trusts only a MEASURED pointing state (`PointingStateSource`), so on a SkyWatcher it is on the
  mechanical tier like the session.
- ~~No telemetry surfacing (P4) yet~~: the watcher still only logs (it has no session to carry a
  verdict), which is the residual of P4 below -- a rig sitting idle in its limit with no session is
  visible in the log and in the driver's stopped tracking, nowhere else.
- **The profile is matched by the hub's own identity rule** (`Uri.DeviceKey`, scheme + host + path,
  2026-08-30): whole-URI equality skipped any profile whose mount query had drifted from the connected
  URI (re-discovery, a reconciled setting), silently. Pinned.

## The meridian test needs the pointing state (corrected 2026-08-30)

**The bug.** `MountLimits.Evaluate` read the hour angle alone, and `Session.EnforceMountLimitsAsync`
passed only that. But the meridian limit is a test on the RA AXIS, and the same hour angle puts that
axis in two places: a GEM that has not flipped (`PointingState.ThroughThePole`, ASCOM `pierWest`,
counterweight down while looking east) swings its tube toward the pier as it tracks west, while one that
has flipped (`Normal`, `pierEast`) is 12 h round on the same axis and moving AWAY from it. So with the
shipped defaults (warn 20, act 40, flip latest 10) every `AcrossMeridian` observation went: flip at
<= 10 min, warning at 20, **tracking stopped at 40 min -> `LimitReached` -> night over**, 30 minutes
after a successful flip. The flip clamp made it a certainty rather than a chance: it guarantees the flip
happens BEFORE the action threshold, which is exactly where the rig then sat when the threshold arrived.

**The fix.** `Evaluate` takes `PointingState` and reads the offset toward the pier as
`Normal ? -HA : HA`: post-flip, west is safe however far it goes, and the hazard is instead pointing
EAST (the mirror case, which a wrong-way goto or a bad sync produces -- the first cut waved it through
as "east = rising = safe"). `Unknown` keeps the hour-angle reading, the sky-coordinate approximation
this limit shipped with (right for a mount that has not flipped, wrong after one), labelled as the
weaker tier per the two-tier invariant. The horizon test is untouched: altitude is a sky quantity.
`Session` passes the `PierSide` it already polls; `MountLimitWatcher` reads `GetSideOfPierAsync` with
`Unknown` as the failed-read default -- **never `Normal`, which is the state in which the meridian test
is silent.**

**Why no test caught it.** None of the 38 P0/P2 cases involved a flip or a pointing state. Two traps
that decided how the new ones had to be written:

- **`default(PointingState)` is `Normal`.** An unconfigured NSubstitute `ValueTask<PointingState>`
  returns it, so the watcher tests left to the default would go green with enforcement deleted. Every
  mock now configures the state explicitly.
- **`FakeMountDriver` derives its pointing state from the CURRENT hour angle** (`HA >= 0 ? Normal :
  ThroughThePole`): it models a mount that flips the instant it crosses, and so can never be in the
  counterweight-up state a real GEM tracks into. Its `SetSideOfPierAsync` (previously
  `NotSupportedException`) now forces a state until the next slew or sync, the way ASCOM lets a client
  command one by writing `SideOfPier`, and `SessionMountLimitTests` uses it to hold the rig pre-flip.

Seen to fail first: the post-flip session case against the unfixed decider (mount stopped); the existing
"it stops the mount" session cases against the fixed decider WITHOUT the forced state (fake reports
`Normal`, nothing stops) -- which is why the helper forces `ThroughThePole` by default.

**A SkyWatcher-driver finding -- RESOLVED 2026-08-30, see "as ported" below.**
`SkywatcherMountDriverBase.RaToSteps` / `DecToSteps` only ever produced the NORMAL-state axis solution:
there is no through-the-pole branch (GSServer picks one from the destination's hour angle). So a goto or
sync to an EASTERN target lands the encoder model in `Normal`, which in the driver's own convention
(home = HA 6 h, counterweight down) is counterweight-UP, and a session "flip" re-slews to identical
encoder targets, i.e. moves nothing. The limit reads the driver's state, so on this driver it fires
right after a slew to an eastern target and never on the west-tracking case -- honest to what the driver
reported, and useless until the driver was. A limit is only as right as the pointing state under it.

**How GSS handles the flip (`origin/master` `eb7e92c`), which is the port the SkyWatcher driver is
missing.** GSS is a driver, so it owns both halves, and it keeps them apart:

- **Choosing** (`Axes.RaDecToAxesXy`): the axis solution is picked from the TARGET's hour angle. HA in
  [0, 180) deg uses the straight solution; HA > 180 (east) is "adjusted to be through the pole" --
  `X += 180, Y = 180 - Dec` -- with the Dec sign mirrored in the south first. `GetAlternatePosition` may
  then swap to the 180-deg alternate if it lies inside the Flip Angle and the hardware limits.
- **Reporting** (`SkyServer.SideOfPier`): from the DEC AXIS app angle, `|Y| < 90 -> pierEast, else
  pierWest`, mirrored in the south. Mechanical, from the encoders. Our SkyWatcher
  `GetSideOfPierAsync` (`0 < pos < CPR/2 -> Normal`) is this rule, ported.
- **Flipping**: GSS never flips on its own while tracking. The client re-issues a goto past the
  meridian, `IsFlipRequired` compares the new solution's pier side with the current one, and the flip
  IS that goto landing on the other solution. Writing `SideOfPier` is a forced flip to the alternate
  solution, refused outside the flip limits.

The SkyWatcher driver had ported the reporting half and not the choosing half.

**As ported (2026-08-30): `SkyToSteps(ra, dec, PointingState)`.** In the driver's own step convention
(home = HA 6 h, counterweight down, Dec axis at the pole) the two solutions are: straight,
`raSteps = (HA - 6h)/24 * CPR`, `decSteps = (90 - Dec)/360 * CPR`; through the pole, the same with
`HA + 12 h` and `decSteps` NEGATED (the Dec axis the same angle the negative way from home) -- GSS's
`X += 180; Y = 180 - Y` in our coordinates. Checked against the physics rather than trusted: with tracking
increasing steps, `Normal` at HA 0 is counterweight horizontal and at HA +6 h counterweight DOWN, so
Normal is the west-safe (post-flip) solution and through-the-pole the east-safe (pre-flip) one, in both
hemispheres, which is exactly the mapping `MountLimits.Evaluate` assumes. Five decisions ride on it:

- **The state is a CHOICE made by the caller, not by the conversion.** A goto asks
  `DestinationSideOfPierAsync` (HA >= 0 -> Normal, the rule the session's flip logic already uses);
  a sync keeps the half the Dec encoder is in (`IsThroughThePole(_posDec)`), because a sync says
  where the mount already IS and must not teleport the model across the pier on every plate-solve
  sync of an unflipped rig.
- **Decided ONCE per goto and kept for the refinement passes** (`_gotoPointingState`): a target just
  east of the meridian has crossed it by the time the axes stop, and re-deciding per pass would flip
  the mount in the middle of its own goto.
- **`StepsToRa` needs the Dec encoder** (through the pole the same RA axis angle looks 12 h away),
  and `StepsToDec` takes the magnitude of the folded axis angle, so a Dec axis wound past half a turn
  reads as the mirror it physically is.
- **The home boundary is INCLUSIVE, Normal** -- GSS `|Y| < 90.0000000001` -- so a mount that has
  never moved is Normal and the connect-time pole sync round-trips to HA 0 rather than 12 h.
- **The flip is the next goto, exactly as in GSS.** Nothing flips while tracking; the session's
  re-slew to the same target now chooses the Normal solution and the mount physically flips.
  `pierSideChanged` is a real signal on this driver for the first time.

Deliberately unchanged: a Dec guide pulse still moves the Dec AXIS in the commanded direction, so the
sky Dec sense reverses in the through-the-pole state as it does on every GEM -- the guider's
calibration owns that (CLAUDE.md, guider calibration pier-side invariant), not the driver.
`SetSideOfPierAsync` (GSS's forced flip to the alternate solution) is still unsupported. Hardware
validation is still outstanding: this was verified against the fake motor controller, which executes
whatever step targets it is given.

Pinned by six `FakeSkywatcherMountDriverTests` cases -- an eastern goto lands through the pole (Dec steps
negative) and reads back, a western one lands straight, a sync keeps its half, an unflipped rig tracked
45 min past the meridian FLIPS when re-slewed to its target, a target that transits during its own slew
keeps the command-time solution, home is Normal -- five of which failed against the old driver. The full
Functional suite (347) and the full unit suite (5206, 69 gated skips) stayed green.

**LX200-base, SGP and the fake report a COMPUTED pointing state, and a computed state must not feed the
limit as if it were measured.** `MeadeLX200ProtocolMountDriverBase.CalculateSideOfPierAsync` derives
pier side from the hour angle (`HA >= 0 -> Normal, else ThroughThePole`); `FakeMountDriver` uses the
same rule; `SgpMountDriverBase` answers a constant `Normal`. Each is the "the mount handles the flip"
assumption made concrete: the state a mount WOULD be in if its firmware always kept the counterweight
down. (`OnStepMountDriver` overrides with a real `:Gm#` query, so OnStep does report the mechanical
state; ASCOM/Alpaca pass the device's own answer through.) Consequence: west of the meridian such a
driver always says `Normal`, the offset is `-HA`, and **the meridian limit can never fire on it**. Right
only if the firmware really flips or stops itself (P5's territory: observe, do not duplicate); wrong on
the many LX200-protocol mounts that track past the meridian until the next goto, where the driver says
`Normal` while the mount is mechanically through-the-pole, counterweight up, and the limit is silent
exactly when it matters. The root is that `GetSideOfPierAsync` answers two different questions across
drivers -- "which state is the mount IN" (SkyWatcher encoder, OnStep, ASCOM) and "which state would a
slew to here CHOOSE" (LX200 base, Fake, SGP) -- and the limit wants only the first, while the flip gate
wants the second and already has `DestinationSideOfPierAsync` for it. **Decision deferred (tracked in
`TODO.md`):** either a computed answer reaches the limit as `Unknown` (the HA approximation, which DOES
fire past the meridian), or the interface grows a "measured vs computed" capability the limit and the
flip gate read differently. Not changed in this pass because the same method feeds the flip gate.

**The home edge.** Raw home (encoders 0,0) reads HA = +6 h on the SkyWatcher, through the pole, which the
meridian test reads as 6 h past the limit; once the site is pushed the driver re-syncs home to
(LST, pole), HA = 0. `Session` pushes the site in `InitialisationAsync` before its first poll (which
could not build a J2000 transform without it anyway), so a run never sees the raw reading --
`AHomedSkyWatcherProducesNoVerdict` pins that with its premise asserted. The residual is the
sessionless watcher on a mount connected with no site at all: one logged action (a no-op stop on a
mount that is not tracking) and a warning per tick until a profile pushes the site.

## P1b as built: the axis angle, and the limit prefers it (2026-08-30)

**`IMountDriver.GetAxisAngleAsync(TelescopeAxis) -> ValueTask<double?>`**, a default interface method
returning null, overridden by the SkyWatcher driver only. Degrees from the mount's home position, folded
into (-180, 180]: for a GEM home is counterweight down with the tube on the pole (GSS `HomeAxisX/Y = 90`).
The primary angle is hemisphere-corrected -- negated in the south, where the motor runs the other way
(`axisHours = 6 - HA`) -- so that positive is "turned in the tracking direction from home" everywhere:
`(HA - 6 h) x 15` in the Normal state, `(HA + 6 h) x 15` through the pole. The secondary angle is
positive in the straight half, negative through the pole, which `StepsToDec`'s hemisphere mirror already
arranges. **Angle, not steps**, exactly as the phase table asked: a consumer never sees CPR, the
`0x800000` home or the southern mirroring, the re-derivation `StepsToRa` exists to prevent.

**The one consumer contract is `|primary| - 90`**: how far the counterweight is above horizontal, in
degrees, in EITHER pointing state. That is the quantity the meridian limit is about, and it is what
`MountLimits.Evaluate` now takes as `primaryAxisAngleDeg`: when present (non-null, non-NaN) the meridian
test uses `(|angle| - 90) x 4` minutes of hour angle and does not consult the hour angle or the pointing
state at all -- fallback, never cross-check, per the invariant above. On a synced SkyWatcher this is the
same number the hour-angle tier produces (the port of `SkyToSteps` made the two coincide), so what the
tier buys is independence from the clock, the site and the pointing state a sync believed: a wrong
longitude shifts the hour angle by hours while the axis has not moved. What it cannot buy is
independence from the encoders' home -- Synta step counters are relative, a mount powered on off home or
synced to a wrong solution reports an angle wrong by exactly that much, and GSS has the same blind spot.

**The tier is labelled.** `MountLimitVerdict.Basis` (`HourAngle` | `AxisAngle`) says which one answered
and `Describe()` prints it ("measured on the RA axis" / "estimated from the hour angle"), which is the
plan's requirement that a user never mistakes a sky-coordinate estimate for a mechanical limit -- they
set the threshold wrong in opposite directions depending on which they believe they have.

**Plumbing.** `MountState.PrimaryAxisAngleDeg` (NaN = no model, like every other unknown there; the
session's `PollDriverReadAsync` is constrained to non-nullable structs, so the driver's null becomes NaN at
the read) is polled beside the hour angle and pier side, and `MountLimitWatcher` reads the same thing
with NaN as its failed-read default -- an unreadable axis must fall back to the estimate, not fire.
`FakeMountDriver` keeps the interface default, so the plain fake exercises the hour-angle tier and the
SkyWatcher fake the axis tier; both are pinned. Not done for OnStep, which exposes raw steps but whose
axis model (and home convention) this pass did not study.

**Tests**: seven pure cases (axis beyond horizontal is the offset whatever the sky says; sign does not
matter; at/below horizontal is clear even when the sky says 60 min past; the axis wins over a disagreeing
hour angle in both directions; the axis needs no hour angle at all; null/NaN fall back and say so; the
horizon test is untouched), two session cases (a SkyWatcher verdict reports `AxisAngle` with the expected
50 min, a plain fake reports `HourAngle`), one watcher case (axis wins both ways), four SkyWatcher-fake
cases (home reads -90/0, eastern and western gotos read +45/-45 and -45/+45, the southern primary angle is
hemisphere-corrected, alt-az answers null). Sabotage (decider ignores the axis) fails the tier tests.

## Only a MEASURED pointing state may drive the limit (2026-08-30)

`IMountDriver.PointingStateSource` (`None` / `Computed` / `Measured`, default `Computed`) says how a
driver knows the state it reports, and `MountLimits.TrustedPointingState` hands `Evaluate`
`Unknown` for anything not measured. The LX200-base driver, SGP and `FakeMountDriver` derive the state
from the hour angle -- "the firmware will have flipped", a prediction -- and west of the meridian that
reads as post-flip, which would silence the meridian limit on exactly the rig tracking into its pier.
Untrusted, the limit falls back to the hour-angle tier there, which can stop a flipped rig 30 min early
but cannot be silenced; the flip logic keeps reading `GetSideOfPierAsync`, because "would a slew to
here flip" wants the computed answer. Measured: SkyWatcher (Dec encoder; `None` in alt-az), OnStep
(`:Gm#`), ASCOM and Alpaca (the device's own `SideOfPier`). The fake is measured only while a test has
forced a state through `SetSideOfPierAsync`, which is what lets the session tests hold a rig pre-flip.
**Default `Computed`, deliberately**: an unaware driver then reports the weaker claim and can never
silence the limit. Pinned in the pure, session and watcher suites (a computed `Normal` at HA +1 h still
stops).

## P4 + P5 as built (2026-08-30)

**P4.** `ISessionTelemetry.MountLimitVerdict` is the surface; `Session` already had it. It crosses the
wire as `MountLimitDto` inside `SessionStateDto` (numeric enums, `ExceededBy` through `ForWire`;
nullable and NOT required, so an older node reads as `Clear` and an older client still deserialises),
reaches `RemoteSessionMirror` and `LiveSessionState` (holder-boxed: a 24-byte struct crossing the poll
and render threads would tear), and from there the Home board -- `RigCard.MountLimit`, set only when
breached -- where the **Flip column doubles as the limit column** (`limit 7m` for a meridian warning,
`LIMIT` once acted, warning colour; the limit is what ends the night, so it outranks the flip) and the
detail card gets the full `Describe()` sentence directly under the status, not gated on the rig
running (a stopped rig is exactly the one to show). **Both notification feeds fire on CLASS
transitions only** -- clear -> warning -> acted -> clear -- never per poll (`AppSignalHandler
.NotifyLimitTransitions` for the GUI, `EventBroadcaster.NotifyLimitTransition` for the node, whose
notification then rides to remote cards as `LastNotification`). The latch's downgrade to `Warn` after
acting leaves `IsWarningOnly` false, so it is not a transition. The TUI renders the same
`HomeBoardLayout`, so it got the cell and the row for free.

**P5.** `Session.DetectDriverEnforcedStop`, on the poll: a mount whose tracking was observed ON and is
now OFF while it is not slewing and this session did not ask for the stop has enforced a limit of its
own (GSServer, OnStep, an ASCOM driver with limits) or stalled. It is latched as
`MountLimitVerdict.DriverEnforcedStop` (`MountLimitKind.DriverEnforced`, response `Warn`: the driver
already acted) and ends the run through `LimitReached` -- not as a fault, and NOT fought: without this
the next observation's `EnsureTrackingAsync` switched tracking straight back on against the driver's
stop. Independent of `Setup.MountLimits`, because the driver's limit exists whether or not ours does.
Three guards decide whether it means anything: **slewing gates it** (a Synta goto runs the axes
"running, not tracking" until it arrives, the same signature as a stop), **the poll reads `IsSlewing`
BEFORE `IsTracking`** (on SkyWatcher `IsSlewingAsync` is the goto-completion hook that resumes
tracking, so the other order can read "not slewing, not tracking" for a mount that is fine), and **it
is debounced over two polls**. Every place the session itself stops tracking (`Finalise`, sky flats,
the configured limit) raises `_mountStopCommanded` first, cleared the next time tracking is seen on.
Pinned: the fake stopping on its own is read as `DriverEnforced` after two polls and not one; the
session's own limit stop keeps its own name; a slewing SkyWatcher with tracking off is not a stop.

## P1 editor UI as built (2026-08-30)

Two places, because the two things being edited live in two places:

- **The limits are a profile fact, so they are edited on the profile panel**: `PanelSection.MountLimits`
  right after `Site`, built by `EquipmentTab.BuildMountLimits` on the `BuildSite` pattern. The display
  row is `EquipmentActions.DescribeMountLimits` -- absolute thresholds ("stop at 40 min (warn 20)"),
  never the stored extras -- with `[>]` posting `EditMountLimitsSignal`. The editor asks for the four
  numbers in measurable terms ("past meridian, warn at (min)", "act a further (min)", "horizon floor
  (deg)", "warn from (deg above)"), parsed and range-checked by the pure `TryParseMountLimits`
  (0..360 min, 0..60 deg) onto the current record composed with the pending switch and responses;
  the switch and the two responses are ONE cycle button each showing the pending value (Yes / No;
  Warn -> Stop tracking -> Park), the way a device setting such as "Dec Pulse as GOTO" is edited, and
  save with the numbers through `UpdateProfileSignal` (one save path, `SetMountLimits` a `with`).
  Cancel/Escape drop all seven. The first cut drew On|Off and Warn|Stop|Park as pill PAIRS that saved
  on every click -- past Cancel -- and painted both halves in the active colour (`CreateButton` is
  the same mix as `SegmentActive`), which is how it read live. The handler routes only.
- **The flip settings are a session preference, so they got their first UI in the session config
  editor**: a "Meridian Flip" `ConfigGroup` (pause before, earliest, latest; all in minutes, the
  unit the limit shares; earliest can never pass latest). **The deadline carries a caveat** --
  `ConfigFieldDescriptor.Caveat`, a `Func<SessionConfiguration, ProfileData?, string?>` -- rendered in
  the warning colour beside the value on the GUI (`SessionConfigStyle.WarnText`) and appended to the
  value on the TUI: "mount limit clamps this to N min" whenever
  `MountLimitConfiguration.ClampFlipLatestMinutes` would bite. The clamp keeps the rig safe silently;
  the editor says so where the number is set. `SessionTabState.ActiveProfileData` carries the profile
  from the render entry point (which has the app state) to where the form is built (which does not).

What the editor does NOT yet do: label the tier (`MountLimitVerdict.Basis` is known only once a
verdict exists; showing "this rig's limit will be measured / estimated" needs the mount's
`PointingStateSource` at edit time, i.e. a connected driver), and the "ask in measurable terms"
wording could still say WHY (Dec- and tube-dependent) in a tooltip the panel has no room for.

## End to end on the fake SkyWatcher (2026-08-30)

Three runs of the real `ObservationLoopAsync` on the observation-loop harness (`SessionObservationLoopTests`,
fake clock anchored to a December night at Vienna, time pumped in 5 s steps), all on `mountPort:"SkyWatcher"`
so the axis model, the measured pointing state and the mechanical tier are the ones exercised:

- **Flip + limits coexist.** Limits warn 20 / act 40 min, target crossing the meridian, imaged 75 min. The
  GEM flips (`MeridianFlipCount > 0`, pier side `Normal` afterwards -- the port made the flip real), the run
  ends 66 min past the meridian with the verdict CLEAR and tracking on. This is the exact scenario the
  hour-angle-only decider ended 30 min after the flip.
- **The limit is the ultimate clamp.** The flip configured LATER than the limit (earliest 60 / latest 90 vs
  act at 20): the flip window collapses, the loop sits in the pre-flip obstruction pause while the mount
  tracks through the pole, and at 20 min the limit acts -- verdict `Meridian`, `Basis = AxisAngle`, tracking
  off, hour angle frozen at 20-30 min, no flip, no advance to the next target, phase not `Failed`.
- **P5, a driver's own stop.** No limits configured; the mount is stopped from outside after the third
  frame (issued from the pump thread between two advances -- issued from the loop's own `FrameWritten`
  handler it landed at an undefined point relative to the poll and the test passed or failed on that
  alone). The run ends `DriverEnforced`, tracking stays off, the second target is never started.

Two defects the E2E found that the unit and poll-level tests could not:

- **The imaging loop's `while` condition includes `IsTrackingAsync`, so it left on the FIRST "not
  tracking" read** -- before the driver-stop detector's second poll -- and returned
  `AdvanceToNextObservation`, whereupon the next observation's `EnsureTrackingAsync` switched tracking
  straight back on against the driver's stop: the fight P5 exists to prevent. An undecided loop exit now
  asks the detector again at the tick cadence until it has had its full look (`DriverStopDebouncePolls`).
- **On the SkyWatcher driver a guide pulse on a STOPPED mount read as tracking**: with tracking off an RA
  pulse runs the axis in constant-speed mode for its duration, the same status signature as sidereal
  tracking, and the guider keeps correcting after the limit acts. `IsTrackingAsync` now masks those
  pulses (`_raPulseOnStoppedAxis`, a counter raised before the first write), and the observation loop stops
  the guider on `LimitReached` -- Finalise would too, but flats may run first, and on a stopped mount every
  RA correction is a real axis move.

Not verified live: the GUI's editor panel (start-up wedged in serial probing on this box), and any real
mount.

## Hosts, as of 2026-08-30

| Surface | tianwen-server | tianwen-gui | tianwen (TUI) |
|---|---|---|---|
| Watcher (P3, no session) | `MountLimitWatcherService` | `Program.cs` tracker | `TuiSubCommand` tracker |
| Verdict on the Home board | n/a | shared `HomeBoardLayout` | shared `HomeBoardLayout` (cards built per frame in `TuiHomeTab`) |
| Feed on class transitions | `EventBroadcaster` | `PollPreviewTelemetry` -> `NotifyLimitTransitions` | loop -> `NotifyLimitTransitions` (the TUI has no telemetry poll) |
| Verdict with NO session (manual slew) | log only | `NotifyLimitTransitions` -> `MountLimitWatcher.VerdictFor` -> local `LiveSessionState` -> card + feed | same call, same seam |
| Flip settings + clamp caveat | n/a | "Meridian Flip" config group | same group; caveat appended to the value |
| Limits editor | n/a | `PanelSection.MountLimits` | **not built** -- the TUI equipment tab has its own site bar and key routing; a limits row there is open |
| Live Session tab verdict | n/a | not shown (the flip countdown lives on the Home board only) | same |

## What is still open

- **Hardware validation** of the SkyWatcher axis-solution port (`SkyToSteps`) and the forced flip: the
  fake motor controller executes whatever step targets it is given.
- ~~**Watcher-side surfacing**~~ (done 2026-08-30, found live -- see "Live verification" below):
  `MountLimitWatcher.VerdictFor(mountUri)` publishes the last tick's verdict per mount (by `DeviceKey`,
  dropped for any mount the tick skipped), and `AppSignalHandler.NotifyLimitTransitions` feeds the LOCAL
  `LiveSessionState.MountLimitVerdict` from it whenever no session exists, so the Home card row and the
  feed work for a manual slew exactly as for a run. The server still has no session-less surface for it.
- **Tier labelling in the editor**, above.
- **OnStep axis angle**: exposes raw steps, axis model not studied; stays on the hour-angle tier.
- **A verdict on the TUI's Live Session surface** beyond the Home board cell/row, and **a limits editor
  row in the TUI equipment tab** (its site bar is bespoke, not the profile-panel section list).
- **Live TUI verification** of the editor path (the GUI was verified live 2026-08-30, below).

## Live verification, 2026-08-30 (GUI, fake SkyWatcher, Melbourne profile)

Driven through the SDL inspector against `tianwen-gui`; every finding below was fixed the same day.

1. **Start-up wedged before the first frame, and it was ours.** `DiscoverOnlyDeviceType(Profile)` ran the
   whole serial probe pass for a scan of JSON files -- at start-up on the MAIN thread, and from this
   watcher's every 5 s tick (so every unconnected serial device was being probed nine times a tick). On
   this box the first probe opened COM4, a Windows "Standard Serial over Bluetooth link" listener port
   (created for a paired RAX20 advertising SPP; `bthmodem.sys`), and its `:GVP#` write never completed:
   `SerialPort.WriteTimeout` is infinite by default and `SerialStream.WriteAsync` ignores its token, so no
   budget could end it. A `dotnet-stack` dump showed NO thread in serial I/O -- a pending overlapped write
   is invisible -- and the tell was the missing `COM4 --> :GVP#` line, which is logged after the await.
   Fixes, each pinned: the profile scan no longer probes (`DeviceDiscoveryTests`); writes are bounded at
   the port AND as a task (`SerialConnectionBase.WriteTimeoutMs`), closes are bounded (`TryClose`), and
   an attempt whose I/O ignores its budget is abandoned (`SerialProbeService.AbandonGrace`); a write the
   driver never completed marks the connection (`ISerialConnection.HasAbandonedIo`) and the pass gives
   that port up for the rest of the discovery (`SerialProbeServiceTests`, two cases). The distinction is
   deliberate: a device at the wrong baud or waiting for a different message still COMPLETES the write
   (no handshaking is ever enabled) and only its READ times out, which still walks every protocol and
   baud as before -- COM5 did, all 9 x 3 x 2. Only a port that would not take the bytes is given up.
2. **The editor's On|Off and Warn|Stop|Park pill pairs painted both halves in the active colour**
   (`CreateButton` is the same palette mix as `SegmentActive`) and saved on every click, past Cancel.
   Now one cycle button each showing the pending value, saved with the numbers -- the app's existing
   convention for a bool/enum setting ("Dec Pulse as GOTO": Yes / No). Label column widened 128 -> 210.
3. **The watcher stopped the mount and the GUI showed nothing.** Profile limits enabled with a 45 deg
   horizon floor, fake mount connected at home (alt 37.9 deg here), `SkyMapSlewToObject` to a 35 deg
   target: the log read warn (4.5 deg margin, mid-slew) -> `StopTracking` (6.5 deg below) -> latched, in
   three ticks, while the Home card read "Idle" and the feed carried only the slew's notification. Closed
   by `VerdictFor` above. Two wordings fixed on the way: `Describe()` ended with a period the watcher's
   log line then doubled, and the latched verdict said "Will warn only" about a mount it had just stopped
   -- `MountLimitVerdict.Latched` (also on the wire, optional) makes it say the limit has already acted.

## Carried over from the 2026-08-29 handoff notes

The rest of that session's record lives where it belongs -- the GSS findings, the "tests all passed
either way" lesson and the dual-axis capability table in `gss-parity-audit.md`, the test traps (sync
not slew, tracking OFF, `ExternalTimePump`, `[Fact(Timeout)]` because the regression hangs) in
`CLAUDE.md` and the sections above, its outstanding list in `TODO.md`. Three things were only in the
notes:

- **Geometric altitude existed three times before it existed once.** A fourth copy was written for the
  limit before the user caught it: `CometObservability.AltitudeDeg` was already half-hoisted (it
  borrowed `SiteContext.ComputeLST` and hand-rolled the rest), and `SiteContext`'s own remarks already
  named `NeuralGuideFeatures` as a candidate. Both now use `SiteContext.AltitudeDegrees`. Deliberately
  NOT hoisted: `IsAboveHorizon` (skips the `Asin`, matters per star on the sky map) and `VSOP87a`
  (equatorial-to-horizontal wholesale, radians, off SOFA `Gmst06`, its altitude feeds its azimuth).
  `SOFAHelpers.AltitudeFromAstrom` and `Transform.ElevationTopocentric` are not duplicates either: SOFA
  apparent place and REFRACTED altitude, which is what a planner and an eye want and what a mechanical
  limit must not use.
- **The UI round-trips unknown profile fields by construction.** Every profile mutation goes through
  `EquipmentActions` as `data with { ... }`; there is no positional `new ProfileData(...)` outside
  `Empty` and tests. So a `MountLimits` block hand-edited into the JSON survived the GUI rewriting the
  profile even before the editor existed. (Hedged at the time; the user pushed back; they were right.)
- **A horizon-limit test must use an hour angle clear of the meridian threshold**, or it asserts on
  whichever verdict won the ranking -- the two are ranked against each other. Bit twice; now the named
  constant `DescendingClearOfMeridian` (3 min) in `MountLimitsTests`, whose remark says to check it
  first if the horizon tests ever fail together.

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
