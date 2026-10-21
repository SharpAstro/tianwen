# Power boxes: switches, dew heaters and the supply they measure

**Status: P1 BUILT (2026-10-21), P2-P8 NOT STARTED.** TianWen had a switch device type and nothing behind it:
`ISwitchDriver` declared no members, and the ASCOM and Alpaca switch drivers were empty. A power box is the first switch
the owner has (a Gemini Power&Data Hub Advanced 3), and it carries what a night runs on: the camera's 12 V, the USB
hub its data goes through, the dew heaters, and the only measurement of the supply, which is what the SV605CC's
cooler dropout needs to be read against.

## What shipped: the switch interface and the Gemini Advanced 3 (P1)

- **`ISwitchDriver`** is a list of `SwitchChannel`s (index, name, description, kind, range, step, unit, writable) and a
  get and a set per channel. It is shaped after ASCOM's ISwitchV2 so an ASCOM or Alpaca switch maps channel for channel;
  `SwitchChannelKind` (Output, Level, Reading) is what ISwitchV2 leaves a client to guess from a range. A driver with no
  channels yet lists none.
- **`GeminiPowerBoxDriver`**, native and ASCOM-free, found by `GeminiPowerBoxSerialProbe` (19200 baud, `>H#`). The wire:
  `docs/architecture/gemini-powerbox-adv3-protocol.md`. Verified on the owner's box (firmware 309, 2026-10-21): every
  channel read, USB F switched off and on three times, each confirmed by a status frame in about 215 ms.
- **What it does not do, deliberately:** it never writes a dew heater's mode (the vendor driver writes one on every
  connect), and it does not ask whether a run uses an output before switching it, which is the caller's question (P2).

## A switch on the node (P2)

Tracked by #1730. Since P6 of the hardware-in-the-server plan the hardware is the node's, so a client reaches a
switch only through it:

- routes for a switch's channels, their values and a set (`/api/v1/switches/...`), in the surfaces `NodeAccessGate`
  knows (reading is seeing, a set is a command);
- **a set is actuation, so it asks `DeviceOwnershipGate` first** and answers 409 naming the run, as every other
  actuation route does: switching off the port a running camera draws from, or the USB port its data uses, ends the
  run;
- the readings pushed as `DEVICE-STATE` on a change at the resolution they are shown, through
  `DeviceHubReadingExtensions`, like every other device read.

## The switch panel in the GUI (P3)

Tracked by #1731. One panel per switch, built from its channels, so it serves any power box, ASCOM and Alpaca
switches included, without a line per vendor:

- **Readings as gauges**: input voltage, output current and power, ambient temperature, humidity, dew point and the lens
  probe, each with its unit; a reading whose sensor is absent shows as absent, never 0.
- **Outputs as toggles**, an output the device keeps on (the Advanced 3's DC1) shown on and not pressable.
- **Levels as sliders** (the dew heaters), disabled with the device's reason where it will not take one (a dew heater
  the box runs in Auto).
- A refusal from the node (a run holds the box) is shown as its sentence, as other actuation refusals are.
- Where it lives (the Equipment tab, beside the device, and a compact supply readout on the Live Session tab) is decided
  with the owner when the panel is drawn.

## ASCOM and Alpaca switches (P4)

Tracked by #1732. `AscomSwitchDriver` and `AlpacaSwitchDriver` implement `ISwitchDriver` over ISwitchV2 and the
Alpaca switch API: `MaxSwitch`, `GetSwitchName`, `GetSwitchDescription`, `MinSwitchValue`/`MaxSwitchValue`,
`SwitchStep`, `CanWrite`, `GetSwitchValue`/`SetSwitchValue`. The kind is inferred: not writable is a Reading, a range
of 0 to 1 in steps of 1 an Output, anything else a Level; the unit is not in ISwitchV2 and stays empty. The SVBONY SV241
Pro's ASCOM driver is one to test it on (its native driver is a task of its own, unverified: the owner has the plain
SV241).

## The supply beside a run (P5)

Tracked by #1733. With a camera on a power box's port, the box's voltage and current are the supply that
camera's cooler draws from. A run records them beside the camera's cooler telemetry, so a cooler that drops to 0 % with
its exposures stopping (the SV605CC's fault in the field) can be read against what the supply did at that moment. Which
form (the session's telemetry history, a card in each light's header) is decided once the first fault is caught with
them.

## The rig's wiring (P6)

Tracked by #1734. The box knows its ports, not what is plugged into them, so nothing can yet say that switching DC2 off
cuts the focuser, and the profile has no place for a power box at all (`ProfileData` holds the mount, the guider and the
OTAs; an `OTAData` its camera, cover, focuser and filter wheel). So:

- **the profile holds its power boxes**, beside the mount and the guider;
- **every device of the profile names the outputs that feed it**: its POWER (a 12 V port) and its DATA (a USB port of
  the box's hub), each optional, so the mount, the guider camera, and each OTA's camera, focuser, filter wheel and cover
  say where they hang;
- **each OTA names its dew heater channels**, which is what P8 drives;
- the Equipment tab shows it as a **wiring diagram**: the box's outputs on one side, the rig's devices on the other, a
  line for each feed, edited by drawing the line.

That lets the panel label a port by what it powers, lets the ownership gate refuse a set on a port that feeds a device a
run holds (rather than every set while any run holds the box), and is what P7 and P8 act through.

## The session drives the box (P7)

Tracked by #1735. With the wiring known, the session loop switches what it uses, as part of the ONE order a
run starts and ends in:

- **power on before connecting**: the ports of the devices the run will use, in an order (power, then data), waiting for
  each device to appear before its connect, instead of everything on all night;
- **power off at the end, inside `RigShutdown`**, after the cameras have warmed (never cut a cold camera's 12 V, which
  is a thermal shock to the sensor) and after the mount is parked;
- **a power cycle as the last recovery rung** for a device that has stopped answering and cannot reset itself from
  software (`CanResetDevice` false, as on the SV605CC): off, wait, on, reconnect, restore its settings, as the DAL's
  reset rung does for a ToupTek; a cooled camera only once it is warm enough;
- **flats and the cover**: a flat run powers what it uses and nothing more.

## Dew heater control (P8)

Tracked by #1736. Each OTA's dew heaters run against the margin between its lens temperature (the box's probe,
else the ambient) and the dew point (the box's sensor, else the weather device), stepped up as the margin closes and
down as it opens, a cycle the session owns rather than a constant 100 % all night. The box's own Auto mode is the
alternative, and which to use is a profile choice per OTA: the session's control knows which OTA a heater is wrapped
around and logs what it did, the box's does not. Heaters go off for flats (a warm tube changes the flat) and at the end
of the run.
