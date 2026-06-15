# Fake Mount/Camera Disturbance Model

**Status: PROPOSED (design under review).** First step landed pragmatically: the neural-vs-P
comparison test (`GuideLoopTests.GivenSameScenarioWhenNeuralPlusPVsPOnlyThenNeuralIsNotWorse`) is
migrated onto the coupling harness via a new `SetupCoupledGuidedMount` helper (misaligned
`FakeSkywatcher` + coupled `FakeCamera`, loop closed through mount pulses). It now records ~99 real
guide samples per run instead of the 2-sample vacuity, and asserts `TotalSamples > 50`. This is the
targeted slice of "Migration plan" step 6 using the *existing* fakes -- the full `IDisturbanceTerm`
refactor (steps 1-5, 7) and migrating the other `SetupGuidedMount`-based tests are still open.

A coherent way to model guiding disturbances (periodic error, polar misalignment,
flexure, cable snag, wind, atmospheric seeing, gear noise) across the fake mount and
fake camera, so that:

- the simulated guide star behaves like a real one (stays in frame, drifts realistically),
- the guide loop can correct what is physically correctable and cannot correct what isn't,
- there is **one** disturbance model, not three overlapping ones.

## Why this exists (the problem)

Today disturbances live in three disconnected places with different fidelity and, in one
case, wrong physics:

| Where | Models | Fidelity | Consumed by |
|---|---|---|---|
| `FakeMountDriver._accumulated*` (`UpdateTrackingState`) | sidereal, PE, polar drift, wind, cable-snag, flexure, gear-noise -- all summed into `GetRA/Dec` | crude (constant-rate); **sidereal is added to reported RA, which is physically wrong for a tracking mount** | hand-rolled `GuideLoopTests.SetupGuidedMount.RenderFrame` |
| `FakeSkywatcherMountDriver` | polar misalignment (real az/alt axis tilt) via the believed/true seam | high | `FakeCameraMountCouplingTests` |
| `FakeCameraDriver` | worm PE (positional, from RA encoder), seeing jitter hook, ST-4 integrator | medium | `FakeCameraMountCouplingTests` |

The same physical effect (periodic error) is modelled in two places; sidereal is a
disturbance in one model and absent in the other; seeing exists only on the camera.

The concrete failure this surfaced: the neural-vs-P comparison test
(`GivenSameScenarioWhenNeuralPlusPVsPOnlyThenNeuralIsNotWorse`) records only **2 error
samples over 360 frames**. `FakeMountDriver.GetRightAscensionAsync` returns
`_ra + _accumulatedRaHours`, and `_accumulatedRaHours` includes the full sidereal advance
(`SIDEREAL_RATE_HOURS_PER_SECOND * elapsed`). With a 2 s exposure that is
`24/86164 h/s * 2 s * 15 * 3600 = 30.1" = 20.0 px` of RA motion per frame, against a 16 px
tracker search radius and a guide-rate correction capped at ~13 px/frame (and ~0 on the
first frame, since a P-controller's correction is proportional to a near-zero initial
error). The star leaves the ROI after ~2 frames and never returns. The RMS "comparison" is
two early samples -- both runs identical, ratio 1.000 -- and the guardrail proves nothing.

## Core principle: the believed/true split is the universal carrier

`IFakeTruePointingSource` already exists, but only carries polar misalignment today. Promote
it to the single carrier for **every** mount-mechanical error:

```
believed_pointing(t)  = encoders: slews + guide pulses; tracks sidereal perfectly
                        => the sky RA/Dec the mount THINKS it points at stays ~constant
                           while tracking. A real mount can only report this.

true_pointing(t)      = believed_pointing(t) + SUM( mount_mechanical_errors(ctx) )
                        => where the optical axis actually points.
                        => only a camera/plate-solve can witness it (as in reality).
```

Two consequences kill the current bugs:

- **Sidereal is not a disturbance term.** A tracking mount holds sky-RA constant; the guide
  render subtracts a captured reference, so the sidereal baseline cancels structurally.
  `FakeMountDriver` adding `SIDEREAL_RATE * elapsed` to reported RA is simply the wrong
  model and is removed.
- **Guide pulses move `believed`, which moves `true`, which moves the rendered star.** The
  loop closes through the mount exactly as `FakeSkywatcher` + `FakeCamera` already do.

## The optical chain and where disturbances inject

```mermaid
flowchart LR
    sky[Sky / target] --> atm[Atmosphere]
    atm --> axis[Mount axes\nRA/Dec encoders]
    axis --> drive[Drivetrain\nworm + gears]
    drive --> tube[Optical tube\nflexure points]
    tube --> ao[Tip-tilt / AO\noptional, fast]
    ao --> sensor[Sensor]

    atm -. seeing .-> atm
    axis -. polar misalignment, cone .-> axis
    drive -. periodic error, backlash, gear noise .-> drive
    tube -. flexure, cable snag .-> tube
```

Disturbances entering at the **axis / drivetrain / optical-tube** stages move the optical
axis: they show up as a `believed -> true` pointing delta and a mount pulse (which moves the
axis) can null them. Disturbances entering at the **atmosphere** stage move only the apparent
centroid: they are *not* reflected in pointing, so a mount pulse cannot null them -- and
chasing them just injects the seeing variance into the corrections.

## Correctability is derived, not declared

We deliberately do **not** tag a term "uncorrectable." Seeing is uncorrectable *with a mount
at ~0.5 Hz*, but a fast tip-tilt / adaptive-optics actuator (or, for differential flexure, an
on-axis guider) changes that. So correctability is computed from two facts:

```
a term is correctable by actuator A  <=>  A.stage is at or downstream of term.stage
                                          AND A.bandwidth >= term.bandwidth
```

- **Mount pulse actuator**: stage = `MountAxis`, bandwidth ~ 0.5 Hz. Reaches every
  pointing-stage term (axis/drivetrain/tube) that is slow enough; misses fast gear noise and
  anything at the atmosphere stage.
- **Tip-tilt / AO actuator (future)**: stage = `Sensor`-adjacent, bandwidth ~ 50-1000 Hz.
  Reaches the atmosphere stage too -- so adding it flips seeing to "correctable" with **no
  change to any disturbance term.** That is the whole point of deriving it.

## Components

```
IDisturbanceTerm                     // shared abstraction for ALL disturbances
    Stage      : DisturbanceStage    // Atmosphere | MountAxis | Drivetrain | OpticalTube
    Character  : DisturbanceCharacter// drift | periodic | impulse | stochastic (+ correlation time)
    Evaluate(DisturbanceContext ctx) : (dRaArcsec, dDecArcsec)   // native frame, deterministic
    // implementations: PeriodicError, PolarMisalignment, Flexure, CableSnag, Backlash,
    //                   WindGust, GearNoise, AtmosphericSeeing

DisturbanceContext                   // everything a term needs to evaluate
    { elapsedSeconds, raWormPhase, hourAngle, pierSide, lastPulseRa, lastPulseDec }

DisturbanceModel                     // ONE ordered list of IDisturbanceTerm, deterministic, pure
    PointingDelta(ctx)  = SUM(term.Evaluate(ctx) for term where term.Stage != Atmosphere)
    SensorDelta(ctx)    = SUM(term.Evaluate(ctx) for term where term.Stage == Atmosphere)

CorrectionActuator                   // mount pulse now; AO later
    { Stage, BandwidthHz }
    Corrects(term)  =>  Stage >= term.Stage && BandwidthHz >= term.BandwidthHz
```

Placement:

- **Fake mount** (both `FakeMountDriver` and `FakeSkywatcherMountDriver`) holds a
  `DisturbanceModel`. `IFakeTruePointingSource.GetTruePointingNativeAsync` returns
  `believed + model.PointingDelta(ctx)`. Public `GetRA/Dec` return believed only.
- **Fake camera** reads true pointing for the guide cam (it already does), projects relative
  to the captured reference, then adds `model.SensorDelta(ctx)` (seeing) post-projection.
- A single `IDisturbanceTerm` list is configured per device from `DeviceQueryKey` URI keys
  (the existing pattern: `PePeakTopeakArcsec`, `PePeriodSeconds`,
  `polarMisalignmentAzArcmin`, etc.), so both fake mounts and the camera read the same knobs.

## Loop closure (uniform, mechanism unchanged)

```mermaid
sequenceDiagram
    participant GL as GuideLoop
    participant Cam as FakeCamera
    participant Mnt as Fake mount (believed)
    participant Mdl as DisturbanceModel

    GL->>Cam: PulseGuide(dir, ms)
    Cam->>Mnt: ST-4 pulse -> believed += correction
    GL->>Cam: GetImage (next exposure)
    Cam->>Mnt: read true = believed + Mdl.PointingDelta(ctx)
    Cam->>Mdl: SensorDelta(ctx)  (seeing)
    Cam-->>GL: star at project(true - reference) + sensorDelta
    Note over GL: correctable pointing error shrinks;\nseeing remains as a noise floor
```

## Term catalog

| Term | Stage | Character | Correctable by mount pulse? | Source today |
|---|---|---|:--:|---|
| Periodic error | Drivetrain | periodic (worm phase) | yes | `FakeCamera` (positional) + `FakeMountDriver` (time-based) -- unify on positional |
| Polar misalignment | MountAxis | drift (HA-dependent) | yes | `FakeSkywatcher` (real tilt) -- keep, make a term |
| Flexure | OpticalTube | drift (HA-dependent) | yes | `FakeMountDriver` |
| Cable snag | OpticalTube | impulse (at HA/time) | yes (as a step) | `FakeMountDriver` |
| Backlash | Drivetrain | dead-zone on correction | partially (interacts with pulses) | none (mounts) |
| Wind gust | OpticalTube | stochastic (OU, slow) | partially (bandwidth-limited) | `FakeMountDriver` |
| Gear noise | Drivetrain | stochastic (OU, fast) | no (too fast for 0.5 Hz) | `FakeMountDriver` |
| Atmospheric seeing | Atmosphere | stochastic (zero-mean, per-frame) | no with mount; yes with AO | `FakeCamera` render hook |

## Migration plan

| # | Step | Size |
|---|---|---|
| 1 | Introduce `IDisturbanceTerm`, `DisturbanceStage`, `DisturbanceCharacter`, `DisturbanceContext`, `DisturbanceModel`, `CorrectionActuator` (pure, in `TianWen.Lib/Devices/Fake/Disturbance/`) | M |
| 2 | Port the existing math into terms: `PeriodicError`, `PolarMisalignment`, `Flexure`, `CableSnag`, `WindGust`, `GearNoise`, `AtmosphericSeeing` (lift from `FakeMountDriver.UpdateTrackingState` + the `FakeCamera` PE + `SyntheticStarFieldRenderer` seeing hook) | M |
| 3 | `FakeSkywatcherMountDriver` composes a `DisturbanceModel`; `GetTruePointingNativeAsync` = believed + `PointingDelta`. Move worm PE from the camera to a Drivetrain term so it is positional in one place. | M |
| 4 | `FakeCameraDriver` render: pointing from the true seam, plus `SensorDelta` (seeing) added post-projection. Drop the camera-side PE once it is a mount term. | S |
| 5 | Retire `FakeMountDriver._accumulated*` -- either delete it (if all tests migrate) or reimplement `UpdateTrackingState` to compose the same `DisturbanceModel`. **Remove the sidereal-into-RA term entirely.** | M |
| 6 | Migrate `GuideLoopTests.SetupGuidedMount` to drive frames through a `FakeCamera` coupled to a `FakeSkywatcher` via `DeviceHub` (the `FakeCameraMountCouplingTests` harness). Re-baseline RMS thresholds against the now-~360-sample runs. | L |
| 7 | Add `WindGust` + `AtmosphericSeeing` knobs to the coupling path if the neural guardrail needs its full disturbance palette (they exist on `FakeMountDriver` today; they need to be terms the coupling harness can configure). | S |

## Test impact

- The neural-vs-P comparison and the other `SetupGuidedMount`-based tests
  (`GivenWindGusts...`, `GivenCableSnag...`, `GivenCombinedDisturbances...`) currently record
  ~2 samples; after migration they record ~360 and their RMS assertions become meaningful.
  Thresholds must be re-baselined in the same change (expect them to move).
- `FakeCameraMountCouplingTests` should pass unchanged -- it already uses the believed/true
  seam; it just gains more term types behind the same coupling.
- `FakeSkywatcherMisalignmentTests` (pure static math) and `PolarAlignmentSessionTests`
  (NSubstitute + synthetic source) are unaffected.

## Verification

1. `cd src && dotnet build` -- zero warnings.
2. `dotnet test TianWen.Lib.Tests` then `dotnet test TianWen.Lib.Tests.Functional` (sequential, never parallel suites).
3. The migrated comparison test reports ~360 samples (not 2) and a non-trivial neural-vs-P ratio.
4. Live sanity with the fake SkyWatcher profile (run-gui + inspector): guiding stays bounded under PE + polar misalignment + (added) wind/seeing.

## Out of scope

- Real adaptive-optics / tip-tilt hardware drivers. The `CorrectionActuator` abstraction
  leaves room for one, but only the mount-pulse actuator exists initially.
- On-axis guider differential-flexure modelling (a separate optical-train concern).
