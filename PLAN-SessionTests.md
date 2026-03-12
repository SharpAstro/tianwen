# Session Testing & Auto-Focus Plan

## Amended Plan — 7 Phases

### Phase 1: FakeGuider State Machine

**File**: `TianWen.Lib/Devices/Fake/FakeGuider.cs`

Currently all methods throw `NotImplementedException`. Rewrite as a state machine:
- States: `Idle`, `Looping`, `Calibrating`, `Guiding`, `Settling`
- Use `Interlocked` for thread-safe state transitions (same pattern as `FakeCameraDriver`)
- Use `ITimer` from `External.TimeProvider` for settle simulation
- `GuideAsync` transitions through Calibrating → Guiding with timer
- `DitherAsync` sets Settling, timer fires Done after `settleTime`
- Raise `GuiderStateChangedEvent` on transitions

### Phase 2: FakeCamera Cooling Simulation

**File**: `TianWen.Lib/Devices/Fake/FakeCameraDriver.cs`

Add fields: `_ccdTemperature = 20.0`, `_heatsinkTemperature = 20.0`,
`_setpointTemperature = 20.0`, `_coolerOn`, `_coolerPower`.

`GetCCDTemperatureAsync` moves 1°C toward setpoint per call when cooler is on.
This tests Session's actual cooling ramp loop.

### Phase 3: FakeFocuser Temperature + Focus Model

**File**: `TianWen.Lib/Devices/Fake/FakeFocuserDriver.cs`

The fake focuser needs to be a physically plausible simulation:

```
Fields:
  _temperature: double = 15.0        // ambient start
  _tempDriftRate: double = -0.5      // °C per hour (cooling overnight)
  _baseBestFocus: int = 1000         // perfect focus at base temp
  _tempCoefficient: double = 5.0     // steps per °C of focus shift
  _lastDirection: int = 0            // +1 or -1, for backlash tracking
  _backlashSteps: int = 0            // consumed backlash pending
  _trueBacklash: int = 20            // actual mechanical backlash
```

**Temperature model**: Each call to `GetTemperatureAsync()` computes temp from
`baseTemp + driftRate * elapsedHours` (using `External.TimeProvider`).

**True best focus**: `_baseBestFocus + (int)(_tempCoefficient * (currentTemp - baseTemp))`
— shifts with temperature, simulating thermal expansion.

**Backlash model**: When `BeginMoveAsync` reverses direction, the first
`_trueBacklash` steps of movement produce no actual position change. The driver
tracks `_lastDirection` and `_backlashSteps` (consumed backlash remaining).

**Public properties** (for test assertions):
- `TrueBestFocus` — the current best focus position accounting for temperature
- `TrueBacklash` — settable in test setup

### Phase 4: Star Field Generation

**New file**: `TianWen.Lib/Devices/Fake/SyntheticStarFieldRenderer.cs`

Pure static function — no state, testable independently.

```csharp
internal static class SyntheticStarFieldRenderer
{
    public static float[,] Render(
        ICelestialObjectDB db,
        double centerRA,          // hours (from camera.Target.RA)
        double centerDec,         // degrees (from camera.Target.Dec)
        int width, int height,
        double pixelScaleArcsec,  // from PixelSizeX * BinX / FocalLength * 206.265
        double defocusSteps,      // abs(focuserPos - trueBestFocus)
        double hyperbolaA,        // min FWHM in pixels at perfect focus (~2.0)
        double hyperbolaB,        // asymptote scaling (~50 steps)
        TimeSpan exposure,
        double fRatio,
        int gain, int offset,
        int seed = 0)
```

**Data flow — no extra wiring needed**: Session already sets `camera.Target`,
`camera.FocalLength`, `camera.Latitude`, `camera.Longitude`, `camera.FocusPosition`
before each exposure. The FakeCameraDriver reads these in `StopExposureCore` to
call the renderer. Only `ICelestialObjectDB` needs injection (property setter).

**Signal model**:
- Point sources (stars): `flux = baseFlux * 10^(-0.4 * vMag) * exposure.TotalSeconds / fRatio²`
- PSF FWHM from defocus: `fwhm = hyperbolaA * cosh(asinh(defocusSteps / hyperbolaB))`
  (reuses `Hyperbola.CalculateValueAtPosition` math)
- Render as 2D Gaussian with computed FWHM, distribute flux over PSF area
- Extended objects: elliptical Gaussian from `CelestialObjectShape`, surface brightness
  from catalog (invariant of aperture), values above sky background
- Sky background: `skyBg = baseSky * exposure.TotalSeconds / fRatio²`
- Read noise: Gaussian σ ≈ 5 ADU (constant per read)
- Shot noise: Poisson √(signal + sky + dark)
- Dark current: `darkRate * exposure.TotalSeconds` (small)

**Gnomonic projection**: Copy from `CatalogPlateSolver.ProjectCatalogStars` — same
`ξ = cosδ sin(Δα) / cosC`, `η = (cosδ₀ sinδ - sinδ₀ cosδ cosΔα) / cosC` math.

**Integration**: In `FakeCameraDriver.StopExposureCore`, if `CelestialObjectDB` and
`Target` are both set, call `SyntheticStarFieldRenderer.Render(...)`. The focuser
position and true best focus determine defocus. If not set, fall back to current
random data.

### Phase 5: Backlash Measurement & Compensation

**New file**: `TianWen.Lib/Astrometry/Focus/BacklashMeasurement.cs`

```csharp
public static class BacklashMeasurement
{
    /// <summary>
    /// Measures mechanical backlash by:
    /// 1. Move to startPos, take exposure, measure HFD, fit rough focus
    /// 2. Move outward by range steps, back inward to startPos
    /// 3. Measure HFD again — delta in apparent best focus = backlash
    /// </summary>
    public static async Task<int> MeasureAsync(
        IFocuserDriver focuser,
        ICameraDriver camera,
        int startPos,
        int range,
        TimeProvider timeProvider,
        CancellationToken ct)
```

**SessionConfiguration additions**:
```csharp
int BacklashSteps             // 0 = unknown
bool MeasureBacklashIfUnknown // default true
```

**Backlash-compensated moves**: New extension or helper method:
```csharp
public static async Task MoveWithBacklashCompensationAsync(
    this IFocuserDriver focuser,
    int targetPosition,
    int backlashSteps,
    CancellationToken ct)
```
When reversing direction: overshoot by `backlashSteps`, then return to target.
Always approach from the same direction (convention: approach from below).

### Phase 6: Focus Drift Detection & Auto-Refocus

**File**: `TianWen.Lib/Sequencing/Session.cs` — new methods in the Session class

**Focus drift detection** — in `ImagingLoopAsync`, after each successful image fetch:
```csharp
var stars = await image.FindStarsAsync(channel: 0, snrMin: 10f, maxStars: 200, ct);
if (stars.Count >= 10)
{
    var currentHFD = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
    if (baselineHFD > 0 && currentHFD > baselineHFD * 1.3f) // 30% degradation
    {
        // trigger refocus
    }
}
```

**Auto-refocus** — new `AutoFocusAsync` method:
```csharp
internal async Task<FocusSolution?> AutoFocusAsync(
    OTA telescope,
    int backlashSteps,
    CancellationToken ct)
```
Algorithm:
1. Record current focuser position as center
2. Define scan range (e.g., ±200 steps in 9 positions)
3. For each position (with backlash compensation):
   a. Move focuser
   b. Take short exposure (1-2s)
   c. Detect stars, measure median HFD
   d. Add to `MetricSampleMap`
4. Call `TryGetBestFocusSolution()` — uses existing hyperbola fitting
5. Move to `BestFocus` position (with backlash compensation)
6. Update `baselineHFD` with the fitted minimum `A` parameter

**First focus run at session start** (`InitialRoughFocusAsync` replacement):
1. If `config.BacklashSteps == 0 && config.MeasureBacklashIfUnknown`:
   Run `BacklashMeasurement.MeasureAsync()`, store result
2. Run full `AutoFocusAsync` to establish baseline
3. Store `baselineHFD` per camera

### Phase 7: Observation Duration + PeriodicTimer + Session Tests

**7a. Observation Duration**

In `ImagingLoopAsync`, add elapsed time tracking:
- When `observation.Duration != TimeSpan.MaxValue` and elapsed >= Duration, break
- `TimeSpan.MaxValue` = "as long as possible" (bounded by session end time)

**7b. PeriodicTimer**

Replace overslept pattern in `ImagingLoopAsync`:
```csharp
using var ticker = new PeriodicTimer(tickDuration, External.TimeProvider);
// ... start exposures, write images ...
await ticker.WaitForNextTickAsync(cancellationToken);
// ... fetch images ...
```
Removes `overslept` variable and `SleepWithOvertimeAsync` calls.
`FakeTimeProvider.Advance()` triggers `WaitForNextTickAsync` completion.

**7c. Session Tests** — new `SessionTests.cs`

Construct `Session` directly with fake devices (internal, visible to test project).

| # | Test | Phases Required |
|---|------|-----------------|
| 1 | Single observation lifecycle | 1, 2 |
| 2 | Multiple observation sequencing | 1, 2 |
| 3 | Cancellation → finalize | 1, 2 |
| 4 | Twilight boundary stop | 1, 2 |
| 5 | Camera cooling ramp | 2 |
| 6 | Observation duration limit | 7a |
| 7 | Star field contains detectable stars | 3, 4 |
| 8 | Star field is plate-solvable | 3, 4 |
| 9 | Focus drift triggers auto-refocus | 3, 4, 6 |
| 10 | Backlash measurement converges | 3, 4, 5 |
| 11 | Auto-focus finds correct position | 3, 4, 5, 6 |
| 12 | Temperature drift causes focus shift | 3, 4, 6 |

**Test 9 detail**: Set `FakeFocuserDriver._tempDriftRate` high so temperature
changes rapidly. Advance FakeTimeProvider during imaging loop. Star field
FWHM grows as true best focus drifts. Session detects 30% HFD increase,
triggers auto-refocus. Assert focuser moves to new best position.

**Test 11 detail**: Start focuser at position 800, true best focus at 1000.
Run `AutoFocusAsync`. Assert `FocusSolution.BestFocus` is within ±5 steps of
1000. Assert focuser is at the solution position.

**Test 12 detail**: Start at best focus. Advance time so temperature drops 2°C.
True best focus shifts by `2 * tempCoefficient = 10` steps. Take exposure.
Assert median HFD is larger than baseline. Trigger refocus. Assert new
focuser position tracks the shifted best focus.

## Dependency Graph

```
Phase 1 (FakeGuider) ─────────┐
Phase 2 (FakeCamera cooling) ──┤
Phase 3 (FakeFocuser temp) ────┼──> Phase 7c (basic tests 1-6)
Phase 7a (Observation Duration)─┘
Phase 4 (Star field renderer) ──┬──> Phase 7c (tests 7-8)
Phase 5 (Backlash) ─────────────┤
Phase 6 (Auto-focus) ───────────┴──> Phase 7c (tests 9-12)
Phase 7b (PeriodicTimer) ──────────> Phase 7c (all tests)
```

## Key Architectural Decisions

1. **Star field data flows through existing camera properties**: No extra wiring.
   Session sets `Target`, `FocalLength`, `FocusPosition` on the camera. The
   FakeCameraDriver reads them to render the star field. Only `ICelestialObjectDB`
   needs a property setter.

2. **Focus model is self-consistent**: `FakeFocuserDriver` has a physically
   plausible temperature→focus relationship. The star field renderer uses the
   same hyperbola math as the fitting algorithm. Tests verify the full loop:
   temperature drift → PSF degradation → detection → V-curve → correction.

3. **Backlash tracked by the focuser driver**: The driver knows `_lastDirection`
   and can report consumed backlash. The compensation helper always approaches
   from one direction (below), overshooting and returning.

4. **PeriodicTimer replaces overslept**: Idiomatic .NET 8+, deterministic with
   FakeTimeProvider, eliminates manual timing compensation.

5. **`TimeSpan.MaxValue` as "as long as possible"**: No new type needed.

6. **Backlash measurement is optional**: `MeasureBacklashIfUnknown` config flag.
   If backlash is provided in config, skip the slow measurement.

## Files to Create/Modify

### New Files
- `TianWen.Lib/Devices/Fake/SyntheticStarFieldRenderer.cs`
- `TianWen.Lib/Astrometry/Focus/BacklashMeasurement.cs`
- `TianWen.Lib.Tests/SessionTests.cs`

### Modified Files
- `TianWen.Lib/Devices/Fake/FakeGuider.cs` — full rewrite (Phase 1)
- `TianWen.Lib/Devices/Fake/FakeCameraDriver.cs` — cooling + star field (Phase 2, 4)
- `TianWen.Lib/Devices/Fake/FakeFocuserDriver.cs` — temp + backlash model (Phase 3)
- `TianWen.Lib/Sequencing/Session.cs` — auto-focus, drift detection, PeriodicTimer (Phase 6, 7)
- `TianWen.Lib/Sequencing/SessionConfiguration.cs` — backlash fields (Phase 5)
