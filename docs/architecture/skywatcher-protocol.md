# Skywatcher motor controller: serial and WiFi protocol

Reference for TianWen's native `SkywatcherMountDriver` (`IMountDriver`), which drives SkyWatcher (Synta)
mounts (EQ6-R, HEQ5, AZ-EQ6, EQ8, Star Adventurer GTi, AZ-GTi and the rest of `SkywatcherMountModel`) by
speaking the motor controller's own axis protocol, the one EQMOD and GSServer speak. Every behaviour lives
in `SkywatcherMountDriverBase<TDevice>`; `SkywatcherMountDriver` adds nothing, and
`FakeSkywatcherMountDriver` subclasses the same base, so tests run the real protocol code against
`FakeSkywatcherSerialDevice`. The pure codec is `SkywatcherProtocol`, the guide-rate set
`SkywatcherGuideRate`, addressing `SkywatcherDevice`, discovery `SkywatcherSerialProbeBase` and
`SkywatcherDeviceSource`, the WiFi transport `SkywatcherUdpConnection`, all under
`src/TianWen.Lib/Devices/Skywatcher/`. There is no ASCOM dependency.

> **Derived from the code and from GSServer**, the reference Synta driver. GSS's own wire output is pinned
> by `SkywatcherGssOracleTests` from transcripts that `tools/GssOracle` recorded
> (`src/TianWen.Lib.Tests/Data/gss-oracle-transcripts.json`; recorded from a stale GSS tree, see the
> "Related" note in [`gss-parity-audit.md`](../plans/gss-parity-audit.md)). **Nothing here is
> bench-validated.** The SkyWatcher bench queue is #635 to #644 plus #658; where the code relies on
> firmware behaviour, the text says what it assumes.

## Transports

**Serial.** `SkywatcherDevice.ConnectSerialDeviceAsync` opens the URI's `port` through
`IExternal.OpenSerialDeviceAsync`, whose `External` implementation keeps one connection per port name
and hands it back while it is open.

| Parameter | Value |
|-----------|-------|
| Baud rate | the URI's `baud`: 9600 (`DEFAULT_LEGACY_BAUD`, a legacy mount on an external serial adapter, and the default) or 115200 (`DEFAULT_USB_BAUD`, a board with integrated USB such as the EQ6-R or AZ-EQ6) |
| Data / parity / stop | 8-N-1, the .NET `SerialPort` defaults, which `SerialConnection` does not change |
| Handshake | none; DTR and RTS are not asserted |
| Encoding | ASCII |
| Write timeout | 2000 ms, at the port and at the task (`SerialConnectionBase.WriteTimeoutMs`) |
| Read timeout | **none.** The driver's connection keeps `SynchronousReads` off and `SerialPort.ReadTimeout` at its infinite default, so a silent board holds the read, and the port lock, until the caller's token fires |

**WiFi.** When `port` parses as an IP address (`IPAddress.TryParse`), the same method returns a
`SkywatcherUdpConnection` to UDP port 11880 (`SkywatcherProtocol.WIFI_PORT`) and ignores `baud`. It
sends one datagram per command and takes one datagram per reply, cut at the first `\r`; the socket is
`Connect`ed to the mount, so only its datagrams are received. `ReceiveTimeout` is set to 2000 ms, but
.NET documents `Socket.ReceiveTimeout` as governing synchronous `Receive` only, and every read here is
`ReceiveAsync(CancellationToken)`: a lost datagram is bounded by the caller's token, as on serial
(inferred from the API, not measured). A `SocketException` or a cancellation both read as "no reply".

**On both transports the read helpers return `null` for no reply, a timeout, a dead port and the caller's
own cancellation alike** (`SerialConnectionBase.TryReadTerminatedRawAsync`, the UDP `catch` blocks). The
driver cannot tell them apart, which is fact 1 of #810.

## Framing

- **Command:** `:` + command letter + axis + optional data + `\r` (`SkywatcherProtocol.BuildCommand`). The
  axis is `1` (RA, primary), `2` (Dec, secondary) or `3` (both; only `:F3` uses it).
- **Reply:** `=` + data + `\r` when the board took the command, `!` + code + `\r` when it refused. Read
  `\r`-terminated with the terminator stripped, 128 bytes at most (`MaxTerminatedResponseBytes`).
  `TryParseResponse` accepts only a leading `=` and returns the rest as data.
- **Error codes:** the driver classifies an exchange three ways only (`SkywatcherAck`): accepted (`=`),
  refused (anything else), or no answer (`null`, a timeout, which is a different fact from a refusal).
  Comments name `!0` (unknown command) and `!2` (motor not stopped). `!2` is the one the design turns on:
  the code assumes, after GSS, that firmware refuses `:G` on a running axis (the fake does), so every path
  that sends `:G` first stops the axis and waits. No code is parsed beyond the first character.
- **Numbers:** a 24-bit value is six uppercase hex digits in **little-endian byte order**
  (`EncodeUInt24` / `DecodeUInt24`: 0x800000 is `000080`). Positions are offset by 0x800000
  (`POSITION_OFFSET`), so home is step 0 and `DecodePosition` spans -0x800000 to 0x7FFFFF. Exceptions:
  the `:f` and `:q` replies are read nibble by nibble in wire order, `:g` is two hex digits, the `:G`
  payload is two decimal digits, `:P` and `:O` one digit. `DecodeUInt24` uses `byte.Parse`, so an `=`
  reply holding a non-hex digit throws `FormatException`; `ParseHexNibble` instead maps one to 0.
- **Motion mode (`:G <func><dir>`, `EncodeMotionMode`):** func 0 high-speed goto, 1 low-speed slew
  (tracking, guiding, `MoveAxisAsync` up to 2x sidereal), 2 low-speed goto, 3 high-speed slew; the speed
  bit inverts between goto and slew (`SkywatcherMotionFunc`). Dir bit 0 = reverse, bit 1 = southern
  hemisphere, set on every `:G` from the site latitude (`IsSouthernHemisphere`; an unset latitude is taken
  as north, with one warning).
- **Step period (`:I`, `ComputeT1Preset`):** T1 = timer frequency x 360 / speed (deg/s) / CPR, multiplied
  by the high-speed ratio when the func is a high-speed one (`IsHighSpeed`).

## Commands

| Cmd | Axis | Data | Reply, as parsed | Used by |
|-----|------|------|------------------|---------|
| `:e` | 1 | none | 24-bit: byte 0 mount model (`SkywatcherMountModel`), byte 1 minor, byte 2 major; `VersionString` = `major.minor` (`TryParseFirmwareResponse`) | `InitDeviceAsync`, both discovery paths |
| `:q` | 1 | `010000` | six flag nibbles in order (`ParseCapabilities`) | `InitDeviceAsync`; kept in `_capabilities`, read nowhere |
| `:a` | 1, 2 | none | counts per revolution; RA forced to 78848 on 80GT/114GT (`OverrideGearRatio`) | `InitDeviceAsync` |
| `:b` | 1 | none | timer frequency, used for both axes | `InitDeviceAsync` |
| `:s` | 1, 2 | none | steps per worm revolution | `InitDeviceAsync`, `GetWormPeriodStepsAsync` |
| `:g` | 1 | none | high-speed ratio, one hex byte; 16 when unparseable | `InitDeviceAsync` |
| `:j` | 1, 2 | none | position, 24-bit minus 0x800000 | every position read, the slew pass |
| `:f` | 1, 2 | none | three nibbles: n0 bit 0 constant-speed (not goto), bit 1 reverse, bit 2 high speed; n1 bit 0 running; n2 bit 0 init done. Tracking = running and constant-speed | `QueryAxisStatusAsync` |
| `:E` | 1, 2 | position + 0x800000 | `=` | `SyncRaDecAsync`, the pole sync |
| `:F` | 3 | none | `=` | `InitDeviceAsync` (initialise both axes) |
| `:P` | 1, 2 | one digit, the `SkywatcherGuideRate` index | `=` | `InitDeviceAsync`, the guide-rate setters (ST-4 port rate) |
| `:G` | 1, 2 | `<func><dir>` | `=`; `!2` on a running axis | every motion |
| `:H` | 1, 2 | step count, unsigned and relative (direction from `:G`) | `=` | `SlewAxisToAsync`, the Dec micro-goto |
| `:M` | 1, 2 | break-point steps: 3500 high-speed, 0 low-speed | `=` | same |
| `:I` | 1, 2 | T1 step period | `=` | tracking, `MoveAxisAsync`, pulses |
| `:J` | 1, 2 | none | `=` | start motion |
| `:K` | 1, 2 | none | `=` | decelerating stop; it only STARTS the deceleration |
| `:L` | 1, 2 | none | `=` | instant stop (`AbortSlewAsync`) |
| `:O` | 1 | `1` / `0` | `=` | `CameraSnapAsync` (snap port on / off) |

The driver never sends `:S` (absolute goto target), `:d`, `:D`, `:W` or the advanced `:X` set. Reads go
through `SendAndReceiveAsync`, actions through `SendCommandAsync` (best-effort: a refusal or silence is
logged and the call returns) or `SendCommandVerifiedAsync` (below).

## Connect / init sequence

`InitDeviceAsync`, after the transport opens:

1. `:e1`. **The only gate:** no parseable reply, and the connect fails.
2. `:q1010000`, `:a1`, `:a2`, `:b1`, `:s1`, `:s2`, `:g1`, `:j1`, `:j2`. Each unanswered read leaves its
   field at the default (CPR 0, timer 0, ratio 16, worm 0, position 0) and **the connect still succeeds**.
3. `:F3`, then `:P1` and `:P2` with the default rate, 0.5x sidereal (index 2).
4. If both encoders read 0 and the latitude is known, sync to (LST, +90 or -90) through `:E1` / `:E2`. At
   connect the latitude is normally still unset (the profile pushes the site afterwards), so the same sync
   runs from `SetSiteLatitudeAsync` / `SetSiteLongitudeAsync` instead (`MaybeSyncToPoleAfterSiteSetAsync`),
   once both are set and only while the encoders still read 0. That path skips alt-az; the connect-time
   one does not, and in alt-az its sync throws, which fails the connect whenever the latitude was set
   before connecting.

No time is ever sent: `TimeIsSetByUs` is true, `TryGetUTCDateFromMountAsync` answers the `TimeProvider`'s
clock, `SetUTCDateAsync` is a no-op, and LST comes from that clock and the site longitude (0 while unset).

## How a slew is done

`BeginSlewRaDecAsync` refuses alt-az and a zero CPR, then chooses the axis solution from
`DestinationSideOfPierAsync` (hour angle >= 0: `Normal`, else `ThroughThePole`). The choice is made **once
per goto** (`_gotoPointingState`) and arms the refinement (`_gotoTargetRa` / `_gotoTargetDec`) before the
first pass. One pass is `SlewToRaDecCoreAsync`:

1. `StopAxisAndWaitAsync` on both axes: `:K`, then `:f` every 25 ms, `:K` again every fifth poll, giving up
   after 3.5 s with a warning and returning as if stopped.
2. `:j1` and `:j2`, so the delta is never taken from a stale cache (a zero delta skips the axis).
3. `SkyToSteps` for the chosen solution, and `_slewCommandedAtTicks` stamped **before the first write**.
4. Per axis (RA, then Dec), `SlewAxisToAsync`: beyond 640 sidereal-seconds of steps a high-speed goto
   (`:G` func 0, `:M` 3500), else low-speed (func 2, `:M` 0); `:G`, `:H |delta|`, `:M`, `:J`.

**Completion is polled; the protocol has no arrival callback.** `IsSlewingAsync` reads `:f1` and `:f2` and
reports slewing while an axis is running in goto mode and no guide pulse is in flight. Then, in order:

- **Start grace.** A goto commanded under 2 s ago (`SlewStartGrace`) and not yet seen running reports
  slewing, because a controller can answer "not running" just after `:J`
  ([`gss-parity-audit.md`](../plans/gss-parity-audit.md) finding 2; bench #640).
- **Refinement.** The target steps encode the hour angle at command time, so a long slew lands late by
  its own duration. With a goto pending, a residual above 30 arcsec re-runs the pass with the same
  solution, up to twice (`MaxGotoRefineAttempts`), under a CAS gate so two pollers cannot both issue it;
  it reports slewing meanwhile (bench #639).
- **Arrival.** The target is disarmed and `SetTrackingAsync(true)` runs, best-effort, as GSS does after a
  goto. **Assumed, not verified:** the board may also resume sidereal tracking on its own; the fake does
  (bench #638), and the setter then only re-times `:I1`.

`AbortSlewAsync` disarms the refinement first, then sends `:L1` and `:L2`.

## How tracking and pulse guiding work

**Tracking is the RA axis run as a low-speed slew at sidereal** (`SetTrackingAsync(true)`): `:f1`; an axis
already running constant-speed in the tracking direction gets `:I1` alone (a `:G` would be refused `!2`);
otherwise stop-wait if running, `:G1` (func 1, forward in the north, reverse in the south), `:I1`, `:J1`.
`SetTrackingAsync(false)` sends `:K1` without waiting. Only sidereal is native. `IsTrackingAsync` is `:f1`
running and constant-speed, masked while an RA pulse runs on a stopped axis (`_raPulseOnStoppedAxis`).

**A guide pulse is two methods** (the repo [`CLAUDE.md`](../../CLAUDE.md), "A guide pulse is TWO
methods"). `StartPulseGuideAsync` returns once the board is COMMANDED; the hold runs in the background
(`RunPulseAsync`), because Synta boards have no "pulse for N ms". The in-flight counter
(`_pulseGuideInFlight`) rises before the first write and falls when the hold ends, so an overlapping RA +
Dec pair (`CanPulseGuideSimultaneously` is true) clears only when both finish. Pulses under 20 ms are
ignored. The rate is `SkywatcherGuideRate`, five firmware indices at most 1.0x, snapped by `Nearest`.

| Pulse | Start | Hold, then end |
|-------|-------|----------------|
| RA, mount tracking (a fresh `:f1`) | `:I1` at (1 + f) x sidereal West, (1 - f) x sidereal East; East at 1.0x commands sidereal / 1000 (`EastPulseHaltsTheAxis`) | **verified** `:I1` back to sidereal |
| RA, mount stopped | `:G1`, `:I1` at the combined rate, `:J1` | **verified** `:K1` |
| Dec | stop-wait if running, `:G2` func 1 (North forward, South reverse), `:I2` at f x sidereal, `:J2` | **verified** `:K2` |
| Dec, `decPulseGoto=true` | steps = duration x rate (zero skips), stop-wait, `:G2` func 2, `:H2`, `:M2 000000`, `:J2` | `:f2` polled every 25 ms until stopped, 3.5 s cap |

**Verified** means `SendCommandVerifiedAsync`: up to three attempts, each accepted only on `=`, then
`SkywatcherDriverException`. These three are the commands that fail BACKWARD, leaving an axis at a rate
the driver believes it cancelled ([`gss-parity-audit.md`](../plans/gss-parity-audit.md) finding 3); every
other command fails forward and stays best-effort. They run in a `finally` under `CancellationToken.None`,
so a cancelled pulse still restores. A fault after "commanded" has no caller, so it is parked in
`_pendingPulseFault` and re-thrown by the next `StartPulseGuideAsync` **and by `IsPulseGuidingAsync`**,
inside the guide frame that caused it; a cancellation is logged, never parked. `ResilientCall.IsTransient`
does not accept `SkywatcherDriverException` (it derives from `Exception`), so the resilience layer never
retries it; it reaches the session as a `GuidingErrorEvent` from `BuiltInGuiderDriver`.

## Pier side and axis angle

**The pier side is the Dec encoder half, a MECHANICAL state that tracking never changes.**
`GetSideOfPierAsync` reads `:j2` fresh: a folded Dec axis angle below 0 is `ThroughThePole`, anything
else `Normal`, home included (`IsThroughThePole`). `PointingStateSource` is `Measured` (`None` in alt-az,
which reports `Unknown`). It changes only when the Dec axis moves (a goto, in practice) or a sync rewrites
it; the meridian-flip invariant in [`CLAUDE.md`](../../CLAUDE.md) rests on that. A goto CHOOSES the half
(above); a sync keeps the half the Dec encoder is in, so a plate-solve sync never moves the model across
the pier. `SetSideOfPierAsync` is GSS's forced flip: the current sky position re-reached through the
other solution in one goto pass (bench #636).

The conversions (`StepsToRa`, `StepsToDec`, `SkyToSteps`): home, step 0, is hour angle 6 h at the pole.
North, HA = 6 + steps / CPR x 24 and Dec = 90 - |fold(steps / CPR x 360)|; south, HA = 6 - steps / CPR x 24
and Dec = -90 + |fold(...)|; through the pole, HA is 12 h less and the Dec axis the same angle negated.
The derivation is in [`mount-safety-limits.md`](../plans/mount-safety-limits.md) ("As ported").

`GetAxisAngleAsync` is the mechanical tier `MountLimits.Evaluate` prefers over the hour angle: one `:j`,
the primary angle folded into (-180, 180] and negated in the south, the secondary the folded Dec angle;
`null` in alt-az or before CPR is known. Contract and consumer: "P1b as built" in the same plan, bench #637.

## Park / home

`ParkAsync` disarms the refinement, stop-waits both axes and drives each to step 0 with `SlewAxisToAsync`
from the **cached** positions (no `:j` refresh, unlike the slew pass), then sets `_isParked` at once.
`AtParkAsync` reads that flag, `UnparkAsync` clears it and sends nothing, `AtHomeAsync` compares the cached
steps with 0, and `CanSetPark` is false.

## Mapping to `IMountDriver`

The last column is what the member does when CONNECTED and the reply is missing, late or unparseable.
Reads behave the same when NOT connected (`SendAndReceiveAsync` returns `null` for a closed port), so they
cannot tell the two apart; an action on a closed port, or one whose write fails, throws
`InvalidOperationException`, which `ResilientCall.IsTransient` also does not accept. **Violation** marks a
read that returns a value where [`driver-resilience.md`](driver-resilience.md) requires a transient throw
(#810); the suggested fix is one line each.

| Member | Wire | Connected, no valid reply |
|--------|------|---------------------------|
| `IsSlewingAsync` | `:f1`, `:f2`; may send a refinement pass and the tracking start | **Violation.** `QueryAxisStatusAsync` answers "stopped, not tracking", so mid-slew it reads not slewing; with a goto pending and the grace spent it runs the refinement from stale positions and may stop the axes and re-goto. Fix: `QueryAxisStatusAsync` throws `IOException` |
| `IsTrackingAsync` | `:f1` | **Violation.** `false`: an RA pulse then takes the stopped-axis branch and its verified `:K1` stops tracking; two such polls latch `DriverEnforcedStop`. Fix: as above |
| `SetTrackingAsync(true)` | `:f1`, then `:I1`, or `:G1` `:I1` `:J1` | a lost `:f1` takes the restart branch, whose `:G1` a running axis refuses (logged only); silently does nothing while CPR or the timer frequency is 0 |
| `SetTrackingAsync(false)`, `AbortSlewAsync`, `SyncRaDecAsync`, `CameraSnapAsync`, guide-rate setters | `:K1`; `:L1` `:L2`; `:E1` `:E2`; `:O1`; `:P1` (`:P2` for Dec) | best-effort: logged, returns; a sync updates the cache as though it took |
| `GetRightAscensionAsync`, `GetDeclinationAsync`, `GetTargetRightAscensionAsync`, `GetTargetDeclinationAsync`, `GetHourAngleAsync` | `:j1` / `:j2` (the target getters read the current position) | **Violation.** The cached steps of the last good read, converted at the current LST (a stale RA also drifts); 0 steps if never read. A non-hex `=` reply throws `FormatException`, not transient. Fix: throw `IOException` when the reply does not parse |
| `GetAxisPositionAsync` | `:j` | **Violation.** `null`, the interface's "no encoder data". Fix: throw instead |
| `GetAxisAngleAsync` | `:j` | **Violation.** `null`, the "no axis model" answer, so the limit falls back to the hour-angle tier on stale positions. Fix: throw, keeping `null` for alt-az and CPR 0 |
| `GetSideOfPierAsync` | `:j2` | **Violation.** `PointingState.Normal`, a definite and MEASURED answer the flip decision and the verified-pointing latch trust. Fix: throw instead |
| `SetSideOfPierAsync` | `:j2`, `:j1`, `:j2`, one goto pass | inherits the `Normal` fallback: can skip a needed flip or flip the other way |
| `BeginSlewRaDecAsync`, `ParkAsync`, `MoveAxisAsync` | stop-waits, `:j`, `:G` `:H` `:M` `:J`; `MoveAxisAsync` `:G` `:I` `:J` (rate 0: `:K`) | **Violation, via `:f`.** A lost `:f` ends a stop-wait early, the next `:G` is refused `!2` and only logged, and the move is lost silently |
| `StartPulseGuideAsync` | as tabled above | start commands best-effort; restore and stop verified, then `SkywatcherDriverException`; its `:f1` inherits the `IsTrackingAsync` violation |
| `IsPulseGuidingAsync` | none | the in-flight counter; re-throws a parked fault |
| `DestinationSideOfPierAsync`, `GetSiderealTimeAsync`, `GetWormPeriodStepsAsync`, guide-rate getters, site, time, `AtParkAsync`, `AtHomeAsync` | none | host clock and cached values; `GetWormPeriodStepsAsync` is 0 when `:s` went unanswered at connect |
| `Get/SetRightAscensionRateAsync`, `Get/SetDeclinationRateAsync` | none | getters 0; setters throw `InvalidOperationException` |

`TrackingSpeeds` lists Sidereal, Lunar and Solar, but `SetTrackingSpeedAsync` throws `ArgumentException`
for anything but Sidereal and `GetTrackingSpeedAsync` always answers Sidereal. In alt-az (`?alignment=AltAz`,
report-only, [`altaz-mount-support.md`](../plans/altaz-mount-support.md)) an RA/Dec slew, starting
tracking, a sync and `SetSideOfPierAsync` throw `NotSupportedException`.

## Discovery

**Serial:** two `ISerialProbe`s with one handshake (`SkywatcherSerialProbeBase`): `SkywatcherSerialProbeUsb`
at 115200 and `SkywatcherSerialProbeLegacy` at 9600, `Shared`, `CarriageReturnTerminated`, a 300 ms budget
and one attempt. Each writes `:e1` and matches any reply `TryParseFirmwareResponse` accepts, then publishes
`Mount://SkywatcherDevice/Skywatcher_<model>_<fw>_<port>?port=<port>&baud=<baud>#Skywatcher <model> (FW <fw>)`.
`SerialProbeService` runs the 9600 group before the 115200 one (`BaudSortOrder`), on its own connection
with synchronous reads. **WiFi:** `SkywatcherDeviceSource.DiscoverWiFiAsync` broadcasts `:e1\r` to
255.255.255.255:11880 and collects replies for 2 s; each responder becomes a device whose `port` is its IP
address. `AddSkywatcher()` registers the source (which `ConsumesSerialProbe`) and both probes. The Synta
handshake names the model and the firmware and nothing else, so the port (or IP address) is the identity.

## The fake

`FakeDevice` builds `FakeSkywatcherMountDriver` over a fresh `FakeSkywatcherSerialDevice` for a fake mount
URI with `port=SkyWatcher` (`SessionTestHelper` `mountPort: "SkyWatcher"`). The serial fake:

- answers as an EQ6 (model 0x00), firmware 3.39, CPR 9,024,000 on both axes, timer 1,500,000, high-speed
  ratio 16, worm 50,133 steps, `:q` `060000`; also `:S`, `:d`, `:D` and `:W`, which GSS sends and the
  driver does not. An unknown letter fails the WRITE, which the driver turns into `InvalidOperationException`;
- integrates motion on a 50 ms timer and again at the head of every command, so a sub-tick pulse still
  moves; gotos run at 3 deg/s; an RA goto that arrives **resumes sidereal tracking** in the direction of
  the `:G` hemisphere bit (the assumption in bench #638), a Dec goto stops;
- refuses `:G` on a running axis with `!2`, so every test enforces stop-then-`:G`;
- delays "running" after `:J` by `slewStartLatencyMs` (the grace test);
- takes `InjectCommandFault(cmd, axis, occurrences, response, skipFirstMatches)`: the next matching
  commands answer `response`, or nothing when it is `null`, and do not reach the state machine. With
  `null` it is the per-command lost-reply knob #810 asks for; note an empty buffer reads `null` at once,
  where hardware would wait on the transport;
- records the last 4096 commands (`CommandLogSnapshot`) for wire assertions.

`FakeSkywatcherMountDriver` leaves the wire untouched. Its believed pointing is the base driver's; the TRUE
pointing the fake camera renders (`GetTruePointingNativeAsync`) adds polar misalignment and the disturbance
terms of [`fake-disturbance-model.md`](fake-disturbance-model.md), and it overrides `SyncRaDecAsync` and
`BeginSlewRaDecAsync` only to re-anchor that model. Because the fake executes whatever step targets it is
given and reports the driver's own model back, it cannot catch an error in the axis convention itself.
Tests: `SkywatcherProtocolTests`, `SkywatcherGssOracleTests`, `SkywatcherPulseRestoreTests`,
`SkywatcherGuideRateTests`, `SkywatcherSerialProbeTests`, `FakeSkywatcherMountDriverTests`.

## Known issues

- **The #810 violations tabled above.** The worst are `QueryAxisStatusAsync`'s "stopped" fallback (it
  feeds `IsSlewingAsync`, `IsTrackingAsync` and every stop-wait) and `GetSideOfPierAsync`'s `Normal`.
- **No read timeout on either transport, and the verified commands run under `CancellationToken.None`.**
  By reading the code, a board that is alive but never answers one of them holds that read and the port
  lock indefinitely, rather than failing into the three-attempt retry; the fake answers at once, so
  `SkywatcherPulseRestoreTests` cannot see it.
- **Nothing pairs a reply with its command**, and nothing drains the input before a command: a reply that
  arrives after its read gave up is taken as the next command's answer.
- **A half-initialised connect succeeds.** Only `:e1` gates it; a lost `:a` or `:b` leaves CPR or the timer
  at 0, after which slews throw `InvalidOperationException` and `SetTrackingAsync(true)` does nothing.
- **Two stop-waits time out silently**: `StopAxisAndWaitAsync` and the Dec micro-goto wait log after 3.5 s
  and return as though stopped (left deliberately, finding 3 of the audit).
- **`CanSetSideOfPier` is false although `SetSideOfPierAsync` is implemented**, so a caller that honours
  the capability never uses it (#636).
- **Park** drives to step 0 from cached positions and reads parked before it arrives; on a board that
  resumes tracking after a goto (#638), as the fake does, RA tracks away from the park position.
- **Unverified axis convention:** the driver takes step 0 as hour angle 6 h at the pole, yet the pole sync
  declares the powered-on position to be hour angle 0, which writes the RA encoder a quarter turn from 0
  (-CPR/4 in the north). Whether the two agree on a real mount is open until #635.
- **Parsed and unused:** `_capabilities` (`:q`) and `_supportsAdvancedCommands`; the `:X` set, pPEC and
  home sensors were not audited ([`gss-parity-audit.md`](../plans/gss-parity-audit.md), "Not audited").
- **24-bit wrap:** a position crossing +/-0x800000 decodes as a jump of 16,777,216 steps, as in GSS; the
  safety limits are what keep an axis away from it (the audit's verdict on GSServer's own rollover
  commit, `6e6dba9`).
- **The device id embeds the firmware version**, so a firmware update gives the mount a new id; and
  `NativeDriverBlacklist` has no Synta entry, so an EQMOD or GSS ASCOM driver for the same mount stays
  listed beside the native one.
