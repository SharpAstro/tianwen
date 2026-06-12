# Plan: Absolute Expected-Star-Count Oracle for the First Scout of the Night

Status: NOT STARTED. Authored 2026-06-12 for hand-off; all file/line facts verified against main @ `ae85cd8`.

Follow-up to [`PLAN-fov-obstruction-detection.md`](PLAN-fov-obstruction-detection.md)
(Known limitations #1) and [`ARCH-fov-obstruction.md`](ARCH-fov-obstruction.md).

Goal: today `ScoutAndProbeAsync` returns `Healthy` unconditionally for the first
observation of the night because there is no prior-observation baseline to compare
against. A target behind a tree at the very start of the night is missed until the
in-flight condition-deterioration check trips, after we have already burned
guider-start + several full-length exposures. Give the first scout an ABSOLUTE oracle:
a catalog-derived expected star count for the field, weighted by an estimated limiting
magnitude for the OTA x exposure. When the scout count falls far below the catalog
expectation, run the existing nudge test (which already disambiguates obstruction vs
transparency) instead of waving the target through.

## Current state (verified facts)

- `ScoutAndProbeAsync` (`src/TianWen.Lib/Sequencing/Session.Imaging.Obstruction.cs:144`):
  the early-out at lines 159-169:
  ```csharp
  var prevBaseline = TryGetPreviousObservationBaseline();
  if (prevBaseline is null)
  {
      _logger.LogInformation("Scout: no previous baseline for {Target}; skipping obstruction classification.", ...);
      return new ScoutResult(preMetrics, ScoutClassification.Healthy, null);
  }
  ```
  `TryGetPreviousObservationBaseline()` (line 467) returns null when
  `_activeObservation - 1 < 0`.
- Baseline classifier `ClassifyAgainstBaseline` (line 208): **star count only** (no HFD),
  exposure-scaled `expectedStars = baseline.StarCount * sqrt(scoutExp / baselineExp)`,
  healthy when `ratio >= ObstructionStarCountRatioHealthy` (default 0.7). Tentative
  obstruction goes to `NudgeTestAsync` (line 255) which already distinguishes
  obstruction (count recovers after an altitude nudge) from transparency (still bad).
- Scout star detection (line 441): `image.FindStarsAsync(0, snrMin: 10, maxStars: 200, ...)`;
  imaging-loop baseline uses `maxStars: 1000` (Session.Imaging.cs:611). `FrameMetrics.FromStarList`
  counts only stars in the central 80% of the frame (10% margin per side).
- Result types in `src/TianWen.Lib/Sequencing/ScoutResult.cs`:
  `ScoutResult(FrameMetrics[] Metrics, ScoutClassification Classification, TimeSpan? EstimatedClearIn)`,
  `ScoutClassification { Healthy, Transparency, Obstruction }`.
- FOV math: `ComputeWidestHalfFovDeg` (Obstruction.cs:479) from
  `CoordinateUtils.PixelScaleArcsec(c.PixelSizeX, t.FocalLength)` x `c.NumX * bin`.
  Inputs available per OTA: `OTA.FocalLength` (int mm), `OTA.Aperture` (int? mm),
  `ICameraDriver.PixelSizeX/Y` (um), `NumX/NumY`, `Gain`, `Filter`. No QE/read-noise on
  the driver.
- Catalog access: **no cone search** on `ICelestialObjectDB`. Spatial access is
  `IRaDecIndex CoordinateGrid` with indexer `this[double ra, double dec]` returning the
  cell's `IReadOnlyCollection<CatalogIndex>` (cells ~1/15 h RA x ~1 deg Dec). Per-star
  data via `TryLookupByIndex(CatalogIndex, out CelestialObject)` -> `obj.V_Mag` (Half,
  ~0.04% NaN), `obj.ObjectType == ObjectType.Star`. The cell-walk pattern already
  exists in `SyntheticStarFieldRenderer.ProjectCatalogStars` (Fake namespace, lines
  471-484) - the fake camera renders its star fields from THIS SAME catalog.
- Detectability model (fake reference): `SyntheticStarFieldRenderer.DetectabilityMagCutoff`
  (`src/TianWen.Lib/Devices/Fake/SyntheticStarFieldRenderer.cs:395`):
  ```csharp
  DetectabilityMagCutoff(apertureScaleFactor, exposureSeconds, fwhmPixels = 2.0, readNoise = 5.0, snrThreshold = 5.0)
  // apertureScaleFactor = (apertureMm / 50)^2; pure SNR-vs-readNoise, no sky background term
  ```
  `FakeCameraDriver` (lines 1048-1056) uses it with `Math.Min(15.0, cutoff)` and feeds
  `ProjectCatalogStars`.
- Tycho-2 completeness: ~99% to V=11.0, ~90% at V=11.5 - the catalog count is a FLOOR
  for what a real frame detects whenever the true limiting magnitude exceeds ~11.5.
- Cross-session persistence pattern (if ever needed):
  `BacklashHistoryPersistence` -> `<IExternal.ProfileFolder>/BacklashHistory/<id>.json`
  via `external.AtomicWriteJsonAsync` + source-generated JsonTypeInfo.
- Tests: pure classifier tests in `src/TianWen.Lib.Tests/SessionScoutClassifierTests.cs`;
  integration in `src/TianWen.Lib.Tests.Functional/SessionScoutAndProbeTests.cs`
  (`CreateScoutSessionAsync`, `SetBaselineForObservationForTest`,
  `RunScoutWithTimePumpAsync` with ExternalTimePump, cloud sim via `ctx.Camera.CloudCoverage`).
  Existing test `GivenFirstObservationNoBaselineWhenScoutThenHealthy` pins the current
  early-out and WILL need updating.
- PLAN-fov-obstruction-detection.md:195-221 quotes the limitation and the two candidate
  fixes (catalog-derived expectation - chosen here - or cross-session cache - deferred).

## Design

### Why the catalog floor works as an oracle

We do not need an accurate limiting-magnitude model (the "Risk: expected-star-count
model is hard" bullet in the parent plan). We need a one-sided test:

```
limitMag      = min(EstimateDetectabilityMagCutoff(aperture, exposure, snr=10), 11.0)
expectedFloor = CatalogStarsInField(targetRaDec, 0.8 x FOV, limitMag)     // central-80% to match FrameMetrics
suspicious    = scoutStarCount < OracleFactor * expectedFloor             // OracleFactor default 0.4
```

- Clamping `limitMag` at 11.0 keeps the expectation inside Tycho-2 completeness, so the
  catalog count UNDERESTIMATES what a healthy frame sees. A healthy real frame
  (limiting mag 13-15) detects strictly more stars than the floor; a tree across half
  the aperture or a fully blocked FOV crushes the count below it.
- `OracleFactor` (new `SessionConfiguration` knob, default 0.4) absorbs detection
  losses (SNR threshold, the central-80% margin mismatch, marginal seeing). It is
  deliberately looser than the relative `ObstructionStarCountRatioHealthy = 0.7`
  because the absolute floor is a weaker reference than a same-night baseline.
- A suspicious first scout does NOT classify obstruction directly - it routes into the
  existing `NudgeTestAsync`, which makes the actual obstruction-vs-transparency call.
  False positives therefore cost one nudge slew + one scout exposure, not a skipped target.
- Sparse-field guard: when `expectedFloor < MinOracleStarCount` (knob, default 10 - e.g.
  tiny FOV at high galactic latitude through the clamp), skip the oracle and keep the
  old Healthy early-out (log why). An expectation of 3 stars cannot support a 0.4 test.

### Shared helpers - extract, do not duplicate (feedback_one_path)

1. **`StarDetectionModel.DetectabilityMagCutoff`** - move the formula from
   `SyntheticStarFieldRenderer` to a new
   `src/TianWen.Lib/Astrometry/StarDetectionModel.cs` (public static); the fake calls
   the shared one (its own signature stays as a thin forwarder or call-site update).
   Session oracle calls it with `snrThreshold: 10` to match the scout's `FindStarsAsync(snrMin: 10)`.
   `apertureScaleFactor = (OTA.Aperture ?? f/6-derived guess)^2 / 50^2`; when `Aperture`
   is null, derive a conservative aperture from focal length assuming f/7
   (`aperture = focalLength / 7.0`) and log it - slower optics underestimate, which is
   the safe direction for a floor.
2. **`CatalogStarCounter.CountStarsInField(ICelestialObjectDB db, double raHours, double decDeg, double fovWdeg, double fovHdeg, double magLimit)`**
   - new helper in `src/TianWen.Lib/Astrometry/Catalogs/`, extracted from the cell-walk
   in `ProjectCatalogStars` (walk `CoordinateGrid` cells covering the box with RA
   wrap-around and cos(dec) scaling, `TryLookupByIndex`, filter
   `ObjectType == Star && !Half.IsNaN(V_Mag) && (double)V_Mag <= magLimit`, exact box
   test on each star's RA/Dec). Refactor `ProjectCatalogStars` to share the cell-walk
   enumeration core so fake rendering and the oracle cannot drift apart.
   NOTE: `CatalogPlateSolver` self-inits the DB; the oracle must do the same idempotent
   `InitDBAsync` fast-path call (or document that Session init already guarantees it -
   verify where `ICelestialObjectDB.InitDBAsync` runs during `InitialisationAsync`).
3. Filter gate: narrowband filters (Ha/OIII/SII) kill the broadband floor. v1: if the
   active `Filter.Name` matches a narrowband set (case-insensitive contains:
   "ha", "h-alpha", "oiii", "o3", "sii", "s2", "nb", "duo", "dual", "tri", "quad"),
   skip the oracle for that OTA and log. Multi-OTA union rule: oracle verdict counts
   only OTAs it could evaluate; if none, keep old behavior.

### Wiring into `ScoutAndProbeAsync`

Replace the lines 159-169 early-out with:

```csharp
var prevBaseline = TryGetPreviousObservationBaseline();
if (prevBaseline is null)
{
    var oracle = await TryClassifyAgainstCatalogFloorAsync(observation, preMetrics, scoutExposure, ct);
    if (oracle == ScoutClassification.Healthy)   // incl. oracle-skipped cases
        return new ScoutResult(preMetrics, ScoutClassification.Healthy, null);
    // suspicious -> same disambiguation path as the relative classifier
    var (postMetrics, classification) = await NudgeTestAsync(observation, preMetrics, scoutExposure, ct);
    ...existing obstruction/transparency handling (mirror the prevBaseline!=null flow,
    including EstimateObstructionClearTimeAsync on Obstruction)...
}
```

Factor the post-classification handling shared with the baseline path into a local
helper if it would otherwise be duplicated. Log the oracle inputs at Information level:
`"Scout oracle for {Target}: {Scout} detected vs catalog floor {Floor} (limitMag {Mag:F1}, factor {Factor}) -> {Verdict}"`.
Surface through the existing `ScoutCompletedEventArgs` unchanged (classification +
star counts already carried).

Hygiene fix while in the file: raise the scout `FindStarsAsync` `maxStars: 200 -> 1000`
(line 441) to match the baseline cap - the 200 cap silently deflates ratios in dense
fields for BOTH classifiers (documented gap #4 of the fact-finding; cheap, behavior-safe).

### Why the fake camera makes this deterministic to test

`FakeCameraDriver` renders star fields from the SAME catalog via `ProjectCatalogStars`
with `DetectabilityMagCutoff(snr=5)`-clamped mag 15. With a 5 s scout on a typical fake
profile the rendered cutoff (~14.6) far exceeds the oracle's clamped 11.0, so a clear
fake frame beats the floor comfortably; `ctx.Camera.CloudCoverage >= 1.0` (blackout,
reference_pulse_routing_auto_mount) collapses detection and trips the oracle.

## Phases

| Phase | Work | Est. |
|------:|------|------|
| 1 | Extract `StarDetectionModel.DetectabilityMagCutoff` (fake forwards to it) + `CatalogStarCounter.CountStarsInField` sharing the cell-walk with `ProjectCatalogStars`; unit tests | M |
| 2 | `TryClassifyAgainstCatalogFloorAsync` in Obstruction.cs + knobs (`OracleFactor = 0.4`, `MinOracleStarCount = 10`, `FirstScoutOracleEnabled = true`) + narrowband gate + maxStars 200->1000 | M |
| 3 | Functional tests (below) incl. updating `GivenFirstObservationNoBaselineWhenScoutThenHealthy` | M |
| 4 | Docs: PLAN-fov-obstruction-detection Known-limitations note resolved, ARCH doc diagram touch-up, TODO tick, PLAN-summary row | S |

## Tests

Unit (`TianWen.Lib.Tests`):

1. `CatalogStarCounterTests.CountStarsInField_PleiadesVsGalacticPole_DenseFieldHasMore`
   - real catalog fixture (DB load mirrors `CatalogPlateSolver` test setup); pin
   approximate counts for two known fields at mag 11.
2. `CountStarsInField_RaWrapAround_CountsAcross0h` + `NearPole_CellWalkCoversFullRaRange`.
3. `StarDetectionModelTests.DetectabilityMagCutoff_MatchesLegacyFormula` - golden values
   vs the pre-move formula (0.01 s -> 7.87, 5 s -> 14.62 with defaults, from the
   Solve & Sync debugging session) so the extraction is provably identical.
4. Oracle decision-table tests as a pure function if `TryClassifyAgainstCatalogFloorAsync`
   factors its core decision into `ClassifyAgainstCatalogFloor(scoutCount, floor, factor, minFloor)`
   (do this - mirrors `ClassifyAgainstBaseline`'s testability).

Functional (`SessionScoutAndProbeTests` harness):

5. `GivenFirstObservationClearSkyWhenScoutThenOracleHealthy` - replaces/extends the
   pinned no-baseline test: assert Healthy AND that the oracle ran (log or event).
6. `GivenFirstObservationBlackoutWhenScoutThenOracleTriggersNudgeAndClassifies` -
   `CloudCoverage = 1.0`: nudge test runs (both scouts dark -> Transparency, the
   correct verdict for cloud).
7. `GivenFirstObservationNarrowbandFilterWhenScoutThenOracleSkipped` - fake filter
   wheel with an Ha filter active: Healthy + skip logged.
8. `GivenSparseFieldExpectationBelowMinimumWhenScoutThenOracleSkipped` - tiny-FOV
   profile (long focal length) at a high-galactic-latitude target.

Run order: build, unit, then functional (never parallel suites).

## Out of scope

- Cross-session per-(galactic-latitude x magnitude) baseline cache (the alternative in
  the parent plan) - the catalog floor supersedes it for the first-light case; revisit
  only if real-world false-positive rates demand per-rig calibration. Persistence
  pattern documented above if it returns.
- Sky-background term in the detectability model (moonlight/light pollution lower real
  detection counts; `OracleFactor` headroom covers it for the floor test).
- Filter transmission modelling beyond the narrowband skip (no transmission data on
  `InstalledFilter` today).
- Unifying with the guider-calibration slew safety item (Session.Lifecycle.cs:19) -
  explicitly listed as a separate limitation in the parent plan.
- Catalog-floor checks for NON-first observations (relative baseline stays primary;
  blending the two signals is a possible v2).
