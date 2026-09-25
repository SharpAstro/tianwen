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

## Command Reference

### Mount Info

| Command | Response | Description |
|---------|----------|-------------|
| `:MRSVE#` | `:RMRVE12xxxxxx#` | Firmware version. `12` identifies SGP, `xxxxxx` is date (YYMMDD) |

### Mount Status

| Command | Response | Description |
|---------|----------|-------------|
| `:MGAS#` | `:HRAS01{TR}{SP}0{HEM}1{xx}#` | Get axis status (see breakdown below) |

`:HRAS013501105#` field breakdown:
```
:HRAS 01 3 5 0 1 1 05
      ^^ ^ ^ ^ ^ ^ ^^
      |  | | | | | +-- delay trigger? (unknown)
      |  | | | | +---- fixed (always 1?)
      |  | | | +------ hemisphere (1=north, 0=south)
      |  | | +-------- fixed separator (always 0?)
      |  | +---------- speed index (0-7)
      |  +------------ tracking rate (0=solar, 1=lunar, 2=half-sidereal, 3=sidereal)
      +--------------- fixed prefix (always 01)
```

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
| `:MGGR` | `:HRGR{xx}{yy}#` | Get guide rate (RA=xx, DEC=yy, 0-99). **Note: unterminated command** |
| `:MSGR{nn}{mm}#` | `:HRGR0#` | Set guide rate (nn=RA 0-99, mm=DEC 0-99) |

Default: `:MSGR5050#` = 50% RA, 50% DEC.

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

## Mapping to `IMountDriver`

Every exchange takes the port's lock (`ISerialConnection.WaitAsync`), writes the command and reads one
`#`-terminated reply through `ISerialConnection.TryReadTerminatedAsync`
(`SgpMountDriverBase.SendAndReceiveAsync` for a read, `SendCommandAsync` for a command). Four transport
facts decide what "no reply" means here (#810):

- **The read has no deadline of its own.** Only a write is bounded (`SerialConnectionBase.WriteTimeoutMs`,
  2 s), and the driver does not set `SynchronousReads`, so a mount that never answers holds the call until
  the caller's token fires.
- **The read helper swallows every exception, the caller's cancellation included.**
  `SerialConnectionBase.TryReadTerminatedRawAsync` returns -1 and `TryReadTerminatedAsync` null, exactly as
  for a reply whose `#` is not within `MaxTerminatedResponseBytes`, so the driver cannot tell a lost reply
  from a cancelled one.
- **Nothing discards input before a command** (the driver never calls `DiscardInBuffer`), so a reply that
  arrives after its read was given up is taken as the answer to the next command.
- **A failed write throws `InvalidOperationException`** (`SgpMountDriverBase.WriteAsync`), and so does a
  command sent with the port closed (`SendCommandAsync`: "Serial port is not connected").
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
| `IsTrackingAsync` | `:MGAS#` through `GetMountStatusAsync`; `true` when the reply matches `MountStatusRegex`, whatever its tracking-rate digit | `false` | **`false`**: `GetMountStatusAsync` returns `TrackingRate -1`, and that is the only way this can be false, since the mount has no tracking-off command. It feeds `Session.DetectDriverEnforcedStop` (two such polls after tracking was seen latch `MountLimitVerdict.DriverEnforcedStop` and end the run) and `IMountDriver.EnsureTrackingAsync`, which with `CanSetTracking` false is this read alone, so `ObservationLoopAsync` and `InitialRoughFocusAsync` give up with "Failed to enable tracking" |
| `GetCameraSnapSettingsAsync` | `:MGCS#`; shutter and interval parsed as whole seconds, then the shot count (`CameraSettingsRegex`) | the cached settings | **the cached settings**: the last `CameraSnapAsync` this driver instance sent, or `null` if none, which reads the same as a real answer |
| `SetTrackingSpeedAsync` | `:MSTR3#` Sidereal, `:MSTR1#` Lunar, `:MSTR0#` Solar; `ArgumentException` for any other speed, before anything is sent | `InvalidOperationException` | **returns normally**: the `:HRTR0#` ack is read and discarded, and the cached speed is updated as if the mount accepted it |
| `SetTrackingAsync` | `true`: the cached speed's `:MSTR` again (through `SetTrackingSpeedAsync`). `false`: nothing, and it returns normally (`CanSetTracking` is `false`) | `true`: `InvalidOperationException`. `false`: returns normally | `true`: **returns normally**, as `SetTrackingSpeedAsync` |
| `MoveAxisAsync` | Primary only; any other axis throws `InvalidOperationException`. Rate 0: `:MSMR2#`. Otherwise `:MSMS<n>#` then `:MSMR1#` (positive rate, west) or `:MSMR0#` (east) under one lock, `n` being the highest of the seven speeds not above the rate's magnitude (1 below 1x) | rate 0: `InvalidOperationException`. Any other rate: sends nothing, yet returns normally and sets the slewing flag | **returns normally**: every ack is read and discarded, and the slewing flag is set (cleared for rate 0) as if the mount obeyed |
| `AbortSlewAsync` | `:MSMR2#` | `InvalidOperationException` | **returns normally and clears the slewing flag**, the stop unacknowledged |
| `SetGuideRateRightAscensionAsync` | `:MSGR<pp>50#`: `pp` is the rate as a whole percentage of sidereal, and the Dec field is always `50`; `ArgumentException` outside 0 to 99 percent, before anything is sent | `InvalidOperationException` | **returns normally**, cached percentage updated |
| `CameraSnapAsync` | `:MSCA1<ssss><iii><nnn>#`: shutter and interval in whole seconds (the unit is unconfirmed, a TODO in the code), then the shot count | `InvalidOperationException` | **returns normally**, settings cached |
| `SetHemisphereAsync` (not on `IMountDriver`; only tests call it) | `:MSHE1#` north, `:MSHE0#` south; sets the modelled Dec to +90 or -90 | `InvalidOperationException` | **returns normally**, Dec updated |

The driver never sends `:MSTR2#` (half-sidereal is not in `TrackingSpeeds`), `:MGGR`, `:MGEL` or `:MSEL`.

### Members the driver models locally

The command set above has no read for position, pier side, park or home, and no sync, goto or pulse
command, so these members never touch the wire and cannot meet a missing reply. They are only as right as
the last connect, sync or setter.

| Member | Returns |
|---|---|
| `IsSlewingAsync` | **the `_isMoving` flag, not a read**: set by a non-zero `MoveAxisAsync`, cleared by `MoveAxisAsync(0)` and `AbortSlewAsync` once `:MSMR2#` has been written, whether or not the mount acknowledged the stop. The driver parses no motion field from `:MGAS#` |
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
| `GetSideOfPierAsync` / `DestinationSideOfPierAsync` | `PointingState.Normal`, a placeholder (`PointingStateSource` is `None`); `SetSideOfPierAsync` throws `InvalidOperationException` |
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
2. `:MRSVE#`. A reply matching `^:RMRVE12(\d{6})$` (`SgpFirmwareRegex`) sets the firmware date that
   `DriverInfo` shows. **Any other outcome is not an error**: no reply, a wrong reply or another device's
   answer leaves the firmware `Unknown` and the connect goes on, so connecting never checks that an SGP is
   on the port (discovery does).
3. `:MGAS#` through `GetMountStatusAsync`. The hemisphere digit sets the modelled Dec, and the target Dec,
   to +90 or -90. The tracking-rate digit sets the cached tracking speed: `0` Solar, `1` Lunar, `3`
   Sidereal, and anything else Sidereal, which takes in half-sidereal (`2`) and a failed read. A failed
   read keeps the hemisphere the driver last knew (north on a new driver instance).
4. `GetSiteLatitudeAsync` and `GetSiteLongitudeAsync`, which return cached fields and send nothing.

Nothing is written to the mount at connect: no hemisphere, tracking rate, time or site. Because reads have
no deadline, a mount that never answers holds the connect until the caller's token fires.
`InitDeviceAsync` catches every exception, logs "Failed to initialize SGP mount" and returns `false`, which
`ConnectAsync` turns into an `InvalidOperationException`. `DoDisconnectDeviceAsync` takes the port lock and
closes the port.

## Discovery

`AddIOptron()` registers `IOptronSerialProbe` with the serial probe service and `IOptronDeviceSource` as a
mount source; the CLI, server and GUI `Program.cs` all call it.

- **`IOptronSerialProbe`** runs at 28800 baud, ASCII, `ProbeFraming.HashTerminated`,
  `ProbeExclusivity.Shared`, with a 500 ms `Budget`, one attempt, no `Warmup` and no `AssertControlLines`.
  It writes `:MRSVE#`, reads to `#` and matches the same `^:RMRVE12(\d{6})$` as the connect (`12` for the
  SGP, six digits of firmware date). No other registered probe runs at 28800 (the rest use 9600 or
  115200), so it has its baud group, and that port handle, to itself.
- **The published URI** is
  `Mount://IOptronDevice/SkyGuider-Pro_<date>_<port>?port=<probed port>#iOptron SkyGuider Pro (<date>) on <port>`,
  where `<port>` has lost its `serial:` prefix and `SafeName` turns `_`, `/` and `:` into `-`. The firmware
  keeps no serial number (see Device Identity below), so the device id is the firmware date plus the port,
  and a firmware update or a different port makes a different device. The probe's comment expects
  `ReconcileUri` to carry a stored URI across a port change, but `ReconcileUri` pairs URIs by path
  (`DeviceBase.SameDevice`), and here the path holds the port.
- **Pinned ports.** `MatchesDeviceHosts` is `IOptronDevice`, so a port a profile pins to an SGP is
  re-verified by this probe alone before the general pass (`SerialProbeService`). Verification compares
  scheme, host and path, so the date and the port must both still match; otherwise the port falls through
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
- **It always tracks** and always sends a valid `:MGAS#`, so `IsTrackingAsync` is never `false` on the fake.
- **The speed digit in its `:MGAS#` is always `0`**: the field it prints is never assigned, not even by
  `:MSMS`.
- **Nothing reads the hour-angle offset** it accumulates (no command reports position, as on the real
  mount), so a move is observable only through its acks.
- **It ignores the baud** (the real `IOptronDevice` refuses anything but 28800), and each connect builds a
  new instance, so its state starts from the defaults again.

## Known issues

The SGP driver is listed as affected in #810 (a native serial driver that is connected and gets no valid
reply must throw a transient exception, never return a value). The bold rows above are its cases:

- **`IsTrackingAsync`** (#810's known-instances table, row 5): a lost or unparseable `:MGAS#` reads "not
  tracking", which on a mount that cannot stop tracking is always wrong, and which two polls in a row turn
  into `DriverEnforcedStop`.
- **`IsSlewingAsync`**: not a read at all; the flag clears once a stop is written, acknowledged or not.
- **Every command** (`SendCommandAsync`, and `MoveAxisAsync`'s inline pair): the ack is read and discarded,
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
