# iOptron SkyGuider Pro (SGP) Serial Protocol

Reference for TianWen's native iOptron SkyGuider Pro driver (`IMountDriver`). The SkyGuider Pro is a
star tracker: it motorises the RA axis only (no Dec motor, no goto), and besides tracking and manual RA
moves it has an ST-4 guide port and a camera shutter-release port. The whole driver is
`SgpMountDriverBase<TDevice>` (`src/TianWen.Lib/Devices/IOptron/SgpMountDriverBase.cs`, a
`DeviceDriverBase` whose `SgpDeviceInfo` holds the open `ISerialConnection`); `SgpMountDriver` binds it
to `IOptronDevice` for real hardware, and `FakeSgpMountDriver` binds it to `FakeDevice` for tests. The
same folder holds `IOptronDevice` (the device URI and the 28800-baud port open), `IOptronSerialProbe`
and `IOptronDeviceSource` (discovery); `AddIOptron()` in
`src/TianWen.Lib/Extensions/IOptronServiceCollectionExtensions.cs` registers them. The serial
configuration and command reference below are the wire format, the sections after them say how the
driver uses it, and the firmware analysis closes the document.

## Serial Configuration
- **Baud rate**: 28800
- **Encoding**: ASCII
- **Terminator**: `#` (hash character)
- **Command prefix**: `:M` (mount command)
- **Response prefix**: `:H` (host response) or `:R` (read response)

### Ports

The command set is iOptron's own and undocumented; it is not the iOptron RS-232 command language of
their goto mounts. Two ports carry it, at 28800 baud, 8-N-1:

- **The HBX port**, through a USB-to-serial cable. Source: the Stargazers Lounge thread
  [Is there a way to control the RA on the SkyGuider Pro with a laptop?](https://stargazerslounge.com/topic/356836-is-there-a-way-to-control-the-ra-on-the-skyguider-pro-with-a-laptop/).
- **The mount's own USB socket**, a Prolific PL2303 bridge (`067B:2303`), which is DUAL USE: firmware
  updates and this control protocol. Measured on the bench on 2026-11-05 (firmware `170518`): discovery
  found the mount on it, and it answered every query and moved and stopped the RA axis.

The firmware has only two UARTs (see Firmware Analysis): 9600 baud between the hand controller and the
head, and 28800 baud for control, so both ports most likely reach the same command parser.

## Command Reference

### Mount Info

| Command | Response | Description |
|---------|----------|-------------|
| `:MRSVE#` | `:RMRVE12xxxxxx#` | Firmware version. `12` identifies SGP, `xxxxxx` is date (YYMMDD) |

### Mount Status

| Command | Response | Description |
|---------|----------|-------------|
| `:MGAS#` | `:HRAS0{MO}{TR}{SP}0{HEM}1{xx}#` | Get axis status (see breakdown below) |

`:HRAS013501105#` field breakdown:
```
:HRAS 01 3 5 0 1 1 05
      ^^ ^ ^ ^ ^ ^ ^^
      |  | | | | | +-- delay trigger? (unknown)
      |  | | | | +---- fixed (always 1?)
      |  | | | +------ hemisphere (1=north, 0=south)
      |  | | +-------- fixed separator (always 0?)
      |  | +---------- speed index (0-7 here; 9 on the bench, see below)
      |  +------------ tracking rate (0=solar, 1=lunar, 2=half-sidereal, 3=sidereal)
      +--------------- motion: 01 at rest, 02 while the RA axis moves
```

The first field is not a fixed prefix, as this table used to say: on the bench of 2026-11-05 (firmware
`170518`) it read `01` at rest and `02` through a move, and stayed `02` for about 1.6 s after a stop at full
speed while the RA servo wound down (`:HRAS023900105#` moving, `:HRAS013900105#` at rest). The same mount read
speed index `9`, outside the seven speeds `:MSMS` sets; what 9 is was not found out.

### Hemisphere

| Command | Response | Description |
|---------|----------|-------------|
| `:MSHE0#` | `:HRHE0#` | Set southern hemisphere |
| `:MSHE1#` | `:HRHE0#` | Set northern hemisphere |

### Tracking Rate

| Command | Response | Description |
|---------|----------|-------------|
| `:MSTR0#` | `:HRTR0#` | Solar tracking |
| `:MSTR1#` | `:HRTR0#` | Lunar tracking |
| `:MSTR2#` | `:HRTR0#` | Half-sidereal tracking |
| `:MSTR3#` | `:HRTR0#` | Sidereal tracking |

### RA Movement

| Command | Response | Description |
|---------|----------|-------------|
| `:MSMR0#` | `:HRMR0#` | Move east |
| `:MSMR1#` | `:HRMR1#` | Move west |
| `:MSMR2#` | `:HRMR2#` | Stop (resume tracking) |

On the bench the acknowledgements did not follow this table: a stop came back as `:HRMR0#` in one run and
`:HRMR1#` in another, and a move's or a stop's acknowledgement arrived late (see "Replies out of step"). So
the driver matches an `:HRMR` reply by its prefix alone. One `:MSMR2#` stops the axis; the status keeps
reporting motion until the servo is at rest.

### Slew Speed

| Command | Response | Description |
|---------|----------|-------------|
| `:MSMS1#` | `:HRMS0#` | 1x sidereal (guide speed) |
| `:MSMS2#` | `:HRMS0#` | 2x sidereal |
| `:MSMS3#` | `:HRMS0#` | 8x sidereal |
| `:MSMS4#` | `:HRMS0#` | 16x sidereal |
| `:MSMS5#` | `:HRMS0#` | 64x sidereal |
| `:MSMS6#` | `:HRMS0#` | 128x sidereal |
| `:MSMS7#` | `:HRMS0#` | 144x sidereal (max) |

Speed multiples of sidereal rate (15.0417 arcsec/sec): 1, 2, 8, 16, 64, 128, 144.

### Guide Rate

| Command | Response | Description |
|---------|----------|-------------|
| `:MGGR#` | `:HRGR{xx}{yy}#` | Get guide rate (RA=xx, DEC=yy, 0-99) |
| `:MSGR{nn}{mm}#` | `:HRGR0#` | Set guide rate (nn=RA 0-99, mm=DEC 0-99) |

Default: `:MSGR5050#` = 50% RA, 50% DEC.

This table used to call `:MGGR` an unterminated command. On the bench `:MGGR#` with its `#` answered
`:HRGR5050#` at once, while `:MGGR` without it was taken together with the next command (`:MGGR:MRSVE#` was
answered `:HRGR5050#`, the firmware query lost). Send it terminated. `:MGEL` is listed unterminated below
for the same reason and was not tried.

### Camera Snap

| Command | Response | Description |
|---------|----------|-------------|
| `:MGCS#` | `:HRCSxyaaaabbbcccdddppkkkkk#` | Get camera settings |
| `:MSCA{y}{aaaa}{bbb}{ccc}#` | `:HRCA0#` | Trigger camera snap |

#### MGCS Response Breakdown

`:HRCS0100300050020000000000#`:
```
:HRCS xy aaaa bbb ccc ddd pp kkkkk
      01 0030 005 002 000 00 00000
      ^^ ^^^^ ^^^ ^^^ ^^^ ^^ ^^^^^
      |  |    |   |   |   |  +-- unknown
      |  |    |   |   |   +----- unknown
      |  |    |   |   +--------- unknown (not sent in MSCA)
      |  |    |   +------------- shot count
      |  |    +----------------- interval
      |  +---------------------- shutter length
      +------------------------- x=unknown, y=active/start flag
```

**Units for shutter and interval are TBD** (possibly seconds).

#### MSCA Command

Only sends `{y}{aaaa}{bbb}{ccc}` (11 chars after `:MSCA`):
- `y` = start flag (1=start)
- `aaaa` = shutter length (4 digits)
- `bbb` = interval (3 digits)
- `ccc` = shot count (3 digits)

Example: `:MSCA10030005002#` = start, shutter=30, interval=5, 2 shots.

### Eyepiece Light

| Command | Response | Description |
|---------|----------|-------------|
| `:MGEL` | `:HREL{x}#` | Get intensity (0-9). **Note: unterminated command** |
| `:MSEL{x}#` | `:HREL{x}#` | Set intensity (x=0-9) |

## Replies out of step

Measured on the bench of 2026-11-05 (firmware `170518`, over the mount's own USB socket, logging every byte
for up to a second after each command):

- **At rest the mount is in step** and sends nothing unasked: each command gets its own reply within about
  25 ms. An unknown command, or a valid one with a stray byte in front of it, gets no reply at all.
- **After a move starts it falls one behind.** The move's `:HRMR0#` came at once AND again with the next
  command, and from then on the mount answered each command with the reply it was holding and held the new
  one. A stop was answered with the previous status, its own `:HRMR` coming with the next command.
- **It stays behind across a port close.** The next session's first `:MRSVE#` was answered with the last
  session's status, so the discovery probe, reading one reply, did not recognise the mount.
- **Waiting never brings the held reply**: only the next command releases it.

So every exchange, the driver's and the probe's, goes through `SgpWire.ExchangeAsync`: it discards what is
already on the wire, sends the command, and takes the reply that carries the command's own prefix (`:MSxx`
and `:MGxx` are answered by `:HRxx`, `:MRSVE` by `:RMRVE`). A reply with another prefix answered an earlier
command, so the command is SENT AGAIN, up to three times. Every command but the camera-snap trigger
(`:MSCA`) can be repeated harmlessly. Once the driver has seen the mount out of step it sends each
repeatable command twice and takes the second reply, so a status it reads is the mount's state now and not
at the previous poll. `SgpOutOfStepReplayTests` replays the bench's sequences; `SgpMountHardwareProbe`
(gated on `TIANWEN_SGP_PORT`) runs the driver against the mount.

## Mapping to `IMountDriver`

Every exchange takes the port's lock (`ISerialConnection.WaitAsync`) and goes through
`SgpWire.ExchangeAsync` (above), which reads `#`-terminated replies through
`ISerialConnection.TryReadTerminatedAsync` (`SgpMountDriverBase.SendAndReceiveAsync` for a read,
`SendCommandAsync` for a command). The transport facts that decide what "no reply" means here (#810):

- **The read has no deadline of its own.** Only a write is bounded (`SerialConnection.WriteTimeout`, 2 s,
  held by Serial.Lib), and `SerialConnection` opens its port with no read deadline, so a mount that never
  answers holds the call until the caller's token fires.
- **The read helper swallows every exception, the caller's cancellation included.**
  `SerialConnection.TryReadTerminatedRawAsync` returns -1 and `TryReadTerminatedAsync` null, exactly as
  for a reply whose `#` is not within `MaxTerminatedResponseBytes`, so the driver cannot tell a lost reply
  from a cancelled one.
- **Input is discarded before each exchange**, and a reply is taken only when its prefix is the command's,
  so a late reply no longer answers the next command.
- **A failed write ends the exchange with no reply**, and a command sent with the port closed throws
  `InvalidOperationException` (`SendCommandAsync`: "Serial port is not connected").
  `ResilientCall.IsTransient` accepts neither (it retries `IOException`, `SocketException`,
  `ObjectDisposedException`, `TimeoutException`, a `TaskCanceledException` wrapping a `TimeoutException`,
  `COMException` and an `AggregateException` whose inner exceptions are all transient), so these faults get
  no retry and no reconnect.

"Not connected" below means the port is not open: the driver tests `_deviceInfo.SerialDevice` for
`IsOpen`, not `Connected`. A **bold** entry breaks the #810 rule that a connected read with no valid reply
throws a transient exception.

### Members that talk to the mount

| Member | Sends | Not connected | Connected, reply missing, late or unparseable |
|---|---|---|---|
| `IsSlewingAsync` | `:MGAS#` through `GetMountStatusAsync`; the motion field (`02` moving), so it stays `true` while the servo winds down after a stop | the last motion read | **the last motion read**, which the connect sets and every valid status updates |
| `GetCameraSnapSettingsAsync` | `:MGCS#`; shutter and interval parsed as whole seconds, then the shot count (`CameraSettingsRegex`) | the cached settings | **the cached settings**: the last `CameraSnapAsync` this driver instance sent, or `null` if none, which reads the same as a real answer |
| `SetTrackingSpeedAsync` | `:MSTR3#` Sidereal, `:MSTR1#` Lunar, `:MSTR0#` Solar; `ArgumentException` for any other speed, before anything is sent | `InvalidOperationException` | **returns normally**: the `:HRTR0#` ack is read and discarded, and the cached speed is updated as if the mount accepted it |
| `SetTrackingAsync` | `true`: the cached speed's `:MSTR` again (through `SetTrackingSpeedAsync`). `false`: nothing, and it returns normally (`CanSetTracking` is `false`) | `true`: `InvalidOperationException`. `false`: returns normally | `true`: **returns normally**, as `SetTrackingSpeedAsync` |
| `MoveAxisAsync` | Primary only; any other axis throws `InvalidOperationException`. Rate 0: `:MSMR2#`. Otherwise `:MSMS<n>#` then `:MSMR1#` (positive rate, west) or `:MSMR0#` (east) under one lock, `n` being the highest of the seven speeds not above the rate's magnitude (1 below 1x) | rate 0: `InvalidOperationException`. Any other rate: sends nothing, yet returns normally and sets the slewing record | **returns normally**: every ack is matched and discarded, and a move sets the slewing record; a stop leaves it for `IsSlewingAsync` to clear from the mount's status |
| `AbortSlewAsync` | `:MSMR2#` | `InvalidOperationException` | **returns normally**, the stop unacknowledged |
| `SetGuideRateRightAscensionAsync` | `:MSGR<pp>50#`: `pp` is the rate as a whole percentage of sidereal, and the Dec field is always `50`; `ArgumentException` outside 0 to 99 percent, before anything is sent | `InvalidOperationException` | **returns normally**, cached percentage updated |
| `CameraSnapAsync` | `:MSCA1<ssss><iii><nnn>#`: shutter and interval in whole seconds (the unit is unconfirmed, a TODO in the code), then the shot count. The one command never sent twice, since it starts the shutter sequence | `InvalidOperationException` | **returns normally**, settings cached |
| `SetHemisphereAsync` (not on `IMountDriver`; only tests call it) | `:MSHE1#` north, `:MSHE0#` south; sets the modelled Dec to +90 or -90 | `InvalidOperationException` | **returns normally**, Dec updated |

The driver never sends `:MSTR2#` (half-sidereal is not in `TrackingSpeeds`), `:MGGR`, `:MGEL` or `:MSEL`.

### Members the driver models locally

The command set above has no read for position, pier side, park or home, and no sync, goto or pulse
command, so these members never touch the wire and cannot meet a missing reply. They are only as right as
the last connect, sync or setter.

| Member | Returns |
|---|---|
| `IsTrackingAsync` | `true` while connected, without asking: the mount has no tracking-off, a stop resumes tracking. It used to read `:MGAS#` and answer `false` on any reply that did not parse, which was every read during a move (`:HRAS02...`) and every stale reply; two such polls latch `DriverEnforcedStop` and end a run |
| `GetRightAscensionAsync` | the RA of the last `SyncRaDecAsync`, `0.0` before any. Held constant: neither time nor `MoveAxisAsync` moves it. `SyncRaDecAsync` and `SetTrackingAsync(true)` record a tracking-start anchor (`_trackingStartTicks`, `_raAtTrackingStart`) that nothing reads |
| `GetDeclinationAsync` | +90 or -90 from the hemisphere (the connect's `:MGAS#`, or `SetHemisphereAsync`), or the Dec of the last `SyncRaDecAsync` |
| `GetTargetRightAscensionAsync` / `GetTargetDeclinationAsync` | `0.0` always (`_targetRa` is never set); the +90 or -90 set at connect, which neither a sync nor `SetHemisphereAsync` updates |
| `SyncRaDecAsync` | sends nothing; replaces the modelled RA and Dec |
| `BeginSlewRaDecAsync` | throws `InvalidOperationException` (no goto) |
| `GetSiderealTimeAsync` / `GetHourAngleAsync` | LST from the host clock (`TimeProvider`) and the longitude last set; LST minus the modelled RA. Both throw `InvalidOperationException` ("Site longitude has not been set", from `Transform.SiteLongitude`) until a longitude is set |
| `GetSiteLatitudeAsync` / `GetSiteLongitudeAsync` / `GetSiteElevationAsync`, and their setters | cached only: `NaN`, `NaN` and `0.0` until set. Setting a southern latitude does not send `:MSHE0#` |
| `GetTrackingSpeedAsync` | cached: the connect's `:MGAS#` tracking-rate digit, then the last `SetTrackingSpeedAsync` |
| `TrackingSpeeds` | Sidereal, Lunar and Solar |
| `GetGuideRateRightAscensionAsync` | the cached percentage (50 until set) of the sidereal rate, in degrees per second; `:MGGR` is never sent |
| `GetGuideRateDeclinationAsync` | `0.0`, although every RA set writes the mount's Dec field as 50. `SetGuideRateDeclinationAsync` throws `InvalidOperationException` |
| `GetRightAscensionRateAsync` / `GetDeclinationRateAsync` | `0.0`; both setters throw `InvalidOperationException` |
| `GetSideOfPierAsync` / `DestinationSideOfPierAsync` | computed from the hour angle as on the LX200 base (west of the meridian `Normal`, east `ThroughThePole`), `Unknown` before a longitude is set; `PointingStateSource` is `Computed`, so whether a flip happened is read off the image. It was `None` with a constant `Normal` until the slew-less flip (`docs/plans/slew-less-mount.md`, P2), which hid every flip; `SetSideOfPierAsync` throws `InvalidOperationException` |
| `AtHomeAsync` / `AtParkAsync` | `false`; `ParkAsync` and `UnparkAsync` throw `InvalidOperationException` |
| `StartPulseGuideAsync` / `IsPulseGuidingAsync` | throws `InvalidOperationException` (guide through the ST-4 port instead); `false` |
| `TryGetUTCDateFromMountAsync` / `SetUTCDateAsync` | the host clock; does nothing. `TimeIsSetByUs` is `true` |
| `GetAlignmentAsync` / `EquatorialSystem` | `AlignmentMode.GermanPolar`; `EquatorialCoordinateType.Topocentric` |
| `GetAxisPositionAsync` / `GetAxisAngleAsync` / `GetWormPeriodStepsAsync` | not overridden, so the interface defaults: `null`, `null`, `0` |
| Capabilities | `CanSync`, `CanSetGuideRates`, `CanCameraSnap` and `CanMoveAxis(Primary)` are `true`; every other `Can*` is `false`. `AxisRates(Primary)` is the seven speeds (1, 2, 8, 16, 64, 128 and 144 times `SIDEREAL_RATE`, 15.0417 arcsec/s) in degrees per second, and empty for the other axes |
| `DriverInfo` / `Description` | `iOptron SkyGuider Pro (FW <date>)`, the date from the connect's `:MRSVE#` or `Unknown`; a constant |

## Connect handshake

`ConnectAsync` (`DeviceDriverBase`) runs `SgpMountDriverBase.DoConnectDeviceAsync`, then `InitDeviceAsync`:

1. Open the port named by the URI's `port` query key at 28800 baud, ASCII, through
   `IOptronDevice.ConnectSerialDeviceAsync` (`IExternal.OpenSerialDeviceAsync`; control lines not asserted,
   no boot wait). It returns no connection for any other baud and throws for a URI without `port`; either
   way `DoConnectDeviceAsync` reports failure (logging any exception) and `ConnectAsync` throws
   `InvalidOperationException` ("Could not connect to device").
2. `:MRSVE#`, through `SgpWire`, so a reply a previous session left held is passed over. A reply matching `^:RMRVE12(\d{6})$` (`SgpFirmwareRegex`) sets the firmware date that
   `DriverInfo` shows. **Any other outcome is not an error**: no reply, a wrong reply or another device's
   answer leaves the firmware `Unknown` and the connect goes on, so connecting never checks that an SGP is
   on the port (discovery does).
3. `:MGAS#` through `GetMountStatusAsync`. The motion field starts the slewing record, the hemisphere digit sets the modelled Dec, and the target Dec,
   to +90 or -90. The tracking-rate digit sets the cached tracking speed: `0` Solar, `1` Lunar, `3`
   Sidereal, and anything else Sidereal, which takes in half-sidereal (`2`) and a failed read. A failed
   read keeps the hemisphere the driver last knew (north on a new driver instance).
4. `GetSiteLatitudeAsync` and `GetSiteLongitudeAsync`, which return cached fields and send nothing.

Nothing is written to the mount at connect: no hemisphere, tracking rate, time or site. Because reads have
no deadline, a mount that never answers holds the connect until the caller's token fires.
`InitDeviceAsync` catches every exception, logs "Failed to initialize SGP mount" and returns `false`, which
`ConnectAsync` turns into an `InvalidOperationException`, closing the port it opened and leaving the driver
not connected, so a retry runs the whole connect again (#806). `DoDisconnectDeviceAsync` takes the port lock and
closes the port.

## Discovery

`AddIOptron()` registers `IOptronSerialProbe` with the serial probe service and `IOptronDeviceSource` as a
mount source; the CLI, server and GUI `Program.cs` all call it.

- **`IOptronSerialProbe`** runs at 28800 baud, ASCII, `ProbeFraming.HashTerminated`,
  `ProbeExclusivity.Shared`, with a 500 ms `Budget`, one attempt, no `Warmup` and no `AssertControlLines`.
  It sends `:MRSVE#` through `SgpWire` (so a reply a previous session left held is passed over, which a single
  read once mistook for "not an SGP") and matches the same `^:RMRVE12(\d{6})$` as the connect (`12` for the
  SGP, six digits of firmware date). No other registered probe runs at 28800 (the rest use 9600 or
  115200), so it has its baud group, and that port handle, to itself.
- **The published URI** is
  `Mount://IOptronDevice/SkyGuider-Pro_<port>?port=<probed port>&firmware=<date>#iOptron SkyGuider Pro (<date>) on <port>`,
  where `<port>` has lost its `serial:` prefix and `SafeName` turns `_`, `/` and `:` into `-`. The firmware
  keeps no serial number (see Device Identity below), so the id ends in the port, which discovery swaps for the
  USB bridge's serial (the FTDI HBX cable: `SkyGuider-Pro_usb-0403-6001-A10MOSEFA`) or the USB socket (the
  SGP's own Prolific) where the port has one (#1090, #2040). The firmware date is evidence in the query, never
  in the id, so an update does not make a new device (`device-architecture.md`, "Step 3").
- **Pinned ports.** `MatchesDeviceHosts` is `IOptronDevice`, so a port a profile pins to an SGP is
  re-verified by this probe alone before the general pass (`SerialProbeService`). Verification compares
  scheme, host and path, so the port's own key must still match; otherwise the port falls through
  to the general pass.
- **`IOptronDeviceSource`** (`ConsumesSerialProbe = true`, `CheckSupportAsync` always true, mounts only)
  turns `ISerialProbeService.ResultsFor("iOptron")` into `IOptronDevice`s, and
  `IOptronDevice.NewInstanceFromDevice` makes an `SgpMountDriver` for each. A hand-entered
  `Mount://IOptronDevice/...?port=...` URI skips the probe, and the connect then checks nothing (see
  Connect handshake).
- **No blacklist entry.** `NativeDriverBlacklist` deliberately leaves the `ASCOM.iOptron2017` mount driver
  visible, because the native driver covers only the SkyGuider Pro, not the CEM/GEM/HEM range.

## The fake

`port=SGP` on a fake mount selects it (`FakeDeviceSource` lists `Mount://FakeDevice/FakeMount_SGP`, "Fake
Mount (SGP)"): `FakeDevice.CreateMountDriver` returns a `FakeSgpMountDriver`, and
`FakeDevice.ConnectSerialDeviceAsync` hands it a new `FakeSgpSerialDevice`, northern when the URI's
`latitude` is 0 or more. `FakeSgpMountDriver` (`src/TianWen.Lib/Devices/Fake/FakeSgpMountDriver.cs`) adds
nothing to `SgpMountDriverBase`, so tests run the production driver over the fake transport;
`FakeSgpMountDriverTests` (`TianWen.Lib.Tests.Functional`) covers it.

`FakeSgpSerialDevice` (`src/TianWen.Lib/Devices/Fake/FakeSgpSerialDevice.cs`) is an in-memory
`ISerialConnection`: a write is parsed at once and its reply appended to a buffer the next read consumes.
It answers every command in the reference above, including the ones the driver never sends (`:MSTR2#`,
`:MGGR`, `:MGEL`, `:MSEL`), with the replies the reference gives, and reports firmware `170518`. It keeps
the tracking rate, move direction, slew speed, both guide rates, eyepiece light and camera settings (a
`:MSCA` is read back by `:MGCS#`), and a 50 ms timer accumulates an hour-angle offset while a move runs.
Where it is simpler than the mount:

- **It always answers, at once.** A command it does not know makes the write fail (`TryWriteAsync` returns
  `false`, so the driver throws `InvalidOperationException`), and a read with nothing buffered returns
  `null` immediately where the real port waits for the caller's token. It has no lost-reply knob like
  `FakeGeminiFocuserSerialDevice.DropReplies`, so no test reaches the **bold** rows above.
- **It is always in step** (see "Replies out of step"; `SgpOutOfStepReplayTests` scripts the real mount's
  sequences instead), and its `:MGAS#` motion field drops to `01` the moment a stop arrives, with no servo to
  wind down.
- **The speed digit in its `:MGAS#` is always `0`**: the field it prints is never assigned, not even by
  `:MSMS`.
- **No COMMAND reports the hour-angle offset** it accumulates (no command reports position, as on the real
  mount), so the driver sees a move only through its acks. **The fake camera does see it**, once a test places
  the rig (`FakeSgpMountDriver.PlaceAtJ2000Async`): the serial fake then keeps a TRUE pointing whose RA the
  offset moves (west lowers it, at the commanded speed times `MoveRateError`, a deliberate error), and whose Dec
  only `TurnDecByHand` changes, since the mount has no Dec motor. `SwingDecOverThePoleByHand` is the operator's half of a flip: the pointing goes 12 h round and the tube's mechanical side turns over, which the driver reports through `IFakeMechanicalPointingStateSource`, so the camera rolls the field 180 degrees on a flip and on nothing else. `FakeSgpMountDriver` is an
  `IFakeTruePointingSource`, so the camera renders that sky while the driver's reads keep its last sync, as on
  the real mount. A rig never placed reports the driver's belief as its truth. This is what the slew-less
  session is tested on (`docs/plans/slew-less-mount.md`).
- **It ignores the baud** (the real `IOptronDevice` refuses anything but 28800), and each connect builds a
  new instance, so its state starts from the defaults again.

## Known issues

The SGP driver is listed as affected in #810 (a native serial driver that is connected and gets no valid
reply must throw a transient exception, never return a value). The bold rows above are its cases:

- **`IsTrackingAsync`** (#810's known-instances table, row 5) is no longer one: it no longer reads the
  mount (2026-11-05).
- **`IsSlewingAsync`**: a lost status keeps the last motion read rather than throwing.
- **Every command** (`SendCommandAsync`, and `MoveAxisAsync`'s inline pair): the ack is matched and discarded,
  so no command is verified, and the cached state (tracking speed, guide rate, slewing flag, camera
  settings, Dec) moves as if each was accepted.
- **`GetCameraSnapSettingsAsync`**: falls back to the settings last sent, indistinguishable from a read.
- **The transport under all of them** (#810's "three facts"): the read has no deadline, the helper reports
  the caller's cancellation as "no reply", and a failed write throws `InvalidOperationException`, which
  `ResilientCall.IsTransient` does not retry.

#810 also asks for a lost-reply knob on each fake serial device, which `FakeSgpSerialDevice` does not have
yet (see The fake).

## Firmware Analysis (SGPro_20170518.bin)

- **MCU**: STM32F103 (medium density, Cortex-M3)
- **Size**: 22,029 bytes
- **SRAM usage**: ~5KB (SP = `0x20001468`)
- **Firmware version string**: `01.01.00` at offset `0x54F7`
- **Firmware date**: `170518` at offset `0x54F0`

### Flash Configuration
- Config area: page 37 (`0x08009400`-`0x080097FF`)
- Stores numeric motor/tracking parameters only (no strings)
- Flash unlock keys at offset `0x1400`: `0x45670123` / `0xCDEF89AB`

### UARTs
- 9600 baud (offset `0x5570`): HC-to-mount-head channel
- 28800 baud (offset `0x556C`): main control protocol

### Device Identity
- No serial number: firmware does **not** read STM32 hardware UID (`0x1FFFF7E8`)
- No user string storage in flash config area
- STM32 Option Bytes (`0x1FFFF800`) referenced only for flash protection

### Response Templates in Firmware

| Offset | Template | Notes |
|--------|----------|-------|
| `0x1DE4` | `:HRCSxyaaaabbbcccdddppkkkkk#` | Camera settings |
| `0x1E54` | `:RMRVE12yyyyyy#` | Firmware version |
| `0x1E68` | `:HRVE121212xxxxxxyyyyyyzzzzzz#` | Extended version (query cmd unknown) |
| `0x22D0` | `:HRASx0xxxxxxx#` | Axis status |
| `0x230C` | `:HRGRxxxx#` | Guide rate |

Format strings `%04d`, `%03d`, `%02d`, `%05d` at offsets `0x1E10`-`0x1E3C` fill template placeholders.

### Miscellaneous
- `LES`/`RGG` at `0x1DC0`/`0x1DC4`: 3-char strings followed by coefficient tables (PEC curves? motor params?)
- Custom 16-byte header before vector table: bytes include `0x11=17`, `0x05=5`, `0x12=18` encoding firmware date
