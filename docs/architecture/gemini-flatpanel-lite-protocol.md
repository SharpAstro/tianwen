# Gemini FlatPanel Lite — serial protocol

Reference for TianWen's native `GeminiFlatPanelDriver` (`ICoverDriver`). The Gemini FlatPanel Lite is a
USB flat-field light panel: a driver-controlled electroluminescent panel with 0–255 brightness and **no
motorised cover flap**. In ASCOM terms it is a `CoverCalibrator` that is *calibrator-only* (it reports
`CoverStatus.NotPresent`). It presents as a virtual COM port (CH341 USB-to-serial bridge).

This document is the wire-format spec the driver implements; it is transport-only (no ASCOM dependency),
so the panel works on platforms without the ASCOM Platform (win-arm64, Linux, Raspberry Pi).

## Serial parameters

| Parameter | Value |
|-----------|-------|
| Baud rate | 9600 |
| Data bits | 8 |
| Parity | None |
| Stop bits | 1 |
| Handshake | none, but **DTR and RTS must be asserted** on open |
| Read/write timeout | 5 s |
| Encoding | ASCII |

After opening the port, allow the controller ~2 s to reset before the first command (the USB bridge
toggling DTR resets the MCU).

## Framing

Every message — in both directions — is `>` … `#`.

- **Command (host → device):** `>` + command letter + optional argument + `#`.
- **Response (device → host):** `>` + command letter (echoing the query) + payload + `#`.

Reads terminate on the `#` byte (`ProbeFraming.HashTerminated`, the same framing family as LX200). The
payload of a response is everything between the command letter and the `#`.

## Query commands (request → response)

Each query blocks for a `#`-terminated reply (retry with a short budget; the controller answers within a
few hundred ms once past the post-open reset).

| Command | Response | Meaning |
|---------|----------|---------|
| `>H#` | `>HGeminiFlatPanelLite#` | Identity handshake. The payload must equal `GeminiFlatPanelLite`; anything else is not this device. |
| `>V#` | `>V<int>#` | Firmware version (integer). Minimum supported firmware is **203**. |
| `>S#` | `>S<0\|1>#` | Light status. `1` = on, `0` = off (first payload char). |
| `>J#` | `>J<0-255>#` | Current brightness (0–255). |

## Action commands (fire-and-forget)

These are not acknowledged with a parsed reply; issue the write and allow ~100 ms to settle.

| Command | Meaning |
|---------|---------|
| `>L#` | Light **on** |
| `>D#` | Light **off** (dark) |
| `>B<n>#` | Set brightness `<n>` (decimal, 0–255) |
| `>Y0#` / `>Y1#` | Brightness mode **Low** / **High** |
| `>T0#` / `>T1#` | Panel beeper **off** / **on** |
| `>X<AAA><BBB><CCC><DDD><EEE>#` | Set the five IR-remote presets A–E, each a **3-digit zero-padded** brightness (e.g. `>X005025064128252#`) |

### Turning the panel on / off

- **On at brightness `n`:** `>L#`, then `>B<n>#`.
- **Off:** `>D#` (optionally followed by `>B0#`).

Brightness (`>B#`) and on/off (`>L#`/`>D#`) are independent: `>L#` restores the panel to its last set
brightness, and `>B<n>#` changes the level whether or not the light is currently on.

## Connect handshake

1. Open the port (9600-8N1, DTR + RTS asserted); wait ~2 s.
2. `>H#` — verify the identity payload is `GeminiFlatPanelLite`.
3. `>V#` — verify firmware ≥ 203.
4. `>S#` — read initial on/off status.
5. `>J#` — read initial brightness.

The panel is then ready. (The vendor app additionally pushes beep-mode, brightness-mode, and IR presets
at connect time; those are optional for flat capture.)

## Mapping to `ICoverDriver`

| `ICoverDriver` member | Wire behaviour |
|-----------------------|----------------|
| `GetCoverStateAsync` | always `CoverStatus.NotPresent` (no flap; no cover commands exist) |
| `GetCalibratorStateAsync` | `>S#` → `1` ⇒ `Ready`, `0` ⇒ `Off` |
| `GetBrightnessAsync` | `>J#` → 0–255 |
| `MaxBrightness` | `255` |
| `BeginCalibratorOn(n)` | `>L#` then `>B<n>#` |
| `BeginCalibratorOff` | `>D#` |
| `BeginOpen` / `BeginClose` | no-op (no flap) |

## Discovery

Auto-detected by `GeminiFlatPanelSerialProbe` (`ISerialProbe`, `HashTerminated`, 9600 baud): it writes
`>H#` and matches the `>HGeminiFlatPanelLite#` reply, then publishes a
`CoverCalibrator://GeminiDevice/Gemini_<port>?port=serial:<port>` URI. It shares the 9600-baud probe group with
the LX200-family mounts, so the port handle is opened once and reused.

**DTR/RTS during discovery.** The connect path asserts DTR + RTS (above). The discovery probe deliberately
does **not**: the probe service opens one shared serial handle per COM port and runs every 9600-baud probe
against it, and asserting DTR at that open could reset a DTR-triggered controller (e.g. some OnStep boards)
on another port. Consequence: if a panel only answers `>H#` with DTR asserted, auto-discovery may miss it —
assign it manually (`CoverCalibrator://GeminiDevice/…?port=serial:COMx`) and the driver's own connect (which does
assert DTR) drives it. In practice many CH341-bridged panels answer without DTR, so discovery usually works;
this is a hardware-validation item.
