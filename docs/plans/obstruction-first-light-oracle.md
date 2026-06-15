# Plan: Zenith Calibration Anchor — First-Scout Obstruction Oracle + Cloud Gate

Status: **A + C SHIPPED** (2026-06-13, branch `feat/top-5-todo`, folded into PR #25). Supersedes the
original catalog-floor-only draft. B (transparency HUD readout) is the remaining follow-up.

Delivered:
- `StarDetectionModel` (`src/TianWen.Lib/Astrometry/StarDetectionModel.cs`) — shared detectability
  formula; `SyntheticStarFieldRenderer.DetectabilityMagCutoff` forwards to it (golden-pinned).
- `CatalogStarCounter` (`src/TianWen.Lib/Astrometry/Catalogs/CatalogStarCounter.cs`) — shared
  cell-walk `EnumerateFieldStars` + `CountStarsInField` + cumulative `CountStarsByMagnitude`;
  `ProjectCatalogStars` reuses the same walk (one-path).
- `NightSkyGauge` (`src/TianWen.Lib/Sequencing/NightSkyGauge.cs`) — detected/predicted zenith
  efficiency + inverted effective limiting magnitude.
- `Session.Imaging.SkyGauge.cs` — capture in `InitialRoughFocusAsync` (A/C input),
  `ClassifyFirstScoutAgainstZenithAsync` (A, airmass-dimmed catalog x efficiency x `OracleFactor`,
  narrowband/sparse/missing-DB guards), `WaitForCloudGateAsync` (C, hold-and-re-gauge after rough
  focus). 4 knobs on `SessionConfiguration`. Scout `maxStars` 200 -> 1000 hygiene fix.
- Tests: 16 unit (`StarDetectionModelTests`, `NightSkyGaugeTests`, `CatalogStarCounterTests`) +
  2 functional (`SessionScoutAndProbeTests` oracle + gauge), plus the existing 11 scout + 41
  full-session functional tests still green.

Follow-up to [`fov-obstruction-detection.md`](fov-obstruction-detection.md)
(Known limitations #1) and [`../architecture/fov-obstruction.md`](../architecture/fov-obstruction.md).

## The core idea: zenith is the night's calibration anchor

For any semi-serious setup the **zenith is always unobstructed** — no tree, no roofline,
minimum air mass. So a frame taken near zenith is the one place where
`detected / catalog-predicted` is guaranteed to carry **no obstruction confound**: the only
thing that moves the ratio there is the sky itself (transparency) and the rig's intrinsic
limiting magnitude. We already take such a frame for free — `InitialRoughFocusAsync` slews
near zenith and runs `FindStarsAsync` before the first target.

That single zenith measurement (`detectedAtZenith` vs `catalogPredictedAtZenith`) yields three
things:

| Output | What | Action | Scope |
|--------|------|--------|-------|
| **A. Obstruction oracle** | first-target scout falls short of `catalog x zenithEfficiency` | existing `NudgeTestAsync` | this plan (v1) |
| **C. Cloud gate** | the zenith ratio *itself* craters | route to condition-wait/recovery | this plan (v1) |
| **B. Transparency readout** | the ratio / effective limiting magnitude | log + telemetry + HUD; feed condition logic | follow-up |

A and C share the one zenith measurement and are complementary (per-target vs whole-sky), so
they ship together. B is a thin presentation/telemetry layer on the same number, deferred.

## The problem each solves

- **A** — today `ScoutAndProbeAsync` returns `Healthy` unconditionally for the **first**
  observation of the night (no prior-observation baseline to compare against). A target behind
  a tree at the start of the night is missed until the in-flight condition-deterioration check
  trips, after burning guider-start + several full-length exposures.
- **C** — `InitialRoughFocusAsync` only requires >=15 stars to pass, so **thick haze** (e.g. 50
  detected where the catalog predicts 500) sails through and the session images into cloud. The
  zenith frame, read as an absolute count, is an early whole-sky cloud detector — *before* the
  first target.

## Current state (verified facts, main @ d9342a6 unless noted)

- `ScoutAndProbeAsync` (`src/TianWen.Lib/Sequencing/Session.Imaging.Obstruction.cs:144`): the
  first-observation early-out (lines 159-169) returns `Healthy` when
  `TryGetPreviousObservationBaseline()` (line 467) is null (`_activeObservation - 1 < 0`).
- Baseline classifier `ClassifyAgainstBaseline` (line 208): star-count only, exposure-scaled
  `expectedStars = baseline.StarCount * sqrt(scoutExp / baselineExp)`, healthy when
  `ratio >= ObstructionStarCountRatioHealthy` (0.7). Tentative obstruction -> `NudgeTestAsync`
  (line 255) which distinguishes obstruction (count recovers after altitude nudge) from
  transparency (still bad).
- Scout star detection (line 441): `image.FindStarsAsync(0, snrMin: 10, maxStars: 200, ...)`;
  imaging-loop baseline uses `maxStars: 1000` (Session.Imaging.cs:611). `FrameMetrics.FromStarList`
  counts only stars in the central 80% of the frame.
- Result types (`src/TianWen.Lib/Sequencing/ScoutResult.cs`): `ScoutResult(FrameMetrics[] Metrics,
  ScoutClassification Classification, TimeSpan? EstimatedClearIn)`,
  `ScoutClassification { Healthy, Transparency, Obstruction }`.
- **Rough-focus gauge source** — `InitialRoughFocusAsync` (`Session.Focus.cs:176`):
  slews near zenith (`BeginSlewToZenithAsync(distMeridian)`, line 191), stamps a `Zenith`
  `Target` from the post-slew J2000 read (line 215), then per-OTA
  `image.FindStarsAsync(0, snrMin: 15, ...)` (line 297) and logs `stars.Count` (need >=15 to pass).
  So per-OTA `(detectedCount, zenith RA/Dec, exposure `expTimesSec[i]`, gain, snrMin=15)` are all
  in hand at best rough focus. The slew is offset from the meridian by `distMeridian`, so air mass
  is ~1 (not exactly 1) — close enough for the anchor; note it.
- **Condition-wait/recovery machinery** — `ImagingLoopAsync` (`Session.Imaging.cs:891-914`):
  when `starCountRatio < Configuration.ConditionDeteriorationThreshold` (0.5) it waits up to
  `Configuration.ConditionRecoveryTimeout ?? 10min` then `AdvanceToNextObservation`. C mirrors
  this pattern as a *pre-loop* gate.
- FOV math: `ComputeWidestHalfFovDeg` (Obstruction.cs:479) from
  `CoordinateUtils.PixelScaleArcsec(c.PixelSizeX, t.FocalLength)` x `c.NumX * bin`. Per OTA:
  `OTA.FocalLength` (mm), `OTA.Aperture` (int? mm), `ICameraDriver.PixelSizeX/Y` (um), `NumX/NumY`,
  `Gain`, `Filter`. No QE/read-noise on the driver.
- Catalog access (`TianWen.Lib.Astrometry.Catalogs`): no cone search. Spatial access is
  `db.CoordinateGrid` (`IRaDecIndex`), indexer `this[double ra, double dec]` -> cell's
  `IReadOnlyCollection<CatalogIndex>` (cells ~1/15 h RA x ~1 deg Dec). Per-star via
  `db.TryLookupByIndex(CatalogIndex, out CelestialObject)`; `CelestialObject(ObjectType ObjectType,
  double RA /*hours*/, Half Dec /*deg*/, Half V_Mag, ...)`. The cell-walk pattern exists in
  `SyntheticStarFieldRenderer.ProjectCatalogStars` (lines 459-484) — the fake camera renders from
  THIS SAME catalog, which is what makes the whole thing deterministically testable.
- Detectability model: **extracted** to `Astrometry.StarDetectionModel.DetectabilityMagCutoff`
  (pure SNR-vs-read-noise, no sky-background term); the fake forwards to it. Golden values
  (defaults): 0.01 s -> ~7.87, 5 s -> ~14.62.
- Tycho-2 completeness: ~99% to V=11.0, ~90% at V=11.5 — the catalog count is a FLOOR for what a
  real frame detects whenever the true limiting mag exceeds ~11.5.
- Tests: pure classifier tests `src/TianWen.Lib.Tests/SessionScoutClassifierTests.cs`; integration
  `src/TianWen.Lib.Tests.Functional/SessionScoutAndProbeTests.cs` (`CreateScoutSessionAsync`,
  `SetBaselineForObservationForTest`, `RunScoutWithTimePumpAsync`, cloud sim via
  `ctx.Camera.CloudCoverage`). Existing `GivenFirstObservationNoBaselineWhenScoutThenHealthy` pins
  the current early-out and WILL change.

## Design

### The night gauge (shared measurement, captured once in rough focus)

At best rough focus, per OTA:

```
theoreticalLimit_z = StarDetectionModel.DetectabilityMagCutoff(apertureScale, zenithExp, snr=15)
catalogPredicted_z = CatalogStarCounter.CountStarsInField(zenithRaDec, fovW, fovH, theoreticalLimit_z)
efficiency         = clamp(detected_z / max(catalogPredicted_z, 1), 0, 1)     // ~transparency
```

`efficiency` is the obstruction-free transparency-x-detection factor for tonight. Store a
`NightSkyGauge { double Efficiency; double EffectiveLimitMag; double TheoreticalLimitMag;
int DetectedAtZenith; double CatalogPredictedAtZenith; bool Valid }` per OTA (or a session-level
union — pick the most pessimistic OTA for the cloud gate; per-OTA for the oracle). `EffectiveLimitMag`
= the magnitude where `CountStarsInField(zenith, mag)` first reaches `detected_z` (inverted from the
magnitude histogram) — this is the value B surfaces.

`apertureScale = (Aperture ?? focalLength / 7.0)^2 / 50^2` (when `Aperture` is null, assume f/7 — slow
optics underestimate, the safe direction for a floor; log it).

### A — first-scout obstruction oracle (gauge-calibrated, catalog-floor fallback)

Replace the `prevBaseline is null` early-out (Obstruction.cs:159-169):

```
limitMag_t  = StarDetectionModel.DetectabilityMagCutoff(apertureScale, scoutExp, snr=10)
            - extinction(targetAltitude)         // k=0.2 mag/airmass beyond zenith; conservative
expected    = CatalogStarCounter.CountStarsInField(targetRaDec, 0.8*fov, limitMag_t)
            * (gauge.Valid ? gauge.Efficiency : 1.0)
floor       = gauge.Valid ? expected : min(expected, CountStarsInField(targetRaDec, 0.8*fov, 11.0))
suspicious  = scoutCount < OracleFactor * floor          // OracleFactor default 0.4
```

- With a valid gauge, `expected` is calibrated to **tonight** (efficiency folds in transparency;
  extinction folds in the target's lower altitude). Without one, fall back to the static mag-11
  catalog floor (the original design) — catalog UNDERestimates (clamp <= Tycho-2 completeness), so
  a healthy frame always clears it.
- A suspicious first scout does NOT classify obstruction directly — it routes into the existing
  `NudgeTestAsync` (obstruction vs transparency). A false positive costs one nudge slew + one scout
  exposure, not a skipped target.
- **Sparse-field guard**: when `floor < MinOracleStarCount` (default 10), skip the oracle, keep the
  old `Healthy` early-out, log why (a 3-star expectation can't support a 0.4 test).
- **Narrowband gate**: if the active `Filter.Name` matches a narrowband set (case-insensitive
  contains: "ha","h-alpha","oiii","o3","sii","s2","nb","duo","dual","tri","quad"), skip the oracle
  for that OTA (broadband floor is meaningless). Multi-OTA: verdict counts only evaluable OTAs; if
  none, keep old behaviour.

### C — cloud gate (whole-sky, fires at rough focus before the first target)

```
if gauge.Valid && gauge.Efficiency < CloudGateEfficiencyFloor:    // default 0.15
    -> serious cloud (zenith is unobstructed, so a crushed ratio = sky, not tree)
    -> mirror the Session.Imaging condition-wait: hold, re-gauge at zenith on an interval,
       up to ConditionRecoveryTimeout (?? 10min); recover -> proceed, timeout -> skip/abort cleanly.
```

- This is distinct from the per-target oracle: it's a session-level "is it even worth starting"
  gate, keyed on the obstruction-free zenith anchor. It reuses the existing recovery knob.
- If rough focus itself fails to reach >=15 stars (fully clouded), that already aborts rough focus
  today — C handles the *partial* case (focuses but transparency is terrible).

### B — transparency readout (follow-up, NOT v1)

`gauge.Efficiency` and `gauge.EffectiveLimitMag` are a transparency signal essentially for free.
Two expressions: a model-light index (`detected_z / CountStarsInField(zenith, 11.0)`, ~1 clear) or
the effective limiting magnitude / extinction gap to `TheoreticalLimitMag`. Read as a RELATIVE
signal (the absolute number carries the model's systematic error). Follow-up wires it to live-session
telemetry / sky-map HUD and (optionally) the ongoing condition-deterioration logic.

### Shared helpers — extract, do not duplicate (feedback_one_path)

1. **`StarDetectionModel.DetectabilityMagCutoff`** — DONE (`Astrometry/StarDetectionModel.cs`); fake
   forwards to it.
2. **`CatalogStarCounter`** (new, `src/TianWen.Lib/Astrometry/Catalogs/`):
   - `CountStarsInField(db, raHours, decDeg, fovWdeg, fovHdeg, magLimit)` -> single count.
   - `CountStarsByMagnitude(db, raHours, decDeg, fovWdeg, fovHdeg)` -> cumulative counts per 0.5-mag
     bin (mag 0..15), so the gauge can invert for `EffectiveLimitMag` (B) and the oracle can read any
     limit without re-walking. `CountStarsInField` is a thin wrapper over this.
   - Extract the cell-walk core (grid cells over the box, RA wrap, cos(dec) scaling, `TryLookupByIndex`,
     filter `ObjectType == Star && !Half.IsNaN(V_Mag)`, exact RA/Dec box test) and have
     `ProjectCatalogStars` share the same enumeration so fake rendering and the oracle can't drift.
   - Self-init the DB via the idempotent `InitDBAsync` fast-path (like `CatalogPlateSolver`), or
     verify `InitialisationAsync` already guarantees Tycho-2 bulk load.

### Knobs (SessionConfiguration; new optional args BEFORE the trailing ct)

`FirstScoutOracleEnabled = true`, `OracleFactor = 0.4f`, `MinOracleStarCount = 10`,
`CloudGateEfficiencyFloor = 0.15f`. (`ConditionRecoveryTimeout` reused for C.)

## Phases

| Phase | Work | Est. |
|------:|------|------|
| 1 | `StarDetectionModel` (DONE) + `CatalogStarCounter` (count + histogram, shares cell-walk with `ProjectCatalogStars`); unit tests | M |
| 2 | `NightSkyGauge` type + capture in `InitialRoughFocusAsync` (per OTA: efficiency + effective limit); plumb onto the session | M |
| 3 | **A** — `TryClassifyAgainstZenithGaugeAsync` in Obstruction.cs (gauge-calibrated expected, catalog-floor fallback, sparse + narrowband guards) replacing the early-out; knobs; maxStars 200->1000 hygiene fix | M |
| 4 | **C** — cloud gate after rough focus: efficiency < floor -> condition-wait/recovery (mirror Session.Imaging.cs:891-914) | M |
| 5 | Functional tests (below) incl. updating `GivenFirstObservationNoBaselineWhenScoutThenHealthy` | M |
| 6 | Docs: parent-plan limitation resolved, ARCH touch-up, TODO tick, summary.md row; note B as the next follow-up | S |

## Tests

Unit (`TianWen.Lib.Tests`):
1. `CatalogStarCounterTests` — `CountStarsInField_PleiadesVsGalacticPole_DenseFieldHasMore`;
   `_RaWrapAround_CountsAcross0h`; `_NearPole_CellWalkCoversFullRaRange`;
   `CountStarsByMagnitude_IsCumulativeAndMonotone`.
2. `StarDetectionModelTests.DetectabilityMagCutoff_MatchesLegacyFormula` — golden values
   (0.01 s -> 7.87, 5 s -> 14.62) so the extraction is provably identical.
3. Pure decision tests for A's core: `ClassifyAgainstZenithFloor(scoutCount, expected, efficiency,
   factor, minFloor)` (factor it out like `ClassifyAgainstBaseline`). Pure gauge math:
   `NightSkyGauge.FromCounts` (efficiency clamp, effective-limit inversion).

Functional (`SessionScoutAndProbeTests` harness, fake renders from the same catalog):
4. `GivenFirstObservationClearSkyWhenScoutThenOracleHealthy` — replaces the pinned no-baseline test;
   Healthy AND the gauge/oracle ran.
5. `GivenFirstObservationBlackoutWhenScoutThenOracleTriggersNudgeAndClassifies` — `CloudCoverage=1.0`
   at the target while zenith was clear: nudge runs -> correct verdict.
6. `GivenZenithHazyWhenRoughFocusThenCloudGateWaits` (C) — zenith efficiency below floor -> waits up
   to recovery timeout (ExternalTimePump), then proceeds/aborts.
7. `GivenFirstObservationNarrowbandFilterWhenScoutThenOracleSkipped`.
8. `GivenSparseFieldExpectationBelowMinimumWhenScoutThenOracleSkipped` (long-focal tiny FOV, high
   galactic latitude).

Run order: build, unit, then functional (never parallel suites).

## Out of scope (this plan)

- **B** beyond computing the value — live-session/HUD surfacing + feeding ongoing condition logic is
  the immediate follow-up, not v1.
- Sky-background term in the detectability model (`OracleFactor` + the efficiency ratio absorb it).
- Filter transmission modelling beyond the narrowband skip.
- Catalog-floor / gauge checks for NON-first observations (the relative same-night baseline stays
  primary there; blending is a possible v2).
- Cross-session per-(galactic-latitude x magnitude) baseline cache (the zenith anchor supersedes it
  for first light).
