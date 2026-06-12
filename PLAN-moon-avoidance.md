# Plan: Moon Avoidance in Target Scoring

Status: SHIPPED on branch `feat/moon-avoidance` (2026-06-12). Authored 2026-06-12; all
file/line facts verified against main @ `ae85cd8`.

Implementation notes / deviations from the plan as authored:

- **No `SessionConfiguration.MoonAvoidance*` knobs (Phase 3 partial deviation).** The plan
  assumed the session path scores targets, but `ObservationScheduler` is used *only* by the
  planner (`PlannerActions`, `TianWen.UI.Abstractions`) and tests - never by the `Session`
  loop, which consumes a pre-scored `ScheduledObservationTree`. Adding the knobs to
  `SessionConfiguration` would have been dead code. The radius instead lives as an optional
  parameter (`moonAvoidanceRadiusDeg`, default `DefaultMoonAvoidanceRadiusDeg = 30`, ON;
  `0` disables) on `Schedule` / `TonightsBest` / the public `ScoreTarget` overload - the
  paths that actually run. UI surfacing of the knob stays out of scope as planned.
- `MinMoonSeparationDeg` on `ScoredTarget` records the closest the *above-horizon,
  illuminated* Moon comes to the target across its imaging window (NaN when the Moon never
  interferes); it is informational, not radius-clamped.
- Radius/illumination/horizon context is folded into the shared `MoonGrid` struct (which now
  also carries `AvoidanceRadiusDeg`), so each `ScoreTarget` call takes a single nullable
  `MoonGrid?` argument; `null` (or radius 0) = no penalty.
- Tests: `MoonAvoidanceTests.cs` (6 tests) - black-box on the real Dec 4 2025 winter full
  Moon (near/far/Schedule/grid-matches-ephemeris) plus white-box synthetic `MoonGrid`s that
  isolate the illumination gate, horizon gate, and quadratic proximity falloff (a real new
  Moon is always below the horizon, which would otherwise conflate the two gates). Full unit
  (2596) + functional (286) suites green.

Goal: penalise targets that sit near a bright Moon when scoring/scheduling, so the
scheduler stops putting a faint galaxy 10 deg from a 95%-lit Moon ahead of an
equally good target on the dark side of the sky. TODO.md: "Moon penalty in target
scoring - penalise targets within ~30 deg of a bright Moon (illumination x proximity
factor). Compute angular separation per target in ObservationScheduler.ScoreTarget".

## Current state (verified facts)

- `ObservationScheduler.ScoreTarget` has two overloads in
  `src/TianWen.Lib/Sequencing/ObservationScheduler.cs`:
  - Public convenience overload (line 116): `ScoreTarget(Target, Transform, DateTimeOffset astroDark, DateTimeOffset astroTwilight, byte minHeightAboveHorizon, ObjectType)` - builds the grid via `PrecomputeAstromGrid` and delegates.
  - Internal batch overload (line 606): `ScoreTarget(Target, ReadOnlySpan<Astrom> astroms, ReadOnlySpan<DateTimeOffset> times, astroDark, astroTwilight, minHeightAboveHorizon, double siteLong, ObjectType, double objectBonus = 1.0)`.
- Grid: `PrecomputeAstromGrid` (line 579) builds 15-min bins (`AstromGridStep`); per bin
  `times[i]` is a full `DateTimeOffset`, `astroms[i]` a SOFA `Astrom` (site lat/lon/elev baked in).
- Per-bin altitude: `SOFAHelpers.AltitudeFromAstrom(target.RA, target.Dec, in astroms[i])`
  (RA hours, Dec degrees -> altitude degrees). Score accumulation (lines 628-643):
  `totalScore += alt - minHeightAboveHorizon` for bins above the minimum; a x0.5
  meridian-outside-dark-window penalty already exists at line 663 (precedent for
  multiplicative penalties).
- **The batch overload receives `siteLong` but NOT `siteLat`** - relevant because
  topocentric Moon position needs both (see Design).
- Moon ephemeris already exists:
  - `MeeusMoon` (`src/TianWen.Lib/Astrometry/Lunar/MeeusMoon.cs`, internal static):
    `GetPhase(double jd)` returns `(double Illumination, bool Waxing)`; accuracy ~0.3 deg
    in longitude.
  - `VSOP87a.ReduceJ2000(CatalogIndex.Moon, DateTimeOffset, out raHours, out decDeg, out distance)`
    returns geocentric J2000 - no site needed.
  - `VSOP87a.Reduce(CatalogIndex.Moon, time, lat, lon, out ra, out dec, out az, out alt, out dist)`
    returns topocentric apparent + altitude - needs site lat/lon.
- `CoordinateUtils.AngularSeparationDeg(ra1Hours, dec1Deg, ra2Hours, dec2Deg)` in
  `src/TianWen.Lib/Astrometry/CoordinateUtils.cs:33` (haversine, RA in hours).
- `ScoredTarget` is `readonly record struct` in `src/TianWen.Lib/Sequencing/TargetScore.cs:9`
  with `Half TotalScore`, `Half ObjectBonus`, `CombinedScore => TotalScore * ObjectBonus`.
- ScoreTarget call sites (blast radius - all pick up the penalty automatically once it
  lives inside the batch overload):
  - `Schedule` main loop (ObservationScheduler.cs:191-193)
  - `TonightsBest` x2 (lines 779-780, 824-825)
  - `PlannerActions` x5 (RecomputeForDate x2, SearchTargets, CommitSuggestion, TogglePinFromExternal)
  - tests/benchmarks (`ObservationScheduleVisualizationTests`, `ScoreTargetBenchmarks`)
- The planner already computes Moon data for display: `PlannerActions.ComputeMoonData`
  (PlannerActions.cs:1582) - 10-min altitude profile + `MeeusMoon.GetPhase` at midnight.
  `PlannerState` has `MoonIllumination`, `MoonWaxing`, `MoonAltitudeProfile` but NOT Moon RA/Dec.
- `SessionConfiguration` (`src/TianWen.Lib/Sequencing/SessionConfiguration.cs`) is a
  positional `record struct`; new knobs are appended as trailing params with defaults.

## Design

### Penalty model

Per time bin `i`, multiply the bin's altitude contribution by a Moon factor:

```
sepDeg     = AngularSeparationDeg(moonRa[i], moonDec[i], target.RA, target.Dec)
proximity  = Clamp((radiusDeg - sepDeg) / radiusDeg, 0, 1)     // 1 at 0 deg, 0 at radius
moonFactor = 1 - illumination * proximity * proximity          // quadratic falloff
binScore   = (alt - minHeight) * moonFactor
```

- `radiusDeg` default 30 (config knob). Quadratic proximity keeps the penalty gentle at
  the rim and harsh inside ~10 deg, approximating the ACP Lorentzian without new constants.
- `illumination` in [0,1] from `MeeusMoon.GetPhase` - new Moon disables the penalty
  naturally; full Moon at 0 separation zeroes the bin.
- **Moon below the horizon at that bin => no penalty for that bin.** A bright Moon that
  rises at 3am must not penalise a target imaged 21:00-01:00. Use
  `SOFAHelpers.AltitudeFromAstrom(moonRa[i], moonDec[i], in astroms[i]) > 0`.
  (Geocentric J2000 Moon RA/Dec through `AltitudeFromAstrom` carries up to ~1 deg of
  parallax error - irrelevant against a 30 deg radius and a horizon test.)

### Moon grid is target-independent: compute once, share

Computing Moon position per (target x bin) is wasteful; the Moon's path is the same for
every target. Extend the precomputed grid:

```csharp
// new readonly struct next to PrecomputeAstromGrid
internal readonly struct MoonGrid
{
    public readonly double[] RaHours;       // per bin, geocentric J2000 via ReduceJ2000
    public readonly double[] DecDeg;        // per bin
    public readonly bool[] AboveHorizon;    // per bin, via AltitudeFromAstrom(moonRa, moonDec, astrom)
    public readonly double Illumination;    // GetPhase at the night midpoint (changes <2%/night)
}
```

- Built by a new `PrecomputeMoonGrid(ReadOnlySpan<DateTimeOffset> times, ReadOnlySpan<Astrom> astroms)`
  called right after `PrecomputeAstromGrid` at the three grid-construction sites
  (Schedule line ~183, TonightsBest x2). `MeeusMoon.GetPhase` needs a Julian Date; reuse
  whatever JD helper `ComputeMoonData` uses (see PlannerActions.cs:1582).
- The Moon moves ~0.55 deg/h, so per-bin (15 min) sampling is more than enough.
- `MeeusMoon` is `internal`; the scheduler is in the same assembly - no visibility change.

### Plumbing

- Batch `ScoreTarget` gains an optional parameter: `in MoonGrid? moonGrid = null`
  (or a `ReadOnlySpan`-friendly shape; `MoonGrid?` boxed-nullable on a struct is fine
  since it is passed once per target, not per bin). `null` => no penalty (back-compat
  for any caller that does not precompute).
- The public convenience overload computes its own `MoonGrid` (it already builds the
  astrom grid) so planner single-target calls (SearchTargets, CommitSuggestion,
  TogglePinFromExternal) get the penalty automatically.
- Knobs: `MoonAvoidanceRadiusDeg` (double, default 30) and `MoonAvoidanceEnabled`
  (bool, default true). Two homes:
  - `SessionConfiguration` for the session path (append trailing params).
  - `ObservationScheduler.Schedule` + `TonightsBest` take the radius as an optional
    parameter (default 30) so the planner does not need a SessionConfiguration.
  Keep one source of truth: define `public const double DefaultMoonAvoidanceRadiusDeg = 30`
  on `ObservationScheduler` and reference it from `SessionConfiguration`'s default.
- Optional but cheap and useful for UI: add `double MinMoonSeparationDeg` (and possibly
  `double MoonIllumination`) to `ScoredTarget` so the planner list can later show a Moon
  warning chip. `ScoredTarget` is constructed in one place (line 674). Appending fields
  with defaults keeps existing `with`/ctor uses compiling.

### What NOT to do

- Do not consult `PlannerState.MoonIllumination` from the scheduler - TianWen.Lib must
  not depend on UI state; compute illumination inside the scheduler from the same
  ephemeris (one path).
- Do not penalise via a post-hoc score multiplier on the whole night - per-bin matters:
  a target 25 deg from the Moon only while the Moon is up should keep its pre-moonrise
  window score intact, which also makes `OptimalStart` drift toward the dark part of the
  night for free (the existing optimal-window extraction reads the per-bin scores).

## Phases

| Phase | Work | Est. |
|------:|------|------|
| 1 | `MoonGrid` struct + `PrecomputeMoonGrid` + unit tests for the grid itself (Moon RA/Dec sane vs known ephemeris date, horizon flags) | S |
| 2 | Penalty inside batch `ScoreTarget` (+ optional `MinMoonSeparationDeg` on `ScoredTarget`); wire grid construction at Schedule + TonightsBest x2 + public overload | M |
| 3 | Knobs: `Schedule`/`TonightsBest` optional radius param, `SessionConfiguration.MoonAvoidance*`, `SessionFactory` pass-through | S |
| 4 | Tests (below) + re-baseline any existing scheduler tests whose pinned scores shift on Moon-bright test nights | M |

## Tests (`src/TianWen.Lib.Tests/ObservationSchedulerTests.cs` conventions)

Existing conventions: `[Collection("Scheduling")]`, Shouldly, `CreateTransform()` =
Vienna 48.2N/16.4E, static `Target` fields (M42, M13), static AstroDark/AstroTwilight
constants. Pick test nights by real lunar phase (verify with Stellarium or
`MeeusMoon.GetPhase` while writing the test, then pin):

1. `ScoreTarget_TargetNearFullMoon_ScoreHeavilyReduced` - full-Moon night, target
   within ~10 deg of the Moon all night: score < 0.3x the no-Moon score for the same
   geometry (compare against the same target scored with `MoonAvoidanceRadiusDeg = 0`
   or a new-Moon night).
2. `ScoreTarget_TargetFarFromMoon_ScoreUnchanged` - same night, target > 60 deg away:
   equal (within Half precision) to the radius-0 score.
3. `ScoreTarget_NewMoon_NoPenalty` - new-Moon night, target anywhere: equal to radius-0.
4. `ScoreTarget_MoonBelowHorizonDuringWindow_NoPenalty` - choose night/target where the
   Moon rises after the target's window closes.
5. `Schedule_TwoEqualTargetsOneNearMoon_FarTargetWinsSlot` - end-to-end: scheduler
   prefers the dark-sky target for the contested bin.
6. Grid test: `PrecomputeMoonGrid_KnownDate_MatchesReduceJ2000` - spot-check 3 bins.

Run: `cd src && dotnet build` then
`dotnet test TianWen.Lib.Tests --no-build --filter "FullyQualifiedName~ObservationScheduler"`.
Watch for pre-existing pinned-score assertions that legitimately shift (investigate each,
per feedback_no_preexisting_failures - do not blind-update).

## Out of scope

- Moon-altitude weighting of the penalty strength (low Moon scatters less) - the
  horizon gate is the v1 approximation.
- Sky-brightness modelling (Krisciunas-Schaefer) - the quadratic proximity factor is
  deliberately simple.
- UI surfacing of `MinMoonSeparationDeg` in planner rows (field is added; rendering is
  a separate planner work item).
- Narrowband leniency (Ha targets tolerate moonlight) - would need filter-aware
  proposals at scoring time; note for later via `ProposedObservation.FilterPlan`.
