# Gemini Focuser Pro: serial protocol

Reference for TianWen's native `GeminiFocuserDriver` (`IFocuserDriver`). The Gemini Focuser Pro is a
rebadged **myFocuserPro2** Arduino focuser controller: an absolute stepper focuser with temperature
readout and optional temperature compensation, presented over a virtual COM port (CH34x USB-to-serial
bridge). This document is the wire-format spec the driver implements; it is transport-only (no ASCOM
dependency), so the focuser works on platforms without the ASCOM Platform (win-arm64, Linux, Raspberry Pi).

> **Derived from the vendor `ASCOM.GeminiFocuserPro.Focuser` driver (decompiled), and bench-validated on
> two units on 2026-09-25 (#654)**: one running the stock myFocuserPro2 `myFP2ULN2003` v324 with a
> temperature probe, one running `Gemini_Focuser_BZ_IR_V8` v338 (a renamed myFocuserPro2 build) without.
> Every command, reply and timing below was measured on both unless a row says otherwise; **Bench results**
> at the end has the numbers.

## Serial parameters

| Parameter | Value |
|-----------|-------|
| Baud rate | 9600 (vendor default; configurable 9600–115200) |
| Data bits | 8 |
| Parity | None |
| Stop bits | 1 |
| Handshake | none; **DTR + RTS asserted** on open (resets the Arduino) |
| Encoding | ASCII |

**Every port open resets the board, whether or not DTR is asserted** (on Windows through .NET
`SerialPort`, measured with `DtrEnable = false` too), and it ignores input until it has booted: the first
`:02#` answered at about 1.95 s after a DTR open on both units, none before 1.5 s. The driver sleeps
2.2 s through this before the first handshake. Because the reset is unavoidable, **a cold reopen within
30 s of a move loses the position** (see **Bench results**), and a probe cable must never be plugged in
with the board powered (the vendor guide's own warning; doing it mid-exchange silenced a board for
several seconds on the bench).

## Framing

Commands are `:` … `#`; responses are `<status-char>` + payload + `#`.

- **Command (host → device):** `:` + numeric code + optional argument + `#` (e.g. `:05<pos>#`).
- **Response (device → host):** a single leading status char (a myFocuserPro2 response code) + the payload
  + `#`. The decoder strips the leading char **unconditionally**, it is never part of the value, exactly
  as the vendor driver's `Substring(1, len-2)` does. `ParsePayload` also tolerates the terminator being
  present or already stripped by the read.

Reads terminate on the `#` byte (`ProbeFraming.HashTerminated`, the same framing family as LX200). Reads
use the cancellable **synchronous** path (`ISerialConnection.SynchronousReads`) because CH34x bridges
spuriously abort async `BaseStream` reads (`ERROR_OPERATION_ABORTED`) after the first read.

## Get commands (request → response)

| Command | Reply (payload after strip) | Meaning |
|---------|-----------------------------|---------|
| Command | Raw reply (bench) | Payload | Meaning |
|---------|-------------------|---------|---------|
| `:02#`  | `EOK#` | `OK` | Controller-present handshake. `OK` = a live myFocuserPro2 board. **This is the only identity check** discovery uses. |
| `:04#`  | `FmyFP2ULN2003\r\n324#` / `FGemini_Focuser_BZ_IR_V8\r\n338#` | `<name>\r\n<version>` | Firmware name + integer version. One Gemini unit names itself, the other runs a stock build, so the name is metadata, never a gate. |
| `:03#`  | `F324#` | `<int>` | Firmware version alone (not used by the driver). |
| `:00#`  | `P7820#` | `<int>` | Current absolute position (steps). |
| `:01#`  | `I0#` / `I1#` | `0` / `1` | Is-moving flag (`1` = moving). |
| `:06#`  | `Z21.25#` | `<double>` | Temperature (°C). **With no probe found at boot it answers a placeholder**: `20.00` on stock myFocuserPro2 (the vendor guide: "If no temperature probe is connected the temperature is set to 20"), `18.00` on the Gemini build. |
| `:08#`  | `M16000#` | `<int>` | Maximum step position (`MaxStep`; also reported as `MaxIncrement`). |
| `:24#`  | `10#` | `0` / `1` | Temperature compensation currently enabled. |
| `:25#`  | `A1#` / `A0#` | `0` / `1` | Temperature compensation available, which **is the probe flag**: `1` exactly when a probe was found at boot, on both firmwares. |
| `:83#`  | `c1#` / `c0#` | `0` / `1` | Temperature probe present (from INDI's myFocuserPro2 driver; agrees with `:25#` on both units; not used by the driver, which already has `:25#`). |
| `:33#`  | `T5.40#` / `T50.00#` | `<double>` | Step size (microns): a configured value, 5.40 and 50.00 on the two units. |

The status characters are the firmware's own and differ per command (`E`, `F`, `P`, `I`, `Z`, `M`, `1`,
`A`, `c`, `T`); the codec strips the first character without reading it, so none is load-bearing.

## Set commands

| Command | Reply | Meaning |
|---------|-------|---------|
| `:05<pos>#` | none (silent) | Move to absolute position `<pos>`. Poll `:01#` for completion. |
| `:27#` | none (silent) | Halt any motion. |
| `:230#` / `:231#` | `OK` | Temperature compensation **off** / **on** (this set command acks). |

**Move/Halt are silent fire-and-forget** (unlike the FlatPanel Lite, which acks everything), so the driver
does not wait on them; keeping the autofocus hot path fast. Measured: no byte within 1 s of a Move, of a
Halt while moving, or of a Halt while idle, and nothing unsolicited when a move ends (polled without
discarding input, as the driver reads). Halt takes effect within about 125 ms. Only the temp-comp toggle
replies, and the codec drains that ack (bounded) so it can't offset the next read.

## Connect handshake

1. Open the port (9600-8N1, DTR + RTS asserted); wait ~2 s for the Arduino boot.
2. `:02#`; verify the payload is `OK` (else "not a Gemini Focuser").
3. `:04#`; log the firmware name + version.
4. `:08#`; cache `MaxStep` (`MaxIncrement` = `MaxStep`).
5. `:33#`; cache `StepSize` (a positive value sets `CanGetStepSize`).
6. `:25#`; cache `TempCompAvailable`.

Position, is-moving, temperature and temp-comp state are polled live thereafter.

## Mapping to `IFocuserDriver`

| `IFocuserDriver` member | Wire behaviour |
|-------------------------|----------------|
| `Absolute` | always `true` |
| `GetPositionAsync` | `:00#` (→ `int.MinValue` when unavailable) |
| `GetIsMovingAsync` | `:01#` |
| `GetTemperatureAsync` | `:06#`, only when `:25#` found a probe at connect; `NaN` otherwise and when unavailable, never the placeholder |
| `MaxStep` / `MaxIncrement` | `:08#` (cached at connect) |
| `StepSize` / `CanGetStepSize` | `:33#` (cached at connect) |
| `TempCompAvailable` | `:25#` (cached at connect) |
| `GetTempCompAsync` | `:24#` |
| `SetTempCompAsync` | `:231#` / `:230#` |
| `BeginMoveAsync(pos)` | `:05<pos>#` (clamped to `[0, MaxStep]`) |
| `BeginHaltAsync` | `:27#` |
| `BacklashStepsIn` / `BacklashStepsOut` | `-1` (unknown, TianWen's backlash auto-tuning owns it) |

## Discovery

Auto-detected by `GeminiFocuserSerialProbe` (`ISerialProbe`, `HashTerminated`, 9600 baud): it writes `:02#`
and matches the `OK` reply, then publishes a `Focuser://GeminiFocuserDevice/GeminiFocuser_<port>?port=serial:<port>`
URI with the `:04#` firmware name captured into metadata.

**No distinctive identity, measured.** `:02#`→`OK` is the *generic* myFocuserPro2 handshake; any
myFocuserPro2-based controller answers it. One of the two bench units does name itself
(`Gemini_Focuser_BZ_IR_V8`), the other runs a stock `myFP2ULN2003` build, so a matcher that required the
Gemini name would reject a genuine Gemini. We treat any responder as a Gemini Focuser Pro and surface the
reported name in metadata. Neither firmware has a serial number or board ID (none in the vendor command
set, none in INDI's myFocuserPro2 driver), and the CH340 bridge carries no USB serial number either (two
units differ only in their USB port path), so **the port is the only identity**.

**DTR/RTS + boot delay.** Like the FlatPanel Lite, the probe declares `Warmup = 2200 ms` and
`AssertControlLines = true` (honoured only on the isolated per-probe pass, so toggling DTR can't reset a
different controller sharing the port on pass 1). Manual assignment
(`Focuser://GeminiFocuserDevice/…?port=serial:COMx`) also works, that path reconstructs the device from the
URI and the driver's own connect asserts DTR + boot-waits.

## Native-driver blacklist

When a native Gemini Focuser Pro is discovered, the `ASCOM.GeminiFocuserPro.Focuser` ASCOM driver is hidden
from discovery (`NativeDriverBlacklist`, keyed on ProgID → `GeminiFocuserDevice`), so the picker offers one
entry per physical focuser. If no native device is found, the ASCOM twin passes through as the fallback.

## Bench results (2026-09-25, #654)

Two units on COM3 and COM4, motors free, raw exchanges over .NET `SerialPort` plus
`GeminiFocuserHardwareTests` (`TIANWEN_GEMINI_FOCUSER_PORT`) through the real driver on each.

| | COM3 | COM4 |
|---|---|---|
| `:04#` | `myFP2ULN2003` v324 | `Gemini_Focuser_BZ_IR_V8` v338 |
| probe (`:25#`, `:83#`) | fitted: `1`, reads live (22.50 to 26.63 C in hand over 12 s, in 1/16 C steps) | none: `0`, `:06#` answers 18.00 |
| first `:02#` answer after a DTR open | about 1.95 s (none at 1.25 s, 470 ms late at 1.5 s) | about 1.9 s |
| open with DTR NOT asserted | still resets (no answer until about 1.6 s) | still resets (about 1.3 s) |
| move rate | 64 to 66 steps/s, lands exactly | about 330 steps/s, lands exactly |
| `MaxStep` / step size | 16000 / 5.40 um | 15000 / 50.00 um |

**The 2.2 s boot delay holds**, with about a quarter of a second to spare.

**A cold reopen within 30 s of a move LOSES THE POSITION.** The firmware saves a changed position to EEPROM
30 s after the change (the vendor guide: "You have to wait 30s before disconnecting and reconnecting"), and
since every open resets the board, a reopen before then restores the old value while the motor stays where
it went. Measured on COM4: moved 6000 to 6500 and reopened at once, the board reported 6000; moved to 6300
and waited 35 s, the reopen reported 6300. A reconnect, a quick application restart and a discovery probe
all reopen the port. The fix is designed in #780: the driver keeps the last position it knew and
re-syncs with `:31<pos>#` when a reopen inside that window reports another value (and trusts the board
outside it, since the controller's buttons and IR remote move it legitimately).
