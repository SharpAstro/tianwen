# Camera cooling: a gentle rate, one warm-up, a real ambient

Milestone: `camera-cooling`. Opened 2026-10-26, after `tianwen darks --setpoint` sent the dark run through the same ramps and
the owner asked how they compare with N.I.N.A.'s cooling duration with respect to thermal stress. Cooled cameras are not
cheap: this plan is about how fast a sensor's temperature is made to change, and what it is warmed back to.

## What there is today

- **One cool-down, `CameraCoolingRamp`**, used by the session, the node's cool job (`POST /devices/camera/cool`), the
  crash journal's recovery and the dark run. Each step (every 15 s) sets the setpoint one degree past the sensor's own
  reading (`SetupointDirection.SetpointTemp`), so the setpoint never runs ahead of a cooler that cannot keep up, and the
  ramp stops stepping once the cooler passes 80 % power (`CoolDownPowerThreshold`).
- **One warm-up**: `CameraCoolingRamp` towards ambient, for the session (`CoolCamerasToAmbientAsync`) and for the hub
  (`DeviceHubCameraSafetyExtensions.WarmCameraAsync`), which serves every warm-up outside a session. The hub used to step
  2 °C every 30 s of its own.
- **Two settings that did not do what they said** (retired by #1839): `CooldownRampInterval` and `WarmupRampInterval` (5 min
  each) only capped a ramp. They are now `CooldownMaxRate` and `WarmupMaxRate` in °C a minute (default 2), carried by the
  saved session (an old file with ramp seconds loads the defaults), `SessionConfigApiDto`, the cool request's optional
  `MaxRateCPerMinute` and the GUI's "Cooling Rates" rows (0.5 °C/min steps, 0.5 to 4).

Measured on the QHY294PROC on 2026-10-26, cooling 16 to 10 °C: 16.0 to 9.4 °C in about 1.5 min, 1.1 to 1.2 °C per 15 s step
(about 4.5 °C a minute), 0.6 °C under before settling at 10.0, cooler power never above 20 %. The camera reports no heat sink.

## Rate, not duration

Thermal stress is the RATE a sensor's temperature changes at, not how long a ramp takes. Today's rate is about 4 °C a minute
whatever the drop, and nothing can ask for a gentler one. N.I.N.A.'s cooling duration spreads the change over the time given,
open-loop on the clock as far as is known here (to be read in its source): its rate is the drop divided by the duration, gentle
for a small drop and as fast as ours for a large one, and a short duration with a large drop runs the setpoint ahead of the
sensor and drives the cooler flat out.

The change keeps what protects the camera (the setpoint bounded by the sensor, the 80 % power stop) and replaces the two
interval settings with a maximum rate for cooling and for warming, in °C a minute. A minimum duration for a large drop then
follows from the rate, without slowing a small one needlessly. Tests run on the fake clock only: no test waits out a ramp.
Issue: #1839.

**Shipped (#1839).** A step moves the setpoint min(1 °C, rate x step interval) past the sensor, so the ceiling holds on
the fake clock and a small drop is not slowed; a rate of 4 °C a minute or more is a degree a step, the ramp as it was, so the
GUI offers nothing faster. The default of 2 °C a minute is about half the 4.5 °C a minute measured above. A planned ramp is
drop / rate, planned as a 30 °C drop when the sensor's first reading fails. The safety cap is twice the plan plus the four
steps that confirm the target once there, and never under 5 minutes (a 30 °C warm-up plans 15 minutes and is capped at 31), so
a ramp whose sensor follows its setpoint always ends on its own exit and only a cooler that does not follow reaches the cap. A
rate that is not finite, or not above zero, is its direction's default, checked once in `CameraCoolingRamp.RunAsync` for
every caller.

The ceiling holds less in two places. On a whole-degree camera (below) the next setpoint is rounded from the sensor's
reading, so a sensor lagging half a degree behind its last setpoint is given one up to 1.5 °C past the reading and held for a
setpoint's hold: up to 3 °C a minute over that hold at the default, 2 on average across it. And an ASCOM or Alpaca camera says
`SetpointResolutionC` 0, so it is now sent half-degree setpoints where every setpoint used to be whole: a driver over a
whole-degree SDK that truncates would stall at 0 °C as the DAL did, and nothing here detects it (the setpoint it reads back
may only echo what was set). Not checked against a real driver: trying a fractional setpoint on one ASCOM or Alpaca camera is
#1921.

**A camera that takes whole degrees only is held by time, not by a fraction.** Every vendor SDK behind `DALCameraDriver`
takes a whole-degree target and truncates a fractional one toward zero, which undoes half a step AWAY from zero (a cool-down
stalls at 0 °C, a warm-up never leaves it, and above 0 °C the sensor moves a degree a step whatever the rate). So such a
camera says `ICameraDriver.SetpointResolutionC` (1), and `CameraCoolingRamp` keeps its whole-degree step but holds each setpoint
for 1 °C / rate minutes, a whole number of step intervals rounded up (2 °C/min: two 15 s steps), still polling every step. The
setpoint is rounded to the sensor's side of a half degree, never a bare `(int)`. `FakeCameraDriver.MakeWholeDegreeSetpoint`
quantises exactly as the DAL does, and fake-clock tests cool 20 to -10 and -5 to -10 and warm -10 to 20 and 5 to 20.

The setting is `SessionConfiguration.CooldownMaxRate` / `WarmupMaxRate` (°C per minute): saved in the session file (an older file
with ramp seconds loads with the default rates, as does a rate of 0 or less), on the node's session config wire, in the cool
job's optional `MaxRateCPerMinute`, and in the GUI's session rows (0.5 to 4 °C/min in 0.5 steps).

## One warm-up

The hub's warm-up and the session's are two rules for one act, and a rate can only be honoured by one. The session's ramp
becomes the only warm-up, with the hub's lease check (a held camera is refused unless its holder asks, as a dark library
warming the camera it holds does), its cooler-off at the end, and the cooler intent recorded as Warm then Off.
Issue: #1840.

**Shipped, the ramp side (#1840).** `CameraCoolingRamp` now does the whole warm-up any host needs. An ambient target with no
usable heat sink resolves to `CameraReading.WarmUpTargetC(NaN)`, the +25 C fallback, in `ICameraDriver.CoolToSetpointAsync`
(it was NaN, which made the camera "not coolable", so a session never warmed a heatsinkless camera at all). A warm-up ends
once the cooler is no longer holding the sensor down: power at or below `CameraCoolingRamp.WarmUpPowerThreshold` (1 %) for
the ramp's consecutive-step count, whatever the sensor reads, so the QHY294PROC case of 2026-10-26 (15 minutes stepping at 0 %
toward an unreachable +25 C) costs a few steps, not the safety cap. `Session.CoolCamerasToAmbientAsync` passes that constant.
Pinned by `CameraCoolingRampTests`. 
**Shipped, the hub side (#1840).** `WarmCameraAsync` runs `CameraCoolingRamp.RunAsync` (Ambient, upward, `WarmUpPowerThreshold`,
`WarmupMaxRate` as the ceiling on its rate, #1839) and keeps the lease check, the no-cooler and cooler-off skips, the intent recorded as Warm
then Off, the cooler switched off at the end (also when the ramp fails) and the optional disconnect. Pinned by
`EquipmentWarmUpTests` (15 s steps, a held camera refused, a cooler stuck at 0 % ending in a few steps).

## The profile's settings everywhere

The node's cool job, the crash journal's recovery and the dark run ramp with the declared default settings, never the ones
the user set; only a session run reads them. Every path that moves a cooler takes the active profile's cooling settings.
Issue: #1841.

## Where ambient comes from

A warm-up aims for the heat sink's temperature, and most cameras have none: QHY and ZWO report the sensor's temperature,
cooler power and target only, PlayerOne and SVBony nothing more, so the DAL driver answers NaN and the warm-up aims for its
+25 °C fallback. A cooler cannot warm a sensor above ambient, so on a cold night the hub's warm-up steps until its 15 minute
cap. In order of trust, the target becomes:

1. the camera's own heat sink, where it has one (ASCOM's `HeatSinkTemperature`);
2. the camera's own environment sensor, where the SDK has one (ToupTek's `TOUPCAM_OPTION_ENV_HT`, which the DAL does not
   pass on yet): #1843;
3. a device on the rig that reads ambient once it is operational: a power box's probe, a focuser's temperature, a weather
   device, passed by the orchestration, never driver to driver: #388;
4. the sensor's own temperature when a cool-down started with the cooler off, ambient plus a little self-heating, kept beside
   the cooler intent so the crash journal carries it: #1842.
