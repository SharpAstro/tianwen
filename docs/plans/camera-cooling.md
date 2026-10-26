# Camera cooling: a gentle rate, one warm-up, a real ambient

Milestone: `camera-cooling`. Opened 2026-10-26, after `tianwen darks --setpoint` sent the dark run through the same ramps and
the owner asked how they compare with N.I.N.A.'s cooling duration with respect to thermal stress. Cooled cameras are not
cheap: this plan is about how fast a sensor's temperature is made to change, and what it is warmed back to.

## What there is today

- **One cool-down, `CameraCoolingRamp`**, used by the session, the node's cool job (`POST /devices/camera/cool`), the
  crash journal's recovery and the dark run. Each step (every 15 s) sets the setpoint one degree past the sensor's own
  reading (`SetupointDirection.SetpointTemp`), so the setpoint never runs ahead of a cooler that cannot keep up, and the
  ramp stops stepping once the cooler passes 80 % power (`CoolDownPowerThreshold`).
- **Two warm-ups**: the session's, through the same ramp towards ambient (`CoolCamerasToAmbientAsync`), and the hub's,
  2 °C every 30 s for at most 30 steps then the cooler off (`DeviceHubCameraSafetyExtensions.WarmCameraAsync`), which
  serves every warm-up outside a session.
- **Two settings that do not do what they say**: the session tab's `CooldownRampInterval` and `WarmupRampInterval` (5 min
  each) only cap a ramp, which gives up at twice the budget; neither slows it.

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

## One warm-up

The hub's warm-up and the session's are two rules for one act, and a rate can only be honoured by one. The session's ramp
becomes the only warm-up, with the hub's lease check (a held camera is refused unless its holder asks, as a dark library
warming the camera it holds does), its cooler-off at the end, and the cooler intent recorded as Warm then Off.
Issue: #1840.

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
