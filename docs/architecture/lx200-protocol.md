# Meade LX200: serial protocol

Reference for TianWen's native Meade LX200 mount driver. `MeadeLX200ProtocolMountDriverBase<TDevice>`
(`src/TianWen.Lib/Devices/MeadeLX200ProtocolMountDriverBase.cs`) implements `IMountDriver` over the classic
LX200 `:XX#` ASCII command set. `MeadeLX200ProtocolMountDriver` (`src/TianWen.Lib/Devices/Meade/`) is its empty
production subclass, built by `MeadeDevice` (`Mount://MeadeDevice/...`) and registered with the probe by
`AddMeade()` (`MeadeServiceCollectionExtensions`); `FakeMeadeLX200ProtocolMountDriver` is its empty test
subclass over `FakeMeadeLX200SerialDevice` (`src/TianWen.Lib/Devices/Fake/`). The class comment says it was
developed against an LX85. OnStep derives from this base (`OnStepMountDriver<TDevice>`) and documents what it
adds or overrides in [`onstep-protocol.md`](onstep-protocol.md); everything it inherits is described here.

> **Derived from the code, not from Meade's documentation.** The driver was hand-written (against an LX85, per
> its class comment) before the repo had protocol docs or plans, nothing in the repo quotes Meade's command
> reference, and no bench session is recorded. Every command,
> reply and parse below is what the code sends and expects. Where it relies on mount behaviour, the text says
> what the code **assumes**; those assumptions stay unverified until a real mount is exercised.

## Serial parameters

| Parameter | Value |
|---|---|
| Baud rate | 9600 (the `DeviceBase.ConnectSerialDeviceAsync` default); the URI's `baud` key overrides it for the driver, the probe is fixed at 9600 |
| Port | the URI's `port` key, opened only when it starts with `serial:` or `COM`, or its last path segment starts with `tty`, through `IExternal.OpenSerialDeviceAsync`, which hands back an already-open connection to the same address |
| Line settings | .NET `SerialPort` defaults (8 data bits, no parity, 1 stop bit, no handshake); `SerialConnection` sets none |
| DTR / RTS | not asserted: the base never asks for `assertControlLines` |
| Encoding | Latin1 on the driver's connection (the base's `_encoding`), so the degree byte 0xDF survives; ASCII in `MeadeSerialProbe` |
| Write bound | 2 s, twice: the port's `WriteTimeout` and a task deadline (`SerialConnectionBase.WriteTimeoutMs`) |
| Read bound | none. The driver never sets `SynchronousReads`, and the async path sets no `ReadTimeout`, so a silent mount holds a read until the caller's token ends it (#810) |
| Close bound | 2 s (`SerialConnection.CloseTimeoutMs`), then the handle is abandoned |

## Framing

- **Command:** `:` + body + `#`, added by the private `SendAsync`; the command constants hold only the body
  (`GR` goes out as `:GR#`). `SendAsync` throws `InvalidOperationException` when the driver is not
  `Connected`, and when the transport reports a failed write ("Failed to send raw message").
- **One exchange at a time:** every helper holds the connection's lock (`ISerialConnection.WaitAsync`) from
  the write to the last read. `SetUTCDateAsync` and the `:MS#` step of `BeginSlewRaDecAsync` hold it across
  several writes and reads.
- **Terminated reply:** read up to `#` or a NUL byte (the base's `Terminators`), terminator stripped.
  `SendAndReceiveAsync` allows 128 bytes (`SerialConnectionBase.MaxTerminatedResponseBytes`);
  `SendAndReceiveRawAsync` reads into a 10-byte buffer (`:D#`, `:GL#`, `:GS#`, `:Gt#`, `:Gg#`). A reply whose
  terminator does not arrive within the buffer is refused (-1), never truncated, and its tail stays in the
  stream.
- **Fixed-length reply:** `SendAndReceiveExactlyAsync` reads an exact count with no terminator: 3 bytes for
  `:GW#`, 1 for `:Sr`, `:Sd` and `:MS#`. A `#` after such a reply would stay in the stream and be read by the
  next terminated read as an empty reply, and the TODO in `AlignmentDetailsAsync` ("LX800 fixed GW response
  not being terminated") says at least one firmware now sends it.
- **No reply read:** `:AP#`, `:AL#`, `:TQ#`, `:TL#`, `:Q#`, `:U#`, `:hP#`, `:Mg`. The code assumes the mount
  answers nothing.

## Get commands

| Command | Reply the code expects | Parse | Member |
|---|---|---|---|
| `:GVP#` | product name, terminated | stored for `DriverInfo` / `Description` | `InitDeviceAsync`, the probe |
| `:GVN#` | firmware / product number, terminated | stored | `InitDeviceAsync`, the probe |
| `:GR#` / `:Gr#` | `HH:MM:SS` (high precision) or `HH:MM.T` (low; T is tenths of a minute) | `HmsOrHmTToHours`: exactly one `.` is the low form, anything else goes to `HMSToHours`. The form also picks the format `:Sr` is written in | `GetRightAscensionAsync` / `GetTargetRightAscensionAsync` |
| `:GD#` / `:Gd#` | `sDD<0xDF>MM` or `sDD<0xDF>MM:SS` | 0xDF replaced by `:`, then `DMSToDegree`; 7 characters or more is high precision | `GetDeclinationAsync` / `GetTargetDeclinationAsync` |
| `:GS#` | `HH:MM:SS` | `Utf8Parser` `TimeSpan`, folded into 24 h | `GetSiderealTimeAsync` |
| `:GW#` | 3 bytes, unterminated: mode, tracking, stars | `A` AltAz, `P` Polar, `G` GermanPolar, else throws; `T` is tracking, any other byte is not; `1` / `2` stars, else 0 (read by nothing) | `GetAlignmentAsync`, `IsTrackingAsync` |
| `:D#` | `#` alone when idle; a first byte of `\|` or 0x7F while slewing | first byte only | `IsSlewingAsync` |
| `:GT#` | tracking frequency, Hz | invariant `double`: 59.9 to 60.1 is sidereal, 57.3 to 58.9 lunar, anything else `None` | `GetTrackingSpeedAsync` |
| `:GC#` | `MM/dd/yy` | `DateTime.TryParseExact`, invariant | `TryGetUTCDateFromMountAsync` |
| `:GL#` | `HH:MM:SS` | as `:GS#` | `TryGetUTCDateFromMountAsync` |
| `:GG#` | `sHH` or `sHH.H`, the hours to add to local time for UTC (the code's comment) | `double.TryParse` in the CURRENT culture | `TryGetUTCDateFromMountAsync`, `SetUTCDateAsync` |
| `:Gt#` | `sDD<sep>MM`, 5 bytes or more | `GetLatOrLongAsync`: degrees, skip exactly one separator byte of any value, minutes; a result below -180 gets 360 added | `GetSiteLatitudeAsync` |
| `:Gg#` | as `:Gt#`, degrees WEST positive | as `:Gt#`, then negated | `GetSiteLongitudeAsync` |

## Set and action commands

| Command | Sent by | Reply read | Accepted when |
|---|---|---|---|
| `:SrHH:MM:SS#` / `:SrHH:MM.T#` | `SetTargetRightAscensionAsync` (private), after a `:GR#` to learn the precision; RA must be in [0, 24) | 1 byte | `1`; anything else throws `InvalidOperationException` |
| `:SdsDD*MM#` / `:SdsDD*MM:SS#` | `SetTargetDeclinationAsync` (private), after a `:GD#`; a sign only when negative | 1 byte | `1`, as above |
| `:MS#` | `BeginSlewRaDecAsync`, after `:Sr` and `:Sd` | 1 byte; after a digit other than `0`, a terminated message | `0` is slewing. `1` "below horizon limit", `2` "above hight limit" (sic), other digits "unknown reason", all thrown as `InvalidOperationException`; no byte, a non-digit or no message: "unrecognized response" |
| `:CM#` | `SyncRaDecAsync`, after `:Sr` and `:Sd` | terminated | any non-empty reply; the content is not checked |
| `:Q#` | `AbortSlewAsync`, only when `:D#` reads slewing | none | |
| `:AP#` / `:AL#` | `SetTrackingAsync(true)` / `(false)` | none | |
| `:TQ#` / `:TL#` | `SetTrackingSpeedAsync`: `:TQ#` for sidereal AND solar, `:TL#` for lunar; other speeds throw `ArgumentException` | none | |
| `:Mg<d><ms>#` | `StartPulseGuideAsync`; `<d>` is `n` `s` `e` `w`, `<ms>` is formatted `0000`, so a pulse over 9999 ms throws `ArgumentException` | none | |
| `:hP#` | `ParkAsync` (virtual) | none | |
| `:U#` | `TrySetHighPrecisionAsync` at connect | none | the code assumes it TOGGLES the precision, so it re-reads `:GR#` after each |
| `:SLHH:mm:ss#`, then `:SCMM/dd/yy#` | `SetUTCDateAsync`: local time is UTC minus the `:GG#` offset | terminated, each | anything but exactly `1`, which throws `ArgumentException`. Two more terminated reads then discard what the code's comment calls "Updating Planetary Data#" and a blank line; `TimeIsSetByUs` is whether both arrived |
| `:StsDD*MM#` | `SetSiteLatitudeAsync` | terminated | `1`; but this command never leaves the driver (**Known issues**) |
| `:SgDDD*MM#` | `SetSiteLongitudeAsync`; an east (positive) longitude is written as `360 - degrees` with the minutes unchanged | terminated | `1` |

The code reads `1` as FAILURE for `:SL` and `:SC` (the fake answers `0` for success), and reads the acks of
`:St`, `:Sg` and `:CM#` as terminated replies where `:Sr` and `:Sd` take one bare byte. Neither the polarity
nor the termination is verified against a real mount.

## Connect handshake

`DeviceDriverBase.ConnectAsync` runs `DoConnectDeviceAsync`, then `InitDeviceAsync`:

1. Open the port (above). An exception is logged and the connect fails ("Could not connect to device").
2. `:GVP#`, then `:GVN#`; a missing reply to either, or an exception here or in step 3, makes
   `InitDeviceAsync` return `false`. Unlike discovery, the driver checks no product name, so a manually
   assigned port connects to any LX200 responder.
3. `TrySetHighPrecisionAsync`: read `:GR#`; while it is the low-precision form, send `:U#` and read again, at
   most three reads. Still low is only a warning: `:Sr` and `:Sd` follow whatever form `:GR#` / `:GD#` answer.

Nothing else is read or set at connect: no time, no site, no tracking state.

## Side of pier is computed, not read

The classic command set has no pier-side query, so `CalculateSideOfPierAsync` derives one from the hour
angle, `ConditionHA(LST - RA)` with LST from `:GS#`: HA of 0 or more (west of the meridian) is `Normal`, HA
below 0 (east) is `ThroughThePole`, NaN is `Unknown`. Declination and hemisphere are not consulted.
`GetSideOfPierAsync` applies it to the current `:GR#`, `DestinationSideOfPierAsync` to the destination RA, and
`PointingStateSource` answers `Computed`. It is the side the mount would be on if its firmware flipped exactly
at the meridian (the XML comment's assumption), which means for a caller:

- **The side changes when the POINTING crosses the meridian, whether or not the tube moved.** A mount that
  tracks through without flipping reads `ThroughThePole`, then `Normal`, all the same, so a pier-side change is
  never evidence of a flip. The session reads flip success off the image instead
  (`MeridianFlipVerification.FromSolves`; `docs/plans/meridian-flip-verification.md`).
- **A safety limit does not trust it.** `MountLimits.TrustedPointingState` turns a `Computed` state into
  `Unknown`, the hour-angle tier, or into the session's latched `_verifiedPointingState` when it has one
  (`docs/plans/mount-safety-limits.md`).
- **The flip decision compares two computed sides**, current and destination, made by the same rule.
- **`SyncRaDecAsync` refuses only `Unknown`**, which here means a NaN hour angle; its XML comment's "does not
  allow sync across the meridian" is enforced by nothing.

There is no mechanical tier either: `GetAxisPositionAsync`, `GetAxisAngleAsync` and `GetWormPeriodStepsAsync`
keep the `IMountDriver` defaults (`null`, `null`, `0`). OnStep replaces `GetSideOfPierAsync` with `:Gm#` and
answers `Measured`; its `DestinationSideOfPierAsync` stays the computed one above.

## Mapping to `IMountDriver`

The rule from #810 (and `driver-resilience.md`) is that a CONNECTED read with no valid reply throws a transient
exception. `ResilientCall.IsTransient` (`src/TianWen.Lib/Sequencing/ResilientCall.cs`) accepts I/O, socket,
disposed-object, timeout and COM exceptions but **not `InvalidOperationException` or `FormatException`**, so
every throw below gets no retry, no reconnect and no fault count. "Missing" includes a read the caller's own
token ended: the transport's read helpers swallow that cancellation and report no reply. **#810** marks a
violation; `InvalidOperation` is short for `InvalidOperationException`.

| Member | Wire | Not connected | Connected: reply missing or timed out | Connected: reply does not parse |
|---|---|---|---|---|
| `CanSetTracking`, `CanPark`, `CanSlewAsync`, `CanSync`, `CanPulseGuide`, `CanPulseGuideSimultaneously` | `true` | | | |
| `CanSetSideOfPier`, `CanSlew`, `CanMoveAxis`, `CanSetGuideRates`, the two rate flags; `CanSetPark`, `CanUnpark` (virtual) | `false` | | | |
| `TrackingSpeeds` (virtual), `EquatorialSystem` | sidereal and lunar; `Topocentric` | | | |
| `DriverInfo`, `Description` | `:GVP#` / `:GVN#` from connect, `Unknown` before | | | |
| `GetTrackingSpeedAsync` | `:GT#` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `InvalidOperation` (**#810**, wrong type); a rate outside both bands is `None` |
| `SetTrackingSpeedAsync` (virtual) | `:TQ#` / `:TL#` | `InvalidOperation` | no reply is read, so a lost command is invisible | n/a |
| `GetAlignmentAsync` | `:GW#`, byte 1 | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `InvalidOperation` for a byte other than `A` `P` `G` (**#810**, wrong type) |
| `IsTrackingAsync` (virtual) | `:GW#`, byte 2 | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `false` for any byte but `T` (**#810**) |
| `SetTrackingAsync` (virtual) | pulse check, `:D#`, then `:AP#` / `:AL#` | `InvalidOperation` | `:D#` reads "not slewing" (**#810**) and the command goes out unacknowledged | as missing |
| `IsSlewingAsync` (virtual) | `:D#` | `false` | `false`, "not slewing" (**#810**) | `false` for any first byte but `\|` or 0x7F, and for a reply over 9 bytes (**#810**) |
| `IsPulseGuidingAsync` | none: true until the end tick `StartPulseGuideAsync` set | the local clock | n/a | n/a |
| `AtHomeAsync`, `AtParkAsync` (virtual) | none: always `false` | | | |
| `TryGetUTCDateFromMountAsync` | `:GC#`, `:GL#`, `:GG#`; date + time + offset, kind UTC | `null` | `InvalidOperation` (**#810**, wrong type) | `InvalidOperation` (**#810**, wrong type) |
| `SetUTCDateAsync` | `:GG#`, `:SL`, `:SC`, two discard reads | `InvalidOperation` (`:GG#` is read before the guard) | `:GG#`: `InvalidOperation` (**#810**, wrong type); a missing ack is taken as success (**#810**) | an ack of exactly `1`: `ArgumentException`. A `DateTime` whose kind is not UTC is ignored silently |
| `GetSiderealTimeAsync` | `:GS#` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `InvalidOperation` (**#810**, wrong type) |
| `GetRightAscensionAsync`, `GetTargetRightAscensionAsync` | `:GR#` / `:Gr#` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `NaN` (**#810**); `FormatException` when the reply has one `.` and a non-numeric tail |
| `GetDeclinationAsync`, `GetTargetDeclinationAsync` | `:GD#` / `:Gd#` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `NaN` (**#810**), also for a separator other than 0xDF |
| `GetHourAngleAsync` | `:GS#`, `:GR#` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `:GS#`: `InvalidOperation`; `:GR#`: `NaN` (**#810**) |
| `GetSideOfPierAsync` (virtual) | `:GR#`, `:GS#`, derived | `InvalidOperation` (the `:GR#` read comes before `CheckPointingStateAsync`'s `Unknown`) | `InvalidOperation` (**#810**, wrong type) | `:GR#`: `Unknown` (**#810**); `:GS#`: `InvalidOperation` |
| `DestinationSideOfPierAsync` (virtual) | `:GS#`, derived | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `InvalidOperation` (**#810**, wrong type) |
| `PointingStateSource` (virtual) | `Computed` | | | |
| `GetSiteLatitudeAsync`, `GetSiteLongitudeAsync` | `:Gt#` / `:Gg#` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `InvalidOperation` (**#810**, wrong type) |
| `SetSiteLatitudeAsync` | nothing is ever sent | `InvalidOperation`, "formatting error", before any I/O | the same | the same |
| `SetSiteLongitudeAsync` | `:Sg` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | `InvalidOperation` for any reply but `1` |
| `GetSiteElevationAsync` (`SetSiteElevationAsync` stores nothing), the two rate getters, the two guide-rate getters | none: `NaN`; `0`; 2/3 sidereal in degrees per second (`DEFAULT_GUIDE_RATE`) | | | |
| `SetSideOfPierAsync`, `MoveAxisAsync`, `UnparkAsync` (virtual), the four rate setters | none: always `InvalidOperation` | | | |
| `ParkAsync` (virtual) | `:hP#` | `InvalidOperation` | not read | n/a |
| `StartPulseGuideAsync` | `:GW#` tracking check, end tick, `:Mg` | `InvalidOperation` | `:GW#`: `InvalidOperation` (**#810**, wrong type); `:Mg` is not acknowledged | a `:GW#` byte 2 other than `T`: `InvalidOperation`, "tracking is off" |
| `BeginSlewRaDecAsync` | pulse check, `:D#`, `:GR#` + `:Sr`, `:GD#` + `:Sd`, `:MS#` | `InvalidOperation` | `:D#` reads "not slewing" and the slew proceeds (**#810**); any later reply: `InvalidOperation` (**#810**, wrong type) | a garbage `:GR#` reads as high precision; a refused ack: `InvalidOperation` |
| `AbortSlewAsync` | pulse check, `:D#`, `:Q#` only while slewing | returns, having sent nothing | `:D#` reads "not slewing", `:Q#` is never sent, and the call returns normally (**#810**) | as missing (**#810**) |
| `SyncRaDecAsync` | side of pier, `:Sr`, `:Sd`, `:CM#` | `InvalidOperation` | `InvalidOperation` (**#810**, wrong type) | a garbage `:GR#` gives `Unknown`, then `InvalidOperation`; an empty `:CM#` reply throws |
| `TimeIsSetByUs` | set by `SetUTCDateAsync` | | | |

`StartPulseGuideAsync` raises the end tick BEFORE it writes `:Mg`, so `IsPulseGuidingAsync` is already true
when the starter returns, as `IMountDriver` requires; it also stays true for the duration when the write
failed. "Not connected" departs from #810 as well: most reads throw where #810 keeps a neutral value, and only
`IsSlewingAsync` and `TryGetUTCDateFromMountAsync` answer one (`MeadeLX200BasedMountTests` pins the throw from
`GetSiderealTimeAsync` after a disconnect). The interface's `WaitForSlewCompleteAsync` polls every 251 ms, and
each poll here is four exchanges (`:D#`, `:GC#`, `:GL#`, `:GG#`).

## Discovery

`MeadeSerialProbe` (`ISerialProbe`): name `Meade`, 9600 baud, ASCII, `ProbeFraming.HashTerminated`,
`ProbeExclusivity.Shared` (one handle with the other 9600-baud probes), a 500 ms budget for the whole exchange
(doubled on the second pass), one attempt. `SerialProbeService` reads through the synchronous path while
probing (`SynchronousReads = true`) and drains the receive buffer before and after each probe. The probe calls
`MeadeDeviceSource.TryGetMountInfo`, which holds the connection lock throughout and terminates on `#` alone:

1. `:GVP#`: a reply of 2 characters or more once trailing whitespace is trimmed, else no match.
2. `:GVN#`.
3. `:GP#`, `:GO#`, `:GN#`, `:GM#` (site slots 4 down to 1). A 15-character name starting `TW@` carries a
   TianWen id (the rest of the name). A slot reading `<AN UNUSED SITE>` while no id is known yet gets one:
   `:S<slot>TW@<12 base64url characters>#` (9 random bytes), kept on a 1-byte `1`. Any other non-empty name is
   recorded as a site.

`MeadeSerialProbe.ProbeAsync` then requires the `:GVP#` reply to match `^(?:LX|Autostar|Audiostar)`
(`MeadeDeviceSource.SupportedProductsRegex`) and publishes
`Mount://MeadeDevice/<id>?port=<port>#<name> (<number>) on <port>`. The id is `<name>_<number>_<uuid>`, or with
no TianWen id `<name>_<number>_<sites joined by ,>_<port>`, each part through `SafeName` (`_`, `/` and `:`
become `-`). The written id is what keeps the device the same across a USB re-enumeration, so **discovery
writes to the mount**. `MeadeDeviceSource.DiscoverAsync` only turns the service's `Meade` matches into
`MeadeDevice`s (`ConsumesSerialProbe`), and `MatchesDeviceHosts` lets the probe verify a pinned `MeadeDevice`
port. The pass structure is the probe service's: `docs/plans/serial-probe.md`.

## The fake

`FakeDevice` with `port=LX200` builds `FakeMeadeLX200ProtocolMountDriver` over `FakeMeadeLX200SerialDevice`,
which is also the serial device `FakeDevice` hands any mount port it has no other fake for. It simulates:

- A German equatorial with an HA axis (hours) and a Dec axis (degrees), starting at home (HA axis 6 h, Dec axis
  at the pole) and NOT tracking; a 50 ms timer advances the HA axis at the sidereal rate while tracking.
- `:MS#`: a bare `1` (no message, so the base reports "unrecognized response: 1") for a target at or below the
  horizon, else `0`, tracking ON (its comment: an LX85 starts tracking on the first slew) and both axes at
  1.5 degrees/s. It goes through the pole for an eastern target whose Dec is on the pole's side of the equator,
  so for an eastern target on the other side it is mechanically `Normal` where the base computes
  `ThroughThePole`; that mechanical state never crosses the LX200 wire.
- `:CM#` moves the axes onto the target on the current side (`Synced#`); `:Q#` stops a slew; `:D#` answers
  0x7F `#` while slewing, `#` otherwise; `:Mg` moves the axis at once by 2/3 sidereal times the duration.
- `:U#` toggles the precision, starting LOW; coordinates answer in the current precision (Dec with 0xDF), and
  `:Sr` / `:Sd` accept only its format (0xDF, `*` or 0xB0 as the degree mark), answering `1` or `0`.
- `:GS#` from SOFA for the URI's site; `:Gt#` / `:Gg#` from its `latitude` / `longitude` (`:Gg#` negated);
  `:GG#` always `+00`; `:GL#` / `:GC#` from the injected `ITimeProvider`; `:GT#` 60.1 after `:TQ#`, 57.1 after
  `:TL#`; `:GVP#` `Fake LX200 Mount`, `:GVN#` `A4s4`, `:GW#` `GT0` or `GN0`.

Simpler than a real mount: every reply is already in memory, so a read never waits and a missing reply returns
at once rather than at the caller's token, and there is no lost-reply knob (#810 asks for one). `:SL` answers an
unterminated `0`, which the fake's terminated read swallows as no reply; the date `:SL` / `:SC` set is never
read back, and `TimeIsSetByUs` stays `false` (the ack read takes the first line of `:SC`'s block and the second
discard read finds nothing). `:hP#`, `:St`, `:Sg` and the site-name commands are not implemented, so their write
fails and `ParkAsync` / `SetSiteLongitudeAsync` throw "Failed to send raw message". `Fake LX200 Mount` fails the
probe's regex, so the fake is reached only through `FakeDevice`. `MeadeLX200BasedMountTests` pins connect,
alignment, tracking, a completed slew and disconnect; none of its slews checks where the mount ended up.

## Known issues

- **#810, reads that invent a value or throw the wrong type** (the Mapping table). One line each:
  `IsSlewingAsync` should throw `IOException` when connected and no terminated reply arrived (today a lost `:D#`
  ends `WaitForSlewCompleteAsync` early, lets `SetTrackingAsync` and `BeginSlewRaDecAsync` through, and turns
  `AbortSlewAsync` into a silent no-op); every missing-reply `InvalidOperationException` should be an
  `IOException`; a `NaN` from `HMSToHours` / `DMSToDegree` should throw `IOException`; `IsTrackingAsync` should
  throw on a byte other than `T` or `N` (the fake's two); a missing `:SL` / `:SC` ack should throw rather than
  pass. The session also reads `IsSlewingAsync` through `CatchAsync(..., false)` in three places (#810).
- **`SetSiteLatitudeAsync` never sends anything.** Its buffer is `offset + 1 + 2` bytes (5, or 6 with a sign)
  for a 7-byte body (`St`, `DD`, `*`, `MM`), so the minutes never fit and every latitude throws "formatting
  error". A run whose request names a site reaches it through `Session.SettleSiteAsync` and
  `MountSiteExtensions.SetSiteAsync`, in initialisation with no catch, so the run fails there (#837).
- **Angles of 24 degrees or more are written modulo 24.** `SetTargetDeclinationAsync`, `SetSiteLatitudeAsync`
  and `SetSiteLongitudeAsync` format the `Hours` component of `TimeSpan.FromHours(degrees)`, which is the hour
  of the day, 0 to 23: Dec -45.125 goes out as `:Sd-21*08#`, so a goto or sync beyond 24 degrees either side of
  the equator lands on the wrong declination. RA is folded into 24 h first and is unaffected.
  `MeadeLX200BasedMountTests` slews to -45.125 and asserts only that the slew finishes. The root cause is the
  `TimeSpan` itself, so the fix is one sexagesimal formatter for every angle (#837).
- **Longitude does not round-trip.** `GetLatOrLongAsync` applies its 360-degree wrap BEFORE
  `GetSiteLongitudeAsync` negates, so the case its own comment describes (214 from the mount meaning 146 east)
  reads back as -214. `SetSiteLongitudeAsync` keeps the minutes when it writes `360 - degrees`, so 16 degrees
  18 minutes east goes out as `:Sg344*18#` (344.3 west) where the read side would expect 343.7 (#837, to
  re-verify during the fix).
- **`:GG#` is parsed in the current culture** (`GetUtcCorrectionAsync`), unlike every other number here, so a
  fractional offset such as `-05.5` misreads where `.` is a group separator. The fake answers whole hours only.
- **The fake's lunar rate is outside the base's band** (57.1 Hz against 57.3 to 58.9), so lunar reads back
  `None` on the fake and `EnsureTrackingAsync(TrackingSpeed.Lunar)` re-sends it every call. Solar goes out as
  `:TQ#`, sidereal.
- **`AtParkAsync` is always `false`** while `CanPark` is `true`, so `Session.Finalise` polls it 1000 times at
  100 ms after `:hP#` before calling the park incomplete.
- **A failed init leaves the driver `Connected`.** `InitDeviceAsync` catches everything and returns `false`;
  `DeviceDriverBase.TrySetConnectionStateAsync` has already set the state to connected and reverts it only when
  init THROWS, so `ConnectAsync` throws while `Connected` reads `true`, and the next `ConnectAsync` returns at
  once without re-running init. The base's bug, not this driver's; it is on #806.
- **The async read can swallow the next line.** `SerialConnectionBase.TryReadTerminatedRawAsync` reads with
  `ReadAtLeastAsync`, which may return bytes past the terminator, and drops them, so a multi-line reply arriving
  in one chunk (the `:SC` block) would leave `SetUTCDateAsync`'s discard reads waiting for the caller's token.
  And nothing discards a late reply: a read abandoned on the token leaves its reply to answer the next command.
- **Discovery writes to mounts it then rejects.** `TryGetMountInfo` writes the `TW@` id into an unused slot
  before `ProbeAsync` checks the product regex, so any device on the shared 9600 handle that answers
  `<AN UNUSED SITE>` gets a site name. `OnStepDeviceSource` carries a copy of the same writing code.
- **Bench validation is outstanding** for every "the code assumes" above, above all the acks of `:SL`, `:SC`,
  `:St`, `:Sg` and `:CM#`, the termination of `:GW#`, the bytes of `:D#`, the `:MS#` failure message, `:U#`
  toggling, and whether any firmware answers `:GR#` with fractional seconds (one `.` reads as the low form).
