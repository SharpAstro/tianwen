# Gemini Focuser Pro — serial protocol

Reference for TianWen's native `GeminiFocuserDriver` (`IFocuserDriver`). The Gemini Focuser Pro is a
rebadged **myFocuserPro2** Arduino focuser controller: an absolute stepper focuser with temperature
readout and optional temperature compensation, presented over a virtual COM port (CH34x USB-to-serial
bridge). This document is the wire-format spec the driver implements; it is transport-only (no ASCOM
dependency), so the focuser works on platforms without the ASCOM Platform (win-arm64, Linux, Raspberry Pi).

> **Derived from the vendor `ASCOM.GeminiFocuserPro.Focuser` driver (decompiled).** Not yet validated
> against real hardware — the command set, framing and connect sequence are transcribed from the vendor
> driver's `comms()` transport and property getters/setters; the exact `:04#` firmware name string and the
> DTR-reset requirement are confirmed once a unit is connected (see **Open items**).

## Serial parameters

| Parameter | Value |
|-----------|-------|
| Baud rate | 9600 (vendor default; configurable 9600–115200) |
| Data bits | 8 |
| Parity | None |
| Stop bits | 1 |
| Handshake | none; **DTR + RTS asserted** on open (resets the Arduino) |
| Encoding | ASCII |

After opening the port the Arduino auto-resets (DTR toggle) and ignores input until it has booted
(~2 s — the vendor driver's `DelayOnConnect` defaults to 2 s). The driver sleeps through this before the
first handshake.

## Framing

Commands are `:` … `#`; responses are `<status-char>` + payload + `#`.

- **Command (host → device):** `:` + numeric code + optional argument + `#` (e.g. `:05<pos>#`).
- **Response (device → host):** a single leading status char (a myFocuserPro2 response code) + the payload
  + `#`. The decoder strips the leading char **unconditionally** — it is never part of the value — exactly
  as the vendor driver's `Substring(1, len-2)` does. `ParsePayload` also tolerates the terminator being
  present or already stripped by the read.

Reads terminate on the `#` byte (`ProbeFraming.HashTerminated`, the same framing family as LX200). Reads
use the cancellable **synchronous** path (`ISerialConnection.SynchronousReads`) because CH34x bridges
spuriously abort async `BaseStream` reads (`ERROR_OPERATION_ABORTED`) after the first read.

## Get commands (request → response)

| Command | Reply (payload after strip) | Meaning |
|---------|-----------------------------|---------|
| `:02#`  | `OK` | Controller-present handshake. `OK` = a live myFocuserPro2 board. **This is the only identity check** — there is no Gemini-specific token on the wire. |
| `:04#`  | `<name>\r\n<version>` | Firmware name + integer version. The name is whatever myFocuserPro2 build was flashed (e.g. `myFP2ESP32`); the vendor driver stores but never validates it. |
| `:00#`  | `<int>` | Current absolute position (steps). |
| `:01#`  | `0` / `1` | Is-moving flag (`1` = moving). |
| `:06#`  | `<double>` | Temperature (°C). |
| `:08#`  | `<int>` | Maximum step position (`MaxStep`; also reported as `MaxIncrement`). |
| `:24#`  | `0` / `1` | Temperature compensation currently enabled. |
| `:25#`  | `0` / `1` | Temperature compensation available. |
| `:33#`  | `<double>` | Step size (microns). |

## Set commands

| Command | Reply | Meaning |
|---------|-------|---------|
| `:05<pos>#` | none (silent) | Move to absolute position `<pos>`. Poll `:01#` for completion. |
| `:27#` | none (silent) | Halt any motion. |
| `:230#` / `:231#` | `OK` | Temperature compensation **off** / **on** (this set command acks). |

**Move/Halt are silent fire-and-forget** (unlike the FlatPanel Lite, which acks everything), so the driver
does not wait on them — keeping the autofocus hot path fast. Only the temp-comp toggle replies, and the
codec drains that ack (bounded) so it can't offset the next read.

## Connect handshake

1. Open the port (9600-8N1, DTR + RTS asserted); wait ~2 s for the Arduino boot.
2. `:02#` — verify the payload is `OK` (else "not a Gemini Focuser").
3. `:04#` — log the firmware name + version.
4. `:08#` — cache `MaxStep` (`MaxIncrement` = `MaxStep`).
5. `:33#` — cache `StepSize` (a positive value sets `CanGetStepSize`).
6. `:25#` — cache `TempCompAvailable`.

Position, is-moving, temperature and temp-comp state are polled live thereafter.

## Mapping to `IFocuserDriver`

| `IFocuserDriver` member | Wire behaviour |
|-------------------------|----------------|
| `Absolute` | always `true` |
| `GetPositionAsync` | `:00#` (→ `int.MinValue` when unavailable) |
| `GetIsMovingAsync` | `:01#` |
| `GetTemperatureAsync` | `:06#` (→ `NaN` when unavailable) |
| `MaxStep` / `MaxIncrement` | `:08#` (cached at connect) |
| `StepSize` / `CanGetStepSize` | `:33#` (cached at connect) |
| `TempCompAvailable` | `:25#` (cached at connect) |
| `GetTempCompAsync` | `:24#` |
| `SetTempCompAsync` | `:231#` / `:230#` |
| `BeginMoveAsync(pos)` | `:05<pos>#` (clamped to `[0, MaxStep]`) |
| `BeginHaltAsync` | `:27#` |
| `BacklashStepsIn` / `BacklashStepsOut` | `-1` (unknown — TianWen's backlash auto-tuning owns it) |

## Discovery

Auto-detected by `GeminiFocuserSerialProbe` (`ISerialProbe`, `HashTerminated`, 9600 baud): it writes `:02#`
and matches the `OK` reply, then publishes a `Focuser://GeminiFocuserDevice/GeminiFocuser_<port>?port=serial:<port>`
URI with the `:04#` firmware name captured into metadata.

**No distinctive identity.** `:02#`→`OK` is the *generic* myFocuserPro2 handshake — any myFocuserPro2-based
controller answers it, and there is no Gemini-specific string on the wire. By design we treat any responder
as a Gemini Focuser Pro (we don't expect to encounter other myFocuserPro2 units in practice) and surface the
reported firmware name in metadata; if a real unit turns out to carry a distinctive flashed name, tightening
the matcher is a one-line change.

**DTR/RTS + boot delay.** Like the FlatPanel Lite, the probe declares `Warmup = 2200 ms` and
`AssertControlLines = true` (honoured only on the isolated per-probe pass, so toggling DTR can't reset a
different controller sharing the port on pass 1). Manual assignment
(`Focuser://GeminiFocuserDevice/…?port=serial:COMx`) also works — that path reconstructs the device from the
URI and the driver's own connect asserts DTR + boot-waits.

## Native-driver blacklist

When a native Gemini Focuser Pro is discovered, the `ASCOM.GeminiFocuserPro.Focuser` ASCOM driver is hidden
from discovery (`NativeDriverBlacklist`, keyed on ProgID → `GeminiFocuserDevice`), so the picker offers one
entry per physical focuser. If no native device is found, the ASCOM twin passes through as the fallback.

## Open items (hardware validation)

- **Exact `:04#` firmware name** — recorded in probe metadata; tighten the matcher only if a distinctive
  string appears.
- **DTR-reset requirement** — the vendor gates it behind a `ResetControllerOnConnect` setting; we default to
  assert + 2.2 s boot. Confirm the board needs it (and the exact boot time).
- **Whether Move/Halt are truly unacked on this firmware** — the vendor treats them as silent; if the board
  acks them, add a bounded drain to `SendAsync` (mirroring the temp-comp toggle / the FlatPanel path).
