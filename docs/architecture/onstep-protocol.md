# OnStep / OnStepX: serial and WiFi protocol

Reference for TianWen's native `OnStepMountDriver<TDevice>` (`IMountDriver`), which drives mounts running
OnStep or OnStepX, the open-source telescope controller firmware (OnStepX being the active fork,
`hjd1964/OnStepX`, per the code's own comment), over USB serial or over WiFi / Ethernet. OnStep speaks an
extended Meade LX200 command set, so the driver derives from `MeadeLX200ProtocolMountDriverBase<TDevice>` and
inherits every command it does not override: init (`:GVP#`, `:GVN#`, `:U#`), coordinates, site and time,
alignment (`:GW#`), goto (`:Sr`, `:Sd`, `:MS#`), abort (`:Q#`), sync (`:CM#`) and pulse guiding (`:Mg`). Those
are documented in [`lx200-protocol.md`](lx200-protocol.md); this document covers only what OnStep adds or
overrides. The driver is generic over `TDevice` so `FakeOnStepMountDriver` runs the same code over
`FakeDevice` while production uses `OnStepDevice`. Code: `src/TianWen.Lib/Devices/OnStep/`.

> **Derived from the code, not from firmware.** The driver shipped on 2026-04-15 with no design doc. Its
> wire differences were checked at the time against the OnStepX sources and the OnCue ASCOM driver, but the
> repo quotes neither, and the only real-controller evidence it records is discovery against a Teeseek mount
> (`docs/plans/serial-probe.md`, the cold-start row). So every command, reply and parse below is what the code
> sends and expects. Where it relies on firmware behaviour, the text says what the code **assumes**; those
> assumptions stay unverified until a real controller is exercised through the driver.

## Transports

`OnStepDevice.ConnectSerialDeviceAsync` picks the transport from the URI: a non-empty `host` query key means
TCP; otherwise it defers to `DeviceBase.ConnectSerialDeviceAsync` and the serial `port`.

| | Serial | WiFi / Ethernet |
|---|---|---|
| URI | `Mount://OnStepDevice/<id>?port=COM5#<name>` | `Mount://OnStepDevice/<id>?host=192.168.1.42&tcp=9999#<name>` |
| Endpoint | `port` (`COMx`, `serial:COMx`, `/dev/tty*`) | `host`: an IP literal, or a name resolved by `Dns.GetHostAddressesAsync` (first address). `tcp`: default `OnStepDevice.DefaultTcpPort` = 9999, the ESP32 SmartHand Controller default per the code's comment |
| Rate | 9600 baud; `?baud=` overrides it for the driver, the probe is fixed at 9600 | n/a |
| Line settings | .NET `SerialPort` defaults (8 data bits, no parity, 1 stop bit, no handshake); the code sets none | `NoDelay = true` |
| DTR / RTS | not asserted: neither the probe nor the driver asks for `assertControlLines` | n/a |
| Connect bound | `SerialPort.Open` | 3 s (`TcpSerialConnection.DefaultConnectTimeout`), then `TimeoutException` |
| Encoding | Latin1 on the driver's connection (the base's `_encoding`); ASCII in both probes | same |
| I/O bounds | writes 2 s (`SerialConnectionBase.WriteTimeoutMs`); reads have no timeout of their own and end on the caller's token (#810) | `ReadTimeout` / `WriteTimeout` = 2000 ms are set on the `NetworkStream`, but the driver reads through `ReadAsync`, which those properties do not bound per the .NET documentation, so reads end on the caller's token here too |

The Equipment tab edits the transport in place through `OnStepDevice.Settings`: `port` shows only while `host`
is empty, `host` always, `tcp` only while `host` is set. Filling in a host flips the mount to TCP on its next
connect; clearing it reverts to the serial port (`OnStepDeviceExposesEditableTransportSettings`).

**`TcpSerialConnection`** (`src/TianWen.Lib/Connections/`) is the reusable half: an `ISerialConnection` over a
TCP byte stream, so the LX200 base runs over WiFi unchanged. TCP has no message boundaries, so it buffers
(256 bytes, grown on demand) and scans for a terminator, or counts bytes for an exact read. `CreateAsync`
connects cooperatively, so no pool thread blocks on the handshake. `DiscardInBuffer` drops the buffered bytes
plus whatever `TcpClient.Available` reports, up to 4 KiB, logging them when verbose. An `IOException`,
`SocketException` or `ObjectDisposedException` on a read or write, and a peer close, set `IsOpen = false` and
return `null` / `false`. A cancelled read is not caught, so it surfaces as the runtime's
`OperationCanceledException` where the serial helpers return `null`. OnStep is its only user;
`OnStepWifiTransportTests` drives the real driver through it against an in-process TCP responder.

## Framing

- **Command:** `:` + body + `#`, built by the base's private `SendAsync` (`GU` goes out as `:GU#`).
- **Terminated reply:** read up to `#` or NUL (the base's `Terminators`), terminator stripped. Used for
  `:GU#`, `:Gm#`, `:GX44#`, `:GX45#` and the discovery reads.
- **One-byte reply:** exactly one byte, no terminator: `:Te#`, `:Td#`, `:hP#`, `:hR#`, `:hQ#`, and a site-name
  set during discovery. The code assumes a bare `1` or `0`; a trailing `#` would stay in the stream and be
  read as the start of the next reply.
- **No reply read:** `:TQ#`, `:TL#`, `:TS#`, `:TK#`. The code assumes the firmware answers nothing.
- **Cold-start quirk:** the first `:GVP#` after a cold boot may answer a bare `0` with no `#` (INDI's
  `lx200_OnStep.cpp`, as `OnStepQuirkProbeTests` models it). Discovery absorbs it (see **Discovery**); the
  driver's own init `:GVP#` has no such handling, and a bare `0` there would leave the read waiting for a `#`
  until the caller's token ends it.

## The `:GU#` status word

One round trip carries every state the driver reads (`GetStatusAsync`, decoded by the private `OnStepStatus`
struct). The reply is treated as a bag of single-character flags, tested with ordinal, **case-sensitive**
`Contains`, because OnStep uses case for opposite states.

| Letter | The code reads it as | Used by |
|---|---|---|
| `n` | not tracking | `IsTracking` is its ABSENCE: `IsTrackingAsync` |
| `N` | no goto in progress | `IsGotoInProgress` is its ABSENCE: `IsSlewingAsync` |
| `P` | parked | `IsParked`: `AtParkAsync` and both park waits |
| `I` | parking in progress | `IsParking`: decoded, read by nothing |
| `F` | park failed | `IsParkFailed`: both park waits throw |
| `p` | not parked | never tested; "not parked" is the absence of `P` |

Not decoded: the tracking-rate symbols the code's comment lists (`(` lunar, `O` solar, `k` king, none for
sidereal), any pulse-guide or pier-side letter, and whatever else the word carries. The code assumes none of
the five letters above appears in the word with another meaning.

**Tracking and slewing are inferred from letters that are ABSENT.** A reply that is empty, truncated or
garbage therefore decodes as tracking, slewing, not parked and not failed. The only reply `GetStatusAsync`
rejects is none at all (`null`, thrown as `InvalidOperationException`); there is no length or alphabet check
(#810, row 8).

## Commands OnStep adds or overrides

| Command | Reply | Parse | Member | What it replaces in the LX200 base |
|---|---|---|---|---|
| `:GU#` | `#`-terminated flags | letter tests above | `IsTrackingAsync`, `IsSlewingAsync`, `AtParkAsync`, the park waits | `:GW#`'s tracking letter (alignment still uses `:GW#`); `:D#`; a constant "not parked" |
| `:Gm#` | `#`-terminated, one char | `E` to `Normal`, `W` to `ThroughThePole`, anything else (the code's `N`: none, parked or unaligned) to `Unknown` | `GetSideOfPierAsync` | the side derived from the hour angle |
| `:Te#` / `:Td#` | 1 byte | `1` is success, anything else throws | `SetTrackingAsync` | `:AP#` / `:AL#`, which read no reply |
| `:hP#` | 1 byte | `1` accepted, then polls `:GU#` | `ParkAsync` | the same `:hP#`, but no reply read and no wait |
| `:hR#` | 1 byte | `1` accepted, then polls `:GU#` | `UnparkAsync` | nothing: the base throws |
| `:hQ#` | 1 byte | `1` accepted | `SetParkAsync` (OnStep's own; not on `IMountDriver`) | nothing |
| `:TQ#` `:TL#` `:TS#` `:TK#` | none read | | `SetTrackingSpeedAsync`: sidereal, lunar, solar, King | the base sends `:TQ#` for solar and has no King |
| `:GX44#` / `:GX45#` | `#`-terminated integer | `long.TryParse` (invariant), else `null` | `GetAxisPositionAsync`, axis 1 / axis 2; the tertiary axis answers `null` with no query | the interface default `null` |

The code reads `:GX44#` / `:GX45#` as raw step counts per mechanical axis, meant (per its comment) to mirror
SkyWatcher's `:j1` / `:j2` for the neural guider's periodic-error features. It assumes integer counts; a
decimal reply reads `null`.

## Park and unpark

1. Send `:hP#` and read one byte. Anything but `1`, no byte included, throws `InvalidOperationException`
   ("OnStep refused park request").
2. The code assumes the ack means "park started", and that `:GU#` then shows `I` and finally `P` or `F`.
3. `WaitForParkStateAsync(parkedExpected: true)` polls `:GU#` every 250 ms (`ParkPollInterval`, slept through
   `ITimeProvider.SleepAsync`, so fake time drives it in tests). `F` throws `InvalidOperationException`; `P`
   logs and returns; past 60 s (`ParkTimeout`) it throws `TimeoutException` naming the last raw flags.
4. `UnparkAsync` is the same with `:hR#` and `parkedExpected: false`: it returns at the first reply lacking `P`.
5. `SetParkAsync` sends `:hQ#`, checks for `1`, and does not poll.

`ParkAsync` therefore returns only once the mount reports parked; the base returns straight after sending.
The `F` test runs first in BOTH directions, so the code assumes the firmware clears `F` once an unpark
starts; if it does not, an unpark after a failed park throws.

One trap sits in the base: its `SendAsync` calls `AtParkAsync` before every command unless `CanUnpark` or
`CanSetPark` is true. OnStep's `AtParkAsync` is itself a command, sent under the port lock, so the two
`true` overrides are load-bearing: with both `false`, the first command would wait on the non-reentrant
port lock its own caller holds.

## Identity across transports

One mount keeps one device id over USB and over WiFi, because the id carries a UUID stored in the mount's own
memory, in an LX200 site-name slot. `OnStepDeviceSource.TryGetMountInfo`, shared by the serial and the WiFi
probe and run under the connection lock:

1. `:GVP#`, the product name. A reply trimming to one character or less ends the exchange, which is what makes
   the cold-start `0` a non-match.
2. `:GVN#`, the product number (the firmware version).
3. The four site names, slot 4 down to slot 1: `:GP#`, `:GO#`, `:GN#`, `:GM#`.
   - A name exactly 15 characters long starting `TW@` is a UUID; the first one found wins.
   - A name reading `<AN UNUSED SITE>`, while no UUID has been seen, gets a fresh one: 9 random bytes as
     base64url (12 characters), written as `:S<slot>TW@<12 chars>#`, kept when the one-byte reply is `1`.
   - Any other name is a real site, kept for the fallback id.

The id is `<product>_<number>_<uuid>`, each part through `SafeName` (`_`, `/` and `:` become `-`). With no
UUID it falls back to `<product>_<number>_<sites>_<port>` on serial and `<product>_<number>_<sites>_<n>_wifi_<ip>`
on WiFi, which differ by transport. With a stable id, `IDeviceDiscovery.ReconcileUri` adopts whatever transport
discovery found (a new COM port, a serial profile now reached over WiFi) and keeps the stored URI's
non-transport keys (`DeviceDiscoveryExtensionsTests`).

Caveats. The code assumes an unused OnStep slot reads `<AN UNUSED SITE>` (Meade's text) and that a site set
answers one byte; if either is wrong, no UUID is written and the id falls back. The write happens during
DISCOVERY, and usually not by this code: Meade is registered before OnStep in all three hosts and both
probes are `HashTerminated`, so on the shared 9600-baud handle `MeadeDeviceSource.TryGetMountInfo`, a copy of
this method, runs first and writes the same `TW@` UUID before its product regex rejects `On-Step`. The two
copies must stay identical. The id also carries the `:GVN#` version, so a firmware update changes it.

## Discovery

**Serial.** `OnStepSerialProbe` runs inside the central `ISerialProbeService`: 9600 baud, ASCII,
`HashTerminated`, `Shared` (one open handle for the 9600-baud group with Meade, QHYCFW3 and QFOC), an 800 ms
budget, one attempt (the service's second pass doubles the budget for a slow cold start), and
`MatchesDeviceHosts = OnStepDevice`. It runs `TryGetMountInfo` and matches when the product name and number
are both present and the name matches `^On[-]?Step`, case-insensitive (the code's comment says OnStepX answers
`On-Step`). It publishes `Mount://OnStepDevice/<id>?port=<port>#<product> (<number>) on <port>`, and
`OnStepDeviceSource.DiscoverAsync` materialises `probeService.ResultsFor("OnStep")`.

The cold-start `0` is absorbed by ORDER, not by code: Meade's `:GVP#` meets it, finds no `#`, times out, and
leaves a clean stream for OnStep's own `:GVP#`; `SerialProbeService`'s post-probe `DiscardInBuffer` catches a
`0` that arrives late (`OnStepQuirkProbeTests`).

**WiFi.** Inside `OnStepDeviceSource.DiscoverAsync`, which runs only after the serial pass because the source
declares `ConsumesSerialProbe`:

1. `ScanTelescopeMdnsAsync` binds UDP 5353 with `ReuseAddress`, joins 224.0.0.251, sends one PTR query for
   `_telescope._tcp.local` (QU bit set) and collects replies for 2 s. If the bind fails it returns nothing.
2. `ParseMdnsResponse` takes the PTR instance name and one A record per packet (the last seen); SRV records
   are not read. Addresses are de-duplicated.
3. `QueryWifiHostAsync` opens a `TcpSerialConnection` to each address on port 9999, always, and runs
   `TryGetMountInfo` with the same regex, the whole exchange bounded at 2 s. A match becomes
   `Mount://OnStepDevice/<id>?host=<ip>&tcp=9999#<product> (<number>) [<instance> @ <ip>]`.

Every failure in the WiFi leg is logged at debug level and swallowed.

## Mapping to `IMountDriver`

Only the members OnStep overrides. The rule from #810 (and `driver-resilience.md`) is that a CONNECTED read
with no valid reply throws a transient exception. `ResilientCall.IsTransient`
(`src/TianWen.Lib/Sequencing/ResilientCall.cs`) accepts I/O, socket, disposed-object, timeout and COM
exceptions but **not `InvalidOperationException`**, so every OnStep throw below except the park timeout gets
no retry, no reconnect and no fault count. **X** marks a violation of #810.

| Member | Wire | Not connected | Connected: reply missing or timed out | Connected: reply does not parse |
|---|---|---|---|---|
| `CanSetPark`, `CanUnpark` | `true`, `true` (base: `false`) | | | |
| `TrackingSpeeds` | sidereal, lunar, solar, King | | | |
| `SetTrackingSpeedAsync` | `:TQ#` `:TL#` `:TS#` `:TK#` | `InvalidOperationException` | no reply is read, so a lost command is invisible | n/a; any other speed throws `ArgumentException` |
| `SetTrackingAsync` | `:GU#` check, then `:Te#` / `:Td#` | `InvalidOperationException` | `InvalidOperationException` (**X**, wrong type) | `InvalidOperationException`, for a `0` refusal and garbage alike |
| `IsTrackingAsync` | `:GU#` | `InvalidOperationException` | `InvalidOperationException` (**X**, wrong type) | `true`, tracking (**X**: hides a tracking stop) |
| `IsSlewingAsync` | `:GU#` | `InvalidOperationException` | `InvalidOperationException` (**X**, wrong type) | `true`, slewing (**X**) |
| `AtParkAsync` | `:GU#` | `InvalidOperationException` | `InvalidOperationException` (**X**, wrong type) | `false`, not parked (**X**) |
| `PointingStateSource` | `Measured` | | | |
| `GetSideOfPierAsync` | `:Gm#` | `Unknown` | `Unknown` (**X**) | `Unknown` (**X**) |
| `ParkAsync` | `:hP#`, then the `:GU#` poll | `InvalidOperationException` | `InvalidOperationException`, for the ack or a poll (**X**, wrong type) | ack: `InvalidOperationException`; poll: keeps polling, then `TimeoutException` at 60 s |
| `UnparkAsync` | `:hR#`, then the `:GU#` poll | `InvalidOperationException` | as `ParkAsync` (**X**, wrong type) | ack: `InvalidOperationException`; poll: returns at once, "unparked" (**X**) |
| `GetAxisPositionAsync` | `:GX44#` / `:GX45#` | `null` | `null` (**X**) | `null` (**X**) |

"Not connected" departs from #810 too: the `:GU#` reads throw where #810 keeps a neutral value (the base's
helpers return `null` from a closed port, which `GetStatusAsync` throws on, and the base's `SendAsync` throws
on an open port whose driver is not `Connected`). Over WiFi a caller's cancellation mid-read escapes all of
them as `OperationCanceledException`.

**Everything else is the LX200 base, unchanged.** Five inherited members behave in ways worth knowing on OnStep:

- `IsPulseGuidingAsync` is the base's local end-of-pulse timer (not virtual); no `:GU#` letter is read for it.
  `StartPulseGuideAsync` asks `IsTrackingAsync` first, so every pulse costs a `:GU#`.
- `GetTrackingSpeedAsync` is the base's `:GT#` band (59.9 to 60.1 Hz sidereal, 57.3 to 58.9 lunar, else
  `None`), so solar and King can be set but never read back as themselves.
- `DestinationSideOfPierAsync` stays hour-angle derived, so the flip decision compares a MEASURED current side
  with a COMPUTED destination.
- `SyncRaDecAsync` sends `:CM#` (nothing sends `:CS#`), refuses when `GetSideOfPierAsync` is `Unknown`, which
  is every `:Gm#` reply but `E` or `W`, and takes any non-empty reply as success.
- `GetAxisAngleAsync` and `GetWormPeriodStepsAsync` keep the interface defaults, `null` and `0`.

## The fake

`FakeDevice` with `port=OnStep` builds `FakeOnStepMountDriver` (an empty subclass of
`OnStepMountDriver<FakeDevice>`, so every override runs unchanged) over `FakeOnStepSerialDevice`, which extends
`FakeMeadeLX200SerialDevice` through its `TryHandleExtensionCommand` hook:

- `:GU#` emits only `n` (not tracking), `N` (not slewing) and one of `p` / `I` / `P` / `F`, then `#`.
- `:hP#` answers `1`, stops any slew and enters Parking; a timer on the injected `ITimeProvider` makes it
  Parked 300 ms later, with tracking off, so the 250 ms poll sees at least one `I`. The axes do not move.
- `:hR#` answers `1` and unparks at once; `:hQ#` answers `1` and stores nothing; `:Te#` / `:Td#` always
  answer `1`.
- `:TS#` / `:TK#` set the base fake's tenths-of-a-hertz rate to 600 / 601, which the `:GT#` band reads back
  as sidereal.
- `:Gm#` answers `N` while parking or parked, `W` when the Dec axis is past the pole, else `E`.
- `:GX44#` / `:GX45#` answer the LX200 fake's axis angles times 11,378 steps per degree (HA hours x 15 for axis
  1): a TeeSeek-class harmonic mount, a 200-step NEMA 17 at 256 microsteps through 80:1, 4,096,000
  microsteps per turn, about 0.32 arcsec per step. At home (HA 6 h, Dec axis +90 in the north) both are positive.

Simpler than real firmware: park never fails (the `Failed` state is unreachable, so the `F` branch and the
60 s timeout have no test), `:Te#` never refuses, there is no lost-reply knob (#810 asks for one), and `:GVP#`
is the LX200 fake's `Fake LX200 Mount`, so the fake would fail the product regex and is reached only through
`FakeDevice`, never through discovery. `OnStepMountTests` pins connect and alignment, tracking via `:Te#` and
`n`, slewing via the absence of `N`, park `p` to `I` to `P`, unpark, the axis counts and the capabilities.

## Deliberately not replicated, and out of scope

- **No jog and no guide-rate control.** `MoveAxisAsync` throws and `CanSetGuideRates` is `false`, both from
  the base; OnStep's `:Mn#` / `:Ms#` / `:Me#` / `:Mw#` jogs and its `:Rn#` / `:GX90#` guide rates are #542.
- **No axis model.** `:GX44#` / `:GX45#` are passed through raw; `GetAxisAngleAsync` stays `null` until a real
  controller has been read at home and at known hour angles (#648), so the mount limits use the hour-angle
  tier on OnStep.
- **No local sync check.** Sync sends `:CM#` and trusts the mount's reply, with no "target too far" test or
  prompt; a park is polled to completion rather than assumed. (A comparison with the OnCue ASCOM driver that
  motivated both lives outside the repo and is not restated here.)
- **The ASCOM OnStep driver is not hidden** when a native mount is found (`NativeDriverBlacklist` leaves mount
  drivers out on purpose), so a machine with it installed can list the mount twice.

## Known issues and open follow-ups

- **#810, reads that invent a value** (the Mapping table): an unvalidated `:GU#` reads garbage as "tracking,
  slewing", which hides the stop `Session.DetectDriverEnforcedStop` exists to see and ends an unpark wait
  early; `:Gm#` and `:GX4n#` return neutral values while connected; every missing reply throws
  `InvalidOperationException`, which the session never retries.
- **#648, the axis angle**, and **#542**, the rest: jog and guide rates; an mDNS fallback to an ephemeral port
  when Bonjour or Avahi owns 5353 (today WiFi discovery then silently finds nothing, common on macOS); SRV
  parsing for a non-default port; an "add unseen device" dialog for a WiFi mount that does not advertise
  mDNS; tests for `EquipmentActions.ReconcileAllProfilesAsync`.
- **The step counts reach the guider and are thrown away.** `GetWormPeriodStepsAsync` answers `0` and
  `GuideLoop` computes a periodic-error phase only when it is positive, so on OnStep every guide frame pays
  two extra round trips (`:GX44#`, `:GX45#`) and the phase stays `NaN`.
- **`CanSetPark` is `true` with nothing to call.** `SetParkAsync` is not on `IMountDriver`, and the Alpaca
  plane answers `cansetpark` from the flag but has no `setpark` member.
- **The device id changes with the firmware version**, since it includes the `:GVN#` product number.
- **A dead TCP link leaves the driver `Connected`.** `TcpSerialConnection` sets `IsOpen = false` on a fault and
  every later read returns `null`, but nothing clears `DeviceDriverBase.Connected`, and `ResilientCall`
  reconnects only when it is clear.
- **Nothing discards a late reply.** A read abandoned on the caller's token leaves its reply in the stream, to
  be read as the answer to the next command.
- **WiFi discovery waits for the serial pass**; splitting them is open in `docs/plans/soft-discovery.md`.
- **Test stub drift:** `OnStepQuirkProbeTests`' stub answers `:GL#` (LX200's local-time query) instead of the
  slot-4 `:GP#` the probe sends; the test passes only because that unanswered read ends on the probe budget.
- **Bench validation is outstanding** for every "the code assumes" above, above all the one-byte replies, the
  `:GU#` alphabet, the unused-site text and the units of `:GX44#`.
