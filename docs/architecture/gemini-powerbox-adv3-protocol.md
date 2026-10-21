# Gemini Power&Data Hub Advanced 3: serial protocol

The Gemini Power&Data Hub Advanced 3 is a 12 V power box and USB hub: one always-on 12 V port (DC1), four switchable
ones (DC2 to DC5), two dew heater outputs (DEW6, DEW7), six switchable USB ports (A and B USB 3.2, C to F USB 2.0), an
ambient temperature and humidity sensor (AHT20), a lens temperature probe (DS18B20), and a meter on the 12 V outputs.
Its control link is a CH340 serial port behind the box's own WCH hub, which enumerates only once the box has 12 V.

TianWen drives it natively (`Devices/Gemini/GeminiPowerBox*.cs`, registered by `AddGemini()`) as an `ISwitchDriver`.
Read out of the vendor's ASCOM driver (`ASCOM.GeminiPowerBoxPlusAdv3.Switch`, 3.0.2) and checked byte for byte on the
owner's box, firmware 309, on 2026-10-21. The plan it belongs to: `docs/plans/power-boxes.md`.

## Serial parameters

19200 baud, 8 data bits, no parity, 1 stop bit, no handshake, DTR and RTS asserted, as the vendor driver opens it. The
vendor driver then waits 2 s before its first command; the bench box sent nothing at the open and answered at once, but
a slower board's start is not measured, so the driver and the probe keep the wait.

## Framing

- **Command**: `>` + letter + optional decimal argument + `#`, then a line feed (the vendor driver's `WriteLine`). No
  checksum.
- **Reply**: `*` + letter + payload + `#`, with no line feed.
- **Only queries reply.** `O`, `C`, `X`, `Y`, `M` and `Z` are answered by nothing: the next status frame is the only
  sign a set took.
- **The box speaks unasked.** Once anything has sent `>G#`, the box sends a status frame every 3 s, and it was still doing
  so when a new connection opened the port: the probe's first read on the bench was a `*G` frame, before the `*H` it had
  asked for. So every query reads past status frames until its own letter (`GeminiPowerBoxProtocol.QueryAsync`, at most
  four).

## Commands

| Command | Reply | Meaning |
|---|---|---|
| `>H#` | `*HGeminiPowerBoxPlusAdv3#` | Identity: the model, no serial number (the CH340 has none either) |
| `>V#` | `*V309#` | Firmware; the vendor driver refuses below 308 |
| `>G#` | `*G<status>#` | Status (below), and from then on a frame every 3 s |
| `>O<n>#` / `>C<n>#` | none | Output `n` on / off: 2 to 5 are DC2 to DC5, 6 to 11 USB A to F (`>O10#`, two digits); DC1 is never sent |
| `>X<p>#` / `>Y<p>#` | none | DEW6 / DEW7 power, whole percent, heeded in the box's Manual mode |
| `>M<ch><mode>#` | none | DEW6 (`ch` 1) / DEW7 (2) mode: 0 Auto, 1 Manual, 2 Switch. **TianWen never sends it.** |
| `>Z<ch><s>#` | none | DEW6 / DEW7 on or off in Auto or Switch mode. **TianWen never sends it.** |

There is no factory reset, no firmware command and no saved default anywhere in the vendor driver.

## The status frame

Each value is FOLLOWED by its letter; the bench box sent

```
*G1111D111111U1A1T1A1M1B1M100C100C 24.81S 26.87T 56.00H 17.35D12.9V 0.16C  2.06P#
```

Split on the letters `D U A T M B C S H V P` (the vendor driver's set), the seventeen values are, in order:

| # | Value | Letter | Meaning |
|---|---|---|---|
| 0 | `1111` | D | DC2..DC5, `1` on |
| 1 | `111111` | U | USB A..F, `1` on |
| 2 | `1` | A | ambient sensor (AHT20) attached |
| 3 | `1` | T | lens probe (DS18B20) attached |
| 4 | `1` | A | DEW6 on (Auto or Switch mode) |
| 5 | `1` | M | DEW6 mode: 0 Auto, 1 Manual, 2 Switch |
| 6 | `1` | B | DEW7 on (Auto or Switch mode) |
| 7 | `1` | M | DEW7 mode |
| 8 | `100` | C | DEW6 manual power, percent |
| 9 | `100` | C | DEW7 manual power, percent |
| 10 | ` 24.81` | S | lens probe, C |
| 11 | ` 26.87` | T | ambient, C |
| 12 | ` 56.00` | H | relative humidity, percent |
| 13 | ` 17.35` | D | dew point, C, computed by the box |
| 14 | `12.9` | V | input voltage, V |
| 15 | ` 0.16` | C | total current of the 12 V outputs, A |
| 16 | `  2.06` | P | total power of the 12 V outputs, W |

Numbers are space-padded and use a point whatever the PC's locale. The vendor driver parses them with the current
culture, so on a comma-decimal Windows it reads 12.9 V as 129; TianWen parses invariantly.

## Reading the port: one reader, never cut off

Serial.Lib drops the bytes a failed or cancelled read consumed (its contract, so a partial reply cannot contaminate the
next exchange), and allows one reader and one writer at a time. A frame arrives in pieces over about 30 ms. So:

- the driver's reader loop is the port's ONLY reader once connected, and waits up to 6 s for each frame; a first version
  that read in 500 ms windows lost the frame a window closed on, and missed the 3 s push on the bench;
- a read that times out after 6 s has lost nothing, since nothing was arriving, and the loop asks for status again;
- a command only WRITES, under the connection's lock, beside the reader; the queries that need a reply (identity,
  firmware) are made at connect, before the reader starts;
- a query reads past what is not its reply, a whole status frame or the TAIL of one (Serial.Lib ends a read at the
  first `#`, so a read that joins a frame midway returns ` 0.16C  2.06P` with no `*`), at most four;
- a port that goes away under the connection (closed under the reader, or three status requests in a row that cannot
  be written) ends it: the driver reports itself disconnected once and refuses reads and sets until reconnected;
- a disconnect cancels the reader, CLOSES the port, and only then waits for the reader (3 s at most), so a read that
  does not heed its token cannot hold the disconnect.

## Connect handshake

Open (DTR and RTS), wait 2 s, `>H#` up to three times with 1 s each until `GeminiPowerBoxPlusAdv3`, `>V#` the same way
(refused below 308, and refused when it never answers), start the reader, `>G#`, and wait for a frame read after that
request (8 s). Anything short of that stops the reader, closes the port and throws. A connect first tears down whatever
an earlier connection left, so a reconnect without a disconnect neither leaks the old reader nor counts the old
connection's frame as the new one's.
The connect writes NO dew mode, unlike the vendor driver, which writes `>M1x#` and `>M2x#` on every connect.

## Mapping to `ISwitchDriver`

| Channel | Name | Kind | Read from | Written by |
|---|---|---|---|---|
| 0 | DC1 | Output, not writable | always 1 | never |
| 1..4 | DC2..DC5 | Output | status field 0 | `>O2#`..`>O5#` / `>C2#`..`>C5#` |
| 5..10 | USB A..F | Output | status field 1 | `>O6#`..`>O11#` / `>C6#`..`>C11#` |
| 11, 12 | DEW6, DEW7 | Level 0..100 % | Manual: fields 8, 9; Switch: 100 or 0; Auto: NaN (the box does not say what power it chose) | `>X<p>#`, `>Y<p>#`, Manual mode only; otherwise refused naming the box's mode |
| 13..15 | Voltage, Current, Power | Reading | fields 14..16 | never |
| 16..18 | Ambient temperature, Humidity, Dew point | Reading | fields 11..13; NaN without the AHT20 | never |
| 19 | Lens temperature | Reading | field 10; NaN without the probe | never |

- **A read** answers from the latest frame, and throws `IOException` when that frame is older than 12 s: a box gone
  quiet is a fault, never a value. Not connected throws.
- **A set** writes, asks for status at once (`>G#`), and returns when a frame read after the write shows the new value,
  about 215 ms on the bench; none within 8 s throws `IOException`. Out of range throws `ArgumentOutOfRangeException`, a
  channel that cannot be written `InvalidOperationException`.
- **The driver does not ask who uses an output.** Switching off the port a running camera draws from ends the run, so a
  caller asks `DeviceOwnershipGate` first (#1730).

## Discovery

`GeminiPowerBoxSerialProbe`: 19200 baud, `#`-terminated, DTR and RTS asserted, a 2 s warm-up, `>H#` then `>V#`, matching
the identity. The only 19200-baud probe. The device is `switch://GeminiPowerBoxDevice/GeminiPowerBox_<port>?port=...`,
known by its port, since the box has no serial number. A device that is not the box is SENT nothing but `>H#`, but the
probe asserts DTR and RTS, so on the probe service's isolated pass every port still unmatched is opened at 19200 with them
asserted, which resets a board that resets on DTR (an Arduino). The FlatPanel Lite's probe already does the same at 9600,
so this is one more reset per scan, not a new kind of one.

## Native-driver blacklist

`ASCOM.GeminiPowerBoxPlusAdv3.Switch` is hidden when the native box is discovered (`NativeDriverBlacklist`), so the
picker offers the box once.

## Bench results (2026-10-21)

The owner's box, firmware 309, on COM5, 12.9 V in, the SV605CC on DC1 and its USB on USB A:

- identity, firmware and status exactly as above; a status frame every 3 s after `>G#`, and still coming across a reopen;
- DC5 and USB F switched off and on by hand (`>C5#`, `>O5#`, `>C11#`, `>O11#`): each change in the next frame, nothing
  else moved;
- `GeminiPowerBoxHardwareProbe` (set `TIANWEN_GEMINI_POWERBOX_PORT`, and `TIANWEN_GEMINI_POWERBOX_SWITCH=1` to switch
  USB F) three times in a row: probe matched, connect in about 2.35 s, every channel read, USB F off and on each confirmed
  in about 215 ms.

## The fake

`FakeGeminiPowerBoxSerialDevice` (tests) answers `>H#`, `>V#` and `>G#` in the bench layout, takes the sets silently,
pushes a frame when a test asks, and reads as a port does: it waits for bytes, and a cancelled read gives up what it took.
Unplugged or closed, a waiting read ends empty and every write fails and is counted (`WritesAfterClose`), which is how a
test sees a reader the driver left running.
